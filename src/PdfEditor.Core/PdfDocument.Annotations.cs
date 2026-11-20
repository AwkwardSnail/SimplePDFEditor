using System.Runtime.InteropServices;
using PdfEditor.Core.Interop;

namespace PdfEditor.Core;

// Comments, highlights and text search.
public sealed unsafe partial class PdfDocument
{
    private const int SubtypeText = 1, SubtypeFreeText = 3, SubtypeHighlight = 9, SubtypeUnderline = 10, SubtypeSquiggly = 11, SubtypeStrikeout = 12;
    private const double CommentIconSize = 20;

    /// <summary>Adds a sticky-note comment whose icon's top-left corner is at (x, y). Returns its annotation index.</summary>
    public int AddComment(int pageIndex, double x, double y, string text, string? author = null, PdfColor? color = null)
    {
        using var page = OpenPage(pageIndex);
        var annot = CreateAnnotation(page, SubtypeText, text, author, color ?? PdfColor.Yellow);
        try
        {
            SetRect(annot, page.ViewToUser.MapBounds(x, y, x + CommentIconSize, y + CommentIconSize));
            return Native.FPDFPage_GetAnnotCount(page.Handle) - 1;
        }
        finally { Native.FPDFPage_CloseAnnot(annot); }
    }

    /// <summary>
    /// Highlights the given areas (typically one rectangle per line of text) as a single
    /// annotation, optionally carrying a comment. Returns its annotation index.
    /// </summary>
    public int AddHighlight(int pageIndex, IEnumerable<PdfRect> areas, PdfColor? color = null, string? comment = null, string? author = null) =>
        AddMarkup(pageIndex, AnnotationKind.Highlight, areas, color, comment, author);

    /// <summary>Adds a highlight, underline, strikeout or squiggly annotation over the given areas.</summary>
    public int AddMarkup(int pageIndex, AnnotationKind kind, IEnumerable<PdfRect> areas, PdfColor? color = null, string? comment = null, string? author = null)
    {
        int subtype = kind switch
        {
            AnnotationKind.Highlight => SubtypeHighlight,
            AnnotationKind.Underline => SubtypeUnderline,
            AnnotationKind.Strikeout => SubtypeStrikeout,
            AnnotationKind.Squiggly => SubtypeSquiggly,
            _ => throw new ArgumentException("Only highlight, underline, strikeout and squiggly are text markups.", nameof(kind)),
        };
        var rects = areas.Where(a => a.Width > 0 && a.Height > 0).ToList();
        if (rects.Count == 0) throw new ArgumentException("Nothing to mark: no area was given.", nameof(areas));

        using var page = OpenPage(pageIndex);
        var annot = CreateAnnotation(page, subtype, comment ?? "", author, color ?? (kind == AnnotationKind.Highlight ? PdfColor.Yellow : new PdfColor(220, 40, 40)));
        try
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var r in rects)
            {
                // Quad order used by PDF viewers: top-left, top-right, bottom-left, bottom-right.
                var tl = page.ViewToUser.Apply(r.X, r.Y);
                var tr = page.ViewToUser.Apply(r.Right, r.Y);
                var bl = page.ViewToUser.Apply(r.X, r.Bottom);
                var br = page.ViewToUser.Apply(r.Right, r.Bottom);
                var quad = new Native.FS_QUADPOINTSF
                {
                    x1 = (float)tl.X, y1 = (float)tl.Y, x2 = (float)tr.X, y2 = (float)tr.Y,
                    x3 = (float)bl.X, y3 = (float)bl.Y, x4 = (float)br.X, y4 = (float)br.Y,
                };
                if (Native.FPDFAnnot_AppendAttachmentPoints(annot, &quad) == 0) throw new PdfException("Could not add the markup.");
                foreach (var p in new[] { tl, tr, bl, br })
                {
                    minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                    minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                }
            }
            var rect = new Native.FS_RECTF { left = (float)minX, bottom = (float)minY, right = (float)maxX, top = (float)maxY };
            Native.FPDFAnnot_SetRect(annot, &rect);
            return Native.FPDFPage_GetAnnotCount(page.Handle) - 1;
        }
        finally { Native.FPDFPage_CloseAnnot(annot); }
    }

    /// <summary>Highlights every occurrence of a phrase on a page. Returns how many were highlighted.</summary>
    public int HighlightText(int pageIndex, string phrase, PdfColor? color = null, bool matchCase = false)
    {
        var matches = FindText(pageIndex, phrase, matchCase);
        foreach (var match in matches) AddHighlight(pageIndex, match.Rects, color);
        return matches.Count;
    }

    public IReadOnlyList<AnnotationInfo> GetAnnotations(int pageIndex)
    {
        using var page = OpenPage(pageIndex);
        var list = new List<AnnotationInfo>();
        int count = Native.FPDFPage_GetAnnotCount(page.Handle);
        for (int i = 0; i < count; i++)
        {
            var annot = Native.FPDFPage_GetAnnot(page.Handle, i);
            if (annot == IntPtr.Zero) continue;
            try
            {
                var kind = Native.FPDFAnnot_GetSubtype(annot) switch
                {
                    SubtypeText => AnnotationKind.Comment,
                    SubtypeHighlight => AnnotationKind.Highlight,
                    SubtypeUnderline => AnnotationKind.Underline,
                    SubtypeStrikeout => AnnotationKind.Strikeout,
                    SubtypeSquiggly => AnnotationKind.Squiggly,
                    SubtypeFreeText => AnnotationKind.FreeText,
                    _ => AnnotationKind.Other,
                };
                Native.FS_RECTF rect;
                Native.FPDFAnnot_GetRect(annot, &rect);
                uint r = 0, g = 0, b = 0, a = 255;
                Native.FPDFAnnot_GetColor(annot, 0, &r, &g, &b, &a);
                list.Add(new AnnotationInfo(i, kind, page.UserToView.MapBounds(rect.left, rect.bottom, rect.right, rect.top),
                    GetString(annot, "Contents"), GetString(annot, "T"), new PdfColor((byte)r, (byte)g, (byte)b, (byte)a)));
            }
            finally { Native.FPDFPage_CloseAnnot(annot); }
        }
        return list;
    }

    /// <summary>Changes the comment text of an annotation.</summary>
    public void SetAnnotationText(int pageIndex, int annotationIndex, string text)
    {
        using var page = OpenPage(pageIndex);
        var annot = Native.FPDFPage_GetAnnot(page.Handle, annotationIndex);
        if (annot == IntPtr.Zero) throw new ArgumentOutOfRangeException(nameof(annotationIndex));
        try
        {
            Native.FPDFAnnot_SetStringValue(annot, "Contents", text);
            Native.FPDFAnnot_SetStringValue(annot, "M", PdfDate(DateTimeOffset.Now));
        }
        finally { Native.FPDFPage_CloseAnnot(annot); }
    }

    /// <summary>Moves a comment, highlight or other annotation by an offset in view space (points, y downwards).</summary>
    public void MoveAnnotation(int pageIndex, int annotationIndex, double dx, double dy)
    {
        using var page = OpenPage(pageIndex);
        if (annotationIndex < 0 || annotationIndex >= Native.FPDFPage_GetAnnotCount(page.Handle)) throw new ArgumentOutOfRangeException(nameof(annotationIndex));
        var annot = Native.FPDFPage_GetAnnot(page.Handle, annotationIndex);
        if (annot == IntPtr.Zero) throw new PdfException("Could not move the annotation.");
        try
        {
            // The offset in the page's own coordinates (the page may be turned).
            var v = page.ViewToUser;
            float ux = (float)(v.A * dx + v.C * dy), uy = (float)(v.B * dx + v.D * dy);
            int quads = (int)Native.FPDFAnnot_CountAttachmentPoints(annot);
            for (int q = 0; q < quads; q++)
            {
                Native.FS_QUADPOINTSF quad;
                if (Native.FPDFAnnot_GetAttachmentPoints(annot, (nuint)q, &quad) == 0) continue;
                quad.x1 += ux; quad.x2 += ux; quad.x3 += ux; quad.x4 += ux;
                quad.y1 += uy; quad.y2 += uy; quad.y3 += uy; quad.y4 += uy;
                Native.FPDFAnnot_SetAttachmentPoints(annot, (nuint)q, &quad);
            }
            Native.FS_RECTF rect;
            if (Native.FPDFAnnot_GetRect(annot, &rect) == 0) throw new PdfException("Could not move the annotation.");
            rect.left += ux; rect.right += ux;
            rect.top += uy; rect.bottom += uy;
            Native.FPDFAnnot_SetRect(annot, &rect);
            Native.FPDFAnnot_SetStringValue(annot, "M", PdfDate(DateTimeOffset.Now));
        }
        finally { Native.FPDFPage_CloseAnnot(annot); }
    }

    public void RemoveAnnotation(int pageIndex, int annotationIndex)
    {
        using var page = OpenPage(pageIndex);
        if (annotationIndex < 0 || annotationIndex >= Native.FPDFPage_GetAnnotCount(page.Handle))
            throw new ArgumentOutOfRangeException(nameof(annotationIndex));
        if (Native.FPDFPage_RemoveAnnot(page.Handle, annotationIndex) == 0) throw new PdfException("Could not remove the annotation.");
    }

    private static IntPtr CreateAnnotation(PageHandle page, int subtype, string contents, string? author, PdfColor color)
    {
        var annot = Native.FPDFPage_CreateAnnot(page.Handle, subtype);
        if (annot == IntPtr.Zero) throw new PdfException("Could not create the annotation.");
        Native.FPDFAnnot_SetColor(annot, 0, color.R, color.G, color.B, color.A);
        Native.FPDFAnnot_SetFlags(annot, Native.FPDF_ANNOT_FLAG_PRINT);
        Native.FPDFAnnot_SetStringValue(annot, "Contents", contents);
        if (!string.IsNullOrEmpty(author)) Native.FPDFAnnot_SetStringValue(annot, "T", author);
        string now = PdfDate(DateTimeOffset.Now);
        Native.FPDFAnnot_SetStringValue(annot, "CreationDate", now);
        Native.FPDFAnnot_SetStringValue(annot, "M", now);
        return annot;
    }

    private static void SetRect(IntPtr annot, PdfRect userBounds)
    {
        // In user space y grows upwards, so the rectangle's "top" is its larger y.
        var rect = new Native.FS_RECTF
        {
            left = (float)userBounds.X, bottom = (float)userBounds.Y,
            right = (float)userBounds.Right, top = (float)userBounds.Bottom,
        };
        Native.FPDFAnnot_SetRect(annot, &rect);
    }

    private static string GetString(IntPtr annot, string key)
    {
        int bytes = (int)Native.FPDFAnnot_GetStringValue(annot, key, null, new CULong(0)).Value;
        if (bytes <= 2) return "";
        var buffer = new char[bytes / 2];
        fixed (char* p = buffer) Native.FPDFAnnot_GetStringValue(annot, key, p, new CULong((uint)bytes));
        return new string(buffer, 0, buffer.Length - 1);
    }

    private static string PdfDate(DateTimeOffset t)
    {
        var o = t.Offset;
        return $"D:{t:yyyyMMddHHmmss}{(o < TimeSpan.Zero ? '-' : '+')}{Math.Abs(o.Hours):00}'{Math.Abs(o.Minutes):00}'";
    }

    // ------------------------------------------------------------------ search and selection

    /// <summary>Finds every occurrence of a phrase on a page.</summary>
    public IReadOnlyList<TextMatch> FindText(int pageIndex, string phrase, bool matchCase = false)
    {
        var matches = new List<TextMatch>();
        if (string.IsNullOrEmpty(phrase)) return matches;
        using var page = OpenPage(pageIndex);
        var textPage = Native.FPDFText_LoadPage(page.Handle);
        if (textPage == IntPtr.Zero) return matches;
        try
        {
            var search = Native.FPDFText_FindStart(textPage, phrase, new CULong(matchCase ? Native.FPDF_MATCHCASE : 0), 0);
            if (search == IntPtr.Zero) return matches;
            try
            {
                while (Native.FPDFText_FindNext(search) != 0)
                {
                    int start = Native.FPDFText_GetSchResultIndex(search), length = Native.FPDFText_GetSchCount(search);
                    matches.Add(new TextMatch(start, length, RangeRects(page, textPage, start, length)));
                }
            }
            finally { Native.FPDFText_FindClose(search); }
        }
        finally { Native.FPDFText_ClosePage(textPage); }
        return matches;
    }

    /// <summary>Index of the character at a point, or -1. Pair with <see cref="GetTextRects"/> for drag selection.</summary>
    public int GetCharIndexAt(int pageIndex, double x, double y, double tolerance = 4)
    {
        using var page = OpenPage(pageIndex);
        var textPage = Native.FPDFText_LoadPage(page.Handle);
        if (textPage == IntPtr.Zero) return -1;
        try
        {
            var p = page.ViewToUser.Apply(x, y);
            return Math.Max(-1, Native.FPDFText_GetCharIndexAtPos(textPage, p.X, p.Y, tolerance, tolerance));
        }
        finally { Native.FPDFText_ClosePage(textPage); }
    }

    /// <summary>Rectangles (one per line) covering a range of characters, ready to pass to <see cref="AddHighlight"/>.</summary>
    public IReadOnlyList<PdfRect> GetTextRects(int pageIndex, int charIndex, int length)
    {
        using var page = OpenPage(pageIndex);
        var textPage = Native.FPDFText_LoadPage(page.Handle);
        if (textPage == IntPtr.Zero) return [];
        try { return RangeRects(page, textPage, charIndex, length); }
        finally { Native.FPDFText_ClosePage(textPage); }
    }

    private static List<PdfRect> RangeRects(PageHandle page, IntPtr textPage, int start, int length)
    {
        var rects = new List<PdfRect>();
        int count = Native.FPDFText_CountRects(textPage, start, length);
        for (int i = 0; i < count; i++)
        {
            double l, t, r, b;
            if (Native.FPDFText_GetRect(textPage, i, &l, &t, &r, &b) != 0)
                rects.Add(page.UserToView.MapBounds(l, b, r, t));
        }
        return rects;
    }
}
