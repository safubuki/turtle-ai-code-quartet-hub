using System.ComponentModel;
using System.Runtime.InteropServices;
using TurtleAIQuartetHub.Panel.Services;

// ブラウザ等の外部アプリ前面時・極小表示（WS_EX_NOACTIVATE）から呼び出した場合でも、
// フォアグラウンドロックによるタスクバー点滅や背面残留を起こさず確実に前面化できることを検証する。
internal static class ForegroundActivationTests
{
    public const int Count = 5;

    private const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_QUIT = 0x0012;
    private const int SW_MINIMIZE = 6;

    public static async Task RunAsync(List<string> failures)
    {
        await RunCaseAsync("無効ハンドルでは TryActivateForegroundWindow / StopWindowFlashing が安全に false を返す", TestInvalidHandlesReturnFalseAsync, failures);
        await RunCaseAsync("別スレッドの外部窓より背後に置かれた対象窓を TryActivateForegroundWindow で前面へ引き出せる", TestBehindExternalWindowBroughtAboveAsync, failures);
        await RunCaseAsync("最小化状態の対象窓を TryActivateForegroundWindow で復元して前面へ出せる", TestMinimizedWindowRestoredAndActivatedAsync, failures);
        await RunCaseAsync("FocusMaximizedOnMonitorAsync で背後窓を最大化し外部窓より前面へ出せる", TestFocusMaximizedOnMonitorBringsAboveExternalAsync, failures);
        await RunCaseAsync("ソース構成：全フォーカス経路が TryActivateForegroundWindow・ハング保護・点滅停止・4画面前面突破を備える", TestSourceGuardsAsync, failures);
    }

    private static async Task RunCaseAsync(string name, Func<Task> check, List<string> failures)
    {
        try
        {
            await check();
        }
        catch (Exception ex)
        {
            failures.Add($"フォアグラウンド前面化・ロック突破 [{name}]: {ex.Message}");
        }
    }

    private static Task TestInvalidHandlesReturnFalseAsync()
    {
        var arranger = new WindowArranger();
        if (arranger.TryActivateForegroundWindow(IntPtr.Zero))
        {
            throw new InvalidOperationException("IntPtr.Zero で true が返りました。");
        }

        if (arranger.StopWindowFlashing(IntPtr.Zero))
        {
            throw new InvalidOperationException("IntPtr.Zero で StopWindowFlashing が true を返りました。");
        }

        return Task.CompletedTask;
    }

    private static async Task TestBehindExternalWindowBroughtAboveAsync()
    {
        var arranger = new WindowArranger();
        using var target = new IsolatedTestWindow("Target", 40, 40, 320, 220);
        using var external = new IsolatedTestWindow("ExternalBrowser", 40, 40, 320, 220);

        // 擬似ブラウザ（external）を対象（target）より上に置く。
        arranger.PlaceDirectlyBehind(target.Handle, external.Handle);
        await WaitUntilAsync(() => arranger.IsAbove(external.Handle, target.Handle), 300);
        if (!arranger.IsAbove(external.Handle, target.Handle))
        {
            throw new InvalidOperationException("事前条件（external > target）を整えられませんでした。");
        }

        _ = arranger.TryActivateForegroundWindow(target.Handle);
        await WaitUntilAsync(() => arranger.IsAbove(target.Handle, external.Handle), 500);
        if (!arranger.IsAbove(target.Handle, external.Handle))
        {
            throw new InvalidOperationException("TryActivateForegroundWindow 後も target が external より上へ出ていません。");
        }
    }

    private static async Task TestMinimizedWindowRestoredAndActivatedAsync()
    {
        var arranger = new WindowArranger();
        using var target = new IsolatedTestWindow("MinimizedTarget", 60, 60, 300, 200);

        _ = ShowWindowAsync(target.Handle, SW_MINIMIZE);
        await WaitUntilAsync(() => arranger.IsMinimized(target.Handle), 400);
        if (!arranger.IsMinimized(target.Handle))
        {
            throw new InvalidOperationException("事前条件（最小化）を整えられませんでした。");
        }

        _ = arranger.TryActivateForegroundWindow(target.Handle);
        await WaitUntilAsync(() => !arranger.IsMinimized(target.Handle), 600);
        if (arranger.IsMinimized(target.Handle))
        {
            throw new InvalidOperationException("TryActivateForegroundWindow 後も最小化のままです。");
        }
    }

    private static async Task TestFocusMaximizedOnMonitorBringsAboveExternalAsync()
    {
        var arranger = new WindowArranger();
        using var target = new IsolatedTestWindow("MaxTarget", 80, 80, 320, 220);
        using var external = new IsolatedTestWindow("ExternalCover", 80, 80, 320, 220);

        arranger.PlaceDirectlyBehind(target.Handle, external.Handle);
        await WaitUntilAsync(() => arranger.IsAbove(external.Handle, target.Handle), 300);

        var ok = await arranger.FocusMaximizedOnMonitorAsync(target.Handle, 0);
        if (!ok)
        {
            throw new InvalidOperationException("FocusMaximizedOnMonitorAsync が false を返しました。");
        }

        await WaitUntilAsync(() => IsZoomed(target.Handle) && arranger.IsAbove(target.Handle, external.Handle), 500);
        if (!IsZoomed(target.Handle))
        {
            throw new InvalidOperationException("対象ウィンドウが最大化されていません。");
        }

        if (!arranger.IsAbove(target.Handle, external.Handle))
        {
            throw new InvalidOperationException("最大化後も対象ウィンドウが external より前面に出ていません。");
        }
    }

    private static Task TestSourceGuardsAsync()
    {
        var repoRoot = FindRepoRoot();
        var arrangerText = File.ReadAllText(Path.Combine(repoRoot, "src", "TurtleAIQuartetHub.Panel", "Services", "WindowArranger.cs"));
        var mainWindowText = File.ReadAllText(Path.Combine(repoRoot, "src", "TurtleAIQuartetHub.Panel", "MainWindow.xaml.cs"));

        var requiredArrangerTokens = new[]
        {
            "public bool TryActivateForegroundWindow(",
            "AllowSetForegroundWindow(",
            "AttachThreadInput(",
            "IsHungAppWindow(",
            "FlashWindowEx(",
            "FLASHW_STOP",
            "StopWindowFlashing("
        };
        foreach (var token in requiredArrangerTokens)
        {
            if (!arrangerText.Contains(token, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"WindowArranger.cs に必須要素 '{token}' が見つかりません。");
            }
        }

        var requiredMainTokens = new[]
        {
            "TryActivateForegroundWindow(",
            "HasAnyManagedSlotObscuredByExternalWindow()",
            "EnsureManagedTilesAboveExternalWindows()",
            "IsSelfOrChild(source, SlotCliPickerPopup.Child)",
            "IsSelfOrChild(source, StandardAiAppsPopup.Child)"
        };
        foreach (var token in requiredMainTokens)
        {
            if (!mainWindowText.Contains(token, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"MainWindow.xaml.cs に必須要素 '{token}' が見つかりません。");
            }
        }

        return Task.CompletedTask;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "TurtleAIQuartetHub.Panel", "Services", "WindowArranger.cs")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("リポジトリルートを検出できませんでした。");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (!predicate() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(12);
        }
    }

    // 各検証窓を別スレッド上に作成し、他スレッド／他プロセス相当の非同期メッセージキュー環境を再現する。
    private sealed class IsolatedTestWindow : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private WndProc? _wndProc;
        private Exception? _initException;

        public IntPtr Handle { get; private set; }

        public IsolatedTestWindow(string title, int x, int y, int width, int height)
        {
            _thread = new Thread(() => RunLoop(title, x, y, width, height)) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException($"検証ウィンドウ '{title}' の生成がタイムアウトしました。");
            }

            if (_initException is not null)
            {
                throw _initException;
            }
        }

        private void RunLoop(string title, int x, int y, int width, int height)
        {
            var className = $"TurtleForegroundTest_{Guid.NewGuid():N}";
            try
            {
                _wndProc = WindowProc;
                var hInstance = GetModuleHandle(null);
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = _wndProc,
                    hInstance = hInstance,
                    lpszClassName = className
                };
                if (RegisterClassExW(ref wc) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                // 視覚的に画面を邪魔しない透明・クリックスルー・ツールウィンドウとして生成する。
                var exStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW;
                Handle = CreateWindowExW(
                    exStyle,
                    className,
                    title,
                    WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                    x,
                    y,
                    width,
                    height,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    hInstance,
                    IntPtr.Zero);
                if (Handle == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                _ = SetLayeredWindowAttributes(Handle, 0, 1, LWA_ALPHA);
                _ready.Set();

                while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    _ = TranslateMessage(ref msg);
                    _ = DispatchMessageW(ref msg);
                }
            }
            catch (Exception ex)
            {
                _initException = ex;
                _ready.Set();
            }
            finally
            {
                _ = UnregisterClassW(className, GetModuleHandle(null));
            }
        }

        private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_DESTROY)
            {
                PostQuitMessage(0);
                return IntPtr.Zero;
            }

            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                _ = PostMessage(Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }

            _thread.Join(TimeSpan.FromSeconds(2));
            _ready.Dispose();
        }
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
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
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);
}
