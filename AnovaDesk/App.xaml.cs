using System.Windows;

namespace AnovaDesk;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppRuntime.Configure(e.Args);

        // Must happen before any shell/notification interaction so toasts are attributed to us.
        ToastChannel.Initialize();

        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(args.Exception.Message, "AnovaDesk error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
