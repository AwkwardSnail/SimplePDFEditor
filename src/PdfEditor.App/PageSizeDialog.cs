using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace PdfEditor.App;

/// <summary>The size chosen for a new page. Without a width and height it takes the size of the page it follows.</summary>
internal sealed record NewPageSize(double? Width, double? Height)
{
    public static readonly NewPageSize SameAsCurrent = new(null, null);

    /// <summary>The size in points, given the size of the page the new one follows.</summary>
    public (double Width, double Height) Resolve(double currentWidth, double currentHeight) =>
        Width is { } w && Height is { } h ? (w, h) : (currentWidth, currentHeight);
}

/// <summary>Asks what size a new page should be, and whether to use that size from now on.</summary>
internal static class PageSizeDialog
{
    private const double PointsPerMillimetre = 72 / 25.4;

    private static readonly (string Name, double Width, double Height)[] Standard =
    [
        ("A3", 297, 420), ("A4", 210, 297), ("A5", 148, 210), ("Letter", 215.9, 279.4), ("Legal", 215.9, 355.6),
    ];

    private static string SettingsFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfEditor", "new-page-size.txt");

    /// <summary>The size kept for every new page, or null when the user is to be asked each time.</summary>
    public static NewPageSize? Remembered
    {
        get
        {
            try
            {
                if (!File.Exists(SettingsFile)) return null;
                string text = File.ReadAllText(SettingsFile).Trim();
                if (text == "same") return NewPageSize.SameAsCurrent;
                var parts = text.Split(' ');
                if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double h) && w is >= 10 and <= 15000 && h is >= 10 and <= 15000)
                    return new NewPageSize(w, h);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return null;
        }
        private set
        {
            try
            {
                if (value is null) { File.Delete(SettingsFile); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
                File.WriteAllText(SettingsFile, value.Width is { } w && value.Height is { } h
                    ? string.Create(CultureInfo.InvariantCulture, $"{w:0.###} {h:0.###}") : "same");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // not kept; the dialog simply shows again next time
        }
    }

    /// <summary>
    /// Shows the dialog. Returns the chosen size, or null when cancelled. The choice of whether to
    /// keep the size for later pages is stored (on this computer only) before returning.
    /// </summary>
    public static NewPageSize? Show(DependencyObject owner, double currentWidth, double currentHeight)
    {
        var window = new Window
        {
            Title = "Add page",
            Owner = Window.GetWindow(owner),
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        var remembered = Remembered;

        var sizes = new ComboBox { Width = 190 };
        sizes.Items.Add("Same as this page");
        foreach (var (name, w, h) in Standard) sizes.Items.Add($"{name}  ({Format(w)} × {Format(h)} mm)");
        sizes.Items.Add("Other size");
        int custom = sizes.Items.Count - 1;

        var portrait = new RadioButton { Content = "Portrait", GroupName = "Orientation", IsChecked = true, Margin = new Thickness(0, 0, 14, 0) };
        var landscape = new RadioButton { Content = "Landscape", GroupName = "Orientation" };
        var width = new TextBox { Width = 70 };
        var height = new TextBox { Width = 70 };
        var keep = new CheckBox
        {
            Content = "Use this size for every new page from now on",
            IsChecked = remembered is not null,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var keepNote = new TextBlock
        {
            Text = "Add page will then stop asking. Hold Shift while choosing Add page to see this again.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 300, Margin = new Thickness(20, 2, 0, 0), Opacity = 0.65,
        };

        // The boxes show the size that will be used; they can be typed in only for "Other size".
        void Show()
        {
            int i = sizes.SelectedIndex;
            bool isStandard = i >= 1 && i < custom;
            portrait.IsEnabled = landscape.IsEnabled = isStandard;
            width.IsEnabled = height.IsEnabled = i == custom;
            if (i == custom) return; // keep what is typed
            double w = currentWidth / PointsPerMillimetre, h = currentHeight / PointsPerMillimetre;
            if (isStandard)
            {
                (_, w, h) = Standard[i - 1];
                if (landscape.IsChecked == true) (w, h) = (h, w);
            }
            width.Text = Format(w);
            height.Text = Format(h);
        }
        sizes.SelectionChanged += (_, _) => Show();
        portrait.Checked += (_, _) => Show();
        landscape.Checked += (_, _) => Show();

        // Start from the size kept last time, otherwise from the current page's size (what Add page always did).
        sizes.SelectedIndex = 0;
        if (remembered is { Width: { } rw, Height: { } rh })
        {
            double mw = rw / PointsPerMillimetre, mh = rh / PointsPerMillimetre;
            int match = Array.FindIndex(Standard, s => Near(s.Width, mw) && Near(s.Height, mh) || Near(s.Width, mh) && Near(s.Height, mw));
            if (match >= 0)
            {
                if (!Near(Standard[match].Width, mw)) landscape.IsChecked = true;
                sizes.SelectedIndex = match + 1;
            }
            else
            {
                sizes.SelectedIndex = custom;
                width.Text = Format(mw);
                height.Text = Format(mh);
            }
        }
        Show();

        var grid = new Grid { Margin = new Thickness(14) };
        for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < 7; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Place(UIElement element, int row, int column, int span = 1)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            Grid.SetColumnSpan(element, span);
            grid.Children.Add(element);
        }
        TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 8) };
        StackPanel WithUnit(TextBox box)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(box);
            row.Children.Add(new TextBlock { Text = "mm", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            return row;
        }

        Place(Label("Size"), 0, 0);
        Place(new Border { Child = sizes, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left }, 0, 1, 2);
        var orientation = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        orientation.Children.Add(portrait);
        orientation.Children.Add(landscape);
        Place(orientation, 1, 1, 2);
        Place(Label("Width"), 2, 0);
        Place(WithUnit(width), 2, 1, 2);
        Place(Label("Height"), 3, 0);
        Place(WithUnit(height), 3, 1, 2);
        Place(keep, 4, 0, 3);
        Place(keepNote, 5, 0, 3);

        var ok = new Button { Content = "Add page", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        NewPageSize? result = null;
        ok.Click += (_, _) =>
        {
            NewPageSize chosen;
            if (sizes.SelectedIndex == 0) chosen = NewPageSize.SameAsCurrent;
            else
            {
                // Between 10 mm and 5 m a side, which is as far as PDF readers reliably go.
                if (!TryParse(width.Text, out double w) || !TryParse(height.Text, out double h) || w < 10 || h < 10 || w > 5000 || h > 5000)
                {
                    MessageBox.Show(window, "Enter a width and a height in millimetres, between 10 and 5000.", "Add page", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                chosen = new NewPageSize(w * PointsPerMillimetre, h * PointsPerMillimetre);
            }
            Remembered = keep.IsChecked == true ? chosen : null;
            result = chosen;
            window.DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Place(buttons, 6, 0, 3);

        window.Content = grid;
        window.Loaded += (_, _) => sizes.Focus();
        return window.ShowDialog() == true ? result : null;
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.6;

    private static string Format(double millimetres) => Math.Round(millimetres, 1).ToString("0.#", CultureInfo.CurrentCulture);

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
