using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PdfEditor.App;

public partial class App : Application
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Every window, dialogs included, gets a dark title bar to go with the rest of it.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is not Window window) return;
            var handle = new WindowInteropHelper(window).Handle;
            int on = 1;
            if (handle != IntPtr.Zero) DwmSetWindowAttribute(handle, 20 /* use the dark title bar */, ref on, sizeof(int));
        }));
        base.OnStartup(e);
    }
}