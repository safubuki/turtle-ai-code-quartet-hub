using System.IO;
using System.Text.Json;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

/// <summary>
/// Hub データ・VS Code / Antigravity IDE のユーザーデータが大きいとき、どこが重く、なぜ増えたのかを判定する。
/// 起こりうる現象を事前に洗い出した診断コード（H1〜H8、V1〜V11、G1〜G11）で結果を返し、
/// 利用者が記号と大きさだけを口頭で伝えれば原因が分かるようにする。一覧は docs/data-folder-diagnostics.md。
/// </summary>
public static class DataFolderDiagnosticsService
{
    private const long MB = 1024L * 1024;
    private const long GB = 1024L * MB;

    // フォルダを 1 回だけ走査し、この深さまでのフォルダ別合計を持つ
    // （Hub の user-data\A\User\globalStorage\<拡張機能> が 5 階層目）。
    private const int IndexDepth = 5;

    private const string UserDataDirectoryName = "user-data";
    private const string UserDirectoryName = "User";

    // 消しても次回起動時に作り直されるキャッシュ（拡張機能のインストーラー・ログ・クラッシュダンプは別に数える）。
    private static readonly HashSet<string> RegenerableCacheNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "CachedData", "CachedExtensions", "CachedProfilesData", "Code Cache", "GPUCache",
        "DawnGraphiteCache", "DawnWebGPUCache", "ShaderCache", "GrShaderCache", "VideoDecodeStats",
        "blob_storage", "clp", "agent-host", "copilot-terminal-output"
    };

    // サインイン情報などを含むブラウザ保存領域。再生成できないので削除は勧めない。
    private static readonly HashSet<string> WebStorageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "WebStorage", "Service Worker", "IndexedDB", "Local Storage", "Session Storage", "Partitions", "Shared Dictionary"
    };

    private const string ExtensionInstallerCacheName = "CachedExtensionVSIXs";
    private const string LogsName = "logs";
    private const string CrashDumpName = "Crashpad";
    private const string BackupsName = "Backups";
    private const string WorkspaceStorageName = "workspaceStorage";
    private const string GlobalStorageName = "globalStorage";
    private const string HistoryName = "History";

    private static readonly HashSet<string> KnownIdeRootNames = new(
        RegenerableCacheNames.Concat(WebStorageNames).Concat(
        [
            ExtensionInstallerCacheName, LogsName, CrashDumpName, BackupsName, UserDirectoryName
        ]),
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> KnownIdeUserNames = new(StringComparer.OrdinalIgnoreCase)
    {
        WorkspaceStorageName, GlobalStorageName, HistoryName
    };

    private static readonly HashSet<string> KnownHubRootNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "config", UserDataDirectoryName, "slots.json", "update-badges.json"
    };

    // 名前から用途を説明する（容量の多い場所の一覧用）。
    private static readonly Dictionary<string, string> KnownLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        [ExtensionInstallerCacheName] = "拡張機能の古いインストーラー",
        [LogsName] = "ログ",
        [CrashDumpName] = "クラッシュダンプ",
        [BackupsName] = "未保存ファイルの退避",
        [WorkspaceStorageName] = "ワークスペースごとのデータ（チャット履歴など）",
        [GlobalStorageName] = "拡張機能のデータ",
        [HistoryName] = "ローカル履歴（タイムライン）",
        [UserDataDirectoryName] = "スロット別の VS Code データ",
        ["config"] = "ハブの設定",
        ["slots.json"] = "スロットの保存状態",
        ["update-badges.json"] = "更新バッジの既読状態",
        ["panel.log"] = "ハブの診断ログ"
    };

    internal sealed record WorkspaceStorageEntry(string RelativePath, long Bytes, string WorkspaceName, bool IsOrphan);

    public static DataFolderDiagnosis Analyze(
        DataFolderKind kind,
        string rootPath,
        DataFolderAnalysisContext context,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var index = FolderSizeIndex.Build(rootPath, IndexDepth, progress, cancellationToken);
        var workspaces = GetWorkspaceStorageDirectories(kind, index)
            .SelectMany(directory => index.Children(directory))
            .Select(entry => ReadWorkspaceStorageEntry(rootPath, entry, index.Size(entry)))
            .ToList();
        return Evaluate(kind, rootPath, index, context, workspaces);
    }

    internal static DataFolderDiagnosis Evaluate(
        DataFolderKind kind,
        string rootPath,
        FolderSizeIndex index,
        DataFolderAnalysisContext context,
        IReadOnlyList<WorkspaceStorageEntry> workspaces)
    {
        var findings = kind == DataFolderKind.Hub
            ? EvaluateHub(index, context, workspaces)
            : EvaluateIde(kind, index, context, workspaces);
        var ordered = findings
            .OrderByDescending(finding => finding.Level)
            .ThenByDescending(finding => finding.Bytes)
            .ToList();
        return new DataFolderDiagnosis(kind, rootPath, index.TotalBytes, index.FileCount, ordered, BuildHeavyEntries(index));
    }

    private static List<DataFolderFinding> EvaluateHub(
        FolderSizeIndex index,
        DataFolderAnalysisContext context,
        IReadOnlyList<WorkspaceStorageEntry> workspaces)
    {
        var findings = new List<DataFolderFinding>();
        var userDataBytes = index.Size(UserDataDirectoryName);
        if (!context.UseDedicatedUserDataDirs)
        {
            Add(findings, "H1", Grade(userDataBytes, 50 * MB, 1 * GB), userDataBytes,
                $"使われていないスロット別の VS Code データ（user-data）が {Format(userDataBytes)} 残っています。",
                "以前スロット別プロファイル（useDedicatedUserDataDirs）を使っていたときのデータです。ハブは起動時に自動で削除しますが、このデータを使う VS Code の窓が開いているとファイルがロックされて消し残ります。",
                "VS Code の窓をすべて閉じてからハブを再起動してください。自動で削除されます。");
        }
        else
        {
            var slotDirectories = index.Children(UserDataDirectoryName).ToList();
            var knownSlots = slotDirectories
                .Where(directory => context.SlotDirectoryNames.Contains(FolderSizeIndex.NameOf(directory), StringComparer.OrdinalIgnoreCase))
                .ToList();

            var cacheBySlot = knownSlots.ToDictionary(
                slot => slot,
                slot => index.Children(slot).Where(child => RegenerableCacheNames.Contains(FolderSizeIndex.NameOf(child))).Sum(index.Size));
            var cacheBytes = cacheBySlot.Values.Sum();
            Add(findings, "H2", Grade(cacheBytes, 500 * MB, 2 * GB), cacheBytes,
                $"スロット別の VS Code キャッシュが合計 {Format(cacheBytes)} あります（{DescribeBySlot(cacheBySlot)}）。",
                "スロット別プロファイルではスロットごとにキャッシュを持ちます。ハブは起動時に回収しますが、VS Code の窓が開いているスロットは回収を見送るため、開きっぱなしの使い方だと増え続けます。",
                "VS Code の窓を閉じてからハブを再起動すると回収されます。スロット別プロファイルが不要なら、設定の useDedicatedUserDataDirs を false にすると次回起動時にまとめて削除されます。");

            var workspaceBySlot = knownSlots.ToDictionary(
                slot => slot,
                slot => index.Size(Join(slot, UserDirectoryName, WorkspaceStorageName)));
            var workspaceBytes = workspaceBySlot.Values.Sum();
            var orphanBytes = workspaces.Where(entry => entry.IsOrphan).Sum(entry => entry.Bytes);
            Add(findings, "H3", Grade(workspaceBytes, 1 * GB, 3 * GB), workspaceBytes,
                $"スロット別のワークスペースデータ（チャット履歴など）が合計 {Format(workspaceBytes)} あります（{DescribeBySlot(workspaceBySlot)}）。"
                    + (orphanBytes > 0 ? $"うち {Format(orphanBytes)} は、もう存在しないフォルダのデータです。" : string.Empty),
                "スロット別プロファイルでは、Copilot のチャット履歴や拡張機能のワークスペース別データがスロットごとに別々に溜まります。同じフォルダを別のスロットで開くと重複して作られます。",
                "存在しないフォルダのデータは、VS Code を閉じてから削除できます。大きいものはチャット履歴の可能性が高いため、必要か確認してから削除してください。");

            var extensionBytes = knownSlots
                .SelectMany(slot => index.Children(Join(slot, UserDirectoryName, GlobalStorageName)))
                .GroupBy(FolderSizeIndex.NameOf, StringComparer.OrdinalIgnoreCase)
                .Select(group => (Name: group.Key, Bytes: group.Sum(index.Size)))
                .OrderByDescending(item => item.Bytes)
                .ToList();
            var globalBytes = knownSlots.Sum(slot => index.Size(Join(slot, UserDirectoryName, GlobalStorageName)));
            Add(findings, "H4", Grade(globalBytes, 1 * GB, 3 * GB), globalBytes,
                $"スロット別の拡張機能データ（globalStorage）が合計 {Format(globalBytes)} あります"
                    + (extensionBytes.Count > 0 ? $"（最大: {extensionBytes[0].Name} {Format(extensionBytes[0].Bytes)}）。" : "。"),
                "拡張機能が保存するデータ（Copilot のインデックスや言語サーバーのキャッシュなど）が、スロットごとに作られています。",
                "最大の拡張機能を確認し、その拡張機能のキャッシュ削除機能を使うか、不要なら拡張機能を削除してください。");

            var webBytes = knownSlots
                .SelectMany(slot => index.Children(slot))
                .Where(child => WebStorageNames.Contains(FolderSizeIndex.NameOf(child)))
                .Sum(index.Size);
            Add(findings, "H5", Grade(webBytes, 1 * GB, 3 * GB), webBytes,
                $"スロット別のブラウザ保存領域（WebStorage など）が合計 {Format(webBytes)} あります。",
                "拡張機能や Web ビューが保存するデータで、サインイン情報も含みます。スロットごとに別々に作られます。",
                "サインイン情報を含むため削除はおすすめしません。Web ビューを多用する拡張機能を見直してください。");

            var unknownSlots = slotDirectories.Except(knownSlots).OrderByDescending(index.Size).ToList();
            var unknownSlotBytes = unknownSlots.Sum(index.Size);
            Add(findings, "H6", Grade(unknownSlotBytes, 50 * MB, 1 * GB), unknownSlotBytes,
                $"今のスロット名に対応しないスロット別データが {Format(unknownSlotBytes)} あります（{string.Join("・", unknownSlots.Take(4).Select(FolderSizeIndex.NameOf))}）。",
                "以前のスロット名で作られたデータです。ハブはこのフォルダを使わず、自動でも削除しません。",
                "VS Code を閉じてから user-data 内の該当フォルダを削除できます。");
        }

        var logBytes = index.Children(string.Empty)
            .Where(child => index.IsFile(child)
                && FolderSizeIndex.NameOf(child).StartsWith("panel", StringComparison.OrdinalIgnoreCase)
                && FolderSizeIndex.NameOf(child).EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            .Sum(index.Size);
        Add(findings, "H7", logBytes >= 10 * MB ? SystemHealthLevel.Warning : SystemHealthLevel.Normal, logBytes,
            $"ハブの診断ログ（panel.log）が {Format(logBytes)} あります。",
            "ログは 2 MB を超えるとハブの起動時に切り詰めます。長期間ハブを再起動していないか、切り詰めに失敗しています。",
            "ハブを再起動してください。改善しない場合は panel.log を削除しても問題ありません。");

        var unknownRoot = index.Children(string.Empty)
            .Where(child => !KnownHubRootNames.Contains(FolderSizeIndex.NameOf(child))
                && !(index.IsFile(child) && FolderSizeIndex.NameOf(child).EndsWith(".log", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(index.Size)
            .ToList();
        var unknownRootBytes = unknownRoot.Sum(index.Size);
        Add(findings, "H8", Grade(unknownRootBytes, 200 * MB, 2 * GB), unknownRootBytes,
            $"ハブが作らないデータが {Format(unknownRootBytes)} あります（{string.Join("・", unknownRoot.Take(3).Select(child => $"{FolderSizeIndex.NameOf(child)} {Format(index.Size(child))}"))}）。",
            "設定の stateDirectory が他の用途のフォルダを指しているか、別のツールが同じ場所を使っている可能性があります。",
            "中身を確認してください。ハブのデータには専用の空フォルダを指定するのが安全です。");
        return findings;
    }

    private static List<DataFolderFinding> EvaluateIde(
        DataFolderKind kind,
        FolderSizeIndex index,
        DataFolderAnalysisContext context,
        IReadOnlyList<WorkspaceStorageEntry> workspaces)
    {
        var prefix = kind == DataFolderKind.Antigravity ? "G" : "V";
        var app = kind == DataFolderKind.Antigravity ? "Antigravity IDE" : "VS Code";
        var reclaimed = context.HubReclaimsIdeCaches;
        var findings = new List<DataFolderFinding>();
        var rootChildren = index.Children(string.Empty).ToList();

        var caches = rootChildren
            .Where(child => RegenerableCacheNames.Contains(FolderSizeIndex.NameOf(child)))
            .OrderByDescending(index.Size)
            .ToList();
        var cacheBytes = caches.Sum(index.Size);
        Add(findings, prefix + "1", Grade(cacheBytes, 1 * GB, 3 * GB), cacheBytes,
            $"再生成できるキャッシュが {Format(cacheBytes)} あります"
                + (caches.Count > 0 ? $"（最大: {FolderSizeIndex.NameOf(caches[0])} {Format(index.Size(caches[0]))}）。" : "。"),
            reclaimed
                ? $"ハブは起動時に、{app} が 1 つも起動していないときだけキャッシュを回収します。{app} の窓を開いたままハブを起動し直す使い方だと回収されず、増え続けます。"
                : $"ハブは {app} のキャッシュを回収していません。{app} 自身も古いキャッシュを消さないことがあります。",
            reclaimed
                ? $"{app} をすべて閉じてからハブを起動し直すと、自動で回収されます。"
                : $"{app} を閉じてから、このフォルダの Cache・Code Cache・GPUCache・CachedData を削除できます（次回起動時に作り直されます）。");

        var installerBytes = index.Size(ExtensionInstallerCacheName);
        Add(findings, prefix + "2", Grade(installerBytes, 300 * MB, 1 * GB), installerBytes,
            $"拡張機能の更新で残った古いインストーラー（CachedExtensionVSIXs）が {Format(installerBytes)} あります。",
            "拡張機能を更新するたびに古い .vsix が残ります。",
            reclaimed
                ? $"{app} をすべて閉じてからハブを起動し直すと、自動で回収されます。"
                : $"{app} を閉じてから CachedExtensionVSIXs の中身を削除できます（必要なら再ダウンロードされます）。");

        var workspaceBytes = index.Size(Join(UserDirectoryName, WorkspaceStorageName));
        var largestWorkspace = workspaces.OrderByDescending(entry => entry.Bytes).FirstOrDefault();
        var orphans = workspaces.Where(entry => entry.IsOrphan).ToList();
        var orphanBytes = orphans.Sum(entry => entry.Bytes);
        Add(findings, prefix + "3", Grade(workspaceBytes, 2 * GB, 5 * GB), workspaceBytes,
            $"ワークスペースごとのデータ（チャット履歴など）が {Format(workspaceBytes)}・{workspaces.Count} 件あります"
                + (largestWorkspace is not null ? $"（最大: {largestWorkspace.WorkspaceName} {Format(largestWorkspace.Bytes)}）。" : "。")
                + (orphans.Count > 0 ? $"うち {orphans.Count} 件・{Format(orphanBytes)} は、もう存在しないフォルダのデータです。" : string.Empty),
            "フォルダを開くたびに、Copilot のチャット履歴や拡張機能のワークスペース別データが作られ、閉じても残ります。",
            $"もう使わないフォルダのデータは、{app} を閉じてから削除できます。大きいものはチャット履歴の可能性が高いため、必要か確認してください。");

        Add(findings, prefix + "4", Grade(orphanBytes, 300 * MB, 1 * GB), orphanBytes,
            $"もう存在しないフォルダのワークスペースデータが {orphans.Count} 件・{Format(orphanBytes)} 残っています。",
            $"フォルダを削除・移動しても、{app} はそのワークスペースのデータを自動では消しません。",
            $"{app} を閉じてから User\\workspaceStorage 内の該当フォルダを削除できます。");

        var extensions = index.Children(Join(UserDirectoryName, GlobalStorageName)).OrderByDescending(index.Size).ToList();
        var globalBytes = index.Size(Join(UserDirectoryName, GlobalStorageName));
        Add(findings, prefix + "5", Grade(globalBytes, 1 * GB, 3 * GB), globalBytes,
            $"拡張機能のデータ（globalStorage）が {Format(globalBytes)} あります"
                + (extensions.Count > 0 ? $"（最大: {FolderSizeIndex.NameOf(extensions[0])} {Format(index.Size(extensions[0]))}）。" : "。"),
            "拡張機能が保存するデータ（Copilot のインデックス、言語サーバーやツールのキャッシュなど）です。",
            "最大の拡張機能を確認し、その拡張機能のキャッシュ削除機能を使うか、不要なら拡張機能を削除してください。");

        var logBytes = index.Size(LogsName);
        Add(findings, prefix + "6", Grade(logBytes, 300 * MB, 1 * GB), logBytes,
            $"ログ（logs）が {Format(logBytes)} あります。",
            $"{app} は起動のたびにログを残します。拡張機能が詳細なログを出していると急に増えます。",
            reclaimed
                ? $"{app} をすべて閉じてからハブを起動し直すと、自動で回収されます。"
                : $"{app} を閉じてから logs の中身を削除できます。");

        var crashBytes = index.Size(CrashDumpName);
        Add(findings, prefix + "7", Grade(crashBytes, 200 * MB, 1 * GB), crashBytes,
            $"クラッシュダンプ（Crashpad）が {Format(crashBytes)} あります。",
            $"{app} やその拡張機能が異常終了した記録です。繰り返し落ちている可能性があります。",
            reclaimed
                ? "ハブの起動時に自動で削除されます。頻繁に増える場合は、落ちる原因の調査が必要です。"
                : "削除して問題ありません。頻繁に増える場合は、落ちる原因の調査が必要です。");

        var backupBytes = index.Size(BackupsName);
        Add(findings, prefix + "8", Grade(backupBytes, 300 * MB, 1 * GB), backupBytes,
            $"未保存ファイルの退避（Backups）が {Format(backupBytes)} あります。",
            "保存していない編集内容の退避です。大きいファイルを未保存のまま開いていると増えます。",
            $"{app} で未保存のファイルを保存するか閉じてください。");

        var historyBytes = index.Size(Join(UserDirectoryName, HistoryName));
        Add(findings, prefix + "9", Grade(historyBytes, 500 * MB, 2 * GB), historyBytes,
            $"ローカル履歴（タイムライン）が {Format(historyBytes)} あります。",
            "ファイルを保存するたびに残る履歴です。大きいファイルを頻繁に保存すると増えます。",
            "設定 workbench.localHistory.maxFileSize / maxFileEntries で上限を下げると増えにくくなります。");

        var webBytes = rootChildren.Where(child => WebStorageNames.Contains(FolderSizeIndex.NameOf(child))).Sum(index.Size);
        Add(findings, prefix + "10", Grade(webBytes, 1 * GB, 3 * GB), webBytes,
            $"ブラウザ保存領域（WebStorage など）が {Format(webBytes)} あります。",
            "拡張機能や Web ビューが保存するデータで、サインイン情報も含みます。",
            "サインイン情報を含むため削除はおすすめしません。Web ビューを多用する拡張機能を見直してください。");

        // どの分類にも当てはまらない大きいフォルダ。名前を伝えてもらい、分類と対処を追加していく。
        var unknown = rootChildren
            .Where(child => !KnownIdeRootNames.Contains(FolderSizeIndex.NameOf(child)))
            .Concat(index.Children(UserDirectoryName).Where(child => !KnownIdeUserNames.Contains(FolderSizeIndex.NameOf(child))))
            .Where(child => index.Size(child) >= 1 * GB)
            .OrderByDescending(index.Size)
            .ToList();
        var unknownBytes = unknown.Sum(index.Size);
        Add(findings, prefix + "11", Grade(unknownBytes, 1 * GB, 3 * GB), unknownBytes,
            $"分類できない大きなデータがあります（{string.Join("・", unknown.Take(3).Select(child => $"{child} {Format(index.Size(child))}"))}）。",
            "上のどの分類にも当てはまらないデータです。",
            "このフォルダ名を伝えてください。分類と対処を追加します。");
        return findings;
    }

    // 一覧には「中身で見ないと意味が分からない入れ物」（user-data、各スロット、User）を開いて並べる。
    private static IReadOnlyList<DataFolderHeavyEntry> BuildHeavyEntries(FolderSizeIndex index)
    {
        var leaves = new List<string>();
        CollectLeaves(index, string.Empty, leaves);
        var total = Math.Max(1, index.TotalBytes);
        return leaves
            .Where(path => index.Size(path) > 0)
            .OrderByDescending(index.Size)
            .Take(6)
            .Select(path => new DataFolderHeavyEntry(path, DescribeEntry(path), index.Size(path), index.Size(path) * 100d / total))
            .ToList();
    }

    private static void CollectLeaves(FolderSizeIndex index, string directory, List<string> leaves)
    {
        foreach (var child in index.Children(directory))
        {
            var name = FolderSizeIndex.NameOf(child);
            var parentName = FolderSizeIndex.NameOf(directory);
            var expand = !index.IsFile(child)
                && (string.Equals(name, UserDataDirectoryName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, UserDirectoryName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(parentName, UserDataDirectoryName, StringComparison.OrdinalIgnoreCase));
            if (expand && index.Children(child).Any())
            {
                CollectLeaves(index, child, leaves);
            }
            else
            {
                leaves.Add(child);
            }
        }
    }

    private static string DescribeEntry(string path)
    {
        var name = FolderSizeIndex.NameOf(path);
        if (RegenerableCacheNames.Contains(name))
        {
            return "再生成できるキャッシュ";
        }

        if (WebStorageNames.Contains(name))
        {
            return "ブラウザ保存領域（サインイン情報など）";
        }

        return KnownLabels.TryGetValue(name, out var label) ? label : "その他";
    }

    private static IEnumerable<string> GetWorkspaceStorageDirectories(DataFolderKind kind, FolderSizeIndex index)
    {
        if (kind != DataFolderKind.Hub)
        {
            return [Join(UserDirectoryName, WorkspaceStorageName)];
        }

        return index.Children(UserDataDirectoryName)
            .Select(slot => Join(slot, UserDirectoryName, WorkspaceStorageName));
    }

    // workspace.json の folder / workspace（file: URI）が今も存在するかで、消えたフォルダのデータかを判定する。
    // リモート（vscode-remote:）や読めないものは、存在を確かめられないので残っている扱いにする。
    private static WorkspaceStorageEntry ReadWorkspaceStorageEntry(string rootPath, string relativePath, long bytes)
    {
        try
        {
            var jsonPath = Path.Combine(rootPath, relativePath, "workspace.json");
            if (File.Exists(jsonPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
                foreach (var property in new[] { "folder", "workspace" })
                {
                    if (document.RootElement.TryGetProperty(property, out var value)
                        && value.GetString() is { } uriText
                        && Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
                    {
                        var name = Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.TrimEnd('/') ?? uriText);
                        var localPath = TryGetLocalPath(uri);
                        var isOrphan = localPath is not null
                            && !(property == "folder" ? Directory.Exists(localPath) : File.Exists(localPath));
                        return new WorkspaceStorageEntry(relativePath, bytes, name, isOrphan);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or UriFormatException)
        {
            // 読めない workspace.json は判定対象から外す。
        }

        return new WorkspaceStorageEntry(relativePath, bytes, FolderSizeIndex.NameOf(relativePath), IsOrphan: false);
    }

    // VS Code はドライブ文字のコロンを %3A にした URI（file:///c%3A/work/repo）を書くため、
    // Uri.LocalPath に任せずに自分で Windows のパスへ戻す。file: 以外はローカルではないので null。
    internal static string? TryGetLocalPath(Uri uri)
    {
        if (!uri.IsFile)
        {
            return null;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath).Replace('/', '\\');
        if (!string.IsNullOrEmpty(uri.Host))
        {
            return $@"\\{uri.Host}{path}";
        }

        if (path.Length >= 3 && path[0] == '\\' && char.IsLetter(path[1]) && path[2] == ':')
        {
            path = path[1..];
        }

        return path;
    }

    private static string DescribeBySlot(Dictionary<string, long> bytesBySlot)
    {
        var parts = bytesBySlot
            .Where(pair => pair.Value > 0)
            .OrderByDescending(pair => pair.Value)
            .Select(pair => $"{FolderSizeIndex.NameOf(pair.Key)} {Format(pair.Value)}")
            .ToList();
        return parts.Count == 0 ? "各スロット 0 B" : string.Join("・", parts);
    }

    private static void Add(
        List<DataFolderFinding> findings,
        string code,
        SystemHealthLevel level,
        long bytes,
        string symptom,
        string cause,
        string remedy)
    {
        if (level != SystemHealthLevel.Normal)
        {
            findings.Add(new DataFolderFinding(code, level, symptom, cause, remedy, bytes));
        }
    }

    private static SystemHealthLevel Grade(long bytes, long warningBytes, long criticalBytes)
    {
        if (bytes >= criticalBytes)
        {
            return SystemHealthLevel.Critical;
        }

        return bytes >= warningBytes ? SystemHealthLevel.Warning : SystemHealthLevel.Normal;
    }

    private static string Join(params string[] segments) => string.Join('\\', segments.Where(segment => segment.Length > 0));

    private static string Format(long bytes) => SystemMonitorService.FormatBytes(bytes);
}

/// <summary>
/// フォルダを 1 回だけ走査して、決めた深さまでの各フォルダの合計サイズを持つ。
/// キーはルートからの相対パス（区切りは \、ルートは空文字）。ルート直下のファイルも 1 件として持つ。
/// </summary>
internal sealed class FolderSizeIndex
{
    private readonly int _maxDepth;
    private readonly Dictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase) { [string.Empty] = 0 };
    private readonly Dictionary<string, HashSet<string>> _children = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    public FolderSizeIndex(int maxDepth)
    {
        _maxDepth = maxDepth;
    }

    public long TotalBytes => _sizes[string.Empty];

    public int FileCount { get; private set; }

    public static FolderSizeIndex Build(string rootPath, int maxDepth, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var index = new FolderSizeIndex(maxDepth);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var file in new DirectoryInfo(rootPath).EnumerateFiles("*", options))
        {
            long length;
            try
            {
                length = file.Length;
            }
            catch (IOException)
            {
                continue;
            }

            index.AddFile(Path.GetRelativePath(rootPath, file.FullName), length);
            if (index.FileCount % 2000 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(index.FileCount);
            }
        }

        progress?.Report(index.FileCount);
        return index;
    }

    public void AddFile(string relativePath, long length)
    {
        var segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return;
        }

        FileCount++;
        _sizes[string.Empty] += length;
        if (segments.Length == 1)
        {
            _files.Add(segments[0]);
            AddSize(string.Empty, segments[0], length);
            return;
        }

        var parent = string.Empty;
        var directoryDepth = Math.Min(segments.Length - 1, _maxDepth);
        for (var depth = 0; depth < directoryDepth; depth++)
        {
            var path = parent.Length == 0 ? segments[depth] : $"{parent}\\{segments[depth]}";
            AddSize(parent, path, length);
            parent = path;
        }
    }

    public long Size(string relativePath) => _sizes.GetValueOrDefault(relativePath);

    public IEnumerable<string> Children(string relativePath) =>
        _children.TryGetValue(relativePath, out var children) ? children : [];

    public bool IsFile(string relativePath) => _files.Contains(relativePath);

    public static string NameOf(string relativePath)
    {
        var separator = relativePath.LastIndexOf('\\');
        return separator >= 0 ? relativePath[(separator + 1)..] : relativePath;
    }

    private void AddSize(string parent, string path, long length)
    {
        _sizes[path] = _sizes.GetValueOrDefault(path) + length;
        if (!_children.TryGetValue(parent, out var children))
        {
            children = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _children[parent] = children;
        }

        children.Add(path);
    }
}
