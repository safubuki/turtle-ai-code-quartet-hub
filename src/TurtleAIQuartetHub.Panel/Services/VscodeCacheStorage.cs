using System.IO;

namespace TurtleAIQuartetHub.Panel.Services;

// Chromium の既知の CacheStorage 配置だけを対象にする。
// User、IndexedDB、Cookies、Service Worker の登録情報や QuotaManager は保持する。
internal static class VscodeCacheStorage
{
    internal const string DirectoryName = "CacheStorage";

    internal static IEnumerable<string> GetRelativePaths(Func<string, IEnumerable<string>> children, string prefix = "")
    {
        foreach (var partition in new[] { prefix }.Concat(children(Join(prefix, "Partitions"))))
        {
            yield return Join(partition, "Service Worker", DirectoryName);
            foreach (var bucket in children(Join(partition, "WebStorage")))
            {
                yield return Join(bucket, DirectoryName);
            }
        }
    }

    internal static long Prune(string userDataDirectory, Func<bool> canContinue)
    {
        var root = Path.GetFullPath(userDataDirectory);
        var reclaimed = 0L;
        foreach (var relativePath in GetRelativePaths(Children))
        {
            if (!canContinue())
            {
                break;
            }

            if (!IsUnlinkedDirectory(root, relativePath))
            {
                continue;
            }

            var bytes = FileSystemCleanup.DeleteDirectoryBestEffort(Path.Combine(root, relativePath), canContinue);
            reclaimed += bytes;
            DiagnosticLog.Write($"VS Code CacheStorage prune: path={Path.Combine(root, relativePath)}, reclaimed={bytes} bytes.");
        }

        return reclaimed;

        IEnumerable<string> Children(string relativePath)
        {
            try
            {
                if (!IsUnlinkedDirectory(root, relativePath))
                {
                    return [];
                }

                return new DirectoryInfo(Path.Combine(root, relativePath))
                    .EnumerateDirectories("*", new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    })
                    .Select(directory => Path.GetRelativePath(root, directory.FullName))
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Write(ex);
                return [];
            }
        }
    }

    // 削除対象だけでなくプロファイルまでの祖先も検査し、リンクを経由して外へ出ない。
    internal static bool IsUnlinkedDirectory(string root, string relativePath = "")
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
            if (!string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
                && !fullPath.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            for (var directory = new DirectoryInfo(fullPath); directory is not null; directory = directory.Parent)
            {
                if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Join(params string[] parts) => string.Join('\\', parts.Where(part => part.Length > 0));
}
