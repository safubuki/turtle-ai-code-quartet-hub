using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class AiStatusDetector
{
    private const string CodexSourceName = "Codex";
    private const string CopilotSourceName = "Copilot";
    private static readonly TimeSpan ErrorSignalWindow = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan UiAutomationCachedEvidenceWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CompletionLogClockSkewTolerance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CopilotRunningLeaseWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SlotRunningHoldWindow = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan CodexProbableRunningWindow = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan CodexSuspectWindow = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan CodexQuietCompletionWindow = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan CodexConfirmationWindow = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CodexBroadcastOwnerStickyWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UiReadyCompletionDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FutureLogTolerance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DecisionLogInterval = TimeSpan.FromSeconds(10);
    private const int MaxRecentLogBytes = 96 * 1024;
    private const int MaxCandidateLogFilesPerSource = 6;
    private const int NegativeUiProbeGraceCount = 1;
    private static readonly string[] ExtensionHostDirectoryNames = ["exthost", "remoteexthost", "remoteexhost"];

    private static readonly ExtensionLogSource CodexLogSource = new(
        CodexSourceName,
        ["openai.chatgpt"],
        "Codex.log",
        "thread-stream-state-changed",
        [],
        [],
        ["Activating Codex extension", "Initialize received", "method=client-status-changed"],
        [],
        [],
        false,
        [],
        [],
        ["commandExecution/requestApproval"]);

    private static readonly ExtensionLogSource CopilotLogSource = new(
        CopilotSourceName,
        ["GitHub.copilot-chat", "github.copilot-chat"],
        "GitHub Copilot Chat.log",
        "ccreq:",
        [" | success |", " | cancelled |", " | unknown |", "request done:", "message 0 returned", "Stop hook result:"],
        ["[panel/editAgent]", "[retry-server-error-panel/editAgent]", "[retry-error-panel/editAgent]"],
        ["Copilot Chat:", "Logged in as", "Got Copilot token"],
        ["Latest entry:", " | markdown", " | success |", " | cancelled |", " | networkError |"],
        [" | networkError |"],
        false,
        [],
        [],
        []);

    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly VscodeChatUiStatusReader _uiStatusReader = new();
    private readonly object _stateLock = new();
    private readonly object _codexBroadcastOwnerLock = new();
    private readonly ConcurrentDictionary<string, EngineRuntimeState> _engineStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SlotRuntimeState> _slotStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _dismissedAtBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _slotStartedAtByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, UiAutomationProbeResult> _lastUiProbeBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastDecisionSignatureBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastDecisionLoggedAtBySlot = new(StringComparer.OrdinalIgnoreCase);
    private string? _codexBroadcastOwnerSlotKey;
    private DateTimeOffset? _codexBroadcastOwnerLastActivityAt;
    private static readonly ConcurrentDictionary<string, CachedLogEvidence> LogEvidenceCache = new(StringComparer.OrdinalIgnoreCase);

    // 86a305a 成功ベースライン: シンプルな表示優先フロー用の状態
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRunningSeenBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _completedAtBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _confirmationRequestedAtBySlot = new(StringComparer.OrdinalIgnoreCase);

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
        if (slot.WindowHandle == IntPtr.Zero)
        {
            ClearSlotState(slot);
            return new AiStatusSnapshot(AiStatus.Idle, "VS Code は起動していません。", null)
            {
                Diagnostics = new AiStatusDiagnostics(
                    slot.Name,
                    0,
                    ToDiagnostic(SlotRuntimeState.Idle()),
                    ToDiagnostic(EngineRuntimeState.Idle(AiEngine.Copilot)),
                    ToDiagnostic(EngineRuntimeState.Idle(AiEngine.Codex)),
                    UiAutomationProbeResult.Unknown("VS Code ウィンドウがないため UI Automation は実行していません。"),
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    "no window")
            };
        }

        var slotKey = GetSlotKey(slot);
        var slotStartedAt = GetSlotStartedAt(slot);
        var now = DateTimeOffset.Now;
        var userDataDirectory = SlotUserDataPaths.GetEffectiveUserDataDirectory(slot.Name, config);
        var canReadLogs = !string.IsNullOrWhiteSpace(userDataDirectory) && Directory.Exists(userDataDirectory);
        var copilotLog = canReadLogs
            ? FilterEvidence(slotKey, slotStartedAt, ReadLatestEvidence(userDataDirectory!, CopilotLogSource))
            : AiLogEvidence.Empty(CopilotLogSource);
        var codexLog = canReadLogs
            ? FilterEvidence(slotKey, slotStartedAt, ReadLatestEvidence(userDataDirectory!, CodexLogSource))
            : AiLogEvidence.Empty(CodexLogSource);

        var uiProbe = slot.AllowUiAutomationProbe
            ? _uiStatusReader.TryRead(slot)
            : GetCachedUiProbe(slotKey);

        if (slot.AllowUiAutomationProbe)
        {
            _lastUiProbeBySlot[slotKey] = uiProbe;
        }

        lock (_stateLock)
        {
            // 主判定: 86a305a 成功ベースラインの表示優先フロー
            var baselineDecision = TryDecideByLegacyFlow(slot, slotKey, copilotLog, codexLog, uiProbe, now);

            // 診断用: engine/slot state machine を常時進める
            var previousSlotState = GetSlotState(slotKey);
            var previousCopilot = GetEngineState(slotKey, AiEngine.Copilot);
            var previousCodex = GetEngineState(slotKey, AiEngine.Codex);
            var uiOwner = ResolveUiAutomationOwner(slot, uiProbe, previousCopilot, previousCodex, copilotLog, codexLog, now);

            var nextCopilot = AdvanceCopilotState(previousCopilot, copilotLog, uiProbe, slot.AllowUiAutomationProbe, uiOwner, now);
            var nextCodex = AdvanceCodexState(slot, slotKey, previousCodex, codexLog, uiProbe, slot.AllowUiAutomationProbe, uiOwner, now);
            var nextSlotState = AdvanceSlotState(previousSlotState, uiProbe, slot.AllowUiAutomationProbe, uiOwner, nextCopilot, nextCodex, copilotLog, codexLog, now);

            StoreEngineState(slotKey, nextCopilot);
            StoreEngineState(slotKey, nextCodex);
            StoreSlotState(slotKey, nextSlotState);

            var complexDecision = AggregateDecision(canReadLogs, nextSlotState, nextCopilot, nextCodex);

            // baseline が Running/WaitingForConfirmation/Completed → baseline を採用
            // それ以外 (Idle/null) は複雑 state machine の結果を使用
            AiStatus finalStatus;
            string finalDetail;
            DateTimeOffset? finalEventAt;
            string finalSourceName;
            string finalReason;

            if (baselineDecision?.Status is AiStatus.Running or AiStatus.WaitingForConfirmation or AiStatus.Completed)
            {
                finalStatus = baselineDecision.Status;
                finalDetail = baselineDecision.Detail;
                finalEventAt = baselineDecision.EventAt;
                finalSourceName = baselineDecision.SourceName;
                finalReason = $"simpleFlow={baselineDecision.SourceName}";
            }
            else
            {
                finalStatus = complexDecision.Status;
                finalDetail = complexDecision.Detail;
                finalEventAt = complexDecision.EventAt;
                finalSourceName = complexDecision.SourceName;
                finalReason = complexDecision.Reason;
            }

            var diagnostics = new AiStatusDiagnostics(
                slot.Name,
                slot.WindowHandle.ToInt64(),
                ToDiagnostic(nextSlotState),
                ToDiagnostic(nextCopilot),
                ToDiagnostic(nextCodex),
                uiProbe,
                finalSourceName,
                complexDecision.EngineName,
                complexDecision.Confidence,
                finalReason);

            MaybeLogDecision(slotKey, slot.Name, finalStatus, diagnostics);

            return new AiStatusSnapshot(finalStatus, finalDetail, finalEventAt, finalSourceName)
            {
                Diagnostics = diagnostics
            };
        }
    }

    // 86a305a 成功ベースライン: シンプルな表示優先フロー。
    // Running/WaitingForConfirmation/Completed を返した場合、その結果を主判定として採用する。
    // null を返した場合は複雑な state machine の結果にフォールスルーする。
    // 注意: _stateLock 内で呼ぶこと。
    private AiStatusSnapshot? TryDecideByLegacyFlow(
        WindowSlotStatusSnapshot slot,
        string slotKey,
        AiLogEvidence copilotLog,
        AiLogEvidence codexLog,
        UiAutomationProbeResult uiProbe,
        DateTimeOffset now)
    {
        var hadPreviousRunning = _lastRunningSeenBySlot.TryGetValue(slotKey, out var lastRunningSeenAt);
        var hasDirectUiRunning = HasDirectUiRunning(uiProbe);
        var hasDirectUiWaiting = HasDirectUiWaiting(uiProbe);

        // --- Step 2: ログの Running/WaitingForConfirmation を採用 (FilterEvidence 済みでスタールガード適用済み) ---
        // Copilot: ccreq: が最新完了シグナルより新しければ Running
        var copilotRunningAt = copilotLog.LastRunningSignalAt;
        var copilotCompletedAt = copilotLog.LastCompletionSignalAt;
        var copilotHasRunning = copilotRunningAt.HasValue
            && (!copilotCompletedAt.HasValue || copilotRunningAt.Value > copilotCompletedAt.Value);

        // Codex: broadcast 対策付きで Running/WaitingForConfirmation 判定
        // foreground/focused または このスロットで最近 Running を確認していれば owned とみなす
        var codexRunningAt = codexLog.LastRunningSignalAt;
        var codexConfirmAt = codexLog.LastConfirmationSignalAt;
        var codexOwned = slot.IsForeground
            || slot.IsFocused
            || (hadPreviousRunning && now - lastRunningSeenAt <= CodexBroadcastOwnerStickyWindow);
        var codexHasRunning = codexOwned
            && codexRunningAt.HasValue
            && IsRecent(codexRunningAt, CodexBroadcastOwnerStickyWindow, now)  // Fix A: 時間窓を追加して古いログシグナルによる永続Running を防止
            && (!codexConfirmAt.HasValue || codexRunningAt.Value >= codexConfirmAt.Value);
        var codexHasWaiting = codexOwned
            && codexConfirmAt.HasValue
            && (!codexRunningAt.HasValue || codexConfirmAt.Value > codexRunningAt.Value);

        if (copilotHasRunning || codexHasRunning)
        {
            string sourceName;
            DateTimeOffset? eventAt;
            if (copilotHasRunning && codexHasRunning)
            {
                var cp = copilotRunningAt!.Value;
                var cx = codexRunningAt!.Value;
                sourceName = cp >= cx ? CopilotSourceName : CodexSourceName;
                eventAt = cp >= cx ? cp : cx;
            }
            else if (copilotHasRunning)
            {
                sourceName = CopilotSourceName;
                eventAt = copilotRunningAt;
            }
            else
            {
                sourceName = CodexSourceName;
                eventAt = codexRunningAt;
            }

            _lastRunningSeenBySlot[slotKey] = Max(eventAt, now) ?? now;  // Fix B: アンカーを now 下限にすることでログタイムスタンプへの逆戻りを防止
            _completedAtBySlot.TryRemove(slotKey, out _);
            _confirmationRequestedAtBySlot.TryRemove(slotKey, out _);
            return new AiStatusSnapshot(AiStatus.Running, $"{sourceName}: ログからAI実行中を検出しました。", eventAt, sourceName);
        }

        if (codexHasWaiting)
        {
            _confirmationRequestedAtBySlot[slotKey] = now;  // Fix C-2b: 検出時刻を格納してTTLを検出ベースにする
            _completedAtBySlot.TryRemove(slotKey, out _);
            return new AiStatusSnapshot(AiStatus.WaitingForConfirmation, "Codex: ログからユーザー確認待ちを検出しました。", codexConfirmAt, CodexSourceName);
        }

        // --- Step 3: UIA Running → 採用し lastRunningSeen を更新 ---
        if (hasDirectUiRunning)
        {
            var evidenceAt = uiProbe.EvidenceAt ?? now;
            _lastRunningSeenBySlot[slotKey] = evidenceAt;
            _completedAtBySlot.TryRemove(slotKey, out _);
            _confirmationRequestedAtBySlot.TryRemove(slotKey, out _);
            return new AiStatusSnapshot(AiStatus.Running, uiProbe.Detail, evidenceAt, "UiAutomation");
        }

        // --- Step 4: UIA WaitingForConfirmation → 採用し confirmationRequested を更新 ---
        if (hasDirectUiWaiting)
        {
            var evidenceAt = uiProbe.EvidenceAt ?? now;
            _confirmationRequestedAtBySlot[slotKey] = now;  // Fix C-2b: 検出時刻を格納してTTLを検出ベースにする
            _completedAtBySlot.TryRemove(slotKey, out _);
            return new AiStatusSnapshot(AiStatus.WaitingForConfirmation, uiProbe.Detail, evidenceAt, "UiAutomation");
        }

        // --- Step 5: 直前 Running を短時間保持 (12秒 Running bridge) ---
        // Copilot の単独完了ログがある場合は bridge をスキップして Step 6 へ
        var hasCopilotStandaloneCompletion = copilotCompletedAt.HasValue && !copilotHasRunning;
        if (hadPreviousRunning
            && now - lastRunningSeenAt <= SlotRunningHoldWindow
            && !hasCopilotStandaloneCompletion)
        {
            return new AiStatusSnapshot(AiStatus.Running, "VS Code UI: 直前の実行表示を保持しています。", lastRunningSeenAt, "bridge");
        }

        // --- Step 6: ログの Completed/Error を採用 ---
        var copilotErrorAt = copilotLog.LastErrorSignalAt;
        if (copilotErrorAt.HasValue
            && (!copilotCompletedAt.HasValue || copilotErrorAt.Value >= copilotCompletedAt.Value)
            && (!copilotRunningAt.HasValue || copilotErrorAt.Value >= copilotRunningAt.Value))
        {
            return new AiStatusSnapshot(AiStatus.Error, "Copilot: エラーを検出しました。", copilotErrorAt, CopilotSourceName);
        }

        if (hasCopilotStandaloneCompletion)
        {
            _completedAtBySlot[slotKey] = copilotCompletedAt!.Value;
            _lastRunningSeenBySlot.TryRemove(slotKey, out _);
            _confirmationRequestedAtBySlot.TryRemove(slotKey, out _);
            return new AiStatusSnapshot(AiStatus.Completed, "Copilot: AI実行は完了しました。", copilotCompletedAt, CopilotSourceName);
        }

        // --- Step 7: 直前の WaitingForConfirmation を保持 (Fix C-2a: TTL付きで固着を防止) ---
        if (_confirmationRequestedAtBySlot.TryGetValue(slotKey, out var lastConfirmationSeenAt))
        {
            if (IsRecent(lastConfirmationSeenAt, CodexConfirmationWindow, now))
            {
                return new AiStatusSnapshot(AiStatus.WaitingForConfirmation, "VS Code UI: 直前のユーザー確認待ちを保持しています。", lastConfirmationSeenAt, "bridge");
            }

            // TTL 切れ: 確認が解消されたと判断して Completed へ遷移
            _confirmationRequestedAtBySlot.TryRemove(slotKey, out _);
            _completedAtBySlot[slotKey] = now;
            _lastRunningSeenBySlot.TryRemove(slotKey, out _);
            return new AiStatusSnapshot(AiStatus.Completed, "VS Code UI: 確認待ちが解消されました。", now, "confirmationExpired");
        }

        // --- Step 8: 直前の Completed を保持 ---
        if (_completedAtBySlot.TryGetValue(slotKey, out var legacyCompletedAt))
        {
            return new AiStatusSnapshot(AiStatus.Completed, "VS Code UI: 直前のAI実行完了を保持しています。", legacyCompletedAt, "hold");
        }

        // --- Step 9: Running が消えた → Idle ではなく Completed へ ---
        if (hadPreviousRunning)
        {
            _lastRunningSeenBySlot.TryRemove(slotKey, out _);
            _completedAtBySlot[slotKey] = now;
            _confirmationRequestedAtBySlot.TryRemove(slotKey, out _);
            return new AiStatusSnapshot(AiStatus.Completed, $"VS Code UI: {lastRunningSeenAt:HH:mm:ss} の実行表示が消えました。", now, "runningExpired");
        }

        // --- Step 10: 証拠なし → null (複雑 state machine にフォールスルー) ---
        return null;
    }

    public bool ShouldPrioritizeUiAutomationProbe(WindowSlot slot)
    {
        return GetUiAutomationProbePriority(slot) > 0;
    }

    public int GetUiAutomationProbePriority(WindowSlot slot)
    {
        if (slot.WindowHandle == IntPtr.Zero)
        {
            return 0;
        }

        var slotKey = GetSlotKey(slot);
        lock (_stateLock)
        {
            var now = DateTimeOffset.Now;

            // legacy flow が Running/WaitingForConfirmation を保持している場合は最高優先度
            if (_lastRunningSeenBySlot.TryGetValue(slotKey, out var lastRunning)
                && now - lastRunning <= SlotRunningHoldWindow)
            {
                return 4;
            }

            if (_confirmationRequestedAtBySlot.ContainsKey(slotKey))
            {
                return 4;
            }

            var slotState = GetSlotState(slotKey);
            if (slotState.State is SlotRuntimeStatus.WaitingForConfirmation or SlotRuntimeStatus.Running
                || slotState.HasObservedUiRunning)
            {
                return 4;
            }

            if (slotState.State == SlotRuntimeStatus.SuspectRunning)
            {
                return 3;
            }

            var copilot = GetEngineState(slotKey, AiEngine.Copilot);
            var codex = GetEngineState(slotKey, AiEngine.Codex);
            if (IsEngineDisplayEligible(copilot) || IsEngineDisplayEligible(codex))
            {
                return 2;
            }

            return copilot.State == InternalEngineState.SuspectRunning || codex.State == InternalEngineState.SuspectRunning
                ? 1
                : 0;
        }
    }

    public void Acknowledge(WindowSlot slot)
    {
        var slotKey = GetSlotKey(slot);
        lock (_stateLock)
        {
            StoreSlotState(slotKey, SlotRuntimeState.Idle());
            StoreEngineState(slotKey, EngineRuntimeState.Idle(AiEngine.Copilot));
            StoreEngineState(slotKey, EngineRuntimeState.Idle(AiEngine.Codex));
            _lastUiProbeBySlot.TryRemove(slotKey, out _);
            _dismissedAtBySlot[slotKey] = DateTimeOffset.Now;
            // legacy flow state もリセット
            _lastRunningSeenBySlot.TryRemove(slotKey, out _);
            _completedAtBySlot.TryRemove(slotKey, out _);
            _confirmationRequestedAtBySlot.TryRemove(slotKey, out _);
        }
    }

    public void ResetSlotSession(WindowSlot slot)
    {
        ClearSlotState(slot);
        _slotStartedAtByName[slot.Name] = DateTimeOffset.Now;
    }

    public void SwapSlotSessions(string sourceSlotName, string targetSlotName)
    {
        SwapPrefixedEntries(_engineStates, sourceSlotName, targetSlotName);
        SwapPrefixedEntries(_slotStates, sourceSlotName, targetSlotName);
        SwapPrefixedEntries(_dismissedAtBySlot, sourceSlotName, targetSlotName);
        SwapPrefixedEntries(_lastUiProbeBySlot, sourceSlotName, targetSlotName);
        SwapPrefixedEntries(_lastDecisionSignatureBySlot, sourceSlotName, targetSlotName);
        SwapPrefixedEntries(_lastDecisionLoggedAtBySlot, sourceSlotName, targetSlotName);
        // legacy flow state もスワップ
        SwapPrefixedEntries(_lastRunningSeenBySlot, sourceSlotName, targetSlotName);
        SwapPrefixedEntries(_completedAtBySlot, sourceSlotName, targetSlotName);
        SwapPrefixedEntries(_confirmationRequestedAtBySlot, sourceSlotName, targetSlotName);
        SwapCodexBroadcastOwner(sourceSlotName, targetSlotName);

        var hasSource = _slotStartedAtByName.TryRemove(sourceSlotName, out var sourceStarted);
        var hasTarget = _slotStartedAtByName.TryRemove(targetSlotName, out var targetStarted);
        if (hasSource)
        {
            _slotStartedAtByName[targetSlotName] = sourceStarted;
        }

        if (hasTarget)
        {
            _slotStartedAtByName[sourceSlotName] = targetStarted;
        }
    }

    private static string GetEngineStateKey(string slotKey, AiEngine engine)
    {
        return $"{slotKey}|{engine}";
    }

    private EngineRuntimeState GetEngineState(string slotKey, AiEngine engine)
    {
        return _engineStates.TryGetValue(GetEngineStateKey(slotKey, engine), out var state)
            ? state
            : EngineRuntimeState.Idle(engine);
    }

    private void StoreEngineState(string slotKey, EngineRuntimeState state)
    {
        _engineStates[GetEngineStateKey(slotKey, state.Engine)] = state;
    }

    private SlotRuntimeState GetSlotState(string slotKey)
    {
        return _slotStates.TryGetValue(slotKey, out var state)
            ? state
            : SlotRuntimeState.Idle();
    }

    private void StoreSlotState(string slotKey, SlotRuntimeState state)
    {
        _slotStates[slotKey] = state;
    }

    private UiAutomationProbeResult GetCachedUiProbe(string slotKey)
    {
        if (_lastUiProbeBySlot.TryGetValue(slotKey, out var cached)
            && cached.Status.HasValue
            && cached.EvidenceAt is { } evidenceAt
            && DateTimeOffset.Now - evidenceAt <= UiAutomationCachedEvidenceWindow)
        {
            return cached;
        }

        return UiAutomationProbeResult.Unknown("この refresh では UI Automation probe を実行していません。");
    }

    private static UiAutomationOwner ResolveUiAutomationOwner(
        WindowSlotStatusSnapshot slot,
        UiAutomationProbeResult uiProbe,
        EngineRuntimeState previousCopilot,
        EngineRuntimeState previousCodex,
        AiLogEvidence copilotLog,
        AiLogEvidence codexLog,
        DateTimeOffset now)
    {
        if (!HasDirectUiActivity(uiProbe))
        {
            return UiAutomationOwner.None;
        }

        var copilotHintAt = GetCopilotUiHintAt(previousCopilot, copilotLog, now);
        var codexHintAt = GetCodexUiHintAt(previousCodex, codexLog, now);
        var hasCopilotContext = HasCopilotUiContext(uiProbe);
        var hasCodexContext = HasCodexUiContext(uiProbe);

        if (HasDirectUiWaiting(uiProbe))
        {
            if (IsRecent(codexLog.LastConfirmationSignalAt, CodexConfirmationWindow, now)
                || previousCodex.State == InternalEngineState.WaitingForConfirmation)
            {
                return UiAutomationOwner.Codex;
            }
        }

        if (hasCopilotContext && !hasCodexContext)
        {
            return UiAutomationOwner.Copilot;
        }

        if (hasCodexContext && !hasCopilotContext)
        {
            return UiAutomationOwner.Codex;
        }

        if (copilotHintAt.HasValue && !codexHintAt.HasValue)
        {
            return UiAutomationOwner.Copilot;
        }

        if (codexHintAt.HasValue && !copilotHintAt.HasValue)
        {
            return UiAutomationOwner.Codex;
        }

        if (copilotHintAt.HasValue && codexHintAt.HasValue)
        {
            var delta = codexHintAt.Value - copilotHintAt.Value;
            if (delta.Duration() <= TimeSpan.FromSeconds(2))
            {
                return UiAutomationOwner.UnknownAi;
            }

            return delta > TimeSpan.Zero
                ? UiAutomationOwner.Codex
                : UiAutomationOwner.Copilot;
        }

        if (previousCopilot.State is InternalEngineState.Running or InternalEngineState.WaitingForConfirmation
            && previousCodex.State is not (InternalEngineState.Running or InternalEngineState.WaitingForConfirmation))
        {
            return UiAutomationOwner.Copilot;
        }

        if (previousCodex.State is InternalEngineState.Running or InternalEngineState.WaitingForConfirmation
            && previousCopilot.State is not (InternalEngineState.Running or InternalEngineState.WaitingForConfirmation))
        {
            return UiAutomationOwner.Codex;
        }

        return UiAutomationOwner.UnknownAi;
    }

    private static bool HasDirectUiActivity(UiAutomationProbeResult uiProbe)
    {
        return HasDirectUiWaiting(uiProbe) || HasDirectUiRunning(uiProbe);
    }

    private static bool HasDirectUiWaiting(UiAutomationProbeResult uiProbe)
    {
        return !uiProbe.TimedOut
            && (uiProbe.Status == AiStatus.WaitingForConfirmation || uiProbe.FoundConfirmationButton);
    }

    private static bool HasDirectUiRunning(UiAutomationProbeResult uiProbe)
    {
        return !uiProbe.TimedOut
            && (uiProbe.Status == AiStatus.Running
                || uiProbe.FoundRunningText
                || uiProbe.FoundRunningClass
                || uiProbe.FoundStopButton);
    }

    private static bool HasCopilotUiContext(UiAutomationProbeResult uiProbe)
    {
        return ContainsUiContext(uiProbe,
            "copilot",
            "copilot-chat",
            "github.copilot",
            "chat-response-loading",
            "chat-thinking-box");
    }

    private static bool HasCodexUiContext(UiAutomationProbeResult uiProbe)
    {
        return ContainsUiContext(uiProbe,
            "codex",
            "openai",
            "openai.chatgpt",
            "requestapproval",
            "thread-stream-state-changed");
    }

    private static bool ContainsUiContext(UiAutomationProbeResult uiProbe, params string[] hints)
    {
        var haystacks = new[]
        {
            uiProbe.EvidenceText,
            uiProbe.EvidenceAutomationId,
            uiProbe.EvidenceClassName,
            uiProbe.Detail
        };

        return haystacks.Any(value => !string.IsNullOrWhiteSpace(value)
            && hints.Any(hint => value.Contains(hint, StringComparison.OrdinalIgnoreCase)));
    }

    private static DateTimeOffset? GetCopilotUiHintAt(EngineRuntimeState previous, AiLogEvidence log, DateTimeOffset now)
    {
        var hint = IsRecent(log.LastRunningSignalAt, CopilotRunningLeaseWindow, now)
            ? log.LastRunningSignalAt
            : null;

        if (previous.State is InternalEngineState.Running or InternalEngineState.WaitingForConfirmation
            && IsRecent(previous.LastRunningEvidenceAt, CopilotRunningLeaseWindow, now))
        {
            hint = Max(hint, previous.LastRunningEvidenceAt);
        }

        return hint;
    }

    private static DateTimeOffset? GetCodexUiHintAt(EngineRuntimeState previous, AiLogEvidence log, DateTimeOffset now)
    {
        var hint = IsRecent(log.LastActivitySignalAt, CodexProbableRunningWindow, now)
            ? log.LastActivitySignalAt
            : null;

        hint = Max(hint, IsRecent(log.LastRunningSignalAt, CodexProbableRunningWindow, now)
            ? log.LastRunningSignalAt
            : null);
        hint = Max(hint, IsRecent(log.LastConfirmationSignalAt, CodexConfirmationWindow, now)
            ? log.LastConfirmationSignalAt
            : null);

        if (previous.State is InternalEngineState.SuspectRunning or InternalEngineState.Running or InternalEngineState.WaitingForConfirmation
            && IsRecent(previous.LastRunningEvidenceAt, CodexProbableRunningWindow, now))
        {
            hint = Max(hint, previous.LastRunningEvidenceAt);
        }

        return hint;
    }

    private static bool IsFreshNegativeUiProbe(UiAutomationProbeResult uiProbe, bool hasFreshUiProbe)
    {
        return hasFreshUiProbe
            && !uiProbe.TimedOut
            && uiProbe.ScanCompleted
            && uiProbe.Status is null
            && !uiProbe.HasRunningEvidence;
    }

    private static bool IsUiReadyAfterRunning(
        EngineRuntimeState previous,
        UiAutomationProbeResult uiProbe,
        bool hasFreshUiProbe,
        DateTimeOffset now)
    {
        if (!hasFreshUiProbe
            || uiProbe.TimedOut
            || uiProbe.Status is not null
            || uiProbe.FoundConfirmationButton
            || uiProbe.HasRunningEvidence
            || !uiProbe.HasReadyInputEvidence)
        {
            return false;
        }

        if (previous.State != InternalEngineState.Running
            || previous.Confidence is not (EvidenceConfidence.Confirmed or EvidenceConfidence.Probable)
            || previous.LastRunningEvidenceAt is not { } lastRunningEvidenceAt)
        {
            return false;
        }

        return now - lastRunningEvidenceAt >= UiReadyCompletionDelay;
    }

    private static string GetUiReadyCompletionReason()
    {
        return "実行表示と停止ボタンが消え、入力可能状態に戻ったため完了と判定しました。";
    }

    private static EngineRuntimeState AdvanceCopilotState(
        EngineRuntimeState previous,
        AiLogEvidence log,
        UiAutomationProbeResult uiProbe,
        bool hasFreshUiProbe,
        UiAutomationOwner uiOwner,
        DateTimeOffset now)
    {
        var runningAt = IgnoreIfNotNewerThan(log.LastRunningSignalAt, previous.CompletedAt);
        var errorAt = IgnoreIfNotNewerThan(log.LastErrorSignalAt, previous.CompletedAt);
        var freshNegative = IsFreshNegativeUiProbe(uiProbe, hasFreshUiProbe);
        var hasUiRunning = uiOwner == UiAutomationOwner.Copilot && uiProbe.Status is AiStatus.Running or AiStatus.WaitingForConfirmation;

        if (hasUiRunning)
        {
            var evidenceAt = uiProbe.EvidenceAt ?? now;
            var runningStartedAt = previous.RunningStartedAt ?? evidenceAt;
            var baselineAt = previous.CompletionBaselineAt ?? previous.CompletedAt;
            return new EngineRuntimeState(
                AiEngine.Copilot,
                uiProbe.Status == AiStatus.WaitingForConfirmation ? InternalEngineState.WaitingForConfirmation : InternalEngineState.Running,
                EvidenceConfidence.Confirmed,
                previous.FirstSeenAt ?? runningStartedAt,
                evidenceAt,
                runningStartedAt,
                baselineAt,
                null,
                EvidenceSource.UiAutomation,
                uiProbe.Detail,
                evidenceAt,
                evidenceAt,
                previous.LastUiNegativeAt,
                0);
        }

        if (IsUiReadyAfterRunning(previous, uiProbe, hasFreshUiProbe, now))
        {
            return new EngineRuntimeState(
                AiEngine.Copilot,
                InternalEngineState.Completed,
                previous.Confidence,
                previous.FirstSeenAt ?? previous.RunningStartedAt ?? now,
                now,
                previous.RunningStartedAt,
                previous.CompletionBaselineAt,
                now,
                EvidenceSource.UiAutomation,
                GetUiReadyCompletionReason(),
                previous.LastRunningEvidenceAt,
                previous.LastUiEvidenceAt,
                now,
                0);
        }

        if (runningAt is { } runningSignalAt && IsRecent(runningSignalAt, CopilotRunningLeaseWindow, now))
        {
            var runningStartedAt = previous.RunningStartedAt ?? previous.LastUiEvidenceAt ?? runningSignalAt;
            var baselineAt = previous.CompletionBaselineAt ?? previous.CompletedAt;
            if (HasRecentUiAnchor(previous, CopilotRunningLeaseWindow, now))
            {
                return new EngineRuntimeState(
                    AiEngine.Copilot,
                    InternalEngineState.Running,
                    EvidenceConfidence.Probable,
                    previous.FirstSeenAt ?? runningStartedAt,
                    runningSignalAt,
                    runningStartedAt,
                    baselineAt,
                    null,
                    EvidenceSource.Log,
                    "recent ccreq after UI-confirmed run",
                    runningSignalAt,
                    previous.LastUiEvidenceAt,
                    freshNegative ? now : previous.LastUiNegativeAt,
                    0);
            }

            return new EngineRuntimeState(
                AiEngine.Copilot,
                InternalEngineState.SuspectRunning,
                EvidenceConfidence.Suspect,
                previous.FirstSeenAt ?? runningSignalAt,
                runningSignalAt,
                previous.RunningStartedAt,
                previous.CompletionBaselineAt,
                null,
                EvidenceSource.Log,
                "recent ccreq awaiting UI confirmation",
                runningSignalAt,
                previous.LastUiEvidenceAt,
                freshNegative ? now : previous.LastUiNegativeAt,
                0);
        }

        if (previous.State is InternalEngineState.Running or InternalEngineState.WaitingForConfirmation
            && HasRecentUiAnchor(previous, CopilotRunningLeaseWindow, now)
            && IsRecent(previous.LastRunningEvidenceAt, CopilotRunningLeaseWindow, now))
        {
            return previous with
            {
                LastUiNegativeAt = freshNegative ? now : previous.LastUiNegativeAt,
                ConsecutiveNegativeUiProbes = 0
            };
        }

        if (previous.State == InternalEngineState.SuspectRunning
            && IsRecent(previous.LastEvidenceAt, CopilotRunningLeaseWindow, now))
        {
            return previous with
            {
                LastUiNegativeAt = freshNegative ? now : previous.LastUiNegativeAt,
                ConsecutiveNegativeUiProbes = freshNegative ? previous.ConsecutiveNegativeUiProbes + 1 : previous.ConsecutiveNegativeUiProbes
            };
        }

        if (errorAt is { } errorSignalAt && IsRecent(errorSignalAt, ErrorSignalWindow, now))
        {
            return new EngineRuntimeState(
                AiEngine.Copilot,
                InternalEngineState.Error,
                EvidenceConfidence.Confirmed,
                previous.FirstSeenAt ?? errorSignalAt,
                errorSignalAt,
                null,
                null,
                null,
                EvidenceSource.Log,
                "recent Copilot error log",
                null,
                previous.LastUiEvidenceAt,
                previous.LastUiNegativeAt,
                0);
        }

        if (previous.State == InternalEngineState.Completed)
        {
            return previous;
        }

        return new EngineRuntimeState(
            AiEngine.Copilot,
            InternalEngineState.Idle,
            EvidenceConfidence.Confirmed,
            null,
            Max(previous.LastEvidenceAt, Max(runningAt, errorAt)),
            null,
            null,
            null,
            EvidenceSource.None,
            previous.State is InternalEngineState.Running or InternalEngineState.WaitingForConfirmation
                ? "copilot running lease expired"
                : "no Copilot evidence",
            null,
            previous.LastUiEvidenceAt,
            freshNegative ? now : previous.LastUiNegativeAt,
            0);
    }

    private EngineRuntimeState AdvanceCodexState(
        WindowSlotStatusSnapshot slot,
        string slotKey,
        EngineRuntimeState previous,
        AiLogEvidence log,
        UiAutomationProbeResult uiProbe,
        bool hasFreshUiProbe,
        UiAutomationOwner uiOwner,
        DateTimeOffset now)
    {
        var completedFloor = previous.CompletedAt;
        var runningAt = IgnoreIfNotNewerThan(Max(log.LastRunningSignalAt, log.LastActivitySignalAt), completedFloor);
        var confirmationAt = IgnoreIfNotNewerThan(log.LastConfirmationSignalAt, completedFloor);
        var errorAt = IgnoreIfNotNewerThan(log.LastErrorSignalAt, completedFloor);
        var hasUiRunning = uiOwner == UiAutomationOwner.Codex && uiProbe.Status == AiStatus.Running;
        var hasUiConfirmation = uiOwner == UiAutomationOwner.Codex && uiProbe.Status == AiStatus.WaitingForConfirmation;
        var freshNegative = IsFreshNegativeUiProbe(uiProbe, hasFreshUiProbe);
        var negativeCount = freshNegative ? previous.ConsecutiveNegativeUiProbes + 1 : previous.ConsecutiveNegativeUiProbes;

        if (hasUiConfirmation)
        {
            var evidenceAt = uiProbe.EvidenceAt ?? now;
            var runningStartedAt = previous.RunningStartedAt ?? runningAt ?? evidenceAt;
            var baselineAt = previous.CompletionBaselineAt ?? previous.CompletedAt;
            return new EngineRuntimeState(
                AiEngine.Codex,
                InternalEngineState.WaitingForConfirmation,
                EvidenceConfidence.Confirmed,
                previous.FirstSeenAt ?? runningStartedAt,
                evidenceAt,
                runningStartedAt,
                baselineAt,
                null,
                EvidenceSource.UiAutomation,
                uiProbe.Detail,
                evidenceAt,
                evidenceAt,
                null,
                0);
        }

        if (hasUiRunning)
        {
            var evidenceAt = uiProbe.EvidenceAt ?? now;
            var runningStartedAt = previous.RunningStartedAt ?? runningAt ?? evidenceAt;
            var baselineAt = previous.CompletionBaselineAt ?? previous.CompletedAt;
            return new EngineRuntimeState(
                AiEngine.Codex,
                InternalEngineState.Running,
                EvidenceConfidence.Confirmed,
                previous.FirstSeenAt ?? runningStartedAt,
                evidenceAt,
                runningStartedAt,
                baselineAt,
                null,
                EvidenceSource.UiAutomation,
                uiProbe.Detail,
                evidenceAt,
                evidenceAt,
                null,
                0);
        }

        if (previous.State == InternalEngineState.SuspectRunning
            && hasFreshUiProbe
            && !uiProbe.TimedOut
            && uiProbe.Status is null
            && !uiProbe.HasRunningEvidence
            && uiProbe.HasReadyInputEvidence)
        {
            return new EngineRuntimeState(
                AiEngine.Codex,
                InternalEngineState.Idle,
                EvidenceConfidence.Confirmed,
                null,
                previous.LastEvidenceAt,
                null,
                null,
                null,
                EvidenceSource.None,
                "UIA input-ready without confirmed Codex running",
                null,
                previous.LastUiEvidenceAt,
                now,
                0);
        }

        if (IsUiReadyAfterRunning(previous, uiProbe, hasFreshUiProbe, now))
        {
            return new EngineRuntimeState(
                AiEngine.Codex,
                InternalEngineState.Completed,
                previous.Confidence,
                previous.FirstSeenAt ?? previous.RunningStartedAt ?? now,
                now,
                previous.RunningStartedAt,
                previous.CompletionBaselineAt,
                now,
                EvidenceSource.UiAutomation,
                GetUiReadyCompletionReason(),
                previous.LastRunningEvidenceAt,
                previous.LastUiEvidenceAt,
                now,
                0);
        }

        var confirmationOwned = confirmationAt is { } confirmationSignalAt
            && IsRecent(confirmationSignalAt, CodexConfirmationWindow, now)
            && IsCodexBroadcastOwner(slot, slotKey, confirmationSignalAt, previous, now);
        if (confirmationOwned && confirmationAt is { } ownedConfirmationAt)
        {
            if (!HasRecentUiAnchor(previous, CodexConfirmationWindow, now))
            {
                return new EngineRuntimeState(
                    AiEngine.Codex,
                    InternalEngineState.SuspectRunning,
                    EvidenceConfidence.Suspect,
                    previous.FirstSeenAt ?? ownedConfirmationAt,
                    ownedConfirmationAt,
                    previous.RunningStartedAt,
                    previous.CompletionBaselineAt,
                    null,
                    EvidenceSource.Log,
                    "requestApproval awaiting UI confirmation",
                    Max(previous.LastRunningEvidenceAt, Max(runningAt, ownedConfirmationAt)),
                    previous.LastUiEvidenceAt,
                    freshNegative ? now : previous.LastUiNegativeAt,
                    negativeCount);
            }

            var runningStartedAt = previous.RunningStartedAt ?? runningAt ?? ownedConfirmationAt;
            var baselineAt = previous.CompletionBaselineAt ?? previous.CompletedAt;
            return new EngineRuntimeState(
                AiEngine.Codex,
                InternalEngineState.WaitingForConfirmation,
                EvidenceConfidence.Probable,
                previous.FirstSeenAt ?? runningStartedAt,
                ownedConfirmationAt,
                runningStartedAt,
                baselineAt,
                null,
                EvidenceSource.Log,
                "requestApproval log assigned to active slot",
                Max(previous.LastRunningEvidenceAt, Max(runningAt, ownedConfirmationAt)),
                previous.LastUiEvidenceAt,
                freshNegative ? now : previous.LastUiNegativeAt,
                0);
        }

        var activityOwned = runningAt is { } runningSignalAt
            && IsRecent(runningSignalAt, CodexProbableRunningWindow, now)
            && IsCodexBroadcastOwner(slot, slotKey, runningSignalAt, previous, now);
        if (activityOwned && runningAt is { } ownedRunningAt)
        {
            if (!HasRecentUiAnchor(previous, CodexProbableRunningWindow, now))
            {
                return new EngineRuntimeState(
                    AiEngine.Codex,
                    InternalEngineState.SuspectRunning,
                    EvidenceConfidence.Suspect,
                    previous.FirstSeenAt ?? ownedRunningAt,
                    ownedRunningAt,
                    previous.RunningStartedAt,
                    previous.CompletionBaselineAt,
                    null,
                    EvidenceSource.Log,
                    "recent stream activity awaiting UI confirmation",
                    ownedRunningAt,
                    previous.LastUiEvidenceAt,
                    freshNegative ? now : previous.LastUiNegativeAt,
                    negativeCount);
            }

            var runningStartedAt = previous.RunningStartedAt ?? ownedRunningAt;
            var baselineAt = previous.CompletionBaselineAt ?? previous.CompletedAt;
            var reason = slot.IsForeground || slot.IsFocused
                ? "foreground slot recent stream activity"
                : previous.LastUiEvidenceAt.HasValue
                    ? "recent stream activity after confirmed running"
                    : "recent owned stream activity";

            return new EngineRuntimeState(
                AiEngine.Codex,
                InternalEngineState.Running,
                EvidenceConfidence.Probable,
                previous.FirstSeenAt ?? runningStartedAt,
                ownedRunningAt,
                runningStartedAt,
                baselineAt,
                null,
                EvidenceSource.Log,
                reason,
                ownedRunningAt,
                previous.LastUiEvidenceAt,
                freshNegative ? now : previous.LastUiNegativeAt,
                0);
        }

        if (runningAt is { } broadcastRunningAt && IsRecent(broadcastRunningAt, CodexSuspectWindow, now))
        {
            if (previous.State == InternalEngineState.Completed)
            {
                return previous;
            }

            return new EngineRuntimeState(
                AiEngine.Codex,
                InternalEngineState.SuspectRunning,
                EvidenceConfidence.Suspect,
                previous.State == InternalEngineState.SuspectRunning
                    ? previous.FirstSeenAt ?? broadcastRunningAt
                    : broadcastRunningAt,
                broadcastRunningAt,
                previous.State is InternalEngineState.Running or InternalEngineState.WaitingForConfirmation
                    ? previous.RunningStartedAt
                    : null,
                previous.CompletionBaselineAt,
                null,
                EvidenceSource.BroadcastLog,
                "broadcast log ignored, no owner",
                broadcastRunningAt,
                previous.LastUiEvidenceAt,
                freshNegative ? now : previous.LastUiNegativeAt,
                negativeCount);
        }

        if (previous.State == InternalEngineState.Completed)
        {
            return previous;
        }

        if (previous.State == InternalEngineState.SuspectRunning)
        {
            if (IsRecent(previous.LastEvidenceAt, CodexSuspectWindow, now))
            {
                return previous with
                {
                    LastUiNegativeAt = freshNegative ? now : previous.LastUiNegativeAt,
                    ConsecutiveNegativeUiProbes = freshNegative ? negativeCount : previous.ConsecutiveNegativeUiProbes
                };
            }

            return new EngineRuntimeState(
                AiEngine.Codex,
                InternalEngineState.Idle,
                EvidenceConfidence.Confirmed,
                null,
                previous.LastEvidenceAt,
                null,
                null,
                null,
                EvidenceSource.None,
                "broadcast suspect expired",
                null,
                previous.LastUiEvidenceAt,
                freshNegative ? now : previous.LastUiNegativeAt,
                0);
        }

        if (previous.State == InternalEngineState.WaitingForConfirmation)
        {
            if (freshNegative && !IsRecent(previous.LastRunningEvidenceAt, CodexProbableRunningWindow, now))
            {
                return new EngineRuntimeState(
                    AiEngine.Codex,
                    InternalEngineState.Idle,
                    EvidenceConfidence.Confirmed,
                    null,
                    previous.LastEvidenceAt,
                    null,
                    null,
                    null,
                    EvidenceSource.None,
                    "codex confirmation no longer visible",
                    null,
                    previous.LastUiEvidenceAt,
                    now,
                    0);
            }

            return previous with
            {
                LastUiNegativeAt = freshNegative ? now : previous.LastUiNegativeAt,
                ConsecutiveNegativeUiProbes = freshNegative ? negativeCount : previous.ConsecutiveNegativeUiProbes
            };
        }

        if (previous.State == InternalEngineState.Running
            && previous.Confidence is EvidenceConfidence.Confirmed or EvidenceConfidence.Probable
            && previous.LastRunningEvidenceAt is { } lastRunningEvidenceAt)
        {
            var quietFor = now - lastRunningEvidenceAt;
            if (quietFor >= CodexQuietCompletionWindow)
            {
                if (freshNegative)
                {
                    return previous with
                    {
                        LastReason = uiProbe.HasReadyInputEvidence
                            ? "input ready observed; waiting minimum completion delay"
                            : "awaiting input-ready completion proof",
                        LastUiNegativeAt = now,
                        ConsecutiveNegativeUiProbes = negativeCount
                    };
                }

                return previous with
                {
                    LastReason = "awaiting input-ready completion proof",
                    LastUiNegativeAt = freshNegative ? now : previous.LastUiNegativeAt,
                    ConsecutiveNegativeUiProbes = freshNegative ? negativeCount : previous.ConsecutiveNegativeUiProbes
                };
            }

            return previous with
            {
                LastUiNegativeAt = freshNegative ? now : previous.LastUiNegativeAt,
                ConsecutiveNegativeUiProbes = freshNegative ? negativeCount : previous.ConsecutiveNegativeUiProbes
            };
        }

        if (errorAt is { } errorSignalAt && IsRecent(errorSignalAt, ErrorSignalWindow, now))
        {
            return new EngineRuntimeState(
                AiEngine.Codex,
                InternalEngineState.Error,
                EvidenceConfidence.Confirmed,
                previous.FirstSeenAt ?? errorSignalAt,
                errorSignalAt,
                null,
                null,
                null,
                EvidenceSource.Log,
                "recent Codex error log",
                null,
                previous.LastUiEvidenceAt,
                previous.LastUiNegativeAt,
                0);
        }

        return new EngineRuntimeState(
            AiEngine.Codex,
            InternalEngineState.Idle,
            EvidenceConfidence.Confirmed,
            null,
            Max(previous.LastEvidenceAt, Max(runningAt, Max(confirmationAt, errorAt))),
            null,
            null,
            null,
            EvidenceSource.None,
            previous.State == InternalEngineState.Running
                ? "codex activity quiet without completion evidence"
                : "no Codex evidence",
            null,
            previous.LastUiEvidenceAt,
            freshNegative ? now : previous.LastUiNegativeAt,
            0);
    }

    private static SlotRuntimeState AdvanceSlotState(
        SlotRuntimeState previous,
        UiAutomationProbeResult uiProbe,
        bool hasFreshUiProbe,
        UiAutomationOwner uiOwner,
        EngineRuntimeState copilot,
        EngineRuntimeState codex,
        AiLogEvidence copilotLog,
        AiLogEvidence codexLog,
        DateTimeOffset now)
    {
        var evidenceAt = uiProbe.EvidenceAt ?? now;
        var hasDirectWaiting = HasDirectUiWaiting(uiProbe);
        var hasDirectRunning = HasDirectUiRunning(uiProbe);
        var hasUiReady = hasFreshUiProbe
            && !uiProbe.TimedOut
            && uiProbe.Status is null
            && !uiProbe.HasRunningEvidence
            && uiProbe.HasReadyInputEvidence;
        var freshNegative = IsFreshNegativeUiProbe(uiProbe, hasFreshUiProbe);
        var negativeCount = freshNegative ? previous.ConsecutiveNegativeUiProbes + 1 : 0;

        if (hasDirectWaiting)
        {
            var owner = MapSlotOwner(uiOwner, previous.Owner);
            return new SlotRuntimeState(
                SlotRuntimeStatus.WaitingForConfirmation,
                SlotRuntimeSource.UiAutomationDirect,
                owner == SlotRuntimeOwner.None ? SlotRuntimeOwner.UnknownAi : owner,
                previous.FirstSeenAt ?? evidenceAt,
                evidenceAt,
                previous.LastUiReadyAt,
                evidenceAt,
                GetSlotEvidenceText(uiProbe),
                BuildSlotUiReason("confirmation visible", owner, uiProbe),
                true,
                0);
        }

        if (hasDirectRunning)
        {
            var owner = MapSlotOwner(uiOwner, previous.Owner);
            return new SlotRuntimeState(
                SlotRuntimeStatus.Running,
                SlotRuntimeSource.UiAutomationDirect,
                owner == SlotRuntimeOwner.None ? SlotRuntimeOwner.UnknownAi : owner,
                previous.FirstSeenAt ?? evidenceAt,
                evidenceAt,
                previous.LastUiReadyAt,
                evidenceAt,
                GetSlotEvidenceText(uiProbe),
                BuildSlotUiReason("running visible", owner, uiProbe),
                true,
                0);
        }

        if (previous.HasObservedUiRunning
            && previous.LastUiRunningAt is { } lastUiRunningAt
            && hasUiReady
            && now - lastUiRunningAt >= UiReadyCompletionDelay)
        {
            var owner = previous.Owner is SlotRuntimeOwner.Copilot or SlotRuntimeOwner.Codex
                ? previous.Owner
                : SlotRuntimeOwner.UnknownAi;
            return previous with
            {
                State = SlotRuntimeStatus.Completed,
                Source = SlotRuntimeSource.UiAutomationDirect,
                Owner = owner,
                LastUiReadyAt = evidenceAt,
                LastEvidenceAt = evidenceAt,
                EvidenceText = GetSlotEvidenceText(uiProbe),
                Reason = GetUiReadyCompletionReason(),
                ConsecutiveNegativeUiProbes = 0
            };
        }

        if (previous.State == SlotRuntimeStatus.Completed)
        {
            return hasUiReady
                ? previous with
                {
                    LastUiReadyAt = evidenceAt,
                    LastEvidenceAt = evidenceAt,
                    EvidenceText = GetSlotEvidenceText(uiProbe)
                }
                : previous;
        }

        var engineAggregate = GetSlotEngineAggregateCandidate(copilot, codex);
        if (previous.State is SlotRuntimeStatus.Running or SlotRuntimeStatus.WaitingForConfirmation)
        {
            if (hasUiReady
                && previous.LastUiRunningAt is { } runningAt
                && now - runningAt < UiReadyCompletionDelay)
            {
                return previous with
                {
                    Source = SlotRuntimeSource.UiAutomationDirect,
                    LastUiReadyAt = evidenceAt,
                    LastEvidenceAt = evidenceAt,
                    EvidenceText = GetSlotEvidenceText(uiProbe),
                    Reason = "input ready observed; waiting minimum completion delay",
                    ConsecutiveNegativeUiProbes = 0
                };
            }

            if (previous.LastUiRunningAt is { } previousUiRunningAt
                && now - previousUiRunningAt <= SlotRunningHoldWindow
                && (uiProbe.TimedOut || !hasFreshUiProbe || !uiProbe.ScanCompleted || negativeCount <= NegativeUiProbeGraceCount))
            {
                var source = engineAggregate is not null
                    ? SlotRuntimeSource.EngineAggregate
                    : HasFreshAssistLog(copilotLog, codexLog, now)
                        ? SlotRuntimeSource.LogAssist
                        : previous.Source;
                var reason = uiProbe.TimedOut
                    ? "UI Automation timed out; keeping last direct running observation"
                    : !hasFreshUiProbe || !uiProbe.ScanCompleted
                        ? "UI Automation pending; keeping last direct running observation"
                        : negativeCount <= NegativeUiProbeGraceCount
                            ? "single negative probe ignored after direct running"
                            : previous.Reason;

                return previous with
                {
                    Source = source,
                    LastUiReadyAt = hasUiReady ? evidenceAt : previous.LastUiReadyAt,
                    LastEvidenceAt = Max(previous.LastEvidenceAt, Max(engineAggregate?.EventAt, uiProbe.EvidenceAt)),
                    Reason = reason,
                    ConsecutiveNegativeUiProbes = negativeCount
                };
            }

            // Active run latch: once UIA direct Running was observed, never fall to Idle/SuspectRunning
            // without explicit input-ready proof (handled by the Completed path above).
            if (previous.HasObservedUiRunning)
            {
                var latchSource = engineAggregate is not null
                    ? SlotRuntimeSource.EngineAggregate
                    : HasFreshAssistLog(copilotLog, codexLog, now)
                        ? SlotRuntimeSource.LogAssist
                        : previous.Source;
                var latchReason = !hasFreshUiProbe
                    ? "active run latch; no UIA probe this refresh; keep active run"
                    : uiProbe.TimedOut
                        ? "active run latch; UIA timeout; keep previous state"
                        : negativeCount <= NegativeUiProbeGraceCount
                            ? $"active run latch; {negativeCount} negative probe(s); no ready proof yet"
                            : $"active run latch; {negativeCount} consecutive negatives; no ready proof yet";
                return previous with
                {
                    Source = latchSource,
                    LastUiReadyAt = hasUiReady ? evidenceAt : previous.LastUiReadyAt,
                    LastEvidenceAt = Max(previous.LastEvidenceAt, Max(engineAggregate?.EventAt, uiProbe.EvidenceAt)),
                    Reason = latchReason,
                    ConsecutiveNegativeUiProbes = negativeCount
                };
            }
        }

        if (engineAggregate is not null)
        {
            return new SlotRuntimeState(
                engineAggregate.State,
                SlotRuntimeSource.EngineAggregate,
                engineAggregate.Owner,
                previous.FirstSeenAt ?? engineAggregate.EventAt ?? now,
                previous.LastUiRunningAt,
                previous.LastUiReadyAt,
                engineAggregate.EventAt ?? previous.LastEvidenceAt,
                previous.EvidenceText,
                engineAggregate.Reason,
                previous.HasObservedUiRunning,
                negativeCount);
        }

        if (HasFreshAssistLog(copilotLog, codexLog, now))
        {
            return new SlotRuntimeState(
                SlotRuntimeStatus.SuspectRunning,
                SlotRuntimeSource.LogAssist,
                ResolveSlotLogOwner(previous, copilotLog, codexLog, now),
                previous.FirstSeenAt ?? Max(copilotLog.LastRunningSignalAt, Max(codexLog.LastRunningSignalAt, codexLog.LastConfirmationSignalAt)) ?? now,
                previous.LastUiRunningAt,
                previous.LastUiReadyAt,
                Max(copilotLog.LastRunningSignalAt, Max(codexLog.LastRunningSignalAt, codexLog.LastConfirmationSignalAt)) ?? previous.LastEvidenceAt,
                previous.EvidenceText,
                "log activity awaiting UI confirmation",
                previous.HasObservedUiRunning,
                negativeCount);
        }

        if (previous.State == SlotRuntimeStatus.SuspectRunning
            && IsRecent(previous.LastEvidenceAt, TimeSpan.FromSeconds(15), now))
        {
            return previous with { ConsecutiveNegativeUiProbes = negativeCount };
        }

        if (hasUiReady)
        {
            return new SlotRuntimeState(
                SlotRuntimeStatus.Idle,
                SlotRuntimeSource.None,
                SlotRuntimeOwner.None,
                null,
                previous.LastUiRunningAt,
                evidenceAt,
                evidenceAt,
                GetSlotEvidenceText(uiProbe),
                "input ready visible without running evidence",
                previous.HasObservedUiRunning,
                0);
        }

        return new SlotRuntimeState(
            SlotRuntimeStatus.Idle,
            SlotRuntimeSource.None,
            SlotRuntimeOwner.None,
            null,
            previous.LastUiRunningAt,
            previous.LastUiReadyAt,
            Max(previous.LastEvidenceAt, Max(copilotLog.LastRunningSignalAt, Max(codexLog.LastRunningSignalAt, codexLog.LastConfirmationSignalAt))),
            previous.EvidenceText,
            previous.HasObservedUiRunning
                ? "running hold expired without fresh UI evidence"
                : "no slot-level evidence",
            previous.HasObservedUiRunning,
            0);
    }

    private static DisplayDecision AggregateDecision(bool canReadLogs, SlotRuntimeState slotState, EngineRuntimeState copilot, EngineRuntimeState codex)
    {
        var candidates = new[] { CreateSlotDisplayCandidate(slotState), CreateDisplayCandidate(copilot), CreateDisplayCandidate(codex) }
            .Where(candidate => candidate is not null)
            .Cast<DisplayCandidate>()
            .OrderByDescending(candidate => candidate.Priority)
            .ThenByDescending(candidate => candidate.ConfidencePriority)
            .ThenByDescending(candidate => candidate.EventAt ?? DateTimeOffset.MinValue)
            .ToList();

        if (candidates.Count > 0)
        {
            var selected = candidates[0];
            return new DisplayDecision(
                selected.Status,
                selected.Detail,
                selected.EventAt,
                selected.SourceName,
                selected.EngineName,
                selected.Confidence,
                selected.Reason);
        }

        var latestCause = new[]
            {
                new IdleCauseCandidate(slotState.LastEvidenceAt, slotState.Owner == SlotRuntimeOwner.None ? string.Empty : slotState.Owner.ToString(), GetSlotConfidence(slotState), slotState.Reason),
                new IdleCauseCandidate(copilot.LastEvidenceAt, copilot.Engine.ToString(), copilot.Confidence.ToString(), copilot.LastReason),
                new IdleCauseCandidate(codex.LastEvidenceAt, codex.Engine.ToString(), codex.Confidence.ToString(), codex.LastReason)
            }
            .OrderByDescending(state => state.EventAt ?? DateTimeOffset.MinValue)
            .First();
        var hasMeaningfulCause = latestCause.EventAt.HasValue;

        var idleReason = hasMeaningfulCause && !string.IsNullOrWhiteSpace(latestCause.Reason)
            ? latestCause.Reason
            : canReadLogs
                ? "no active evidence"
                : "user-data-dir missing";
        var idleDetail = canReadLogs
            ? !hasMeaningfulCause || string.IsNullOrWhiteSpace(latestCause.Reason)
                ? "AI は待機中です。"
                : $"AI は待機中です。{latestCause.Owner}: {latestCause.Reason}"
            : "VS Code の user-data-dir が見つかりません。AI は待機中として扱います。";

        return new DisplayDecision(
            AiStatus.Idle,
            idleDetail,
            latestCause.EventAt,
            string.Empty,
            hasMeaningfulCause ? latestCause.Owner : string.Empty,
            hasMeaningfulCause ? latestCause.Confidence : string.Empty,
            idleReason);
    }

    private static DisplayCandidate? CreateSlotDisplayCandidate(SlotRuntimeState state)
    {
        return state.State switch
        {
            SlotRuntimeStatus.WaitingForConfirmation => new DisplayCandidate(
                AiStatus.WaitingForConfirmation,
                BuildSlotDisplayDetail(state, AiStatus.WaitingForConfirmation),
                state.LastEvidenceAt,
                state.Source.ToString(),
                state.Owner == SlotRuntimeOwner.None ? string.Empty : state.Owner.ToString(),
                GetSlotConfidence(state),
                state.Reason,
                7,
                GetSlotConfidencePriority(state)),
            SlotRuntimeStatus.Running => new DisplayCandidate(
                AiStatus.Running,
                BuildSlotDisplayDetail(state, AiStatus.Running),
                state.LastEvidenceAt,
                state.Source.ToString(),
                state.Owner == SlotRuntimeOwner.None ? string.Empty : state.Owner.ToString(),
                GetSlotConfidence(state),
                state.Reason,
                6,
                GetSlotConfidencePriority(state)),
            SlotRuntimeStatus.Completed => new DisplayCandidate(
                AiStatus.Completed,
                BuildSlotDisplayDetail(state, AiStatus.Completed),
                state.LastEvidenceAt,
                state.Source.ToString(),
                state.Owner == SlotRuntimeOwner.None ? string.Empty : state.Owner.ToString(),
                GetSlotConfidence(state),
                state.Reason,
                2,
                GetSlotConfidencePriority(state)),
            _ => null
        };
    }

    private static DisplayCandidate? CreateDisplayCandidate(EngineRuntimeState state)
    {
        if (!IsEngineDisplayEligible(state))
        {
            return null;
        }

        return state.State switch
        {
            InternalEngineState.WaitingForConfirmation => new DisplayCandidate(
                AiStatus.WaitingForConfirmation,
                $"{state.Engine}: AI は確認待ちです。{state.LastReason}",
                state.LastEvidenceAt,
                SlotRuntimeSource.EngineAggregate.ToString(),
                state.Engine.ToString(),
                state.Confidence.ToString(),
                state.LastReason,
                5,
                GetConfidencePriority(state.Confidence)),
            InternalEngineState.Running => new DisplayCandidate(
                AiStatus.Running,
                $"{state.Engine}: AI は実行中です。{state.LastReason}",
                state.LastEvidenceAt,
                SlotRuntimeSource.EngineAggregate.ToString(),
                state.Engine.ToString(),
                state.Confidence.ToString(),
                state.LastReason,
                4,
                GetConfidencePriority(state.Confidence)),
            InternalEngineState.Error => new DisplayCandidate(
                AiStatus.Error,
                $"{state.Engine}: エラーを検出しました。{state.LastReason}",
                state.LastEvidenceAt,
                SlotRuntimeSource.EngineAggregate.ToString(),
                state.Engine.ToString(),
                state.Confidence.ToString(),
                state.LastReason,
                3,
                GetConfidencePriority(state.Confidence)),
            InternalEngineState.Completed => new DisplayCandidate(
                AiStatus.Completed,
                $"{state.Engine}: AI 実行は完了しました。{state.LastReason}",
                state.CompletedAt ?? state.LastEvidenceAt,
                SlotRuntimeSource.EngineAggregate.ToString(),
                state.Engine.ToString(),
                state.Confidence.ToString(),
                state.LastReason,
                1,
                GetConfidencePriority(state.Confidence)),
            _ => null
        };
    }

    private static bool IsEngineDisplayEligible(EngineRuntimeState state)
    {
        return state.State switch
        {
            InternalEngineState.WaitingForConfirmation or InternalEngineState.Running =>
                state.LastEvidenceSource == EvidenceSource.UiAutomation || state.LastUiEvidenceAt.HasValue,
            InternalEngineState.Completed =>
                state.LastEvidenceSource == EvidenceSource.UiAutomation
                || string.Equals(state.LastReason, GetUiReadyCompletionReason(), StringComparison.Ordinal),
            _ => true
        };
    }

    private static bool HasRecentUiAnchor(EngineRuntimeState state, TimeSpan window, DateTimeOffset now)
    {
        return state.LastUiEvidenceAt is { } lastUiEvidenceAt && IsRecent(lastUiEvidenceAt, window, now);
    }

    private static SlotEngineAggregateCandidate? GetSlotEngineAggregateCandidate(EngineRuntimeState copilot, EngineRuntimeState codex)
    {
        var candidates = new[]
            {
                CreateSlotEngineAggregateCandidate(copilot),
                CreateSlotEngineAggregateCandidate(codex)
            }
            .Where(candidate => candidate is not null)
            .Cast<SlotEngineAggregateCandidate>()
            .OrderByDescending(candidate => candidate.Priority)
            .ThenByDescending(candidate => candidate.EventAt ?? DateTimeOffset.MinValue)
            .ToList();

        return candidates.Count > 0 ? candidates[0] : null;
    }

    private static SlotEngineAggregateCandidate? CreateSlotEngineAggregateCandidate(EngineRuntimeState state)
    {
        if (!IsEngineDisplayEligible(state))
        {
            return null;
        }

        return state.State switch
        {
            InternalEngineState.WaitingForConfirmation => new SlotEngineAggregateCandidate(SlotRuntimeStatus.WaitingForConfirmation, state.Engine == AiEngine.Copilot ? SlotRuntimeOwner.Copilot : SlotRuntimeOwner.Codex, state.LastEvidenceAt, state.LastReason, 2),
            InternalEngineState.Running => new SlotEngineAggregateCandidate(SlotRuntimeStatus.Running, state.Engine == AiEngine.Copilot ? SlotRuntimeOwner.Copilot : SlotRuntimeOwner.Codex, state.LastEvidenceAt, state.LastReason, 1),
            _ => null
        };
    }

    private static SlotRuntimeOwner ResolveSlotLogOwner(SlotRuntimeState previous, AiLogEvidence copilotLog, AiLogEvidence codexLog, DateTimeOffset now)
    {
        if (previous.Owner is SlotRuntimeOwner.Copilot or SlotRuntimeOwner.Codex)
        {
            return previous.Owner;
        }

        var hasCopilot = IsRecent(copilotLog.LastRunningSignalAt, CopilotRunningLeaseWindow, now);
        var hasCodex = IsRecent(codexLog.LastRunningSignalAt, CodexProbableRunningWindow, now)
            || IsRecent(codexLog.LastConfirmationSignalAt, CodexConfirmationWindow, now);

        if (hasCopilot && !hasCodex)
        {
            return SlotRuntimeOwner.Copilot;
        }

        if (hasCodex && !hasCopilot)
        {
            return SlotRuntimeOwner.Codex;
        }

        return SlotRuntimeOwner.UnknownAi;
    }

    private static bool HasFreshAssistLog(AiLogEvidence copilotLog, AiLogEvidence codexLog, DateTimeOffset now)
    {
        return IsRecent(copilotLog.LastRunningSignalAt, CopilotRunningLeaseWindow, now)
            || IsRecent(codexLog.LastRunningSignalAt, CodexProbableRunningWindow, now)
            || IsRecent(codexLog.LastConfirmationSignalAt, CodexConfirmationWindow, now);
    }

    private static SlotRuntimeOwner MapSlotOwner(UiAutomationOwner owner, SlotRuntimeOwner fallback)
    {
        return owner switch
        {
            UiAutomationOwner.Copilot => SlotRuntimeOwner.Copilot,
            UiAutomationOwner.Codex => SlotRuntimeOwner.Codex,
            UiAutomationOwner.UnknownAi => SlotRuntimeOwner.UnknownAi,
            _ => fallback
        };
    }

    private static string BuildSlotUiReason(string reason, SlotRuntimeOwner owner, UiAutomationProbeResult uiProbe)
    {
        var ownerLabel = owner switch
        {
            SlotRuntimeOwner.Copilot => "owner=Copilot",
            SlotRuntimeOwner.Codex => "owner=Codex",
            _ => "owner=UnknownAi"
        };
        var evidence = GetSlotEvidenceText(uiProbe);
        return string.IsNullOrWhiteSpace(evidence)
            ? $"UI Automation {reason}; {ownerLabel}"
            : $"UI Automation {reason}; {ownerLabel}; evidence={evidence}";
    }

    private static string GetSlotEvidenceText(UiAutomationProbeResult uiProbe)
    {
        return !string.IsNullOrWhiteSpace(uiProbe.EvidenceText)
            ? uiProbe.EvidenceText
            : uiProbe.Detail;
    }

    private static string BuildSlotDisplayDetail(SlotRuntimeState state, AiStatus status)
    {
        var subject = state.Owner switch
        {
            SlotRuntimeOwner.Copilot => "Copilot",
            SlotRuntimeOwner.Codex => "Codex",
            _ => "AI"
        };
        var ownerText = state.Owner == SlotRuntimeOwner.UnknownAi ? " owner=UnknownAi" : string.Empty;
        var statusText = status switch
        {
            AiStatus.WaitingForConfirmation => "確認待ちです。",
            AiStatus.Running => "実行中です。",
            AiStatus.Completed => "実行は完了しました。",
            _ => "待機中です。"
        };
        return string.IsNullOrWhiteSpace(state.Reason)
            ? $"{subject}: AI は{statusText}{ownerText}"
            : $"{subject}: AI は{statusText}{ownerText} {state.Reason}";
    }

    private static string GetSlotConfidence(SlotRuntimeState state)
    {
        return state.Source switch
        {
            SlotRuntimeSource.UiAutomationDirect => EvidenceConfidence.Confirmed.ToString(),
            SlotRuntimeSource.EngineAggregate => EvidenceConfidence.Probable.ToString(),
            SlotRuntimeSource.LogAssist => EvidenceConfidence.Suspect.ToString(),
            _ => string.Empty
        };
    }

    private static int GetSlotConfidencePriority(SlotRuntimeState state)
    {
        return state.Source switch
        {
            SlotRuntimeSource.UiAutomationDirect => GetConfidencePriority(EvidenceConfidence.Confirmed),
            SlotRuntimeSource.EngineAggregate => GetConfidencePriority(EvidenceConfidence.Probable),
            SlotRuntimeSource.LogAssist => GetConfidencePriority(EvidenceConfidence.Suspect),
            _ => 0
        };
    }

    private void MaybeLogDecision(string slotKey, string slotName, AiStatus status, AiStatusDiagnostics diagnostics)
    {
        var signature = $"{status}|{diagnostics.FinalSource}|{diagnostics.FinalEngine}|{diagnostics.FinalConfidence}|{diagnostics.FinalReason}|{diagnostics.SlotLevel.State}|{diagnostics.SlotLevel.Owner}";
        var now = DateTimeOffset.Now;
        var changed = !_lastDecisionSignatureBySlot.TryGetValue(slotKey, out var previousSignature)
            || !string.Equals(previousSignature, signature, StringComparison.Ordinal);
        var intervalElapsed = !_lastDecisionLoggedAtBySlot.TryGetValue(slotKey, out var previousLoggedAt)
            || now - previousLoggedAt >= DecisionLogInterval;

        if (!changed && !intervalElapsed)
        {
            return;
        }

        _lastDecisionSignatureBySlot[slotKey] = signature;
        _lastDecisionLoggedAtBySlot[slotKey] = now;

        var sourcePart = string.IsNullOrWhiteSpace(diagnostics.FinalSource)
            ? string.Empty
            : $" source={diagnostics.FinalSource}";
        var enginePart = string.IsNullOrWhiteSpace(diagnostics.FinalEngine)
            ? string.Empty
            : $" engine={diagnostics.FinalEngine}";
        var confidencePart = string.IsNullOrWhiteSpace(diagnostics.FinalConfidence)
            ? string.Empty
            : $" confidence={diagnostics.FinalConfidence}";
        var slotStatePart = string.IsNullOrWhiteSpace(diagnostics.SlotLevel.State)
            ? string.Empty
            : $" slotLevelState={diagnostics.SlotLevel.State}";
        var slotOwnerPart = string.IsNullOrWhiteSpace(diagnostics.SlotLevel.Owner)
            ? string.Empty
            : $" slotLevelOwner={diagnostics.SlotLevel.Owner}";
        var reason = string.IsNullOrWhiteSpace(diagnostics.FinalReason)
            ? "n/a"
            : diagnostics.FinalReason.Replace("\r", " ").Replace("\n", " ");
        var evidenceTextPart = string.IsNullOrWhiteSpace(diagnostics.UiProbe.EvidenceText)
            ? string.Empty
            : $" evidenceText={diagnostics.UiProbe.EvidenceText.Replace("\r", " ").Replace("\n", " ")}";
        var evidenceClassPart = string.IsNullOrWhiteSpace(diagnostics.UiProbe.EvidenceClassName)
            ? string.Empty
            : $" evidenceClassName={diagnostics.UiProbe.EvidenceClassName.Replace("\r", " ").Replace("\n", " ")}";
        var evidenceAutomationIdPart = string.IsNullOrWhiteSpace(diagnostics.UiProbe.EvidenceAutomationId)
            ? string.Empty
            : $" evidenceAutomationId={diagnostics.UiProbe.EvidenceAutomationId.Replace("\r", " ").Replace("\n", " ")}";

        var slotLatchPart = diagnostics.SlotLevel.HasObservedUiRunning
            ? $" hasObservedUiRunning=true consecutiveNeg={diagnostics.SlotLevel.ConsecutiveNegativeUiProbes}"
            : string.Empty;

        DiagnosticLog.Write(
            $"AI {slotName} final={status}{sourcePart}{enginePart}{confidencePart}{slotStatePart}{slotOwnerPart}{slotLatchPart} reason={reason}{evidenceTextPart}{evidenceClassPart}{evidenceAutomationIdPart} runningText={diagnostics.UiProbe.FoundRunningText} runningClass={diagnostics.UiProbe.FoundRunningClass} stopButton={diagnostics.UiProbe.FoundStopButton} inputReady={diagnostics.UiProbe.FoundInputReady} sendButton={diagnostics.UiProbe.FoundSendButton} timedOut={diagnostics.UiProbe.TimedOut} scanCompleted={diagnostics.UiProbe.ScanCompleted}");
    }

    private bool IsCodexBroadcastOwner(
        WindowSlotStatusSnapshot slot,
        string slotKey,
        DateTimeOffset activityAt,
        EngineRuntimeState previous,
        DateTimeOffset now)
    {
        if (!IsRecent(activityAt, CodexProbableRunningWindow, now))
        {
            ExpireCodexBroadcastOwner(now);
            return false;
        }

        lock (_codexBroadcastOwnerLock)
        {
            var ownerIsFresh = _codexBroadcastOwnerLastActivityAt is { } ownerLastActivityAt
                && now - ownerLastActivityAt <= CodexBroadcastOwnerStickyWindow;

            if (!ownerIsFresh)
            {
                _codexBroadcastOwnerSlotKey = null;
                _codexBroadcastOwnerLastActivityAt = null;
            }

            if (ownerIsFresh && string.Equals(_codexBroadcastOwnerSlotKey, slotKey, StringComparison.OrdinalIgnoreCase))
            {
                _codexBroadcastOwnerLastActivityAt = Max(_codexBroadcastOwnerLastActivityAt, activityAt);
                return true;
            }

            if (ownerIsFresh)
            {
                return false;
            }

            var canClaimOwner = slot.IsForeground
                || slot.IsFocused
                || (previous.LastUiEvidenceAt is { } lastUiEvidenceAt && now - lastUiEvidenceAt <= CodexBroadcastOwnerStickyWindow)
                || (previous.State is InternalEngineState.Running or InternalEngineState.WaitingForConfirmation
                    && previous.Confidence is EvidenceConfidence.Confirmed or EvidenceConfidence.Probable
                    && previous.LastRunningEvidenceAt is { } lastRunningEvidenceAt
                    && now - lastRunningEvidenceAt <= CodexBroadcastOwnerStickyWindow);

            if (!canClaimOwner)
            {
                return false;
            }

            _codexBroadcastOwnerSlotKey = slotKey;
            _codexBroadcastOwnerLastActivityAt = activityAt;
            return true;
        }
    }

    private void ExpireCodexBroadcastOwner(DateTimeOffset now)
    {
        lock (_codexBroadcastOwnerLock)
        {
            if (_codexBroadcastOwnerLastActivityAt is { } ownerLastActivityAt
                && now - ownerLastActivityAt <= CodexBroadcastOwnerStickyWindow)
            {
                return;
            }

            _codexBroadcastOwnerSlotKey = null;
            _codexBroadcastOwnerLastActivityAt = null;
        }
    }

    private AiLogEvidence FilterEvidence(string slotKey, DateTimeOffset slotStartedAt, AiLogEvidence evidence)
    {
        return evidence with
        {
            LastEventAt = FilterTimestamp(slotKey, slotStartedAt, evidence.LastEventAt),
            LastRunningSignalAt = FilterTimestamp(slotKey, slotStartedAt, evidence.LastRunningSignalAt),
            LastCompletionSignalAt = FilterTimestamp(slotKey, slotStartedAt, evidence.LastCompletionSignalAt),
            LastStandaloneCompletionSignalAt = FilterTimestamp(slotKey, slotStartedAt, evidence.LastStandaloneCompletionSignalAt),
            LastErrorSignalAt = FilterTimestamp(slotKey, slotStartedAt, evidence.LastErrorSignalAt),
            LastIdleSignalAt = FilterTimestamp(slotKey, slotStartedAt, evidence.LastIdleSignalAt),
            LastSecondaryRunningSignalAt = FilterTimestamp(slotKey, slotStartedAt, evidence.LastSecondaryRunningSignalAt),
            LastActivitySignalAt = FilterTimestamp(slotKey, slotStartedAt, evidence.LastActivitySignalAt),
            LastConfirmationSignalAt = FilterTimestamp(slotKey, slotStartedAt, evidence.LastConfirmationSignalAt)
        };
    }

    private DateTimeOffset? FilterTimestamp(string slotKey, DateTimeOffset slotStartedAt, DateTimeOffset? eventAt)
    {
        if (!eventAt.HasValue)
        {
            return null;
        }

        if (eventAt.Value < _startedAt || eventAt.Value < slotStartedAt || eventAt.Value > DateTimeOffset.Now + FutureLogTolerance)
        {
            return null;
        }

        if (_dismissedAtBySlot.TryGetValue(slotKey, out var dismissedAt) && eventAt.Value <= dismissedAt)
        {
            return null;
        }

        return eventAt;
    }

    private static bool IsCompletionForObservedRun(DateTimeOffset completionAt, DateTimeOffset? baselineAt, DateTimeOffset? runningStartedAt)
    {
        if (completionAt > DateTimeOffset.Now + FutureLogTolerance)
        {
            return false;
        }

        if (baselineAt.HasValue && completionAt <= baselineAt.Value)
        {
            return false;
        }

        return runningStartedAt.HasValue && completionAt >= runningStartedAt.Value - CompletionLogClockSkewTolerance;
    }

    private static DateTimeOffset? IgnoreIfNotNewerThan(DateTimeOffset? candidate, DateTimeOffset? floor)
    {
        if (!candidate.HasValue)
        {
            return null;
        }

        return floor.HasValue && candidate.Value <= floor.Value ? null : candidate;
    }

    private static int GetConfidencePriority(EvidenceConfidence confidence)
    {
        return confidence switch
        {
            EvidenceConfidence.Confirmed => 3,
            EvidenceConfidence.Probable => 2,
            EvidenceConfidence.Suspect => 1,
            _ => 0
        };
    }

    private static bool IsRecent(DateTimeOffset? eventAt, TimeSpan window, DateTimeOffset now)
    {
        return eventAt.HasValue && IsRecent(eventAt.Value, window, now);
    }

    private static bool IsRecent(DateTimeOffset eventAt, TimeSpan window, DateTimeOffset now)
    {
        return eventAt <= now + FutureLogTolerance && now - eventAt <= window;
    }

    private static DateTimeOffset? Max(DateTimeOffset? current, DateTimeOffset? candidate)
    {
        if (!current.HasValue)
        {
            return candidate;
        }

        if (!candidate.HasValue)
        {
            return current;
        }

        return candidate > current ? candidate : current;
    }

    private static DateTimeOffset? Max(DateTimeOffset? current, DateTimeOffset candidate)
    {
        return !current.HasValue || candidate > current.Value ? candidate : current;
    }

    private static string GetSlotKey(WindowSlot slot)
    {
        return $"{slot.Name}:{slot.WindowHandle.ToInt64()}";
    }

    private static string GetSlotKey(WindowSlotStatusSnapshot slot)
    {
        return $"{slot.Name}:{slot.WindowHandle.ToInt64()}";
    }

    private DateTimeOffset GetSlotStartedAt(WindowSlotStatusSnapshot slot)
    {
        return _slotStartedAtByName.GetOrAdd(slot.Name, _startedAt);
    }

    private void ClearSlotState(WindowSlot slot)
    {
        ClearSlotState(slot.Name);
    }

    private void ClearSlotState(WindowSlotStatusSnapshot slot)
    {
        ClearSlotState(slot.Name);
    }

    private void ClearSlotState(string slotName)
    {
        RemovePrefixedEntries(_engineStates, slotName);
        RemovePrefixedEntries(_slotStates, slotName);
        RemovePrefixedEntries(_dismissedAtBySlot, slotName);
        RemovePrefixedEntries(_lastUiProbeBySlot, slotName);
        RemovePrefixedEntries(_lastDecisionSignatureBySlot, slotName);
        RemovePrefixedEntries(_lastDecisionLoggedAtBySlot, slotName);
        // legacy flow state もクリア
        RemovePrefixedEntries(_lastRunningSeenBySlot, slotName);
        RemovePrefixedEntries(_completedAtBySlot, slotName);
        RemovePrefixedEntries(_confirmationRequestedAtBySlot, slotName);

        lock (_codexBroadcastOwnerLock)
        {
            if (_codexBroadcastOwnerSlotKey?.StartsWith($"{slotName}:", StringComparison.OrdinalIgnoreCase) == true)
            {
                _codexBroadcastOwnerSlotKey = null;
                _codexBroadcastOwnerLastActivityAt = null;
            }
        }
    }

    private static void RemovePrefixedEntries<TValue>(ConcurrentDictionary<string, TValue> dict, string slotName)
    {
        var prefix = $"{slotName}:";
        foreach (var key in dict.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            dict.TryRemove(key, out _);
        }
    }

    private static void SwapPrefixedEntries<TValue>(ConcurrentDictionary<string, TValue> dict, string slotNameA, string slotNameB)
    {
        if (string.Equals(slotNameA, slotNameB, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var prefixA = $"{slotNameA}:";
        var prefixB = $"{slotNameB}:";

        var entriesA = dict.Where(kv => kv.Key.StartsWith(prefixA, StringComparison.OrdinalIgnoreCase)).ToList();
        var entriesB = dict.Where(kv => kv.Key.StartsWith(prefixB, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var entry in entriesA)
        {
            dict.TryRemove(entry.Key, out _);
        }

        foreach (var entry in entriesB)
        {
            dict.TryRemove(entry.Key, out _);
        }

        foreach (var entry in entriesA)
        {
            dict[$"{slotNameB}:{entry.Key[prefixA.Length..]}"] = entry.Value;
        }

        foreach (var entry in entriesB)
        {
            dict[$"{slotNameA}:{entry.Key[prefixB.Length..]}"] = entry.Value;
        }
    }

    private void SwapCodexBroadcastOwner(string slotNameA, string slotNameB)
    {
        lock (_codexBroadcastOwnerLock)
        {
            if (_codexBroadcastOwnerSlotKey is null)
            {
                return;
            }

            var prefixA = $"{slotNameA}:";
            var prefixB = $"{slotNameB}:";
            if (_codexBroadcastOwnerSlotKey.StartsWith(prefixA, StringComparison.OrdinalIgnoreCase))
            {
                _codexBroadcastOwnerSlotKey = $"{slotNameB}:{_codexBroadcastOwnerSlotKey[prefixA.Length..]}";
            }
            else if (_codexBroadcastOwnerSlotKey.StartsWith(prefixB, StringComparison.OrdinalIgnoreCase))
            {
                _codexBroadcastOwnerSlotKey = $"{slotNameA}:{_codexBroadcastOwnerSlotKey[prefixB.Length..]}";
            }
        }
    }

    private static AiEngineStatusSnapshot ToDiagnostic(EngineRuntimeState state)
    {
        return new AiEngineStatusSnapshot(
            state.State.ToString(),
            state.Confidence.ToString(),
            state.LastEvidenceAt,
            state.LastReason);
    }

    private static SlotAiStatusSnapshot ToDiagnostic(SlotRuntimeState state)
    {
        return new SlotAiStatusSnapshot(
            state.State.ToString(),
            state.Source.ToString(),
            state.Owner == SlotRuntimeOwner.None ? string.Empty : state.Owner.ToString(),
            state.LastUiRunningAt,
            state.LastUiReadyAt,
            state.LastEvidenceAt,
            state.EvidenceText,
            state.Reason,
            state.HasObservedUiRunning,
            state.ConsecutiveNegativeUiProbes);
    }

    private static AiLogEvidence ReadLatestEvidence(string userDataDirectory, ExtensionLogSource source, int maxRecentLogBytes = MaxRecentLogBytes)
    {
        var newestEvidence = AiLogEvidence.Empty(source);

        foreach (var fileInfo in EnumerateCandidateLogFiles(userDataDirectory, source)
                     .Select(TryGetLogFileInfo)
                     .Where(fileInfo => fileInfo is not null)
                     .Cast<FileInfo>()
                     .OrderByDescending(fileInfo => fileInfo.LastWriteTimeUtc)
                     .Take(MaxCandidateLogFilesPerSource))
        {
            var evidence = ReadLogEvidence(fileInfo, source, maxRecentLogBytes);
            if (evidence.LastEventAt.HasValue
                && (!newestEvidence.LastEventAt.HasValue || evidence.LastEventAt.Value > newestEvidence.LastEventAt.Value))
            {
                newestEvidence = evidence;
            }
        }

        return newestEvidence;
    }

    private static FileInfo? TryGetLogFileInfo(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            return fileInfo.Exists ? fileInfo : null;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            return null;
        }
    }

    private static IEnumerable<string> EnumerateCandidateLogFiles(string userDataDirectory, ExtensionLogSource source)
    {
        var logsDirectory = Path.Combine(userDataDirectory, "logs");
        if (!Directory.Exists(logsDirectory))
        {
            yield break;
        }

        List<DirectoryInfo> sessions;
        try
        {
            sessions = new DirectoryInfo(logsDirectory)
                .EnumerateDirectories()
                .OrderByDescending(directory => directory.LastWriteTimeUtc)
                .Take(8)
                .ToList();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            yield break;
        }

        foreach (var session in sessions)
        {
            IEnumerable<DirectoryInfo> windows;
            try
            {
                windows = session.EnumerateDirectories("window*").ToList();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
                continue;
            }

            foreach (var window in windows)
            {
                foreach (var logPath in EnumerateExtensionHostLogFiles(window.FullName, source))
                {
                    yield return logPath;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateExtensionHostLogFiles(string windowDirectory, ExtensionLogSource source)
    {
        var yieldedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hostDirectoryName in ExtensionHostDirectoryNames)
        {
            foreach (var logPath in EnumerateHostLogFiles(Path.Combine(windowDirectory, hostDirectoryName), source))
            {
                if (yieldedPaths.Add(logPath))
                {
                    yield return logPath;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateHostLogFiles(string hostDirectory, ExtensionLogSource source)
    {
        if (!Directory.Exists(hostDirectory))
        {
            yield break;
        }

        foreach (var logPath in EnumerateDirectLogFiles(hostDirectory, source))
        {
            yield return logPath;
        }

        HashSet<string> extensionDirectoryNames = new(source.ExtensionDirectoryNames, StringComparer.OrdinalIgnoreCase);
        List<DirectoryInfo> nestedDirectories;
        try
        {
            nestedDirectories = new DirectoryInfo(hostDirectory)
                .EnumerateDirectories()
                .Where(directory => !extensionDirectoryNames.Contains(directory.Name))
                .OrderByDescending(directory => directory.LastWriteTimeUtc)
                .Take(12)
                .ToList();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            yield break;
        }

        foreach (var nestedDirectory in nestedDirectories)
        {
            foreach (var logPath in EnumerateDirectLogFiles(nestedDirectory.FullName, source))
            {
                yield return logPath;
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectLogFiles(string parentDirectory, ExtensionLogSource source)
    {
        foreach (var extensionDirectoryName in source.ExtensionDirectoryNames)
        {
            var logPath = Path.Combine(parentDirectory, extensionDirectoryName, source.LogFileName);
            if (File.Exists(logPath))
            {
                yield return logPath;
            }
        }
    }

    private static AiLogEvidence ReadLogEvidence(FileInfo fileInfo, ExtensionLogSource source, int maxRecentLogBytes)
    {
        var cacheKey = $"{maxRecentLogBytes}:{fileInfo.FullName}";
        if (LogEvidenceCache.TryGetValue(cacheKey, out var cached)
            && cached.Length == fileInfo.Length
            && cached.LastWriteTimeUtc == fileInfo.LastWriteTimeUtc)
        {
            return cached.Evidence;
        }

        var evidence = AiLogEvidence.Empty(source);
        var readSucceeded = false;

        try
        {
            foreach (var line in ReadRecentLines(fileInfo.FullName, maxRecentLogBytes))
            {
                if (!TryParseLogTimestamp(line, out var timestamp))
                {
                    continue;
                }

                evidence = evidence with { LastEventAt = Max(evidence.LastEventAt, timestamp) };

                if (source.ErrorSignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase)))
                {
                    evidence = evidence with { LastErrorSignalAt = Max(evidence.LastErrorSignalAt, timestamp) };
                    continue;
                }

                if (source.CompletionSignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase)))
                {
                    evidence = evidence with { LastCompletionSignalAt = Max(evidence.LastCompletionSignalAt, timestamp) };
                    if (source.StandaloneCompletionSignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase)))
                    {
                        evidence = evidence with { LastStandaloneCompletionSignalAt = Max(evidence.LastStandaloneCompletionSignalAt, timestamp) };
                    }

                    continue;
                }

                if (line.Contains(source.RunningSignal, StringComparison.OrdinalIgnoreCase)
                    && !source.IgnoredRunningSignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase)))
                {
                    evidence = evidence with { LastRunningSignalAt = Max(evidence.LastRunningSignalAt, timestamp) };
                    continue;
                }

                if (source.SecondaryRunningSignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase)))
                {
                    evidence = evidence with { LastSecondaryRunningSignalAt = Max(evidence.LastSecondaryRunningSignalAt, timestamp) };
                    continue;
                }

                if (source.ActivitySignals.Length > 0
                    && source.ActivitySignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase)))
                {
                    evidence = evidence with { LastActivitySignalAt = Max(evidence.LastActivitySignalAt, timestamp) };
                    continue;
                }

                if (source.ConfirmationSignals.Length > 0
                    && source.ConfirmationSignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase)))
                {
                    evidence = evidence with { LastConfirmationSignalAt = Max(evidence.LastConfirmationSignalAt, timestamp) };
                    continue;
                }

                if (source.IdleSignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase)))
                {
                    evidence = evidence with { LastIdleSignalAt = Max(evidence.LastIdleSignalAt, timestamp) };
                }
            }

            readSucceeded = true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }

        if (readSucceeded)
        {
            LogEvidenceCache[cacheKey] = new CachedLogEvidence(fileInfo.Length, fileInfo.LastWriteTimeUtc, evidence);
        }

        return evidence;
    }

    private static IEnumerable<string> ReadRecentLines(string path, int maxRecentLogBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytesToRead = (int)Math.Min(stream.Length, maxRecentLogBytes);
        if (bytesToRead <= 0)
        {
            yield break;
        }

        stream.Seek(-bytesToRead, SeekOrigin.End);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        if (stream.Position > 0)
        {
            _ = reader.ReadLine();
        }

        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static bool TryParseLogTimestamp(string line, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (line.Length < 23)
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                line[..23],
                "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var localTime))
        {
            return false;
        }

        timestamp = new DateTimeOffset(localTime);
        return true;
    }

    private sealed record ExtensionLogSource(
        string DisplayName,
        string[] ExtensionDirectoryNames,
        string LogFileName,
        string RunningSignal,
        string[] CompletionSignals,
        string[] StandaloneCompletionSignals,
        string[] IdleSignals,
        string[] IgnoredRunningSignals,
        string[] ErrorSignals,
        bool AllowStandaloneCompletion,
        string[] SecondaryRunningSignals,
        string[] ActivitySignals,
        string[] ConfirmationSignals);

    private readonly record struct CachedLogEvidence(
        long Length,
        DateTime LastWriteTimeUtc,
        AiLogEvidence Evidence);

    private readonly record struct AiLogEvidence(
        string SourceName,
        bool AllowStandaloneCompletion,
        DateTimeOffset? LastEventAt,
        DateTimeOffset? LastRunningSignalAt,
        DateTimeOffset? LastCompletionSignalAt,
        DateTimeOffset? LastStandaloneCompletionSignalAt,
        DateTimeOffset? LastErrorSignalAt,
        DateTimeOffset? LastIdleSignalAt,
        DateTimeOffset? LastSecondaryRunningSignalAt,
        DateTimeOffset? LastActivitySignalAt,
        DateTimeOffset? LastConfirmationSignalAt)
    {
        public static AiLogEvidence Empty(ExtensionLogSource source)
        {
            return new AiLogEvidence(
                source.DisplayName,
                source.AllowStandaloneCompletion,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }
    }

    private sealed record SlotRuntimeState(
        SlotRuntimeStatus State,
        SlotRuntimeSource Source,
        SlotRuntimeOwner Owner,
        DateTimeOffset? FirstSeenAt,
        DateTimeOffset? LastUiRunningAt,
        DateTimeOffset? LastUiReadyAt,
        DateTimeOffset? LastEvidenceAt,
        string EvidenceText,
        string Reason,
        bool HasObservedUiRunning,
        int ConsecutiveNegativeUiProbes)
    {
        public static SlotRuntimeState Idle()
        {
            return new SlotRuntimeState(
                SlotRuntimeStatus.Idle,
                SlotRuntimeSource.None,
                SlotRuntimeOwner.None,
                null,
                null,
                null,
                null,
                string.Empty,
                string.Empty,
                false,
                0);
        }
    }

    private sealed record EngineRuntimeState(
        AiEngine Engine,
        InternalEngineState State,
        EvidenceConfidence Confidence,
        DateTimeOffset? FirstSeenAt,
        DateTimeOffset? LastEvidenceAt,
        DateTimeOffset? RunningStartedAt,
        DateTimeOffset? CompletionBaselineAt,
        DateTimeOffset? CompletedAt,
        EvidenceSource LastEvidenceSource,
        string LastReason,
        DateTimeOffset? LastRunningEvidenceAt,
        DateTimeOffset? LastUiEvidenceAt,
        DateTimeOffset? LastUiNegativeAt,
        int ConsecutiveNegativeUiProbes)
    {
        public static EngineRuntimeState Idle(AiEngine engine)
        {
            return new EngineRuntimeState(
                engine,
                InternalEngineState.Idle,
                EvidenceConfidence.Confirmed,
                null,
                null,
                null,
                null,
                null,
                EvidenceSource.None,
                string.Empty,
                null,
                null,
                null,
                0);
        }
    }

    private sealed record DisplayCandidate(
        AiStatus Status,
        string Detail,
        DateTimeOffset? EventAt,
        string SourceName,
        string EngineName,
        string Confidence,
        string Reason,
        int Priority,
        int ConfidencePriority);

    private sealed record DisplayDecision(
        AiStatus Status,
        string Detail,
        DateTimeOffset? EventAt,
        string SourceName,
        string EngineName,
        string Confidence,
        string Reason);

    private sealed record SlotEngineAggregateCandidate(
        SlotRuntimeStatus State,
        SlotRuntimeOwner Owner,
        DateTimeOffset? EventAt,
        string Reason,
        int Priority);

    private sealed record IdleCauseCandidate(
        DateTimeOffset? EventAt,
        string Owner,
        string Confidence,
        string Reason);

    private enum AiEngine
    {
        Copilot,
        Codex
    }

    private enum SlotRuntimeStatus
    {
        Idle,
        SuspectRunning,
        Running,
        WaitingForConfirmation,
        Completed
    }

    private enum SlotRuntimeSource
    {
        None,
        UiAutomationDirect,
        EngineAggregate,
        LogAssist
    }

    private enum SlotRuntimeOwner
    {
        None,
        Copilot,
        Codex,
        UnknownAi
    }

    private enum InternalEngineState
    {
        Idle,
        SuspectRunning,
        Running,
        WaitingForConfirmation,
        Completed,
        Error
    }

    private enum EvidenceConfidence
    {
        Suspect,
        Probable,
        Confirmed
    }

    private enum EvidenceSource
    {
        None,
        UiAutomation,
        Log,
        BroadcastLog
    }

    private enum UiAutomationOwner
    {
        None,
        Copilot,
        Codex,
        UnknownAi
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
