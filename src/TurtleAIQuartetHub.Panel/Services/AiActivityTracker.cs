using System.IO;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public enum AiHookEventKind
{
    Unknown,
    Start,
    Stop,
    Interrupt
}

public sealed record AiHookEvent(string Tool, string EventName, AiHookEventKind Kind, string Cwd, string SessionId);

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

        return new AiHookEvent(tool, eventName, Classify(eventName, detail), cwd, session);
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
                // Antigravity の USER_CANCELED はユーザー自身による中断。偽の完了（水色）を残さず、
                // 確認済みの中断として緑の明滅だけを止める。
                return detail.Trim().Equals("user_canceled", StringComparison.OrdinalIgnoreCase)
                    ? AiHookEventKind.Interrupt
                    : AiHookEventKind.Stop;
            case "agentstop":
            case "stopfailure":
            case "stopcancelled":
                // Grok は完了・API エラー・中断を別イベントで知らせる。どれも同じ完了の光にし、
                // 中断や失敗のあと作業中の緑が 60 分残らないようにする。
                return AiHookEventKind.Stop;
            case "permissionrequest":
                // Codex の許可確認は作業の途中。開始済みの緑の明滅を続け、青い光にはしない。
                return AiHookEventKind.Unknown;
            case "notification":
                // permission_prompt / elicitation_dialog は許可や質問の確認で、作業の途中。
                // idle_prompt は応答のあとの入力待ちで、消した完了の光を戻してしまう。どちらも表示を変えない。
                return detail.Trim().ToLowerInvariant() switch
                {
                    _ => AiHookEventKind.Unknown
                };
            case "interrupt":
                return AiHookEventKind.Interrupt;
            default:
                return AiHookEventKind.Unknown;
        }
    }
}

// スロット 1 つ分の AI 状態。作業中のセッションと、まだ見ていない完了を別々に持つ。
public sealed class AiActivityTracker
{
    // 中断などで完了通知が来ないまま残ったセッションを片付けるまでの時間。
    public static readonly TimeSpan StaleSessionTimeout = TimeSpan.FromMinutes(60);

    private readonly Dictionary<string, DateTimeOffset> _activeSessions = new(StringComparer.OrdinalIgnoreCase);
    private bool _needsAttention;

    public AiActivityState State => _needsAttention
        ? AiActivityState.Done
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
                _activeSessions[key] = now;
                // 新しく指示を出した＝そのウィンドウを見ている。前の完了の光は役目を終えている。
                _needsAttention = false;
                break;
            case AiHookEventKind.Stop:
                _activeSessions.Remove(key);
                _needsAttention = true;
                break;
            case AiHookEventKind.Interrupt:
                _activeSessions.Remove(key);
                break;
            default:
                return false;
        }

        LastTool = hookEvent.Tool;
        return before != State;
    }

    // パネルでフォーカス表示を切り替えた（1 面フォーカスにした／4 面表示へ戻した）ので完了の光を消す。
    // 作業中のセッションが残っていれば作業中へ戻る。
    public bool Acknowledge()
    {
        if (!_needsAttention)
        {
            return false;
        }

        _needsAttention = false;
        return true;
    }

    public bool PruneStale(DateTimeOffset now)
    {
        var before = State;
        foreach (var key in _activeSessions.Where(pair => now - pair.Value > StaleSessionTimeout).Select(pair => pair.Key).ToList())
        {
            _activeSessions.Remove(key);
        }

        return before != State;
    }

    public bool Reset()
    {
        var before = State;
        _activeSessions.Clear();
        _needsAttention = false;
        return before != State;
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
