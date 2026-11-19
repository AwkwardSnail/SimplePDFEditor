using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PdfEditor.App;

/// <summary>A small "type some text" dialog.</summary>
internal static class TextPrompt
{
    /// <summary>Returns the entered text, or null when cancelled.</summary>
    public static string? Show(DependencyObject owner, string title, string initial = "", bool multiline = true, bool password = false)
    {
        var window = new Window
        {
            Title = title,
            Owner = Window.GetWindow(owner),
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };

        Control input;
        Func<string> read;
        if (password)
        {
            var box = new PasswordBox();
            input = box;
            read = () => box.Password;
        }
        else
        {
            var box = new TextBox
            {
                Text = initial,
                AcceptsReturn = multiline,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            if (multiline) box.Height = 110;
            box.SelectAll();
            input = box;
            read = () => box.Text;
        }

        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = !multiline };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        ok.Click += (_, _) => window.DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(input);
        if (multiline && !password)
            panel.Children.Add(new TextBlock { Text = "Ctrl+Enter to confirm", Opacity = 0.65, Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(buttons);
        window.Content = panel;

        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                window.DialogResult = true;
                e.Handled = true;
            }
        };
        window.Loaded += (_, _) => input.Focus();
        return window.ShowDialog() == true ? read() : null;
    }
}
