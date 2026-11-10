using System.Windows;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

// AI の作業状態の表示。AI ツールのフック（Claude Code / Codex / GitHub Copilot）が送る
// 「指示を受けた」「応答が終わった」「許可を待っている」の明示的な通知だけを使い、画面やログからは推定しない。
// ・作業中: スロットのバッジ（極小表示はカード）の枠がゆっくり明滅する。
// ・完了／許可待ち: バッジ全体がぼんやり光る。フォーカス表示中に終わっても光らせ、
//   パネル操作などでそのスロットのフォーカス表示が切り替わった（1 面フォーカス⇔4 面表示）ときに消す。
public partial class MainWindow
{
    private readonly AiNotifyHookInstaller _aiNotifyHookInstaller = new();

    // パイプ経由の通知。パネルを前面に出す通常の外部コマンドとは分け、作業の邪魔をしない。
    private void HandleAiHookEvent(string[] args)
    {
        var hookEvent = AiHookEventParser.TryParse(args);
        if (hookEvent is null || hookEvent.Kind == AiHookEventKind.Unknown)
        {
            return;
        }

        var targets = AiActivitySlotMatcher.FindTargets(
            _statusStore.Slots.Where(slot => slot.WindowHandle != IntPtr.Zero),
            GetAiActivityMatchPaths,
            hookEvent.Cwd);
        if (targets.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var slot in targets)
        {
            if (slot.AiTracker.Apply(hookEvent, now))
            {
                slot.NotifyAiActivityChanged();
            }
            else if (hookEvent.Kind is AiHookEventKind.Stop or AiHookEventKind.Attention)
            {
                // 状態が同じでも、ツールチップのツール名・文言は最新の通知に合わせる。
                slot.NotifyAiActivityChanged();
            }
        }

        DiagnosticLog.Write(
            LogLevel.Info,
            $"AI event: {hookEvent.Tool} {hookEvent.EventName} -> {string.Join(",", targets.Select(slot => $"{slot.Name}:{slot.AiActivity}"))}");
    }

    private static IEnumerable<string> GetAiActivityMatchPaths(WindowSlot slot)
    {
        return new[] { slot.CurrentWorkspacePath, slot.DisplayPath, slot.EffectivePath, slot.SavedWorkspacePath, slot.Path }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    // 周期更新から呼ぶ。完了通知が来ないまま長時間たった「作業中」を片付ける。
    private void PruneStaleAiActivity()
    {
        if (_statusStore.Slots.All(slot => slot.AiActivity != AiActivityState.Working))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var slot in _statusStore.Slots)
        {
            if (slot.AiTracker.PruneStale(now))
            {
                slot.NotifyAiActivityChanged();
            }
        }
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
            DiagnosticLog.Write(LogLevel.Info, $"AI activity acknowledged by focus change: slot {slot.Name} -> {slot.AiActivity}");
        }
    }

    private void RefreshAiNotifySettingsStatus(IReadOnlyList<AiNotifyHookToolResult>? results = null)
    {
        results ??= _aiNotifyHookInstaller.GetStatus();
        AiNotifyStatusText.Text = string.Join("　", results.Select(result => $"{result.DisplayName}: {result.Message}"));
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
        foreach (var slot in _statusStore.Slots)
        {
            if (slot.AiTracker.Reset())
            {
                slot.NotifyAiActivityChanged();
            }
        }

        DiagnosticLog.Write(
            LogLevel.Info,
            $"AI notify hooks removed: {string.Join(", ", results.Select(result => $"{result.Tool}={result.Status}"))}");
    }
}
