using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class SystemMonitorService
{
    private static readonly Brush NormalUsageBrush = CreateFrozenBrush("#45D483");
    private static readonly Brush WarningUsageBrush = CreateFrozenBrush("#F2CA57");
    private static readonly Brush CriticalUsageBrush = CreateFrozenBrush("#E56B5F");

    private readonly object _cpuLock = new();
    private ulong _lastIdleTime;
    private ulong _lastKernelTime;
    private ulong _lastUserTime;
    private DateTimeOffset _lastCpuSampleAt = DateTimeOffset.MinValue;
    private double _lastCpuPercent;
    private bool _hasCpuBaseline;

    public SystemMonitorService()
    {
        PrimeCpuBaseline();
    }

    public void PrimeCpuBaseline()
    {
        lock (_cpuLock)
        {
            if (TryGetSystemTimes(out var idle, out var kernel, out var user))
            {
                _lastIdleTime = idle;
                _lastKernelTime = kernel;
                _lastUserTime = user;
                _lastCpuSampleAt = DateTimeOffset.UtcNow;
                _hasCpuBaseline = true;
            }
        }
    }

    public SystemResourceSnapshot CaptureSystemResources(int activeManagedWindowCount)
    {
        var cpuPercent = SampleCpuUsagePercent();
        var cpuBrush = GetUsageBrush(cpuPercent, warningThreshold: 70d, criticalThreshold: 88d);
        var coreCount = Environment.ProcessorCount;
        var cpuDetail = activeManagedWindowCount > 0
            ? $"論理プロセッサ: {coreCount} コア / 管理中ウィンドウ: {activeManagedWindowCount} 個"
            : $"論理プロセッサ: {coreCount} コア";

        var (totalMem, availMem) = ReadPhysicalMemory();
        var usedMem = totalMem > availMem ? totalMem - availMem : 0UL;
        var memPercent = totalMem > 0
            ? Math.Clamp((double)usedMem * 100d / totalMem, 0d, 100d)
            : 0d;
        var memBrush = GetUsageBrush(memPercent, warningThreshold: 75d, criticalThreshold: 90d);

        return new SystemResourceSnapshot(
            CpuUsagePercent: cpuPercent,
            CpuUsageText: $"{cpuPercent:0.0} %",
            CpuDetailText: cpuDetail,
            CpuBarBrush: cpuBrush,
            TotalMemoryBytes: totalMem,
            UsedMemoryBytes: usedMem,
            AvailableMemoryBytes: availMem,
            MemoryUsagePercent: memPercent,
            MemoryUsagePercentText: $"{memPercent:0.0} %",
            MemoryDetailText: $"使用 {FormatBytes((long)Math.Min(usedMem, long.MaxValue))} / {FormatBytes((long)Math.Min(totalMem, long.MaxValue))}",
            MemoryAvailableText: $"空き容量: {FormatBytes((long)Math.Min(availMem, long.MaxValue))}",
            MemoryBarBrush: memBrush);
    }

    public IReadOnlyList<DriveUsageItem> GetDriveUsages()
    {
        var result = new List<DriveUsageItem>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            return result;
        }

        foreach (var drive in drives)
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                var total = drive.TotalSize;
                if (total <= 0)
                {
                    continue;
                }

                var free = Math.Clamp(drive.AvailableFreeSpace, 0L, total);
                var used = Math.Max(0L, total - free);
                var percent = Math.Clamp((double)used * 100d / total, 0d, 100d);
                var rootPath = drive.Name;
                var label = string.Empty;
                try
                {
                    label = drive.VolumeLabel?.Trim() ?? string.Empty;
                }
                catch
                {
                    // ボリュームラベル取得失敗時はドライブ名のみ表示
                }

                var displayTitle = string.IsNullOrWhiteSpace(label)
                    ? rootPath.TrimEnd('\\')
                    : $"{rootPath.TrimEnd('\\')} ({label})";

                var driveTypeLabel = drive.DriveType switch
                {
                    DriveType.Fixed => "ローカル ディスク",
                    DriveType.Removable => "リムーバブル",
                    DriveType.Network => "ネットワーク ドライブ",
                    DriveType.Ram => "RAM ディスク",
                    DriveType.CDRom => "光学ドライブ",
                    _ => "ドライブ"
                };

                var brush = GetUsageBrush(percent, warningThreshold: 80d, criticalThreshold: 92d);

                result.Add(new DriveUsageItem(
                    RootPath: rootPath,
                    DisplayTitle: displayTitle,
                    DriveTypeLabel: driveTypeLabel,
                    TotalBytes: total,
                    UsedBytes: used,
                    FreeBytes: free,
                    UsagePercent: percent,
                    UsagePercentText: $"{percent:0.0} %",
                    CapacityDetailText: $"使用 {FormatBytes(used)} / {FormatBytes(total)}",
                    FreeSpaceText: $"空き {FormatBytes(free)}",
                    BarBrush: brush));
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
            }
        }

        return result;
    }

    public IReadOnlyList<FolderUsageItem> BuildFolderUsageItems(
        IEnumerable<WindowSlot> slots,
        IEnumerable<StoredPanelSlot> storedPanels,
        AppConfig config)
    {
        var items = new List<FolderUsageItem>();
        var seenLocalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var slot in slots)
        {
            var localFolder = slot.ExplorerWorkspacePath;
            var rawDisplayPath = slot.DisplayPath;

            if (!string.IsNullOrWhiteSpace(localFolder) && Directory.Exists(localFolder))
            {
                seenLocalPaths.Add(localFolder);
                items.Add(new FolderUsageItem(
                    badgeText: slot.Name,
                    categoryKind: "Slot",
                    title: slot.DisplayTitle,
                    folderPath: localFolder,
                    displayPath: localFolder,
                    initialSizeText: "計測中...",
                    isCalculating: true));
            }
            else if (string.IsNullOrWhiteSpace(rawDisplayPath))
            {
                items.Add(new FolderUsageItem(
                    badgeText: slot.Name,
                    categoryKind: "Slot",
                    title: slot.DisplayTitle,
                    folderPath: string.Empty,
                    displayPath: "ワークスペース未設定",
                    initialSizeText: "未設定",
                    isCalculating: false));
            }
            else if (rawDisplayPath.Contains("://", StringComparison.Ordinal))
            {
                items.Add(new FolderUsageItem(
                    badgeText: slot.Name,
                    categoryKind: "Slot",
                    title: slot.DisplayTitle,
                    folderPath: string.Empty,
                    displayPath: rawDisplayPath,
                    initialSizeText: "リモート (対象外)",
                    isCalculating: false));
            }
            else
            {
                items.Add(new FolderUsageItem(
                    badgeText: slot.Name,
                    categoryKind: "Slot",
                    title: slot.DisplayTitle,
                    folderPath: string.Empty,
                    displayPath: rawDisplayPath,
                    initialSizeText: "フォルダなし",
                    isCalculating: false));
            }
        }

        foreach (var stored in storedPanels)
        {
            if (!stored.HasContent || string.IsNullOrWhiteSpace(stored.WorkspacePath))
            {
                continue;
            }

            var resolvedStoredPath = TryResolveLocalDirectory(stored.WorkspacePath);
            if (string.IsNullOrWhiteSpace(resolvedStoredPath) || !seenLocalPaths.Add(resolvedStoredPath))
            {
                continue;
            }

            items.Add(new FolderUsageItem(
                badgeText: stored.Label,
                categoryKind: "Stored",
                title: $"[控え] {stored.DisplayTitle}",
                folderPath: resolvedStoredPath,
                displayPath: resolvedStoredPath,
                initialSizeText: "計測中...",
                isCalculating: true));
        }

        if (!string.IsNullOrWhiteSpace(config.StateDirectory)
            && Directory.Exists(config.StateDirectory)
            && seenLocalPaths.Add(Path.GetFullPath(config.StateDirectory)))
        {
            var hubPath = Path.GetFullPath(config.StateDirectory);
            items.Add(new FolderUsageItem(
                badgeText: "Hub",
                categoryKind: "App",
                title: "Turtle AI Quartet Hub データ",
                folderPath: hubPath,
                displayPath: hubPath,
                initialSizeText: "計測中...",
                isCalculating: true));
        }

        var vsCodeDataDir = SlotUserDataPaths.GetInstalledUserDataDirectory(config);
        if (!string.IsNullOrWhiteSpace(vsCodeDataDir)
            && Directory.Exists(vsCodeDataDir)
            && seenLocalPaths.Add(Path.GetFullPath(vsCodeDataDir)))
        {
            var fullVsCodeDir = Path.GetFullPath(vsCodeDataDir);
            items.Add(new FolderUsageItem(
                badgeText: "Code",
                categoryKind: "App",
                title: "VS Code ユーザーデータ・キャッシュ",
                folderPath: fullVsCodeDir,
                displayPath: fullVsCodeDir,
                initialSizeText: "計測中...",
                isCalculating: true));
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            var antigravityDataDir = Path.Combine(appData, "Antigravity IDE");
            if (Directory.Exists(antigravityDataDir) && seenLocalPaths.Add(Path.GetFullPath(antigravityDataDir)))
            {
                var fullAgyDir = Path.GetFullPath(antigravityDataDir);
                items.Add(new FolderUsageItem(
                    badgeText: "AGY",
                    categoryKind: "App",
                    title: "Antigravity IDE ユーザーデータ",
                    folderPath: fullAgyDir,
                    displayPath: fullAgyDir,
                    initialSizeText: "計測中...",
                    isCalculating: true));
            }
        }

        return items;
    }

    // 1 件の巨大フォルダや応答しない I/O が、後続の控えを待たせないようにする。
    internal static async Task MeasureFolderUsagesAsync(
        IReadOnlyList<FolderUsageItem> items,
        Func<FolderUsageItem, FolderUsageMeasurement, Task> reportAsync,
        CancellationToken cancellationToken,
        TimeSpan? itemTimeout = null,
        Func<string, CancellationToken, FolderUsageMeasurement>? measure = null)
    {
        var timeout = itemTimeout ?? TimeSpan.FromSeconds(3);
        measure ??= MeasureFolderUsage;
        await Parallel.ForEachAsync(items, new ParallelOptions
        {
            MaxDegreeOfParallelism = 2,
            CancellationToken = cancellationToken
        }, async (item, token) =>
        {
            using var itemCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var itemToken = itemCancellation.Token;
            var measurementTask = Task.Run(() =>
            {
                try
                {
                    return measure(item.FolderPath, itemToken);
                }
                catch (OperationCanceledException) when (itemToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write(ex);
                    return new FolderUsageMeasurement(null, UnavailableReason: "計測不可");
                }
            }, itemToken);

            FolderUsageMeasurement result;
            try
            {
                result = await measurementTask.WaitAsync(timeout, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                result = new FolderUsageMeasurement(null, UnavailableReason: "スキップ (時間超過)");
            }
            finally
            {
                // 待機だけを終わらせず、進行中のディレクトリ走査にも中断を伝える。
                itemCancellation.Cancel();
            }

            token.ThrowIfCancellationRequested();
            await reportAsync(item, result).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    internal static FolderUsageMeasurement MeasureFolderUsage(string directoryPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(directoryPath);
        var root = Path.GetPathRoot(fullPath);
        if (string.Equals(Path.TrimEndingDirectorySeparator(fullPath),
            Path.TrimEndingDirectorySeparator(root ?? string.Empty), StringComparison.OrdinalIgnoreCase))
        {
            // ルートはファイルを列挙せず、OS が管理するボリューム使用量を表示する。
            var drive = new DriveInfo(fullPath);
            var total = drive.TotalSize;
            var used = total - Math.Clamp(drive.TotalFreeSpace, 0L, total);
            cancellationToken.ThrowIfCancellationRequested();
            return new FolderUsageMeasurement(used, DriveTotalBytes: total);
        }

        var (sizeBytes, fileCount) = CalculateDirectorySize(fullPath, cancellationToken);
        return new FolderUsageMeasurement(sizeBytes, fileCount);
    }

    public static (long TotalBytes, int FileCount) CalculateDirectorySize(string directoryPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // ルート自身が消失・アクセス拒否の場合は、0 B の成功として扱わない。
        var rootDirectory = new DirectoryInfo(directoryPath);
        if ((rootDirectory.Attributes & FileAttributes.Directory) == 0)
        {
            throw new DirectoryNotFoundException(directoryPath);
        }
        var totalBytes = 0L;
        var fileCount = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        // 空ディレクトリが大量にある場合も、ディレクトリ/項目ごとに中断を確認する。
        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootDirectory);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry is DirectoryInfo child)
                    {
                        pending.Push(child);
                    }
                    else if (entry is FileInfo file)
                    {
                        try
                        {
                            totalBytes += file.Length;
                            fileCount++;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // 走査中に削除・ロックされた一時ファイルは無視して継続。
                        }
                    }
                }
            }
            catch (Exception ex) when (!ReferenceEquals(directory, rootDirectory)
                && ex is IOException or UnauthorizedAccessException)
            {
                // 途中で消えた/アクセス不能になった子フォルダだけを除外する。
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (totalBytes, fileCount);
    }

    public static void UpdateRelativeFolderRatios(IReadOnlyList<FolderUsageItem> items)
    {
        const long minimumReferenceBytes = 64L * 1024L * 1024L; // 64 MB 基準下限
        var maxBytes = items
            .Where(item => !item.IsDriveUsage && item.SizeBytes.HasValue && item.SizeBytes.Value > 0)
            .Select(item => item.SizeBytes!.Value)
            .DefaultIfEmpty(0L)
            .Max();

        var denominator = Math.Max(maxBytes, minimumReferenceBytes);
        foreach (var item in items)
        {
            if (item.IsDriveUsage)
            {
                continue;
            }

            if (!item.SizeBytes.HasValue || item.SizeBytes.Value <= 0)
            {
                item.UsageRatio = 0d;
                continue;
            }

            var ratio = (double)item.SizeBytes.Value * 100d / denominator;
            item.UsageRatio = Math.Clamp(ratio, 2d, 100d);
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        const double kb = 1024d;
        const double mb = kb * 1024d;
        const double gb = mb * 1024d;
        const double tb = gb * 1024d;

        if (bytes >= tb)
        {
            return $"{bytes / tb:0.00} TB";
        }

        if (bytes >= gb)
        {
            return $"{bytes / gb:0.0} GB";
        }

        if (bytes >= mb)
        {
            return $"{bytes / mb:0.0} MB";
        }

        if (bytes >= kb)
        {
            return $"{bytes / kb:0} KB";
        }

        return $"{bytes} B";
    }

    private double SampleCpuUsagePercent()
    {
        lock (_cpuLock)
        {
            if (!TryGetSystemTimes(out var idle, out var kernel, out var user))
            {
                return _lastCpuPercent;
            }

            var now = DateTimeOffset.UtcNow;
            if (!_hasCpuBaseline)
            {
                _lastIdleTime = idle;
                _lastKernelTime = kernel;
                _lastUserTime = user;
                _lastCpuSampleAt = now;
                _hasCpuBaseline = true;
                return _lastCpuPercent;
            }

            var elapsedMs = (now - _lastCpuSampleAt).TotalMilliseconds;
            if (elapsedMs < 80d && _lastCpuPercent > 0d)
            {
                return _lastCpuPercent;
            }

            var idleDelta = idle >= _lastIdleTime ? idle - _lastIdleTime : 0UL;
            var kernelDelta = kernel >= _lastKernelTime ? kernel - _lastKernelTime : 0UL;
            var userDelta = user >= _lastUserTime ? user - _lastUserTime : 0UL;

            _lastIdleTime = idle;
            _lastKernelTime = kernel;
            _lastUserTime = user;
            _lastCpuSampleAt = now;

            var totalSystem = kernelDelta + userDelta;
            if (totalSystem == 0)
            {
                return _lastCpuPercent;
            }

            var busy = totalSystem >= idleDelta ? totalSystem - idleDelta : 0UL;
            _lastCpuPercent = Math.Clamp((double)busy * 100d / totalSystem, 0d, 100d);
            return _lastCpuPercent;
        }
    }

    private static (ulong TotalBytes, ulong AvailableBytes) ReadPhysicalMemory()
    {
        var memoryStatus = new MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
        };

        if (GlobalMemoryStatusEx(ref memoryStatus) && memoryStatus.ullTotalPhys > 0)
        {
            return (memoryStatus.ullTotalPhys, memoryStatus.ullAvailPhys);
        }

        var gcInfo = GC.GetGCMemoryInfo();
        var total = gcInfo.TotalAvailableMemoryBytes > 0 ? (ulong)gcInfo.TotalAvailableMemoryBytes : 0UL;
        return (total, 0UL);
    }

    private static string TryResolveLocalDirectory(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Contains("://", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim());
            if (Directory.Exists(expanded))
            {
                return Path.GetFullPath(expanded);
            }

            if (File.Exists(expanded))
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(expanded));
                return !string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent) ? parent : string.Empty;
            }
        }
        catch
        {
            return string.Empty;
        }

        return string.Empty;
    }

    private static Brush GetUsageBrush(double percent, double warningThreshold, double criticalThreshold)
    {
        if (percent >= criticalThreshold)
        {
            return CriticalUsageBrush;
        }

        if (percent >= warningThreshold)
        {
            return WarningUsageBrush;
        }

        return NormalUsageBrush;
    }

    private static Brush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static bool TryGetSystemTimes(out ulong idle, out ulong kernel, out ulong user)
    {
        if (GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            idle = ToUInt64(idleTime);
            kernel = ToUInt64(kernelTime);
            user = ToUInt64(userTime);
            return true;
        }

        idle = 0;
        kernel = 0;
        user = 0;
        return false;
    }

    private static ulong ToUInt64(FILETIME fileTime)
    {
        return ((ulong)fileTime.dwHighDateTime << 32) | fileTime.dwLowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FILETIME lpIdleTime,
        out FILETIME lpKernelTime,
        out FILETIME lpUserTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
