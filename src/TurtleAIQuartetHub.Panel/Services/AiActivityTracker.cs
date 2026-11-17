using System.IO;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public enum AiHookEventKind
{
    Unknown,
    Start,
    Stop,
    Interrupt,

    // 作業がまだ続いている証拠（PostToolUse / 回復可能な errorOccurred）。表示は作業中のまま。
    Progress,

    // ツールの実行を始めた、または利用者の許可・回答を待っている（PreToolUse / 許可の確認）。
    // コマンドの実行や許可待ちで長く静かになりうる。表示は作業中のまま。
    ToolRunning
}

public sealed record AiHookEvent(string Tool, string EventName, AiHookEventKind Kind, string Cwd, string SessionId, string Detail = "");

public static class AiHookEventParser
{
    public const string CommandName = "--ai-event";

    // パイプで届く引数: ["--ai-event", tool, event, cwd, session, detail]
    public static AiHookEvent? TryParse(IReadOnlyList<string> args)
    {
        if (args.Count < 4 || !string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var tool = args[1].Trim().ToLowerInvariant();
        var eventName = args[2].Trim();
        var cwd = args[3].Trim();
        var session = args.Count > 4 ? args[4].Trim() : string.Empty;
        var detail = args.Count > 5 ? args[5].Trim() : string.Empty;
        if (tool.Length == 0 || eventName.Length == 0 || cwd.Length == 0)
        {
            return null;
        }

        return new AiHookEvent(tool, eventName, Classify(eventName, detail), cwd, session, detail);
    }

    public static AiHookEventKind Classify(string eventName, string detail)
    {
        switch (eventName.Trim().ToLowerInvariant())
        {
            case "userpromptsubmit":
            case "userpromptsubmitted":
            case "preinvocation":
                // Antigravity は毎回のモデル呼び出し前に PreInvocation を送る。作業中への遷移はべき等に保つ。
                return AiHookEventKind.Start;
            case "stop":
            case "agentstop":
                // Antigravity の USER_CANCELED / cancel 等や Copilot の abort / user_exit は
                // ユーザー自身による中断。偽の完了（青）を残さず、作業中の緑だけを止める。
                return IsCancellationReason(detail)
                    ? AiHookEventKind.Interrupt
                    : AiHookEventKind.Stop;
            case "stopfailure":
            case "stopcancelled":
                // Claude Code / Grok は失敗・中断を Stop 以外のイベントで知らせる。
                // 作業中の緑が 60 分残らないよう同じ完了表示へ移す。
                return AiHookEventKind.Stop;
            case "erroroccurred":
                // Copilot CLI の回復可能なツールエラーはターン継続中なので作業が続いている証拠として扱い、
                // 非回復の失敗だけが作業中を終了して完了（青）で知らせる。
                return detail.Trim().Equals("recoverable", StringComparison.OrdinalIgnoreCase)
                    ? AiHookEventKind.Progress
                    : AiHookEventKind.Stop;
            case "pretooluse":
                // Copilot / Codex / Claude Code は停止ボタンや Esc で中断しても終了系のフックを送らない。
                // ツールの前後の知らせで「まだ動いている」ことを確かめ、途絶えたら作業中を片付ける。
                return AiHookEventKind.ToolRunning;
            case "posttooluse":
                return AiHookEventKind.Progress;
            case "permissionrequest":
                // Codex の許可確認は作業の途中。開始済みの緑の周回を続け、青い光にはしない。
                // 利用者の操作待ちで静かになるので、長めに待つ。
                return AiHookEventKind.ToolRunning;
            case "notification":
                // permission_prompt / elicitation_dialog は許可や質問の確認で、作業の途中。表示は作業中のまま、
                // 利用者の操作待ちで静かになるので長めに待つ。
                // idle_prompt は入力待ち（AI はもう動いていない）。Esc / Ctrl+C の中断では Stop が来ないため、
                // 残った作業中だけを光らせずに止める。完了の光は付けない（消した光を戻さない）。
                return detail.Trim().ToLowerInvariant() switch
                {
                    "permission_prompt" or "elicitation_dialog" => AiHookEventKind.ToolRunning,
                    "idle_prompt" => AiHookEventKind.Interrupt,
                    _ => AiHookEventKind.Unknown
                };
            case "interrupt":
            case "sessionend":
                return AiHookEventKind.Interrupt;
            default:
                return AiHookEventKind.Unknown;
        }
    }

    private static bool IsCancellationReason(string detail)
    {
        return detail.Trim().ToLowerInvariant() switch
        {
            "user_canceled"
            or "user_cancelled"
            or "cancel"
            or "canceled"
            or "cancelled"
            or "abort"
            or "aborted"
            or "user_exit"
            or "interrupted" => true,
            _ => false
        };
    }
}

// スロット 1 つ分の AI 状態。作業中のセッションと、まだ見ていない完了を別々に持つ。
public sealed class AiActivityTracker
{
    // 中断などで完了通知が来ないまま残ったセッションを片付けるまでの時間。
    public static readonly TimeSpan StaleSessionTimeout = TimeSpan.FromMinutes(60);

    // GitHub Copilot（CLI / VS Code のチャット）・Codex・Claude Code は、停止ボタンや Esc で中断しても終了系のフックを送らない。
    // 作業中はツールの前後（PreToolUse / PostToolUse）で知らせが続くので、それが途絶えたら止まったとみなす。
    // ツールの実行後（モデルの考え中）に知らせが途絶えた時間。
    public static readonly TimeSpan QuietSessionTimeout = TimeSpan.FromMinutes(3);

    // ツールの実行中（コマンドの実行や、利用者の許可待ち）に知らせが途絶えた時間。長いビルドなどで誤って消さない。
    public static readonly TimeSpan QuietToolTimeout = TimeSpan.FromMinutes(10);

    // Antigravity はモデルを呼ぶたびに PreInvocation（開始）が届くが、ツール実行中かどうかは分からない。
    // 中断の Stop が届かない版もあるため、長めの途絶えで片付ける。
    public static readonly TimeSpan AntigravityQuietTimeout = TimeSpan.FromMinutes(10);

    // 知らせの途絶えで片付ける対象のツールと、（考え中, ツール実行中）の待ち時間。
    // Grok は中断を StopCancelled で必ず知らせるので対象外（60 分の片付けだけ）。
    private static readonly Dictionary<string, (TimeSpan Quiet, TimeSpan ToolRunning)> QuietTimeouts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["copilot"] = (QuietSessionTimeout, QuietToolTimeout),
        ["codex"] = (QuietSessionTimeout, QuietToolTimeout),
        ["claude"] = (QuietSessionTimeout, QuietToolTimeout),
        ["antigravity"] = (AntigravityQuietTimeout, AntigravityQuietTimeout)
    };

    private readonly Dictionary<string, ActiveSession> _activeSessions = new(StringComparer.OrdinalIgnoreCase);

    // 知らせの途絶えで片付けたセッション。止まったと見なしたあとに作業の知らせが来たら（長い考え中だった）作業中へ戻す。
    private readonly Dictionary<string, ActiveSession> _quietEndedSessions = new(StringComparer.OrdinalIgnoreCase);
    private bool _needsAttention;

    // 終了の知らせが来ないまま時間切れで作業中を片付けた。まだ見ていない黄色の知らせ。
    private bool _timedOut;

    public AiActivityState State => _needsAttention
        ? AiActivityState.Done
        : _timedOut
            ? AiActivityState.TimedOut
            : _activeSessions.Count > 0 ? AiActivityState.Working : AiActivityState.Idle;

    public string LastTool { get; private set; } = string.Empty;

    // フォーカス表示中（そのウィンドウを見ている最中）に終わっても、終わったことに気づけるよう必ず光らせる。
    public bool Apply(AiHookEvent hookEvent, DateTimeOffset now)
    {
        var before = State;
        var key = string.IsNullOrEmpty(hookEvent.SessionId) ? hookEvent.Tool : $"{hookEvent.Tool}:{hookEvent.SessionId}";
        switch (hookEvent.Kind)
        {
            case AiHookEventKind.Start:
                // 同一ツールの別セッションが中断未通知のまま残っていると、後続完了の解除時に
                // 緑の作業中へ戻ってしまうため、同ツールの古いセッションは置き換える。
                // 異なるツール（例: Claude と Copilot）は並行動作として残す。
                RemoveSessionsForTool(_activeSessions, hookEvent.Tool, exceptKey: key);
                RemoveSessionsForTool(_quietEndedSessions, hookEvent.Tool);
                _activeSessions[key] = new ActiveSession(hookEvent.Tool, now);
                // 新しく指示を出した＝そのウィンドウを見ている。前の完了・時間切れの光は役目を終えている。
                _needsAttention = false;
                _timedOut = false;
                break;
            case AiHookEventKind.Stop:
                // 開始で同ツールのセッションは 1 つに置き換わっている。完了・中断の SessionId が開始と違っても
                // （ツールが付け直す・空で届くなど）作業中が残らないよう、同ツールの作業中をまとめて止める。
                RemoveSessionsForTool(_activeSessions, hookEvent.Tool);
                RemoveSessionsForTool(_quietEndedSessions, hookEvent.Tool);
                // 時間切れのあとに完了が届いた＝通知は遅れただけ。青の完了で知らせる。
                _needsAttention = true;
                _timedOut = false;
                break;
            case AiHookEventKind.Interrupt:
                RemoveSessionsForTool(_activeSessions, hookEvent.Tool);
                RemoveSessionsForTool(_quietEndedSessions, hookEvent.Tool);
                // 中断の知らせが届いた＝止まった理由が分かった。時間切れの黄色は出さない。
                _timedOut = false;
                break;
            case AiHookEventKind.Progress:
            case AiHookEventKind.ToolRunning:
                if (!RecordActivity(hookEvent.Tool, hookEvent.Kind == AiHookEventKind.ToolRunning, now))
                {
                    // 完了・中断のあとや、開始を知らないセッションの知らせでは作業中にしない。
                    return false;
                }

                break;
            default:
                return false;
        }

        LastTool = hookEvent.Tool;
        return before != State;
    }

    // 作業中のセッションの「最後に動いた時刻」を進める。知らせの途絶えで片付けたばかりなら作業中へ戻す。
    private bool RecordActivity(string tool, bool toolRunning, DateTimeOffset now)
    {
        var keys = FindSessionKeysForTool(_activeSessions, tool);
        if (keys.Count == 0)
        {
            keys = FindSessionKeysForTool(_quietEndedSessions, tool);
            if (keys.Count == 0)
            {
                return false;
            }

            foreach (var quietKey in keys)
            {
                _activeSessions[quietKey] = _quietEndedSessions[quietKey];
                _quietEndedSessions.Remove(quietKey);
            }

            // 長く考えていただけで、まだ動いていた。時間切れの黄色を取り消して作業中へ戻す。
            _timedOut = false;
        }

        foreach (var activeKey in keys)
        {
            var session = _activeSessions[activeKey];
            session.LastActivityAt = now;
            session.ToolRunning = toolRunning;
        }

        return true;
    }

    private static List<string> FindSessionKeysForTool(Dictionary<string, ActiveSession> sessions, string tool)
    {
        if (string.IsNullOrEmpty(tool))
        {
            return [];
        }

        var prefix = tool + ":";
        return sessions.Keys
            .Where(existingKey => string.Equals(existingKey, tool, StringComparison.OrdinalIgnoreCase)
                || existingKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static void RemoveSessionsForTool(Dictionary<string, ActiveSession> sessions, string tool, string? exceptKey = null)
    {
        foreach (var existingKey in FindSessionKeysForTool(sessions, tool))
        {
            if (exceptKey is not null && string.Equals(existingKey, exceptKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sessions.Remove(existingKey);
        }
    }

    // パネルでフォーカス表示を切り替えた（1 面フォーカスにした／4 面表示へ戻した）ので完了の光を消す。
    // 作業中のセッションが残っていれば作業中へ戻る。
    public bool Acknowledge()
    {
        if (!_needsAttention && !_timedOut)
        {
            return false;
        }

        _needsAttention = false;
        _timedOut = false;
        return true;
    }

    public bool PruneStale(DateTimeOffset now)
    {
        var before = State;
        foreach (var (key, session) in _activeSessions.ToList())
        {
            if (now - session.LastActivityAt <= GetTimeout(session))
            {
                continue;
            }

            _activeSessions.Remove(key);
            // 終了の知らせが来ないまま止まった。停止ボタンの中断か通知の取りこぼしかは分からないので、黄色で知らせる。
            _timedOut = true;
            LastTool = session.Tool;
            if (QuietTimeouts.ContainsKey(session.Tool))
            {
                // 停止ボタンでの中断とみなして光らせずに止める。長い考え中だっただけなら、次の知らせで作業中へ戻す。
                _quietEndedSessions[key] = session;
            }
        }

        foreach (var (key, session) in _quietEndedSessions.ToList())
        {
            if (now - session.LastActivityAt > StaleSessionTimeout)
            {
                _quietEndedSessions.Remove(key);
            }
        }

        return before != State;
    }

    // 利用者がカードの停止ボタンで作業中の表示を止めた。停止ボタンでは終了の知らせが来ないツールがあるため。
    // 作業中のときだけ働く。止めたあとに届く作業の知らせでは戻さず、次の指示（開始）で作業中になる。
    // AI の処理そのものは止めない。
    public bool StopWorking()
    {
        if (State != AiActivityState.Working)
        {
            return false;
        }

        _activeSessions.Clear();
        _quietEndedSessions.Clear();
        return true;
    }

    public bool Reset()
    {
        var before = State;
        _activeSessions.Clear();
        _quietEndedSessions.Clear();
        _needsAttention = false;
        _timedOut = false;
        return before != State;
    }

    private static TimeSpan GetTimeout(ActiveSession session)
    {
        if (!QuietTimeouts.TryGetValue(session.Tool, out var timeouts))
        {
            return StaleSessionTimeout;
        }

        return session.ToolRunning ? timeouts.ToolRunning : timeouts.Quiet;
    }

    private sealed class ActiveSession(string tool, DateTimeOffset lastActivityAt)
    {
        public string Tool { get; } = tool;

        public DateTimeOffset LastActivityAt { get; set; } = lastActivityAt;

        public bool ToolRunning { get; set; }
    }
}

public static class AiActivitySlotMatcher
{
    // cwd がスロットのワークスペースと同じか、その配下なら対象。最も深く一致したスロットだけを選ぶ。
    public static IReadOnlyList<T> FindTargets<T>(IEnumerable<T> items, Func<T, IEnumerable<string>> getPaths, string cwd)
    {
        var normalizedCwd = NormalizeLocalPath(cwd);
        if (normalizedCwd.Length == 0)
        {
            return [];
        }

        var best = new List<T>();
        var bestLength = 0;
        foreach (var item in items)
        {
            var length = getPaths(item)
                .Select(NormalizeLocalPath)
                .Where(path => path.Length > 0 && IsSameOrUnder(normalizedCwd, path))
                .Select(path => path.Length)
                .DefaultIfEmpty(0)
                .Max();
            if (length == 0 || length < bestLength)
            {
                continue;
            }

            if (length > bestLength)
            {
                best.Clear();
                bestLength = length;
            }

            best.Add(item);
        }

        return best;
    }

    public static string NormalizeLocalPath(string? path)
    {
        var value = (path ?? string.Empty).Trim().Trim('"').Replace('/', '\\');
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            value = value[4..];
        }

        // 「/c/git_home/...」（Git Bash 形式）は C:\git_home\... と同じ場所。
        if (value.Length >= 3 && value[0] == '\\' && char.IsLetter(value[1]) && value[2] == '\\')
        {
            value = $"{value[1]}:{value[2..]}";
        }

        if (value.Length < 3 || !System.IO.Path.IsPathFullyQualified(value))
        {
            return string.Empty;
        }

        try
        {
            value = System.IO.Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }

        var root = System.IO.Path.GetPathRoot(value) ?? string.Empty;
        return value.Length > root.Length ? value.TrimEnd('\\') : value;
    }

    private static bool IsSameOrUnder(string cwd, string root)
    {
        if (string.Equals(cwd, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = root.EndsWith('\\') ? root : root + "\\";
        return cwd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
