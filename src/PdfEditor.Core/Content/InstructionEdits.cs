using System.Globalization;
using System.Text;
using PdfEditor.Core.Interop;

namespace PdfEditor.Core.Content;

/// <summary>
/// Changes to a page made in its own instructions: an item is taken out, given another place,
/// or drawn earlier or later than its neighbours, by rewriting only the instructions that draw
/// it. Everything else on the page stays byte for byte as it was, so nothing the editor does
/// not understand can be lost.
/// </summary>
internal static class InstructionEdits
{
    /// <summary>What to do with one item: with a transform (in the page's own coordinates) it is moved or turned; without, it is removed.</summary>
    public readonly record struct Change(int Item, Affine? Transform);

    /// <summary>
    /// What PDFium knows that the instructions alone do not say. Text starts where the text before
    /// it ended, which depends on the widths of the letters: PDFium has worked that out, so it is
    /// asked where each piece of text is (its object's matrix, in the page's coordinates). It also
    /// knows how much room each item takes (left, bottom, right, top).
    /// </summary>
    public sealed record Facts(Func<int, Affine?> Placed, Func<int, (double Left, double Bottom, double Right, double Top)?> Bounds);

    private readonly record struct Edit(int Start, int End, byte[] With);

    /// <summary>
    /// The page's instructions with the changes made, or null when one of them cannot be made
    /// this way (the caller then uses the older route).
    /// </summary>
    public static byte[]? Apply(PageInstructions page, IReadOnlyList<Change> changes, Facts facts)
    {
        var edits = new List<Edit>();
        foreach (var change in changes)
        {
            if (EditFor(page, change, facts) is not { } edit) return null;
            edits.Add(edit);
        }
        return Applied(page, edits);
    }

    /// <summary>
    /// The page's instructions with some items drawn just before, or just after, another item
    /// instead of where they are: a move down or up in what lies over what. The items are taken
    /// out where they were and drawn again at the new point, each in a box of its own in which
    /// every setting they were drawn with (space, clip, colours, line, font) is given afresh.
    /// Null when that cannot be done faithfully.
    /// </summary>
    /// <param name="moving">The items to move, in the order they are drawn.</param>
    /// <param name="passed">The item they are to end up just before (moving down) or just after (moving up).</param>
    public static byte[]? Relocate(PageInstructions page, IReadOnlyList<int> moving, int passed, bool up, Facts facts)
    {
        if (passed < 0 || passed >= page.Items.Count || moving.Contains(passed)) return null;
        var target = page.Items[passed];
        if (target.InTextBlock && target.Kind != ItemKind.Text) return null; // a shape drawn inside a block of text: not followed
        if (up && target.ClipsToo) return null;                              // what comes after it is clipped by it
        if (target.State.CannotBeRepeated) return null;

        // The items drawn again, each in a box of its own, for a point where the given settings hold;
        // null when those settings cannot be turned into the items' own.
        string? Boxed(DrawState theirs, Affine space, Clip clip)
        {
            var sb = new StringBuilder();
            foreach (int index in moving)
            {
                var item = page.Items[index];
                if (Alone(page, item, index, facts) is not { } drawn) return null;
                if (SettingsFor(item, theirs, space, clip, facts.Bounds(index)) is not { } settings) return null;
                sb.Append("q ").Append(settings).Append(drawn).Append(" Q ");
            }
            return sb.ToString();
        }

        var edits = new List<Edit>();
        foreach (int index in moving)
        {
            if (index < 0 || index >= page.Items.Count) return null;
            var item = page.Items[index];
            if (item.InTextBlock && item.Kind != ItemKind.Text) return null;
            if (item.State.CannotBeRepeated) return null;
            if (EditFor(page, new Change(index, null), facts) is not { } removal) return null;
            edits.Add(removal);
        }

        // Right next to the item passed, if the settings there allow it. Where they do not (it is drawn
        // under a clip the moving items would be cut by, or with see-through settings they never had),
        // step outwards: just before, or after, the saved-state section it is drawn in, where those
        // settings are not in force. That passes the rest of the section too, which belongs with it.
        string? group = Boxed(target.State, target.Space, target.Clip);
        if (group is null)
        {
            foreach (var section in target.Sections.Reverse())
            {
                if (section.InTextBlock || section.End < 0) continue;
                // The moving items must not be inside the section: they would be drawn inside what they are to pass.
                if (moving.Any(i => page.Items[i].Start >= section.Start && page.Items[i].End <= section.End)) continue;
                if (Boxed(section.State, section.Space, section.Clip) is not { } outside) continue;
                int at = up ? section.End : section.Start;
                edits.Add(new Edit(at, at, Encoding.Latin1.GetBytes(outside)));
                return Applied(page, edits);
            }
            return null;
        }
        if (target.Kind != ItemKind.Text)
        {
            int at = up ? target.End : target.Start;
            edits.Add(new Edit(at, at, Encoding.Latin1.GetBytes(group.ToString())));
        }
        else
        {
            // The new point is inside a block of text. The block is closed, the items drawn, and a
            // block opened again with the writing position put back where it was.
            if (target.Text is not { } state || state.Mode >= 4 || facts.Placed(passed) is not { } here) return null;
            try
            {
                var position = Position(target, state, here);
                var sb = new StringBuilder();
                if (up)
                {
                    // After the piece: the next one may start where it ended.
                    sb.Append("ET ").Append(group).Append("BT ").Append(Matrix(state.LineStart)).Append(" Tm ");
                    if (state.NextFollowsOn)
                    {
                        if (ShiftToNext(page, passed, state.LineStart, facts) is not { } shift) return null;
                        sb.Append(shift);
                    }
                    edits.Add(new Edit(target.End, target.End, Encoding.Latin1.GetBytes(sb.ToString())));
                }
                else
                {
                    // Before the piece: it is then shown from its own place, given outright.
                    sb.Append("ET ").Append(group).Append("BT ");
                    if (target.Operator == "\"")
                    {
                        if (SpacingOf(page, target, state) is not { } spacing) return null;
                        sb.Append(spacing);
                    }
                    sb.Append(Matrix(state.LineStart)).Append(" Tm ");
                    if (Along(position, state.LineStart, state) is not { } along) return null;
                    sb.Append(Shift(along, state));
                    sb.Append(Shown(page, target, state)).Append(target.Operator == "TJ" ? " TJ " : " Tj ");
                    edits.Add(new Edit(target.Start, target.End, Encoding.Latin1.GetBytes(sb.ToString())));
                }
            }
            catch (PdfException) { return null; }
        }
        return Applied(page, edits);
    }

    private static byte[]? Applied(PageInstructions page, List<Edit> edits)
    {
        // Applied from the end backwards, so earlier places stay where they are.
        var data = page.Data.ToList();
        int limit = int.MaxValue;
        foreach (var (start, end, with) in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End))
        {
            if (end > limit) return null; // two changes to the same instructions
            data.RemoveRange(start, end - start);
            // A space each side: instructions may be written with nothing between them ("Tf[" and the like).
            data.InsertRange(start, [(byte)' ', .. with, (byte)' ']);
            limit = start;
        }
        return data.ToArray();
    }

    /// <summary>What takes the place of an item's own instructions when it is moved, turned or removed.</summary>
    private static Edit? EditFor(PageInstructions page, Change change, Facts facts)
    {
        if (change.Item < 0 || change.Item >= page.Items.Count) return null;
        var item = page.Items[change.Item];
        if (item.Kind == ItemKind.Text)
            return TextChange(page, change, facts) is { } text ? new Edit(item.Start, item.End, Encoding.Latin1.GetBytes(text)) : null;
        if (item.InTextBlock) return null;

        if (change.Transform is not { } move)
        {
            // A path that also clips keeps clipping: only its painting goes ("n" paints nothing).
            return item.ClipsToo ? new Edit(item.OperatorAt, item.End, "n"u8.ToArray()) : new Edit(item.Start, item.End, " "u8.ToArray());
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
        boxed.AddRange(" Q"u8.ToArray());
        return new Edit(item.Start, item.End, boxed.ToArray());
    }

    // ------------------------------------------------------------------ text

    /// <summary>
    /// The text position where a piece is shown. PDFium's matrix for the piece is: stretch and
    /// rise, then the text position, then the space it is drawn in; undoing the first and the
    /// last leaves the text position itself.
    /// </summary>
    private static Affine Position(DrawnItem item, TextState state, Affine pdfiums) =>
        Affine.Concat(Affine.Concat(new Affine(state.Stretch, 0, 0, 1, 0, state.Rise).Invert(), pdfiums), item.Space.Invert());

    /// <summary>The text itself as written (a string, or an array of strings and shifts).</summary>
    private static string Shown(PageInstructions page, DrawnItem item, TextState state) =>
        Encoding.Latin1.GetString(page.Data, state.StringAt, item.OperatorAt - state.StringAt).TrimEnd();

    /// <summary>The double quote sets the spacing of words and letters as well as showing: those two settings, as instructions.</summary>
    private static string? SpacingOf(PageInstructions page, DrawnItem item, TextState state)
    {
        var numbers = Encoding.Latin1.GetString(page.Data, item.Start, state.StringAt - item.Start).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return numbers.Length < 2 ? null : $"{numbers[^2]} Tw {numbers[^1]} Tc ";
    }

    /// <summary>How far along its line a text position lies from another on the same line; null when it is not along the line.</summary>
    private static double? Along(Affine wanted, Affine from, TextState state)
    {
        var step = Affine.Concat(wanted, from.Invert());
        if (Math.Abs(step.A - 1) > 1e-3 || Math.Abs(step.D - 1) > 1e-3 || Math.Abs(step.B) > 1e-3 || Math.Abs(step.C) > 1e-3) return null;
        if (Math.Abs(step.F) > 0.02 * Math.Max(1, Math.Abs(state.FontSize))) return null; // text written downwards, say
        return step.E;
    }

    /// <summary>An instruction that moves the writing position along the line without showing anything.</summary>
    private static string Shift(double along, TextState state)
    {
        double thousandths = -along * 1000 / (state.FontSize * state.Stretch);
        return Math.Abs(thousandths) > 0.0005 ? $"[{Number(thousandths, 4)}] TJ " : "";
    }

    /// <summary>
    /// Moves the writing position from where it stands to where the piece after the given one
    /// starts, leaving out what the instructions in between will add themselves.
    /// </summary>
    private static string? ShiftToNext(PageInstructions page, int index, Affine from, Facts facts)
    {
        var item = page.Items[index];
        var state = item.Text!;
        if (index + 1 >= page.Items.Count || page.Items[index + 1].Text is not { } nextState || facts.Placed(index + 1) is not { } next) return null;
        var wanted = Position(page.Items[index + 1], nextState, next);
        return Along(wanted, from, state) is { } along ? Shift(along - state.ShiftBeforeNext, state) : null;
    }

    /// <summary>
    /// What takes the place of the instruction that shows a piece of text. The piece is shown at
    /// its new place (or not at all), and then the writing position is put back exactly as the
    /// original instruction would have left it, so the text after it does not shift.
    /// </summary>
    private static string? TextChange(PageInstructions page, Change change, Facts facts)
    {
        var item = page.Items[change.Item];
        if (item.Text is not { } state || state.Mode >= 4) return null;             // text that clips shapes what follows
        if (Math.Abs(state.FontSize) < 1e-6 || Math.Abs(state.Stretch) < 1e-6) return null;
        if (facts.Placed(change.Item) is not { } here) return null;
        var sb = new StringBuilder();
        try
        {
            if (item.Operator == "\"")
            {
                if (SpacingOf(page, item, state) is not { } spacing) return null;
                sb.Append(spacing);
            }

            Affine left; // where the writing position stands after what is written here, before any correction
            if (change.Transform is { } move)
            {
                var moved = Affine.Concat(Affine.Concat(Affine.Concat(new Affine(state.Stretch, 0, 0, 1, 0, state.Rise).Invert(), here), move), item.Space.Invert());
                sb.Append(Matrix(moved)).Append(" Tm ").Append(Shown(page, item, state)).Append(item.Operator == "TJ" ? " TJ " : " Tj ");
                // Back to the start of the line, as it was: a later move to a new line is measured from there.
                sb.Append(Matrix(state.LineStart)).Append(" Tm ");
                left = state.LineStart;
            }
            else
            {
                // The quotes also start a new line; that stays.
                if (item.Operator is "'" or "\"") sb.Append("T* ");
                left = Position(item, state, here); // nothing shown: the position has not moved on
            }

            if (state.NextFollowsOn)
            {
                // The next piece starts where this one ended. PDFium knows where that is.
                if (ShiftToNext(page, change.Item, left, facts) is not { } shift) return null;
                sb.Append(shift);
            }
        }
        catch (PdfException) { return null; } // a space with no way back (scaled to nothing)
        return sb.Length == 0 ? " " : sb.ToString();
    }

    // ------------------------------------------------------------------ drawing an item again somewhere else

    /// <summary>An item's own instructions, able to stand anywhere: a shape as written; text in a block of its own, at its place.</summary>
    private static string? Alone(PageInstructions page, DrawnItem item, int index, Facts facts)
    {
        if (item.Kind != ItemKind.Text) return Encoding.Latin1.GetString(page.Data, item.Start, item.End - item.Start);
        if (item.Text is not { } state || state.Mode >= 4 || facts.Placed(index) is not { } here) return null;
        try { return $"BT {Matrix(Position(item, state, here))} Tm {Shown(page, item, state)}{(item.Operator == "TJ" ? " TJ" : " Tj")} ET"; }
        catch (PdfException) { return null; }
    }

    /// <summary>
    /// The instructions that, given at a point where the given settings, space and clip are in
    /// force, put in force everything <paramref name="item"/> was drawn with. Only what differs is written.
    /// Null when the difference cannot be made up (a setting that cannot be taken back).
    /// </summary>
    private static string? SettingsFor(DrawnItem item, DrawState theirs, Affine space, Clip clip, (double Left, double Bottom, double Right, double Top)? bounds)
    {
        var sb = new StringBuilder();
        DrawState mine = item.State;
        try
        {
            // The space: from the one in force at the new point to the one the item was drawn in.
            var current = space;
            void Into(Affine space)
            {
                var step = Affine.Concat(space, current.Invert());
                if (!IsIdentity(step)) sb.Append(Matrix(step)).Append(" cm ");
                current = space;
            }

            // The clip can only be narrowed. Where the new point is clipped by something the item was
            // not under, the item must fit inside that clip whole; then all its own clips are given.
            int shared = 0;
            while (shared < mine.Clips.Count && shared < theirs.Clips.Count && mine.Clips[shared] == theirs.Clips[shared]) shared++;
            if (shared < theirs.Clips.Count)
            {
                if (bounds is not { } b || !clip.LeavesWhole(b.Left, b.Bottom, b.Right, b.Top)) return null;
                shared = 0;
            }
            foreach (var step in mine.Clips.Skip(shared))
            {
                Into(step.Space);
                sb.Append(step.Path).Append(' ');
            }
            Into(item.Space);

            // Settings made through a named set (see-through, blending, masks) add up and cannot be
            // taken back one by one: those in force at the new point must be ones the item had too.
            int same = 0;
            while (same < mine.Settings.Count && same < theirs.Settings.Count && mine.Settings[same] == theirs.Settings[same]) same++;
            if (same < theirs.Settings.Count) return null;
            foreach (string setting in mine.Settings.Skip(same)) sb.Append(setting).Append(' ');

            void Give(string? wanted, string? inForce, string standard)
            {
                if (wanted == inForce) return;
                sb.Append(wanted ?? standard).Append(' ');
            }
            Give(Join(mine.FillSpace, mine.FillColour), Join(theirs.FillSpace, theirs.FillColour), "0 g");
            Give(Join(mine.StrokeSpace, mine.StrokeColour), Join(theirs.StrokeSpace, theirs.StrokeColour), "0 G");
            Give(mine.LineWidth, theirs.LineWidth, "1 w");
            Give(mine.Cap, theirs.Cap, "0 J");
            Give(mine.Join, theirs.Join, "0 j");
            Give(mine.Mitre, theirs.Mitre, "10 M");
            Give(mine.Dash, theirs.Dash, "[] 0 d");
            Give(mine.Intent, theirs.Intent, "/RelativeColorimetric ri");
            Give(mine.Flatness, theirs.Flatness, "0 i");
            if (mine.Font is not null && mine.Font != theirs.Font) sb.Append(mine.Font).Append(' ');
            Give(mine.LetterSpace, theirs.LetterSpace, "0 Tc");
            Give(mine.WordSpace, theirs.WordSpace, "0 Tw");
            Give(mine.Stretch, theirs.Stretch, "100 Tz");
            Give(mine.Leading, theirs.Leading, "0 TL");
            Give(mine.Mode, theirs.Mode, "0 Tr");
            Give(mine.Rise, theirs.Rise, "0 Ts");
        }
        catch (PdfException) { return null; }
        return sb.ToString();

        static string? Join(string? space, string? colour) => space is null && colour is null ? null : $"{space} {colour}".Trim();
    }

    private static bool IsIdentity(Affine m) =>
        Math.Abs(m.A - 1) < 1e-9 && Math.Abs(m.D - 1) < 1e-9 && Math.Abs(m.B) < 1e-9 && Math.Abs(m.C) < 1e-9 && Math.Abs(m.E) < 1e-9 && Math.Abs(m.F) < 1e-9;

    private static string Matrix(Affine m) => $"{Number(m.A)} {Number(m.B)} {Number(m.C)} {Number(m.D)} {Number(m.E)} {Number(m.F)}";

    private static string Number(double value, int places = 6) => Math.Round(value, places).ToString("0." + new string('#', places), CultureInfo.InvariantCulture);
}
