using PdfEditor.Core.Interop;

namespace PdfEditor.Core;

public sealed unsafe partial class PdfDocument
{
    private const int SubtypeLink = 2;
    private const uint ActionUri = 3;

    /// <summary>
    /// Breaks a page in two at a height: everything that starts at or below <paramref name="y"/>
    /// (view space) moves to a new page of the same size inserted straight after, shifted up to
    /// the page's top margin. Comments, highlights and web links move with their content. Items
    /// in the bottom 8% of the page (footers, page numbers) and items that begin above the line
    /// stay where they are. Returns how many items moved; zero means nothing lay below and no
    /// page was added.
    /// </summary>
    public int InsertPageBreak(int pageIndex, double y) => BreakPage(pageIndex, y, cut: false);

    /// <summary>
    /// Cuts a page in two at a height, like cutting a sheet of paper: the page is shortened to end
    /// at <paramref name="y"/> and what starts at or below the line goes onto a new page just tall
    /// enough for the rest. Meant for very long pages (a web page saved as one sheet). Items that
    /// cross the line stay on the first page and are cut off at its new edge.
    /// Returns how many items moved; zero means nothing lay below and nothing changed.
    /// </summary>
    public int SplitPage(int pageIndex, double y) => BreakPage(pageIndex, y, cut: true);

    private int BreakPage(int pageIndex, double y, bool cut)
    {
        using var page = OpenPage(pageIndex);
        double width = page.Width, height = page.Height;
        if (cut && (y <= 1 || y >= height - 1)) return 0;
        double footerTop = cut ? double.MaxValue : height * 0.92;

        var all = new List<(IntPtr Obj, PdfRect Bounds)>();
        int count = Native.FPDFPage_CountObjects(page.Handle);
        for (int i = 0; i < count; i++)
        {
            var obj = Native.FPDFPage_GetObject(page.Handle, i);
            all.Add((obj, ObjectBounds(page, obj)));
        }
        var moving = all.Where(o => o.Bounds.Y >= y - 0.5 && o.Bounds.Y < footerTop).ToList();
        if (moving.Count == 0) return 0;

        // How far up the moved content goes: to the top of the new page when cutting, otherwise
        // to where the page's content starts now (its top margin).
        double dy = cut ? -y : Math.Clamp(all.Min(o => o.Bounds.Y), 18, Math.Max(18, height / 4)) - moving.Min(o => o.Bounds.Y);
        double newHeight = cut ? height - y : height;

        // A new page straight after this one, turned the same way.
        int rotation = Native.FPDFPage_GetRotation(page.Handle);
        bool sideways = rotation % 2 == 1;
        var created = Native.FPDFPage_New(Doc, pageIndex + 1, sideways ? newHeight : width, sideways ? width : newHeight);
        if (created == IntPtr.Zero) throw new PdfException("Could not add a page.");
        Native.FPDFPage_SetRotation(created, rotation);
        using var next = new PageHandle(created, this, pageIndex + 1) { Dirty = true, ChangedExisting = true };

        // From this page's own coordinates, via what is seen on screen, to the new page's coordinates.
        // (The two pages need not share an origin, so this is more than a shift.)
        var toNext = Affine.Concat(Affine.Concat(page.UserToView, new Affine(1, 0, 0, 1, 0, dy)), next.ViewToUser);
        foreach (var (obj, _) in moving)
        {
            if (Native.FPDFPage_RemoveObject(page.Handle, obj) == 0) continue;
            TransformObject(obj, toNext);
            Native.FPDFPage_InsertObject(next.Handle, obj);
        }
        MoveAnnotationsToPage(page, next, y, footerTop, toNext);

        if (cut)
        {
            // Shorten this page to end at the line.
            var box = page.ViewToUser.MapBounds(0, 0, width, y);
            // Both boxes: a crop box inherited from the page tree would otherwise keep the old size.
            Native.FPDFPage_SetMediaBox(page.Handle, (float)box.X, (float)box.Y, (float)box.Right, (float)box.Bottom);
            Native.FPDFPage_SetCropBox(page.Handle, (float)box.X, (float)box.Y, (float)box.Right, (float)box.Bottom);
        }

        page.Dirty = page.ChangedExisting = true;
        _charsByFont = null;
        return moving.Count;
    }

    /// <summary>
    /// Heights (view space) where a page can be broken without cutting through anything: the
    /// middles of the empty bands between rows of content. Page-sized backgrounds, frames and
    /// long rules are ignored, since no line could avoid them.
    /// </summary>
    public IReadOnlyList<double> GetBreakPositions(int pageIndex)
    {
        using var page = OpenPage(pageIndex);
        double sheet = Math.Min(page.Height, 1.414 * page.Width); // the height of an ordinary sheet this wide
        var spans = new List<(double Top, double Bottom)>();
        int count = Native.FPDFPage_CountObjects(page.Handle);
        for (int i = 0; i < count; i++)
        {
            var obj = Native.FPDFPage_GetObject(page.Handle, i);
            var b = ObjectBounds(page, obj);
            if (b.Height <= 0 && b.Width <= 0) continue;
            int type = Native.FPDFPageObj_GetType(obj);
            bool isContent = type is Native.PAGEOBJ_TEXT or Native.PAGEOBJ_IMAGE;
            if (b.Width * b.Height > 0.6 * page.Width * sheet) continue;        // a background
            if (!isContent && b.Height > 0.25 * sheet) continue;                // a frame, panel or long rule
            spans.Add((b.Y, b.Bottom));
        }
        var positions = new List<double>();
        double covered = double.NaN;
        foreach (var (top, bottom) in spans.OrderBy(s => s.Top))
        {
            if (!double.IsNaN(covered) && top - covered >= 1) positions.Add((covered + top) / 2);
            covered = double.IsNaN(covered) ? bottom : Math.Max(covered, bottom);
        }
        return positions.Where(p => p > 1 && p < page.Height - 1).ToList();
    }

    /// <summary>
    /// Recreates the comments, text markups and web links that lie below a line on another page,
    /// then removes the originals. <paramref name="toNext"/> maps this page's coordinates to the other's.
    /// </summary>
    private void MoveAnnotationsToPage(PageHandle from, PageHandle to, double fromY, double footerTop, Affine toNext)
    {
        var remove = new List<int>();
        int count = Native.FPDFPage_GetAnnotCount(from.Handle);
        for (int i = 0; i < count; i++)
        {
            var annot = Native.FPDFPage_GetAnnot(from.Handle, i);
            if (annot == IntPtr.Zero) continue;
            try
            {
                int subtype = Native.FPDFAnnot_GetSubtype(annot);
                bool markup = subtype is SubtypeText or SubtypeHighlight or SubtypeUnderline or SubtypeSquiggly or SubtypeStrikeout;
                string? uri = subtype == SubtypeLink ? LinkUri(annot) : null;
                if (!markup && uri is null) continue; // other kinds (form fields, links within the document) stay
                Native.FS_RECTF rect;
                if (Native.FPDFAnnot_GetRect(annot, &rect) == 0) continue;
                var view = from.UserToView.MapBounds(rect.left, rect.bottom, rect.right, rect.top);
                if (view.Y < fromY - 0.5 || view.Y >= footerTop) continue;

                var copy = Native.FPDFPage_CreateAnnot(to.Handle, subtype);
                if (copy == IntPtr.Zero) continue;
                try
                {
                    if (uri is not null)
                    {
                        Native.FPDFAnnot_SetURI(copy, uri);
                        Native.FPDFAnnot_SetBorder(copy, 0, 0, 0); // links on a page are not outlined
                    }
                    else
                    {
                        uint r = 0, g = 0, b = 0, a = 255;
                        if (Native.FPDFAnnot_GetColor(annot, 0, &r, &g, &b, &a) != 0) Native.FPDFAnnot_SetColor(copy, 0, r, g, b, a);
                        Native.FPDFAnnot_SetFlags(copy, Native.FPDF_ANNOT_FLAG_PRINT);
                        foreach (var key in new[] { "Contents", "T", "CreationDate", "M" })
                        {
                            string value = GetString(annot, key);
                            if (value.Length > 0) Native.FPDFAnnot_SetStringValue(copy, key, value);
                        }
                        int quads = (int)Native.FPDFAnnot_CountAttachmentPoints(annot);
                        for (int q = 0; q < quads; q++)
                        {
                            Native.FS_QUADPOINTSF quad;
                            if (Native.FPDFAnnot_GetAttachmentPoints(annot, (nuint)q, &quad) == 0) continue;
                            (quad.x1, quad.y1) = Map(toNext, quad.x1, quad.y1);
                            (quad.x2, quad.y2) = Map(toNext, quad.x2, quad.y2);
                            (quad.x3, quad.y3) = Map(toNext, quad.x3, quad.y3);
                            (quad.x4, quad.y4) = Map(toNext, quad.x4, quad.y4);
                            Native.FPDFAnnot_AppendAttachmentPoints(copy, &quad);
                        }
                    }
                    var moved = toNext.MapBounds(rect.left, rect.bottom, rect.right, rect.top);
                    var placed = new Native.FS_RECTF { left = (float)moved.X, bottom = (float)moved.Y, right = (float)moved.Right, top = (float)moved.Bottom };
                    Native.FPDFAnnot_SetRect(copy, &placed);
                    remove.Add(i);
                }
                finally { Native.FPDFPage_CloseAnnot(copy); }
            }
            finally { Native.FPDFPage_CloseAnnot(annot); }
        }
        foreach (int index in remove.OrderByDescending(i => i)) Native.FPDFPage_RemoveAnnot(from.Handle, index);

        static (float, float) Map(Affine t, float x, float y)
        {
            var p = t.Apply(x, y);
            return ((float)p.X, (float)p.Y);
        }
    }

    /// <summary>The web address a link annotation opens, or null when it does something else.</summary>
    private string? LinkUri(IntPtr annot)
    {
        var link = Native.FPDFAnnot_GetLink(annot);
        if (link == IntPtr.Zero) return null;
        var action = Native.FPDFLink_GetAction(link);
        if (action == IntPtr.Zero || Native.FPDFAction_GetType(action).Value != ActionUri) return null;
        int length = (int)Native.FPDFAction_GetURIPath(Doc, action, null, new System.Runtime.InteropServices.CULong(0)).Value;
        if (length <= 1) return null;
        var buffer = new byte[length];
        fixed (byte* p = buffer) Native.FPDFAction_GetURIPath(Doc, action, p, new System.Runtime.InteropServices.CULong((uint)length));
        return System.Text.Encoding.ASCII.GetString(buffer, 0, length - 1);
    }
}
