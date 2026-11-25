using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

// 極小表示（Micro モード）用の円形オービット・ダイヤル（MicroDialWindow）と MainWindow の連携管理。
//
// MainWindow は AllowsTransparency=False（WindowChrome 付き）のままタスクバー連携・標準/縮小表示の
// 安定性を保ち、極小表示の間だけ DWMWA_CLOAK で隠して専用の透過円形ウィンドウ MicroDialWindow を出す。
// 万一 DWMWA_CLOAK が拒否される環境や生成失敗時は、従来どおり MainWindow の MicroPanel へ自動フォールバックする。
public partial class MainWindow
{
    private const double MicroRenameViewportWidth = 430;
    private const double MicroRenameViewportHeight = 180;
    private const double MicroModalViewportHeight = 280;
    private const double MicroStoredOverviewExtraGapDip = 48;

    private MicroDialWindow? _microDialWindow;
    private FrameworkElement? _microDialModeButtonIcon;
    private bool _microDialSuspendedForOverlay;
    private WindowArranger.WindowBounds? _boundsBeforeMicroRename;
    private double _minWidthBeforeMicroRename;
    private double _minHeightBeforeMicroRename;

    // 縮小表示のタイトルバーに出す「極小表示（円形オービット・ダイヤル）へ切り替え」用のベクターアイコン。
    // 外円＋4分割十字スポーク＋中央小円のシルエットで、押した先の円形ダイヤルを一目で連想させる。
    // 線の色は親 Button の Foreground に連動するため、通常時・ホバー時・無効時の見た目が他ボタンと一致する。
    internal static Canvas CreateMicroDialModeIcon()
    {
        var strokeBinding = new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1)
        };

        var outerRing = new Ellipse
        {
            Width = 12.0,
            Height = 12.0,
            StrokeThickness = 1.15,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
        outerRing.SetBinding(Shape.StrokeProperty, strokeBinding);
        Canvas.SetLeft(outerRing, 0.5);
        Canvas.SetTop(outerRing, 0.5);

        var centerRing = new Ellipse
        {
            Width = 3.8,
            Height = 3.8,
            StrokeThickness = 1.05,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
        centerRing.SetBinding(Shape.StrokeProperty, strokeBinding);
        Canvas.SetLeft(centerRing, 4.6);
        Canvas.SetTop(centerRing, 4.6);

        var spokesGeometry = Geometry.Parse("M 6.5,0.9 L 6.5,4.6 M 6.5,8.4 L 6.5,12.1 M 0.9,6.5 L 4.6,6.5 M 8.4,6.5 L 12.1,6.5");
        spokesGeometry.Freeze();
        var spokes = new Path
        {
            Data = spokesGeometry,
            StrokeThickness = 1.0,
            IsHitTestVisible = false
        };
        spokes.SetBinding(Shape.StrokeProperty, strokeBinding);

        var canvas = new Canvas
        {
            Width = 13.0,
            Height = 13.0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false
        };
        canvas.Children.Add(outerRing);
        canvas.Children.Add(spokes);
        canvas.Children.Add(centerRing);
        return canvas;
    }

    private bool IsMicroDialActive => IsMicroMode
        && _microDialWindow is not null
        && !_microDialSuspendedForOverlay;

    private IntPtr GetActivePresentationPanelHandle()
    {
        if (IsMicroDialActive && _microDialWindow is { } dial && dial.Handle != IntPtr.Zero)
        {
            return dial.Handle;
        }

        return new WindowInteropHelper(this).Handle;
    }

    private void EnsureMicroDialActiveForMicroMode(WindowArranger.WindowBounds coreBoundsPx, bool deferShowForTransition)
    {
        if (!IsMicroMode || !IsLoaded)
        {
            return;
        }

        var mainHandle = new WindowInteropHelper(this).Handle;
        if (mainHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (_microDialWindow is null)
            {
                _microDialWindow = new MicroDialWindow(
                    _statusStore.Slots,
                    _statusStore.Config.MicroGhostOpacityEnabled,
                    getAuxApplications: () => _statusStore.AuxiliaryApplications,
                    onSlotActivated: OnMicroDialSlotActivated,
                    onStoredPanelDropped: OnMicroDialStoredPanelDropped,
                    onSlotSwapped: OnMicroDialSlotSwapped,
                    onToggleVisibility: () => ToggleVisibilityButton_Click(this, new RoutedEventArgs()),
                    onToggleStoredOverview: OnMicroDialToggleStoredOverview,
                    onPinTop: () => PinAllTopButton_Click(this, new RoutedEventArgs()),
                    onSendBack: () => SendAllBackButton_Click(this, new RoutedEventArgs()),
                    onLaunchAuxApp: OnMicroDialLaunchAuxApp,
                    onSwitchStandard: () => SetDisplayMode(DisplayMode.Standard),
                    onSwitchCompact: () => SetDisplayMode(DisplayMode.Compact),
                    onMinimize: RequestPanelMinimize,
                    onRequestClose: () => CloseButton_Click(this, new RoutedEventArgs()),
                    onGhostOpacityChanged: OnMicroDialGhostOpacityChanged,
                    onRecenterRequested: RecenterMicroDial,
                    onCoreCenterMoved: OnMicroDialCoreCenterMoved,
                    contextMenuStyle: TryFindResource("PanelContextMenuStyle") as Style,
                    buildContextMenu: (slot, ghostEnabled, toggleGhost, recenter) =>
                        BuildMicroDialContextMenu(slot, ghostEnabled, toggleGhost, recenter),
                    onOpenDownloadsFolder: () => DownloadsFolderButton_Click(this, new RoutedEventArgs()),
                    onOpenPullRequestInbox: () => PullRequestInboxButton_Click(this, new RoutedEventArgs()),
                    pullRequestIconGeometry: TryFindResource("GitPullRequestGeometry") as Geometry);
            }

            _microDialSuspendedForOverlay = false;
            SyncMicroDialUiState();
            _microDialWindow.PlaceCoreCenteredAt(coreBoundsPx.Left, coreBoundsPx.Top, coreBoundsPx.Width, coreBoundsPx.Height);

            if (!deferShowForTransition)
            {
                // 本物 MainWindow を Cloak できない環境では安全のため従来 MicroPanel へフォールバックする。
                if (!SetCloaked(mainHandle, true))
                {
                    DisposeMicroDialWindow();
                    return;
                }

                _microDialWindow.ShowTopmost();
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            DisposeMicroDialWindow();
            SetCloaked(mainHandle, false);
        }
    }

    // モード切替モーフィング（標準/縮小 → 極小）の完了時に呼ぶ。
    // MainWindow は Cloak を維持したまま、変形完了と同じ瞬間に円形ダイヤルを表示する。
    private bool CompleteMicroDialRevealAfterTransition()
    {
        if (!IsMicroMode || _microDialWindow is null)
        {
            return false;
        }

        var mainHandle = new WindowInteropHelper(this).Handle;
        if (!SetCloaked(mainHandle, true))
        {
            DisposeMicroDialWindow();
            return false;
        }

        _microDialSuspendedForOverlay = false;
        SyncMicroDialUiState();
        _microDialWindow.ShowTopmost();
        return true;
    }

    private void DeactivateMicroDialForNonMicroMode(bool keepMainCloakedDuringTransition)
    {
        _microDialSuspendedForOverlay = false;
        DisposeMicroDialWindow();
        if (!keepMainCloakedDuringTransition)
        {
            SetCloaked(new WindowInteropHelper(this).Handle, false);
        }
    }

    private void DisposeMicroDialWindow()
    {
        if (_microDialWindow is null)
        {
            return;
        }

        var dial = _microDialWindow;
        _microDialWindow = null;
        try
        {
            dial.HideWithPopups();
            dial.Close();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    // 終了確認・クリア確認・控えの名前変更など、本体パネル内モーダルを表示する間だけ円形ダイヤルを一時退避して本物を前面に出す。
    private void SuspendMicroDialForMainOverlay(bool expandForRename = false, bool expandForModal = false)
    {
        if (!IsMicroMode || _microDialWindow is null)
        {
            return;
        }

        _microDialSuspendedForOverlay = true;
        _microDialWindow.HideWithPopups();
        if (expandForModal)
        {
            EnsureMicroOverlayViewport(MicroRenameViewportWidth, MicroModalViewportHeight);
        }
        else if (expandForRename)
        {
            EnsureMicroOverlayViewport(MicroRenameViewportWidth, MicroRenameViewportHeight);
        }

        SetCloaked(new WindowInteropHelper(this).Handle, false);
    }

    private void ResumeMicroDialAfterMainOverlay(bool restoreRenameViewport = false)
    {
        if (restoreRenameViewport)
        {
            RestoreBoundsAfterMicroRename();
        }

        if (!_microDialSuspendedForOverlay)
        {
            return;
        }

        _microDialSuspendedForOverlay = false;
        if (!IsMicroMode || _microDialWindow is null || WindowState == WindowState.Minimized || _closeConfirmed)
        {
            return;
        }

        var mainHandle = new WindowInteropHelper(this).Handle;
        if (AppExitConfirmOverlay.Visibility == Visibility.Visible
            || StoredRenameOverlay.Visibility == Visibility.Visible
            || ClearSlotInfoOverlay.Visibility == Visibility.Visible
            || DeleteStoredPanelOverlay.Visibility == Visibility.Visible)
        {
            _microDialSuspendedForOverlay = true;
            return;
        }

        var currentBounds = GetCurrentWindowBounds();
        _microDialWindow.PlaceCoreCenteredAt(currentBounds.Left, currentBounds.Top, currentBounds.Width, currentBounds.Height);
        if (!SetCloaked(mainHandle, true))
        {
            DisposeMicroDialWindow();
            return;
        }

        _microDialWindow.ShowTopmost();
        SyncMicroDialUiState();
    }

    private void EnsureMicroOverlayViewport(double requiredWidthDip, double requiredHeightDip)
    {
        if (_boundsBeforeMicroRename.HasValue)
        {
            return;
        }

        var panelHandle = new WindowInteropHelper(this).Handle;
        var scale = GetCurrentDpiScale(panelHandle);
        var currentBounds = GetCurrentWindowBounds();
        var requiredWidth = requiredWidthDip * scale;
        var requiredHeight = requiredHeightDip * scale;
        if (currentBounds.Width >= requiredWidth && currentBounds.Height >= requiredHeight)
        {
            return;
        }

        _boundsBeforeMicroRename = currentBounds;
        _minWidthBeforeMicroRename = MinWidth;
        _minHeightBeforeMicroRename = MinHeight;

        var targetWidth = Math.Max(currentBounds.Width, requiredWidth);
        var targetHeight = Math.Max(currentBounds.Height, requiredHeight);
        var targetLeft = currentBounds.Left + ((currentBounds.Width - targetWidth) / 2.0);
        var targetTop = currentBounds.Top + ((currentBounds.Height - targetHeight) / 2.0);

        if (panelHandle != IntPtr.Zero
            && _windowArranger.TryGetMonitorWorkAreaForWindow(panelHandle, out var workArea))
        {
            targetWidth = Math.Min(targetWidth, workArea.Width);
            targetHeight = Math.Min(targetHeight, workArea.Height);
            targetLeft = ClampToWorkArea(targetLeft, workArea.Left, workArea.Width, targetWidth);
            targetTop = ClampToWorkArea(targetTop, workArea.Top, workArea.Height, targetHeight);
        }

        MinWidth = Math.Min(requiredWidthDip, targetWidth / scale);
        MinHeight = Math.Min(requiredHeightDip, targetHeight / scale);
        SetWindowBounds(targetLeft, targetTop, targetWidth, targetHeight);
    }

    private void RestoreBoundsAfterMicroRename()
    {
        if (_boundsBeforeMicroRename is not { } originalBounds)
        {
            return;
        }

        MinWidth = _minWidthBeforeMicroRename;
        MinHeight = _minHeightBeforeMicroRename;
        _boundsBeforeMicroRename = null;
        SetWindowBounds(originalBounds.Left, originalBounds.Top, originalBounds.Width, originalBounds.Height);
    }

    private void HideMicroDialForMinimize()
    {
        _microDialWindow?.HideWithPopups();
    }

    private void RestoreMicroDialAfterUnminimize()
    {
        if (!IsMicroMode || _microDialWindow is null || _microDialSuspendedForOverlay || WindowState == WindowState.Minimized)
        {
            return;
        }

        var mainHandle = new WindowInteropHelper(this).Handle;
        var currentBounds = GetCurrentWindowBounds();
        _microDialWindow.PlaceCoreCenteredAt(currentBounds.Left, currentBounds.Top, currentBounds.Width, currentBounds.Height);
        if (!SetCloaked(mainHandle, true))
        {
            DisposeMicroDialWindow();
            return;
        }

        _microDialWindow.ShowTopmost();
        SyncMicroDialUiState();
    }

    private void SyncMicroDialUiState()
    {
        if (_microDialWindow is null)
        {
            return;
        }

        _microDialWindow.SetWindowsHiddenState(_areWindowsHidden);
        _microDialWindow.SetStoredOverviewOpen(StoredOverviewPopup.IsOpen && !_isStoredOverviewClosing);
        _microDialWindow.SetPendingStoredPanel(_selectedOverviewStoredPanel);
        _microDialWindow.SetPullRequestBadge(
            UpdateBadge.GetIsActive(PullRequestInboxButton),
            PullRequestInboxButton.ToolTip as string);
        _microDialWindow.RefreshVisuals();
    }

    private void OnMicroDialSlotActivated(WindowSlot slot)
    {
        if (_isBusy || slot.WindowHandle == IntPtr.Zero)
        {
            // 未起動（停止中）スロットはクリックだけで誤起動させない。起動は右クリックメニュー「起動する」から行う。
            return;
        }

        ToggleSlotFocus(slot);
        _microDialWindow?.ShowTopmost();
    }

    private async void OnMicroDialStoredPanelDropped(StoredPanelSlot storedPanel, WindowSlot targetSlot)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            await ShowStoredPanelFromOverviewAsync(storedPanel, targetSlot);
            _microDialWindow?.ShowTopmost();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    private async void OnMicroDialSlotSwapped(WindowSlot sourceSlot, WindowSlot targetSlot)
    {
        if (_isBusy || ReferenceEquals(sourceSlot, targetSlot))
        {
            return;
        }

        _statusStore.SwapSlotContents(sourceSlot, targetSlot);
        UpdateDisplayBadges();
        _statusStore.Message = $"スロット{sourceSlot.Name}とスロット{targetSlot.Name}のカードを入れ替えました。";
        RefreshAuxiliaryUi();

        if (_areWindowsHidden)
        {
            return;
        }

        try
        {
            await ArrangeSlotsAfterPanelStateChangeWithSettlingAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    private void OnMicroDialToggleStoredOverview()
    {
        if (StoredOverviewPopup.IsOpen && !_isStoredOverviewClosing)
        {
            CloseStoredOverview(animate: true);
            return;
        }

        OpenStoredOverview();
    }

    private async void OnMicroDialLaunchAuxApp(LauncherApplication application)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            await OpenAuxiliaryApplicationAsync(application);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    private void OnMicroDialGhostOpacityChanged(bool enabled)
    {
        _statusStore.Config.MicroGhostOpacityEnabled = enabled;
        try
        {
            _statusStore.Config.SaveToUserConfig();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    private void RecenterMicroDial()
    {
        if (!IsMicroMode)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var scale = GetCurrentDpiScale(handle);
        var centered = GetMicroModeCenteredBounds(MicroWindowSize * scale, MicroWindowSize * scale);
        SetWindowBounds(centered.Left, centered.Top, centered.Width, centered.Height);
        _microDialWindow?.PlaceCoreCenteredAt(centered.Left, centered.Top, centered.Width, centered.Height);
        RepositionStoredOverview();
    }

    private void OnMicroDialCoreCenterMoved(Point coreCenterScreenPx)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var scale = GetCurrentDpiScale(handle);
        var widthPx = MicroWindowSize * scale;
        var heightPx = MicroWindowSize * scale;
        var leftPx = coreCenterScreenPx.X - (widthPx / 2.0);
        var topPx = coreCenterScreenPx.Y - (heightPx / 2.0);
        SetWindowPos(
            handle,
            IntPtr.Zero,
            (int)Math.Round(leftPx),
            (int)Math.Round(topPx),
            Math.Max(1, (int)Math.Round(widthPx)),
            Math.Max(1, (int)Math.Round(heightPx)),
            SWP_NOZORDER | SWP_NOACTIVATE);
        RepositionStoredOverview();
    }
}
