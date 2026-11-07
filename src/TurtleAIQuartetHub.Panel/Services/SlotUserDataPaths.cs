using System.Diagnostics;
using System.IO;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public static class SlotUserDataPaths
{
    // 起動時の背景清掃とハブからの VS Code 起動を直列化する。UI スレッドでは非同期待機する。
    internal static SemaphoreSlim UserDataMaintenanceGate { get; } = new(1, 1);

    private static readonly string[] SharedUserFiles =
    [
        "settings.json",
        "keybindings.json",
        "chatLanguageModels.json",
        "mcp.json"
    ];

    private static readonly string[] SharedUserDirectories =
    [
        "snippets",
        "prompts"
    ];

    // 消してよいのは再生成できるキャッシュだけ。
    // 残すもの: User（設定・キーバインド・globalStorage・workspaceStorage）、
    // WebStorage / Partitions / Local Storage / Session Storage（サインインとチャット履歴）。
    // ただし既知の配置にある CacheStorage だけは、全 VS Code 停止中に別途回収する。
    private static readonly string[] RegenerableCacheDirectoryNames =
    [
        "Cache",
        "CachedData",
        "CachedExtensions",
        "CachedExtensionVSIXs",
        "CachedProfilesData",
        "Code Cache",
        "GPUCache",
        "DawnGraphiteCache",
        "DawnWebGPUCache",
        "Crashpad",
        "logs",
        "VideoDecodeStats",
        "blob_storage",
        "clp",
        "agent-host",
        "copilot-terminal-output"
    ];

    private static readonly string[] VsCodeProcessNames = ["Code", "Code - Insiders", "VSCodium", "Codium"];

    // クラッシュダンプは VS Code 実行中でも消してよい。
    private static readonly string[] AlwaysSafeCacheDirectoryNames = ["Crashpad"];

    private static readonly string[] SharedRootFiles =
    [
        "machineid",
        "languagepacks.json",
        "Local State",
        "Preferences"
    ];

    // Chromium/Electron storage trees are intentionally excluded to avoid heavy startup copies on lower-spec PCs.
    private static readonly string[] SharedRootDirectories = [];

    public static string GetUserDataDirectory(WindowSlot slot, AppConfig config)
    {
        return GetUserDataDirectory(slot.RuntimeSlotName, config);
    }

    public static string GetUserDataDirectory(string slotName, AppConfig config)
    {
        var safeSlotName = new string(slotName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        if (string.IsNullOrWhiteSpace(safeSlotName))
        {
            safeSlotName = "slot";
        }

        return Path.Combine(config.StateDirectory, "user-data", safeSlotName);
    }

    public static string GetEffectiveUserDataDirectory(WindowSlot slot, AppConfig config)
    {
        return GetEffectiveUserDataDirectory(slot.RuntimeSlotName, config);
    }

    public static string GetEffectiveUserDataDirectory(string slotName, AppConfig config)
    {
        if (config.UseDedicatedUserDataDirs)
        {
            return GetUserDataDirectory(slotName, config);
        }

        return GetInstalledUserDataDirectory(config.CodeCommand) ?? GetUserDataDirectory(slotName, config);
    }

    public static void PrepareDedicatedUserData(WindowSlot slot, AppConfig config, string codeCommand)
    {
        var targetDirectory = GetUserDataDirectory(slot, config);
        Directory.CreateDirectory(targetDirectory);

        if (config.InheritMainUserState)
        {
            var sourceDirectory = GetInstalledUserDataDirectory(codeCommand);
            if (!string.IsNullOrWhiteSpace(sourceDirectory) && Directory.Exists(sourceDirectory))
            {
                try
                {
                    SyncSharedState(sourceDirectory, targetDirectory);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write(ex);
                }
            }
        }

        try
        {
            PruneRegenerableCaches(targetDirectory);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }

        // settings.json が無い初回だけ Roaming の通常 VS Code と共有する。
        // 既存ファイルはその窓で保存した変更を優先し、再リンク・上書きしない。
        // リンクを共有したままの場合は Roaming 側へ restoreWindows を書かない。
        // storage.json / Cache / WebStorage はここでは触らない。
        try
        {
            VscodeUserSettings.TryShareInstalledSettingsFile(
                targetDirectory,
                GetInstalledUserDataDirectory(codeCommand));
            VscodeUserSettings.SynchronizeDedicatedSlotSettings(targetDirectory, config, codeCommand);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    public static string? GetInstalledUserDataDirectory(AppConfig config)
    {
        return GetInstalledUserDataDirectory(config.CodeCommand);
    }

    public static long ReclaimUnusedUserData(AppConfig config)
    {
        UserDataMaintenanceGate.Wait();
        try
        {
            return ReclaimUnusedUserData(config, GetInstalledUserDataDirectory(config.CodeCommand), IsAnyVsCodeProcessAlive);
        }
        finally
        {
            UserDataMaintenanceGate.Release();
        }
    }

    // 実機の AppData に触れず、プロセス残存も再現できるテスト用の依存境界。
    internal static long ReclaimUnusedUserData(AppConfig config, string? installedDirectory, Func<bool> isAnyVsCodeProcessAlive)
    {
        var reclaimed = 0L;
        var slotUserDataRoot = Path.Combine(config.StateDirectory, "user-data");
        var canContinue = CreateInactiveProcessCheck(isAnyVsCodeProcessAlive);

        if (!string.IsNullOrWhiteSpace(installedDirectory))
        {
            reclaimed += PruneCacheDirectories(installedDirectory, AlwaysSafeCacheDirectoryNames);
        }

        if (!canContinue())
        {
            DiagnosticLog.Write("Skipped VS Code cache prune (including CacheStorage): a VS Code process is running or its state is unavailable. Close all VS Code processes and restart the hub.");
            return reclaimed;
        }

        // 共有プロファイル運用ではスロット別 user-data は使わない。過去の専用プロファイルが
        // 残っていると Cache / WebStorage だけで数 GB になるため、起動時に回収する。
        if (!config.UseDedicatedUserDataDirs && VscodeCacheStorage.IsUnlinkedDirectory(slotUserDataRoot))
        {
            reclaimed += FileSystemCleanup.DeleteDirectoryBestEffort(slotUserDataRoot, canContinue);
        }

        if (!string.IsNullOrWhiteSpace(installedDirectory))
        {
            reclaimed += PruneRegenerableCaches(installedDirectory, canContinue);
        }

        if (config.UseDedicatedUserDataDirs && VscodeCacheStorage.IsUnlinkedDirectory(slotUserDataRoot))
        {
            foreach (var slotDirectory in Directory.EnumerateDirectories(slotUserDataRoot))
            {
                if (!canContinue())
                {
                    break;
                }

                reclaimed += PruneRegenerableCaches(slotDirectory, canContinue);
            }
        }

        return reclaimed;
    }

    private static void SyncSharedState(string sourceDirectory, string targetDirectory)
    {
        foreach (var fileName in SharedRootFiles)
        {
            CopyFileIfNeeded(
                Path.Combine(sourceDirectory, fileName),
                Path.Combine(targetDirectory, fileName));
        }

        foreach (var directoryName in SharedRootDirectories)
        {
            CopyDirectoryIfNeeded(
                Path.Combine(sourceDirectory, directoryName),
                Path.Combine(targetDirectory, directoryName));
        }

        var sourceUserDirectory = Path.Combine(sourceDirectory, "User");
        var targetUserDirectory = Path.Combine(targetDirectory, "User");
        Directory.CreateDirectory(targetUserDirectory);

        foreach (var fileName in SharedUserFiles)
        {
            var sourcePath = Path.Combine(sourceUserDirectory, fileName);
            var targetPath = Path.Combine(targetUserDirectory, fileName);
            if (string.Equals(fileName, "settings.json", StringComparison.OrdinalIgnoreCase))
            {
                CopyFileIfMissing(sourcePath, targetPath);
            }
            else
            {
                CopyFileIfNeeded(sourcePath, targetPath);
            }
        }

        foreach (var directoryName in SharedUserDirectories)
        {
            CopyDirectoryIfNeeded(
                Path.Combine(sourceUserDirectory, directoryName),
                Path.Combine(targetUserDirectory, directoryName));
        }
    }

    public static string? GetInstalledUserDataDirectory(string codeCommand)
    {
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(applicationData))
        {
            return null;
        }

        var commandName = Path.GetFileNameWithoutExtension(codeCommand);
        if (commandName.Contains("insiders", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(applicationData, "Code - Insiders");
        }

        if (commandName.Contains("code", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(applicationData, "Code");
        }

        return null;
    }

    private static void CopyDirectoryIfNeeded(string sourceDirectory, string targetDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        if (DirectorySurfaceMatches(sourceDirectory, targetDirectory))
        {
            return;
        }

        Directory.CreateDirectory(targetDirectory);

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory))
        {
            CopyFileIfNeeded(filePath, Path.Combine(targetDirectory, Path.GetFileName(filePath)));
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectoryIfNeeded(directoryPath, Path.Combine(targetDirectory, Path.GetFileName(directoryPath)));
        }
    }

    private static bool DirectorySurfaceMatches(string sourceDirectory, string targetDirectory)
    {
        if (!Directory.Exists(targetDirectory))
        {
            return false;
        }

        try
        {
            return EnumerateDirectorySurface(sourceDirectory)
                .SequenceEqual(EnumerateDirectorySurface(targetDirectory), StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            return false;
        }
    }

    private static IEnumerable<string> EnumerateDirectorySurface(string directoryPath)
    {
        return new DirectoryInfo(directoryPath)
            .EnumerateFileSystemInfos()
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry switch
            {
                FileInfo fileInfo => $"F|{fileInfo.Name}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}",
                DirectoryInfo directoryInfo => $"D|{directoryInfo.Name}|{directoryInfo.LastWriteTimeUtc.Ticks}",
                _ => $"X|{entry.Name}|{entry.LastWriteTimeUtc.Ticks}"
            });
    }

    private static void CopyFileIfMissing(string sourcePath, string targetPath)
    {
        if (File.Exists(targetPath) || !File.Exists(sourcePath))
        {
            return;
        }

        CopyFileIfNeeded(sourcePath, targetPath);
    }

    private static void CopyFileIfNeeded(string sourcePath, string targetPath)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        try
        {
            var sourceInfo = new FileInfo(sourcePath);
            var targetInfo = new FileInfo(targetPath);
            if (targetInfo.Exists
                && targetInfo.Length == sourceInfo.Length
                && targetInfo.LastWriteTimeUtc == sourceInfo.LastWriteTimeUtc)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    private static long PruneRegenerableCaches(string userDataDirectory, Func<bool>? canContinue = null)
    {
        canContinue ??= CreateInactiveProcessCheck(IsAnyVsCodeProcessAlive);
        return PruneCacheDirectories(userDataDirectory, RegenerableCacheDirectoryNames, canContinue)
            + VscodeCacheStorage.Prune(userDataDirectory, canContinue);
    }

    private static long PruneCacheDirectories(string userDataDirectory, IReadOnlyList<string> directoryNames, Func<bool>? canContinue = null)
    {
        if (!VscodeCacheStorage.IsUnlinkedDirectory(userDataDirectory))
        {
            return 0;
        }

        var reclaimed = 0L;
        foreach (var directoryName in directoryNames)
        {
            if (canContinue?.Invoke() == false)
            {
                break;
            }

            reclaimed += FileSystemCleanup.DeleteDirectoryBestEffort(Path.Combine(userDataDirectory, directoryName), canContinue);
        }

        return reclaimed;
    }

    private static Func<bool> CreateInactiveProcessCheck(Func<bool> isAnyVsCodeProcessAlive)
    {
        var elapsed = Stopwatch.StartNew();
        var nextCheck = TimeSpan.Zero;
        var stopped = true;
        return () =>
        {
            if (stopped && elapsed.Elapsed >= nextCheck)
            {
                try
                {
                    stopped = !isAnyVsCodeProcessAlive();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write(ex);
                    stopped = false;
                }

                nextCheck = elapsed.Elapsed + TimeSpan.FromMilliseconds(200);
                if (!stopped)
                {
                    DiagnosticLog.Write("VS Code cache prune stopped: a process is running or its state could not be checked.");
                }
            }

            return stopped;
        };
    }

    private static bool IsAnyVsCodeProcessAlive()
    {
        foreach (var processName in VsCodeProcessNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(processName);
                var count = processes.Length;
                foreach (var process in processes)
                {
                    process.Dispose();
                }

                if (count > 0)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
                return true;
            }
        }

        return false;
    }
}
