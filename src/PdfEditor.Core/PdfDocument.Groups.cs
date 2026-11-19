using System.Runtime.InteropServices;
using PdfEditor.Core.Interop;

namespace PdfEditor.Core;

/// <summary>
/// A group: several items the PDF stores as one unit (a "form"), often a header or logo reused
/// across pages. Its items cannot be edited until the group is taken apart with Ungroup.
/// </summary>
public sealed record GroupInfo(int ObjectIndex, PdfRect Bounds, int ItemCount);

// Groups (PDF form XObjects).
public sealed unsafe partial class PdfDocument
{
    private const int PageObjPath = 2, PageObjForm = 5;
    // Clips that trim no more than this (points) off an item are ignored when a group is taken apart:
    // the copies cannot carry a clip, and a sliver this thin is not visible.
    private const float UngroupClipSlack = 3;

    public IReadOnlyList<GroupInfo> GetGroups(int pageIndex)
    {
        using var page = OpenPage(pageIndex);
        var groups = new List<GroupInfo>();
        int count = Native.FPDFPage_CountObjects(page.Handle);
        for (int i = 0; i < count; i++)
        {
            var obj = Native.FPDFPage_GetObject(page.Handle, i);
            if (Native.FPDFPageObj_GetType(obj) != PageObjForm) continue;
            groups.Add(new GroupInfo(i, ObjectBounds(page, obj), Math.Max(0, Native.FPDFFormObj_CountObjects(obj))));
        }
        return groups;
    }

    /// <summary>True when one of a group's own items lies under a point (a group's bounds can span far more than its items).</summary>
    public bool GroupItemAt(int pageIndex, int objectIndex, double x, double y)
    {
        using var page = OpenPage(pageIndex);
        var form = GetObject(page, objectIndex, PageObjForm, "group");
        Native.FS_MATRIX fm;
        if (Native.FPDFPageObj_GetMatrix(form, &fm) == 0) return false;
        var toView = Affine.Concat(new Affine(fm.a, fm.b, fm.c, fm.d, fm.e, fm.f), page.UserToView);
        int count = Native.FPDFFormObj_CountObjects(form);
        for (int i = 0; i < count; i++)
        {
            var item = Native.FPDFFormObj_GetObject(form, new CULong((uint)i));
            float l = 0, b = 0, r = 0, t = 0;
            if (item == IntPtr.Zero || Native.FPDFPageObj_GetBounds(item, &l, &b, &r, &t) == 0) continue;
            var bounds = toView.MapBounds(l, b, r, t);
            if (new PdfRect(bounds.X - 2, bounds.Y - 2, bounds.Width + 4, bounds.Height + 4).Contains(x, y)) return true;
        }
        return false;
    }

    /// <summary>
    /// Takes a group apart on this page: its text, shapes and images become ordinary items that
    /// can be selected and edited, looking the same as before. Returns false, changing nothing,
    /// when the group holds something that cannot be rebuilt faithfully (shading, clipping).
    /// </summary>
    public bool Ungroup(int pageIndex, int objectIndex) => Ungroup(pageIndex, objectIndex, out _);

    /// <summary>As <see cref="Ungroup(int, int)"/>, also saying what stood in the way when it returns false.</summary>
    public bool Ungroup(int pageIndex, int objectIndex, out string? problem)
    {
        problem = null;
        using var page = OpenPage(pageIndex);
        var form = GetObject(page, objectIndex, PageObjForm, "group");
        if (!ClipIsHarmless(form, UngroupClipSlack)) { problem = "the group is clipped"; return false; }

        var (glyphs, pageFonts) = ReadPageText(page);
        var copies = new List<IntPtr>();
        problem = CopyGroupItems(page, form, Affine.Identity, glyphs, pageFonts, copies);
        if (problem is not null)
        {
            foreach (var copy in copies) Native.FPDFPageObj_Destroy(copy);
            return false;
        }

        // The copies take the group's place in the drawing order, then the group goes.
        for (int k = 0; k < copies.Count; k++)
        {
            Native.FPDFPage_InsertObjectAtIndex(page.Handle, copies[k], (nuint)(objectIndex + k));
            page.NewObjects.Add(copies[k]);
        }
        Native.FPDFPage_RemoveObject(page.Handle, form);
        Native.FPDFPageObj_Destroy(form);
        page.Dirty = page.ChangedExisting = true;
        return true;
    }

    /// <summary>
    /// Where each character of the page is drawn, by the object that draws it, and the fonts the
    /// page itself draws text with, by name (see ClashesWithPageFont). Needed to copy text objects.
    /// </summary>
    private (Dictionary<IntPtr, List<(char Ch, double X, double Y)>> Glyphs, Dictionary<string, List<IntPtr>> PageFonts) ReadPageText(PageHandle page)
    {
        // Where each character of the page is drawn, by the object that draws it.
        var glyphs = new Dictionary<IntPtr, List<(char Ch, double X, double Y)>>();
        var textPage = Native.FPDFText_LoadPage(page.Handle);
        if (textPage != IntPtr.Zero)
        {
            try
            {
                int n = Native.FPDFText_CountChars(textPage);
                for (int i = 0; i < n; i++)
                {
                    if (Native.FPDFText_IsGenerated(textPage, i) == 1) continue;
                    var obj = Native.FPDFText_GetTextObject(textPage, i);
                    if (obj == IntPtr.Zero) continue;
                    double x, y;
                    Native.FPDFText_GetCharOrigin(textPage, i, &x, &y);
                    if (!glyphs.TryGetValue(obj, out var list)) glyphs[obj] = list = [];
                    list.Add(((char)Native.FPDFText_GetUnicode(textPage, i), x, y));
                }
            }
            finally { Native.FPDFText_ClosePage(textPage); }
        }

        _glyphCodes.Clear();
        foreach (var list in glyphs.Values) DropPhantomSpaces(list);
        // Fonts the page itself draws text with, by name (see ClashesWithPageFont).
        var pageFonts = new Dictionary<string, List<IntPtr>>();
        int total = Native.FPDFPage_CountObjects(page.Handle);
        for (int i = 0; i < total; i++)
        {
            var obj = Native.FPDFPage_GetObject(page.Handle, i);
            if (Native.FPDFPageObj_GetType(obj) != Native.PAGEOBJ_TEXT) continue;
            var font = Native.FPDFTextObj_GetFont(obj);
            string name = FontName(obj);
            if (!pageFonts.TryGetValue(name, out var list)) pageFonts[name] = list = [];
            if (!list.Contains(font)) list.Add(font);
        }
        return (glyphs, pageFonts);
    }

    /// <summary>Copies a group's items (and those of groups inside it) as stand-alone objects.</summary>
    private string? CopyGroupItems(PageHandle page, IntPtr form, Affine outer, Dictionary<IntPtr, List<(char Ch, double X, double Y)>> glyphs, Dictionary<string, List<IntPtr>> pageFonts, List<IntPtr> copies)
    {
        Native.FS_MATRIX fm;
        if (Native.FPDFPageObj_GetMatrix(form, &fm) == 0) return "the group has no readable position";
        var formMatrix = Affine.Concat(new Affine(fm.a, fm.b, fm.c, fm.d, fm.e, fm.f), outer);

        int count = Native.FPDFFormObj_CountObjects(form);
        for (int i = 0; i < count; i++)
        {
            var item = Native.FPDFFormObj_GetObject(form, new CULong((uint)i));
            if (item == IntPtr.Zero) return "an item could not be read";
            if (!ClipIsHarmless(item, UngroupClipSlack)) return "an item in it is clipped";
            Native.FS_MATRIX im;
            if (Native.FPDFPageObj_GetMatrix(item, &im) == 0) return "an item has no readable position";
            var matrix = Affine.Concat(new Affine(im.a, im.b, im.c, im.d, im.e, im.f), formMatrix);

            switch (Native.FPDFPageObj_GetType(item))
            {
                case Native.PAGEOBJ_TEXT:
                    if (!glyphs.TryGetValue(item, out var chars) || chars.All(c => char.IsWhiteSpace(c.Ch))) break; // draws nothing
                    var text = ClashesWithPageFont(item, chars, pageFonts)
                        ? TextAsOutlines(item, chars, matrix)
                        : Pin(page, item, chars, always: true, matrix: matrix);
                    if (text == IntPtr.Zero) return "some of its text could not be copied";
                    copies.Add(text);
                    break;
                case PageObjPath:
                    var path = CopyPath(item, matrix);
                    if (path != IntPtr.Zero) copies.Add(path);
                    break;
                case Native.PAGEOBJ_IMAGE:
                    var image = CopyImage(page, item, matrix);
                    if (image == IntPtr.Zero) return "an image in it could not be copied";
                    copies.Add(image);
                    break;
                case PageObjForm:
                    if (CopyGroupItems(page, item, formMatrix, glyphs, pageFonts, copies) is { } inner) return inner;
                    break;
                default:
                    return "it contains shading, which cannot be rebuilt";
            }
        }
        return null;
    }

    /// <summary>
    /// True when the page already draws text with a different font of the same name. PDFium
    /// tells fonts apart by name only when it writes a page, so the group's text would then be
    /// drawn with the page's font: a different cut of the font that may lack its letters.
    /// </summary>
    private static bool ClashesWithPageFont(IntPtr textObj, List<(char Ch, double X, double Y)> chars, Dictionary<string, List<IntPtr>> pageFonts)
    {
        var font = Native.FPDFTextObj_GetFont(textObj);
        if (!pageFonts.TryGetValue(FontName(textObj), out var sameName)) return false;
        // Fonts that are not embedded (the standard ones) are the same font wherever they appear.
        // Embedded ones are separate cuts, each holding only its own letters: either would break the other.
        return sameName.Any(other => other != font && (Native.FPDFFont_GetIsEmbedded(other) == 1 || Native.FPDFFont_GetIsEmbedded(font) == 1));
    }

    /// <summary>
    /// Draws a text object's letters as filled shapes: the same look without depending on its
    /// font. Used where the text cannot be kept as text; it can then be moved but not retyped.
    /// </summary>
    private static IntPtr TextAsOutlines(IntPtr textObj, List<(char Ch, double X, double Y)> glyphs, Affine matrix)
    {
        const int LineTo = 0, BezierTo = 1;
        var font = Native.FPDFTextObj_GetFont(textObj);
        float size = 0;
        if (font == IntPtr.Zero || Native.FPDFTextObj_GetFontSize(textObj, &size) == 0 || size <= 0) return IntPtr.Zero;
        double a = matrix.A, b = matrix.B, norm2 = a * a + b * b;
        if (norm2 < 1e-12 || glyphs.Count == 0) return IntPtr.Zero;
        var (x0, y0) = (glyphs[0].X, glyphs[0].Y);

        IntPtr path = IntPtr.Zero;
        for (int k = 0; k < glyphs.Count; k++)
        {
            char ch = glyphs[k].Ch;
            if (char.IsWhiteSpace(ch) || ch == '\0') continue;
            // Where the letter starts along the line, in the text's own units.
            double along = ((glyphs[k].X - x0) * a + (glyphs[k].Y - y0) * b) / norm2;
            // A ligature is reported as its letters on one spot: draw the joined glyph once if the font has it.
            int same = 1;
            while (k + same < glyphs.Count && Math.Abs(glyphs[k + same].X - glyphs[k].X) < 0.01 && Math.Abs(glyphs[k + same].Y - glyphs[k].Y) < 0.01) same++;
            IntPtr outline = IntPtr.Zero;
            if (same > 1)
            {
                string joined = new(glyphs.Skip(k).Take(same).Select(g => g.Ch).ToArray());
                uint ligature = joined switch { "ff" => 0xFB00, "fi" => 0xFB01, "fl" => 0xFB02, "ffi" => 0xFB03, "ffl" => 0xFB04, _ => 0u };
                if (ligature != 0) outline = Native.FPDFFont_GetGlyphPath(font, ligature, size);
                if (outline != IntPtr.Zero && Native.FPDFGlyphPath_CountGlyphSegments(outline) > 0) k += same - 1;
                else outline = IntPtr.Zero;
            }
            if (outline == IntPtr.Zero)
            {
                // Letters of an unknown ligature are drawn one after another.
                if (k > 0 && Math.Abs(glyphs[k].X - glyphs[k - 1].X) < 0.01 && Math.Abs(glyphs[k].Y - glyphs[k - 1].Y) < 0.01)
                {
                    float before = 0;
                    Native.FPDFFont_GetGlyphWidth(font, glyphs[k - 1].Ch, size, &before);
                    along += before;
                }
                outline = Native.FPDFFont_GetGlyphPath(font, ch, size);
            }
            if (outline == IntPtr.Zero) continue;

            int segments = Native.FPDFGlyphPath_CountGlyphSegments(outline);
            var curve = new List<(float X, float Y)>();
            for (int s = 0; s < segments; s++)
            {
                var segment = Native.FPDFGlyphPath_GetGlyphPathSegment(outline, s);
                float x, y;
                Native.FPDFPathSegment_GetPoint(segment, &x, &y);
                x += (float)along;
                int type = Native.FPDFPathSegment_GetType(segment);
                if (path == IntPtr.Zero) path = Native.FPDFPageObj_CreateNewPath(x, y);
                else if (type == LineTo) Native.FPDFPath_LineTo(path, x, y);
                else if (type == BezierTo)
                {
                    curve.Add((x, y));
                    if (curve.Count == 3)
                    {
                        Native.FPDFPath_BezierTo(path, curve[0].X, curve[0].Y, curve[1].X, curve[1].Y, curve[2].X, curve[2].Y);
                        curve.Clear();
                    }
                }
                else Native.FPDFPath_MoveTo(path, x, y);
                if (Native.FPDFPathSegment_GetClose(segment) != 0) Native.FPDFPath_Close(path);
            }
        }
        if (path == IntPtr.Zero) return IntPtr.Zero;

        Native.FPDFPath_SetDrawMode(path, 2, 0); // filled, non-zero winding, as glyphs are
        uint r = 0, g = 0, bl = 0, al = 255;
        Native.FPDFPageObj_GetFillColor(textObj, &r, &g, &bl, &al);
        Native.FPDFPageObj_SetFillColor(path, r, g, bl, al);
        var m = (matrix with { E = x0, F = y0 }).ToNative();
        Native.FPDFPageObj_SetMatrix(path, &m);
        return path;
    }
    private static IntPtr CopyPath(IntPtr source, Affine matrix)
    {
        const int LineTo = 0, BezierTo = 1, MoveTo = 2;
        int segments = Native.FPDFPath_CountSegments(source);
        if (segments <= 0) return IntPtr.Zero;

        IntPtr path = IntPtr.Zero;
        var curve = new List<(float X, float Y)>();
        for (int s = 0; s < segments; s++)
        {
            var segment = Native.FPDFPath_GetPathSegment(source, s);
            float x, y;
            Native.FPDFPathSegment_GetPoint(segment, &x, &y);
            int type = Native.FPDFPathSegment_GetType(segment);
            if (path == IntPtr.Zero)
            {
                path = Native.FPDFPageObj_CreateNewPath(x, y);
                if (path == IntPtr.Zero) return IntPtr.Zero;
            }
            else if (type == MoveTo) Native.FPDFPath_MoveTo(path, x, y);
            else if (type == LineTo) Native.FPDFPath_LineTo(path, x, y);
            else if (type == BezierTo)
            {
                // A curve arrives as three points: two control points and its end.
                curve.Add((x, y));
                if (curve.Count == 3)
                {
                    Native.FPDFPath_BezierTo(path, curve[0].X, curve[0].Y, curve[1].X, curve[1].Y, curve[2].X, curve[2].Y);
                    curve.Clear();
                }
            }
            if (Native.FPDFPathSegment_GetClose(segment) != 0) Native.FPDFPath_Close(path);
        }

        int fill = 0, stroke = 0;
        Native.FPDFPath_GetDrawMode(source, &fill, &stroke);
        Native.FPDFPath_SetDrawMode(path, fill, stroke);
        uint r, g, b, a;
        if (Native.FPDFPageObj_GetFillColor(source, &r, &g, &b, &a) != 0) Native.FPDFPageObj_SetFillColor(path, r, g, b, a);
        if (Native.FPDFPageObj_GetStrokeColor(source, &r, &g, &b, &a) != 0) Native.FPDFPageObj_SetStrokeColor(path, r, g, b, a);
        float width = 1;
        if (Native.FPDFPageObj_GetStrokeWidth(source, &width) != 0) Native.FPDFPageObj_SetStrokeWidth(path, width);
        int cap = Native.FPDFPageObj_GetLineCap(source), join = Native.FPDFPageObj_GetLineJoin(source);
        if (cap >= 0) Native.FPDFPageObj_SetLineCap(path, cap);
        if (join >= 0) Native.FPDFPageObj_SetLineJoin(path, join);
        var m = matrix.ToNative();
        Native.FPDFPageObj_SetMatrix(path, &m);
        return path;
    }

    private IntPtr CopyImage(PageHandle page, IntPtr source, Affine matrix)
    {
        var bitmap = Native.FPDFImageObj_GetBitmap(source);
        if (bitmap == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            int w = Native.FPDFBitmap_GetWidth(bitmap), h = Native.FPDFBitmap_GetHeight(bitmap), stride = Native.FPDFBitmap_GetStride(bitmap);
            int format = Native.FPDFBitmap_GetFormat(bitmap); // 1 grey, 2 BGR, 3 BGRx, 4 BGRA
            byte* pixels = (byte*)Native.FPDFBitmap_GetBuffer(bitmap);
            if (w <= 0 || h <= 0 || pixels == null || format is < 1 or > 4) return IntPtr.Zero;

            var bgra = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                byte* row = pixels + (long)y * stride;
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    switch (format)
                    {
                        case 1: bgra[o] = bgra[o + 1] = bgra[o + 2] = row[x]; bgra[o + 3] = 255; break;
                        case 2: bgra[o] = row[x * 3]; bgra[o + 1] = row[x * 3 + 1]; bgra[o + 2] = row[x * 3 + 2]; bgra[o + 3] = 255; break;
                        default:
                            bgra[o] = row[x * 4]; bgra[o + 1] = row[x * 4 + 1]; bgra[o + 2] = row[x * 4 + 2];
                            bgra[o + 3] = format == 4 ? row[x * 4 + 3] : (byte)255;
                            break;
                    }
                }
            }
            var image = Native.FPDFPageObj_NewImageObj(Doc);
            if (image == IntPtr.Zero) return IntPtr.Zero;
            try { SetPixels(page, image, bgra, w, h); }
            catch (PdfException)
            {
                Native.FPDFPageObj_Destroy(image);
                return IntPtr.Zero;
            }
            var m = matrix.ToNative();
            Native.FPDFPageObj_SetMatrix(image, &m);
            return image;
        }
        finally { Native.FPDFBitmap_Destroy(bitmap); }
    }
}
