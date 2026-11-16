using System.IO;
using System.Runtime.InteropServices;

namespace TurtleAIQuartetHub.Panel.Services;

// Windows の既知フォルダ（ユーザーが D: 等へ移動した場合も含む）を優先してダウンロードフォルダの場所を決める。
public static class DownloadsFolderResolver
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string Resolve()
    {
        return ResolvePath(
            TryGetKnownDownloadsFolderPath,
            () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Directory.Exists);
    }

    internal static string ResolvePath(
        Func<string?> knownFolderReader,
        Func<string?> userProfileReader,
        Func<string, bool> directoryExists)
    {
        var knownPath = knownFolderReader()?.Trim();
        var userProfile = userProfileReader()?.Trim();
        var fallbackPath = string.IsNullOrWhiteSpace(userProfile)
            ? string.Empty
            : Path.Combine(userProfile, "Downloads");

        if (!string.IsNullOrWhiteSpace(knownPath) && directoryExists(knownPath))
        {
            return knownPath;
        }

        if (!string.IsNullOrWhiteSpace(fallbackPath) && directoryExists(fallbackPath))
        {
            return fallbackPath;
        }

        return !string.IsNullOrWhiteSpace(knownPath) ? knownPath : fallbackPath;
    }

    private static string? TryGetKnownDownloadsFolderPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var folderId = DownloadsFolderId;
        var ptr = IntPtr.Zero;
        try
        {
            var hr = SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out ptr);
            return hr == 0 && ptr != IntPtr.Zero ? Marshal.PtrToStringUni(ptr) : null;
        }
        finally
        {
            if (ptr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        ref Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
