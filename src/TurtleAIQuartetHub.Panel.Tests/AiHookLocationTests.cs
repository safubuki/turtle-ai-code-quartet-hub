using System.IO;
using System.Text;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

internal static class AiHookLocationTests
{
    public const int Count = 14;

    public static void Run(List<string> failures)
    {
        var home = Path.Combine(Path.GetTempPath(), "turtle-hook-location-" + Guid.NewGuid().ToString("N"));
        try
        {
            var installer = new AiNotifyHookInstaller(home);
            var missing = installer.GetSettingsLocations();
            Check(failures, "場所確認だけでは未導入のホームや設定フォルダを作成しない",
                !Directory.Exists(home) && missing.Count == 4
                && missing.All(item => !item.CanOpenFolder && !item.CanViewSettings && item.Status == AiNotifyHookStatus.ToolMissing));
            Check(failures, "4ツールの表示先はインストーラーと同じ注入ホームの設定ファイル",
                missing.Select(item => item.SettingsPath).SequenceEqual([installer.ClaudeSettingsPath, installer.CodexHooksPath, installer.CopilotHookPath, installer.GrokHooksPath])
                && missing.All(item => item.FolderPath == Path.GetDirectoryName(item.SettingsPath)));

            var detail = new string('詳', 2000);
            var statuses = new[]
            {
                new AiNotifyHookToolResult("claude", "Claude Code", AiNotifyHookStatus.Error, detail),
                new AiNotifyHookToolResult("codex", "Codex", AiNotifyHookStatus.Installed, "設定済み"),
                new AiNotifyHookToolResult("copilot", "GitHub Copilot", AiNotifyHookStatus.NotInstalled, "未設定"),
                new AiNotifyHookToolResult("grok", "Grok", AiNotifyHookStatus.ToolMissing, "未インストール")
            };
            var display = installer.GetSettingsLocations(statuses);
            Check(failures, "表示は短い状態とホーム省略パス、長いエラーはツールチップへ分離",
                display[0].StatusText == "設定エラー" && display[0].StatusDetailToolTip == detail
                && display[1].StatusText == "設定済み" && display[2].StatusText == "未設定"
                && display[3].StatusText == "未インストール" && display[3].DisplayName == "Grok"
                && display.Select(item => item.DisplayPath).SequenceEqual(["~/.claude/settings.json", "~/.codex/hooks.json", "~/.copilot/hooks/turtle-ai-quartet-hub.json", "~/.grok/hooks/turtle-ai-quartet-hub.json"]));

            Directory.CreateDirectory(Path.GetDirectoryName(installer.ClaudeSettingsPath)!);
            var folderOnly = installer.GetSettingsLocations(statuses)[0];
            Check(failures, "設定フォルダだけ存在する時はフォルダを開け、設定閲覧は無効",
                folderOnly.CanOpenFolder && !folderOnly.CanViewSettings);

            const string original = "// 利用者のコメント\r\n{\"custom\": true,\r\n  \"hooks\": { /* 設定の原文 */ }\r\n}\r\n";
            File.WriteAllText(installer.ClaudeSettingsPath, original, new UTF8Encoding(true));
            var beforeBytes = File.ReadAllBytes(installer.ClaudeSettingsPath);
            var beforeFiles = Directory.GetFiles(home, "*", SearchOption.AllDirectories);
            var location = installer.GetSettingsLocations(statuses)[0];
            var preview = installer.ReadSettingsPreview(location);
            Check(failures, "BOM付き設定もコメント・改行・原文を変えずに読み取る",
                location.CanViewSettings && preview.Succeeded && preview.Content == original && !preview.IsTruncated && preview.Message.Length == 0);
            Check(failures, "設定の閲覧は書換・バックアップ・通知スクリプトを作成しない",
                beforeBytes.SequenceEqual(File.ReadAllBytes(installer.ClaudeSettingsPath))
                && beforeFiles.SequenceEqual(Directory.GetFiles(home, "*", SearchOption.AllDirectories))
                && !File.Exists(installer.ScriptPath));

            File.Delete(installer.ClaudeSettingsPath);
            var disappeared = installer.ReadSettingsPreview(location);
            Check(failures, "一覧取得後に設定ファイルが消えても失敗メッセージを返す",
                !disappeared.Succeeded && disappeared.Content.Length == 0 && disappeared.Message.Contains("まだありません"));

            var limit = AiNotifyHookInstaller.SettingsPreviewCharacterLimit;
            File.WriteAllText(installer.ClaudeSettingsPath, new string('a', limit + 100), new UTF8Encoding(false));
            var large = installer.ReadSettingsPreview(location);
            Check(failures, "大きなファイルは上限以内の原文と省略メッセージを返す",
                large.Succeeded && large.IsTruncated && large.Content.Length == limit && large.Message.Contains("先頭"));
            File.WriteAllText(installer.ClaudeSettingsPath, new string('b', limit), new UTF8Encoding(false));
            var exact = installer.ReadSettingsPreview(location);
            Check(failures, "上限と同じ文字数なら省略扱いにしない",
                exact.Succeeded && !exact.IsTruncated && exact.Content.Length == limit);
            File.WriteAllText(installer.ClaudeSettingsPath, new string('c', limit - 1) + "😀" + "end", new UTF8Encoding(false));
            var surrogate = installer.ReadSettingsPreview(location);
            Check(failures, "表示上限が絵文字の途中に来ても片側サロゲートを残さない",
                surrogate.Succeeded && surrogate.IsTruncated && surrogate.Content.Length == limit - 1
                && !char.IsHighSurrogate(surrogate.Content[^1]));

            var otherPath = Path.Combine(home, "private.json");
            File.WriteAllText(otherPath, "表示対象外の設定");
            var other = installer.ReadSettingsPreview(location with { SettingsPath = otherPath });
            Check(failures, "モデルを差し替えても既知設定以外のファイルを読まない",
                !other.Succeeded && other.Content.Length == 0);
            var unknown = installer.ReadSettingsPreview(location with { Tool = "other" });
            Check(failures, "知らないツール名から既知パスを渡しても閲覧対象にしない",
                !unknown.Succeeded && unknown.Content.Length == 0);

            Directory.CreateDirectory(Path.GetDirectoryName(installer.GrokHooksPath)!);
            const string grokOriginal = "{\r\n  \"hooks\": {}\r\n}\r\n";
            File.WriteAllText(installer.GrokHooksPath, grokOriginal, new UTF8Encoding(false));
            var grokBefore = Directory.GetFiles(home, "*", SearchOption.AllDirectories);
            var grokLocation = installer.GetSettingsLocations().Single(item => item.Tool == "grok");
            var grokPreview = installer.ReadSettingsPreview(grokLocation);
            Check(failures, "Grok の専用 JSON は原文のまま読め、閲覧では隣のファイルを作らない",
                grokLocation.CanViewSettings && grokPreview.Succeeded && grokPreview.Content == grokOriginal
                && grokBefore.SequenceEqual(Directory.GetFiles(home, "*", SearchOption.AllDirectories)));

            using (new FileStream(installer.ClaudeSettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var locked = installer.ReadSettingsPreview(location);
                Check(failures, "読取が拒否された設定は例外を外へ出さず状態確認を案内",
                    !locked.Succeeded && locked.Content.Length == 0 && locked.Message.Contains("読み取れません"));
            }
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    private static void Check(List<string> failures, string name, bool condition)
    {
        if (!condition) failures.Add($"AIフックの保存先・閲覧: {name}");
    }
}
