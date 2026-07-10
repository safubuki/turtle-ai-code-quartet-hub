using System.Windows;
using System.Windows.Threading;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

public partial class App : Application
{
    private SingleInstanceCoordinator? _singleInstanceCoordinator;
    private UiThreadWatchdog? _uiThreadWatchdog;

    public static bool IsSessionEnding { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        // 描画はGPU既定に任せる（SoftwareOnly強制を撤去）。パネルUIの描画の滑らかさを優先し、
        // 特定環境で描画乱れが出る場合のみ SoftwareOnly へ戻すこと。
        // ランチャーは外部ウィンドウや P/Invoke を多用するため、一過性の例外で全体が落ちないようにする。
        // UI スレッドの未処理例外はログして握りつぶし、アプリを継続させる。
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += App_AppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += App_UnobservedTaskException;

        // 起動マーカー。会社環境などでハングした際、ログの最後がこの行（＝終了マーカー無し）か
        // [ERROR]/[FATAL] かで「正常に閉じたのか／落ちたのか／固まったのか」を切り分けられる。
        DiagnosticLog.TrimIfOversized();
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        DiagnosticLog.Write(LogLevel.Info, $"Panel starting (version={version}, pid={Environment.ProcessId}).");

        // OS のシャットダウン/サインアウトによる終了は OnExit まで完走せず「終了マーカー無し」に
        // なることがある。「勝手に落ちた」と区別できるよう、セッション終了要求そのものを記録する。
        SessionEnding += (_, args) =>
        {
            IsSessionEnding = true;
            DiagnosticLog.Write(LogLevel.Info, $"OS session ending ({args.ReasonSessionEnding}); panel will be terminated by the system.");
        };

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

        // ハングは例外を投げずログにも現れないため、UI スレッドの応答途絶を背景スレッドから
        // 監視して痕跡を残す。原因調査（どの時刻から固まったか）の起点になる。
        _uiThreadWatchdog = new UiThreadWatchdog(Dispatcher);

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
        // ここに来たら継続不能（プロセスが落ちる直前）。原因調査のため必ず FATAL で残す。
        // IsTerminating が true なら、この直後にプロセスが終了する＝異常終了の決定的な痕跡になる。
        if (e.ExceptionObject is Exception exception)
        {
            DiagnosticLog.Write(LogLevel.Fatal, $"Unhandled exception, terminating={e.IsTerminating}.");
            DiagnosticLog.Write(LogLevel.Fatal, exception);
        }
        else
        {
            DiagnosticLog.Write(
                LogLevel.Fatal,
                $"Unhandled non-exception error, terminating={e.IsTerminating}: {e.ExceptionObject}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 正常終了マーカー。この行がログ末尾にあれば「ユーザー操作で正常に閉じた」と判断できる。
        // 逆に起動マーカーの後にこの行が無ければ、ハングまたは強制終了（タスクキル・クラッシュ）を疑う。
        _uiThreadWatchdog?.Dispose();
        DiagnosticLog.Write(LogLevel.Info, $"Panel exiting normally (code={e.ApplicationExitCode}).");

        if (_singleInstanceCoordinator?.IsPrimary == true)
        {
            TaskbarJumpListService.SetInactiveMenu();
        }

        _singleInstanceCoordinator?.Dispose();
        base.OnExit(e);
    }
}
