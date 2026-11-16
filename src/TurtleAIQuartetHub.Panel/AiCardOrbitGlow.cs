using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel;

/// <summary>
/// AI が作業中の間だけ、カード 1px 枠線（角丸含む）の中心線上を
/// 1 筋の緑の LED 光が滑らかに周回する軽量ビジュアル要素。
/// </summary>
/// <remarks>
/// <para>
/// 従来の Path + StrokeDashArray + 巨大 BlurEffect 方式では、太いストロークが角丸の内側で潰れ、
/// Shape.GetLayoutClip やオフスクリーン効果境界によりカード周囲に四角いクリップ角が露出していた。
/// </para>
/// <para>
/// 本クラスでは <see cref="DrawingVisual"/> 上に直線と正確な円弧（ArcTo）による高密度グラデーション・トレイルを
/// 枠線中心（inset 0.5 DIP, 角丸半径 R - 0.5）へ直接描画することで、角丸への完全な追従・クリップ角の根絶・
/// 段差のない滑らかな LED 発光を両立している。
/// </para>
/// </remarks>
public sealed class AiCardOrbitGlow : FrameworkElement
{
    public static readonly DependencyProperty ActivityProperty = DependencyProperty.Register(
        nameof(Activity),
        typeof(AiActivityState),
        typeof(AiCardOrbitGlow),
        new FrameworkPropertyMetadata(AiActivityState.Idle, OnMetricChanged));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius),
        typeof(double),
        typeof(AiCardOrbitGlow),
        new FrameworkPropertyMetadata(8d, OnMetricChanged));

    public static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        nameof(Phase),
        typeof(double),
        typeof(AiCardOrbitGlow),
        new FrameworkPropertyMetadata(0d, OnPhaseChanged));

    /// <summary>1px 枠線（BorderThickness="1"）の中心線オフセット。</summary>
    internal const double BorderCenterInset = 0.5;

    private const double MinSize = 8.0;
    private const int DesiredAnimationFrameRate = 60;
    private const int GlowSliceCount = 18;
    private const int CoreSliceCount = 10;

    private static readonly Color TailAuraColor = Color.FromRgb(0x45, 0xD4, 0x83);
    private static readonly Color HeadBodyColor = Color.FromRgb(0x7F, 0xF3, 0xB4);
    private static readonly Color CoreTailColor = Color.FromRgb(0x98, 0xFF, 0xC8);
    private static readonly Color CoreHeadColor = Color.FromRgb(0xC8, 0xFF, 0xE2);

    private readonly DrawingVisual _visual = new();
    private RoundedPerimeter? _perimeter;
    private CachedSlicePen[]? _cachedSlices;
    private double _builtWidth = -1;
    private double _builtHeight = -1;
    private double _builtRadius = -1;
    private bool _motionRunning;

    public AiCardOrbitGlow()
    {
        AddVisualChild(_visual);
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = false;
        Visibility = Visibility.Collapsed;

        Loaded += (_, _) => SyncMotionWithVisibility();
        Unloaded += (_, _) => StopAnimationClockOnly();
        IsVisibleChanged += (_, _) => SyncMotionWithVisibility();
    }

    public AiActivityState Activity
    {
        get => (AiActivityState)GetValue(ActivityProperty);
        set => SetValue(ActivityProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>周回アニメーションの正規化時間位相（0.0〜1.0）。テスト時は直接設定して任意位置を描画できる。</summary>
    public double Phase
    {
        get => (double)GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    internal void SetStaticPhase(double phase)
    {
        StopAnimationClockOnly();
        Phase = phase;
        RenderFrame();
    }

    internal Rect GetVisualContentBounds() => _visual.ContentBounds;

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _visual;

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override Size ArrangeOverride(Size finalSize)
    {
        RebuildAndRender(finalSize);
        return finalSize;
    }

    private static void OnMetricChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (AiCardOrbitGlow)d;
        self._builtWidth = -1;
        if (e.Property == ActivityProperty)
        {
            self.SyncVisibility();
            return;
        }

        self.InvalidateArrange();
    }

    private static void OnPhaseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (AiCardOrbitGlow)d;
        self.RenderFrame();
    }

    private void SyncVisibility()
    {
        if (Activity == AiActivityState.Working)
        {
            if (Visibility != Visibility.Visible)
            {
                Visibility = Visibility.Visible;
            }
            else
            {
                InvalidateArrange();
            }

            return;
        }

        StopAll();
        Visibility = Visibility.Collapsed;
    }

    private void SyncMotionWithVisibility()
    {
        if (Activity != AiActivityState.Working || !IsVisible)
        {
            StopAnimationClockOnly();
            return;
        }

        if (_perimeter is not null && !_motionRunning)
        {
            StartMotionClock(_perimeter.Length);
        }
    }

    private void RebuildAndRender(Size size)
    {
        if (Activity != AiActivityState.Working
            || size.Width < MinSize
            || size.Height < MinSize
            || !double.IsFinite(size.Width)
            || !double.IsFinite(size.Height))
        {
            if (Activity != AiActivityState.Working)
            {
                StopAll();
            }
            else
            {
                ClearDrawing();
            }

            return;
        }

        var cornerInput = double.IsFinite(CornerRadius) ? Math.Max(0, CornerRadius) : 0;
        var needsRebuild = _perimeter is null
            || _cachedSlices is null
            || Math.Abs(size.Width - _builtWidth) >= 0.5
            || Math.Abs(size.Height - _builtHeight) >= 0.5
            || Math.Abs(cornerInput - _builtRadius) >= 0.01;

        if (needsRebuild)
        {
            var perimeter = CreatePerimeter(size, cornerInput);
            if (perimeter.Length < MinSize)
            {
                StopAll();
                return;
            }

            _perimeter = perimeter;
            _cachedSlices = BuildTrailSlices(size);
            _builtWidth = size.Width;
            _builtHeight = size.Height;
            _builtRadius = cornerInput;

            // サイズが変わったときは周期を再計算して滑らかなアニメーションを張り直す。
            if (IsVisible)
            {
                StartMotionClock(perimeter.Length);
            }
        }
        else if (IsVisible && !_motionRunning && _perimeter is not null)
        {
            StartMotionClock(_perimeter.Length);
        }

        RenderFrame();
    }

    private void StartMotionClock(double perimeterLength)
    {
        var durationSeconds = ComputeDurationSeconds(perimeterLength);
        var animation = new DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromSeconds(durationSeconds)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(animation, DesiredAnimationFrameRate);

        BeginAnimation(PhaseProperty, null);
        BeginAnimation(PhaseProperty, animation);
        _motionRunning = true;
    }

    private void StopAnimationClockOnly()
    {
        if (!_motionRunning)
        {
            return;
        }

        BeginAnimation(PhaseProperty, null);
        _motionRunning = false;
    }

    private void StopAll()
    {
        StopAnimationClockOnly();
        _perimeter = null;
        _cachedSlices = null;
        _builtWidth = -1;
        _builtHeight = -1;
        _builtRadius = -1;
        ClearDrawing();
    }

    private void ClearDrawing()
    {
        using var dc = _visual.RenderOpen();
    }

    private void RenderFrame()
    {
        if (Activity != AiActivityState.Working || _perimeter is null || _cachedSlices is null)
        {
            ClearDrawing();
            return;
        }

        var (progress, speedFactor) = EvaluateMotion(Phase);
        var headDist = progress * _perimeter.Length;
        var trailLength = ComputeTrailLength(_perimeter.Length, speedFactor);

        using var dc = _visual.RenderOpen();
        for (int i = 0; i < _cachedSlices.Length; i++)
        {
            var slice = _cachedSlices[i];
            var sliceLen = trailLength * slice.LengthFraction;
            if (sliceLen < 0.8)
            {
                continue;
            }

            var geometry = _perimeter.CreateSubGeometry(headDist - sliceLen, headDist);
            if (geometry is not null)
            {
                dc.DrawGeometry(null, slice.Pen, geometry);
            }
        }
    }

    /// <summary>周長に応じた穏やかな 1 周秒数を返す（標準 ~3.60s / 縮小 ~2.77s / 極小 ~2.45s）。</summary>
    internal static double ComputeDurationSeconds(double perimeterLength) =>
        Math.Clamp(2.35 + ((perimeterLength - 120.0) * 0.0016), 2.45, 3.60);

    /// <summary>周長と現在速度係数に応じた LED トレイル長を返す。</summary>
    internal static double ComputeTrailLength(double perimeterLength, double speedFactor)
    {
        var baseTrail = Math.Min(Math.Clamp(perimeterLength * 0.21, 48.0, 185.0), perimeterLength * 0.46);
        return baseTrail * (0.92 + (0.18 * Math.Clamp(speedFactor, 0.0, 1.0)));
    }

    /// <summary>
    /// 1 周の経過位相（0..1）から、周回進捗（0..1）と正規化速度係数（0..1）を返す。
    /// 前半〜中盤はふわっとゆっくり巡り、終盤（0.80 付近）に向けて心地よく少しだけ加速し、
    /// 始点・終点で速度と位置が連続に繋がるため周回の境目でカクつかない。
    /// </summary>
    internal static (double Progress, double SpeedFactor) EvaluateMotion(double phase)
    {
        if (!double.IsFinite(phase))
        {
            return (0.0, 0.0);
        }

        var t = phase - Math.Floor(phase);
        const int steps = MotionTable.Steps;
        var scaled = t * steps;
        var index = Math.Clamp((int)scaled, 0, steps - 1);
        var frac = scaled - index;
        var p0 = MotionTable.Cumulative[index];
        var p1 = MotionTable.Cumulative[index + 1];
        var s0 = MotionTable.SpeedNorm[index];
        var s1 = MotionTable.SpeedNorm[index + 1];
        return (p0 + ((p1 - p0) * frac), s0 + ((s1 - s0) * frac));
    }

    internal static RoundedPerimeter CreatePerimeter(Size size, double cardCornerRadius)
    {
        var width = Math.Max(1.0, size.Width - (BorderCenterInset * 2));
        var height = Math.Max(1.0, size.Height - (BorderCenterInset * 2));
        var radius = Math.Min(Math.Max(0.0, cardCornerRadius - BorderCenterInset), Math.Min(width, height) * 0.5);
        return new RoundedPerimeter(BorderCenterInset, BorderCenterInset, width, height, radius);
    }

    private static CachedSlicePen[] BuildTrailSlices(Size size)
    {
        // カード短辺に合わせて線の太さをスケールし、縮小・極小モードでも隣カードや外枠境界を汚さない。
        var shortSide = Math.Min(size.Width, size.Height);
        var scale = Math.Clamp((shortSide - 22.0) / 48.0, 0.0, 1.0);

        var outerMaxW = Lerp(4.20, 7.20, scale);
        var bodyMinW = Lerp(1.50, 2.20, scale);
        var coreMaxW = Lerp(1.10, 1.45, scale);
        var coreMinW = Lerp(0.95, 1.15, scale);

        var slices = new CachedSlicePen[GlowSliceCount + CoreSliceCount];

        // 1. 外側オーラ〜中間ボディ（18 段で高精細ディスプレイでも段差の見えない連続フェードを生成）
        for (int i = 0; i < GlowSliceCount; i++)
        {
            var u = i / (double)(GlowSliceCount - 1);
            var fraction = 1.0 - (0.93 * Math.Pow(u, 0.88));
            var strokeWidth = Lerp(outerMaxW, bodyMinW, Math.Pow(u, 0.90));
            var alpha = Lerp(0.011, 0.050, Math.Pow(u, 1.35));
            var color = LerpColor(TailAuraColor, HeadBodyColor, u);
            slices[i] = CreateCachedSlice(fraction, strokeWidth, alpha, color);
        }

        // 2. LED 芯（先頭 32% の区間に 10 段を重ねて枠線上の鋭い輝きを表現）
        for (int i = 0; i < CoreSliceCount; i++)
        {
            var u = i / (double)(CoreSliceCount - 1);
            var fraction = 0.32 * (1.0 - (0.86 * u));
            var strokeWidth = Lerp(coreMaxW, coreMinW, u);
            var alpha = Lerp(0.032, 0.115, Math.Pow(u, 1.25));
            var color = LerpColor(CoreTailColor, CoreHeadColor, u);
            slices[GlowSliceCount + i] = CreateCachedSlice(fraction, strokeWidth, alpha, color);
        }

        return slices;
    }

    private static CachedSlicePen CreateCachedSlice(double fraction, double strokeWidth, double alpha, Color color)
    {
        var alphaByte = (byte)Math.Clamp((int)Math.Round(alpha * 255.0), 0, 255);
        var brush = new SolidColorBrush(Color.FromArgb(alphaByte, color.R, color.G, color.B));
        brush.Freeze();

        var pen = new Pen(brush, strokeWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return new CachedSlicePen(fraction, pen);
    }

    private static Color LerpColor(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Clamp((int)Math.Round(Lerp(a.R, b.R, t)), 0, 255),
        (byte)Math.Clamp((int)Math.Round(Lerp(a.G, b.G, t)), 0, 255),
        (byte)Math.Clamp((int)Math.Round(Lerp(a.B, b.B, t)), 0, 255));

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    private readonly record struct CachedSlicePen(double LengthFraction, Pen Pen);

    private static class MotionTable
    {
        public const int Steps = 256;
        public static readonly double[] Cumulative = new double[Steps + 1];
        public static readonly double[] SpeedNorm = new double[Steps + 1];
        public static readonly double[] SpeedRatio = new double[Steps + 1];

        static MotionTable()
        {
            var raw = new double[Steps + 1];
            double total = 0.0;
            for (int i = 0; i <= Steps; i++)
            {
                var u = i / (double)Steps;
                // 終盤（u = 0.80 付近）を山にしたガウス状加速バンプ。周期端（0 と 1）を跨いで滑らかに接続する。
                var d = u - 0.80;
                if (d > 0.5) d -= 1.0;
                if (d < -0.5) d += 1.0;
                var accelBump = Math.Exp(-(d * d) / (2.0 * 0.14 * 0.14));
                raw[i] = 0.72 + (0.76 * accelBump);
                if (i > 0)
                {
                    total += 0.5 * (raw[i - 1] + raw[i]) / Steps;
                }
            }

            Cumulative[0] = 0.0;
            for (int i = 1; i <= Steps; i++)
            {
                Cumulative[i] = Cumulative[i - 1] + (0.5 * (raw[i - 1] + raw[i]) / Steps / total);
            }

            Cumulative[Steps] = 1.0;

            double minV = double.MaxValue;
            double maxV = double.MinValue;
            for (int i = 0; i <= Steps; i++)
            {
                var v = raw[i] / total;
                SpeedRatio[i] = v;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }

            var span = maxV - minV;
            for (int i = 0; i <= Steps; i++)
            {
                SpeedNorm[i] = span > 0 ? (SpeedRatio[i] - minV) / span : 0.0;
            }
        }
    }

    /// <summary>角丸矩形の厳密な弧長パラメータ化モデル。</summary>
    internal sealed class RoundedPerimeter
    {
        private readonly double _left;
        private readonly double _top;
        private readonly double _right;
        private readonly double _bottom;
        private readonly double _radius;
        private readonly double _straightX;
        private readonly double _straightY;
        private readonly double _quarterArc;
        private readonly double[] _segmentLengths;

        public RoundedPerimeter(double left, double top, double width, double height, double radius)
        {
            _left = left;
            _top = top;
            _right = left + width;
            _bottom = top + height;
            _radius = Math.Min(Math.Max(0.0, radius), Math.Min(width, height) * 0.5);
            _straightX = Math.Max(0.0, width - (2.0 * _radius));
            _straightY = Math.Max(0.0, height - (2.0 * _radius));
            _quarterArc = Math.PI * 0.5 * _radius;
            _segmentLengths = [_straightX, _quarterArc, _straightY, _quarterArc, _straightX, _quarterArc, _straightY, _quarterArc];
            Length = (2.0 * _straightX) + (2.0 * _straightY) + (4.0 * _quarterArc);
        }

        public double Length { get; }

        public Point EvaluateAtDistance(double distance)
        {
            if (Length <= 0.0)
            {
                return new Point(_left, _top);
            }

            var d = distance % Length;
            if (d < 0.0)
            {
                d += Length;
            }

            var cursor = 0.0;
            for (int seg = 0; seg < 8; seg++)
            {
                var segLen = _segmentLengths[seg];
                if (segLen <= 1e-6)
                {
                    continue;
                }

                if (d <= cursor + segLen || seg == 7)
                {
                    return EvaluateSegment(seg, d - cursor, segLen);
                }

                cursor += segLen;
            }

            return EvaluateSegment(0, 0.0, _segmentLengths[0]);
        }

        public StreamGeometry? CreateSubGeometry(double startDist, double endDist)
        {
            if (Length <= 0.0 || endDist <= startDist)
            {
                return null;
            }

            var span = Math.Min(endDist - startDist, Length * 0.99);
            var s = startDist % Length;
            if (s < 0.0)
            {
                s += Length;
            }

            var e = s + span;
            var geom = new StreamGeometry();
            using var ctx = geom.Open();

            var started = false;
            var cursor = 0.0;
            for (int lap = 0; lap < 2; lap++)
            {
                for (int seg = 0; seg < 8; seg++)
                {
                    var segLen = _segmentLengths[seg];
                    if (segLen <= 1e-6)
                    {
                        continue;
                    }

                    var segStart = cursor;
                    var segEnd = cursor + segLen;
                    cursor = segEnd;

                    if (segEnd <= s)
                    {
                        continue;
                    }

                    if (segStart >= e)
                    {
                        geom.Freeze();
                        return started ? geom : null;
                    }

                    var localStart = Math.Max(0.0, s - segStart);
                    var localEnd = Math.Min(segLen, e - segStart);
                    if (localEnd <= localStart + 1e-6)
                    {
                        continue;
                    }

                    if (!started)
                    {
                        ctx.BeginFigure(EvaluateSegment(seg, localStart, segLen), isFilled: false, isClosed: false);
                        started = true;
                    }

                    var endPt = EvaluateSegment(seg, localEnd, segLen);
                    if ((seg & 1) == 0)
                    {
                        ctx.LineTo(endPt, isStroked: true, isSmoothJoin: true);
                    }
                    else
                    {
                        ctx.ArcTo(endPt, new Size(_radius, _radius), 0.0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);
                    }
                }
            }

            geom.Freeze();
            return started ? geom : null;
        }

        private Point EvaluateSegment(int seg, double localDist, double segLen)
        {
            var t = segLen > 0.0 ? Math.Clamp(localDist / segLen, 0.0, 1.0) : 0.0;
            return seg switch
            {
                0 => new Point(_left + _radius + (_straightX * t), _top),
                1 => ArcPoint(_right - _radius, _top + _radius, -Math.PI * 0.5 + (Math.PI * 0.5 * t)),
                2 => new Point(_right, _top + _radius + (_straightY * t)),
                3 => ArcPoint(_right - _radius, _bottom - _radius, Math.PI * 0.5 * t),
                4 => new Point(_right - _radius - (_straightX * t), _bottom),
                5 => ArcPoint(_left + _radius, _bottom - _radius, Math.PI * 0.5 + (Math.PI * 0.5 * t)),
                6 => new Point(_left, _bottom - _radius - (_straightY * t)),
                7 => ArcPoint(_left + _radius, _top + _radius, Math.PI + (Math.PI * 0.5 * t)),
                _ => default
            };
        }

        private Point ArcPoint(double cx, double cy, double angle) =>
            new(cx + (_radius * Math.Cos(angle)), cy + (_radius * Math.Sin(angle)));
    }
}
