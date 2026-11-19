# PDFeditor

A PDF editor in C# for The Awkward Snail.

## Layout

- `src/PdfEditor.Core` – the editing engine (no UI), including paragraph reflow (`Reflow/`) and RTF import (`Rtf/`). Built on [PDFium](https://pdfium.googlesource.com/pdfium/) (BSD/Apache licensed) through its native packages for Windows, Linux and macOS.
- `src/PdfEditor.App` – the Windows desktop editor (WPF). The whole editor lives in `PdfEditorControl`, so it can be dropped into another WPF window; `MainWindow` is a thin shell around it.
- `tests/PdfEditor.Core.Tests` – xUnit tests for the engine.

## Editing paragraphs with reflow

A PDF has no paragraphs, only characters placed at positions. `PdfEditor.Core` works the paragraphs out from where the characters sit, and can rewrite one so that its words wrap again, the way a word processor would.

![A Word-style page before and after five paragraph edits](docs/reflow-before-after.png)

```csharp
using var doc = PdfDocument.Open("in.pdf");

var paragraph = doc.FindParagraphAt(0, 120, 300);       // or doc.GetParagraphs(0)
Console.WriteLine($"{paragraph.Alignment}, {paragraph.LineCount} lines: {paragraph.Text}");

doc.ReplaceParagraphText(0, paragraph.Index, "New wording for the whole paragraph...");
doc.ReplaceInParagraph(0, paragraph.Index, start: 10, length: 5, "words");
doc.FormatParagraphText(0, paragraph.Index, 0, 4, new TextFormat { Bold = true, Color = new PdfColor(200, 0, 0) });
doc.SetParagraphAlignment(0, paragraph.Index, TextAlignment.Justify);
doc.ReplaceText("colour", "color");                     // whole document, every changed paragraph re-flows
doc.Save("out.pdf");
```

Paragraph indices are valid until the page changes, so read the paragraphs again after each edit.

**What is kept**

- Position, width, alignment (left, centre, right, justified), first-line indent, list-item hanging indent and line spacing.
- Formatting of the text that survives an edit: bold or italic words, colours, sizes, superscripts. New text takes the formatting around it.
- The original embedded fonts. Embedded fonts usually hold only the letters the document already uses; any other letter is drawn in a look-alike font (a standard PDF font of the same family, or an installed font for accents and other scripts), and the result says so.
- Everything else on the page. When the paragraph grows or shrinks, content below it in the same column (text, images, highlights and comments) moves with it; the other columns and the footer area stay put.

**How paragraphs are found**

Lines are grouped into a paragraph when they share a text size and the page's usual line spacing, line up on the left, centre or right, and when each line was too full to take the next line's first word (a line with room to spare ended on purpose). Hyphenated words split across lines are joined back together.

**Limits**

- Detection is a judgement made from positions. Unusual layouts (tables, text in shapes, multi-column text drawn out of order) may be grouped differently from how they look.
- A forced line break inside a paragraph is drawn, but the PDF does not record it, so reading the page back gives one paragraph of two lines.
- Re-written lines use the font's normal letter spacing; fine kerning from the original is not kept for those lines.
- Text that runs past the bottom of the page is reported (`RunsOffPage`), not moved to the next page.
- Right-to-left scripts, vertical text and text inside form XObjects are not re-flowed.

**Keeping the rest of the page intact.** When an existing object changes, PDFium rewrites the page's original content and drops word and character spacing on the way, which would shift justified text elsewhere on the page. Before that happens, any text that depends on those settings is redrawn with each character pinned where it was. This also protects the editor's move, resize, delete and edit-text operations.

## Importing RTF

RTF documents (from Word, WordPad, LibreOffice and others) are laid out as PDF pages. The pages are ordinary editable PDF content, so imported paragraphs can be edited with reflow straight away.

```csharp
using var doc = PdfDocument.FromRtf(File.ReadAllBytes("notes.rtf"), out var result);
foreach (var warning in result.Warnings) Console.WriteLine(warning);   // what could not be brought across
doc.Save("notes.pdf");

existing.ImportRtf("appendix.rtf", index: 3);                         // or insert into a PDF that is open
```

**Brought across:** page size and margins; fonts, sizes, bold, italic, underline, strikethrough, colours, highlighting, superscript and subscript; alignment (including justified), indents, space before and after, line spacing, tab stops; bulleted and numbered lists; tables with borders; PNG and JPEG pictures; page and section breaks; Unicode and code-page text (including Cyrillic, Greek and other scripts).

**Left out (and reported in `Warnings`):** headers and footers, WMF/EMF pictures, tables inside tables (flattened). Footnotes, comments, fields' codes and drawing objects are skipped silently.

**Fonts.** By default the standard PDF fonts are used (Times, Helvetica, Courier, chosen to match each RTF font's family): small files that look the same everywhere. Text those fonts cannot show (for example Cyrillic or Greek) is drawn in a matching installed font, which is embedded. `new RtfImportOptions { EmbedFonts = true }` embeds the installed fonts the RTF names (Calibri, Georgia, ...) for a closer look; PDFium embeds whole font files, so this makes much larger PDFs.

Line breaks are worked out by this library, so they can differ slightly from the word processor's.

## Running the editor (Windows)

Needs the .NET 8 SDK (or Visual Studio 2022 with the ".NET desktop development" workload).

```
dotnet run --project src/PdfEditor.App
```

or open `PdfEditor.sln` in Visual Studio and run `PdfEditor.App`. A PDF path on the command line, or a PDF dropped on the window, opens that file.

### Using it

| Tool | What it does |
|---|---|
| **Select** | Click an item to select it, drag to move text or images, drag an image's corner handle to resize it, Delete to remove. Double-click a paragraph to rewrite it: when you finish, it re-flows. Double-click a comment to change it. With text selected, the Paragraph buttons set its alignment. |
| **Text** | Click where new text should start and type. Enter adds a line; Ctrl+Enter or clicking elsewhere finishes; Esc cancels. Font, size, bold, italic and colour come from the toolbar. |
| **Highlight** | Drag across text to highlight it in the chosen colour. Dragging over an empty area highlights that area. |
| **Comment** | Click where the comment should go and type it. Comments and highlights are listed on the right. |
| **Image** | Pick an image file; it is placed in the middle of the page, ready to drag and resize. |

Vector drawings (shapes that sit together) are selected, moved and deleted as one item. A click on something inside a group (a header or logo block stored as one unit) takes the group apart so its parts can be selected; where the group's text cannot safely stay text, it becomes shapes that look the same.

Scrolling past the end of a page carries on to the next page. The page list on the left can be resized by dragging its edge, and pages reordered by dragging them.

**Find & replace** (Ctrl+H) replaces text throughout the document and re-flows each paragraph it changes.

**Open** also accepts RTF files: the document is laid out as a new PDF (named after the RTF file, not saved until you save it). **Insert file** adds the pages of a PDF or an RTF document after the current page. Anything that could not be imported is listed after the import.

Page buttons add a blank page, insert another PDF's pages, delete, rotate and reorder the current page. Undo/redo (Ctrl+Z / Ctrl+Y) covers every change. Ctrl+mouse wheel zooms.

## What the engine does

- **Pages** – add blank pages, delete, reorder, rotate, import pages from another PDF.
- **Text** – add text (standard fonts, or an embedded TrueType font when characters need it), list the text runs on a page, edit a run in place, move or remove it.
- **Images** – add JPEG, PNG, BMP, GIF or TGA images, list, move and remove them.
- **Comments and highlights** – sticky-note comments, highlight/underline/strikeout/squiggly markup, search and "highlight every match".
- **Rendering** – render any page to BGRA pixels for display.

All coordinates are in points (1/72 inch) from the top-left of the page as displayed.

```csharp
using PdfEditor.Core;

using var doc = PdfDocument.Open("in.pdf");
doc.AddPage();                                        // blank A4 page at the end
doc.AddText(0, 72, 72, "Hello", new TextStyle { Size = 18, Bold = true });
doc.AddImage(0, File.ReadAllBytes("logo.png"), 72, 120, width: 150);
doc.HighlightText(0, "important");
doc.AddComment(0, 400, 72, "Please review", author: "Me");

var run = doc.FindTextRunAt(0, 100, 300);             // click-to-edit
if (run is not null) doc.EditText(0, run.ObjectIndex, "New wording");

doc.Save("out.pdf");
```

### Limits of text editing

A PDF stores text as separately positioned runs, not paragraphs. Editing a run changes its wording in place; the surrounding text does not reflow. When the run's embedded font lacks a needed character, a similar font is substituted and `EditText` reports it.

## Tests

```
dotnet test tests/PdfEditor.Core.Tests
```

The engine tests run on Windows, Linux and macOS. The app itself only runs on Windows.

The reflow and RTF tests read files in `tests/PdfEditor.Core.Tests/Fixtures`, made by `make_fixtures.py` there (python-docx, reportlab and LibreOffice): a Word-style document exported by LibreOffice with subset-embedded fonts, a two-column document justified with word spacing, and a Word document saved as RTF by LibreOffice.

PDFium is not thread-safe, even across separate documents: use the library from one thread at a time.
