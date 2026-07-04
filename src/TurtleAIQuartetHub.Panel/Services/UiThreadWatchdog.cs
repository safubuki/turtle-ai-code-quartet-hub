using System.Windows.Threading;

namespace TurtleAIQuartetHub.Panel.Services;

// UI スレッドの生存監視。ハング（UI スレッドの無期限ブロック）は例外を投げないため、
// 既存の未処理例外ハンドラ群では一切ログに残らない。「気づいたら固まって/落ちていたのに
// ログに何も無い」を切り分けられるよう、背景スレッドから定期的に UI スレッドへ ping を
// 送り、応答が途絶えたら WARN を、回復したらその旨を panel.log に書き残す。
// スリープ復帰でも一度 WARN が出るが、直後の recovered とペアになるので区別できる。
public sealed class UiThreadWatchdog : IDisposable
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HangThreshold = TimeSpan.FromSeconds(10);
    // ハング継続中の再警告間隔。毎周期書くとログが埋まるため間引く。
    private static readonly TimeSpan RepeatLogInterval = TimeSpan.FromMinutes(1);

    private readonly Dispatcher _dispatcher;
    // 停止合図。CancellationTokenSource だと Dispose 後の WaitHandle アクセスが
    // 監視スレッドと競合して例外になり得るため、Set するだけのイベントで済ませる
    // （プロセス終了時なのでハンドルの明示破棄は不要）。
    private readonly ManualResetEvent _stopSignal = new(false);
    private long _lastResponseTicks = DateTime.UtcNow.Ticks;

    public UiThreadWatchdog(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        var thread = new Thread(MonitorLoop)
        {
            IsBackground = true,
            Name = "UiThreadWatchdog"
        };
        thread.Start();
    }

    private void MonitorLoop()
    {
        var hangReportedAt = (DateTime?)null;
        var lastRepeatLogAt = DateTime.MinValue;

        while (true)
        {
            try
            {
                // Input 優先度で積む。Background だと正常な高負荷でも後回しにされ誤検知しやすい。
                _ = _dispatcher.BeginInvoke(
                    DispatcherPriority.Input,
                    new Action(() => Interlocked.Exchange(ref _lastResponseTicks, DateTime.UtcNow.Ticks)));

                if (_stopSignal.WaitOne(ProbeInterval))
                {
                    return;
                }

                var lastResponse = new DateTime(Interlocked.Read(ref _lastResponseTicks), DateTimeKind.Utc);
                var silence = DateTime.UtcNow - lastResponse;

                if (silence >= HangThreshold)
                {
                    if (hangReportedAt is null)
                    {
                        hangReportedAt = DateTime.UtcNow;
                        lastRepeatLogAt = DateTime.UtcNow;
                        DiagnosticLog.Write(
                            LogLevel.Warn,
                            $"UI thread unresponsive for {silence.TotalSeconds:0}s. "
                            + "Possible causes: a managed window's process is not responding, or a long-running UI handler. "
                            + "(This line is written from the watchdog thread.)");
                    }
                    else if (DateTime.UtcNow - lastRepeatLogAt >= RepeatLogInterval)
                    {
                        lastRepeatLogAt = DateTime.UtcNow;
                        DiagnosticLog.Write(
                            LogLevel.Warn,
                            $"UI thread still unresponsive ({silence.TotalSeconds:0}s and counting).");
                    }
                }
                else if (hangReportedAt is not null)
                {
                    var reportedAt = hangReportedAt.Value;
                    hangReportedAt = null;
                    DiagnosticLog.Write(
                        LogLevel.Info,
                        $"UI thread recovered (was unresponsive since {reportedAt.ToLocalTime():HH:mm:ss}). "
                        + "If this pairs with system sleep/resume, it can be ignored.");
                }
            }
            catch
            {
                // 監視自身がアプリを壊してはならない。少し置いて次周回で再試行する
                // （Dispatcher 終了後などに例外が続いてもホットループにしない）。
                if (_stopSignal.WaitOne(ProbeInterval))
                {
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        _stopSignal.Set();
    }
}
