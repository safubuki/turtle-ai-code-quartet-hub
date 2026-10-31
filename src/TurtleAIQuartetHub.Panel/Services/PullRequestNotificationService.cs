using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TurtleAIQuartetHub.Panel.Services;

/// <summary>
/// GitHub CLI (gh) のサインイン情報で GitHub の通知 API を読み、未読の Pull Request 関連の通知
/// （PR 本体へのコメント・レビュー・レビュー依頼・メンション、CI 結果の CheckSuite）を数える。
/// ハブ自身はトークンを持たず、gh が無い・未サインインのときは確認しない。
/// </summary>
public sealed class PullRequestNotificationService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    private static readonly HashSet<string> PullRequestSubjectTypes = new(StringComparer.Ordinal)
    {
        "PullRequest",
        "CheckSuite"
    };

    public sealed record CheckResult(
        bool IsAvailable,
        IReadOnlyList<DateTimeOffset> UnreadUpdateTimes,
        string Message)
    {
        // gh は入っているが未サインイン。gh が無い場合（GitHub を使わない人も多い）とは区別して案内する。
        public bool RequiresSignIn { get; init; }

        public static CheckResult Unavailable(string message) => new(false, [], message);
    }

    public async Task<CheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var ghPath = FindGhExecutable();
        if (ghPath is null)
        {
            return CheckResult.Unavailable("GitHub CLI (gh) が見つからないため、更新を確認していません。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ghPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // 既定（all=false / participating=false）のまま呼び、購読中のリポジトリを含む未読の通知だけを受け取る。
        startInfo.ArgumentList.Add("api");
        startInfo.ArgumentList.Add("notifications?per_page=50");
        startInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        startInfo.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        startInfo.Environment["NO_COLOR"] = "1";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return CheckResult.Unavailable("GitHub CLI (gh) を起動できませんでした。");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                return CheckResult.Unavailable(DescribeFailure(error)) with { RequiresSignIn = IsSignInRequired(error) };
            }

            return new CheckResult(true, ParseUnreadPullRequestUpdateTimes(output), string.Empty);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CheckResult.Unavailable("GitHub への確認がタイムアウトしました。");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            DiagnosticLog.Write(ex);
            return CheckResult.Unavailable("GitHub CLI (gh) を起動できませんでした。");
        }
        catch (JsonException ex)
        {
            DiagnosticLog.Write(ex);
            return CheckResult.Unavailable("GitHub の通知を読み取れませんでした。");
        }
    }

    /// <summary>通知 API の応答から、未読の Pull Request 関連通知の更新時刻を取り出す。</summary>
    internal static IReadOnlyList<DateTimeOffset> ParseUnreadPullRequestUpdateTimes(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var times = new List<DateTimeOffset>();
        foreach (var notification in document.RootElement.EnumerateArray())
        {
            if (notification.TryGetProperty("unread", out var unread) && unread.ValueKind == JsonValueKind.False)
            {
                continue;
            }

            if (!notification.TryGetProperty("subject", out var subject)
                || !subject.TryGetProperty("type", out var type)
                || type.GetString() is not { } typeName
                || !PullRequestSubjectTypes.Contains(typeName))
            {
                continue;
            }

            if (notification.TryGetProperty("updated_at", out var updatedAt)
                && DateTimeOffset.TryParse(updatedAt.GetString(), out var time))
            {
                times.Add(time);
            }
        }

        return times;
    }

    /// <summary>最後に受信箱を開いた時刻より後に更新された未読通知の数。</summary>
    internal static int CountUpdatesSince(IReadOnlyList<DateTimeOffset> unreadUpdateTimes, DateTimeOffset? lastSeen)
    {
        return lastSeen is { } seen
            ? unreadUpdateTimes.Count(time => time > seen)
            : unreadUpdateTimes.Count;
    }

    internal static bool IsSignInRequired(string error)
    {
        return error.Contains("auth login", StringComparison.OrdinalIgnoreCase)
            || error.Contains("not logged", StringComparison.OrdinalIgnoreCase)
            || error.Contains("401", StringComparison.Ordinal);
    }

    internal static string DescribeFailure(string error)
    {
        if (IsSignInRequired(error))
        {
            return "gh auth login で GitHub にサインインすると、更新を確認できます。";
        }

        var firstLine = error
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine)
            ? "GitHub の通知を取得できませんでした。"
            : $"GitHub の通知を取得できませんでした: {firstLine}";
    }

    // ハブ起動後に gh を入れた場合でも見つかるよう、PATH に加えて公式インストーラーの既定の場所も探す。
    private static string? FindGhExecutable()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "gh", "bin"));

        foreach (var directory in directories)
        {
            try
            {
                var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(directory), "gh.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // PATH に不正な文字を含む項目があっても、残りの候補を探す。
            }
        }

        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            DiagnosticLog.Write(ex);
        }
    }
}
