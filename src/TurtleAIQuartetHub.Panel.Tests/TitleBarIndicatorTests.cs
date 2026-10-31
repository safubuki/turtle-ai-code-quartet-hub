using System.IO;
using System.Windows.Media;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

// システムモニタのアイコン色の集約、PR 通知の赤丸判定、既読状態の保存、控えのフォルダ変更を確認する。
internal static class TitleBarIndicatorTests
{
    private static readonly PagefileAssessment NormalPagefile = new(PagefileAssessmentLevel.Normal, "余裕あり", string.Empty);

    public static void Run(List<string> failures)
    {
        VerifySystemHealthAggregation(failures);
        VerifyPullRequestNotificationParsing(failures);
        VerifyUpdateBadgeStateStore(failures);
        VerifyChangeStoredPanelWorkspace(failures);
    }

    private static void VerifySystemHealthAggregation(List<string> failures)
    {
        var healthy = SystemMonitorService.EvaluateHealth(10, 40, [Drive(@"C:\", 50)], NormalPagefile);
        Check(failures, "健康状態: すべて正常なら緑で原因なし",
            healthy.Level == SystemHealthLevel.Normal && healthy.Issues.Count == 0);

        var warning = SystemMonitorService.EvaluateHealth(10, 40, [Drive(@"C:\", 85)], NormalPagefile);
        Check(failures, "健康状態: ディスク 1 台が注意なら黄で原因にドライブ名を出す",
            warning.Level == SystemHealthLevel.Warning
            && warning.Issues.Count == 1
            && warning.Issues[0].Text.Contains("C:", StringComparison.Ordinal),
            string.Join(" / ", warning.Issues.Select(issue => issue.Text)));

        var critical = SystemMonitorService.EvaluateHealth(
            75,
            95,
            [Drive(@"C:\", 85)],
            new PagefileAssessment(PagefileAssessmentLevel.Warning, "注意", "hint"));
        Check(failures, "健康状態: 1 つでも警告なら赤で、原因は警告から並ぶ",
            critical.Level == SystemHealthLevel.Critical
            && critical.Issues.Count == 4
            && critical.Issues[0].Level == SystemHealthLevel.Critical
            && critical.Issues[0].Text.Contains("メモリ", StringComparison.Ordinal),
            string.Join(" / ", critical.Issues.Select(issue => $"{issue.Level}:{issue.Text}")));

        var opticalOnly = SystemMonitorService.EvaluateHealth(10, 40, [Drive(@"D:\", 100, isFixed: false)], NormalPagefile);
        Check(failures, "健康状態: 光学・ネットワークドライブの満杯は色に含めない",
            opticalOnly.Level == SystemHealthLevel.Normal);

        var pagefileCritical = SystemMonitorService.EvaluateHealth(
            10,
            40,
            [],
            new PagefileAssessment(PagefileAssessmentLevel.Critical, "拡張あり", "hint"));
        Check(failures, "健康状態: ページファイルの拡張は警告",
            pagefileCritical.Level == SystemHealthLevel.Critical
            && pagefileCritical.Issues[0].Text.Contains("拡張あり", StringComparison.Ordinal));

        Check(failures, "健康状態: しきい値ちょうどで段階が上がる",
            SystemMonitorService.GetUsageLevel(SystemMonitorService.CpuWarningPercent, SystemMonitorService.CpuWarningPercent, SystemMonitorService.CpuCriticalPercent) == SystemHealthLevel.Warning
            && SystemMonitorService.GetUsageLevel(SystemMonitorService.CpuCriticalPercent, SystemMonitorService.CpuWarningPercent, SystemMonitorService.CpuCriticalPercent) == SystemHealthLevel.Critical
            && SystemMonitorService.GetUsageLevel(SystemMonitorService.CpuWarningPercent - 0.1, SystemMonitorService.CpuWarningPercent, SystemMonitorService.CpuCriticalPercent) == SystemHealthLevel.Normal);
    }

    private static void VerifyPullRequestNotificationParsing(List<string> failures)
    {
        const string json = """
            [
              { "unread": true,  "updated_at": "2026-10-30T01:00:00Z", "subject": { "type": "PullRequest" } },
              { "unread": true,  "updated_at": "2026-10-30T02:00:00Z", "subject": { "type": "CheckSuite" } },
              { "unread": true,  "updated_at": "2026-10-30T03:00:00Z", "subject": { "type": "Issue" } },
              { "unread": false, "updated_at": "2026-10-30T04:00:00Z", "subject": { "type": "PullRequest" } },
              { "unread": true,  "updated_at": "not-a-date",           "subject": { "type": "PullRequest" } }
            ]
            """;
        var times = PullRequestNotificationService.ParseUnreadPullRequestUpdateTimes(json);
        Check(failures, "PR 通知: 未読の PullRequest / CheckSuite だけを数える",
            times.Count == 2,
            string.Join(",", times));

        Check(failures, "PR 通知: 空の応答や配列以外は 0 件",
            PullRequestNotificationService.ParseUnreadPullRequestUpdateTimes("").Count == 0
            && PullRequestNotificationService.ParseUnreadPullRequestUpdateTimes("{\"message\":\"x\"}").Count == 0);

        var seenAt = DateTimeOffset.Parse("2026-10-30T01:30:00Z");
        Check(failures, "PR 通知: 未確認なら未読すべて、確認後はそれより新しい更新だけを数える",
            PullRequestNotificationService.CountUpdatesSince(times, null) == 2
            && PullRequestNotificationService.CountUpdatesSince(times, seenAt) == 1
            && PullRequestNotificationService.CountUpdatesSince(times, DateTimeOffset.Parse("2026-10-30T02:00:00Z")) == 0);

        Check(failures, "PR 通知: 未サインインなら gh auth login を案内する",
            PullRequestNotificationService.DescribeFailure("To get started with GitHub CLI, please run:  gh auth login")
                .Contains("gh auth login", StringComparison.Ordinal));

        Check(failures, "PR 通知: 未サインインだけをステータス欄の案内対象にし、通信エラーは対象外",
            PullRequestNotificationService.IsSignInRequired("To get started with GitHub CLI, please run:  gh auth login")
            && PullRequestNotificationService.IsSignInRequired("HTTP 401: Bad credentials (https://api.github.com/notifications)")
            && !PullRequestNotificationService.IsSignInRequired("error connecting to api.github.com"));
    }

    private static void VerifyUpdateBadgeStateStore(List<string> failures)
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), "TurtleUpdateBadgeTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new UpdateBadgeStateStore(stateDirectory);
            var first = DateTimeOffset.Parse("2026-10-30T05:00:00Z");
            var earlier = DateTimeOffset.Parse("2026-10-30T04:00:00Z");
            var missingBefore = store.GetLastSeen(UpdateBadgeStateStore.PullRequestsKey) is null;
            store.MarkSeen(UpdateBadgeStateStore.PullRequestsKey, first);
            store.MarkSeen(UpdateBadgeStateStore.PullRequestsKey, earlier);

            var reloaded = new UpdateBadgeStateStore(stateDirectory);
            Check(failures, "既読状態: 保存して読み直せ、古い時刻で巻き戻らない",
                missingBefore && reloaded.GetLastSeen(UpdateBadgeStateStore.PullRequestsKey) == first,
                reloaded.GetLastSeen(UpdateBadgeStateStore.PullRequestsKey)?.ToString() ?? "null");

            File.WriteAllText(Path.Combine(stateDirectory, "update-badges.json"), "{ broken");
            Check(failures, "既読状態: 壊れたファイルは未確認として扱う",
                new UpdateBadgeStateStore(stateDirectory).GetLastSeen(UpdateBadgeStateStore.PullRequestsKey) is null);
        }
        catch (Exception ex)
        {
            failures.Add($"既読状態: {ex.Message}");
        }
        finally
        {
            if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, recursive: true);
        }
    }

    private static void VerifyChangeStoredPanelWorkspace(List<string> failures)
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), "TurtleStoredFolderTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new StatusStore(new AppConfig { StateDirectory = stateDirectory });
            var auto = store.StoredPanels[0];
            auto.LoadFrom("old-repo", @"C:\work\old-repo", "antigravity");
            var custom = store.StoredPanels[1];
            custom.LoadFrom("お気に入り", @"C:\work\other", "vscode");
            var empty = store.StoredPanels[2];

            var autoChanged = store.ChangeStoredPanelWorkspace(auto, @"C:\work\new-repo");
            var customChanged = store.ChangeStoredPanelWorkspace(custom, @"C:\work\renamed");
            var emptyChanged = store.ChangeStoredPanelWorkspace(empty, @"C:\work\x");

            Check(failures, "控えのフォルダ変更: 自動で付いた名前は新しいフォルダ名にし、選択アプリは残す",
                autoChanged && auto.PanelTitle == "new-repo" && auto.WorkspacePath == @"C:\work\new-repo" && auto.ApplicationId == "antigravity",
                $"{auto.PanelTitle} / {auto.WorkspacePath} / {auto.ApplicationId}");
            Check(failures, "控えのフォルダ変更: 利用者が付けた名前は残す",
                customChanged && custom.PanelTitle == "お気に入り" && custom.WorkspacePath == @"C:\work\renamed",
                $"{custom.PanelTitle} / {custom.WorkspacePath}");
            Check(failures, "控えのフォルダ変更: 空きの控えは変更扱いにしない（登録を使う）",
                !emptyChanged && !empty.HasContent);
        }
        catch (Exception ex)
        {
            failures.Add($"控えのフォルダ変更: {ex.Message}");
        }
        finally
        {
            if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, recursive: true);
        }
    }

    private static DriveUsageItem Drive(string root, double percent, bool isFixed = true)
    {
        return new DriveUsageItem(
            RootPath: root,
            DisplayTitle: root,
            DriveTypeLabel: isFixed ? "ローカル ディスク" : "光学ドライブ",
            TotalBytes: 100,
            UsedBytes: (long)percent,
            FreeBytes: 100 - (long)percent,
            UsagePercent: percent,
            UsagePercentText: $"{percent:0.0} %",
            CapacityDetailText: string.Empty,
            FreeSpaceText: "空き 1 GB",
            BarBrush: Brushes.Transparent)
        {
            IsFixedDrive = isFixed
        };
    }

    private static void Check(List<string> failures, string name, bool condition, string? detail = null)
    {
        if (!condition)
        {
            failures.Add(detail is null ? name : $"{name}: {detail}");
        }
    }
}
