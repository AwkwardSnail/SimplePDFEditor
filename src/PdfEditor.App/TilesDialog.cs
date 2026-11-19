using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PdfEditor.Core;

namespace PdfEditor.App;

/// <summary>Asks how a large page should be spread over sheets of paper for printing.</summary>
internal static class TilesDialog
{
    private const double PointsPerMillimetre = 72 / 25.4;

    private static readonly (string Name, double Width, double Height)[] Papers =
    [
        ("A4", 210, 297), ("A3", 297, 420), ("A5", 148, 210), ("Letter", 215.9, 279.4), ("Legal", 215.9, 355.6),
    ];

    /// <summary>Returns the chosen options, or null when cancelled.</summary>
    public static TileOptions? Show(DependencyObject owner, PdfSize page)
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

        var overlap = new TextBox { Width = 50, Text = "10" };
        var margin = new TextBox { Width = 50, Text = "5" };
        var marks = new CheckBox { Content = "Cut marks and sheet numbers in the margins", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 330, Margin = new Thickness(0, 12, 0, 0), MinHeight = 34 };
        var ok = new Button { Content = "Save tiles", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };

        // The options as they stand, or null (with the reason) when something typed is not usable.
        TileOptions? Read(out string problem)
        {
            problem = "";
            var (_, w, h) = Papers[Math.Max(0, paper.SelectedIndex)];
            if (landscape.IsChecked == true) (w, h) = (h, w);
            if (!TryParse(overlap.Text, out double o) || o < 0 || o > 50) { problem = "Enter an overlap between 0 and 50 mm."; return null; }
            if (!TryParse(margin.Text, out double m) || m < 0 || m > 30) { problem = "Enter a margin between 0 and 30 mm."; return null; }
            var options = new TileOptions
            {
                PaperWidth = w * PointsPerMillimetre, PaperHeight = h * PointsPerMillimetre,
                Overlap = o * PointsPerMillimetre, Margin = m * PointsPerMillimetre, CutMarks = marks.IsChecked == true,
            };
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
            catch (ArgumentException) { problem = "The margin and overlap leave too little of the paper to print on."; return null; }
            return options;
        }

        void Update()
        {
            across.IsEnabled = down.IsEnabled = fit.IsChecked == true;
            percent.IsEnabled = scaled.IsChecked == true;
            if (Read(out string problem) is not { } options)
            {
                summary.Text = problem;
                ok.IsEnabled = false;
                return;
            }
            var plan = PdfDocument.PlanTiles(page, options);
            ok.IsEnabled = true;
            summary.Text = $"{plan.Sheets} sheet{(plan.Sheets == 1 ? "" : "s")}: {plan.Columns} across, {plan.Rows} down. " +
                           $"The poster will be {Format(plan.PosterWidth / PointsPerMillimetre)} × {Format(plan.PosterHeight / PointsPerMillimetre)} mm, " +
                           $"{Math.Round(plan.Scale * 100):0}% of the page's size.";
        }
        paper.SelectionChanged += (_, _) => Update();
        foreach (var radio in new[] { portrait, landscape, actual, fit, scaled }) radio.Checked += (_, _) => Update();
        foreach (var box in new[] { across, down, percent, overlap, margin }) box.TextChanged += (_, _) => Update();
        marks.Click += (_, _) => Update();

        var panel = new StackPanel { Margin = new Thickness(14) };
        TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) };
        StackPanel Row(params UIElement[] items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var item in items) row.Children.Add(item);
            return row;
        }
        TextBlock Words(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

        var first = Heading("Paper to print on");
        first.Margin = new Thickness(0, 0, 0, 6);
        panel.Children.Add(first);
        panel.Children.Add(Row(paper));
        panel.Children.Add(Row(portrait, landscape));
        panel.Children.Add(Heading("Size of the poster"));
        panel.Children.Add(actual);
        panel.Children.Add(Row(fit, across, Words("sheets across and"), down, Words("down")));
        panel.Children.Add(Row(scaled, percent, Words("%")));
        panel.Children.Add(Heading("Joining the sheets"));
        var overlapLabel = Words("Overlap between sheets");
        overlapLabel.Width = 190;
        var marginLabel = Words("Blank edge the printer cannot reach");
        marginLabel.Width = 190;
        var mm1 = Words("mm");
        mm1.Margin = new Thickness(6, 0, 0, 0);
        var mm2 = Words("mm");
        mm2.Margin = new Thickness(6, 0, 0, 0);
        panel.Children.Add(Row(overlapLabel, overlap, mm1));
        panel.Children.Add(Row(marginLabel, margin, mm2));
        panel.Children.Add(marks);
        panel.Children.Add(summary);

        TileOptions? result = null;
        ok.Click += (_, _) =>
        {
            result = Read(out _);
            if (result is not null) window.DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        window.Content = panel;
        window.Loaded += (_, _) => { Update(); paper.Focus(); };
        return window.ShowDialog() == true ? result : null;
    }

    private static string Format(double millimetres) => Math.Round(millimetres, 1).ToString("0.#", CultureInfo.CurrentCulture);

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
