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
    private const int DwmwaExtendedFrameBounds = 9;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private static readonly uint ArrangeFlags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW;
    private static readonly uint LayerFlags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;

    public int Arrange(IReadOnlyList<WindowSlot> slots, int gap, int monitorIndex)
    {
        return ArrangeCore(slots, gap, monitorIndex, excludedSlot: null);
    }

    public int ArrangeExcept(IReadOnlyList<WindowSlot> slots, WindowSlot excludedSlot, int gap, int monitorIndex)
    {
        return ArrangeCore(slots, gap, monitorIndex, excludedSlot);
    }

    public bool NeedsArrange(IReadOnlyList<WindowSlot> slots, int gap, int monitorIndex, int tolerance = 48)
    {
        var placements = BuildPlacements(slots, gap, monitorIndex, excludedSlot: null);
        foreach (var placement in placements)
        {
            // 期待セルは可視枠（DWM 拡張フレーム）基準なので、現在値も可視枠で比較する。
            if (IsIconic(placement.Handle) || !TryGetVisibleBounds(placement.Handle, out var current))
            {
                return true;
            }

            if (!IsCloseToExpectedBounds(current, placement, Math.Max(0, tolerance)))
            {
                return true;
            }
        }

        return false;
    }

    private int ArrangeCore(IReadOnlyList<WindowSlot> slots, int gap, int monitorIndex, WindowSlot? excludedSlot)
    {
        var placements = BuildPlacements(slots, gap, monitorIndex, excludedSlot);

        if (placements.Count == 0)
        {
            return 0;
        }

        foreach (var placement in placements)
        {
            RestoreForResize(placement.Handle);
        }

        // 各ウィンドウの不可視枠（DWM 拡張フレームと GetWindowRect の差）を打ち消し、
        // 可視枠がセルにそろうように配置する。これで上端/下端/中央や縦横の隙間が均等になる。
        var targets = placements
            .Select(CompensateForFrame)
            .ToList();

        var deferredWindowPos = BeginDeferWindowPos(targets.Count);
        if (deferredWindowPos != IntPtr.Zero)
        {
            var queued = true;
            foreach (var target in targets)
            {
                deferredWindowPos = DeferWindowPos(
                    deferredWindowPos,
                    target.Handle,
                    IntPtr.Zero,
                    target.X,
                    target.Y,
                    target.Width,
                    target.Height,
                    ArrangeFlags);
                if (deferredWindowPos == IntPtr.Zero)
                {
                    queued = false;
                    break;
                }
            }

            if (queued && EndDeferWindowPos(deferredWindowPos))
            {
                return targets.Count;
            }
        }

        var arranged = 0;
        foreach (var target in targets)
        {
            if (SetWindowPos(
                target.Handle,
                IntPtr.Zero,
                target.X,
                target.Y,
                target.Width,
                target.Height,
                ArrangeFlags))
            {
                arranged++;
            }
        }

        return arranged;
    }

    // baseMonitorIndex は全ディスプレイ移動で決まる「ベース」。各スロットは MonitorOverride を
    // 持つときだけ別ディスプレイへ単独配置し、持たないときはベースに追従する。象限セル（index→
    // 列/行）は固定のまま、作業領域だけスロットごとの実効ディスプレイから取る。これにより同じ
    // ディスプレイへ複数スロットを単独移動しても、各々の象限へ自然にタイルする。
    private static List<WindowPlacement> BuildPlacements(
        IReadOnlyList<WindowSlot> slots,
        int gap,
        int baseMonitorIndex,
        WindowSlot? excludedSlot)
    {
        var monitors = GetOrderedMonitors();
        if (monitors.Count == 0)
        {
            return [];
        }

        var normalizedGap = Math.Clamp(gap, 0, 64);
        var placements = new List<WindowPlacement>(Math.Min(4, slots.Count));

        for (var index = 0; index < Math.Min(4, slots.Count); index++)
        {
            var slot = slots[index];
            if (ReferenceEquals(slot, excludedSlot))
            {
                continue;
            }

            // フォーカス中（1 面・最大化）のスロットはタイル配置の対象外。これにより全 Arrange は
            // 各ディスプレイの最大化ウィンドウを保ったまま、非フォーカスのみを象限へ並べる。
            // 複数ディスプレイで同時にフォーカス（各ディスプレイ 1 つ）を維持する土台になる。
            if (slot.IsFocused)
            {
                continue;
            }

            if (slot.WindowHandle == IntPtr.Zero || !IsWindow(slot.WindowHandle))
            {
                continue;
            }

            var effectiveMonitor = NormalizeMonitorIndex(slot.MonitorOverride ?? baseMonitorIndex, monitors.Count);
            var workArea = monitors[effectiveMonitor].WorkArea;
            var cellWidth = Math.Max(320, (workArea.Width - normalizedGap * 3) / 2);
            var cellHeight = Math.Max(240, (workArea.Height - normalizedGap * 3) / 2);

            var column = index % 2;
            var row = index / 2;
            var x = workArea.Left + normalizedGap + column * (cellWidth + normalizedGap);
            var y = workArea.Top + normalizedGap + row * (cellHeight + normalizedGap);
            placements.Add(new WindowPlacement(slot.WindowHandle, x, y, cellWidth, cellHeight));
        }

        return placements;
    }

    private static bool IsCloseToExpectedBounds(WindowBounds current, WindowPlacement expected, int tolerance)
    {
        return Math.Abs(current.Left - expected.X) <= tolerance
            && Math.Abs(current.Top - expected.Y) <= tolerance
            && Math.Abs(current.Width - expected.Width) <= tolerance
            && Math.Abs(current.Height - expected.Height) <= tolerance;
    }

    // セル（可視枠で表現した目標矩形）を、ウィンドウの不可視枠ぶん外側へ広げた
    // 実際の SetWindowPos 用矩形へ変換する。
    private static WindowPlacement CompensateForFrame(WindowPlacement cell)
    {
        var inset = GetFrameInset(cell.Handle);
        return new WindowPlacement(
            cell.Handle,
            cell.X - inset.Left,
            cell.Y - inset.Top,
            cell.Width + inset.Left + inset.Right,
            cell.Height + inset.Top + inset.Bottom);
    }

    // GetWindowRect と DWM 拡張フレーム（可視枠）の差＝各辺の不可視枠幅を返す。
    // DWM 非対応や取得失敗時は補正なし（ゼロ）。
    private static FrameInset GetFrameInset(IntPtr windowHandle)
    {
        if (GetWindowRect(windowHandle, out var windowRect)
            && DwmGetWindowAttribute(windowHandle, DwmwaExtendedFrameBounds, out var frame, Marshal.SizeOf<RECT>()) == 0)
        {
            var left = frame.Left - windowRect.Left;
            var top = frame.Top - windowRect.Top;
            var right = windowRect.Right - frame.Right;
            var bottom = windowRect.Bottom - frame.Bottom;
            if (left >= 0 && top >= 0 && right >= 0 && bottom >= 0
                && (left > 0 || top > 0 || right > 0 || bottom > 0))
            {
                return new FrameInset(left, top, right, bottom);
            }
        }

        return FrameInset.Zero;
    }

    // ウィンドウの可視枠（DWM 拡張フレーム）を返す。取得できないときは GetWindowRect で代用。
    private static bool TryGetVisibleBounds(IntPtr windowHandle, out WindowBounds bounds)
    {
        if (DwmGetWindowAttribute(windowHandle, DwmwaExtendedFrameBounds, out var frame, Marshal.SizeOf<RECT>()) == 0)
        {
            bounds = new WindowBounds(
                frame.Left,
                frame.Top,
                Math.Max(0, frame.Right - frame.Left),
                Math.Max(0, frame.Bottom - frame.Top));
            return true;
        }

        if (GetWindowRect(windowHandle, out var rect))
        {
            bounds = new WindowBounds(
                rect.Left,
                rect.Top,
                Math.Max(0, rect.Right - rect.Left),
                Math.Max(0, rect.Bottom - rect.Top));
            return true;
        }

        bounds = default;
        return false;
    }

    public bool BringToFront(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        return SetWindowPos(windowHandle, HWND_TOPMOST, 0, 0, 0, 0, LayerFlags);
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

        var raised = SetWindowPos(windowHandle, HWND_TOPMOST, 0, 0, 0, 0, LayerFlags);
        var demoted = SetWindowPos(windowHandle, HWND_NOTOPMOST, 0, 0, 0, 0, LayerFlags);
        return raised || demoted;
    }

    public bool SetBackmost(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        var demoted = SetWindowPos(windowHandle, HWND_NOTOPMOST, 0, 0, 0, 0, LayerFlags);
        var sentToBack = SetWindowPos(windowHandle, HWND_BOTTOM, 0, 0, 0, 0, LayerFlags);
        return demoted || sentToBack;
    }

    public bool ReleaseTopmost(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        return SetWindowPos(windowHandle, HWND_NOTOPMOST, 0, 0, 0, 0, LayerFlags);
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

    // バッジ表示用に、指定インデックスを現在のモニタ枚数で正規化した 1 始まりの番号を返す。
    public int ResolveMonitorNumber(int monitorIndex)
    {
        var monitorCount = GetMonitorCount();
        return NormalizeMonitorIndex(monitorIndex, monitorCount) + 1;
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

    // 指定ディスプレイで最大化してから前面化する。単独移動したスロットのフォーカス（1 面）を
    // その実効ディスプレイで開くために使う。ウィンドウが既にそのディスプレイに居れば移動しない。
    public bool FocusMaximizedOnMonitor(IntPtr windowHandle, int monitorIndex)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        EnsureWindowOnMonitor(windowHandle, monitorIndex);
        ShowWindow(windowHandle, SW_MAXIMIZE);
        return SetForegroundWindow(windowHandle);
    }

    public bool Maximize(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        return ShowWindow(windowHandle, SW_MAXIMIZE);
    }

    // 前面化を伴わずに指定ディスプレイで最大化する。非表示からのフォーカス復帰で使う。
    public bool MaximizeOnMonitor(IntPtr windowHandle, int monitorIndex)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        EnsureWindowOnMonitor(windowHandle, monitorIndex);
        return ShowWindow(windowHandle, SW_MAXIMIZE);
    }

    // ウィンドウが指定ディスプレイに無ければ、そのディスプレイの作業領域内へ移してから
    // 最大化できるようにする。SW_MAXIMIZE は「現在ウィンドウが載っているディスプレイ」へ
    // 最大化するため、先に移動しておかないと別ディスプレイで最大化されてしまう。
    private static void EnsureWindowOnMonitor(IntPtr windowHandle, int monitorIndex)
    {
        var monitors = GetOrderedMonitors();
        if (monitors.Count == 0)
        {
            return;
        }

        var target = NormalizeMonitorIndex(monitorIndex, monitors.Count);
        var currentHandle = MonitorFromWindow(windowHandle, MONITOR_DEFAULTTONEAREST);
        if (monitors[target].Handle == currentHandle)
        {
            return;
        }

        RestoreForResize(windowHandle);
        var workArea = monitors[target].WorkArea;
        // 直後に SW_MAXIMIZE するので暫定サイズ。作業領域内へ確実に載せることだけが目的。
        SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            workArea.Left + 40,
            workArea.Top + 40,
            Math.Max(320, workArea.Width - 80),
            Math.Max(240, workArea.Height - 80),
            ArrangeFlags);
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

    // ウィンドウが最小化（アイコン化）されているか。ユーザーがアプリ外で直接最小化したかの判定に使う。
    public bool IsMinimized(IntPtr windowHandle)
    {
        return windowHandle != IntPtr.Zero && IsWindow(windowHandle) && IsIconic(windowHandle);
    }

    public bool TryGetWindowBounds(IntPtr windowHandle, out WindowBounds bounds)
    {
        bounds = default;
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle) || !GetWindowRect(windowHandle, out var rect))
        {
            return false;
        }

        bounds = new WindowBounds(
            rect.Left,
            rect.Top,
            Math.Max(0, rect.Right - rect.Left),
            Math.Max(0, rect.Bottom - rect.Top));
        return true;
    }

    public bool TryGetMonitorWorkAreaForWindow(IntPtr windowHandle, out WindowBounds workAreaBounds)
    {
        workAreaBounds = default;
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        var monitorHandle = MonitorFromWindow(windowHandle, MONITOR_DEFAULTTONEAREST);
        if (monitorHandle == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO
        {
            cbSize = Marshal.SizeOf<MONITORINFO>()
        };

        if (!GetMonitorInfo(monitorHandle, ref info))
        {
            return false;
        }

        workAreaBounds = new WindowBounds(
            info.rcWork.Left,
            info.rcWork.Top,
            Math.Max(0, info.rcWork.Right - info.rcWork.Left),
            Math.Max(0, info.rcWork.Bottom - info.rcWork.Top));
        return true;
    }

    public bool SetWindowBounds(IntPtr windowHandle, WindowBounds bounds)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        return SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            bounds.Left,
            bounds.Top,
            Math.Max(1, bounds.Width),
            Math.Max(1, bounds.Height),
            ArrangeFlags);
    }

    private static void RestoreForResize(IntPtr windowHandle)
    {
        if (IsIconic(windowHandle) || IsZoomed(windowHandle))
        {
            ShowWindow(windowHandle, SW_RESTORE);
        }
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
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr BeginDeferWindowPos(int nNumWindows);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DeferWindowPos(
        IntPtr hWinPosInfo,
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EndDeferWindowPos(IntPtr hWinPosInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    public readonly record struct WindowBounds(int Left, int Top, int Width, int Height);

    private readonly record struct WindowPlacement(IntPtr Handle, int X, int Y, int Width, int Height);

    private readonly record struct FrameInset(int Left, int Top, int Right, int Bottom)
    {
        public static FrameInset Zero { get; } = new(0, 0, 0, 0);
    }

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
