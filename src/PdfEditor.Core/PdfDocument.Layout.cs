using PdfEditor.Core.Interop;

namespace PdfEditor.Core;

public sealed unsafe partial class PdfDocument
{
    /// <summary>
    /// When an object already on a page changes, PDFium rewrites the page's original content.
    /// In doing so it drops text settings such as word spacing and character spacing, so text
    /// drawn with them (justified text, letter-spaced headings) would shift. Before that
    /// happens, each such text object is swapped for a new one in the same font with every
    /// character pinned to where it is now.
    /// </summary>
    internal void PreserveTextLayout(PageHandle page)
    {
        int count = Native.FPDFPage_CountObjects(page.Handle);
        var indexOf = new Dictionary<IntPtr, int>(count);
        for (int i = 0; i < count; i++)
        {
            var obj = Native.FPDFPage_GetObject(page.Handle, i);
            indexOf[obj] = i;
            KeepColours(obj);
        }

        var chars = new Dictionary<IntPtr, List<(char Ch, double X, double Y)>>();
        var textPage = Native.FPDFText_LoadPage(page.Handle);
        if (textPage == IntPtr.Zero) return;
        try
        {
            int n = Native.FPDFText_CountChars(textPage);
            for (int i = 0; i < n; i++)
            {
                if (Native.FPDFText_IsGenerated(textPage, i) == 1) continue;
                var obj = Native.FPDFText_GetTextObject(textPage, i);
                if (obj == IntPtr.Zero || !indexOf.ContainsKey(obj)) continue;
                double x, y;
                Native.FPDFText_GetCharOrigin(textPage, i, &x, &y);
                if (!chars.TryGetValue(obj, out var list)) chars[obj] = list = [];
                list.Add(((char)Native.FPDFText_GetUnicode(textPage, i), x, y));
            }
        }
        finally { Native.FPDFText_ClosePage(textPage); }

        var replacements = new List<(int Index, IntPtr Old, IntPtr New)>();
        foreach (var (obj, glyphs) in chars)
        {
            var pinned = Pin(page, obj, glyphs);
            if (pinned != IntPtr.Zero) replacements.Add((indexOf[obj], obj, pinned));
        }
        // Highest index first, so inserting and removing does not shift the ones still to do.
        foreach (var (index, old, pinned) in replacements.OrderByDescending(r => r.Index))
        {
            Native.FPDFPage_InsertObjectAtIndex(page.Handle, pinned, (nuint)index);
            Native.FPDFPage_RemoveObject(page.Handle, old);
            Native.FPDFPageObj_Destroy(old);
        }
    }

    /// <summary>
    /// PDFium only writes RGB colours back when it rewrites a page; colours in other colour spaces
    /// (CMYK, grey, ICC: common in print-ready PDFs) would turn black. Setting each object's
    /// colours again as RGB keeps what is seen on screen.
    /// </summary>
    private static void KeepColours(IntPtr obj)
    {
        int type = Native.FPDFPageObj_GetType(obj);
        if (type != Native.PAGEOBJ_TEXT && type != 2) return; // text and paths carry their own colours
        uint r, g, b, a;
        if (Native.FPDFPageObj_GetFillColor(obj, &r, &g, &b, &a) != 0) Native.FPDFPageObj_SetFillColor(obj, r, g, b, a);
        if (Native.FPDFPageObj_GetStrokeColor(obj, &r, &g, &b, &a) != 0) Native.FPDFPageObj_SetStrokeColor(obj, r, g, b, a);
    }

    /// <summary>A pinned copy of a text object, or zero when it needs none or cannot have one.</summary>
    private IntPtr Pin(PageHandle page, IntPtr obj, List<(char Ch, double X, double Y)> glyphs)
    {
        if (glyphs.Count < 2 || glyphs.Any(g => g.Ch == '\0' || char.IsSurrogate(g.Ch))) return IntPtr.Zero;
        var font = Native.FPDFTextObj_GetFont(obj);
        float size = 0;
        Native.FS_MATRIX m;
        if (font == IntPtr.Zero || Native.FPDFTextObj_GetFontSize(obj, &size) == 0 || size <= 0 || Native.FPDFPageObj_GetMatrix(obj, &m) == 0) return IntPtr.Zero;
        double a = m.a, b = m.b, norm2 = a * a + b * b;
        if (norm2 < 1e-12) return IntPtr.Zero;

        // Where each character sits along the line (text space), against where plain text would put it.
        var (x0, y0) = (glyphs[0].X, glyphs[0].Y);
        var positions = new float[glyphs.Count - 1];
        double natural = 0, tolerance = 0.01 * size;
        bool needed = false;
        for (int k = 1; k < glyphs.Count; k++)
        {
            double dx = glyphs[k].X - x0, dy = glyphs[k].Y - y0;
            double along = (dx * a + dy * b) / norm2;
            double across = (dy * a - dx * b) / norm2;
            if (Math.Abs(across) > tolerance) return IntPtr.Zero; // not on one straight line
            float w = 0;
            Native.FPDFFont_GetGlyphWidth(font, glyphs[k - 1].Ch, size, &w);
            natural += w;
            // Two characters at one spot are one joined glyph (a ligature such as "fl"). Drawn as
            // separate letters they must sit side by side, not on top of each other.
            if (k >= 1 && Math.Abs(glyphs[k].X - glyphs[k - 1].X) < 0.01 && Math.Abs(glyphs[k].Y - glyphs[k - 1].Y) < 0.01)
            {
                along = (k >= 2 ? positions[k - 2] : 0) + w;
                needed = true;
            }
            positions[k - 1] = (float)along;
            if (Math.Abs(along - natural) > tolerance) needed = true;
        }
        if (!needed || !ClipIsHarmless(obj)) return IntPtr.Zero;

        var pinned = Native.FPDFPageObj_CreateTextObj(Doc, font, size);
        if (pinned == IntPtr.Zero) return IntPtr.Zero;
        bool ok = Native.FPDFText_SetText(pinned, new string(glyphs.Select(g => g.Ch).ToArray())) != 0;
        if (ok)
        {
            var matrix = new Native.FS_MATRIX { a = m.a, b = m.b, c = m.c, d = m.d, e = (float)x0, f = (float)y0 };
            Native.FPDFPageObj_SetMatrix(pinned, &matrix);
            fixed (float* p = positions) ok = Native.FPDFText_SetPositions(pinned, p, (nuint)positions.Length) != 0;
        }
        if (!ok)
        {
            Native.FPDFPageObj_Destroy(pinned);
            return IntPtr.Zero;
        }
        uint r = 0, g = 0, bl = 0, al = 255;
        Native.FPDFPageObj_GetFillColor(obj, &r, &g, &bl, &al);
        Native.FPDFPageObj_SetFillColor(pinned, r, g, bl, al);
        int mode = Native.FPDFTextObj_GetTextRenderMode(obj);
        if (mode > 0)
        {
            Native.FPDFTextObj_SetTextRenderMode(pinned, mode);
            Native.FPDFPageObj_GetStrokeColor(obj, &r, &g, &bl, &al);
            Native.FPDFPageObj_SetStrokeColor(pinned, r, g, bl, al);
        }
        return pinned;
    }

    /// <summary>
    /// True when an object's clipping cannot cut it (the copy will not carry the clip). Only
    /// clip paths whose bounding box contains the whole object count as harmless.
    /// </summary>
    private static bool ClipIsHarmless(IntPtr obj)
    {
        var clip = Native.FPDFPageObj_GetClipPath(obj);
        if (clip == IntPtr.Zero) return true;
        float l = 0, bt = 0, r = 0, t = 0;
        if (Native.FPDFPageObj_GetBounds(obj, &l, &bt, &r, &t) == 0) return false;
        int paths = Native.FPDFClipPath_CountPaths(clip);
        if (paths < 0) return true; // an empty clip: nothing is cut
        for (int p = 0; p < paths; p++)
        {
            int segments = Native.FPDFClipPath_CountPathSegments(clip, p);
            if (segments <= 0) return false; // a text clip or unreadable path
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int s = 0; s < segments; s++)
            {
                float x, y;
                if (Native.FPDFPathSegment_GetPoint(Native.FPDFClipPath_GetPathSegment(clip, p, s), &x, &y) == 0) return false;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            const float slack = 0.5f;
            if (l < minX - slack || r > maxX + slack || bt < minY - slack || t > maxY + slack) return false;
        }
        return true;
    }
}
