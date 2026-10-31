using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

// タイトルバーのアイコンでの状態表示。
// ・システムモニタ: CPU / メモリ / ディスク / ページファイルの最も悪い状態をアイコン色（緑・黄・赤）にする。
// ・Pull Request 受信箱: 未読の PR 関連通知があれば右下に赤丸（UpdateBadge）を出し、受信箱を開いたら既読にする。
public partial class MainWindow
{
    private const string SystemMonitorToolTipTitle = "システムモニタ";
    private const string PullRequestInboxToolTipTitle = "GitHub の Pull Request 受信箱を開く（github.com/pulls/inbox）";
    // モーダルを開いていない間の評価間隔。CPU 使用率はこの間の平均になるので、一瞬の負荷では色が跳ねにくい。
    private static readonly TimeSpan SystemHealthCheckInterval = TimeSpan.FromSeconds(5);
    // 通知 API の推奨ポーリング間隔（X-Poll-Interval: 60 秒）より十分長くする。
    private static readonly TimeSpan PullRequestCheckInterval = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PullRequestFirstCheckDelay = TimeSpan.FromSeconds(5);

    private readonly PullRequestNotificationService _pullRequestNotificationService = new();
    private DispatcherTimer? _systemHealthTimer;
    private bool _isSystemHealthCheckInFlight;
    private DispatcherTimer? _pullRequestCheckTimer;
    private bool _isPullRequestCheckInFlight;
    private bool _hasCompletedFirstPullRequestCheck;
    private UpdateBadgeStateStore? _updateBadgeStateStore;
    private IReadOnlyList<DateTimeOffset> _pullRequestUnreadUpdateTimes = [];
    private DateTime? _lastPullRequestCheckAt;
    private string? _pullRequestUnavailableMessage;

    private void StartTitleBarIndicators()
    {
        if (_systemHealthTimer is not null)
        {
            return;
        }

        _systemHealthTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = SystemHealthCheckInterval
        };
        _systemHealthTimer.Tick += SystemHealthTimer_Tick;
        _systemHealthTimer.Start();
        SystemHealthTimer_Tick(null, EventArgs.Empty);

        if (!_statusStore.Config.CheckPullRequestUpdates)
        {
            return;
        }

        _updateBadgeStateStore = new UpdateBadgeStateStore(_statusStore.Config.StateDirectory);
        _pullRequestCheckTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = PullRequestFirstCheckDelay
        };
        _pullRequestCheckTimer.Tick += PullRequestCheckTimer_Tick;
        _pullRequestCheckTimer.Start();
    }

    private async void SystemHealthTimer_Tick(object? sender, EventArgs e)
    {
        // モーダル表示中は 1.5 秒ごとの更新が同じ値でアイコン色も更新する。
        if (_isSystemHealthCheckInFlight || SystemMonitorOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        _isSystemHealthCheckInFlight = true;
        try
        {
            var summary = await Task.Run(_systemMonitorService.CaptureHealth);
            if (SystemMonitorOverlay.Visibility != Visibility.Visible)
            {
                ApplySystemHealth(summary);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
        finally
        {
            _isSystemHealthCheckInFlight = false;
        }
    }

    private void ApplySystemHealth(SystemHealthSummary summary)
    {
        SystemMonitorButton.Foreground = SystemMonitorService.GetHealthBrush(summary.Level);

        var lines = new List<string> { SystemMonitorToolTipTitle };
        if (summary.Issues.Count == 0)
        {
            lines.Add("すべて正常です（CPU・メモリ・ディスク・ページファイル）");
        }
        else
        {
            lines.AddRange(summary.Issues.Select(issue =>
                $"{(issue.Level == SystemHealthLevel.Critical ? "警告" : "注意")}: {issue.Text}"));
        }

        SystemMonitorButton.ToolTip = string.Join(Environment.NewLine, lines);
        AutomationProperties.SetHelpText(SystemMonitorButton, string.Join("、", lines.Skip(1)));
    }

    private async void PullRequestCheckTimer_Tick(object? sender, EventArgs e)
    {
        if (_pullRequestCheckTimer is not null)
        {
            _pullRequestCheckTimer.Interval = PullRequestCheckInterval;
        }

        if (_isPullRequestCheckInFlight)
        {
            return;
        }

        _isPullRequestCheckInFlight = true;
        try
        {
            var result = await _pullRequestNotificationService.CheckAsync(_refreshCancellation.Token);
            if (!result.IsAvailable)
            {
                // 未導入・未サインインは定期的に繰り返すので、状態が変わったときだけ記録する。
                if (!string.Equals(_pullRequestUnavailableMessage, result.Message, StringComparison.Ordinal))
                {
                    DiagnosticLog.Write(LogLevel.Info, $"Pull Request update check unavailable: {result.Message}");
                }

                _pullRequestUnavailableMessage = result.Message;
                _pullRequestUnreadUpdateTimes = [];

                // 起動後の最初の確認で未サインインだったときだけ、ステータス欄で 1 回案内する。
                // gh が無い場合は案内しない（ツールチップには理由を出す）。操作中のメッセージは上書きしない。
                if (!_hasCompletedFirstPullRequestCheck && result.RequiresSignIn && !_isBusy)
                {
                    _statusStore.Message = "GitHub CLI (gh) にサインインすると、Pull Request の更新を受信箱アイコンの赤丸でお知らせします（ターミナルで gh auth login）。";
                }
            }
            else
            {
                _pullRequestUnavailableMessage = null;
                _pullRequestUnreadUpdateTimes = result.UnreadUpdateTimes;
                _lastPullRequestCheckAt = DateTime.Now;
            }

            _hasCompletedFirstPullRequestCheck = true;
            ApplyPullRequestBadge();
        }
        catch (OperationCanceledException)
        {
            // パネル終了時のキャンセル。
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
        finally
        {
            _isPullRequestCheckInFlight = false;
        }
    }

    private void ApplyPullRequestBadge()
    {
        if (_updateBadgeStateStore is null)
        {
            return;
        }

        var count = PullRequestNotificationService.CountUpdatesSince(
            _pullRequestUnreadUpdateTimes,
            _updateBadgeStateStore.GetLastSeen(UpdateBadgeStateStore.PullRequestsKey));
        UpdateBadge.SetIsActive(PullRequestInboxButton, count > 0);

        var status = _pullRequestUnavailableMessage
            ?? (count > 0
                ? $"新しい更新があります（未読の通知 {count} 件）"
                : _lastPullRequestCheckAt is { } checkedAt
                    ? $"新しい更新はありません（確認: {checkedAt:HH:mm}）"
                    : "更新を確認しています...");
        PullRequestInboxButton.ToolTip = $"{PullRequestInboxToolTipTitle}{Environment.NewLine}{status}";
        AutomationProperties.SetHelpText(PullRequestInboxButton, status);
    }

    // 受信箱を開いたら、その時点までの通知は既読扱いにして赤丸を消す。
    // PC の時計が GitHub より遅れていても消えるよう、受け取った通知の最新時刻も下限にする。
    private void MarkPullRequestsSeen()
    {
        if (_updateBadgeStateStore is null)
        {
            return;
        }

        var seenAt = DateTimeOffset.UtcNow;
        if (_pullRequestUnreadUpdateTimes.Count > 0)
        {
            var latest = _pullRequestUnreadUpdateTimes.Max();
            if (latest > seenAt)
            {
                seenAt = latest;
            }
        }

        _updateBadgeStateStore.MarkSeen(UpdateBadgeStateStore.PullRequestsKey, seenAt);
        ApplyPullRequestBadge();
    }
}
