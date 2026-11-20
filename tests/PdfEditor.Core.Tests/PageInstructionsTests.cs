using PdfEditor.Core.Content;
using PdfEditor.Core.Interop;
using PdfEditor.Core.Preflight;
using Xunit;

namespace PdfEditor.Core.Tests;

/// <summary>
/// The editor reads a page's drawing instructions itself, so that an edit can change only the
/// instructions of the item concerned. That depends on its list of items being the same as
/// PDFium's list of page objects: same number, same kinds, same order.
/// </summary>
public unsafe class PageInstructionsTests
{
    private static List<int> AsPdfiumSeesIt(PdfDocument doc, int pageIndex)
    {
        var kinds = new List<int>();
        using var page = doc.OpenPage(pageIndex);
        int count = Native.FPDFPage_CountObjects(page.Handle);
        for (int i = 0; i < count; i++) kinds.Add(Native.FPDFPageObj_GetType(Native.FPDFPage_GetObject(page.Handle, i)));
        return kinds;
    }

    private static void AssertSameItems(byte[] pdf, string what)
    {
        byte[] bytes = DistinctFontNames.Apply(pdf); // the bytes the editor really opens
        var file = PdfFile.Open(bytes);
        using var doc = PdfDocument.Open(bytes);
        Assert.True(doc.PageCount > 0);
        for (int p = 0; p < doc.PageCount; p++)
        {
            var read = PageInstructions.Read(file, p, out string? problem);
            Assert.True(read is not null, $"{what}, page {p + 1}: {problem}");
            Assert.Equal(AsPdfiumSeesIt(doc, p), read!.Items.Select(i => (int)i.Kind).ToList());
        }
    }

    [Theory]
    [InlineData("word-style.pdf")]
    [InlineData("two-columns.pdf")]
    public void Items_read_from_a_page_match_PDFiums_objects(string fixture)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        AssertSameItems(File.ReadAllBytes(path), fixture);
    }

    [Fact]
    public void They_match_for_gradients_Type3_text_and_files_without_a_table()
    {
        // These are written by hand without a cross-reference table, so they also try the reader's repair.
        AssertSameItems(PdfDocumentTests.PdfWithGradients(), "gradients");
        AssertSameItems(PdfDocumentTests.PdfWithType3Letters(), "Type 3 letters");
    }

    [Fact]
    public void They_match_after_the_editor_has_saved_a_file_too()
    {
        using var doc = PdfDocument.Open(Path.Combine(AppContext.BaseDirectory, "Fixtures", "word-style.pdf"));
        doc.AddShape(0, ShapeKind.Ellipse, [new(60, 60), new(200, 140)], new ShapeStyle { Fill = new PdfColor(255, 235, 60) });
        doc.AddText(0, 72, 300, "Added text");
        doc.AddWatermark(0, "DRAFT");
        doc.MoveObject(0, doc.GetTextRuns(0)[3].ObjectIndex, 5, 0); // makes PDFium write the page again
        AssertSameItems(doc.ToBytes(), "a saved file"); // packed objects, a cross-reference stream
    }

    [Fact]
    public void What_does_not_draw_is_not_counted()
    {
        // An empty piece of text, a path that is only used to clip, a lone move, a name that is no picture.
        const string content = "BT /F1 12 Tf 20 20 Td () Tj [() -100] TJ (Real) Tj ET 0 0 50 50 re W n 10 10 m 20 20 m S /Missing Do 1 0 0 rg 5 5 20 20 re f";
        byte[] pdf = System.Text.Encoding.ASCII.GetBytes(
            "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
            "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]/Contents 4 0 R/Resources<</Font<</F1<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>>>>>>>endobj\n" +
            $"4 0 obj<</Length {content.Length}>>stream\n{content}\nendstream endobj\ntrailer<</Root 1 0 R>>\n%%EOF");
        AssertSameItems(pdf, "odd instructions");
        var read = PageInstructions.Read(PdfFile.Open(pdf), 0, out _)!;
        Assert.Equal([ItemKind.Text, ItemKind.Path], read.Items.Select(i => i.Kind).ToArray());
        // Each item knows which instructions are its own.
        Assert.Equal("(Real) Tj", System.Text.Encoding.ASCII.GetString(read.Data, read.Items[0].Start, read.Items[0].End - read.Items[0].Start));
        Assert.Equal("5 5 20 20 re f", System.Text.Encoding.ASCII.GetString(read.Data, read.Items[1].Start, read.Items[1].End - read.Items[1].Start));
    }
    // ------------------------------------------------------------------ edits made in the instructions

    private const string Font = "/Font<</F1<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>>>";
    /// <summary>A small grey square in the top-right corner, drawn first: the item the tests move or remove.</summary>
    private const string Handle = "0.5 g 280 280 10 10 re f ";

    private static byte[] Page(string content, string resources = "", params string[] extra)
    {
        content = Handle + content;
        var sb = new System.Text.StringBuilder("%PDF-1.7\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n");
        sb.Append($"3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 300]/Contents 4 0 R/Resources<<{resources}>>>>endobj\n");
        sb.Append($"4 0 obj<</Length {content.Length}>>stream\n{content}\nendstream endobj\n");
        for (int i = 0; i < extra.Length; i++) sb.Append($"{5 + i} 0 obj{extra[i]}endobj\n");
        sb.Append("trailer<</Root 1 0 R>>\n%%EOF");
        return System.Text.Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string Stream(string dict, string data) => $"<<{dict}/Length {data.Length}>>stream\n{data}\nendstream ";

    /// <summary>How many pixels differ between two pictures of a page, leaving out the handle's corner.</summary>
    private static int Differing(RenderedPage a, RenderedPage b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        int n = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                if (x > a.Width * 0.88 && y < a.Height * 0.12) continue;
                int i = y * a.Stride + x * 4;
                if (a.Pixels[i] != b.Pixels[i] || a.Pixels[i + 1] != b.Pixels[i + 1] || a.Pixels[i + 2] != b.Pixels[i + 2]) n++;
            }
        return n;
    }

    public static IEnumerable<object[]> ThingsPdfiumCannotWrite() =>
    [
        ["a tiled pattern", Page("/Pattern cs /P1 scn 50 50 200 200 re f", "/Pattern<</P1 5 0 R>>",
            Stream("/Type/Pattern/PatternType 1/PaintType 1/TilingType 1/BBox[0 0 20 20]/XStep 20/YStep 20/Resources<<>>", "1 0 0 rg 0 0 10 10 re f 0 0 1 rg 10 10 10 10 re f")), "/Pattern cs"],
        ["a gradient used as a fill", Page("/Pattern cs /P1 scn 50 50 200 200 re f",
            "/Pattern<</P1<</PatternType 2/Shading<</ShadingType 2/ColorSpace/DeviceRGB/Coords[50 0 250 0]/Function<</FunctionType 2/Domain[0 1]/C0[1 0 0]/C1[0 0 1]/N 1>>>>>>>>"), "/P1 scn"],
        ["a soft mask", Page("q /GS1 gs 0 0 1 rg 50 50 200 200 re f Q", "/ExtGState<</GS1<</SMask<</Type/Mask/S/Luminosity/G 5 0 R>>>>>>",
            Stream("/Type/XObject/Subtype/Form/BBox[0 0 300 300]/Group<</S/Transparency/CS/DeviceGray>>/Resources<</Shading<</Sh<</ShadingType 2/ColorSpace/DeviceGray/Coords[50 0 250 0]/Function<</FunctionType 2/Domain[0 1]/C0[0]/C1[1]/N 1>>>>>>>>", "/Sh sh")), "/GS1 gs"],
        ["an inline picture", Page("q 150 0 0 150 50 50 cm BI /W 2 /H 2 /CS /RGB /BPC 8 /F /AHx ID ff000000ff000000ffffff00> EI Q"), "BI /W 2"],
        ["text used to clip", Page("q BT /F1 70 Tf 7 Tr 20 110 Td (CLIP) Tj ET 1 0 0 rg 0 100 300 40 re f 0 0 1 rg 0 140 300 40 re f Q", Font), "7 Tr"],
        ["CMYK and grey colours", Page("0 1 1 0 k 50 50 80 80 re f 0.5 g 150 50 80 80 re f 1 0 0 0 K 8 w 50 200 m 250 200 l S"), "0 1 1 0 k"],
        ["letter and word spacing", Page("BT /F1 20 Tf 4 Tc 12 Tw 30 150 Td (Spaced out words) Tj ET", Font), "4 Tc 12 Tw"],
        ["a spot ink", Page("/CS1 cs 1 scn 50 50 200 200 re f", "/ColorSpace<</CS1[/Separation/PANTONE#20Orange/DeviceCMYK<</FunctionType 2/Domain[0 1]/C0[0 0 0 0]/C1[0 0.6 1 0]/N 1>>]>>"), "/CS1 cs 1 scn"],
    ];

    [Theory]
    [MemberData(nameof(ThingsPdfiumCannotWrite))]
    public void Moving_one_item_leaves_the_rest_of_the_page_exactly_as_it_was(string what, byte[] pdf, string mustStillBeThere)
    {
        using var doc = PdfDocument.Open(pdf);
        var before = doc.Render(0, 2, false);
        string text = doc.GetPageText(0);
        int direct = PdfDocument.DirectEdits;
        var handle = doc.GetDrawings(0).Where(d => d.Bounds.X > 270 && d.Bounds.Y < 30).OrderBy(d => d.Bounds.Width).First();
        doc.MoveObjects(0, handle.ObjectIndices, -6, 4);
        Assert.Equal(direct + 1, PdfDocument.DirectEdits); // it went the direct way

        byte[] saved = doc.ToBytes();
        using var again = PdfDocument.Open(saved);
        Assert.Equal(0, Differing(before, again.Render(0, 2, false)));   // not one pixel of the rest changed
        Assert.Equal(text, again.GetPageText(0));
        Assert.Contains(mustStillBeThere, PdfDocumentTests.Unpacked(saved)); // and it is still written the way it was: {what}
        _ = what;
        // The handle itself is where it was moved to: 6 to the left, 4 down.
        var moved = again.GetDrawings(0).Where(d => d.Bounds.Width < 12 && d.Bounds.Y < 40).OrderBy(d => d.Bounds.Y).First();
        Assert.Equal(274, moved.Bounds.X, 0.1);
        Assert.Equal(14, moved.Bounds.Y, 0.1);

        // Removing it works the same way.
        again.RemoveObjects(0, moved.ObjectIndices);
        using var third = PdfDocument.Open(again.ToBytes());
        Assert.Equal(0, Differing(before, third.Render(0, 2, false)));
        Assert.DoesNotContain(third.GetDrawings(0), d => d.Bounds.Width < 12 && d.Bounds.Y < 40);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void An_item_drawn_in_a_scaled_and_turned_space_moves_by_what_was_asked(int pageRotation)
    {
        // The box is drawn inside a space scaled by 2 and turned a little, then a nested one shifted again.
        byte[] pdf = Page("q 2 0 0 2 20 20 cm q 0.966 0.259 -0.259 0.966 10 0 cm 1 0 0 rg 20 20 30 20 re f Q Q 0 0 1 rg 200 40 30 30 re f");
        using var doc = PdfDocument.Open(pdf);
        doc.SetPageRotation(0, pageRotation);
        var red = doc.GetDrawings(0).OrderByDescending(d => d.Bounds.Width * d.Bounds.Height).First();
        var blue = doc.GetDrawings(0).Single(d => Math.Abs(d.Bounds.Width - 30) < 0.5 && Math.Abs(d.Bounds.Height - 30) < 0.5);
        int direct = PdfDocument.DirectEdits;
        doc.MoveObjects(0, red.ObjectIndices, 25, -10);
        Assert.Equal(direct + 1, PdfDocument.DirectEdits);
        using var again = PdfDocument.Open(doc.ToBytes());
        var after = again.GetDrawings(0).OrderByDescending(d => d.Bounds.Width * d.Bounds.Height).First();
        Assert.Equal(red.Bounds.X + 25, after.Bounds.X, 0.05);
        Assert.Equal(red.Bounds.Y - 10, after.Bounds.Y, 0.05);
        Assert.Equal(red.Bounds.Width, after.Bounds.Width, 0.05);
        Assert.Equal(blue.Bounds, again.GetDrawings(0).Single(d => Math.Abs(d.Bounds.Width - 30) < 0.5 && Math.Abs(d.Bounds.Height - 30) < 0.5).Bounds);
    }

    [Fact]
    public void Removing_a_shape_that_also_clips_keeps_the_clip()
    {
        // The red square is painted and used as a clip: the blue bar after it shows only inside the square.
        byte[] pdf = Page("q 1 0 0 rg 100 100 100 100 re W f 0 0 1 rg 50 140 200 20 re f Q");
        using var doc = PdfDocument.Open(pdf);
        doc.RemoveObjects(0, [1]); // the handle is object 0, the red square 1, the bar 2 (the square and bar touch, so they select as one drawing)
        using var again = PdfDocument.Open(doc.ToBytes());
        var page = again.Render(0, 1, false);
        (int R, int G, int B) At(int x, int y) { int i = y * page.Stride + x * 4; return (page.Pixels[i + 2], page.Pixels[i + 1], page.Pixels[i]); }
        Assert.Equal((255, 255, 255), At(150, 120));  // the red is gone
        Assert.Equal((0, 0, 255), At(150, 150));      // the bar still shows inside the square
        Assert.Equal((255, 255, 255), At(70, 150));   // and is still cut off outside it
    }

    [Fact]
    public void Pictures_groups_and_text_are_moved_and_removed_directly()
    {
        byte[] pdf = Page("q 60 0 0 60 40 40 cm /Im Do Q q 1 0 0 1 150 150 cm /Fm Do Q BT /F1 20 Tf 40 250 Td (Some text) Tj ET",
            "/XObject<</Im 5 0 R/Fm 6 0 R>>" + Font,
            Stream("/Type/XObject/Subtype/Image/Width 2/Height 2/ColorSpace/DeviceRGB/BitsPerComponent 8/Filter/ASCIIHexDecode", "ff000000ff000000ffffff00>"),
            Stream("/Type/XObject/Subtype/Form/BBox[0 0 50 50]/Resources<<>>", "0 0 1 rg 0 0 50 50 re f"));
        using var doc = PdfDocument.Open(pdf);
        var image = doc.GetImages(0).Single();
        int direct = PdfDocument.DirectEdits, rewritten = PdfDocument.RewrittenEdits;
        doc.MoveObject(0, image.ObjectIndex, 30, 20);
        Assert.Equal(image.Bounds.X + 30, doc.GetImages(0).Single().Bounds.X, 0.05);
        Assert.Equal(image.Bounds.Y + 20, doc.GetImages(0).Single().Bounds.Y, 0.05);
        Assert.Equal((direct + 1, rewritten), (PdfDocument.DirectEdits, PdfDocument.RewrittenEdits));

        var run = doc.GetTextRuns(0).Single();
        doc.MoveObject(0, run.ObjectIndex, 5, 5);
        Assert.Equal(run.Bounds.X + 5, doc.GetTextRuns(0).Single().Bounds.X, 0.05);
        Assert.Equal((direct + 2, rewritten), (PdfDocument.DirectEdits, PdfDocument.RewrittenEdits)); // text too

        doc.RemoveObject(0, doc.GetImages(0).Single().ObjectIndex);
        Assert.Empty(doc.GetImages(0));
        Assert.Contains("Some text", doc.GetPageText(0));
    }

    [Fact]
    public void A_clipped_item_moved_out_of_its_clip_takes_its_clip_along()
    {
        // The red square is drawn under a clip that cuts it (it shows from 60 to 90 only, not 50 to 90),
        // beside a blue square under the same clip. Moved 100 to the right, the red one would be cut away
        // entirely if its clip stayed behind.
        byte[] pdf = Page("q 60 40 60 60 re W n 1 0 0 rg 50 50 40 40 re f 0 0 1 rg 95 50 20 20 re f Q 0 1 0 rg 20 200 20 20 re f");
        using var doc = PdfDocument.Open(pdf);
        var before = doc.Render(0, 1, false);
        Assert.Equal((255, 255, 255), Colour(before, 55, 70)); // cut off there
        Assert.Equal((255, 0, 0), Colour(before, 70, 70));
        int direct = PdfDocument.DirectEdits;
        doc.MoveObjects(0, [1], 100, 0);
        Assert.Equal(direct + 1, PdfDocument.DirectEdits);
        using var again = PdfDocument.Open(doc.ToBytes());
        var page = again.Render(0, 1, false);
        Assert.Equal((255, 0, 0), Colour(page, 170, 70));      // seen at its new place
        Assert.Equal((255, 255, 255), Colour(page, 155, 70));  // and still cut where its clip cut it: the clip moved with it
        Assert.Equal((255, 255, 255), Colour(page, 70, 70));   // gone from where it was
        Assert.Equal((0, 0, 255), Colour(page, 100, 60));      // the blue square under the same clip is untouched
        Assert.Equal((0, 255, 0), Colour(page, 30, 210));
        Assert.Equal(AsPdfiumSeesIt(doc, 0).Count, AsPdfiumSeesIt(again, 0).Count);
    }

    [Fact]
    public void Text_moved_out_of_its_clip_stays_whole_and_the_text_after_it_stays_put()
    {
        // A column of text drawn under a clip the width of the column; one piece is moved out to the right.
        byte[] pdf = TextPage("q 30 100 120 150 re W n BT /F1 12 Tf 14 TL 35 220 Td (one ) Tj (two) Tj T* (three) Tj ET Q");
        using var doc = PdfDocument.Open(pdf);
        var places = Pieces(doc);
        static int Ink(RenderedPage page, PdfRect area)
        {
            int n = 0;
            for (int y = (int)area.Y; y < (int)area.Bottom; y++)
                for (int x = (int)area.X; x < (int)area.Right; x++)
                    if (page.Pixels[y * page.Stride + x * 4] < 235) n++;
            return n;
        }
        int inkBefore = Ink(doc.Render(0, 1, false), places[0].Bounds);
        Assert.True(inkBefore > 20);
        int direct = PdfDocument.DirectEdits;
        doc.MoveObject(0, doc.GetTextRuns(0)[0].ObjectIndex, 150, 0);
        Assert.Equal(direct + 1, PdfDocument.DirectEdits);
        using var again = PdfDocument.Open(doc.ToBytes());
        var now = Pieces(again);
        Assert.Equal(3, now.Count);
        AssertSamePlace((places[0].Text, new PdfRect(places[0].Bounds.X + 150, places[0].Bounds.Y, places[0].Bounds.Width, places[0].Bounds.Height)), now.Single(p => p.Text.Trim() == "one"), "the moved piece");
        AssertSamePlace(places[1], now.Single(p => p.Text.Trim() == "two"), "the piece after it");
        AssertSamePlace(places[2], now.Single(p => p.Text.Trim() == "three"), "the next line");
        // It really shows there (it was not cut away by the column's clip).
        int inkAfter = Ink(again.Render(0, 1, false), now.Single(p => p.Text.Trim() == "one").Bounds);
        Assert.InRange(inkAfter, inkBefore * 0.9, inkBefore * 1.1);
    }
    // ------------------------------------------------------------------ text, where pieces share a writing position

    public static IEnumerable<object[]> TextBlocks() =>
    [
        ["pieces one after another", "BT /F1 12 Tf 30 200 Td (first ) Tj (second ) Tj (third ) Tj (fourth) Tj ET"],
        ["with spacing between letters and words", "BT /F1 12 Tf 2 Tc 5 Tw 30 200 Td (one two ) Tj (three four ) Tj (five) Tj ET"],
        ["kerned pieces", "BT /F1 12 Tf 30 200 Td [(ab) -300 (cd)] TJ [(ef) 250 (gh)] TJ (ij) Tj ET"],
        ["a shift between pieces", "BT /F1 12 Tf 30 200 Td (ab) Tj [-2000] TJ (cd) Tj [500] TJ (ef) Tj ET"],
        ["lines made with Td", "BT /F1 12 Tf 30 200 Td (line one) Tj 0 -16 Td (line two) Tj (more) Tj 0 -16 Td (line three) Tj ET"],
        ["lines made with leading", "BT /F1 12 Tf 15 TL 30 200 Td (line one) Tj T* (line two) Tj (more) Tj (line three) ' (tail) Tj (line four) ' ET"],
        ["the double quote", "BT /F1 12 Tf 15 TL 30 200 Td (one) Tj 3 1 (two words) \" (after) Tj T* (three words here) Tj ET"],
        ["stretched", "BT /F1 12 Tf 150 Tz 30 200 Td (wide ) Tj (text ) Tj (here) Tj ET"],
        ["raised", "BT /F1 12 Tf 30 200 Td (base ) Tj 5 Ts (up ) Tj 0 Ts (down ) Tj (end) Tj ET"],
        ["a change of size in the line", "BT /F1 12 Tf 30 200 Td (small ) Tj /F1 20 Tf (big ) Tj /F1 12 Tf (small) Tj ET"],
        ["in a scaled and turned space", "q 1.5 0 0 1.5 10 20 cm q 0.966 0.259 -0.259 0.966 20 0 cm BT /F1 10 Tf 20 60 Td (one ) Tj (two ) Tj (three) Tj ET Q Q"],
        ["set with a turned text matrix", "BT /F1 12 Tf 0 1 -1 0 150 60 Tm (up ) Tj (the ) Tj (page) Tj 0 -16 Td (next) Tj ET"],
        ["two blocks", "BT /F1 12 Tf 30 200 Td (a ) Tj (b) Tj ET BT /F1 12 Tf 30 150 Td (c ) Tj (d) Tj ET"],
    ];

    private static byte[] TextPage(string content) => Page(content, "/Font<</F1<</Type/Font/Subtype/Type1/BaseFont/Courier>>>>");

    /// <summary>Each piece of text on the page: what it says and exactly where it is.</summary>
    private static List<(string Text, PdfRect Bounds)> Pieces(PdfDocument doc) =>
        doc.GetTextRuns(0).Select(r => (r.Text, r.Bounds)).ToList();

    private static void AssertSamePlace((string Text, PdfRect Bounds) expected, (string Text, PdfRect Bounds) actual, string because)
    {
        Assert.Equal(expected.Text.Trim(), actual.Text.Trim()); // PDFium adds a space or a line end after a piece depending on what follows it
        Assert.True(Math.Abs(expected.Bounds.X - actual.Bounds.X) < 0.02 && Math.Abs(expected.Bounds.Y - actual.Bounds.Y) < 0.02 &&
                    Math.Abs(expected.Bounds.Width - actual.Bounds.Width) < 0.02 && Math.Abs(expected.Bounds.Height - actual.Bounds.Height) < 0.02,
            $"{because}: '{expected.Text}' was at {expected.Bounds}, is at {actual.Bounds}");
    }

    [Theory]
    [MemberData(nameof(TextBlocks))]
    public void Removing_any_piece_of_text_leaves_the_others_exactly_where_they_were(string what, string content)
    {
        byte[] pdf = TextPage(content);
        int count;
        using (var first = PdfDocument.Open(pdf)) count = first.GetTextRuns(0).Count;
        Assert.True(count >= 3, what);
        for (int victim = 0; victim < count; victim++)
        {
            using var doc = PdfDocument.Open(pdf);
            var before = Pieces(doc);
            int direct = PdfDocument.DirectEdits;
            doc.RemoveObject(0, doc.GetTextRuns(0)[victim].ObjectIndex);
            Assert.True(PdfDocument.DirectEdits == direct + 1, $"{what}: removing piece {victim} did not go the direct way");
            using var saved = PdfDocument.Open(doc.ToBytes());
            var after = Pieces(saved);
            Assert.Equal(count - 1, after.Count);
            before.RemoveAt(victim);
            for (int i = 0; i < after.Count; i++) AssertSamePlace(before[i], after[i], $"{what}, after removing piece {victim}");
        }
    }

    [Theory]
    [MemberData(nameof(TextBlocks))]
    public void Moving_any_piece_of_text_moves_only_that_piece(string what, string content)
    {
        byte[] pdf = TextPage(content);
        int count;
        using (var first = PdfDocument.Open(pdf)) count = first.GetTextRuns(0).Count;
        for (int moved = 0; moved < count; moved++)
        {
            using var doc = PdfDocument.Open(pdf);
            var before = Pieces(doc);
            int direct = PdfDocument.DirectEdits;
            doc.MoveObject(0, doc.GetTextRuns(0)[moved].ObjectIndex, 7, -5);
            Assert.True(PdfDocument.DirectEdits == direct + 1, $"{what}: moving piece {moved} did not go the direct way");
            using var saved = PdfDocument.Open(doc.ToBytes());
            var after = Pieces(saved);
            Assert.Equal(count, after.Count);
            for (int i = 0; i < count; i++)
            {
                var expected = i == moved
                    ? (before[i].Text, new PdfRect(before[i].Bounds.X + 7, before[i].Bounds.Y - 5, before[i].Bounds.Width, before[i].Bounds.Height))
                    : before[i];
                AssertSamePlace(expected, after[i], $"{what}, after moving piece {moved}");
            }
        }
    }

    [Fact]
    public void A_block_of_lines_moves_and_turns_together_and_several_pieces_can_go_at_once()
    {
        byte[] pdf = TextPage("BT /F1 12 Tf 15 TL 60 220 Td (line one ) Tj (more) Tj T* (line two) Tj (line three) ' ET BT /F1 12 Tf 60 100 Td (apart) Tj ET");
        using var doc = PdfDocument.Open(pdf);
        var before = Pieces(doc);
        var block = doc.GetTextRuns(0).Take(4).Select(r => r.ObjectIndex).ToList();
        int direct = PdfDocument.DirectEdits;

        doc.MoveObjects(0, block, 20, 30);
        var moved = Pieces(doc);
        for (int i = 0; i < 4; i++) AssertSamePlace((before[i].Text, new PdfRect(before[i].Bounds.X + 20, before[i].Bounds.Y + 30, before[i].Bounds.Width, before[i].Bounds.Height)), moved[i], "the block moved");
        AssertSamePlace(before[4], moved[4], "the piece apart");

        // A quarter turn about the block's middle: its width and height change places, its middle stays.
        PdfRect Around(List<(string Text, PdfRect Bounds)> p) => new(p.Take(4).Min(x => x.Bounds.X), p.Take(4).Min(x => x.Bounds.Y),
            p.Take(4).Max(x => x.Bounds.Right) - p.Take(4).Min(x => x.Bounds.X), p.Take(4).Max(x => x.Bounds.Bottom) - p.Take(4).Min(x => x.Bounds.Y));
        var was = Around(moved);
        doc.RotateObjects(0, doc.GetTextRuns(0).Take(4).Select(r => r.ObjectIndex), clockwise: true);
        var turned = Pieces(doc);
        var now = Around(turned);
        Assert.Equal(was.Width, now.Height, 0.05);
        Assert.Equal(was.Height, now.Width, 0.05);
        Assert.Equal(was.X + was.Width / 2, now.X + now.Width / 2, 0.05);
        AssertSamePlace(before[4], turned[4], "the piece apart");

        // Two neighbouring pieces removed in one go: the rest stay.
        doc.RemoveObjects(0, doc.GetTextRuns(0).Take(2).Select(r => r.ObjectIndex));
        var left = Pieces(doc);
        Assert.Equal(3, left.Count);
        AssertSamePlace(turned[2], left[0], "after removing two");
        AssertSamePlace(turned[3], left[1], "after removing two");
        Assert.Equal(direct + 3, PdfDocument.DirectEdits);
        using var saved = PdfDocument.Open(doc.ToBytes());
        Assert.Equal(["line two", "line three", "apart"], saved.GetTextRuns(0).Select(r => r.Text.Trim()).ToArray());
    }
    // ------------------------------------------------------------------ layers: drawing an item earlier or later

    private static (int R, int G, int B) Colour(RenderedPage page, double x, double yFromBottom, double scale = 1)
    {
        int i = (int)((300 - yFromBottom) * scale) * page.Stride + (int)(x * scale) * 4;
        return (page.Pixels[i + 2], page.Pixels[i + 1], page.Pixels[i]);
    }

    /// <summary>How many pixels differ outside a box given in the page's own coordinates (y up), ignoring the handle's corner.</summary>
    private static int DifferingOutside(RenderedPage a, RenderedPage b, double left, double bottom, double right, double top)
    {
        int n = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                if (x > a.Width * 0.88 && y < a.Height * 0.12) continue;
                if (x >= left - 2 && x <= right + 2 && 300 - y >= bottom - 2 && 300 - y <= top + 2) continue;
                int i = y * a.Stride + x * 4;
                if (a.Pixels[i] != b.Pixels[i] || a.Pixels[i + 1] != b.Pixels[i + 1] || a.Pixels[i + 2] != b.Pixels[i + 2]) n++;
            }
        return n;
    }

    private const string RedPicture = "/Type/XObject/Subtype/Image/Width 1/Height 1/ColorSpace/DeviceRGB/BitsPerComponent 8/Filter/ASCIIHexDecode";

    // Each: two items that overlap in the square from (100,100) to (140,140) or wider; the one to move (PDFium's
    // index; the handle is 0), which way, and the colour the middle of the overlap must have afterwards.
    public static IEnumerable<object[]> Overlaps() =>
    [
        ["plain boxes, the upper one down", Page("1 0 0 rg 50 50 100 100 re f 0 0 1 rg 100 100 100 100 re f"), 2, false, 255, 0, 0],
        ["plain boxes, the lower one up", Page("1 0 0 rg 50 50 100 100 re f 0 0 1 rg 100 100 100 100 re f"), 1, true, 255, 0, 0],
        ["in scaled spaces with line settings, down", Page("q 2 0 0 2 0 0 cm 1 0 0 rg 0 0 1 RG 3 w [4 2] 0 d 25 25 50 50 re B Q q 0.5 0 0 0.5 100 100 cm 0 1 0 rg 0 0 200 200 re f Q"), 2, false, 255, 0, 0],
        ["in scaled spaces with line settings, up", Page("q 2 0 0 2 0 0 cm 1 0 0 rg 0 0 1 RG 3 w [4 2] 0 d 25 25 50 50 re B Q q 0.5 0 0 0.5 100 100 cm 0 1 0 rg 0 0 200 200 re f Q"), 1, true, 255, 0, 0],
        ["a colour set outside the item's own box", Page("1 0 0 rg q 50 50 100 100 re f Q 0 0 1 rg q 100 100 100 100 re f Q"), 2, false, 255, 0, 0],
        ["a clipped item moved up keeps its clip", Page("q 60 60 80 80 re W n 1 0 0 rg 50 50 100 100 re f Q 0 0 1 rg 100 100 100 100 re f"), 1, true, 255, 0, 0],
        ["an item moved down past a clipped one", Page("q 60 60 80 80 re W n 1 0 0 rg 50 50 100 100 re f Q 0 0 1 rg 100 100 100 100 re f"), 2, false, 255, 0, 0],
        ["a see-through item moved down", Page("1 0 0 rg 50 50 100 100 re f q /GS1 gs 0 0 1 rg 100 100 100 100 re f Q", "/ExtGState<</GS1<</ca 0.5>>>>"), 2, false, 255, 0, 0],
        ["a tiled pattern moved down", Page("0 0 1 rg 100 100 100 100 re f /Pattern cs /P1 scn 50 50 100 100 re f", "/Pattern<</P1 5 0 R>>",
            Stream("/Type/Pattern/PatternType 1/PaintType 1/TilingType 1/BBox[0 0 20 20]/XStep 20/YStep 20/Resources<<>>", "1 0 0 rg 0 0 20 20 re f")), 2, false, 0, 0, 255],
        ["a picture moved down", Page("0 0 1 rg 100 100 100 100 re f q 100 0 0 100 50 50 cm /Im Do Q", "/XObject<</Im 5 0 R>>", Stream(RedPicture, "ff0000>")), 2, false, 0, 0, 255],
        ["a picture moved up", Page("q 100 0 0 100 50 50 cm /Im Do Q 0 0 1 rg 100 100 100 100 re f", "/XObject<</Im 5 0 R>>", Stream(RedPicture, "ff0000>")), 1, true, 255, 0, 0],
        ["an inline picture moved down", Page("0 0 1 rg 100 100 100 100 re f q 100 0 0 100 50 50 cm BI /W 1 /H 1 /CS /RGB /BPC 8 /F /AHx ID ff0000> EI Q"), 2, false, 0, 0, 255],
        ["a group moved down", Page("0 0 1 rg 100 100 100 100 re f q 1 0 0 1 50 50 cm /Fm Do Q", "/XObject<</Fm 5 0 R>>", Stream("/Type/XObject/Subtype/Form/BBox[0 0 100 100]/Resources<<>>", "1 0 0 rg 0 0 100 100 re f")), 2, false, 0, 0, 255],
        ["a gradient moved down", Page("0 0 1 rg 100 100 100 100 re f q 50 50 100 100 re W n /Sh sh Q",
            "/Shading<</Sh<</ShadingType 2/ColorSpace/DeviceRGB/Coords[50 0 150 0]/Function<</FunctionType 2/Domain[0 1]/C0[1 0 0]/C1[1 0 0]/N 1>>>>>>"), 2, false, 0, 0, 255],
    ];

    [Theory]
    [MemberData(nameof(Overlaps))]
    public void An_item_moved_a_layer_changes_only_what_lies_over_what(string what, byte[] pdf, int item, bool up, int r, int g, int b)
    {
        using var doc = PdfDocument.Open(pdf);
        var before = doc.Render(0, 1, false);
        var kinds = AsPdfiumSeesIt(doc, 0);
        int direct = PdfDocument.DirectEdits;
        Assert.True(doc.MoveLayer(0, [item], up), what);
        Assert.True(PdfDocument.DirectEdits == direct + 1, $"{what}: did not go the direct way");

        using var saved = PdfDocument.Open(doc.ToBytes());
        var after = saved.Render(0, 1, false);
        Assert.Equal((r, g, b), Colour(after, 120, 120));                              // the other one is on top where they overlap
        Assert.Equal(0, DifferingOutside(before, after, 94, 94, 156, 156));            // and nothing else on the page changed (a thick outline reaches a little past the square)
        // The same items, in the new order: the moved one now next to the one it passed.
        var expected = kinds.ToList();
        int other = up ? item + 1 : item - 1;
        (expected[item], expected[other]) = (expected[other], expected[item]);
        Assert.Equal(expected, AsPdfiumSeesIt(saved, 0));

        // Moved back, the page is as it was to begin with.
        Assert.True(saved.MoveLayer(0, [other], !up), what);
        using var back = PdfDocument.Open(saved.ToBytes());
        Assert.Equal(0, Differing(before, back.Render(0, 1, false)));
        Assert.Equal(kinds, AsPdfiumSeesIt(back, 0));
    }

    [Fact]
    public void A_box_moved_down_through_a_block_of_text_leaves_every_piece_in_place()
    {
        // Three pieces of text sharing a writing position, and a green box drawn over all of them.
        byte[] pdf = TextPage("BT /F1 20 Tf 22 TL 2 Tc 40 200 Td (first ) Tj (second) Tj T* (third) Tj ET 0 1 0 rg 30 170 240 55 re f");
        using var doc = PdfDocument.Open(pdf);
        var places = Pieces(doc);
        int Ink(PdfDocument d, int piece)
        {
            var page = d.Render(0, 1, false);
            var bounds = d.GetTextRuns(0)[piece].Bounds;
            int n = 0;
            for (int y = (int)bounds.Y; y < (int)bounds.Bottom; y++)
                for (int x = (int)bounds.X; x < (int)bounds.Right; x++)
                {
                    int i = y * page.Stride + x * 4;
                    if (page.Pixels[i] < 180 && page.Pixels[i + 1] < 180 && page.Pixels[i + 2] < 180) n++; // the text is mid grey (the colour in force), on green or white
                }
            return n;
        }
        Assert.Equal((0, 0, 0), (Ink(doc, 0), Ink(doc, 1), Ink(doc, 2))); // all hidden under the box
        int direct = PdfDocument.DirectEdits;

        // One layer down: under the last piece only.
        Assert.True(doc.MoveLayer(0, [4], up: false));
        Assert.Equal([1, 1, 2, 1], AsPdfiumSeesIt(doc, 0).Skip(1).ToList());
        Assert.True(Ink(doc, 2) > 50 && Ink(doc, 0) == 0 && Ink(doc, 1) == 0, $"only the third piece should show: ink {Ink(doc, 0)}, {Ink(doc, 1)}, {Ink(doc, 2)}");
        // Again: under the second too. And again: under all three.
        Assert.True(doc.MoveLayer(0, [3], up: false));
        Assert.True(Ink(doc, 2) > 50 && Ink(doc, 1) > 50 && Ink(doc, 0) == 0, "the second and third show");
        Assert.True(doc.MoveLayer(0, [2], up: false));
        Assert.True(Ink(doc, 0) > 50 && Ink(doc, 1) > 50 && Ink(doc, 2) > 50, "all three show");
        Assert.False(doc.MoveLayer(0, [1], up: false)); // nothing under it now but the handle, which it does not touch
        Assert.Equal(direct + 3, PdfDocument.DirectEdits);

        using var saved = PdfDocument.Open(doc.ToBytes());
        var now = Pieces(saved);
        Assert.Equal(3, now.Count);
        for (int i = 0; i < 3; i++) AssertSamePlace(places[i], now[i], "after the box went under the text");
    }

    [Fact]
    public void Text_is_moved_a_layer_too_with_its_look_and_place_kept()
    {
        // Red, spaced, stretched text over a blue box; the first piece is moved under the box.
        byte[] pdf = TextPage("0 0 1 rg 40 140 220 40 re f BT /F1 24 Tf 2 Tc 120 Tz 1 0 0 rg 50 150 Td (Over) Tj ( it) Tj ET");
        using var doc = PdfDocument.Open(pdf);
        var places = Pieces(doc);
        var before = doc.Render(0, 1, false);
        int Red(RenderedPage page, PdfRect area)
        {
            int n = 0;
            for (int y = (int)area.Y; y < (int)area.Bottom; y++)
                for (int x = (int)area.X; x < (int)area.Right; x++)
                {
                    int i = y * page.Stride + x * 4;
                    if (page.Pixels[i + 2] > 130 && page.Pixels[i + 1] < 100 && page.Pixels[i] < 140) n++; // thin red strokes, blended a little with the blue beneath
                }
            return n;
        }
        Assert.True(Red(before, places[0].Bounds) > 20, $"red pixels to begin with: {Red(before, places[0].Bounds)}");
        int direct = PdfDocument.DirectEdits;
        Assert.True(doc.MoveLayer(0, [2], up: false));
        Assert.Equal(direct + 1, PdfDocument.DirectEdits);
        using var saved = PdfDocument.Open(doc.ToBytes());
        var under = saved.Render(0, 1, false);
        Assert.Equal(0, Red(under, places[0].Bounds));                 // hidden by the box now
        Assert.True(Red(under, places[1].Bounds) > 10);               // the piece after it still shows, where it was
        var now = Pieces(saved);
        AssertSamePlace(places[0], now.Single(p => p.Text.Trim() == "Over"), "the moved piece");
        AssertSamePlace(places[1], now.Single(p => p.Text.Trim() == "it"), "the piece after it");

        // And up again: the page is as it was.
        Assert.True(saved.MoveLayer(0, [1], up: true));
        using var back = PdfDocument.Open(saved.ToBytes());
        Assert.Equal(0, Differing(before, back.Render(0, 1, false)));
    }
    // ------------------------------------------------------------------ the filling and line of shapes

    [Fact]
    public void A_shapes_filling_and_line_are_changed_without_touching_the_rest()
    {
        // A CMYK square (which PDFium would turn into RGB), then: a stroked box, a filled box drawn
        // by the even-odd rule with a hole in it, and a filled-only circle-ish shape.
        byte[] pdf = Page("0 1 1 0 k 20 20 40 40 re f " +
                          "0 0 1 RG 3 w 60 200 60 40 re S " +
                          "0 0.6 0 rg 150 200 80 60 re 170 215 40 30 re f* " +
                          "0.8 0.2 0.2 rg 60 100 m 120 100 l 90 150 l h f");
        using var doc = PdfDocument.Open(pdf);
        int direct = PdfDocument.DirectEdits;
        var yellow = new PdfColor(255, 235, 60);

        Assert.Equal(1, doc.SetShapeFill(0, [2], yellow));              // the stroked box gets a filling
        Assert.Equal(1, doc.SetShapeFill(0, [3], yellow));              // the box with a hole changes colour, keeping its hole
        Assert.Equal(1, doc.SetShapeFill(0, [4], null));                // the triangle becomes see-through, keeping an outline
        Assert.Equal(1, doc.SetShapeLine(0, [2], new PdfColor(200, 30, 30), 6));
        Assert.Equal(direct + 4, PdfDocument.DirectEdits);

        byte[] saved = doc.ToBytes();
        using var again = PdfDocument.Open(saved);
        var page = again.Render(0, 1, false);
        Assert.Equal((255, 235, 60), Colour(page, 90, 220));            // the first box is filled
        Assert.Equal((200, 30, 30), Colour(page, 60, 220));             // and its line is red and thick now
        Assert.Equal((255, 235, 60), Colour(page, 160, 205));           // the second box is yellow
        Assert.Equal((255, 255, 255), Colour(page, 190, 230));          // with its hole still a hole
        Assert.Equal((255, 255, 255), Colour(page, 90, 115));           // the triangle is see-through
        var edge = Colour(page, 90, 100);                               // with a thin outline in the colour it had (blended at a point wide)
        Assert.True(edge.R > edge.G + 50 && edge.G == edge.B, $"the outline is {edge}");
        Assert.Contains("0 1 1 0 k", PdfDocumentTests.Unpacked(saved));  // and the CMYK square is still CMYK

        var style = again.GetShapeStyle(0, 2)!;
        Assert.Equal((new PdfColor(200, 30, 30), 6.0, yellow), (style.Stroke, style.Width, style.Fill!.Value));
        Assert.Equal(5, AsPdfiumSeesIt(again, 0).Count);
    }
    // ------------------------------------------------------------------ rewriting a paragraph

    [Fact]
    public void Rewriting_a_paragraph_leaves_the_rest_of_the_page_as_it_was_written()
    {
        // Two paragraphs with a heading between them, beside a CMYK square and a tiled pattern
        // (both of which PDFium would turn into something else if it wrote the page again).
        byte[] pdf = Page(
            "0 1 1 0 k 230 230 40 40 re f /Pattern cs /P1 scn 230 170 40 40 re f 0 g " +
            "BT /F1 10 Tf 12 TL 20 260 Td (The first paragraph has two lines) Tj T* (of plain words in it.) Tj ET " +
            "BT /F1 14 Tf 20 215 Td (A heading) Tj ET " +
            "BT /F1 10 Tf 12 TL 20 190 Td (The second paragraph follows the) Tj T* (heading and also has two lines.) Tj ET",
            "/Font<</F1<</Type/Font/Subtype/Type1/BaseFont/Courier>>>>/Pattern<</P1 5 0 R>>",
            Stream("/Type/Pattern/PatternType 1/PaintType 1/TilingType 1/BBox[0 0 10 10]/XStep 10/YStep 10/Resources<<>>", "1 0 0 rg 0 0 5 5 re f"));
        using var doc = PdfDocument.Open(pdf);
        var before = doc.GetParagraphs(0);
        Assert.Equal(3, before.Count);
        var picture = doc.Render(0, 1, false);
        int direct = PdfDocument.DirectEdits, rewritten = PdfDocument.RewrittenEdits;

        doc.ReplaceParagraphText(0, 0, "The first paragraph now says something else, a little longer than before, over three lines.");
        Assert.Equal((direct + 1, rewritten), (PdfDocument.DirectEdits, PdfDocument.RewrittenEdits));

        byte[] saved = doc.ToBytes();
        using var again = PdfDocument.Open(saved);
        var after = again.GetParagraphs(0);
        // The same three paragraphs in the same order: the rewritten one is still first, not last.
        Assert.Equal(3, after.Count);
        Assert.StartsWith("The first paragraph now says", after[0].Text);
        Assert.Equal("A heading", after[1].Text.Trim());
        Assert.StartsWith("The second paragraph follows", after[2].Text);
        Assert.True(after[0].LineCount >= 3);
        // What lay below was pushed down by the extra line; nothing else moved.
        Assert.True(after[1].Bounds.Y > before[1].Bounds.Y + 5, "the heading moved down");
        Assert.Equal(after[1].Bounds.Y - before[1].Bounds.Y, after[2].Bounds.Y - before[2].Bounds.Y, 0.05);
        // The square and the pattern are exactly as they were, on screen and in the file.
        var now = again.Render(0, 1, false);
        for (int y = 20; y < 140; y++)
            for (int x = 225; x < 275; x++)
                Assert.True(now.Pixels[y * now.Stride + x * 4] == picture.Pixels[y * picture.Stride + x * 4], $"pixel {x},{y} changed");
        string file = PdfDocumentTests.Unpacked(saved);
        Assert.Contains("0 1 1 0 k", file);
        Assert.Contains("/Pattern cs /P1 scn", file);
        Assert.DoesNotContain("two lines) Tj T* (of plain words", file); // the old words are gone from the file, not just hidden
    }
    // ------------------------------------------------------------------ the look of text

    [Fact]
    public void Text_takes_another_colour_or_font_without_the_rest_of_the_page_being_written_again()
    {
        // Blue text in one block, sharing a writing position, beside a CMYK square.
        byte[] pdf = Page("0 1 1 0 k 230 230 40 40 re f BT /F1 12 Tf 0 0 1 rg 30 200 Td (one ) Tj (two ) Tj (three) Tj 0 -20 Td (below) Tj ET",
            "/Font<</F1<</Type/Font/Subtype/Type1/BaseFont/Courier>>>>");
        using var doc = PdfDocument.Open(pdf);
        var places = Pieces(doc);
        int direct = PdfDocument.DirectEdits, rewritten = PdfDocument.RewrittenEdits;
        var red = new PdfColor(200, 30, 30);
        var blue = new PdfColor(0, 0, 255);

        // Colour alone: the piece keeps its font and its place; the pieces after it keep their colour.
        Assert.Equal(1, doc.FormatText(0, [doc.GetTextRuns(0)[1].ObjectIndex], new TextFormat { Color = red }));
        var runs = doc.GetTextRuns(0);
        Assert.Equal([blue, red, blue, blue], runs.Select(r => r.Color).ToArray());
        Assert.All(runs, r => Assert.Equal("Courier", r.FontName));
        for (int i = 0; i < 4; i++) AssertSamePlace(places[i], (runs[i].Text, runs[i].Bounds), "after recolouring");

        // Bold and bigger: that piece is set anew; the others stay put, and it keeps its place in the order.
        Assert.Equal(1, doc.FormatText(0, [doc.GetTextRuns(0)[2].ObjectIndex], new TextFormat { Bold = true, Size = 16 }));
        Assert.Equal((direct + 2, rewritten), (PdfDocument.DirectEdits, PdfDocument.RewrittenEdits));
        byte[] saved = doc.ToBytes();
        using var again = PdfDocument.Open(saved);
        runs = again.GetTextRuns(0);
        Assert.Equal(["one", "two", "three", "below"], runs.Select(r => r.Text.Trim()).ToArray());
        Assert.Equal(["Courier", "Courier", "Courier-Bold", "Courier"], runs.Select(r => r.FontName).ToArray());
        Assert.Equal([blue, red, blue, blue], runs.Select(r => r.Color).ToArray());
        foreach (int i in new[] { 0, 1, 3 }) AssertSamePlace(places[i], (runs[i].Text, runs[i].Bounds), "after restyling");
        Assert.Equal(places[2].Bounds.X, runs[2].Bounds.X, 0.6);                    // it starts where it started
        Assert.Equal(places[2].Bounds.Bottom, runs[2].Bounds.Bottom, 0.6);          // on the same line
        Assert.InRange(runs[2].Bounds.Height / places[2].Bounds.Height, 1.2, 1.5);  // a third bigger
        Assert.Contains("0 1 1 0 k", PdfDocumentTests.Unpacked(saved));              // the CMYK square is as it was written
    }
}