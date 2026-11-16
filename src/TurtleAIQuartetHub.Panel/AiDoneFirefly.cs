using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel;

public enum AiDonePulseRole
{
    None,
    Halo,
    CardEdge,
    CardSurface,
    BadgeSurface,
    BadgeGlow,
    MicroSurface
}

/// <summary>
/// 完了時（<see cref="AiActivityState.Done"/>）の青表示を、通常時より少し早い
/// 3.0 秒周期のホタル点滅で滑らかに脈動させる添付ビヘイビア。
/// </summary>
/// <remarks>
/// すべてのロール（光背・枠線・カード地・A〜D バッジ・光背リング・極小タイル）を
/// <see cref="UIElement.OpacityProperty"/> のみのアニメーションへ統一し、モニタ同期の
/// 高フレームレートかつ水平接線（速度 0 接続）の 2 区間スプライン曲線で補間することで、
/// 8bit RGB 階段バンディングや 30fps コマ落ち・折れ角のない滑らかな呼吸発光を実現する。
/// </remarks>
public static class AiDoneFirefly
{
    public const double CycleDurationSeconds = 3.0;
    internal const double PeakSeconds = 1.15;

    // 始端・終端とも傾き 0（p1.y=0, p2.y=1）で水平に接続し、頂点・底・周期境界のすべてで角を作らない。
    private static readonly KeySpline RiseSpline = CreateFrozenSpline(0.55, 0.0, 0.22, 1.0);
    private static readonly KeySpline FadeSpline = CreateFrozenSpline(0.30, 0.0, 0.55, 1.0);

    private static readonly KeyTime KeyStart = KeyTime.FromTimeSpan(TimeSpan.Zero);
    private static readonly KeyTime KeyPeak = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(PeakSeconds));
    private static readonly KeyTime KeyEnd = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(CycleDurationSeconds));

    private static readonly long ClockEpochTicks = Stopwatch.GetTimestamp();

    public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached(
        "Role",
        typeof(AiDonePulseRole),
        typeof(AiDoneFirefly),
        new PropertyMetadata(AiDonePulseRole.None, OnStateInputChanged));

    public static readonly DependencyProperty ActivityProperty = DependencyProperty.RegisterAttached(
        "Activity",
        typeof(AiActivityState),
        typeof(AiDoneFirefly),
        new PropertyMetadata(AiActivityState.Idle, OnStateInputChanged));

    private static readonly DependencyProperty ActiveControllerProperty = DependencyProperty.RegisterAttached(
        "ActiveController",
        typeof(PulseController),
        typeof(AiDoneFirefly),
        new PropertyMetadata(null));

    public static AiDonePulseRole GetRole(DependencyObject obj) => (AiDonePulseRole)obj.GetValue(RoleProperty);

    public static void SetRole(DependencyObject obj, AiDonePulseRole value) => obj.SetValue(RoleProperty, value);

    public static AiActivityState GetActivity(DependencyObject obj) => (AiActivityState)obj.GetValue(ActivityProperty);

    public static void SetActivity(DependencyObject obj, AiActivityState value) => obj.SetValue(ActivityProperty, value);

    private static void OnStateInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        EnsureHooked(element);
        SyncElement(element);
    }

    private static void EnsureHooked(FrameworkElement element)
    {
        if (element.GetValue(ActiveControllerProperty) is PulseController)
        {
            return;
        }

        var controller = new PulseController(element);
        element.SetValue(ActiveControllerProperty, controller);
        element.IsVisibleChanged += controller.OnVisibilityOrLifetimeChanged;
        element.Loaded += controller.OnVisibilityOrLifetimeChanged;
        element.Unloaded += controller.OnUnloaded;
    }

    private static void SyncElement(FrameworkElement element, bool allowDetachedForTesting = false)
    {
        var controller = (PulseController?)element.GetValue(ActiveControllerProperty);
        if (controller is null)
        {
            return;
        }

        var role = GetRole(element);
        var isVisible = element.IsVisible || (allowDetachedForTesting && AreAncestorsVisible(element));
        var shouldRun = role != AiDonePulseRole.None
            && GetActivity(element) == AiActivityState.Done
            && isVisible;

        if (shouldRun)
        {
            controller.StartOrSync(role);
        }
        else
        {
            controller.Stop();
        }
    }

    private static bool AreAncestorsVisible(DependencyObject element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is UIElement ui && ui.Visibility != Visibility.Visible)
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return true;
    }

    /// <summary>正規化位相（0..1）に対する 3.0 秒ホタル点滅の強度比（0..1）を返す（検証・プレビュー用）。</summary>
    internal static double EvaluateIntensity(double phase)
    {
        if (!double.IsFinite(phase))
        {
            return 0.0;
        }

        var u = phase - Math.Floor(phase);
        var seconds = u * CycleDurationSeconds;
        var anim = CreateDoublePulseAnimation(0.0, 1.0);
        var clock = (AnimationClock)anim.CreateClock();
        clock.Controller!.Begin();
        clock.Controller.SeekAlignedToLastTick(TimeSpan.FromSeconds(seconds), TimeSeekOrigin.BeginTime);
        return (double)anim.GetCurrentValue(0.0, 0.0, clock);
    }

    /// <summary>
    /// 指定ビジュアルツリー配下で動作中の完了ホタル時計を一斉に任意位相へ固定する（テスト・静止描画検証専用）。
    /// </summary>
    internal static void FreezeAtPhaseForTesting(DependencyObject root, double normalizedPhase)
    {
        var u = double.IsFinite(normalizedPhase) ? normalizedPhase - Math.Floor(normalizedPhase) : 0.0;
        var offset = TimeSpan.FromSeconds(u * CycleDurationSeconds);
        SeekTreeRecursive(root, offset);
    }

    private static void SeekTreeRecursive(DependencyObject node, TimeSpan offset)
    {
        if (node is FrameworkElement fe && node.GetValue(ActiveControllerProperty) is PulseController controller)
        {
            SyncElement(fe, allowDetachedForTesting: true);
            controller.SeekAndPauseForTesting(offset);
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            SeekTreeRecursive(VisualTreeHelper.GetChild(node, i), offset);
        }
    }

    private static TimeSpan GetSharedPhaseOffset()
    {
        var elapsedSeconds = Stopwatch.GetElapsedTime(ClockEpochTicks).TotalSeconds;
        var phaseSeconds = elapsedSeconds % CycleDurationSeconds;
        return TimeSpan.FromSeconds(phaseSeconds < 0 ? 0 : phaseSeconds);
    }

    private static (double MinOpacity, double MaxOpacity) GetOpacityRange(AiDonePulseRole role) => role switch
    {
        AiDonePulseRole.Halo => (0.12, 0.90),
        AiDonePulseRole.CardEdge => (0.28, 0.98),
        AiDonePulseRole.CardSurface => (0.00, 0.62),
        AiDonePulseRole.MicroSurface => (0.00, 0.68),
        AiDonePulseRole.BadgeSurface => (0.00, 0.66),
        AiDonePulseRole.BadgeGlow => (0.16, 0.96),
        _ => (0.0, 0.0)
    };

    private static DoubleAnimationUsingKeyFrames CreateDoublePulseAnimation(double minVal, double maxVal)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromSeconds(CycleDurationSeconds)),
            RepeatBehavior = RepeatBehavior.Forever
        };

        // DesiredFrameRate を絞らずモニタのネイティブリフレッシュレートへ追従させ、
        // 60Hz 以上でも滑らかに補間されるようにする。
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(minVal, KeyStart));
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(maxVal, KeyPeak, RiseSpline));
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(minVal, KeyEnd, FadeSpline));
        return anim;
    }

    private static KeySpline CreateFrozenSpline(double x1, double y1, double x2, double y2)
    {
        var spline = new KeySpline(x1, y1, x2, y2);
        spline.Freeze();
        return spline;
    }

    private sealed class PulseController(FrameworkElement element)
    {
        private readonly FrameworkElement _element = element;
        private AiDonePulseRole _runningRole = AiDonePulseRole.None;
        private AnimationClock? _opacityClock;

        public void OnVisibilityOrLifetimeChanged(object? sender, DependencyPropertyChangedEventArgs e) => SyncElement(_element);

        public void OnVisibilityOrLifetimeChanged(object? sender, RoutedEventArgs e) => SyncElement(_element);

        public void OnUnloaded(object? sender, RoutedEventArgs e) => Stop();

        public void StartOrSync(AiDonePulseRole role)
        {
            if (_runningRole == role)
            {
                return;
            }

            Stop();
            _runningRole = role;

            var (minOpacity, maxOpacity) = GetOpacityRange(role);
            var animation = CreateDoublePulseAnimation(minOpacity, maxOpacity);
            var clock = (AnimationClock)animation.CreateClock();
            _element.ApplyAnimationClock(UIElement.OpacityProperty, clock);
            clock.Controller!.SeekAlignedToLastTick(GetSharedPhaseOffset(), TimeSeekOrigin.BeginTime);
            _opacityClock = clock;
        }

        public void SeekAndPauseForTesting(TimeSpan offset)
        {
            if (_opacityClock?.Controller is null)
            {
                return;
            }

            _opacityClock.Controller.SeekAlignedToLastTick(offset, TimeSeekOrigin.BeginTime);
            _opacityClock.Controller.Pause();
        }

        public void Stop()
        {
            if (_runningRole == AiDonePulseRole.None)
            {
                return;
            }

            _runningRole = AiDonePulseRole.None;
            if (_opacityClock is not null)
            {
                _opacityClock.Controller?.Stop();
                _opacityClock.Controller?.Remove();
                _opacityClock = null;
            }

            _element.ApplyAnimationClock(UIElement.OpacityProperty, null);
        }
    }
}
