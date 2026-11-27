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
                // 作業中の緑が残らないよう同じ完了表示へ移す。
                return AiHookEventKind.Stop;
            case "erroroccurred":
                // Copilot CLI の回復可能なツールエラーはターン継続中のため無視し、
                // 非回復の失敗だけが作業中を終了して完了（青）で知らせる。
                return detail.Trim().Equals("recoverable", StringComparison.OrdinalIgnoreCase)
                    ? AiHookEventKind.Unknown
                    : AiHookEventKind.Stop;
            case "permissionrequest":
                // Codex の許可確認は作業の途中。開始済みの緑の周回を続け、青い光にはしない。
                return AiHookEventKind.Unknown;
            case "notification":
                // permission_prompt / elicitation_dialog は許可や質問の確認で、作業の途中。表示を変えない。
                // idle_prompt は入力待ち（AI はもう動いていない）。Esc / Ctrl+C の中断では Stop が来ないため、
                // 残った作業中だけを光らせずに止める。完了の光は付けない（消した光を戻さない）。
                return detail.Trim().ToLowerInvariant() switch
                {
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
// 作業中は時間では片付けない（長い処理を途中で消さない）。終了・中断の知らせ、次の指示、
// カードの停止ボタン、ウィンドウを閉じる、手動リセットのいずれかで終わる。
//
// 同じウィンドウ・同じツールで複数の会話（セッション）を動かしたとき:
//   緑の作業中は「そのウィンドウで最後に指示を出したセッション」が終わるまで続ける。
//   それより前のセッションは「置き換え済み」として覚えておき、そのセッション自身の完了が届いたら
//   （＝実際に並行して動いていた証拠）、完了の青で知らせつつ、最後のセッションの作業中は消さない（確認すると緑に戻る）。
//   逆に最後のセッションが先に終わったときは、前のセッションが「まだ動いている」のか「停止ボタンで通知なしに止められた」のかを
//   フックからは区別できないため、作業中へは戻さない（「動いていないのに実行中のまま」を再発させないため）。
//   前のセッションがあとで終わったときは、その完了もまた青で知らせる。
public sealed class AiActivityTracker
{
    // 置き換え済みセッションを覚えておく上限。通知なしに止められた会話の ID がたまり続けないようにする。
    private const int MaxSupersededSessions = 8;

    private readonly HashSet<string> _activeSessions = new(StringComparer.OrdinalIgnoreCase);
    // 作業中・置き換え済みの各セッションが指示を受けた時刻（置き換え済みセッションが終わったときの、かかった時間に使う）。
    private readonly Dictionary<string, DateTimeOffset> _sessionStartedAt = new(StringComparer.OrdinalIgnoreCase);
    // 同じツールの新しいセッションの開始で作業中から外したセッション（古い順）。どちらも SessionId 付きのときだけ覚える。
    private readonly List<string> _supersededSessions = [];
    private bool _needsAttention;

    public AiActivityState State => _needsAttention
        ? AiActivityState.Done
        : _activeSessions.Count > 0 ? AiActivityState.Working : AiActivityState.Idle;

    // 完了の青を確認したあと、まだ作業中の表示（緑）に戻るセッションが残っているか。ツールチップの案内に使う。
    public bool HasWorkingSessions => _activeSessions.Count > 0;

    public string LastTool { get; private set; } = string.Empty;

    // 作業中になった時刻（経過時間の表示用）。表示だけに使い、時間で状態を変えることはしない。
    // 待機・完了から作業中になったとき、または利用者が新しい指示を送ったときに数え始める。
    // Antigravity はモデルを呼ぶたびに開始（PreInvocation）を送るため、それだけでは数え直さない。
    public DateTimeOffset? WorkingSince { get; private set; }

    // まだ見ていない完了（青）の時刻。ツールチップに出し、カルテットハブを再起動しても完了を戻せるよう保存する。
    // 古さは時間で判断しない（時刻を見せて利用者が判断する）。
    public DateTimeOffset? CompletedAt { get; private set; }

    // 完了したタスクにかかった時間（作業中になってから完了まで）。完了の青と一緒に、止まった時計として見せ続ける。
    public TimeSpan? CompletedDuration { get; private set; }

    // フォーカス表示中（そのウィンドウを見ている最中）に終わっても、終わったことに気づけるよう必ず光らせる。
    public bool Apply(AiHookEvent hookEvent, DateTimeOffset now)
    {
        var before = State;
        var hasSessionId = !string.IsNullOrEmpty(hookEvent.SessionId);
        var key = hasSessionId ? $"{hookEvent.Tool}:{hookEvent.SessionId}" : hookEvent.Tool;
        // 置き換え済みの前のセッション自身からの知らせか。そのセッションが実際に動いていた（または止まった）ことが
        // はっきりしているので、最後に指示を出した同じツールのセッションの作業中は消さない。
        var fromSupersededSession = hasSessionId
            && hookEvent.Kind is AiHookEventKind.Stop or AiHookEventKind.Interrupt
            && !_activeSessions.Contains(key)
            && !_activeSessions.Contains(hookEvent.Tool)
            && IsSuperseded(key);
        switch (hookEvent.Kind)
        {
            case AiHookEventKind.Start:
                if (_activeSessions.Count == 0 || WorkingSince is null || IsPromptSubmit(hookEvent.EventName))
                {
                    WorkingSince = now;
                }

                if (!_activeSessions.Contains(key) || IsPromptSubmit(hookEvent.EventName) || !_sessionStartedAt.ContainsKey(key))
                {
                    _sessionStartedAt[key] = now;
                }

                // 同一ツールの別セッションが中断未通知のまま残っていると、後続完了の解除時に
                // 緑の作業中へ戻ってしまうため、同ツールの古いセッションは作業中から外す（置き換え）。
                // 外したセッションは、そのセッション自身の完了・中断が届いたときに見分けられるよう覚えておく
                // （新旧どちらも SessionId 付きのときだけ。ID の無いツールは従来どおり置き換えるだけにする）。
                // 異なるツール（例: Claude と Copilot）は並行動作として残す。
                SupersedeSessionsForTool(hookEvent.Tool, key, rememberSuperseded: hasSessionId);
                ForgetSuperseded(key);
                _activeSessions.Add(key);
                // 新しく指示を出した＝そのウィンドウを見ている。前の完了の光は役目を終えている。
                _needsAttention = false;
                CompletedAt = null;
                CompletedDuration = null;
                break;
            case AiHookEventKind.Stop when fromSupersededSession:
                // 同じウィンドウで前に指示を出したセッションが、あとのセッションの作業中に終わった（並行して動いていた）。
                // 完了は青で知らせ、あとのセッションの作業中は残す（青を確認すると緑の作業中へ戻る）。
                // かかった時間は、終わったそのセッション自身の分にする。
                CompletedDuration = _sessionStartedAt.TryGetValue(key, out var supersededSince) && now >= supersededSince
                    ? now - supersededSince
                    : null;
                ForgetSuperseded(key);
                _needsAttention = true;
                CompletedAt = now;
                break;
            case AiHookEventKind.Stop:
                // 開始で同ツールのセッションは 1 つに置き換わっている。完了・中断の SessionId が開始と違っても
                // （ツールが付け直す・空で届くなど）作業中が残らないよう、同ツールの作業中をまとめて止める。
                // 作業中の時計を止めて、かかった時間を残す（数え始めを知らない完了では残さない）。
                // 置き換え済みのセッションは作業中へ戻さない（通知なしに止められた会話かもしれないため）。
                CompletedDuration = WorkingSince is { } since && now >= since ? now - since : null;
                ForgetSuperseded(key);
                RemoveSessionsForTool(hookEvent.Tool);
                _needsAttention = true;
                CompletedAt = now;
                break;
            case AiHookEventKind.Interrupt when fromSupersededSession:
                // 置き換え済みの前のセッション自身の中断・終了。あとのセッションの作業中は消さない。
                ForgetSuperseded(key);
                break;
            case AiHookEventKind.Interrupt:
                if (IsSessionEnd(hookEvent.EventName) && !string.IsNullOrEmpty(hookEvent.SessionId))
                {
                    // セッションの終了は、作業中として記録しているそのセッションのときだけ止める。
                    // VS Code 版 Codex は次の会話用に空の予備スレッドを先に作り、使わなければ後で終了する。
                    // その SessionEnd で、同じウィンドウで動いている別スレッドの作業中まで消さない。
                    // 開始が SessionId なしで届いていた場合（キーがツール名だけ）は、同じセッションとみなして止める。
                    _activeSessions.Remove(key);
                    _activeSessions.Remove(hookEvent.Tool);
                }
                else
                {
                    // Esc などの中断は、開始と違う SessionId や空で届いても作業中が残らないよう、同ツールをまとめて止める。
                    RemoveSessionsForTool(hookEvent.Tool);
                }

                ForgetSuperseded(key);
                break;
            default:
                return false;
        }

        LastTool = hookEvent.Tool;
        ClearWorkingSinceIfIdle();
        PruneSessionStarts();
        return before != State;
    }

    private bool IsSuperseded(string key) =>
        _supersededSessions.Exists(existingKey => string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase));

    private void ForgetSuperseded(string key) =>
        _supersededSessions.RemoveAll(existingKey => string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase));

    // 同じツールのほかのセッションを作業中から外す。新旧どちらも SessionId 付きなら、置き換え済みとして覚えておく。
    // SessionId の無い開始が来たツールは ID で見分けられないため、置き換え済みの記録も捨てて従来どおりにする。
    private void SupersedeSessionsForTool(string tool, string exceptKey, bool rememberSuperseded)
    {
        if (string.IsNullOrEmpty(tool))
        {
            return;
        }

        var prefix = tool + ":";
        var replaced = _activeSessions
            .Where(existingKey => !string.Equals(existingKey, exceptKey, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(existingKey, tool, StringComparison.OrdinalIgnoreCase)
                    || existingKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var existingKey in replaced)
        {
            _activeSessions.Remove(existingKey);
            if (rememberSuperseded && existingKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                ForgetSuperseded(existingKey);
                _supersededSessions.Add(existingKey);
            }
        }

        if (!rememberSuperseded)
        {
            _supersededSessions.RemoveAll(existingKey => existingKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        // 通知なしに止められた会話の ID がたまり続けないよう、古いものから捨てる。
        while (_supersededSessions.Count > MaxSupersededSessions)
        {
            _supersededSessions.RemoveAt(0);
        }
    }

    // 作業中でも置き換え済みでもなくなったセッションの開始時刻を捨てる。
    private void PruneSessionStarts()
    {
        if (_sessionStartedAt.Count == 0)
        {
            return;
        }

        foreach (var staleKey in _sessionStartedAt.Keys
            .Where(existingKey => !_activeSessions.Contains(existingKey) && !IsSuperseded(existingKey))
            .ToList())
        {
            _sessionStartedAt.Remove(staleKey);
        }
    }

    private static bool IsPromptSubmit(string eventName) =>
        eventName.Trim().ToLowerInvariant() is "userpromptsubmit" or "userpromptsubmitted";

    private static bool IsSessionEnd(string eventName) =>
        eventName.Trim().Equals("sessionend", StringComparison.OrdinalIgnoreCase);

    private void ClearWorkingSinceIfIdle()
    {
        if (_activeSessions.Count == 0)
        {
            WorkingSince = null;
        }
    }

    private void RemoveSessionsForTool(string tool, string? exceptKey = null)
    {
        if (string.IsNullOrEmpty(tool))
        {
            return;
        }

        var prefix = tool + ":";
        _activeSessions.RemoveWhere(existingKey =>
            (exceptKey is null || !string.Equals(existingKey, exceptKey, StringComparison.OrdinalIgnoreCase))
            && (string.Equals(existingKey, tool, StringComparison.OrdinalIgnoreCase)
                || existingKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
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
        CompletedAt = null;
        CompletedDuration = null;
        return true;
    }

    // カルテットハブの再起動前に保存しておいた、まだ見ていない完了を戻す。
    // 何も表示していないときだけ戻す（再起動後に届いた知らせを優先する）。
    public bool RestoreCompletion(string tool, DateTimeOffset completedAt, TimeSpan? duration = null)
    {
        if (State != AiActivityState.Idle || string.IsNullOrWhiteSpace(tool))
        {
            return false;
        }

        _needsAttention = true;
        CompletedAt = completedAt;
        CompletedDuration = duration is { } value && value >= TimeSpan.Zero ? value : null;
        LastTool = tool;
        return true;
    }

    // 利用者がカードの停止ボタンで作業中の表示を止めた。停止ボタンや Esc で中断しても
    // 終了の知らせを送らないツール（Copilot、VS Code 版の Claude Code、Antigravity の一部）があるため。
    // 作業中のときだけ働き、次の指示（開始）でまた作業中になる。AI の処理そのものは止めない。
    public bool StopWorking()
    {
        if (State != AiActivityState.Working)
        {
            return false;
        }

        _activeSessions.Clear();
        WorkingSince = null;
        PruneSessionStarts();
        return true;
    }

    public bool Reset()
    {
        var before = State;
        _activeSessions.Clear();
        _supersededSessions.Clear();
        _sessionStartedAt.Clear();
        WorkingSince = null;
        _needsAttention = false;
        CompletedAt = null;
        CompletedDuration = null;
        return before != State;
    }
}

// 作業中の経過時間と、完了したタスクにかかった時間の表示。カードの小さな枠に収まる時計の書き方（0:42、12:05、1:02:33）にする。
public static class AiWorkingElapsed
{
    public static string Format(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var hours = (long)elapsed.TotalHours;
        return hours > 0
            ? $"{hours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:00}";
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

    // どちらかがもう一方と同じか、その配下なら同じワークスペースとみなす。
    public static bool IsSameWorkspace(string? first, string? second)
    {
        var a = NormalizeLocalPath(first);
        var b = NormalizeLocalPath(second);
        return a.Length > 0 && b.Length > 0 && (IsSameOrUnder(a, b) || IsSameOrUnder(b, a));
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
