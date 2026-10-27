using System.Diagnostics;
using System.IO;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

// システムモニタの「ディスク整理」。再取得できる更新ファイルだけを候補にし、
// 設定・履歴・サインイン情報を含むフォルダには触れない。
public sealed class DiskCleanupService
{
    // これより新しいものは更新処理中・適用待ちの可能性があるため「注意」にする。
    internal static readonly TimeSpan RecentThreshold = TimeSpan.FromHours(24);

    // 削除前にこの名前へ移動する。移動できない＝使用中なので削除しない。
    internal const string TombstoneSuffix = ".turtle-deleting";

    private const long MinimumInstallerBytes = 10L * 1024 * 1024;
    private const long MaximumSideFileBytes = 1L * 1024 * 1024;
    private const int MaximumInstallerDirectoryEntries = 20;

    private static readonly string[] InstallerExtensions =
        [".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle"];

    private static readonly string[] CopilotProcessNames = ["copilot"];

    public static string? GetDefaultCopilotPackageRoot()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? null
            : Path.Combine(userProfile, ".copilot", "pkg");
    }

    public static string GetDefaultTempRoot()
    {
        return Path.TrimEndingDirectorySeparator(Path.GetTempPath());
    }

    public DiskCleanupScanResult Scan(CancellationToken cancellationToken)
    {
        var copilotRoot = GetDefaultCopilotPackageRoot();
        return Scan(
            copilotRoot,
            GetDefaultTempRoot(),
            DateTime.Now,
            IsCopilotCliRunning(copilotRoot),
            cancellationToken);
    }

    public DiskCleanupDeleteResult Delete(IEnumerable<DiskCleanupCandidate> candidates)
    {
        var copilotRoot = GetDefaultCopilotPackageRoot();
        return Delete(
            candidates,
            copilotRoot,
            GetDefaultTempRoot(),
            () => IsCopilotCliRunning(copilotRoot));
    }

    internal static DiskCleanupScanResult Scan(
        string? copilotPackageRoot,
        string tempRoot,
        DateTime now,
        bool isCopilotRunning,
        CancellationToken cancellationToken)
    {
        var hasCopilotRoot = !string.IsNullOrWhiteSpace(copilotPackageRoot) && Directory.Exists(copilotPackageRoot);
        var copilot = hasCopilotRoot
            ? ScanCopilotVersions(copilotPackageRoot!, isCopilotRunning, cancellationToken)
            : [];
        var temp = Directory.Exists(tempRoot)
            ? ScanTempInstallers(tempRoot, now, cancellationToken)
            : [];
        return new DiskCleanupScanResult(copilot, temp, isCopilotRunning, hasCopilotRoot);
    }

    internal static DiskCleanupDeleteResult Delete(
        IEnumerable<DiskCleanupCandidate> candidates,
        string? copilotPackageRoot,
        string tempRoot,
        Func<bool> isCopilotRunning)
    {
        var reclaimed = 0L;
        var deleted = 0;
        var skipped = 0;
        bool? copilotRunning = null;

        foreach (var candidate in candidates.Where(candidate => candidate.IsSelected && candidate.CanDelete))
        {
            var allowedRoot = candidate.Category == DiskCleanupCategory.CopilotCliVersion
                ? copilotPackageRoot
                : tempRoot;
            if (!IsStrictlyUnder(candidate.Path, allowedRoot))
            {
                DiagnosticLog.Write(LogLevel.Warn, $"Disk cleanup refused a path outside its root: {candidate.Path}");
                skipped++;
                continue;
            }

            // スキャン後に Copilot CLI を起動した場合に備え、削除直前にも確認する。
            if (candidate.Category == DiskCleanupCategory.CopilotCliVersion)
            {
                copilotRunning ??= isCopilotRunning();
                if (copilotRunning.Value && !IsTombstone(candidate.Path))
                {
                    skipped++;
                    continue;
                }
            }

            if (TryDeleteCandidate(candidate, out var bytes))
            {
                deleted++;
            }
            else
            {
                skipped++;
            }

            reclaimed += bytes;
        }

        return new DiskCleanupDeleteResult(reclaimed, deleted, skipped);
    }

    private static List<DiskCleanupCandidate> ScanCopilotVersions(
        string packageRoot,
        bool isCopilotRunning,
        CancellationToken cancellationToken)
    {
        var result = new List<DiskCleanupCandidate>();
        foreach (var platformDirectory in EnumerateRealDirectories(packageRoot))
        {
            // tmp は更新のダウンロード作業場所。途中のダウンロードを壊さないよう対象外にする。
            if (string.Equals(platformDirectory.Name, "tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var versions = new List<(DirectoryInfo Directory, Version Version)>();
            foreach (var versionDirectory in EnumerateRealDirectories(platformDirectory.FullName))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsTombstone(versionDirectory.FullName))
                {
                    result.Add(CreateCopilotCandidate(
                        versionDirectory,
                        $"{platformDirectory.Name} 削除途中の旧バージョン",
                        "前回の整理で使用中だったため残った旧バージョンです。",
                        DiskCleanupSafety.Safe,
                        cancellationToken));
                    continue;
                }

                if (TryParseVersion(versionDirectory.Name, out var version))
                {
                    versions.Add((versionDirectory, version));
                }
            }

            if (versions.Count == 0)
            {
                continue;
            }

            var latest = versions.MaxBy(entry => entry.Version);
            foreach (var (directory, version) in versions.OrderByDescending(entry => entry.Version))
            {
                var title = $"{platformDirectory.Name}  {directory.Name}";
                if (ReferenceEquals(directory, latest.Directory))
                {
                    result.Add(CreateCopilotCandidate(
                        directory,
                        title,
                        "最新版です。Copilot CLI が使うため保持します。",
                        DiskCleanupSafety.Protected,
                        cancellationToken));
                }
                else if (isCopilotRunning)
                {
                    result.Add(CreateCopilotCandidate(
                        directory,
                        title,
                        "Copilot CLI の実行中は保持します（実行中のセッションが旧版を使っている可能性があります）。終了後に削除できます。",
                        DiskCleanupSafety.Protected,
                        cancellationToken));
                }
                else
                {
                    result.Add(CreateCopilotCandidate(
                        directory,
                        title,
                        $"自動更新で残った旧版です（最新版 {latest.Directory.Name} を使うため不要）。",
                        DiskCleanupSafety.Safe,
                        cancellationToken));
                }
            }
        }

        return result;
    }

    private static DiskCleanupCandidate CreateCopilotCandidate(
        DirectoryInfo directory,
        string title,
        string reason,
        DiskCleanupSafety safety,
        CancellationToken cancellationToken)
    {
        return new DiskCleanupCandidate(
            DiskCleanupCategory.CopilotCliVersion,
            directory.FullName,
            isDirectory: true,
            title,
            reason,
            FileSystemCleanup.MeasureDirectoryBytes(directory.FullName, cancellationToken),
            directory.LastWriteTime,
            safety);
    }

    private static List<DiskCleanupCandidate> ScanTempInstallers(
        string tempRoot,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var result = new List<DiskCleanupCandidate>();
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(tempRoot).EnumerateFileSystemInfos().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write(ex);
            return result;
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                var candidate = entry switch
                {
                    FileInfo file => EvaluateTempFile(file, now),
                    DirectoryInfo directory => EvaluateTempDirectory(directory, now),
                    _ => null
                };
                if (candidate is not null)
                {
                    result.Add(candidate);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 走査中に消えた・読めない項目は候補にしない。
            }
        }

        return result
            .OrderByDescending(candidate => candidate.SizeBytes)
            .ToList();
    }

    private static DiskCleanupCandidate? EvaluateTempFile(FileInfo file, DateTime now)
    {
        if (IsTombstone(file.FullName))
        {
            return new DiskCleanupCandidate(
                DiskCleanupCategory.TempInstaller,
                file.FullName,
                isDirectory: false,
                file.Name,
                "前回の整理で使用中だったため残ったファイルです。",
                file.Length,
                file.LastWriteTime,
                DiskCleanupSafety.Safe);
        }

        if (!IsInstallerFile(file) || file.Length < MinimumInstallerBytes)
        {
            return null;
        }

        var (safety, reason) = EvaluateTempSafety(
            [file],
            file.LastWriteTime,
            now,
            DescribeInstallerFile(file));
        return new DiskCleanupCandidate(
            DiskCleanupCategory.TempInstaller,
            file.FullName,
            isDirectory: false,
            file.Name,
            reason,
            file.Length,
            file.LastWriteTime,
            safety);
    }

    // インストーラー本体と小さな付随ファイル（.flag / metadata.json など）だけが入ったフォルダを候補にする。
    // サブフォルダや多数のファイルを含むフォルダは、他アプリの作業中データの可能性があるため触れない。
    private static DiskCleanupCandidate? EvaluateTempDirectory(DirectoryInfo directory, DateTime now)
    {
        if (IsTombstone(directory.FullName))
        {
            return new DiskCleanupCandidate(
                DiskCleanupCategory.TempInstaller,
                directory.FullName,
                isDirectory: true,
                directory.Name,
                "前回の整理で使用中だったため残ったフォルダです。",
                FileSystemCleanup.MeasureDirectoryBytes(directory.FullName),
                directory.LastWriteTime,
                DiskCleanupSafety.Safe);
        }

        var files = new List<FileInfo>();
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry is not FileInfo file || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }

            files.Add(file);
            if (files.Count > MaximumInstallerDirectoryEntries)
            {
                return null;
            }
        }

        var installers = files.Where(file => IsInstallerFile(file) && file.Length >= MinimumInstallerBytes).ToList();
        if (installers.Count == 0
            || files.Except(installers).Any(file => file.Length > MaximumSideFileBytes))
        {
            return null;
        }

        var lastWrite = files.Select(file => file.LastWriteTime).Append(directory.LastWriteTime).Max();
        var (safety, reason) = EvaluateTempSafety(
            files,
            lastWrite,
            now,
            DescribeInstallerDirectory(directory, installers));
        return new DiskCleanupCandidate(
            DiskCleanupCategory.TempInstaller,
            directory.FullName,
            isDirectory: true,
            directory.Name,
            reason,
            files.Sum(file => file.Length),
            lastWrite,
            safety);
    }

    private static (DiskCleanupSafety Safety, string Reason) EvaluateTempSafety(
        IReadOnlyList<FileInfo> files,
        DateTime lastWrite,
        DateTime now,
        string description)
    {
        if (files.Any(IsFileLocked))
        {
            return (DiskCleanupSafety.Protected, $"{description} 実行中・使用中のため削除できません。");
        }

        if (now - lastWrite < RecentThreshold)
        {
            return (DiskCleanupSafety.Caution,
                $"{description} 24 時間以内に作成されたため、更新の適用待ちの可能性があります。消した場合は次回更新時に再ダウンロードされます。");
        }

        return (DiskCleanupSafety.Safe, $"{description} 必要になれば各アプリが再ダウンロードします。");
    }

    private static string DescribeInstallerFile(FileInfo file)
    {
        return Path.GetExtension(file.Name).ToLowerInvariant() switch
        {
            ".msix" or ".msixbundle" or ".appx" or ".appxbundle" => "アプリ更新パッケージです。",
            ".msi" => "インストーラーです。",
            _ => "ダウンロード済みのインストーラーです。"
        };
    }

    private static string DescribeInstallerDirectory(DirectoryInfo directory, IReadOnlyList<FileInfo> installers)
    {
        // VS Code / Antigravity など VS Code 系エディタの更新キャッシュ（例: vscode-stable-user-x64）。
        if (directory.Name.Contains("-user-", StringComparison.OrdinalIgnoreCase)
            || directory.Name.Contains("-system-", StringComparison.OrdinalIgnoreCase))
        {
            return "エディタの自動更新用インストーラーです。";
        }

        return installers.Count == 1
            ? $"更新インストーラー（{installers[0].Name}）だけが入ったフォルダです。"
            : "更新インストーラーだけが入ったフォルダです。";
    }

    private static bool TryDeleteCandidate(DiskCleanupCandidate candidate, out long reclaimedBytes)
    {
        reclaimedBytes = 0;
        var path = candidate.Path;
        if (!(candidate.IsDirectory ? Directory.Exists(path) : File.Exists(path)))
        {
            // 既に消えていれば整理済みとして扱う。
            return true;
        }

        if (!candidate.IsDirectory && IsFileLocked(new FileInfo(path)))
        {
            return false;
        }

        var tombstone = path;
        if (!IsTombstone(path))
        {
            tombstone = GetAvailableTombstonePath(path);
            try
            {
                // フォルダ内のファイルを誰かが開いていると移動に失敗する。途中まで消して
                // 実行中のアプリを壊さないよう、移動できたものだけを削除する。
                if (candidate.IsDirectory)
                {
                    Directory.Move(path, tombstone);
                }
                else
                {
                    File.Move(path, tombstone);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        if (candidate.IsDirectory)
        {
            reclaimedBytes = FileSystemCleanup.DeleteDirectoryBestEffort(tombstone);
            return !Directory.Exists(tombstone);
        }

        reclaimedBytes = FileSystemCleanup.DeleteFileBestEffort(tombstone);
        return !File.Exists(tombstone);
    }

    private static string GetAvailableTombstonePath(string path)
    {
        var candidate = path + TombstoneSuffix;
        var index = 1;
        while (Directory.Exists(candidate) || File.Exists(candidate))
        {
            candidate = $"{path}.{index++}{TombstoneSuffix}";
        }

        return candidate;
    }

    internal static bool IsCopilotCliRunning(string? packageRoot)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (CopilotProcessNames.Any(name => string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }

                    if (string.IsNullOrWhiteSpace(packageRoot))
                    {
                        continue;
                    }

                    var modulePath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(modulePath) && IsStrictlyUnder(modulePath, packageRoot))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // 管理者プロセスや終了直後のプロセスは判定対象外。
                }
            }
        }

        return false;
    }

    private static bool IsFileLocked(FileInfo file)
    {
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsInstallerFile(FileInfo file)
    {
        return InstallerExtensions.Contains(Path.GetExtension(file.Name), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsTombstone(string path)
    {
        return Path.TrimEndingDirectorySeparator(path).EndsWith(TombstoneSuffix, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryParseVersion(string name, out Version version)
    {
        // 1.0.103 / 1.0.103-beta.1 のような名前を想定する。
        var core = name.Split('-', '+')[0];
        if (Version.TryParse(core, out var parsed) && parsed is not null)
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0);
        return false;
    }

    private static IEnumerable<DirectoryInfo> EnumerateRealDirectories(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateDirectories()
                .Where(directory => !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsStrictlyUnder(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                && fullPath.Length > fullRoot.Length;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
