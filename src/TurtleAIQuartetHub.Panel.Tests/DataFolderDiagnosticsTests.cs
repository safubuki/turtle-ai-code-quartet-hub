using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

// 容量の解析（診断コード H1〜H8 / V1〜V11 / G1〜G11）が、想定した現象で想定したコードを出すことを確認する。
internal static class DataFolderDiagnosticsTests
{
    public const int Count = 14;

    private const long MB = 1024L * 1024;
    private const long GB = 1024L * MB;

    private static readonly string[] Slots = ["A", "B", "C", "D"];

    public static void Run(List<string> failures)
    {
        var healthyHub = EvaluateHub(dedicated: false, ("panel.log", 2 * MB), ("slots.json", 7 * 1024), (@"config\turtle-ai-quartet-hub.json", 4 * 1024));
        Check(failures, "解析: 小さい Hub データは問題なし",
            healthyHub.Findings.Count == 0
            && healthyHub.Level == SystemHealthLevel.Normal
            && healthyHub.KeywordText.StartsWith("問題なし", StringComparison.Ordinal),
            healthyHub.KeywordText);

        var leftover = EvaluateHub(dedicated: false, (@"user-data\A\Cache\data_1", 900 * MB), (@"user-data\B\User\workspaceStorage\x\state.vscdb", 700 * MB));
        Check(failures, "解析: 専用プロファイル無効なのに残る user-data は H1（1 GB 以上で要対処）",
            Codes(leftover) == "H1" && leftover.Findings[0].Level == SystemHealthLevel.Critical,
            Codes(leftover));

        var dedicated = EvaluateHub(
            dedicated: true,
            (@"user-data\A\Cache\data_1", 600 * MB),
            (@"user-data\B\GPUCache\data_1", 100 * MB),
            (@"user-data\A\User\workspaceStorage\abc\state.vscdb", 1200 * MB),
            (@"user-data\old-slot\Cache\data_1", 80 * MB));
        Check(failures, "解析: 専用プロファイルのキャッシュ・ワークスペースデータ・旧スロット名を H2 / H3 / H6 に分ける",
            Codes(dedicated) == "H3,H2,H6"
            && dedicated.Findings.Single(finding => finding.Code == "H2").Symptom.Contains("A 600.0 MB", StringComparison.Ordinal),
            string.Join(" / ", dedicated.Findings.Select(finding => $"{finding.Code}:{finding.Symptom}")));

        var hubOther = EvaluateHub(dedicated: false, ("panel.log", 12 * MB), (@"backup\big.zip", 300 * MB));
        Check(failures, "解析: Hub の肥大したログは H7、ハブが作らないデータは H8（名前付き）",
            Codes(hubOther) == "H8,H7"
            && hubOther.Findings[0].Symptom.Contains("backup", StringComparison.Ordinal),
            Codes(hubOther));

        var vsCodeCache = EvaluateIde(DataFolderKind.VsCode, (@"Cache\Cache_Data\f_1", 1500 * MB), (@"CachedData\x", 10 * MB));
        var agyCache = EvaluateIde(DataFolderKind.Antigravity, (@"Cache\Cache_Data\f_1", 1500 * MB));
        Check(failures, "解析: IDE のキャッシュは V1 / G1 で、回収の有無で原因の説明を変える",
            Codes(vsCodeCache) == "V1" && Codes(agyCache) == "G1"
            && vsCodeCache.Findings[0].Cause.Contains("1 つも起動していない", StringComparison.Ordinal)
            && agyCache.Findings[0].Cause.Contains("回収していません", StringComparison.Ordinal),
            $"{Codes(vsCodeCache)} / {Codes(agyCache)}");

        var workspaces = new List<DataFolderDiagnosticsService.WorkspaceStorageEntry>
        {
            new(@"User\workspaceStorage\a", 2 * GB, "big-repo", IsOrphan: false),
            new(@"User\workspaceStorage\b", 400 * MB, "deleted-repo", IsOrphan: true)
        };
        var vsCodeWorkspace = EvaluateIde(
            DataFolderKind.VsCode,
            workspaces,
            (@"User\workspaceStorage\a\state.vscdb", 2 * GB),
            (@"User\workspaceStorage\b\state.vscdb", 400 * MB));
        Check(failures, "解析: ワークスペースデータの肥大は V3（最大のフォルダ名付き）、消えたフォルダの分は V4",
            Codes(vsCodeWorkspace) == "V3,V4"
            && vsCodeWorkspace.Findings[0].Symptom.Contains("big-repo", StringComparison.Ordinal)
            && vsCodeWorkspace.Findings[0].Symptom.Contains("1 件", StringComparison.Ordinal),
            string.Join(" / ", vsCodeWorkspace.Findings.Select(finding => $"{finding.Code}:{finding.Symptom}")));

        var vsCodeMisc = EvaluateIde(
            DataFolderKind.VsCode,
            (@"CachedExtensionVSIXs\ext.vsix", 400 * MB),
            (@"User\globalStorage\github.copilot-chat\index.db", 1100 * MB),
            (@"User\globalStorage\ms-python.python\x", 50 * MB),
            (@"logs\20261030\main.log", 350 * MB),
            (@"Crashpad\reports\dump.dmp", 250 * MB),
            (@"Backups\x\file", 350 * MB),
            (@"User\History\x\entry", 600 * MB),
            (@"WebStorage\1\CacheStorage\x", 1100 * MB));
        Check(failures, "解析: VSIX・拡張機能データ・ログ・ダンプ・退避・履歴・ブラウザ保存領域をそれぞれ V2 / V5〜V10 に分ける",
            Codes(vsCodeMisc).Split(',').OrderBy(code => code, StringComparer.Ordinal).SequenceEqual(new[] { "V10", "V2", "V5", "V6", "V7", "V8", "V9" })
            && vsCodeMisc.Findings.Single(finding => finding.Code == "V5").Symptom.Contains("github.copilot-chat", StringComparison.Ordinal),
            Codes(vsCodeMisc));

        var unknown = EvaluateIde(DataFolderKind.VsCode, (@"User\profiles\abc\globalStorage\x", 1200 * MB), (@"Mystery\x", 50 * MB));
        Check(failures, "解析: 分類できない 1 GB 以上のフォルダは V11 で場所を示す（小さいものは出さない）",
            Codes(unknown) == "V11"
            && unknown.Findings[0].Symptom.Contains(@"User\profiles", StringComparison.Ordinal)
            && !unknown.Findings[0].Symptom.Contains("Mystery", StringComparison.Ordinal),
            Codes(unknown));

        Check(failures, "解析: 結論と伝えるキーワードは要対処を先に並べる",
            leftover.ConclusionText.Contains("対処が必要", StringComparison.Ordinal)
            && vsCodeCache.KeywordText.StartsWith("V1（1.5 GB）／合計", StringComparison.Ordinal),
            $"{leftover.ConclusionText} / {vsCodeCache.KeywordText}");

        Check(failures, "解析: 容量の多い場所は user-data・各スロット・User を開いて中身で並べる",
            dedicated.HeavyEntries.Count > 0
            && dedicated.HeavyEntries[0].RelativePath == @"user-data\A\User\workspaceStorage"
            && dedicated.HeavyEntries[0].Label.Contains("ワークスペース", StringComparison.Ordinal)
            && dedicated.HeavyEntries.Any(entry => entry.RelativePath == @"user-data\A\Cache" && entry.Label == "再生成できるキャッシュ"),
            string.Join(" / ", dedicated.HeavyEntries.Select(entry => entry.RelativePath)));

        var index = new FolderSizeIndex(maxDepth: 2);
        index.AddFile(@"a\b\c\d\file.bin", 10);
        index.AddFile(@"a\x.txt", 5);
        index.AddFile("root.txt", 1);
        Check(failures, "解析: サイズ索引は深い階層も上位フォルダと合計に数え、直下のファイルも 1 件で持つ",
            index.TotalBytes == 16 && index.FileCount == 3
            && index.Size("a") == 15 && index.Size(@"a\b") == 10 && index.Size(@"a\b\c") == 0
            && index.IsFile("root.txt") && index.Size("root.txt") == 1);

        Check(failures, "解析: VS Code の file URI（c%3A）をローカルパスに戻す",
            DataFolderDiagnosticsService.TryGetLocalPath(new Uri("file:///c%3A/work/my%20repo")) == @"c:\work\my repo",
            DataFolderDiagnosticsService.TryGetLocalPath(new Uri("file:///c%3A/work/my%20repo")));
        Check(failures, "解析: UNC の file URI は \\\\server\\share に戻す",
            DataFolderDiagnosticsService.TryGetLocalPath(new Uri("file://server/share/repo")) == @"\\server\share\repo",
            DataFolderDiagnosticsService.TryGetLocalPath(new Uri("file://server/share/repo")));
        Check(failures, "解析: リモートのワークスペースは存在確認しない（消えた扱いにしない）",
            DataFolderDiagnosticsService.TryGetLocalPath(new Uri("vscode-vfs://github/owner/repo")) is null);
    }

    private static DataFolderDiagnosis EvaluateHub(bool dedicated, params (string Path, long Bytes)[] files)
    {
        return DataFolderDiagnosticsService.Evaluate(
            DataFolderKind.Hub,
            @"C:\hub",
            BuildIndex(files),
            new DataFolderAnalysisContext(dedicated, Slots, HubReclaimsIdeCaches: false),
            []);
    }

    private static DataFolderDiagnosis EvaluateIde(DataFolderKind kind, params (string Path, long Bytes)[] files)
    {
        return EvaluateIde(kind, [], files);
    }

    private static DataFolderDiagnosis EvaluateIde(
        DataFolderKind kind,
        IReadOnlyList<DataFolderDiagnosticsService.WorkspaceStorageEntry> workspaces,
        params (string Path, long Bytes)[] files)
    {
        return DataFolderDiagnosticsService.Evaluate(
            kind,
            @"C:\ide",
            BuildIndex(files),
            new DataFolderAnalysisContext(false, Slots, HubReclaimsIdeCaches: kind == DataFolderKind.VsCode),
            workspaces);
    }

    private static FolderSizeIndex BuildIndex((string Path, long Bytes)[] files)
    {
        var index = new FolderSizeIndex(maxDepth: 5);
        foreach (var (path, bytes) in files)
        {
            index.AddFile(path, bytes);
        }

        return index;
    }

    private static string Codes(DataFolderDiagnosis diagnosis) => string.Join(",", diagnosis.Findings.Select(finding => finding.Code));

    private static void Check(List<string> failures, string name, bool condition, string? detail = null)
    {
        if (!condition)
        {
            failures.Add(detail is null ? name : $"{name}: {detail}");
        }
    }
}
