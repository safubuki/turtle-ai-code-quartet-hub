using System.Runtime.InteropServices;
using System.Windows.Automation;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class VscodeChatUiStatusReader
{
    private const int MaxElementsToInspect = 6000;
    private const int MaxTextLengthForStatus = 100;
    private const int MaxTextLengthForConfirmation = 140;

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
        "Planning",
        "Thinking",
        "Working",
        "Generating"
    ];

    private static readonly string[] CompletedStatusPrefixes =
    [
        "Ran ",
        "ran ",
        "実行済みコマンド",
        "ウェブを",
        "編集済みファイル"
    ];

    private static readonly string[] ChatContextFragments =
    [
        "chat",
        "チャット",
        "copilot",
        "codex",
        "agent",
        "response",
        "interactive-response",
        "interactive-item-container",
        "editing-session",
        "system-initiated-request",
        "interactive-session-status",
        "interactive-session-status-item",
        "chat-progress-reservable",
        "chat-response-loading",
        "chat-most-recent-response"
    ];

    private static readonly string[] ErrorStatusFragments =
    [
        "service disruption",
        "no response was returned",
        "network error",
        "networkerror",
        "internal server error"
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

    private static readonly string[] ConfirmationContextFragments =
    [
        "approval",
        "approve",
        "confirmation",
        "confirm",
        "continue",
        "allow",
        "terminal",
        "command",
        "pwsh",
        "powershell",
        "bash",
        "cmd",
        "承認",
        "確認",
        "続行",
        "許可",
        "コマンド",
        "ターミナル"
    ];

    public AiStatusSnapshot? TryRead(WindowSlot slot)
    {
        return TryRead(slot.WindowHandle);
    }

    internal AiStatusSnapshot? TryRead(WindowSlotStatusSnapshot slot)
    {
        return TryRead(slot.WindowHandle);
    }

    private AiStatusSnapshot? TryRead(IntPtr windowHandle)
    {
        try
        {
            var root = AutomationElement.FromHandle(windowHandle);
            if (root is null)
            {
                return null;
            }

            return TryRead(root);
        }
        catch (ElementNotAvailableException ex)
        {
            DiagnosticLog.Write(ex);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            DiagnosticLog.Write(ex);
            return null;
        }
        catch (COMException ex)
        {
            DiagnosticLog.Write(ex);
            return null;
        }
    }

    private static AiStatusSnapshot? TryRead(AutomationElement root)
    {
        var walker = TreeWalker.RawViewWalker;
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);

        var inspected = 0;
        AiStatusSnapshot? runningResult = null;
        AiStatusSnapshot? confirmationResult = null;
        AiStatusSnapshot? errorResult = null;
        AiStatusSnapshot? completedResult = null;

        while (queue.Count > 0 && inspected < MaxElementsToInspect)
        {
            var element = queue.Dequeue();
            inspected++;

            if (runningResult is null && TryReadRunningSignal(element, out var runningDetail))
            {
                runningResult = new AiStatusSnapshot(AiStatus.Running, runningDetail, DateTimeOffset.Now);
            }

            if (confirmationResult is null && TryReadConfirmationSignal(element, out var confirmationDetail))
            {
                confirmationResult = new AiStatusSnapshot(AiStatus.WaitingForConfirmation, confirmationDetail, DateTimeOffset.Now);
            }

            if (errorResult is null && TryReadErrorSignal(element, out var errorDetail))
            {
                errorResult = new AiStatusSnapshot(AiStatus.Error, errorDetail, DateTimeOffset.Now);
            }

            if (completedResult is null && TryReadCompletedSignal(element, out var completedDetail))
            {
                completedResult = new AiStatusSnapshot(AiStatus.Completed, completedDetail, DateTimeOffset.Now);
            }

            if (runningResult is not null && confirmationResult is not null)
            {
                break;
            }

            EnqueueChildren(walker, element, queue);
        }

        return confirmationResult ?? runningResult ?? errorResult ?? completedResult;
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

    private static bool TryReadRunningSignal(AutomationElement element, out string detail)
    {
        var name = GetStringProperty(element, AutomationElement.NameProperty);
        var automationId = GetStringProperty(element, AutomationElement.AutomationIdProperty);
        var className = GetStringProperty(element, AutomationElement.ClassNameProperty);
        var combinedContext = $"{name} {automationId} {className}";
        var isVisible = IsVisible(element);
        var hasChatContext = HasChatContext(element, combinedContext);

        if (isVisible && hasChatContext && IsCurrentStatusText(name))
        {
            detail = $"VS Code UI: {name} を検出しました。";
            return true;
        }

        detail = string.Empty;
        return false;
    }

    private static bool TryReadErrorSignal(AutomationElement element, out string detail)
    {
        var name = GetStringProperty(element, AutomationElement.NameProperty);
        var automationId = GetStringProperty(element, AutomationElement.AutomationIdProperty);
        var className = GetStringProperty(element, AutomationElement.ClassNameProperty);
        var combinedContext = $"{name} {automationId} {className}";

        if (IsVisible(element)
            && HasChatContext(element, combinedContext)
            && ContainsAny(name, ErrorStatusFragments))
        {
            detail = $"VS Code UI: {TrimForDetail(name)} をエラー表示として検出しました。";
            return true;
        }

        detail = string.Empty;
        return false;
    }

    private static bool TryReadCompletedSignal(AutomationElement element, out string detail)
    {
        var name = GetStringProperty(element, AutomationElement.NameProperty);
        var automationId = GetStringProperty(element, AutomationElement.AutomationIdProperty);
        var className = GetStringProperty(element, AutomationElement.ClassNameProperty);
        var combinedContext = $"{name} {automationId} {className}";

        if (IsVisible(element)
            && HasChatContext(element, combinedContext)
            && IsCompletedStatusText(name))
        {
            detail = $"VS Code UI: {TrimForDetail(name)} を完了表示として検出しました。";
            return true;
        }

        detail = string.Empty;
        return false;
    }

    private static bool IsVisible(AutomationElement element)
    {
        try
        {
            var offscreen = element.GetCurrentPropertyValue(AutomationElement.IsOffscreenProperty, true);
            if (offscreen is bool isOffscreen && isOffscreen)
            {
                return false;
            }

            var rectangle = element.GetCurrentPropertyValue(AutomationElement.BoundingRectangleProperty, true);
            return rectangle is not System.Windows.Rect rect || rect.Width > 0 && rect.Height > 0;
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
        if (text.Length == 0)
        {
            return false;
        }

        var normalized = text.TrimEnd('.', '…').Trim();
        if (RunningStatusPrefixes.Any(signal => normalized.StartsWith(signal, StringComparison.OrdinalIgnoreCase))
            || normalized.Contains("生成しています")
            || normalized.Contains("進行中"))
        {
            return true;
        }

        if (normalized.Length > MaxTextLengthForStatus)
        {
            return false;
        }

        return RunningStatusExactTexts.Any(signal => string.Equals(normalized, signal, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCompletedStatusText(string value)
    {
        var text = value.Trim();
        if (text.Length == 0 || text.Length > MaxTextLengthForConfirmation)
        {
            return false;
        }

        var normalized = text.TrimEnd('.', '…').Trim();
        return CompletedStatusPrefixes.Any(signal => normalized.StartsWith(signal, StringComparison.OrdinalIgnoreCase))
            || normalized.Contains("個のファイルを編集しました")
            || (normalized.Contains("コマンド") && normalized.Contains("ran ", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsActionLikeElement(AutomationElement element, string className)
    {
        try
        {
            var controlType = element.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty, true);
            if (controlType == ControlType.Button
                || controlType == ControlType.MenuItem
                || controlType == ControlType.Hyperlink)
            {
                return true;
            }
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

        return className.Contains("button", StringComparison.OrdinalIgnoreCase)
            || className.Contains("monaco-button", StringComparison.OrdinalIgnoreCase)
            || className.Contains("action-item", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadConfirmationSignal(AutomationElement element, out string detail)
    {
        var name = GetStringProperty(element, AutomationElement.NameProperty);
        var automationId = GetStringProperty(element, AutomationElement.AutomationIdProperty);
        var className = GetStringProperty(element, AutomationElement.ClassNameProperty);
        var combinedContext = $"{automationId} {className}";
        var hasChatContext = HasChatContext(element, combinedContext);

        if (IsVisible(element)
            && IsEnabled(element)
            && hasChatContext
            && IsActionLikeElement(element, className)
            && IsConfirmationActionName(name, out var requiresContext)
            && (!requiresContext || HasConfirmationPromptContext(element)))
        {
            detail = string.IsNullOrWhiteSpace(name)
                ? "VS Code UI: チャット確認ボタンを検出しました。"
                : $"VS Code UI: {TrimForDetail(name)} を検出しました。";
            return true;
        }

        detail = string.Empty;
        return false;
    }

    private static bool HasChatContext(AutomationElement element, string selfContext)
    {
        if (ContainsAny(selfContext, ChatContextFragments))
        {
            return true;
        }

        var walker = TreeWalker.RawViewWalker;
        var current = element;
        for (var depth = 0; depth < 10; depth++)
        {
            AutomationElement? parent;
            try
            {
                parent = walker.GetParent(current);
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

            if (parent is null)
            {
                return false;
            }

            var parentContext =
                $"{GetStringProperty(parent, AutomationElement.NameProperty)} {GetStringProperty(parent, AutomationElement.AutomationIdProperty)} {GetStringProperty(parent, AutomationElement.ClassNameProperty)}";
            if (ContainsAny(parentContext, ChatContextFragments))
            {
                return true;
            }

            current = parent;
        }

        return false;
    }

    private static bool HasConfirmationPromptContext(AutomationElement element)
    {
        var walker = TreeWalker.RawViewWalker;
        var current = element;

        for (var depth = 0; depth < 6; depth++)
        {
            var context = string.Join(
                " ",
                GetStringProperty(current, AutomationElement.NameProperty),
                GetStringProperty(current, AutomationElement.AutomationIdProperty),
                GetStringProperty(current, AutomationElement.ClassNameProperty),
                GetStringProperty(current, AutomationElement.HelpTextProperty));

            if (ContainsAny(context, ConfirmationContextFragments))
            {
                return true;
            }

            AutomationElement? parent;
            try
            {
                parent = walker.GetParent(current);
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

            if (parent is null)
            {
                return false;
            }

            current = parent;
        }

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
}
