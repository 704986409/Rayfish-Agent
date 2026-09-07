using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace RayLink.App;

public sealed class App : Application
{
    private static int _showingFatalError;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            args.Handled = true;
            ShowUnhandledException(args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ShowUnhandledException(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) => { args.SetObserved(); ShowUnhandledException(args.Exception); };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ShowUnhandledException(Exception exception)
    {
        if (Interlocked.Exchange(ref _showingFatalError, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var message = new TextBox
                {
                    Text = exception.ToString(), IsReadOnly = true, AcceptsReturn = true,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 180, MinWidth = 520
                };
                var window = new Window
                {
                    Title = "AgentLink error",
                    Width = 680, Height = 360, MinWidth = 560, MinHeight = 260,
                    Content = message, WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                window.Closed += (_, _) => Interlocked.Exchange(ref _showingFatalError, 0);
                window.Show();
            }
            catch { Interlocked.Exchange(ref _showingFatalError, 0); }
        });
    }
}
