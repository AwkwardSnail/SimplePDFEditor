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
    public static byte[]? Apply(PageInstructions page, IReadOnlyList<Change> changes)
    {
        var output = new List<(int Start, int End, byte[] With)>();
        foreach (var change in changes)
        {
            if (change.Item < 0 || change.Item >= page.Items.Count) return null;
            var item = page.Items[change.Item];
            if (item.Kind == ItemKind.Text) return null; // text shares its position with the text around it: not yet done this way

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
            boxed.AddRange(Encoding.ASCII.GetBytes($"q {N(local.A)} {N(local.B)} {N(local.C)} {N(local.D)} {N(local.E)} {N(local.F)} cm "));
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
            data.InsertRange(start, with);
            limit = start;
        }
        return data.ToArray();
    }

    private static string N(double value) => Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);
}
