using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

// アプリ本体（メインパネル）の起動・終了アニメーション。
//
// 確実に表示され、かつコマ飛びしない設計（ModeMorphGhostWindow 直系の超軽量構成）:
//   - WPF の AllowsTransparency=False ウィンドウへ Win32 SetWindowLong(WS_EX_LAYERED) を後から
//     付けても WPF 内部フックに剥ぎ取られるため、縮小・拡大アニメーション（ModeMorphGhostWindow）で
//     実績のある「本物を DWMWA_CLOAK で隠しつつ、AllowsTransparency=True の超シンプル影武者上で
//     WPF レンダースレッド独立クロックの DoubleAnimation を走らせる」方式を採用する。
//   - 過去にカクついた原因（BitmapCache への毎フレーム拡大縮小、HighQuality フィルタ、多重 Blur/リング、
//     起動直後の待機不足と _refreshTimer 競合）を完全撤去し、構成を「プレート＋パネル画像＋1px 縁」の
//     最小 3 枚に絞る。
//   - 起動時は ContentRendered 後に 120ms 落ち着かせ、さらに影武者表示後 70ms 待ってから
//     920ms の S 字カーブ（SineEase EaseInOut）で余裕を持ってふわっと浮かび上がらせる。
//   - 終了時も確認オーバーレイ消去後の再描画が確定してから影武者へ隙間なく引き継ぎ、
//     60ms 安定させてから 680ms の S 字カーブで静かに消え失せる。
public partial class MainWindow
{
    private static readonly TimeSpan StartupWarmupDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan StartupGhostSettleDelay = TimeSpan.FromMilliseconds(70);
    private static readonly TimeSpan StartupRevealDuration = TimeSpan.FromMilliseconds(920);
    private static readonly TimeSpan StartupRevealGhostCloseDelay = TimeSpan.FromMilliseconds(70);
    private static readonly TimeSpan StartupRevealPreStartTimeout = TimeSpan.FromMilliseconds(2000);
    private static readonly TimeSpan StartupRevealWatchdogSlack = TimeSpan.FromMilliseconds(1600);

    private static readonly TimeSpan ExitOverlayClearSettleDelay = TimeSpan.FromMilliseconds(70);
    private static readonly TimeSpan ExitGhostSettleDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan ExitFadeDuration = TimeSpan.FromMilliseconds(680);
    private static readonly TimeSpan ExitWatchdogSlack = TimeSpan.FromMilliseconds(1200);

    private const double StartupRevealStartScale = 0.965;
    private const double StartupRevealStartOffsetY = 12.0;
    private const double ExitVanishEndScale = 0.965;
    private const double ExitVanishEndOffsetY = 10.0;
    private const double StartupGhostPadding = 22.0;

    private bool _isStartupCloakArmed;
    private bool _isStartupRevealRunning;
    private bool _hasStartupRevealFinished;
    private bool _isExitAnimationRunning;
    private Action? _forceFinishStartupReveal;
    private DispatcherTimer? _startupPreStartWatchdog;
    private TaskCompletionSource<bool>? _startupRevealCompletionSource;

    // Loaded 内の重い非同期初期化や定期リフレッシュタイマーを起動演出完了後へ譲り、
    // アニメーション中の UI スレッド詰まりによるコマ飛びを防ぐ。
    private async Task WaitForStartupRevealIfActiveAsync()
    {
        var tcs = _startupRevealCompletionSource;
        if (tcs is null || tcs.Task.IsCompleted)
        {
            return;
        }

        _ = await Task.WhenAny(
            tcs.Task,
            Task.Delay(StartupWarmupDelay + StartupGhostSettleDelay + StartupRevealDuration + TimeSpan.FromMilliseconds(600)));
    }

    // OnSourceInitialized から呼ぶ。本物のウィンドウが 1 フレームも表示される前に
    // DWMWA_CLOAK と遷移無効化を掛けて、OS 既定の出現を隠しておく。
    private void ArmStartupRevealCloak(IntPtr handle)
    {
        if (_hasStartupRevealFinished || !CanUseStartupRevealAnimation(handle))
        {
            _hasStartupRevealFinished = true;
            return;
        }

        if (!SetCloaked(handle, true))
        {
            DiagnosticLog.Write(LogLevel.Warn, "Startup reveal could not cloak main window; falling back to instant show.");
            _hasStartupRevealFinished = true;
            return;
        }

        WindowArranger.SetTransitionsDisabled(handle, true);
        _isStartupCloakArmed = true;
        _startupRevealCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _startupPreStartWatchdog = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = StartupRevealPreStartTimeout
        };
        _startupPreStartWatchdog.Tick += (_, _) =>
        {
            StopStartupPreStartWatchdog();
            if (!_isStartupRevealRunning && !_hasStartupRevealFinished)
            {
                DiagnosticLog.Write(LogLevel.Warn, "Startup reveal did not start in time; releasing startup cloak.");
                ReleaseStartupCloakFallback(handle);
            }
        };
        _startupPreStartWatchdog.Start();
    }

    // ContentRendered（初回描画完了時）から 1 回だけ呼ぶ。
    private async void BeginStartupRevealIfArmed()
    {
        StopStartupPreStartWatchdog();
        if (!_isStartupCloakArmed || _isStartupRevealRunning || _hasStartupRevealFinished)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero
            || !IsVisible
            || WindowState != WindowState.Normal
            || PanelFrameBorder.ActualWidth <= 0
            || PanelFrameBorder.ActualHeight <= 0)
        {
            ReleaseStartupCloakFallback(handle);
            return;
        }

        StartupRevealGhostWindow? ghost = null;
        DispatcherTimer? watchdog = null;
        var completed = false;

        void Finish(bool closeGhostImmediately)
        {
            if (completed)
            {
                return;
            }

            completed = true;
            watchdog?.Stop();
            _isStartupCloakArmed = false;
            _isStartupRevealRunning = false;
            _hasStartupRevealFinished = true;
            _forceFinishStartupReveal = null;

            SetCloaked(handle, false);
            WindowArranger.SetTransitionsDisabled(handle, false);
            _startupRevealCompletionSource?.TrySetResult(true);

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

            // 本物の cloak が外れて 1〜2 フレーム描かれたあとで影武者を閉じる（隙間・チラつきゼロ）。
            var closeTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = StartupRevealGhostCloseDelay
            };
            closeTimer.Tick += (_, _) =>
            {
                closeTimer.Stop();
                SafeCloseStartupGhost(closingGhost);
            };
            closeTimer.Start();
        }

        _isStartupRevealRunning = true;
        _forceFinishStartupReveal = () => Finish(closeGhostImmediately: true);

        watchdog = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = StartupWarmupDelay + StartupGhostSettleDelay + StartupRevealDuration + StartupRevealWatchdogSlack
        };
        watchdog.Tick += (_, _) =>
        {
            DiagnosticLog.Write(LogLevel.Warn, "Startup reveal animation did not complete in time; forcing completion.");
            Finish(closeGhostImmediately: true);
        };
        watchdog.Start();

        try
        {
            // 初回レイアウト・DirectX 初期化・JIT が落ち着くまで一呼吸待ってから影武者を出す。
            await Task.Delay(StartupWarmupDelay);
            if (completed || !IsVisible || WindowState != WindowState.Normal)
            {
                Finish(closeGhostImmediately: true);
                return;
            }

            UpdateLayout();
            var panelRect = GetPanelScreenRect();
            if (panelRect.Width <= 0 || panelRect.Height <= 0)
            {
                Finish(closeGhostImmediately: true);
                return;
            }

            var panelImage = CapturePanelImage();
            var scale = VisualTreeHelper.GetDpi(this);
            var ghostArea = panelRect;
            ghostArea.Inflate(StartupGhostPadding * scale.DpiScaleX, StartupGhostPadding * scale.DpiScaleY);

            ghost = new StartupRevealGhostWindow(
                ghostArea,
                panelRect,
                panelImage,
                initialOpacity: 0.0,
                initialScale: StartupRevealStartScale,
                initialOffsetY: StartupRevealStartOffsetY);
            var shownGhost = ghost;
            shownGhost.ContentRendered += async (_, _) =>
            {
                if (completed)
                {
                    return;
                }

                try
                {
                    // 影武者の初回描画パイプラインが 100% 立ち上がるのを待ってから再生し、冒頭コマ飛びを防ぐ。
                    await Task.Delay(StartupGhostSettleDelay);
                    if (completed)
                    {
                        return;
                    }

                    shownGhost.PlayReveal(() => Finish(closeGhostImmediately: false));
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

    // ユーザー確認でアプリ本体を閉じるとき、確認ダイアログが消えた綺麗な状態を
    // 超軽量影武者へ隙間なく引き継ぎ、680ms かけて余裕を持ってふわっと消え失せる。
    private async Task ClosePanelWithVanishAnimationAsync()
    {
        if (_isExitAnimationRunning)
        {
            return;
        }

        _isExitAnimationRunning = true;
        CompleteStartupRevealImmediately();
        CompleteRestoreRevealImmediately();

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero
            || !IsVisible
            || WindowState != WindowState.Normal
            || !SystemParameters.ClientAreaAnimation
            || PanelFrameBorder.ActualWidth <= 0
            || PanelFrameBorder.ActualHeight <= 0)
        {
            Close();
            return;
        }

        StartupRevealGhostWindow? ghost = null;
        try
        {
            // 終了確認オーバーレイを非表示にした直後の画面更新を確定させてから撮影する。
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(ExitOverlayClearSettleDelay);

            UpdateLayout();
            var panelRect = GetPanelScreenRect();
            if (panelRect.Width <= 0 || panelRect.Height <= 0)
            {
                Close();
                return;
            }

            var panelImage = CapturePanelImage();
            var scale = VisualTreeHelper.GetDpi(this);
            var ghostArea = panelRect;
            ghostArea.Inflate(StartupGhostPadding * scale.DpiScaleX, StartupGhostPadding * scale.DpiScaleY);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ghost = new StartupRevealGhostWindow(
                ghostArea,
                panelRect,
                panelImage,
                initialOpacity: 1.0,
                initialScale: 1.0,
                initialOffsetY: 0.0);
            var shownGhost = ghost;
            shownGhost.ContentRendered += async (_, _) =>
            {
                try
                {
                    // 影武者が本物と完全一致して描画された瞬間に本物を隠し、隙間ゼロで引き継ぐ。
                    SetCloaked(handle, true);
                    WindowArranger.SetTransitionsDisabled(handle, true);

                    await Task.Delay(ExitGhostSettleDelay);
                    if (tcs.Task.IsCompleted)
                    {
                        return;
                    }

                    shownGhost.PlayVanish(() => tcs.TrySetResult(true));
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write(ex);
                    tcs.TrySetResult(true);
                }
            };
            shownGhost.ShowGhost();

            _ = await Task.WhenAny(
                tcs.Task,
                Task.Delay(ExitGhostSettleDelay + ExitFadeDuration + ExitWatchdogSlack));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
        finally
        {
            if (ghost is not null)
            {
                SafeCloseStartupGhost(ghost);
            }

            Close();
        }
    }

    private void CompleteStartupRevealImmediately()
    {
        StopStartupPreStartWatchdog();
        if (_forceFinishStartupReveal is not null)
        {
            _forceFinishStartupReveal();
            return;
        }

        if (_isStartupCloakArmed)
        {
            var handle = new WindowInteropHelper(this).Handle;
            ReleaseStartupCloakFallback(handle);
        }
    }

    private void ReleaseStartupCloakFallback(IntPtr handle)
    {
        StopStartupPreStartWatchdog();
        _isStartupCloakArmed = false;
        _isStartupRevealRunning = false;
        _hasStartupRevealFinished = true;
        _forceFinishStartupReveal = null;
        _startupRevealCompletionSource?.TrySetResult(true);
        if (handle != IntPtr.Zero)
        {
            SetCloaked(handle, false);
            WindowArranger.SetTransitionsDisabled(handle, false);
        }
    }

    private void StopStartupPreStartWatchdog()
    {
        if (_startupPreStartWatchdog is not null)
        {
            _startupPreStartWatchdog.Stop();
            _startupPreStartWatchdog = null;
        }
    }

    private bool CanUseStartupRevealAnimation(IntPtr handle)
    {
        return handle != IntPtr.Zero
            && WindowState == WindowState.Normal
            && SystemParameters.ClientAreaAnimation;
    }

    private static void SafeCloseStartupGhost(Window ghost)
    {
        try
        {
            ghost.Close();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    // テスト専用の無菌検証ヘルパー。画面へ Show() せずに影武者のビジュアルツリー・計測サイズ・
    // AllowsTransparency 状態および起動／終了タイムライン生成を検証する。
    internal static (Size StageSize, bool AllowsTransparency) VerifyStartupGhostOffscreenForTesting()
    {
        var pixel = new byte[4 * 4 * 4];
        var image = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Pbgra32, null, pixel, 16);
        image.Freeze();

        var panelRect = new Rect(100, 100, 650, 535);
        var ghostArea = panelRect;
        ghostArea.Inflate(StartupGhostPadding, StartupGhostPadding);

        var ghost = new StartupRevealGhostWindow(
            ghostArea,
            panelRect,
            image,
            initialOpacity: 0.0,
            initialScale: StartupRevealStartScale,
            initialOffsetY: StartupRevealStartOffsetY);
        var stageSize = ghost.MeasureStageOffscreenForTesting();
        ghost.PlayReveal(() => { });
        ghost.PlayVanish(() => { });
        return (stageSize, ghost.AllowsTransparency);
    }

    // 動き始めと終わりの速度が滑らかに 0 へ繋がる優雅な S 字フェード曲線（テスト対象）。
    internal static double EvaluateSmoothFadeProgress(double linearProgress)
    {
        var t = Math.Clamp(linearProgress, 0.0, 1.0);
        return 0.5 * (1.0 - Math.Cos(Math.PI * t));
    }

    // ModeMorphGhostWindow と同じ超軽量・最小 3 レイヤー構成の透過影武者ウィンドウ。
    // BlurEffect / BitmapCache / HighQuality スケーリングを一切使わず、WPF の GPU 独立クロックで滑らかに駆動する。
    private sealed class StartupRevealGhostWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const uint SwpNoActivateFlag = 0x0010;
        private static readonly IntPtr HwndTopmost = new(-1);

        private readonly Rect _area;
        private readonly Rect _panelRect;
        private readonly Canvas _canvas = new();
        private readonly Grid _stage = new();
        private readonly Border _bodyContainer;
        private readonly Grid _crispLayer = new();
        private readonly Border _edgeGlow;
        // 最小化のときだけ使う、あらかじめぼかしたパネル画像（軽い／強い）。くっきりした層の後ろに重ね、
        // 入れ替えていくことで輪郭ごと空気に溶けるように見せる（毎フレームの BlurEffect は使わない）。
        private readonly Border _lightSoftLayer = CreateSoftLayer();
        private readonly Border _heavySoftLayer = CreateSoftLayer();
        private SoftenedPanelImage? _lightSoftImage;
        private SoftenedPanelImage? _heavySoftImage;
        private readonly ScaleTransform _bodyScale;
        private readonly TranslateTransform _bodyTranslate;
        private DpiScale _dpi = new(1, 1);

        public StartupRevealGhostWindow(
            Rect area,
            Rect panelRect,
            BitmapSource panelImage,
            double initialOpacity,
            double initialScale,
            double initialOffsetY,
            double cornerRadius = 8.0)
        {
            _area = area;
            _panelRect = panelRect;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            IsHitTestVisible = false;
            UseLayoutRounding = false;
            SnapsToDevicePixels = false;

            _bodyScale = new ScaleTransform(initialScale, initialScale);
            _bodyTranslate = new TranslateTransform(0, initialOffsetY);

            // 1. ベースプレート（ModeMorphGhostWindow と同じシンプルな地色・角丸・枠）
            var plate = new Border
            {
                Background = CreateFrozenSolid(Color.FromRgb(0x10, 0x12, 0x11)),
                BorderBrush = CreateFrozenSolid(Color.FromRgb(0x2E, 0x38, 0x32)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(cornerRadius)
            };

            // 2. パネル画像本体（HighQuality フィルタ・BitmapCache 不使用で軽快・鮮明）
            var imageBrush = new ImageBrush(panelImage)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            imageBrush.Freeze();
            var imageLayer = new Border
            {
                CornerRadius = new CornerRadius(cornerRadius),
                Background = imageBrush
            };

            // 3. さりげない AI ツールらしさを添える 1.5px のほのかなエメラルド縁枠（1 枚のみ）
            _edgeGlow = new Border
            {
                CornerRadius = new CornerRadius(cornerRadius),
                BorderBrush = CreateFrozenSolid(Color.FromRgb(0x5E, 0xE3, 0x98)),
                BorderThickness = new Thickness(1.5),
                Background = Brushes.Transparent,
                Opacity = 0.0
            };

            _crispLayer.Children.Add(plate);
            _crispLayer.Children.Add(imageLayer);
            _crispLayer.Children.Add(_edgeGlow);

            // 奥から「強いぼかし → 軽いぼかし → くっきり」の順に重ねる（ぼかし層は最小化のときだけ表示する）。
            var contentGrid = new Grid();
            contentGrid.Children.Add(_heavySoftLayer);
            contentGrid.Children.Add(_lightSoftLayer);
            contentGrid.Children.Add(_crispLayer);

            var transformGroup = new TransformGroup();
            transformGroup.Children.Add(_bodyScale);
            transformGroup.Children.Add(_bodyTranslate);

            _bodyContainer = new Border
            {
                Opacity = initialOpacity,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = transformGroup,
                Child = contentGrid
            };

            _stage.Children.Add(_bodyContainer);
            _canvas.Children.Add(_stage);
            Content = _canvas;
        }

        public Size MeasureStageOffscreenForTesting()
        {
            var local = ToLocal(_panelRect);
            Canvas.SetLeft(_stage, local.Left);
            Canvas.SetTop(_stage, local.Top);
            _stage.Width = local.Width;
            _stage.Height = local.Height;
            ApplySoftLayerMargins(local.Width, local.Height);
            _stage.Measure(new Size(local.Width, local.Height));
            _stage.Arrange(new Rect(0, 0, local.Width, local.Height));
            return _stage.DesiredSize;
        }

        // テスト用: ぼかし層（強い）の配置後の大きさ。輪郭の外側へにじむ分だけパネルより大きくなる。
        public Size HeavySoftLayerRenderSizeForTesting => _heavySoftLayer.RenderSize;

        // 最小化の前に呼ぶ。ぼかし画像は ShowGhost で実寸が決まったときに、外側へにじむ分だけ広げて置く。
        public void SetSoftenedImages(SoftenedPanelImage light, SoftenedPanelImage heavy)
        {
            _lightSoftImage = light;
            _heavySoftImage = heavy;
            _lightSoftLayer.Background = CreateFrozenImageBrush(light.Image);
            _heavySoftLayer.Background = CreateFrozenImageBrush(heavy.Image);
            _lightSoftLayer.Visibility = Visibility.Visible;
            _heavySoftLayer.Visibility = Visibility.Visible;
        }

        private void ApplySoftLayerMargins(double stageWidth, double stageHeight)
        {
            if (_lightSoftImage is { } light)
            {
                _lightSoftLayer.Margin = CreateBleedMargin(light, stageWidth, stageHeight);
            }

            if (_heavySoftImage is { } heavy)
            {
                _heavySoftLayer.Margin = CreateBleedMargin(heavy, stageWidth, stageHeight);
            }
        }

        private static Thickness CreateBleedMargin(SoftenedPanelImage image, double stageWidth, double stageHeight)
        {
            var x = stageWidth * image.MarginFractionX;
            var y = stageHeight * image.MarginFractionY;
            return new Thickness(-x, -y, -x, -y);
        }

        private static Border CreateSoftLayer()
        {
            return new Border
            {
                Opacity = 0.0,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
        }

        private static ImageBrush CreateFrozenImageBrush(BitmapSource image)
        {
            // 縮小してぼかした画像を引き伸ばして使う（ぼけているので引き伸ばしても粗さは見えない）。
            var brush = new ImageBrush(image) { Stretch = Stretch.Fill };
            brush.Freeze();
            return brush;
        }

        public void ShowGhost()
        {
            var handle = new WindowInteropHelper(this).EnsureHandle();
            var exStyle = GetWindowLong(handle, GwlExStyle);
            // AllowsTransparency=True で WPF が付与済みの WS_EX_LAYERED を保ったままクリックスルー属性を足す。
            _ = SetWindowLong(handle, GwlExStyle, exStyle | WsExTransparent | WsExToolWindow | WsExNoActivate);
            PlaceWindow(handle);
            _dpi = VisualTreeHelper.GetDpi(this);
            PlaceWindow(handle);

            var local = ToLocal(_panelRect);
            Canvas.SetLeft(_stage, local.Left);
            Canvas.SetTop(_stage, local.Top);
            _stage.Width = local.Width;
            _stage.Height = local.Height;
            ApplySoftLayerMargins(local.Width, local.Height);
            Show();
        }

        public void PlayReveal(Action onCompleted)
        {
            var duration = new Duration(StartupRevealDuration);
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

            var fade = new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = ease };
            // Completed は必ず BeginAnimation より前に登録する。
            fade.Completed += (_, _) => onCompleted();

            _bodyContainer.BeginAnimation(OpacityProperty, fade);
            _bodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(StartupRevealStartScale, 1.0, duration) { EasingFunction = ease });
            _bodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(StartupRevealStartScale, 1.0, duration) { EasingFunction = ease });
            _bodyTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(StartupRevealStartOffsetY, 0.0, duration) { EasingFunction = ease });

            var edgePulse = new DoubleAnimationUsingKeyFrames();
            edgePulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            edgePulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.72, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360))) { EasingFunction = ease });
            edgePulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(StartupRevealDuration)) { EasingFunction = ease });
            _edgeGlow.BeginAnimation(OpacityProperty, edgePulse);
        }

        public void PlayVanish(Action onCompleted)
        {
            var duration = new Duration(ExitFadeDuration);
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

            var fade = new DoubleAnimation(1.0, 0.0, duration) { EasingFunction = ease };
            // Completed は必ず BeginAnimation より前に登録する。
            fade.Completed += (_, _) => onCompleted();

            _bodyContainer.BeginAnimation(OpacityProperty, fade);
            _bodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, ExitVanishEndScale, duration) { EasingFunction = ease });
            _bodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, ExitVanishEndScale, duration) { EasingFunction = ease });
            _bodyTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, ExitVanishEndOffsetY, duration) { EasingFunction = ease });

            var edgePulse = new DoubleAnimationUsingKeyFrames();
            edgePulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            edgePulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.45, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))) { EasingFunction = ease });
            edgePulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(ExitFadeDuration)) { EasingFunction = ease });
            _edgeGlow.BeginAnimation(OpacityProperty, edgePulse);
        }

        // 最小化: パネル全体がタスクバーのボタンとほぼ同じ大きさまで縮みながら、ボタンの中心へ吸い寄せられて消える。
        // 縮小の基準はパネルの中央、移動はパネル中心→ボタン中心。移動と縮みを同じ曲線で進めるので、
        // パネルの四隅がそろってボタンへ集まっていくように見える（横幅だけ少し先に細らせて吸い口の形にする）。
        // 飛びながら「くっきり → 軽くぼけ → 強くぼけ」と重ねた画像を入れ替えて輪郭ごと空気に溶かし、
        // ボタンへ届く手前（道のりの約 9 割）で消えきる。
        public void PlayMinimizeSink(MinimizeSinkPlan plan, Action onCompleted)
        {
            // 計画の移動量は物理 px なので、影武者を置いたディスプレイの倍率で DIP へ直す。
            var travel = new Vector(plan.Travel.X / _dpi.DpiScaleX, plan.Travel.Y / _dpi.DpiScaleY);

            _bodyContainer.RenderTransformOrigin = new Point(0.5, 0.5);

            var fadeEnd = TimeAt(MinimizeSinkDuration, MinimizeFadeEndRatio);
            var fade = new DoubleAnimationUsingKeyFrames { Duration = new Duration(fadeEnd) };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new SplineDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(fadeEnd), MinimizeFadeSpline));
            // Completed は必ず BeginAnimation より前に登録する。消えきった時点で終わる（残りの移動は見えないため待たない）。
            fade.Completed += (_, _) => onCompleted();

            var duration = MinimizeSinkDuration;
            _bodyContainer.BeginAnimation(OpacityProperty, fade);
            _bodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, BuildSplineTimeline(1.0, plan.EndScale, MinimizeNarrowSpline, duration));
            _bodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, BuildSplineTimeline(1.0, plan.EndScale, MinimizeTravelSpline, duration));
            _bodyTranslate.BeginAnimation(TranslateTransform.XProperty, BuildSplineTimeline(0.0, travel.X, MinimizeTravelSpline, duration));
            _bodyTranslate.BeginAnimation(TranslateTransform.YProperty, BuildSplineTimeline(0.0, travel.Y, MinimizeTravelSpline, duration));

            // ぼかし画像を用意できなかったときは、くっきりした層のまま全体の薄れだけで消す。
            if (_lightSoftImage is null || _heavySoftImage is null)
            {
                return;
            }

            // くっきりした層（枠・画像）は早めに引き、後ろの軽いぼかし→強いぼかしへ受け渡す。
            _crispLayer.BeginAnimation(OpacityProperty, BuildBlendTimeline(duration, MinimizeCrispBlend));
            _lightSoftLayer.BeginAnimation(OpacityProperty, BuildBlendTimeline(duration, MinimizeLightSoftBlend));
            _heavySoftLayer.BeginAnimation(OpacityProperty, BuildBlendTimeline(duration, MinimizeHeavySoftBlend));
        }

        // 復元: 最小化をそっくり逆に再生する。タスクバーのボタンから、強くぼけたもやが集まるように現れ、
        // 軽いぼかし→くっきりへ戻りながら元の大きさへ広がって、元の位置にふわっと収まる。
        // 最後のコマは本物と同じ位置・同じ見た目（くっきりした層だけが不透明）なので、呼び出し側は完了後に
        // 本物の cloak を外してから影武者を閉じる。
        public void PlayRestoreReveal(MinimizeSinkPlan plan, Action onCompleted)
        {
            var duration = RestoreRevealDuration;
            var travel = new Vector(plan.Travel.X / _dpi.DpiScaleX, plan.Travel.Y / _dpi.DpiScaleY);

            _bodyContainer.RenderTransformOrigin = new Point(0.5, 0.5);

            // 押した直後から待たせないよう、最小化の「消えきったあとの区間」は逆再生せず、はじめからボタン付近でふわっと現れ始める。
            var appear = new DoubleAnimationUsingKeyFrames { Duration = new Duration(duration) };
            appear.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            appear.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(duration), RestoreFadeSpline));

            var travelX = BuildSplineTimeline(travel.X, 0.0, RestoreTravelSpline, duration);
            // Completed は必ず BeginAnimation より前に登録する（いちばん長い移動の終わり＝元の位置に収まった時点）。
            travelX.Completed += (_, _) => onCompleted();

            _bodyContainer.BeginAnimation(OpacityProperty, appear);
            _bodyScale.BeginAnimation(ScaleTransform.ScaleXProperty, BuildSplineTimeline(plan.EndScale, 1.0, RestoreNarrowSpline, duration));
            _bodyScale.BeginAnimation(ScaleTransform.ScaleYProperty, BuildSplineTimeline(plan.EndScale, 1.0, RestoreTravelSpline, duration));
            _bodyTranslate.BeginAnimation(TranslateTransform.XProperty, travelX);
            _bodyTranslate.BeginAnimation(TranslateTransform.YProperty, BuildSplineTimeline(travel.Y, 0.0, RestoreTravelSpline, duration));

            if (_lightSoftImage is null || _heavySoftImage is null)
            {
                return;
            }

            _crispLayer.BeginAnimation(OpacityProperty, BuildBlendTimeline(duration, MirrorBlendPoints(MinimizeCrispBlend)));
            _lightSoftLayer.BeginAnimation(OpacityProperty, BuildBlendTimeline(duration, MirrorBlendPoints(MinimizeLightSoftBlend)));
            _heavySoftLayer.BeginAnimation(OpacityProperty, BuildBlendTimeline(duration, MirrorBlendPoints(MinimizeHeavySoftBlend)));
        }

        // from→to を、指定の時間で指定の曲線（CSS の cubic-bezier と同じ KeySpline）に沿って動かす。
        private static DoubleAnimationUsingKeyFrames BuildSplineTimeline(double from, double to, KeySpline spline, TimeSpan duration)
        {
            var animation = new DoubleAnimationUsingKeyFrames { Duration = new Duration(duration) };
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(duration), spline));
            return animation;
        }

        // （全体時間に対する割合, 不透明度）の点を、なめらかな S 字（MinimizeBlendSpline）でつなぐ。
        private static DoubleAnimationUsingKeyFrames BuildBlendTimeline(TimeSpan duration, (double Ratio, double Value)[] points)
        {
            var animation = new DoubleAnimationUsingKeyFrames { Duration = new Duration(duration) };
            foreach (var (ratio, value) in points)
            {
                var keyTime = KeyTime.FromTimeSpan(TimeAt(duration, ratio));
                animation.KeyFrames.Add(ratio <= 0.0
                    ? new LinearDoubleKeyFrame(value, keyTime)
                    : new SplineDoubleKeyFrame(value, keyTime, MinimizeBlendSpline));
            }

            return animation;
        }

        public Point BodyRenderTransformOriginForTesting => _bodyContainer.RenderTransformOrigin;

        private static SolidColorBrush CreateFrozenSolid(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private Rect ToLocal(Rect screenRect)
        {
            return new Rect(
                (screenRect.Left - _area.Left) / _dpi.DpiScaleX,
                (screenRect.Top - _area.Top) / _dpi.DpiScaleY,
                screenRect.Width / _dpi.DpiScaleX,
                screenRect.Height / _dpi.DpiScaleY);
        }

        private void PlaceWindow(IntPtr handle)
        {
            SetWindowPos(
                handle,
                HwndTopmost,
                (int)Math.Floor(_area.Left),
                (int)Math.Floor(_area.Top),
                (int)Math.Ceiling(_area.Width),
                (int)Math.Ceiling(_area.Height),
                SwpNoActivateFlag);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    }
}
