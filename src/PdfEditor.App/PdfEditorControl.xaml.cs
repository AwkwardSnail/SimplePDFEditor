using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PdfEditor.Core;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace PdfEditor.App;

/// <summary>
/// The whole PDF editor as one control: toolbar, page thumbnails, the page being edited and
/// the comments list. Shows one page at a time. Host it in any WPF window.
/// </summary>
public partial class PdfEditorControl : UserControl
{
    private enum Tool { Select, Text, Highlight, Comment, Line, Arrow, Box, Circle, Spline }
    private enum ItemKind { Text, Image, Annotation, Drawing }
    private enum Drag { None, Move, Resize, Highlight, Shape, Band }

    /// <summary>What is currently selected on the page. Bounds are in page points.</summary>
    /// <summary>For a drawing, Index is its first shape and Group lists all of its shapes.</summary>
    private sealed record Selection(ItemKind Kind, int Index, PdfRect Bounds, IReadOnlyList<int>? Group = null);

    private const double HandleSize = 10;
    private static readonly double[] ZoomSteps = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];

    private EditorSession _session = EditorSession.New();
    private readonly ObservableCollection<PageItem> _pages = [];
    private int _page;
    private double _zoom = 1;
    private Tool _tool = Tool.Select;
    private bool _updating; // true while the control itself is changing list selections

    // What is on the current page, refreshed with every render and used for hit-testing.
    private IReadOnlyList<TextRun> _runs = [];
    private IReadOnlyList<ImageInfo> _images = [];
    private IReadOnlyList<DrawingInfo> _drawings = [];
    private IReadOnlyList<ShapeInfo> _shapes = [];
    // Which shapes the user has taken out of their figure, or joined into one, while this document is open.
    private readonly ShapeGroups _shapeGroups = new();
    private int _groupsPageCount = -1;
    private IReadOnlyList<AnnotationInfo> _annotations = [];

    private Selection? _selection;
    private Drag _drag;
    /// <summary>The shape being drawn, as points on the page: the two ends of a drag, or a spline's points so far followed by where the mouse is.</summary>
    private List<PdfPoint>? _shape;
    private int _shapePage;

    private static (string Name, PdfColor Colour)[] ShapeColours => ShapeDialogs.Colours;
    private Point _dragStart;            // page points
    private PdfRect _dragBounds;         // where the dragged item would land
    private int _highlightStartChar, _highlightEndChar;
    private IReadOnlyList<PdfRect> _highlightRects = [];

    private double[]? _breakPositions;   // while choosing where to break the page: the heights the line snaps to
    private double _breakY = double.NaN; // where the break line is now (page points), when the mouse is over the page
    private bool _breakSnapped;

    private TextBox? _editor;            // the in-place text box, when typing on the page
    private Action<string>? _editorCommit;

    public PdfEditorControl()
    {
        InitializeComponent();
        Thumbs.ItemsSource = _pages;
        FillColours(LineColourBox, ShapeColours);
        FillBox.Items.Add(new ComboBoxItem { Content = "None" });
        FillColours(FillBox, ShapeColours); // leaves "None", the first, selected
        FillColours(ColorBox, [("Black", PdfColor.Black), ("Red", new(200, 30, 30)), ("Blue", new(30, 70, 200)), ("Green", new(20, 130, 60)), ("Grey", new(110, 110, 110)), ("White", new(255, 255, 255))]);
        FillColours(HighlightBox, [("Yellow", PdfColor.Yellow), ("Green", new(140, 235, 130)), ("Pink", new(255, 160, 200)), ("Blue", new(140, 200, 255)), ("Orange", new(255, 190, 90))]);
        PreviewKeyDown += OnPreviewKeyDown;
        Drop += OnDrop;
        Loaded += (_, _) => RefreshAll();
        _session.Changed += OnSessionChanged;
        UpdateHint();
    }

    // ------------------------------------------------------------------ public surface for a host app

    /// <summary>File name of the open document, with a star when it has unsaved changes.</summary>
    public string DocumentTitle =>
        (_session.FilePath is null ? _session.SuggestedFileName : Path.GetFileName(_session.FilePath)) + (_session.IsModified ? " *" : "");

    public event EventHandler? TitleChanged;

    /// <summary>
    /// Opens a PDF, or turns an RTF document into a new PDF, first offering to save unsaved
    /// changes. Returns false if nothing was opened.
    /// </summary>
    public bool OpenFile(string path)
    {
        if (!ConfirmDiscardChanges()) return false;
        EditorSession? opened = null;
        try
        {
            if (IsRtf(path))
            {
                opened = EditorSession.FromRtf(path, out var result);
                ReplaceSession(opened);
                ReportImport(result, Path.GetFileName(path));
                return true;
            }
            try { opened = EditorSession.Open(path); }
            catch (PdfException e) when (e.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                var password = TextPrompt.Show(this, "This PDF needs a password", password: true);
                if (password is null) return false;
                opened = EditorSession.Open(path, password);
            }
        }
        catch (Exception e)
        {
            ShowError($"Could not open {Path.GetFileName(path)}.", e);
            return false;
        }
        ReplaceSession(opened);
        // Once the page is on screen, so the question is asked with the document in view.
        var session = _session;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => { if (ReferenceEquals(session, _session)) OfferToConvertLooseText(); });
        return true;
    }

    /// <summary>
    /// Part of the check made when a file is opened. Text stored letter by letter, or in Type 3
    /// fonts (PDFs printed from Chrome are often both), cannot be selected by the line, and Type 3
    /// text cannot be kept as text when its page is changed. Offers to rebuild it as ordinary text.
    /// </summary>
    private void OfferToConvertLooseText()
    {
        LooseTextReport report;
        try { report = Doc.FindLooseText(); }
        catch (PdfException) { return; }
        if (!report.Any) return;

        int pages = report.Pages.Count;
        string where = pages == Doc.PageCount ? (pages == 1 ? "This page" : "Every page") : pages == 1 ? "One page" : $"{pages} of the {Doc.PageCount} pages";
        string what = report.Type3Pages.Count > 0
            ? "stores its text letter by letter, in a kind of font (Type 3) that the editor can show but cannot keep as text once a page is changed"
            : "stores its text letter by letter, so a click picks up one letter at a time";
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"{where} of this file {what}. PDFs saved from a web browser are often made this way.\n\n" +
            "Convert it to ordinary text?\n\n" +
            "Yes: each line becomes one piece of text" + (report.Type3Pages.Count > 0 ? " in the closest standard font" : "") + ", in the same place and size. " +
            "Lines can then be selected and moved, and paragraphs rewritten." + (report.Type3Pages.Count > 0 ? " The letters will look a little different." : "") +
            " Nothing is saved until you save, and Undo brings the original back.\n\n" +
            "No: the file stays as it is." + (report.Type3Pages.Count > 0 ? " On any page you change, this text is then kept as small pictures of itself, so it still looks the same but is no longer text." : ""),
            "SimplePDF Editor", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes) return;

        int made = 0;
        _selection = null;
        if (Edit(d => made = d.ConvertLooseText()))
            ShowStatus($"The text was rebuilt as {made} piece{(made == 1 ? "" : "s")} of ordinary text. Undo brings the original back.");
    }

    /// <summary>Asks about unsaved changes. Returns false when the user cancels, meaning: do not close.</summary>
    public bool ConfirmDiscardChanges()
    {
        CommitEditor();
        if (!_session.IsModified) return true;
        string name = _session.FilePath is null ? _session.SuggestedFileName : Path.GetFileName(_session.FilePath);
        return MessageBox.Show(Window.GetWindow(this), $"Save changes to {name}?", "SimplePDF Editor", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => Save(saveAs: false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    // ------------------------------------------------------------------ session handling

    private PdfDocument Doc => _session.Document;

    private void ReplaceSession(EditorSession session)
    {
        _session.Changed -= OnSessionChanged;
        _session.Dispose();
        _session = session;
        _session.Changed += OnSessionChanged;
        _page = 0;
        _selection = null;
        _shapeGroups.Clear();
        RefreshAll();
    }

    private void OnSessionChanged(object? sender, EventArgs e) => RefreshAll();

    /// <summary>Runs an edit as one undo step and reports any failure to the user.</summary>
    private bool Edit(Action<PdfDocument> edit)
    {
        try
        {
            _session.Apply(edit);
            return true;
        }
        catch (Exception e)
        {
            ShowError("That change could not be made.", e);
            RefreshAll();
            return false;
        }
    }

    private void ShowError(string what, Exception e) =>
        MessageBox.Show(Window.GetWindow(this), what + "\n\n" + e.Message, "SimplePDF Editor", MessageBoxButton.OK, MessageBoxImage.Warning);

    private bool Save(bool saveAs)
    {
        CommitEditor();
        string? path = _session.FilePath;
        if (saveAs || path is null)
        {
            var dialog = new SaveFileDialog { Filter = "PDF files|*.pdf", DefaultExt = ".pdf", FileName = path is null ? _session.SuggestedFileName : Path.GetFileName(path) };
            if (path is not null) dialog.InitialDirectory = Path.GetDirectoryName(path);
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return false;
            path = dialog.FileName;
        }
        try
        {
            _session.Save(path);
            return true;
        }
        catch (Exception e)
        {
            ShowError($"Could not save to {Path.GetFileName(path)}.", e);
            return false;
        }
    }

    // ------------------------------------------------------------------ drawing the current state

    /// <summary>Screen units (WPF device-independent pixels) per PDF point at the current zoom.</summary>
    private double ViewScale => _zoom * 96.0 / 72.0;

    private void RefreshAll()
    {
        if (!IsLoaded) return;
        DiscardEditor();
        int count = Doc.PageCount;
        _page = Math.Clamp(_page, 0, Math.Max(0, count - 1));
        // Pages added or removed: what was noted about shapes was noted by page number, and no longer fits.
        if (count != _groupsPageCount) _shapeGroups.Clear();
        _groupsPageCount = count;

        _updating = true;
        try
        {
            while (_pages.Count > count) _pages.RemoveAt(_pages.Count - 1);
            while (_pages.Count < count) _pages.Add(new PageItem(_pages.Count, this));
            foreach (var item in _pages) item.Invalidate();
            Thumbs.SelectedIndex = _page;
            Thumbs.ScrollIntoView(Thumbs.SelectedItem);
        }
        finally { _updating = false; }

        RenderPage();
        UndoButton.IsEnabled = _session.CanUndo;
        RedoButton.IsEnabled = _session.CanRedo;
        DeletePageButton.IsEnabled = count > 1;
        PageUpButton.IsEnabled = _page > 0;
        PageDownButton.IsEnabled = _page < count - 1;
        PageText.Text = $"Page {_page + 1} of {count}";
        ZoomText.Text = $"{_zoom * 100:0}%";
        TitleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RenderPage()
    {
        var size = Doc.GetPageSize(_page);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var image = Doc.Render(_page, ViewScale * dpi);
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96 * dpi, 96 * dpi, PixelFormats.Bgr32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        PageImage.Source = bitmap;
        PageImage.Width = Overlay.Width = size.Width * ViewScale;
        PageImage.Height = Overlay.Height = size.Height * ViewScale;

        _runs = Doc.GetTextRuns(_page);
        _images = Doc.GetImages(_page);
        _shapes = Doc.GetShapes(_page);
        var (apart, together) = _shapeGroups.Update(_page, _shapes);
        _drawings = Doc.GetDrawings(_page, apart, together);
        _annotations = Doc.GetAnnotations(_page);
        if (_breakPositions is not null)
        {
            _breakPositions = [.. Doc.GetBreakPositions(_page)];
            _breakY = double.NaN;
        }

        // Keep the selection if the same item is still there (for example after moving it).
        if (_selection is { } s)
        {
            _selection = s.Kind switch
            {
                ItemKind.Text => _runs.Where(r => r.ObjectIndex == s.Index).Select(r => s with { Bounds = r.Bounds }).FirstOrDefault(),
                ItemKind.Image => _images.Where(i => i.ObjectIndex == s.Index).Select(i => s with { Bounds = i.Bounds }).FirstOrDefault(),
                ItemKind.Drawing => DrawingSelection(_drawings.Where(d => d.ObjectIndices.Any((s.Group ?? [s.Index]).Contains)).ToList()),
                _ => _annotations.Where(a => a.Index == s.Index).Select(a => s with { Bounds = a.Bounds }).FirstOrDefault(),
            };
        }

        _updating = true;
        try
        {
            Notes.ItemsSource = _annotations.Select(a => new NoteItem(a)).ToList();
            Notes.SelectedIndex = _selection is { Kind: ItemKind.Annotation } sel ? sel.Index : -1;
        }
        finally { _updating = false; }
        DrawOverlay();
    }

    /// <summary>Redraws the selection outline, resize handle and highlight preview.</summary>
    private void DrawOverlay()
    {
        for (int i = Overlay.Children.Count - 1; i >= 0; i--)
            if (!ReferenceEquals(Overlay.Children[i], _editor)) Overlay.Children.RemoveAt(i);

        foreach (var r in _highlightRects)
            AddRectangle(r, fill: new SolidColorBrush(ToColor(CurrentColour(HighlightBox), 120)), stroke: null);

        if (_drag == Drag.Band)
            AddRectangle(_dragBounds, fill: new SolidColorBrush(Color.FromArgb(30, 30, 144, 255)), stroke: Brushes.DodgerBlue, dashed: true);
        if (_selection is { } s)
        {
            var bounds = _drag is Drag.Move or Drag.Resize ? _dragBounds : s.Bounds;
            AddRectangle(bounds, fill: null, stroke: Brushes.DodgerBlue, dashed: true, grow: 2);
            if (s.Kind == ItemKind.Image)
            {
                var handle = new Rectangle { Width = HandleSize, Height = HandleSize, Fill = Brushes.White, Stroke = Brushes.DodgerBlue, StrokeThickness = 1.5, IsHitTestVisible = false };
                Canvas.SetLeft(handle, bounds.Right * ViewScale - HandleSize / 2);
                Canvas.SetTop(handle, bounds.Bottom * ViewScale - HandleSize / 2);
                Overlay.Children.Add(handle);
            }
        }
        if (_shape is not null && _shapePage != _page) _shape = null; // left behind on another page
        if (_shape is { } drawn)
        {
            // The shape as it will be: the same outline the engine draws, at the size shown.
            var style = CurrentShapeStyle();
            var kind = CurrentShapeKind;
            var outline = PdfDocument.ShapeOutline(kind, drawn, style.Width);
            if (outline.Count > 0)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    Point At(PdfPoint q) => new(q.X * ViewScale, q.Y * ViewScale);
                    bool open = false;
                    foreach (var step in outline)
                    {
                        if (step.Kind == 'M') { g.BeginFigure(At(step.End), true, false); open = true; }
                        else if (!open) continue;
                        else if (step.Kind == 'L') g.LineTo(At(step.End), true, true);
                        else if (step.Kind == 'C') g.BezierTo(At(step.Control1), At(step.Control2), At(step.End), true, true);
                        else if (step.Kind == 'Z') g.LineTo(At(outline.Last(s => s.Kind == 'M').End), true, true);
                    }
                }
                var stroke = new SolidColorBrush(ToColor(style.Stroke));
                PdfColor? inside = kind switch { ShapeKind.Arrow => style.Stroke, ShapeKind.Line => null, _ => style.Fill };
                Overlay.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = geometry, Stroke = stroke, StrokeThickness = Math.Max(1, style.Width * ViewScale), IsHitTestVisible = false,
                    Fill = inside is { } c ? new SolidColorBrush(ToColor(c)) : null,
                    StrokeLineJoin = kind == ShapeKind.Box ? PenLineJoin.Miter : PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                });
            }
        }
        if (_breakPositions is not null && !double.IsNaN(_breakY))
        {
            // Solid where it sits in a gap between rows of content, dashed where it would cut through something.
            var line = new System.Windows.Shapes.Line
            {
                X1 = 0, X2 = Overlay.Width, Y1 = _breakY * ViewScale, Y2 = _breakY * ViewScale,
                Stroke = Brushes.DodgerBlue, StrokeThickness = 2, IsHitTestVisible = false,
            };
            if (!_breakSnapped) line.StrokeDashArray = [4, 3];
            Overlay.Children.Add(line);
        }
        DeleteNoteButton.IsEnabled = EditNoteButton.IsEnabled = _selection is { Kind: ItemKind.Annotation };
    }

    private void AddRectangle(PdfRect r, Brush? fill, Brush? stroke, bool dashed = false, double grow = 0)
    {
        var shape = new Rectangle
        {
            Width = Math.Max(1, r.Width * ViewScale + 2 * grow),
            Height = Math.Max(1, r.Height * ViewScale + 2 * grow),
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
        };
        if (dashed) shape.StrokeDashArray = [4, 3];
        Canvas.SetLeft(shape, r.X * ViewScale - grow);
        Canvas.SetTop(shape, r.Y * ViewScale - grow);
        Overlay.Children.Add(shape);
    }

    internal ImageSource? RenderThumbnail(int pageIndex, out double width, out double height)
    {
        width = 100;
        height = 140;
        if (pageIndex >= Doc.PageCount) return null;
        var size = Doc.GetPageSize(pageIndex);
        double fit = Math.Min(_thumbWidth / size.Width, _thumbWidth * 1.45 / size.Height);
        width = size.Width * fit;
        height = size.Height * fit;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var image = Doc.Render(pageIndex, fit * dpi);
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96 * dpi, 96 * dpi, PixelFormats.Bgr32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    // ------------------------------------------------------------------ toolbar: file, undo, pages, zoom

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmDiscardChanges()) ReplaceSession(EditorSession.New());
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PDF or RTF|*.pdf;*.rtf|PDF files|*.pdf|Rich text (RTF)|*.rtf|All files|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) OpenFile(dialog.FileName);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save(saveAs: false);
    private void SaveAs_Click(object sender, RoutedEventArgs e) => Save(saveAs: true);

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        DiscardEditor();
        _selection = null;
        _session.Undo();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        DiscardEditor();
        _selection = null;
        _session.Redo();
    }

    private void AddPage_Click(object sender, RoutedEventArgs e)
    {
        CommitEditor();
        EndPageBreak();
        var current = Doc.GetPageSize(_page);
        // Ask for the size, unless one was kept for every new page (Shift asks again).
        var choice = (Keyboard.Modifiers & ModifierKeys.Shift) == 0 ? PageSizeDialog.Remembered : null;
        choice ??= PageSizeDialog.Show(this, current.Width, current.Height);
        if (choice is null) return;
        var size = choice.Resolve(current.Width, current.Height);
        int at = _page + 1;
        _selection = null;
        _page = at; // set first: the refresh triggered by the edit shows the new page
        if (!Edit(d => d.AddPage(size.Width, size.Height, at))) { _page = at - 1; RefreshAll(); }
    }

    /// <summary>Saves the current page as a separate PDF spread over sheets of paper. The document itself is not changed.</summary>
    private void PrintAsTiles_Click(object sender, RoutedEventArgs e)
    {
        CommitEditor();
        EndPageBreak();
        var pageSize = Doc.GetPageSize(_page);
        BitmapSource? small = null;
        try
        {
            // A small picture of the page for the dialog to lay the sheets over.
            var image = Doc.Render(_page, Math.Min(2, 1100 / Math.Max(pageSize.Width, pageSize.Height)));
            small = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr32, null, image.Pixels, image.Stride);
            small.Freeze();
        }
        catch (Exception ex) when (ex is PdfException or ArgumentException or OutOfMemoryException) { } // the dialog then shows a blank page
        if (TilesDialog.Show(this, pageSize, small) is not { } choice) return;
        string name = Path.GetFileNameWithoutExtension(_session.FilePath ?? _session.SuggestedFileName);
        var dialog = new SaveFileDialog
        {
            Filter = "PDF files|*.pdf", Title = "Save the tiles as", DefaultExt = ".pdf",
            FileName = $"{name} - tiles{(Doc.PageCount > 1 ? $" (page {_page + 1})" : "")}.pdf",
        };
        if (_session.FilePath is { } current) dialog.InitialDirectory = Path.GetDirectoryName(current);
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (_session.FilePath is not null && string.Equals(Path.GetFullPath(dialog.FileName), _session.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            ShowStatus("The tiles need a file of their own: choose a name other than the document's.");
            return;
        }
        try
        {
            using var tiles = choice.Sheets is { } sheets ? Doc.CreateTiles(_page, sheets, choice.Options) : Doc.CreateTiles(_page, choice.Options);
            tiles.Save(dialog.FileName);
            ShowStatus($"Saved {tiles.PageCount} sheet{(tiles.PageCount == 1 ? "" : "s")} to {Path.GetFileName(dialog.FileName)}. Print it at 100% (no fit to page). This document is unchanged.");
        }
        catch (Exception ex) { ShowError("The tiles could not be made.", ex); }
    }

    private void InsertFile_Click(object sender, RoutedEventArgs e)
    {
        CommitEditor();
        var dialog = new OpenFileDialog { Filter = "PDF or RTF|*.pdf;*.rtf|PDF files|*.pdf|Rich text (RTF)|*.rtf", Title = "Insert pages from" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            int at = _page + 1;
            _selection = null;
            _page = at;
            if (IsRtf(dialog.FileName))
            {
                byte[] rtf = File.ReadAllBytes(dialog.FileName);
                RtfImportResult? result = null;
                if (!Edit(d => result = d.ImportRtf(rtf, at))) { _page = at - 1; RefreshAll(); return; }
                ReportImport(result!, Path.GetFileName(dialog.FileName));
                return;
            }
            using var other = PdfDocument.Open(dialog.FileName);
            if (!Edit(d => d.ImportPages(other, index: at))) { _page = at - 1; RefreshAll(); }
        }
        catch (Exception ex)
        {
            ShowError($"Could not read {Path.GetFileName(dialog.FileName)}.", ex);
        }
    }

    private void DeletePage_Click(object sender, RoutedEventArgs e)
    {
        CommitEditor();
        if (Doc.PageCount <= 1) return;
        int page = _page;
        _selection = null;
        Edit(d => d.DeletePage(page));
    }

    private void Rotate_Click(object sender, RoutedEventArgs e) => RotatePage(90);

    private void RotatePage(int degrees)
    {
        CommitEditor();
        int page = _page;
        _selection = null;
        Edit(d => d.SetPageRotation(page, d.GetPageRotation(page) + degrees));
    }

    /// <summary>Asks for a new size for the current page and gives it that size, with or without scaling what is on it.</summary>
    private void ResizePage()
    {
        CommitEditor();
        EndPageBreak();
        var current = Doc.GetPageSize(_page);
        if (PageSizeDialog.ShowResize(this, current.Width, current.Height) is not { } chosen) return;
        int page = _page;
        _selection = null;
        Edit(d => d.ResizePage(page, chosen.Width, chosen.Height, chosen.ScaleContent));
    }

    /// <summary>A right-click on a page in the list makes it the current page and offers what can be done to it.</summary>
    private void Thumbs_RightClick(object sender, MouseButtonEventArgs e)
    {
        int index = ThumbIndexAt(e.OriginalSource);
        if (index < 0) return;
        e.Handled = true;
        CommitEditor();
        if (index != _page) GoToPage(index);

        var menu = new ContextMenu { PlacementTarget = Thumbs };
        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Rotate right", () => RotatePage(90));
        Add("Rotate left", () => RotatePage(-90));
        Add("Resize page...", ResizePage);
        menu.Items.Add(new Separator());
        Add("Delete page", () => DeletePage_Click(this, new RoutedEventArgs()), Doc.PageCount > 1);
        menu.IsOpen = true;
    }

    private void MovePageUp_Click(object sender, RoutedEventArgs e) => MovePage(-1);
    private void MovePageDown_Click(object sender, RoutedEventArgs e) => MovePage(1);

    private void MovePage(int by)
    {
        CommitEditor();
        int from = _page, to = _page + by;
        if (to < 0 || to >= Doc.PageCount) return;
        _selection = null;
        _page = to;
        if (!Edit(d => d.MovePage(from, to))) { _page = from; RefreshAll(); }
    }

    private void Thumbs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || Thumbs.SelectedIndex < 0 || Thumbs.SelectedIndex == _page) return;
        GoToPage(Thumbs.SelectedIndex);
    }

    private void GoToPage(int page, bool showBottom = false)
    {
        if (page < 0 || page >= Doc.PageCount || page == _page) return;
        CommitEditor();
        _page = page;
        _selection = null;
        RefreshAll();
        if (showBottom)
        {
            PageScroll.UpdateLayout(); // the new page's height must be known before scrolling to its end
            PageScroll.ScrollToBottom();
        }
        else PageScroll.ScrollToTop();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(ZoomSteps.LastOrDefault(z => z < _zoom - 0.001, ZoomSteps[0]));
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(ZoomSteps.FirstOrDefault(z => z > _zoom + 0.001, ZoomSteps[^1]));

    private void FitWidth_Click(object sender, RoutedEventArgs e)
    {
        double available = PageScroll.ActualWidth - 80; // page margins and scroll bar
        if (available > 50) SetZoom(available / (Doc.GetPageSize(_page).Width * 96.0 / 72.0));
    }

    private void SetZoom(double zoom)
    {
        CommitEditor();
        _zoom = Math.Clamp(zoom, 0.1, 6);
        RefreshAll();
    }

    private void PageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
            e.Handled = true;
            return;
        }
        // Scrolling past the end of a page carries on to the next page (and past the top, to the one before).
        if (e.Delta < 0 && PageScroll.VerticalOffset >= PageScroll.ScrollableHeight - 0.5 && _page < Doc.PageCount - 1)
        {
            GoToPage(_page + 1);
            e.Handled = true;
        }
        else if (e.Delta > 0 && PageScroll.VerticalOffset <= 0.5 && _page > 0)
        {
            GoToPage(_page - 1, showBottom: true);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ tools

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string name } || HintText is null) return; // still loading
        CommitEditor();
        _tool = Enum.Parse<Tool>(name);
        _breakPositions = null;
        _shape = null;
        Overlay.Cursor = _tool switch { Tool.Text => Cursors.IBeam, Tool.Highlight => Cursors.Cross, Tool.Comment => Cursors.Pen, Tool.Select => Cursors.Arrow, _ => Cursors.Cross };
        UpdateHint();
        DrawOverlay();
    }

    private void UpdateHint() => HintText.Text = _tool switch
    {
        Tool.Text => "Click where the text should start. Enter adds a line; Ctrl+Enter or a click elsewhere finishes; Esc cancels.",
        Tool.Highlight => "Drag across text to highlight it. Dragging over an empty area highlights that area.",
        Tool.Comment => "Click where the comment should go.",
        Tool.Line => "Drag to draw a line. Shift keeps it level, upright or at 45 degrees. Right-click a shape to fill or delete it.",
        Tool.Arrow => "Drag to draw an arrow; it points where the drag ends. Shift keeps it level, upright or at 45 degrees.",
        Tool.Box => "Drag to draw a box. Shift makes it square. Right-click a shape to fill it, make it see-through or delete it.",
        Tool.Circle => "Drag to draw a circle or oval. Shift keeps it round. Right-click a shape to fill it, make it see-through or delete it.",
        Tool.Spline => "Click point after point; the curve passes through each. Double-click or Enter finishes; Esc cancels.",
        _ => "Click to select, drag to move, Delete to remove. Drag from empty paper to select several lines. Double-click a paragraph to rewrite it, or a comment to edit it.",
    };

    private TextStyle CurrentTextStyle()
    {
        if (!float.TryParse(SizeBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out float size) &&
            !float.TryParse(SizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out size)) size = 12;
        return new TextStyle
        {
            Font = FontBox.SelectedIndex switch { 1 => StandardFont.Times, 2 => StandardFont.Courier, _ => StandardFont.Helvetica },
            Bold = BoldButton.IsChecked == true,
            Italic = ItalicButton.IsChecked == true,
            Size = Math.Clamp(size, 4, 400),
            Color = CurrentColour(ColorBox),
        };
    }

    private void AddImage_Click(object sender, RoutedEventArgs e)
    {
        CommitEditor();
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tga|All files|*.*", Title = "Choose an image" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        byte[] data;
        try { data = File.ReadAllBytes(dialog.FileName); }
        catch (Exception ex) { ShowError("Could not read the image file.", ex); return; }

        // Place it centred, no wider or taller than half the page; it can be dragged and resized afterwards.
        var size = Doc.GetPageSize(_page);
        int page = _page;
        bool added = Edit(d =>
        {
            d.AddImage(page, data, 0, 0);
            var image = d.GetImages(page)[^1];
            double fit = Math.Min(1, Math.Min(size.Width / 2 / image.Bounds.Width, size.Height / 2 / image.Bounds.Height));
            double w = image.Bounds.Width * fit, h = image.Bounds.Height * fit;
            d.SetImageBounds(page, image.ObjectIndex, new PdfRect((size.Width - w) / 2, (size.Height - h) / 2, w, h));
        });
        if (!added) return;
        SelectTool.IsChecked = true;
        if (_images.Count > 0) Select(new Selection(ItemKind.Image, _images[^1].ObjectIndex, _images[^1].Bounds));
    }

    // ------------------------------------------------------------------ mouse on the page

    private Point PagePoint(MouseEventArgs e)
    {
        var p = e.GetPosition(Overlay);
        return new Point(p.X / ViewScale, p.Y / ViewScale);
    }

    private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (_editor is not null)
        {
            CommitEditor(); // a click away from the text box finishes typing
            return;
        }
        var p = PagePoint(e);
        if (_breakPositions is not null)
        {
            MoveBreakLine(p.Y);
            BreakPageAt(_breakY);
            e.Handled = true;
            return;
        }
        switch (_tool)
        {
            case Tool.Select:
                SelectToolDown(p, e.ClickCount);
                break;
            case Tool.Text:
                var style = CurrentTextStyle();
                int page = _page;
                BeginTyping(p.X, p.Y, "", style.Size, style, multiline: true,
                    text => { if (text.Trim().Length > 0) Edit(d => d.AddText(page, p.X, p.Y, text, style)); });
                break;
            case Tool.Highlight:
                _drag = Drag.Highlight;
                _dragStart = p;
                _highlightStartChar = _highlightEndChar = Doc.GetCharIndexAt(_page, p.X, p.Y, 6);
                _highlightRects = [];
                Overlay.CaptureMouse();
                break;
            case Tool.Comment:
                AddComment(p);
                break;
            case Tool.Spline:
                SplineClick(p, e.ClickCount);
                break;
            case Tool.Line or Tool.Arrow or Tool.Box or Tool.Circle:
                _shape = [new PdfPoint(p.X, p.Y), new PdfPoint(p.X, p.Y)];
                _shapePage = _page;
                _drag = Drag.Shape;
                Overlay.CaptureMouse();
                break;
        }
        e.Handled = true;
    }

    private void SelectToolDown(Point p, int clickCount)
    {
        // The resize handle of a selected image wins over whatever lies beneath it.
        if (_selection is { Kind: ItemKind.Image } current)
        {
            double reach = HandleSize / ViewScale;
            if (Math.Abs(p.X - current.Bounds.Right) <= reach && Math.Abs(p.Y - current.Bounds.Bottom) <= reach)
            {
                StartDrag(Drag.Resize, p, current.Bounds);
                return;
            }
        }

        // A press inside a block of selected lines moves the block.
        if (clickCount < 2 && _selection is { Kind: ItemKind.Text, Group: not null } block && block.Bounds.Contains(p.X, p.Y))
        {
            StartDrag(Drag.Move, p, block.Bounds);
            return;
        }
        // The same for several drawings picked together.
        if (clickCount < 2 && _selection is { Kind: ItemKind.Drawing } figures && IsSeveralDrawings(figures) && figures.Bounds.Contains(p.X, p.Y))
        {
            StartDrag(Drag.Move, p, figures.Bounds);
            return;
        }

        var hit = HitTest(p) ?? UngroupAt(p);
        Select(hit);
        if (hit is null)
        {
            // On empty paper: drag out a box to select the lines of text it touches.
            StartDrag(Drag.Band, p, new PdfRect(p.X, p.Y, 0, 0));
            return;
        }

        if (clickCount >= 2)
        {
            if (hit.Kind == ItemKind.Text)
            {
                if (!BeginEditingParagraph(p)) BeginEditingRun(hit);
            }
            else if (hit.Kind == ItemKind.Annotation) EditAnnotationText(hit.Index);
            return;
        }
        if (hit.Kind != ItemKind.Annotation) StartDrag(Drag.Move, p, hit.Bounds);
    }

    private void StartDrag(Drag drag, Point p, PdfRect bounds)
    {
        _drag = drag;
        _dragStart = p;
        _dragBounds = bounds;
        Overlay.CaptureMouse();
    }

    /// <summary>Topmost item under a point: comments and highlights first, then text, then images.</summary>
    private Selection? HitTest(Point p)
    {
        var note = _annotations.LastOrDefault(a => a.Bounds.Contains(p.X, p.Y));
        if (note is not null) return new Selection(ItemKind.Annotation, note.Index, note.Bounds);
        // Text that spreads over much of the page (a watermark) is picked only when nothing else is there,
        // or it would be in the way of everything it lies across.
        double large = Overlay.Width * Overlay.Height / (ViewScale * ViewScale) * 0.2;
        var run = _runs.LastOrDefault(r => r.Bounds.Contains(p.X, p.Y) && r.Bounds.Width * r.Bounds.Height < large);
        if (run is not null) return new Selection(ItemKind.Text, run.ObjectIndex, run.Bounds);
        var image = _images.LastOrDefault(i => i.Bounds.Contains(p.X, p.Y));
        if (image is not null) return new Selection(ItemKind.Image, image.ObjectIndex, image.Bounds);
        // Vector drawings last, smallest first, so a figure inside a frame is picked before the frame.
        if (DrawingAt(p) is { } drawing) return new Selection(ItemKind.Drawing, drawing.ObjectIndices[0], drawing.Bounds, drawing.ObjectIndices);
        var wide = _runs.LastOrDefault(r => r.Bounds.Contains(p.X, p.Y));
        return wide is null ? null : new Selection(ItemKind.Text, wide.ObjectIndex, wide.Bounds);
    }

    /// <summary>
    /// The drawing at a point. What is actually drawn there comes first, and of that whatever lies
    /// on top: a shape's filling, or its line. Failing that (empty space inside a figure, or inside
    /// a box with no filling), the smallest drawing the point is within, so that a figure inside a
    /// frame is picked before the frame; a shape on its own counts only within its own outline.
    /// </summary>
    private DrawingInfo? DrawingAt(Point p)
    {
        IReadOnlyList<ShapeHit> hits;
        try { hits = Doc.HitShapes(_page, p.X, p.Y, 4 / ViewScale); }
        catch (PdfException) { hits = []; }
        if (hits.LastOrDefault(h => h.OnInk) is { } top && _drawings.FirstOrDefault(d => d.ObjectIndices.Contains(top.ObjectIndex)) is { } shown) return shown;
        var within = hits.Where(h => h.Inside).Select(h => h.ObjectIndex).ToHashSet();
        return _drawings.Where(d => d.Bounds.Contains(p.X, p.Y) && (d.ObjectIndices.Count > 1 || within.Contains(d.ObjectIndices[0])))
                        .OrderBy(d => d.Bounds.Width * d.Bounds.Height).FirstOrDefault();
    }

    /// <summary>One selection that holds all the given drawings, to be moved, changed or deleted together.</summary>
    private static Selection? DrawingSelection(IReadOnlyList<DrawingInfo> drawings)
    {
        if (drawings.Count == 0) return null;
        double left = drawings.Min(d => d.Bounds.X), top = drawings.Min(d => d.Bounds.Y);
        var bounds = new PdfRect(left, top, drawings.Max(d => d.Bounds.Right) - left, drawings.Max(d => d.Bounds.Bottom) - top);
        var shapes = drawings.SelectMany(d => d.ObjectIndices).Distinct().OrderBy(i => i).ToList();
        return new Selection(ItemKind.Drawing, shapes[0], bounds, shapes);
    }

    /// <summary>Whether the selection is several drawings picked together (with a dragged-out box).</summary>
    private bool IsSeveralDrawings(Selection s) =>
        s is { Kind: ItemKind.Drawing, Group: { } shapes } && _drawings.Count(d => d.ObjectIndices.Any(shapes.Contains)) > 1;

    private void Select(Selection? selection)
    {
        _selection = selection;
        _updating = true;
        try { Notes.SelectedIndex = selection is { Kind: ItemKind.Annotation } ? selection.Index : -1; }
        finally { _updating = false; }
        DrawOverlay();
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_breakPositions is not null)
        {
            MoveBreakLine(PagePoint(e).Y);
            return;
        }
        if (_tool == Tool.Spline && _shape is { Count: > 0 } curve && _drag == Drag.None)
        {
            // The last point follows the mouse, showing where the curve would go next.
            var at = PagePoint(e);
            curve[^1] = new PdfPoint(at.X, at.Y);
            DrawOverlay();
            return;
        }
        if (_drag == Drag.None || _selection is null && _drag is not (Drag.Highlight or Drag.Shape or Drag.Band)) return;
        var p = PagePoint(e);
        switch (_drag)
        {
            case Drag.Band:
                _dragBounds = new PdfRect(Math.Min(p.X, _dragStart.X), Math.Min(p.Y, _dragStart.Y), Math.Abs(p.X - _dragStart.X), Math.Abs(p.Y - _dragStart.Y));
                break;
            case Drag.Shape when _shape is { Count: 2 } ends:
                ends[1] = Constrain(ends[0], new PdfPoint(p.X, p.Y));
                break;
            case Drag.Move:
                var s = _selection!.Bounds;
                _dragBounds = new PdfRect(s.X + p.X - _dragStart.X, s.Y + p.Y - _dragStart.Y, s.Width, s.Height);
                break;
            case Drag.Resize:
                // Keep the picture's proportions: the width follows the mouse and the height follows the width.
                var b = _selection!.Bounds;
                double width = Math.Max(8, b.Width + p.X - _dragStart.X);
                _dragBounds = new PdfRect(b.X, b.Y, width, width * b.Height / b.Width);
                break;
            case Drag.Highlight:
                int index = Doc.GetCharIndexAt(_page, p.X, p.Y, 6);
                if (index >= 0)
                {
                    if (_highlightStartChar < 0) _highlightStartChar = index;
                    _highlightEndChar = index;
                }
                if (_highlightStartChar >= 0 && _highlightEndChar >= 0)
                {
                    int first = Math.Min(_highlightStartChar, _highlightEndChar);
                    _highlightRects = Doc.GetTextRects(_page, first, Math.Abs(_highlightEndChar - _highlightStartChar) + 1);
                }
                else
                {
                    var area = new PdfRect(Math.Min(p.X, _dragStart.X), Math.Min(p.Y, _dragStart.Y), Math.Abs(p.X - _dragStart.X), Math.Abs(p.Y - _dragStart.Y));
                    _highlightRects = area.Width > 2 && area.Height > 2 ? [area] : [];
                }
                break;
        }
        DrawOverlay();
    }

    private void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == Drag.None) return;
        var drag = _drag;
        _drag = Drag.None;
        Overlay.ReleaseMouseCapture();
        int page = _page;

        if (drag == Drag.Band)
        {
            SelectTextIn(_dragBounds);
            return;
        }

        if (drag == Drag.Shape)
        {
            var points = _shape;
            _shape = null;
            // A click without a drag draws nothing.
            if (points is { Count: 2 } && Math.Abs(points[0].X - points[1].X) + Math.Abs(points[0].Y - points[1].Y) > 3) AddShape(points);
            else DrawOverlay();
            return;
        }

        if (drag == Drag.Highlight)
        {
            var rects = _highlightRects;
            _highlightRects = [];
            if (rects.Count > 0)
            {
                var colour = CurrentColour(HighlightBox);
                Edit(d => d.AddHighlight(page, rects, colour, author: Environment.UserName));
            }
            else DrawOverlay();
            return;
        }

        if (_selection is not { } s) return;
        var target = _dragBounds;
        double dx = target.X - s.Bounds.X, dy = target.Y - s.Bounds.Y;
        if (drag == Drag.Move && (Math.Abs(dx) > 0.5 || Math.Abs(dy) > 0.5))
        {
            if (s.Group is { } shapes) Edit(d => d.MoveObjects(page, shapes, dx, dy));
            else Edit(d => d.MoveObject(page, s.Index, dx, dy));
        }
        else if (drag == Drag.Resize && Math.Abs(target.Width - s.Bounds.Width) > 0.5)
            Edit(d => d.SetImageBounds(page, s.Index, target));
        else DrawOverlay();
    }

    /// <summary>
    /// Selects the pieces of text a dragged-out box touches, as one block that moves together.
    /// Text spread over much of the page (a watermark) is taken only when it lies wholly inside.
    /// </summary>
    private void SelectTextIn(PdfRect area)
    {
        if (area.Width < 3 && area.Height < 3) { Select(null); return; } // a click, not a drag
        double large = Overlay.Width * Overlay.Height / (ViewScale * ViewScale) * 0.2;
        var touched = _runs.Where(r => r.Text.Trim().Length > 0
            && r.Bounds.X < area.Right && r.Bounds.Right > area.X && r.Bounds.Y < area.Bottom && r.Bounds.Bottom > area.Y
            && (r.Bounds.Width * r.Bounds.Height < large || r.Bounds.X >= area.X && r.Bounds.Right <= area.Right && r.Bounds.Y >= area.Y && r.Bounds.Bottom <= area.Bottom)).ToList();
        if (touched.Count == 0)
        {
            // No text there: the drawings the box touches instead (but not a frame the box was drawn inside of).
            var drawings = _drawings.Where(d => d.Bounds.X < area.Right && d.Bounds.Right > area.X && d.Bounds.Y < area.Bottom && d.Bounds.Bottom > area.Y
                && !(d.Bounds.X < area.X && d.Bounds.Right > area.Right && d.Bounds.Y < area.Y && d.Bounds.Bottom > area.Bottom)).ToList();
            Select(DrawingSelection(drawings));
            if (drawings.Count > 1) ShowStatus($"{drawings.Count} drawings selected. Drag to move them together, Delete to remove them, right-click to change or group them.");
            return;
        }
        if (touched.Count == 1)
        {
            Select(new Selection(ItemKind.Text, touched[0].ObjectIndex, touched[0].Bounds));
            return;
        }
        double left = touched.Min(r => r.Bounds.X), top = touched.Min(r => r.Bounds.Y);
        var bounds = new PdfRect(left, top, touched.Max(r => r.Bounds.Right) - left, touched.Max(r => r.Bounds.Bottom) - top);
        Select(new Selection(ItemKind.Text, touched[0].ObjectIndex, bounds, touched.Select(r => r.ObjectIndex).ToList()));
        ShowStatus($"{touched.Count} pieces of text selected. Drag to move them together, Delete to remove them, right-click for font, colour and turning.");
    }

    // ------------------------------------------------------------------ drawing shapes

    private ShapeKind CurrentShapeKind => _tool switch
    {
        Tool.Arrow => ShapeKind.Arrow, Tool.Box => ShapeKind.Box, Tool.Circle => ShapeKind.Ellipse, Tool.Spline => ShapeKind.Spline, _ => ShapeKind.Line,
    };

    private ShapeStyle CurrentShapeStyle() => new()
    {
        Stroke = CurrentColour(LineColourBox),
        Width = LineWidthBox.SelectedItem is ComboBoxItem { Tag: string width } ? double.Parse(width, CultureInfo.InvariantCulture) : 2,
        Fill = FillBox.SelectedItem is ComboBoxItem { Tag: PdfColor fill } ? fill : null,
    };

    /// <summary>With Shift held: a line or arrow keeps to level, upright or 45 degrees; a box is square and a circle round.</summary>
    private PdfPoint Constrain(PdfPoint from, PdfPoint to)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return to;
        double dx = to.X - from.X, dy = to.Y - from.Y;
        if (_tool is Tool.Box or Tool.Circle)
        {
            double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            return new PdfPoint(from.X + (dx < 0 ? -side : side), from.Y + (dy < 0 ? -side : side));
        }
        double length = Math.Sqrt(dx * dx + dy * dy), angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        return new PdfPoint(from.X + length * Math.Cos(angle), from.Y + length * Math.Sin(angle));
    }

    /// <summary>A click with the Spline tool adds a point; a double-click finishes the curve.</summary>
    private void SplineClick(Point p, int clickCount)
    {
        var at = new PdfPoint(p.X, p.Y);
        if (_shape is null || _shapePage != _page)
        {
            _shape = [at, at]; // the first point, and the one that follows the mouse
            _shapePage = _page;
        }
        else if (clickCount >= 2) { FinishSpline(); return; }
        else
        {
            _shape[^1] = at;
            _shape.Add(at);
        }
        DrawOverlay();
    }

    private void FinishSpline()
    {
        var points = _shape;
        _shape = null;
        if (points is null) return;
        points.RemoveAt(points.Count - 1); // the point that was following the mouse
        if (points.Count >= 2 && PdfDocument.ShapeOutline(ShapeKind.Spline, points, 1).Count > 0) AddShape(points);
        else DrawOverlay();
    }

    private void AddShape(List<PdfPoint> points)
    {
        int page = _page;
        var kind = CurrentShapeKind;
        var style = CurrentShapeStyle();
        _selection = null;
        Edit(d => d.AddShape(page, kind, points, style));
    }

    /// <summary>
    /// A right-click on a drawing offers to fill it, make it see-through or delete it. Returns
    /// false when there is no drawing at the point.
    /// </summary>
    private bool ShowDrawingMenu(Point p)
    {
        // Anywhere inside several drawings picked together is a right-click on all of them.
        var drawing = _selection is { Kind: ItemKind.Drawing } several && IsSeveralDrawings(several) && several.Bounds.Contains(p.X, p.Y) ? null : DrawingAt(p);
        if (drawing is null && !(_selection is { Kind: ItemKind.Drawing } kept && IsSeveralDrawings(kept) && kept.Bounds.Contains(p.X, p.Y))) return false;
        Focus();
        if (drawing is not null) Select(new Selection(ItemKind.Drawing, drawing.ObjectIndices[0], drawing.Bounds, drawing.ObjectIndices));

        int page = _page;
        var shapes = _selection!.Group!;
        var menu = new ContextMenu { PlacementTarget = Overlay };
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        // What the dialogs start from: the first shape in the drawing that has a look of its own.
        ShapeStyle? Look() => shapes.Select(i => Doc.GetShapeStyle(page, i)).FirstOrDefault(s => s is not null);

        Add("Filling...", () =>
        {
            if (!ShapeDialogs.AskFilling(this, Look()?.Fill, out var fill)) return;
            _selection = null;
            int changed = 0;
            if (!Edit(d => changed = d.SetShapeFill(page, shapes, fill))) return;
            ShowStatus(changed == 0 ? "Nothing to fill here: lines and arrows take no filling."
                : fill is null ? "The shape is see-through now. Undo puts the filling back." : "Filled. Undo takes the filling away again.");
        });
        Add("Line...", () =>
        {
            var look = Look();
            if (ShapeDialogs.AskLine(this, look?.Stroke ?? PdfColor.Black, look?.Width ?? 2) is not { } line) return;
            _selection = null;
            int changed = 0;
            if (Edit(d => changed = d.SetShapeLine(page, shapes, line.Colour, line.Width)) && changed == 0) ShowStatus("There is no line to change here.");
        });
        menu.Items.Add(new Separator());
        AddLayerItems(menu, shapes);

        // Shapes that touch are taken as one figure. Ungroup lets each be picked on its own; Group joins
        // shapes picked together. Either holds while the document is open (a PDF keeps no groups).
        var places = _shapes.Where(s => shapes.Contains(s.ObjectIndex)).Select(s => s.Bounds).ToList();
        int figures = _drawings.Count(d => d.ObjectIndices.Any(shapes.Contains));
        bool canUngroup = shapes.Count > 1 && _drawings.Any(d => d.ObjectIndices.Count > 1 && d.ObjectIndices.Any(shapes.Contains));
        bool canGroup = figures > 1;
        if (canUngroup || canGroup) menu.Items.Add(new Separator());
        if (canUngroup) Add("Ungroup", () =>
        {
            _shapeGroups.Ungroup(page, places);
            _selection = null;
            RefreshAll();
            ShowStatus($"Ungrouped: each of the {places.Count} shapes can now be picked on its own. Shapes that touch are one figure again when the file is next opened.");
        });
        if (canGroup) Add("Group", () =>
        {
            _shapeGroups.Group(page, places);
            RefreshAll();
            ShowStatus($"Grouped: the {places.Count} shapes are picked and moved as one figure while this file is open.");
        });
        menu.Items.Add(new Separator());
        Add("Delete", DeleteSelection);
        menu.IsOpen = true;
        return true;
    }

    /// <summary>The two "move one layer" entries, for whatever items a menu is about.</summary>
    private void AddLayerItems(ContextMenu menu, IReadOnlyList<int> objects)
    {
        int page = _page;
        void Add(string header, bool up)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                _selection = null;
                bool moved = false;
                if (!Edit(d => moved = d.MoveLayer(page, objects, up))) return;
                ShowStatus(moved ? (up ? "Moved one layer up." : "Moved one layer down.")
                    : up ? "Nothing lies over it: it is at the front already." : "Nothing lies under it: it is at the back already.");
            };
            menu.Items.Add(item);
        }
        Add("Move one layer up", true);
        Add("Move one layer down", false);
    }

    /// <summary>
    /// A right-click on text offers a new font and colour, turning, a move up or down a layer,
    /// and deleting. With a block of lines selected, it acts on the whole block.
    /// </summary>
    private void ShowTextMenu(Selection text)
    {
        Focus();
        if (_selection is { Kind: ItemKind.Text, Group: { } chosen } block && chosen.Contains(text.Index)) text = block; // keep the block
        else Select(text);
        int page = _page;
        IReadOnlyList<int> pieces = text.Group ?? [text.Index];

        var menu = new ContextMenu { PlacementTarget = Overlay };
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Font and colour...", () =>
        {
            var sample = _runs.FirstOrDefault(r => pieces.Contains(r.ObjectIndex));
            if (sample is null || ShapeDialogs.AskText(this, sample, pieces.Count) is not { } format) return;
            _selection = null;
            int changed = 0;
            if (Edit(d => changed = d.FormatText(page, pieces, format)))
                ShowStatus(changed == 0 ? "Nothing changed: this text already looks that way." : "The text has its new look. Undo brings the old one back.");
        });
        Add("Rotate right", () => { _selection = null; Edit(d => d.RotateObjects(page, pieces, clockwise: true)); });
        Add("Rotate left", () => { _selection = null; Edit(d => d.RotateObjects(page, pieces, clockwise: false)); });
        menu.Items.Add(new Separator());
        AddLayerItems(menu, pieces);
        menu.Items.Add(new Separator());
        Add("Delete", DeleteSelection);
        menu.IsOpen = true;
    }

    private void Watermark_Click(object sender, RoutedEventArgs e)
    {
        CommitEditor();
        EndPageBreak();
        if (ShapeDialogs.AskWatermark(this, Doc.PageCount) is not { } mark) return;
        int page = _page, pages = Doc.PageCount;
        _selection = null;
        bool added = Edit(d =>
        {
            if (!mark.EveryPage) d.AddWatermark(page, mark.Text, mark.Options);
            else for (int i = 0; i < pages; i++) d.AddWatermark(i, mark.Text, mark.Options);
        });
        if (added) ShowStatus(mark.EveryPage ? $"Watermark added to all {pages} pages. Undo takes it off." : "Watermark added. To take it off, click it with Select and press Delete, or Undo.");
    }

    // ------------------------------------------------------------------ typing on the page

    private void BeginEditingRun(Selection run)
    {
        var info = _runs.FirstOrDefault(r => r.ObjectIndex == run.Index);
        if (info is null) return;
        int page = _page;
        // The run's height on the page is a better guide to its visible size than the font's nominal size.
        double size = Math.Max(4, info.Bounds.Height * 0.85);
        // Turned text: its box is as tall as the line is long, so the size comes from the text itself,
        // and the editing box is turned the same way, starting at the corner where the line starts.
        double angle = Math.Abs(info.Angle) < 1 ? 0 : info.Angle, length = info.Bounds.Width;
        double startX = info.Bounds.X, startY = info.Bounds.Y;
        if (angle != 0)
        {
            if (info.VisibleSize > 0) size = Math.Max(4, info.VisibleSize);
            double radians = angle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
            length = Math.Abs(cos) * info.Bounds.Width + Math.Abs(sin) * info.Bounds.Height;
            // The top-left of the line as written: which corner of its box that is depends on the turn.
            if (sin > 0.5) startX = info.Bounds.Right;                                    // reads down the page
            else if (sin < -0.5) startY = info.Bounds.Bottom;                             // reads up the page
            else if (cos < 0) (startX, startY) = (info.Bounds.Right, info.Bounds.Bottom); // upside down
        }
        var style = new TextStyle
        {
            Font = info.FontName.Contains("Courier", StringComparison.OrdinalIgnoreCase) ? StandardFont.Courier
                 : info.FontName.Contains("Times", StringComparison.OrdinalIgnoreCase) ? StandardFont.Times : StandardFont.Helvetica,
            Bold = info.FontName.Contains("Bold", StringComparison.OrdinalIgnoreCase),
            Italic = info.FontName.Contains("Italic", StringComparison.OrdinalIgnoreCase) || info.FontName.Contains("Oblique", StringComparison.OrdinalIgnoreCase),
            Color = info.Color,
        };
        BeginTyping(startX, startY, info.Text, size, style, multiline: false, text =>
        {
            if (text == info.Text) return;
            _selection = null; // the run may get a new index, or be removed if the text is now empty
            Edit(d => d.EditText(page, info.ObjectIndex, text));
        }, minWidth: length, angle: angle);
    }

    private void BeginTyping(double x, double y, string initial, double fontSize, TextStyle style, bool multiline, Action<string> commit, double minWidth = 0, double wrapWidth = 0, double angle = 0)
    {
        DiscardEditor();
        var box = new TextBox
        {
            Style = new Style(typeof(TextBox)), // the plain system look: this box sits on the white page, not on the dark desk
            CaretBrush = Brushes.Black,
            TextWrapping = wrapWidth > 0 ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Text = initial,
            AcceptsReturn = multiline,
            FontSize = fontSize * ViewScale,
            FontFamily = new FontFamily(style.Font switch { StandardFont.Times => "Times New Roman", StandardFont.Courier => "Courier New", _ => "Arial" }),
            FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = new SolidColorBrush(ToColor(style.Color)),
            Background = new SolidColorBrush(Color.FromArgb(245, 255, 255, 255)),
            BorderBrush = Brushes.DodgerBlue,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            MinWidth = Math.Max(60, minWidth * ViewScale + 24),
        };
        if (wrapWidth > 0) box.Width = wrapWidth * ViewScale + 8;
        // The text box draws its text 3 units inside its edge; shift it so the text sits where it will land.
        Canvas.SetLeft(box, x * ViewScale - 3);
        Canvas.SetTop(box, y * ViewScale - 1);
        if (angle != 0) box.RenderTransform = new RotateTransform(angle, 3, 1); // about the point where the text starts
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DiscardEditor(); e.Handled = true; }
            else if (e.Key == Key.Enter && (!multiline || Keyboard.Modifiers == ModifierKeys.Control)) { CommitEditor(); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, e) =>
        {
            // Moving to the toolbar (to change font or colour) should not end typing; clicking the page does.
            if (ReferenceEquals(_editor, box) && e.NewFocus is not null && !IsInToolbar(e.NewFocus as DependencyObject)) CommitEditor();
        };
        _editor = box;
        _editorCommit = commit;
        Overlay.Children.Add(box);
        box.Focus();
        box.CaretIndex = box.Text.Length;
        if (initial.Length > 0) box.SelectAll();
    }

    /// <summary>
    /// Edits the whole paragraph under a point. When done, the paragraph re-flows: words re-wrap,
    /// alignment and fonts are kept, and what is below moves if it grows or shrinks.
    /// Returns false when there is no editable paragraph there.
    /// </summary>
    private bool BeginEditingParagraph(Point at)
    {
        TextParagraph? paragraph;
        try { paragraph = Doc.FindParagraphAt(_page, at.X, at.Y); }
        catch { return false; }
        if (paragraph is not { IsEditable: true }) return false;

        var main = paragraph.Spans.OrderByDescending(s => s.Length).First();
        var style = new TextStyle
        {
            Font = FamilyOf(main.FontName),
            Bold = main.Bold,
            Italic = main.Italic,
            Color = main.Color,
        };
        int page = _page;
        string original = paragraph.Text;
        BeginTyping(paragraph.Bounds.X, paragraph.Bounds.Y, original, paragraph.FontSize, style, multiline: true, text =>
        {
            text = text.Replace("\r\n", "\n");
            if (text == original) return;
            _selection = null;
            ReflowResult? result = null;
            if (!Edit(d => result = d.ReplaceParagraphText(page, paragraph.Index, text))) return;
            ReportReflow(result!);
        }, minWidth: paragraph.Bounds.Width, wrapWidth: paragraph.Bounds.Width);
        _editor!.SelectAll();
        ShowStatus("Editing the paragraph: it re-flows when you finish. Enter starts a new line; Ctrl+Enter or a click elsewhere finishes; Esc cancels.");
        return true;
    }

    private void ReportReflow(ReflowResult result)
    {
        var notes = new List<string> { $"Paragraph re-flowed onto {result.LineCount} line{(result.LineCount == 1 ? "" : "s")}." };
        if (result.MovedItems > 0) notes.Add($"{result.MovedItems} item{(result.MovedItems == 1 ? "" : "s")} below moved {(result.HeightChange > 0 ? "down" : "up")}.");
        if (result.LeftInPlace) notes.Add(result.HeightChange > 0 ? "This page is laid out in boxes or tables, so nothing below was moved: the paragraph may overlap what is under it. Drag things to make room." : "This page is laid out in boxes or tables, so nothing below was moved up.");
        if (result.UsedSubstituteFont) notes.Add("Some characters were not in the original font, so a similar font was used for them.");
        if (result.RunsOffPage) notes.Add("Warning: text now runs past the bottom of the page.");
        ShowStatus(string.Join(" ", notes));
    }

    private void ShowStatus(string message) => HintText.Text = message;

    private static bool IsRtf(string path) => path.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Says how an RTF import went, and what was left out.</summary>
    private void ReportImport(RtfImportResult result, string name)
    {
        string pages = $"{result.PageCount} page{(result.PageCount == 1 ? "" : "s")}";
        ShowStatus($"{name} was laid out as {pages}. Double-click a paragraph to edit it.");
        if (result.Warnings.Count > 0)
            MessageBox.Show(Window.GetWindow(this), $"{name} was imported as {pages}, but not everything could be brought across:\n\n• " + string.Join("\n• ", result.Warnings),
                "SimplePDF Editor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static StandardFont FamilyOf(string fontName) =>
        fontName.Contains("Courier", StringComparison.OrdinalIgnoreCase) || fontName.Contains("Mono", StringComparison.OrdinalIgnoreCase) ? StandardFont.Courier
        : (fontName.Contains("Times", StringComparison.OrdinalIgnoreCase) || fontName.Contains("Serif", StringComparison.OrdinalIgnoreCase))
          && !fontName.Contains("Sans", StringComparison.OrdinalIgnoreCase) ? StandardFont.Times
        : StandardFont.Helvetica;

    /// <summary>The paragraph the current selection belongs to.</summary>
    private TextParagraph? SelectedParagraph()
    {
        if (_selection is not { Kind: ItemKind.Text } s) return null;
        return Doc.FindParagraphAt(_page, s.Bounds.X + s.Bounds.Width / 2, s.Bounds.Y + s.Bounds.Height / 2);
    }

    private void Align_Click(object sender, RoutedEventArgs e)
    {
        CommitEditor();
        if (sender is not Button { Tag: string name }) return;
        var paragraph = SelectedParagraph();
        if (paragraph is null)
        {
            ShowStatus("Select some text first (Select tool), then choose an alignment for its paragraph.");
            return;
        }
        if (!paragraph.IsEditable)
        {
            ShowStatus("That paragraph cannot be re-flowed (it uses right-to-left script or unknown characters).");
            return;
        }
        int page = _page;
        _selection = null;
        ReflowResult? result = null;
        if (Edit(d => result = d.SetParagraphAlignment(page, paragraph.Index, Enum.Parse<PdfEditor.Core.TextAlignment>(name)))) ReportReflow(result!);
    }

    private void FindReplace_Click(object sender, RoutedEventArgs e)
    {
        CommitEditor();
        var find = TextPrompt.Show(this, "Find", multiline: false);
        if (string.IsNullOrEmpty(find)) return;
        int matches = 0;
        for (int i = 0; i < Doc.PageCount; i++)
            foreach (var p in Doc.GetParagraphs(i))
                for (int at = p.Text.IndexOf(find, StringComparison.OrdinalIgnoreCase); at >= 0; at = p.Text.IndexOf(find, at + find.Length, StringComparison.OrdinalIgnoreCase))
                    matches++;
        if (matches == 0)
        {
            ShowStatus($"\"{find}\" was not found.");
            return;
        }
        var replacement = TextPrompt.Show(this, $"Replace {matches} \"{find}\" with", multiline: false);
        if (replacement is null) return;
        _selection = null;
        int replaced = 0;
        if (Edit(d => replaced = d.ReplaceText(find, replacement)))
            ShowStatus($"Replaced {replaced} occurrence{(replaced == 1 ? "" : "s")}; the paragraphs they were in have re-flowed.");
    }

    private bool IsInToolbar(DependencyObject? element)
    {
        for (; element is not null; element = element is Visual ? VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (ReferenceEquals(element, Tools)) return true;
        return false;
    }

    /// <summary>Finishes typing and applies the text.</summary>
    private void CommitEditor()
    {
        if (_editor is not { } box) return;
        var commit = _editorCommit;
        string text = box.Text;
        DiscardEditor();
        commit?.Invoke(text);
    }

    /// <summary>Removes the text box without applying anything.</summary>
    private void DiscardEditor()
    {
        if (_editor is not { } box) return;
        _editor = null;         // cleared first: removing the box fires focus events that look at it
        _editorCommit = null;
        Overlay.Children.Remove(box);
    }

    // ------------------------------------------------------------------ comments

    private void AddComment(Point p)
    {
        var text = TextPrompt.Show(this, "New comment");
        if (string.IsNullOrWhiteSpace(text)) return;
        int page = _page;
        Edit(d => d.AddComment(page, p.X, p.Y, text, Environment.UserName));
    }

    private void EditAnnotationText(int index)
    {
        var note = _annotations.FirstOrDefault(a => a.Index == index);
        if (note is null) return;
        var text = TextPrompt.Show(this, note.Kind == AnnotationKind.Comment ? "Edit comment" : "Note on this markup", note.Contents);
        if (text is null || text == note.Contents) return;
        int page = _page;
        Edit(d => d.SetAnnotationText(page, index, text));
    }

    private void Notes_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var note = Notes.SelectedIndex >= 0 && Notes.SelectedIndex < _annotations.Count ? _annotations[Notes.SelectedIndex] : null;
        _selection = note is null ? null : new Selection(ItemKind.Annotation, note.Index, note.Bounds);
        DrawOverlay();
    }

    private void Notes_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_selection is { Kind: ItemKind.Annotation } s) EditAnnotationText(s.Index);
    }

    private void EditNote_Click(object sender, RoutedEventArgs e)
    {
        if (_selection is { Kind: ItemKind.Annotation } s) EditAnnotationText(s.Index);
    }

    private void DeleteNote_Click(object sender, RoutedEventArgs e) => DeleteSelection();

    private void DeleteSelection()
    {
        if (_selection is not { } s) return;
        int page = _page;
        _selection = null;
        if (s.Kind == ItemKind.Annotation) Edit(d => d.RemoveAnnotation(page, s.Index));
        else if (s.Group is { } shapes) Edit(d => d.RemoveObjects(page, shapes));
        else Edit(d => d.RemoveObject(page, s.Index));
    }

    // ------------------------------------------------------------------ keyboard and drag-and-drop

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool typing = e.OriginalSource is TextBox or ComboBox or PasswordBox;

        if (ctrl && e.Key == Key.O) Open_Click(this, e);
        else if (ctrl && e.Key == Key.S) Save(saveAs: shift);
        else if (ctrl && e.Key == Key.N) New_Click(this, e);
        else if (ctrl && e.Key == Key.H) FindReplace_Click(this, e);
        else if (ctrl && e.Key is Key.OemPlus or Key.Add) ZoomIn_Click(this, e);
        else if (ctrl && e.Key is Key.OemMinus or Key.Subtract) ZoomOut_Click(this, e);
        else if (typing) return; // leave everything else to the text box
        else if (ctrl && e.Key == Key.Z && !shift) Undo_Click(this, e);
        else if (ctrl && (e.Key == Key.Y || e.Key == Key.Z && shift)) Redo_Click(this, e);
        else if (e.Key == Key.Delete && _selection is not null) DeleteSelection();
        else if (e.Key == Key.Escape && _shape is not null) { _shape = null; if (_drag == Drag.Shape) { _drag = Drag.None; Overlay.ReleaseMouseCapture(); } DrawOverlay(); }
        else if (e.Key == Key.Enter && _tool == Tool.Spline && _shape is not null) FinishSpline();
        else if (e.Key == Key.Escape && _breakPositions is not null) { EndPageBreak(); UpdateHint(); }
        else if (e.Key == Key.Escape) Select(null);
        else if (e.Key == Key.PageDown) GoToPage(_page + 1);
        else if (e.Key == Key.PageUp) GoToPage(_page - 1);
        else return;
        e.Handled = true;
    }

    // ------------------------------------------------------------------ groups

    /// <summary>
    /// When a click lands on nothing selectable but inside a group (a header or logo block the
    /// PDF stores as one unit), takes the group apart so its parts can be selected, then
    /// returns what is now under the point. One undo step puts the group back.
    /// </summary>
    private Selection? UngroupAt(Point p)
    {
        GroupInfo? group;
        try
        {
            // Only when the click is on one of the group's items, not just inside its (often page-wide) bounds.
            group = Doc.GetGroups(_page).Where(g => g.Bounds.Contains(p.X, p.Y) && Doc.GroupItemAt(_page, g.ObjectIndex, p.X, p.Y))
                       .OrderBy(g => g.Bounds.Width * g.Bounds.Height).FirstOrDefault();
        }
        catch (Exception) { return null; }
        if (group is null) return null;
        int page = _page;
        try
        {
            _session.Apply(d =>
            {
                if (!d.Ungroup(page, group.ObjectIndex, out var problem)) throw new InvalidOperationException(problem);
            });
        }
        catch (Exception e)
        {
            RefreshAll();
            ShowStatus($"This part of the page is a group that cannot be taken apart ({e.Message}).");
            return null;
        }
        ShowStatus("A group was taken apart so its parts can be selected and moved. Text that could not stay as text became shapes. Undo puts the group back.");
        return HitTest(p);
    }

    // ------------------------------------------------------------------ page list: resizing and reordering

    private double _thumbWidth = 110;
    private System.Windows.Threading.DispatcherTimer? _thumbResize;
    private Point? _thumbDragStart;
    private int _thumbDragIndex = -1;
    private const string PageDragFormat = "PdfEditor.PageIndex";

    private void Thumbs_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = Math.Max(60, Thumbs.ActualWidth - 44); // room for the scroll bar and margins
        if (Math.Abs(width - _thumbWidth) < 4) return;
        _thumbWidth = width;
        // Redraw once the dragging settles, not for every pixel.
        _thumbResize ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _thumbResize.Stop();
        _thumbResize.Tick -= ThumbResize_Tick;
        _thumbResize.Tick += ThumbResize_Tick;
        _thumbResize.Start();
    }

    private void ThumbResize_Tick(object? sender, EventArgs e)
    {
        _thumbResize?.Stop();
        foreach (var item in _pages) item.Invalidate();
    }

    private int ThumbIndexAt(object? source)
    {
        if (source is not DependencyObject element) return -1;
        var container = ItemsControl.ContainerFromElement(Thumbs, element) as ListBoxItem;
        return container is null ? -1 : Thumbs.ItemContainerGenerator.IndexFromContainer(container);
    }

    private void Thumbs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _thumbDragIndex = ThumbIndexAt(e.OriginalSource);
        _thumbDragStart = _thumbDragIndex >= 0 ? e.GetPosition(Thumbs) : null;
    }

    private void Thumbs_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_thumbDragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) { _thumbDragStart = null; return; }
        var now = e.GetPosition(Thumbs);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        int index = _thumbDragIndex;
        _thumbDragStart = null;
        DragDrop.DoDragDrop(Thumbs, new DataObject(PageDragFormat, index), DragDropEffects.Move);
        DropLine.Visibility = Visibility.Collapsed; // the drag is over, wherever it ended
    }

    /// <summary>
    /// The gap between pages nearest the mouse: 0 is before the first page, the page count is
    /// after the last. Also gives the height at which to draw the line marking that gap.
    /// </summary>
    private int DropGapAt(DragEventArgs e, out double lineY)
    {
        double y = e.GetPosition(Thumbs).Y;
        lineY = 0;
        int gap = 0;
        for (int i = 0; i < _pages.Count; i++)
        {
            // Pages scrolled out of view have no visual; they cannot be under the mouse.
            if (Thumbs.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item) continue;
            double top = item.TranslatePoint(new Point(0, 0), Thumbs).Y;
            if (y < top + item.ActualHeight / 2)
            {
                lineY = top;
                return i;
            }
            lineY = top + item.ActualHeight;
            gap = i + 1;
        }
        return gap;
    }

    private void Thumbs_DragLeave(object sender, DragEventArgs e) => DropLine.Visibility = Visibility.Collapsed;

    private void Thumbs_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(PageDragFormat)) return; // a file being dropped: the editor handles it
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        DropGapAt(e, out double lineY);
        DropLine.Margin = new Thickness(6, Math.Clamp(lineY - 1.5, 0, Math.Max(0, Thumbs.ActualHeight - 3)), 6, 0);
        DropLine.Visibility = Visibility.Visible;
    }

    private void Thumbs_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(PageDragFormat) is not int from) return;
        e.Handled = true;
        DropLine.Visibility = Visibility.Collapsed;
        // The page lands in the gap the line marked; taking it out first shifts later gaps up by one.
        int gap = DropGapAt(e, out _);
        int to = gap > from ? gap - 1 : gap;
        if (from < 0 || from >= Doc.PageCount || to == from) return;
        CommitEditor();
        _selection = null;
        int previous = _page;
        _page = to;
        _shapeGroups.Clear(); // noted by page number, which no longer fits
        if (!Edit(d => d.MovePage(from, to))) { _page = previous; RefreshAll(); }
        else ShowStatus($"Page {from + 1} moved to position {to + 1}.");
    }

    // ------------------------------------------------------------------ image menu

    /// <summary>Right-clicking an image selects it and offers what can be done with it.</summary>
    private void Overlay_RightClick(object sender, MouseButtonEventArgs e)
    {
        CommitEditor();
        var p = PagePoint(e);
        var image = _images.LastOrDefault(i => i.Bounds.Contains(p.X, p.Y));
        if (image is null)
        {
            if (ShowGradientMenu(p)) { e.Handled = true; return; }
            // Anywhere inside a block of selected lines is a right-click on the block.
            if (_selection is { Kind: ItemKind.Text, Group: not null } block && block.Bounds.Contains(p.X, p.Y)) { ShowTextMenu(block); e.Handled = true; return; }
            if (HitTest(p) is { Kind: ItemKind.Text } text) { ShowTextMenu(text); e.Handled = true; return; }
            e.Handled = ShowDrawingMenu(p);
            return;
        }
        Focus();
        Select(new Selection(ItemKind.Image, image.ObjectIndex, image.Bounds));

        var menu = new ContextMenu { PlacementTarget = Overlay };
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Edit image in Paint...", () => EditImageInPaint(image));
        Add("Rotate right", () => RotateSelectedImage(image, 90));
        Add("Rotate left", () => RotateSelectedImage(image, -90));
        Add("Resize image...", () => ResizeImage(image));
        Add("Replace image...", () => ReplaceImage(image));
        menu.Items.Add(new Separator());
        AddLayerItems(menu, [image.ObjectIndex]);
        menu.Items.Add(new Separator());
        Add("Delete image", DeleteSelection);
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// A right-click on a shaded box (a gradient) offers to delete it. Boxes that are one flat
    /// colour all over, as some programs write a see-through effect, can all go at once.
    /// Returns false when there is no shaded box at the point.
    /// </summary>
    private bool ShowGradientMenu(Point p)
    {
        IReadOnlyList<GradientInfo> gradients;
        try { gradients = Doc.GetGradients(_page); }
        catch (PdfException) { return false; }
        var hit = gradients.Where(g => g.Bounds.Contains(p.X, p.Y)).OrderBy(g => g.Bounds.Width * g.Bounds.Height).FirstOrDefault();
        if (hit is null) return false;
        Focus();
        Select(new Selection(ItemKind.Drawing, hit.ObjectIndex, hit.Bounds, [hit.ObjectIndex]));

        int page = _page;
        var menu = new ContextMenu { PlacementTarget = Overlay };
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Delete this shaded box", () =>
        {
            _selection = null;
            if (Edit(d => d.RemoveObjects(page, [hit.ObjectIndex]))) ShowStatus("Shaded box deleted. Undo puts it back.");
        });
        if (hit.PlainColour is { } colour)
        {
            var alike = gradients.Where(g => g.PlainColour == colour).Select(g => g.ObjectIndex).ToList();
            if (alike.Count > 1)
            {
                string name = colour is { R: < 40, G: < 40, B: < 40 } ? "plain black" : colour is { R: > 215, G: > 215, B: > 215 } ? "plain white" : "one-colour";
                Add($"Delete all {alike.Count} {name} boxes like it on this page", () =>
                {
                    _selection = null;
                    if (Edit(d => d.RemoveObjects(page, alike))) ShowStatus($"{alike.Count} {name} boxes deleted. Undo puts them back.");
                });
            }
        }
        menu.IsOpen = true;
        return true;
    }

    private void RotateSelectedImage(ImageInfo image, int degrees)
    {
        int page = _page;
        Edit(d => d.RotateImage(page, image.ObjectIndex, degrees));
    }

    private void ResizeImage(ImageInfo image)
    {
        if (ImageSizeDialog.Show(this, image.Bounds.Width, image.Bounds.Height) is not { } size) return;
        int page = _page;
        // The top-left corner stays where it is.
        Edit(d => d.SetImageBounds(page, image.ObjectIndex, new PdfRect(image.Bounds.X, image.Bounds.Y, size.Width, size.Height)));
    }

    private void ReplaceImage(ImageInfo image)
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tga|All files|*.*", Title = "Replace the image with" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        byte[] data;
        try { data = File.ReadAllBytes(dialog.FileName); }
        catch (Exception ex) { ShowError("Could not read the image file.", ex); return; }
        int page = _page;
        if (Edit(d => d.ReplaceImage(page, image.ObjectIndex, data, asDisplayed: true)))
            ShowStatus("Image replaced. It keeps the old image's place; a picture of other proportions is fitted inside the old outline.");
    }

    /// <summary>
    /// Opens the image in Paint as a temporary PNG file. When the user has saved there and
    /// confirms here, the edited picture replaces the image. The temporary file is then deleted.
    /// </summary>
    private void EditImageInPaint(ImageInfo image)
    {
        int page = _page;
        string folder = Path.Combine(Path.GetTempPath(), "PdfEditor");
        string file = Path.Combine(folder, $"image-{Guid.NewGuid():N}.png");
        try
        {
            // The way it looks on the page: a scan stored sideways opens upright in Paint.
            var pixels = Doc.GetImagePixels(page, image.ObjectIndex, asDisplayed: true);
            Directory.CreateDirectory(folder);
            var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Pixels, pixels.Stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) encoder.Save(stream);
        }
        catch (Exception ex)
        {
            ShowError("The image could not be prepared for editing.", ex);
            return;
        }

        try
        {
            var written = File.GetLastWriteTimeUtc(file);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mspaint.exe", $"\"{file}\"") { UseShellExecute = true }); }
            catch (Exception ex)
            {
                ShowError("Paint could not be started.", ex);
                return;
            }
            var answer = MessageBox.Show(Window.GetWindow(this),
                "The image is open in Paint.\n\nMake your changes, save in Paint (Ctrl+S), then choose OK here to bring the edited image into the document.\n\nCancel leaves the image as it was.",
                "Edit image", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (answer != MessageBoxResult.OK) return;
            if (File.GetLastWriteTimeUtc(file) == written)
            {
                ShowStatus("The image was not saved in Paint, so nothing changed.");
                return;
            }
            byte[] edited;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                edited = memory.ToArray();
            }
            if (Edit(d => d.ReplaceImage(page, image.ObjectIndex, edited, asDisplayed: true))) ShowStatus("The edited image is in the document. Undo brings the old one back.");
        }
        catch (Exception ex)
        {
            ShowError("The edited image could not be brought back.", ex);
        }
        finally
        {
            try { File.Delete(file); } catch (Exception) { /* Paint may still hold it; it is in the temporary folder */ }
        }
    }

    // ------------------------------------------------------------------ comments panel and page breaks

    private void NotesToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = NotesPanel.Visibility != Visibility.Visible;
        NotesPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        NotesToggle.Content = show ? "»" : "«";
        NotesToggle.ToolTip = show ? "Hide the comments panel" : "Show comments and highlights";
    }

    /// <summary>Breaks the page above the selected item: it and everything below it go onto a new page.</summary>
    private void PageBreak_Click(object sender, RoutedEventArgs e)
    {
        CommitEditor();
        if (_breakPositions is not null) // choosing it a second time puts the line away
        {
            EndPageBreak();
            UpdateHint();
            return;
        }
        if (_selection is not { } s || s.Kind == ItemKind.Annotation)
        {
            BeginPageBreak();
            return;
        }
        // For text, break above its whole paragraph, not through the middle of it.
        double y = s.Bounds.Y;
        if (s.Kind == ItemKind.Text && SelectedParagraph() is { } paragraph) y = Math.Min(y, paragraph.Bounds.Y);
        int page = _page;
        int moved = 0;
        try
        {
            _session.Apply(d =>
            {
                moved = d.InsertPageBreak(page, y - 1);
                if (moved == 0) throw new InvalidOperationException("nothing to move");
            });
        }
        catch (InvalidOperationException)
        {
            RefreshAll();
            ShowStatus("Nothing could be moved: the selected item is in the footer area, which stays on its page.");
            return;
        }
        catch (Exception ex)
        {
            ShowError("The page break could not be made.", ex);
            RefreshAll();
            return;
        }
        _selection = null;
        _page = page + 1;
        RefreshAll();
        PageScroll.ScrollToTop();
        ShowStatus($"Page break added: {moved} item{(moved == 1 ? "" : "s")} moved to the new page {page + 2}. Undo puts them back.");
    }

    /// <summary>A page much longer than a sheet of paper (a web page saved as one page) is cut; others get a page of their own size.</summary>
    private bool IsLongPage(int page)
    {
        var size = Doc.GetPageSize(page);
        return size.Height > 1.7 * size.Width;
    }

    /// <summary>Shows a line that follows the mouse over the page; a click breaks the page there.</summary>
    private void BeginPageBreak()
    {
        _selection = null;
        _breakPositions = [.. Doc.GetBreakPositions(_page)];
        _breakY = double.NaN;
        Overlay.Cursor = Cursors.SizeNS;
        DrawOverlay();
        ShowStatus(IsLongPage(_page)
            ? "Move the line to where this long page should be cut and click. It settles in the gaps between rows. Esc cancels."
            : "Move the line to where the page should break and click: what is below goes to a new page. It settles in the gaps between rows. Esc cancels.");
    }

    private void Overlay_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_breakPositions is null || double.IsNaN(_breakY)) return;
        _breakY = double.NaN; // no line while the mouse is elsewhere
        DrawOverlay();
    }

    private void EndPageBreak()
    {
        if (_breakPositions is null) return;
        _breakPositions = null;
        _breakY = double.NaN;
        Overlay.Cursor = _tool switch { Tool.Text => Cursors.IBeam, Tool.Highlight => Cursors.Cross, Tool.Comment => Cursors.Pen, Tool.Select => Cursors.Arrow, _ => Cursors.Cross };
        DrawOverlay();
    }

    /// <summary>Puts the break line at a height (page points), settling into the nearest gap when one is close.</summary>
    private void MoveBreakLine(double y)
    {
        if (_breakPositions is null) return;
        double reach = 12 / ViewScale; // twelve pixels on screen
        double nearest = double.NaN;
        foreach (double position in _breakPositions)
            if (double.IsNaN(nearest) || Math.Abs(position - y) < Math.Abs(nearest - y)) nearest = position;
        _breakSnapped = !double.IsNaN(nearest) && Math.Abs(nearest - y) <= reach;
        double placed = Math.Clamp(_breakSnapped ? nearest : y, 0, Doc.GetPageSize(_page).Height);
        if (placed == _breakY) return;
        _breakY = placed;
        DrawOverlay();
    }

    private void BreakPageAt(double y)
    {
        if (double.IsNaN(y)) return;
        int page = _page;
        bool cut = IsLongPage(page);
        int moved = 0;
        try
        {
            _session.Apply(d =>
            {
                moved = cut ? d.SplitPage(page, y) : d.InsertPageBreak(page, y);
                if (moved == 0) throw new InvalidOperationException("nothing to move");
            });
        }
        catch (InvalidOperationException)
        {
            RefreshAll();
            ShowStatus("There is nothing below the line to put on a new page. Move the line higher, or press Esc.");
            return;
        }
        catch (Exception ex)
        {
            EndPageBreak();
            ShowError("The page break could not be made.", ex);
            RefreshAll();
            return;
        }
        _selection = null;
        _page = page + 1;
        // A long page is usually cut several times: keep the line for the rest of it.
        bool again = cut && IsLongPage(_page);
        if (!again) EndPageBreak();
        RefreshAll();
        PageScroll.ScrollToTop();
        string what = cut ? $"Page cut in two: the rest is now page {page + 2}." : $"Page break added: {moved} item{(moved == 1 ? "" : "s")} moved to the new page {page + 2}.";
        ShowStatus(what + (again ? " Click again to cut the rest, or press Esc to finish." : " Undo puts it back."));
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files &&
            (files[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || IsRtf(files[0])))
            OpenFile(files[0]);
    }

    // ------------------------------------------------------------------ colours

    private static void FillColours(ComboBox box, (string Name, PdfColor Colour)[] colours)
    {
        foreach (var (name, colour) in colours)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Rectangle { Width = 14, Height = 14, Fill = new SolidColorBrush(ToColor(colour)), Stroke = Brushes.Gray, StrokeThickness = 1, Margin = new Thickness(0, 0, 6, 0) });
            row.Children.Add(new TextBlock { Text = name });
            box.Items.Add(new ComboBoxItem { Content = row, Tag = colour });
        }
        if (box.SelectedIndex < 0) box.SelectedIndex = 0;
        else box.SelectedIndex = 0;
    }

    private static PdfColor CurrentColour(ComboBox box) => box.SelectedItem is ComboBoxItem { Tag: PdfColor colour } ? colour : PdfColor.Black;

    private static Color ToColor(PdfColor c, byte? alpha = null) => Color.FromArgb(alpha ?? c.A, c.R, c.G, c.B);
}

/// <summary>One entry in the page thumbnail list. The picture is only rendered when the list shows it.</summary>
internal sealed class PageItem(int index, PdfEditorControl owner) : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private double _width = 100, _height = 140;
    private bool _stale = true;

    public int Number => index + 1;
    public double ThumbWidth { get { Ensure(); return _width; } }
    public double ThumbHeight { get { Ensure(); return _height; } }
    public ImageSource? Thumbnail { get { Ensure(); return _thumbnail; } }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Marks the picture as out of date; it is redrawn the next time it is shown.</summary>
    public void Invalidate()
    {
        _stale = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private void Ensure()
    {
        if (!_stale) return;
        _stale = false;
        try { _thumbnail = owner.RenderThumbnail(index, out _width, out _height); }
        catch { _thumbnail = null; }
    }
}

/// <summary>One entry in the comments list.</summary>
internal sealed class NoteItem(AnnotationInfo note)
{
    public string Heading => note.Kind + (note.Author.Length > 0 ? " - " + note.Author : "");
    public string Body => note.Contents.Length > 0 ? note.Contents : note.Kind == AnnotationKind.Comment ? "(empty)" : "(no note)";
}
