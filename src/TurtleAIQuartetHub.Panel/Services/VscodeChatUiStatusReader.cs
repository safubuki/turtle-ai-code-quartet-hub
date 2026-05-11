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
    private static readonly TimeSpan MaxScanDuration = TimeSpan.FromMilliseconds(220);

    private static readonly string[] RunningStatusExactTexts =
    [
        "作業中",
        "実行中",
        "処理中",
        "生成中",
        "思考中",
        "考え中",
        "Working",
        "Running",
        "Generating",
        "Thinking"
    ];

    private static readonly string[] RunningStatusPrefixes =
    [
        "Optimizing tool selection",
        "Preparing",
        "Planning"
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
        "copilot",
        "codex",
        "agent",
        "interactive",
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
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);

        var inspected = 0;
        int? runningFoundAt = null;
        string? runningDetail = null;
        bool timedOut = false;

        while (queue.Count > 0 && inspected < MaxElementsToInspect)
        {
            var element = queue.Dequeue();
            inspected++;

            var snapshot = ReadElementSnapshot(element, rootBounds);
            if (TryReadConfirmationSignal(snapshot, out var confirmDetail))
            {
                return new UiAutomationProbeResult(
                    AiStatus.WaitingForConfirmation,
                    DateTimeOffset.Now,
                    false,
                    false,
                    inspected,
                    confirmDetail);
            }

            if (runningDetail is null && TryReadRunningSignal(snapshot, out var detail))
            {
                runningDetail = detail;
                runningFoundAt = inspected;
            }

            if (runningFoundAt.HasValue
                && inspected - runningFoundAt.Value >= MaxElementsAfterRunningSignal)
            {
                break;
            }

            EnqueueChildren(walker, element, queue);

            if (stopwatch.Elapsed >= MaxScanDuration)
            {
                timedOut = true;
                break;
            }
        }

        if (runningDetail is not null)
        {
            return new UiAutomationProbeResult(
                AiStatus.Running,
                DateTimeOffset.Now,
                timedOut,
                false,
                inspected,
                runningDetail);
        }

        var scanCompleted = !timedOut && queue.Count == 0;
        var probeDetail = scanCompleted
            ? "VS Code UI Automation の走査を最後まで完了しましたが、実行中シグナルは見つかりませんでした。"
            : timedOut
                ? "VS Code UI Automation の走査がタイムアウトしたため、状態は未確定です。"
                : "VS Code UI Automation の走査を打ち切ったため、状態は未確定です。";

        return new UiAutomationProbeResult(
            null,
            null,
            timedOut,
            scanCompleted,
            inspected,
            probeDetail);
    }

    private static ElementSnapshot ReadElementSnapshot(AutomationElement element, System.Windows.Rect? rootBounds)
    {
        var isVisible = IsVisible(element, rootBounds);
        return new ElementSnapshot(
            GetStringProperty(element, AutomationElement.NameProperty),
            GetStringProperty(element, AutomationElement.AutomationIdProperty),
            GetStringProperty(element, AutomationElement.ClassNameProperty),
            isVisible,
            isVisible && IsEnabled(element));
    }

    private static void EnqueueChildren(
        TreeWalker walker,
        AutomationElement parent,
        Queue<AutomationElement> queue)
    {
        AutomationElement? child = null;
        try
        {
            child = walker.GetFirstChild(parent);
            while (child is not null)
            {
                queue.Enqueue(child);
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

    private static bool TryReadRunningSignal(ElementSnapshot element, out string detail)
    {
        var combinedContext = $"{element.AutomationId} {element.ClassName}";

        if (element.IsVisible && IsCurrentStatusText(element.Name))
        {
            detail = $"VS Code UI: {element.Name} を検出しました。";
            return true;
        }

        if (element.IsVisible
            && ContainsAny(element.ClassName, RunningClassFragments)
            && ContainsAny(combinedContext, ChatContextFragments))
        {
            detail = "VS Code UI: チャットの実行中インジケーターを検出しました。";
            return true;
        }

        if (element.IsVisible
            && element.IsEnabled
            && ContainsAny(combinedContext, ChatContextFragments)
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

    private static bool IsCurrentStatusText(string value)
    {
        var text = value.Trim();
        if (text.Length == 0 || text.Length > MaxTextLengthForStatus)
        {
            return false;
        }

        var normalized = text.TrimEnd('.', '…').Trim();
        return RunningStatusExactTexts.Any(signal => string.Equals(normalized, signal, StringComparison.OrdinalIgnoreCase))
            || RunningStatusPrefixes.Any(signal => normalized.StartsWith(signal, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsStopAction(string value)
    {
        return StopActionTexts.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadConfirmationSignal(ElementSnapshot element, out string detail)
    {
        var combinedContext = $"{element.AutomationId} {element.ClassName} {element.Name}";

        if (element.IsVisible
            && element.IsEnabled
            && IsConfirmationActionName(element.Name, out var requiresContext)
            && (!requiresContext || ContainsAny(combinedContext, ChatContextFragments)))
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

    private static string TrimForDetail(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 80 ? trimmed : $"{trimmed[..77]}...";
    }

    private readonly record struct ElementSnapshot(
        string Name,
        string AutomationId,
        string ClassName,
        bool IsVisible,
        bool IsEnabled);
}

public sealed record UiAutomationProbeResult(
    AiStatus? Status,
    DateTimeOffset? EvidenceAt,
    bool TimedOut,
    bool ScanCompleted,
    int InspectedElementCount,
    string Detail)
{
    public static UiAutomationProbeResult Unknown(string detail)
    {
        return new UiAutomationProbeResult(
            null,
            null,
            false,
            false,
            0,
            detail);
    }

    public AiStatusSnapshot? ToSnapshot(string sourceName = "UI Automation")
    {
        return Status.HasValue
            ? new AiStatusSnapshot(Status.Value, Detail, EvidenceAt, sourceName)
            : null;
    }
}
