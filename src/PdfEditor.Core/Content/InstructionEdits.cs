using System.Globalization;
using System.Text;
using PdfEditor.Core.Interop;

namespace PdfEditor.Core.Content;

/// <summary>
/// Changes to a page made in its own instructions: an item is taken out, or given another
/// place, by rewriting only the instructions that draw it. Everything else on the page stays
/// byte for byte as it was, so nothing the editor does not understand can be lost.
/// </summary>
internal static class InstructionEdits
{
    /// <summary>What to do with one item: with a transform (in the page's own coordinates) it is moved or turned; without, it is removed.</summary>
    public readonly record struct Change(int Item, Affine? Transform);

    /// <summary>
    /// The page's instructions with the changes made, or null when one of them cannot be made
    /// this way (the caller then uses the older route).
    /// </summary>
    /// <param name="placed">
    /// Where PDFium has a piece of text (its object's matrix, in the page's coordinates). Text
    /// starts where the text before it ended, which the instructions alone do not say: that needs
    /// the widths of the letters. PDFium has worked it out, so it is asked.
    /// </param>
    public static byte[]? Apply(PageInstructions page, IReadOnlyList<Change> changes, Func<int, Affine?> placed)
    {
        var output = new List<(int Start, int End, byte[] With)>();
        foreach (var change in changes)
        {
            if (change.Item < 0 || change.Item >= page.Items.Count) return null;
            var item = page.Items[change.Item];
            if (item.Kind == ItemKind.Text)
            {
                if (TextChange(page, change, placed) is not { } text) return null;
                output.Add((item.Start, item.End, Encoding.Latin1.GetBytes(text)));
                continue;
            }

            if (change.Transform is not { } move)
            {
                // A path that also clips keeps clipping: only its painting goes ("n" paints nothing).
                output.Add(item.ClipsToo ? (item.OperatorAt, item.End, "n"u8.ToArray()) : (item.Start, item.End, " "u8.ToArray()));
                continue;
            }
            if (item.ClipsToo) return null; // boxed in, it would stop clipping what follows

            // The move is given in the page's coordinates; the item is drawn in the space in force at
            // that point. Seen from inside that space, the same move is: out to the page, move, back in.
            Affine local;
            try { local = Affine.Concat(Affine.Concat(item.Space, move), item.Space.Invert()); }
            catch (PdfException) { return null; }
            var boxed = new List<byte>();
            boxed.AddRange(Encoding.ASCII.GetBytes($"q {Matrix(local)} cm "));
            boxed.AddRange(page.Data.AsSpan(item.Start, item.End - item.Start).ToArray());
            boxed.AddRange(" Q "u8.ToArray());
            output.Add((item.Start, item.End, boxed.ToArray()));
        }

        // Applied from the end backwards, so earlier places stay where they are.
        var data = page.Data.ToList();
        int limit = int.MaxValue;
        foreach (var (start, end, with) in output.OrderByDescending(o => o.Start))
        {
            if (end > limit) return null; // two changes to the same instructions
            data.RemoveRange(start, end - start);
            // A space each side: instructions may be written with nothing between them ("Tf[" and the like).
            data.InsertRange(start, [(byte)' ', .. with, (byte)' ']);
            limit = start;
        }
        return data.ToArray();
    }

    /// <summary>
    /// What takes the place of the instruction that shows a piece of text. The piece is shown at
    /// its new place (or not at all), and then the writing position is put back exactly as the
    /// original instruction would have left it, so the text after it does not shift.
    /// </summary>
    private static string? TextChange(PageInstructions page, Change change, Func<int, Affine?> placed)
    {
        var item = page.Items[change.Item];
        if (item.Text is not { } state || state.Mode >= 4) return null;             // text that clips shapes what follows
        if (Math.Abs(state.FontSize) < 1e-6 || Math.Abs(state.Stretch) < 1e-6) return null;
        if (placed(change.Item) is not { } here) return null;
        string all = Encoding.Latin1.GetString(page.Data, item.Start, item.OperatorAt - item.Start);
        string shown = Encoding.Latin1.GetString(page.Data, state.StringAt, item.OperatorAt - state.StringAt).TrimEnd();
        var sb = new StringBuilder();

        try
        {
            // PDFium's matrix for the text is: stretch and rise, then the text position, then the space.
            // Undoing the first and last leaves the text position itself.
            var inside = new Affine(state.Stretch, 0, 0, 1, 0, state.Rise).Invert();
            var outOfSpace = item.Space.Invert();
            Affine Position(Affine pdfiums) => Affine.Concat(Affine.Concat(inside, pdfiums), outOfSpace);

            // The double quote sets the spacing of words and letters as well as showing: keep that.
            if (item.Operator == "\"")
            {
                var numbers = all[..(state.StringAt - item.Start)].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (numbers.Length < 2) return null;
                sb.Append(numbers[^2]).Append(" Tw ").Append(numbers[^1]).Append(" Tc ");
            }

            Affine left; // where the writing position stands after what is written here, before any correction
            if (change.Transform is { } move)
            {
                var moved = Affine.Concat(Affine.Concat(Affine.Concat(inside, here), move), outOfSpace);
                sb.Append(Matrix(moved)).Append(" Tm ").Append(shown).Append(item.Operator == "TJ" ? " TJ " : " Tj ");
                // Back to the start of the line, as it was: a later move to a new line is measured from there.
                sb.Append(Matrix(state.LineStart)).Append(" Tm ");
                left = state.LineStart;
            }
            else
            {
                // The quotes also start a new line; that stays.
                if (item.Operator is "'" or "\"") sb.Append("T* ");
                left = Position(here); // nothing shown: the position has not moved on
            }

            if (state.NextFollowsOn)
            {
                // The next piece starts where this one ended. PDFium knows where that is.
                if (placed(change.Item + 1) is not { } next || page.Items[change.Item + 1].Text is not { } nextState) return null;
                var nextInside = new Affine(nextState.Stretch, 0, 0, 1, 0, nextState.Rise).Invert();
                var wanted = Affine.Concat(Affine.Concat(nextInside, next), outOfSpace);
                var step = Affine.Concat(wanted, left.Invert()); // from where it stands to where it should: a shift along the line
                if (Math.Abs(step.A - 1) > 1e-3 || Math.Abs(step.D - 1) > 1e-3 || Math.Abs(step.B) > 1e-3 || Math.Abs(step.C) > 1e-3) return null;
                if (Math.Abs(step.F) > 0.02 * Math.Max(1, Math.Abs(state.FontSize))) return null; // not along the line (text written downwards, say)
                double along = step.E - state.ShiftBeforeNext; // what the instructions in between will add themselves
                double thousandths = -along * 1000 / (state.FontSize * state.Stretch);
                if (Math.Abs(thousandths) > 0.0005) sb.Append('[').Append(Number(thousandths, 4)).Append("] TJ ");
            }
        }
        catch (PdfException) { return null; } // a space with no way back (scaled to nothing)
        return sb.Length == 0 ? " " : sb.ToString();
    }

    private static string Matrix(Affine m) => $"{Number(m.A)} {Number(m.B)} {Number(m.C)} {Number(m.D)} {Number(m.E)} {Number(m.F)}";

    private static string Number(double value, int places = 6) => Math.Round(value, places).ToString("0." + new string('#', places), CultureInfo.InvariantCulture);
}
