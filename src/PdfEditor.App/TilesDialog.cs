using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PdfEditor.Core;

namespace PdfEditor.App;

/// <summary>What was chosen in the tiles dialog. Without sheets the options lay them out; with sheets, each was placed by hand.</summary>
internal sealed record TilesChoice(TileOptions Options, IReadOnlyList<TileSheet>? Sheets);

/// <summary>
/// Asks how a large page should be spread over sheets of paper for printing, showing the sheets
/// over a small picture of the page. Auto lays the sheets out in a grid; Manual lets each sheet
/// be added and dragged into place.
/// </summary>
internal static class TilesDialog
{
    private const double PointsPerMillimetre = 72 / 25.4;

    private static readonly (string Name, double Width, double Height)[] Papers =
    [
        ("A4", 210, 297), ("A3", 297, 420), ("A5", 148, 210), ("Letter", 215.9, 279.4), ("Legal", 215.9, 355.6),
    ];

    /// <summary>
    /// A sheet placed by hand: its top-left corner on the page (in the page's points), its paper
    /// (a place in <see cref="Papers"/>), which way it is turned and its blank edge in millimetres.
    /// </summary>
    private sealed class ManualSheet
    {
        public double X, Y;
        public int Paper;
        public bool Landscape;
        public double Margin = 5;
        public bool MarginOk = true; // false while what is typed for the blank edge cannot be used
    }

    /// <summary>Returns what was chosen, or null when cancelled. The picture is the page, drawn small.</summary>
    public static TilesChoice? Show(DependencyObject owner, PdfSize page, ImageSource? picture)
    {
        var window = new Window
        {
            Title = "Print as tiles",
            Owner = Window.GetWindow(owner),
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };

        var auto = new RadioButton { Content = "Auto", GroupName = "TileMode", IsChecked = true, Margin = new Thickness(0, 0, 14, 0), ToolTip = "Lay the sheets out in a grid" };
        var manual = new RadioButton { Content = "Manual", GroupName = "TileMode", ToolTip = "Add sheets one at a time and drag each into place" };

        var paper = new ComboBox { Width = 190 };
        foreach (var (name, w, h) in Papers) paper.Items.Add($"{name}  ({Format(w)} × {Format(h)} mm)");
        paper.SelectedIndex = 0;
        var portrait = new RadioButton { Content = "Portrait", GroupName = "TilePaper", IsChecked = true, Margin = new Thickness(0, 0, 14, 0) };
        var landscape = new RadioButton { Content = "Landscape", GroupName = "TilePaper" };

        var actual = new RadioButton { Content = "Real size", GroupName = "TileSize", IsChecked = true, Margin = new Thickness(0, 0, 0, 6) };
        var fit = new RadioButton { Content = "Fit on", GroupName = "TileSize", VerticalAlignment = VerticalAlignment.Center };
        var across = new TextBox { Width = 34, Text = "2", Margin = new Thickness(6, 0, 6, 0) };
        var down = new TextBox { Width = 34, Text = "2", Margin = new Thickness(6, 0, 6, 0) };
        var scaled = new RadioButton { Content = "Scale to", GroupName = "TileSize", VerticalAlignment = VerticalAlignment.Center };
        var percent = new TextBox { Width = 50, Text = "100", Margin = new Thickness(6, 0, 6, 0) };
        var manualPercent = new TextBox { Width = 50, Text = "100", Margin = new Thickness(6, 0, 6, 0) };

        var add = new Button { Content = "+", Width = 36, Height = 28, FontSize = 17, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Add a sheet" };
        var remove = new Button { Content = "−", Width = 36, Height = 28, FontSize = 17, Padding = new Thickness(0), ToolTip = "Remove the selected sheet (Delete)" };
        AutomationProperties.SetName(add, "Add a sheet");
        AutomationProperties.SetName(remove, "Remove the selected sheet");

        var overlap = new TextBox { Width = 50, Text = "10" };
        var margin = new TextBox { Width = 50, Text = "5" };
        var marks = new CheckBox { Content = "Cut marks and sheet numbers in the margins", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Width = 320, Margin = new Thickness(0, 12, 0, 0), MinHeight = 52 };
        var ok = new Button { Content = "Save tiles", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };

        var view = new SheetsView { Width = 460, Height = 520, Page = page, Picture = picture, Focusable = true, FocusVisualStyle = null };
        var placed = new List<ManualSheet>();
        int selected = -1;
        var rows = new StackPanel();          // the table of sheets: one row each
        var rowBorders = new List<Border>();

        // The paper as it stands in the list, upright, in points.
        (double W, double H) Paper()
        {
            var (_, w, h) = Papers[Math.Max(0, paper.SelectedIndex)];
            return (w * PointsPerMillimetre, h * PointsPerMillimetre);
        }

        // What Auto and Manual share, or null (with the reason) when something typed is not usable.
        TileOptions? ReadShared(out string problem)
        {
            problem = "";
            if (!TryParse(overlap.Text, out double o) || o < 0 || o > 50) { problem = "Enter an overlap between 0 and 50 mm."; return null; }
            if (!TryParse(margin.Text, out double m) || m < 0 || m > 30) { problem = "Enter a blank edge between 0 and 30 mm."; return null; }
            var (w, h) = Paper();
            return new TileOptions
            {
                PaperWidth = w, PaperHeight = h,
                Overlap = o * PointsPerMillimetre, Margin = m * PointsPerMillimetre, CutMarks = marks.IsChecked == true,
            };
        }

        TileOptions? ReadAuto(out string problem)
        {
            if (ReadShared(out problem) is not { } options) return null;
            if (landscape.IsChecked == true) options = options with { PaperWidth = options.PaperHeight, PaperHeight = options.PaperWidth };
            try
            {
                if (fit.IsChecked == true)
                {
                    if (!int.TryParse(across.Text, out int n) || !int.TryParse(down.Text, out int k) || n < 1 || k < 1 || n > 20 || k > 20)
                    {
                        problem = "Enter how many sheets across and down, from 1 to 20.";
                        return null;
                    }
                    options = options with { Scale = PdfDocument.TileScaleToFit(page, options, n, k) };
                }
                else if (scaled.IsChecked == true)
                {
                    if (!TryParse(percent.Text, out double p) || p < 5 || p > 2000) { problem = "Enter a scale between 5 and 2000 per cent."; return null; }
                    options = options with { Scale = p / 100 };
                }
                var plan = PdfDocument.PlanTiles(page, options);
                if (plan.Sheets > 400) { problem = $"That would take {plan.Sheets} sheets. Choose a smaller size or larger paper."; return null; }
            }
            catch (ArgumentException) { problem = "The blank edge and overlap leave too little of the paper to print on."; return null; }
            return options;
        }

        TileOptions? ReadManual(out string problem)
        {
            // Each sheet has its own paper and blank edge, so only the overlap and scale are shared.
            problem = "";
            if (!TryParse(overlap.Text, out double o) || o < 0 || o > 50) { problem = "Enter an overlap between 0 and 50 mm."; return null; }
            if (!TryParse(manualPercent.Text, out double p) || p < 5 || p > 2000) { problem = "Enter a scale between 5 and 2000 per cent."; return null; }
            int bad = placed.FindIndex(s => !s.MarginOk);
            if (bad >= 0) { problem = $"Enter a blank edge between 0 and 30 mm for sheet {bad + 1}."; return null; }
            return new TileOptions { Overlap = o * PointsPerMillimetre, Margin = 0, Scale = p / 100, CutMarks = marks.IsChecked == true };
        }

        // A hand-placed sheet as the engine wants it.
        TileSheet Sheet(ManualSheet s)
        {
            var (_, w, h) = Papers[s.Paper];
            if (s.Landscape) (w, h) = (h, w);
            return new TileSheet(s.X, s.Y, w * PointsPerMillimetre, h * PointsPerMillimetre, s.Margin * PointsPerMillimetre);
        }

        // In Manual the page is shown with room around it, so a sheet can hang over any edge.
        Rect ManualBounds(TileOptions o)
        {
            double longest = placed.Count == 0 ? 297 : placed.Max(s => Math.Max(Papers[s.Paper].Width, Papers[s.Paper].Height));
            double room = longest * PointsPerMillimetre / o.Scale / 2;
            return new Rect(-room, -room, page.Width + 2 * room, page.Height + 2 * room);
        }

        void KeepInView(TileOptions o)
        {
            var bounds = ManualBounds(o);
            foreach (var s in placed)
            {
                var sheet = Sheet(s);
                double w = sheet.PaperWidth / o.Scale, h = sheet.PaperHeight / o.Scale;
                s.X = Math.Max(bounds.Left, Math.Min(bounds.Right - w, s.X));
                s.Y = Math.Max(bounds.Top, Math.Min(bounds.Bottom - h, s.Y));
            }
        }

        void Update()
        {
            bool isManual = manual.IsChecked == true;
            across.IsEnabled = down.IsEnabled = fit.IsChecked == true;
            percent.IsEnabled = scaled.IsChecked == true;
            remove.IsEnabled = isManual && selected >= 0;
            view.Selected = isManual ? selected : -1;
            view.Numbered = isManual;

            if (isManual)
            {
                if (ReadManual(out string problem) is not { } options)
                {
                    summary.Text = problem;
                    ok.IsEnabled = add.IsEnabled = false;
                    view.InvalidateVisual();
                    return;
                }
                add.IsEnabled = placed.Count < 400;
                KeepInView(options);
                var sheets = placed.Select(Sheet).ToList();
                view.Show(sheets, options, ManualBounds(options));
                ok.IsEnabled = sheets.Count > 0;
                if (sheets.Count == 0) summary.Text = "Choose + to add a sheet, then drag it into place over the page.";
                else
                {
                    double missing = Uncovered(page, sheets, options);
                    summary.Text = $"{sheets.Count} sheet{(sheets.Count == 1 ? "" : "s")} at {Math.Round(options.Scale * 100):0}% of the page's size. " +
                                   (missing < 0.0005 ? "The whole page is covered." : $"{Math.Max(0.1, Math.Round(missing * 100, 1)).ToString("0.#", CultureInfo.CurrentCulture)}% of the page is on no sheet.");
                }
                return;
            }

            if (ReadAuto(out string trouble) is not { } grid)
            {
                summary.Text = trouble;
                ok.IsEnabled = false;
                view.InvalidateVisual();
                return;
            }
            var plan = PdfDocument.PlanTiles(page, grid);
            var laid = PdfDocument.TileSheets(page, grid);
            var all = new Rect(0, 0, page.Width, page.Height);
            foreach (var s in laid) all.Union(new Rect(s.X, s.Y, s.PaperWidth / grid.Scale, s.PaperHeight / grid.Scale));
            all.Inflate(all.Width * 0.02, all.Height * 0.02);
            view.Show(laid, grid, all);
            ok.IsEnabled = true;
            summary.Text = $"{plan.Sheets} sheet{(plan.Sheets == 1 ? "" : "s")}: {plan.Columns} across, {plan.Rows} down. " +
                           $"The poster will be {Format(plan.PosterWidth / PointsPerMillimetre)} × {Format(plan.PosterHeight / PointsPerMillimetre)} mm, " +
                           $"{Math.Round(plan.Scale * 100):0}% of the page's size.";
        }

        // The selected sheet's row is picked out in the table.
        void MarkRows()
        {
            for (int i = 0; i < rowBorders.Count; i++)
                rowBorders[i].Background = i == selected ? Lookup("SelectBrush", Brushes.DarkSlateGray) : Brushes.Transparent;
        }

        void Select(int index, bool show = true)
        {
            selected = index;
            MarkRows();
            if (show && index >= 0 && index < rowBorders.Count) rowBorders[index].BringIntoView();
            Update();
        }

        // One row of the table for each sheet: its number, paper, which way up, and blank edge.
        void RebuildRows()
        {
            rows.Children.Clear();
            rowBorders.Clear();
            foreach (var s in placed)
            {
                var sheet = s;
                var number = new TextBlock { Text = (placed.IndexOf(s) + 1).ToString(CultureInfo.CurrentCulture), Width = 22, VerticalAlignment = VerticalAlignment.Center, TextAlignment = System.Windows.TextAlignment.Center };
                var size = new ComboBox { Width = 78, Margin = new Thickness(0, 0, 4, 0), ToolTip = "The paper this sheet is printed on" };
                foreach (var (name, _, _) in Papers) size.Items.Add(name);
                size.SelectedIndex = s.Paper;
                var way = new ComboBox { Width = 104, Margin = new Thickness(0, 0, 4, 0) };
                way.Items.Add("Portrait");
                way.Items.Add("Landscape");
                way.SelectedIndex = s.Landscape ? 1 : 0;
                var edge = new TextBox { Width = 44, Text = s.Margin.ToString("0.#", CultureInfo.CurrentCulture), ToolTip = "The blank edge this sheet's printer cannot reach, in millimetres" };
                AutomationProperties.SetName(size, $"Paper for sheet {number.Text}");
                AutomationProperties.SetName(way, $"Which way up for sheet {number.Text}");
                AutomationProperties.SetName(edge, $"Blank edge for sheet {number.Text}");
                size.SelectionChanged += (_, _) => { sheet.Paper = Math.Max(0, size.SelectedIndex); Update(); };
                way.SelectionChanged += (_, _) => { sheet.Landscape = way.SelectedIndex == 1; Update(); };
                edge.TextChanged += (_, _) =>
                {
                    sheet.MarginOk = TryParse(edge.Text, out double m) && m >= 0 && m <= 30;
                    if (sheet.MarginOk) sheet.Margin = m;
                    Update();
                };

                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(number);
                line.Children.Add(size);
                line.Children.Add(way);
                line.Children.Add(edge);
                line.Children.Add(new TextBlock { Text = "mm", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) });
                var border = new Border { Child = line, Padding = new Thickness(0, 3, 0, 3), Background = Brushes.Transparent };
                // Working in a row, or clicking it, selects its sheet.
                void Pick() { int i = placed.IndexOf(sheet); if (i >= 0 && i != selected) Select(i, show: false); }
                border.PreviewMouseLeftButtonDown += (_, _) => Pick();
                border.GotKeyboardFocus += (_, _) => Pick();
                rows.Children.Add(border);
                rowBorders.Add(border);
            }
        }

        void AddSheet()
        {
            if (ReadManual(out _) is not { } o) return;
            // A new sheet is like the selected one (or the last); the first takes Auto's paper and blank edge.
            var like = selected >= 0 && selected < placed.Count ? placed[selected] : placed.LastOrDefault();
            var sheet = new ManualSheet
            {
                Paper = like?.Paper ?? Math.Max(0, paper.SelectedIndex),
                Landscape = like?.Landscape ?? landscape.IsChecked == true,
                Margin = like?.Margin ?? (TryParse(margin.Text, out double typed) && typed >= 0 && typed <= 30 ? typed : 5),
            };
            var own = Sheet(sheet);
            double edge = sheet.Margin * PointsPerMillimetre / o.Scale, w = own.PaperWidth / o.Scale, h = own.PaperHeight / o.Scale;
            if (placed.Count == 0) (sheet.X, sheet.Y) = (-edge, -edge); // its printed area starts at the page's corner
            else
            {
                // Beside the last one, their printed areas sharing the overlap; onto a new row once past the page's right edge.
                var last = Sheet(placed[^1]);
                double lastEdge = placed[^1].Margin * PointsPerMillimetre / o.Scale, shared = o.Overlap / o.Scale;
                sheet.X = last.X + last.PaperWidth / o.Scale - lastEdge - shared - edge;
                sheet.Y = last.Y + lastEdge - edge;
                // "Past the edge" is when less than a quarter of its printed area would still be on the page.
                if (sheet.X + edge + (w - 2 * edge) / 4 > page.Width)
                {
                    sheet.X = -edge;
                    sheet.Y = last.Y + last.PaperHeight / o.Scale - lastEdge - shared - edge;
                    // No room below either: in the middle of the page, for the user to place.
                    if (sheet.Y + edge + (h - 2 * edge) / 4 > page.Height) (sheet.X, sheet.Y) = ((page.Width - w) / 2, (page.Height - h) / 2);
                }
            }
            placed.Add(sheet);
            RebuildRows();
            Select(placed.Count - 1);
            view.Focus();
        }

        void RemoveSheet()
        {
            if (selected < 0 || selected >= placed.Count) return;
            placed.RemoveAt(selected);
            RebuildRows();
            Select(Math.Min(selected, placed.Count - 1));
        }

        // Dragging a sheet over the page.
        Vector grab = default;
        bool dragging = false;
        view.MouseLeftButtonDown += (_, e) =>
        {
            view.Focus();
            if (manual.IsChecked != true) return;
            var at = view.ToPage(e.GetPosition(view));
            int hit = view.SheetAt(at, selected);
            Select(hit);
            if (hit < 0) return;
            grab = at - new Point(placed[hit].X, placed[hit].Y);
            dragging = view.CaptureMouse();
            e.Handled = true;
        };
        view.MouseMove += (_, e) =>
        {
            if (!dragging || selected < 0) return;
            var at = view.ToPage(e.GetPosition(view));
            (placed[selected].X, placed[selected].Y) = (at.X - grab.X, at.Y - grab.Y);
            Update();
        };
        view.MouseLeftButtonUp += (_, _) =>
        {
            if (!dragging) return;
            dragging = false;
            view.ReleaseMouseCapture();
        };
        view.LostMouseCapture += (_, _) => dragging = false;
        view.KeyDown += (_, e) =>
        {
            if (manual.IsChecked != true || selected < 0) return;
            if (e.Key == Key.Delete) { RemoveSheet(); e.Handled = true; return; }
            if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down) || ReadManual(out string _) is not { } o) return;
            // One millimetre on the paper each press; ten with Shift.
            double step = (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1) * PointsPerMillimetre / o.Scale;
            if (e.Key == Key.Left) placed[selected].X -= step;
            if (e.Key == Key.Right) placed[selected].X += step;
            if (e.Key == Key.Up) placed[selected].Y -= step;
            if (e.Key == Key.Down) placed[selected].Y += step;
            e.Handled = true;
            Update();
        };

        add.Click += (_, _) => AddSheet();
        remove.Click += (_, _) => RemoveSheet();
        paper.SelectionChanged += (_, _) => Update();
        foreach (var radio in new[] { actual, fit, scaled }) radio.Checked += (_, _) => Update();
        foreach (var radio in new[] { portrait, landscape }) radio.Checked += (_, _) => Update();
        foreach (var box in new[] { across, down, percent, manualPercent, overlap, margin }) box.TextChanged += (_, _) => Update();
        marks.Click += (_, _) => Update();

        var panel = new StackPanel { Margin = new Thickness(14), Width = 320 };
        TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) };
        StackPanel Row(params UIElement[] items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var item in items) row.Children.Add(item);
            return row;
        }
        TextBlock Words(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
        TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.65, Margin = new Thickness(0, 0, 0, 6) };

        panel.Children.Add(Row(auto, manual));
        var paperPart = new StackPanel();
        paperPart.Children.Add(Heading("Paper to print on"));
        paperPart.Children.Add(Row(paper));
        paperPart.Children.Add(Row(portrait, landscape));
        panel.Children.Add(paperPart);
        panel.Children.Add(Heading("Size of the poster"));
        var autoSize = new StackPanel();
        autoSize.Children.Add(actual);
        autoSize.Children.Add(Row(fit, across, Words("sheets across and"), down, Words("down")));
        autoSize.Children.Add(Row(scaled, percent, Words("%")));
        var manualSize = new StackPanel { Visibility = Visibility.Collapsed };
        manualSize.Children.Add(Row(Words("Scale to"), manualPercent, Words("%")));
        panel.Children.Add(autoSize);
        panel.Children.Add(manualSize);

        var sheetsPart = new StackPanel { Visibility = Visibility.Collapsed };
        sheetsPart.Children.Add(Heading("Sheets"));
        sheetsPart.Children.Add(Row(add, remove));
        TextBlock Column(string text, double width) => new() { Text = text, Width = width, Opacity = 0.65, FontSize = 12 };
        sheetsPart.Children.Add(Row(Column("", 22), Column("Paper", 82), Column("Way up", 108), Column("Blank edge", 80)));
        sheetsPart.Children.Add(new ScrollViewer
        {
            Content = rows, Height = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 6), Focusable = false,
        });
        sheetsPart.Children.Add(Note("Drag a sheet to place it; the arrow keys move it 1 mm. A new sheet starts like the selected one."));
        panel.Children.Add(sheetsPart);

        panel.Children.Add(Heading("Joining the sheets"));
        var overlapLabel = Words("Overlap between sheets");
        overlapLabel.Width = 235;
        var marginLabel = Words("Blank edge the printer cannot reach");
        marginLabel.Width = 235;
        var mm1 = Words("mm");
        mm1.Margin = new Thickness(6, 0, 0, 0);
        var mm2 = Words("mm");
        mm2.Margin = new Thickness(6, 0, 0, 0);
        panel.Children.Add(Row(overlapLabel, overlap, mm1));
        var marginRow = Row(marginLabel, margin, mm2);
        panel.Children.Add(marginRow);
        panel.Children.Add(marks);
        panel.Children.Add(summary);

        foreach (var radio in new[] { auto, manual })
            radio.Checked += (_, _) =>
            {
                bool isManual = manual.IsChecked == true;
                autoSize.Visibility = isManual ? Visibility.Collapsed : Visibility.Visible;
                manualSize.Visibility = sheetsPart.Visibility = isManual ? Visibility.Visible : Visibility.Collapsed;
                // In Manual each sheet has its own paper and blank edge, in the table.
                paperPart.Visibility = marginRow.Visibility = isManual ? Visibility.Collapsed : Visibility.Visible;
                overlapLabel.Text = isManual ? "Overlap for the next sheet added" : "Overlap between sheets";
                Update();
            };

        TilesChoice? result = null;
        ok.Click += (_, _) =>
        {
            if (manual.IsChecked == true)
            {
                if (ReadManual(out _) is { } o && placed.Count > 0) result = new TilesChoice(o, placed.Select(Sheet).ToList());
            }
            else if (ReadAuto(out _) is { } o) result = new TilesChoice(o, null);
            if (result is not null) window.DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        // The page with its sheets, and what the colours mean.
        var legend = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Width = 460 };
        void Swatch(Color colour, string text)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 2) };
            item.Children.Add(new Border { Width = 12, Height = 12, Background = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B)), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = text, Opacity = 0.8, FontSize = 12 });
            legend.Children.Add(item);
        }
        Swatch(SheetsView.Green, "two blank edges overlap");
        Swatch(SheetsView.Orange, "a blank edge over a printed area");
        Swatch(SheetsView.Red, "two printed areas overlap");

        var right = new StackPanel { Margin = new Thickness(0, 14, 14, 14) };
        right.Children.Add(new Border { BorderThickness = new Thickness(1), BorderBrush = Lookup("LineBrush", Brushes.Gray), Child = view });
        right.Children.Add(legend);
        right.Children.Add(buttons);

        var whole = new StackPanel { Orientation = Orientation.Horizontal };
        whole.Children.Add(panel);
        whole.Children.Add(right);
        window.Content = whole;
        window.Loaded += (_, _) => { Update(); paper.Focus(); };
        return window.ShowDialog() == true ? result : null;
    }

    /// <summary>The share of the page that lies on no sheet's printed area (0 to 1).</summary>
    private static double Uncovered(PdfSize page, IReadOnlyList<TileSheet> sheets, TileOptions o)
    {
        if (sheets.Count > 60) return 0; // too many to be worth working out on every mouse move
        var areas = sheets.Select(s =>
        {
            double edge = (s.Margin ?? o.Margin) / o.Scale;
            return new Rect(s.X + edge, s.Y + edge, Math.Max(0, s.PaperWidth / o.Scale - 2 * edge), Math.Max(0, s.PaperHeight / o.Scale - 2 * edge));
        }).ToList();
        var xs = new SortedSet<double> { 0, page.Width };
        var ys = new SortedSet<double> { 0, page.Height };
        foreach (var a in areas)
        {
            foreach (double x in new[] { a.Left, a.Right }) if (x > 0 && x < page.Width) xs.Add(x);
            foreach (double y in new[] { a.Top, a.Bottom }) if (y > 0 && y < page.Height) ys.Add(y);
        }
        var xl = xs.ToList();
        var yl = ys.ToList();
        double covered = 0;
        for (int i = 0; i + 1 < xl.Count; i++)
            for (int j = 0; j + 1 < yl.Count; j++)
            {
                var middle = new Point((xl[i] + xl[i + 1]) / 2, (yl[j] + yl[j + 1]) / 2);
                if (areas.Any(a => a.Contains(middle))) covered += (xl[i + 1] - xl[i]) * (yl[j + 1] - yl[j]);
            }
        return Math.Max(0, 1 - covered / (page.Width * page.Height));
    }

    private static Brush Lookup(string key, Brush fallback) => Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private static string Format(double millimetres) => Math.Round(millimetres, 1).ToString("0.#", CultureInfo.CurrentCulture);

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// The page drawn small with the sheets over it. Each sheet is a light tint with its printed
    /// area darker; where sheets lie over each other the colour says what meets what.
    /// </summary>
    private sealed class SheetsView : FrameworkElement
    {
        public static readonly Color Green = Color.FromArgb(125, 40, 175, 70);     // blank edge over blank edge
        public static readonly Color Orange = Color.FromArgb(140, 255, 150, 0);    // blank edge over a printed area
        public static readonly Color Red = Color.FromArgb(125, 225, 40, 40);       // printed area over printed area

        private static readonly Brush PaperTint = Frozen(Color.FromArgb(38, 50, 110, 220));
        private static readonly Brush PrintedTint = Frozen(Color.FromArgb(52, 50, 110, 220));
        private static readonly Brush GreenBrush = Frozen(Green), OrangeBrush = Frozen(Orange), RedBrush = Frozen(Red);
        private static readonly Pen Outline = FrozenPen(Color.FromArgb(210, 50, 100, 200), 1);
        private static readonly Pen SelectedOutline = FrozenPen(Color.FromRgb(20, 60, 170), 2.5);
        private static readonly Pen PageEdge = FrozenPen(Color.FromRgb(120, 120, 120), 1);

        public PdfSize Page;
        public ImageSource? Picture;
        public int Selected = -1;
        public bool Numbered;

        private IReadOnlyList<TileSheet> _sheets = [];
        private double _scale = 1, _margin;     // the blank edge of sheets without their own, in paper points
        private Rect _bounds = new(0, 0, 1, 1); // the part of the page's plane that is shown

        public void Show(IReadOnlyList<TileSheet> sheets, TileOptions options, Rect bounds)
        {
            (_sheets, _scale, _margin, _bounds) = (sheets, options.Scale, options.Margin, bounds);
            InvalidateVisual();
        }

        private double Zoom => Math.Min(ActualWidth / _bounds.Width, ActualHeight / _bounds.Height);

        private Point Corner
        {
            get
            {
                double k = Zoom;
                return new Point((ActualWidth - _bounds.Width * k) / 2 - _bounds.X * k, (ActualHeight - _bounds.Height * k) / 2 - _bounds.Y * k);
            }
        }

        /// <summary>A point of this view, as a point on the page.</summary>
        public Point ToPage(Point p)
        {
            var c = Corner;
            double k = Zoom;
            return new Point((p.X - c.X) / k, (p.Y - c.Y) / k);
        }

        private Rect ToView(Rect r)
        {
            var c = Corner;
            double k = Zoom;
            return new Rect(c.X + r.X * k, c.Y + r.Y * k, r.Width * k, r.Height * k);
        }

        private Rect PaperOf(TileSheet s) => new(s.X, s.Y, s.PaperWidth / _scale, s.PaperHeight / _scale);

        private Rect PrintedOf(TileSheet s)
        {
            var p = PaperOf(s);
            double edge = (s.Margin ?? _margin) / _scale; // in the page's points
            return new Rect(p.X + edge, p.Y + edge, Math.Max(0, p.Width - 2 * edge), Math.Max(0, p.Height - 2 * edge));
        }

        /// <summary>The sheet under a point of the page: the one given first if it is there, otherwise the last added. -1 for none.</summary>
        public int SheetAt(Point pagePoint, int preferred)
        {
            if (preferred >= 0 && preferred < _sheets.Count && PaperOf(_sheets[preferred]).Contains(pagePoint)) return preferred;
            for (int i = _sheets.Count - 1; i >= 0; i--) if (PaperOf(_sheets[i]).Contains(pagePoint)) return i;
            return -1;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var all = new Rect(0, 0, ActualWidth, ActualHeight);
            dc.DrawRectangle(Lookup("BgBrush", Brushes.Black), null, all); // also makes the whole view answer the mouse
            if (ActualWidth < 1 || ActualHeight < 1) return;
            dc.PushClip(new RectangleGeometry(all));

            var page = ToView(new Rect(0, 0, Page.Width, Page.Height));
            dc.DrawRectangle(Brushes.White, PageEdge, page);
            if (Picture is not null) dc.DrawImage(Picture, page);

            var papers = _sheets.Select(s => ToView(PaperOf(s))).ToList();
            var printed = _sheets.Select(s => ToView(PrintedOf(s))).ToList();
            for (int i = 0; i < papers.Count; i++)
            {
                dc.DrawRectangle(PaperTint, null, papers[i]);
                dc.DrawRectangle(PrintedTint, null, printed[i]);
            }

            // Where two sheets lie over each other. Each sheet's blank edge is four strips, so every
            // meeting is one rectangle with another.
            var green = new List<Rect>();
            var orange = new List<Rect>();
            var red = new List<Rect>();
            var edges = papers.Select((p, i) => Strips(p, printed[i])).ToList();
            for (int i = 0; i < papers.Count; i++)
                for (int j = i + 1; j < papers.Count; j++)
                {
                    if (!papers[i].IntersectsWith(papers[j])) continue;
                    Meet(printed[i], printed[j], red);
                    foreach (var a in edges[i])
                    {
                        Meet(a, printed[j], orange);
                        foreach (var b in edges[j]) Meet(a, b, green);
                    }
                    foreach (var b in edges[j]) Meet(printed[i], b, orange);
                }
            // Each colour is laid down once, however many sheets meet there, so the page stays visible beneath.
            void Fill(Brush brush, List<Rect> rects)
            {
                if (rects.Count == 0) return;
                var shape = new GeometryGroup { FillRule = FillRule.Nonzero };
                foreach (var r in rects) shape.Children.Add(new RectangleGeometry(r));
                dc.DrawGeometry(brush, null, shape);
            }
            Fill(GreenBrush, green);
            Fill(OrangeBrush, orange);
            Fill(RedBrush, red);

            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var face = new Typeface(System.Windows.Documents.TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            for (int i = 0; i < papers.Count; i++)
            {
                if (i == Selected) continue;
                dc.DrawRectangle(null, Outline, papers[i]);
            }
            if (Selected >= 0 && Selected < papers.Count) dc.DrawRectangle(null, SelectedOutline, papers[Selected]);
            if (Numbered)
                for (int i = 0; i < papers.Count; i++)
                {
                    if (printed[i].Width < 20 || printed[i].Height < 18) continue;
                    var number = new FormattedText((i + 1).ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 13, Brushes.White, dip);
                    var tag = new Rect(printed[i].X, printed[i].Y, number.Width + 8, number.Height + 2);
                    dc.DrawRectangle(i == Selected ? SelectedOutline.Brush : Outline.Brush, null, tag);
                    dc.DrawText(number, new Point(tag.X + 4, tag.Y + 1));
                }
            dc.Pop();
        }

        private static List<Rect> Strips(Rect paper, Rect printed)
        {
            var strips = new List<Rect>(4);
            void Add(double x, double y, double w, double h) { if (w > 0.01 && h > 0.01) strips.Add(new Rect(x, y, w, h)); }
            Add(paper.X, paper.Y, paper.Width, printed.Y - paper.Y);
            Add(paper.X, printed.Bottom, paper.Width, paper.Bottom - printed.Bottom);
            Add(paper.X, printed.Y, printed.X - paper.X, printed.Height);
            Add(printed.Right, printed.Y, paper.Right - printed.Right, printed.Height);
            return strips;
        }

        private static void Meet(Rect a, Rect b, List<Rect> into)
        {
            var both = Rect.Intersect(a, b);
            if (!both.IsEmpty && both.Width > 0.01 && both.Height > 0.01) into.Add(both);
        }

        private static Brush Frozen(Color colour)
        {
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            return brush;
        }

        private static Pen FrozenPen(Color colour, double width)
        {
            var pen = new Pen(Frozen(colour), width);
            pen.Freeze();
            return pen;
        }
    }
}
