using PdfEditor.Core.Interop;
using PdfEditor.Core.Reflow;

namespace PdfEditor.Core;

// The look of whole pieces of text (font, size, bold, italic, colour), and turning items.
public sealed unsafe partial class PdfDocument
{
    /// <summary>
    /// Changes the look of whole pieces of text: their colour, size, bold or italic, or the family
    /// of font they are set in. Each piece stays where it starts. A change of font, weight or
    /// slant sets the text in a standard font (the document's own fonts rarely hold the letters
    /// for another weight); characters the standard fonts lack are drawn in an installed font.
    /// Text that is not upright only takes a new colour. Returns how many pieces changed.
    /// </summary>
    public int FormatText(int pageIndex, IEnumerable<int> objectIndices, TextFormat format)
    {
        if (format.Size is { } wanted && (wanted <= 0 || wanted > 2000)) throw new ArgumentOutOfRangeException(nameof(format), "The size must be between 0 and 2000 points.");
        var indices = objectIndices.Distinct().ToList();
        bool restyle = format.Font is not null || format.Bold is not null || format.Italic is not null || format.Size is not null;
        int changed = 0;

        // Pieces that only get a new colour are done first, in the page's own instructions: the colour
        // is set just for the piece and put back after it. Nothing else on the page is touched.
        var coloured = new HashSet<int>();
        if (format.Color is { A: 255 } plain && EditInstructionsDirectly)
        {
            var only = new List<int>();
            using (var look = OpenPage(pageIndex))
            {
                var seen = restyle ? PageAnalysis.Analyze(look) : null;
                var styles = new Dictionary<StyleRef, StyleRef>();
                foreach (int index in indices)
                {
                    var obj = GetObject(look, index);
                    if (Native.FPDFPageObj_GetType(obj) != Native.PAGEOBJ_TEXT) continue;
                    List<Glyph>? glyphs = null;
                    seen?.GlyphsByObject.TryGetValue(obj, out glyphs);
                    StyleRef? source = glyphs is { Count: > 0 } && glyphs.Any(g => !char.IsWhiteSpace(g.Ch)) ? glyphs[0].Style : null;
                    StyleRef? target = null;
                    if (source is not null && !styles.TryGetValue(source, out target)) styles[source] = target = Reformat(look, source, format with { Color = null });
                    if (source is null || ReferenceEquals(target, source)) only.Add(index);
                }
            }
            if (only.Count > 0 && TryRewriteInstructions(pageIndex, (read, _) => Content.InstructionEdits.Recolour(read, only, plain)))
            {
                coloured.UnionWith(only);
                changed += only.Count;
            }
        }

        using var page = OpenPage(pageIndex);
        var objects = indices.Where(i => !coloured.Contains(i)).Select(i => GetObject(page, i)).Where(o => Native.FPDFPageObj_GetType(o) == Native.PAGEOBJ_TEXT).ToList();
        if (objects.Count == 0) return changed;
        var analysis = restyle ? PageAnalysis.Analyze(page) : null;
        var coverage = new Coverage(this, page);
        var derived = new Dictionary<StyleRef, StyleRef>();
        var systemFonts = new Dictionary<StyleRef, StyleRef?>();
        // Restyled pieces are drawn anew and the old ones taken away afterwards, in the page's instructions
        // (see PageHandle.RemoveLater), unless something here has to go through PDFium anyway.
        bool later = EditInstructionsDirectly, throughPdfium = false;
        int before = changed;

        foreach (var obj in objects)
        {
            List<Glyph>? glyphs = null;
            analysis?.GlyphsByObject.TryGetValue(obj, out glyphs);
            StyleRef? source = glyphs is { Count: > 0 } && glyphs.Any(g => !char.IsWhiteSpace(g.Ch)) ? glyphs[0].Style : null;
            StyleRef? target = null;
            if (source is not null && !derived.TryGetValue(source, out target)) derived[source] = target = Reformat(page, source, format with { Color = null });

            if (source is null || ReferenceEquals(target, source))
            {
                // Nothing about the letters changes (or they cannot be rebuilt): a new colour at most.
                if (format.Color is not { } colour) continue;
                Native.FPDFPageObj_SetFillColor(obj, colour.R, colour.G, colour.B, colour.A);
                throughPdfium = true;
                changed++;
                continue;
            }

            var look = format.Color is { } c ? Clone(target!, fill: c) : target!;
            var text = new System.Text.StringBuilder();
            foreach (var g in glyphs!.OrderBy(g => g.Index))
            {
                if (g.SpaceBefore && text.Length > 0 && text[^1] != ' ' && g.Ch != ' ') text.Append(' ');
                text.Append(g.Ch);
            }
            string all = text.ToString().TrimEnd();
            coverage.Check(all.Select(ch => (look, ch)));
            StyleRef FontFor(char ch)
            {
                if (coverage.Has(look, ch)) return look;
                if (!systemFonts.TryGetValue(look, out var system)) systemFonts[look] = system = SystemLookAlike(look);
                return system ?? look;
            }

            // In the old piece's place in the drawing order, starting where it started.
            int index = -1, count = Native.FPDFPage_CountObjects(page.Handle);
            for (int i = 0; i < count && index < 0; i++) if (Native.FPDFPage_GetObject(page.Handle, i) == obj) index = i;
            var first = glyphs!.OrderBy(g => g.Index).First();
            double x = first.X;
            int start = 0, made = 0;
            while (start < all.Length)
            {
                var use = FontFor(all[start]);
                int end = start;
                while (end < all.Length && ReferenceEquals(FontFor(all[end]), use)) end++;
                string piece = all[start..end];
                page.NewObjects.Add(DrawText(page, piece, use, x, first.Y, later ? null : index + made));
                foreach (char ch in piece) x += use.Advance(ch);
                made++;
                start = end;
            }
            if (later) page.RemoveLater(obj);
            else
            {
                Native.FPDFPage_RemoveObject(page.Handle, obj);
                Native.FPDFPageObj_Destroy(obj);
                throughPdfium = true;
            }
            changed++;
        }
        if (changed > before)
        {
            page.Dirty = true;
            if (throughPdfium) page.ChangedExisting = true;
            else page.NewTakeThePlaceOfOld = true;
            _charsByFont = null;
        }
        return changed;
    }

    /// <summary>
    /// Turns items a quarter turn, clockwise or anticlockwise as seen, about the middle of the
    /// area they cover together, so a block of lines turns as one.
    /// </summary>
    public void RotateObjects(int pageIndex, IEnumerable<int> objectIndices, bool clockwise)
    {
        var indices = objectIndices.Distinct().ToList();
        if (indices.Count == 0) return;
        Affine turnInPage;
        using (var measure = OpenPage(pageIndex)) turnInPage = QuarterTurn(measure, indices.Select(i => GetObject(measure, i)).ToList(), clockwise);
        if (TryEditInstructions(pageIndex, indices, _ => turnInPage)) return;

        using var page = OpenPage(pageIndex);
        var objects = indices.Select(i => GetObject(page, i)).ToList();
        foreach (var obj in objects) TransformObject(obj, turnInPage);
        page.Dirty = page.ChangedExisting = true;
    }

    /// <summary>A quarter turn about the middle of the area the objects cover together, in the page's own coordinates.</summary>
    private static Affine QuarterTurn(PageHandle page, List<IntPtr> objects, bool clockwise)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var obj in objects)
        {
            var b = ObjectBounds(page, obj);
            (left, top, right, bottom) = (Math.Min(left, b.X), Math.Min(top, b.Y), Math.Max(right, b.Right), Math.Max(bottom, b.Bottom));
        }
        double cx = (left + right) / 2, cy = (top + bottom) / 2;
        // As seen (y runs down the page): clockwise takes (x, y) about the middle to (-y, x).
        var turn = clockwise ? new Affine(0, 1, -1, 0, cx + cy, cy - cx) : new Affine(0, -1, 1, 0, cx - cy, cy + cx);
        return Affine.Concat(Affine.Concat(page.UserToView, turn), page.ViewToUser);
    }
}
