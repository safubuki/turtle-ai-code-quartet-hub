using System.Runtime.InteropServices;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class WindowArranger
{
    private const uint WM_CLOSE = 0x0010;
    private const int SW_MAXIMIZE = 3;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const uint MONITORINFOF_PRIMARY = 0x00000001;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    public int Arrange(IReadOnlyList<WindowSlot> slots, int gap, int monitorIndex)
    {
        var monitors = GetOrderedMonitors();
        if (monitors.Count == 0)
        {
            return 0;
        }

        var workArea = monitors[NormalizeMonitorIndex(monitorIndex, monitors.Count)].WorkArea;
        var normalizedGap = Math.Clamp(gap, 0, 64);
        var cellWidth = Math.Max(320, (workArea.Width - normalizedGap * 3) / 2);
        var cellHeight = Math.Max(240, (workArea.Height - normalizedGap * 3) / 2);
        var arranged = 0;

        for (var index = 0; index < Math.Min(4, slots.Count); index++)
        {
            var slot = slots[index];
            if (slot.WindowHandle == IntPtr.Zero || !IsWindow(slot.WindowHandle))
            {
                continue;
            }

            var column = index % 2;
            var row = index / 2;
            var x = workArea.Left + normalizedGap + column * (cellWidth + normalizedGap);
            var y = workArea.Top + normalizedGap + row * (cellHeight + normalizedGap);

            ShowWindow(slot.WindowHandle, SW_RESTORE);
            SetWindowPos(slot.WindowHandle, IntPtr.Zero, x, y, cellWidth, cellHeight, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW);
            if (SetWindowPos(slot.WindowHandle, IntPtr.Zero, x, y, cellWidth, cellHeight, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER))
            {
                arranged++;
            }
        }

        return arranged;
    }

    public bool BringToFront(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        return SetWindowPos(windowHandle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW);
    }

    public bool BringToFrontOnce(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        if (IsIconic(windowHandle))
        {
            ShowWindow(windowHandle, SW_RESTORE);
        }

        var raised = SetWindowPos(windowHandle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW);
        var demoted = SetWindowPos(windowHandle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW);
        return raised || demoted;
    }

    public bool SetBackmost(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        var demoted = SetWindowPos(windowHandle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW);
        var sentToBack = SetWindowPos(windowHandle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW);
        return demoted || sentToBack;
    }

    public int GetMonitorCount()
    {
        return GetOrderedMonitors().Count;
    }

    public int GetDefaultMonitorIndex(string monitorSetting)
    {
        var monitors = GetOrderedMonitors();
        if (monitors.Count == 0)
        {
            return 0;
        }

        if (int.TryParse(monitorSetting, out var configuredIndex))
        {
            return NormalizeMonitorIndex(configuredIndex - 1, monitors.Count);
        }

        return 0;
    }

    public int GetMonitorIndexForWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return -1;
        }

        var monitorHandle = MonitorFromWindow(windowHandle, MONITOR_DEFAULTTONEAREST);
        var monitors = GetOrderedMonitors();
        for (var index = 0; index < monitors.Count; index++)
        {
            if (monitors[index].Handle == monitorHandle)
            {
                return index;
            }
        }

        return -1;
    }

    public string GetMonitorLabel(int monitorIndex)
    {
        var monitorCount = GetMonitorCount();
        if (monitorCount == 0)
        {
            return "ディスプレイ 1/1";
        }

        return $"ディスプレイ {NormalizeMonitorIndex(monitorIndex, monitorCount) + 1}/{monitorCount}";
    }

    public bool Focus(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        ShowWindow(windowHandle, SW_RESTORE);
        return SetForegroundWindow(windowHandle);
    }

    public bool FocusMaximized(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        ShowWindow(windowHandle, SW_MAXIMIZE);
        return SetForegroundWindow(windowHandle);
    }

    public bool Close(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        return PostMessage(windowHandle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    public bool Minimize(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        return ShowWindow(windowHandle, SW_MINIMIZE);
    }

    public bool Restore(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        return ShowWindow(windowHandle, SW_RESTORE);
    }

    private static List<MonitorWorkArea> GetOrderedMonitors()
    {
        var monitors = new List<MonitorWorkArea>();

        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var info = new MONITORINFO
            {
                cbSize = Marshal.SizeOf<MONITORINFO>()
            };

            if (GetMonitorInfo(hMonitor, ref info))
            {
                monitors.Add(new MonitorWorkArea(
                    hMonitor,
                    new WorkArea(
                        info.rcWork.Left,
                        info.rcWork.Top,
                        info.rcWork.Right - info.rcWork.Left,
                        info.rcWork.Bottom - info.rcWork.Top),
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }

            return true;
        }, IntPtr.Zero);

        if (monitors.Count == 0)
        {
            var monitor = MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
            var info = new MONITORINFO
            {
                cbSize = Marshal.SizeOf<MONITORINFO>()
            };

            if (GetMonitorInfo(monitor, ref info))
            {
                monitors.Add(new MonitorWorkArea(
                    monitor,
                    new WorkArea(
                        info.rcWork.Left,
                        info.rcWork.Top,
                        info.rcWork.Right - info.rcWork.Left,
                        info.rcWork.Bottom - info.rcWork.Top),
                    true));
            }
            else
            {
                monitors.Add(new MonitorWorkArea(IntPtr.Zero, new WorkArea(0, 0, 1280, 720), true));
            }
        }

        return monitors
            .OrderByDescending(item => item.IsPrimary)
            .ThenBy(item => item.WorkArea.Left)
            .ThenBy(item => item.WorkArea.Top)
            .ToList();
    }

    private static int NormalizeMonitorIndex(int monitorIndex, int monitorCount)
    {
        if (monitorCount <= 0)
        {
            return 0;
        }

        var normalizedIndex = monitorIndex % monitorCount;
        if (normalizedIndex < 0)
        {
            normalizedIndex += monitorCount;
        }

        return normalizedIndex;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    private readonly record struct WorkArea(int Left, int Top, int Width, int Height);

    private readonly record struct MonitorWorkArea(IntPtr Handle, WorkArea WorkArea, bool IsPrimary);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
