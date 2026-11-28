using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

// A→C のフォーカス切替で、C を Topmost 化せず（パネルを一瞬も隠さず・C も帯切替でちらつかせず）、
// 自プロセスの DWM サムネイル盾の裏側で旧フォーカス A を 4 面へ戻し、完了後に盾が閉じて
// Panel > C > A へ収まることを実 HWND で検証する。
internal static class FocusSwitchBackgroundRestoreTests
{
    public const int Count = 5;

    private const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_WINDOWPOSCHANGING = 0x0046;
    private const uint WM_WINDOWPOSCHANGED = 0x0047;
    private const int SW_MAXIMIZE = 3;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;
    private const uint GW_HWNDNEXT = 2;
    private const uint LayerFlags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS;

    public static async Task RunAsync(List<string> failures)
    {
        await RunCaseAsync("A→C背面復元でパネルもCもちらつかず盾がパネル直下でAを隠して完了後に閉じる", TestShieldRestoresBehindPanelAndFocusedAsync, failures);
        await RunCaseAsync("背面復元を途中でキャンセルしてもDWMサムネイル盾が残留しない", TestCanceledBackgroundRestoreHidesShieldAsync, failures);
        await RunCaseAsync("盾無効時でもCをTopmost化せずAを4面へ戻してCの直下へ配置する", TestDisabledShieldNeverPromotesFocusedWindowAsync, failures);
        await RunCaseAsync("最大化済みの背面窓が無いときはDWMサムネイル盾を表示しない", TestNormalTilesSkipFocusShieldAsync, failures);
        await RunCaseAsync("手動最大化時のArrangeOnlyBehindFocusAsyncでもパネル直下盾とC>Aを維持する", TestArrangeOnlyBehindFocusWithShieldAsync, failures);
    }

    private static async Task RunCaseAsync(string name, Func<Task> check, List<string> failures)
    {
        try
        {
            await RunOnStaDispatcherAsync(check);
        }
        catch (Exception ex)
        {
            failures.Add($"フォーカス切替の背面復元 [{name}]: {ex.Message}");
        }
    }

    // 本番と同じく WPF の STA Dispatcher（メッセージループ付き UI スレッド）上で
    // 盾ウィンドウの生成・非同期継続・破棄を実行する。
    private static Task RunOnStaDispatcherAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await action();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static async Task TestShieldRestoresBehindPanelAndFocusedAsync()
    {
        var arranger = new WindowArranger();
        using var shield = new FocusSwitchShieldWindow(arranger);
        using var panel = new TransparentTestWindow("Panel", 20, 20, 220, 120, topmost: true);
        using var winA = new TransparentTestWindow("A", 10, 10, 400, 300, topmost: false);
        using var winC = new TransparentTestWindow("C", 10, 320, 400, 300, topmost: false);

        var slots = CreateSlots(winA.Handle, winC.Handle);
        await SetupMaximizedPairWithCAboveAAsync(arranger, winA.Handle, winC.Handle, placeBelowAnchor: true);

        winC.Monitoring = true;
        var shieldShown = false;
        var pollSawShieldAbovePanel = false;
        var pollSawCAbovePanel = false;
        var pollSawCTopmost = false;
        using var pollStop = new CancellationTokenSource();
        var poller = Task.Run(() =>
        {
            while (!pollStop.IsCancellationRequested)
            {
                var shieldHandle = shield.Handle;
                if (shieldHandle != IntPtr.Zero && arranger.IsAbove(shieldHandle, panel.Handle))
                {
                    pollSawShieldAbovePanel = true;
                }

                if (arranger.IsAbove(winC.Handle, panel.Handle))
                {
                    pollSawCAbovePanel = true;
                }

                if (arranger.IsInTopmostLayer(winC.Handle))
                {
                    pollSawCTopmost = true;
                }

                Thread.SpinWait(40);
            }
        });

        try
        {
            var arranged = await arranger.ArrangeExceptBehindFocusAsync(
                slots,
                excludedSlot: slots[2],
                gap: 6,
                monitorIndex: 0,
                keepAboveHandle: winC.Handle,
                tryBeginShield: source =>
                {
                    shieldShown = shield.TryShowBehind(source, [panel.Handle]);
                    return shieldShown;
                },
                endShield: shield.Hide);

            if (arranged != 1)
            {
                throw new InvalidOperationException($"期待した背面整列件数 1 に対して {arranged} 件でした。");
            }
        }
        finally
        {
            pollStop.Cancel();
            await poller;
            winC.Monitoring = false;
        }

        await WaitUntilAsync(() => !IsZoomed(winA.Handle) && arranger.IsAbove(winC.Handle, winA.Handle), 300);

        if (!shieldShown)
        {
            throw new InvalidOperationException("最大化済み背面窓の復元時に DWM サムネイル盾が有効化されませんでした。");
        }

        if (pollSawShieldAbovePanel || pollSawCAbovePanel)
        {
            throw new InvalidOperationException($"メインパネルより前面に盾または C が出ました (shield>panel={pollSawShieldAbovePanel}, C>panel={pollSawCAbovePanel})。");
        }

        if (pollSawCTopmost || winC.EverObservedTopmost || arranger.IsInTopmostLayer(winC.Handle))
        {
            throw new InvalidOperationException("新フォーカス C が Topmost 帯へ昇格しました（帯切替は禁止）。");
        }

        if (shield.IsActive || (shield.Handle != IntPtr.Zero && IsWindowVisible(shield.Handle)))
        {
            throw new InvalidOperationException("背面復元完了後に DWM サムネイル盾が非表示になっていません。");
        }

        if (IsZoomed(winA.Handle) || !IsZoomed(winC.Handle))
        {
            throw new InvalidOperationException($"最大化状態が不正です (zoomedA={IsZoomed(winA.Handle)}, zoomedC={IsZoomed(winC.Handle)})。");
        }

        if (!arranger.IsAbove(panel.Handle, winC.Handle) || !arranger.IsAbove(winC.Handle, winA.Handle))
        {
            throw new InvalidOperationException($"完了後の Z 順が Panel > C > A になっていません (Panel>C={arranger.IsAbove(panel.Handle, winC.Handle)}, C>A={arranger.IsAbove(winC.Handle, winA.Handle)})。");
        }
    }

    private static async Task TestCanceledBackgroundRestoreHidesShieldAsync()
    {
        var arranger = new WindowArranger();
        using var shield = new FocusSwitchShieldWindow(arranger);
        using var panel = new TransparentTestWindow("Panel", 20, 20, 220, 120, topmost: true);
        using var winA = new TransparentTestWindow("A", 10, 10, 400, 300, topmost: false);
        using var winC = new TransparentTestWindow("C", 10, 320, 400, 300, topmost: false);

        var slots = CreateSlots(winA.Handle, winC.Handle);
        await SetupMaximizedPairWithCAboveAAsync(arranger, winA.Handle, winC.Handle, placeBelowAnchor: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
        try
        {
            await arranger.ArrangeExceptBehindFocusAsync(
                slots,
                excludedSlot: slots[2],
                gap: 6,
                monitorIndex: 0,
                keepAboveHandle: winC.Handle,
                tryBeginShield: source => shield.TryShowBehind(source, [panel.Handle]),
                endShield: shield.Hide,
                cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // キャンセル経路でも finally で盾が必ず閉じることを検証する。
        }

        if (shield.IsActive || (shield.Handle != IntPtr.Zero && IsWindowVisible(shield.Handle)))
        {
            throw new InvalidOperationException("キャンセル後に DWM サムネイル盾が残留しました。");
        }

        if (arranger.IsInTopmostLayer(winC.Handle))
        {
            throw new InvalidOperationException("キャンセル後に C が Topmost 帯になっています。");
        }
    }

    private static async Task TestDisabledShieldNeverPromotesFocusedWindowAsync()
    {
        var arranger = new WindowArranger();
        using var winA = new TransparentTestWindow("A", 10, 10, 400, 300, topmost: false);
        using var winC = new TransparentTestWindow("C", 10, 320, 400, 300, topmost: false);

        var slots = CreateSlots(winA.Handle, winC.Handle);
        await SetupMaximizedPairWithCAboveAAsync(arranger, winA.Handle, winC.Handle, placeBelowAnchor: false);

        winC.Monitoring = true;
        var arranged = await arranger.ArrangeExceptBehindFocusAsync(
            slots,
            excludedSlot: slots[2],
            gap: 6,
            monitorIndex: 0,
            keepAboveHandle: winC.Handle);
        winC.Monitoring = false;

        if (arranged != 1)
        {
            throw new InvalidOperationException($"期待した背面整列件数 1 に対して {arranged} 件でした。");
        }

        if (winC.EverObservedTopmost || arranger.IsInTopmostLayer(winC.Handle))
        {
            throw new InvalidOperationException("盾無効時に C が Topmost 帯へ昇格しました。");
        }

        if (IsZoomed(winA.Handle) || !arranger.IsAbove(winC.Handle, winA.Handle))
        {
            throw new InvalidOperationException($"盾無効時の背面復元結果が不正です (zoomedA={IsZoomed(winA.Handle)}, C>A={arranger.IsAbove(winC.Handle, winA.Handle)})。");
        }
    }

    private static async Task TestNormalTilesSkipFocusShieldAsync()
    {
        var arranger = new WindowArranger();
        using var winA = new TransparentTestWindow("A", 10, 10, 400, 300, topmost: false);
        using var winC = new TransparentTestWindow("C", 10, 320, 400, 300, topmost: false);

        var slots = CreateSlots(winA.Handle, winC.Handle);
        ShowWindowAsync(winC.Handle, SW_MAXIMIZE);
        await WaitUntilAsync(() => IsZoomed(winC.Handle), 250);
        arranger.PlaceDirectlyBehind(winA.Handle, winC.Handle);
        await WaitUntilAsync(() => arranger.IsAbove(winC.Handle, winA.Handle), 250);

        var shieldCalled = false;
        var arranged = await arranger.ArrangeExceptBehindFocusAsync(
            slots,
            excludedSlot: slots[2],
            gap: 6,
            monitorIndex: 0,
            keepAboveHandle: winC.Handle,
            tryBeginShield: _ =>
            {
                shieldCalled = true;
                return true;
            },
            endShield: () => { });

        if (arranged != 1)
        {
            throw new InvalidOperationException($"期待した背面整列件数 1 に対して {arranged} 件でした。");
        }

        if (shieldCalled)
        {
            throw new InvalidOperationException("既に通常状態のタイルしかないのに盾の起動コールバックが呼ばれました。");
        }
    }

    private static async Task TestArrangeOnlyBehindFocusWithShieldAsync()
    {
        var arranger = new WindowArranger();
        using var shield = new FocusSwitchShieldWindow(arranger);
        using var panel = new TransparentTestWindow("Panel", 20, 20, 220, 120, topmost: true);
        using var winA = new TransparentTestWindow("A", 10, 10, 400, 300, topmost: false);
        using var winC = new TransparentTestWindow("C", 10, 320, 400, 300, topmost: false);

        var slots = CreateSlots(winA.Handle, winC.Handle);
        await SetupMaximizedPairWithCAboveAAsync(arranger, winA.Handle, winC.Handle, placeBelowAnchor: true);

        var sawShieldAbovePanel = false;
        using var pollStop = new CancellationTokenSource();
        var poller = Task.Run(() =>
        {
            while (!pollStop.IsCancellationRequested)
            {
                var shieldHandle = shield.Handle;
                if (shieldHandle != IntPtr.Zero && arranger.IsAbove(shieldHandle, panel.Handle))
                {
                    sawShieldAbovePanel = true;
                }

                Thread.SpinWait(40);
            }
        });

        int arranged;
        try
        {
            arranged = await arranger.ArrangeOnlyBehindFocusAsync(
                slots,
                handles: [winA.Handle],
                gap: 6,
                monitorIndex: 0,
                keepAboveHandle: winC.Handle,
                tryBeginShield: source => shield.TryShowBehind(source, [panel.Handle]),
                endShield: shield.Hide);
        }
        finally
        {
            pollStop.Cancel();
            await poller;
        }

        await WaitUntilAsync(() => !IsZoomed(winA.Handle) && arranger.IsAbove(winC.Handle, winA.Handle), 300);

        if (arranged != 1 || sawShieldAbovePanel || shield.IsActive || IsZoomed(winA.Handle)
            || arranger.IsInTopmostLayer(winC.Handle) || !arranger.IsAbove(panel.Handle, winC.Handle)
            || !arranger.IsAbove(winC.Handle, winA.Handle))
        {
            throw new InvalidOperationException($"ArrangeOnlyBehindFocusAsync の検証に失敗しました (arranged={arranged}, shield>panel={sawShieldAbovePanel}, shieldActive={shield.IsActive}, zoomedA={IsZoomed(winA.Handle)}, topC={arranger.IsInTopmostLayer(winC.Handle)}, Panel>C={arranger.IsAbove(panel.Handle, winC.Handle)}, C>A={arranger.IsAbove(winC.Handle, winA.Handle)})。");
        }
    }

    private static WindowSlot[] CreateSlots(IntPtr handleA, IntPtr handleC)
    {
        var slots = Enumerable.Range(0, 4)
            .Select(index => new WindowSlot(new SlotConfig { Name = ((char)('A' + index)).ToString() }))
            .ToArray();
        slots[0].WindowHandle = handleA;
        slots[0].IsFocused = false;
        slots[2].WindowHandle = handleC;
        slots[2].IsFocused = true;
        return slots;
    }

    private static async Task SetupMaximizedPairWithCAboveAAsync(
        WindowArranger arranger,
        IntPtr handleA,
        IntPtr handleC,
        bool placeBelowAnchor)
    {
        ShowWindowAsync(handleA, SW_MAXIMIZE);
        ShowWindowAsync(handleC, SW_MAXIMIZE);
        await WaitUntilAsync(() => IsZoomed(handleA) && IsZoomed(handleC), 300);

        if (placeBelowAnchor)
        {
            var anchor = GetTopWindow(IntPtr.Zero);
            for (var i = 0; i < 16 && anchor != IntPtr.Zero; i++)
            {
                var next = GetWindow(anchor, GW_HWNDNEXT);
                if (next == IntPtr.Zero)
                {
                    break;
                }

                anchor = next;
            }

            if (anchor != IntPtr.Zero && anchor != handleC && anchor != handleA)
            {
                SetWindowPos(handleC, anchor, 0, 0, 0, 0, LayerFlags);
                await Task.Delay(30);
            }
        }

        SetWindowPos(handleA, handleC, 0, 0, 0, 0, LayerFlags);
        await WaitUntilAsync(() => arranger.IsAbove(handleC, handleA), 300);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(8);
        }
    }

    // 画面へ映らずクリックも透過する別スレッドのネイティブ検証ウィンドウ。
    private sealed class TransparentTestWindow : IDisposable
    {
        private readonly Thread _thread;
        private readonly WndProcDelegate _proc;
        private readonly WindowArranger _arranger = new();

        public IntPtr Handle { get; private set; }
        public bool Monitoring { get; set; }
        public bool EverObservedTopmost { get; private set; }

        public TransparentTestWindow(string name, int x, int y, int width, int height, bool topmost)
        {
            _proc = WndProc;
            var ready = new ManualResetEventSlim();
            Exception? startupError = null;
            _thread = new Thread(() =>
            {
                try
                {
                    var className = $"QuartetFocusBgTest_{name}_{Guid.NewGuid():N}";
                    var wc = new WNDCLASSEX
                    {
                        cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                        lpfnWndProc = _proc,
                        hInstance = GetModuleHandle(null),
                        lpszClassName = className
                    };
                    if (RegisterClassExW(ref wc) == 0)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    var exStyle = (uint)(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | (topmost ? WS_EX_TOPMOST : 0));
                    var style = (uint)(WS_OVERLAPPEDWINDOW | WS_VISIBLE);
                    Handle = CreateWindowExW(exStyle, className, name, style, x, y, width, height, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                    if (Handle == IntPtr.Zero)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    SetLayeredWindowAttributes(Handle, 0, 0, LWA_ALPHA);
                    ready.Set();

                    while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        TranslateMessage(ref msg);
                        DispatchMessageW(ref msg);
                    }
                }
                catch (Exception ex)
                {
                    startupError = ex;
                    ready.Set();
                }
            })
            {
                IsBackground = true
            };
            _thread.Start();
            ready.Wait();
            if (startupError is not null)
            {
                throw startupError;
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (Monitoring && (msg == WM_WINDOWPOSCHANGING || msg == WM_WINDOWPOSCHANGED))
            {
                if (_arranger.IsInTopmostLayer(hWnd))
                {
                    EverObservedTopmost = true;
                }
            }

            if (msg == WM_DESTROY)
            {
                PostMessageW(IntPtr.Zero, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }

            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                PostMessageW(Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                _thread.Join(1500);
                Handle = IntPtr.Zero;
            }
        }
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
