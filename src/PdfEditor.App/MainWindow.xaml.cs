using System.ComponentModel;
using System.Windows;

namespace PdfEditor.App;

/// <summary>Thin shell around <see cref="PdfEditorControl"/>, which can equally be embedded in another WPF app.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Editor.TitleChanged += (_, _) => Title = Editor.DocumentTitle + " - SimplePDF Editor";
        Title = Editor.DocumentTitle + " - SimplePDF Editor";

        // A PDF path on the command line opens that file, so other apps can launch the editor on a document.
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
            Loaded += (_, _) => Editor.OpenFile(args[1]);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!Editor.ConfirmDiscardChanges()) e.Cancel = true;
        base.OnClosing(e);
    }
}
