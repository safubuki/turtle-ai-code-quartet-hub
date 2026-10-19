using System.Collections.Concurrent;
using System.IO;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

internal static class FolderUsageCalculationTests
{
    public static async Task RunAsync(List<string> failures)
    {
        await VerifyDriveAndFoldersAsync(failures);
        await VerifySlowFolderDoesNotBlockAsync(failures);
        await VerifyTimeoutsAndFailuresAsync(failures);
        await VerifyCancellationAsync(failures);
        VerifyDrivePresentation(failures);
    }

    private static async Task VerifyDriveAndFoldersAsync(List<string> failures)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"TurtleHub_FolderUsage_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var empty = Directory.CreateDirectory(Path.Combine(tempRoot, "empty")).FullName;
            var data = Directory.CreateDirectory(Path.Combine(tempRoot, "data")).FullName;
            File.WriteAllBytes(Path.Combine(data, "test.bin"), new byte[1234]);
            var driveRoot = Path.GetPathRoot(tempRoot)!;
            var items = new[] { Item(driveRoot), Item(data), Item(empty), Item(Path.Combine(tempRoot, "missing")) };
            var results = new ConcurrentDictionary<string, FolderUsageMeasurement>();
            await SystemMonitorService.MeasureFolderUsagesAsync(items, (item, result) =>
            {
                results[item.FolderPath] = result;
                return Task.CompletedTask;
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            Check(failures, "ドライブ直下は走査せず容量情報を返し、後続の控えも完了する",
                results.Count == 4 && results[driveRoot].DriveTotalBytes > 0
                && results[driveRoot].SizeBytes >= 0 && results[driveRoot].FileCount == 0);
            Check(failures, "ドライブの子フォルダは通常のフォルダサイズとして計測する",
                results[data] is { SizeBytes: 1234, FileCount: 1, DriveTotalBytes: null });
            Check(failures, "空フォルダの 0 B と消失フォルダの計測不可を区別する",
                results[empty] is { SizeBytes: 0, FileCount: 0 }
                && results[items[3].FolderPath] is { SizeBytes: null, UnavailableReason: "計測不可" });

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = false;
            try
            {
                SystemMonitorService.CalculateDirectorySize(empty, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            Check(failures, "ファイルのないフォルダでもキャンセルを尊重する", cancelled);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static async Task VerifySlowFolderDoesNotBlockAsync(List<string> failures)
    {
        using var releaseSlow = new ManualResetEventSlim();
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new ConcurrentQueue<string>();
        var calculation = SystemMonitorService.MeasureFolderUsagesAsync(
            [Item("slow"), Item("fast"), Item("later")], (item, _) =>
            {
                reports.Enqueue(item.FolderPath);
                if (item.FolderPath == "fast") fastReported.TrySetResult();
                return Task.CompletedTask;
            }, CancellationToken.None, TimeSpan.FromSeconds(5), (path, token) =>
            {
                if (path == "slow")
                {
                    slowStarted.TrySetResult();
                    releaseSlow.Wait(token);
                }

                return new FolderUsageMeasurement(100, 1);
            });
        try
        {
            await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fastReported.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(failures, "先頭が計測中でも後続のサイズが先に通知される",
                !reports.Contains("slow") && reports.Contains("fast"));
        }
        finally
        {
            releaseSlow.Set();
            await calculation;
        }

        Check(failures, "全フォルダが一度ずつ完了通知される",
            reports.Count == 3 && reports.Distinct().Count() == 3);
    }

    private static async Task VerifyTimeoutsAndFailuresAsync(List<string> failures)
    {
        var cancelledWorkers = 0;
        var results = new ConcurrentDictionary<string, FolderUsageMeasurement>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await SystemMonitorService.MeasureFolderUsagesAsync(
            [Item("slow1"), Item("slow2"), Item("error"), Item("later")], (item, result) =>
            {
                results[item.FolderPath] = result;
                return Task.CompletedTask;
            }, CancellationToken.None, TimeSpan.FromMilliseconds(500), (path, token) =>
            {
                if (path.StartsWith("slow", StringComparison.Ordinal))
                {
                    try
                    {
                        token.WaitHandle.WaitOne();
                        token.ThrowIfCancellationRequested();
                    }
                    finally
                    {
                        if (Interlocked.Increment(ref cancelledWorkers) == 2) stopped.TrySetResult();
                    }
                }

                if (path == "error") throw new IOException("計測中の読み取り失敗を模擬");
                return new FolderUsageMeasurement(200, 2);
            }).WaitAsync(TimeSpan.FromSeconds(5));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Check(failures, "並行枠が両方とも遅くても時間超過でスキップし、走査を中断する",
            results["slow1"] is { SizeBytes: null, UnavailableReason: "スキップ (時間超過)" }
            && results["slow2"] is { SizeBytes: null, UnavailableReason: "スキップ (時間超過)" }
            && cancelledWorkers == 2);
        Check(failures, "例外のあった項目だけを計測不可にし、後続はサイズを表示する",
            results["error"] is { SizeBytes: null, UnavailableReason: "計測不可" }
            && results["later"] is { SizeBytes: 200, FileCount: 2 } && results.Count == 4);
    }

    private static async Task VerifyCancellationAsync(List<string> failures)
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = 0;
        var calculation = SystemMonitorService.MeasureFolderUsagesAsync([Item("old")], (_, _) =>
        {
            Interlocked.Increment(ref reports);
            return Task.CompletedTask;
        }, cancellation.Token, TimeSpan.FromSeconds(5), (_, _) =>
        {
            // OS の同期 I/O のように、キャンセルへ即応できず遅れて戻る処理を模擬。
            started.TrySetResult();
            release.Wait();
            exited.TrySetResult();
            return new FolderUsageMeasurement(500, 5);
        });
        var cancelled = false;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            try
            {
                await calculation.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
        }
        finally
        {
            release.Set();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Check(failures, "閉じる/再計測時は I/O の終了を待たずに中断し、古い結果を通知しない",
            cancelled && reports == 0);
    }

    private static void VerifyDrivePresentation(List<string> failures)
    {
        var drive = Item("drive");
        var folder = Item("folder");
        drive.ApplyDriveUsage(800L * 1024 * 1024, 1000L * 1024 * 1024, "800 MB");
        folder.ApplyMeasuredSize(100L * 1024 * 1024, 10, "100 MB");
        SystemMonitorService.UpdateRelativeFolderRatios([drive, folder]);
        Check(failures, "ドライブ使用量を明示し、通常フォルダの相対バーから除外する",
            drive.IsDriveUsage && !drive.IsCalculating && drive.SubDetailText == "ドライブ使用量"
            && drive.UsageRatio == 80d && folder.UsageRatio == 100d);

        folder.ApplyUnavailable("スキップ (時間超過)");
        Check(failures, "スキップ時は部分サイズやファイル数を確定値として残さない",
            !folder.IsCalculating && folder.SizeBytes is null && folder.FileCount == 0
            && !folder.HasSubDetail && folder.UsageRatio == 0);
    }

    private static FolderUsageItem Item(string path) => new("1", "Stored", path, path, path, "計測中...", true);

    private static void Check(List<string> failures, string name, bool condition)
    {
        if (!condition) failures.Add(name);
    }
}
