using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class VscodeChatUiStatusReader
{
    private const int MaxElementsToInspect = 1500;
    private const int MaxElementsAfterRunningSignal = 240;
    private const int MaxTextLengthForStatus = 48;
    private const int MaxTextLengthForConfirmation = 140;
    private static readonly TimeSpan MaxScanDuration = TimeSpan.FromMilliseconds(350);

    private static readonly string[] RunningStatusExactTexts =
    [
        "作業中",
        "実行中",
        "処理中",
        "生成中",
        "思考中",
        "考え中",
        "分析中",
        "評価中",
        "検索中",
        "読み取り中",
        "確認中",
        "レビュー中",
        "編集中",
        "調査中",
        "処理を実行中",
        "ツール実行中",
        "Working",
        "Running",
        "Generating",
        "Thinking"
    ];

    private static readonly string[] RunningStatusPrefixes =
    [
        "Optimizing tool selection",
        "Preparing",
        "Planning",
        "Analyzing",
        "Evaluating",
        "Searching",
        "Reading",
        "Checking",
        "Reviewing",
        "Editing",
        "Running tools",
        "Using tool",
        "Calling tool"
    ];

    private static readonly string[] RunningClassFragments =
    [
        "chat-response-loading",
        "chat-thinking-box"
    ];

    private static readonly string[] StopActionTexts =
    [
        "中断",
        "中止",
        "取り消す",
        "キャンセル",
        "Stop",
        "Cancel"
    ];

    private static readonly string[] ChatContextFragments =
    [
        "chat",
        "chat-widget",
        "chat-view",
        "チャット",
        "会話",
        "copilot",
        "codex",
        "agent",
        "エージェント",
        "応答",
        "プロンプト",
        "interactive",
        "interactive-session",
        "interactive-session-status",
        "action-label",
        "codicon"
    ];

    private static readonly string[] StopClassFragments =
    [
        "codicon-stop",
        "codicon-stop-circle",
        "codicon-debug-stop",
        "codicon-circle-slash"
    ];

    private static readonly string[] ConfirmationActionTexts =
    [
        "Continue",
        "続行",
        "Allow",
        "許可"
    ];

    private static readonly string[] ContextualConfirmationActionTexts =
    [
        "はい",
        "Yes",
        "Run",
        "実行",
        "実行する",
        "Approve",
        "承認"
    ];

    private static readonly string[] InputContextFragments =
    [
        "input",
        "prompt",
        "composer",
        "chat-input",
        "chatinput",
        "interactive-input",
        "textbox",
        "editor",
        "monaco",
        "message",
        "メッセージ",
        "入力",
        "質問",
        "指示"
    ];

    private static readonly string[] SendActionTexts =
    [
        "Send",
        "送信",
        "送る",
        "Submit"
    ];

    private static readonly string[] SendAutomationIdFragments =
    [
        "send",
        "submit"
    ];

    private static readonly string[] SendClassFragments =
    [
        "codicon-send",
        "send",
        "submit"
    ];

    public UiAutomationProbeResult TryRead(WindowSlot slot)
    {
        return TryRead(slot.WindowHandle);
    }

    internal UiAutomationProbeResult TryRead(WindowSlotStatusSnapshot slot)
    {
        return TryRead(slot.WindowHandle);
    }

    private UiAutomationProbeResult TryRead(IntPtr windowHandle)
    {
        try
        {
            var root = AutomationElement.FromHandle(windowHandle);
            if (root is null)
            {
                return UiAutomationProbeResult.Unknown("VS Code UI Automation のルート取得に失敗しました。");
            }

            return TryRead(root);
        }
        catch (ElementNotAvailableException ex)
        {
            DiagnosticLog.Write(ex);
            return UiAutomationProbeResult.Unknown($"VS Code UI Automation の要素参照に失敗しました: {ex.GetType().Name}");
        }
        catch (InvalidOperationException ex)
        {
            DiagnosticLog.Write(ex);
            return UiAutomationProbeResult.Unknown($"VS Code UI Automation の走査に失敗しました: {ex.GetType().Name}");
        }
        catch (COMException ex)
        {
            DiagnosticLog.Write(ex);
            return UiAutomationProbeResult.Unknown($"VS Code UI Automation の COM 呼び出しに失敗しました: 0x{ex.HResult:X8}");
        }
    }

    private static UiAutomationProbeResult TryRead(AutomationElement root)
    {
        var stopwatch = Stopwatch.StartNew();
        var rootBounds = TryGetBoundingRectangle(root);
        var walker = TreeWalker.RawViewWalker;
        var queue = new Queue<(AutomationElement Element, bool InheritedChatContext)>();
        queue.Enqueue((root, false));

        var inspected = 0;
        int? runningFoundAt = null;
        string? runningDetail = null;
        bool timedOut = false;
        bool foundRunningText = false;
        bool foundRunningClass = false;
        bool foundStopButton = false;
        bool foundConfirmationButton = false;
        bool foundInputBox = false;
        bool foundInputReady = false;
        bool foundSendButton = false;
        bool foundDisabledSendButton = false;
        string evidenceText = string.Empty;
        string evidenceAutomationId = string.Empty;
        string evidenceClassName = string.Empty;
        DateTimeOffset? evidenceAt = null;

        void CaptureEvidence(ElementSnapshot snapshot, bool prefer)
        {
            var alreadyCaptured = !string.IsNullOrWhiteSpace(evidenceText)
                || !string.IsNullOrWhiteSpace(evidenceAutomationId)
                || !string.IsNullOrWhiteSpace(evidenceClassName);
            if (alreadyCaptured && !prefer)
            {
                return;
            }

            evidenceText = TrimForDetail(snapshot.Name);
            evidenceAutomationId = snapshot.AutomationId;
            evidenceClassName = snapshot.ClassName;
            evidenceAt = DateTimeOffset.Now;
        }

        while (queue.Count > 0 && inspected < MaxElementsToInspect)
        {
            var (element, inheritedChatContext) = queue.Dequeue();
            inspected++;

            var snapshot = ReadElementSnapshot(element, rootBounds);
            var hasChatContext = inheritedChatContext || HasChatContext(snapshot);

            if (TryReadInputBox(snapshot, hasChatContext))
            {
                foundInputBox = true;
                if (snapshot.IsEnabled)
                {
                    foundInputReady = true;
                    CaptureEvidence(snapshot, prefer: false);
                }
            }

            if (TryReadSendButton(snapshot, hasChatContext, out var sendButtonEnabled))
            {
                if (sendButtonEnabled)
                {
                    foundSendButton = true;
                    CaptureEvidence(snapshot, prefer: false);
                }
                else
                {
                    foundDisabledSendButton = true;
                }
            }

            if (TryReadConfirmationSignal(snapshot, hasChatContext, out var confirmDetail))
            {
                foundConfirmationButton = true;
                CaptureEvidence(snapshot, prefer: true);
                return CreateProbeResult(
                    AiStatus.WaitingForConfirmation,
                    DateTimeOffset.Now,
                    false,
                    false,
                    inspected,
                    confirmDetail,
                    foundRunningText,
                    foundRunningClass,
                    foundStopButton,
                    foundConfirmationButton,
                    foundInputBox,
                    foundInputReady,
                    foundSendButton,
                    foundDisabledSendButton,
                    evidenceText,
                    evidenceAutomationId,
                    evidenceClassName);
            }

            if (TryReadRunningTextSignal(snapshot, hasChatContext, out var runningTextDetail))
            {
                foundRunningText = true;
                if (runningDetail is null)
                {
                    runningDetail = runningTextDetail;
                    runningFoundAt = inspected;
                    CaptureEvidence(snapshot, prefer: true);
                }
            }

            if (TryReadRunningClassSignal(snapshot, hasChatContext, out var runningClassDetail))
            {
                foundRunningClass = true;
                if (runningDetail is null)
                {
                    runningDetail = runningClassDetail;
                    runningFoundAt = inspected;
                    CaptureEvidence(snapshot, prefer: true);
                }
            }

            if (TryReadStopSignal(snapshot, hasChatContext, out var stopDetail))
            {
                foundStopButton = true;
                if (runningDetail is null)
                {
                    runningDetail = stopDetail;
                    runningFoundAt = inspected;
                    CaptureEvidence(snapshot, prefer: true);
                }
            }

            if (runningFoundAt.HasValue
                && inspected - runningFoundAt.Value >= MaxElementsAfterRunningSignal)
            {
                break;
            }

            EnqueueChildren(walker, element, queue, hasChatContext);

            if (stopwatch.Elapsed >= MaxScanDuration)
            {
                timedOut = true;
                break;
            }
        }

        if (runningDetail is not null)
        {
            return CreateProbeResult(
                AiStatus.Running,
                evidenceAt ?? DateTimeOffset.Now,
                timedOut,
                false,
                inspected,
                runningDetail,
                foundRunningText,
                foundRunningClass,
                foundStopButton,
                foundConfirmationButton,
                foundInputBox,
                foundInputReady,
                foundSendButton,
                foundDisabledSendButton,
                evidenceText,
                evidenceAutomationId,
                evidenceClassName);
        }

        var scanCompleted = !timedOut && queue.Count == 0;
        var probeDetail = scanCompleted
            ? foundInputReady || foundSendButton
                ? "VS Code UI Automation の走査を完了し、チャット入力が可能な状態に戻っていることを確認しました。"
                : foundInputBox
                    ? "VS Code UI Automation の走査を完了し、入力欄は見つかりましたが実行中シグナルは見つかりませんでした。"
                    : "VS Code UI Automation の走査を最後まで完了しましたが、実行中シグナルは見つかりませんでした。"
            : timedOut
                ? "VS Code UI Automation の走査がタイムアウトしたため、状態は未確定です。"
                : "VS Code UI Automation の走査を打ち切ったため、状態は未確定です。";

        return CreateProbeResult(
            null,
            null,
            timedOut,
            scanCompleted,
            inspected,
            probeDetail,
            foundRunningText,
            foundRunningClass,
            foundStopButton,
            foundConfirmationButton,
            foundInputBox,
            foundInputReady,
            foundSendButton,
            foundDisabledSendButton,
            evidenceText,
            evidenceAutomationId,
            evidenceClassName);
    }

    private static UiAutomationProbeResult CreateProbeResult(
        AiStatus? status,
        DateTimeOffset? evidenceAt,
        bool timedOut,
        bool scanCompleted,
        int inspectedElementCount,
        string detail,
        bool foundRunningText,
        bool foundRunningClass,
        bool foundStopButton,
        bool foundConfirmationButton,
        bool foundInputBox,
        bool foundInputReady,
        bool foundSendButton,
        bool foundDisabledSendButton,
        string evidenceText,
        string evidenceAutomationId,
        string evidenceClassName)
    {
        return new UiAutomationProbeResult(
            status,
            evidenceAt,
            timedOut,
            scanCompleted,
            inspectedElementCount,
            detail,
            foundRunningText,
            foundRunningClass,
            foundStopButton,
            foundConfirmationButton,
            foundInputBox,
            foundInputReady,
            foundSendButton,
            foundDisabledSendButton,
            evidenceText,
            evidenceAutomationId,
            evidenceClassName);
    }

    private static ElementSnapshot ReadElementSnapshot(AutomationElement element, System.Windows.Rect? rootBounds)
    {
        var isVisible = IsVisible(element, rootBounds);
        return new ElementSnapshot(
            GetStringProperty(element, AutomationElement.NameProperty),
            GetStringProperty(element, AutomationElement.AutomationIdProperty),
            GetStringProperty(element, AutomationElement.ClassNameProperty),
            GetControlTypeName(element),
            isVisible,
            isVisible && IsEnabled(element));
    }

    private static void EnqueueChildren(
        TreeWalker walker,
        AutomationElement parent,
        Queue<(AutomationElement Element, bool InheritedChatContext)> queue,
        bool parentHasChatContext)
    {
        AutomationElement? child = null;
        try
        {
            child = walker.GetFirstChild(parent);
            while (child is not null)
            {
                queue.Enqueue((child, parentHasChatContext));
                child = walker.GetNextSibling(child);
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (COMException)
        {
        }
    }

    private static bool TryReadRunningTextSignal(ElementSnapshot element, bool hasChatContext, out string detail)
    {
        if (element.IsVisible && IsCurrentStatusText(element.Name, GetCombinedContext(element), hasChatContext))
        {
            detail = $"VS Code UI: {element.Name} を検出しました。";
            return true;
        }

        detail = string.Empty;
        return false;
    }

    private static bool TryReadRunningClassSignal(ElementSnapshot element, bool hasChatContext, out string detail)
    {
        if (element.IsVisible
            && ContainsAny(element.ClassName, RunningClassFragments)
            && hasChatContext)
        {
            detail = "VS Code UI: チャットの実行中インジケーターを検出しました。";
            return true;
        }

        detail = string.Empty;
        return false;
    }

    private static bool TryReadStopSignal(ElementSnapshot element, bool hasChatContext, out string detail)
    {
        var combinedContext = GetCombinedContext(element);

        if (element.IsVisible
            && element.IsEnabled
            && hasChatContext
            && (ContainsAny(element.ClassName, StopClassFragments)
                || ContainsAny(combinedContext, StopClassFragments)
                || ContainsStopAction(element.Name)))
        {
            detail = string.IsNullOrWhiteSpace(element.Name)
                ? "VS Code UI: チャット中断ボタンを検出しました。"
                : $"VS Code UI: {TrimForDetail(element.Name)} を検出しました。";
            return true;
        }

        detail = string.Empty;
        return false;
    }

    private static bool TryReadInputBox(ElementSnapshot element, bool hasChatContext)
    {
        if (!element.IsVisible || !hasChatContext)
        {
            return false;
        }

        if (string.Equals(element.ControlType, "ControlType.Edit", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(element.ControlType, "ControlType.Document", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var combinedContext = GetCombinedContext(element);
        return ContainsAny(combinedContext, InputContextFragments);
    }

    private static bool TryReadSendButton(ElementSnapshot element, bool hasChatContext, out bool isEnabled)
    {
        isEnabled = false;
        if (!element.IsVisible
            || !hasChatContext
            || !string.Equals(element.ControlType, "ControlType.Button", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var combinedContext = GetCombinedContext(element);
        var matchesSend = ContainsAny(element.Name, SendActionTexts)
            || ContainsAny(element.AutomationId, SendAutomationIdFragments)
            || ContainsAny(element.ClassName, SendClassFragments)
            || ContainsAny(combinedContext, SendClassFragments);
        if (!matchesSend)
        {
            return false;
        }

        isEnabled = element.IsEnabled;
        return true;
    }

    private static bool IsVisible(AutomationElement element, System.Windows.Rect? rootBounds)
    {
        try
        {
            var rect = TryGetBoundingRectangle(element);
            if (rect.HasValue)
            {
                return !rootBounds.HasValue || Intersects(rootBounds.Value, rect.Value);
            }

            var offscreen = element.GetCurrentPropertyValue(AutomationElement.IsOffscreenProperty, true);
            if (offscreen is bool isOffscreen && isOffscreen)
            {
                return false;
            }

            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static System.Windows.Rect? TryGetBoundingRectangle(AutomationElement element)
    {
        try
        {
            var value = element.GetCurrentPropertyValue(AutomationElement.BoundingRectangleProperty, true);
            if (value is not System.Windows.Rect rect
                || rect.IsEmpty
                || rect.Width <= 0
                || rect.Height <= 0)
            {
                return null;
            }

            return rect;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static bool Intersects(System.Windows.Rect rootBounds, System.Windows.Rect elementBounds)
    {
        return elementBounds.Right > rootBounds.Left
            && elementBounds.Left < rootBounds.Right
            && elementBounds.Bottom > rootBounds.Top
            && elementBounds.Top < rootBounds.Bottom;
    }

    private static bool IsEnabled(AutomationElement element)
    {
        try
        {
            var enabled = element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true);
            return enabled is not bool value || value;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static bool IsCurrentStatusText(string value, string combinedContext, bool hasChatContext = false)
    {
        var text = value.Trim();
        if (text.Length == 0 || text.Length > MaxTextLengthForStatus)
        {
            return false;
        }

        var normalized = text.TrimEnd('.', '…').Trim();
        if (normalized.StartsWith("Thinking Effort", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hasChatContextFinal = hasChatContext || ContainsAny(combinedContext, ChatContextFragments);
        var exactMatch = RunningStatusExactTexts.Any(signal => string.Equals(normalized, signal, StringComparison.OrdinalIgnoreCase));
        if (exactMatch)
        {
            // "Thinking" (English exact) is prone to collision with model setting UI; require chat context.
            if (string.Equals(normalized, "Thinking", StringComparison.OrdinalIgnoreCase))
            {
                return hasChatContextFinal;
            }

            // All other exact status texts (考え中, 作業中, 実行中, Working, Running, Generating, etc.)
            // are treated as context-free: visible in the UI tree means Running.
            return true;
        }

        var prefixMatch = RunningStatusPrefixes.Any(signal => normalized.StartsWith(signal, StringComparison.OrdinalIgnoreCase));
        if (!prefixMatch)
        {
            return false;
        }

        return hasChatContextFinal || normalized.Length >= 12 || normalized.Contains("tool", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsStopAction(string value)
    {
        return StopActionTexts.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadConfirmationSignal(ElementSnapshot element, bool hasChatContext, out string detail)
    {
        var combinedContext = GetCombinedContext(element);

        if (element.IsVisible
            && element.IsEnabled
            && IsConfirmationActionName(element.Name, out var requiresContext)
            && (!requiresContext || hasChatContext))
        {
            detail = string.IsNullOrWhiteSpace(element.Name)
                ? "VS Code UI: チャット確認ボタンを検出しました。"
                : $"VS Code UI: {TrimForDetail(element.Name)} を検出しました。";
            return true;
        }

        detail = string.Empty;
        return false;
    }

    private static bool IsConfirmationActionName(string value, out bool requiresContext)
    {
        requiresContext = false;
        var trimmed = NormalizeActionName(value);
        if (trimmed.Length == 0 || trimmed.Length > MaxTextLengthForConfirmation)
        {
            return false;
        }

        // Exclude debug keybinding patterns like "Continue (F5)", "続行 (F5)"
        if (trimmed.Contains("(F", StringComparison.Ordinal))
        {
            return false;
        }

        if (ConfirmationActionTexts.Any(signal =>
            string.Equals(trimmed, signal, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith($"{signal} ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith($"{signal}(", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        requiresContext = true;
        return ContextualConfirmationActionTexts.Any(signal =>
            string.Equals(trimmed, signal, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith($"{signal} ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith($"{signal}(", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith($"{signal}、", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeActionName(string value)
    {
        var trimmed = value.Trim();
        var index = 0;
        while (index < trimmed.Length && (char.IsDigit(trimmed[index]) || char.IsWhiteSpace(trimmed[index])))
        {
            index++;
        }

        if (index < trimmed.Length && (trimmed[index] == '.' || trimmed[index] == '。' || trimmed[index] == ':' || trimmed[index] == ')'))
        {
            index++;
            while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index]))
            {
                index++;
            }

            return trimmed[index..].Trim();
        }

        return trimmed;
    }

    private static bool ContainsAny(string value, IEnumerable<string> fragments)
    {
        return fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasChatContext(ElementSnapshot element)
    {
        return ContainsAny(GetCombinedContext(element), ChatContextFragments);
    }

    private static string GetCombinedContext(ElementSnapshot element)
    {
        return $"{element.Name} {element.AutomationId} {element.ClassName} {element.ControlType}";
    }

    private static string GetStringProperty(AutomationElement element, AutomationProperty property)
    {
        try
        {
            var value = element.GetCurrentPropertyValue(property, true);
            return value == AutomationElement.NotSupported || value is null
                ? string.Empty
                : value.ToString() ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
        catch (COMException)
        {
            return string.Empty;
        }
    }

    private static string GetControlTypeName(AutomationElement element)
    {
        try
        {
            var value = element.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty, true);
            return value is ControlType controlType
                ? controlType.ProgrammaticName ?? string.Empty
                : string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
        catch (COMException)
        {
            return string.Empty;
        }
    }

    private static string TrimForDetail(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 80 ? trimmed : $"{trimmed[..77]}...";
    }

    private readonly record struct ElementSnapshot(
        string Name,
        string AutomationId,
        string ClassName,
        string ControlType,
        bool IsVisible,
        bool IsEnabled);
}

public sealed record UiAutomationProbeResult(
    AiStatus? Status,
    DateTimeOffset? EvidenceAt,
    bool TimedOut,
    bool ScanCompleted,
    int InspectedElementCount,
    string Detail,
    bool FoundRunningText,
    bool FoundRunningClass,
    bool FoundStopButton,
    bool FoundConfirmationButton,
    bool FoundInputBox,
    bool FoundInputReady,
    bool FoundSendButton,
    bool FoundDisabledSendButton,
    string EvidenceText,
    string EvidenceAutomationId,
    string EvidenceClassName)
{
    public bool HasRunningEvidence => FoundRunningText || FoundRunningClass || FoundStopButton;

    public bool HasReadyInputEvidence => FoundInputReady || FoundSendButton;

    public static UiAutomationProbeResult Unknown(string detail)
    {
        return new UiAutomationProbeResult(
            null,
            null,
            false,
            false,
            0,
            detail,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            string.Empty,
            string.Empty,
            string.Empty);
    }

    public AiStatusSnapshot? ToSnapshot(string sourceName = "UI Automation")
    {
        return Status.HasValue
            ? new AiStatusSnapshot(Status.Value, Detail, EvidenceAt, sourceName)
            : null;
    }
}
