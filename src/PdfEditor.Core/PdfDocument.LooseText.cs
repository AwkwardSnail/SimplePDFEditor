using PdfEditor.Core.Interop;
using PdfEditor.Core.Reflow;

namespace PdfEditor.Core;

/// <summary>
/// What the check for loose text found in a document. Some programs (Chrome's "Save as PDF"
/// among them) store text letter by letter, often in Type 3 fonts: fonts whose letters are small
/// drawings. Such text can be shown, but PDFium cannot write Type 3 text back when a page is
/// changed, and letters stored one by one cannot be selected or moved as lines.
/// </summary>
/// <param name="Type3Pages">Pages (zero-based) that hold text in Type 3 fonts.</param>
/// <param name="LetterByLetterPages">Pages whose text is mostly stored a letter at a time.</param>
public sealed record LooseTextReport(IReadOnlyList<int> Type3Pages, IReadOnlyList<int> LetterByLetterPages)
{
    public bool Any => Type3Pages.Count > 0 || LetterByLetterPages.Count > 0;
    /// <summary>Every page that would gain from <see cref="PdfDocument.ConvertLooseText(int)"/>.</summary>
    public IReadOnlyList<int> Pages => Type3Pages.Union(LetterByLetterPages).OrderBy(p => p).ToList();
}

// Text stored letter by letter, and text in Type 3 fonts: finding it, rebuilding it as ordinary
// lines of text, and keeping what was not rebuilt from vanishing when its page is rewritten.
public sealed unsafe partial class PdfDocument
{
    /// <summary>A Type 3 font has no font program and no name of its own; that is how it is told apart here.</summary>
    private static bool IsType3(IntPtr textObj) => Native.FPDFTextObj_GetFont(textObj) != IntPtr.Zero && FontName(textObj).Length == 0;

    /// <summary>Looks through the document for Type 3 text and for pages whose text is stored a letter at a time.</summary>
    public LooseTextReport FindLooseText()
    {
        var type3 = new List<int>();
        var letters = new List<int>();
        for (int p = 0; p < PageCount; p++)
        {
            using var page = OpenPage(p);
            int count = Native.FPDFPage_CountObjects(page.Handle), textObjects = 0, type3Objects = 0;
            for (int i = 0; i < count; i++)
            {
                var obj = Native.FPDFPage_GetObject(page.Handle, i);
                if (Native.FPDFPageObj_GetType(obj) != Native.PAGEOBJ_TEXT) continue;
                textObjects++;
                if (IsType3(obj)) type3Objects++;
            }
            if (type3Objects > 0) type3.Add(p);
            if (textObjects < 40) continue; // too little text to call it either way

            // Letter by letter: far more pieces of text than a page of words would need.
            var textPage = Native.FPDFText_LoadPage(page.Handle);
            if (textPage == IntPtr.Zero) continue;
            try
            {
                int chars = Native.FPDFText_CountChars(textPage), visible = 0;
                for (int i = 0; i < chars; i++)
                    if (Native.FPDFText_IsGenerated(textPage, i) != 1 && !char.IsWhiteSpace((char)Native.FPDFText_GetUnicode(textPage, i))) visible++;
                if (visible < textObjects * 2) letters.Add(p);
            }
            finally { Native.FPDFText_ClosePage(textPage); }
        }
        return new LooseTextReport(type3, letters);
    }

    /// <summary>Rebuilds the loose text of every page that has any. Returns how many lines of text were rebuilt.</summary>
    public int ConvertLooseText()
    {
        int lines = 0;
        foreach (int p in FindLooseText().Pages) lines += ConvertLooseText(p);
        return lines;
    }

    /// <summary>
    /// Rebuilds a page's loose text as ordinary text: each stretch of a line in one look becomes
    /// one piece of text, in the same place and size. Text in a Type 3 font is set in the standard
    /// font whose letters are closest in width (sans, serif or mono, regular or bold), fitted to
    /// the width it had, so the page keeps its layout though the letters look a little different.
    /// Text in an ordinary font keeps its font and is only joined up. Text that is not upright is
    /// left as it is. Returns how many pieces of text were made.
    /// </summary>
    public int ConvertLooseText(int pageIndex)
    {
        using var page = OpenPage(pageIndex);
        var analysis = PageAnalysis.Analyze(page);
        if (analysis.Glyphs.Count == 0) return 0;

        // Stretches: neighbours on a line that share a look and are not set apart by a wide gap.
        var stretches = new List<List<Glyph>>();
        foreach (var line in analysis.Glyphs.Select(g => g.Line).OfType<Line>().Distinct())
        {
            List<Glyph>? current = null;
            foreach (var g in line.Glyphs)
            {
                var last = current?[^1];
                if (last is null || !ReferenceEquals(last.Style, g.Style) || g.X - last.End > g.Style.Em || g.X < last.X || Math.Abs(g.Y - last.Y) > 0.05 * g.Style.Em)
                    stretches.Add(current = []);
                current!.Add(g);
            }
        }

        var coverage = new Coverage(this, page);
        var lookAlikes = new Dictionary<IntPtr, string>();           // Type 3 font -> the standard font chosen for it
        var standIns = new Dictionary<(IntPtr, StyleRef), StyleRef>();
        var plans = new List<(List<Glyph> Glyphs, StyleRef Style)>();
        foreach (var stretch in stretches)
        {
            var source = stretch[0].Style;
            bool type3 = source.FontName.Length == 0;
            if (!type3 && stretch.Select(g => g.Obj).Distinct().Count() < 2) continue; // one piece already

            StyleRef target = source;
            if (type3)
            {
                if (!lookAlikes.TryGetValue(source.Font, out var name))
                    lookAlikes[source.Font] = name = ClosestStandardFont(analysis.Glyphs.Where(g => g.Style.Font == source.Font).ToList());
                if (!standIns.TryGetValue((source.Font, source), out target!))
                    standIns[(source.Font, source)] = target = new StyleRef
                    {
                        Font = StandardFontHandle(name), FontName = name, FontSize = source.FontSize, Matrix = source.Matrix, Lin = source.Lin,
                        Fill = source.Fill, Stroke = source.Stroke, RenderMode = source.RenderMode,
                    };
            }
            else
            {
                // Its own font, if that font can be written from plain characters; otherwise it stays as it is.
                coverage.Check(stretch.Select(g => (source, g.Ch)));
                if (stretch.Any(g => !coverage.Has(source, g.Ch))) continue;
            }
            plans.Add((stretch, target));
        }
        if (plans.Count == 0) return 0;
        coverage.Check(plans.SelectMany(p => p.Glyphs.Select(g => (p.Style, g.Ch))));

        // Draw the new text, then take the old pieces away.
        int made = 0;
        var replaced = new HashSet<IntPtr>();
        var systemFonts = new Dictionary<StyleRef, StyleRef?>();
        foreach (var (glyphs, style) in plans)
        {
            // Fit to the width the stretch had, within reason: a look-alike's letters are never quite as wide.
            // Where a word break falls. A joined pair of letters ("fi" drawn as one shape) reads as two
            // characters in one spot, which looks like a gap after it; measure from the pair instead.
            var spaced = new bool[glyphs.Count];
            for (int i = 1; i < glyphs.Count; i++)
            {
                spaced[i] = glyphs[i].SpaceBefore;
                int first = i - 1;
                while (first > 0 && Math.Abs(glyphs[first - 1].X - glyphs[i - 1].X) < 0.02 * style.Em) first--;
                if (!spaced[i] || first == i - 1) continue;
                double pair = 0;
                for (int k = first; k < i; k++) pair += style.Advance(glyphs[k].Ch);
                spaced[i] = glyphs[i].X - glyphs[first].X - pair > 0.15 * style.Em;
            }
            double natural = 0;
            for (int i = 0; i < glyphs.Count; i++) natural += (spaced[i] ? style.Advance(' ') : 0) + style.Advance(glyphs[i].Ch);
            double had = glyphs[^1].End - glyphs[0].X;
            double fit = style.FromDocument || natural <= 0.01 ? 1 : Math.Clamp(had / natural, 0.85, 1.2);
            var fitted = Math.Abs(fit - 1) < 0.005 ? style : Stretched(style, fit);

            // One piece, unless some characters need another font (an arrow, say, that the standard fonts lack).
            int start = 0;
            while (start < glyphs.Count)
            {
                StyleRef use = FontFor(glyphs[start].Ch);
                var text = new System.Text.StringBuilder();
                int end = start;
                while (end < glyphs.Count && ReferenceEquals(FontFor(glyphs[end].Ch), use))
                {
                    if (end > start && spaced[end]) text.Append(' ');
                    text.Append(glyphs[end].Ch);
                    end++;
                }
                var obj = DrawText(page, text.ToString(), use, glyphs[start].X, glyphs[start].Y);
                page.NewObjects.Add(obj);
                made++;
                start = end;
            }
            foreach (var g in glyphs) replaced.Add(g.Obj);

            StyleRef FontFor(char c)
            {
                if (coverage.Has(style, c)) return fitted;
                if (!systemFonts.TryGetValue(style, out var system)) systemFonts[style] = system = SystemLookAlike(style);
                return system ?? fitted;
            }
        }

        // Old pieces go when everything they drew was rebuilt; so do Type 3 pieces that drew nothing (lone spaces).
        var rebuilt = plans.SelectMany(p => p.Glyphs).ToHashSet();
        foreach (var obj in analysis.Objects)
        {
            if (Native.FPDFPageObj_GetType(obj) != Native.PAGEOBJ_TEXT) continue;
            bool gone;
            if (analysis.GlyphsByObject.TryGetValue(obj, out var own))
                gone = replaced.Contains(obj) ? own.All(g => rebuilt.Contains(g) || char.IsWhiteSpace(g.Ch)) : IsType3(obj) && own.All(g => char.IsWhiteSpace(g.Ch));
            else gone = IsType3(obj) && analysis.StyleOf(obj) is not null; // upright Type 3 text that shows no characters
            if (!gone) continue;
            Native.FPDFPage_RemoveObject(page.Handle, obj);
            Native.FPDFPageObj_Destroy(obj);
        }
        page.Dirty = page.ChangedExisting = true;
        _charsByFont = null;
        return made;
    }

    /// <summary>The same look, wider or narrower by a factor along the line of text.</summary>
    private static StyleRef Stretched(StyleRef s, double factor) => new()
    {
        Font = s.Font, FontName = s.FontName, FontSize = s.FontSize,
        Matrix = s.Matrix with { A = s.Matrix.A * factor, B = s.Matrix.B * factor },
        Lin = s.Lin with { A = s.Lin.A * factor, B = s.Lin.B * factor },
        Fill = s.Fill, Stroke = s.Stroke, RenderMode = s.RenderMode, FromDocument = s.FromDocument, CoversEverything = s.CoversEverything,
    };

    /// <summary>
    /// The standard font whose letters are closest in width to those of a font on the page, going
    /// by how far each letter moves the next one along. Letters all the same width mean a
    /// typewriter font; otherwise it is the best match among sans and serif, regular and bold.
    /// </summary>
    private string ClosestStandardFont(List<Glyph> glyphs)
    {
        // How wide each letter is, in ems, from the letter that follows it on the same line.
        var widths = new List<(char Ch, double Width)>();
        for (int i = 0; i + 1 < glyphs.Count && widths.Count < 600; i++)
        {
            Glyph g = glyphs[i], next = glyphs[i + 1];
            if (!ReferenceEquals(g.Line, next.Line) || next.SpaceBefore || char.IsWhiteSpace(g.Ch) || g.Style.Em <= 0) continue;
            double step = (next.X - g.X) / g.Style.Em;
            if (step is > 0.05 and < 1.5) widths.Add((g.Ch, step));
        }
        if (widths.Count < 4) return "Helvetica";

        double mean = widths.Average(w => w.Width);
        bool sameWidth = widths.Select(w => w.Ch).Distinct().Count() >= 4 && widths.All(w => Math.Abs(w.Width - mean) < 0.02);
        if (sameWidth) return "Courier";

        string best = "Helvetica";
        double bestError = double.MaxValue;
        foreach (string name in new[] { "Helvetica", "Helvetica-Bold", "Times-Roman", "Times-Bold" })
        {
            var font = StandardFontHandle(name);
            double error = 0;
            int counted = 0;
            foreach (var (ch, width) in widths)
            {
                float w = 0;
                if (Native.FPDFFont_GetGlyphWidth(font, ch, 1000, &w) == 0 || w <= 0) continue;
                error += (width - w / 1000) * (width - w / 1000);
                counted++;
            }
            if (counted < 4) continue;
            error /= counted;
            // Bold has to be clearly the better match: small or light text is easily mistaken for it.
            if (name.EndsWith("Bold", StringComparison.Ordinal)) error *= 1.6;
            if (error < bestError) (best, bestError) = (name, error);
        }
        return best;
    }

    /// <summary>
    /// Type 3 text that is still on a page about to be rewritten is replaced by small pictures of
    /// itself, since PDFium would leave it out. Called from <see cref="PreserveTextLayout"/>.
    /// </summary>
    private void KeepType3Text(PageHandle page, Dictionary<IntPtr, int> indexOf,
        Dictionary<IntPtr, List<(char Ch, double X, double Y)>> chars, List<(int Index, IntPtr Old, IntPtr New)> replacements)
    {
        foreach (var (obj, index) in indexOf)
        {
            if (page.NewObjects.Contains(obj) || Native.FPDFPageObj_GetType(obj) != Native.PAGEOBJ_TEXT || !IsType3(obj)) continue;
            replacements.Add((index, obj, TextAsPicture(page, obj))); // zero when it shows nothing: then it is only removed
            chars.Remove(obj);
        }
    }
}
