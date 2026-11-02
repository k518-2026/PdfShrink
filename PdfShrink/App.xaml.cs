using System.Windows;
using System.Windows.Threading;

namespace PdfShrink;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("処理中にエラーが発生しました。\n\n" + e.Exception.Message,
            "PdfShrink", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
