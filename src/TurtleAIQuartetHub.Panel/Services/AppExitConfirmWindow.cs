using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TurtleAIQuartetHub.Panel.Services;

// 縮小表示・極小表示のときに「アプリを終了しますか？」のカード単体だけを表示する独立透過ウィンドウ。
// 本体 MainWindow を 430x250 へ無理やり縦伸ばししたり、極小ダイヤルの裏から四角い MainWindow を
// 露出させたりせず、余計な背景・縮小画面・黒余白ゼロで確認カードのみを画面へ浮かべる。
internal sealed class AppExitConfirmWindow : Window
{
    private const double CardWidthDip = 372.0;
    private const double ShadowPaddingDip = 12.0;
    private const uint SwpNoSize = 0x0001;
    private static readonly IntPtr HwndTopmost = new(-1);

    private static readonly Brush SurfaceBrush = CreateFrozenBrush(Color.FromRgb(0x15, 0x1A, 0x17));
    private static readonly Brush AccentBrush = CreateFrozenBrush(Color.FromRgb(0x45, 0xD4, 0x83));
    private static readonly Brush AccentDarkBrush = CreateFrozenBrush(Color.FromRgb(0x16, 0x3A, 0x26));
    private static readonly Brush AccentHoverBrush = CreateFrozenBrush(Color.FromRgb(0x1D, 0x4A, 0x31));
    private static readonly Brush TextBrush = CreateFrozenBrush(Color.FromRgb(0xEE, 0xF4, 0xEF));
    private static readonly Brush MutedTextBrush = CreateFrozenBrush(Color.FromRgb(0xA6, 0xBC, 0xB0));
    private static readonly Brush NoticeBgBrush = CreateFrozenBrush(Color.FromRgb(0x14, 0x23, 0x1B));
    private static readonly Brush NoticeBorderBrush = CreateFrozenBrush(Color.FromRgb(0x31, 0x5B, 0x43));
    private static readonly Brush NoticeTextBrush = CreateFrozenBrush(Color.FromRgb(0xCD, 0xE9, 0xD7));
    private static readonly Brush SecondaryBgBrush = CreateFrozenBrush(Color.FromRgb(0x1F, 0x26, 0x22));
    private static readonly Brush SecondaryHoverBrush = CreateFrozenBrush(Color.FromRgb(0x2A, 0x34, 0x2E));
    private static readonly Brush SecondaryBorderBrush = CreateFrozenBrush(Color.FromRgb(0x3B, 0x49, 0x41));
    private static readonly Brush ConfirmTextBrush = CreateFrozenBrush(Color.FromRgb(0xE9, 0xFF, 0xF2));

    private readonly Action _onConfirmed;
    private readonly Action _onCanceled;
    private readonly Rect _anchorScreenRectPx;
    private readonly WindowArranger.WindowBounds? _workAreaPx;
    private readonly Grid _rootGrid;
    private readonly Button _cancelButton;
    private readonly Button _confirmButton;
    private bool _resultDispatched;

    public AppExitConfirmWindow(
        Rect anchorScreenRectPx,
        WindowArranger.WindowBounds? workAreaPx,
        Action onConfirmed,
        Action onCanceled)
    {
        _anchorScreenRectPx = anchorScreenRectPx;
        _workAreaPx = workAreaPx;
        _onConfirmed = onConfirmed;
        _onCanceled = onCanceled;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "終了の確認";

        _cancelButton = CreateActionButton(
            text: "キャンセル",
            background: SecondaryBgBrush,
            hoverBackground: SecondaryHoverBrush,
            border: SecondaryBorderBrush,
            foreground: TextBrush,
            isCancel: true);
        _cancelButton.Margin = new Thickness(0, 0, 8, 0);
        _cancelButton.Click += (_, _) => DispatchCancelAndClose();

        _confirmButton = CreateActionButton(
            text: "終了する",
            background: AccentDarkBrush,
            hoverBackground: AccentHoverBrush,
            border: AccentBrush,
            foreground: ConfirmTextBrush,
            isCancel: false);
        _confirmButton.Click += (_, _) => DispatchConfirmAndClose();

        var iconBadge = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(10),
            Background = AccentDarkBrush,
            BorderBrush = AccentBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = "×",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var headerTexts = new StackPanel();
        headerTexts.Children.Add(new TextBlock
        {
            Text = "アプリを終了しますか？",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextBrush
        });
        headerTexts.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 5, 0, 0),
            Text = "管理中の IDE / CLI ウィンドウは閉じません。",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = MutedTextBrush
        });

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconBadge, 0);
        Grid.SetColumn(headerTexts, 1);
        headerGrid.Children.Add(iconBadge);
        headerGrid.Children.Add(headerTexts);

        var noticeBox = new Border
        {
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(10, 8, 10, 8),
            Background = NoticeBgBrush,
            BorderBrush = NoticeBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new TextBlock
            {
                Text = "パネルのみ終了します。作業中のウィンドウはそのまま残ります。",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                Foreground = NoticeTextBrush,
                TextWrapping = TextWrapping.Wrap
            }
        };

        var buttonRow = new StackPanel
        {
            Margin = new Thickness(0, 16, 0, 0),
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttonRow.Children.Add(_cancelButton);
        buttonRow.Children.Add(_confirmButton);

        var cardGrid = new Grid();
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(headerGrid, 0);
        Grid.SetRow(noticeBox, 1);
        Grid.SetRow(buttonRow, 2);
        cardGrid.Children.Add(headerGrid);
        cardGrid.Children.Add(noticeBox);
        cardGrid.Children.Add(buttonRow);

        var cardBorder = new Border
        {
            Width = CardWidthDip,
            Padding = new Thickness(18),
            Background = SurfaceBrush,
            BorderBrush = AccentBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = cardGrid
        };
        cardBorder.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && !IsInsideButton(e.OriginalSource as DependencyObject))
            {
                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                }
            }
        };

        // BlurEffect を使わない軽量 2 層ソフト影（カード本体だけをくっきり浮かせる）
        var outerShadow = new Border
        {
            Margin = new Thickness(ShadowPaddingDip - 5, ShadowPaddingDip - 2, ShadowPaddingDip - 5, ShadowPaddingDip - 8),
            CornerRadius = new CornerRadius(16),
            Background = CreateFrozenBrush(Color.FromArgb(0x28, 0x00, 0x00, 0x00)),
            IsHitTestVisible = false
        };
        var innerShadow = new Border
        {
            Margin = new Thickness(ShadowPaddingDip - 2, ShadowPaddingDip - 1, ShadowPaddingDip - 2, ShadowPaddingDip - 4),
            CornerRadius = new CornerRadius(14),
            Background = CreateFrozenBrush(Color.FromArgb(0x46, 0x00, 0x00, 0x00)),
            IsHitTestVisible = false
        };

        var cardHost = new Border
        {
            Margin = new Thickness(ShadowPaddingDip),
            Child = cardBorder
        };

        _rootGrid = new Grid
        {
            Background = Brushes.Transparent
        };
        _rootGrid.Children.Add(outerShadow);
        _rootGrid.Children.Add(innerShadow);
        _rootGrid.Children.Add(cardHost);

        Content = _rootGrid;

        SourceInitialized += (_, _) => PositionAtAnchorCenter();
        Loaded += (_, _) =>
        {
            PositionAtAnchorCenter();
            Activate();
            _cancelButton.Focus();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DispatchCancelAndClose();
            }
        };
        Closing += (_, _) =>
        {
            if (!_resultDispatched)
            {
                _resultDispatched = true;
                _onCanceled();
            }
        };
    }

    public void CloseSilently()
    {
        _resultDispatched = true;
        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void FocusDefaultButton()
    {
        try
        {
            Activate();
            _cancelButton.Focus();
        }
        catch (InvalidOperationException)
        {
        }
    }

    // アンカー矩形（縮小パネルまたは極小ダイヤル）の中心へカード中心を合わせ、モニタ作業領域内へクランプする（物理 px）。
    internal static (int LeftPx, int TopPx) ComputeCenteredPositionPx(
        Rect anchorScreenRectPx,
        Size dialogSizePx,
        WindowArranger.WindowBounds? workAreaPx)
    {
        var width = Math.Max(1.0, dialogSizePx.Width);
        var height = Math.Max(1.0, dialogSizePx.Height);
        var left = anchorScreenRectPx.Left + ((anchorScreenRectPx.Width - width) / 2.0);
        var top = anchorScreenRectPx.Top + ((anchorScreenRectPx.Height - height) / 2.0);

        if (workAreaPx is { } area && area.Width > 0 && area.Height > 0)
        {
            var maxLeft = Math.Max(area.Left, area.Left + area.Width - width);
            var maxTop = Math.Max(area.Top, area.Top + area.Height - height);
            left = Math.Clamp(left, area.Left, maxLeft);
            top = Math.Clamp(top, area.Top, maxTop);
        }

        return ((int)Math.Round(left), (int)Math.Round(top));
    }

    internal (
        Size DesiredCardSizeDip,
        bool AllowsTransparencyValue,
        bool CancelIsDefaultCancelKey,
        int ConfirmInvokeCount,
        int CancelInvokeCount) VerifyOffscreenForTesting()
    {
        _rootGrid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _rootGrid.DesiredSize;
        _rootGrid.Arrange(new Rect(size));
        _rootGrid.UpdateLayout();
        return (size, AllowsTransparency, _cancelButton.IsCancel, 0, 0);
    }

    internal void SimulateConfirmClickForTesting() => DispatchConfirmAndClose();

    internal void SimulateCancelClickForTesting() => DispatchCancelAndClose();

    internal BitmapSource RenderOffscreenForPreview()
    {
        _rootGrid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _rootGrid.DesiredSize;
        _rootGrid.Arrange(new Rect(size));
        _rootGrid.UpdateLayout();

        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(size.Width * 2)),
            Math.Max(1, (int)Math.Ceiling(size.Height * 2)),
            192,
            192,
            PixelFormats.Pbgra32);
        rtb.Render(_rootGrid);
        rtb.Freeze();
        return rtb;
    }

    private void PositionAtAnchorCenter()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _rootGrid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = _rootGrid.DesiredSize;
        var widthDip = ActualWidth > 0 ? ActualWidth : desired.Width;
        var heightDip = ActualHeight > 0 ? ActualHeight : desired.Height;
        var dpi = VisualTreeHelper.GetDpi(this);
        var (leftPx, topPx) = ComputeCenteredPositionPx(
            _anchorScreenRectPx,
            new Size(widthDip * dpi.DpiScaleX, heightDip * dpi.DpiScaleY),
            _workAreaPx);
        _ = SetWindowPos(handle, HwndTopmost, leftPx, topPx, 0, 0, SwpNoSize);
    }

    private void DispatchConfirmAndClose()
    {
        if (_resultDispatched)
        {
            return;
        }

        _resultDispatched = true;
        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
        }

        _onConfirmed();
    }

    private void DispatchCancelAndClose()
    {
        if (_resultDispatched)
        {
            return;
        }

        _resultDispatched = true;
        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
        }

        _onCanceled();
    }

    private static Button CreateActionButton(
        string text,
        Brush background,
        Brush hoverBackground,
        Brush border,
        Brush foreground,
        bool isCancel)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 96,
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = foreground,
            Background = background,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            IsCancel = isCancel
        };

        var borderFactory = new FrameworkElementFactory(typeof(Border), "BtnBorder");
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = borderFactory };
        var hoverTrigger = new Trigger { Property = IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hoverBackground, "BtnBorder"));
        template.Triggers.Add(hoverTrigger);

        var focusTrigger = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focusTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, AccentBrush, "BtnBorder"));
        template.Triggers.Add(focusTrigger);

        button.Template = template;
        return button;
    }

    private static bool IsInsideButton(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is Button)
            {
                return true;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
