using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VscodeSquare.Panel.Models;
using VscodeSquare.Panel.Services;

namespace VscodeSquare.Panel;

public partial class MainWindow : Window
{
    private readonly WindowEnumerator _windowEnumerator = new();
    private readonly WindowArranger _windowArranger = new();
    private readonly VscodeLauncher _vscodeLauncher;
    private readonly StatusStore _statusStore;
    private readonly DispatcherTimer _refreshTimer;
    private WindowSlot.SlotWindowLayerMode _managedWindowLayerMode = WindowSlot.SlotWindowLayerMode.Topmost;
    private int? _activeMonitorIndex;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();

        var config = AppConfig.Load();
        _statusStore = new StatusStore(config);
        _vscodeLauncher = new VscodeLauncher(_windowEnumerator);
        DataContext = _statusStore;

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += (_, _) => RefreshSlots();
        _refreshTimer.Start();

        Loaded += (_, _) =>
        {
            Topmost = true;
            RefreshSlots();
            ApplyManagedWindowLayers();
        };
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            _statusStore.LoadSavedSettings();
            RefreshSlots();

            if (!_vscodeLauncher.IsCodeCommandAvailable(_statusStore.Config.CodeCommand))
            {
                _statusStore.Message = $"`{_statusStore.Config.CodeCommand}` が見つかりません。VS Codeのコマンドまたは設定を確認してください。";
                return;
            }

            _statusStore.Message = "未起動のVS Codeを起動しています...";
            var assignments = await _vscodeLauncher.LaunchMissingAsync(
                _statusStore.Slots,
                _statusStore.Config,
                CancellationToken.None);

            foreach (var assignment in assignments)
            {
                _statusStore.AssignWindow(assignment.Slot, assignment.Window);
            }

            RefreshSlots();

            if (assignments.Count > 0)
            {
                ArrangeSlotsOnActiveMonitor();
                _statusStore.ClearFocusedSlot();
                _statusStore.Message = $"{assignments.Count}個のVS Codeを起動して2x2に配置しました。";
            }
            else
            {
                var arranged = ArrangeSlotsOnActiveMonitor();
                _statusStore.ClearFocusedSlot();
                _statusStore.Message = arranged > 0
                    ? $"{arranged}個のVS Codeを2x2に配置しました。"
                    : "新しいVS Codeウィンドウは見つかりませんでした。";
            }
        });
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshSlots();
        _statusStore.SaveCurrentSettings();
        _statusStore.Message = "設定を保存しました。";
    }

    private void LoadSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _statusStore.LoadSavedSettings();
        RefreshSlots();
        _statusStore.Message = "設定を読み込みました。";
    }

    private void CloseAllButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshSlots();
        _statusStore.SaveCurrentSettings();

        var closed = 0;
        foreach (var slot in _statusStore.Slots)
        {
            if (!_windowArranger.Close(slot.WindowHandle))
            {
                continue;
            }

            _statusStore.ClearWindow(slot);
            closed++;
        }

        _statusStore.Message = closed == 0
            ? "閉じるVS Codeウィンドウがありません。"
            : $"{closed}個のVS Codeを閉じて設定を保存しました。";
    }

    private void SlotCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        if (IsInteractiveCardChild(e.OriginalSource as DependencyObject))
        {
            return;
        }

        ToggleSlotFocus(slot);
    }

    private void ToggleSlotFocus(WindowSlot slot)
    {
        RefreshSlots();

        if (slot.IsFocused)
        {
            var arranged = ArrangeSlotsOnActiveMonitor();
            _statusStore.ClearFocusedSlot();
            _statusStore.Message = arranged == 0
                ? "4分割表示に戻せるVS Codeウィンドウがありません。"
                : $"{arranged}個のVS Codeを4分割表示に戻しました。";
            return;
        }

        SetManagedWindowLayer(WindowSlot.SlotWindowLayerMode.Topmost, false);
        if (_windowArranger.FocusMaximized(slot.WindowHandle))
        {
            _statusStore.SetFocusedSlot(slot);
            BringPanelToFront();
            _statusStore.Message = $"スロット{slot.Name}をフォーカス表示しました。";
            return;
        }

        _statusStore.Message = $"スロット{slot.Name}のVS Codeウィンドウが見つかりません。";
    }

    private void PinAllTopButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshSlots();
        if (SetManagedWindowLayer(WindowSlot.SlotWindowLayerMode.Topmost))
        {
            _statusStore.Message = "管理中のVS Codeを最前面にしました。";
            return;
        }

        _statusStore.Message = "最前面にできるVS Codeウィンドウがありません。";
    }

    private void SendAllBackButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshSlots();
        if (_statusStore.Slots.Any(slot => slot.IsFocused))
        {
            _statusStore.ClearFocusedSlot();
            ArrangeSlotsOnActiveMonitor();
        }

        if (SetManagedWindowLayer(WindowSlot.SlotWindowLayerMode.Backmost))
        {
            _statusStore.Message = "管理中のVS Codeを最背面にしました。";
            return;
        }

        _statusStore.Message = "最背面にできるVS Codeウィンドウがありません。";
    }

    private void ToggleMonitorButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshSlots();

        var monitorCount = _windowArranger.GetMonitorCount();
        if (monitorCount <= 1)
        {
            _statusStore.Message = "利用可能なディスプレイが1枚のため切り替えできません。";
            return;
        }

        var nextMonitorIndex = (GetActiveMonitorIndex() + 1) % monitorCount;
        _activeMonitorIndex = nextMonitorIndex;

        var arranged = ArrangeSlotsOnActiveMonitor();
        _statusStore.ClearFocusedSlot();
        _statusStore.Message = arranged > 0
            ? $"{arranged}個のVS Codeを{_windowArranger.GetMonitorLabel(nextMonitorIndex)}に移動しました。"
            : $"次回の配置先を{_windowArranger.GetMonitorLabel(nextMonitorIndex)}に切り替えました。";
    }

    private void CloseSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        RefreshSlots();
        _statusStore.CaptureWorkspacePath(slot);
        if (_windowArranger.Close(slot.WindowHandle))
        {
            _statusStore.ClearWindow(slot);
            _statusStore.Message = $"スロット{slot.Name}を閉じました。";
            return;
        }

        _statusStore.Message = $"スロット{slot.Name}のVS Codeウィンドウが見つかりません。";
    }

    private void RefreshSlots()
    {
        _statusStore.RefreshWindowStatuses(_windowEnumerator);
    }

    private int ArrangeSlotsOnActiveMonitor()
    {
        var arranged = _windowArranger.Arrange(_statusStore.Slots, _statusStore.Config.Gap, GetActiveMonitorIndex());
        ApplyManagedWindowLayers();
        return arranged;
    }

    private void ApplyManagedWindowLayers()
    {
        SetManagedWindowLayer(_managedWindowLayerMode);
    }

    private bool SetManagedWindowLayer(WindowSlot.SlotWindowLayerMode layerMode, bool bringPanelAfterChange = true)
    {
        _managedWindowLayerMode = layerMode;
        var appliedAny = false;

        foreach (var slot in _statusStore.Slots)
        {
            appliedAny |= ApplyLayerToSlot(slot, layerMode, false);
        }

        if (bringPanelAfterChange)
        {
            BringPanelToFront();
        }

        return appliedAny;
    }

    private bool ApplyLayerToSlot(WindowSlot slot, WindowSlot.SlotWindowLayerMode layerMode, bool bringPanelAfterChange = true)
    {
        if (slot.WindowHandle == IntPtr.Zero)
        {
            slot.WindowLayerMode = layerMode;
            return false;
        }

        slot.WindowLayerMode = layerMode;
        var applied = layerMode switch
        {
            WindowSlot.SlotWindowLayerMode.Topmost => _windowArranger.SetTopmost(slot.WindowHandle),
            WindowSlot.SlotWindowLayerMode.Backmost => _windowArranger.SetBackmost(slot.WindowHandle),
            _ => false
        };

        if (bringPanelAfterChange)
        {
            BringPanelToFront();
        }

        return applied;
    }

    private void BringPanelToFront()
    {
        var panelHandle = new WindowInteropHelper(this).Handle;
        if (panelHandle == IntPtr.Zero)
        {
            return;
        }

        if (!Topmost)
        {
            Topmost = true;
        }

        _windowArranger.BringToFront(panelHandle);
    }

    private static bool IsInteractiveCardChild(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBox)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private int GetActiveMonitorIndex()
    {
        if (_activeMonitorIndex.HasValue)
        {
            return _activeMonitorIndex.Value;
        }

        foreach (var slot in _statusStore.Slots)
        {
            var detectedMonitorIndex = _windowArranger.GetMonitorIndexForWindow(slot.WindowHandle);
            if (detectedMonitorIndex >= 0)
            {
                _activeMonitorIndex = detectedMonitorIndex;
                return detectedMonitorIndex;
            }
        }

        _activeMonitorIndex = _windowArranger.GetDefaultMonitorIndex(_statusStore.Config.Monitor);
        return _activeMonitorIndex.Value;
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        _isBusy = true;
        SetBusyState(true);

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            _statusStore.Message = ex.Message;
        }
        finally
        {
            _isBusy = false;
            SetBusyState(false);
        }
    }

    private void SetBusyState(bool busy)
    {
        LaunchButton.IsEnabled = !busy;
        TopmostAllButton.IsEnabled = !busy;
        BackmostAllButton.IsEnabled = !busy;
        ToggleMonitorButton.IsEnabled = !busy;
        SaveSettingsButton.IsEnabled = !busy;
        LoadSettingsButton.IsEnabled = !busy;
        CloseAllButton.IsEnabled = !busy;
    }
}
