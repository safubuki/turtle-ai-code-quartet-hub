using System.IO;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

internal static class DiskCleanupTests
{
    private const long InstallerBytes = 11L * 1024 * 1024;

    public static void Run(List<string> failures)
    {
        WithTempRoot(root => VerifyCopilotVersions(failures, root));
        WithTempRoot(root => VerifyTempInstallerRules(failures, root));
        WithTempRoot(root => VerifyDeleteSafety(failures, root));
        VerifyPagefileSettingsParsing(failures);
        VerifyPagefileAssessment(failures);
        VerifyPagefileCapture(failures);
        VerifyNestedFolderTotals(failures);
    }

    private static void VerifyCopilotVersions(List<string> failures, string root)
    {
        var pkg = Path.Combine(root, "pkg");
        foreach (var version in new[] { "1.0.9", "1.0.10", "1.0.100" })
        {
            CreateFile(Path.Combine(pkg, "win32-x64", version, "index.js"), 1000);
        }

        CreateFile(Path.Combine(pkg, "universal", "1.0.48", "index.js"), 500);
        CreateFile(Path.Combine(pkg, "tmp", "download.part"), 500);

        var idle = DiskCleanupService.Scan(pkg, Path.Combine(root, "no-temp"), DateTime.Now, isCopilotRunning: false, CancellationToken.None);
        var byName = idle.CopilotCandidates.ToDictionary(candidate => Path.GetFileName(candidate.Path) + "@" + Path.GetFileName(Path.GetDirectoryName(candidate.Path)));

        Check(failures, "Copilot: 版は数値順で比較し 1.0.100 を最新として保持する",
            byName["1.0.100@win32-x64"].Safety == DiskCleanupSafety.Protected
            && byName["1.0.10@win32-x64"].Safety == DiskCleanupSafety.Safe
            && byName["1.0.9@win32-x64"].Safety == DiskCleanupSafety.Safe);
        Check(failures, "Copilot: 環境ごとに最新版を保持し、tmp は候補にしない",
            byName["1.0.48@universal"].Safety == DiskCleanupSafety.Protected
            && idle.CopilotCandidates.Count == 4);
        Check(failures, "Copilot: 「安全」の旧版は既定で選択され、保持は選択できない",
            byName["1.0.9@win32-x64"].IsSelected && !byName["1.0.100@win32-x64"].IsSelected
            && !SetAndRead(byName["1.0.100@win32-x64"]));

        var running = DiskCleanupService.Scan(pkg, Path.Combine(root, "no-temp"), DateTime.Now, isCopilotRunning: true, CancellationToken.None);
        Check(failures, "Copilot: 実行中は旧版もすべて保持にする",
            running.IsCopilotRunning && running.CopilotCandidates.All(candidate => !candidate.CanDelete));
    }

    private static void VerifyTempInstallerRules(List<string> failures, string root)
    {
        var temp = Path.Combine(root, "Temp");
        var now = DateTime.Now;
        var old = now.AddDays(-3);

        CreateFile(Path.Combine(temp, "old-setup.exe"), InstallerBytes, old);
        CreateFile(Path.Combine(temp, "fresh-update.msix"), InstallerBytes, now);
        CreateFile(Path.Combine(temp, "small.exe"), 1024, old);
        CreateFile(Path.Combine(temp, "notes.zip"), InstallerBytes, old);
        CreateFile(Path.Combine(temp, "vscode-stable-user-x64", "CodeSetup.exe"), InstallerBytes, old);
        CreateFile(Path.Combine(temp, "vscode-stable-user-x64", "CodeSetup.flag"), 4, old);
        CreateFile(Path.Combine(temp, "mixed", "Setup.exe"), InstallerBytes, old);
        CreateFile(Path.Combine(temp, "mixed", "data.bin"), 2L * 1024 * 1024, old);
        CreateFile(Path.Combine(temp, "nested", "Setup.exe"), InstallerBytes, old);
        Directory.CreateDirectory(Path.Combine(temp, "nested", "work"));
        CreateFile(Path.Combine(temp, "running-setup.exe"), InstallerBytes, old);
        Directory.SetLastWriteTime(Path.Combine(temp, "vscode-stable-user-x64"), old);

        using var locked = new FileStream(Path.Combine(temp, "running-setup.exe"), FileMode.Open, FileAccess.Read, FileShare.None);
        var scan = DiskCleanupService.Scan(null, temp, now, isCopilotRunning: false, CancellationToken.None);
        var byName = scan.TempCandidates.ToDictionary(candidate => Path.GetFileName(candidate.Path));

        Check(failures, "Temp: 24 時間より古いインストーラーは「安全」",
            byName.TryGetValue("old-setup.exe", out var oldSetup) && oldSetup.Safety == DiskCleanupSafety.Safe && oldSetup.IsSelected);
        Check(failures, "Temp: 24 時間以内のものは「注意」で既定では選択しない",
            byName.TryGetValue("fresh-update.msix", out var fresh) && fresh.Safety == DiskCleanupSafety.Caution && !fresh.IsSelected);
        Check(failures, "Temp: インストーラーと小さな付随ファイルだけのフォルダは候補にする",
            byName.TryGetValue("vscode-stable-user-x64", out var editor) && editor.IsDirectory
            && editor.Safety == DiskCleanupSafety.Safe && editor.SizeBytes == InstallerBytes + 4);
        Check(failures, "Temp: 小さい exe・インストーラー以外・他のデータやサブフォルダを含むフォルダは候補にしない",
            !byName.ContainsKey("small.exe") && !byName.ContainsKey("notes.zip")
            && !byName.ContainsKey("mixed") && !byName.ContainsKey("nested"));
        Check(failures, "Temp: 使用中のインストーラーは「保持」で削除できない",
            byName.TryGetValue("running-setup.exe", out var running) && !running.CanDelete);
    }

    private static void VerifyDeleteSafety(List<string> failures, string root)
    {
        var temp = Path.Combine(root, "Temp");
        var pkg = Path.Combine(root, "pkg");
        var old = DateTime.Now.AddDays(-3);
        CreateFile(Path.Combine(temp, "old-setup.exe"), InstallerBytes, old);
        CreateFile(Path.Combine(temp, "busy-setup.exe"), InstallerBytes, old);
        CreateFile(Path.Combine(pkg, "win32-x64", "1.0.1", "index.js"), 2000);
        CreateFile(Path.Combine(pkg, "win32-x64", "1.0.2", "index.js"), 2000);

        // 旧版の中にリンクがあっても、リンク先の実データは消さない。
        var outside = Path.Combine(root, "outside");
        CreateFile(Path.Combine(outside, "keep.txt"), 10);
        var linked = UserDataReclaimTests.TryCreateJunction(Path.Combine(pkg, "win32-x64", "1.0.1", "linked"), outside);

        var scan = DiskCleanupService.Scan(pkg, temp, DateTime.Now, isCopilotRunning: false, CancellationToken.None);
        var targets = scan.All.Where(candidate => candidate.CanDelete).ToList();
        var outsideFile = Path.Combine(root, "elsewhere.exe");
        CreateFile(outsideFile, 100);
        targets.Add(new DiskCleanupCandidate(DiskCleanupCategory.TempInstaller, outsideFile, false, "elsewhere.exe", "", 100, old, DiskCleanupSafety.Safe));

        // スキャン後に使用中になったファイルは削除直前の確認でスキップする。
        using (new FileStream(Path.Combine(temp, "busy-setup.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = DiskCleanupService.Delete(targets, pkg, temp, () => false);
            Check(failures, "削除: 安全な候補は削除し、回収量を数える",
                !File.Exists(Path.Combine(temp, "old-setup.exe"))
                && !Directory.Exists(Path.Combine(pkg, "win32-x64", "1.0.1"))
                && result.ReclaimedBytes >= InstallerBytes + 2000);
            Check(failures, "削除: 使用中ファイルとルート外のパスはスキップする",
                File.Exists(Path.Combine(temp, "busy-setup.exe")) && File.Exists(outsideFile) && result.SkippedCount == 2);
            Check(failures, "削除: 最新版の Copilot CLI は残す", Directory.Exists(Path.Combine(pkg, "win32-x64", "1.0.2")));
        }

        Check(failures, "削除: 旧版内のジャンクション先は削除しない",
            linked && File.Exists(Path.Combine(outside, "keep.txt")));
        Check(failures, "削除: 一時的な退避名（.turtle-deleting）を残さない",
            !Directory.EnumerateFileSystemEntries(root, "*" + DiskCleanupService.TombstoneSuffix, SearchOption.AllDirectories).Any());

        CreateFile(Path.Combine(pkg, "win32-x64", "1.0.0", "index.js"), 2000);
        var rescan = DiskCleanupService.Scan(pkg, temp, DateTime.Now, isCopilotRunning: false, CancellationToken.None);
        var blocked = DiskCleanupService.Delete(rescan.CopilotCandidates.Where(candidate => candidate.CanDelete), pkg, temp, () => true);
        Check(failures, "削除: スキャン後に Copilot CLI が起動したら旧版を消さない",
            Directory.Exists(Path.Combine(pkg, "win32-x64", "1.0.0")) && blocked.SkippedCount == 1);
    }

    private static void VerifyPagefileSettingsParsing(List<string> failures)
    {
        Check(failures, "ページファイル設定: ?:\\pagefile.sys は全ドライブ自動管理",
            SystemMonitorService.ParsePagingFiles([@"?:\pagefile.sys"]) is { Mode: PagefileManagementMode.SystemManaged, CanGrow: true });
        Check(failures, "ページファイル設定: サイズ 0 0 はシステム管理サイズ",
            SystemMonitorService.ParsePagingFiles([@"C:\pagefile.sys 0 0"]).Mode == PagefileManagementMode.SystemManaged);
        Check(failures, "ページファイル設定: 初期と最大が異なるカスタムは拡張ありとして扱う",
            SystemMonitorService.ParsePagingFiles([@"C:\pagefile.sys 4096 8192"]) is { Mode: PagefileManagementMode.Custom, CanGrow: true, MaximumBytes: 8192L * 1024 * 1024 });
        Check(failures, "ページファイル設定: 初期=最大は固定サイズで拡張しない",
            SystemMonitorService.ParsePagingFiles([@"C:\pagefile.sys 8192 8192"]) is { Mode: PagefileManagementMode.Custom, CanGrow: false });
        Check(failures, "ページファイル設定: 値がなければページファイルなし",
            SystemMonitorService.ParsePagingFiles([""]).Mode == PagefileManagementMode.None
            && SystemMonitorService.ParsePagingFiles(null).Mode == PagefileManagementMode.None);
    }

    private static void VerifyPagefileAssessment(List<string> failures)
    {
        const ulong gb = 1024UL * 1024 * 1024;
        var auto = new PagefileSettings(PagefileManagementMode.SystemManaged, null, null);
        PagefileSnapshot Snapshot(ulong committed, long? growth) =>
            new(48 * gb, 52 * gb, committed, 4L * 1024 * 1024 * 1024, 0, 0, growth, auto);

        Check(failures, "ページファイル評価: 余裕があれば「余裕あり」",
            SystemMonitorService.AssessPagefile(Snapshot(26 * gb, 0)) is { Level: PagefileAssessmentLevel.Normal, BadgeText: "余裕あり" });
        Check(failures, "ページファイル評価: 75% 以上は「注意」",
            SystemMonitorService.AssessPagefile(Snapshot(42 * gb, 0)).Level == PagefileAssessmentLevel.Warning);
        Check(failures, "ページファイル評価: 90% 以上は「逼迫」",
            SystemMonitorService.AssessPagefile(Snapshot(49 * gb, 0)) is { Level: PagefileAssessmentLevel.Critical, BadgeText: "逼迫" });
        Check(failures, "ページファイル評価: ハブ起動後の拡張を最優先で知らせる",
            SystemMonitorService.AssessPagefile(Snapshot(20 * gb, SystemMonitorService.PagefileGrowthNoticeBytes)) is { BadgeText: "拡張あり" });
    }

    private static void VerifyPagefileCapture(List<string> failures)
    {
        var snapshot = new SystemMonitorService().CapturePagefile();
        Check(failures, "ページファイル取得: コミット上限・コミット量・物理メモリを取得できる",
            snapshot.PhysicalMemoryBytes > 0 && snapshot.CommitLimitBytes > 0
            && snapshot.CommittedBytes > 0 && snapshot.CommittedBytes <= snapshot.CommitLimitBytes
            && snapshot.PagefileAllocatedBytes >= 0 && snapshot.PagefileInUseBytes <= Math.Max(snapshot.PagefileAllocatedBytes, 0));
    }

    private static void VerifyNestedFolderTotals(List<string> failures)
    {
        var code = Measured(@"C:\Users\u\AppData\Roaming\Code", 100);
        var chat = Measured(@"C:\Users\u\AppData\Roaming\Code\User\workspaceStorage", 30);
        var codeInsiders = Measured(@"C:\Users\u\AppData\Roaming\Code - Insiders", 50);
        Check(failures, "フォルダ計: 入れ子のフォルダを二重に数えない（似た名前の兄弟は別に数える）",
            SystemMonitorService.SumWithoutNestedDuplicates([code, chat, codeInsiders]) == 150);
    }

    private static FolderUsageItem Measured(string path, long bytes)
    {
        var item = new FolderUsageItem("X", "App", "x", path, path, "", false);
        item.ApplyMeasuredSize(bytes, 1, bytes + " B");
        return item;
    }

    private static bool SetAndRead(DiskCleanupCandidate candidate)
    {
        candidate.IsSelected = true;
        return candidate.IsSelected;
    }

    private static void CreateFile(string path, long length, DateTime? lastWrite = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(length);
        }

        if (lastWrite is DateTime time)
        {
            File.SetLastWriteTime(path, time);
        }
    }

    private static void WithTempRoot(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), $"TurtleHub_DiskCleanup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            test(root);
        }
        finally
        {
            // ジャンクションの先を辿らないよう、本体と同じ削除処理で後始末する。
            FileSystemCleanup.DeleteDirectoryBestEffort(root);
        }
    }

    private static void Check(List<string> failures, string name, bool condition)
    {
        if (!condition)
        {
            failures.Add(name);
        }
    }
}
