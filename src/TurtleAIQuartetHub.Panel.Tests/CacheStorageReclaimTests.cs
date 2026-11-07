using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

internal static class CacheStorageReclaimTests
{
    public static int Count { get; private set; }

    public static async Task RunAsync(List<string> failures)
    {
        Count = 0;
        var root = Path.Combine(Path.GetTempPath(), $"TurtleHub_CacheStorage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var state = Path.Combine(root, "state");
            var installed = Path.Combine(root, "installed");
            var slot = Path.Combine(state, "user-data", "A");
            var config = new AppConfig { StateDirectory = state, CodeCommand = "notepad", UseDedicatedUserDataDirs = true };
            string[] cachePaths =
            [
                @"WebStorage\1\CacheStorage\entry",
                @"WebStorage\2\CacheStorage\entry",
                @"Service Worker\CacheStorage\origin\entry",
                @"Partitions\webview\WebStorage\3\CacheStorage\entry",
                @"Partitions\webview\Service Worker\CacheStorage\origin\entry"
            ];
            string[] keepPaths =
            [
                @"User\settings.json", @"User\workspaceStorage\repo\state.vscdb",
                @"User\globalStorage\state.vscdb", @"Backups\unsaved",
                @"WebStorage\QuotaManager", @"WebStorage\1\IndexedDB\data",
                @"Service Worker\Database\data", @"Service Worker\ScriptCache\data",
                @"Cookies", @"Local Storage\leveldb\data", @"Session Storage\data",
                @"Partitions\webview\Cookies", @"Partitions\webview\WebStorage\3\IndexedDB\data",
                @"User\CacheStorage\not-a-cache", @"WebStorage\1\CacheStorage-other\data"
            ];
            foreach (var profile in new[] { installed, slot })
            {
                foreach (var path in cachePaths.Concat(keepPaths))
                {
                    Write(profile, path);
                }
            }

            var reclaimed = SlotUserDataPaths.ReclaimUnusedUserData(config, installed, () => false);
            Check(failures, "標準・専用・Partition の既知 CacheStorage を回収する",
                new[] { installed, slot }.All(profile => cachePaths.All(path => !File.Exists(Path.Combine(profile, path)))));
            Check(failures, "CacheStorage 回収量は削除したファイルのみ", reclaimed == cachePaths.Length * 2 * 16);
            Check(failures, "設定・認証・履歴・未保存データと似た名前のフォルダを保持する",
                new[] { installed, slot }.All(profile => keepPaths.All(path => File.ReadAllBytes(Path.Combine(profile, path)).SequenceEqual(new byte[16]))));
            Check(failures, "回収済みの再実行は 0 バイト", SlotUserDataPaths.ReclaimUnusedUserData(config, installed, () => false) == 0);

            Write(installed, cachePaths[0]);
            Write(slot, cachePaths[0]);
            Write(slot, @"Cache\data");
            Check(failures, "窓なしプロセスが残る場合も標準・専用の清掃を見送る",
                SlotUserDataPaths.ReclaimUnusedUserData(config, installed, () => true) == 0
                && File.Exists(Path.Combine(installed, cachePaths[0])) && File.Exists(Path.Combine(slot, cachePaths[0]))
                && File.Exists(Path.Combine(slot, @"Cache\data")));
            Check(failures, "プロセスの確認失敗時は削除しない",
                SlotUserDataPaths.ReclaimUnusedUserData(config, installed, () => throw new InvalidOperationException("probe failed")) == 0
                && File.Exists(Path.Combine(slot, cachePaths[0])));
            config.UseDedicatedUserDataDirs = false;
            Check(failures, "プロセス残存時は旧専用プロファイル全体の削除も見送る",
                SlotUserDataPaths.ReclaimUnusedUserData(config, installed, () => true) == 0 && Directory.Exists(slot));

            var lockedRoot = Path.Combine(root, "locked");
            var lockedFile = Write(lockedRoot, cachePaths[0]);
            Write(lockedRoot, cachePaths[1]);
            using (var locked = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Check(failures, "ロック中のキャッシュを残し、他のキャッシュは回収する",
                    VscodeCacheStorage.Prune(lockedRoot, () => true) == 16 && File.Exists(lockedFile));
            }
            Check(failures, "ロック解放後の再実行で残りを回収する", VscodeCacheStorage.Prune(lockedRoot, () => true) == 16);

            var interrupted = Path.Combine(root, "interrupted");
            Write(interrupted, @"Service Worker\CacheStorage\first");
            var second = Write(interrupted, @"Service Worker\CacheStorage\second");
            var allowed = true;
            var bytes = VscodeCacheStorage.Prune(interrupted, () =>
            {
                // 最初のファイルが消えた時点でプロセス起動に相当する停止を通知する。
                allowed &= File.Exists(Path.Combine(interrupted, @"Service Worker\CacheStorage\first")) && File.Exists(second);
                return allowed;
            });
            Check(failures, "清掃途中の停止要求で残りのキャッシュを保持する", bytes == 16
                && Directory.EnumerateFiles(Path.Combine(interrupted, @"Service Worker\CacheStorage")).Count() == 1);

            VerifyLinks(root, failures);
            await VerifyLaunchWaitAsync(failures);
        }
        finally
        {
            // このテストで作った一時ツリーだけを、リンクを辿らずに片付ける。
            FileSystemCleanup.DeleteDirectoryBestEffort(root);
        }
    }

    private static void VerifyLinks(string root, List<string> failures)
    {
        var outside = Path.Combine(root, "outside");
        var keep = Write(outside, @"1\CacheStorage\keep");
        Write(outside, @"CacheStorage\keep");
        Write(outside, @"WebStorage\1\CacheStorage\keep");
        Write(outside, "keep");
        var originalBytes = FileSystemCleanup.MeasureDirectoryBytes(outside);
        string[] links = ["", "WebStorage", @"WebStorage\1", @"WebStorage\1\CacheStorage", "Partitions", @"Partitions\webview", @"Service Worker"];
        foreach (var relative in links)
        {
            var profile = Path.Combine(root, "linked-" + Guid.NewGuid().ToString("N"));
            var linkPath = Path.Combine(profile, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            if (!UserDataReclaimTests.TryCreateJunction(linkPath, outside))
            {
                failures.Add($"CacheStorage リンク保護: ジャンクション作成失敗 ({relative})");
                continue;
            }

            Check(failures, $"CacheStorage の祖先・対象リンクを辿らない ({relative})",
                VscodeCacheStorage.Prune(profile, () => true) == 0
                && Directory.Exists(linkPath) && File.Exists(keep)
                && FileSystemCleanup.MeasureDirectoryBytes(outside) == originalBytes);
        }

        var innerProfile = Path.Combine(root, "inner-link");
        var cache = Path.Combine(innerProfile, @"WebStorage\1\CacheStorage");
        Directory.CreateDirectory(cache);
        if (!UserDataReclaimTests.TryCreateJunction(Path.Combine(cache, "linked"), outside))
        {
            failures.Add("CacheStorage 内部リンクの作成失敗");
        }
        else
        {
            Check(failures, "CacheStorage 内部のリンク先の実データも保持する",
                VscodeCacheStorage.Prune(innerProfile, () => true) == 0 && !Directory.Exists(cache)
                && FileSystemCleanup.MeasureDirectoryBytes(outside) == originalBytes);
        }
    }

    private static async Task VerifyLaunchWaitAsync(List<string> failures)
    {
        var launcher = new VscodeLauncher(new WindowEnumerator());
        using var cancellation = new CancellationTokenSource();
        await SlotUserDataPaths.UserDataMaintenanceGate.WaitAsync();
        try
        {
            var launch = launcher.LaunchMissingAsync([], new AppConfig(), cancellation.Token);
            Check(failures, "清掃中は VS Code 起動を非同期で待機する", !launch.IsCompleted);
            cancellation.Cancel();
            try
            {
                await launch;
                failures.Add("清掃待機中の VS Code 起動がキャンセルされませんでした。");
            }
            catch (OperationCanceledException)
            {
                Check(failures, "待機のキャンセルで清掃側の排他を解放しない", SlotUserDataPaths.UserDataMaintenanceGate.CurrentCount == 0);
            }
        }
        finally
        {
            SlotUserDataPaths.UserDataMaintenanceGate.Release();
        }

        await launcher.LaunchMissingAsync([], new AppConfig(), CancellationToken.None);
        Check(failures, "起動処理完了で排他を解放する", SlotUserDataPaths.UserDataMaintenanceGate.CurrentCount == 1);
    }

    private static string Write(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[16]);
        return path;
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
