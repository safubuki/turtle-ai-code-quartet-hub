using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace TurtleAIQuartetHub.Tools;

/// <summary>
/// 一時的な WPF 検証ツールの STAThread Main から Run を呼ぶ。
/// 検証失敗は標準エラーと終了コード 1 で報告し、ユーザーにダイアログを出さない。
/// 本体アプリには組み込まない。
/// </summary>
internal static class WpfCheckGuard
{
    public static int Run(Func<int> check)
    {
        // この検証プロセスだけに適用する。Windows 全体のエラー報告設定は変更しない。
        var previousErrorMode = GetErrorMode();
        SetErrorMode(previousErrorMode | 0x0001u | 0x0002u); // FAILCRITICALERRORS | NOGPFAULTERRORBOX
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.UnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        try
        {
            return check();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            dispatcher.UnhandledException -= OnDispatcherException;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            SetErrorMode(previousErrorMode);
        }
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ExitWithError(e.Exception);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        ExitWithError(e.ExceptionObject);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        ExitWithError(e.Exception);
    }

    private static void ExitWithError(object exception)
    {
        try
        {
            Console.Error.WriteLine(exception);
        }
        finally
        {
            Environment.Exit(1);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
