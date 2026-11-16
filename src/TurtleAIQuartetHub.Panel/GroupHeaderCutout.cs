using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TurtleAIQuartetHub.Panel;

/// <summary>
/// グループ見出しラベル（IDE / CLI）と重なる上辺の 1px 枠線部分だけを切り抜き、
/// 枠内を塗りつぶさず枠外だけ背景色（カード地）を見せるための軽量添付ビヘイビア。
/// </summary>
public static class GroupHeaderCutout
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.RegisterAttached(
        "Header",
        typeof(FrameworkElement),
        typeof(GroupHeaderCutout),
        new PropertyMetadata(null, OnHeaderChanged));

    private static readonly DependencyProperty TrackerProperty = DependencyProperty.RegisterAttached(
        "Tracker",
        typeof(CutoutTracker),
        typeof(GroupHeaderCutout),
        new PropertyMetadata(null));

    public static FrameworkElement? GetHeader(DependencyObject obj) => (FrameworkElement?)obj.GetValue(HeaderProperty);

    public static void SetHeader(DependencyObject obj, FrameworkElement? value) => obj.SetValue(HeaderProperty, value);

    private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border)
        {
            return;
        }

        if (e.OldValue is FrameworkElement oldHeader && border.GetValue(TrackerProperty) is CutoutTracker existing)
        {
            existing.Detach(oldHeader);
            border.ClearValue(TrackerProperty);
            border.ClearValue(UIElement.ClipProperty);
        }

        if (e.NewValue is FrameworkElement newHeader)
        {
            var tracker = new CutoutTracker(border, newHeader);
            border.SetValue(TrackerProperty, tracker);
            tracker.UpdateClip();
        }
    }

    /// <summary>
    /// 枠線矩形から上辺のラベル占有区間だけを除外したクリップジオメトリを生成する。
    /// </summary>
    internal static Geometry BuildCutoutGeometry(double width, double height, double gapLeft, double gapRight)
    {
        if (width <= 0 || height <= 0)
        {
            return Geometry.Empty;
        }

        // 角丸領域を欠損させないよう安全側の最小余白を確保する。
        var clampedLeft = Math.Max(0.0, gapLeft);
        var clampedRight = Math.Min(width, gapRight);
        if (clampedRight <= clampedLeft + 0.5)
        {
            var rect = new RectangleGeometry(new Rect(0, 0, width, height));
            rect.Freeze();
            return rect;
        }

        var geometry = new PathGeometry { FillRule = FillRule.EvenOdd };

        var outer = new PathFigure { StartPoint = new Point(0, 0), IsClosed = true, IsFilled = true };
        outer.Segments.Add(new LineSegment(new Point(width, 0), isStroked: false));
        outer.Segments.Add(new LineSegment(new Point(width, height), isStroked: false));
        outer.Segments.Add(new LineSegment(new Point(0, height), isStroked: false));
        geometry.Figures.Add(outer);

        // 上辺 1px 枠線（y=0..1、丸め誤差を考慮して -1..2）だけをラベル幅でくり抜く。
        var hole = new PathFigure { StartPoint = new Point(clampedLeft, -1.0), IsClosed = true, IsFilled = true };
        hole.Segments.Add(new LineSegment(new Point(clampedRight, -1.0), isStroked: false));
        hole.Segments.Add(new LineSegment(new Point(clampedRight, 2.0), isStroked: false));
        hole.Segments.Add(new LineSegment(new Point(clampedLeft, 2.0), isStroked: false));
        geometry.Figures.Add(hole);

        geometry.Freeze();
        return geometry;
    }

    private sealed class CutoutTracker
    {
        private readonly Border _border;
        private readonly FrameworkElement _header;
        private double _lastW = -1;
        private double _lastH = -1;
        private double _lastLeft = double.NaN;
        private double _lastRight = double.NaN;

        public CutoutTracker(Border border, FrameworkElement header)
        {
            _border = border;
            _header = header;
            _border.SizeChanged += OnMetricsChanged;
            _header.SizeChanged += OnMetricsChanged;
            _border.Loaded += OnLoaded;
            _header.Loaded += OnLoaded;
        }

        public void Detach(FrameworkElement oldHeader)
        {
            _border.SizeChanged -= OnMetricsChanged;
            oldHeader.SizeChanged -= OnMetricsChanged;
            _border.Loaded -= OnLoaded;
            oldHeader.Loaded -= OnLoaded;
        }

        private void OnLoaded(object? sender, RoutedEventArgs e) => UpdateClip();

        private void OnMetricsChanged(object? sender, SizeChangedEventArgs e) => UpdateClip();

        public void UpdateClip()
        {
            var w = _border.ActualWidth;
            var h = _border.ActualHeight;
            var hw = _header.ActualWidth;
            if (w <= 0 || h <= 0 || hw <= 0)
            {
                return;
            }

            double left;
            try
            {
                left = _header.TranslatePoint(new Point(0, 0), _border).X;
            }
            catch (InvalidOperationException)
            {
                // まだ同じビジュアルツリーに属していない場合は Margin.Left を仮使用し、Loaded で再計算する。
                left = _header.Margin.Left;
            }

            var right = left + hw;
            if (Math.Abs(w - _lastW) < 0.1
                && Math.Abs(h - _lastH) < 0.1
                && Math.Abs(left - _lastLeft) < 0.1
                && Math.Abs(right - _lastRight) < 0.1)
            {
                return;
            }

            _lastW = w;
            _lastH = h;
            _lastLeft = left;
            _lastRight = right;
            _border.Clip = BuildCutoutGeometry(w, h, left, right);
        }
    }
}

