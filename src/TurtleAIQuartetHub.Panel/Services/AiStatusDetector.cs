using System.Collections.Concurrent;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

// AI 状態検出器 (UI Automation 一次ソース版)。
//
// 設計方針:
//   - 「VS Code のチャット UI に何が表示されているか」を唯一の真実とする。
//   - Running シグナル (実行中テキスト/思考中ボックス/Stop ボタン) があれば Running。
//   - Confirmation ボタンが見えていれば WaitingForConfirmation。
//   - 入力欄が有効/送信ボタンが押せる状態 (= ready) で、直前が Running/WaitingForConfirmation
//     なら Completed へ遷移。Completed 表示は一定時間後に自動的に Idle に戻る。
//   - UI Automation が取れないティック (タイムアウト/未許可/失敗) では、前回状態を一定期間保持し、
//     保持期間を超えたら Idle に落とす。
//   - ログ読み込み・engine 別ステートマシン・broadcast オーナーシップ等の従来ロジックは廃止。
//     これらは「ログだけで動いている AI 風活動」を Running と誤判定する原因だったため、
//     UI で実際に確認できるものだけを信頼する。
public sealed class AiStatusDetector
{
    // UI Automation の走査は 500ms 予算で頻繁にタイムアウトする (chat-response-loading が
    // UI tree の深い位置にあるため、AI が動いている最中ほど probe が完走しにくい)。
    // タイムアウトは「AI が止まった証拠」ではなく「分からない」を意味するので、長めに Running を保持する。
    // ScanCompleted (完走) で Running が見つからなければ即降格 (HandleScanCompletedNegative 経由)。
    private static readonly TimeSpan UiStaleHoldWindow = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CompletedDisplayDuration = TimeSpan.FromMinutes(5);
    // 一瞬の Running シグナルを誤検出と切り捨てる閾値。短すぎるとチラつき、長すぎると本物の高速完了を見逃す。
    private static readonly TimeSpan MinRunningDurationForCompleted = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan RecentActivityWindow = TimeSpan.FromSeconds(30);

    private const int ProbePriorityActive = 3;
    private const int ProbePriorityRecent = 2;
    private const int ProbePriorityFocused = 1;
    private const int ProbePriorityIdle = 0;

    private readonly VscodeChatUiStatusReader _uiReader = new();
    private readonly ConcurrentDictionary<string, SlotRuntime> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    // 環境変数 TURTLE_AI_PROBE_DEBUG=1 で全 probe の詳細ログを panel.log に出力する。
    // 何が誤検出 / 検出漏れを引き起こしているかを調査するためのスイッチ。
    private static readonly bool ProbeDebugLogging =
        string.Equals(Environment.GetEnvironmentVariable("TURTLE_AI_PROBE_DEBUG"), "1", StringComparison.Ordinal);

    public AiStatusSnapshot Detect(WindowSlot slot, AppConfig config)
    {
        return Detect(
            new WindowSlotStatusSnapshot(
                slot.Name,
                slot.WindowHandle,
                slot.WindowTitle,
                slot.CurrentWorkspacePath,
                null,
                slot.IsFocused,
                slot.WindowHandle != IntPtr.Zero && slot.WindowHandle == WindowEnumerator.GetForegroundWindowHandle(),
                true),
            config);
    }

    internal AiStatusSnapshot Detect(WindowSlotStatusSnapshot slot, AppConfig config)
    {
        _ = config;

        if (slot.WindowHandle == IntPtr.Zero)
        {
            ClearSlotState(slot.Name);
            var emptyDetail = "VS Code は起動していません。";
            return new AiStatusSnapshot(AiStatus.Idle, emptyDetail, null, "UI")
            {
                Diagnostics = BuildDiagnostics(
                    slot.Name,
                    0,
                    AiStatus.Idle,
                    "no window",
                    UiAutomationProbeResult.Unknown(emptyDetail),
                    state: null)
            };
        }

        var now = DateTimeOffset.Now;
        var probe = slot.AllowUiAutomationProbe
            ? _uiReader.TryRead(slot)
            : UiAutomationProbeResult.Unknown("UI Automation の走査は今回のティックでスキップされました。");

        lock (_lock)
        {
            var state = _slots.GetOrAdd(NormalizeKey(slot.Name), _ => new SlotRuntime());
            var prevState = state.DisplayState;
            ApplyProbe(state, slot, probe, now);

            // 表示状態が変化した時 (特に Running 遷移) は、原因究明用に証拠をログに残す。
            if (prevState != state.DisplayState)
            {
                LogStateTransition(slot.Name, prevState, state.DisplayState, probe, state);
            }

            // 詳細デバッグモード: 毎 probe の発見シグナルをすべて記録する。
            if (ProbeDebugLogging)
            {
                LogProbeDebug(slot.Name, state.DisplayState, probe);
            }

            var diagnostics = BuildDiagnostics(slot.Name, slot.WindowHandle.ToInt64(), state.DisplayState, state.LastReason, probe, state);
            return new AiStatusSnapshot(state.DisplayState, state.LastDetail, state.LastEventAt, state.LastSourceLabel)
            {
                Diagnostics = diagnostics
            };
        }
    }

    private static void LogStateTransition(string slotName, AiStatus from, AiStatus to, UiAutomationProbeResult probe, SlotRuntime state)
    {
        // false positive の原因究明用。誰が見ても何が引き金になったかが分かる粒度で残す。
        var evidenceParts = new List<string>();
        if (probe.FoundRunningText) evidenceParts.Add($"text=\"{probe.EvidenceText}\"");
        if (probe.FoundRunningClass) evidenceParts.Add($"running-class");
        if (probe.FoundStopButton) evidenceParts.Add("stop-button");
        if (probe.FoundConfirmationButton) evidenceParts.Add($"confirmation=\"{probe.ConfirmationEvidenceText}\"");
        if (probe.FoundInputReady) evidenceParts.Add("input-ready");
        if (probe.TimedOut) evidenceParts.Add("timed-out");

        var evidence = evidenceParts.Count > 0 ? string.Join(", ", evidenceParts) : "no signals";
        DiagnosticLog.Write(
            $"AI状態 [{slotName}] {from} -> {to} : {state.LastReason} | evidence: {evidence} | automationId=\"{probe.EvidenceAutomationId}\" | className=\"{probe.EvidenceClassName}\"");
    }

    private static void LogProbeDebug(string slotName, AiStatus displayState, UiAutomationProbeResult probe)
    {
        // 1 probe あたりの全シグナルを記録する詳細モード。誤検出 / 検出漏れの両方の原因究明に使う。
        var parts = new List<string>();
        if (probe.FoundRunningText) parts.Add($"text=\"{probe.EvidenceText}\"");
        if (probe.FoundRunningClass) parts.Add("running-class");
        if (probe.FoundStopButton) parts.Add("stop-button");
        if (probe.FoundConfirmationButton) parts.Add($"confirm=\"{probe.ConfirmationEvidenceText}\"");
        if (probe.FoundInputReady) parts.Add("input-ready");
        if (probe.FoundSendButton) parts.Add("send-enabled");
        if (probe.FoundDisabledSendButton) parts.Add("send-disabled");
        if (probe.TimedOut) parts.Add("timeout");
        if (probe.ScanCompleted) parts.Add("scan-complete");

        var signals = parts.Count > 0 ? string.Join(", ", parts) : "no-signals";
        DiagnosticLog.Write(
            $"PROBE [{slotName}] display={displayState} | {signals} | inspected={probe.InspectedElementCount} | aid=\"{probe.EvidenceAutomationId}\" | cls=\"{probe.EvidenceClassName}\" | text=\"{probe.EvidenceText}\"");
    }

    public int GetUiAutomationProbePriority(WindowSlot slot)
    {
        if (slot.WindowHandle == IntPtr.Zero)
        {
            return ProbePriorityIdle;
        }

        if (!_slots.TryGetValue(NormalizeKey(slot.Name), out var state))
        {
            return slot.IsFocused ? ProbePriorityFocused : ProbePriorityIdle;
        }

        if (state.DisplayState is AiStatus.Running or AiStatus.WaitingForConfirmation or AiStatus.Completed)
        {
            return ProbePriorityActive;
        }

        var now = DateTimeOffset.Now;
        if (state.LastUiRunningAt is { } lastRunning && now - lastRunning <= RecentActivityWindow)
        {
            return ProbePriorityRecent;
        }

        if (slot.IsFocused)
        {
            return ProbePriorityFocused;
        }

        return ProbePriorityIdle;
    }

    public void Acknowledge(WindowSlot slot)
    {
        var key = NormalizeKey(slot.Name);
        lock (_lock)
        {
            if (!_slots.TryGetValue(key, out var state))
            {
                return;
            }

            state.AcknowledgedAt = DateTimeOffset.Now;
            if (state.DisplayState == AiStatus.Completed)
            {
                ResetRunningProgress(state);
                state.DisplayState = AiStatus.Idle;
                state.LastDetail = "AI は待機中です。";
                state.LastSourceLabel = "Ack";
                state.LastReason = "user acknowledged";
            }
        }
    }

    public void ResetSlotSession(WindowSlot slot)
    {
        ClearSlotState(slot.Name);
    }

    internal void ResetSlotSession(WindowSlotStatusSnapshot slot)
    {
        ClearSlotState(slot.Name);
    }

    public void SwapSlotSessions(string sourceSlotName, string targetSlotName)
    {
        if (string.IsNullOrWhiteSpace(sourceSlotName) || string.IsNullOrWhiteSpace(targetSlotName))
        {
            return;
        }

        var sourceKey = NormalizeKey(sourceSlotName);
        var targetKey = NormalizeKey(targetSlotName);
        if (string.Equals(sourceKey, targetKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_lock)
        {
            _slots.TryRemove(sourceKey, out var sourceState);
            _slots.TryRemove(targetKey, out var targetState);
            if (sourceState is not null)
            {
                _slots[targetKey] = sourceState;
            }

            if (targetState is not null)
            {
                _slots[sourceKey] = targetState;
            }
        }
    }

    private void ApplyProbe(SlotRuntime state, WindowSlotStatusSnapshot slot, UiAutomationProbeResult probe, DateTimeOffset now)
    {
        // Completed の自動失効を最初に処理する。
        if (state.DisplayState == AiStatus.Completed
            && state.CompletedAt is { } completedAt
            && now - completedAt > CompletedDisplayDuration)
        {
            ResetRunningProgress(state);
            state.DisplayState = AiStatus.Idle;
            state.LastDetail = "AI は待機中です。";
            state.LastSourceLabel = "Hysteresis";
            state.LastReason = "completed display expired";
        }

        switch (probe.Status)
        {
            case AiStatus.Running:
                HandleRunningSignal(state, probe, now);
                return;

            case AiStatus.WaitingForConfirmation:
                EnterWaitingForConfirmation(state, probe, now);
                return;
        }

        // Status == null。Suspect Running を抱えていて、走査が完了して Running が再確認されなかった
        // 場合は誤検出として直ちに破棄する。
        if (state.HasSuspectRunning && probe.ScanCompleted)
        {
            state.HasSuspectRunning = false;
            state.SuspectRunningAt = null;
        }

        // ここから先は probe.Status == null。
        // 走査が完全に終わって何も見つからなかったなら "UI で確認済みの ready 状態" として扱う。
        if (probe.ScanCompleted)
        {
            HandleScanCompletedNegative(state, probe, now);
            return;
        }

        // タイムアウト or 走査未完了。UI の判定材料がない。
        HandleProbeInconclusive(state, slot, probe, now);
    }

    private static void HandleRunningSignal(SlotRuntime state, UiAutomationProbeResult probe, DateTimeOffset now)
    {
        // UI Reader 側で誤検出源 (Document/Edit のテキスト、debug-stop ボタン等) を排除済み。
        // ここまで Running シグナルが到達したら即 Running 確定して良い。
        var evidenceKind = probe.FoundRunningClass ? "class"
            : probe.FoundStopButton ? "stop"
            : "text";
        EnterRunning(state, probe, now, evidenceKind);
        state.HasSuspectRunning = false;
        state.SuspectRunningAt = null;
    }

    private static void EnterRunning(SlotRuntime state, UiAutomationProbeResult probe, DateTimeOffset now, string evidenceKind)
    {
        var wasRunning = state.DisplayState == AiStatus.Running;
        state.DisplayState = AiStatus.Running;
        state.LastUiRunningAt = now;
        state.LastEventAt = probe.EvidenceAt ?? now;
        state.HasSeenUiRunningInSession = true;
        state.RunningSinceUtc ??= now;
        state.CompletedAt = null;
        state.AcknowledgedAt = null;
        state.LastEvidenceText = TrimEvidence(probe.EvidenceText);
        state.LastDetail = string.IsNullOrWhiteSpace(probe.Detail)
            ? "AI が実行中です。"
            : probe.Detail;
        state.LastSourceLabel = "UI";
        state.LastReason = wasRunning
            ? $"ui running (continued, {evidenceKind})"
            : $"ui running (entered, {evidenceKind})";
    }

    private static void EnterWaitingForConfirmation(SlotRuntime state, UiAutomationProbeResult probe, DateTimeOffset now)
    {
        state.DisplayState = AiStatus.WaitingForConfirmation;
        state.LastUiConfirmationAt = now;
        state.LastEventAt = probe.EvidenceAt ?? now;
        state.LastEvidenceText = TrimEvidence(probe.EvidenceText);
        state.LastDetail = string.IsNullOrWhiteSpace(probe.Detail)
            ? "AI が承認を待っています。"
            : probe.Detail;
        state.LastSourceLabel = "UI";
        state.LastReason = "ui confirmation";
        state.CompletedAt = null;
        state.AcknowledgedAt = null;
    }

    private static void HandleScanCompletedNegative(SlotRuntime state, UiAutomationProbeResult probe, DateTimeOffset now)
    {
        if (probe.HasReadyInputEvidence)
        {
            state.LastUiReadyAt = now;
        }

        switch (state.DisplayState)
        {
            case AiStatus.Running:
            case AiStatus.WaitingForConfirmation:
                if (state.RunningSinceUtc is { } runningSince
                    && now - runningSince >= MinRunningDurationForCompleted)
                {
                    state.DisplayState = AiStatus.Completed;
                    state.CompletedAt = now;
                    state.LastEventAt = now;
                    state.LastDetail = "AI が応答を完了しました。";
                    state.LastSourceLabel = "UI";
                    state.LastReason = "ui ready after running";
                    state.RunningSinceUtc = null;
                }
                else
                {
                    // 走査の Running 検出が一瞬で消えた: 誤検出として静かに Idle へ戻す。
                    ResetRunningProgress(state);
                    state.DisplayState = AiStatus.Idle;
                    state.LastDetail = "AI は待機中です。";
                    state.LastSourceLabel = "UI";
                    state.LastReason = "ui ready (running too brief, treated as false positive)";
                }

                break;

            case AiStatus.Completed:
                // 完了表示中。Acknowledged されていれば Idle へ。
                if (state.AcknowledgedAt is not null)
                {
                    ResetRunningProgress(state);
                    state.DisplayState = AiStatus.Idle;
                    state.LastDetail = "AI は待機中です。";
                    state.LastSourceLabel = "Ack";
                    state.LastReason = "ack after completion";
                }

                break;

            default:
                state.DisplayState = AiStatus.Idle;
                if (string.IsNullOrEmpty(state.LastDetail) || state.LastSourceLabel != "UI")
                {
                    state.LastDetail = "AI は待機中です。";
                }

                state.LastSourceLabel = "UI";
                state.LastReason = "ui idle";
                break;
        }
    }

    private static void HandleProbeInconclusive(SlotRuntime state, WindowSlotStatusSnapshot slot, UiAutomationProbeResult probe, DateTimeOffset now)
    {
        var staleness = state.LastUiRunningAt is { } lastRunning
            ? now - lastRunning
            : TimeSpan.MaxValue;

        switch (state.DisplayState)
        {
            case AiStatus.Running:
            case AiStatus.WaitingForConfirmation:
                if (staleness > UiStaleHoldWindow)
                {
                    // 長時間 UI で Running が確認できない。Completed か Idle に落とす。
                    if (state.HasSeenUiRunningInSession
                        && state.RunningSinceUtc is { } runningSince
                        && now - runningSince >= MinRunningDurationForCompleted)
                    {
                        state.DisplayState = AiStatus.Completed;
                        state.CompletedAt = now;
                        state.LastEventAt = now;
                        state.LastDetail = "AI 応答が確認できなくなりました (完了とみなします)。";
                        state.LastSourceLabel = "Hysteresis";
                        state.LastReason = "ui stale, downgraded to completed";
                        state.RunningSinceUtc = null;
                    }
                    else
                    {
                        ResetRunningProgress(state);
                        state.DisplayState = AiStatus.Idle;
                        state.LastDetail = "AI は待機中です。";
                        state.LastSourceLabel = "Hysteresis";
                        state.LastReason = "ui stale, no confirmed running";
                    }
                }
                else
                {
                    state.LastSourceLabel = "Hysteresis";
                    state.LastReason = "ui inconclusive, holding previous running";
                }

                break;

            case AiStatus.Completed:
                // Completed 表示は CompletedDisplayDuration の自動失効に任せる。
                state.LastSourceLabel = "Hysteresis";
                state.LastReason = "ui inconclusive, holding completed";
                break;

            default:
                state.LastSourceLabel = "Hysteresis";
                state.LastReason = probe.TimedOut ? "ui timeout, idle" : "ui inconclusive, idle";
                break;
        }

        _ = slot;
    }

    private static void ResetRunningProgress(SlotRuntime state)
    {
        state.RunningSinceUtc = null;
        state.CompletedAt = null;
        state.AcknowledgedAt = null;
        state.HasSeenUiRunningInSession = false;
        state.HasSuspectRunning = false;
        state.SuspectRunningAt = null;
    }

    private void ClearSlotState(string slotName)
    {
        if (string.IsNullOrWhiteSpace(slotName))
        {
            return;
        }

        var key = NormalizeKey(slotName);
        lock (_lock)
        {
            _slots.TryRemove(key, out _);
        }
    }

    private static string NormalizeKey(string slotName)
    {
        return string.IsNullOrWhiteSpace(slotName) ? string.Empty : slotName.Trim();
    }

    private static string TrimEvidence(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 80 ? trimmed : trimmed[..77] + "...";
    }

    private static AiStatusDiagnostics BuildDiagnostics(
        string slotName,
        long hwnd,
        AiStatus displayState,
        string reason,
        UiAutomationProbeResult probe,
        SlotRuntime? state)
    {
        var slotLevel = new SlotAiStatusSnapshot(
            State: displayState.ToString(),
            Source: state?.LastSourceLabel ?? "UI",
            Owner: string.Empty,
            LastUiRunningAt: state?.LastUiRunningAt,
            LastUiReadyAt: state?.LastUiReadyAt,
            LastEvidenceAt: state?.LastEventAt,
            EvidenceText: state?.LastEvidenceText ?? string.Empty,
            Reason: reason ?? string.Empty,
            HasObservedUiRunning: state?.HasSeenUiRunningInSession ?? false,
            ConsecutiveNegativeUiProbes: 0);

        var idleEngine = new AiEngineStatusSnapshot("Idle", "Confirmed", null, string.Empty);

        return new AiStatusDiagnostics(
            slotName,
            hwnd,
            slotLevel,
            idleEngine,
            idleEngine,
            idleEngine,
            probe,
            slotLevel.Source,
            string.Empty,
            string.Empty,
            slotLevel.Reason);
    }

    private sealed class SlotRuntime
    {
        public AiStatus DisplayState = AiStatus.Idle;
        public DateTimeOffset? RunningSinceUtc;
        public DateTimeOffset? LastUiRunningAt;
        public DateTimeOffset? LastUiReadyAt;
        public DateTimeOffset? LastUiConfirmationAt;
        public DateTimeOffset? CompletedAt;
        public DateTimeOffset? AcknowledgedAt;
        public DateTimeOffset? LastEventAt;
        public bool HasSeenUiRunningInSession;
        public bool HasSuspectRunning;
        public DateTimeOffset? SuspectRunningAt;
        public string LastEvidenceText = string.Empty;
        public string LastDetail = "AI は待機中です。";
        public string LastSourceLabel = "UI";
        public string LastReason = "initial";
    }
}

public sealed record AiStatusSnapshot(
    AiStatus Status,
    string Detail,
    DateTimeOffset? EventAt,
    string SourceName = "")
{
    public AiStatusDiagnostics? Diagnostics { get; init; }
}

public sealed record AiStatusDiagnostics(
    string SlotName,
    long Hwnd,
    SlotAiStatusSnapshot SlotLevel,
    AiEngineStatusSnapshot Copilot,
    AiEngineStatusSnapshot Codex,
    AiEngineStatusSnapshot Claude,
    UiAutomationProbeResult UiProbe,
    string FinalSource,
    string FinalEngine,
    string FinalConfidence,
    string FinalReason);

public sealed record SlotAiStatusSnapshot(
    string State,
    string Source,
    string Owner,
    DateTimeOffset? LastUiRunningAt,
    DateTimeOffset? LastUiReadyAt,
    DateTimeOffset? LastEvidenceAt,
    string EvidenceText,
    string Reason,
    bool HasObservedUiRunning,
    int ConsecutiveNegativeUiProbes);

public sealed record AiEngineStatusSnapshot(
    string State,
    string Confidence,
    DateTimeOffset? LastEvidenceAt,
    string Reason);
