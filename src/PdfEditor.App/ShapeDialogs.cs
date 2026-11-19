using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PdfEditor.Core;

namespace PdfEditor.App;

/// <summary>The small dialogs for a drawing's filling and line, and for a watermark.</summary>
internal static class ShapeDialogs
{
    /// <summary>The colours offered for lines and fillings.</summary>
    public static readonly (string Name, PdfColor Colour)[] Colours =
    [
        ("Black", PdfColor.Black), ("Red", new(200, 30, 30)), ("Blue", new(30, 70, 200)), ("Green", new(20, 130, 60)),
        ("Grey", new(110, 110, 110)), ("White", new(255, 255, 255)), ("Yellow", PdfColor.Yellow),
    ];

    private static ComboBoxItem Item(string name, PdfColor colour)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Rectangle
        {
            Width = 14, Height = 14, Fill = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B)),
            Stroke = Brushes.Gray, StrokeThickness = 1, Margin = new Thickness(0, 0, 6, 0),
        });
        row.Children.Add(new TextBlock { Text = name });
        return new ComboBoxItem { Content = row, Tag = colour };
    }

    /// <summary>A list of the colours with the given one chosen; a colour that is not in the list is added as "As it is now".</summary>
    private static ComboBox ColourBox(PdfColor? chosen, string? none)
    {
        var box = new ComboBox { Width = 170 };
        if (none is not null) box.Items.Add(new ComboBoxItem { Content = none });
        foreach (var (name, colour) in Colours) box.Items.Add(Item(name, colour));
        box.SelectedIndex = 0;
        if (chosen is { } c)
        {
            var same = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is PdfColor p && (p.R, p.G, p.B) == (c.R, c.G, c.B));
            if (same is null) box.Items.Add(same = Item("As it is now", c));
            box.SelectedItem = same;
        }
        return box;
    }

    private static Window Dialog(DependencyObject owner, string title) => new()
    {
        Title = title,
        Owner = Window.GetWindow(owner),
        SizeToContent = SizeToContent.WidthAndHeight,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false,
    };

    private static TextBlock Label(string text) => new() { Text = text, Width = 90, VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel Row(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var item in items) row.Children.Add(item);
        return row;
    }

    /// <summary>Shows the panel with OK and Cancel beneath it. True when OK was chosen and <paramref name="accept"/> agreed.</summary>
    private static bool Run(Window window, StackPanel panel, string okText, Func<bool> accept, UIElement focus)
    {
        var ok = new Button { Content = okText, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        ok.Click += (_, _) => { if (accept()) window.DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        panel.Margin = new Thickness(14);
        window.Content = panel;
        window.Loaded += (_, _) => focus.Focus();
        return window.ShowDialog() == true;
    }

    /// <summary>Asks how a drawing is filled. False when cancelled; otherwise <paramref name="fill"/> is the colour, or null for see-through.</summary>
    public static bool AskFilling(DependencyObject owner, PdfColor? current, out PdfColor? fill)
    {
        var window = Dialog(owner, "Filling");
        var box = ColourBox(current, "None (see-through)");
        if (current is null) box.SelectedIndex = 0;
        var panel = new StackPanel();
        panel.Children.Add(Row(Label("Filling"), box));
        PdfColor? chosen = null;
        bool done = Run(window, panel, "OK", () => { chosen = box.SelectedItem is ComboBoxItem { Tag: PdfColor c } ? c : null; return true; }, box);
        fill = chosen;
        return done;
    }

    /// <summary>Asks for a drawing's line colour and thickness (in points). Null when cancelled.</summary>
    public static (PdfColor Colour, double Width)? AskLine(DependencyObject owner, PdfColor current, double width)
    {
        var window = Dialog(owner, "Line");
        var box = ColourBox(current, null);
        var thickness = new ComboBox { Width = 80, IsEditable = true, Text = (width > 0 ? width : 2).ToString("0.##", CultureInfo.CurrentCulture) };
        foreach (double w in new[] { 0.5, 1, 2, 3, 5, 8 }) thickness.Items.Add(new ComboBoxItem { Content = w.ToString("0.##", CultureInfo.CurrentCulture) });
        var points = new TextBlock { Text = "pt", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(Row(Label("Colour"), box));
        panel.Children.Add(Row(Label("Thickness"), thickness, points));
        (PdfColor, double)? result = null;
        bool done = Run(window, panel, "OK", () =>
        {
            if (!TryParse(thickness.Text, out double w) || w < 0.1 || w > 50)
            {
                MessageBox.Show(window, "Enter a thickness between 0.1 and 50 points.", "Line", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            result = (box.SelectedItem is ComboBoxItem { Tag: PdfColor c } ? c : current, w);
            return true;
        }, box);
        return done ? result : null;
    }

    /// <summary>
    /// Asks for a new look for one or more pieces of text. Only what is changed in the dialog is
    /// set in the result, so whatever is left alone stays as it is. Null when cancelled or when
    /// nothing was changed.
    /// </summary>
    public static TextFormat? AskText(DependencyObject owner, TextRun sample, int pieces)
    {
        var window = Dialog(owner, pieces > 1 ? $"Font and colour ({pieces} pieces of text)" : "Font and colour");
        var font = new ComboBox { Width = 170 };
        foreach (string name in new[] { "As it is", "Sans (Helvetica)", "Serif (Times)", "Mono (Courier)" }) font.Items.Add(new ComboBoxItem { Content = name });
        font.SelectedIndex = 0;
        var size = new TextBox { Width = 60 };
        var points = new TextBlock { Text = "pt  (empty: as it is)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Opacity = 0.65 };
        bool wasBold = System.Text.RegularExpressions.Regex.IsMatch(sample.FontName, "Bold|Black|Heavy|Semibold|Demi", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        bool wasItalic = System.Text.RegularExpressions.Regex.IsMatch(sample.FontName, "Italic|Oblique", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var bold = new CheckBox { Content = "Bold", IsChecked = wasBold, Margin = new Thickness(0, 0, 14, 0) };
        var italic = new CheckBox { Content = "Italic", IsChecked = wasItalic };
        var colour = ColourBox(null, "As it is");

        var panel = new StackPanel();
        panel.Children.Add(Row(Label("Font"), font));
        panel.Children.Add(Row(Label("Size"), size, points));
        panel.Children.Add(Row(Label("Style"), bold, italic));
        panel.Children.Add(Row(Label("Colour"), colour));
        panel.Children.Add(new TextBlock
        {
            Text = "A change of font, bold or italic sets the text in a standard font, so its letters may look a little different.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 300, Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Left,
        });
        TextFormat? result = null;
        bool done = Run(window, panel, "OK", () =>
        {
            double? newSize = null;
            if (size.Text.Trim().Length > 0)
            {
                if (!TryParse(size.Text, out double s) || s < 2 || s > 1000)
                {
                    MessageBox.Show(window, "Enter a size between 2 and 1000 points, or leave it empty.", window.Title, MessageBoxButton.OK, MessageBoxImage.Information);
                    return false;
                }
                newSize = s;
            }
            result = new TextFormat
            {
                Font = font.SelectedIndex switch { 1 => StandardFont.Helvetica, 2 => StandardFont.Times, 3 => StandardFont.Courier, _ => null },
                Size = newSize,
                Bold = bold.IsChecked == wasBold ? null : bold.IsChecked == true,
                Italic = italic.IsChecked == wasItalic ? null : italic.IsChecked == true,
                Color = colour.SelectedItem is ComboBoxItem { Tag: PdfColor c } ? c : null,
            };
            return true;
        }, font);
        if (!done || result is null) return null;
        return result is { Font: null, Size: null, Bold: null, Italic: null, Color: null } ? null : result;
    }

    /// <summary>Asks for a watermark: its words, look, and whether it goes on every page. Null when cancelled.</summary>
    public static (string Text, WatermarkOptions Options, bool EveryPage)? AskWatermark(DependencyObject owner, int pageCount)
    {
        var window = Dialog(owner, "Watermark");
        var words = new TextBox { Width = 230, Text = "DRAFT" };
        var colour = new ComboBox { Width = 170 };
        foreach (var (name, c) in new (string, PdfColor)[] { ("Grey", new(120, 120, 120)), ("Red", new(200, 30, 30)), ("Blue", new(30, 70, 200)), ("Green", new(20, 130, 60)), ("Black", PdfColor.Black) })
            colour.Items.Add(Item(name, c));
        colour.SelectedIndex = 0;
        var strength = new ComboBox { Width = 170 };
        foreach (var (name, opacity) in new[] { ("Faint", 0.12), ("Light", 0.25), ("Strong", 0.45) })
            strength.Items.Add(new ComboBoxItem { Content = name, Tag = opacity });
        strength.SelectedIndex = 1;
        var diagonal = new RadioButton { Content = "Corner to corner", GroupName = "WatermarkWay", IsChecked = true, Margin = new Thickness(0, 0, 14, 0) };
        var level = new RadioButton { Content = "Level", GroupName = "WatermarkWay" };
        var thisPage = new RadioButton { Content = "This page", GroupName = "WatermarkPages", IsChecked = true, Margin = new Thickness(0, 0, 14, 0) };
        var everyPage = new RadioButton { Content = pageCount > 1 ? $"All {pageCount} pages" : "Every page", GroupName = "WatermarkPages", IsEnabled = pageCount > 1 };

        var panel = new StackPanel();
        panel.Children.Add(Row(Label("Words"), words));
        panel.Children.Add(Row(Label("Colour"), colour));
        panel.Children.Add(Row(Label("Strength"), strength));
        panel.Children.Add(Row(Label("Direction"), diagonal, level));
        panel.Children.Add(Row(Label("Put it on"), thisPage, everyPage));
        panel.Children.Add(new TextBlock
        {
            Text = "The watermark is see-through text across the middle of the page. To take it off again, click it with Select and press Delete.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 320, Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Left,
        });
        (string, WatermarkOptions, bool)? result = null;
        bool done = Run(window, panel, "Add watermark", () =>
        {
            if (words.Text.Trim().Length == 0) return false;
            result = (words.Text.Trim(), new WatermarkOptions
            {
                Color = colour.SelectedItem is ComboBoxItem { Tag: PdfColor c } ? c : new PdfColor(120, 120, 120),
                Opacity = strength.SelectedItem is ComboBoxItem { Tag: double o } ? o : 0.25,
                Diagonal = diagonal.IsChecked == true,
            }, everyPage.IsChecked == true);
            return true;
        }, words);
        return done ? result : null;
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
