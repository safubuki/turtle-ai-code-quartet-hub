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
    private static readonly TimeSpan CopilotRunningLeaseWindow = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan CodexProbableRunningWindow = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan CodexSuspectWindow = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan CodexQuietCompletionWindow = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan CodexConfirmationWindow = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CodexBroadcastOwnerStickyWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FutureLogTolerance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DecisionLogInterval = TimeSpan.FromSeconds(10);
    private const int MaxRecentLogBytes = 96 * 1024;
    private const int MaxCandidateLogFilesPerSource = 6;
    private static readonly string[] ExtensionHostDirectoryNames = ["exthost", "remoteexthost", "remoteexhost"];

    private static readonly ExtensionLogSource CodexLogSource = new(
        CodexSourceName,
        ["openai.chatgpt"],
        "Codex.log",
        "Conversation created",
        [],
        [],
        ["Activating Codex extension", "Initialize received", "method=client-status-changed"],
        [],
        [],
        false,
        [],
        ["thread-stream-state-changed", "thread-read-state-changed"],
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
    private readonly ConcurrentDictionary<string, DateTimeOffset> _dismissedAtBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _slotStartedAtByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, UiAutomationProbeResult> _lastUiProbeBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastDecisionSignatureBySlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastDecisionLoggedAtBySlot = new(StringComparer.OrdinalIgnoreCase);
    private string? _codexBroadcastOwnerSlotKey;
    private DateTimeOffset? _codexBroadcastOwnerLastActivityAt;
    private static readonly ConcurrentDictionary<string, CachedLogEvidence> LogEvidenceCache = new(StringComparer.OrdinalIgnoreCase);

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
                    ToDiagnostic(EngineRuntimeState.Idle(AiEngine.Copilot)),
                    ToDiagnostic(EngineRuntimeState.Idle(AiEngine.Codex)),
                    UiAutomationProbeResult.Unknown("VS Code ウィンドウがないため UI Automation は実行していません。"),
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
            var previousCopilot = GetEngineState(slotKey, AiEngine.Copilot);
            var previousCodex = GetEngineState(slotKey, AiEngine.Codex);
            var uiOwner = ResolveUiAutomationOwner(slot, uiProbe, previousCopilot, previousCodex, copilotLog, codexLog, now);

            var nextCopilot = AdvanceCopilotState(previousCopilot, copilotLog, uiProbe, slot.AllowUiAutomationProbe, uiOwner, now);
            var nextCodex = AdvanceCodexState(slot, slotKey, previousCodex, codexLog, uiProbe, slot.AllowUiAutomationProbe, uiOwner, now);

            StoreEngineState(slotKey, nextCopilot);
            StoreEngineState(slotKey, nextCodex);

            var decision = AggregateDecision(canReadLogs, nextCopilot, nextCodex);
            var diagnostics = new AiStatusDiagnostics(
                slot.Name,
                slot.WindowHandle.ToInt64(),
                ToDiagnostic(nextCopilot),
                ToDiagnostic(nextCodex),
                uiProbe,
                decision.EngineName,
                decision.Confidence,
                decision.Reason);

            MaybeLogDecision(slotKey, slot.Name, decision.Status, diagnostics);

            return new AiStatusSnapshot(decision.Status, decision.Detail, decision.EventAt, decision.SourceName)
            {
                Diagnostics = diagnostics
            };
        }
    }

    public bool ShouldPrioritizeUiAutomationProbe(WindowSlot slot)
    {
        if (slot.WindowHandle == IntPtr.Zero)
        {
            return false;
        }

        var slotKey = GetSlotKey(slot);
        lock (_stateLock)
        {
            return GetEngineState(slotKey, AiEngine.Copilot).State is InternalEngineState.Running or InternalEngineState.WaitingForConfirmation or InternalEngineState.SuspectRunning
                || GetEngineState(slotKey, AiEngine.Codex).State is InternalEngineState.Running or InternalEngineState.WaitingForConfirmation or InternalEngineState.SuspectRunning;
        }
    }

    public void Acknowledge(WindowSlot slot)
    {
        var slotKey = GetSlotKey(slot);
        lock (_stateLock)
        {
            StoreEngineState(slotKey, EngineRuntimeState.Idle(AiEngine.Copilot));
            StoreEngineState(slotKey, EngineRuntimeState.Idle(AiEngine.Codex));
            _lastUiProbeBySlot.TryRemove(slotKey, out _);
            _dismissedAtBySlot[slotKey] = DateTimeOffset.Now;
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
        SwapPrefixedEntries(_dismissedAtBySlot, sourceSlotName, targetSlotName);
        SwapPrefixedEntries(_lastUiProbeBySlot, sourceSlotName, targetSlotName);
        SwapPrefixedEntries(_lastDecisionSignatureBySlot, sourceSlotName, targetSlotName);
        SwapPrefixedEntries(_lastDecisionLoggedAtBySlot, sourceSlotName, targetSlotName);
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
        if (uiProbe.Status is not (AiStatus.Running or AiStatus.WaitingForConfirmation))
        {
            return UiAutomationOwner.None;
        }

        var copilotHintAt = GetCopilotUiHintAt(previousCopilot, copilotLog, now);
        var codexHintAt = GetCodexUiHintAt(previousCodex, codexLog, now);

        if (uiProbe.Status == AiStatus.WaitingForConfirmation)
        {
            if (IsRecent(codexLog.LastConfirmationSignalAt, CodexConfirmationWindow, now)
                || previousCodex.State == InternalEngineState.WaitingForConfirmation)
            {
                return UiAutomationOwner.Codex;
            }
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
            return codexHintAt.Value > copilotHintAt.Value
                ? UiAutomationOwner.Codex
                : UiAutomationOwner.Copilot;
        }

        if (slot.IsForeground || slot.IsFocused)
        {
            if (previousCodex.State is InternalEngineState.SuspectRunning or InternalEngineState.Running or InternalEngineState.WaitingForConfirmation)
            {
                return UiAutomationOwner.Codex;
            }

            if (previousCopilot.State is InternalEngineState.Running or InternalEngineState.WaitingForConfirmation)
            {
                return UiAutomationOwner.Copilot;
            }
        }

        return UiAutomationOwner.None;
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

    private static EngineRuntimeState AdvanceCopilotState(
        EngineRuntimeState previous,
        AiLogEvidence log,
        UiAutomationProbeResult uiProbe,
        bool hasFreshUiProbe,
        UiAutomationOwner uiOwner,
        DateTimeOffset now)
    {
        var completedAt = IgnoreIfNotNewerThan(log.LastStandaloneCompletionSignalAt, previous.CompletedAt);
        var runningAt = IgnoreIfNotNewerThan(log.LastRunningSignalAt, previous.CompletedAt);
        var errorAt = IgnoreIfNotNewerThan(log.LastErrorSignalAt, previous.CompletedAt);
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
                hasFreshUiProbe && uiProbe.Status is null && uiProbe.ScanCompleted ? now : previous.LastUiNegativeAt,
                0);
        }

        if (completedAt is { } completionSignalAt)
        {
            var observedRunningAt = previous.RunningStartedAt ?? runningAt;
            if (IsCompletionForObservedRun(completionSignalAt, previous.CompletionBaselineAt, observedRunningAt))
            {
                return new EngineRuntimeState(
                    AiEngine.Copilot,
                    InternalEngineState.Completed,
                    EvidenceConfidence.Confirmed,
                    previous.FirstSeenAt ?? observedRunningAt ?? completionSignalAt,
                    completionSignalAt,
                    observedRunningAt,
                    previous.CompletionBaselineAt,
                    completionSignalAt,
                    EvidenceSource.Log,
                    "panel/editAgent success after observed running",
                    previous.LastRunningEvidenceAt ?? observedRunningAt,
                    previous.LastUiEvidenceAt,
                    previous.LastUiNegativeAt,
                    0);
            }
        }

        if (runningAt is { } runningSignalAt && IsRecent(runningSignalAt, CopilotRunningLeaseWindow, now))
        {
            var runningStartedAt = previous.RunningStartedAt ?? runningSignalAt;
            var baselineAt = previous.CompletionBaselineAt ?? previous.CompletedAt;
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
                "recent ccreq lease",
                runningSignalAt,
                previous.LastUiEvidenceAt,
                hasFreshUiProbe && uiProbe.Status is null && uiProbe.ScanCompleted ? now : previous.LastUiNegativeAt,
                0);
        }

        if (previous.State is InternalEngineState.Running or InternalEngineState.WaitingForConfirmation
            && IsRecent(previous.LastRunningEvidenceAt, CopilotRunningLeaseWindow, now))
        {
            return previous with
            {
                LastUiNegativeAt = hasFreshUiProbe && uiProbe.Status is null && uiProbe.ScanCompleted ? now : previous.LastUiNegativeAt,
                ConsecutiveNegativeUiProbes = 0
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
            Max(previous.LastEvidenceAt, Max(completedAt, Max(runningAt, errorAt))),
            null,
            null,
            null,
            EvidenceSource.None,
            previous.State is InternalEngineState.Running or InternalEngineState.WaitingForConfirmation
                ? "copilot running lease expired"
                : "no Copilot evidence",
            null,
            previous.LastUiEvidenceAt,
            hasFreshUiProbe && uiProbe.Status is null && uiProbe.ScanCompleted ? now : previous.LastUiNegativeAt,
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
        var freshNegative = hasFreshUiProbe && uiProbe.Status is null && uiProbe.ScanCompleted;
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

        var confirmationOwned = confirmationAt is { } confirmationSignalAt
            && IsRecent(confirmationSignalAt, CodexConfirmationWindow, now)
            && IsCodexBroadcastOwner(slot, slotKey, confirmationSignalAt, previous, now);
        if (confirmationOwned && confirmationAt is { } ownedConfirmationAt)
        {
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
                if (freshNegative || negativeCount >= 2)
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
                        previous.LastEvidenceSource,
                        previous.Confidence == EvidenceConfidence.Confirmed
                            ? "confirmed running quiet completion"
                            : "probable running quiet completion",
                        previous.LastRunningEvidenceAt,
                        previous.LastUiEvidenceAt,
                        freshNegative ? now : previous.LastUiNegativeAt,
                        0);
                }

                return previous with
                {
                    LastReason = "awaiting quiet completion confirmation",
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

    private static DisplayDecision AggregateDecision(bool canReadLogs, EngineRuntimeState copilot, EngineRuntimeState codex)
    {
        var candidates = new[] { CreateDisplayCandidate(copilot), CreateDisplayCandidate(codex) }
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

        var latestCause = new[] { copilot, codex }
            .OrderByDescending(state => state.LastEvidenceAt ?? DateTimeOffset.MinValue)
            .First();
        var hasMeaningfulCause = latestCause.LastEvidenceAt.HasValue;

        var idleReason = hasMeaningfulCause && !string.IsNullOrWhiteSpace(latestCause.LastReason)
            ? latestCause.LastReason
            : canReadLogs
                ? "no active evidence"
                : "user-data-dir missing";
        var idleDetail = canReadLogs
            ? !hasMeaningfulCause || string.IsNullOrWhiteSpace(latestCause.LastReason)
                ? "AI は待機中です。"
                : $"AI は待機中です。{latestCause.Engine}: {latestCause.LastReason}"
            : "VS Code の user-data-dir が見つかりません。AI は待機中として扱います。";

        return new DisplayDecision(
            AiStatus.Idle,
            idleDetail,
            latestCause.LastEvidenceAt,
            string.Empty,
            hasMeaningfulCause ? latestCause.Engine.ToString() : string.Empty,
            hasMeaningfulCause ? latestCause.Confidence.ToString() : string.Empty,
            idleReason);
    }

    private static DisplayCandidate? CreateDisplayCandidate(EngineRuntimeState state)
    {
        return state.State switch
        {
            InternalEngineState.WaitingForConfirmation => new DisplayCandidate(
                AiStatus.WaitingForConfirmation,
                $"{state.Engine}: AI は確認待ちです。{state.LastReason}",
                state.LastEvidenceAt,
                state.Engine.ToString(),
                state.Engine.ToString(),
                state.Confidence.ToString(),
                state.LastReason,
                5,
                GetConfidencePriority(state.Confidence)),
            InternalEngineState.Running => new DisplayCandidate(
                AiStatus.Running,
                $"{state.Engine}: AI は実行中です。{state.LastReason}",
                state.LastEvidenceAt,
                state.Engine.ToString(),
                state.Engine.ToString(),
                state.Confidence.ToString(),
                state.LastReason,
                4,
                GetConfidencePriority(state.Confidence)),
            InternalEngineState.Error => new DisplayCandidate(
                AiStatus.Error,
                $"{state.Engine}: エラーを検出しました。{state.LastReason}",
                state.LastEvidenceAt,
                state.Engine.ToString(),
                state.Engine.ToString(),
                state.Confidence.ToString(),
                state.LastReason,
                3,
                GetConfidencePriority(state.Confidence)),
            InternalEngineState.Completed => new DisplayCandidate(
                AiStatus.Completed,
                $"{state.Engine}: AI 実行は完了しました。{state.LastReason}",
                state.CompletedAt ?? state.LastEvidenceAt,
                state.Engine.ToString(),
                state.Engine.ToString(),
                state.Confidence.ToString(),
                state.LastReason,
                2,
                GetConfidencePriority(state.Confidence)),
            _ => null
        };
    }

    private void MaybeLogDecision(string slotKey, string slotName, AiStatus status, AiStatusDiagnostics diagnostics)
    {
        var signature = $"{status}|{diagnostics.FinalEngine}|{diagnostics.FinalConfidence}|{diagnostics.FinalReason}";
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

        var enginePart = string.IsNullOrWhiteSpace(diagnostics.FinalEngine)
            ? string.Empty
            : $" engine={diagnostics.FinalEngine}";
        var confidencePart = string.IsNullOrWhiteSpace(diagnostics.FinalConfidence)
            ? string.Empty
            : $" confidence={diagnostics.FinalConfidence}";
        var reason = string.IsNullOrWhiteSpace(diagnostics.FinalReason)
            ? "n/a"
            : diagnostics.FinalReason.Replace("\r", " ").Replace("\n", " ");

        DiagnosticLog.Write($"AI status {slotName}: final={status}{enginePart}{confidencePart} reason={reason}");
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
        RemovePrefixedEntries(_dismissedAtBySlot, slotName);
        RemovePrefixedEntries(_lastUiProbeBySlot, slotName);
        RemovePrefixedEntries(_lastDecisionSignatureBySlot, slotName);
        RemovePrefixedEntries(_lastDecisionLoggedAtBySlot, slotName);

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

    private enum AiEngine
    {
        Copilot,
        Codex
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
        Codex
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
    AiEngineStatusSnapshot Copilot,
    AiEngineStatusSnapshot Codex,
    UiAutomationProbeResult UiProbe,
    string FinalEngine,
    string FinalConfidence,
    string FinalReason);

public sealed record AiEngineStatusSnapshot(
    string State,
    string Confidence,
    DateTimeOffset? LastEvidenceAt,
    string Reason);
