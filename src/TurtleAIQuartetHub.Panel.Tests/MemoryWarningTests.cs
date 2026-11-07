using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

internal static class MemoryWarningTests
{
    public static int Count { get; private set; }

    public static void Run(List<string> failures)
    {
        Count = 0;
        const ulong gb = 1024UL * 1024 * 1024;
        var snapshot = new PagefileSnapshot(
            48 * gb, 51 * gb, 42 * gb, 3L * (long)gb, 0, 0, 0,
            new PagefileSettings(PagefileManagementMode.SystemManaged, null, null));

        CheckNormal(failures, "画像の物理メモリ47.5%・コミット約82%では警告文なし・アイコン緑", snapshot, 47.5);
        CheckNormal(failures, "コミット率だけが90%を超えても物理メモリが緑なら警告しない",
            snapshot with { CommittedBytes = 50 * gb }, 47.5);
        CheckNormal(failures, "過去にページファイルが増えても空きが戻れば警告しない",
            snapshot with { GrowthSinceHubStartBytes = 2L * (long)gb }, 47.5);
        CheckNormal(failures, "ページファイル無効だけでは空きのあるメモリを警告しない",
            snapshot with { Settings = new PagefileSettings(PagefileManagementMode.None, null, null) }, 47.5);
        CheckNormal(failures, "ページファイル設定を取得できなくても緑のときは警告しない",
            snapshot with { Settings = new PagefileSettings(PagefileManagementMode.Unknown, null, null) }, 47.5);
        CheckNormal(failures, "物理メモリ75%未満は警告なし", snapshot, 74.9);

        foreach (var (percent, expected) in new[]
        {
            (75d, PagefileAssessmentLevel.Warning),
            (89.9d, PagefileAssessmentLevel.Warning),
            (90d, PagefileAssessmentLevel.Critical)
        })
        {
            // コミットが低くても現在の物理メモリが多ければ知らせる。
            var assessment = SystemMonitorService.AssessPagefile(snapshot with { CommittedBytes = 20 * gb }, percent);
            var expectedHealth = expected == PagefileAssessmentLevel.Warning ? SystemHealthLevel.Warning : SystemHealthLevel.Critical;
            var health = SystemMonitorService.EvaluateHealth(3, percent, [], assessment);
            Check(failures, $"物理メモリ{percent}%の警告文とアイコンがバーと同じ段階になる",
                assessment.Level == expected && assessment.HintText.Length > 0
                && health.Level == expectedHealth);
        }

        var grown = snapshot with { GrowthSinceHubStartBytes = 2L * (long)gb, CommittedBytes = 50 * gb };
        var high = SystemMonitorService.AssessPagefile(grown, 95);
        var recovered = SystemMonitorService.AssessPagefile(grown, 47.5);
        Check(failures, "高負荷から回復したら拡張履歴が残っていても警告文とアイコン色を解除する",
            high.Level == PagefileAssessmentLevel.Critical
            && recovered is { Level: PagefileAssessmentLevel.Normal, HintText: "" }
            && SystemMonitorService.EvaluateHealth(3, 47.5, [], recovered).Level == SystemHealthLevel.Normal);

        var yellow = SystemMonitorService.AssessPagefile(grown, 80);
        Check(failures, "過去の拡張やコミット率で物理メモリの注意を赤へ引き上げない",
            yellow.Level == PagefileAssessmentLevel.Warning
            && SystemMonitorService.EvaluateHealth(3, 80, [], yellow).Level == SystemHealthLevel.Warning);
        Check(failures, "メモリ警告を消してもCPUの警告は保持する",
            SystemMonitorService.EvaluateHealth(95, 47.5, [], recovered) is { Level: SystemHealthLevel.Critical, Issues.Count: 1 });
    }

    private static void CheckNormal(List<string> failures, string name, PagefileSnapshot snapshot, double percent)
    {
        var assessment = SystemMonitorService.AssessPagefile(snapshot, percent);
        var health = SystemMonitorService.EvaluateHealth(3, percent, [], assessment);
        Check(failures, name, assessment is { Level: PagefileAssessmentLevel.Normal, HintText: "" }
            && health is { Level: SystemHealthLevel.Normal, Issues.Count: 0 });
    }

    private static void Check(List<string> failures, string name, bool condition)
    {
        Count++;
        if (!condition)
        {
            failures.Add(name);
        }
    }
}
