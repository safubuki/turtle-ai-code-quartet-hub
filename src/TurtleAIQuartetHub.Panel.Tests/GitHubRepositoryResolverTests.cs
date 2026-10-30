using System.IO;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

internal static class GitHubRepositoryResolverTests
{
    public static int Count { get; private set; }

    public static void Run(List<string> failures)
    {
        // リモート URL の正規化
        Check("HTTPS（.git 付き）", GitHubRepositoryResolver.ToGitHubWebUrl("https://github.com/owner/repo.git") == "https://github.com/owner/repo", failures);
        Check("HTTPS（.git なし・末尾スラッシュ）", GitHubRepositoryResolver.ToGitHubWebUrl("https://github.com/owner/repo/") == "https://github.com/owner/repo", failures);
        Check("HTTPS（認証情報付き）", GitHubRepositoryResolver.ToGitHubWebUrl("https://user@github.com/owner/repo.git") == "https://github.com/owner/repo", failures);
        Check("SSH scp 形式", GitHubRepositoryResolver.ToGitHubWebUrl("git@github.com:owner/repo.git") == "https://github.com/owner/repo", failures);
        Check("ssh:// 形式（ポート付き）", GitHubRepositoryResolver.ToGitHubWebUrl("ssh://git@ssh.github.com:443/owner/repo.git") == "https://github.com/owner/repo", failures);
        Check("SSH 別名ホスト", GitHubRepositoryResolver.ToGitHubWebUrl("git@github.com-work:owner/repo.git") == "https://github.com/owner/repo", failures);
        Check("GitHub 以外は空", GitHubRepositoryResolver.ToGitHubWebUrl("git@gitlab.com:owner/repo.git") == string.Empty, failures);
        Check("owner/repo 以外の形は空", GitHubRepositoryResolver.ToGitHubWebUrl("https://github.com/owner") == string.Empty, failures);
        Check("ローカルパスのリモートは空", GitHubRepositoryResolver.ToGitHubWebUrl(@"C:\repos\bare.git") == string.Empty, failures);

        // .git/config の読み取りとフォルダ探索
        WithTempDirectory(root =>
        {
            var repo = Path.Combine(root, "repo");
            WriteConfig(Path.Combine(repo, ".git"), "[core]\n\tbare = false\n[remote \"upstream\"]\n\turl = https://github.com/other/fork.git\n[remote \"origin\"]\n\turl = git@github.com:owner/repo.git\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n");
            var sub = Directory.CreateDirectory(Path.Combine(repo, "src", "app")).FullName;
            var resolved = GitHubRepositoryResolver.Resolve(sub);
            Check("サブフォルダから親の .git を見つけ origin を優先", resolved.WebUrl == "https://github.com/owner/repo", failures);
        }, failures);

        WithTempDirectory(root =>
        {
            var repo = Path.Combine(root, "repo");
            WriteConfig(Path.Combine(repo, ".git"), "[remote \"origin\"]\n\turl = https://gitlab.com/owner/repo.git\n[remote \"github\"]\n\turl = https://github.com/owner/mirror\n");
            Check("origin が GitHub 以外なら他の GitHub リモートを使う", GitHubRepositoryResolver.Resolve(repo).WebUrl == "https://github.com/owner/mirror", failures);
        }, failures);

        WithTempDirectory(root =>
        {
            var main = Path.Combine(root, "main");
            var mainGit = Path.Combine(main, ".git");
            WriteConfig(mainGit, "[remote \"origin\"]\n\turl = https://github.com/owner/worktree-repo.git\n");
            var worktreeGit = Directory.CreateDirectory(Path.Combine(mainGit, "worktrees", "feature")).FullName;
            File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../..\n");
            var worktree = Directory.CreateDirectory(Path.Combine(root, "feature")).FullName;
            File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGit}\n");
            Check("worktree の .git ファイルから本体の config を読む", GitHubRepositoryResolver.Resolve(worktree).WebUrl == "https://github.com/owner/worktree-repo", failures);
        }, failures);

        WithTempDirectory(root =>
        {
            var plain = Directory.CreateDirectory(Path.Combine(root, "plain")).FullName;
            var resolved = GitHubRepositoryResolver.Resolve(plain);
            Check("Git 管理外は解決しない", !resolved.IsResolved && resolved.Message.Contains("Git 管理されていない"), failures);

            var noRemote = Path.Combine(root, "no-remote");
            WriteConfig(Path.Combine(noRemote, ".git"), "[core]\n\tbare = false\n");
            Check("リモート未設定は解決しない", !GitHubRepositoryResolver.Resolve(noRemote).IsResolved, failures);
        }, failures);

        Check("未設定は解決しない", !GitHubRepositoryResolver.Resolve("").IsResolved, failures);

        // スロットの .code-workspace（ファイル）からも親フォルダのリポジトリを使う
        WithTempDirectory(root =>
        {
            var repo = Path.Combine(root, "repo");
            WriteConfig(Path.Combine(repo, ".git"), "[remote \"origin\"]\n\turl = https://github.com/owner/workspace-repo.git\n");
            var workspaceFile = Path.Combine(repo, "app.code-workspace");
            File.WriteAllText(workspaceFile, "{}");
            var slot = new WindowSlot(new SlotConfig { Name = "A" }) { Path = workspaceFile };
            Check("スロットの .code-workspace から GitHub を解決", slot.CanOpenGitHub && slot.GitHubWebUrl == "https://github.com/owner/workspace-repo", failures);

            slot.Path = string.Empty;
            Check("スロットのパスを消すと無効になる", !slot.CanOpenGitHub, failures);
        }, failures);
    }

    private static void WriteConfig(string gitDirectory, string content)
    {
        Directory.CreateDirectory(gitDirectory);
        File.WriteAllText(Path.Combine(gitDirectory, "config"), content);
    }

    private static void WithTempDirectory(Action<string> action, List<string> failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "TurtleGitHubResolverTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            action(root);
        }
        catch (Exception ex)
        {
            failures.Add($"GitHub URL 解決: {ex.Message}");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(string name, bool passed, List<string> failures)
    {
        Count++;
        if (!passed) failures.Add($"GitHub URL 解決: {name}");
    }
}
