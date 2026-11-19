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
    private static byte[] PdfWithGroup()
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
