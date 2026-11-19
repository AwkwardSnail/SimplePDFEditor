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
        _glyphCodes.Clear(); // font handles from an earlier page are no longer valid
        KeepGradients(page); // PDFium leaves gradients out when it writes a page
        int count = Native.FPDFPage_CountObjects(page.Handle);
        var indexOf = new Dictionary<IntPtr, int>(count);
        for (int i = 0; i < count; i++)
        {
            var obj = Native.FPDFPage_GetObject(page.Handle, i);
            indexOf[obj] = i;
            if (!page.NewObjects.Contains(obj)) KeepColours(obj);
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
        foreach (var glyphs in chars.Values) DropPhantomSpaces(glyphs);
        KeepType3Text(page, indexOf, chars, replacements); // PDFium leaves Type 3 text out when it writes a page
        SeparateSameNamedFonts(page, indexOf, chars, replacements);
        foreach (var (obj, glyphs) in chars)
        {
            if (page.NewObjects.Contains(obj)) continue; // made in this session: already written exactly as wanted
            var pinned = Pin(page, obj, glyphs);
            if (pinned != IntPtr.Zero) replacements.Add((indexOf[obj], obj, pinned));
        }
        // Highest index first, so inserting and removing does not shift the ones still to do.
        foreach (var (index, old, pinned) in replacements.OrderByDescending(r => r.Index))
        {
            if (pinned != IntPtr.Zero) Native.FPDFPage_InsertObjectAtIndex(page.Handle, pinned, (nuint)index);
            Native.FPDFPage_RemoveObject(page.Handle, old);
            Native.FPDFPageObj_Destroy(old);
        }
    }

    /// <summary>
    /// PDFium tells fonts apart only by name and kind when it writes a page. A page can hold
    /// several different embedded fonts with one name (separate cuts, each with its own letters
    /// and codes; PDFs made on macOS often do), and all their text would then be drawn with
    /// whichever comes first: wrong or missing letters. So for each name the most-used font
    /// keeps its text, and text in the others is redrawn as shapes that look the same: outlines
    /// of its letters, or a small picture of it where the letters are not known.
    /// </summary>
    private void SeparateSameNamedFonts(PageHandle page, Dictionary<IntPtr, int> indexOf,
        Dictionary<IntPtr, List<(char Ch, double X, double Y)>> chars, List<(int Index, IntPtr Old, IntPtr New)> replacements)
    {
        var textObjects = new List<(IntPtr Obj, IntPtr Font, string Name)>();
        var uses = new Dictionary<string, Dictionary<IntPtr, int>>();
        foreach (var obj in indexOf.Keys)
        {
            if (Native.FPDFPageObj_GetType(obj) != Native.PAGEOBJ_TEXT) continue;
            var font = Native.FPDFTextObj_GetFont(obj);
            if (font == IntPtr.Zero) continue;
            string name = FontName(obj);
            if (name.Length == 0) continue; // Type 3 text, already seen to by KeepType3Text
            textObjects.Add((obj, font, name));
            if (!uses.TryGetValue(name, out var fonts)) uses[name] = fonts = [];
            fonts[font] = fonts.GetValueOrDefault(font) + 1;
        }

        var displaced = new HashSet<IntPtr>();
        foreach (var fonts in uses.Values)
        {
            if (fonts.Count < 2 || !fonts.Keys.Any(f => Native.FPDFFont_GetIsEmbedded(f) == 1)) continue; // standard fonts are one font everywhere
            var main = fonts.OrderByDescending(f => f.Value).First().Key;
            foreach (var other in fonts.Keys.Where(f => f != main)) displaced.Add(other);
        }
        if (displaced.Count == 0) return;

        foreach (var (obj, font, _) in textObjects)
        {
            if (!displaced.Contains(font)) continue;
            IntPtr shapes = IntPtr.Zero;
            Native.FS_MATRIX m;
            if (chars.TryGetValue(obj, out var glyphs) && glyphs.Any(g => !char.IsWhiteSpace(g.Ch) && g.Ch != '\0') && glyphs.All(g => g.Ch != '\0')
                && Native.FPDFPageObj_GetMatrix(obj, &m) != 0)
                shapes = TextAsOutlines(obj, glyphs, new Affine(m.a, m.b, m.c, m.d, m.e, m.f));
            if (shapes == IntPtr.Zero) shapes = TextAsPicture(page, obj); // zero when it draws nothing at all
            replacements.Add((indexOf[obj], obj, shapes));
            chars.Remove(obj);
        }
    }

    /// <summary>A text object as a small picture of itself, or zero when it shows nothing.</summary>
    private IntPtr TextAsPicture(PageHandle page, IntPtr textObj)
    {
        const float scale = 4; // 288 pixels per inch
        float l = 0, b = 0, r = 0, t = 0;
        if (Native.FPDFPageObj_GetBounds(textObj, &l, &b, &r, &t) == 0 || r - l <= 0 || t - b <= 0) return IntPtr.Zero;
        var bitmap = Native.FPDFTextObj_GetRenderedBitmap(Doc, page.Handle, textObj, scale);
        if (bitmap == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            int w = Native.FPDFBitmap_GetWidth(bitmap), h = Native.FPDFBitmap_GetHeight(bitmap), stride = Native.FPDFBitmap_GetStride(bitmap);
            byte* pixels = (byte*)Native.FPDFBitmap_GetBuffer(bitmap);
            if (w <= 0 || h <= 0 || pixels == null || Native.FPDFBitmap_GetFormat(bitmap) != Native.FPDFBitmap_BGRA) return IntPtr.Zero;
            var bgra = new byte[w * h * 4];
            bool visible = false;
            for (int y = 0; y < h; y++)
            {
                new ReadOnlySpan<byte>(pixels + (long)y * stride, w * 4).CopyTo(bgra.AsSpan(y * w * 4));
                for (int x = 3; x < w * 4 && !visible; x += 4) visible = bgra[y * w * 4 + x] != 0;
            }
            if (!visible) return IntPtr.Zero;
            var image = Native.FPDFPageObj_NewImageObj(Doc);
            if (image == IntPtr.Zero) return IntPtr.Zero;
            try { SetPixels(page, image, bgra, w, h); }
            catch (PdfException)
            {
                Native.FPDFPageObj_Destroy(image);
                return IntPtr.Zero;
            }
            var m = new Native.FS_MATRIX { a = r - l, d = t - b, e = l, f = b };
            Native.FPDFPageObj_SetMatrix(image, &m);
            return image;
        }
        finally { Native.FPDFBitmap_Destroy(bitmap); }
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
    /// <param name="always">Copy even when the text needs no pinning (used when taking a group apart).</param>
    /// <param name="matrix">The object's full transform, when it differs from its own matrix (an object inside a group).</param>
    private IntPtr Pin(PageHandle page, IntPtr obj, List<(char Ch, double X, double Y)> glyphs, bool always = false, Affine? matrix = null)
    {
        var exact = PinByCodes(page, obj, glyphs, always, matrix, out bool resolved);
        return resolved ? exact : PinByUnicode(page, obj, glyphs, always, matrix);
    }

    /// <summary>
    /// Removes spaces PDFium made up: where it sees a gap it reports a space sitting on the very
    /// spot of the next character. They are not in the PDF, and must not be copied into it.
    /// </summary>
    internal static void DropPhantomSpaces(List<(char Ch, double X, double Y)> glyphs)
    {
        for (int i = glyphs.Count - 2; i >= 0; i--)
            if (glyphs[i].Ch == ' ' && glyphs[i + 1].Ch != ' ' && Math.Abs(glyphs[i].X - glyphs[i + 1].X) < 0.01 && Math.Abs(glyphs[i].Y - glyphs[i + 1].Y) < 0.01)
                glyphs.RemoveAt(i);
    }

    /// <summary>One glyph code of a font: the text it stands for and how far it advances (in ems).</summary>
    private sealed record GlyphCode(uint Code, string Text, double Width);

    private readonly Dictionary<IntPtr, List<GlyphCode>> _glyphCodes = new();

    /// <summary>
    /// The glyph codes (1-255) of a font, found by drawing each one twice and reading back what
    /// PDFium makes of it. Text has to be copied by code, not by character: a font can have
    /// several codes for one character (alternates, ligatures such as "Th"), and only the code
    /// says which glyph is meant.
    /// </summary>
    private List<GlyphCode> GlyphCodes(PageHandle page, IntPtr font)
    {
        if (_glyphCodes.TryGetValue(font, out var known)) return known;
        const float size = 10;
        var probes = new Dictionary<IntPtr, uint>();
        var found = new Dictionary<uint, List<(char Ch, double X)>>();
        try
        {
            uint* pair = stackalloc uint[2];
            for (uint code = 1; code < 256; code++)
            {
                var probe = Native.FPDFPageObj_CreateTextObj(Doc, font, size);
                if (probe == IntPtr.Zero) continue;
                pair[0] = pair[1] = code;
                // Each probe gets its own spot: PDFium drops text drawn twice in one place.
                var m = new Native.FS_MATRIX { a = 1, d = 1, e = 5 + code % 16 * 35, f = 5 + code / 16 * 30 };
                if (Native.FPDFText_SetCharcodes(probe, pair, 2) == 0) { Native.FPDFPageObj_Destroy(probe); continue; }
                Native.FPDFPageObj_SetMatrix(probe, &m);
                Native.FPDFPage_InsertObject(page.Handle, probe);
                probes[probe] = code;
            }
            var textPage = Native.FPDFText_LoadPage(page.Handle);
            if (textPage != IntPtr.Zero)
            {
                try
                {
                    int n = Native.FPDFText_CountChars(textPage);
                    for (int i = 0; i < n; i++)
                    {
                        if (Native.FPDFText_IsGenerated(textPage, i) == 1) continue;
                        if (!probes.TryGetValue(Native.FPDFText_GetTextObject(textPage, i), out uint code)) continue;
                        double x, y;
                        Native.FPDFText_GetCharOrigin(textPage, i, &x, &y);
                        if (!found.TryGetValue(code, out var list)) found[code] = list = [];
                        list.Add(((char)Native.FPDFText_GetUnicode(textPage, i), x));
                    }
                }
                finally { Native.FPDFText_ClosePage(textPage); }
            }
        }
        finally
        {
            foreach (var probe in probes.Keys)
            {
                Native.FPDFPage_RemoveObject(page.Handle, probe);
                Native.FPDFPageObj_Destroy(probe);
            }
        }

        var table = new List<GlyphCode>();
        foreach (var (code, chars) in found)
        {
            if (chars.Any(c => c.Ch == '\0')) continue;
            double first = chars[0].X;
            var one = new string(chars.Where(c => Math.Abs(c.X - first) < 0.001).Select(c => c.Ch).ToArray());
            var rest = chars.Where(c => Math.Abs(c.X - first) >= 0.001).ToList();
            if (string.IsNullOrWhiteSpace(one))
            {
                // PDFium reports only one of two spaces in a row, so a space's width is asked for directly.
                float w = 0;
                Native.FPDFFont_GetGlyphWidth(font, one[0], 1, &w);
                table.Add(new GlyphCode(code, one[..1], w));
            }
            else if (rest.Count > 0) table.Add(new GlyphCode(code, one, (rest[0].X - first) / size));
            else
            {
                // Both copies at one spot: the glyph does not advance at all.
                if (one.Length % 2 == 0 && one[..(one.Length / 2)] == one[(one.Length / 2)..]) one = one[..(one.Length / 2)];
                table.Add(new GlyphCode(code, one, 0));
            }
        }
        return _glyphCodes[font] = table;
    }

    /// <summary>
    /// A copy of a text object made from its original glyph codes, each pinned where it is now.
    /// <paramref name="resolved"/> is false when the codes could not be worked out (the caller
    /// then falls back to copying by character).
    /// </summary>
    private IntPtr PinByCodes(PageHandle page, IntPtr obj, List<(char Ch, double X, double Y)> glyphs, bool always, Affine? matrix, out bool resolved)
    {
        resolved = false;
        if (glyphs.Count == 0) return IntPtr.Zero;
        var font = Native.FPDFTextObj_GetFont(obj);
        float size = 0;
        Native.FS_MATRIX m;
        if (font == IntPtr.Zero || Native.FPDFTextObj_GetFontSize(obj, &size) == 0 || size <= 0 || Native.FPDFPageObj_GetMatrix(obj, &m) == 0) return IntPtr.Zero;
        if (matrix is { } full) m = full.ToNative();
        double a = m.a, b = m.b, norm2 = a * a + b * b;
        if (norm2 < 1e-12) return IntPtr.Zero;

        // Characters drawn at one spot come from one glyph (a ligature gives two or three).
        var units = new List<(string Text, double Along)>();
        var (x0, y0) = (glyphs[0].X, glyphs[0].Y);
        double tolerance = 0.01 * size;
        for (int k = 0; k < glyphs.Count; k++)
        {
            double dx = glyphs[k].X - x0, dy = glyphs[k].Y - y0;
            if (Math.Abs((dy * a - dx * b) / norm2) > tolerance) return IntPtr.Zero; // not on one straight line
            if (k > 0 && Math.Abs(glyphs[k].X - glyphs[k - 1].X) < 0.01 && Math.Abs(glyphs[k].Y - glyphs[k - 1].Y) < 0.01)
                units[^1] = (units[^1].Text + glyphs[k].Ch, units[^1].Along);
            else units.Add((glyphs[k].Ch.ToString(), (dx * a + dy * b) / norm2));
        }

        // For each glyph, the code with that text whose width is closest to how far the glyph advances.
        var table = GlyphCodes(page, font);
        var codes = new uint[units.Count];
        bool needed = false;
        for (int k = 0; k < units.Count; k++)
        {
            var candidates = table.Where(c => c.Text == units[k].Text).ToList();
            if (candidates.Count == 0) return IntPtr.Zero; // not a simple font, or text PDFium reads differently
            GlyphCode best;
            if (k + 1 < units.Count)
            {
                double advance = (units[k + 1].Along - units[k].Along) / size;
                best = candidates.OrderBy(c => Math.Abs(c.Width - advance)).First();
                if (Math.Abs(best.Width - advance) * size > tolerance) needed = true;
            }
            else best = candidates.OrderByDescending(c => c.Width).First();
            codes[k] = best.Code;
        }
        resolved = true;
        if (!always && (!needed || !ClipIsHarmless(obj))) return IntPtr.Zero;

        var pinned = Native.FPDFPageObj_CreateTextObj(Doc, font, size);
        if (pinned == IntPtr.Zero) return IntPtr.Zero;
        bool ok;
        fixed (uint* c = codes) ok = Native.FPDFText_SetCharcodes(pinned, c, (nuint)codes.Length) != 0;
        if (ok)
        {
            var placed = new Native.FS_MATRIX { a = m.a, b = m.b, c = m.c, d = m.d, e = (float)x0, f = (float)y0 };
            Native.FPDFPageObj_SetMatrix(pinned, &placed);
            // Positions go on last: changing a colour afterwards makes PDFium recalculate and lose them.
            CopyTextLook(obj, pinned);
            if (units.Count > 1)
            {
                var positions = units.Skip(1).Select(u => (float)u.Along).ToArray();
                fixed (float* p = positions) ok = Native.FPDFText_SetPositions(pinned, p, (nuint)positions.Length) != 0;
            }
        }
        if (!ok)
        {
            Native.FPDFPageObj_Destroy(pinned);
            resolved = false;
            return IntPtr.Zero;
        }
        return pinned;
    }

    private static void CopyTextLook(IntPtr from, IntPtr to)
    {
        uint r = 0, g = 0, b = 0, a = 255;
        Native.FPDFPageObj_GetFillColor(from, &r, &g, &b, &a);
        Native.FPDFPageObj_SetFillColor(to, r, g, b, a);
        int mode = Native.FPDFTextObj_GetTextRenderMode(from);
        if (mode > 0)
        {
            Native.FPDFTextObj_SetTextRenderMode(to, mode);
            Native.FPDFPageObj_GetStrokeColor(from, &r, &g, &b, &a);
            Native.FPDFPageObj_SetStrokeColor(to, r, g, b, a);
        }
    }

    /// <summary>The fallback copy, made from characters: used when a font's glyph codes cannot be worked out.</summary>
    private IntPtr PinByUnicode(PageHandle page, IntPtr obj, List<(char Ch, double X, double Y)> glyphs, bool always = false, Affine? matrix = null)
    {
        if (glyphs.Count == 0 || glyphs.Count < 2 && !always || glyphs.Any(g => g.Ch == '\0' || char.IsSurrogate(g.Ch))) return IntPtr.Zero;
        var font = Native.FPDFTextObj_GetFont(obj);
        float size = 0;
        Native.FS_MATRIX m;
        if (font == IntPtr.Zero || Native.FPDFTextObj_GetFontSize(obj, &size) == 0 || size <= 0 || Native.FPDFPageObj_GetMatrix(obj, &m) == 0) return IntPtr.Zero;
        if (matrix is { } full) m = full.ToNative();
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
        if (!always && (!needed || !ClipIsHarmless(obj))) return IntPtr.Zero;

        var pinned = Native.FPDFPageObj_CreateTextObj(Doc, font, size);
        if (pinned == IntPtr.Zero) return IntPtr.Zero;
        bool ok = Native.FPDFText_SetText(pinned, new string(glyphs.Select(g => g.Ch).ToArray())) != 0;
        if (ok)
        {
            var placed = new Native.FS_MATRIX { a = m.a, b = m.b, c = m.c, d = m.d, e = (float)x0, f = (float)y0 };
            Native.FPDFPageObj_SetMatrix(pinned, &placed);
            CopyTextLook(obj, pinned); // before the positions, which a later colour change would lose
            if (positions.Length > 0)
                fixed (float* p = positions) ok = Native.FPDFText_SetPositions(pinned, p, (nuint)positions.Length) != 0;
        }
        if (!ok)
        {
            Native.FPDFPageObj_Destroy(pinned);
            return IntPtr.Zero;
        }
        return pinned;
    }

    /// <summary>
    /// True when an object's clipping cannot cut it (the copy will not carry the clip). Only
    /// clip paths whose bounding box contains the whole object count as harmless.
    /// </summary>
    private static bool ClipIsHarmless(IntPtr obj, float slack = 0.5f)
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
            if (l < minX - slack || r > maxX + slack || bt < minY - slack || t > maxY + slack) return false;
        }
        return true;
    }
}
