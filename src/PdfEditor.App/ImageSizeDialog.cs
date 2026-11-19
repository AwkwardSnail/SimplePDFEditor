using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace PdfEditor.App;

/// <summary>Asks for an image's width and height in millimetres.</summary>
internal static class ImageSizeDialog
{
    private const double MillimetresPerPoint = 25.4 / 72;

    /// <summary>Returns the chosen size in points, or null when cancelled.</summary>
    public static (double Width, double Height)? Show(DependencyObject owner, double widthPoints, double heightPoints)
    {
        var window = new Window
        {
            Title = "Resize image",
            Owner = Window.GetWindow(owner),
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        double ratio = heightPoints / widthPoints;
        var width = new TextBox { Width = 80, Text = Format(widthPoints * MillimetresPerPoint) };
        var height = new TextBox { Width = 80, Text = Format(heightPoints * MillimetresPerPoint) };
        var keep = new CheckBox { Content = "Keep proportions", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };

        // With proportions kept, typing one side fills in the other.
        bool updating = false;
        void Follow(TextBox source, TextBox other, Func<double, double> convert)
        {
            if (updating || keep.IsChecked != true || !TryParse(source.Text, out double value)) return;
            updating = true;
            other.Text = Format(convert(value));
            updating = false;
        }
        width.TextChanged += (_, _) => Follow(width, height, w => w * ratio);
        height.TextChanged += (_, _) => Follow(height, width, h => h / ratio);

        var grid = new Grid { Margin = new Thickness(14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Place(UIElement element, int row, int column, int span = 1)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            Grid.SetColumnSpan(element, span);
            grid.Children.Add(element);
        }
        Place(new TextBlock { Text = "Width", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 6) }, 0, 0);
        Place(new Border { Child = width, Margin = new Thickness(0, 0, 6, 6) }, 0, 1);
        Place(new TextBlock { Text = "mm", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) }, 0, 2);
        Place(new TextBlock { Text = "Height", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }, 1, 0);
        Place(new Border { Child = height, Margin = new Thickness(0, 0, 6, 0) }, 1, 1);
        Place(new TextBlock { Text = "mm", VerticalAlignment = VerticalAlignment.Center }, 1, 2);
        Place(keep, 2, 0, 3);

        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        (double, double)? result = null;
        ok.Click += (_, _) =>
        {
            if (!TryParse(width.Text, out double w) || !TryParse(height.Text, out double h) || w <= 0 || h <= 0 || w > 5000 || h > 5000)
            {
                MessageBox.Show(window, "Enter a width and a height in millimetres, as numbers above zero.", "Resize image", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            result = (w / MillimetresPerPoint, h / MillimetresPerPoint);
            window.DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Place(buttons, 3, 0, 3);

        window.Content = grid;
        window.Loaded += (_, _) => { width.Focus(); width.SelectAll(); };
        return window.ShowDialog() == true ? result : null;
    }

    private static string Format(double millimetres) => Math.Round(millimetres, 1).ToString("0.#", CultureInfo.CurrentCulture);

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
