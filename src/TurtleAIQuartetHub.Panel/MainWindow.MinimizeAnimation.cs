using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

// アプリ本体（メインパネル）の最小化アニメーション。
//
// WindowStyle=None のパネルは OS 既定の最小化アニメーションが付かず「カチッ」と消えてしまうため、
// 起動・終了演出と同じ超軽量の透過影武者（StartupRevealGhostWindow）で、タスクバーのこのアプリのボタンへ
// 縮みながら吸い込まれる動きを見せる。
//   1. タスクバー上のこのアプリのボタンの位置を UI オートメーションで調べる（最小化ボタンにマウスが乗った時点で
//      先読みしておくので、普段は待ち時間なし）。見つからなければタスクバーの帯の上の代わりの位置へ向かう。
//   2. 今のパネルを画像に撮り、同じ位置に影武者を重ねて出す（クリック透過・非アクティブ・最前面）。
//   3. 影武者が本物とぴったり重なって描かれた瞬間に、本物を OS 既定の遷移なしで即座に最小化する。
//      上に影武者が重なっているので見た目は変わらず、本物は待たせずにタスクバーへ収まる
//      （本物を cloak で隠し続けないので、「本物が見えないまま残る」事故が起きない）。
//   4. 影武者だけが、ボタンとほぼ同じ大きさになるまで縮みながらボタンの中心へ吸い寄せられる。
//      ゆっくり動き出して（ふわっ）途中で加速し（スーッ）、着地の直前だけ少し緩める。横幅を縦より少し先に細らせて、
//      吸い口へ吸い込まれていく形にする。
//   5. 飛びながら、重ねた「くっきり → 軽くぼけ → 強くぼけ」の画像を入れ替えて輪郭ごと空気に溶かし、
//      ボタンへ届く手前（道のりの約 9 割）で消えきる。ぼかし画像は撮影した画像を縮小して CPU で一度だけ作る
//      （透過影武者に毎フレームの BlurEffect を掛けるとコマ飛びするため使わない）。
// Windows の「アニメーションを表示する」または「ウィンドウを最大化や最小化するときにアニメーションで表示する」が
// オフのとき、表示モード切替や終了演出の途中、まだ表示していないときは、従来どおり一瞬で最小化する。
public partial class MainWindow
{
    // アプリ全体のテンポをそろえるため、表示モード切替（ModeMorphDuration = 340ms）と同じ長さにする（テストで一致を保証）。
    // ※ 別ファイルの static フィールドを初期化に使うと初期化順が不定になるため、値は直接書く。
    private static readonly TimeSpan MinimizeGhostSettleDelay = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan MinimizeSinkDuration = TimeSpan.FromMilliseconds(340);
    // ボタンの位置の問い合わせは温まっていれば十数 ms で返る。押してからの待ちを増やさないよう、待つのはここまで。
    private static readonly TimeSpan MinimizeTargetLookupBudget = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan MinimizeTargetFreshness = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MinimizePreStartTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan MinimizeWatchdogSlack = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan TaskbarButtonWarmupDelay = TimeSpan.FromSeconds(3);

    // 吸い寄せられる移動と縦の縮み: ゆっくり動き出し、途中で加速し、着地の直前だけ少し緩める。
    internal static readonly KeySpline MinimizeTravelSpline = CreateFrozenSpline(0.40, 0.0, 0.65, 0.80);
    // 横の縮み: 縦より少し先に細って、吸い口へ吸い込まれる形にする。
    internal static readonly KeySpline MinimizeNarrowSpline = CreateFrozenSpline(0.35, 0.0, 0.60, 0.85);
    // 全体の薄れ方（0 〜 MinimizeFadeEndRatio の区間）: 前半ははっきり見せ、後半は輪郭ごとぼけながら
    // 空気に溶けるように薄れ、ボタンへ届く手前で消えきる（不透明度 = 1 − この進み）。
    internal static readonly KeySpline MinimizeFadeSpline = CreateFrozenSpline(0.50, 0.0, 0.75, 1.0);
    // くっきり⇄ぼかしの受け渡しに使うなめらかな S 字。
    internal static readonly KeySpline MinimizeBlendSpline = CreateFrozenSpline(0.40, 0.0, 0.60, 1.0);

    // 以下は全体時間（MinimizeSinkDuration）に対する割合。
    // 消えきる時点。移動はこのとき道のりの約 9 割で、ボタンに届く頃にはもう見えない。
    internal const double MinimizeFadeEndRatio = 0.86;
    // くっきりした層（枠・画像）を引いていく区間。
    internal const double MinimizeCrispFadeStartRatio = 0.08;
    internal const double MinimizeCrispFadeEndRatio = 0.50;
    // 軽いぼかしを出して、強いぼかしへ受け渡す区間。
    internal const double MinimizeLightSoftInStartRatio = 0.02;
    internal const double MinimizeLightSoftInEndRatio = 0.24;
    internal const double MinimizeLightSoftOutStartRatio = 0.38;
    internal const double MinimizeLightSoftOutEndRatio = 0.66;
    // 強いぼかしを出す区間（その後は全体の薄れで消える）。
    internal const double MinimizeHeavySoftInStartRatio = 0.24;
    internal const double MinimizeHeavySoftInEndRatio = 0.52;

    // 各層の不透明度の時刻表（全体時間に対する割合, 不透明度）。最後の点の値は終わりまで保たれる。
    // 復元はこれを時間について裏返して（MirrorBlendPoints）そっくり逆に再生する。
    internal static readonly (double Ratio, double Value)[] MinimizeCrispBlend =
    [
        (0.0, 1.0),
        (MinimizeCrispFadeStartRatio, 1.0),
        (MinimizeCrispFadeEndRatio, 0.0)
    ];

    internal static readonly (double Ratio, double Value)[] MinimizeLightSoftBlend =
    [
        (0.0, 0.0),
        (MinimizeLightSoftInStartRatio, 0.0),
        (MinimizeLightSoftInEndRatio, 1.0),
        (MinimizeLightSoftOutStartRatio, 1.0),
        (MinimizeLightSoftOutEndRatio, 0.0)
    ];

    internal static readonly (double Ratio, double Value)[] MinimizeHeavySoftBlend =
    [
        (0.0, 0.0),
        (MinimizeHeavySoftInStartRatio, 0.0),
        (MinimizeHeavySoftInEndRatio, 1.0)
    ];

    // ぼかし画像の作り方: パネル画像を 1/4 に縮小し、箱型ぼかし 3 回（半径は縮小後の画素）。
    // 軽い＝元の大きさで約 10px、強い＝約 22px のぼけ。輪郭の外へにじむ分（最大で約 60px）を収めるため、
    // 影武者の余白は起動・終了より広く取る。
    private const double MinimizeSoftenDownscale = 0.25;
    private const int MinimizeLightSoftenRadius = 2;
    private const int MinimizeHeavySoftenRadius = 5;
    private const double MinimizeGhostPadding = 40.0;

    // 最後の大きさはボタンとほぼ同じ。極端なボタンの大きさでも、点になりすぎず大きすぎない範囲に収める。
    private const double MinimizeMinEndScale = 0.035;
    private const double MinimizeMaxEndScale = 0.3;
    // ボタンが見つからないときの代わりの着地点の大きさ（DIP、タスクバーのボタン程度）。
    private const double MinimizeFallbackTargetSize = 44.0;

    private readonly TaskbarButtonLocator _taskbarButtonLocator = new();
    private bool _isMinimizeAnimationRunning;
    private Action? _forceFinishMinimizeAnimation;

    // タスクバー（代わりの着地点を置く帯）がある画面の縁。
    internal enum MinimizeSinkEdge
    {
        Bottom,
        Top,
        Left,
        Right
    }

    // 影武者を置く範囲・着地点（すべて物理 px）・パネル中心から着地点中心への移動量・最後の倍率。
    internal readonly record struct MinimizeSinkPlan(
        Rect GhostArea,
        Rect TargetRect,
        Vector Travel,
        double EndScale);

    // タイトルバーの最小化ボタン、Win+↓・システムメニューの最小化（SC_MINIMIZE）から呼ぶ。
    private void RequestPanelMinimize()
    {
        CancelScheduledPanelFrontRestore();
        CancelScheduledFocusedSlotReassert();
        CancelScheduledPostLaunchArrange();
        MinimizePanelWithSinkAnimation();
    }

    // 最小化ボタンにマウスが乗った時点や起動の少しあとで、タスクバーのボタンの位置を先読みしておく。
    private void PrefetchTaskbarButtonRect()
    {
        if (_isMinimizeAnimationRunning || !CanAnimateMinimize(new WindowInteropHelper(this).Handle))
        {
            return;
        }

        if (_taskbarButtonLocator.TryGetRecent(MinimizeTargetFreshness, out _))
        {
            return;
        }

        _ = _taskbarButtonLocator.RefreshAsync(new WindowInteropHelper(this).Handle, Title);
    }

    // 初回の UI オートメーション接続は時間がかかるので、起動演出が落ち着いたあとで一度だけ温めておく。
    private async void WarmUpTaskbarButtonLocator()
    {
        try
        {
            await Task.Delay(TaskbarButtonWarmupDelay);
            if (!_refreshCancellation.IsCancellationRequested)
            {
                PrefetchTaskbarButtonRect();
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    // WM_SYSCOMMAND(SC_MINIMIZE) を演出付きの最小化へ置き換えられるときは true（既定処理を止める）。
    private bool TryInterceptSystemMinimize()
    {
        if (_isMinimizeAnimationRunning)
        {
            return true;
        }

        if (!CanAnimateMinimize(new WindowInteropHelper(this).Handle))
        {
            return false;
        }

        // ウィンドウプロシージャの中では撮影や新しいウィンドウ表示をせず、抜けたあとで始める。
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(RequestPanelMinimize));
        return true;
    }

    private async void MinimizePanelWithSinkAnimation()
    {
        if (_isMinimizeAnimationRunning)
        {
            return;
        }

        // 起動直後のふわっと出現演出中に最小化されたら、先に起動演出を終わらせて本物を解放する。
        CompleteStartupRevealImmediately();

        var handle = new WindowInteropHelper(this).Handle;
        if (!CanAnimateMinimize(handle))
        {
            MinimizeImmediately();
            return;
        }

        _isMinimizeAnimationRunning = true;
        Rect? taskbarButton;
        try
        {
            taskbarButton = await ResolveTaskbarButtonRectAsync(handle);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            taskbarButton = null;
        }

        // 位置を調べている間に状況が変わっていたら（終了・別の切替が始まった等）、演出せずに最小化だけ行う。
        if (!CanAnimateMinimize(handle)
            || !_windowArranger.TryGetMonitorAreasForWindow(handle, out var monitor, out var workArea))
        {
            _isMinimizeAnimationRunning = false;
            MinimizeImmediately();
            return;
        }

        StartupRevealGhostWindow? ghost = null;
        var minimizeApplied = false;
        var completed = false;

        // 影武者が描かれないまま本物の最小化が遅れないよう、まずは短い期限で見張る（描かれたら全体の期限へ延ばす）。
        var watchdog = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = MinimizePreStartTimeout
        };

        // 後始末は 1 か所に集め、何度呼ばれても 1 回だけ行う。どの経路で終わっても（影武者が描かれない・
        // アニメーションの完了合図が来ない・例外）、最小化の要求だけは必ず本物へ反映して影武者を閉じる。
        void Finish(bool applyPendingMinimize)
        {
            if (completed)
            {
                return;
            }

            completed = true;
            watchdog.Stop();
            _isMinimizeAnimationRunning = false;
            _forceFinishMinimizeAnimation = null;

            if (!minimizeApplied && applyPendingMinimize)
            {
                minimizeApplied = true;
                MinimizeImmediately();
            }

            WindowArranger.SetTransitionsDisabled(handle, false);
            if (ghost is not null)
            {
                SafeCloseStartupGhost(ghost);
            }
        }

        _forceFinishMinimizeAnimation = () => Finish(applyPendingMinimize: false);

        watchdog.Tick += (_, _) =>
        {
            DiagnosticLog.Write(
                LogLevel.Warn,
                minimizeApplied
                    ? "Minimize sink animation did not complete in time; forcing completion."
                    : "Minimize sink animation did not start in time; minimizing immediately.");
            Finish(applyPendingMinimize: true);
        };
        watchdog.Start();

        try
        {
            UpdateLayout();
            var fromMicroDial = IsMicroDialActive && _microDialWindow is not null;
            var panelRect = fromMicroDial ? _microDialWindow!.GetCoreScreenRect() : GetPanelScreenRect();
            if (panelRect.Width <= 0 || panelRect.Height <= 0)
            {
                Finish(applyPendingMinimize: true);
                return;
            }

            var panelImage = fromMicroDial ? _microDialWindow!.CaptureCoreCircleImage() : CapturePanelImage();
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
                initialOpacity: 1.0,
                initialScale: 1.0,
                initialOffsetY: 0.0,
                cornerRadius: fromMicroDial ? MicroDialWindow.CoreRadiusDip : 8.0);
            if (TryCreateSoftenedPanelImages(panelImage, out var lightSoft, out var heavySoft))
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
                    if (fromMicroDial)
                    {
                        HideMicroDialForMinimize();
                    }

                    // 影武者が本物と完全一致して描かれた瞬間に、本物を OS 既定の遷移なしで即座に最小化する。
                    WindowArranger.SetTransitionsDisabled(handle, true);
                    minimizeApplied = true;
                    WindowState = WindowState.Minimized;

                    watchdog.Stop();
                    watchdog.Interval = MinimizeGhostSettleDelay + MinimizeSinkDuration + MinimizeWatchdogSlack;
                    watchdog.Start();

                    // 影武者の描画パイプラインが落ち着くのを一瞬だけ待ってから再生し、冒頭のコマ飛びを防ぐ。
                    await Task.Delay(MinimizeGhostSettleDelay);
                    if (completed)
                    {
                        return;
                    }

                    shownGhost.PlayMinimizeSink(plan, () => Finish(applyPendingMinimize: true));
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write(ex);
                    Finish(applyPendingMinimize: true);
                }
            };
            shownGhost.ShowGhost();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            Finish(applyPendingMinimize: true);
        }
    }

    // 先読みした位置が新しければそのまま使う。古ければ問い合わせ直し、短い期限内に返らなければ最後に分かった位置を使う。
    private async Task<Rect?> ResolveTaskbarButtonRectAsync(IntPtr handle)
    {
        if (_taskbarButtonLocator.TryGetRecent(MinimizeTargetFreshness, out var recent))
        {
            return recent;
        }

        var lookup = _taskbarButtonLocator.RefreshAsync(handle, Title);
        var finished = await Task.WhenAny(lookup, Task.Delay(MinimizeTargetLookupBudget));
        if (finished == lookup && lookup.Result is { } found)
        {
            return found;
        }

        return _taskbarButtonLocator.LastKnown;
    }

    // 輪郭ごとふわっと消すための、軽い・強いぼかし画像を作る。失敗しても演出は続けられる（くっきりのまま薄れる）。
    private static bool TryCreateSoftenedPanelImages(
        BitmapSource panelImage,
        out SoftenedPanelImage light,
        out SoftenedPanelImage heavy)
    {
        try
        {
            var small = PanelImageSoftener.Downscale(panelImage, MinimizeSoftenDownscale);
            light = PanelImageSoftener.Soften(small, MinimizeLightSoftenRadius);
            heavy = PanelImageSoftener.Soften(small, MinimizeHeavySoftenRadius);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            light = default;
            heavy = default;
            return false;
        }
    }

    // 全体時間に対する割合を時刻へ。
    private static TimeSpan TimeAt(TimeSpan duration, double ratio)
    {
        return TimeSpan.FromTicks((long)Math.Round(duration.Ticks * Math.Clamp(ratio, 0.0, 1.0)));
    }

    // 最小化の演出中に復元（タスクバーのクリックなど）されたときや、アプリ終了時に呼ぶ。影武者をすぐ閉じる。
    private void CompleteMinimizeAnimationImmediately()
    {
        _forceFinishMinimizeAnimation?.Invoke();
    }

    private void MinimizeImmediately()
    {
        try
        {
            WindowState = WindowState.Minimized;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }
    }

    private bool CanAnimateMinimize(IntPtr handle)
    {
        return handle != IntPtr.Zero
            && IsLoaded
            && IsVisible
            && WindowState == WindowState.Normal
            && !_isModeTransitionRunning
            && !_isExitAnimationRunning
            && !_isRestoreRevealRunning
            && !_isRestoreRevealArmed
            && SystemParameters.ClientAreaAnimation
            && SystemParameters.MinimizeAnimation
            && PanelFrameBorder.ActualWidth > 0
            && PanelFrameBorder.ActualHeight > 0;
    }

    // アニメーションは UI スレッド以外（テストの STA スレッドなど）でも組み立てるので、共有する曲線は凍結しておく。
    private static KeySpline CreateFrozenSpline(double x1, double y1, double x2, double y2)
    {
        var spline = new KeySpline(x1, y1, x2, y2);
        spline.Freeze();
        return spline;
    }

    private static Rect ToRect(WindowArranger.WindowBounds bounds)
    {
        return new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    // ディスプレイ全体と作業領域の差（＝タスクバーが占める帯）がいちばん広い縁。
    // 自動的に隠す設定などで差がないときは、いちばん一般的な下側とみなす。
    internal static MinimizeSinkEdge ResolveTaskbarEdge(Rect monitorRect, Rect workArea)
    {
        var edge = MinimizeSinkEdge.Bottom;
        var widest = monitorRect.Bottom - workArea.Bottom;

        var topGap = workArea.Top - monitorRect.Top;
        if (topGap > widest)
        {
            edge = MinimizeSinkEdge.Top;
            widest = topGap;
        }

        var leftGap = workArea.Left - monitorRect.Left;
        if (leftGap > widest)
        {
            edge = MinimizeSinkEdge.Left;
            widest = leftGap;
        }

        var rightGap = monitorRect.Right - workArea.Right;
        if (rightGap > widest)
        {
            edge = MinimizeSinkEdge.Right;
            widest = rightGap;
        }

        return widest > 0 ? edge : MinimizeSinkEdge.Bottom;
    }

    // 吸い込まれる先（物理 px）。パネルと同じディスプレイにあるタスクバーのボタンが分かればそこへ。
    // 分からない・別のディスプレイにある・隠れているときは、タスクバーの帯の上（帯がなければ画面の縁）で、
    // パネルの中心にいちばん近い位置へ向かう。
    internal static Rect ResolveMinimizeTargetRect(
        Rect panelRect,
        Rect monitorRect,
        Rect workArea,
        Rect? taskbarButtonRect,
        double fallbackSize)
    {
        if (taskbarButtonRect is { } button && IsUsableTaskbarButtonRect(button, monitorRect))
        {
            return button;
        }

        if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0)
        {
            workArea = monitorRect;
        }

        var size = Math.Max(1.0, fallbackSize);
        var half = size / 2;
        var panelCenter = new Point(panelRect.Left + panelRect.Width / 2, panelRect.Top + panelRect.Height / 2);
        double centerX;
        double centerY;
        switch (ResolveTaskbarEdge(monitorRect, workArea))
        {
            case MinimizeSinkEdge.Top:
                centerX = Math.Clamp(panelCenter.X, monitorRect.Left + half, monitorRect.Right - half);
                centerY = workArea.Top > monitorRect.Top ? (monitorRect.Top + workArea.Top) / 2 : monitorRect.Top + half;
                break;
            case MinimizeSinkEdge.Left:
                centerX = workArea.Left > monitorRect.Left ? (monitorRect.Left + workArea.Left) / 2 : monitorRect.Left + half;
                centerY = Math.Clamp(panelCenter.Y, monitorRect.Top + half, monitorRect.Bottom - half);
                break;
            case MinimizeSinkEdge.Right:
                centerX = workArea.Right < monitorRect.Right ? (workArea.Right + monitorRect.Right) / 2 : monitorRect.Right - half;
                centerY = Math.Clamp(panelCenter.Y, monitorRect.Top + half, monitorRect.Bottom - half);
                break;
            default:
                centerX = Math.Clamp(panelCenter.X, monitorRect.Left + half, monitorRect.Right - half);
                centerY = workArea.Bottom < monitorRect.Bottom ? (workArea.Bottom + monitorRect.Bottom) / 2 : monitorRect.Bottom - half;
                break;
        }

        return new Rect(centerX - half, centerY - half, size, size);
    }

    // 自動的に隠す設定で画面外にある・別のディスプレイにある・大きさがおかしいボタンは使わない。
    private static bool IsUsableTaskbarButtonRect(Rect button, Rect monitorRect)
    {
        if (button.IsEmpty || button.Width < 4 || button.Height < 4 || button.Width > 600 || button.Height > 600)
        {
            return false;
        }

        var center = new Point(button.Left + button.Width / 2, button.Top + button.Height / 2);
        return monitorRect.Contains(center);
    }

    // すべて物理 px。影武者は、パネルの中心が着地点の中心へ吸い寄せられながら、着地点とほぼ同じ大きさまで縮む。
    // 影武者の範囲は、最初のパネルと着地点の両方を含む（タスクバーの上にも重ねて描くため、タスクバーで切らない）。
    internal static MinimizeSinkPlan BuildMinimizeSinkPlan(
        Rect panelRect,
        Rect targetRect,
        double paddingX,
        double paddingY)
    {
        var panelCenter = new Point(panelRect.Left + panelRect.Width / 2, panelRect.Top + panelRect.Height / 2);
        var targetCenter = new Point(targetRect.Left + targetRect.Width / 2, targetRect.Top + targetRect.Height / 2);

        var fit = Math.Min(
            targetRect.Width / Math.Max(1.0, panelRect.Width),
            targetRect.Height / Math.Max(1.0, panelRect.Height));
        var endScale = Math.Clamp(fit, MinimizeMinEndScale, MinimizeMaxEndScale);

        var area = panelRect;
        area.Inflate(paddingX, paddingY);
        var targetArea = targetRect;
        targetArea.Inflate(paddingX, paddingY);
        area.Union(targetArea);

        return new MinimizeSinkPlan(area, targetRect, targetCenter - panelCenter, endScale);
    }

    // テスト用: 最小化演出の時間配分（安定待ち・ボタン位置の待ち上限・全体）。
    internal static (TimeSpan Settle, TimeSpan LookupBudget, TimeSpan Total) GetMinimizeSinkTimelineForTesting()
    {
        return (MinimizeGhostSettleDelay, MinimizeTargetLookupBudget, MinimizeSinkDuration);
    }

    // テスト専用の無菌検証ヘルパー。画面へ Show() せずに、最小化用の影武者の計測サイズ・
    // AllowsTransparency・縮小の基準点（中央）・ぼかし層の外側へのにじみと、吸い込みタイムラインの生成を検証する。
    internal static (Size StageSize, bool AllowsTransparency, Point AppliedOrigin, Size HeavySoftSize, double GhostPadding)
        VerifyMinimizeGhostOffscreenForTesting()
    {
        var width = 650;
        var height = 535;
        var pixel = new byte[width * height * 4];
        Array.Fill(pixel, (byte)0xFF);
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixel, width * 4);
        image.Freeze();

        var panelRect = new Rect(635, 272, width, height);
        var plan = BuildMinimizeSinkPlan(
            panelRect,
            new Rect(1353, 1032, 44, 48),
            MinimizeGhostPadding,
            MinimizeGhostPadding);

        var ghost = new StartupRevealGhostWindow(
            plan.GhostArea,
            panelRect,
            image,
            initialOpacity: 1.0,
            initialScale: 1.0,
            initialOffsetY: 0.0);
        if (!TryCreateSoftenedPanelImages(image, out var light, out var heavy))
        {
            throw new InvalidOperationException("ぼかし画像を作れませんでした。");
        }

        ghost.SetSoftenedImages(light, heavy);
        var stageSize = ghost.MeasureStageOffscreenForTesting();
        ghost.PlayMinimizeSink(plan, () => { });
        return (stageSize, ghost.AllowsTransparency, ghost.BodyRenderTransformOriginForTesting, ghost.HeavySoftLayerRenderSizeForTesting, MinimizeGhostPadding);
    }
}
