using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using TurtleAIQuartetHub.Panel;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;
using TurtleAIQuartetHub.Tools;

internal static class Program
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;

    [STAThread]
    private static int Main() => WpfCheckGuard.Run(Run);

    private static int Run()
    {
        RunChecks(asyncWindows: false);
        RunChecks(asyncWindows: true);
        Console.WriteLine($"前面・背面の実HWND統合チェック {_checks}件が合格しました。");
        return 0;
    }

    private static void RunChecks(bool asyncWindows)
    {
        using var fixture = new Fixture(asyncWindows);
        foreach (var compact in new[] { false, true })
        {
            foreach (var focused in new[] { false, true })
            {
                var description = $"{(compact ? "縮小" : "標準")}・{(focused ? "集中" : "4面")}";
                fixture.Reset(focused);
                fixture.Click(compact, front: false);
                Check(description + "の背面ボタンで全窓が管理外窓より後ろへ移動", fixture.IsBehindExternal);
                fixture.Click(compact, front: true);
                Check(description + "の前面ボタンで全窓が管理外窓より前へ移動", fixture.IsInFrontOfExternal);
            }
        }

        fixture.Reset(focused: true);
        fixture.Click(compact: false, front: false);
        fixture.Call("EnsureFocusedSlotAboveTiles", fixture.Slots[0], false);
        Check("再適用でも最背面と集中窓 > タイルを維持", fixture.IsBehindExternal);

        fixture.Raise(fixture.Slots[1].WindowHandle);
        fixture.Call("RepairFocusedZOrderIfNeeded", fixture.Slots[0]);
        Check("周期Z順補正でも管理外窓より後ろを維持", fixture.IsBehindExternal);

        fixture.Call("ScheduleFocusTransitionLayerFinalize");
        Pump(TimeSpan.FromMilliseconds(600));
        Check("遅延したレイヤー確定でも最背面を維持", fixture.IsBehindExternal);

        var before = fixture.Order();
        fixture.Set("_areWindowsHidden", true);
        fixture.Click(compact: true, front: true);
        fixture.Click(compact: true, front: false);
        Check("非表示中は前後移動を保留してレイヤー状態だけ更新",
            () => before.SequenceEqual(fixture.Order())
                && fixture.Slots.All(slot => slot.WindowLayerMode == WindowSlot.SlotWindowLayerMode.Backmost));
    }

    private static void Check(string name, Func<bool> assertion)
    {
        // 非同期要求が未処理の一瞬を成功とみなさず、遅延した要求と順序補正も確認する。
        Pump(TimeSpan.FromMilliseconds(100));
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!assertion() && DateTime.UtcNow < deadline)
        {
            Pump(TimeSpan.FromMilliseconds(20));
        }

        if (!assertion())
        {
            throw new InvalidOperationException(name);
        }

        _checks++;
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MainWindow _panel = new();
        private readonly WindowArranger _arranger = new();
        private readonly List<IntPtr> _windows = [];
        private readonly List<(Thread Thread, Dispatcher Dispatcher)> _windowThreads = [];
        private readonly bool _asyncWindows;
        private readonly WindowFocusSynchronizer _sync;
        private bool _focused;
        private readonly IntPtr _external;
        public WindowSlot[] Slots { get; }

        public Fixture(bool asyncWindows)
        {
            _asyncWindows = asyncWindows;
            ((DispatcherTimer)Get("_refreshTimer")!).Stop();
            var store = (StatusStore)Get("_statusStore")!;
            typeof(StatusStore).GetField("_suppressPersistence", Flags)!.SetValue(store, true);
            store.Config.StateDirectory = Path.Combine(AppContext.BaseDirectory, "isolated-state");
            Slots = store.Slots.ToArray();
            var overlayHandler = (PropertyChangedEventHandler)typeof(MainWindow)
                .GetMethod("Slot_PropertyChanged", Flags)!.CreateDelegate(typeof(PropertyChangedEventHandler), _panel);
            foreach (var slot in Slots)
            {
                slot.PropertyChanged -= overlayHandler;
                slot.WindowHandle = CreateHiddenWindow();
                slot.MonitorOverride = null;
                slot.IsFocused = false;
            }

            _external = CreateHiddenWindow();
            _sync = new WindowFocusSynchronizer(
                handle => _focused && handle == Slots[0].WindowHandle
                    ? ManagedWindowState.Maximized : ManagedWindowState.Normal,
                _ => 0, _arranger.IsAbove);
            Set("_windowFocusSynchronizer", _sync);
        }

        private IntPtr CreateHiddenWindow()
        {
            // 検証専用の非表示トップレベル窓。ユーザーのアプリ・保存状態は操作しない。
            if (_asyncWindows)
            {
                var ready = new TaskCompletionSource<(IntPtr Handle, Dispatcher Dispatcher)>();
                var thread = new Thread(() =>
                {
                    try
                    {
                        var handle = CreateNativeWindow();
                        ready.SetResult((handle, Dispatcher.CurrentDispatcher));
                        Dispatcher.Run();
                        DestroyWindow(handle);
                    }
                    catch (Exception exception)
                    {
                        ready.TrySetException(exception);
                    }
                }) { IsBackground = true };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                var created = ready.Task.GetAwaiter().GetResult();
                _windowThreads.Add((thread, created.Dispatcher));
                _windows.Add(created.Handle);
                return created.Handle;
            }

            var localHandle = CreateNativeWindow();
            _windows.Add(localHandle);
            return localHandle;
        }

        private static IntPtr CreateNativeWindow()
        {
            var handle = CreateWindowExW(0x08000080, "STATIC", "Window layer regression check",
                0x80000000, 0, 0, 80, 80, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return handle;
        }

        public void Reset(bool focused)
        {
            _focused = focused;
            Set("_areWindowsHidden", false);
            foreach (var slot in Slots)
            {
                slot.IsFocused = focused && ReferenceEquals(slot, Slots[0]);
                Raise(slot.WindowHandle);
            }

            _sync.Observe(Slots);
            Raise(_external);
            DrainWindowRequests();
        }

        public void Click(bool compact, bool front)
        {
            Button button;
            if (compact)
            {
                var grid = (Grid)((Border)_panel.FindName("CompactBarPanel")).Child;
                button = grid.Children.OfType<Button>().Single(candidate =>
                    Equals(candidate.Content, front ? "前" : "背"));
            }
            else
            {
                button = (Button)_panel.FindName(front ? "TopmostAllButton" : "BackmostAllButton");
            }

            // 実際のXAMLに登録されたClickハンドラを実行する。
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        }

        public bool IsBehindExternal() => HasExpectedFocusOrder()
            && Slots.All(slot => _arranger.IsAbove(_external, slot.WindowHandle) && IsNormalLayer(slot.WindowHandle));

        public bool IsInFrontOfExternal() => HasExpectedFocusOrder()
            && Slots.All(slot => _arranger.IsAbove(slot.WindowHandle, _external) && IsNormalLayer(slot.WindowHandle));

        private bool HasExpectedFocusOrder() => Slots[0].IsFocused == _focused
            && (!_focused || Slots.Skip(1).All(slot => _arranger.IsAbove(Slots[0].WindowHandle, slot.WindowHandle)));

        private static bool IsNormalLayer(IntPtr handle) => !IsWindowVisible(handle)
            && (GetWindowLongW(handle, -20) & 0x00000008) == 0;

        public IntPtr[] Order() => _windows.OrderBy(handle =>
            _windows.Count(other => _arranger.IsAbove(other, handle))).ToArray();

        public void Raise(IntPtr handle)
        {
            if (!SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0013))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        private void DrainWindowRequests()
        {
            foreach (var (_, dispatcher) in _windowThreads)
            {
                dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
        }

        private object? Get(string name) => typeof(MainWindow).GetField(name, Flags)!.GetValue(_panel);
        public void Set(string name, object value) => typeof(MainWindow).GetField(name, Flags)!.SetValue(_panel, value);
        public object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, Flags)!.Invoke(_panel, args);

        public void Dispose()
        {
            Call("CancelFocusSwitchArrange");
            Call("CancelScheduledPanelFrontRestore");
            Call("CancelScheduledFocusedSlotReassert");
            // 本体の終了時復元を通さず、検証用窓を非表示のまま破棄する。
            Set("_areWindowsHidden", false);
            Set("_closeConfirmed", true);
            _panel.Close();
            foreach (var (thread, dispatcher) in _windowThreads)
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                if (!thread.Join(TimeSpan.FromSeconds(2)))
                {
                    throw new InvalidOperationException("検証用ウィンドウのスレッドが終了しませんでした。");
                }
            }

            foreach (var handle in _windows.Where(_ => !_asyncWindows))
            {
                DestroyWindow(handle);
            }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr handle, int index);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr handle);
}
