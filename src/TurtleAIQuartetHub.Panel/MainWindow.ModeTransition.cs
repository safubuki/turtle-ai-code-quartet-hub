using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

// 標準・縮小・極小の切り替えアニメーション。
//
// 本物のウィンドウを毎フレーム SetWindowPos で動かすと、そのたびに Windows の再描画と WPF の再レイアウトが
// 走り、ガタガタと段階的に動いて見える。そこで本物のウィンドウは動かさず、次の順で見せる。
//   1. 今のパネルを画像に撮り、同じ位置に透明な「影武者」ウィンドウ（ModeMorphGhostWindow）を重ねて出す。
//   2. 影武者が画面に出たら、本物を DWM の cloak で見えなくし（ウィンドウは開いたまま・アクティブのまま）、
//      その間に新しい表示へ切り替えて最終の位置・大きさへ一度で動かし、新しいパネルも画像に撮る。
//   3. 影武者の中で、角丸の枠を GPU のアニメーションで古い形から新しい形へ小数点単位で滑らかに変形させ、
//      古い画像から新しい画像へクロスフェードする（画像は伸縮せず中央に置き、枠で切り抜くので文字がにじまない）。
//   4. 最後のコマは本物と同じ位置・見た目なので、本物の cloak を外してから影武者を閉じる（すき間のコマを作らない）。
// Windows の「アニメーションを表示する」がオフのとき、まだ表示していないとき、最小化中、cloak が使えない環境では
// 従来どおり一瞬で切り替える。
public partial class MainWindow
{
    private static readonly TimeSpan ModeMorphDuration = TimeSpan.FromMilliseconds(340);
    private static readonly TimeSpan ModeMorphOldFadeDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ModeMorphNewFadeDelay = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan ModeMorphGhostCloseDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan ModeMorphWatchdogSlack = TimeSpan.FromMilliseconds(1500);
    private const double ModeMorphImageScale = 0.97;
    private const int DwmwaCloak = 13;

    private bool _isModeTransitionRunning;
    private bool _isCollectingModeTargetBounds;
    private WindowArranger.WindowBounds? _modeTargetBounds;
    private DisplayMode? _queuedDisplayMode;
    private bool _queuedDisplayModeUpdateMessage;

    private void SetDisplayMode(DisplayMode mode, bool updateMessage = true)
    {
        // 切り替えの途中で押し直されたら、終わってから最後の指示だけを反映する。
        if (_isModeTransitionRunning)
        {
            _queuedDisplayMode = mode;
            _queuedDisplayModeUpdateMessage = updateMessage;
            return;
        }

        if (!CanAnimateModeTransition(mode))
        {
            SetDisplayModeCore(mode, updateMessage);
            return;
        }

        // 後始末は Finish の 1 か所に集め、何度呼ばれても 1 回だけ行う。どの経路で終わっても（失敗・見込み違い・
        // 完了の合図が来ない場合も）本物の cloak を必ず外し、未適用ならモードを適用し、影武者を閉じる。
        // 2026-11-19: 完了の合図を受け取れず cloak が外れないまま、影武者の最後のコマだけが残って
        // 一切操作できなくなった不具合があったため、時間切れの安全装置（watchdog）も付けている。
        var handle = new WindowInteropHelper(this).Handle;
        ModeMorphGhostWindow? ghost = null;
        var coreApplied = false;
        var completed = false;
        DispatcherTimer? watchdog = null;

        void Finish()
        {
            if (completed)
            {
                return;
            }

            completed = true;
            watchdog?.Stop();
            SetCloaked(handle, false);
            if (!coreApplied)
            {
                coreApplied = true;
                try
                {
                    SetDisplayModeCore(mode, updateMessage);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write(ex);
                }
            }

            var closingGhost = ghost;
            if (closingGhost is null)
            {
                FinishModeTransition();
                return;
            }

            // 本物が描かれたあとで影武者を閉じる（同じ見た目が重なっているだけなので、閉じる瞬間に変化は見えない）。
            var closeTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = ModeMorphGhostCloseDelay };
            closeTimer.Tick += (_, _) =>
            {
                closeTimer.Stop();
                try
                {
                    closingGhost.Close();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write(ex);
                }

                FinishModeTransition();
            };
            closeTimer.Start();
        }

        _isModeTransitionRunning = true;
        watchdog = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = ModeMorphDuration + ModeMorphWatchdogSlack };
        watchdog.Tick += (_, _) =>
        {
            DiagnosticLog.Write(LogLevel.Warn, "Display mode transition did not complete in time; forcing completion.");
            Finish();
        };
        watchdog.Start();

        try
        {
            var startRect = GetPanelScreenRect();
            var oldImage = CapturePanelImage();
            var ghostArea = GetModeMorphGhostArea(startRect, EstimateModeTargetRect(mode, startRect));
            ghost = new ModeMorphGhostWindow(ghostArea, startRect, oldImage);
            var shownGhost = ghost;
            shownGhost.ContentRendered += (_, _) =>
            {
                if (completed)
                {
                    return;
                }

                try
                {
                    // 影武者が画面に出てから本物を隠す（隠すのが先だと、何も映らないコマができる）。
                    if (!SetCloaked(handle, true))
                    {
                        Finish();
                        return;
                    }

                    coreApplied = true;
                    var targetRect = ApplyModeWhileCloaked(mode, updateMessage);
                    var newImage = CapturePanelImage();
                    if (!shownGhost.Contains(targetRect))
                    {
                        // 見込みより遠くへ動いたとき（画面端の補正など）は、変形せずに一瞬で見せる。
                        Finish();
                        return;
                    }

                    shownGhost.Morph(targetRect, newImage, Finish);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write(ex);
                    Finish();
                }
            };
            shownGhost.ShowGhost();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            Finish();
        }
    }

    // 隠している間に新しい表示へ切り替え、最終の位置・大きさへ一度で動かして、パネルの画面上の範囲を返す。
    private Rect ApplyModeWhileCloaked(DisplayMode mode, bool updateMessage)
    {
        _modeTargetBounds = null;
        _isCollectingModeTargetBounds = true;
        try
        {
            SetDisplayModeCore(mode, updateMessage);
        }
        finally
        {
            _isCollectingModeTargetBounds = false;
        }

        if (_modeTargetBounds is { } target)
        {
            // 非同期の移動（SWP_ASYNCWINDOWPOS）だと撮影までに反映されないので、ここは同期で動かす。
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(handle, IntPtr.Zero, target.Left, target.Top, Math.Max(1, target.Width), Math.Max(1, target.Height),
                SWP_NOZORDER | SWP_NOACTIVATE);
        }

        UpdateLayout();
        return GetPanelScreenRect();
    }

    // SetDisplayModeCore から呼ぶ。アニメーション中は目標だけ覚え、それ以外はすぐに動かす。
    private void ApplyModeTargetBounds(WindowArranger.WindowBounds bounds)
    {
        if (_isCollectingModeTargetBounds)
        {
            _modeTargetBounds = bounds;
            return;
        }

        SetWindowBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    private void FinishModeTransition()
    {
        _isModeTransitionRunning = false;
        RepositionStoredOverview();
        if (_queuedDisplayMode is not { } queued)
        {
            return;
        }

        _queuedDisplayMode = null;
        if (queued != _displayMode)
        {
            SetDisplayMode(queued, _queuedDisplayModeUpdateMessage);
        }
    }

    private bool CanAnimateModeTransition(DisplayMode mode)
    {
        return mode != _displayMode
            && IsLoaded
            && IsVisible
            && WindowState == WindowState.Normal
            && new WindowInteropHelper(this).Handle != IntPtr.Zero
            && PanelFrameBorder.ActualWidth > 0
            && SystemParameters.ClientAreaAnimation;
    }

    private static bool SetCloaked(IntPtr handle, bool cloaked)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var value = cloaked ? 1 : 0;
        return DwmSetWindowAttribute(handle, DwmwaCloak, ref value, sizeof(int)) >= 0;
    }

    // パネル（ウィンドウの中身全体）の画面上の範囲（物理 px）。
    private Rect GetPanelScreenRect()
    {
        var scale = VisualTreeHelper.GetDpi(this);
        var origin = PanelFrameBorder.PointToScreen(new Point());
        return new Rect(origin.X, origin.Y, PanelFrameBorder.ActualWidth * scale.DpiScaleX, PanelFrameBorder.ActualHeight * scale.DpiScaleY);
    }

    private BitmapSource CapturePanelImage()
    {
        var scale = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(PanelFrameBorder.ActualWidth * scale.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(PanelFrameBorder.ActualHeight * scale.DpiScaleY)),
            96 * scale.DpiScaleX,
            96 * scale.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(PanelFrameBorder);
        bitmap.Freeze();
        return bitmap;
    }

    // 影武者ウィンドウは変形の始めと終わりを両方含む範囲に一度だけ置く（途中で動かすとガタつくため）。
    // 終わりの位置はまだ決まっていないので、SetDisplayModeCore と同じ考え方（中心基準・極小は画面中央）で見込む。
    private Rect EstimateModeTargetRect(DisplayMode mode, Rect startRect)
    {
        var scale = VisualTreeHelper.GetDpi(this);
        double width;
        double height;
        switch (mode)
        {
            case DisplayMode.Micro:
                width = MicroWindowSize;
                height = MicroWindowSize;
                break;
            case DisplayMode.Compact:
            {
                var baseWidth = _standardWindowWidth > 0 ? _standardWindowWidth : startRect.Width / scale.DpiScaleX;
                width = Math.Max(CompactWindowMinWidth, Math.Round(baseWidth * CompactWindowWidthScale));
                height = 200;
                break;
            }
            default:
                width = _standardWindowWidth > 0 ? _standardWindowWidth : 650;
                height = _standardWindowHeight > 0 ? _standardWindowHeight : 535;
                break;
        }

        width *= scale.DpiScaleX;
        height *= scale.DpiScaleY;
        var handle = new WindowInteropHelper(this).Handle;
        var hasWorkArea = _windowArranger.TryGetMonitorWorkAreaForWindow(handle, out var workArea);
        double left;
        double top;
        if (mode == DisplayMode.Micro && hasWorkArea)
        {
            left = workArea.Left + (workArea.Width - width) / 2;
            top = workArea.Top + (workArea.Height - height) / 2;
        }
        else
        {
            left = startRect.Left + startRect.Width / 2 - width / 2;
            top = startRect.Top + startRect.Height / 2 - height / 2;
            if (hasWorkArea)
            {
                left = ClampToWorkArea(left, workArea.Left, workArea.Width, width);
                top = ClampToWorkArea(top, workArea.Top, workArea.Height, height);
            }
        }

        return new Rect(left, top, width, height);
    }

    // 始めと見込みの終わりを含み、影と見込み違いのぶん少し広げた範囲（物理 px）。
    private Rect GetModeMorphGhostArea(Rect startRect, Rect estimatedTarget)
    {
        var scale = VisualTreeHelper.GetDpi(this);
        var area = Rect.Union(startRect, estimatedTarget);
        area.Inflate(48 * scale.DpiScaleX, 48 * scale.DpiScaleY);
        return area;
    }

    // 透明・クリック透過・非アクティブの最前面ウィンドウ。中の角丸の枠だけを GPU のアニメーションで動かす。
    private sealed class ModeMorphGhostWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const uint SwpNoActivateFlag = 0x0010;
        private static readonly IntPtr HwndTopmost = new(-1);

        private readonly Rect _area;
        private readonly Rect _startRect;
        private readonly Canvas _canvas = new();
        private readonly Grid _shape = new();
        private readonly Border _oldLayer;
        private readonly Border _newLayer;
        private readonly ScaleTransform _oldScale = new(1, 1);
        private readonly ScaleTransform _newScale = new(ModeMorphImageScale, ModeMorphImageScale);
        private DpiScale _dpi = new(1, 1);

        public ModeMorphGhostWindow(Rect area, Rect startRect, BitmapSource oldImage)
        {
            _area = area;
            _startRect = startRect;
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

            // 本物のウィンドウと同じ地の色・角丸・枠の色。影はやわらかく薄めに付ける。
            var plate = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x11)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x38, 0x32)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.4, Color = Colors.Black }
            };
            _oldLayer = CreateImageLayer(oldImage, _oldScale);
            _newLayer = CreateImageLayer(null, _newScale);
            _newLayer.Opacity = 0;
            _shape.Children.Add(plate);
            _shape.Children.Add(_oldLayer);
            _shape.Children.Add(_newLayer);
            _canvas.Children.Add(_shape);
            Content = _canvas;
        }

        public void ShowGhost()
        {
            var handle = new WindowInteropHelper(this).EnsureHandle();
            var exStyle = GetWindowLong(handle, GwlExStyle);
            SetWindowLong(handle, GwlExStyle, exStyle | WsExTransparent | WsExToolWindow | WsExNoActivate);
            PlaceWindow(handle);
            // 置いた先のディスプレイの倍率で中身を合わせ直す（倍率が変わると WPF が大きさを変えることがあるので置き直す）。
            _dpi = VisualTreeHelper.GetDpi(this);
            PlaceWindow(handle);
            var start = ToLocal(_startRect);
            SetShapeRect(start);
            Show();
        }

        public bool Contains(Rect screenRect)
        {
            return _area.Contains(screenRect);
        }

        public void Morph(Rect targetScreenRect, BitmapSource newImage, Action onCompleted)
        {
            ((ImageBrush)_newLayer.Background).ImageSource = newImage;
            var from = ToLocal(_startRect);
            var to = ToLocal(targetScreenRect);
            var duration = new Duration(ModeMorphDuration);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            DoubleAnimation Geometry(double a, double b) => new(a, b, duration) { EasingFunction = ease };
            var left = Geometry(from.Left, to.Left);
            // 完了の合図は BeginAnimation より前に付ける。WPF はアニメーション開始時に時計を作り、その時点の
            // Completed の登録だけを引き継ぐため、後から付けると呼ばれない（cloak が外れず操作不能になった原因）。
            left.Completed += (_, _) => onCompleted();
            _shape.BeginAnimation(Canvas.LeftProperty, left);
            _shape.BeginAnimation(Canvas.TopProperty, Geometry(from.Top, to.Top));
            _shape.BeginAnimation(WidthProperty, Geometry(from.Width, to.Width));
            _shape.BeginAnimation(HeightProperty, Geometry(from.Height, to.Height));

            // 古い画像は少し縮みながら先に消え、新しい画像は少し小さい状態から遅れて広がりながら現れる。
            var oldFade = new DoubleAnimation(1, 0, new Duration(ModeMorphOldFadeDuration)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            var oldShrink = new DoubleAnimation(1, ModeMorphImageScale, duration) { EasingFunction = ease };
            var newDuration = new Duration(ModeMorphDuration - ModeMorphNewFadeDelay);
            var newFade = new DoubleAnimation(0, 1, newDuration) { BeginTime = ModeMorphNewFadeDelay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var newGrow = new DoubleAnimation(ModeMorphImageScale, 1, newDuration) { BeginTime = ModeMorphNewFadeDelay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            _oldLayer.BeginAnimation(OpacityProperty, oldFade);
            _oldScale.BeginAnimation(ScaleTransform.ScaleXProperty, oldShrink);
            _oldScale.BeginAnimation(ScaleTransform.ScaleYProperty, oldShrink);
            _newLayer.BeginAnimation(OpacityProperty, newFade);
            _newScale.BeginAnimation(ScaleTransform.ScaleXProperty, newGrow);
            _newScale.BeginAnimation(ScaleTransform.ScaleYProperty, newGrow);
        }

        // 画像は伸び縮みさせず（文字がにじまないように）中央に置き、角丸の枠で切り抜く。
        private static Border CreateImageLayer(BitmapSource? image, ScaleTransform scale)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new ImageBrush(image)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center,
                    RelativeTransform = new TransformGroup { Children = { new TranslateTransform(-0.5, -0.5), scale, new TranslateTransform(0.5, 0.5) } }
                }
            };
        }

        private void SetShapeRect(Rect rect)
        {
            Canvas.SetLeft(_shape, rect.Left);
            Canvas.SetTop(_shape, rect.Top);
            _shape.Width = rect.Width;
            _shape.Height = rect.Height;
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
            SetWindowPos(handle, HwndTopmost,
                (int)Math.Floor(_area.Left), (int)Math.Floor(_area.Top),
                (int)Math.Ceiling(_area.Width), (int)Math.Ceiling(_area.Height),
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
