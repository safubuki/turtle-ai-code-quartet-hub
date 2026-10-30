using System.IO;

namespace TurtleAIQuartetHub.Panel.Services;

/// <summary>
/// ローカルのワークスペースから GitHub リポジトリの Web URL を求める。
/// 周期更新や描画を止めないよう git プロセスは起動せず、.git/config を直接読む。
/// </summary>
public static class GitHubRepositoryResolver
{
    private const int MaxParentSearchDepth = 32;

    public readonly record struct Resolution(string WebUrl, string Message)
    {
        public bool IsResolved => !string.IsNullOrEmpty(WebUrl);
    }

    /// <summary>
    /// フォルダ（またはその親）にある Git リポジトリの GitHub Web URL を求める。
    /// origin を優先し、origin が GitHub でなければ他の GitHub リモートを探す。
    /// </summary>
    public static Resolution Resolve(string? workspaceFolder)
    {
        if (string.IsNullOrWhiteSpace(workspaceFolder))
        {
            return new Resolution(string.Empty, "ワークスペースフォルダが未設定です。");
        }

        try
        {
            var configPath = FindGitConfigPath(workspaceFolder);
            if (configPath is null)
            {
                return new Resolution(string.Empty, "Git 管理されていないフォルダです。");
            }

            var remotes = ReadRemoteUrls(File.ReadAllLines(configPath));
            if (remotes.Count == 0)
            {
                return new Resolution(string.Empty, "Git のリモートが設定されていません。");
            }

            var ordered = remotes
                .OrderBy(remote => string.Equals(remote.Name, "origin", StringComparison.Ordinal) ? 0 : 1);
            foreach (var remote in ordered)
            {
                var webUrl = ToGitHubWebUrl(remote.Url);
                if (!string.IsNullOrEmpty(webUrl))
                {
                    return new Resolution(webUrl, $"GitHub で開く: {webUrl}");
                }
            }

            return new Resolution(string.Empty, $"GitHub 以外のリモートです: {remotes[0].Url}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new Resolution(string.Empty, "Git の設定を読み取れませんでした。");
        }
    }

    /// <summary>
    /// HTTPS / SSH（scp 形式・ssh://）/ git:// のリモート URL を https://github.com/owner/repo へ正規化する。
    /// GitHub 以外、または owner/repo の形でなければ空文字を返す。
    /// </summary>
    public static string ToGitHubWebUrl(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return string.Empty;
        }

        var value = remoteUrl.Trim();
        string host;
        string path;
        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http" or "ssh" or "git" or "git+ssh"))
            {
                return string.Empty;
            }

            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else
        {
            // scp 形式: [user@]host:owner/repo.git
            var colon = value.IndexOf(':');
            if (colon <= 0)
            {
                return string.Empty;
            }

            host = value[..colon];
            var at = host.LastIndexOf('@');
            if (at >= 0)
            {
                host = host[(at + 1)..];
            }

            path = value[(colon + 1)..];
        }

        if (!IsGitHubHost(host))
        {
            return string.Empty;
        }

        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
        {
            return string.Empty;
        }

        var owner = segments[0];
        var repository = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];
        return owner.Length == 0 || repository.Length == 0
            ? string.Empty
            : $"https://github.com/{owner}/{repository}";
    }

    // github.com と SSH over HTTPS の ssh.github.com に加え、複数アカウント運用でよく使う
    // ~/.ssh/config の別名（github.com-work など）も GitHub とみなす。
    private static bool IsGitHubHost(string host)
    {
        return string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "www.github.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "ssh.github.com", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("github.com-", StringComparison.OrdinalIgnoreCase);
    }

    // フォルダから親へ .git を探す。.git がファイル（worktree / サブモジュール）なら gitdir を辿り、
    // worktree の場合は commondir 側（本体の .git）の config を使う。
    private static string? FindGitConfigPath(string workspaceFolder)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(workspaceFolder));
        for (var depth = 0; directory is not null && depth < MaxParentSearchDepth; depth++, directory = directory.Parent)
        {
            var dotGit = Path.Combine(directory.FullName, ".git");
            string? gitDirectory = null;
            if (Directory.Exists(dotGit))
            {
                gitDirectory = dotGit;
            }
            else if (File.Exists(dotGit))
            {
                gitDirectory = ReadGitDirFromFile(dotGit);
            }

            if (gitDirectory is null)
            {
                continue;
            }

            var commonDirFile = Path.Combine(gitDirectory, "commondir");
            if (File.Exists(commonDirFile))
            {
                var commonDir = File.ReadAllText(commonDirFile).Trim();
                if (commonDir.Length > 0)
                {
                    gitDirectory = Path.GetFullPath(Path.Combine(gitDirectory, commonDir));
                }
            }

            var configPath = Path.Combine(gitDirectory, "config");
            return File.Exists(configPath) ? configPath : null;
        }

        return null;
    }

    private static string? ReadGitDirFromFile(string dotGitFile)
    {
        const string prefix = "gitdir:";
        var line = File.ReadLines(dotGitFile).FirstOrDefault(item => item.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            return null;
        }

        var gitDir = line.Trim()[prefix.Length..].Trim();
        if (gitDir.Length == 0)
        {
            return null;
        }

        var fullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dotGitFile)!, gitDir));
        return Directory.Exists(fullPath) ? fullPath : null;
    }

    // [remote "name"] セクションの url を宣言順に集める。
    internal static List<(string Name, string Url)> ReadRemoteUrls(IEnumerable<string> configLines)
    {
        var remotes = new List<(string Name, string Url)>();
        string? currentRemote = null;
        foreach (var rawLine in configLines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                currentRemote = ParseRemoteSectionName(line);
                continue;
            }

            if (currentRemote is null)
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0 || !string.Equals(line[..equals].Trim(), "url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = line[(equals + 1)..].Trim().Trim('"');
            if (url.Length > 0)
            {
                remotes.Add((currentRemote, url));
            }
        }

        return remotes;
    }

    private static string? ParseRemoteSectionName(string sectionLine)
    {
        var end = sectionLine.IndexOf(']');
        var header = (end > 0 ? sectionLine[1..end] : sectionLine[1..]).Trim();
        if (!header.StartsWith("remote", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var firstQuote = header.IndexOf('"');
        var lastQuote = header.LastIndexOf('"');
        return firstQuote >= 0 && lastQuote > firstQuote
            ? header[(firstQuote + 1)..lastQuote]
            : null;
    }
}
