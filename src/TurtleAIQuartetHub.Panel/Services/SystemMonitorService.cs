using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Microsoft.Win32;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class SystemMonitorService
{
    private static readonly Brush NormalUsageBrush = CreateFrozenBrush("#45D483");
    private static readonly Brush WarningUsageBrush = CreateFrozenBrush("#F2CA57");
    private static readonly Brush CriticalUsageBrush = CreateFrozenBrush("#E56B5F");

    // 数万ファイル規模（VS Code データ、AI エージェントのデータ、Temp）でも低速 PC で計測しきれる時間。
    private static readonly TimeSpan LargeFolderMeasurementTimeout = TimeSpan.FromSeconds(15);

    // ハブ起動後に pagefile.sys がこれ以上増えたら「拡張あり」と表示する。
    internal const long PagefileGrowthNoticeBytes = 256L * 1024 * 1024;

    private const int SystemPageFileInformationClass = 18;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    private readonly object _cpuLock = new();
    private long? _minimumObservedPagefileBytes;
    private PagefileSettings? _pagefileSettings;
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
                isCalculating: true)
            {
                MeasurementTimeout = LargeFolderMeasurementTimeout
            });
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
                isCalculating: true)
            {
                MeasurementTimeout = LargeFolderMeasurementTimeout
            });
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
                    isCalculating: true)
                {
                    MeasurementTimeout = LargeFolderMeasurementTimeout
                });
            }
        }

        foreach (var (badgeText, title, folderPath) in GetAiAgentDataFolders(vsCodeDataDir))
        {
            if (!Directory.Exists(folderPath) || !seenLocalPaths.Add(Path.GetFullPath(folderPath)))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(folderPath);
            items.Add(new FolderUsageItem(
                badgeText: badgeText,
                categoryKind: "Agent",
                title: title,
                folderPath: fullPath,
                displayPath: fullPath,
                initialSizeText: "計測中...",
                isCalculating: true)
            {
                MeasurementTimeout = LargeFolderMeasurementTimeout
            });
        }

        return items;
    }

    // 4 窓で AI エージェントを動かすと増えやすいフォルダ。どれも他アプリのデータなので計測だけ行う。
    private static IEnumerable<(string BadgeText, string Title, string FolderPath)> GetAiAgentDataFolders(string? vsCodeDataDir)
    {
        if (!string.IsNullOrWhiteSpace(vsCodeDataDir))
        {
            yield return ("Chat", "VS Code のチャット履歴など", Path.Combine(vsCodeDataDir, "User", "workspaceStorage"));
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return ("Codex", "Codex のデータ（セッション・本体）", Path.Combine(userProfile, ".codex"));
            yield return ("Copilot", "Copilot CLI のデータ（旧バージョン含む）", Path.Combine(userProfile, ".copilot"));
            yield return ("Claude", "Claude Code のデータ", Path.Combine(userProfile, ".claude"));
            yield return ("Gemini", "Gemini / Antigravity のデータ", Path.Combine(userProfile, ".gemini"));
            yield return ("Grok", "Grok CLI のデータ", Path.Combine(userProfile, ".grok"));
        }

        yield return ("Temp", "一時フォルダ（Temp）", DiskCleanupService.GetDefaultTempRoot());
    }

    // 入れ子のフォルダ（VS Code データ内の workspaceStorage など）を合計で二重に数えない。
    internal static long SumWithoutNestedDuplicates(IReadOnlyList<FolderUsageItem> items)
    {
        var measured = items
            .Where(item => !item.IsDriveUsage && item.SizeBytes.HasValue && !string.IsNullOrWhiteSpace(item.FolderPath))
            .Select(item => (Path: Path.TrimEndingDirectorySeparator(item.FolderPath), Bytes: item.SizeBytes!.Value))
            .ToList();

        return measured
            .Where(item => !measured.Any(other =>
                other.Path.Length < item.Path.Length
                && item.Path.StartsWith(other.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Sum(item => item.Bytes);
    }

    // 1 件の巨大フォルダや応答しない I/O が、後続の控えを待たせないようにする。
    internal static async Task MeasureFolderUsagesAsync(
        IReadOnlyList<FolderUsageItem> items,
        Func<FolderUsageItem, FolderUsageMeasurement, Task> reportAsync,
        CancellationToken cancellationToken,
        TimeSpan? itemTimeout = null,
        Func<string, CancellationToken, FolderUsageMeasurement>? measure = null)
    {
        var defaultTimeout = itemTimeout ?? TimeSpan.FromSeconds(3);
        measure ??= MeasureFolderUsage;
        await Parallel.ForEachAsync(items, new ParallelOptions
        {
            MaxDegreeOfParallelism = 2,
            CancellationToken = cancellationToken
        }, async (item, token) =>
        {
            var timeout = item.MeasurementTimeout ?? defaultTimeout;
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

    // 1.5 秒ごとの更新から呼ぶため WMI は使わず、カーネルの集計値を直接読む。
    public PagefileSnapshot CapturePagefile()
    {
        var memoryStatus = new MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
        };
        GlobalMemoryStatusEx(ref memoryStatus);

        // ullTotalPageFile は「物理メモリ + ページファイル」のコミット上限を表す。
        var commitLimit = memoryStatus.ullTotalPageFile;
        var committed = commitLimit > memoryStatus.ullAvailPageFile
            ? commitLimit - memoryStatus.ullAvailPageFile
            : 0UL;
        var (allocated, inUse, peak) = ReadPagefileUsage();

        long? growth = null;
        if (allocated > 0)
        {
            _minimumObservedPagefileBytes = _minimumObservedPagefileBytes is long minimum
                ? Math.Min(minimum, allocated)
                : allocated;
            growth = allocated - _minimumObservedPagefileBytes.Value;
        }

        _pagefileSettings ??= ReadPagefileSettings();
        return new PagefileSnapshot(
            memoryStatus.ullTotalPhys,
            commitLimit,
            committed,
            allocated,
            inUse,
            peak,
            growth,
            _pagefileSettings);
    }

    // 注意文はメモリ使用量カード（半幅）に出すため、短く保つ。余裕ありの文は表示しない。
    internal static PagefileAssessment AssessPagefile(PagefileSnapshot snapshot)
    {
        if (snapshot.Settings.Mode == PagefileManagementMode.None)
        {
            return new PagefileAssessment(
                PagefileAssessmentLevel.Warning,
                "なし",
                "ページファイルがないため、メモリが尽きるとアプリが落ちることがあります。");
        }

        if (snapshot.GrowthSinceHubStartBytes is long growth && growth >= PagefileGrowthNoticeBytes)
        {
            return new PagefileAssessment(
                PagefileAssessmentLevel.Critical,
                "拡張あり",
                $"メモリ不足で pagefile.sys が {FormatBytes(growth)} 増え、ディスクを使っています。PC の再起動で元に戻ります。");
        }

        var percent = snapshot.CommitPercent;
        if (percent >= 90d)
        {
            return new PagefileAssessment(
                PagefileAssessmentLevel.Critical,
                "逼迫",
                snapshot.Settings.CanGrow
                    ? "メモリが足りなくなりかけています。まもなく pagefile.sys が拡張され、ディスクを使います。"
                    : "メモリが足りなくなりかけています。上限に達するとアプリが落ちることがあります。");
        }

        if (percent >= 75d)
        {
            return new PagefileAssessment(
                PagefileAssessmentLevel.Warning,
                "注意",
                snapshot.Settings.CanGrow
                    ? "メモリが少なくなっています。足りなくなると pagefile.sys が拡張されます。"
                    : "メモリが少なくなっています。");
        }

        return new PagefileAssessment(PagefileAssessmentLevel.Normal, "余裕あり", string.Empty);
    }

    public static Brush GetAssessmentBrush(PagefileAssessmentLevel level) => level switch
    {
        PagefileAssessmentLevel.Critical => CriticalUsageBrush,
        PagefileAssessmentLevel.Warning => WarningUsageBrush,
        _ => NormalUsageBrush
    };

    public static string DescribePagefileSettings(PagefileSettings settings) => settings.Mode switch
    {
        PagefileManagementMode.SystemManaged => "自動管理（必要に応じて拡張）",
        PagefileManagementMode.Custom when settings.CanGrow =>
            $"カスタム（{FormatBytes(settings.InitialBytes ?? 0)} ～ 最大 {FormatBytes(settings.MaximumBytes ?? 0)}）",
        PagefileManagementMode.Custom => $"固定サイズ（{FormatBytes(settings.MaximumBytes ?? 0)}）",
        PagefileManagementMode.None => "ページファイルなし",
        _ => "設定を取得できません"
    };

    internal static PagefileSettings ParsePagingFiles(IReadOnlyList<string>? values)
    {
        var entries = values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToList() ?? [];
        if (entries.Count == 0)
        {
            return new PagefileSettings(PagefileManagementMode.None, null, null);
        }

        const long megabyte = 1024L * 1024L;
        var initialTotal = 0L;
        var maximumTotal = 0L;
        foreach (var entry in entries)
        {
            // "?:\pagefile.sys" は全ドライブ自動管理。サイズ省略や "0 0" はそのドライブのシステム管理サイズ。
            if (entry.StartsWith("?:", StringComparison.Ordinal))
            {
                return new PagefileSettings(PagefileManagementMode.SystemManaged, null, null);
            }

            var parts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3
                || !long.TryParse(parts[^2], out var initialMegabytes)
                || !long.TryParse(parts[^1], out var maximumMegabytes)
                || (initialMegabytes == 0 && maximumMegabytes == 0))
            {
                return new PagefileSettings(PagefileManagementMode.SystemManaged, null, null);
            }

            initialTotal += initialMegabytes * megabyte;
            maximumTotal += maximumMegabytes * megabyte;
        }

        return new PagefileSettings(PagefileManagementMode.Custom, initialTotal, maximumTotal);
    }

    private static PagefileSettings ReadPagefileSettings()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            return ParsePagingFiles(key?.GetValue("PagingFiles") as string[]);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write(ex);
            return new PagefileSettings(PagefileManagementMode.Unknown, null, null);
        }
    }

    // SYSTEM_PAGEFILE_INFORMATION: NextEntryOffset, TotalSize, TotalInUse, PeakUsage（ページ数）, PageFileName。
    private static (long Allocated, long InUse, long Peak) ReadPagefileUsage()
    {
        var bufferSize = 1024;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var status = NtQuerySystemInformation(SystemPageFileInformationClass, buffer, bufferSize, out var returned);
                if (status == StatusInfoLengthMismatch)
                {
                    bufferSize = Math.Max(bufferSize * 2, returned);
                    continue;
                }

                if (status != 0 || returned <= 0)
                {
                    return (0, 0, 0);
                }

                long pageSize = Environment.SystemPageSize;
                long allocated = 0, inUse = 0, peak = 0;
                var offset = 0;
                while (offset + 16 <= returned)
                {
                    var entry = buffer + offset;
                    allocated += (uint)Marshal.ReadInt32(entry, 4) * pageSize;
                    inUse += (uint)Marshal.ReadInt32(entry, 8) * pageSize;
                    peak += (uint)Marshal.ReadInt32(entry, 12) * pageSize;
                    var next = Marshal.ReadInt32(entry, 0);
                    if (next <= 0)
                    {
                        break;
                    }

                    offset += next;
                }

                return (allocated, inUse, peak);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return (0, 0, 0);
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

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);
}
