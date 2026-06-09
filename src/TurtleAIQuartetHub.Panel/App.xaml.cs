using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

public partial class App : Application
{
    private SingleInstanceCoordinator? _singleInstanceCoordinator;

    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += App_UnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += App_DomainUnhandledException;

        _singleInstanceCoordinator = new SingleInstanceCoordinator();
        if (!_singleInstanceCoordinator.IsPrimary)
        {
            _ = await _singleInstanceCoordinator.SendToPrimaryAsync(e.Args, CancellationToken.None);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        _singleInstanceCoordinator.CommandReceived += args =>
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (MainWindow is MainWindow mainWindow)
                {
                    mainWindow.ExecuteExternalCommand(args);
                }
            }, DispatcherPriority.Background);
        };

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        if (e.Args.Length > 0)
        {
            _ = Dispatcher.InvokeAsync(
                () => mainWindow.ExecuteExternalCommand(e.Args),
                DispatcherPriority.Background);
        }
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticLog.Write(e.Exception);
        // UI スレッドの単発例外（ウィンドウ消滅との競合など）でアプリ全体を道連れにしない。
        e.Handled = true;
    }

    private static void App_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        DiagnosticLog.Write(e.Exception);
        // fire-and-forget タスクの例外がファイナライザ経由でプロセスを落とすのを防ぐ。
        e.SetObserved();
    }

    private static void App_DomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // ここに来たら継続不能だが、原因調査のため必ずログには残す。
        if (e.ExceptionObject is Exception exception)
        {
            DiagnosticLog.Write(exception);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceCoordinator?.IsPrimary == true)
        {
            TaskbarJumpListService.SetInactiveMenu();
        }

        _singleInstanceCoordinator?.Dispose();
        base.OnExit(e);
    }
}
