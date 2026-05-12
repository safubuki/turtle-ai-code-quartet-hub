using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class VscodeChatUiStatusReader
{
    private const int MaxElementsToInspect = 2400;
    private const int MaxElementsAfterRunningSignal = 180;
    private const int MaxTextLengthForStatus = 48;
    private const int MaxTextLengthForConfirmation = 140;
    // 走査予算: 4 スロット × 700ms × 750ms 間隔 ≒ 3 秒で 1 周。
    // chat-response-loading は UI tree の深い位置にあり、500ms では届かないスロットがある。
    private static readonly TimeSpan MaxScanDuration = TimeSpan.FromMilliseconds(700);
    private static readonly char[] StatusPrefixTrimChars = { '•', '·', '・', '●', '○', '◯', '◦', '→', '*', '-', ' ', '\t', '　' };

    // 「○○中」末尾の日本語のみを残す。英語の汎用語 (Working/Running/Loading/Generating 等) は
    // VS Code ステータスバーや拡張機能のロード表示等に頻出するため除外。
    // どのテキストも IsCurrentStatusText 内で必ずチャットコンテキスト要求 (誤検出の最終ガード)。
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
        "読み込み中",
        "読込中",
        "読込み中",
        "確認中",
        "レビュー中",
        "編集中",
        "調査中",
        "計算中",
        "解析中",
        "応答中",
        "回答中",
        "出力中",
        "ストリーミング中",
        "処理を実行中",
        "ツール実行中"
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

    // Running 中に UI Tree に出現する class フラグメント。
    // 「ローディング表示」「思考中ボックス」「スピン中アイコン」など、
    // AI 応答中のみ表示される要素に付くクラスのみを列挙する。
    // chat-progress-reservable / chat-most-recent-response は応答完了後も残るため含めない。
    private static readonly string[] RunningClassFragments =
    [
        // VS Code Chat API 標準 (Copilot Chat 等)
        "chat-response-loading",
        "chat-thinking-box",
        "chat-progress-message",
        "interactive-progress",
        // codicon のスピン中修飾子 (回転中のスピナーアイコン = ロード中の汎用シグナル)
        "codicon-modifier-spin",
        // 他の AI 拡張で見られる可能性のあるパターン
        "loading-indicator",
        "thinking-indicator",
        "streaming-response"
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

    // チャット系 UI と判定するための context フラグメント (中間レベル)。
    // 拡張機能の UI Automation 上の class/automationId はマチマチなので、
    // 大きなブランド名 (chat/copilot/codex/claude/anthropic/chatgpt) も入れる。
    // ただし汎用語 (agent / codicon / action-label / interactive 単独) は誤検出多発のため除外。
    private static readonly string[] ChatContextFragments =
    [
        "chat",
        "chat-widget",
        "chat-view",
        "copilot",
        "copilot-chat",
        "codex",
        "claude",
        "claude-code",
        "chatgpt",
        "openai.chatgpt",
        "anthropic",
        "interactive-session"
    ];

    // 単独 2〜3 文字動詞 (「処理」「実行」等) は誤検出に弱いため、より確実なチャット拡張固有の
    // フラグメントを含む要素のみで Running 認定する。
    private static readonly string[] StrictChatContextFragments =
    [
        "chat-widget",
        "chat-view",
        "copilot-chat",
        "claude-code",
        "chatgpt",
        "openai.chatgpt",
        "anthropic",
        "interactive-session"
    ];

    // 単独 2〜3 文字の動詞テキスト。短いため誤検出多発でき、strict context 要求。
    private static readonly string[] RunningStatusShortVerbTexts =
    [
        "処理",
        "実行",
        "思考",
        "分析",
        "解析",
        "応答",
        "回答",
        "出力",
        "生成",
        "検索",
        "読込",
        "読み込み"
    ];

    // Stop ボタンの判定クラス。AI チャット拡張で「中断」を表す codicon に限定する。
    // `codicon-debug-stop` は VS Code 本体のデバッグツールバーに常時存在するため除外。
    // `codicon-circle-slash` は「無効化」表示で AI 実行と無関係なため除外。
    private static readonly string[] StopClassFragments =
    [
        "codicon-stop",
        "codicon-stop-circle"
    ];

    private static readonly string[] ConfirmationActionTexts =
    [
        "Continue",
        "続行",
        "Allow",
        "Accept",
        "許可"
    ];

    private static readonly string[] ContextualConfirmationActionTexts =
    [
        "Approve",
        "Deny",
        "Reject",
        "承認"
    ];

    private static readonly string[] ConfirmationExclusionFragments =
    [
        "スクリーン リーダー",
        "screen reader",
        "editor.accessibilitySupport"
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
        string confirmationEvidenceText = string.Empty;
        string confirmationEvidenceAutomationId = string.Empty;
        string confirmationEvidenceClassName = string.Empty;
        string? confirmationDetail = null;

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
            // Fix E: タイムアウトチェックをCOM呼び出しの前に移動して予算超過を防止
            if (stopwatch.Elapsed >= MaxScanDuration)
            {
                timedOut = true;
                break;
            }

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
                if (!foundConfirmationButton)
                {
                    foundConfirmationButton = true;
                    confirmationDetail = confirmDetail;
                    confirmationEvidenceText = TrimForDetail(snapshot.Name);
                    confirmationEvidenceAutomationId = snapshot.AutomationId;
                    confirmationEvidenceClassName = snapshot.ClassName;
                }
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
        }

        if (runningDetail is not null)
        {
            var rejectedReason = foundConfirmationButton
                ? $"running evidence exists; confirmation candidate: [{confirmationEvidenceText}]"
                : string.Empty;
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
                evidenceClassName,
                confirmationEvidenceText,
                confirmationEvidenceAutomationId,
                confirmationEvidenceClassName,
                confirmationAccepted: false,
                confirmationRejectedReason: rejectedReason);
        }

        if (foundConfirmationButton && confirmationDetail is not null)
        {
            return CreateProbeResult(
                AiStatus.WaitingForConfirmation,
                DateTimeOffset.Now,
                timedOut,
                false,
                inspected,
                confirmationDetail,
                foundRunningText,
                foundRunningClass,
                foundStopButton,
                foundConfirmationButton,
                foundInputBox,
                foundInputReady,
                foundSendButton,
                foundDisabledSendButton,
                confirmationEvidenceText,
                confirmationEvidenceAutomationId,
                confirmationEvidenceClassName,
                confirmationEvidenceText,
                confirmationEvidenceAutomationId,
                confirmationEvidenceClassName,
                confirmationAccepted: true,
                confirmationRejectedReason: string.Empty);
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
        string evidenceClassName,
        string confirmationEvidenceText = "",
        string confirmationEvidenceAutomationId = "",
        string confirmationEvidenceClassName = "",
        bool confirmationAccepted = false,
        string confirmationRejectedReason = "")
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
            evidenceClassName,
            confirmationEvidenceText,
            confirmationEvidenceAutomationId,
            confirmationEvidenceClassName,
            confirmationAccepted,
            confirmationRejectedReason);
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
        // コード本文・テキストエディタ・リンク等の中身は Running 判定から除外。
        // これらは Welcome ページのタスク一覧やコードコメントを誤検出する温床。
        if (IsExcludedControlTypeForRunningText(element.ControlType))
        {
            detail = string.Empty;
            return false;
        }

        if (element.IsVisible && IsCurrentStatusText(element.Name, GetCombinedContext(element), hasChatContext))
        {
            detail = $"VS Code UI: {element.Name} を検出しました。";
            return true;
        }

        detail = string.Empty;
        return false;
    }

    private static bool IsExcludedControlTypeForRunningText(string controlType)
    {
        // Document/Edit はテキストエディタの中身、Hyperlink は埋め込みリンクで、
        // Running 状態表示には使われない。これらの中の「○○中」テキストは無視する。
        return string.Equals(controlType, "ControlType.Document", StringComparison.OrdinalIgnoreCase)
            || string.Equals(controlType, "ControlType.Edit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(controlType, "ControlType.Hyperlink", StringComparison.OrdinalIgnoreCase);
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
        // テキスト名 (「キャンセル」「Stop」等) ベースの判定は VS Code のダイアログ/ポップアップ
        // 共通ボタンと衝突するため廃止。codicon クラスのみで判定する。
        if (element.IsVisible
            && element.IsEnabled
            && hasChatContext
            && ContainsAny(element.ClassName, StopClassFragments))
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

        // プレフィックス装飾記号 (• ・ ● → 等) を除いてから判定する。
        // 末尾の記号 (. …) も除く。
        var normalized = text.TrimStart(StatusPrefixTrimChars).TrimEnd('.', '…', ' ', '\t', '　').Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        if (normalized.StartsWith("Thinking Effort", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hasChatContextFinal = hasChatContext || ContainsAny(combinedContext, ChatContextFragments);
        if (!hasChatContextFinal)
        {
            return false;
        }

        // 「○○中」exact (作業中, 実行中, 読み込み中 等) - 中間 chat context で OK
        var exactMatch = RunningStatusExactTexts.Any(signal => string.Equals(normalized, signal, StringComparison.OrdinalIgnoreCase));
        if (exactMatch)
        {
            return true;
        }

        // 単独 2〜3 文字動詞 (処理, 実行, 思考, 分析 等)。
        // チャット拡張内に常時表示される単語ではないため、中間 chat context で許可する。
        var shortVerbMatch = RunningStatusShortVerbTexts.Any(signal => string.Equals(normalized, signal, StringComparison.OrdinalIgnoreCase));
        if (shortVerbMatch)
        {
            return true;
        }

        // 「○○中」suffix マッチ (任意の動詞 + 中)。状態表示用の簡潔なテキストのみを通す。
        //   - 長さ 3〜8 文字 (情報文や説明テキストに含まれる「○○中」を弾く)
        //   - すべてが日本語文字 (ひらがな/カタカナ/漢字) であること。数字や句読点を含む文は弾く。
        //     例: 「最近のタスク。0件が進行中」のような説明文が誤検出されないようにする。
        //   - 「中央」「中止」「待機中」「停止中」「使用中」など状態表示でない単語は除外。
        if (normalized.EndsWith('中')
            && normalized.Length is >= 3 and <= 8
            && IsAllJapaneseChars(normalized)
            && !IsExcludedSuffixForChu(normalized))
        {
            return true;
        }

        var prefixMatch = RunningStatusPrefixes.Any(signal => normalized.StartsWith(signal, StringComparison.OrdinalIgnoreCase));
        return prefixMatch;
    }

    private static bool IsExcludedSuffixForChu(string text)
    {
        // 末尾「中」だが状態表示ではない単語を除外する。
        return text.Equals("中", StringComparison.Ordinal)
            || text.Equals("待機中", StringComparison.Ordinal)
            || text.Equals("停止中", StringComparison.Ordinal)
            || text.Equals("休止中", StringComparison.Ordinal)
            || text.Equals("非実行中", StringComparison.Ordinal)
            || text.Equals("使用中", StringComparison.Ordinal)
            || text.Equals("選択中", StringComparison.Ordinal)
            || text.Equals("展開中", StringComparison.Ordinal);
    }

    private static bool IsAllJapaneseChars(string text)
    {
        foreach (var ch in text)
        {
            if (!IsJapaneseChar(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsJapaneseChar(char ch)
    {
        // ひらがな (U+3040 - U+309F)
        if (ch is >= '぀' and <= 'ゟ') return true;
        // カタカナ + 半濁点/濁点 (U+30A0 - U+30FF)
        if (ch is >= '゠' and <= 'ヿ') return true;
        // CJK 統合漢字 (U+4E00 - U+9FFF)
        if (ch is >= '一' and <= '鿿') return true;
        // CJK 統合漢字 拡張A (U+3400 - U+4DBF)
        if (ch is >= '㐀' and <= '䶿') return true;
        return false;
    }

    private static bool ContainsStopAction(string value)
    {
        return StopActionTexts.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadConfirmationSignal(ElementSnapshot element, bool hasChatContext, out string detail)
    {
        if (!element.IsVisible || !element.IsEnabled)
        {
            detail = string.Empty;
            return false;
        }

        // Exclude VS Code general UI (screen reader notifications, accessibility prompts, etc.)
        var combinedContext = GetCombinedContext(element);
        if (ContainsAny(element.Name, ConfirmationExclusionFragments)
            || ContainsAny(combinedContext, ConfirmationExclusionFragments))
        {
            detail = string.Empty;
            return false;
        }

        // All confirmation signals require chat context (inherited from parent or element itself)
        if (!hasChatContext)
        {
            detail = string.Empty;
            return false;
        }

        if (!IsConfirmationActionName(element.Name, out _))
        {
            detail = string.Empty;
            return false;
        }

        detail = string.IsNullOrWhiteSpace(element.Name)
            ? "VS Code UI: チャット確認ボタンを検出しました。"
            : $"VS Code UI: {TrimForDetail(element.Name)} を検出しました。";
        return true;
    }

    private static bool IsConfirmationActionName(string value, out bool requiresContext)
    {
        requiresContext = true; // All confirmation texts require chat context
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

        return ConfirmationActionTexts.Concat(ContextualConfirmationActionTexts).Any(signal =>
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
    string EvidenceClassName,
    string ConfirmationEvidenceText,
    string ConfirmationEvidenceAutomationId,
    string ConfirmationEvidenceClassName,
    bool ConfirmationAccepted,
    string ConfirmationRejectedReason)
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
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            string.Empty);
    }

    public AiStatusSnapshot? ToSnapshot(string sourceName = "UI Automation")
    {
        return Status.HasValue
            ? new AiStatusSnapshot(Status.Value, Detail, EvidenceAt, sourceName)
            : null;
    }
}
