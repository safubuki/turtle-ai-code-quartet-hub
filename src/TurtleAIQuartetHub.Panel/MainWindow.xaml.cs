using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

public partial class MainWindow : Window
{
    private const double CompactWindowMinHeight = 146;
    private const double CompactWindowMinWidth = 430;
    private const double CompactWindowWidthScale = 0.64;
    private const double MicroWindowSize = 116;
    // \u7E2E\u5C0F=Tile, \u6975\u5C0F=AppIconDefault(2x2 \u30C9\u30C3\u30C8), \u6A19\u6E96=ShowResults\u3002
    // \u30DC\u30BF\u30F3\u306B\u306F\u300C\u6B21\u306B\u9077\u79FB\u3059\u308B\u30E2\u30FC\u30C9\u300D\u306E\u30A2\u30A4\u30B3\u30F3\u3092\u8868\u793A\u3059\u308B\u3002
    private const string CompactModeGlyph = "\uE73F";
    private const string MicroModeGlyph = "\uF158";
    private const string StandardModeGlyph = "\uE740";
    private static readonly TimeSpan PanelFrontRestoreDelay = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan FocusedSlotReassertDelay = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan FocusedSlotReassertInputSuppressWindow = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan DragDropFocusSuppressWindow = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan InteractiveRefreshSuppressWindow = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan[] PostLaunchArrangeRetryDelays =
    [
        TimeSpan.FromMilliseconds(1500),
        TimeSpan.FromMilliseconds(3000),
        TimeSpan.FromMilliseconds(5000),
        TimeSpan.FromMilliseconds(8000),
        TimeSpan.FromMilliseconds(12000),
        TimeSpan.FromMilliseconds(20000),
        TimeSpan.FromMilliseconds(30000)
    ];
    private readonly WindowEnumerator _windowEnumerator = new();
    private readonly WindowArranger _windowArranger = new();
    private readonly VscodeLauncher _vscodeLauncher;
    private readonly ApplicationLauncher _applicationLauncher;
    private readonly StatusStore _statusStore;
    private readonly DispatcherTimer _refreshTimer;
    private readonly CancellationTokenSource _refreshCancellation = new();
    private WindowSlot.SlotWindowLayerMode _managedWindowLayerMode = WindowSlot.SlotWindowLayerMode.Topmost;
    private int? _activeMonitorIndex;
    private bool _isBusy;
    private bool _isRefreshInFlight;
    private bool _areWindowsHidden;
    private DisplayMode _displayMode = DisplayMode.Standard;
    private double _collapsedWindowHeight;
    private double _collapsedWindowMinHeight;
    private double _standardWindowWidth;
    private double _standardWindowHeight;
    private double _standardWindowMinWidth;
    private double _standardWindowMinHeight;
    private WindowSlot? _pendingSlotInfoClear;
    private StoredPanelSlot? _pendingStoredPanelDeletion;
    private WindowSlot? _hiddenFocusedSlot;
    private Point _dragStartPoint;
    private bool _isCardDragDropInProgress;
    private CancellationTokenSource? _panelFrontRestoreCancellation;
    private CancellationTokenSource? _focusedSlotReassertCancellation;
    private CancellationTokenSource? _panelLocateCancellation;
    private CancellationTokenSource? _postLaunchArrangeCancellation;
    private bool _isReassertingFocusedSlot;
    private bool _isWindowMoveOrResizeActive;
    private DateTimeOffset _suppressFocusedSlotReassertUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _suppressSlotFocusFromDragUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _suppressPeriodicRefreshUntil = DateTimeOffset.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        var config = AppConfig.Load();
        _statusStore = new StatusStore(config);
        _vscodeLauncher = new VscodeLauncher(_windowEnumerator);
        _applicationLauncher = new ApplicationLauncher(_windowEnumerator, _vscodeLauncher);
        DataContext = _statusStore;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(config.StatusRefreshIntervalMilliseconds)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
        Activated += MainWindow_Activated;
        StateChanged += MainWindow_StateChanged;
        SizeChanged += MainWindow_SizeChanged;
        LocationChanged += MainWindow_LocationChanged;

        Loaded += async (_, _) =>
        {
            Topmost = true;
            _collapsedWindowHeight = Height;
            _collapsedWindowMinHeight = MinHeight;
            RememberStandardWindowMetrics();
            UpdateDisplayModeChrome();
            await RefreshSlotsAsync(allowDuringBusy: true);
            ApplyManagedWindowLayers();
            UpdateWindowHeightForStoredPanels(StoredPanelsExpander.IsExpanded, true);
            UpdateCompactPanelFrame();
            RefreshAuxiliaryUi();
            RefreshLaunchButtonAvailability();
        };
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        await LaunchAllMissingAsync();
    }

    private async void AuxiliaryApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { Tag: LauncherApplication application })
        {
            return;
        }

        await OpenAuxiliaryApplicationAsync(application);
    }

    private async Task OpenAuxiliaryApplicationAsync(LauncherApplication application)
    {
        if (!application.IsAvailable)
        {
            _statusStore.Message = application.ToolTip;
            return;
        }

        await RunBusyAsync(async () =>
        {
            _statusStore.Message = $"{application.DisplayName} を起動しています...";
            var result = await _applicationLauncher.LaunchSingleWindowApplicationAsync(
                application,
                _statusStore.Config,
                CancellationToken.None);
            _statusStore.Message = result.Message;
        });
    }

    private async Task LaunchAllMissingAsync()
    {
        if (_isBusy)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            // 非表示中にLaunchが押された場合は非表示状態を解除
            if (_areWindowsHidden)
            {
                _areWindowsHidden = false;
                _hiddenFocusedSlot = null;
                UpdateVisibilityButtonVisual();
                foreach (var slot in _statusStore.Slots)
                {
                    slot.IsHidden = false;
                }
            }

            _statusStore.LoadSavedSettings();
            await RefreshSlotsAsync(allowDuringBusy: true);

            var missingSlots = _statusStore.Slots
                .Where(slot => slot.WindowStatus == SlotWindowStatus.Missing)
                .ToList();
            if (missingSlots.Count == 0)
            {
                var arrangedOnly = ArrangeSlotsOnActiveMonitor();
                _statusStore.ClearFocusedSlot();
                _statusStore.Message = arrangedOnly > 0
                    ? $"{arrangedOnly}個の管理中ウィンドウを2x2に配置しました。"
                    : "一括起動の対象スロットがありません。";
                return;
            }

            var launchTargets = new List<(WindowSlot Slot, LauncherApplication Application)>();
            var unavailableSlots = new List<string>();
            foreach (var slot in missingSlots)
            {
                var application = _statusStore.FindApplication(slot.ApplicationId);
                if (application is null
                    || !application.IsWorkspaceApplication
                    || !_applicationLauncher.CanLaunchWorkspaceApplication(application, _statusStore.Config))
                {
                    unavailableSlots.Add($"{slot.Name}:{application?.DisplayName ?? slot.ApplicationId}");
                    continue;
                }

                if (!slot.HasPanelContent)
                {
                    slot.PanelTitle = _statusStore.MakeUniqueTitle($"スロット{slot.Name}", slot);
                }

                launchTargets.Add((slot, application));
            }

            if (launchTargets.Count == 0)
            {
                _statusStore.Message = unavailableSlots.Count > 0
                    ? $"選択アプリが未検出のため起動できません: {string.Join(", ", unavailableSlots)}"
                    : "一括起動できるスロットがありません。";
                return;
            }

            _statusStore.Message = $"未起動スロットを選択アプリで起動しています... {BuildLaunchPlanSummary(launchTargets)}";
            var allAssignments = new List<WindowAssignment>();
            var config = _statusStore.Config;
            var launchGroups = launchTargets
                .GroupBy(item => item.Application.Id)
                .Select(group =>
                {
                    var application = group.First().Application;
                    var slots = group.Select(item => item.Slot).ToList();
                    return (Application: application, Slots: slots);
                })
                .ToList();
            var nonCliTasks = launchGroups
                .Where(group => !group.Application.IsWorkspaceCli)
                .Select(group => _applicationLauncher.LaunchMissingAsync(group.Slots, config, group.Application, CancellationToken.None))
                .ToList();
            var cliTask = LaunchCliGroupsSequentiallyAsync(
                launchGroups.Where(group => group.Application.IsWorkspaceCli).ToList(),
                config);
            var groupTasks = nonCliTasks.Append(cliTask);
            var groupResults = await Task.WhenAll(groupTasks);
            foreach (var assignments in groupResults)
            {
                allAssignments.AddRange(assignments);
            }

            foreach (var assignment in allAssignments)
            {
                _statusStore.AssignWindow(assignment.Slot, assignment.Window);
            }

            await RefreshSlotsAsync(allowDuringBusy: true);

            var skippedMessage = unavailableSlots.Count > 0
                ? $" 未検出でスキップ: {string.Join(", ", unavailableSlots)}"
                : string.Empty;
            if (allAssignments.Count > 0)
            {
                await ArrangeSlotsOnActiveMonitorWithSettlingAsync();
                _statusStore.ClearFocusedSlot();
                _statusStore.Message = $"{allAssignments.Count}個のスロットを選択アプリで起動して2x2に配置しました。{skippedMessage}";
            }
            else
            {
                var arranged = ArrangeSlotsOnActiveMonitor();
                _statusStore.ClearFocusedSlot();
                _statusStore.Message = arranged > 0
                    ? $"{arranged}個の管理中ウィンドウを2x2に配置しました。{skippedMessage}"
                    : $"新しい管理中ウィンドウは見つかりませんでした。{skippedMessage}";
            }
        });
    }

    private async Task<IReadOnlyList<WindowAssignment>> LaunchCliGroupsSequentiallyAsync(
        IReadOnlyList<(LauncherApplication Application, List<WindowSlot> Slots)> groups,
        AppConfig config)
    {
        var assignments = new List<WindowAssignment>();
        foreach (var group in groups)
        {
            var groupAssignments = await _applicationLauncher.LaunchMissingAsync(
                group.Slots,
                config,
                group.Application,
                CancellationToken.None);
            assignments.AddRange(groupAssignments);
        }

        return assignments;
    }

    private static string BuildLaunchPlanSummary(IEnumerable<(WindowSlot Slot, LauncherApplication Application)> launchTargets)
    {
        return string.Join(
            " / ",
            launchTargets
                .GroupBy(item => item.Application.DisplayName)
                .Select(group => $"{group.Key} {group.Count()}個"));
    }

    private static string QuoteExplorerArgument(string path)
    {
        return $"\"{path.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private bool EnsureWorkspaceApplicationAvailable(LauncherApplication? application)
    {
        if (application is not null && _applicationLauncher.CanLaunchWorkspaceApplication(application, _statusStore.Config))
        {
            return true;
        }

        var appName = application?.DisplayName ?? "アプリケーション";
        var message = application?.ToolTip
            ?? $"{appName} が見つかりません。設定で実行ファイルまたはコマンドを指定してください。";
        _statusStore.Message = message;
        MessageBox.Show(
            this,
            message,
            $"{appName} が見つかりません",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        CancelScheduledPanelFrontRestore();
        CancelScheduledFocusedSlotReassert();
        CancelScheduledPostLaunchArrange();
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CancelScheduledPanelFrontRestore();
        CancelScheduledFocusedSlotReassert();
        CancelScheduledPostLaunchArrange();
        Close();
    }

    public void ExecuteExternalCommand(string[]? args)
    {
        _ = ExecuteExternalCommandAsync(args ?? []);
    }

    private async Task ExecuteExternalCommandAsync(string[] args)
    {
        ActivatePanelWindow();
        await RefreshSlotsAsync(allowDuringBusy: true);

        if (args.Length == 0)
        {
            RefreshAuxiliaryUi();
            return;
        }

        if (!string.Equals(args[0], "--activate", StringComparison.OrdinalIgnoreCase))
        {
            SuppressFocusedSlotReassertForPanelInput();
        }

        switch (args[0].ToLowerInvariant())
        {
            case "--activate":
                break;

            case "--locate":
                await LocatePanelAsync();
                break;

            case "--slot-toggle" when args.Length >= 2:
            {
                var slot = _statusStore.FindSlot(args[1]);
                if (slot is not null)
                {
                    HandleCompactSlotToggle(slot);
                }

                break;
            }

            case "--mode" when args.Length >= 2:
            {
                var targetMode = args[1].ToLowerInvariant() switch
                {
                    "compact" => DisplayMode.Compact,
                    "micro" => DisplayMode.Micro,
                    _ => DisplayMode.Standard
                };
                SetDisplayMode(targetMode);
                break;
            }

            case "--launch-all":
                await LaunchAllMissingAsync();
                break;

            case "--launch-app" when args.Length >= 2:
            {
                var application = _statusStore.FindApplication(args[1]);
                if (application is not null)
                {
                    await OpenAuxiliaryApplicationAsync(application);
                }

                break;
            }

            case "--layer" when args.Length >= 2 && string.Equals(args[1], "top", StringComparison.OrdinalIgnoreCase):
                PinAllTopButton_Click(this, new RoutedEventArgs());
                break;

            case "--layer" when args.Length >= 2 && string.Equals(args[1], "back", StringComparison.OrdinalIgnoreCase):
                SendAllBackButton_Click(this, new RoutedEventArgs());
                break;
        }

        RefreshAuxiliaryUi();
    }

    private void DisplayModeButton_Click(object sender, RoutedEventArgs e)
    {
        SuppressFocusedSlotReassertForPanelInput();
        SetDisplayMode(GetNextDisplayMode(_displayMode));
        ActivatePanelWindow();
    }

    private void StandardJumpButton_Click(object sender, RoutedEventArgs e)
    {
        SuppressFocusedSlotReassertForPanelInput();
        SetDisplayMode(DisplayMode.Standard);
        ActivatePanelWindow();
    }

    private static DisplayMode GetNextDisplayMode(DisplayMode current)
    {
        return current switch
        {
            DisplayMode.Standard => DisplayMode.Compact,
            DisplayMode.Compact => DisplayMode.Micro,
            DisplayMode.Micro => DisplayMode.Standard,
            _ => DisplayMode.Standard
        };
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        HelpOverlay.Visibility = Visibility.Visible;
        CloseHelpDialogButton.Focus();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _statusStore.ResetApplicationPathSettings();
        SettingsConfigPathText.Text = AppConfig.GetUserConfigPath();
        SettingsOverlay.Visibility = Visibility.Visible;
        CloseSettingsDialogButton.Focus();
    }

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        HideSettingsDialog();
    }

    private void SaveApplicationPathSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _statusStore.SaveApplicationPathSettings();
        SettingsConfigPathText.Text = AppConfig.GetUserConfigPath();
        _statusStore.Message = $"アプリケーションパス設定を保存しました: {AppConfig.GetUserConfigPath()}";
        RefreshAuxiliaryUi();
    }

    private void ReloadApplicationDetectionButton_Click(object sender, RoutedEventArgs e)
    {
        _statusStore.ReloadApplicationsFromConfig();
        _statusStore.Message = "アプリケーションを再検出しました。";
        RefreshAuxiliaryUi();
    }

    private void RepairPanelStateButton_Click(object sender, RoutedEventArgs e)
    {
        _statusStore.Message = _statusStore.RepairPanelState();
        RefreshAuxiliaryUi();
    }

    private void CloseHelpButton_Click(object sender, RoutedEventArgs e)
    {
        HideHelpDialog();
    }

    private void HideHelpDialog()
    {
        HelpOverlay.Visibility = Visibility.Collapsed;
    }

    private void HideSettingsDialog()
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void CompactSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        HandleCompactSlotToggle(slot);
    }

    private void MicroSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        HandleCompactSlotToggle(slot);
    }

    private void MicroVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleVisibilityButton_Click(sender, e);
    }

    private void CompactVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleVisibilityButton_Click(sender, e);
    }

    private void HandleCompactSlotToggle(WindowSlot slot)
    {
        ActivatePanelWindow();

        if (slot.IsFocused)
        {
            ToggleSlotFocus(slot);
            return;
        }

        ToggleSlotFocus(slot);
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _statusStore.SaveCurrentSettings();
        _statusStore.Message = "設定を保存しました。";
    }

    private async void LoadSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _statusStore.LoadSavedSettings();
        await RefreshSlotsAsync(allowDuringBusy: true);
        _statusStore.Message = "設定を読み込みました。";
    }

    private void CloseAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        SuppressFocusedSlotReassertForPanelInput();

        var closeTargets = _statusStore.Slots.Count(slot => slot.WindowHandle != IntPtr.Zero);
        if (closeTargets == 0)
        {
            _statusStore.Message = "閉じる管理中ウィンドウがありません。";
            RefreshAuxiliaryUi();
            return;
        }

        CloseAllConfirmCountText.Text = $"{closeTargets}個の管理中ウィンドウを閉じます。";
        CloseAllConfirmOverlay.Visibility = Visibility.Visible;
        CancelCloseAllButton.Focus();
    }

    private void ConfirmCloseAllButton_Click(object sender, RoutedEventArgs e)
    {
        HideCloseAllConfirmDialog();
        CloseAllManagedWindows();
    }

    private void CancelCloseAllButton_Click(object sender, RoutedEventArgs e)
    {
        HideCloseAllConfirmDialog();
    }

    private void CloseAllManagedWindows()
    {
        var closedSlots = new List<WindowSlot>();
        var closed = 0;
        foreach (var slot in _statusStore.Slots)
        {
            if (!_windowArranger.Close(slot.WindowHandle))
            {
                continue;
            }

            closedSlots.Add(slot);
            closed++;
        }

        if (closedSlots.Count > 0)
        {
            _statusStore.SaveCurrentSettings();
            foreach (var slot in closedSlots)
            {
                slot.IsHidden = false;
                _statusStore.ClearWindow(slot);
            }
        }

        _areWindowsHidden = _statusStore.Slots.Any(slot => slot.WindowHandle != IntPtr.Zero && slot.IsHidden);
        if (!_areWindowsHidden)
        {
            _hiddenFocusedSlot = null;
        }

        UpdateVisibilityButtonVisual();

        _statusStore.Message = closed == 0
            ? "閉じる管理中ウィンドウがありません。"
            : $"{closed}個の管理中ウィンドウを閉じて設定を保存しました。";
        RefreshAuxiliaryUi();
    }

    private void SlotCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        if (IsSlotFocusSuppressedByDrag())
        {
            e.Handled = true;
            return;
        }

        if (IsInteractiveCardChild(e.OriginalSource as DependencyObject))
        {
            return;
        }

        ToggleSlotFocus(slot);
    }

    private void SlotCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void SlotCard_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || IsSlotFocusSuppressedByDrag())
        {
            return;
        }

        if (IsInteractiveCardChild(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) <= SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(diff.Y) <= SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        var dragData = new DataObject("WindowSlot", slot);
        BeginCardDragDrop();
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, dragData, DragDropEffects.Move);
        }
        finally
        {
            EndCardDragDrop();
        }

        e.Handled = true;
    }

    private void SlotCard_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is not Border border || !e.Data.GetDataPresent("WindowSlot"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var sourceSlot = e.Data.GetData("WindowSlot") as WindowSlot;
        var targetSlot = border.Tag as WindowSlot;
        if (sourceSlot is not null && targetSlot is not null && !ReferenceEquals(sourceSlot, targetSlot))
        {
            border.BorderBrush = (SolidColorBrush)FindResource("AccentBrush");
        }
    }

    private void SlotCard_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.ClearValue(Border.BorderBrushProperty);
        }
    }

    private void SlotCard_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("WindowSlot") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void SlotCard_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.ClearValue(Border.BorderBrushProperty);
        }

        if (!e.Data.GetDataPresent("WindowSlot"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var sourceSlot = e.Data.GetData("WindowSlot") as WindowSlot;
        var targetSlot = (sender as FrameworkElement)?.Tag as WindowSlot;

        if (sourceSlot is null || targetSlot is null || ReferenceEquals(sourceSlot, targetSlot))
        {
            return;
        }

        _statusStore.SwapSlotContents(sourceSlot, targetSlot);
        if (!_areWindowsHidden)
        {
            ArrangeSlotsAfterPanelStateChange();
        }

        _statusStore.Message = $"スロット{sourceSlot.Name}とスロット{targetSlot.Name}のカードを入れ替えました。";
        RefreshAuxiliaryUi();
        e.Handled = true;
    }

    private void StoredPanelCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void StoredPanelCard_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || IsSlotFocusSuppressedByDrag())
        {
            return;
        }

        if (IsInteractiveCardChild(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) <= SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(diff.Y) <= SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (sender is not FrameworkElement { Tag: StoredPanelSlot storedPanel } || !storedPanel.HasContent)
        {
            return;
        }

        var dragData = new DataObject("StoredPanelSlot", storedPanel);
        BeginCardDragDrop();
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, dragData, DragDropEffects.Move);
        }
        finally
        {
            EndCardDragDrop();
        }

        e.Handled = true;
    }

    private void BeginCardDragDrop()
    {
        _isCardDragDropInProgress = true;
        _suppressSlotFocusFromDragUntil = DateTimeOffset.UtcNow + DragDropFocusSuppressWindow;
        SuppressFocusedSlotReassertForPanelInput();
    }

    private void EndCardDragDrop()
    {
        _isCardDragDropInProgress = false;
        _suppressSlotFocusFromDragUntil = DateTimeOffset.UtcNow + DragDropFocusSuppressWindow;
    }

    private bool IsSlotFocusSuppressedByDrag()
    {
        return _isCardDragDropInProgress
            || DateTimeOffset.UtcNow < _suppressSlotFocusFromDragUntil;
    }

    private void StoredPanelCard_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is not Border border || !e.Data.GetDataPresent("StoredPanelSlot"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var sourcePanel = e.Data.GetData("StoredPanelSlot") as StoredPanelSlot;
        var targetPanel = border.Tag as StoredPanelSlot;
        if (sourcePanel is not null && targetPanel is not null && !ReferenceEquals(sourcePanel, targetPanel))
        {
            border.BorderBrush = (SolidColorBrush)FindResource("AccentBrush");
        }
    }

    private void StoredPanelCard_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.ClearValue(Border.BorderBrushProperty);
        }
    }

    private void StoredPanelCard_DragOver(object sender, DragEventArgs e)
    {
        var sourcePanel = e.Data.GetData("StoredPanelSlot") as StoredPanelSlot;
        var targetPanel = (sender as FrameworkElement)?.Tag as StoredPanelSlot;
        e.Effects = sourcePanel is not null
            && targetPanel is not null
            && !ReferenceEquals(sourcePanel, targetPanel)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void StoredPanelCard_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.ClearValue(Border.BorderBrushProperty);
        }

        var sourcePanel = e.Data.GetData("StoredPanelSlot") as StoredPanelSlot;
        var targetPanel = (sender as FrameworkElement)?.Tag as StoredPanelSlot;
        if (sourcePanel is null || targetPanel is null || ReferenceEquals(sourcePanel, targetPanel))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var sourceLabel = sourcePanel.Label;
        var targetLabel = targetPanel.Label;
        var targetHadContent = targetPanel.HasContent;
        _statusStore.SwapStoredPanels(sourcePanel, targetPanel);
        _statusStore.Message = targetHadContent
            ? $"{sourceLabel}と{targetLabel}の控えカードを入れ替えました。"
            : $"{sourceLabel}を{targetLabel}へ移動しました。";
        RefreshAuxiliaryUi();
        e.Handled = true;
    }

    private void StoredPanelTab_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement element || !e.Data.GetDataPresent("StoredPanelSlot"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var sourcePanel = e.Data.GetData("StoredPanelSlot") as StoredPanelSlot;
        var targetPage = element.DataContext as StoredPanelPage;
        if (sourcePanel is null || targetPage is null || targetPage.Slots.Contains(sourcePanel))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        _statusStore.SelectStoredPanelPage(targetPage);
        if (sender is Control control)
        {
            control.BorderBrush = (SolidColorBrush)FindResource("AccentBrush");
            control.Background = (SolidColorBrush)FindResource("AccentDarkBrush");
        }

        e.Handled = true;
    }

    private void StoredPanelTab_DragLeave(object sender, DragEventArgs e)
    {
        ClearStoredPanelTabDragHighlight(sender);
    }

    private void StoredPanelTab_DragOver(object sender, DragEventArgs e)
    {
        var sourcePanel = e.Data.GetData("StoredPanelSlot") as StoredPanelSlot;
        var targetPage = (sender as FrameworkElement)?.DataContext as StoredPanelPage;
        e.Effects = sourcePanel is not null
            && targetPage is not null
            && !targetPage.Slots.Contains(sourcePanel)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void StoredPanelTab_Drop(object sender, DragEventArgs e)
    {
        ClearStoredPanelTabDragHighlight(sender);

        var sourcePanel = e.Data.GetData("StoredPanelSlot") as StoredPanelSlot;
        var targetPage = (sender as FrameworkElement)?.DataContext as StoredPanelPage;
        if (sourcePanel is null || targetPage is null)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        _statusStore.SelectStoredPanelPage(targetPage);
        var sourceLabel = sourcePanel.Label;
        if (_statusStore.TryMoveStoredPanelToPage(sourcePanel, targetPage, out var targetPanel, out var swapped)
            && targetPanel is not null)
        {
            _statusStore.Message = swapped
                ? $"{sourceLabel}を{targetPage.Header}へ移動し、{targetPanel.Label}と入れ替えました。"
                : $"{sourceLabel}を{targetPage.Header}の{targetPanel.Label}へ移動しました。";
            RefreshAuxiliaryUi();
        }

        e.Handled = true;
    }

    private void StoredPanelPageTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StoredPanelPage page)
        {
            _statusStore.SelectStoredPanelPage(page);
        }
    }

    private static void ClearStoredPanelTabDragHighlight(object sender)
    {
        if (sender is not Control control)
        {
            return;
        }

        control.ClearValue(Control.BackgroundProperty);
        control.ClearValue(Control.BorderBrushProperty);
    }

    private async void ToggleSlotFocus(WindowSlot slot)
    {
        var previouslyFocusedSlot = _statusStore.Slots.FirstOrDefault(item => item.IsFocused);

        if (slot.IsFocused)
        {
            if (!_areWindowsHidden)
            {
                CapturePreferredLayout(slot);
                var arranged = ArrangeSlotsOnActiveMonitor(false);
                _statusStore.ClearFocusedSlot();
                SchedulePanelToFront();
                _statusStore.Message = arranged == 0
                    ? "4分割表示に戻せる管理中ウィンドウがありません。"
                    : $"{arranged}個の管理中ウィンドウを4分割表示に戻しました。";
            }
            else
            {
                _statusStore.ClearFocusedSlot();
                _statusStore.Message = "フォーカスを解除しました。";
            }

            RefreshAuxiliaryUi();
            return;
        }

        if (_areWindowsHidden)
        {
            foreach (var s in _statusStore.Slots)
            {
                _windowArranger.Restore(s.WindowHandle);
            }

            RestoreHiddenWindowState();
        }

        if (previouslyFocusedSlot is not null)
        {
            CapturePreferredLayout(previouslyFocusedSlot);
        }

        EnsurePreferredLayout(slot);
        VscodeLayoutState.TryApplyPreferredLayout(slot, _statusStore.Config, slot.PreferredLayout);
        SetManagedWindowLayerState(WindowSlot.SlotWindowLayerMode.Topmost);
        if (_windowArranger.FocusMaximized(slot.WindowHandle))
        {
            // 新フォーカスを最大化する間、前フォーカスは最大化状態のまま「後ろで」覆っているので
            // 白いフラッシュは出ない。整列(他スロットの ShowWindow(SW_RESTORE) アニメ)を
            // 同時にやると panel もアニメ衝突に巻き込まれて沈むため、新フォーカスのアニメが
            // 落ち着いてから整列を遅延実行する。アニメは 1 つだけになり panel の沈下も最小化される。
            _windowArranger.BringToFrontOnce(slot.WindowHandle);
            SendOtherSlotsToBack(slot);
            _statusStore.SetFocusedSlot(slot);
            BringPanelToFrontImmediate();
            SchedulePanelToFront();
            _statusStore.Message = $"スロット{slot.Name}をフォーカス表示しました。";
            RefreshAuxiliaryUi();

            if (previouslyFocusedSlot is not null && !ReferenceEquals(previouslyFocusedSlot, slot))
            {
                // C の最大化アニメ完了後、覆われた状態で B を quadrant に静かに戻す。
                await Task.Delay(280);
                if (slot.WindowHandle != IntPtr.Zero)
                {
                    ArrangeSlotsExceptOnActiveMonitor(slot, false);
                    BringPanelToFrontImmediate();
                }
            }
            else
            {
                ArrangeSlotsExceptOnActiveMonitor(slot, false);
            }
            return;
        }

        _statusStore.Message = $"スロット{slot.Name}の{slot.ApplicationDisplayName}ウィンドウが見つかりません。";
        RefreshAuxiliaryUi();
    }

    private void PinAllTopButton_Click(object sender, RoutedEventArgs e)
    {
        SuppressFocusedSlotReassertForPanelInput();
        if (_areWindowsHidden)
        {
            SetManagedWindowLayerState(WindowSlot.SlotWindowLayerMode.Topmost);
            _statusStore.Message = "最前面に設定しました（表示時に反映されます）。";
            return;
        }

        // フォーカスモード中: フォーカスを維持したまま全スロットのレイヤーだけ変更。
        // FocusMaximized (SetForegroundWindow) は呼ばない。呼ぶとパネルと管理中ウィンドウの
        // アクティベーション争奪で無限ループしてハングする。
        ApplyLayerPreservingFocusMode(WindowSlot.SlotWindowLayerMode.Topmost);
        _statusStore.Message = "管理中ウィンドウを最前面にしました。";
    }

    private void SendAllBackButton_Click(object sender, RoutedEventArgs e)
    {
        SuppressFocusedSlotReassertForPanelInput();
        if (_areWindowsHidden)
        {
            SetManagedWindowLayerState(WindowSlot.SlotWindowLayerMode.Backmost);
            _statusStore.Message = "最背面に設定しました（表示時に反映されます）。";
            return;
        }

        // フォーカスモード中: フォーカスを維持したまま全スロットのレイヤーだけ変更。
        ApplyLayerPreservingFocusMode(WindowSlot.SlotWindowLayerMode.Backmost);
        _statusStore.Message = "管理中ウィンドウを最背面にしました。";
    }

    private void ExitFocusedModeForGlobalLayerChange()
    {
        if (!_statusStore.Slots.Any(slot => slot.IsFocused))
        {
            return;
        }

        CaptureFocusedLayout();
        _statusStore.ClearFocusedSlot();
        ArrangeSlotsOnActiveMonitor(false);
    }

    /// <summary>
    /// フォーカスモード中でも安全にレイヤーを変更する。
    /// フォーカス中は対象スロットを最後に前面化し、非フォーカススロットを背面に保つ。
    /// FocusMaximized (SetForegroundWindow) を呼ばないため、パネルと管理中ウィンドウ間の
    /// アクティベーション争奪による無限ループが発生しない。
    /// 4面表示・1面フォーカス表示のどちらでも動作する。
    /// </summary>
    private void ApplyLayerPreservingFocusMode(WindowSlot.SlotWindowLayerMode layerMode)
    {
        SetManagedWindowLayerState(layerMode);

        var focusedSlot = _statusStore.Slots.FirstOrDefault(slot => slot.IsFocused && slot.WindowHandle != IntPtr.Zero);
        if (focusedSlot is not null)
        {
            ApplyLayerPreservingFocusedSlotOrder(focusedSlot, layerMode);
            BringPanelToFrontImmediate();
            return;
        }

        foreach (var slot in _statusStore.Slots)
        {
            ApplyLayerToSlot(slot, layerMode, false);
        }

        BringPanelToFrontImmediate();
    }

    private void ApplyLayerPreservingFocusedSlotOrder(WindowSlot focusedSlot, WindowSlot.SlotWindowLayerMode layerMode)
    {
        switch (layerMode)
        {
            case WindowSlot.SlotWindowLayerMode.Topmost:
                SendOtherSlotsToBack(focusedSlot);
                _windowArranger.BringToFrontOnce(focusedSlot.WindowHandle);
                break;

            case WindowSlot.SlotWindowLayerMode.Backmost:
                _windowArranger.SetBackmost(focusedSlot.WindowHandle);
                SendOtherSlotsToBack(focusedSlot);
                break;
        }
    }

    private void ToggleVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        SuppressFocusedSlotReassertForPanelInput();
        if (_areWindowsHidden)
        {
            RestoreHiddenWindows();

            if (TryRestoreHiddenFocusedSlot(out var restoredFocusedSlot))
            {
                _statusStore.Message = $"スロット{restoredFocusedSlot.Name}をフォーカス表示に戻しました。";
                RefreshAuxiliaryUi();
                return;
            }

            var arranged = ArrangeSlotsOnActiveMonitor();
            _statusStore.Message = arranged > 0
                ? $"{arranged}個の管理中ウィンドウを表示しました。"
                : "表示できる管理中ウィンドウがありません。";
        }
        else
        {
            // フォーカスモード中の場合、先にフォーカスを解除してから最小化する。
            // ClearFocusedSlot を最小化の後に呼ぶと、最小化中にパネルがアクティブになり
            // ReassertFocusedSlotIfNeeded が走って FocusMaximized でウィンドウが復元され、無限ループになる。
            _hiddenFocusedSlot = _statusStore.Slots.FirstOrDefault(slot => slot.IsFocused && slot.WindowHandle != IntPtr.Zero);
            CaptureFocusedLayout();
            _statusStore.ClearFocusedSlot();

            var minimized = 0;
            foreach (var slot in _statusStore.Slots)
            {
                if (_windowArranger.Minimize(slot.WindowHandle))
                {
                    minimized++;
                    slot.IsHidden = true;
                }
            }

            if (minimized > 0)
            {
                _areWindowsHidden = true;
                UpdateVisibilityButtonVisual();
                _statusStore.Message = $"{minimized}個の管理中ウィンドウを非表示にしました。";
            }
            else
            {
                _hiddenFocusedSlot = null;
                _statusStore.Message = "非表示にできる管理中ウィンドウがありません。";
            }
        }

        RefreshAuxiliaryUi();
    }

    private void ToggleMonitorButton_Click(object sender, RoutedEventArgs e)
    {
        SuppressFocusedSlotReassertForPanelInput();
        var monitorCount = _windowArranger.GetMonitorCount();
        if (monitorCount <= 1)
        {
            _statusStore.Message = "利用可能なディスプレイが1枚のため切り替えできません。";
            return;
        }

        var nextMonitorIndex = (GetActiveMonitorIndex() + 1) % monitorCount;
        _activeMonitorIndex = nextMonitorIndex;

        if (_areWindowsHidden)
        {
            _statusStore.Message = $"配置先を{_windowArranger.GetMonitorLabel(nextMonitorIndex)}に切り替えました（表示時に反映されます）。";
            return;
        }

        // フォーカスモード中の場合、先にフォーカスを解除してからArrangeする。
        // ClearFocusedSlot を後に呼ぶと、Arrange 中にパネルがアクティブ化して
        // ReassertFocusedSlotIfNeeded が走り、無限ループでハングする。
        CaptureFocusedLayout();
        _statusStore.ClearFocusedSlot();
        var arranged = ArrangeSlotsOnActiveMonitor();
        _statusStore.Message = arranged > 0
            ? $"{arranged}個の管理中ウィンドウを{_windowArranger.GetMonitorLabel(nextMonitorIndex)}に移動しました。"
            : $"次回の配置先を{_windowArranger.GetMonitorLabel(nextMonitorIndex)}に切り替えました。";
    }

    private void CloseSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        if (_windowArranger.Close(slot.WindowHandle))
        {
            _statusStore.CaptureWorkspacePath(slot);
            _statusStore.ClearWindow(slot);
            _statusStore.Message = $"スロット{slot.Name}を閉じました。";
            RefreshAuxiliaryUi();
            return;
        }

        _statusStore.Message = $"スロット{slot.Name}の{slot.ApplicationDisplayName}ウィンドウが見つかりません。";
        RefreshAuxiliaryUi();
    }

    private void OpenSlotWorkspaceFolderButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_isBusy || sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        var explorerPath = slot.ExplorerWorkspacePath;
        if (string.IsNullOrWhiteSpace(explorerPath))
        {
            _statusStore.Message = slot.WorkspaceFolderToolTip;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = QuoteExplorerArgument(explorerPath),
                UseShellExecute = false
            });
            _statusStore.Message = $"スロット {slot.Name} のフォルダを開きました: {explorerPath}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DiagnosticLog.Write(ex);
            _statusStore.Message = $"フォルダを開けませんでした: {explorerPath}";
        }
    }

    private void ClearSlotPanelInfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { Tag: WindowSlot slot } || !slot.HasPanelContent)
        {
            return;
        }

        SuppressFocusedSlotReassertForPanelInput();
        _pendingSlotInfoClear = slot;
        ClearSlotInfoMessageText.Text = $"スロット{slot.Name}の保存情報を削除して空のパネルに戻します。";
        var detail = string.IsNullOrWhiteSpace(slot.PanelTitle)
            ? slot.ShortPath
            : slot.PanelTitle;
        ClearSlotInfoDetailText.Text = string.IsNullOrWhiteSpace(detail) || detail == "-"
            ? "この操作は取り消せません。"
            : detail;
        ClearSlotInfoPathText.Text = string.IsNullOrWhiteSpace(slot.Path)
            ? "保存済みパスはありません。"
            : slot.Path;

        ClearSlotInfoOverlay.Visibility = Visibility.Visible;
        CancelClearSlotInfoButton.Focus();
    }

    private async void ConfirmClearSlotInfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingSlotInfoClear is null)
        {
            HideClearSlotInfoDialog();
            return;
        }

        var slot = _pendingSlotInfoClear;
        HideClearSlotInfoDialog();
        await ClearSlotPanelInfoAsync(slot);
    }

    private void CancelClearSlotInfoButton_Click(object sender, RoutedEventArgs e)
    {
        HideClearSlotInfoDialog();
    }

    private async Task ClearSlotPanelInfoAsync(WindowSlot slot)
    {
        if (_isBusy)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
        var hadWindow = slot.WindowHandle != IntPtr.Zero;
        if (hadWindow)
        {
            var windowHandle = slot.WindowHandle;
            if (_windowEnumerator.IsLiveWindow(windowHandle))
            {
                _statusStore.ClearFocusedSlot();
                _windowArranger.ReleaseTopmost(windowHandle);
                if (!_windowArranger.Close(windowHandle))
                {
                    _statusStore.Message = $"スロット{slot.Name}のウィンドウを閉じられなかったため、パネル情報は削除していません。";
                    return;
                }

                for (var attempt = 0; attempt < 12 && _windowEnumerator.IsLiveWindow(windowHandle); attempt++)
                {
                    await Task.Delay(100);
                }
            }
        }

        var slotName = slot.Name;
        _statusStore.ClearFocusedSlot();
        _statusStore.ClearSlotPanelInfo(slot);
        _areWindowsHidden = _statusStore.Slots.Any(item => item.WindowHandle != IntPtr.Zero && item.IsHidden);
        if (!_areWindowsHidden)
        {
            _hiddenFocusedSlot = null;
            ArrangeSlotsAfterPanelStateChange();
        }

        _statusStore.Message = hadWindow
            ? $"スロット{slotName}のウィンドウを閉じ、パネル情報をクリアしました。"
            : $"スロット{slotName}のパネル情報をクリアしました。";
        RefreshAuxiliaryUi();
        });
    }

    private async void SlotMainActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        if (slot.WindowStatus == SlotWindowStatus.Ready)
        {
            // 停止: 既存の CloseSlotButton_Click と同じ
            CloseSlotButton_Click(sender, e);
            return;
        }

        if (slot.WindowStatus != SlotWindowStatus.Missing)
        {
            return;
        }

        var application = _statusStore.FindApplication(slot.ApplicationId);
        if (application is null || !application.IsWorkspaceApplication || !EnsureWorkspaceApplicationAvailable(application))
        {
            return;
        }

        // 新規: 空きスロットにデフォルト名を設定
        if (!slot.HasPanelContent)
        {
            var defaultTitle = $"スロット{slot.Name}";
            slot.PanelTitle = _statusStore.MakeUniqueTitle(defaultTitle, slot);
        }

        if (application is not null)
        {
            _statusStore.SetSlotApplication(slot, application);
        }

        // 起動 / 新規: 個別にワークスペースアプリを起動
        await LaunchSlotApplicationAsync(slot, application!);
    }

    private async Task LaunchSlotApplicationAsync(WindowSlot slot, LauncherApplication application)
    {
        await RunBusyAsync(async () =>
        {
            if (_areWindowsHidden)
            {
                _areWindowsHidden = false;
                _hiddenFocusedSlot = null;
                UpdateVisibilityButtonVisual();
                foreach (var s in _statusStore.Slots)
                {
                    s.IsHidden = false;
                }
            }

            _statusStore.Message = $"スロット{slot.Name}の{application.DisplayName}を起動しています...";
            var assignments = await _applicationLauncher.LaunchMissingAsync(
                new[] { slot },
                _statusStore.Config,
                application,
                CancellationToken.None);

            foreach (var assignment in assignments)
            {
                _statusStore.AssignWindow(assignment.Slot, assignment.Window);
            }

            await RefreshSlotsAsync(allowDuringBusy: true);
            await ArrangeSlotsOnActiveMonitorWithSettlingAsync();

            _statusStore.Message = assignments.Count > 0
                ? $"スロット{slot.Name}の{application.DisplayName}を起動しました。"
                : $"スロット{slot.Name}の{application.DisplayName}ウィンドウの起動を確認できませんでした。";
        });
    }

    private async void SlotApplicationSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (sender is not Button { Tag: SlotApplicationOption option })
        {
            return;
        }

        var slot = option.Slot;
        var application = _statusStore.FindApplication(option.ApplicationId);
        if (application is null || !application.IsWorkspaceApplication)
        {
            return;
        }

        if (!EnsureWorkspaceApplicationAvailable(application))
        {
            return;
        }

        if (slot.WindowStatus == SlotWindowStatus.Missing)
        {
            _statusStore.SetSlotApplication(slot, application);
            _statusStore.Message = $"スロット{slot.Name}の起動対象を {application.DisplayName} にしました。";
            RefreshLaunchButtonAvailability();
            RefreshAuxiliaryUi();
            return;
        }

        await SwitchSlotWorkspaceApplicationAsync(slot, application);
    }

    private async Task SwitchSlotWorkspaceApplicationAsync(WindowSlot slot, LauncherApplication application)
    {
        await RunBusyAsync(async () =>
        {
            var wasSameApplication = string.Equals(slot.ApplicationId, application.Id, StringComparison.OrdinalIgnoreCase);
            if (slot.WindowStatus == SlotWindowStatus.Ready && wasSameApplication)
            {
                ToggleSlotFocus(slot);
                return;
            }

            if (!await CloseSlotWindowForReplacementAsync(slot))
            {
                return;
            }

            if (slot.WindowHandle != IntPtr.Zero)
            {
                if (_statusStore.IsVsCodeSlot(slot))
                {
                    _statusStore.CaptureWorkspacePath(slot);
                }

                if (!_windowArranger.Close(slot.WindowHandle))
                {
                    _statusStore.Message = $"スロット{slot.Name}の現在のアプリを閉じられませんでした。";
                    return;
                }

                await Task.Delay(450);
                _statusStore.ClearWindow(slot);
            }

            if (!slot.HasPanelContent)
            {
                slot.PanelTitle = _statusStore.MakeUniqueTitle($"スロット{slot.Name}", slot);
            }

            _statusStore.SetSlotApplication(slot, application);
            _statusStore.Message = $"スロット{slot.Name}を{application.DisplayName}で開いています...";
            var assignments = await _applicationLauncher.LaunchMissingAsync(
                new[] { slot },
                _statusStore.Config,
                application,
                CancellationToken.None);

            foreach (var assignment in assignments)
            {
                _statusStore.AssignWindow(assignment.Slot, assignment.Window);
            }

            await RefreshSlotsAsync(allowDuringBusy: true);
            await ArrangeSlotsOnActiveMonitorWithSettlingAsync();

            _statusStore.Message = assignments.Count > 0
                ? $"スロット{slot.Name}を{application.DisplayName}で開きました。"
                : $"スロット{slot.Name}の{application.DisplayName}ウィンドウの起動を確認できませんでした。";
        });
    }

    private async Task<bool> CloseSlotWindowForReplacementAsync(WindowSlot slot)
    {
        var currentHandle = slot.WindowHandle;
        if (currentHandle == IntPtr.Zero)
        {
            return true;
        }

        if (_statusStore.IsVsCodeSlot(slot))
        {
            _statusStore.CaptureWorkspacePath(slot);
        }

        _statusStore.ClearFocusedSlot();
        _windowArranger.ReleaseTopmost(currentHandle);

        if (_windowEnumerator.IsLiveWindow(currentHandle) && !_windowArranger.Close(currentHandle))
        {
            _statusStore.Message = $"スロット{slot.Name}の現在のウィンドウを閉じられませんでした。";
            return false;
        }

        for (var attempt = 0; attempt < 12 && _windowEnumerator.IsLiveWindow(currentHandle); attempt++)
        {
            await Task.Delay(100);
        }

        _statusStore.ClearWindow(slot);
        _areWindowsHidden = _statusStore.Slots.Any(item => item.WindowHandle != IntPtr.Zero && item.IsHidden);
        if (!_areWindowsHidden)
        {
            _hiddenFocusedSlot = null;
        }

        return true;
    }

    private void StoreSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        if (slot.WindowHandle != IntPtr.Zero && !_windowArranger.Close(slot.WindowHandle))
        {
            _statusStore.Message = $"スロット{slot.Name}を控えに移す前に {slot.ApplicationDisplayName} を閉じられませんでした。";
            return;
        }

        _statusStore.CaptureWorkspacePath(slot);
        _statusStore.ClearFocusedSlot();
        if (!_statusStore.TryStoreSlotInBack(slot, out var storedPanel))
        {
            _statusStore.Message = _statusStore.StoredPanels.All(item => item.HasContent)
                ? "控え Quartet が満杯のため保存できません。"
                : $"スロット{slot.Name}に控え保存できるワークスペースがありません。";
            return;
        }

        if (!_areWindowsHidden)
        {
            ArrangeSlotsOnActiveMonitor();
        }

        _statusStore.Message = $"スロット{slot.Name}を{storedPanel!.Label}へ控え保存しました。";
        RefreshAuxiliaryUi();
    }

    private async void ShowStoredPanelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (sender is not Button { Tag: StoredPanelSlot storedPanel, CommandParameter: string targetSlotName })
        {
            return;
        }

        if (!storedPanel.HasContent)
        {
            _statusStore.Message = $"{storedPanel.Label} は空です。";
            return;
        }

        var targetSlot = _statusStore.FindSlot(targetSlotName);
        if (targetSlot is null)
        {
            return;
        }

        var application = _statusStore.FindApplication(storedPanel.ApplicationId);
        if (application is null || !application.IsWorkspaceApplication || !EnsureWorkspaceApplicationAvailable(application))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            if (targetSlot.WindowHandle != IntPtr.Zero && !_windowArranger.Close(targetSlot.WindowHandle))
            {
                _statusStore.Message = $"スロット{targetSlot.Name}の現在のアプリを閉じられないため入れ替えできません。";
                return;
            }

            _statusStore.CaptureWorkspacePath(targetSlot);
            _statusStore.ClearFocusedSlot();
            if (!_statusStore.TryShowStoredPanel(storedPanel, targetSlot, out var swappedVisiblePanel))
            {
                _statusStore.Message = $"{storedPanel.Label} をスロット{targetSlot.Name}へ表示できませんでした。";
                return;
            }

            if (application is not null)
            {
                _statusStore.SetSlotApplication(targetSlot, application);
            }

            var assignments = await _applicationLauncher.LaunchMissingAsync(
                new[] { targetSlot },
                _statusStore.Config,
                application!,
                CancellationToken.None);

            foreach (var assignment in assignments)
            {
                _statusStore.AssignWindow(assignment.Slot, assignment.Window);
            }

            await RefreshSlotsAsync(allowDuringBusy: true);

            if (_areWindowsHidden)
            {
                // 非表示中は起動したウィンドウも最小化して非表示を維持
                foreach (var assignment in assignments)
                {
                    _windowArranger.Minimize(assignment.Slot.WindowHandle);
                    assignment.Slot.IsHidden = true;
                }
            }
            else
            {
                // 起動直後にウィンドウ位置を自己復元するケースに備えて再配置する
                await ArrangeSlotsOnActiveMonitorWithSettlingAsync();
            }

            _statusStore.Message = assignments.Count > 0
                ? swappedVisiblePanel
                    ? $"{storedPanel.Label}をスロット{targetSlot.Name}へ表示し、元の内容は控えに戻しました。"
                    : $"{storedPanel.Label}をスロット{targetSlot.Name}へ表示しました。"
                : $"{storedPanel.Label}の設定をスロット{targetSlot.Name}へ移しましたが、{application!.DisplayName}ウィンドウの起動は確認できませんでした。";
        });
    }

    private void DeleteStoredPanelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (sender is not Button { Tag: StoredPanelSlot storedPanel } || !storedPanel.HasContent)
        {
            return;
        }

        _pendingStoredPanelDeletion = storedPanel;
        DeleteStoredPanelMessageText.Text = $"{storedPanel.Label} の保存内容を削除して空きスロットに戻します。";

        var detail = string.IsNullOrWhiteSpace(storedPanel.PanelTitle)
            ? storedPanel.ShortPath
            : storedPanel.PanelTitle;
        DeleteStoredPanelDetailText.Text = string.IsNullOrWhiteSpace(detail) || detail == "-"
            ? "この操作は取り消せません。"
            : detail;
        DeleteStoredPanelPathText.Text = string.IsNullOrWhiteSpace(storedPanel.WorkspacePath)
            ? "保存済みパスはありません。"
            : storedPanel.WorkspacePath;

        DeleteStoredPanelOverlay.Visibility = Visibility.Visible;
        CancelDeleteStoredPanelButton.Focus();
    }

    private void ConfirmDeleteStoredPanelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingStoredPanelDeletion is null)
        {
            HideDeleteStoredPanelDialog();
            return;
        }

        var label = _pendingStoredPanelDeletion.Label;
        _statusStore.ClearStoredPanel(_pendingStoredPanelDeletion);
        _statusStore.Message = $"{label} を空きスロットに戻しました。";
        HideDeleteStoredPanelDialog();
        RefreshAuxiliaryUi();
    }

    private void CancelDeleteStoredPanelButton_Click(object sender, RoutedEventArgs e)
    {
        HideDeleteStoredPanelDialog();
    }

    private async void SettingsClearVisibleSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { Tag: WindowSlot slot } || !slot.HasPanelContent)
        {
            return;
        }

        SuppressFocusedSlotReassertForPanelInput();
        await ClearSlotPanelInfoAsync(slot);
    }

    private void SettingsClearStoredPanelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { Tag: StoredPanelSlot storedPanel } || !storedPanel.HasContent)
        {
            return;
        }

        _statusStore.ClearStoredPanel(storedPanel);
        _statusStore.Message = $"{storedPanel.Label} を空きスロットに戻しました。";
        RefreshAuxiliaryUi();
    }

    private void StoredPanelsExpander_Expanded(object sender, RoutedEventArgs e)
    {
        UpdateWindowHeightForStoredPanels(true);
    }

    private void StoredPanelsExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        UpdateWindowHeightForStoredPanels(false);
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshSlotsAsync();
    }

    private async Task RefreshSlotsAsync(bool allowDuringBusy = false)
    {
        if (_refreshCancellation.IsCancellationRequested
            || _isRefreshInFlight
            || _isBusy && !allowDuringBusy)
        {
            return;
        }

        if (!allowDuringBusy && DateTimeOffset.UtcNow < _suppressPeriodicRefreshUntil)
        {
            return;
        }

        _isRefreshInFlight = true;
        try
        {
            await _statusStore.RefreshWindowStatusesAsync(_windowEnumerator, _refreshCancellation.Token);
            var reattachedCount = ReattachExistingVsCodeWindowsToMissingSlots();
            if (reattachedCount > 0
                && CanReapplyPostLaunchArrangement()
                && _windowArranger.NeedsArrange(_statusStore.Slots, _statusStore.Config.Gap, GetActiveMonitorIndex()))
            {
                await ArrangeSlotsOnActiveMonitorWithSettlingAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _isRefreshInFlight = false;
            RefreshAuxiliaryUi();
        }
    }

    private int ReattachExistingVsCodeWindowsToMissingSlots()
    {
        if (!_statusStore.Config.UseDedicatedUserDataDirs)
        {
            return 0;
        }

        var assignedHandles = _statusStore.Slots
            .Where(slot => slot.WindowHandle != IntPtr.Zero && _windowEnumerator.IsLiveWindow(slot.WindowHandle))
            .Select(slot => slot.WindowHandle)
            .ToHashSet();
        var reattachedCount = 0;

        foreach (var slot in _statusStore.Slots.Where(slot =>
                     slot.IsVsCodeApplication
                     && slot.WindowStatus == SlotWindowStatus.Missing))
        {
            var window = _vscodeLauncher.TryFindExistingSlotWindow(slot, _statusStore.Config);
            if (window is null || assignedHandles.Contains(window.Handle))
            {
                continue;
            }

            _statusStore.AssignWindow(slot, window);
            assignedHandles.Add(window.Handle);
            reattachedCount++;
        }

        if (reattachedCount > 0)
        {
            _statusStore.Message = $"{reattachedCount}個の既に開いていた VS Code ウィンドウをスロットへ再接続しました。";
        }

        return reattachedCount;
    }

    private int ArrangeSlotsOnActiveMonitor(bool bringPanelAfterArrange = true)
    {
        var arranged = _windowArranger.Arrange(_statusStore.Slots, _statusStore.Config.Gap, GetActiveMonitorIndex());
        ApplyManagedWindowLayers(bringPanelAfterArrange);
        RefreshAuxiliaryUi();
        return arranged;
    }

    private void ArrangeSlotsAfterPanelStateChange()
    {
        var focusedSlot = _statusStore.Slots.FirstOrDefault(slot => slot.IsFocused && slot.WindowHandle != IntPtr.Zero);
        if (focusedSlot is null)
        {
            ArrangeSlotsOnActiveMonitor();
            return;
        }

        _windowArranger.BringToFrontOnce(focusedSlot.WindowHandle);
        SendOtherSlotsToBack(focusedSlot);
        ArrangeSlotsExceptOnActiveMonitor(focusedSlot, false);
        SchedulePanelToFront();
        RefreshAuxiliaryUi();
    }

    private async Task<int> ArrangeSlotsOnActiveMonitorWithSettlingAsync(bool bringPanelAfterArrange = true)
    {
        var arranged = ArrangeSlotsOnActiveMonitor(bringPanelAfterArrange);
        foreach (var delay in new[] { TimeSpan.FromMilliseconds(350), TimeSpan.FromMilliseconds(900) })
        {
            await Task.Delay(delay);
            arranged = ArrangeSlotsOnActiveMonitor(bringPanelAfterArrange);
        }

        SchedulePostLaunchArrangeRetries(bringPanelAfterArrange);
        return arranged;
    }

    private void SchedulePostLaunchArrangeRetries(bool bringPanelAfterArrange)
    {
        CancelScheduledPostLaunchArrange();
        if (_areWindowsHidden || WindowState == WindowState.Minimized)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _postLaunchArrangeCancellation = cancellation;
        _ = ReapplyArrangementAfterLaunchAsync(bringPanelAfterArrange, cancellation.Token);
    }

    private void CancelScheduledPostLaunchArrange()
    {
        _postLaunchArrangeCancellation?.Cancel();
        _postLaunchArrangeCancellation?.Dispose();
        _postLaunchArrangeCancellation = null;
    }

    private async Task ReapplyArrangementAfterLaunchAsync(bool bringPanelAfterArrange, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var delay in PostLaunchArrangeRetryDelays)
            {
                await Task.Delay(delay, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (cancellationToken.IsCancellationRequested
                        || !CanReapplyPostLaunchArrangement()
                        || !_windowArranger.NeedsArrange(_statusStore.Slots, _statusStore.Config.Gap, GetActiveMonitorIndex()))
                    {
                        return;
                    }

                    ArrangeSlotsOnActiveMonitor(bringPanelAfterArrange);
                }, DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool CanReapplyPostLaunchArrangement()
    {
        return !_areWindowsHidden
            && WindowState != WindowState.Minimized
            && !_statusStore.Slots.Any(slot => slot.IsFocused);
    }

    private int ArrangeSlotsExceptOnActiveMonitor(WindowSlot excludedSlot, bool refreshAuxiliaryUiAfterArrange = true)
    {
        var arranged = _windowArranger.ArrangeExcept(_statusStore.Slots, excludedSlot, _statusStore.Config.Gap, GetActiveMonitorIndex());
        if (refreshAuxiliaryUiAfterArrange)
        {
            RefreshAuxiliaryUi();
        }

        return arranged;
    }

    private void ApplyManagedWindowLayers(bool bringPanelAfterChange = true)
    {
        SetManagedWindowLayer(_managedWindowLayerMode, bringPanelAfterChange);
    }

    private void SetManagedWindowLayerState(WindowSlot.SlotWindowLayerMode layerMode)
    {
        _managedWindowLayerMode = layerMode;
        foreach (var slot in _statusStore.Slots)
        {
            slot.WindowLayerMode = layerMode;
        }
    }

    private bool SetManagedWindowLayer(
        WindowSlot.SlotWindowLayerMode layerMode,
        bool bringPanelAfterChange = true)
    {
        SetManagedWindowLayerState(layerMode);
        var appliedAny = false;

        foreach (var slot in _statusStore.Slots)
        {
            appliedAny |= ApplyLayerToSlot(slot, layerMode, false);
        }

        if (bringPanelAfterChange)
        {
            SchedulePanelToFront();
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

        var applied = layerMode switch
        {
            WindowSlot.SlotWindowLayerMode.Topmost => _windowArranger.BringToFrontOnce(slot.WindowHandle),
            WindowSlot.SlotWindowLayerMode.Backmost => _windowArranger.SetBackmost(slot.WindowHandle),
            _ => false
        };

        if (bringPanelAfterChange)
        {
            SchedulePanelToFront();
        }

        return applied;
    }

    private void BringPanelToFront()
    {
        BringPanelToFrontImmediate();
    }

    private void ActivatePanelWindow()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        BringPanelToFront();
    }

    private void BringPanelToFrontImmediate()
    {
        if (WindowState == WindowState.Minimized)
        {
            return;
        }

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

    private void SetDisplayMode(DisplayMode mode, bool updateMessage = true)
    {
        if (_displayMode == mode)
        {
            UpdateDisplayModeChrome();
            UpdateCompactPanelFrame();
            RefreshAuxiliaryUi();
            return;
        }

        var previousMode = _displayMode;

        if (mode == DisplayMode.Standard)
        {
            StopPanelLocateEmphasis();
            ClearCompactPanelFrame();
            try
            {
                Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
            }
        }

        if (previousMode == DisplayMode.Standard)
        {
            RememberStandardWindowMetrics();
        }

        var preModeChangeBounds = GetCurrentWindowBounds();

        _displayMode = mode;

        var isStandard = mode == DisplayMode.Standard;
        var isCompact = mode == DisplayMode.Compact;
        var isMicro = mode == DisplayMode.Micro;

        StandardSlotsGrid.Visibility = isStandard ? Visibility.Visible : Visibility.Collapsed;
        CompactBarPanel.Visibility = isCompact ? Visibility.Visible : Visibility.Collapsed;
        MicroPanel.Visibility = isMicro ? Visibility.Visible : Visibility.Collapsed;
        StoredPanelsExpander.Visibility = isStandard ? Visibility.Visible : Visibility.Collapsed;
        FooterControlsGrid.Visibility = isStandard ? Visibility.Visible : Visibility.Collapsed;
        ApplicationLauncherPanel.Visibility = isStandard ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.Visibility = isStandard ? Visibility.Visible : Visibility.Collapsed;
        HelpButton.Visibility = isStandard ? Visibility.Visible : Visibility.Collapsed;
        StandardJumpButton.Visibility = isCompact ? Visibility.Visible : Visibility.Collapsed;
        WindowTitleText.Visibility = isMicro ? Visibility.Collapsed : Visibility.Visible;

        switch (mode)
        {
            case DisplayMode.Compact:
            {
                var baseWidth = _standardWindowWidth > 0 ? _standardWindowWidth : preModeChangeBounds.Width;
                var compactWidth = Math.Max(CompactWindowMinWidth, Math.Round(baseWidth * CompactWindowWidthScale));
                var compactHeight = GetCompactModeHeight(compactWidth);

                MinWidth = CompactWindowMinWidth;
                MinHeight = compactHeight;
                var targetBounds = GetCompactModeBounds(preModeChangeBounds, compactWidth, compactHeight);
                SetWindowBounds(targetBounds.Left, targetBounds.Top, targetBounds.Width, targetBounds.Height);
                break;
            }
            case DisplayMode.Micro:
            {
                MinWidth = MicroWindowSize;
                MinHeight = MicroWindowSize;
                var targetBounds = GetMicroModeCenteredBounds(MicroWindowSize, MicroWindowSize);
                SetWindowBounds(targetBounds.Left, targetBounds.Top, targetBounds.Width, targetBounds.Height);
                break;
            }
            case DisplayMode.Standard:
            default:
            {
                MinWidth = _standardWindowMinWidth > 0 ? _standardWindowMinWidth : MinWidth;
                MinHeight = _standardWindowMinHeight > 0 ? _standardWindowMinHeight : MinHeight;
                var targetWidth = _standardWindowWidth > 0
                    ? Math.Max(MinWidth, _standardWindowWidth)
                    : Width;
                var targetHeight = _standardWindowHeight > 0
                    ? Math.Max(MinHeight, _standardWindowHeight)
                    : Height;
                var targetBounds = GetStandardModeRestoreBounds(preModeChangeBounds, targetWidth, targetHeight);
                SetWindowBounds(targetBounds.Left, targetBounds.Top, targetBounds.Width, targetBounds.Height);
                break;
            }
        }

        UpdateDisplayModeChrome();
        UpdateCompactPanelFrame();
        RefreshAuxiliaryUi();

        if (updateMessage)
        {
            _statusStore.Message = mode switch
            {
                DisplayMode.Compact => "縮小表示に切り替えました。",
                DisplayMode.Micro => "極小表示に切り替えました。",
                _ => "標準表示に戻しました。"
            };
        }
    }

    private void UpdateDisplayModeChrome()
    {
        (DisplayModeButton.Content, DisplayModeButton.ToolTip) = _displayMode switch
        {
            DisplayMode.Standard => (CompactModeGlyph, "縮小表示へ切り替え"),
            DisplayMode.Compact => (MicroModeGlyph, "極小表示へ切り替え"),
            DisplayMode.Micro => (StandardModeGlyph, "標準表示へ戻す"),
            _ => (CompactModeGlyph, "縮小表示へ切り替え")
        };
    }

    private void RememberStandardWindowMetrics()
    {
        if (!IsStandardMode)
        {
            return;
        }

        var currentBounds = GetCurrentWindowBounds();
        _standardWindowWidth = currentBounds.Width;
        _standardWindowHeight = currentBounds.Height;
        _standardWindowMinWidth = MinWidth;
        _standardWindowMinHeight = MinHeight;
    }

    private WindowArranger.WindowBounds GetMicroModeCenteredBounds(double targetWidth, double targetHeight)
    {
        var panelHandle = new WindowInteropHelper(this).Handle;
        if (panelHandle == IntPtr.Zero
            || !_windowArranger.TryGetMonitorWorkAreaForWindow(panelHandle, out var workArea))
        {
            return new WindowArranger.WindowBounds(
                (int)Math.Round(Left + Width / 2 - targetWidth / 2),
                (int)Math.Round(Top + Height / 2 - targetHeight / 2),
                (int)Math.Round(targetWidth),
                (int)Math.Round(targetHeight));
        }

        var left = workArea.Left + (workArea.Width - targetWidth) / 2;
        var top = workArea.Top + (workArea.Height - targetHeight) / 2;
        return new WindowArranger.WindowBounds(
            (int)Math.Round(left),
            (int)Math.Round(top),
            (int)Math.Round(targetWidth),
            (int)Math.Round(targetHeight));
    }

    private double GetCompactModeHeight(double targetWindowWidth)
    {
        const double titleRowHeight = 26;
        const double edgePadding = 1;
        var compactContentWidth = Math.Max(
            0,
            targetWindowWidth
            - RootLayoutGrid.Margin.Left
            - RootLayoutGrid.Margin.Right
            - CompactBarPanel.Margin.Left
            - CompactBarPanel.Margin.Right);

        CompactBarPanel.Measure(new Size(compactContentWidth, double.PositiveInfinity));
        var compactPanelHeight = CompactBarPanel.DesiredSize.Height + CompactBarPanel.Margin.Top + CompactBarPanel.Margin.Bottom;
        var auxiliaryContentWidth = Math.Max(
            0,
            targetWindowWidth
            - RootLayoutGrid.Margin.Left
            - RootLayoutGrid.Margin.Right
            - StoredPanelsRowGrid.Margin.Left
            - StoredPanelsRowGrid.Margin.Right);
        StoredPanelsRowGrid.Measure(new Size(auxiliaryContentWidth, double.PositiveInfinity));
        var auxiliaryRowHeight = StoredPanelsRowGrid.DesiredSize.Height
            + StoredPanelsRowGrid.Margin.Top
            + StoredPanelsRowGrid.Margin.Bottom;
        var desiredHeight = titleRowHeight
            + compactPanelHeight
            + auxiliaryRowHeight
            + RootLayoutGrid.Margin.Top
            + RootLayoutGrid.Margin.Bottom
            + edgePadding;
        return Math.Max(CompactWindowMinHeight, Math.Ceiling(desiredHeight));
    }

    private WindowArranger.WindowBounds GetCurrentWindowBounds()
    {
        var panelHandle = new WindowInteropHelper(this).Handle;
        if (panelHandle != IntPtr.Zero && _windowArranger.TryGetWindowBounds(panelHandle, out var actualBounds))
        {
            return actualBounds;
        }

        return new WindowArranger.WindowBounds(
            (int)Math.Round(Left),
            (int)Math.Round(Top),
            (int)Math.Round(Width),
            (int)Math.Round(Height));
    }

    private WindowArranger.WindowBounds GetCompactModeBounds(WindowArranger.WindowBounds standardBounds, double targetWidth, double targetHeight)
    {
        var compactRight = standardBounds.Left + standardBounds.Width;
        var defaultBounds = new WindowArranger.WindowBounds(
            (int)Math.Round(compactRight - targetWidth),
            standardBounds.Top,
            (int)Math.Round(targetWidth),
            (int)Math.Round(targetHeight));

        var panelHandle = new WindowInteropHelper(this).Handle;
        if (panelHandle == IntPtr.Zero
            || !_windowArranger.TryGetMonitorWorkAreaForWindow(panelHandle, out var workArea))
        {
            return defaultBounds;
        }

        var adjustedLeft = ClampToWorkArea(defaultBounds.Left, workArea.Left, workArea.Width, targetWidth);
        var adjustedTop = ClampToWorkArea(defaultBounds.Top, workArea.Top, workArea.Height, targetHeight);
        return new WindowArranger.WindowBounds(
            (int)Math.Round(adjustedLeft),
            (int)Math.Round(adjustedTop),
            (int)Math.Round(targetWidth),
            (int)Math.Round(targetHeight));
    }

    private void SetWindowBounds(double left, double top, double width, double height)
    {
        var bounds = new WindowArranger.WindowBounds(
            (int)Math.Round(left),
            (int)Math.Round(top),
            (int)Math.Round(width),
            (int)Math.Round(height));
        var panelHandle = new WindowInteropHelper(this).Handle;
        if (panelHandle != IntPtr.Zero && _windowArranger.SetWindowBounds(panelHandle, bounds))
        {
            return;
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    private async Task LocatePanelAsync()
    {
        if (IsStandardMode)
        {
            return;
        }

        ActivatePanelWindow();
        StopPanelLocateEmphasis();

        var cancellation = new CancellationTokenSource();
        _panelLocateCancellation = cancellation;

        try
        {
            UpdateCompactPanelFrame(PanelFrameVisual.Emphasis);
            await Task.Delay(1400, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_panelLocateCancellation, cancellation))
            {
                _panelLocateCancellation = null;
            }

            cancellation.Dispose();
            UpdateCompactPanelFrame();
        }
    }

    private void StopPanelLocateEmphasis()
    {
        _panelLocateCancellation?.Cancel();
        _panelLocateCancellation?.Dispose();
        _panelLocateCancellation = null;
    }

    private void UpdateCompactPanelFrame(PanelFrameVisual visual = PanelFrameVisual.Normal)
    {
        if (IsStandardMode)
        {
            ClearCompactPanelFrame();
            return;
        }

        var color = visual switch
        {
            PanelFrameVisual.Emphasis => (Color)ColorConverter.ConvertFromString("#8AFCB7"),
            _ => (Color)ColorConverter.ConvertFromString("#45D483")
        };

        var borderOpacity = visual switch
        {
            PanelFrameVisual.Emphasis => 1.0,
            _ => 0.82
        };

        var borderThickness = visual == PanelFrameVisual.Emphasis ? 2.25 : 1.75;
        PanelFrameBorder.BorderBrush = new SolidColorBrush(color) { Opacity = borderOpacity };
        PanelFrameBorder.BorderThickness = new Thickness(borderThickness);
        PanelFrameBorder.Effect = null;
    }

    private void ClearCompactPanelFrame()
    {
        PanelFrameBorder.BorderBrush = Brushes.Transparent;
        PanelFrameBorder.BorderThickness = new Thickness(0);
        PanelFrameBorder.Effect = null;
    }

    private WindowArranger.WindowBounds GetStandardModeRestoreBounds(WindowArranger.WindowBounds compactBounds, double targetWidth, double targetHeight)
    {
        var compactRight = compactBounds.Left + compactBounds.Width;
        var defaultBounds = new WindowArranger.WindowBounds(
            (int)Math.Round(compactRight - targetWidth),
            compactBounds.Top,
            (int)Math.Round(targetWidth),
            (int)Math.Round(targetHeight));

        var panelHandle = new WindowInteropHelper(this).Handle;
        if (panelHandle == IntPtr.Zero
            || !_windowArranger.TryGetMonitorWorkAreaForWindow(panelHandle, out var workArea))
        {
            return defaultBounds;
        }

        var adjustedLeft = ClampToWorkArea(defaultBounds.Left, workArea.Left, workArea.Width, targetWidth);
        var adjustedTop = ClampToWorkArea(defaultBounds.Top, workArea.Top, workArea.Height, targetHeight);

        return new WindowArranger.WindowBounds(
            (int)Math.Round(adjustedLeft),
            (int)Math.Round(adjustedTop),
            (int)Math.Round(targetWidth),
            (int)Math.Round(targetHeight));
    }

    private static bool WouldClip(WindowArranger.WindowBounds workArea, WindowArranger.WindowBounds targetBounds)
    {
        return targetBounds.Left < workArea.Left
            || targetBounds.Top < workArea.Top
            || targetBounds.Left + targetBounds.Width > workArea.Left + workArea.Width
            || targetBounds.Top + targetBounds.Height > workArea.Top + workArea.Height;
    }

    private static double ClampToWorkArea(double value, int workAreaStart, int workAreaLength, double targetLength)
    {
        var min = workAreaStart;
        var max = workAreaStart + Math.Max(0, workAreaLength - targetLength);
        if (max < min)
        {
            return min;
        }

        return Math.Min(Math.Max(value, min), max);
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            CancelScheduledPanelFrontRestore();
            CancelScheduledFocusedSlotReassert();
            CancelScheduledPostLaunchArrange();
            return;
        }

        if (IsAnyMouseButtonPressed())
        {
            SuppressFocusedSlotReassertForPanelInput();
            return;
        }

        // マウスクリックで panel がアクティブになった直後に SetForegroundWindow すると、
        // Button の MouseUp/Click が成立しないため、focused 再適用は遅延実行する。
        ScheduleFocusedSlotReassert();
        RefreshAuxiliaryUi();
    }

    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            return;
        }

        if (IsAnyMouseButtonPressed())
        {
            SuppressFocusedSlotReassertForPanelInput();
            return;
        }

        ScheduleFocusedSlotReassert();
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SuppressPeriodicRefreshForInteractiveLayout();
    }

    private void MainWindow_LocationChanged(object? sender, EventArgs e)
    {
        SuppressPeriodicRefreshForInteractiveLayout();
    }

    private void SuppressPeriodicRefreshForInteractiveLayout()
    {
        _suppressPeriodicRefreshUntil = DateTimeOffset.UtcNow + InteractiveRefreshSuppressWindow;
    }

    private void RefreshAuxiliaryUi()
    {
        RefreshLaunchButtonAvailability();
        TaskbarJumpListService.Update(
            _statusStore.Slots,
            _displayMode,
            _statusStore.WorkspaceApplications,
            _statusStore.AuxiliaryApplications);
    }

    private void EnsurePreferredLayout(WindowSlot slot)
    {
        if (!_statusStore.IsVsCodeSlot(slot))
        {
            return;
        }

        if (slot.PreferredLayout.HasAnyValue)
        {
            return;
        }

        if (VscodeLayoutState.TryReadLayoutPreference(slot, _statusStore.Config, out var preference))
        {
            _statusStore.UpdatePreferredLayout(slot, preference);
        }
    }

    private void CapturePreferredLayout(WindowSlot slot)
    {
        if (!_statusStore.IsVsCodeSlot(slot))
        {
            return;
        }

        if (VscodeLayoutState.TryCapturePreferredLayout(slot, _statusStore.Config, _windowArranger, out var preference))
        {
            _statusStore.UpdatePreferredLayout(slot, preference);
        }
    }

    private void CaptureFocusedLayout()
    {
        var focusedSlot = _statusStore.Slots.FirstOrDefault(item => item.IsFocused);
        if (focusedSlot is not null)
        {
            CapturePreferredLayout(focusedSlot);
        }
    }

    private void SchedulePanelToFront(TimeSpan? delay = null)
    {
        CancelScheduledPanelFrontRestore();
        if (WindowState == WindowState.Minimized)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _panelFrontRestoreCancellation = cancellation;
        _ = BringPanelToFrontAfterDelayAsync(delay ?? PanelFrontRestoreDelay, cancellation.Token);
    }

    private void CancelScheduledPanelFrontRestore()
    {
        _panelFrontRestoreCancellation?.Cancel();
        _panelFrontRestoreCancellation?.Dispose();
        _panelFrontRestoreCancellation = null;
    }

    private void ScheduleFocusedSlotReassert(TimeSpan? delay = null)
    {
        CancelScheduledFocusedSlotReassert();
        if (WindowState == WindowState.Minimized
            || _isWindowMoveOrResizeActive
            || DateTimeOffset.UtcNow < _suppressFocusedSlotReassertUntil)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _focusedSlotReassertCancellation = cancellation;
        _ = ReassertFocusedSlotAfterDelayAsync(delay ?? FocusedSlotReassertDelay, cancellation.Token);
    }

    private void CancelScheduledFocusedSlotReassert()
    {
        _focusedSlotReassertCancellation?.Cancel();
        _focusedSlotReassertCancellation?.Dispose();
        _focusedSlotReassertCancellation = null;
    }

    private async Task ReassertFocusedSlotAfterDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    ReassertFocusedSlotIfNeeded();
                }
            }, DispatcherPriority.ContextIdle);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task BringPanelToFrontAfterDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested
                    && WindowState != WindowState.Minimized)
                {
                    BringPanelToFrontImmediate();
                }
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ReassertFocusedSlotIfNeeded()
    {
        if (_isReassertingFocusedSlot
            || _areWindowsHidden
            || _isBusy
            || _isWindowMoveOrResizeActive
            || WindowState == WindowState.Minimized
            || DateTimeOffset.UtcNow < _suppressFocusedSlotReassertUntil
            || IsAnyMouseButtonPressed())
        {
            return;
        }

        var focusedSlot = _statusStore.Slots.FirstOrDefault(slot => slot.IsFocused && slot.WindowHandle != IntPtr.Zero);
        if (focusedSlot is null)
        {
            return;
        }

        _isReassertingFocusedSlot = true;
        try
        {
            if (_windowArranger.FocusMaximized(focusedSlot.WindowHandle))
            {
                SendOtherSlotsToBack(focusedSlot);
                _windowArranger.BringToFrontOnce(focusedSlot.WindowHandle);
                SchedulePanelToFront();
            }
        }
        finally
        {
            _isReassertingFocusedSlot = false;
        }
    }

    private void SendOtherSlotsToBack(WindowSlot focusedSlot)
    {
        foreach (var slot in _statusStore.Slots)
        {
            if (ReferenceEquals(slot, focusedSlot) || slot.WindowHandle == IntPtr.Zero)
            {
                continue;
            }

            _windowArranger.SetBackmost(slot.WindowHandle);
        }
    }

    private void HideCloseAllConfirmDialog()
    {
        CloseAllConfirmOverlay.Visibility = Visibility.Collapsed;
    }

    private void HideClearSlotInfoDialog()
    {
        _pendingSlotInfoClear = null;
        ClearSlotInfoOverlay.Visibility = Visibility.Collapsed;
    }

    private void HideDeleteStoredPanelDialog()
    {
        _pendingStoredPanelDeletion = null;
        DeleteStoredPanelOverlay.Visibility = Visibility.Collapsed;
    }

    private void RestoreHiddenWindowState()
    {
        if (!_areWindowsHidden)
        {
            return;
        }

        _hiddenFocusedSlot = null;
        RestoreHiddenWindows();
        RefreshAuxiliaryUi();
    }

    private void RestoreHiddenWindows()
    {
        _areWindowsHidden = false;
        UpdateVisibilityButtonVisual();
        foreach (var slot in _statusStore.Slots)
        {
            _windowArranger.Restore(slot.WindowHandle);
            slot.IsHidden = false;
        }
    }

    private bool TryRestoreHiddenFocusedSlot(out WindowSlot restoredFocusedSlot)
    {
        restoredFocusedSlot = null!;
        var focusedSlot = _hiddenFocusedSlot;
        _hiddenFocusedSlot = null;

        if (focusedSlot is null || focusedSlot.WindowHandle == IntPtr.Zero)
        {
            return false;
        }

        SetManagedWindowLayerState(WindowSlot.SlotWindowLayerMode.Topmost);
        _windowArranger.Maximize(focusedSlot.WindowHandle);
        SendOtherSlotsToBack(focusedSlot);
        if (!_windowArranger.BringToFrontOnce(focusedSlot.WindowHandle))
        {
            return false;
        }

        _statusStore.SetFocusedSlot(focusedSlot);
        SchedulePanelToFront();
        restoredFocusedSlot = focusedSlot;
        return true;
    }

    private void UpdateVisibilityButtonVisual()
    {
        ToggleVisibilityButton.Content = _areWindowsHidden ? "表示" : "非表示";
        ToggleVisibilityButton.Style = (Style)FindResource(_areWindowsHidden
            ? "VisibilityRestoreButtonStyle"
            : "BarButton");

        if (MicroVisibilityButton is not null)
        {
            MicroVisibilityButton.Content = _areWindowsHidden ? "表" : "非";
            MicroVisibilityButton.ToolTip = _areWindowsHidden
                ? "管理中ウィンドウを再表示"
                : "管理中ウィンドウを非表示";
        }

        if (CompactVisibilityButton is not null)
        {
            CompactVisibilityButton.Content = MicroVisibilityButton?.Content ?? (_areWindowsHidden ? "\u8868" : "\u975E");
            CompactVisibilityButton.ToolTip = MicroVisibilityButton?.ToolTip;
        }
    }

    private void UpdateWindowHeightForStoredPanels(bool isExpanded, bool force = false)
    {
        if (_collapsedWindowHeight <= 0)
        {
            _collapsedWindowHeight = Height;
        }

        if (_collapsedWindowMinHeight <= 0)
        {
            _collapsedWindowMinHeight = MinHeight;
        }

        if (!isExpanded)
        {
            MinHeight = _collapsedWindowMinHeight;
            if (force || Height > _collapsedWindowHeight)
            {
                Height = _collapsedWindowHeight;
            }

            RememberStandardWindowMetrics();
            return;
        }

        StoredPanelsExpanderContent.UpdateLayout();
        var extraHeight = StoredPanelsExpanderContent.ActualHeight;
        if (extraHeight <= 0)
        {
            extraHeight = StoredPanelsExpanderContent.DesiredSize.Height;
        }

        if (extraHeight <= 0)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => UpdateWindowHeightForStoredPanels(true));
            return;
        }

        extraHeight = Math.Max(0, extraHeight + 12);
        var targetHeight = _collapsedWindowHeight + extraHeight;
        var targetMinHeight = _collapsedWindowMinHeight + extraHeight;

        Height = targetHeight;
        MinHeight = targetMinHeight;
        RememberStandardWindowMetrics();
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        SuppressFocusedSlotReassertForPanelInput();

        if (Keyboard.FocusedElement is not TextBox textBox || textBox.IsReadOnly)
        {
            return;
        }

        // ここでの確定処理はスロットカードのインライン編集 (InlineTitleTextBox) 専用。
        // 設定ダイアログなどの通常 TextBox を対象に含めると IsReadOnly/Focusable が書き換わって
        // 2 回目以降クリックしても入力できなくなる。
        if (!IsInlineTitleTextBox(textBox))
        {
            return;
        }

        if (IsSelfOrChild(e.OriginalSource as DependencyObject, textBox))
        {
            return;
        }

        FinishInlineTitleTextBoxEdit(textBox, commit: true, clearKeyboardFocus: true);
    }

    private bool IsInlineTitleTextBox(TextBox textBox)
    {
        return textBox.Style is { } style
            && ReferenceEquals(style, TryFindResource("InlineTitleTextBox") as Style);
    }

    private void InlineTitleTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || e.Key is not (Key.Return or Key.Enter or Key.Escape))
        {
            return;
        }

        FinishInlineTitleTextBoxEdit(textBox, commit: e.Key != Key.Escape, clearKeyboardFocus: true);
        e.Handled = true;
    }

    private void InlineTitleTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox || !textBox.IsReadOnly)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            textBox.IsReadOnly = false;
            textBox.Focusable = true;
            textBox.Cursor = Cursors.IBeam;
            textBox.Focus();
            textBox.SelectAll();
            e.Handled = true;
        }
    }

    private void InlineTitleTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        FinishInlineTitleTextBoxEdit(textBox, commit: true, clearKeyboardFocus: false);
    }

    private static void FinishInlineTitleTextBoxEdit(TextBox textBox, bool commit, bool clearKeyboardFocus)
    {
        if (commit)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
        else
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }

        textBox.IsReadOnly = true;
        textBox.Focusable = false;
        textBox.Cursor = Cursors.Arrow;

        if (clearKeyboardFocus)
        {
            Keyboard.ClearFocus();
        }
    }

    private static bool IsSelfOrChild(DependencyObject? source, DependencyObject target)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, target))
            {
                return true;
            }

            source = GetUiParent(source);
        }

        return false;
    }

    private static bool IsInteractiveCardChild(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase)
            {
                return true;
            }

            if (source is TextBox textBox && !textBox.IsReadOnly)
            {
                return true;
            }

            source = GetUiParent(source);
        }

        return false;
    }

    private static DependencyObject? GetUiParent(DependencyObject source)
    {
        if (source is FrameworkContentElement contentElement)
        {
            return contentElement.Parent;
        }

        try
        {
            return VisualTreeHelper.GetParent(source);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
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

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (CloseAllConfirmOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
        {
            HideCloseAllConfirmDialog();
            e.Handled = true;
            return;
        }

        if (ClearSlotInfoOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
        {
            HideClearSlotInfoDialog();
            e.Handled = true;
            return;
        }

        if (DeleteStoredPanelOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
        {
            HideDeleteStoredPanelDialog();
            e.Handled = true;
            return;
        }

        if (HelpOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
        {
            HideHelpDialog();
            e.Handled = true;
            return;
        }

        if (SettingsOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
        {
            HideSettingsDialog();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        CancelScheduledFocusedSlotReassert();
        CancelScheduledPostLaunchArrange();

        // 非表示中にアプリが終了される場合、管理中ウィンドウを復元してから閉じる
        if (_areWindowsHidden)
        {
            foreach (var slot in _statusStore.Slots)
            {
                _windowArranger.Restore(slot.WindowHandle);
                slot.IsHidden = false;
            }

            _areWindowsHidden = false;
            _hiddenFocusedSlot = null;
            ArrangeSlotsOnActiveMonitor(false);
        }

        base.OnClosing(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(PanelWindowProcHook);
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _refreshCancellation.Cancel();
        _panelFrontRestoreCancellation?.Cancel();
        _panelFrontRestoreCancellation?.Dispose();
        _focusedSlotReassertCancellation?.Cancel();
        _focusedSlotReassertCancellation?.Dispose();
        _postLaunchArrangeCancellation?.Cancel();
        _postLaunchArrangeCancellation?.Dispose();
        StopPanelLocateEmphasis();
        base.OnClosed(e);
    }

    private IntPtr PanelWindowProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // パネルウィンドウのタイトルバードラッグ中に focused slot の reassert が走ると
        // SetForegroundWindow で前面が奪われ、ネイティブ移動ループが破棄されてパネルが
        // 元位置に戻ってしまう。WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE で操作中フラグを立てる。
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE = 0x0232;

        switch (msg)
        {
            case WM_ENTERSIZEMOVE:
                _isWindowMoveOrResizeActive = true;
                SuppressFocusedSlotReassertForPanelInput();
                break;
            case WM_EXITSIZEMOVE:
                _isWindowMoveOrResizeActive = false;
                SuppressFocusedSlotReassertForPanelInput();
                break;
        }

        return IntPtr.Zero;
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
        LaunchButton.IsEnabled = !busy && HasLaunchableWorkspaceSlot();
        ApplicationLauncherPanel.IsEnabled = !busy;
        TopmostAllButton.IsEnabled = !busy;
        BackmostAllButton.IsEnabled = !busy;
        ToggleMonitorButton.IsEnabled = !busy;
        ToggleVisibilityButton.IsEnabled = !busy;
        SaveSettingsButton.IsEnabled = !busy;
        LoadSettingsButton.IsEnabled = !busy;
        CloseAllButton.IsEnabled = !busy;
        SettingsButton.IsEnabled = !busy;
        HelpButton.IsEnabled = !busy;
        DisplayModeButton.IsEnabled = !busy;
        StandardJumpButton.IsEnabled = !busy;
        CompactVisibilityButton.IsEnabled = !busy;
        MicroVisibilityButton.IsEnabled = !busy;
        CompactBarPanel.IsEnabled = !busy;
        StoredPanelsExpander.IsEnabled = !busy;
        SettingsOverlay.IsEnabled = !busy;
        AuxiliaryApplicationPanel.IsEnabled = !busy;
    }

    private void RefreshLaunchButtonAvailability()
    {
        LaunchButton.IsEnabled = !_isBusy && HasLaunchableWorkspaceSlot();
        LaunchButton.ToolTip = _statusStore.LaunchButtonToolTip;
    }

    private bool HasLaunchableWorkspaceSlot()
    {
        return _statusStore.Slots.Any(slot =>
        {
            var application = _statusStore.FindApplication(slot.ApplicationId);
            return application is not null
                && application.IsWorkspaceApplication
                && _applicationLauncher.CanLaunchWorkspaceApplication(application, _statusStore.Config);
        });
    }

    private enum PanelFrameVisual
    {
        Normal,
        Emphasis
    }

    private bool IsCompactMode => _displayMode == DisplayMode.Compact;
    private bool IsMicroMode => _displayMode == DisplayMode.Micro;
    private bool IsStandardMode => _displayMode == DisplayMode.Standard;

    private void SuppressFocusedSlotReassertForPanelInput()
    {
        if (!_statusStore.Slots.Any(slot => slot.IsFocused))
        {
            return;
        }

        _suppressFocusedSlotReassertUntil = DateTimeOffset.UtcNow + FocusedSlotReassertInputSuppressWindow;
        CancelScheduledFocusedSlotReassert();
    }

    private static bool IsAnyMouseButtonPressed()
    {
        return Mouse.LeftButton == MouseButtonState.Pressed
            || Mouse.RightButton == MouseButtonState.Pressed
            || Mouse.MiddleButton == MouseButtonState.Pressed
            || Mouse.XButton1 == MouseButtonState.Pressed
            || Mouse.XButton2 == MouseButtonState.Pressed;
    }
}
