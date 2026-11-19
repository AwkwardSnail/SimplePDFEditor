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
            ("page cut in two", () => Edited("word-style.pdf", d => d.SplitPage(0, d.GetBreakPositions(0)[2]))),
            ("page break", () => Edited("word-style.pdf", d =>
            {
                d.HighlightText(0, "left-coiling");
                d.InsertPageBreak(0, d.GetParagraphs(0).First(p => p.Text.StartsWith("Its shell")).Bounds.Y);
            })),
            ("paragraph removed in a justified two-column file", () => Edited("two-columns.pdf", d => d.ReplaceParagraphText(0, 1, ""))),
            ("comment and highlight on an existing file", () => Edited("word-style.pdf", d =>
            {
                d.HighlightText(0, "snail");
                d.AddComment(0, 300, 300, "Check this");
                d.RemoveAnnotation(0, 0);
            })));
    }

    /// <summary>
    /// Renaming clashing fonts appends an update to the file before it is opened. The update must be
    /// a valid one, in both forms a file's cross-reference can take (a table, or a packed stream).
    /// </summary>
    [Fact]
    public void Renaming_clashing_fonts_gives_a_structurally_valid_file()
    {
        if (Qpdf is null)
        {
            output.WriteLine("qpdf is not installed, so nothing was checked. Install it with: winget install QPDF.QPDF");
            return;
        }
        byte[] table = PdfDocumentTests.PdfWithTwoFonts("AAAAAB+Helvetica", "AAAAAF+Helvetica");

        // The same file with its objects packed into streams, as most modern programs write them.
        string source = Path.Combine(Path.GetTempPath(), $"pdfeditor-fonts-{Guid.NewGuid():N}.pdf"), packedPath = source + ".packed.pdf";
        File.WriteAllBytes(source, table);
        byte[] packed;
        try
        {
            var info = new ProcessStartInfo(Qpdf, $"--object-streams=generate \"{source}\" \"{packedPath}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(info)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            packed = File.ReadAllBytes(packedPath);
        }
        finally
        {
            File.Delete(source);
            File.Delete(packedPath);
        }

        foreach (var (name, pdf) in new[] { ("table", table), ("packed", packed) })
        {
            byte[] renamed = PdfEditor.Core.Preflight.DistinctFontNames.Apply(pdf);
            Assert.True(renamed.Length > pdf.Length, $"{name}: nothing was renamed");
            Assert.Null(Check(renamed));
            using var doc = PdfDocument.Open(pdf);
            Assert.Equal(["Helvetica", "Helvetica-2"], PdfDocumentTests.FontNames(pdf));
            Assert.Null(Check(doc.ToBytes()));
        }
    }

    [Fact]
    public void Imported_and_ungrouped_documents_are_structurally_valid()
    {
        CheckAll(
            ("RTF import", () => PdfDocument.FromRtf(File.ReadAllBytes(Fixture("sample.rtf")), out _)),
            ("page with gradients edited", () =>
            {
                var doc = PdfDocument.Open(PdfDocumentTests.PdfWithGradients());
                doc.MoveObjects(0, doc.GetDrawings(0)[0].ObjectIndices, 10, 0);
                return doc;
            }),
            ("page spread over sheets for printing", () =>
            {
                using var doc = PdfDocument.Open(Fixture("word-style.pdf"));
                return doc.CreateTiles(0, new TileOptions { PaperWidth = 419.5, PaperHeight = 595.3 });
            }),
            ("page resized with its content, then edited", () =>
            {
                var doc = PdfDocument.Open(Fixture("word-style.pdf"));
                doc.ResizePage(0, 1190.55, 1683.78, scaleContent: true);
                doc.AddText(0, 72, 72, "Added after resizing", new TextStyle());
                doc.ResizePage(0, 420, 595, scaleContent: false);
                return doc;
            }),
            ("shapes drawn on a page, then filled and unfilled", () =>
            {
                var doc = PdfDocument.Open(Fixture("word-style.pdf"));
                var style = new ShapeStyle { Stroke = new PdfColor(200, 30, 30), Width = 3 };
                doc.AddShape(0, ShapeKind.Arrow, [new(60, 60), new(200, 120)], style);
                int box = doc.AddShape(0, ShapeKind.Box, [new(60, 150), new(200, 220)], style with { Fill = new PdfColor(255, 235, 60) });
                int ellipse = doc.AddShape(0, ShapeKind.Ellipse, [new(60, 250), new(200, 320)], style);
                doc.AddShape(0, ShapeKind.Spline, [new(60, 350), new(100, 400), new(160, 340), new(220, 390)], style);
                doc.SetShapeFill(0, [box], null);
                doc.SetShapeFill(0, [ellipse], new PdfColor(30, 70, 200));
                return doc;
            }),
            ("watermark added, a paragraph moved a layer down, a shape's line changed", () =>
            {
                var doc = PdfDocument.Open(Fixture("word-style.pdf"));
                int box = doc.AddShape(0, ShapeKind.Box, [new(60, 60), new(300, 200)], new ShapeStyle { Fill = new PdfColor(255, 235, 60) });
                doc.MoveLayer(0, [box], up: false);
                doc.SetShapeLine(0, [doc.GetDrawings(0)[0].ObjectIndices[0]], new PdfColor(30, 70, 200), 4);
                doc.AddWatermark(0, "DRAFT");
                return doc;
            }),
            ("text given another font and colour, and a block turned", () =>
            {
                var doc = PdfDocument.Open(Fixture("word-style.pdf"));
                var runs = doc.GetTextRuns(0);
                doc.FormatText(0, runs.Take(3).Select(r => r.ObjectIndex), new TextFormat { Font = StandardFont.Courier, Bold = true, Color = new PdfColor(30, 70, 200) });
                doc.RotateObjects(0, doc.GetTextRuns(0).Skip(4).Take(3).Select(r => r.ObjectIndex), clockwise: true);
                return doc;
            }),
            ("Type 3 letters rebuilt as ordinary text", () =>
            {
                var doc = PdfDocument.Open(PdfDocumentTests.PdfWithType3Letters());
                doc.ConvertLooseText();
                return doc;
            }),
            ("Type 3 letters kept as pictures after a move", () =>
            {
                var doc = PdfDocument.Open(PdfDocumentTests.PdfWithType3Letters());
                doc.MoveObject(0, doc.GetTextRuns(0)[0].ObjectIndex, 0, 40);
                return doc;
            }),
            ("page spread over sheets placed by hand", () =>
            {
                using var doc = PdfDocument.Open(Fixture("word-style.pdf"));
                return doc.CreateTiles(0, [new TileSheet(-14, -14, 419.5, 595.3), new TileSheet(100, 300, 595.3, 419.5)], new TileOptions());
            }),
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
