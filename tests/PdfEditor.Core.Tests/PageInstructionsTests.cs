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
    public void Pictures_and_groups_are_moved_and_removed_directly_and_text_still_goes_the_older_way()
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
        Assert.Equal(rewritten + 1, PdfDocument.RewrittenEdits); // text is not yet edited directly

        doc.RemoveObject(0, doc.GetImages(0).Single().ObjectIndex);
        Assert.Empty(doc.GetImages(0));
        Assert.Contains("Some text", doc.GetPageText(0));
    }

    [Fact]
    public void A_clipped_item_moved_out_of_its_clip_goes_the_older_way_and_stays_visible()
    {
        // The red square sits inside a clip just larger than itself; moved 100 to the right it would be cut away.
        byte[] pdf = Page("q 40 40 60 60 re W n 1 0 0 rg 50 50 40 40 re f Q");
        using var doc = PdfDocument.Open(pdf);
        var red = doc.GetDrawings(0).Single(d => Math.Abs(d.Bounds.Width - 40) < 1);
        int rewritten = PdfDocument.RewrittenEdits;
        doc.MoveObjects(0, red.ObjectIndices, 100, 0);
        Assert.Equal(rewritten + 1, PdfDocument.RewrittenEdits);
        using var again = PdfDocument.Open(doc.ToBytes());
        var page = again.Render(0, 1, false);
        int i = (300 - 70) * page.Stride + 170 * 4;
        Assert.Equal((255, 0, 0), (page.Pixels[i + 2], page.Pixels[i + 1], page.Pixels[i])); // still seen, at its new place
    }
}