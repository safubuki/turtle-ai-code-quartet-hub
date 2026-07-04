using System.IO;
using System.Text;

namespace TurtleAIQuartetHub.Panel.Services;

// ログ1行の重大度。行を一目で仕分けできるよう、時刻の直後に "[INFO]" などの
// プレフィックスとして出力する。ハング/異常終了の切り分けを楽にするのが目的。
public enum LogLevel
{
    // 通常の動作トレース（起動・終了・各種処理の経過）。
    Info,

    // 致命的ではないが注意したい事象（タイムアウト、想定外だが継続可能な状態）。
    Warn,

    // ハンドルした例外など、異常だがアプリは継続できるもの。
    Error,

    // 継続不能な致命的エラー（AppDomain 未処理例外など）。プロセスが落ちる直前。
    Fatal,
}

public static class DiagnosticLog
{
    // 本日分を抽出するために、各行の先頭に書く時刻プレフィックスのフォーマットと、
    // 日付（年月日）の固定長を共有する。"[2026-06-23T..." の "[" を除いた "2026-06-23" が日付部分。
    private const int DatePrefixLength = 10; // "yyyy-MM-dd"

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TurtleAIQuartetHub",
        "panel.log");

    // UI スレッドとウォッチドッグ等の背景スレッドが同時に書き込むと AppendAllText 同士が
    // 共有違反で失敗し、行が黙って失われる。プロセス内の書き込みを直列化して取りこぼしを防ぐ。
    private static readonly object WriteGate = new();

    // 無制限に追記し続けると数年で肥大するため、起動時にこのサイズを超えていたら
    // 末尾側（新しい方）だけ残して切り詰める。
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private const long TrimTargetBytes = 1 * 1024 * 1024;

    // 設定画面の「ログを表示」「ファイルを開く」から参照するための公開パス。
    public static string FilePath => LogPath;

    // 起動時に一度だけ呼ぶ。上限超過時、末尾 TrimTargetBytes ぶんを行境界で残して書き戻す。
    // 失敗してもログ機能自体は生かす（次回起動で再試行される）。
    public static void TrimIfOversized()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length <= MaxLogBytes)
            {
                return;
            }

            byte[] tail;
            using (var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(-TrimTargetBytes, SeekOrigin.End);
                tail = new byte[TrimTargetBytes];
                var read = stream.Read(tail, 0, tail.Length);
                Array.Resize(ref tail, read);
            }

            // 途中で切れた行を捨て、次の改行の直後から始める。
            var newlineIndex = Array.IndexOf(tail, (byte)'\n');
            var start = newlineIndex >= 0 ? newlineIndex + 1 : 0;

            lock (WriteGate)
            {
                using var output = new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                output.Write(tail, start, tail.Length - start);
            }
        }
        catch
        {
            // Logging must never break the panel UI.
        }
    }

    // レベル未指定の文字列ログは情報扱い。既存の呼び出しは無変更のまま [INFO] になる。
    public static void Write(string message) => Write(LogLevel.Info, message);

    public static void Write(LogLevel level, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            // 形式: "[yyyy-MM-ddTHH:mm:ss...] [LEVEL] message"
            // 日付プレフィックスの位置は従来どおり先頭に保ち、ReadTodayLines の抽出ロジックを壊さない。
            lock (WriteGate)
            {
                File.AppendAllText(
                    LogPath,
                    $"[{DateTimeOffset.Now:O}] [{LevelTag(level)}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the panel UI.
        }
    }

    // 例外はレベル未指定なら異常として扱う。致命的なケースは Write(LogLevel.Fatal, ...) を使う。
    public static void Write(Exception exception) => Write(LogLevel.Error, exception);

    public static void Write(LogLevel level, Exception exception) => Write(level, exception.ToString());

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Fatal => "FATAL",
        _ => "INFO",
    };

    // 本日（ローカル日付）のログ行だけを古い順に返す。アプリ自身が追記中でも読めるよう
    // 共有読み取りで開く。行頭の "[yyyy-MM-dd" を本日の日付と突き合わせて抽出する。
    // 日付プレフィックスを持たない継続行（例外スタックトレースの2行目以降など）は、
    // 直前に本日の行を採用していれば本日分の続きとして一緒に拾う。
    public static IReadOnlyList<string> ReadTodayLines()
    {
        var todayPrefix = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var lines = new List<string>();

        try
        {
            if (!File.Exists(LogPath))
            {
                return lines;
            }

            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var includingContinuation = false;
            while (reader.ReadLine() is { } line)
            {
                if (TryGetLineDate(line, out var lineDate))
                {
                    includingContinuation = string.Equals(lineDate, todayPrefix, StringComparison.Ordinal);
                    if (includingContinuation)
                    {
                        lines.Add(line);
                    }
                }
                else if (includingContinuation)
                {
                    // 日付プレフィックスのない継続行は直前の本日行にぶら下げる。
                    lines.Add(line);
                }
            }
        }
        catch (IOException)
        {
            return lines;
        }
        catch (UnauthorizedAccessException)
        {
            return lines;
        }

        return lines;
    }

    private static bool TryGetLineDate(string line, out string date)
    {
        // 期待形式: "[yyyy-MM-ddTHH:mm:ss...] message"
        if (line.Length >= DatePrefixLength + 1 && line[0] == '[')
        {
            date = line.Substring(1, DatePrefixLength);
            return true;
        }

        date = string.Empty;
        return false;
    }
}
