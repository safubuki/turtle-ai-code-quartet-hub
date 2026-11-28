using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace TurtleAIQuartetHub.Panel.Services;

// A→C フォーカス切替の背面復元中（約 50〜80ms）だけ、新フォーカス C の DWM ライブ映像を
// 自プロセスのクリックスルー盾ウィンドウへ映して「パネル本体 > 盾 > 通常帯」の順で重ねる。
// C 自体を Topmost 帯へ上げないため、メインパネルが一瞬隠れたり C（Electron/VS Code）が
// 帯切替でちらついたりする副作用を起こさず、背面での A の SW_SHOWNOACTIVATE 飛び出しだけを遮蔽する。
public sealed class FocusSwitchShieldWindow : IDisposable
{
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;
    private const int DWM_TNP_RECTDESTINATION = 0x00000001;
    private const int DWM_TNP_OPACITY = 0x00000004;
    private const int DWM_TNP_VISIBLE = 0x00000008;
    private const int DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly TimeSpan FailsafeAutoHideTimeout = TimeSpan.FromSeconds(2);

    private readonly WindowArranger _windowArranger;
    private readonly Dispatcher? _dispatcher;
    private readonly DispatcherTimer? _failsafeTimer;
    private WndProcDelegate? _wndProc;
    private string? _className;
    private IntPtr _handle;
    private IntPtr _thumbnailHandle;
    private int _ownerThreadId;
    private bool _isVisible;
    private bool _disposed;

    public FocusSwitchShieldWindow(WindowArranger windowArranger)
    {
        _windowArranger = windowArranger;
        _dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        if (_dispatcher is not null)
        {
            _failsafeTimer = new DispatcherTimer
            {
                Interval = FailsafeAutoHideTimeout
            };
            _failsafeTimer.Tick += (_, _) => Hide();
        }
    }

    public IntPtr Handle => _handle;
    public bool IsActive => _isVisible;

    // 指定した最大化中のフォーカス窓（sourceWindowHandle）と同じ可視矩形へ、
    // パネル本体等の Topmost オーバーレイより必ず後ろの位置で盾を表示する。
    public bool TryShowBehind(IntPtr sourceWindowHandle, IReadOnlyList<IntPtr>? topOverlayHandles)
    {
        if (_disposed
            || sourceWindowHandle == IntPtr.Zero
            || _windowArranger.GetWindowState(sourceWindowHandle) != ManagedWindowState.Maximized
            || !_windowArranger.TryGetVisibleWindowBounds(sourceWindowHandle, out var bounds)
            || bounds.Width <= 0
            || bounds.Height <= 0)
        {
            return false;
        }

        var insertAfter = ResolveInsertAfterHandle(topOverlayHandles);
        if (!EnsureWindowCreated(insertAfter))
        {
            return false;
        }

        // 先に非表示のままパネル直下へ Z 順と矩形を確定してから、サムネイル登録→可視化を行う。
        _ = SetWindowPos(
            _handle,
            insertAfter,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SWP_NOACTIVATE | SWP_NOOWNERZORDER);

        UnregisterThumbnail();
        if (DwmRegisterThumbnail(_handle, sourceWindowHandle, out var thumbnail) != 0 || thumbnail == IntPtr.Zero)
        {
            return false;
        }

        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_OPACITY | DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT
            {
                Left = 0,
                Top = 0,
                Right = bounds.Width,
                Bottom = bounds.Height
            },
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = false
        };

        if (DwmUpdateThumbnailProperties(thumbnail, ref props) != 0)
        {
            _ = DwmUnregisterThumbnail(thumbnail);
            return false;
        }

        _thumbnailHandle = thumbnail;

        // パネル本体・フォーカス名オーバーレイなど自アプリの Topmost 窓の中で最も下にある窓の直下へ、
        // 単一の同期 SetWindowPos で可視化する。これにより自アプリが一瞬も後ろへ隠れない。
        var shown = SetWindowPos(
            _handle,
            insertAfter,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW);
        if (!shown)
        {
            UnregisterThumbnail();
            return false;
        }

        _isVisible = true;
        _failsafeTimer?.Stop();
        _failsafeTimer?.Start();
        return true;
    }

    public void Hide()
    {
        if (_dispatcher is not null && !_dispatcher.CheckAccess() && !_dispatcher.HasShutdownStarted)
        {
            _dispatcher.Invoke(HideCore);
            return;
        }

        HideCore();
    }

    private void HideCore()
    {
        _failsafeTimer?.Stop();
        UnregisterThumbnail();
        if (_handle != IntPtr.Zero && _isVisible)
        {
            var flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                flags |= SWP_ASYNCWINDOWPOS;
            }

            _ = SetWindowPos(_handle, IntPtr.Zero, 0, 0, 0, 0, flags);
            _isVisible = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher is not null && !_dispatcher.CheckAccess() && !_dispatcher.HasShutdownStarted)
        {
            _dispatcher.Invoke(DisposeCore);
            return;
        }

        DisposeCore();
    }

    private void DisposeCore()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        HideCore();
        if (_handle != IntPtr.Zero)
        {
            _ = DestroyWindow(_handle);
            _handle = IntPtr.Zero;
        }

        if (!string.IsNullOrEmpty(_className))
        {
            _ = UnregisterClassW(_className, GetModuleHandle(null));
            _className = null;
        }

        _wndProc = null;
    }

    private IntPtr ResolveInsertAfterHandle(IReadOnlyList<IntPtr>? topOverlayHandles)
    {
        if (topOverlayHandles is null || topOverlayHandles.Count == 0)
        {
            return HWND_TOPMOST;
        }

        var topmostOverlays = topOverlayHandles
            .Where(handle => handle != IntPtr.Zero && handle != _handle && _windowArranger.IsInTopmostLayer(handle))
            .Distinct()
            .ToList();
        if (topmostOverlays.Count == 0)
        {
            return HWND_TOPMOST;
        }

        return topmostOverlays
            .Aggregate((lowest, candidate) => _windowArranger.IsAbove(lowest, candidate) ? candidate : lowest);
    }

    private bool EnsureWindowCreated(IntPtr initialInsertAfter)
    {
        if (_handle != IntPtr.Zero)
        {
            return true;
        }

        _wndProc = WindowProc;
        var className = $"TurtleAIQuartetHub_FocusShield_{Guid.NewGuid():N}";
        var moduleHandle = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = moduleHandle,
            hbrBackground = IntPtr.Zero,
            lpszClassName = className
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            return false;
        }

        var exStyle = WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        var handle = CreateWindowExW(
            exStyle,
            className,
            string.Empty,
            WS_POPUP,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            moduleHandle,
            IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            _ = UnregisterClassW(className, moduleHandle);
            return false;
        }

        _ = SetLayeredWindowAttributes(handle, 0, 255, LWA_ALPHA);
        _ = WindowArranger.SetTransitionsDisabled(handle, true);
        if (initialInsertAfter != IntPtr.Zero && initialInsertAfter != HWND_TOPMOST)
        {
            _ = SetWindowPos(
                handle,
                initialInsertAfter,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        _className = className;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _handle = handle;
        return true;
    }

    private void UnregisterThumbnail()
    {
        if (_thumbnailHandle != IntPtr.Zero)
        {
            _ = DwmUnregisterThumbnail(_thumbnailHandle);
            _thumbnailHandle = IntPtr.Zero;
        }
    }

    private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 背景塗りつぶしを抑止し、DWM サムネイル以外の白背景が一瞬でも出ないようにする。
        if (msg == WM_ERASEBKGND)
        {
            return new IntPtr(1);
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fSourceClientAreaOnly;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle,
        string className,
        string title,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
