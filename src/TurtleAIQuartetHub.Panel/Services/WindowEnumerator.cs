using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class WindowEnumerator
{
    public static IntPtr GetForegroundWindowHandle()
    {
        return GetForegroundWindow();
    }

    public IReadOnlyList<WindowInfo> GetVsCodeWindows()
    {
        return GetApplicationWindows(["code", "code - insiders", "vscodium", "codium"]);
    }

    public IReadOnlyList<WindowInfo> GetApplicationWindows(IReadOnlyList<string> processNames)
    {
        var windows = new List<WindowInfo>();
        var normalizedProcessNames = NormalizeProcessNames(processNames);

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsCandidateWindow(hWnd))
            {
                return true;
            }

            _ = GetWindowThreadProcessId(hWnd, out var processId);
            if (!IsProcessMatch(processId, normalizedProcessNames))
            {
                return true;
            }

            windows.Add(new WindowInfo(hWnd, GetTitle(hWnd), processId));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public WindowInfo? TryGetWindow(IntPtr hWnd)
    {
        return TryGetWindow(hWnd, ["code", "code - insiders", "vscodium", "codium"]);
    }

    public WindowInfo? TryGetWindow(IntPtr hWnd, IReadOnlyList<string> processNames)
    {
        if (!IsLiveWindow(hWnd) || !IsCandidateWindow(hWnd))
        {
            return null;
        }

        _ = GetWindowThreadProcessId(hWnd, out var processId);
        return IsProcessMatch(processId, NormalizeProcessNames(processNames))
            ? new WindowInfo(hWnd, GetTitle(hWnd), processId)
            : null;
    }

    public bool IsLiveWindow(IntPtr hWnd)
    {
        return hWnd != IntPtr.Zero && IsWindow(hWnd);
    }

    private static bool IsCandidateWindow(IntPtr hWnd)
    {
        return IsWindowVisible(hWnd) && GetWindowTextLength(hWnd) > 0;
    }

    private static bool IsProcessMatch(uint processId, IReadOnlySet<string> normalizedProcessNames)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var processName = NormalizeProcessName(process.ProcessName);
            return normalizedProcessNames.Count == 0 || normalizedProcessNames.Contains(processName);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static IReadOnlySet<string> NormalizeProcessNames(IEnumerable<string> processNames)
    {
        return processNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(NormalizeProcessName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeProcessName(string processName)
    {
        return Path.GetFileNameWithoutExtension(processName.Trim()).ToLowerInvariant();
    }

    private static string GetTitle(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}

public sealed record WindowInfo(IntPtr Handle, string Title, uint ProcessId);
