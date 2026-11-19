# PDFeditor

A PDF editor in C# for The Awkward Snail.

## Layout

- `src/PdfEditor.Core` – the editing engine (no UI). Built on [PDFium](https://pdfium.googlesource.com/pdfium/) (BSD/Apache licensed) through its native packages for Windows, Linux and macOS.
- `src/PdfEditor.App` – the Windows desktop editor (WPF). The whole editor lives in `PdfEditorControl`, so it can be dropped into another WPF window; `MainWindow` is a thin shell around it.
- `tests/PdfEditor.Core.Tests` – xUnit tests for the engine.

## Running the editor (Windows)

Needs the .NET 8 SDK (or Visual Studio 2022 with the ".NET desktop development" workload).

```
dotnet run --project src/PdfEditor.App
```

or open `PdfEditor.sln` in Visual Studio and run `PdfEditor.App`. A PDF path on the command line, or a PDF dropped on the window, opens that file.

### Using it

| Tool | What it does |
|---|---|
| **Select** | Click an item to select it, drag to move text or images, drag an image's corner handle to resize it, double-click text to edit it, double-click a comment to change it, Delete to remove. |
| **Text** | Click where new text should start and type. Enter adds a line; Ctrl+Enter or clicking elsewhere finishes; Esc cancels. Font, size, bold, italic and colour come from the toolbar. |
| **Highlight** | Drag across text to highlight it in the chosen colour. Dragging over an empty area highlights that area. |
| **Comment** | Click where the comment should go and type it. Comments and highlights are listed on the right. |
| **Image** | Pick an image file; it is placed in the middle of the page, ready to drag and resize. |

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
