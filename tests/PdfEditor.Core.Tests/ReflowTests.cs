using PdfEditor.Core;
using PdfEditor.Core.Interop;
using Xunit;

namespace PdfEditor.Core.Tests;

/// <summary>
/// Paragraph detection and reflow, run against PDFs made the way real documents are made
/// (see Fixtures/make_fixtures.py): a Word-style document exported by LibreOffice with
/// subset-embedded fonts, and a two-column reportlab document justified with word spacing.
/// </summary>
public unsafe class ReflowTests
{
    private static PdfDocument Fixture(string name) => PdfDocument.Open(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".pdf"));

    private static PdfDocument Reopen(PdfDocument doc) => PdfDocument.Open(doc.ToBytes());

    private static TextParagraph Find(PdfDocument doc, string startsWith, int page = 0) =>
        doc.GetParagraphs(page).Single(p => p.Text.TrimStart().StartsWith(startsWith, StringComparison.Ordinal));

    /// <summary>Where every character on page 1 is drawn: text, baseline-start x and y in PDF user space.</summary>
    private static List<(char Ch, double X, double Y)> Glyphs(PdfDocument doc)
    {
        using var page = doc.OpenPage(0);
        var textPage = Native.FPDFText_LoadPage(page.Handle);
        var list = new List<(char, double, double)>();
        for (int i = 0; i < Native.FPDFText_CountChars(textPage); i++)
        {
            if (Native.FPDFText_IsGenerated(textPage, i) == 1) continue;
            double x, y;
            Native.FPDFText_GetCharOrigin(textPage, i, &x, &y);
            list.Add(((char)Native.FPDFText_GetUnicode(textPage, i), x, y));
        }
        Native.FPDFText_ClosePage(textPage);
        return list;
    }

    /// <summary>The glyphs from the first occurrence of some text onwards.</summary>
    private static List<(char Ch, double X, double Y)> From(List<(char Ch, double X, double Y)> glyphs, string text)
    {
        string all = new(glyphs.Select(g => g.Ch).ToArray());
        int at = all.IndexOf(text, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{text}' not found");
        return glyphs.Skip(at).ToList();
    }

    // ------------------------------------------------------------------ detection

    [Fact]
    public void Finds_paragraphs_with_their_alignment_and_formatting()
    {
        using var doc = Fixture("word-style");
        var paragraphs = doc.GetParagraphs(0);

        Assert.Equal("The Awkward Snail", paragraphs[0].Text);
        Assert.Equal(TextAlignment.Center, paragraphs[0].Alignment);
        Assert.Equal(18, paragraphs[0].FontSize, 1);

        var intro = paragraphs[1];
        Assert.StartsWith("The common garden snail moves", intro.Text);
        Assert.EndsWith("drying out.", intro.Text);
        Assert.Equal(TextAlignment.Justify, intro.Alignment);
        Assert.Equal(4, intro.LineCount);
        Assert.Equal(11, intro.FontSize, 1);
        Assert.Equal(["garden snail"], intro.Spans.Where(s => s.Bold).Select(s => intro.Text.Substring(s.Start, s.Length)));
        Assert.Equal(["razor"], intro.Spans.Where(s => s.Italic).Select(s => intro.Text.Substring(s.Start, s.Length)));

        Assert.Equal(TextAlignment.Left, Find(doc, "Its shell").Alignment);   // first-line indent, ragged right
        Assert.Equal(2, Find(doc, "Its shell").LineCount);
        Assert.Equal(1, Find(doc, "Snails eat").LineCount);
        Assert.Contains("7 cm2 of ground", Find(doc, "A large snail").Text);  // superscript stays in its paragraph

        var bullet = paragraphs.Single(p => p.Text.Contains("seal the shell"));
        Assert.EndsWith("stay inside for weeks.", bullet.Text);                // wrapped list item is one paragraph
        Assert.Equal(TextAlignment.Left, bullet.Alignment);
        Assert.Equal(TextAlignment.Center, Find(doc, "Page 1").Alignment);
        Assert.All(paragraphs, p => Assert.True(p.IsEditable));
    }

    [Fact]
    public void Finds_columns_as_separate_paragraphs()
    {
        using var doc = Fixture("two-columns");
        var paragraphs = doc.GetParagraphs(0);
        Assert.Equal(5, paragraphs.Count);
        Assert.Equal(8, paragraphs[0].LineCount);
        Assert.Equal(TextAlignment.Justify, paragraphs[0].Alignment);
        Assert.StartsWith("Right column.", paragraphs[3].Text);
        Assert.True(paragraphs[3].Bounds.X > paragraphs[0].Bounds.Right);
        Assert.Equal(13, paragraphs[0].LineSpacing, 1);
    }

    // ------------------------------------------------------------------ editing

    [Fact]
    public void Inserted_text_rewraps_the_paragraph_and_keeps_formatting_and_fonts()
    {
        using var doc = Fixture("word-style");
        var intro = Find(doc, "The common");
        string text = intro.Text.Replace("garden snail moves", "garden snail, a creature famous for its unhurried manner and its spiral home, moves");
        var result = doc.ReplaceParagraphText(0, intro.Index, text);

        Assert.False(result.UsedSubstituteFont); // every new letter is in the embedded regular font
        using var saved = Reopen(doc);
        var after = Find(saved, "The common");
        Assert.Equal(text, after.Text);
        Assert.Equal(TextAlignment.Justify, after.Alignment);
        Assert.Equal(intro.Bounds.X, after.Bounds.X, 1);
        Assert.Equal(intro.Bounds.Y, after.Bounds.Y, 1);
        Assert.InRange(after.Bounds.Right, intro.Bounds.Right - 1, intro.Bounds.Right + 1);
        Assert.Equal(["garden snail"], after.Spans.Where(s => s.Bold).Select(s => after.Text.Substring(s.Start, s.Length)));
        Assert.Equal(["razor"], after.Spans.Where(s => s.Italic).Select(s => after.Text.Substring(s.Start, s.Length)));
        Assert.All(after.Spans, s => Assert.Contains("LiberationSerif", s.FontName));
        Assert.Equal(intro.LineSpacing, after.LineSpacing, 1);
        Assert.Equal(doc.GetParagraphs(0).Count, saved.GetParagraphs(0).Count);
    }

    [Fact]
    public void Growing_a_paragraph_pushes_the_content_below_and_leaves_the_rest_exactly_in_place()
    {
        using var doc = Fixture("word-style");
        var before = doc.GetParagraphs(0);
        var title = before[0];
        var footer = Find(doc, "Page 1");
        var image = doc.GetImages(0).Single();
        var intro = Find(doc, "The common");

        var result = doc.ReplaceParagraphText(0, intro.Index, intro.Text + " After a heavy shower, dozens may appear on a single path within minutes, all heading somewhere with great determination and no apparent plan.");
        Assert.True(result.LineCount > intro.LineCount);
        Assert.Equal((result.LineCount - intro.LineCount) * intro.LineSpacing, result.HeightChange, 1);

        using var saved = Reopen(doc);
        var after = saved.GetParagraphs(0);
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(title.Bounds, after[0].Bounds);                                    // above: untouched
        Assert.Equal(footer.Bounds, Find(saved, "Page 1").Bounds);                      // footer: stays put
        foreach (var (old, now) in before.Zip(after).Skip(2).Where(p => p.First.Text != "Page 1"))
        {
            Assert.Equal(old.Text, now.Text);
            Assert.Equal(old.Bounds.Y + result.HeightChange, now.Bounds.Y, 1);          // below: pushed down
            Assert.Equal(old.Bounds.X, now.Bounds.X, 1);
        }
        Assert.Equal(image.Bounds.Y + result.HeightChange, saved.GetImages(0).Single().Bounds.Y, 1);
    }

    [Fact]
    public void Shrinking_a_paragraph_pulls_its_column_up_and_keeps_the_other_column_and_its_spacing()
    {
        using var doc = Fixture("two-columns");
        var glyphsBefore = Glyphs(doc);
        var paragraphs = doc.GetParagraphs(0);
        var result = doc.ReplaceParagraphText(0, 0, "Short now.");
        Assert.Equal(1, result.LineCount);
        Assert.Equal(-7 * 13, result.HeightChange, 1);

        using var saved = Reopen(doc);
        var after = saved.GetParagraphs(0);
        Assert.Equal("Short now.", after[0].Text);
        Assert.Equal(paragraphs[1].Bounds.Y - 91, after[1].Bounds.Y, 1);
        Assert.Equal(paragraphs[1].Text, after[1].Text);
        Assert.Equal(TextAlignment.Justify, after[1].Alignment); // its word spacing survived the page being rewritten

        // Every character of the right column, and of the moved paragraphs, is exactly where it was (allowing for the move).
        var glyphsAfter = Glyphs(saved);
        double split = paragraphs[3].Bounds.X - 5;
        var rightBefore = glyphsBefore.Where(g => g.X > split).ToList();
        var rightAfter = glyphsAfter.Where(g => g.X > split).ToList();
        Assert.Equal(rightBefore.Select(g => g.Ch), rightAfter.Select(g => g.Ch));
        Assert.All(rightBefore.Zip(rightAfter), p => Assert.True(Math.Abs(p.First.X - p.Second.X) < 0.15 && Math.Abs(p.First.Y - p.Second.Y) < 0.15));
        var movedBefore = From(glyphsBefore.Where(g => g.X < split).ToList(), "Its shell");
        var movedAfter = From(glyphsAfter.Where(g => g.X < split).ToList(), "Its shell");
        Assert.Equal(movedBefore.Select(g => g.Ch), movedAfter.Select(g => g.Ch));
        Assert.All(movedBefore.Zip(movedAfter), p => Assert.True(Math.Abs(p.First.X - p.Second.X) < 0.15 && Math.Abs(p.First.Y + 91 - p.Second.Y) < 0.15));
    }

    [Fact]
    public void A_paragraph_edited_to_a_full_line_stays_separate_from_the_next()
    {
        // A full-width last line no longer shows that the paragraph ended; the gap to the next one still does.
        using var doc = Fixture("word-style");
        int count = doc.GetParagraphs(0).Count;
        var p = Find(doc, "Snails eat");
        doc.ReplaceParagraphText(0, p.Index, "Snails eat leaves, fungi, moss and decaying plants, scraping their food with a ribbon of tiny teeth.");
        var after = doc.GetParagraphs(0);
        Assert.Equal(count, after.Count);
        Assert.Equal(1, Find(doc, "Snails eat").LineCount);
        Assert.Contains(after, x => x.Text.StartsWith("A large snail"));
    }

    [Fact]
    public void Characters_missing_from_an_embedded_subset_are_drawn_with_a_look_alike()
    {
        using var doc = Fixture("word-style");
        var p = Find(doc, "Snails eat");
        var result = doc.ReplaceParagraphText(0, p.Index, "Snails eat leaves in the café garden — and in Москва too.");
        Assert.True(result.UsedSubstituteFont);
        using var saved = Reopen(doc);
        Assert.Equal("Snails eat leaves in the café garden — and in Москва too.", Find(saved, "Snails eat").Text);
    }

    [Fact]
    public void Part_of_a_paragraph_can_be_replaced_or_reformatted()
    {
        using var doc = Fixture("word-style");
        var p = Find(doc, "Snails eat");
        int at = p.Text.IndexOf("fungi", StringComparison.Ordinal);
        doc.ReplaceInParagraph(0, p.Index, at, "fungi".Length, "mushrooms");
        p = Find(doc, "Snails eat");
        Assert.Equal("Snails eat leaves, mushrooms and decaying plants, scraping food with a ribbon of tiny teeth.", p.Text);

        at = p.Text.IndexOf("ribbon", StringComparison.Ordinal);
        doc.FormatParagraphText(0, p.Index, at, "ribbon".Length, new TextFormat { Bold = true, Color = new PdfColor(200, 0, 0) });
        using var saved = Reopen(doc);
        p = Find(saved, "Snails eat");
        var bold = p.Spans.Single(s => s.Bold);
        Assert.Equal("ribbon", p.Text.Substring(bold.Start, bold.Length));
        Assert.Equal(new PdfColor(200, 0, 0), bold.Color);
        Assert.Equal(11, bold.FontSize, 1);
        Assert.Equal(p.Text.Length - "ribbon".Length, p.Spans.Where(s => !s.Bold).Sum(s => s.Length));

        doc.FormatParagraphText(0, Find(doc, "Snails eat").Index, 0, "Snails".Length, new TextFormat { Size = 16 });
        Assert.Contains(Find(doc, "Snails eat").Spans, s => s.FontSize == 16 && s.Length == "Snails".Length);
    }

    [Fact]
    public void Alignment_can_be_changed_and_line_breaks_can_be_forced()
    {
        using var doc = Fixture("word-style");
        var p = Find(doc, "Its shell");
        doc.SetParagraphAlignment(0, p.Index, TextAlignment.Center);
        Assert.Equal(TextAlignment.Center, Find(doc, "Its shell").Alignment);

        // A forced line break draws a new line. (A PDF does not record why a line ended, so reading
        // the page back gives one paragraph of two lines.)
        var q = Find(doc, "Snails eat");
        var result = doc.ReplaceParagraphText(0, q.Index, "First line\nSecond line");
        Assert.Equal(2, result.LineCount);
        var runs = doc.GetTextRuns(0);
        // ("F" is drawn on its own: the embedded subset has no capital F, so a look-alike font supplies it.)
        var first = runs.Single(r => r.Text.Trim() == "F");
        var second = runs.Single(r => r.Text.Trim() == "Second");
        Assert.InRange(second.Bounds.X - first.Bounds.X, -1, 1);
        Assert.InRange(second.Bounds.Bottom - first.Bounds.Bottom, q.LineSpacing - 0.5, q.LineSpacing + 0.5);
    }

    [Fact]
    public void Empty_text_removes_the_paragraph_and_closes_the_gap()
    {
        using var doc = Fixture("two-columns");
        var before = doc.GetParagraphs(0);
        doc.ReplaceParagraphText(0, 1, "");
        var after = doc.GetParagraphs(0);
        Assert.Equal(before.Count - 1, after.Count);
        Assert.Equal(before[2].Text, after[1].Text);
        Assert.Equal(before[1].Bounds.Y, after[1].Bounds.Y, 1);
    }

    [Fact]
    public void Find_and_replace_works_across_the_document_and_reflows()
    {
        using var doc = Fixture("word-style");
        int expected = doc.GetParagraphs(0).Sum(p => System.Text.RegularExpressions.Regex.Matches(p.Text, "snail", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count);
        int replaced = doc.ReplaceText("snail", "slug");
        Assert.Equal(expected, replaced);
        using var saved = Reopen(doc);
        var texts = saved.GetParagraphs(0).Select(p => p.Text).ToList();
        Assert.DoesNotContain(texts, t => t.Contains("snail", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("The Awkward slug", texts);
        Assert.Contains(texts, t => t.StartsWith("The common garden slug moves"));
    }

    [Fact]
    public void Highlights_below_an_edit_move_with_their_text()
    {
        using var doc = Fixture("word-style");
        var match = doc.FindText(0, "left-coiling").Single();
        doc.AddHighlight(0, match.Rects);
        using var annotated = Reopen(doc);
        var intro = Find(annotated, "The common");
        var result = annotated.ReplaceParagraphText(0, intro.Index, intro.Text + " Rain brings them out in great numbers, sometimes covering whole paths and walls in a single evening.");
        Assert.True(result.HeightChange > 0);

        using var saved = Reopen(annotated);
        var highlight = saved.GetAnnotations(0).Single();
        var moved = saved.FindText(0, "left-coiling").Single().Rects[0];
        Assert.Equal(moved.Y, highlight.Bounds.Y, 0);
        Assert.Equal(match.Rects[0].Y + result.HeightChange, moved.Y, 1);
    }

    [Fact]
    public void Editing_or_moving_existing_items_does_not_shift_other_text()
    {
        // reportlab justifies with word spacing, which PDFium loses when it rewrites a page's content.
        using var doc = Fixture("two-columns");
        var before = Glyphs(doc);
        var run = doc.GetTextRuns(0)[0];
        doc.MoveObject(0, run.ObjectIndex, 0, 0);
        using var saved = Reopen(doc);
        var after = Glyphs(saved);
        Assert.Equal(before.Select(g => g.Ch), after.Select(g => g.Ch));
        Assert.All(before.Zip(after), p => Assert.True(Math.Abs(p.First.X - p.Second.X) < 0.15 && Math.Abs(p.First.Y - p.Second.Y) < 0.15,
            $"'{p.First.Ch}' moved from ({p.First.X:0.##},{p.First.Y:0.##}) to ({p.Second.X:0.##},{p.Second.Y:0.##})"));
    }

    [Fact]
    public void Text_added_by_the_editor_can_be_reflowed_too()
    {
        using var doc = PdfDocument.Create();
        doc.AddPage();
        doc.AddText(0, 72, 72, "This paragraph was typed into the editor\nand spans two lines of text.", new TextStyle { Size = 12 });
        var p = doc.GetParagraphs(0).Single();
        Assert.Equal("This paragraph was typed into the editor and spans two lines of text.", p.Text);
        var result = doc.ReplaceParagraphText(0, 0, p.Text + " Now it is longer, so the words have to find new lines to sit on, and the paragraph grows downwards.");
        Assert.False(result.UsedSubstituteFont);
        var after = doc.GetParagraphs(0).Single();
        Assert.Equal(p.Bounds.X, after.Bounds.X, 1);
        Assert.Equal(p.Bounds.Y, after.Bounds.Y, 1);
        Assert.True(after.LineCount >= 3);
        Assert.Equal(14.4, after.LineSpacing, 1);
        Assert.True(after.Bounds.Right <= p.Bounds.Right + 1, "lines must not grow wider than the paragraph was");
    }

    [Fact]
    public void Hyphenated_words_are_rejoined()
    {
        using var doc = PdfDocument.Create();
        doc.AddPage();
        doc.AddText(0, 72, 72, "The snail moved with remark-\nable patience across the stone.", new TextStyle { Size = 12 });
        Assert.Equal("The snail moved with remarkable patience across the stone.", doc.GetParagraphs(0).Single().Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Text_on_a_page_with_a_rotate_flag_is_edited_the_way_it_is_seen(int rotation)
    {
        // Like a scan with an OCR text layer: the page carries a rotate flag, and its text is
        // stored turned so that it reads upright on screen.
        using var doc = PdfDocument.Create();
        doc.AddPage(400, 600);
        doc.SetPageRotation(0, rotation);
        var shown = doc.GetPageSize(0);
        doc.AddText(0, 50, 60, "The snail crossed the garden path at its own\nsteady pace and reached the far wall by noon.", new TextStyle { Size = 11 });
        doc.AddText(0, 50, 200, "A second paragraph further down the page.", new TextStyle { Size = 11 });
        using var start = Reopen(doc);
        var before = start.GetParagraphs(0);
        Assert.Equal(2, before.Count);
        var first = before[0];
        Assert.Equal(2, first.LineCount);
        Assert.InRange(first.Bounds.X, 49, 52);

        void AssertUpright(PdfDocument d, int page)
        {
            Assert.All(d.GetTextRuns(page).Where(r => r.Text.Trim().Length > 3), r => Assert.True(r.Bounds.Width > r.Bounds.Height, $"'{r.Text}' is not horizontal: {r.Bounds}"));
            Assert.All(d.GetParagraphs(page), p => Assert.True(p.Bounds.X >= 0 && p.Bounds.Right <= d.GetPageSize(page).Width + 1 && p.Bounds.Y >= 0 && p.Bounds.Bottom <= d.GetPageSize(page).Height + 1, $"off the page: {p.Bounds}"));
        }

        // Reflow: more words, some in a script the font lacks (so a substitute font is drawn turned too).
        var result = start.ReplaceParagraphText(0, first.Index, first.Text + " It rested there, in the shade, until the evening rain, and then set off for Москва.");
        Assert.True(result.UsedSubstituteFont);
        using var reflowed = Reopen(start);
        var after = reflowed.GetParagraphs(0);
        Assert.Equal(2, after.Count);
        Assert.Contains("Москва", after[0].Text);
        Assert.True(after[0].LineCount > 2);
        Assert.Equal(first.Bounds.X, after[0].Bounds.X, 0);
        Assert.Equal(first.Bounds.Y, after[0].Bounds.Y, 0);
        Assert.Equal(before[1].Bounds.Y + result.HeightChange, after[1].Bounds.Y, 0);   // pushed straight down the screen
        Assert.Equal(before[1].Bounds.X, after[1].Bounds.X, 0);
        AssertUpright(reflowed, 0);

        // A single run edited and moved.
        var run = reflowed.GetTextRuns(0).First(r => r.Text.Contains("second"));
        reflowed.EditText(0, run.ObjectIndex, "A second paragraph, edited.");
        run = reflowed.GetTextRuns(0).First(r => r.Text.Contains("edited"));
        reflowed.MoveObject(0, run.ObjectIndex, 30, 10);
        var moved = reflowed.GetTextRuns(0).First(r => r.Text.Contains("edited"));
        Assert.Equal(run.Bounds.X + 30, moved.Bounds.X, 0);
        Assert.Equal(run.Bounds.Y + 10, moved.Bounds.Y, 0);
        AssertUpright(reflowed, 0);

        // A page break: the new page looks the same way up, with the text upright on it.
        Assert.True(reflowed.InsertPageBreak(0, moved.Bounds.Y - 1) > 0);
        using var broken = Reopen(reflowed);
        Assert.Equal(2, broken.PageCount);
        Assert.Equal(shown, broken.GetPageSize(1));
        Assert.Equal(rotation, broken.GetPageRotation(1));
        Assert.Contains("edited", broken.GetPageText(1));
        Assert.DoesNotContain("edited", broken.GetPageText(0));
        AssertUpright(broken, 0);
        AssertUpright(broken, 1);
        Assert.InRange(broken.GetTextRuns(1).First().Bounds.Y, 0, shown.Height / 3);
    }

    [Fact]
    public void Bad_requests_give_clear_errors()
    {
        using var doc = Fixture("word-style");
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.ReplaceParagraphText(0, 99, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.ReplaceInParagraph(0, 0, 5, 500, "x"));
        Assert.Throws<ArgumentException>(() => doc.ReplaceText("", "x"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_paragraph_growing_inside_a_box_leaves_what_is_below_where_it_is(bool boxed)
    {
        using var made = PdfDocument.Create();
        made.AddPage(595, 842);
        made.AddText(0, 100, 200, "The first line of a short paragraph that was wrapped", new TextStyle { Size = 12 });
        made.AddText(0, 100, 215, "extraordinarily early.", new TextStyle { Size = 12 });
        made.AddText(0, 100, 260, "A separate note below", new TextStyle { Size = 12 });
        if (boxed) made.AddShape(0, ShapeKind.Box, [new PdfPoint(85, 180), new PdfPoint(420, 290)], new ShapeStyle { Stroke = new PdfColor(0, 0, 0), Width = 1 });
        using var doc = Reopen(made);
        var paragraph = Find(doc, "The first line");
        double noteWas = doc.GetTextRuns(0).Single(r => r.Text.StartsWith("A separate")).Bounds.Y;

        var result = doc.ReplaceParagraphText(0, paragraph.Index, paragraph.Text + " And then it was given a good many more words, enough of them to need two or three lines more than it had.");
        Assert.True(result.LineCount > 2);
        Assert.True(result.HeightChange > 10);
        using var saved = Reopen(doc);
        double noteNow = saved.GetTextRuns(0).Single(r => r.Text.StartsWith("A separate")).Bounds.Y;
        if (boxed)
        {
            // Moving the note would leave the box behind: everything stays, and the result says so.
            Assert.True(result.LeftInPlace);
            Assert.Equal(0, result.MovedItems);
            Assert.Equal(noteWas, noteNow, 0.01);
        }
        else
        {
            Assert.False(result.LeftInPlace);
            Assert.Equal(noteWas + result.HeightChange, noteNow, 0.5);
        }
    }
}