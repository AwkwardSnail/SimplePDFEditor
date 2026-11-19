using System.Text;
using PdfEditor.Core.Interop;
using PdfEditor.Core.Reflow;
using PdfEditor.Core.Rtf;

namespace PdfEditor.Core;

public sealed record RtfImportOptions
{
    /// <summary>
    /// Embed the fonts the RTF names (when installed) instead of using the standard PDF fonts.
    /// Looks closer to the original but makes the file much larger: whole font files are embedded.
    /// </summary>
    public bool EmbedFonts { get; init; }
}

/// <param name="FirstPage">Index of the first page added.</param>
/// <param name="Warnings">What could not be imported, in plain words.</param>
public sealed record RtfImportResult(int FirstPage, int PageCount, IReadOnlyList<string> Warnings);

// RTF import: lays an RTF document out onto new pages.
public sealed unsafe partial class PdfDocument
{
    /// <summary>Creates a PDF from an RTF file.</summary>
    public static PdfDocument FromRtf(string path, RtfImportOptions? options = null) => FromRtf(File.ReadAllBytes(path), out _, options);

    /// <summary>Creates a PDF from RTF content.</summary>
    public static PdfDocument FromRtf(byte[] rtf, out RtfImportResult result, RtfImportOptions? options = null)
    {
        var doc = Create();
        try
        {
            result = doc.ImportRtf(rtf, 0, options);
            return doc;
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }

    /// <summary>Lays an RTF file out as new pages, inserted at <paramref name="index"/> (at the end when null).</summary>
    public RtfImportResult ImportRtf(string path, int? index = null, RtfImportOptions? options = null) => ImportRtf(File.ReadAllBytes(path), index, options);

    /// <summary>Lays RTF content out as new pages, inserted at <paramref name="index"/> (at the end when null).</summary>
    public RtfImportResult ImportRtf(byte[] rtf, int? index = null, RtfImportOptions? options = null)
    {
        int at = index ?? PageCount;
        if (at < 0 || at > PageCount) throw new ArgumentOutOfRangeException(nameof(index));
        var model = RtfParser.Parse(rtf);
        var writer = new RtfWriter(this, model, options ?? new(), at);
        writer.Write();
        return new RtfImportResult(at, writer.PagesAdded, model.Warnings);
    }

    /// <summary>Lays the parsed document out, page by page.</summary>
    private sealed class RtfWriter(PdfDocument owner, RtfDocument rtf, RtfImportOptions options, int firstPage)
    {
        private enum Kind { Char, Space, Tab, Break, Picture }

        private sealed record Piece(Kind Kind, char Ch, StyleRef Style, RtfCharFormat Format, double Width, double Rise, RtfPicture? Picture = null);

        private sealed class Placed(Piece piece, double x)
        {
            public Piece Piece { get; } = piece;
            public double X { get; set; } = x;
        }

        private sealed class LaidLine
        {
            public List<Placed> Pieces { get; } = [];
            public double Height { get; set; }
            public double Baseline { get; set; } // from the top of the line
            public bool EndsWithBreak { get; set; }
        }

        private readonly Dictionary<(RtfCharFormat, bool), StyleRef> _styles = new();
        private readonly Dictionary<StyleRef, (double Ascent, double Descent)> _metrics = new();
        private static readonly Encoding WinAnsi = WinAnsiEncoding();
        private PageHandle? _page;
        private double _y;
        private bool _atTop;
        private bool _warnedFonts;

        public int PagesAdded { get; private set; }

        private double Top => rtf.MarginTop;
        private double Bottom => rtf.PageHeight - rtf.MarginBottom;
        private double Left => rtf.MarginLeft;
        private double Right => rtf.PageWidth - rtf.MarginRight;

        private static Encoding WinAnsiEncoding()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }

        public void Write()
        {
            if (rtf.PageWidth < 72 || rtf.PageHeight < 72 || Right - Left < 36 || Bottom - Top < 36)
                throw new PdfException("The RTF file's page size or margins leave no room for text.");
            try
            {
                NewPage();
                foreach (var block in rtf.Blocks)
                {
                    switch (block)
                    {
                        case RtfPageBreak: if (!_atTop) NewPage(); break;
                        case RtfParagraph p: WriteParagraph(p); break;
                        case RtfTableRow row: WriteRow(row); _previous = null; break;
                    }
                }
            }
            finally { ClosePage(); }
        }

        // ------------------------------------------------------------------ pages

        private void NewPage()
        {
            ClosePage();
            var handle = Native.FPDFPage_New(owner.Doc, firstPage + PagesAdded, rtf.PageWidth, rtf.PageHeight);
            if (handle == IntPtr.Zero) throw new PdfException("Could not add a page.");
            _page = new PageHandle(handle, owner) { Dirty = true };
            PagesAdded++;
            _y = Top;
            _atTop = true;
        }

        private void ClosePage()
        {
            _page?.Dispose();
            _page = null;
        }

        // ------------------------------------------------------------------ paragraphs

        private RtfParaFormat? _previous;

        private void WriteParagraph(RtfParagraph p)
        {
            if (p.Format.PageBreakBefore && !_atTop) NewPage();
            // Contextual spacing: no space between paragraphs of the same style (list items, typically).
            bool together = p.Format.ContextualSpacing && _previous is { } prev && prev.Style == p.Format.Style;
            if (together) _y -= _previous!.SpaceAfter;
            else if (!_atTop || PagesAdded == 1) _y += p.Format.SpaceBefore;
            _previous = p.Format;
            foreach (var line in Layout(p, Left, Right))
            {
                if (_y + line.Height > Bottom && !_atTop) NewPage();
                Draw(line, _y);
                _y += line.Height;
                _atTop = false;
            }
            _y += p.Format.SpaceAfter;
        }

        private void WriteRow(RtfTableRow row)
        {
            const double padding = 2;
            var rights = row.CellRights.ToList();
            while (rights.Count < row.Cells.Count) rights.Add(rights.Count == 0 ? Right - Left : rights[^1] + (rights[^1] - (rights.Count > 1 ? rights[^2] : 0)));

            var cells = new List<(double Left, double Right, List<(LaidLine Line, double Y)> Lines, double Height)>();
            for (int i = 0; i < row.Cells.Count; i++)
            {
                double cellLeft = Left + (i == 0 ? row.Left : rights[i - 1]);
                double cellRight = Left + rights[i];
                double padLeft = Math.Min(row.PadLeft, (cellRight - cellLeft) / 4), padRight = Math.Min(row.PadRight, (cellRight - cellLeft) / 4);
                double y = padding;
                var lines = new List<(LaidLine, double)>();
                foreach (var p in row.Cells[i])
                {
                    y += p.Format.SpaceBefore;
                    foreach (var line in Layout(p, cellLeft + padLeft, cellRight - padRight))
                    {
                        lines.Add((line, y));
                        y += line.Height;
                    }
                    y += p.Format.SpaceAfter;
                }
                cells.Add((cellLeft, cellRight, lines, y + padding));
            }
            double height = cells.Max(c => c.Height);
            if (_y + height > Bottom && !_atTop) NewPage();
            foreach (var cell in cells)
            {
                foreach (var (line, y) in cell.Lines) Draw(line, _y + y);
                if (row.Borders) DrawRectangle(cell.Left, _y, cell.Right - cell.Left, height);
            }
            _y += height;
            _atTop = false;
        }

        // ------------------------------------------------------------------ fonts

        private StyleRef StyleFor(RtfCharFormat f, bool fallback)
        {
            if (_styles.TryGetValue((f, fallback), out var style)) return style;
            var font = rtf.Fonts.TryGetValue(f.Font, out var known) ? known : new RtfFont("Times New Roman", "roman", 0);
            var family = FamilyOf(font);
            var textStyle = new TextStyle { Font = family, Bold = f.Bold, Italic = f.Italic };
            double size = f.Script != 0 ? f.Size * 2 / 3 : f.Size;
            var color = f.Color > 0 && f.Color < rtf.Colors.Count && rtf.Colors[f.Color] is { } c ? c : PdfColor.Black;

            IntPtr handle = IntPtr.Zero;
            string name = "";
            bool embedded = false;
            if (!fallback && options.EmbedFonts && font.Name.Length > 0)
            {
                if (FontLocator.Find(font.Name, f.Bold, f.Italic) is { } path)
                {
                    try { (handle, name, embedded) = (owner.LoadFont(path), font.Name, true); }
                    catch (PdfException) { /* not loadable: use a standard font */ }
                }
                if (handle == IntPtr.Zero && !_warnedFonts)
                {
                    _warnedFonts = true;
                    rtf.Warnings.Add("Some fonts are not installed, so similar standard fonts were used.");
                }
            }
            if (handle == IntPtr.Zero && fallback && FindSystemFont(textStyle) is { } system)
                (handle, name, embedded) = (owner.LoadFont(system), Path.GetFileNameWithoutExtension(system), true);
            if (handle == IntPtr.Zero)
            {
                name = StandardFontName(textStyle);
                handle = owner.StandardFontHandle(name);
            }
            return _styles[(f, fallback)] = new StyleRef
            {
                Font = handle, FontName = name, FontSize = (float)size,
                Matrix = Affine.Identity, Lin = new Affine(1, 0, 0, -1, 0, 0),
                Fill = color, CoversEverything = embedded,
            };
        }

        private static StandardFont FamilyOf(RtfFont font)
        {
            string n = font.Name.ToLowerInvariant();
            if (font.Family == "modern" || n.Contains("courier") || n.Contains("mono") || n.Contains("consolas")) return StandardFont.Courier;
            if (n.Contains("sans") || n.Contains("arial") || n.Contains("helvetica") || n.Contains("calibri") || n.Contains("verdana") || n.Contains("segoe") || n.Contains("tahoma"))
                return StandardFont.Helvetica;
            if (font.Family == "roman" || n.Contains("times") || n.Contains("serif") || n.Contains("georgia") || n.Contains("cambria") || n.Contains("garamond") || n.Contains("book"))
                return StandardFont.Times;
            return font.Family == "swiss" ? StandardFont.Helvetica : StandardFont.Times;
        }

        /// <summary>A style that can draw the character: the requested one, or an installed font that has it.</summary>
        private StyleRef Drawable(RtfCharFormat f, char c)
        {
            var style = StyleFor(f, fallback: false);
            if (style.CoversEverything || c < 0x80 || IsWinAnsi(c)) return style;
            return StyleFor(f, fallback: true);
        }

        private static bool IsWinAnsi(char c)
        {
            try { WinAnsi.GetBytes(new[] { c }); return true; }
            catch (EncoderFallbackException) { return false; }
        }

        private (double Ascent, double Descent) Metrics(StyleRef s)
        {
            if (_metrics.TryGetValue(s, out var m)) return m;
            float ascent = 0, descent = 0;
            Native.FPDFFont_GetAscent(s.Font, s.FontSize, &ascent);
            Native.FPDFFont_GetDescent(s.Font, s.FontSize, &descent);
            if (ascent <= 0) ascent = 0.8f * s.FontSize;
            if (descent >= 0) descent = -0.2f * s.FontSize;
            return _metrics[s] = (ascent, -descent);
        }

        // ------------------------------------------------------------------ line layout

        private List<Piece> Pieces(RtfParagraph p, double maxWidth)
        {
            var pieces = new List<Piece>();
            foreach (var inline in p.Content)
            {
                if (inline is RtfPicture pic)
                {
                    double scale = Math.Min(1, Math.Min(maxWidth / pic.Width, (Bottom - Top) / pic.Height));
                    var style = StyleFor(p.EndFormat, false);
                    pieces.Add(new Piece(Kind.Picture, '\0', style, p.EndFormat, pic.Width * scale, 0, pic with { Width = pic.Width * scale, Height = pic.Height * scale }));
                    continue;
                }
                var text = (RtfText)inline;
                var f = text.Format;
                double rise = f.Script > 0 ? -0.33 * f.Size : f.Script < 0 ? 0.15 * f.Size : -f.Raise;
                foreach (char c in text.Text)
                {
                    var kind = c switch { '\t' => Kind.Tab, '\n' => Kind.Break, ' ' => Kind.Space, _ => Kind.Char };
                    var style = kind == Kind.Char ? Drawable(f, c) : StyleFor(f, false);
                    double width = kind is Kind.Char or Kind.Space ? style.Advance(c) : 0;
                    pieces.Add(new Piece(kind, c, style, f, width, rise));
                }
            }
            return pieces;
        }

        private List<LaidLine> Layout(RtfParagraph p, double left, double right)
        {
            var format = p.Format;
            double textLeft = left + format.LeftIndent, textRight = Math.Max(textLeft + 18, right - format.RightIndent);
            var pieces = Pieces(p, textRight - textLeft);
            var lines = new List<LaidLine>();
            var line = new LaidLine();
            lines.Add(line);
            double lineStart = textLeft + format.FirstIndent;
            double x = lineStart;
            double pendingSpace = 0;
            var pendingSpaces = new List<Piece>();

            void NewLine(bool hardBreak)
            {
                line.EndsWithBreak = hardBreak;
                line = new LaidLine();
                lines.Add(line);
                lineStart = x = textLeft;
                pendingSpace = 0;
                pendingSpaces.Clear();
            }

            double NextTab(double from)
            {
                var stops = format.Tabs.Select(t => left + t).ToList();
                if (format.FirstIndent < 0) stops.Add(textLeft); // a hanging indent works as a tab stop
                var next = stops.Where(s => s > from + 0.01).OrderBy(s => s).Cast<double?>().FirstOrDefault();
                if (next is { } n) return n;
                double tab = rtf.DefaultTab;
                return left + (Math.Floor((from - left) / tab + 1e-6) + 1) * tab;
            }

            int i = 0;
            while (i < pieces.Count)
            {
                var piece = pieces[i];
                switch (piece.Kind)
                {
                    case Kind.Space:
                        if (line.Pieces.Count > 0) { pendingSpace += piece.Width; pendingSpaces.Add(piece); }
                        i++;
                        continue;
                    case Kind.Break:
                        NewLine(hardBreak: true);
                        i++;
                        continue;
                    case Kind.Tab:
                        double stop = NextTab(x + pendingSpace);
                        if (stop > textRight + 0.01 && line.Pieces.Count > 0) NewLine(false);
                        else
                        {
                            line.Pieces.Add(new Placed(piece with { Width = stop - x }, x));
                            x = stop;
                            pendingSpace = 0;
                            pendingSpaces.Clear();
                        }
                        i++;
                        continue;
                }

                // A word: characters and pictures up to the next space, tab or break.
                int end = i;
                double width = 0;
                while (end < pieces.Count && pieces[end].Kind is Kind.Char or Kind.Picture) width += pieces[end++].Width;
                if (line.Pieces.Count > 0 && x + pendingSpace + width > textRight + 0.01) NewLine(false);
                // Spaces before the word are kept (for underlines and justification).
                foreach (var space in pendingSpaces)
                {
                    line.Pieces.Add(new Placed(space, x));
                    x += space.Width;
                }
                pendingSpace = 0;
                pendingSpaces.Clear();
                for (int k = i; k < end; k++)
                {
                    if (line.Pieces.Count > 0 && x + pieces[k].Width > textRight + 0.01 && width > textRight - lineStart)
                        NewLine(false); // a word longer than the line breaks between characters
                    line.Pieces.Add(new Placed(pieces[k], x));
                    x += pieces[k].Width;
                }
                i = end;
            }

            for (int n = 0; n < lines.Count; n++)
            {
                var l = lines[n];
                bool last = n == lines.Count - 1 || l.EndsWithBreak;
                Align(l, format.Align, n == 0 ? textLeft + format.FirstIndent : textLeft, textRight, last);
                Measure(l, p, format);
            }
            return lines;
        }

        /// <summary>Applies alignment to the part of a line after its last tab.</summary>
        private static void Align(LaidLine line, RtfAlign align, double start, double right, bool last)
        {
            var pieces = line.Pieces;
            while (pieces.Count > 0 && pieces[^1].Piece.Kind == Kind.Space) pieces.RemoveAt(pieces.Count - 1);
            if (pieces.Count == 0 || align == RtfAlign.Left) return;
            int afterTab = pieces.FindLastIndex(p => p.Piece.Kind == Kind.Tab) + 1;
            double end = pieces[^1].X + pieces[^1].Piece.Width;
            double extra = right - end;
            if (extra <= 0) return;
            if (align == RtfAlign.Justify)
            {
                if (last) return;
                int gaps = 0;
                for (int k = afterTab + 1; k < pieces.Count; k++)
                    if (pieces[k].Piece.Kind == Kind.Space && pieces[k - 1].Piece.Kind != Kind.Space) gaps++;
                if (gaps == 0) return;
                double each = extra / gaps, shift = 0;
                for (int k = afterTab; k < pieces.Count; k++)
                {
                    if (k > afterTab && pieces[k].Piece.Kind == Kind.Space && pieces[k - 1].Piece.Kind != Kind.Space) shift += each;
                    pieces[k].X += shift;
                }
                return;
            }
            if (afterTab > 0) return; // centred or right-aligned text with tabs keeps its tab positions
            double move = align == RtfAlign.Center ? extra / 2 : extra;
            foreach (var p in pieces) p.X += move;
        }

        private void Measure(LaidLine line, RtfParagraph p, RtfParaFormat format)
        {
            double ascent = 0, descent = 0, em = 0;
            foreach (var placed in line.Pieces)
            {
                var piece = placed.Piece;
                if (piece.Kind == Kind.Picture)
                {
                    ascent = Math.Max(ascent, piece.Picture!.Height);
                    continue;
                }
                var (a, d) = Metrics(piece.Style);
                ascent = Math.Max(ascent, a - piece.Rise);
                descent = Math.Max(descent, d + piece.Rise);
                em = Math.Max(em, piece.Format.Size);
            }
            if (line.Pieces.Count == 0 || em == 0 && ascent == 0)
            {
                var (a, d) = Metrics(StyleFor(p.EndFormat, false));
                (ascent, descent, em) = (Math.Max(ascent, a), Math.Max(descent, d), p.EndFormat.Size);
            }
            // Single spacing: the font's own height, at least 1.15 times the text size (as word processors do).
            double natural = Math.Max(ascent + descent, 1.15 * em);
            double baseline = ascent + (natural - ascent - descent) / 2;
            double height = format.LineSpacing switch
            {
                0 => natural,
                > 0 when format.LineMultiple => natural * format.LineSpacing / 240.0,
                > 0 => Math.Max(natural, format.LineSpacing / 20.0),
                _ => -format.LineSpacing / 20.0,
            };
            line.Height = height;
            line.Baseline = height - (natural - baseline); // extra spacing goes above the text
        }

        // ------------------------------------------------------------------ drawing

        private void Draw(LaidLine line, double top)
        {
            var page = _page!;
            double baseline = top + line.Baseline;
            var pieces = line.Pieces;

            // Highlighting goes under the text.
            foreach (var run in Runs(pieces, p => p.Piece.Format.Highlight > 0 && p.Piece.Kind is Kind.Char or Kind.Space or Kind.Tab, p => p.Piece.Format.Highlight))
            {
                var f = run[0].Piece.Format;
                if (f.Highlight >= rtf.Colors.Count || rtf.Colors[f.Highlight] is not { } color) continue;
                double x0 = run[0].X, x1 = run[^1].X + run[^1].Piece.Width;
                DrawFilledRectangle(x0, baseline - 0.9 * f.Size, x1 - x0, 1.15 * f.Size, color);
            }

            for (int k = 0; k < pieces.Count;)
            {
                var first = pieces[k];
                if (first.Piece.Kind == Kind.Picture)
                {
                    var pic = first.Piece.Picture!;
                    try { owner.DrawImage(page, pic.Data, first.X, baseline - pic.Height, pic.Width, pic.Height); }
                    catch (Exception)
                    {
                        const string message = "A picture could not be read and was left out.";
                        if (!rtf.Warnings.Contains(message)) rtf.Warnings.Add(message);
                    }
                    k++;
                    continue;
                }
                if (first.Piece.Kind != Kind.Char) { k++; continue; }
                int end = k + 1;
                double x = first.X + first.Piece.Width;
                while (end < pieces.Count && pieces[end].Piece.Kind == Kind.Char && ReferenceEquals(pieces[end].Piece.Style, first.Piece.Style)
                       && pieces[end].Piece.Rise == first.Piece.Rise && Math.Abs(pieces[end].X - x) < 0.01)
                {
                    x += pieces[end].Piece.Width;
                    end++;
                }
                var text = new string(pieces.Skip(k).Take(end - k).Select(p => p.Piece.Ch).ToArray());
                owner.DrawText(page, text, first.Piece.Style, first.X, baseline + first.Piece.Rise);
                k = end;
            }

            foreach (var run in Runs(pieces, p => p.Piece.Format.Underline && p.Piece.Kind is Kind.Char or Kind.Space, p => p.Piece.Format.Color))
                DrawLine(run, baseline + 0.12 * run[0].Piece.Format.Size, run[0].Piece.Format);
            foreach (var run in Runs(pieces, p => p.Piece.Format.Strike && p.Piece.Kind is Kind.Char or Kind.Space, p => p.Piece.Format.Color))
                DrawLine(run, baseline - 0.28 * run[0].Piece.Format.Size, run[0].Piece.Format);
        }

        /// <summary>Consecutive pieces that match, grouped by a key; runs never start or end with a space.</summary>
        private static IEnumerable<List<Placed>> Runs(List<Placed> pieces, Func<Placed, bool> match, Func<Placed, int> key)
        {
            var run = new List<Placed>();
            foreach (var p in pieces)
            {
                if (match(p) && (run.Count == 0 || key(run[0]) == key(p))) { run.Add(p); continue; }
                if (Trim(run) is { Count: > 0 } done) yield return done;
                run = match(p) ? [p] : [];
            }
            if (Trim(run) is { Count: > 0 } last) yield return last;

            static List<Placed> Trim(List<Placed> r) =>
                r.SkipWhile(p => p.Piece.Kind == Kind.Space).Reverse().SkipWhile(p => p.Piece.Kind == Kind.Space).Reverse().ToList();
        }

        private void DrawLine(List<Placed> run, double y, RtfCharFormat f)
        {
            var color = f.Color > 0 && f.Color < rtf.Colors.Count && rtf.Colors[f.Color] is { } c ? c : PdfColor.Black;
            double x0 = run[0].X, x1 = run[^1].X + run[^1].Piece.Width;
            var (ux0, uy) = _page!.ViewToUser.Apply(x0, y);
            var (ux1, _) = _page.ViewToUser.Apply(x1, y);
            var path = Native.FPDFPageObj_CreateNewPath((float)ux0, (float)uy);
            Native.FPDFPath_LineTo(path, (float)ux1, (float)uy);
            Native.FPDFPath_SetDrawMode(path, 0, 1);
            Native.FPDFPageObj_SetStrokeWidth(path, (float)Math.Max(0.5, 0.06 * f.Size));
            Native.FPDFPageObj_SetStrokeColor(path, color.R, color.G, color.B, 255);
            Native.FPDFPage_InsertObject(_page.Handle, path);
        }

        private void DrawRectangle(double x, double y, double w, double h)
        {
            var (ux, uy) = _page!.ViewToUser.Apply(x, y + h);
            var rect = Native.FPDFPageObj_CreateNewRect((float)ux, (float)uy, (float)w, (float)h);
            Native.FPDFPath_SetDrawMode(rect, 0, 1);
            Native.FPDFPageObj_SetStrokeWidth(rect, 0.5f);
            Native.FPDFPageObj_SetStrokeColor(rect, 0, 0, 0, 255);
            Native.FPDFPage_InsertObject(_page.Handle, rect);
        }

        private void DrawFilledRectangle(double x, double y, double w, double h, PdfColor color)
        {
            var (ux, uy) = _page!.ViewToUser.Apply(x, y + h);
            var rect = Native.FPDFPageObj_CreateNewRect((float)ux, (float)uy, (float)w, (float)h);
            Native.FPDFPath_SetDrawMode(rect, 1, 0);
            Native.FPDFPageObj_SetFillColor(rect, color.R, color.G, color.B, 255);
            Native.FPDFPage_InsertObject(_page.Handle, rect);
        }
    }
}
