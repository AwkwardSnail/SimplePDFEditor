using System.Text.RegularExpressions;
using PdfEditor.Core.Interop;

namespace PdfEditor.Core.Reflow;

/// <summary>
/// One way of drawing text: a font at a size, under a transform, in a colour. Taken from an
/// existing text object or derived from one. Coordinates and sizes are in view space.
/// </summary>
internal sealed unsafe class StyleRef
{
    private readonly Dictionary<int, double> _widths = new();

    public required IntPtr Font { get; init; }        // valid while the page that owns it is loaded
    public required string FontName { get; init; }
    public required float FontSize { get; init; }     // the PDF "Tf" size, before the transform
    public required Affine Matrix { get; init; }      // linear part of the object's matrix, user space
    public required Affine Lin { get; init; }         // Matrix followed by user-to-view, linear part
    public PdfColor Fill { get; init; } = PdfColor.Black;
    public PdfColor Stroke { get; init; } = PdfColor.Black;
    public int RenderMode { get; init; }
    /// <summary>The font came from the document (so it may be a subset), rather than being one we chose.</summary>
    public bool FromDocument { get; init; }
    /// <summary>The font program is inside the PDF, so it may hold only the glyphs the document uses.</summary>
    public bool Embedded { get; init; }
    /// <summary>Set for fonts we embedded ourselves, which cover whatever the font file covers.</summary>
    public bool CoversEverything { get; init; }

    /// <summary>Visible size (em) in view space.</summary>
    public double Em => -Lin.D * FontSize;

    /// <summary>Upright, left-to-right text on the page as displayed: the only kind we reflow.</summary>
    public bool IsHorizontal => Lin.A > 0 && Math.Abs(Lin.B) <= 0.02 * Lin.A && Lin.D < 0;

    public string DisplayName => Regex.Replace(FontName, "^[A-Z]{6}\\+", "");
    public bool Bold => Regex.IsMatch(FontName, "Bold|Black|Heavy|Semibold|SemiBold|Demi", RegexOptions.IgnoreCase);
    public bool Italic => Regex.IsMatch(FontName, "Italic|Oblique", RegexOptions.IgnoreCase) || Math.Abs(Lin.C) > 0.05 * Math.Abs(Lin.D);

    /// <summary>Horizontal advance of a character in view space.</summary>
    public double Advance(int codePoint)
    {
        if (_widths.TryGetValue(codePoint, out var w)) return w;
        float width = 0;
        if (Native.FPDFFont_GetGlyphWidth(Font, (uint)codePoint, FontSize, &width) == 0) width = 0;
        w = width * Lin.A;
        if (codePoint == ' ' && w <= 0.01 * Em) w = 0.25 * Em; // some subset fonts have no usable space width
        return _widths[codePoint] = w;
    }

    public double Advance(char c) => char.IsSurrogate(c) ? 0.5 * Em : Advance((int)c);
}

/// <summary>A character drawn on the page.</summary>
internal sealed class Glyph
{
    public required int Index { get; init; }      // position in PDFium's text page
    public required char Ch { get; init; }
    public required double X { get; init; }       // origin (baseline start), view space
    public required double Y { get; init; }
    public required PdfRect Box { get; init; }    // loose box: font ascent to descent
    public required IntPtr Obj { get; init; }
    public required StyleRef Style { get; init; }
    public bool SpaceBefore { get; set; }
    public Line? Line { get; set; }
    public bool IsSpace => char.IsWhiteSpace(Ch) && Ch != ' ';
    public double End => X + Style.Advance(Ch);
}

internal sealed class Line
{
    public List<Glyph> Glyphs { get; } = [];
    public double Baseline { get; private set; }
    public double Left { get; private set; }
    public double Right { get; private set; }
    public double Top { get; private set; }
    public double Bottom { get; private set; }
    public double Em { get; private set; }
    public string DominantFont { get; private set; } = "";
    public double DominantShare { get; private set; }
    public double Width => Right - Left;
    public double Center => (Left + Right) / 2;

    public void Finish()
    {
        Em = Mode(Glyphs.Select(g => g.Style.Em), 0.05);
        // The baseline most characters sit on, ignoring superscripts and subscripts.
        Baseline = Glyphs.GroupBy(g => Math.Round(g.Y / Math.Max(0.5, 0.05 * Em)))
                         .OrderByDescending(g => g.Count()).First().Average(g => g.Y);
        Left = Glyphs.Min(g => g.X);
        Right = Glyphs.Max(g => g.End);
        Top = Glyphs.Min(g => g.Box.Y);
        Bottom = Glyphs.Max(g => g.Box.Bottom);
        var fonts = Glyphs.GroupBy(g => g.Style.DisplayName).OrderByDescending(g => g.Count()).First();
        DominantFont = fonts.Key;
        DominantShare = fonts.Count() / (double)Glyphs.Count;
    }

    /// <summary>Width of the first word, used to tell a wrapped line from a deliberately short one.</summary>
    public double FirstWordWidth()
    {
        double w = 0;
        for (int i = 0; i < Glyphs.Count; i++)
        {
            if (i > 0 && Glyphs[i].SpaceBefore) break;
            w += Glyphs[i].Style.Advance(Glyphs[i].Ch);
        }
        return w;
    }

    internal static double Mode(IEnumerable<double> values, double bucket) =>
        values.GroupBy(v => Math.Round(v / bucket)).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Average();
}

/// <summary>A character of a paragraph's text with the style it is drawn in.</summary>
/// <param name="Rise">Offset from the line's baseline (positive is lower), for superscripts and subscripts.</param>
internal readonly record struct PChar(char Ch, StyleRef Style, double Rise);

internal sealed class Paragraph
{
    public List<Line> Lines { get; } = [];
    public List<PChar> Chars { get; } = [];
    public TextAlignment Alignment { get; set; } = TextAlignment.Left;
    public double BaseEm { get; set; }
    public StyleRef BaseStyle { get; set; } = null!;
    public double Spacing { get; set; }
    public double BodyLeft { get; set; }
    public double FirstIndent { get; set; }
    /// <summary>A list item: a marker hangs to the left of the text, which lines up at <see cref="BodyLeft"/>.</summary>
    public bool Hanging { get; set; }
    /// <summary>How the lines were found to line up when they were grouped: left, centre or right.</summary>
    public TextAlignment JoinedAs { get; set; } = TextAlignment.Left;
    /// <summary>The box lines wrap within.</summary>
    public double BoxLeft { get; set; }
    public double BoxRight { get; set; }
    public bool Editable { get; set; } = true;

    public double FirstBaseline => Lines[0].Baseline;
    public double LastBaseline => Lines[^1].Baseline;
    public PdfRect Bounds => new(Lines.Min(l => l.Left), Lines.Min(l => l.Top), Lines.Max(l => l.Right) - Lines.Min(l => l.Left), Lines.Max(l => l.Bottom) - Lines.Min(l => l.Top));
    public IEnumerable<Glyph> Glyphs => Lines.SelectMany(l => l.Glyphs);

    public string Text => new(Chars.Select(c => c.Ch).ToArray());

    public TextParagraph ToPublic(int index)
    {
        var spans = new List<TextSpan>();
        int start = 0;
        for (int i = 1; i <= Chars.Count; i++)
        {
            if (i < Chars.Count && ReferenceEquals(Chars[i].Style, Chars[start].Style)) continue;
            var s = Chars[start].Style;
            spans.Add(new TextSpan(start, i - start, s.DisplayName, Math.Round(s.Em, 2), s.Fill, s.Bold, s.Italic));
            start = i;
        }
        return new TextParagraph(index, Text, Bounds, Alignment, Math.Round(BaseEm, 2), Math.Round(Spacing, 2), Lines.Count, spans, Editable);
    }
}

/// <summary>Reads a page's text and works out its lines and paragraphs.</summary>
internal sealed unsafe class PageAnalysis
{
    public required PdfDocument.PageHandle Page { get; init; }
    public List<Glyph> Glyphs { get; } = [];
    public List<Paragraph> Paragraphs { get; } = [];
    public Dictionary<IntPtr, List<Glyph>> GlyphsByObject { get; } = [];
    /// <summary>Top-level page objects as they were when the page was analysed.</summary>
    public List<IntPtr> Objects { get; } = [];
    public double LeftMargin { get; private set; }

    private readonly Dictionary<IntPtr, StyleRef?> _objectStyles = [];
    // Identical looks share one StyleRef, however many objects use them.
    private readonly Dictionary<(IntPtr, float, Affine, PdfColor, PdfColor, int), StyleRef> _styles = [];

    public static PageAnalysis Analyze(PdfDocument.PageHandle page)
    {
        var analysis = new PageAnalysis { Page = page };
        analysis.ReadGlyphs();
        var lines = analysis.BuildLines();
        analysis.BuildParagraphs(lines);
        analysis.FinishParagraphs();
        return analysis;
    }

    // ------------------------------------------------------------------ reading characters

    private void ReadGlyphs()
    {
        int count = Native.FPDFPage_CountObjects(Page.Handle);
        var topLevel = new HashSet<IntPtr>();
        for (int i = 0; i < count; i++)
        {
            var obj = Native.FPDFPage_GetObject(Page.Handle, i);
            Objects.Add(obj);
            topLevel.Add(obj);
        }

        var textPage = Native.FPDFText_LoadPage(Page.Handle);
        if (textPage == IntPtr.Zero) return;
        try
        {
            int chars = Native.FPDFText_CountChars(textPage);
            for (int i = 0; i < chars; i++)
            {
                char ch = (char)Native.FPDFText_GetUnicode(textPage, i);
                if (Native.FPDFText_IsHyphen(textPage, i) == 1) ch = '-'; // PDFium reports a line-end hyphen as U+0002
                if (Native.FPDFText_IsGenerated(textPage, i) == 1)
                {
                    // PDFium adds spaces where it sees a gap between words; remember them as word breaks.
                    if (ch == ' ' && Glyphs.Count > 0) _pendingSpaces.Add(Glyphs.Count);
                    continue;
                }
                var obj = Native.FPDFText_GetTextObject(textPage, i);
                if (obj == IntPtr.Zero || !topLevel.Contains(obj)) continue; // text inside a form XObject stays as it is
                var style = StyleOf(obj);
                if (style is null) continue;

                double ox, oy;
                Native.FPDFText_GetCharOrigin(textPage, i, &ox, &oy);
                var origin = Page.UserToView.Apply(ox, oy);
                Native.FS_RECTF r;
                PdfRect box = Native.FPDFText_GetLooseCharBox(textPage, i, &r) != 0
                    ? Page.UserToView.MapBounds(r.left, r.bottom, r.right, r.top)
                    : new PdfRect(origin.X, origin.Y - style.Em * 0.8, style.Advance(ch), style.Em);

                var glyph = new Glyph { Index = i, Ch = ch, X = origin.X, Y = origin.Y, Box = box, Obj = obj, Style = style };
                Glyphs.Add(glyph);
                if (!GlyphsByObject.TryGetValue(obj, out var list)) GlyphsByObject[obj] = list = [];
                list.Add(glyph);
            }
        }
        finally { Native.FPDFText_ClosePage(textPage); }
    }

    private readonly HashSet<int> _pendingSpaces = [];

    internal StyleRef? StyleOf(IntPtr obj)
    {
        if (_objectStyles.TryGetValue(obj, out var cached)) return cached;
        StyleRef? style = null;
        var font = Native.FPDFTextObj_GetFont(obj);
        float size = 0;
        Native.FS_MATRIX m;
        if (font != IntPtr.Zero && Native.FPDFTextObj_GetFontSize(obj, &size) != 0 && size > 0 && Native.FPDFPageObj_GetMatrix(obj, &m) != 0)
        {
            uint r = 0, g = 0, b = 0, a = 255, sr = 0, sg = 0, sb = 0, sa = 255;
            Native.FPDFPageObj_GetFillColor(obj, &r, &g, &b, &a);
            Native.FPDFPageObj_GetStrokeColor(obj, &sr, &sg, &sb, &sa);
            var matrix = new Affine(m.a, m.b, m.c, m.d, 0, 0);
            style = new StyleRef
            {
                Font = font,
                FontName = PdfDocument.FontName(obj),
                FontSize = size,
                Matrix = matrix,
                Lin = Affine.Concat(matrix, Page.UserToView.WithoutTranslation()),
                Fill = new PdfColor((byte)r, (byte)g, (byte)b, (byte)a),
                Stroke = new PdfColor((byte)sr, (byte)sg, (byte)sb, (byte)sa),
                RenderMode = Math.Max(0, Native.FPDFTextObj_GetTextRenderMode(obj)),
                FromDocument = true,
                Embedded = Native.FPDFFont_GetIsEmbedded(font) == 1,
            };
            if (!style.IsHorizontal || style.Em <= 0.1) style = null;
            else
            {
                var key = (font, size, matrix, style.Fill, style.Stroke, style.RenderMode);
                if (_styles.TryGetValue(key, out var shared)) style = shared;
                else _styles[key] = style;
            }
        }
        return _objectStyles[obj] = style;
    }

    // ------------------------------------------------------------------ lines

    private List<Line> BuildLines()
    {
        var lines = new List<Line>();
        Line? current = null;
        Glyph? previous = null;
        bool pendingSpace = false;
        double top = 0, bottom = 0;

        for (int i = 0; i < Glyphs.Count; i++)
        {
            var g = Glyphs[i];
            if (_pendingSpaces.Contains(i)) pendingSpace = true;
            if (g.IsSpace)
            {
                pendingSpace = true;
                continue;
            }
            if (current is not null && previous is not null && SameLine(previous, g, top, bottom))
            {
                double em = Math.Max(previous.Style.Em, g.Style.Em);
                g.SpaceBefore = pendingSpace || g.X - previous.End > 0.15 * em;
                top = Math.Min(top, g.Box.Y);
                bottom = Math.Max(bottom, g.Box.Bottom);
            }
            else
            {
                current = new Line();
                lines.Add(current);
                top = g.Box.Y;
                bottom = g.Box.Bottom;
            }
            current.Glyphs.Add(g);
            g.Line = current;
            previous = g;
            pendingSpace = false;
        }
        foreach (var line in lines) line.Finish();
        return lines;
    }

    private static bool SameLine(Glyph previous, Glyph g, double top, double bottom)
    {
        double em = Math.Max(previous.Style.Em, g.Style.Em);
        if (g.X < previous.X - 0.1 * em) return false;          // went back: a new line
        if (g.X - previous.End > 3 * em) return false;          // a wide gap: another column or a table cell
        double overlap = Math.Min(bottom, g.Box.Bottom) - Math.Max(top, g.Box.Y);
        return overlap >= 0.5 * Math.Min(bottom - top, g.Box.Height);
    }

    // ------------------------------------------------------------------ paragraphs

    private enum Mode { Unknown, Left, Center, Right }

    /// <summary>The usual distance between lines, per text size: the most common gap between neighbouring lines.</summary>
    private readonly Dictionary<double, double> _typicalGap = [];

    private void LearnLineSpacing(List<Line> lines)
    {
        var gaps = new List<(double Size, double Gap)>();
        for (int i = 1; i < lines.Count; i++)
        {
            var (a, b) = (lines[i - 1], lines[i]);
            double gap = b.Baseline - a.Baseline;
            if (Math.Abs(a.Em - b.Em) > 0.1 * a.Em || gap < 0.7 * a.Em || gap > 2.2 * a.Em) continue;
            if (b.Right < a.Left || b.Left > a.Right) continue;
            // Only count lines that clearly wrapped (the next word did not fit): their gap is line
            // spacing, not the space between two paragraphs.
            double columnRight = lines.Where(l => Math.Abs(l.Left - a.Left) < 2 * a.Em).Max(l => l.Right);
            if (a.Right + a.Glyphs[^1].Style.Advance(' ') + b.FirstWordWidth() < columnRight - 0.3 * a.Em) continue;
            gaps.Add((Math.Round(a.Em * 2) / 2, gap));
        }
        foreach (var size in gaps.GroupBy(g => g.Size))
        {
            var common = size.GroupBy(g => Math.Round(g.Gap * 4) / 4).OrderByDescending(g => g.Count()).First();
            if (common.Count() >= 2) _typicalGap[size.Key] = common.Average(g => g.Gap);
        }
    }

    private double? TypicalGap(double em) => _typicalGap.TryGetValue(Math.Round(em * 2) / 2, out var gap) ? gap : null;

    private void BuildParagraphs(List<Line> lines)
    {
        LearnLineSpacing(lines);
        Paragraph? current = null;
        Mode mode = Mode.Unknown;
        foreach (var line in lines)
        {
            if (current is not null && Continues(current, ref mode, line, TypicalGap(line.Em), ColumnRight(lines, current.Lines[^1]), PageRight(lines, current.Lines[^1])))
            {
                current.Lines.Add(line);
                current.JoinedAs = mode switch { Mode.Center => TextAlignment.Center, Mode.Right => TextAlignment.Right, _ => TextAlignment.Left };
                continue;
            }
            current = new Paragraph();
            current.Lines.Add(line);
            Paragraphs.Add(current);
            mode = Mode.Unknown;
        }
    }

    /// <summary>Decides whether a line carries on the paragraph above it.</summary>
    /// <summary>How far right lines that start where this one does reach: the column's right edge.</summary>
    private static double ColumnRight(List<Line> lines, Line line) =>
        lines.Where(l => Math.Abs(l.Left - line.Left) < 2 * line.Em && l.Em <= 1.2 * line.Em).Max(l => l.Right);

    /// <summary>
    /// The right edge of the page's text area (margins assumed equal), or null when other text
    /// sits beside the line, so the page has columns.
    /// </summary>
    private double? PageRight(List<Line> lines, Line line)
    {
        bool beside = lines.Any(r => r.Left > line.Right + line.Em && Math.Abs(r.Baseline - line.Baseline) < 20 * line.Em);
        return beside ? null : Page.Width - Math.Max(18, lines.Min(l => l.Left));
    }

    private static bool Continues(Paragraph p, ref Mode mode, Line l, double? typicalGap, double columnRight, double? pageRight)
    {
        var q = p.Lines[^1];
        double em = Line.Mode(p.Lines.Select(x => x.Em), 0.05);
        double gap = l.Baseline - q.Baseline;
        if (gap < 0.7 * em || gap > 2.2 * em) return false;
        // Lines of one paragraph sit at the paragraph's spacing, or the page's usual spacing for this size;
        // a bigger gap is space between paragraphs.
        double? spacing = p.Lines.Count >= 2 ? p.Lines[^1].Baseline - p.Lines[^2].Baseline : typicalGap;
        if (spacing is { } expected && Math.Abs(gap - expected) > 0.15 * expected + 0.5) return false;
        if (Math.Abs(l.Em - em) > 0.2 * em) return false;
        if (l.Right < p.Lines.Min(x => x.Left) || l.Left > p.Lines.Max(x => x.Right)) return false;
        if (l.DominantFont != q.DominantFont && l.DominantShare >= 0.9 && q.DominantShare >= 0.9) return false; // e.g. a bold heading above body text

        double tolerance = 0.6 * em;
        // Lines that share an edge share it exactly (positions are glyph origins, not ink), so edges are compared tightly.
        double exact = Math.Max(1, 0.15 * em);
        double bodyLeft = p.Lines.Count >= 2 ? p.Lines[1].Left : q.Left;
        bool leftAligned = Math.Abs(l.Left - bodyLeft) <= exact;
        bool centered = Math.Abs(l.Center - q.Center) <= tolerance && Math.Abs(l.Left - q.Left) > exact;
        bool rightAligned = Math.Abs(l.Right - q.Right) <= exact && Math.Abs(l.Left - q.Left) > exact;
        bool firstLineIndented = p.Lines.Count == 1 && l.Left < q.Left - tolerance && l.Left > q.Left - 6 * em;
        bool hanging = p.Lines.Count == 1 && l.Left > q.Left + tolerance && StartsWithMarker(q) && Math.Abs(l.Left - SecondWordStart(q)) <= tolerance;

        // Centred lines can also look like an indented first line; matching centres with unmatched right edges decides it.
        // (An indented first line puts the centres half an indent apart; centred lines share theirs almost exactly.)
        bool clearlyCentered = centered && Math.Abs(l.Right - q.Right) > exact && Math.Abs(l.Center - q.Center) <= exact;
        Mode lineMode = clearlyCentered ? Mode.Center
            : leftAligned || firstLineIndented || hanging ? Mode.Left
            : centered ? Mode.Center
            : rightAligned ? Mode.Right
            : Mode.Unknown;
        if (lineMode == Mode.Unknown || mode != Mode.Unknown && lineMode != mode) return false;
        if (p.Lines.Count >= 2 && mode == Mode.Left && !leftAligned) return false;

        // A line only wraps when the next word would not fit. If the line above had room for this
        // line's first word, it ended on purpose: a new paragraph starts here.
        double space = q.Glyphs[^1].Style.Advance(' ');
        double word = l.FirstWordWidth();
        double widest = Math.Max(p.Lines.Max(x => x.Width), l.Width);
        double slack = 0.3 * em;
        bool hadRoom = lineMode switch
        {
            Mode.Left => q.Right + space + word < Math.Max(Math.Max(p.Lines.Max(x => x.Right), l.Right), columnRight) - slack,
            _ => q.Width + space + word < widest - slack,
        };
        // A finished sentence with room to spare before the page's margin, followed by a capital,
        // also ends a paragraph (a run of one-line paragraphs gives no other clue).
        if (!hadRoom && lineMode == Mode.Left && pageRight is { } edge && q.Right + space + word < edge - slack
            && q.Glyphs[^1].Ch is '.' or '!' or '?' or ':' && (char.IsUpper(l.Glyphs[0].Ch) || char.IsDigit(l.Glyphs[0].Ch)))
            hadRoom = true;
        if (hadRoom) return false;

        mode = lineMode;
        return true;
    }

    private static bool StartsWithMarker(Line line)
    {
        var first = new string(line.Glyphs.TakeWhile((g, i) => i == 0 || !g.SpaceBefore).Select(g => g.Ch).ToArray());
        // A lone symbol (bullets from symbol fonts often sit in the private-use area), or a list number.
        return first.Length == 1 && !char.IsLetterOrDigit(first[0])
            || Regex.IsMatch(first, @"^(\(?[0-9]{1,3}[.)]|\(?[a-zA-Z][.)]|[ivxIVX]{1,4}[.)])$");
    }

    private static double SecondWordStart(Line line) =>
        line.Glyphs.Skip(1).FirstOrDefault(g => g.SpaceBefore)?.X ?? double.MaxValue;

    private void FinishParagraphs()
    {
        // Typical line spacing per text size on this page, for one-line paragraphs that grow.
        var spacingBySize = Paragraphs.Where(p => p.Lines.Count >= 2)
            .Select(p => (Em: Line.Mode(p.Lines.Select(l => l.Em), 0.05), Spacing: p.Lines[1].Baseline - p.Lines[0].Baseline))
            .ToList();
        LeftMargin = Paragraphs.Count == 0 ? 72 : Math.Max(18, Paragraphs.Min(p => p.Lines.Min(l => l.Left)));

        foreach (var p in Paragraphs)
        {
            var glyphs = p.Glyphs.ToList();
            p.BaseEm = Line.Mode(glyphs.Select(g => g.Style.Em), 0.05);
            p.BaseStyle = glyphs.GroupBy(g => g.Style).OrderByDescending(g => g.Count()).First().Key;

            if (p.Lines.Count >= 2)
            {
                var gaps = p.Lines.Zip(p.Lines.Skip(1), (a, b) => b.Baseline - a.Baseline).OrderBy(x => x).ToList();
                p.Spacing = gaps[gaps.Count / 2];
            }
            else
            {
                var similar = spacingBySize.Where(s => Math.Abs(s.Em - p.BaseEm) <= 0.1 * p.BaseEm).Select(s => s.Spacing).OrderBy(x => x).ToList();
                p.Spacing = similar.Count > 0 ? similar[similar.Count / 2] : 1.2 * p.BaseEm;
            }

            DetectAlignment(p);
            BuildChars(p);
            p.Editable = p.Chars.All(c => c.Ch != '\0' && c.Ch != '�' && !IsRightToLeft(c.Ch));
        }
    }

    private void DetectAlignment(Paragraph p)
    {
        double em = p.BaseEm, tol = Math.Max(1, 0.15 * em);
        var lines = p.Lines;
        if (lines.Count == 1)
        {
            var l = lines[0];
            p.BodyLeft = l.Left;
            p.FirstIndent = 0;
            bool pageCentered = Math.Abs(l.Center - Page.Width / 2) < Math.Max(2, 0.01 * Page.Width) && l.Left > LeftMargin + 2 * em;
            if (pageCentered)
            {
                p.Alignment = TextAlignment.Center;
                double half = Math.Max(l.Width / 2, Page.Width / 2 - LeftMargin);
                (p.BoxLeft, p.BoxRight) = (l.Center - half, l.Center + half);
                return;
            }
            p.Alignment = TextAlignment.Left;
            if (StartsWithMarker(l) && SecondWordStart(l) is var textStart && textStart < l.Right)
            {
                // A one-line list item: further lines should line up with its text, not its marker.
                p.Hanging = true;
                p.BodyLeft = textStart;
                p.FirstIndent = l.Left - textStart;
            }
            // Grow to the right margin (assumed equal to the left one), or to the next column.
            double limit = Page.Width - LeftMargin;
            foreach (var other in Paragraphs)
            {
                if (ReferenceEquals(other, p)) continue;
                var b = other.Lines.Count > 0 ? other.Bounds : default;
                if (b.Y < l.Bottom && b.Bottom > l.Top && b.X >= l.Right) limit = Math.Min(limit, b.X - em);
            }
            (p.BoxLeft, p.BoxRight) = (p.BodyLeft, Math.Max(l.Right, limit));
            return;
        }

        var body = lines.Skip(1).ToList();
        p.BodyLeft = body.Select(l => l.Left).OrderBy(x => x).ElementAt(body.Count / 2);
        // With only two lines "the left edges agree" says nothing, so trust how the lines were joined.
        bool leftAligned = p.JoinedAs == TextAlignment.Left && body.All(l => Math.Abs(l.Left - p.BodyLeft) <= tol);
        var notLast = lines.Take(lines.Count - 1).ToList();
        double alignedRight = notLast.Max(l => l.Right);

        if (leftAligned)
        {
            p.FirstIndent = lines[0].Left - p.BodyLeft;
            p.Hanging = p.FirstIndent < -tol && StartsWithMarker(lines[0]);
            bool rightsAligned = notLast.All(l => alignedRight - l.Right <= 0.3 * em);
            // Justified lines have wider gaps than plain spaces would give (a list marker's gap does not count).
            bool stretched = notLast.Any(l => StretchOf(l, skipMarker: p.Hanging && ReferenceEquals(l, lines[0])) > 0.4 * em);
            p.Alignment = rightsAligned && stretched ? TextAlignment.Justify : TextAlignment.Left;
            (p.BoxLeft, p.BoxRight) = (p.BodyLeft, Math.Max(alignedRight, lines[^1].Right));
        }
        else if (p.JoinedAs == TextAlignment.Center || lines.All(l => Math.Abs(l.Center - lines[0].Center) <= tol))
        {
            p.Alignment = TextAlignment.Center;
            double center = lines.Select(l => l.Center).OrderBy(x => x).ElementAt(lines.Count / 2);
            double half = lines.Max(l => l.Width) / 2;
            (p.BoxLeft, p.BoxRight) = (center - half, center + half);
            p.BodyLeft = p.BoxLeft;
        }
        else
        {
            p.Alignment = TextAlignment.Right;
            double right = lines.Select(l => l.Right).OrderBy(x => x).ElementAt(lines.Count / 2);
            (p.BoxLeft, p.BoxRight) = (right - lines.Max(l => l.Width), right);
            p.BodyLeft = p.BoxLeft;
        }
    }

    /// <summary>How much wider a line is than it would be with ordinary spaces between its words.</summary>
    private static double StretchOf(Line l, bool skipMarker)
    {
        int start = 0;
        if (skipMarker)
        {
            start = l.Glyphs.FindIndex(1, g => g.SpaceBefore);
            if (start < 0) return 0;
        }
        double w = 0;
        for (int i = start; i < l.Glyphs.Count; i++)
        {
            var g = l.Glyphs[i];
            if (i > start && g.SpaceBefore) w += l.Glyphs[i - 1].Style.Advance(' ');
            w += g.Style.Advance(g.Ch);
        }
        return l.Right - l.Glyphs[start].X - w;
    }

    private static void BuildChars(Paragraph p)
    {
        for (int li = 0; li < p.Lines.Count; li++)
        {
            var line = p.Lines[li];
            if (li > 0)
            {
                // Join lines with a space, or rejoin a word that was hyphenated across them.
                int last = p.Chars.Count - 1;
                bool hyphenated = last >= 1 && p.Chars[last].Ch is '-' or '­' or '‐' && char.IsLetter(p.Chars[last - 1].Ch)
                                  && char.IsLower(line.Glyphs[0].Ch);
                if (hyphenated) p.Chars.RemoveAt(last);
                else if (p.Chars.Count > 0) p.Chars.Add(new PChar(' ', p.Chars[^1].Style, 0));
            }
            for (int i = 0; i < line.Glyphs.Count; i++)
            {
                var g = line.Glyphs[i];
                if (i > 0 && g.SpaceBefore) p.Chars.Add(new PChar(' ', line.Glyphs[i - 1].Style, 0));
                double rise = g.Y - line.Baseline;
                if (Math.Abs(rise) < 0.05 * g.Style.Em) rise = 0;
                p.Chars.Add(new PChar(g.Ch, g.Style, rise));
            }
        }
        // A space between two differently formatted words takes the paragraph's ordinary formatting
        // if either word has it ("plain |bold| plain"), otherwise the formatting of the word after it.
        for (int i = 1; i < p.Chars.Count - 1; i++)
        {
            if (p.Chars[i].Ch != ' ' || ReferenceEquals(p.Chars[i - 1].Style, p.Chars[i + 1].Style)) continue;
            var style = ReferenceEquals(p.Chars[i - 1].Style, p.BaseStyle) || ReferenceEquals(p.Chars[i + 1].Style, p.BaseStyle) ? p.BaseStyle : p.Chars[i + 1].Style;
            p.Chars[i] = p.Chars[i] with { Style = style };
        }
    }

    private static bool IsRightToLeft(char c) => c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿';
}
