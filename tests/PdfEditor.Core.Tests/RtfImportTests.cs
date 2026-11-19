using System.Text;
using PdfEditor.Core;
using Xunit;

namespace PdfEditor.Core.Tests;

public class RtfImportTests
{
    private static byte[] Sample => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.rtf"));

    private static PdfDocument Import(string rtf, out RtfImportResult result, RtfImportOptions? options = null) =>
        PdfDocument.FromRtf(Encoding.Latin1.GetBytes(rtf), out result, options);

    private static TextParagraph Find(PdfDocument doc, string startsWith, int page = 0) =>
        doc.GetParagraphs(page).Single(p => p.Text.StartsWith(startsWith, StringComparison.Ordinal));

    /// <summary>Colour of the page at a point, rendered at 72 dpi.</summary>
    private static (byte R, byte G, byte B) Pixel(RenderedPage image, double x, double y)
    {
        int i = (int)y * image.Stride + (int)x * 4;
        return (image.Pixels[i + 2], image.Pixels[i + 1], image.Pixels[i]);
    }

    // ------------------------------------------------------------------ a real word-processor file

    [Fact]
    public void Imports_a_word_processor_document_with_its_formatting()
    {
        using var doc = PdfDocument.FromRtf(Sample, out var result);
        Assert.Equal(0, result.FirstPage);
        Assert.Equal(2, result.PageCount);
        Assert.Empty(result.Warnings);
        Assert.Equal(new PdfSize(595.3, 841.9), new PdfSize(Math.Round(doc.GetPageSize(0).Width, 1), Math.Round(doc.GetPageSize(0).Height, 1)));

        var heading = Find(doc, "Snail Care Sheet");
        Assert.Equal(TextAlignment.Center, heading.Alignment);
        Assert.Equal(16, heading.FontSize, 1);
        Assert.True(heading.Spans.Single().Bold);

        var body = Find(doc, "Garden snails");
        Assert.Equal(TextAlignment.Justify, body.Alignment);
        Assert.Equal(3, body.LineCount);
        Assert.Equal(70.9, body.Bounds.X, 0);                                            // 2.5 cm margin
        string Spanned(Func<TextSpan, bool> f) => string.Concat(body.Spans.Where(f).Select(s => body.Text.Substring(s.Start, s.Length))).Trim();
        Assert.Equal("damp", Spanned(s => s.Bold));
        Assert.Equal("fresh", Spanned(s => s.Italic));
        Assert.Equal("away from salt", Spanned(s => s.Color == new PdfColor(192, 0, 0)));
        Assert.Contains(body.Spans, s => body.Text.Substring(s.Start, s.Length) == "1" && s.FontSize < 9); // superscript

        var page = doc.Render(0);
        var highlight = doc.FindText(0, "every evening").Single().Rects[0];
        var yellow = Pixel(page, highlight.X + 2, highlight.Y - 1);                         // just above the letters
        Assert.True(yellow.R > 240 && yellow.G > 240 && yellow.B < 40, $"expected yellow highlighting, got {yellow}");
        var calcium = doc.FindText(0, "calcium").First().Rects[0];
        Assert.Contains(Enumerable.Range(0, 4), dy => Pixel(page, calcium.X + calcium.Width / 2, calcium.Bottom + dy).R < 100); // underlined

        Assert.Equal(["• Cucumber and lettuce", "• Cuttlefish bone for calcium", "• Fresh water, changed daily"],
            doc.GetParagraphs(0).Where(p => p.Text.StartsWith('•')).Select(p => p.Text));
        Assert.Contains(doc.GetParagraphs(0), p => p.Text == "1. Clean the tank weekly.");
        var bullet = Find(doc, "• Cucumber");
        var number = Find(doc, "1. Clean");
        Assert.Equal(bullet.Bounds.X, number.Bounds.X, 0);
        Assert.Equal(doc.FindText(0, "Cucumber").Single().Rects[0].X, doc.FindText(0, "Clean").Single().Rects[0].X, 0); // text after the marker lines up

        var temperature = doc.FindText(0, "18-24").Single().Rects[0];
        Assert.InRange(temperature.X, 70.9 + 5 / 2.54 * 72, 70.9 + 5 / 2.54 * 72 + 2);   // tab stop at 5 cm (measured at the ink)

        // The body uses Word's default 1.15 lines; this paragraph 1.5 lines.
        double single = Find(doc, "Garden snails").LineSpacing / 1.15;
        Assert.InRange(Find(doc, "This paragraph uses").LineSpacing, 1.5 * single - 0.6, 1.5 * single + 0.6);

        // The table: each cell's text inside its own bordered cell.
        var food = doc.FindText(0, "Food", matchCase: true).Single().Rects[0];
        var often = doc.FindText(0, "How often").Single().Rects[0];
        Assert.InRange(often.Y - food.Y, -1, 1);                                           // same line (ink tops differ slightly)
        Assert.True(often.X > food.Right + 100);
        Assert.True(Enumerable.Range(1, 8).Any(dx => Pixel(page, often.X - dx, often.Y + 4).R < 100), "a border between the cells");

        var image = doc.GetImages(0).Single();
        Assert.InRange(image.Bounds.Width, 3 / 2.54 * 72 - 0.5, 3 / 2.54 * 72 + 0.5);      // 3 cm wide
        Assert.InRange(image.Bounds.Height, image.Bounds.Width / 2 - 0.5, image.Bounds.Width / 2 + 0.5);

        Assert.Equal("Second page: café, naïve, Москва.", Find(doc, "Second page", 1).Text);
    }

    [Fact]
    public void Imported_paragraphs_can_be_edited_with_reflow()
    {
        using var doc = PdfDocument.FromRtf(Sample, out _);
        var body = Find(doc, "Garden snails");
        var result = doc.ReplaceParagraphText(0, body.Index, body.Text.Replace("damp home", "damp and shady home"));
        Assert.False(result.UsedSubstituteFont);
        var after = Find(doc, "Garden snails");
        Assert.Contains("damp and shady home", after.Text);
        Assert.Equal(TextAlignment.Justify, after.Alignment);
        Assert.Equal("damp", string.Concat(after.Spans.Where(s => s.Bold).Select(s => after.Text.Substring(s.Start, s.Length))).Trim());
    }

    [Fact]
    public void Can_embed_the_named_fonts_when_they_are_installed()
    {
        using var doc = PdfDocument.FromRtf(Sample, out var result, new RtfImportOptions { EmbedFonts = true });
        var body = Find(doc, "Garden snails");
        bool installed = body.Spans.Any(s => s.FontName.Contains("Liberation", StringComparison.OrdinalIgnoreCase));
        if (installed) Assert.Empty(result.Warnings);
        else Assert.Contains(result.Warnings, w => w.Contains("not installed"));
        Assert.Contains("Москва", Find(doc, "Second page", 1).Text);
    }

    // ------------------------------------------------------------------ specific RTF features

    [Fact]
    public void Reads_unicode_and_code_page_text()
    {
        using var doc = Import(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\froman Times New Roman;}{\f1\fswiss\fcharset204 Arial;}}" +
                               // (C# expands \u escapes even in verbatim strings, so this one is written "\\u".)
                               @"\pard Caf\'e9 \uc1" + "\\u8364" + @"? 10 {\f1 \'cf\'f0\'e8\'e2\'e5\'f2}\par}", out var result);
        Assert.Equal("Café € 10 Привет", doc.GetParagraphs(0).Single().Text);
    }

    [Fact]
    public void Uses_the_page_setup_and_flows_onto_new_pages()
    {
        var text = string.Concat(Enumerable.Range(1, 80).Select(i => $@"\pard\sa120 Paragraph number {i} of the long document.\par "));
        using var doc = Import(@"{\rtf1\ansi{\fonttbl{\f0\fswiss Arial;}}\paperw11906\paperh16838\margl1440\margr1440\margt1440\margb1440\f0\fs24 " +
                               text + @"\page\pard After the break.\par}", out var result);
        Assert.Equal(595.3, Math.Round(doc.GetPageSize(0).Width, 1));
        Assert.True(result.PageCount >= 3, $"80 paragraphs and a page break need at least 3 pages, got {result.PageCount}");
        var first = doc.GetParagraphs(0)[0];
        Assert.Equal(72, first.Bounds.X, 0);
        Assert.InRange(first.Bounds.Y, 72, 76);
        Assert.All(Enumerable.Range(0, doc.PageCount), p => Assert.True(doc.GetParagraphs(p).All(x => x.Bounds.Bottom <= 841.9 - 72 + 1)));
        var all = Enumerable.Range(0, doc.PageCount).SelectMany(p => doc.GetParagraphs(p)).Select(p => p.Text).ToList();
        Assert.Equal(81, all.Count);
        Assert.Equal("Paragraph number 80 of the long document.", all[79]);
        Assert.Equal(["After the break."], doc.GetParagraphs(doc.PageCount - 1).Select(p => p.Text));
    }

    [Fact]
    public void Applies_tabs_indents_and_exact_line_spacing()
    {
        using var doc = Import(@"{\rtf1\ansi{\fonttbl{\f0\froman Times New Roman;}}\margl1440\f0\fs24" +
                               @"\pard\tx2880 Name:\tab Snail\par" +
                               @"\pard\li720\sl-480\slmult0 Indented by half an inch, with lines exactly 24 points apart so that this paragraph needs a few lines to show it.\par}", out _);
        Assert.InRange(doc.FindText(0, "Snail").Single().Rects[0].X, 72 + 144, 72 + 144 + 1.5); // measured at the ink, just right of the stop
        var indented = Find(doc, "Indented");
        Assert.Equal(72 + 36, indented.Bounds.X, 0);
        Assert.Equal(24, indented.LineSpacing, 1);
        Assert.True(indented.LineCount >= 2);
    }

    [Fact]
    public void Leaves_out_hidden_text_headers_and_unsupported_pictures_and_says_so()
    {
        using var doc = Import(@"{\rtf1\ansi{\fonttbl{\f0\froman Times;}}{\header Header text\par}" +
                               @"\pard Visible{\v  hidden} text.{\pict\wmetafile8\picwgoal1000\pichgoal1000 0102}\par}", out var result);
        Assert.Equal("Visible text.", doc.GetParagraphs(0).Single().Text);
        Assert.Contains(result.Warnings, w => w.Contains("Headers and footers"));
        Assert.Contains(result.Warnings, w => w.Contains("pictures"));
    }

    [Fact]
    public void Inserts_pages_into_an_existing_document()
    {
        using var doc = PdfDocument.Create();
        doc.AddPage();
        doc.AddText(0, 72, 72, "Existing page");
        var result = doc.ImportRtf(Encoding.ASCII.GetBytes(@"{\rtf1\ansi\pard Imported text.\par}"), index: 0);
        Assert.Equal(0, result.FirstPage);
        Assert.Equal(2, doc.PageCount);
        Assert.Contains("Imported text.", doc.GetPageText(0));
        Assert.Contains("Existing page", doc.GetPageText(1));
    }

    [Fact]
    public void An_editor_session_can_start_from_an_rtf_file()
    {
        string pdf = Path.Combine(Path.GetTempPath(), $"rtf-import-{Guid.NewGuid():N}.pdf");
        try
        {
            using var session = EditorSession.FromRtf(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.rtf"), out var result);
            Assert.Equal("sample.pdf", session.SuggestedFileName);
            Assert.Null(session.FilePath);
            Assert.True(session.IsModified);
            Assert.Equal(result.PageCount, session.Document.PageCount);
            session.Apply(d => d.AddComment(0, 20, 20, "Imported from RTF"));
            session.Save(pdf);
            using var saved = PdfDocument.Open(pdf);
            Assert.Contains("Snail Care Sheet", saved.GetPageText(0));
        }
        finally { File.Delete(pdf); }
    }

    [Fact]
    public void Rejects_files_that_are_not_rtf()
    {
        using var doc = PdfDocument.Create();
        Assert.Throws<PdfException>(() => doc.ImportRtf(Encoding.ASCII.GetBytes("plain text, not RTF")));
        Assert.Equal(0, doc.PageCount);
    }
}
