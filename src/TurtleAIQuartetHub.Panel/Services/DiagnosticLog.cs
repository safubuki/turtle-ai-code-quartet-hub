using System.IO;
using System.Text;

namespace TurtleAIQuartetHub.Panel.Services;

public static class DiagnosticLog
{
    // 本日分を抽出するために、各行の先頭に書く時刻プレフィックスのフォーマットと、
    // 日付（年月日）の固定長を共有する。"[2026-06-23T..." の "[" を除いた "2026-06-23" が日付部分。
    private const int DatePrefixLength = 10; // "yyyy-MM-dd"

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TurtleAIQuartetHub",
        "panel.log");

    // 設定画面の「ログを表示」「ファイルを開く」から参照するための公開パス。
    public static string FilePath => LogPath;

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break the panel UI.
        }
    }

    public static void Write(Exception exception)
    {
        Write(exception.ToString());
    }

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
