using System.Windows;
using System.Windows.Threading;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

public partial class App : Application
{
    private SingleInstanceCoordinator? _singleInstanceCoordinator;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // 描画はGPU既定に任せる（SoftwareOnly強制を撤去）。パネルUIの描画の滑らかさを優先し、
        // 特定環境で描画乱れが出る場合のみ SoftwareOnly へ戻すこと。
        // ランチャーは外部ウィンドウや P/Invoke を多用するため、一過性の例外で全体が落ちないようにする。
        // UI スレッドの未処理例外はログして握りつぶし、アプリを継続させる。
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += App_AppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += App_UnobservedTaskException;

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
        // ログだけ残し、アプリは終了させない。ウィンドウ配置やフォーカス整列の一過性の失敗で
        // ランチャー本体が勝手に落ちる事故を防ぐ。
        DiagnosticLog.Write(e.Exception);
        e.Handled = true;
    }

    private static void App_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // バックグラウンドの遅延整列・再アサートなどで観測されなかったタスク例外を握りつぶす。
        DiagnosticLog.Write(e.Exception);
        e.SetObserved();
    }

    private static void App_AppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
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
