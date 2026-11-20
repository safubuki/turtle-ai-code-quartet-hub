using System.Windows;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

// AI の作業状態の表示。AI ツールのフック（Claude Code / Codex / GitHub Copilot / Grok / Antigravity）が送る
// 「指示を受けた」「応答が終わった」の明示的な通知だけを使い、画面やログからは推定しない。
// 許可や質問の確認は作業の途中なので、開始済みの緑の周回を続け、別の色にはしない。
// 作業中は時間では片付けない。停止ボタンで終了の知らせを送らない AI もあるため、カードの停止ボタンで止められる。
// ・作業中: カード枠（角丸含む）の中心線上を、一筋の緑の LED 光がふわっとゆっくり一周し、終盤で少しだけ加速して繰り返す。枠全体は点滅させない。
// ・完了: カードの枠・地・外側の光と A〜D アイコン（極小表示はタイル自体）が少し早めの 3.0 秒ホタル点滅で青く光る。フォーカス表示中に終わっても光らせ、
//   パネル操作などでそのスロットのフォーカス表示が切り替わった（1 面フォーカス⇔4 面表示）ときに消す。
public partial class MainWindow
{
    private readonly AiNotifyHookInstaller _aiNotifyHookInstaller = new();

    // パイプ経由の通知。パネルを前面に出す通常の外部コマンドとは分け、作業の邪魔をしない。
    private void HandleAiHookEvent(string[] args)
    {
        var hookEvent = AiHookEventParser.TryParse(args);
        if (hookEvent is null)
        {
            return;
        }

        // 停止ボタンなどで何が届いたかをあとから確かめられるよう、使わなかった知らせも記録する。
        if (hookEvent.Kind == AiHookEventKind.Unknown)
        {
            DiagnosticLog.Write(LogLevel.Info, $"AI event ignored: {DescribeAiHookEvent(hookEvent)}");
            return;
        }

        var targets = AiActivitySlotMatcher.FindTargets(
            _statusStore.Slots.Where(slot => slot.WindowHandle != IntPtr.Zero),
            GetAiActivityMatchPaths,
            hookEvent.Cwd);
        if (targets.Count == 0)
        {
            DiagnosticLog.Write(LogLevel.Info, $"AI event unmatched: {DescribeAiHookEvent(hookEvent)} cwd={hookEvent.Cwd}");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var completionChanged = false;
        foreach (var slot in targets)
        {
            var hadCompletion = slot.AiActivity == AiActivityState.Done;
            if (slot.AiTracker.Apply(hookEvent, now))
            {
                slot.NotifyAiActivityChanged();
            }
            else if (hookEvent.Kind is AiHookEventKind.Stop)
            {
                // 状態が同じでも、ツールチップのツール名・完了時刻は最新の通知に合わせる。
                slot.NotifyAiActivityChanged();
            }

            completionChanged |= hookEvent.Kind is AiHookEventKind.Stop || hadCompletion != (slot.AiActivity == AiActivityState.Done);
        }

        // まだ見ていない完了は、カルテットハブを再起動しても戻せるよう保存する。
        if (completionChanged)
        {
            _statusStore.SaveAiCompletionState();
        }

        DiagnosticLog.Write(
            LogLevel.Info,
            $"AI event: {DescribeAiHookEvent(hookEvent)} -> {string.Join(",", targets.Select(slot => $"{slot.Name}:{slot.AiActivity}"))}");
    }

    private static string DescribeAiHookEvent(AiHookEvent hookEvent)
    {
        var detail = string.IsNullOrEmpty(hookEvent.Detail) ? string.Empty : $" ({hookEvent.Detail})";
        // どのセッション（会話・スレッド）の知らせかをあとから追えるよう、ID の末尾 8 文字だけ出す。
        // Codex の ID は UUIDv7 で先頭が時刻のため、近い時刻のスレッドは先頭が似ていて見分けにくい。
        var session = hookEvent.SessionId.Length switch
        {
            0 => string.Empty,
            <= 8 => $" [{hookEvent.SessionId}]",
            _ => $" […{hookEvent.SessionId[^8..]}]"
        };
        return $"{hookEvent.Tool} {hookEvent.EventName}{detail}{session}";
    }

    private static IEnumerable<string> GetAiActivityMatchPaths(WindowSlot slot)
    {
        return new[] { slot.CurrentWorkspacePath, slot.DisplayPath, slot.EffectivePath, slot.SavedWorkspacePath, slot.Path }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    // 古い版のハブが書いた専用ファイル（共通スクリプト・Copilot の専用フック）を今の内容へ揃える。
    // フックを解除している（ファイルが無い）ときは何も作らない。
    private void RefreshOwnedAiNotifyHookFiles()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var refreshed = _aiNotifyHookInstaller.RefreshOwnedFiles();
                if (refreshed.Count > 0)
                {
                    DiagnosticLog.Write(LogLevel.Info, $"AI notify hook files refreshed: {string.Join(", ", refreshed)}");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
            }
        });
    }

    // スロットのフォーカス表示が切り替わった（1 面フォーカスにした／やめて 4 面表示へ戻した）。
    // 完了に気づいて操作したものとして光を消す。ウィンドウが前面に来ただけでは消さない。
    private void AcknowledgeAiActivityOnFocusChange(WindowSlot slot)
    {
        if (_statusStore.IsSwappingSlotContents)
        {
            return;
        }

        if (slot.AiTracker.Acknowledge())
        {
            slot.NotifyAiActivityChanged();
            _statusStore.SaveAiCompletionState();
            DiagnosticLog.Write(LogLevel.Info, $"AI activity acknowledged by focus change: slot {slot.Name} -> {slot.AiActivity}");
        }
    }

    // 周期更新から呼ぶ。作業中のカードの経過時間（ストップウォッチ）を進める（表示だけで、状態は変えない）。
    private void RefreshAiElapsed()
    {
        foreach (var slot in _statusStore.Slots)
        {
            slot.RefreshAiElapsed();
        }
    }

    // カードの停止ボタン。停止ボタンで終了の知らせを送らない AI でも、作業中の表示をすぐ止められるようにする。
    // 表示だけを止め、AI の処理には触れない。
    private void StopSlotAiActivityButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        SuppressFocusedSlotReassertForPanelInput();
        StopSlotAiActivity(slot);
    }

    private void StopSlotAiActivity(WindowSlot slot)
    {
        if (slot.StopAiWorking())
        {
            DiagnosticLog.Write(LogLevel.Info, $"AI activity stopped by user: slot {slot.Name} -> {slot.AiActivity}");
        }
    }

    private void RefreshAiNotifySettingsStatus(IReadOnlyList<AiNotifyHookToolResult>? results = null)
    {
        results ??= _aiNotifyHookInstaller.GetStatus();
        AiNotifyHookLocationsItemsControl.ItemsSource = _aiNotifyHookInstaller.GetSettingsLocations(results);
    }

    private void InstallAiNotifyHooksButton_Click(object sender, RoutedEventArgs e)
    {
        var results = _aiNotifyHookInstaller.Install();
        RefreshAiNotifySettingsStatus(results);
        DiagnosticLog.Write(
            LogLevel.Info,
            $"AI notify hooks installed: {string.Join(", ", results.Select(result => $"{result.Tool}={result.Status}"))}");
    }

    private void UninstallAiNotifyHooksButton_Click(object sender, RoutedEventArgs e)
    {
        var results = _aiNotifyHookInstaller.Uninstall();
        RefreshAiNotifySettingsStatus(results);
        _statusStore.ResetAllAiActivity();

        DiagnosticLog.Write(
            LogLevel.Info,
            $"AI notify hooks removed: {string.Join(", ", results.Select(result => $"{result.Tool}={result.Status}"))}");
    }

    private void ResetAiActivityButton_Click(object sender, RoutedEventArgs e)
    {
        var resetCount = _statusStore.ResetAllAiActivity();
        var message = resetCount > 0
            ? $"{resetCount}個のスロットの AI 状態を待機へリセットしました。"
            : "すべてのスロットはすでに待機状態です。";
        ShowAiNotifyAccessMessage(message);
        _statusStore.Message = message;
        if (resetCount > 0)
        {
            DiagnosticLog.Write(LogLevel.Info, $"AI activity manually reset for {resetCount} slot(s).");
        }
    }
}
