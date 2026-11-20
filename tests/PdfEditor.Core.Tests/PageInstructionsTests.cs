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
}
