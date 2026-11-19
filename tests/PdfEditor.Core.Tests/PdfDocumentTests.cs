using PdfEditor.Core;
using Xunit;

namespace PdfEditor.Core.Tests;

public class PdfDocumentTests
{
    // 2x2 opaque PNG (red, green / blue, white).
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAEklEQVR4nGP4z8DAAMIM/4EAAB/uBfsL2WiLAAAAAElFTkSuQmCC");

    private static PdfDocument Reopen(PdfDocument doc) => PdfDocument.Open(doc.ToBytes());

    private static bool HasInk(RenderedPage image, PdfRect area, double scale = 1)
    {
        for (int y = (int)(area.Y * scale); y < Math.Min(image.Height, (int)(area.Bottom * scale)); y++)
            for (int x = (int)(area.X * scale); x < Math.Min(image.Width, (int)(area.Right * scale)); x++)
            {
                int i = y * image.Stride + x * 4;
                if (image.Pixels[i] != 255 || image.Pixels[i + 1] != 255 || image.Pixels[i + 2] != 255) return true;
            }
        return false;
    }

    [Fact]
    public void Pages_can_be_added_moved_rotated_and_deleted()
    {
        using var doc = PdfDocument.Create();
        doc.AddPage(100, 200);
        doc.AddPage(300, 400);
        doc.AddPage(500, 600, index: 0);
        Assert.Equal(3, doc.PageCount);
        Assert.Equal(new PdfSize(500, 600), doc.GetPageSize(0));

        doc.MovePage(0, 2);
        Assert.Equal(new PdfSize(100, 200), doc.GetPageSize(0));
        Assert.Equal(new PdfSize(500, 600), doc.GetPageSize(2));

        doc.SetPageRotation(0, 90);
        Assert.Equal(90, doc.GetPageRotation(0));
        Assert.Equal(new PdfSize(200, 100), doc.GetPageSize(0));

        doc.DeletePage(1);
        using var saved = Reopen(doc);
        Assert.Equal(2, saved.PageCount);
        Assert.Equal(new PdfSize(500, 600), saved.GetPageSize(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => saved.DeletePage(5));
    }

    [Fact]
    public void Pages_can_be_imported_from_another_document()
    {
        using var source = PdfDocument.Create();
        for (int i = 1; i <= 3; i++)
        {
            source.AddPage();
            source.AddText(i - 1, 50, 50, $"Source page {i}");
        }
        using var doc = PdfDocument.Create();
        doc.AddPage();
        doc.ImportPages(source, "2-3", index: 0);
        Assert.Equal(3, doc.PageCount);
        Assert.Contains("Source page 2", doc.GetPageText(0));
        Assert.Contains("Source page 3", doc.GetPageText(1));
    }

    [Fact]
    public void Added_text_survives_saving_and_lands_where_asked()
    {
        using var doc = PdfDocument.Create();
        doc.AddPage();
        doc.AddText(0, 72, 100, "Hello snail\nSecond line", new TextStyle { Size = 20, Bold = true, Color = new PdfColor(200, 0, 0) });

        using var saved = Reopen(doc);
        var runs = saved.GetTextRuns(0);
        Assert.Equal(["Hello snail", "Second line"], runs.Select(r => r.Text));
        Assert.Equal(20, runs[0].FontSize);
        Assert.Equal(new PdfColor(200, 0, 0), runs[0].Color);
        Assert.Contains("Helvetica-Bold", runs[0].FontName);
        Assert.InRange(runs[0].Bounds.X, 71, 75);
        Assert.InRange(runs[0].Bounds.Y, 98, 106);            // top of the first line is near y = 100
        Assert.InRange(runs[1].Bounds.Y - runs[0].Bounds.Y, 23, 25); // 1.2 line spacing
        Assert.True(HasInk(saved.Render(0), runs[0].Bounds));
        Assert.False(HasInk(saved.Render(0), new PdfRect(72, 300, 200, 100)));
    }

    [Fact]
    public void Text_outside_latin_falls_back_to_an_embedded_font()
    {
        using var doc = PdfDocument.Create();
        doc.AddPage();
        doc.AddText(0, 50, 50, "Привет, улитка");
        using var saved = Reopen(doc);
        // (Compared without spaces: which space character comes back depends on the fallback font.)
        Assert.Contains("Привет,улитка", string.Concat(saved.GetPageText(0).Where(c => !char.IsWhiteSpace(c) && c != ' ')));
    }

    [Fact]
    public void Existing_text_can_be_edited_in_place()
    {
        using var doc = PdfDocument.Create();
        doc.AddPage();
        doc.AddText(0, 72, 100, "Invoice total: 100", new TextStyle { Font = StandardFont.Times, Size = 14 });
        doc.AddText(0, 72, 200, "Untouched");

        using var saved = Reopen(doc);
        var run = saved.FindTextRunAt(0, 80, 108)!;
        Assert.Equal("Invoice total: 100", run.Text);
        Assert.Equal(TextEditResult.KeptFont, saved.EditText(0, run.ObjectIndex, "Invoice total: 250"));

        using var edited = Reopen(saved);
        var after = edited.GetTextRuns(0).Single(r => r.Text.StartsWith("Invoice"));
        Assert.Equal("Invoice total: 250", after.Text);
        Assert.Equal(run.Bounds.X, after.Bounds.X, 1);
        Assert.Equal(run.Bounds.Y, after.Bounds.Y, 1);
        Assert.Equal(14, after.FontSize);
        Assert.Contains("Times", after.FontName);
        Assert.DoesNotContain("100", edited.GetPageText(0));
        Assert.Contains("Untouched", edited.GetPageText(0));
    }

    [Fact]
    public void Editing_substitutes_the_font_when_it_lacks_characters_and_empty_text_removes_the_run()
    {
        using var doc = PdfDocument.Create();
        doc.AddPage();
        doc.AddText(0, 72, 100, "Name", new TextStyle { Size = 16, Color = new PdfColor(0, 0, 255) });
        var run = doc.GetTextRuns(0).Single();

        Assert.Equal(TextEditResult.SubstitutedFont, doc.EditText(0, run.ObjectIndex, "Имя"));
        using var saved = Reopen(doc);
        var after = saved.GetTextRuns(0).Single();
        Assert.Equal("Имя", after.Text);
        Assert.Equal(16, after.FontSize);
        Assert.Equal(new PdfColor(0, 0, 255), after.Color);
        Assert.InRange(after.Bounds.X, run.Bounds.X - 1, run.Bounds.X + 1);

        Assert.Equal(TextEditResult.Removed, saved.EditText(0, after.ObjectIndex, ""));
        Assert.Empty(saved.GetTextRuns(0));
    }

    [Fact]
    public void Images_are_placed_sized_moved_and_removed()
    {
        using var doc = PdfDocument.Create();
        doc.AddPage();
        doc.AddImage(0, Png, 100, 150, width: 80);                     // height follows the aspect ratio
        doc.AddImage(0, new byte[] { 0, 0, 255, 255 }, 1, 1, 300, 300, 50, 20); // one red BGRA pixel

        using var saved = Reopen(doc);
        var images = saved.GetImages(0);
        Assert.Equal(2, images.Count);
        Assert.Equal(new PdfRect(100, 150, 80, 80), Round(images[0].Bounds));
        Assert.Equal((2, 2), (images[0].PixelWidth, images[0].PixelHeight));
        Assert.Equal(new PdfRect(300, 300, 50, 20), Round(images[1].Bounds));

        var render = saved.Render(0);
        int i = 310 * render.Stride + 320 * 4;
        Assert.Equal((0, 0, 255), (render.Pixels[i], render.Pixels[i + 1], render.Pixels[i + 2])); // red, in BGR order
        int topLeft = 160 * render.Stride + 110 * 4;                    // top-left quarter of the PNG is red
        Assert.True(render.Pixels[topLeft + 2] > 200 && render.Pixels[topLeft] < 60);

        saved.MoveObject(0, images[0].ObjectIndex, 10, -20);
        Assert.Equal(new PdfRect(110, 130, 80, 80), Round(saved.GetImages(0)[0].Bounds));
        saved.RemoveObject(0, images[1].ObjectIndex);
        Assert.Single(saved.GetImages(0));

        Assert.Throws<PdfException>(() => saved.AddImage(0, new byte[] { 1, 2, 3 }, 0, 0));
    }

    [Fact]
    public void Comments_round_trip_and_can_be_edited_and_removed()
    {
        using var doc = PdfDocument.Create();
        doc.AddPage();
        doc.AddComment(0, 200, 300, "Please check this", "Reviewer");

        using var saved = Reopen(doc);
        var note = saved.GetAnnotations(0).Single();
        Assert.Equal(AnnotationKind.Comment, note.Kind);
        Assert.Equal("Please check this", note.Contents);
        Assert.Equal("Reviewer", note.Author);
        Assert.Equal(new PdfRect(200, 300, 20, 20), Round(note.Bounds));
        Assert.True(HasInk(saved.Render(0), note.Bounds));
        Assert.False(HasInk(saved.Render(0, showAnnotations: false), note.Bounds));

        saved.SetAnnotationText(0, note.Index, "Checked");
        Assert.Equal("Checked", saved.GetAnnotations(0).Single().Contents);
        saved.RemoveAnnotation(0, note.Index);
        Assert.Empty(saved.GetAnnotations(0));
    }

    [Fact]
    public void Text_can_be_found_and_highlighted()
    {
        using var doc = PdfDocument.Create();
        doc.AddPage();
        doc.AddText(0, 72, 100, "The awkward snail is slow.\nA snail carries its home.", new TextStyle { Size = 14 });

        var matches = doc.FindText(0, "SNAIL");
        Assert.Equal(2, matches.Count);
        Assert.Empty(doc.FindText(0, "SNAIL", matchCase: true));
        var runs = doc.GetTextRuns(0);
        Assert.All(matches.Zip(runs), p => Assert.True(p.Second.Bounds.Contains(p.First.Rects[0].X + 1, p.First.Rects[0].Y + p.First.Rects[0].Height / 2)));

        Assert.Equal(2, doc.HighlightText(0, "snail", new PdfColor(0, 255, 0)));
        using var saved = Reopen(doc);
        var highlights = saved.GetAnnotations(0);
        Assert.Equal(2, highlights.Count);
        Assert.All(highlights, h => Assert.Equal(AnnotationKind.Highlight, h.Kind));
        Assert.Equal(new PdfColor(0, 255, 0), highlights[0].Color);
        Assert.Equal(matches[0].Rects[0].X, highlights[0].Bounds.X, 1);

        // The highlight tints the area green without hiding the text underneath.
        var area = highlights[0].Bounds;
        var render = saved.Render(0, 2);
        int greenish = 0, dark = 0;
        for (int y = (int)(area.Y * 2); y < (int)(area.Bottom * 2); y++)
            for (int x = (int)(area.X * 2); x < (int)(area.Right * 2); x++)
            {
                int i = y * render.Stride + x * 4;
                if (render.Pixels[i + 1] > 200 && render.Pixels[i] < 100 && render.Pixels[i + 2] < 100) greenish++;
                if (render.Pixels[i + 1] < 80) dark++;
            }
        Assert.True(greenish > 50, $"expected a green tint, got {greenish} green pixels");
        Assert.True(dark > 10, $"expected text to stay visible, got {dark} dark pixels");
    }

    [Fact]
    public void Selection_by_point_gives_rects_for_highlighting()
    {
        using var doc = PdfDocument.Create();
        doc.AddPage();
        doc.AddText(0, 72, 100, "Select me", new TextStyle { Size = 20 });
        var run = doc.GetTextRuns(0).Single();
        int first = doc.GetCharIndexAt(0, run.Bounds.X + 2, run.Bounds.Y + run.Bounds.Height / 2);
        int last = doc.GetCharIndexAt(0, run.Bounds.Right - 2, run.Bounds.Y + run.Bounds.Height / 2);
        Assert.Equal(0, first);
        Assert.Equal(8, last);
        Assert.Equal(-1, doc.GetCharIndexAt(0, 400, 600));
        var rects = doc.GetTextRects(0, first, last - first + 1);
        Assert.Single(rects);
        Assert.Equal(run.Bounds.Width, rects[0].Width, 3);
    }

    [Fact]
    public void Coordinates_follow_the_displayed_page_when_it_is_rotated()
    {
        using var doc = PdfDocument.Create();
        doc.AddPage(400, 600);
        doc.SetPageRotation(0, 90);
        doc.AddText(0, 100, 50, "Rotated", new TextStyle { Size = 20 });
        doc.AddImage(0, new byte[] { 0, 0, 0, 255 }, 1, 1, 300, 200, 40, 30);
        doc.AddComment(0, 500, 300, "note");

        using var saved = Reopen(doc);
        Assert.Equal(new PdfSize(600, 400), saved.GetPageSize(0));
        var run = saved.GetTextRuns(0).Single();
        Assert.InRange(run.Bounds.X, 99, 103);
        Assert.InRange(run.Bounds.Y, 48, 56);
        Assert.True(run.Bounds.Width > run.Bounds.Height, "text should read horizontally on the displayed page");
        Assert.Equal(new PdfRect(300, 200, 40, 30), Round(saved.GetImages(0).Single().Bounds));
        Assert.Equal(new PdfRect(500, 300, 20, 20), Round(saved.GetAnnotations(0).Single().Bounds));
        Assert.True(HasInk(saved.Render(0), new PdfRect(300, 200, 40, 30)));
    }

    [Fact]
    public void Images_can_be_resized()
    {
        using var doc = PdfDocument.Create();
        doc.AddPage();
        doc.AddImage(0, Png, 100, 150, width: 80);
        doc.SetImageBounds(0, doc.GetImages(0)[0].ObjectIndex, new PdfRect(20, 30, 200, 100));
        using var saved = Reopen(doc);
        Assert.Equal(new PdfRect(20, 30, 200, 100), Round(saved.GetImages(0).Single().Bounds));
    }

    [Fact]
    public void Session_tracks_changes_and_supports_undo_and_redo()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pdfeditor-test-{Guid.NewGuid():N}.pdf");
        try
        {
            using var session = EditorSession.New();
            int changes = 0;
            session.Changed += (_, _) => changes++;
            Assert.False(session.IsModified);
            Assert.False(session.CanUndo);

            session.Apply(d => d.AddText(0, 50, 50, "First"));
            session.Apply(d => d.AddPage());
            Assert.True(session.IsModified);
            Assert.Equal(2, session.Document.PageCount);

            session.Undo();
            Assert.Equal(1, session.Document.PageCount);
            Assert.Contains("First", session.Document.GetPageText(0));
            session.Undo();
            Assert.DoesNotContain("First", session.Document.GetPageText(0));
            Assert.False(session.CanUndo);
            session.Redo();
            session.Redo();
            Assert.Equal(2, session.Document.PageCount);
            Assert.False(session.CanRedo);

            // A failed edit leaves the document untouched and adds no undo step.
            session.Undo();
            Assert.Throws<ArgumentOutOfRangeException>(() => session.Apply(d => { d.AddPage(); d.DeletePage(99); }));
            Assert.Equal(1, session.Document.PageCount);
            Assert.True(session.CanRedo);

            session.Apply(d => d.AddComment(0, 10, 10, "note"));
            Assert.False(session.CanRedo);
            Assert.Throws<InvalidOperationException>(() => session.Save());
            session.Save(path);
            Assert.False(session.IsModified);
            Assert.Equal(path, session.FilePath);
            Assert.Equal(9, changes);

            using var reopened = EditorSession.Open(path);
            Assert.Contains("First", reopened.Document.GetPageText(0));
            Assert.Single(reopened.Document.GetAnnotations(0));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Vector_drawings_are_grouped_and_can_be_moved_and_removed()
    {
        // The RTF sample's table is drawn as six bordered cells: one drawing made of six shapes.
        using var doc = PdfDocument.FromRtf(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.rtf")), out _);
        var table = doc.GetDrawings(0).OrderByDescending(d => d.ObjectIndices.Count).First();
        Assert.Equal(6, table.ObjectIndices.Count);
        Assert.True(table.Bounds.Width > 400);
        string text = doc.GetPageText(0);

        doc.MoveObjects(0, table.ObjectIndices, 12, -30);
        using var moved = Reopen(doc);
        var after = moved.GetDrawings(0).OrderByDescending(d => d.ObjectIndices.Count).First();
        Assert.Equal(6, after.ObjectIndices.Count);
        Assert.Equal(table.Bounds.X + 12, after.Bounds.X, 1);
        Assert.Equal(table.Bounds.Y - 30, after.Bounds.Y, 1);
        Assert.Equal(text, moved.GetPageText(0));

        int before = moved.GetDrawings(0).Count;
        moved.RemoveObjects(0, after.ObjectIndices);
        Assert.Equal(before - 1, moved.GetDrawings(0).Count);
    }

    [Fact]
    public void Colours_outside_rgb_survive_a_page_rewrite()
    {
        // A CMYK-filled box, a grey box and CMYK text, as print-ready PDFs have them. PDFium only
        // writes RGB colours back when it rewrites a page, so these used to turn black.
        const string content = "0.8 0.2 0 0 k 100 600 200 100 re f 0.6 g 100 400 80 80 re f BT 0 1 1 0 k /F1 40 Tf 100 250 Td (Hello) Tj ET";
        string pdf = "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
                     "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj\n" +
                     $"4 0 obj<</Length {content.Length}>>stream\n{content}\nendstream endobj\n" +
                     "5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF";
        using var doc = PdfDocument.Open(System.Text.Encoding.ASCII.GetBytes(pdf));
        var original = doc.Render(0);
        (byte, byte, byte) At(RenderedPage image, int x, int y) => (image.Pixels[y * image.Stride + x * 4], image.Pixels[y * image.Stride + x * 4 + 1], image.Pixels[y * image.Stride + x * 4 + 2]);
        var blue = At(original, 200, 792 - 650);
        var grey = At(original, 140, 792 - 440);
        Assert.NotEqual((0, 0, 0), ((int)blue.Item1, (int)blue.Item2, (int)blue.Item3));

        var run = doc.GetTextRuns(0).Single();
        doc.MoveObject(0, run.ObjectIndex, 30, 0); // changes an original object, so the page is rewritten
        using var saved = Reopen(doc);
        var after = saved.Render(0);
        Assert.Equal(blue, At(after, 200, 792 - 650));
        Assert.Equal(grey, At(after, 140, 792 - 440));
        var red = saved.GetTextRuns(0).Single().Color; // CMYK red, converted to RGB
        Assert.True(red.R > 200 && red.G < 60 && red.B < 60, $"expected red text, got {red}");
    }

    /// <summary>A page whose text is drawn directly, plus a group (form XObject) holding a blue box and its own text.</summary>
    internal static byte[] PdfWithGroup()
    {
        const string page = "BT /F1 20 Tf 100 700 Td (Page text) Tj ET /Fm1 Do";
        const string group = "0 0 1 rg 100 500 80 40 re f BT /F1 16 Tf 100 600 Td (Group text) Tj ET";
        string pdf = "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
                     "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>/XObject<</Fm1 6 0 R>>>>>>endobj\n" +
                     $"4 0 obj<</Length {page.Length}>>stream\n{page}\nendstream endobj\n" +
                     "5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\n" +
                     $"6 0 obj<</Type/XObject/Subtype/Form/BBox[0 0 612 792]/Resources<</Font<</F1 5 0 R>>>>/Length {group.Length}>>stream\n{group}\nendstream endobj\n" +
                     "trailer<</Root 1 0 R>>\n%%EOF";
        return System.Text.Encoding.ASCII.GetBytes(pdf);
    }

    [Fact]
    public void Groups_can_be_taken_apart_into_editable_items()
    {
        using var doc = PdfDocument.Open(PdfWithGroup());
        var group = doc.GetGroups(0).Single();
        Assert.Equal(2, group.ItemCount);
        Assert.True(doc.GroupItemAt(0, group.ObjectIndex, 140, 792 - 520));   // on the blue box
        Assert.False(doc.GroupItemAt(0, group.ObjectIndex, 400, 100));        // inside the group's bounds, but on nothing
        Assert.Equal(111.6, doc.GetDrawings(0).Single().Bounds.Height, 0);     // before: the whole group moves as one drawing (box and text)
        var before = doc.Render(0);

        Assert.True(doc.Ungroup(0, group.ObjectIndex, out var problem), problem);
        using var saved = Reopen(doc);
        Assert.Empty(saved.GetGroups(0));
        var box = saved.GetDrawings(0).Single();
        Assert.Equal(new PdfRect(100, 792 - 540, 80, 40), Round(box.Bounds));
        Assert.Contains("Group text", saved.GetPageText(0));                   // a standard font: stays real text
        Assert.Contains("Page text", saved.GetPageText(0));
        Assert.Equal(2, saved.GetTextRuns(0).Count);

        // Nothing moved or changed colour.
        var after = saved.Render(0);
        int changed = 0;
        for (int i = 0; i < before.Pixels.Length; i += 4)
            if (Math.Abs(before.Pixels[i] - after.Pixels[i]) + Math.Abs(before.Pixels[i + 1] - after.Pixels[i + 1]) + Math.Abs(before.Pixels[i + 2] - after.Pixels[i + 2]) > 60) changed++;
        Assert.True(changed < 20, $"{changed} pixels changed");

        // And the parts are ordinary items now.
        saved.MoveObjects(0, box.ObjectIndices, 50, 0);
        Assert.Equal(150, Math.Round(saved.GetDrawings(0).Single().Bounds.X));
        Assert.Throws<ArgumentException>(() => saved.Ungroup(0, 0));           // nothing here is a group any more
    }

    [Fact]
    public void Resizing_an_image_keeps_its_orientation_on_a_rotated_page()
    {
        // A scan stored sideways: the image is placed on the page, then the page is turned, so the
        // picture is shown rotated. Resizing must not turn it back upright.
        using var doc = PdfDocument.Create();
        doc.AddPage(400, 600);
        doc.AddImage(0, Png, x: 100, y: 100, width: 200, height: 200); // red top-left, green top-right, blue bottom-left, white bottom-right
        doc.SetPageRotation(0, 270);
        var image = doc.GetImages(0).Single();
        (int, int, int) Corner(PdfDocument d, PdfRect r, double fx, double fy)
        {
            var page = d.Render(0);
            int i = (int)(r.Y + r.Height * fy) * page.Stride + (int)(r.X + r.Width * fx) * 4;
            return (page.Pixels[i + 2] > 128 ? 1 : 0, page.Pixels[i + 1] > 128 ? 1 : 0, page.Pixels[i] > 128 ? 1 : 0);
        }
        var before = new[] { Corner(doc, image.Bounds, 0.25, 0.25), Corner(doc, image.Bounds, 0.75, 0.25), Corner(doc, image.Bounds, 0.25, 0.75), Corner(doc, image.Bounds, 0.75, 0.75) };
        Assert.NotEqual((1, 0, 0), before[0]);              // turned: red is no longer at the top-left

        var target = new PdfRect(image.Bounds.X + 20, image.Bounds.Y + 30, image.Bounds.Width * 0.5, image.Bounds.Height * 1.5);
        doc.SetImageBounds(0, image.ObjectIndex, target);
        using var saved = Reopen(doc);
        var resized = saved.GetImages(0).Single().Bounds;
        Assert.Equal(Round(target), Round(resized));
        var after = new[] { Corner(saved, resized, 0.25, 0.25), Corner(saved, resized, 0.75, 0.25), Corner(saved, resized, 0.25, 0.75), Corner(saved, resized, 0.75, 0.75) };
        Assert.Equal(before, after);                        // every quarter still shows the same colour
    }

    [Fact]
    public void A_page_break_moves_what_is_below_onto_a_new_page()
    {
        using var doc = PdfDocument.Open(Path.Combine(AppContext.BaseDirectory, "Fixtures", "word-style.pdf"));
        doc.AddPage();                                                          // a page that must end up third
        var paragraphs = doc.GetParagraphs(0);
        var first = paragraphs.First(p => p.Text.StartsWith("Its shell"));     // the break goes above this paragraph
        var footer = paragraphs.First(p => p.Text == "Page 1");
        double topMargin = paragraphs.Min(p => p.Bounds.Y);
        doc.HighlightText(0, "left-coiling");                                   // in the moved part
        doc.HighlightText(0, "garden snail");                                   // in the part that stays
        int images = doc.GetImages(0).Count;

        int moved = doc.InsertPageBreak(0, first.Bounds.Y);
        Assert.True(moved > 5);
        using var saved = Reopen(doc);
        Assert.Equal(3, saved.PageCount);
        Assert.Equal(doc.GetPageSize(0), saved.GetPageSize(1));

        var stay = saved.GetParagraphs(0).Select(p => p.Text).ToList();
        var go = saved.GetParagraphs(1);
        Assert.Contains(stay, t => t.StartsWith("The common garden snail"));
        Assert.Contains("Page 1", stay);                                        // the footer stays
        Assert.DoesNotContain(stay, t => t.StartsWith("Its shell"));
        Assert.StartsWith("Its shell", go[0].Text);
        Assert.Contains(go, p => p.Text.StartsWith("A large snail"));
        Assert.Equal(first.Bounds.X, go[0].Bounds.X, 1);
        Assert.InRange(go[0].Bounds.Y, topMargin - 3, topMargin + 3);           // starts at the top margin (measured at the ink, so within a few points)
        Assert.Equal(2, go[0].LineCount);
        Assert.Equal(footer.Bounds, saved.GetParagraphs(0).First(p => p.Text == "Page 1").Bounds);
        Assert.Equal(images, saved.GetImages(1).Count);
        Assert.Empty(saved.GetImages(0));

        // Each highlight is with its text.
        var stayed = saved.GetAnnotations(0).Single();
        var went = saved.GetAnnotations(1).Single();
        Assert.Equal(saved.FindText(0, "garden snail").First().Rects[0].Y, stayed.Bounds.Y, 0);
        Assert.Equal(saved.FindText(1, "left-coiling").Single().Rects[0].Y, went.Bounds.Y, 0);

        Assert.Equal(0, saved.InsertPageBreak(2, 100));                         // nothing on the blank page: no page added
        Assert.Equal(3, saved.PageCount);
    }

    [Fact]
    public void Images_can_be_rotated_read_and_replaced()
    {
        using var doc = PdfDocument.Create();
        doc.AddPage();
        doc.AddImage(0, Png, x: 100, y: 200, width: 120, height: 60);   // red top-left, green top-right, blue bottom-left, white bottom-right
        var image = doc.GetImages(0).Single();
        (int, int, int) At(PdfDocument d, PdfRect r, double fx, double fy)
        {
            var page = d.Render(0);
            int i = (int)(r.Y + r.Height * fy) * page.Stride + (int)(r.X + r.Width * fx) * 4;
            return (page.Pixels[i + 2] > 128 ? 1 : 0, page.Pixels[i + 1] > 128 ? 1 : 0, page.Pixels[i] > 128 ? 1 : 0);
        }
        Assert.Equal((1, 0, 0), At(doc, image.Bounds, 0.25, 0.25));

        // Pixels come back as stored: 2 x 2, red first (BGRA order).
        var pixels = doc.GetImagePixels(0, image.ObjectIndex);
        Assert.Equal((2, 2), (pixels.Width, pixels.Height));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels.Pixels.Take(4));

        // A quarter turn clockwise: the outline swaps width and height about the same centre,
        // and the red corner goes from top-left to top-right.
        doc.RotateImage(0, image.ObjectIndex, 90);
        var turned = doc.GetImages(0).Single().Bounds;
        Assert.Equal(new PdfRect(130, 170, 60, 120), Round(turned));
        Assert.Equal((1, 0, 0), At(doc, turned, 0.75, 0.25));
        Assert.Equal((0, 0, 1), At(doc, turned, 0.25, 0.25));       // blue was bottom-left, now top-left
        doc.RotateImage(0, image.ObjectIndex, -90);
        Assert.Equal(Round(image.Bounds), Round(doc.GetImages(0).Single().Bounds));
        Assert.Equal((1, 0, 0), At(doc, image.Bounds, 0.25, 0.25));

        // Replaced by an all-blue picture of the same proportions as the stored one (2 x 2 -> 4 x 4),
        // as when an edited copy comes back: the placement does not change.
        byte[] Blue(int w, int h) { var b = new byte[w * h * 4]; for (int i = 0; i < b.Length; i += 4) { b[i] = 255; b[i + 3] = 255; } return b; }
        doc.RotateImage(0, image.ObjectIndex, 90);                  // replace while turned: the turn must be kept
        doc.ReplaceImage(0, image.ObjectIndex, EncodeBmp(Blue(4, 4), 4, 4));
        Assert.Equal(Round(turned), Round(doc.GetImages(0).Single().Bounds));
        Assert.Equal((0, 0, 1), At(doc, turned, 0.5, 0.5));

        // Replaced by a picture of other proportions (4 wide, 8 high): fitted inside the outline,
        // centred, in its true shape. The image is turned, so its width runs down the page.
        doc.ReplaceImage(0, image.ObjectIndex, EncodeBmp(Blue(4, 8), 4, 8));
        using var saved = Reopen(doc);
        var replaced = saved.GetImages(0).Single();
        Assert.Equal((4, 8), (replaced.PixelWidth, replaced.PixelHeight));
        Assert.Equal(new PdfRect(130, 215, 60, 30), Round(replaced.Bounds));
        Assert.Equal((0, 0, 1), At(saved, replaced.Bounds, 0.5, 0.5));
        Assert.Throws<PdfException>(() => saved.ReplaceImage(0, replaced.ObjectIndex, new byte[] { 1, 2, 3 }));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 0)]
    [InlineData(270, 0)]      // a scan stored sideways, turned upright by the page
    [InlineData(180, 90)]     // page and image both turned
    public void Image_pixels_can_be_read_and_replaced_the_way_the_image_is_displayed(int pageRotation, int imageRotation)
    {
        using var doc = PdfDocument.Create();
        doc.AddPage(400, 600);
        doc.AddImage(0, Png, x: 100, y: 100, width: 150, height: 150);  // red, green / blue, white
        doc.SetPageRotation(0, pageRotation);
        doc.RotateImage(0, doc.GetImages(0).Single().ObjectIndex, imageRotation);
        var image = doc.GetImages(0).Single();

        // What the page shows in each quarter of the image...
        (byte, byte, byte) Quarter(RenderedPage page, PdfRect r, int qx, int qy)
        {
            int i = (int)(r.Y + r.Height * (0.25 + 0.5 * qy)) * page.Stride + (int)(r.X + r.Width * (0.25 + 0.5 * qx)) * 4;
            return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]);
        }
        var rendered = doc.Render(0);
        // ...is what the displayed pixels hold, in the same places.
        var shown = doc.GetImagePixels(0, image.ObjectIndex, asDisplayed: true);
        Assert.Equal((2, 2), (shown.Width, shown.Height));
        for (int qy = 0; qy < 2; qy++)
            for (int qx = 0; qx < 2; qx++)
            {
                int i = (qy * 2 + qx) * 4;
                Assert.Equal(Quarter(rendered, image.Bounds, qx, qy), (shown.Pixels[i], shown.Pixels[i + 1], shown.Pixels[i + 2]));
            }

        // Putting those same displayed pixels back changes nothing on the page.
        doc.ReplaceImage(0, image.ObjectIndex, EncodeBmp(shown.Pixels, 2, 2), asDisplayed: true);
        using var saved = Reopen(doc);
        var after = saved.Render(0);
        var bounds = saved.GetImages(0).Single().Bounds;
        Assert.Equal(Round(image.Bounds), Round(bounds));
        for (int qy = 0; qy < 2; qy++)
            for (int qx = 0; qx < 2; qx++)
                Assert.Equal(Quarter(rendered, image.Bounds, qx, qy), Quarter(after, bounds, qx, qy));

        // A new picture with yellow in its top-left quarter is seen with yellow at the top-left.
        byte[] picture = [0, 255, 255, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255];
        saved.ReplaceImage(0, saved.GetImages(0).Single().ObjectIndex, EncodeBmp(picture, 2, 2), asDisplayed: true);
        Assert.Equal(((byte)0, (byte)255, (byte)255), Quarter(saved.Render(0), saved.GetImages(0).Single().Bounds, 0, 0));
    }

    /// <summary>A 32-bit BMP file from top-down BGRA pixels.</summary>
    private static byte[] EncodeBmp(byte[] bgra, int width, int height)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)'B'); w.Write((byte)'M'); w.Write(54 + bgra.Length); w.Write(0); w.Write(54);
        w.Write(40); w.Write(width); w.Write(-height); w.Write((short)1); w.Write((short)32); w.Write(0); w.Write(bgra.Length);
        w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        w.Write(bgra);
        w.Flush();
        return ms.ToArray();
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Vector_drawings_are_handled_the_way_they_are_seen_on_a_rotated_page(int rotation)
    {
        // The RTF sample's table (six bordered cells) on a page that then gets a rotate flag.
        using var doc = PdfDocument.FromRtf(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.rtf")), out _);
        doc.SetPageRotation(0, rotation);
        var shown = doc.GetPageSize(0);
        var table = doc.GetDrawings(0).OrderByDescending(d => d.ObjectIndices.Count).First();
        Assert.Equal(6, table.ObjectIndices.Count);
        Assert.True(table.Bounds.X >= 0 && table.Bounds.Right <= shown.Width && table.Bounds.Y >= 0 && table.Bounds.Bottom <= shown.Height);

        // Moved 12 right and 30 up as seen on screen: that is exactly how its outline shifts.
        doc.MoveObjects(0, table.ObjectIndices, 12, -30);
        using var moved = Reopen(doc);
        var after = moved.GetDrawings(0).OrderByDescending(d => d.ObjectIndices.Count).First();
        Assert.Equal(6, after.ObjectIndices.Count);
        Assert.Equal(table.Bounds.X + 12, after.Bounds.X, 1);
        Assert.Equal(table.Bounds.Y - 30, after.Bounds.Y, 1);
        Assert.Equal(table.Bounds.Width, after.Bounds.Width, 1);
        Assert.Equal(table.Bounds.Height, after.Bounds.Height, 1);

        // A page break above it: same shape and size on the new page, which is turned the same way.
        Assert.True(moved.InsertPageBreak(0, after.Bounds.Y - 1) > 0);
        using var broken = Reopen(moved);
        Assert.Equal(rotation, broken.GetPageRotation(1));
        Assert.Equal(shown, broken.GetPageSize(1));
        var onNext = broken.GetDrawings(1).OrderByDescending(d => d.ObjectIndices.Count).First();
        Assert.Equal(6, onNext.ObjectIndices.Count);
        Assert.Equal(after.Bounds.X, onNext.Bounds.X, 1);
        Assert.Equal(after.Bounds.Width, onNext.Bounds.Width, 1);
        Assert.Equal(after.Bounds.Height, onNext.Bounds.Height, 1);
        Assert.InRange(onNext.Bounds.Y, 17, shown.Height / 4 + 1);     // it starts the new page, at the top margin
        Assert.DoesNotContain(broken.GetDrawings(0), d => d.ObjectIndices.Count == 6);

        // Deleting it removes all six shapes.
        int before = broken.GetDrawings(1).Count;
        broken.RemoveObjects(1, onNext.ObjectIndices);
        Assert.Equal(before - 1, broken.GetDrawings(1).Count);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void A_group_on_a_rotated_page_comes_apart_without_anything_moving(int rotation)
    {
        using var doc = PdfDocument.Open(PdfWithGroup());
        doc.SetPageRotation(0, rotation);
        using var turned = Reopen(doc);
        var before = turned.Render(0);
        Assert.True(turned.Ungroup(0, turned.GetGroups(0).Single().ObjectIndex, out var problem), problem);
        using var saved = Reopen(turned);
        var after = saved.Render(0);
        int changed = 0;
        for (int i = 0; i < before.Pixels.Length; i += 4)
            if (Math.Abs(before.Pixels[i] - after.Pixels[i]) + Math.Abs(before.Pixels[i + 1] - after.Pixels[i + 1]) + Math.Abs(before.Pixels[i + 2] - after.Pixels[i + 2]) > 60) changed++;
        Assert.True(changed < 20, $"{changed} pixels changed");
        var box = saved.GetDrawings(0).Single();
        Assert.True(Math.Abs(box.Bounds.Width * box.Bounds.Height - 80 * 40) < 10, $"the box is {box.Bounds}");
        Assert.Contains("Group text", saved.GetPageText(0));
    }

    [Fact]
    public void A_page_can_be_spread_over_sheets_without_the_file_growing()
    {
        using var doc = PdfDocument.Open(Path.Combine(AppContext.BaseDirectory, "Fixtures", "word-style.pdf"));
        var page = doc.GetPageSize(0);
        byte[] before = doc.ToBytes();

        // A5 sheets, 5 mm blank edge, 10 mm overlap: an A4 page at real size needs two by two.
        var options = new TileOptions { PaperWidth = 419.5, PaperHeight = 595.3 };
        var plan = PdfDocument.PlanTiles(page, options);
        Assert.Equal((2, 2), (plan.Columns, plan.Rows));
        using var tiles = doc.CreateTiles(0, options);
        Assert.Equal(4, tiles.PageCount);
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(419.5, tiles.GetPageSize(i).Width, 0.1));
        // The page is stored once, whatever the number of sheets.
        byte[] saved = tiles.ToBytes();
        Assert.True(saved.Length < before.Length * 1.3, $"{before.Length} bytes became {saved.Length}");
        Assert.Equal(1, doc.PageCount); // the document itself is untouched
        Assert.Equal(page, doc.GetPageSize(0));

        // Shrunk to fit two sheets side by side, it takes exactly those two.
        var fitted = options with { Scale = PdfDocument.TileScaleToFit(page, options, 2, 1) };
        Assert.Equal((2, 1), (PdfDocument.PlanTiles(page, fitted).Columns, PdfDocument.PlanTiles(page, fitted).Rows));

        // What the sheets show together is the page: the title is on the top-left sheet at full size.
        using var reopened = PdfDocument.Open(saved);
        Assert.Contains("Awkward Snail", reopened.GetPageText(0) + reopened.GetPageText(1));
        Assert.Contains("Row 2, column 1", reopened.GetPageText(2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Tiles_show_a_turned_page_the_way_it_is_seen(int rotation)
    {
        using var doc = PdfDocument.Open(Path.Combine(AppContext.BaseDirectory, "Fixtures", "word-style.pdf"));
        doc.SetPageRotation(0, rotation);
        var page = doc.GetPageSize(0);
        // One sheet exactly the size of the page, nothing added: it must look like the page.
        using var tiles = doc.CreateTiles(0, new TileOptions { PaperWidth = page.Width, PaperHeight = page.Height, Margin = 0, Overlap = 0, CutMarks = false });
        Assert.Equal(1, tiles.PageCount);
        var expected = doc.Render(0, 1, false);
        var actual = tiles.Render(0, 1, false);
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        int changed = 0;
        for (int i = 0; i < expected.Pixels.Length; i += 4)
            if (Math.Abs(expected.Pixels[i] - actual.Pixels[i]) + Math.Abs(expected.Pixels[i + 1] - actual.Pixels[i + 1]) + Math.Abs(expected.Pixels[i + 2] - actual.Pixels[i + 2]) > 120) changed++;
        Assert.True(changed < expected.Width * expected.Height / 200, $"{changed} pixels differ");
    }

    /// <summary>
    /// A PDF with a proper cross-reference table and two fonts, F1 and F2, each drawing one word.
    /// Both fonts carry (dummy) font data of their own unless told otherwise.
    /// </summary>
    internal static byte[] PdfWithTwoFonts(string firstName, string secondName, bool firstEmbedded = true)
    {
        const string content = "BT /F1 20 Tf 72 700 Td (Hello) Tj ET BT /F2 20 Tf 72 650 Td (World) Tj ET";
        var objects = new List<string>
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R/Resources<</Font<</F1 5 0 R/F2 6 0 R>>>>>>",
            $"<</Length {content.Length}>>stream\n{content}\nendstream",
            $"<</Type/Font/Subtype/TrueType/BaseFont/{firstName}/Encoding/WinAnsiEncoding{(firstEmbedded ? "/FontDescriptor 7 0 R" : "")}>>",
            $"<</Type/Font/Subtype/TrueType/BaseFont/{secondName}/Encoding/WinAnsiEncoding/FontDescriptor 8 0 R>>",
            $"<</Type/FontDescriptor/FontName/{firstName}/Flags 32/FontBBox[0 0 1000 1000]/ItalicAngle 0/Ascent 800/Descent -200/CapHeight 700/StemV 80/FontFile2 9 0 R>>",
            $"<</Type/FontDescriptor/FontName/{secondName}/Flags 32/FontBBox[0 0 1000 1000]/ItalicAngle 0/Ascent 800/Descent -200/CapHeight 700/StemV 80/FontFile2 10 0 R>>",
            "<</Length 5>>stream\nfont1\nendstream",
            "<</Length 5>>stream\nfont2\nendstream",
        };
        var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<</Size {objects.Count + 1}/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>The fonts of the two words, top first, without the six-letter prefix of a partial font.</summary>
    internal static string[] FontNames(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return doc.GetTextRuns(0).OrderBy(r => r.Bounds.Y).Select(r => r.FontName.Length > 7 && r.FontName[6] == '+' ? r.FontName[7..] : r.FontName).ToArray();
    }

    [Fact]
    public void Embedded_fonts_that_share_a_name_are_given_their_own_names_on_opening()
    {
        // Two cuts of one font, told apart only by the prefix that PDFium drops.
        Assert.Equal(["Helvetica", "Helvetica-2"], FontNames(PdfWithTwoFonts("AAAAAB+Helvetica", "AAAAAF+Helvetica")));
        // A font the file does not carry is drawn by name, so it keeps the name and the embedded one changes.
        Assert.Equal(["Helvetica", "Helvetica-2"], FontNames(PdfWithTwoFonts("Helvetica", "AAAAAF+Helvetica", firstEmbedded: false)));
    }

    [Fact]
    public void A_file_without_clashing_fonts_is_opened_as_it_is()
    {
        byte[] pdf = PdfWithTwoFonts("AAAAAB+Helvetica", "AAAAAF+Arial");
        Assert.Same(pdf, PdfEditor.Core.Preflight.DistinctFontNames.Apply(pdf));
        Assert.Equal(["Helvetica", "Arial"], FontNames(pdf));
        // Files the reader cannot follow come back untouched as well.
        byte[] noTable = HandMadePdf("0 0 612 792", "0 0 1 rg 50 550 300 200 re f");
        Assert.Same(noTable, PdfEditor.Core.Preflight.DistinctFontNames.Apply(noTable));
        byte[] junk = [1, 2, 3, 4];
        Assert.Same(junk, PdfEditor.Core.Preflight.DistinctFontNames.Apply(junk));
    }

    private static byte[] HandMadePdf(string mediaBox, string content, string pageExtras = "") =>
        System.Text.Encoding.ASCII.GetBytes(
            "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
            $"3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[{mediaBox}]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>{pageExtras}>>endobj\n" +
            $"4 0 obj<</Length {content.Length}>>stream\n{content}\nendstream endobj\n" +
            "5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF");

    [Fact]
    public void A_clipped_item_keeps_its_clip_when_it_moves()
    {
        // A blue rectangle seen through a smaller clip window. PDFium moves an object and its clip
        // separately; moved without its clip, the rectangle would slide out from behind the window and vanish.
        using var doc = PdfDocument.Open(HandMadePdf("0 0 612 792", "q 100 600 200 100 re W n 0 0 1 rg 50 550 300 200 re f Q"));
        bool Blue(PdfDocument d, int x, int y) { var r = d.Render(0); int i = y * r.Stride + x * 4; return r.Pixels[i] > 200 && r.Pixels[i + 2] < 60; }
        Assert.True(Blue(doc, 200, 792 - 650));
        doc.MoveObjects(0, doc.GetDrawings(0).Single().ObjectIndices, 250, 40);
        using var saved = Reopen(doc);
        Assert.True(Blue(saved, 450, 792 - 650 + 40), "the visible window moved with the rectangle");
        Assert.False(Blue(saved, 200, 792 - 650), "nothing is left behind");
        Assert.False(Blue(saved, 330, 792 - 650 + 40), "and it is still clipped to the window, not shown whole");
    }

    [Fact]
    public void A_long_page_can_be_cut_in_two()
    {
        // A page two metres long whose box does not start at the origin, with a web link low down.
        const string link = "/Annots[<</Type/Annot/Subtype/Link/Rect[150 590 260 625]/Border[0 0 0]/A<</S/URI/URI(https://example.com/)>>>>]";
        using var doc = PdfDocument.Open(HandMadePdf("100 100 500 2100",
            "BT /F1 20 Tf 150 1900 Td (Top text) Tj ET BT /F1 20 Tf 150 1300 Td (Middle text) Tj ET BT /F1 20 Tf 150 600 Td (Low text) Tj ET 1 0 0 rg 150 400 100 50 re f", link));
        Assert.Equal(new PdfSize(400, 2000), doc.GetPageSize(0));
        var runs = doc.GetTextRuns(0);
        var middle = runs.Single(r => r.Text == "Middle text");
        var low = runs.Single(r => r.Text == "Low text");
        var box = doc.GetDrawings(0).Single();

        // The places where it can be cut are the gaps between the rows of content.
        var gaps = doc.GetBreakPositions(0);
        Assert.Equal(3, gaps.Count);
        Assert.InRange(gaps[0], runs.Single(r => r.Text == "Top text").Bounds.Bottom, middle.Bounds.Y);
        Assert.InRange(gaps[1], middle.Bounds.Bottom, low.Bounds.Y);

        double cut = gaps[0];
        Assert.Equal(3, doc.SplitPage(0, cut));
        using var saved = Reopen(doc);
        Assert.Equal(2, saved.PageCount);
        Assert.Equal(400, saved.GetPageSize(0).Width, 0);
        Assert.Equal(cut, saved.GetPageSize(0).Height, 0);              // the first page ends at the cut
        Assert.Equal(2000 - cut, saved.GetPageSize(1).Height, 0);       // the second holds the rest, no taller
        Assert.Equal("Top text", saved.GetTextRuns(0).Single().Text);
        Assert.Empty(saved.GetDrawings(0));

        // Everything below is the same distance from the cut as before, and no further left or right.
        var after = saved.GetTextRuns(1);
        var middleAfter = after.Single(r => r.Text == "Middle text");
        var lowAfter = after.Single(r => r.Text == "Low text");
        Assert.Equal(middle.Bounds.X, middleAfter.Bounds.X, 1);
        Assert.Equal(middle.Bounds.Y - cut, middleAfter.Bounds.Y, 1);
        Assert.Equal(low.Bounds.Y - cut, lowAfter.Bounds.Y, 1);
        var boxAfter = saved.GetDrawings(1).Single();
        Assert.Equal(new PdfRect(box.Bounds.X, box.Bounds.Y - cut, box.Bounds.Width, box.Bounds.Height), Round(boxAfter.Bounds) with { Y = Math.Round(boxAfter.Bounds.Y, 3) }, new RectComparer());

        // The link went with its text and still covers it.
        Assert.Empty(saved.GetAnnotations(0));
        var moved = saved.GetAnnotations(1).Single();
        Assert.True(moved.Bounds.Contains(lowAfter.Bounds.X + 5, lowAfter.Bounds.Y + lowAfter.Bounds.Height / 2), $"link at {moved.Bounds}, text at {lowAfter.Bounds}");

        Assert.Equal(0, saved.SplitPage(0, 5000));                      // outside the page: nothing happens
        Assert.Equal(2, saved.PageCount);
    }

    private sealed class RectComparer : IEqualityComparer<PdfRect>
    {
        public bool Equals(PdfRect a, PdfRect b) => Math.Abs(a.X - b.X) < 1 && Math.Abs(a.Y - b.Y) < 1 && Math.Abs(a.Width - b.Width) < 1 && Math.Abs(a.Height - b.Height) < 1;
        public int GetHashCode(PdfRect r) => 0;
    }

    [Fact]
    public void Bad_input_gives_clear_errors()
    {
        Assert.Throws<PdfException>(() => PdfDocument.Open(new byte[] { 1, 2, 3, 4 }));
        using var doc = PdfDocument.Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.AddText(0, 0, 0, "no page yet"));
        doc.AddPage();
        Assert.Throws<ArgumentException>(() => doc.AddHighlight(0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.EditText(0, 3, "x"));
    }

    private static PdfRect Round(PdfRect r) => new(Math.Round(r.X), Math.Round(r.Y), Math.Round(r.Width), Math.Round(r.Height));
}
