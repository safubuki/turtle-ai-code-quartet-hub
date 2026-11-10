using System.IO;
using System.Text.Json.Nodes;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

// AI フックの通知から決まる作業状態（作業中・完了・解除）、スロットの特定、フック設定の足し引きを確認する。
internal static class AiActivityTests
{
    public static void Run(List<string> failures)
    {
        VerifyParser(failures);
        VerifyTrackerTransitions(failures);
        VerifySlotMatching(failures);
        VerifyInstaller(failures);
    }

    private static void VerifyParser(List<string> failures)
    {
        var start = AiHookEventParser.TryParse(["--ai-event", "Claude", "UserPromptSubmit", @"C:\work\a", "s1", ""]);
        Check(failures, "通知: Claude の UserPromptSubmit は開始",
            start is { Tool: "claude", Kind: AiHookEventKind.Start, SessionId: "s1" });
        Check(failures, "通知: Copilot の userPromptSubmitted / agentStop を開始・完了として扱う",
            AiHookEventParser.Classify("userPromptSubmitted", "") == AiHookEventKind.Start
            && AiHookEventParser.Classify("agentStop", "") == AiHookEventKind.Stop);
        Check(failures, "通知: 許可待ちは注意、入力待ち（idle_prompt）は無視",
            AiHookEventParser.Classify("Notification", "permission_prompt") == AiHookEventKind.Attention
            && AiHookEventParser.Classify("Notification", "idle_prompt") == AiHookEventKind.Unknown
            && AiHookEventParser.Classify("PermissionRequest", "") == AiHookEventKind.Attention);
        Check(failures, "通知: cwd が無い・別コマンドは受け付けない",
            AiHookEventParser.TryParse(["--ai-event", "claude", "Stop", ""]) is null
            && AiHookEventParser.TryParse(["--activate"]) is null);
    }

    private static void VerifyTrackerTransitions(List<string> failures)
    {
        var now = DateTimeOffset.UtcNow;
        var tracker = new AiActivityTracker();
        tracker.Apply(Event("claude", AiHookEventKind.Start, "s1"), now);
        Check(failures, "状態: 指示を受けたら作業中", tracker.State == AiActivityState.Working);

        tracker.Apply(Event("claude", AiHookEventKind.Stop, "s1"), now);
        Check(failures, "状態: 完了したら光る（フォーカス表示中でも光らせる）", tracker.State == AiActivityState.Done);

        Check(failures, "状態: フォーカス表示を切り替えたら光が消えて元に戻る",
            tracker.Acknowledge() && tracker.State == AiActivityState.Idle);

        Check(failures, "状態: 光っていないときの切り替えでは何も変わらない", !tracker.Acknowledge());

        tracker.Apply(Event("claude", AiHookEventKind.Start, "s1"), now);
        tracker.Apply(Event("claude", AiHookEventKind.Attention, "s1"), now);
        Check(failures, "状態: 許可待ちは光り、ツールチップ用に許可待ちと分かる",
            tracker.State == AiActivityState.Done && tracker.LastWasAttentionRequest);
        tracker.Acknowledge();
        Check(failures, "状態: 許可待ちを見た後は、作業が続くので作業中へ戻る", tracker.State == AiActivityState.Working);

        var multi = new AiActivityTracker();
        multi.Apply(Event("claude", AiHookEventKind.Start, "a"), now);
        multi.Apply(Event("copilot", AiHookEventKind.Start, "b"), now);
        multi.Apply(Event("copilot", AiHookEventKind.Stop, "b"), now);
        multi.Acknowledge();
        Check(failures, "状態: 同じスロットで別の AI が作業中なら作業中のまま", multi.State == AiActivityState.Working);
        multi.Apply(Event("claude", AiHookEventKind.Interrupt, "a"), now);
        Check(failures, "状態: 中断は光らせずに元へ戻す", multi.State == AiActivityState.Idle);

        var stale = new AiActivityTracker();
        stale.Apply(Event("claude", AiHookEventKind.Start, "x"), now);
        Check(failures, "状態: 完了通知が来ないまま長時間たった作業中は片付ける",
            !stale.PruneStale(now.AddMinutes(10))
            && stale.PruneStale(now + AiActivityTracker.StaleSessionTimeout + TimeSpan.FromSeconds(1))
            && stale.State == AiActivityState.Idle);

        var slotA = new WindowSlot(new SlotConfig { Name = "A" });
        var slotB = new WindowSlot(new SlotConfig { Name = "B" });
        slotA.AiTracker.Apply(Event("claude", AiHookEventKind.Start, "s"), now);
        WindowSlot.SwapAiActivity(slotA, slotB);
        Check(failures, "状態: スロットの入れ替えではワークスペースと一緒に移る",
            slotA.AiActivity == AiActivityState.Idle && slotB.AiActivity == AiActivityState.Working);
        slotB.ClearWindow();
        Check(failures, "状態: ウィンドウが閉じたら消す", slotB.AiActivity == AiActivityState.Idle);
    }

    private static void VerifySlotMatching(List<string> failures)
    {
        var slots = new (string Name, string Path)[]
        {
            ("A", @"C:\git_home\turtle-ai-code-quartet-hub"),
            ("B", @"C:\git_home\turtle"),
            ("C", @"C:\git_home\turtle-ai-code-quartet-hub\src"),
            ("D", "vscode-remote://ssh-remote+host/home/user/repo")
        };

        string Match(string cwd) => string.Join(",", AiActivitySlotMatcher.FindTargets(slots, slot => [slot.Path], cwd).Select(slot => slot.Name));

        Check(failures, "特定: 大文字小文字・区切り文字の違いを無視して一致する",
            Match(@"c:/git_home/turtle-ai-code-quartet-hub/") == "A", Match(@"c:/git_home/turtle-ai-code-quartet-hub/"));
        Check(failures, "特定: 名前の前方一致（turtle と turtle-ai…）を同じフォルダとみなさない",
            Match(@"C:\git_home\turtle-ai-code-quartet-hub\docs") == "A", Match(@"C:\git_home\turtle-ai-code-quartet-hub\docs"));
        Check(failures, "特定: 入れ子のワークスペースは一番深いスロットを選ぶ",
            Match(@"C:\git_home\turtle-ai-code-quartet-hub\src\TurtleAIQuartetHub.Panel") == "C");
        Check(failures, "特定: Git Bash 形式の /c/... も同じ場所として扱う",
            Match("/c/git_home/turtle") == "B", Match("/c/git_home/turtle"));
        Check(failures, "特定: 無関係なフォルダやリモートは対象外",
            Match(@"C:\other") == string.Empty && Match("/home/user/repo") == string.Empty);
    }

    private static void VerifyInstaller(List<string> failures)
    {
        var home = Path.Combine(Path.GetTempPath(), "turtle-ai-notify-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".claude"));
            Directory.CreateDirectory(Path.Combine(home, ".codex"));
            var installer = new AiNotifyHookInstaller(home);

            // 利用者が自分で書いた設定とフックは残ること。
            File.WriteAllText(installer.ClaudeSettingsPath, """
                {
                  "model": "opus",
                  "hooks": {
                    "Stop": [ { "hooks": [ { "type": "command", "command": "echo mine" } ] } ]
                  }
                }
                """);

            var first = installer.Install();
            installer.Install();
            var claude = JsonNode.Parse(File.ReadAllText(installer.ClaudeSettingsPath))!.AsObject();
            var stopCommands = claude["hooks"]!["Stop"]!.AsArray()
                .SelectMany(group => group!["hooks"]!.AsArray())
                .Select(handler => handler!["command"]!.GetValue<string>())
                .ToList();
            Check(failures, "フック設定: 既存の設定と自前のフックを残し、2 回設定しても重複しない",
                claude["model"]!.GetValue<string>() == "opus"
                && stopCommands.Count == 2
                && stopCommands.Contains("echo mine")
                && stopCommands.Count(AiNotifyHookInstaller.IsHubCommand) == 1,
                string.Join(" | ", stopCommands));
            Check(failures, "フック設定: 書き換え前の内容をバックアップに残す",
                File.Exists(installer.ClaudeSettingsPath + ".turtle-hub-backup"));
            Check(failures, "フック設定: Codex は hooks.json を作り、Copilot は未インストールなので触らない",
                File.Exists(installer.CodexHooksPath)
                && first.Single(result => result.Tool == "codex").Status == AiNotifyHookStatus.Installed
                && first.Single(result => result.Tool == "copilot").Status == AiNotifyHookStatus.ToolMissing
                && !File.Exists(installer.CopilotHookPath));
            Check(failures, "フック設定: 通知スクリプトは ASCII だけ（PowerShell 5.1 の文字化け対策）",
                File.ReadAllText(installer.ScriptPath).All(character => character < 128));
            Check(failures, "フック設定: コマンドは / 区切りのパスを引用符で囲む（Git Bash でも動く）",
                installer.BuildCommand("claude", "Stop").Contains($"\"{installer.ScriptPath.Replace('\\', '/')}\"", StringComparison.Ordinal));

            var removed = installer.Uninstall();
            var claudeAfter = JsonNode.Parse(File.ReadAllText(installer.ClaudeSettingsPath))!.AsObject();
            var remaining = claudeAfter["hooks"]!.AsObject();
            Check(failures, "フック解除: 自分が足した項目だけを消し、利用者のフックとスクリプト以外は残す",
                remaining.Count == 1
                && remaining["Stop"]!.AsArray().Count == 1
                && claudeAfter["model"]!.GetValue<string>() == "opus"
                && !File.Exists(installer.ScriptPath)
                && removed.All(result => result.Status != AiNotifyHookStatus.Installed),
                remaining.ToJsonString());
            var codexAfter = JsonNode.Parse(File.ReadAllText(installer.CodexHooksPath))!.AsObject();
            Check(failures, "フック解除: Codex の hooks.json からハブの項目が消える", codexAfter["hooks"] is null);
        }
        finally
        {
            try
            {
                Directory.Delete(home, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static AiHookEvent Event(string tool, AiHookEventKind kind, string session) =>
        new(tool, kind.ToString(), kind, @"C:\work", session);

    private static void Check(List<string> failures, string name, bool condition, string? detail = null)
    {
        if (!condition)
        {
            failures.Add(detail is null ? name : $"{name}: {detail}");
        }
    }
}
