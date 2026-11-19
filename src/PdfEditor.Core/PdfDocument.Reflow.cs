using System.Text.RegularExpressions;
using PdfEditor.Core.Interop;
using PdfEditor.Core.Reflow;

namespace PdfEditor.Core;

// Paragraph-level text editing with reflow.
public sealed unsafe partial class PdfDocument
{
    private readonly Dictionary<string, IntPtr> _standardFonts = new();
    private Dictionary<string, HashSet<char>>? _charsByFont; // characters seen anywhere in the document, per font

    // ------------------------------------------------------------------ public API

    /// <summary>The paragraphs on a page, in reading order.</summary>
    public IReadOnlyList<TextParagraph> GetParagraphs(int pageIndex)
    {
        using var page = OpenPage(pageIndex);
        var analysis = PageAnalysis.Analyze(page);
        return analysis.Paragraphs.Select((p, i) => p.ToPublic(i)).ToList();
    }

    /// <summary>The paragraph under a point, or null.</summary>
    public TextParagraph? FindParagraphAt(int pageIndex, double x, double y) =>
        GetParagraphs(pageIndex).FirstOrDefault(p => new PdfRect(p.Bounds.X - 2, p.Bounds.Y - 2, p.Bounds.Width + 4, p.Bounds.Height + 4).Contains(x, y));

    /// <summary>
    /// Replaces a paragraph's text and lays it out again: words re-wrap within the paragraph's
    /// width, keeping its alignment, line spacing and fonts. Characters that kept their place
    /// keep their formatting; new characters take the formatting of the text before them.
    /// A line break ("\n") in the new text forces a new line. Empty text removes the paragraph.
    /// </summary>
    public ReflowResult ReplaceParagraphText(int pageIndex, int paragraphIndex, string newText, ReflowOptions? options = null)
    {
        using var page = OpenPage(pageIndex);
        var analysis = PageAnalysis.Analyze(page);
        var p = GetParagraph(analysis, paragraphIndex);
        var chars = Restyle(p.Chars, NormalizeNewText(newText), p.BaseStyle);
        return Reflow(analysis, p, chars, options ?? new());
    }

    /// <summary>Replaces part of a paragraph's text (positions as in <see cref="TextParagraph.Text"/>) and lays it out again.</summary>
    public ReflowResult ReplaceInParagraph(int pageIndex, int paragraphIndex, int start, int length, string text, ReflowOptions? options = null)
    {
        using var page = OpenPage(pageIndex);
        var analysis = PageAnalysis.Analyze(page);
        var p = GetParagraph(analysis, paragraphIndex);
        CheckRange(p, start, length);
        int b = start - 1, f = start + length;
        while (b > 0 && p.Chars[b].Ch == ' ') b--;
        while (f < p.Chars.Count - 1 && p.Chars[f].Ch == ' ') f++;
        var style = InsertionStyle(start > 0 ? p.Chars[b] : null, start + length < p.Chars.Count ? p.Chars[f] : null, p.BaseStyle);
        var chars = new List<PChar>(p.Chars.Take(start));
        chars.AddRange(NormalizeNewText(text).Select(c => new PChar(c, style.Style, c == ' ' ? 0 : style.Rise)));
        chars.AddRange(p.Chars.Skip(start + length));
        return Reflow(analysis, p, chars, options ?? new());
    }

    /// <summary>Changes the formatting of part of a paragraph (bold, italic, size or colour) and lays it out again.</summary>
    public ReflowResult FormatParagraphText(int pageIndex, int paragraphIndex, int start, int length, TextFormat format, ReflowOptions? options = null)
    {
        using var page = OpenPage(pageIndex);
        var analysis = PageAnalysis.Analyze(page);
        var p = GetParagraph(analysis, paragraphIndex);
        CheckRange(p, start, length);
        var derived = new Dictionary<StyleRef, StyleRef>();
        var chars = p.Chars.Select((c, i) =>
        {
            if (i < start || i >= start + length) return c;
            if (!derived.TryGetValue(c.Style, out var s)) derived[c.Style] = s = Reformat(page, c.Style, format);
            return c with { Style = s };
        }).ToList();
        return Reflow(analysis, p, chars, options ?? new());
    }

    /// <summary>Changes a paragraph's alignment and lays it out again.</summary>
    public ReflowResult SetParagraphAlignment(int pageIndex, int paragraphIndex, TextAlignment alignment, ReflowOptions? options = null)
    {
        using var page = OpenPage(pageIndex);
        var analysis = PageAnalysis.Analyze(page);
        var p = GetParagraph(analysis, paragraphIndex);
        return Reflow(analysis, p, p.Chars.ToList(), (options ?? new()) with { Alignment = alignment });
    }

    /// <summary>
    /// Replaces every occurrence of <paramref name="find"/>, re-flowing each paragraph it changes.
    /// Searches one page, or every page when <paramref name="pageIndex"/> is null. Returns the number of replacements.
    /// </summary>
    public int ReplaceText(string find, string replacement, bool matchCase = false, int? pageIndex = null, ReflowOptions? options = null)
    {
        if (string.IsNullOrEmpty(find)) throw new ArgumentException("Nothing to find.", nameof(find));
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int total = 0;
        for (int page = pageIndex ?? 0; page < (pageIndex is { } only ? only + 1 : PageCount); page++)
        {
            for (int index = 0; ; index++)
            {
                var paragraphs = GetParagraphs(page);
                if (index >= paragraphs.Count) break;
                var paragraph = paragraphs[index];
                if (!paragraph.IsEditable) continue;
                int hits = Count(paragraph.Text, find, comparison);
                if (hits == 0) continue;
                ReplaceParagraphText(page, index, paragraph.Text.Replace(find, replacement, comparison), options);
                total += hits;
            }
        }
        return total;
    }

    private static int Count(string text, string find, StringComparison comparison)
    {
        int n = 0;
        for (int i = text.IndexOf(find, comparison); i >= 0; i = text.IndexOf(find, i + find.Length, comparison)) n++;
        return n;
    }

    private static Paragraph GetParagraph(PageAnalysis analysis, int index)
    {
        if (index < 0 || index >= analysis.Paragraphs.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"There is no paragraph {index} on this page (it has {analysis.Paragraphs.Count}).");
        var p = analysis.Paragraphs[index];
        if (!p.Editable) throw new PdfException("This paragraph cannot be edited: it contains right-to-left script or characters without a known meaning.");
        return p;
    }

    private static void CheckRange(Paragraph p, int start, int length)
    {
        if (start < 0 || length < 0 || start + length > p.Chars.Count)
            throw new ArgumentOutOfRangeException(nameof(start), "The range is outside the paragraph's text.");
    }

    private static string NormalizeNewText(string text) =>
        Regex.Replace(text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' '), " {2,}", " ");

    // ------------------------------------------------------------------ keeping formatting across an edit

    /// <summary>
    /// Gives the new text the formatting of the old: characters that survive the edit keep their
    /// style, new ones copy the character before them.
    /// </summary>
    private static List<PChar> Restyle(List<PChar> old, string text, StyleRef fallback)
    {
        int n = old.Count, m = text.Length;
        var matchOf = new int[m];
        Array.Fill(matchOf, -1);
        int prefix = 0;
        while (prefix < n && prefix < m && old[prefix].Ch == text[prefix]) { matchOf[prefix] = prefix; prefix++; }
        int suffix = 0;
        while (suffix < n - prefix && suffix < m - prefix && old[n - 1 - suffix].Ch == text[m - 1 - suffix]) { matchOf[m - 1 - suffix] = n - 1 - suffix; suffix++; }

        // Longest common subsequence of the changed middle, when it is small enough.
        int a = n - prefix - suffix, b = m - prefix - suffix;
        if (a > 0 && b > 0 && (long)a * b <= 4_000_000)
        {
            var dp = new int[a + 1, b + 1];
            for (int i = a - 1; i >= 0; i--)
                for (int j = b - 1; j >= 0; j--)
                    dp[i, j] = old[prefix + i].Ch == text[prefix + j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            for (int i = 0, j = 0; i < a && j < b;)
            {
                if (old[prefix + i].Ch == text[prefix + j]) { matchOf[prefix + j] = prefix + i; i++; j++; }
                else if (dp[i + 1, j] >= dp[i, j + 1]) i++;
                else j++;
            }
        }

        var result = new PChar[m];
        for (int j = 0; j < m; j++)
            if (matchOf[j] >= 0) result[j] = old[matchOf[j]];
        for (int j = 0; j < m;)
        {
            if (matchOf[j] >= 0) { j++; continue; }
            int end = j;
            while (end < m && matchOf[end] < 0) end++;
            // Spaces carry the style of the word before them, so look past them to the neighbouring words.
            int back = j - 1;
            while (back > 0 && result[back].Ch == ' ') back--;
            int ahead = end;
            while (ahead < m - 1 && matchOf[ahead] >= 0 && result[ahead].Ch == ' ' && matchOf[ahead + 1] >= 0) ahead++;
            PChar? before = j > 0 ? result[back] : null;
            PChar? after = end < m ? result[ahead] : null;
            var source = InsertionStyle(before, after, fallback);
            for (int k = j; k < end; k++) result[k] = new PChar(text[k], source.Style, text[k] == ' ' ? 0 : source.Rise);
            j = end;
        }
        return result.ToList();
    }

    /// <summary>
    /// Formatting for inserted text: that of its neighbours when they agree; when they differ
    /// (say, after a bold word), the paragraph's ordinary formatting if either neighbour has it.
    /// </summary>
    private static PChar InsertionStyle(PChar? before, PChar? after, StyleRef main)
    {
        if (before is null && after is null) return new PChar(' ', main, 0);
        if (before is null) return after!.Value;
        if (after is null || ReferenceEquals(before.Value.Style, after.Value.Style)) return before.Value;
        if (ReferenceEquals(after.Value.Style, main)) return after.Value;
        if (ReferenceEquals(before.Value.Style, main)) return before.Value;
        return before.Value;
    }

    // ------------------------------------------------------------------ fonts

    /// <summary>A standard font handle (Helvetica, Times, Courier and their variants), loaded once per document.</summary>
    private IntPtr StandardFontHandle(string name)
    {
        if (_standardFonts.TryGetValue(name, out var font)) return font;
        font = Native.FPDFText_LoadStandardFont(Doc, name);
        if (font == IntPtr.Zero) throw new PdfException($"Could not load the standard font {name}.");
        return _standardFonts[name] = font;
    }

    private static StandardFont FamilyOf(string fontName) =>
        Regex.IsMatch(fontName, "Courier|Mono|Consol|Menlo|Typewriter", RegexOptions.IgnoreCase) ? Core.StandardFont.Courier
        : Regex.IsMatch(fontName, "Times|Serif|Garamond|Georgia|Cambria|Minion|Palatino|Book|Roman|Caslon|Baskerville", RegexOptions.IgnoreCase)
          && !Regex.IsMatch(fontName, "Sans", RegexOptions.IgnoreCase) ? Core.StandardFont.Times
        : Core.StandardFont.Helvetica;

    /// <summary>A standard-font look-alike of a style, with the same size, transform and colour.</summary>
    private StyleRef StandardLookAlike(StyleRef s, bool? bold = null, bool? italic = null)
    {
        var style = new TextStyle { Font = FamilyOf(s.DisplayName), Bold = bold ?? s.Bold, Italic = italic ?? s.Italic };
        string name = StandardFontName(style);
        // A sheared (synthetic italic) transform plus an italic font would slant twice.
        var matrix = style.Italic && Math.Abs(s.Matrix.C) > 0.05 * Math.Abs(s.Matrix.D) ? s.Matrix with { C = 0 } : s.Matrix;
        double scale = s.Lin.A / s.Matrix.A; // user-to-view scale along the line
        return new StyleRef
        {
            Font = StandardFontHandle(name), FontName = name, FontSize = s.FontSize, Matrix = matrix,
            Lin = new Affine(s.Lin.A, s.Lin.B, matrix.C * scale, s.Lin.D, 0, 0),
            Fill = s.Fill, Stroke = s.Stroke, RenderMode = s.RenderMode,
        };
    }

    /// <summary>An embedded system font standing in for a style, for characters no standard font has.</summary>
    private StyleRef? SystemLookAlike(StyleRef s)
    {
        var path = FindSystemFont(new TextStyle { Font = FamilyOf(s.DisplayName), Bold = s.Bold, Italic = s.Italic });
        if (path is null) return null;
        return new StyleRef
        {
            Font = LoadFont(path), FontName = Path.GetFileNameWithoutExtension(path), FontSize = s.FontSize, Matrix = s.Matrix, Lin = s.Lin,
            Fill = s.Fill, Stroke = s.Stroke, RenderMode = s.RenderMode, CoversEverything = true,
        };
    }

    private StyleRef Reformat(PageHandle page, StyleRef s, TextFormat f)
    {
        var result = s;
        if (f.Bold is { } bold && bold != s.Bold || f.Italic is { } italic && italic != s.Italic)
            result = StandardLookAlike(s, f.Bold, f.Italic);
        if (f.Size is { } size)
        {
            if (size <= 0) throw new ArgumentOutOfRangeException(nameof(f), "Font size must be positive.");
            result = Clone(result, fontSize: (float)(size / -result.Lin.D));
        }
        if (f.Color is { } color) result = Clone(result, fill: color);
        return result;
    }

    private static StyleRef Clone(StyleRef s, float? fontSize = null, PdfColor? fill = null) => new()
    {
        Font = s.Font, FontName = s.FontName, FontSize = fontSize ?? s.FontSize, Matrix = s.Matrix, Lin = s.Lin,
        Fill = fill ?? s.Fill, Stroke = s.Stroke, RenderMode = s.RenderMode, FromDocument = s.FromDocument, CoversEverything = s.CoversEverything,
    };

    /// <summary>Characters drawn anywhere in the document, per font name: what a subset font is known to contain.</summary>
    private HashSet<char> CharsSeenWith(string fontName)
    {
        if (_charsByFont is null)
        {
            _charsByFont = new();
            for (int i = 0; i < PageCount; i++)
            {
                var page = Native.FPDF_LoadPage(Doc, i);
                if (page == IntPtr.Zero) continue;
                var textPage = Native.FPDFText_LoadPage(page);
                var names = new Dictionary<IntPtr, string>();
                if (textPage != IntPtr.Zero)
                {
                    int count = Native.FPDFText_CountChars(textPage);
                    for (int c = 0; c < count; c++)
                    {
                        var obj = Native.FPDFText_GetTextObject(textPage, c);
                        if (obj == IntPtr.Zero) continue;
                        if (!names.TryGetValue(obj, out var name)) names[obj] = name = FontName(obj);
                        if (!_charsByFont.TryGetValue(name, out var set)) _charsByFont[name] = set = [];
                        set.Add((char)Native.FPDFText_GetUnicode(textPage, c));
                    }
                    Native.FPDFText_ClosePage(textPage);
                }
                Native.FPDF_ClosePage(page);
            }
        }
        return _charsByFont.TryGetValue(fontName, out var chars) ? chars : [];
    }

    /// <summary>
    /// Works out which characters each style can really draw. A character counts as drawable when
    /// PDFium can encode it in the font and decode it back, and, for a font embedded in the
    /// document, when the document already shows it in that font: embedded fonts are usually
    /// subsets holding only the glyphs the document used.
    /// </summary>
    private sealed unsafe class Coverage(PdfDocument owner, PageHandle page)
    {
        private readonly Dictionary<(StyleRef, char), bool> _known = new();

        public bool Has(StyleRef s, char c) => char.IsWhiteSpace(c) || s.CoversEverything || _known.TryGetValue((s, c), out var ok) && ok;

        public void Check(IEnumerable<(StyleRef Style, char Ch)> wanted)
        {
            var todo = wanted.Where(w => !char.IsWhiteSpace(w.Ch) && !w.Style.CoversEverything && !_known.ContainsKey(w)).Distinct().ToList();
            if (todo.Count == 0) return;

            var probes = new List<((StyleRef, char) Key, IntPtr Obj)>();
            try
            {
                foreach (var key in todo)
                {
                    if (key.Style.FromDocument && key.Style.Embedded && !owner.CharsSeenWith(key.Style.FontName).Contains(key.Ch))
                    {
                        _known[key] = false;
                        continue;
                    }
                    var obj = Native.FPDFPageObj_CreateTextObj(owner.Doc, key.Style.Font, key.Style.FontSize);
                    if (obj == IntPtr.Zero) { _known[key] = false; continue; }
                    Native.FPDFText_SetText(obj, key.Ch.ToString());
                    // Give every probe its own spot: PDFium drops text it sees drawn twice in one place.
                    int n = probes.Count;
                    var m = new Native.FS_MATRIX { a = 1, d = 1, e = 3.37f + n % 40 * 14.11f, f = 5.53f + n / 40 * 17.3f };
                    Native.FPDFPageObj_SetMatrix(obj, &m);
                    Native.FPDFPage_InsertObject(page.Handle, obj);
                    probes.Add((key, obj));
                }
                if (probes.Count == 0) return;
                var textPage = Native.FPDFText_LoadPage(page.Handle);
                try
                {
                    foreach (var (key, obj) in probes)
                        _known[key] = textPage != IntPtr.Zero && ReadObjectText(obj, textPage).Trim('\0', ' ', '\r', '\n') == key.Item2.ToString(); // PDFium may add a space between neighbouring probes
                }
                finally { if (textPage != IntPtr.Zero) Native.FPDFText_ClosePage(textPage); }
            }
            finally
            {
                foreach (var (_, obj) in probes)
                {
                    Native.FPDFPage_RemoveObject(page.Handle, obj);
                    Native.FPDFPageObj_Destroy(obj);
                }
            }
        }
    }

    // ------------------------------------------------------------------ layout

    private readonly record struct Item(char Ch, StyleRef Style, double Rise, double Advance);

    private sealed class Word
    {
        public List<Item> Items { get; } = [];
        public double Width { get; set; }
        public double SpaceBefore { get; set; }
        public bool HardBreakBefore { get; set; }
    }

    private sealed class LaidLine
    {
        public List<Word> Words { get; } = [];
        public double Width { get; set; }
        public bool EndsWithHardBreak { get; set; }
        public double X { get; set; }
        public double Baseline { get; set; }
        public double Em { get; set; }
        public double GapExtra { get; set; }
    }

    private static List<Word> ToWords(List<Item> items)
    {
        var words = new List<Word>();
        Word? word = null;
        double pendingSpace = 0;
        bool hardBreak = false;
        foreach (var item in items)
        {
            if (item.Ch == '\n') { word = null; hardBreak = true; pendingSpace = 0; continue; }
            if (item.Ch == ' ') { word = null; pendingSpace = item.Advance; continue; }
            if (word is null)
            {
                word = new Word { SpaceBefore = words.Count == 0 || hardBreak ? 0 : pendingSpace, HardBreakBefore = hardBreak };
                words.Add(word);
                hardBreak = false;
            }
            word.Items.Add(item);
            word.Width += item.Advance;
        }
        return words;
    }

    private static List<LaidLine> Wrap(List<Word> words, double width, double firstIndent)
    {
        var lines = new List<LaidLine> { new() };
        double Available() => width - (lines.Count == 1 ? firstIndent : 0);

        foreach (var w in words)
        {
            var line = lines[^1];
            if (w.HardBreakBefore && (line.Words.Count > 0 || lines.Count > 1))
            {
                line.EndsWithHardBreak = true;
                lines.Add(line = new LaidLine());
            }
            if (line.Words.Count > 0 && line.Width + w.SpaceBefore + w.Width > Available() + 0.01)
                lines.Add(line = new LaidLine());

            if (line.Words.Count == 0 && w.Width > Available())
            {
                // A word longer than the line: break it between characters.
                var piece = new Word();
                foreach (var item in w.Items)
                {
                    if (piece.Items.Count > 0 && piece.Width + item.Advance > Available())
                    {
                        line.Words.Add(piece);
                        line.Width = piece.Width;
                        lines.Add(line = new LaidLine());
                        piece = new Word();
                    }
                    piece.Items.Add(item);
                    piece.Width += item.Advance;
                }
                line.Words.Add(piece);
                line.Width = piece.Width;
                continue;
            }
            line.Width += (line.Words.Count > 0 ? w.SpaceBefore : 0) + w.Width;
            if (line.Words.Count == 0) w.SpaceBefore = 0;
            line.Words.Add(w);
        }
        return lines;
    }

    // ------------------------------------------------------------------ the reflow itself

    private ReflowResult Reflow(PageAnalysis analysis, Paragraph p, List<PChar> chars, ReflowOptions options)
    {
        var page = analysis.Page;
        var alignment = options.Alignment ?? p.Alignment;
        double em = p.BaseEm;

        // 1. Pick a font for every character: the original where it has the glyph, a look-alike where not.
        var coverage = new Coverage(this, page);
        coverage.Check(chars.Select(c => (c.Style, c.Ch)));
        var standIns = new Dictionary<StyleRef, StyleRef>();
        var systemStandIns = new Dictionary<StyleRef, StyleRef?>();
        var missing = chars.Where(c => !coverage.Has(c.Style, c.Ch)).ToList();
        foreach (var c in missing)
            if (!standIns.ContainsKey(c.Style)) standIns[c.Style] = StandardLookAlike(c.Style);
        coverage.Check(missing.Select(c => (standIns[c.Style], c.Ch)));

        bool substituted = false;
        var items = new List<Item>(chars.Count);
        foreach (var c in chars)
        {
            var style = c.Style;
            if (!coverage.Has(style, c.Ch))
            {
                substituted = true;
                style = standIns[style];
                if (!coverage.Has(style, c.Ch))
                {
                    if (!systemStandIns.TryGetValue(c.Style, out var system)) systemStandIns[c.Style] = system = SystemLookAlike(c.Style);
                    style = system ?? style;
                }
            }
            items.Add(new Item(c.Ch, style, c.Rise, c.Ch == '\n' ? 0 : style.Advance(c.Ch)));
        }

        // 2. Wrap the words into the paragraph's box.
        double boxLeft = p.BoxLeft, boxRight = p.BoxRight;
        double indent = alignment is TextAlignment.Left or TextAlignment.Justify && p.Alignment is TextAlignment.Left or TextAlignment.Justify ? p.FirstIndent : 0;
        double width = boxRight - boxLeft;
        var words = ToWords(items);
        if (p.Hanging && indent < 0 && words.Count > 1)
            words[1].SpaceBefore = Math.Max(words[1].SpaceBefore, -indent - words[0].Width); // keep the text after a list marker lined up
        var lines = items.Count == 0 ? [] : Wrap(words, width, indent);
        lines.RemoveAll(l => l.Words.Count == 0 && !l.EndsWithHardBreak && lines.Count > 1);

        double baseline = p.FirstBaseline;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            line.Em = line.Words.SelectMany(w => w.Items).Select(it => it.Style.Em).DefaultIfEmpty(em).Max();
            if (i > 0) baseline += p.Spacing * Math.Max(1, line.Em / em);
            line.Baseline = baseline;
            double available = width - (i == 0 ? indent : 0);
            double start = boxLeft + (i == 0 ? indent : 0);
            line.X = alignment switch
            {
                TextAlignment.Center => start + (available - line.Width) / 2,
                TextAlignment.Right => start + available - line.Width,
                _ => start,
            };
            bool last = i == lines.Count - 1 || line.EndsWithHardBreak;
            if (alignment == TextAlignment.Justify && !last && line.Words.Count > 1)
                line.GapExtra = Math.Max(0, (available - line.Width) / (line.Words.Count - 1));
        }

        double oldLast = p.LastBaseline;
        double dy;
        if (lines.Count > 0) dy = lines[^1].Baseline - oldLast;
        else
        {
            // Removed: what follows in this column moves up to where this paragraph began.
            var next = analysis.Paragraphs.Where(o => o != p && o.FirstBaseline > oldLast && o.Bounds.Right > p.Bounds.X && o.Bounds.X < p.Bounds.Right)
                                          .OrderBy(o => o.FirstBaseline).FirstOrDefault();
            dy = next is null ? -(oldLast - p.FirstBaseline + p.Spacing) : -(next.FirstBaseline - p.FirstBaseline);
        }
        var oldBounds = p.Bounds;
        double descent = oldBounds.Bottom - oldLast;

        // 3. Decide what else on the page has to change.
        var paragraphObjects = p.Glyphs.Select(g => g.Obj).ToHashSet();
        var paragraphGlyphs = p.Glyphs.ToHashSet();
        double columnLeft = Math.Min(oldBounds.X, boxLeft) - 1, columnRight = Math.Max(oldBounds.Right, boxRight) + 1;
        double footerTop = page.Height * 0.92;
        bool push = options.PushFollowingContent && Math.Abs(dy) > 0.01;
        bool InColumn(PdfRect r) => r.Right > columnLeft && r.X < columnRight;
        bool Below(double baselineY) => baselineY > oldLast + 0.3 * em;

        var rebuild = new HashSet<IntPtr>(paragraphObjects); // objects to take apart and redraw
        var move = new List<IntPtr>();
        foreach (var obj in analysis.Objects)
        {
            if (paragraphObjects.Contains(obj)) continue;
            var bounds = ObjectBounds(page, obj);
            if (analysis.GlyphsByObject.TryGetValue(obj, out var glyphs) && glyphs.Any(g => !g.IsSpace))
            {
                var ink = glyphs.Where(g => !g.IsSpace).ToList();
                if (!push || !InColumn(bounds)) continue;
                int below = ink.Count(g => Below(g.Y) && g.Box.Y < footerTop);
                if (below == ink.Count) move.Add(obj);
                else if (below > 0) rebuild.Add(obj); // part of it is below the paragraph
            }
            else if (Native.FPDFPageObj_GetType(obj) == Native.PAGEOBJ_TEXT && Overlap(bounds, oldBounds) > 0.6 * Area(bounds))
            {
                // Text PDFium left out of its text layer (usually a duplicate drawn for a bold effect).
                rebuild.Add(obj);
            }
            else if (push && InColumn(bounds) && bounds.Y >= oldBounds.Bottom - 0.1 * em && bounds.Y < footerTop)
            {
                move.Add(obj);
            }
        }

        // 4. Draw the new text, and redraw whatever else the taken-apart objects held.
        //    New objects are created before old ones are destroyed: they share the old fonts.
        var created = new List<IntPtr>();
        // New text goes where the old text sat in the drawing order, so it keeps its place in reading order.
        int insertAt = analysis.Objects.FindIndex(rebuild.Contains);
        IntPtr Draw(string text, StyleRef style, double x, double y)
        {
            var obj = DrawText(page, text, style, x, y, insertAt < 0 ? null : insertAt++);
            created.Add(obj);
            return obj;
        }
        double newBottom = 0, minX = double.MaxValue, maxX = double.MinValue;
        foreach (var line in lines)
        {
            double x = line.X;
            for (int w = 0; w < line.Words.Count; w++)
            {
                var word = line.Words[w];
                if (w > 0) x += word.SpaceBefore + line.GapExtra;
                minX = Math.Min(minX, x);
                int k = 0;
                while (k < word.Items.Count)
                {
                    int end = k;
                    while (end < word.Items.Count && ReferenceEquals(word.Items[end].Style, word.Items[k].Style) && word.Items[end].Rise == word.Items[k].Rise) end++;
                    var segment = word.Items.GetRange(k, end - k);
                    Draw(new string(segment.Select(s => s.Ch).ToArray()), segment[0].Style, x, line.Baseline + segment[0].Rise);
                    x += segment.Sum(s => s.Advance);
                    k = end;
                }
                maxX = Math.Max(maxX, x);
            }
            newBottom = Math.Max(newBottom, line.Baseline + descent * Math.Max(1, line.Em / em));
        }

        foreach (var obj in rebuild)
        {
            if (!analysis.GlyphsByObject.TryGetValue(obj, out var glyphs)) continue;
            foreach (var run in Runs(glyphs.Where(g => !paragraphGlyphs.Contains(g) && !g.IsSpace).ToList()))
            {
                var first = run[0];
                double shift = push && Below(first.Y) && first.Box.Y < footerTop && first.X < columnRight && first.End > columnLeft ? dy : 0;
                Draw(new string(run.Select(g => g.Ch).ToArray()), first.Style, first.X, first.Y + shift);
            }
        }

        // 5. Move what lies below, then remove what was replaced.
        var shiftUser = (X: page.ViewToUser.C * dy, Y: page.ViewToUser.D * dy);
        bool offPage = newBottom > page.Height + 0.5;
        foreach (var obj in move)
        {
            Native.FPDFPageObj_Transform(obj, 1, 0, 0, 1, shiftUser.X, shiftUser.Y);
            if (ObjectBounds(page, obj).Bottom > page.Height + 0.5) offPage = true;
        }
        foreach (var obj in rebuild)
        {
            Native.FPDFPage_RemoveObject(page.Handle, obj);
            Native.FPDFPageObj_Destroy(obj);
        }
        int movedAnnotations = push ? MoveAnnotations(page, oldBounds.Bottom - 0.3 * em, footerTop, columnLeft, columnRight, shiftUser) : 0;
        page.Dirty = page.ChangedExisting = true;
        _charsByFont = null; // the document's text changed

        var bounds2 = lines.Count == 0
            ? new PdfRect(oldBounds.X, oldBounds.Y, 0, 0)
            : new PdfRect(minX, oldBounds.Y, maxX - minX, newBottom - oldBounds.Y);
        return new ReflowResult(bounds2, lines.Count, dy, substituted, offPage, move.Count + movedAnnotations);
    }

    /// <summary>Splits glyphs into runs that can each be drawn as one piece of text at the first glyph's position.</summary>
    private static IEnumerable<List<Glyph>> Runs(List<Glyph> glyphs)
    {
        var run = new List<Glyph>();
        foreach (var g in glyphs.OrderBy(g => g.Index))
        {
            if (run.Count > 0)
            {
                var prev = run[^1];
                double em = g.Style.Em;
                bool continues = g.Index == prev.Index + 1 && !g.SpaceBefore && Math.Abs(g.Y - prev.Y) < 0.05 * em && Math.Abs(g.X - prev.End) < 0.1 * em;
                if (!continues) { yield return run; run = []; }
            }
            run.Add(g);
        }
        if (run.Count > 0) yield return run;
    }

    private IntPtr DrawText(PageHandle page, string text, StyleRef style, double x, double baseline, int? index = null)
    {
        var obj = Native.FPDFPageObj_CreateTextObj(Doc, style.Font, style.FontSize);
        if (obj == IntPtr.Zero) throw new PdfException("Could not create text.");
        Native.FPDFText_SetText(obj, text);
        var (ux, uy) = page.ViewToUser.Apply(x, baseline);
        var m = (style.Matrix with { E = ux, F = uy }).ToNative();
        Native.FPDFPageObj_SetMatrix(obj, &m);
        Native.FPDFPageObj_SetFillColor(obj, style.Fill.R, style.Fill.G, style.Fill.B, style.Fill.A);
        if (style.RenderMode != 0)
        {
            Native.FPDFTextObj_SetTextRenderMode(obj, style.RenderMode);
            Native.FPDFPageObj_SetStrokeColor(obj, style.Stroke.R, style.Stroke.G, style.Stroke.B, style.Stroke.A);
        }
        if (index is { } at) Native.FPDFPage_InsertObjectAtIndex(page.Handle, obj, (nuint)at);
        else Native.FPDFPage_InsertObject(page.Handle, obj);
        return obj;
    }

    private static int MoveAnnotations(PageHandle page, double fromY, double footerTop, double columnLeft, double columnRight, (double X, double Y) shift)
    {
        int moved = 0;
        int count = Native.FPDFPage_GetAnnotCount(page.Handle);
        for (int i = 0; i < count; i++)
        {
            var annot = Native.FPDFPage_GetAnnot(page.Handle, i);
            if (annot == IntPtr.Zero) continue;
            try
            {
                Native.FS_RECTF rect;
                if (Native.FPDFAnnot_GetRect(annot, &rect) == 0) continue;
                var view = page.UserToView.MapBounds(rect.left, rect.bottom, rect.right, rect.top);
                if (view.Y < fromY || view.Y >= footerTop || view.Right <= columnLeft || view.X >= columnRight) continue;

                int quads = (int)Native.FPDFAnnot_CountAttachmentPoints(annot);
                for (int q = 0; q < quads; q++)
                {
                    Native.FS_QUADPOINTSF quad;
                    if (Native.FPDFAnnot_GetAttachmentPoints(annot, (nuint)q, &quad) == 0) continue;
                    float dx = (float)shift.X, dy = (float)shift.Y;
                    quad.x1 += dx; quad.x2 += dx; quad.x3 += dx; quad.x4 += dx;
                    quad.y1 += dy; quad.y2 += dy; quad.y3 += dy; quad.y4 += dy;
                    Native.FPDFAnnot_SetAttachmentPoints(annot, (nuint)q, &quad);
                }
                rect.left += (float)shift.X; rect.right += (float)shift.X;
                rect.top += (float)shift.Y; rect.bottom += (float)shift.Y;
                Native.FPDFAnnot_SetRect(annot, &rect);
                moved++;
            }
            finally { Native.FPDFPage_CloseAnnot(annot); }
        }
        return moved;
    }

    private static double Area(PdfRect r) => Math.Max(0, r.Width) * Math.Max(0, r.Height);

    private static double Overlap(PdfRect a, PdfRect b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X)) * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y));
}
