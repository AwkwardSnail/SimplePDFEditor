using System.Diagnostics;
using PdfEditor.Core;
using Xunit;
using Xunit.Abstractions;

namespace PdfEditor.Core.Tests;

/// <summary>
/// Saves the result of every kind of edit and has qpdf check the file's structure against
/// the PDF specification (ISO 32000-1): syntax, cross-reference table, object and stream
/// integrity, page tree. qpdf does not judge page content or appearance.
///
/// Needs qpdf (winget install QPDF.QPDF). Without it the tests pass with a note, so the
/// suite still runs on machines that do not have it.
/// </summary>
public class ConformanceTests(ITestOutputHelper output)
{
    private static readonly string? Qpdf = FindQpdf();

    private static string? FindQpdf()
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .SelectMany(dir => new[] { Path.Combine(dir, "qpdf.exe"), Path.Combine(dir, "qpdf") }).ToList();
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (Directory.Exists(programs))
            candidates.AddRange(Directory.GetDirectories(programs, "qpdf*").Select(d => Path.Combine(d, "bin", "qpdf.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>Runs "qpdf --check" on a document. Returns null when the file is clean, otherwise what qpdf said.</summary>
    private static string? Check(byte[] pdf)
    {
        string path = Path.Combine(Path.GetTempPath(), $"pdfeditor-check-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            var info = new ProcessStartInfo(Qpdf!, $"--check \"{path}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(info)!;
            string text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            // 0: no problems. 3: warnings (the file is readable but breaks a rule). 2: errors.
            return process.ExitCode == 0 ? null : $"exit code {process.ExitCode}: {text.Replace(path, "file").Trim()}";
        }
        finally { File.Delete(path); }
    }

    private void CheckAll(params (string Name, Func<PdfDocument> Make)[] cases)
    {
        if (Qpdf is null)
        {
            output.WriteLine("qpdf is not installed, so nothing was checked. Install it with: winget install QPDF.QPDF");
            return;
        }
        var problems = new List<string>();
        foreach (var (name, make) in cases)
        {
            using var doc = make();
            byte[] first = doc.ToBytes();
            if (Check(first) is { } problem) problems.Add($"{name}: {problem}");
            // Opening the saved file and saving it again must also give a clean file.
            using var again = PdfDocument.Open(first);
            if (Check(again.ToBytes()) is { } second) problems.Add($"{name} (saved a second time): {second}");
        }
        output.WriteLine($"{cases.Length} documents checked with {Qpdf}");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static PdfDocument Edited(string fixture, Action<PdfDocument> edit)
    {
        var doc = PdfDocument.Open(Fixture(fixture));
        edit(doc);
        return doc;
    }

    private static PdfDocument New(Action<PdfDocument> edit)
    {
        var doc = PdfDocument.Create();
        doc.AddPage();
        edit(doc);
        return doc;
    }

    private static int Paragraph(PdfDocument doc, string startsWith) => doc.GetParagraphs(0).First(p => p.Text.StartsWith(startsWith)).Index;

    [Fact]
    public void New_content_gives_structurally_valid_files()
    {
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGP8z8DAwMDAxMDAwMDAAAANHQEDasKb6QAAAABJRU5ErkJggg==");
        CheckAll(
            ("empty page", () => New(_ => { })),
            ("text in a standard font", () => New(d => d.AddText(0, 72, 72, "Hello snail\nSecond line", new TextStyle { Size = 14, Bold = true }))),
            ("text needing an embedded font", () => New(d => d.AddText(0, 72, 72, "Привет, улитка"))),
            ("image", () => New(d => d.AddImage(0, new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 }, 2, 2, 100, 100, 80, 80))),
            ("comment", () => New(d => d.AddComment(0, 200, 200, "A note", "Tester"))),
            ("highlight and underline", () => New(d =>
            {
                d.AddText(0, 72, 72, "Highlight this text");
                d.HighlightText(0, "this");
                d.AddMarkup(0, AnnotationKind.Underline, d.FindText(0, "text")[0].Rects);
            })),
            ("pages added, moved, rotated and deleted", () => New(d =>
            {
                d.AddPage(300, 400);
                d.AddPage();
                d.MovePage(0, 2);
                d.SetPageRotation(1, 90);
                d.DeletePage(0);
            })),
            ("pages imported from another file", () => New(d =>
            {
                using var other = PdfDocument.Open(Fixture("word-style.pdf"));
                d.ImportPages(other);
            })));
    }

    [Fact]
    public void Edited_documents_stay_structurally_valid()
    {
        CheckAll(
            ("untouched word-processor file", () => Edited("word-style.pdf", _ => { })),
            ("untouched two-column file", () => Edited("two-columns.pdf", _ => { })),
            ("text run edited", () => Edited("word-style.pdf", d => d.EditText(0, d.GetTextRuns(0)[0].ObjectIndex, "A new title"))),
            ("text run moved and another removed", () => Edited("word-style.pdf", d =>
            {
                d.MoveObject(0, d.GetTextRuns(0)[0].ObjectIndex, 10, 5);
                d.RemoveObject(0, d.GetTextRuns(0)[^1].ObjectIndex);
            })),
            ("image moved and resized", () => Edited("word-style.pdf", d =>
            {
                var image = d.GetImages(0)[0];
                d.SetImageBounds(0, image.ObjectIndex, new PdfRect(100, 400, 120, 60));
            })),
            ("paragraph rewritten with reflow", () => Edited("word-style.pdf", d =>
                d.ReplaceParagraphText(0, Paragraph(d, "The common"), "A much shorter paragraph, with a café and Москва in it."))),
            ("paragraph grown, pushing content down", () => Edited("word-style.pdf", d =>
            {
                int i = Paragraph(d, "The common");
                d.ReplaceParagraphText(0, i, d.GetParagraphs(0)[i].Text + " " + new string('x', 30) + " and many more words to make it wrap onto further lines of the page.");
            })),
            ("paragraph reformatted and realigned", () => Edited("word-style.pdf", d =>
            {
                int i = Paragraph(d, "Snails eat");
                d.FormatParagraphText(0, i, 0, 6, new TextFormat { Bold = true, Color = new PdfColor(200, 0, 0), Size = 14 });
                d.SetParagraphAlignment(0, Paragraph(d, "Its shell"), TextAlignment.Center);
            })),
            ("find and replace", () => Edited("word-style.pdf", d => d.ReplaceText("snail", "slug"))),
            ("paragraph removed in a justified two-column file", () => Edited("two-columns.pdf", d => d.ReplaceParagraphText(0, 1, ""))),
            ("comment and highlight on an existing file", () => Edited("word-style.pdf", d =>
            {
                d.HighlightText(0, "snail");
                d.AddComment(0, 300, 300, "Check this");
                d.RemoveAnnotation(0, 0);
            })));
    }

    [Fact]
    public void Imported_and_ungrouped_documents_are_structurally_valid()
    {
        CheckAll(
            ("RTF import", () => PdfDocument.FromRtf(File.ReadAllBytes(Fixture("sample.rtf")), out _)),
            ("RTF import with embedded fonts", () => PdfDocument.FromRtf(File.ReadAllBytes(Fixture("sample.rtf")), out _, new RtfImportOptions { EmbedFonts = true })),
            ("RTF import, then a paragraph edited", () =>
            {
                var doc = PdfDocument.FromRtf(File.ReadAllBytes(Fixture("sample.rtf")), out _);
                doc.ReplaceParagraphText(0, Paragraph(doc, "Garden snails"), "Garden snails need a damp and shady home.");
                return doc;
            }),
            ("drawing moved and removed", () =>
            {
                var doc = PdfDocument.FromRtf(File.ReadAllBytes(Fixture("sample.rtf")), out _);
                var drawings = doc.GetDrawings(0);
                doc.MoveObjects(0, drawings[0].ObjectIndices, 5, 5);
                doc.RemoveObjects(0, doc.GetDrawings(0)[^1].ObjectIndices);
                return doc;
            }),
            ("group taken apart", () =>
            {
                var doc = PdfDocument.Open(PdfDocumentTests.PdfWithGroup());
                Assert.True(doc.Ungroup(0, doc.GetGroups(0)[0].ObjectIndex));
                return doc;
            }));
    }
}
