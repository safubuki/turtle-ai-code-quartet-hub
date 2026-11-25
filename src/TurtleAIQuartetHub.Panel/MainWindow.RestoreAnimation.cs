using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

// アプリ本体（メインパネル）の最小化からの復元アニメーション。最小化（MainWindow.MinimizeAnimation.cs）を
// そっくり逆に再生し、タスクバーのこのアプリのボタンから、強くぼけたもやが集まるように現れ、
// くっきりへ戻りながら元の大きさへ広がって、元の位置にふわっと収まる。
//
// 最小化と違い、復元は Windows が本物を表示し直すので、表示される前に本物を DWM の cloak で隠しておく必要がある。
//   1. 復元の直前（WM_WINDOWPOSCHANGING：タスクバーのクリック・Alt+Tab・システムメニューなど、どの経路でも来る）、
//      またはアプリ自身が戻す直前（ActivatePanelWindow）に、本物を cloak して「復元待ち」にする。
//   2. 復元された（StateChanged）ら、DPI の補正とレイアウトが落ち着くのを待って、本物を画像に撮る（cloak 中でも撮れる）。
//   3. タスクバーのボタンの位置に影武者を置き、最小化の逆の動きで元の位置まで広げる。
//   4. 最後のコマは本物と同じ見た目なので、本物の cloak を外してから影武者を閉じる（すき間のコマを作らない）。
// 安全装置: 復元待ちにしたのに復元されなかった（思い違い）ときはすぐ cloak を外す。復元待ち・再生のどちらにも
// 期限を付け、どの経路でも最後は必ず cloak を外す（本物が見えないまま残る事故を作らない）。
public partial class MainWindow
{
    // 最小化・表示モード切替と同じ長さ（340ms）にそろえる（テストで一致を保証）。
    private static readonly TimeSpan RestoreRevealDuration = TimeSpan.FromMilliseconds(340);
    private static readonly TimeSpan RestoreGhostSettleDelay = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan RestoreGhostCloseDelay = TimeSpan.FromMilliseconds(70);
    private static readonly TimeSpan RestoreArmTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan RestoreWatchdogSlack = TimeSpan.FromMilliseconds(1200);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    // 最小化中のウィンドウは画面外のこの座標付近へ退避される。
    private const int IconicParkingCoordinate = -30000;

    private static KeySpline? s_restoreTravelSpline;
    private static KeySpline? s_restoreNarrowSpline;
    private static KeySpline? s_restoreFadeSpline;

    private bool _isRestoreRevealArmed;
    private bool _isRestoreRevealRunning;
    private bool _openStoredOverviewAfterRestoreReveal;
    private DispatcherTimer? _restoreArmWatchdog;
    private Action? _forceFinishRestoreReveal;

    // 最小化の曲線を時間について裏返したもの（逆再生）。
    internal static KeySpline RestoreTravelSpline => s_restoreTravelSpline ??= ReverseSpline(MinimizeTravelSpline);
    internal static KeySpline RestoreNarrowSpline => s_restoreNarrowSpline ??= ReverseSpline(MinimizeNarrowSpline);
    internal static KeySpline RestoreFadeSpline => s_restoreFadeSpline ??= ReverseSpline(MinimizeFadeSpline);

    // WM_WINDOWPOSCHANGING から呼ぶ。最小化中のパネルが元の位置へ戻されようとしていたら、表示される前に隠す。
    private void ArmRestoreRevealIfRestoring(IntPtr hwnd, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero
            || _isRestoreRevealArmed
            || _isRestoreRevealRunning
            || WindowState != WindowState.Minimized)
        {
            return;
        }

        var position = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        if (!IsRestoreWindowPos(position.x, position.y, position.flags))
        {
            return;
        }

        TryArmRestoreReveal(hwnd);
    }

    // アプリ自身が WindowState = Normal で戻す直前に呼ぶ（WPF は値を先に変えるため、WM_WINDOWPOSCHANGING では判定できない）。
    private void ArmRestoreRevealBeforeProgrammaticRestore()
    {
        if (WindowState == WindowState.Minimized && !_isRestoreRevealArmed && !_isRestoreRevealRunning)
        {
            TryArmRestoreReveal(new WindowInteropHelper(this).Handle);
        }
    }

    // テスト対象: WINDOWPOS が「最小化中のウィンドウを画面内の位置へ戻す」ものかどうか。
    // 重なり順だけの変更（移動も大きさ変更もしない）や、画面外の退避位置への移動は復元ではない。
    internal static bool IsRestoreWindowPos(int x, int y, uint flags)
    {
        var zOrderOnly = (flags & SwpNoMove) != 0 && (flags & SwpNoSize) != 0;
        return !zOrderOnly
            && (flags & SwpNoMove) == 0
            && x > IconicParkingCoordinate
            && y > IconicParkingCoordinate;
    }

    private bool CanAnimateRestore(IntPtr handle)
    {
        return handle != IntPtr.Zero
            && IsLoaded
            && !_closeConfirmed
            && !_isExitAnimationRunning
            && SystemParameters.ClientAreaAnimation
            && SystemParameters.MinimizeAnimation;
    }

    private void TryArmRestoreReveal(IntPtr handle)
    {
        if (!CanAnimateRestore(handle) || !SetCloaked(handle, true))
        {
            return;
        }

        _isRestoreRevealArmed = true;
        // 吸い込まれた先（＝現れる元）のボタンの位置を、復元が終わるまでの間に調べ直しておく。
        _ = _taskbarButtonLocator.RefreshAsync(handle, Title);

        _restoreArmWatchdog?.Stop();
        _restoreArmWatchdog = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = RestoreArmTimeout
        };
        _restoreArmWatchdog.Tick += (_, _) =>
        {
            if (_isRestoreRevealArmed)
            {
                DiagnosticLog.Write(LogLevel.Warn, "Restore reveal was armed but the panel was not restored in time; releasing cloak.");
            }

            DisarmRestoreReveal();
        };
        _restoreArmWatchdog.Start();

        // 復元でなかった（最小化のまま）なら、メッセージ処理が終わった直後に隠すのをやめる。
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_isRestoreRevealArmed && WindowState == WindowState.Minimized)
            {
                DisarmRestoreReveal();
            }
        }));
    }

    private void DisarmRestoreReveal()
    {
        _restoreArmWatchdog?.Stop();
        _restoreArmWatchdog = null;
        if (!_isRestoreRevealArmed)
        {
            return;
        }

        _isRestoreRevealArmed = false;
        SetCloaked(new WindowInteropHelper(this).Handle, false);
    }

    // StateChanged（最小化から戻った）で呼ぶ。復元待ちなら演出を始めて true を返す。
    private bool BeginRestoreRevealIfArmed()
    {
        if (!_isRestoreRevealArmed)
        {
            return false;
        }

        _restoreArmWatchdog?.Stop();
        _restoreArmWatchdog = null;
        _isRestoreRevealArmed = false;

        var handle = new WindowInteropHelper(this).Handle;
        if (WindowState != WindowState.Normal || !IsVisible)
        {
            SetCloaked(handle, false);
            return false;
        }

        _isRestoreRevealRunning = true;
        RunRestoreReveal(handle);
        return true;
    }

    private async void RunRestoreReveal(IntPtr handle)
    {
        StartupRevealGhostWindow? ghost = null;
        var completed = false;
        var watchdog = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = MinimizeTargetLookupBudget + RestoreGhostSettleDelay + RestoreRevealDuration + RestoreWatchdogSlack
        };

        // 後始末は 1 か所に集め、何度呼ばれても 1 回だけ行う。どの経路で終わっても本物の cloak は必ず外す。
        void Finish(bool closeGhostImmediately)
        {
            if (completed)
            {
                return;
            }

            completed = true;
            watchdog.Stop();
            _isRestoreRevealRunning = false;
            _forceFinishRestoreReveal = null;
            if (!IsMicroMode || !CompleteMicroDialRevealAfterTransition())
            {
                SetCloaked(handle, false);
            }

            if (_openStoredOverviewAfterRestoreReveal)
            {
                _openStoredOverviewAfterRestoreReveal = false;
                if (IsStoredPanelsAreaOpen && WindowState == WindowState.Normal)
                {
                    ScheduleStoredOverviewAutoOpen();
                }
            }

            var closingGhost = ghost;
            if (closingGhost is null)
            {
                return;
            }

            if (closeGhostImmediately)
            {
                SafeCloseStartupGhost(closingGhost);
                return;
            }

            // 本物の cloak が外れて 1〜2 フレーム描かれたあとで影武者を閉じる（同じ見た目が重なっているだけなので変化は見えない）。
            var closeTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = RestoreGhostCloseDelay
            };
            closeTimer.Tick += (_, _) =>
            {
                closeTimer.Stop();
                SafeCloseStartupGhost(closingGhost);
            };
            closeTimer.Start();
        }

        _forceFinishRestoreReveal = () => Finish(closeGhostImmediately: true);
        watchdog.Tick += (_, _) =>
        {
            DiagnosticLog.Write(LogLevel.Warn, "Restore reveal animation did not complete in time; forcing completion.");
            Finish(closeGhostImmediately: true);
        };
        watchdog.Start();

        try
        {
            // 復元直後の DPI 補正（Render 優先度）とレイアウトが終わってから撮影する。
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (completed)
            {
                return;
            }

            if (WindowState != WindowState.Normal
                || !IsVisible
                || PanelFrameBorder.ActualWidth <= 0
                || PanelFrameBorder.ActualHeight <= 0)
            {
                Finish(closeGhostImmediately: true);
                return;
            }

            var buttonLookup = ResolveTaskbarButtonRectAsync(handle);
            UpdateLayout();
            var restoreMicroDial = IsMicroMode && _microDialWindow is not null && !_microDialSuspendedForOverlay;
            var panelRect = restoreMicroDial ? _microDialWindow!.GetCoreScreenRect() : GetPanelScreenRect();
            var panelImage = restoreMicroDial ? _microDialWindow!.CaptureCoreCircleImage() : CapturePanelImage();
            var hasSoftImages = TryCreateSoftenedPanelImages(panelImage, out var lightSoft, out var heavySoft);

            Rect? taskbarButton;
            try
            {
                taskbarButton = await buttonLookup;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
                taskbarButton = null;
            }

            if (completed)
            {
                return;
            }

            if (panelRect.Width <= 0
                || panelRect.Height <= 0
                || !_windowArranger.TryGetMonitorAreasForWindow(handle, out var monitor, out var workArea))
            {
                Finish(closeGhostImmediately: true);
                return;
            }

            var scale = VisualTreeHelper.GetDpi(this);
            var targetRect = ResolveMinimizeTargetRect(
                panelRect,
                ToRect(monitor),
                ToRect(workArea),
                taskbarButton,
                MinimizeFallbackTargetSize * scale.DpiScaleX);
            var plan = BuildMinimizeSinkPlan(
                panelRect,
                targetRect,
                MinimizeGhostPadding * scale.DpiScaleX,
                MinimizeGhostPadding * scale.DpiScaleY);

            ghost = new StartupRevealGhostWindow(
                plan.GhostArea,
                panelRect,
                panelImage,
                initialOpacity: 0.0,
                initialScale: plan.EndScale,
                initialOffsetY: 0.0,
                cornerRadius: restoreMicroDial ? MicroDialWindow.CoreRadiusDip : 8.0);
            if (hasSoftImages)
            {
                ghost.SetSoftenedImages(lightSoft, heavySoft);
            }

            var shownGhost = ghost;
            shownGhost.ContentRendered += async (_, _) =>
            {
                if (completed)
                {
                    return;
                }

                try
                {
                    // 影武者の描画パイプラインが落ち着くのを一瞬だけ待ってから再生し、冒頭のコマ飛びを防ぐ。
                    await Task.Delay(RestoreGhostSettleDelay);
                    if (completed)
                    {
                        return;
                    }

                    shownGhost.PlayRestoreReveal(plan, () => Finish(closeGhostImmediately: false));
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write(ex);
                    Finish(closeGhostImmediately: true);
                }
            };
            shownGhost.ShowGhost();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            Finish(closeGhostImmediately: true);
        }
    }

    // 再び最小化された・表示モードの切替・終了のときに呼ぶ。復元待ちも再生中の演出もすぐ終わらせ、本物を見せる。
    private void CompleteRestoreRevealImmediately()
    {
        DisarmRestoreReveal();
        _forceFinishRestoreReveal?.Invoke();
    }

    // 時間について裏返した曲線: 元の曲線を p(t) とすると 1 − p(1 − t)。
    internal static KeySpline ReverseSpline(KeySpline spline)
    {
        var reversed = new KeySpline(
            1.0 - spline.ControlPoint2.X,
            1.0 - spline.ControlPoint2.Y,
            1.0 - spline.ControlPoint1.X,
            1.0 - spline.ControlPoint1.Y);
        reversed.Freeze();
        return reversed;
    }

    // 時間について裏返した時刻表。最小化の時刻表は最後の点の値を終わりまで保つので、それが復元のはじめの値になる。
    // つなぐ曲線（MinimizeBlendSpline）は左右対称なので、点を裏返すだけでそっくり逆の動きになる。
    internal static (double Ratio, double Value)[] MirrorBlendPoints((double Ratio, double Value)[] points)
    {
        var mirrored = points.Select(point => (Ratio: 1.0 - point.Ratio, point.Value)).ToList();
        var last = points[^1];
        if (last.Ratio < 1.0)
        {
            mirrored.Add((0.0, last.Value));
        }

        return mirrored
            .OrderBy(point => point.Ratio)
            .ToArray();
    }

    // テスト用: 復元演出の時間配分（安定待ち・全体）。
    internal static (TimeSpan Settle, TimeSpan Total) GetRestoreRevealTimelineForTesting()
    {
        return (RestoreGhostSettleDelay, RestoreRevealDuration);
    }

    // テスト専用の無菌検証ヘルパー。画面へ Show() せずに、復元用の影武者でタイムラインを組み立てられることを検証する。
    internal static (bool AllowsTransparency, Point AppliedOrigin) VerifyRestoreGhostOffscreenForTesting()
    {
        var width = 650;
        var height = 535;
        var pixel = new byte[width * height * 4];
        Array.Fill(pixel, (byte)0xFF);
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixel, width * 4);
        image.Freeze();

        var panelRect = new Rect(635, 272, width, height);
        var plan = BuildMinimizeSinkPlan(panelRect, new Rect(1353, 1032, 44, 48), MinimizeGhostPadding, MinimizeGhostPadding);
        var ghost = new StartupRevealGhostWindow(
            plan.GhostArea,
            panelRect,
            image,
            initialOpacity: 0.0,
            initialScale: plan.EndScale,
            initialOffsetY: 0.0);
        if (TryCreateSoftenedPanelImages(image, out var light, out var heavy))
        {
            ghost.SetSoftenedImages(light, heavy);
        }

        _ = ghost.MeasureStageOffscreenForTesting();
        ghost.PlayRestoreReveal(plan, () => { });
        return (ghost.AllowsTransparency, ghost.BodyRenderTransformOriginForTesting);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }
}
