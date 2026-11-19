using PdfEditor.Core.Interop;

namespace PdfEditor.Core;

/// <summary>
/// A shaded fill (gradient) on a page. <see cref="PlainColour"/> is set when it shows one single
/// colour all over, which a real gradient never does: such boxes are usually a transparent
/// effect that the program which made the PDF wrote out as a solid block.
/// </summary>
public sealed record GradientInfo(int ObjectIndex, PdfRect Bounds, PdfColor? PlainColour);

// Gradients ("shadings"). PDFium can show them but cannot write them: when it rewrites a page
// after an edit it leaves them out. So before a page is rewritten each one is replaced by a
// picture of itself.
public sealed unsafe partial class PdfDocument
{
    private const int PageObjectShading = 4;

    /// <summary>
    /// The gradients on a page, each looked at closely enough to tell whether it is really just one
    /// flat colour. This draws every gradient on a private copy of the document, so it is meant
    /// for when the user asks, not for every refresh.
    /// </summary>
    public IReadOnlyList<GradientInfo> GetGradients(int pageIndex)
    {
        var found = new List<(int Index, PdfRect Bounds)>();
        using (var page = OpenPage(pageIndex))
        {
            int count = Native.FPDFPage_CountObjects(page.Handle);
            for (int i = 0; i < count; i++)
            {
                var obj = Native.FPDFPage_GetObject(page.Handle, i);
                if (Native.FPDFPageObj_GetType(obj) == PageObjectShading) found.Add((i, ObjectBounds(page, obj)));
            }
        }
        if (found.Count == 0) return [];

        // Drawing one alone means lifting it off its page for a moment; do that to a copy.
        var result = new List<GradientInfo>();
        using var copy = Open(ToBytes());
        using var copyPage = copy.OpenPage(pageIndex);
        var objects = found.Select(f => Native.FPDFPage_GetObject(copyPage.Handle, f.Index)).ToList();
        using var easel = new Easel(copy, copyPage);
        for (int i = 0; i < found.Count; i++)
        {
            PdfColor? plain = null;
            if (objects[i] != IntPtr.Zero && Native.FPDFPageObj_GetType(objects[i]) == PageObjectShading &&
                Native.FPDFPage_RemoveObject(copyPage.Handle, objects[i]) != 0)
            {
                var picture = easel.Draw(objects[i]);
                Native.FPDFPageObj_Destroy(objects[i]);
                if (picture is { } p) plain = p.PlainColour;
            }
            result.Add(new GradientInfo(found[i].Index, found[i].Bounds, plain));
        }
        return result;
    }

    /// <summary>Replaces every gradient directly on the page by a picture of itself, in the same place and order.</summary>
    private void KeepGradients(PageHandle page)
    {
        var gradients = new List<IntPtr>();
        int count = Native.FPDFPage_CountObjects(page.Handle);
        for (int i = 0; i < count; i++)
        {
            var obj = Native.FPDFPage_GetObject(page.Handle, i);
            if (Native.FPDFPageObj_GetType(obj) == PageObjectShading && !page.NewObjects.Contains(obj)) gradients.Add(obj);
        }
        if (gradients.Count == 0) return;

        using var easel = new Easel(this, page);
        foreach (var gradient in gradients)
        {
            // Its place in the drawing order, found afresh since earlier swaps may have dropped items.
            int index = -1, now = Native.FPDFPage_CountObjects(page.Handle);
            for (int i = 0; i < now && index < 0; i++) if (Native.FPDFPage_GetObject(page.Handle, i) == gradient) index = i;
            if (index < 0 || Native.FPDFPage_RemoveObject(page.Handle, gradient) == 0) continue;

            var picture = easel.Draw(gradient);
            Native.FPDFPageObj_Destroy(gradient);
            if (picture is not { } p) continue; // it showed nothing at all

            var image = Native.FPDFPageObj_NewImageObj(Doc);
            if (image == IntPtr.Zero) continue;
            try { SetPixels(page, image, p.Pixels, p.Width, p.Height); }
            catch (PdfException)
            {
                Native.FPDFPageObj_Destroy(image);
                continue;
            }
            var m = new Native.FS_MATRIX { a = (float)(p.Right - p.Left), d = (float)(p.Top - p.Bottom), e = (float)p.Left, f = (float)p.Bottom };
            Native.FPDFPageObj_SetMatrix(image, &m);
            Native.FPDFPage_InsertObjectAtIndex(page.Handle, image, (nuint)index);
            page.NewObjects.Add(image);
        }
    }

    /// <summary>A gradient drawn by itself: its pixels (top-down BGRA) and the place they cover, in the page's own coordinates.</summary>
    private readonly record struct GradientPicture(byte[] Pixels, int Width, int Height, double Left, double Bottom, double Right, double Top, PdfColor? PlainColour);

    /// <summary>
    /// A spare page on which one object at a time is drawn alone, so that nothing else on its real
    /// page gets into the picture. The spare page is removed again when this is disposed.
    /// </summary>
    private sealed class Easel : IDisposable
    {
        private const double MaxScale = 2;    // 144 pixels per inch: gradients are smooth, more would only add weight
        private const int MaxSide = 1600;

        private readonly PdfDocument _doc;
        private readonly int _index;
        private readonly IntPtr _spare;
        private readonly double _left, _bottom, _right, _top; // the real page's visible area, in its own coordinates

        public Easel(PdfDocument doc, PageHandle page)
        {
            _doc = doc;
            var box = page.ViewToUser.MapBounds(0, 0, page.Width, page.Height);
            (_left, _bottom, _right, _top) = (box.X, box.Y, box.Right, box.Bottom);
            _index = doc.PageCount;
            _spare = Native.FPDFPage_New(doc.Doc, _index, _right - _left, _top - _bottom);
            if (_spare != IntPtr.Zero) Native.FPDFPage_SetMediaBox(_spare, (float)_left, (float)_bottom, (float)_right, (float)_top);
        }

        /// <summary>Draws an object that is on no page at present. It is still on no page afterwards. Null when it shows nothing.</summary>
        public GradientPicture? Draw(IntPtr obj)
        {
            if (_spare == IntPtr.Zero) return null;
            float l = 0, b = 0, r = 0, t = 0;
            if (Native.FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) == 0) return null;
            // Only the part that is on the page can be seen.
            double left = Math.Max(l, _left), right = Math.Min(r, _right), bottom = Math.Max(b, _bottom), top = Math.Min(t, _top);
            if (right - left < 0.01 || top - bottom < 0.01) return null;
            double scale = Math.Min(MaxScale, MaxSide / Math.Max(right - left, top - bottom));

            // Whole pixels of the spare page (its top-left corner is pixel 0, 0) that cover the object.
            int x0 = (int)Math.Floor((left - _left) * scale), x1 = (int)Math.Ceiling((right - _left) * scale);
            int y0 = (int)Math.Floor((_top - top) * scale), y1 = (int)Math.Ceiling((_top - bottom) * scale);
            int w = Math.Max(1, x1 - x0), h = Math.Max(1, y1 - y0);

            Native.FPDFPage_InsertObject(_spare, obj);
            var pixels = new byte[w * h * 4]; // all zero: fully see-through
            try
            {
                fixed (byte* p = pixels)
                {
                    var bitmap = Native.FPDFBitmap_CreateEx(w, h, Native.FPDFBitmap_BGRA, p, w * 4);
                    if (bitmap == IntPtr.Zero) return null;
                    try
                    {
                        Native.FPDF_RenderPageBitmap(bitmap, _spare, -x0, -y0,
                            (int)Math.Round((_right - _left) * scale), (int)Math.Round((_top - _bottom) * scale), 0, 0);
                    }
                    finally { Native.FPDFBitmap_Destroy(bitmap); }
                }
            }
            finally { Native.FPDFPage_RemoveObject(_spare, obj); }

            // What it shows: nothing, one colour all over, or a real gradient.
            bool any = false, plain = true;
            int firstB = 0, firstG = 0, firstR = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 128) continue; // see-through, or the soft edge of its outline
                if (!any) { (firstB, firstG, firstR, any) = (pixels[i], pixels[i + 1], pixels[i + 2], true); continue; }
                if (Math.Abs(pixels[i] - firstB) > 6 || Math.Abs(pixels[i + 1] - firstG) > 6 || Math.Abs(pixels[i + 2] - firstR) > 6) { plain = false; break; }
            }
            if (!any && !pixels.Where((_, i) => i % 4 == 3).Any(a => a != 0)) return null;
            return new GradientPicture(pixels, w, h,
                _left + x0 / scale, _top - y1 / scale, _left + x1 / scale, _top - y0 / scale,
                any && plain ? new PdfColor((byte)firstR, (byte)firstG, (byte)firstB) : null);
        }

        public void Dispose()
        {
            if (_spare == IntPtr.Zero) return;
            Native.FPDF_ClosePage(_spare);
            Native.FPDFPage_Delete(_doc.Doc, _index);
        }
    }
}
