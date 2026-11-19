using PdfEditor.Core.Interop;

namespace PdfEditor.Core;

public sealed unsafe partial class PdfDocument
{
    /// <summary>
    /// Breaks a page in two at a height: everything that starts at or below <paramref name="y"/>
    /// (view space) moves to a new page inserted straight after, shifted up to the page's top
    /// margin. Comments and highlights move with their content. Items in the bottom 8% of the
    /// page (footers, page numbers) and items that begin above the line stay where they are.
    /// Returns how many items moved; zero means nothing lay below and no page was added.
    /// </summary>
    public int InsertPageBreak(int pageIndex, double y)
    {
        using var page = OpenPage(pageIndex);
        double footerTop = page.Height * 0.92;

        var all = new List<(IntPtr Obj, PdfRect Bounds)>();
        int count = Native.FPDFPage_CountObjects(page.Handle);
        for (int i = 0; i < count; i++)
        {
            var obj = Native.FPDFPage_GetObject(page.Handle, i);
            all.Add((obj, ObjectBounds(page, obj)));
        }
        var moving = all.Where(o => o.Bounds.Y >= y - 0.5 && o.Bounds.Y < footerTop).ToList();
        if (moving.Count == 0) return 0;

        // The moved content starts where the page's content starts now (its top margin).
        double topMargin = Math.Clamp(all.Min(o => o.Bounds.Y), 18, Math.Max(18, page.Height / 4));
        double dy = topMargin - moving.Min(o => o.Bounds.Y);
        var shift = (X: page.ViewToUser.C * dy, Y: page.ViewToUser.D * dy);

        // A new page of the same size and rotation, straight after this one.
        int rotation = Native.FPDFPage_GetRotation(page.Handle);
        bool sideways = rotation % 2 == 1;
        var created = Native.FPDFPage_New(Doc, pageIndex + 1, sideways ? page.Height : page.Width, sideways ? page.Width : page.Height);
        if (created == IntPtr.Zero) throw new PdfException("Could not add a page.");
        Native.FPDFPage_SetRotation(created, rotation);
        using var next = new PageHandle(created, this) { Dirty = true, ChangedExisting = true };

        foreach (var (obj, _) in moving)
        {
            if (Native.FPDFPage_RemoveObject(page.Handle, obj) == 0) continue;
            Native.FPDFPageObj_Transform(obj, 1, 0, 0, 1, shift.X, shift.Y);
            Native.FPDFPage_InsertObject(next.Handle, obj);
        }
        MoveAnnotationsToPage(page, next, y, footerTop, shift);

        page.Dirty = page.ChangedExisting = true;
        _charsByFont = null;
        return moving.Count;
    }

    /// <summary>Recreates comments and text markups that lie below a line on another page, then removes the originals.</summary>
    private static void MoveAnnotationsToPage(PageHandle from, PageHandle to, double fromY, double footerTop, (double X, double Y) shift)
    {
        float dx = (float)shift.X, dy = (float)shift.Y;
        var remove = new List<int>();
        int count = Native.FPDFPage_GetAnnotCount(from.Handle);
        for (int i = 0; i < count; i++)
        {
            var annot = Native.FPDFPage_GetAnnot(from.Handle, i);
            if (annot == IntPtr.Zero) continue;
            try
            {
                int subtype = Native.FPDFAnnot_GetSubtype(annot);
                if (subtype is not (SubtypeText or SubtypeHighlight or SubtypeUnderline or SubtypeSquiggly or SubtypeStrikeout)) continue;
                Native.FS_RECTF rect;
                if (Native.FPDFAnnot_GetRect(annot, &rect) == 0) continue;
                var view = from.UserToView.MapBounds(rect.left, rect.bottom, rect.right, rect.top);
                if (view.Y < fromY - 0.5 || view.Y >= footerTop) continue;

                var copy = Native.FPDFPage_CreateAnnot(to.Handle, subtype);
                if (copy == IntPtr.Zero) continue;
                try
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
                        quad.x1 += dx; quad.x2 += dx; quad.x3 += dx; quad.x4 += dx;
                        quad.y1 += dy; quad.y2 += dy; quad.y3 += dy; quad.y4 += dy;
                        Native.FPDFAnnot_AppendAttachmentPoints(copy, &quad);
                    }
                    rect.left += dx; rect.right += dx; rect.top += dy; rect.bottom += dy;
                    Native.FPDFAnnot_SetRect(copy, &rect);
                    remove.Add(i);
                }
                finally { Native.FPDFPage_CloseAnnot(copy); }
            }
            finally { Native.FPDFPage_CloseAnnot(annot); }
        }
        foreach (int index in remove.OrderByDescending(i => i)) Native.FPDFPage_RemoveAnnot(from.Handle, index);
    }
}
