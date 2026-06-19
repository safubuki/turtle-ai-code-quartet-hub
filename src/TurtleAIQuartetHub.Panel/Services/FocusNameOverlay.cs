using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace TurtleAIQuartetHub.Panel.Services;

// フォーカスしたスロットの名前を、そのスロットが最大化される対象ディスプレイの中央へ
// フワッと出して数秒で消すためのオーバーレイ。クリックスルー・非アクティブ・最前面で、
// 下の作業ウィンドウの操作も Alt+Tab の並びも一切邪魔しない。
// 表示中に別フォーカスへ切り替われば Show が後勝ちで差し替え、解除されれば Hide で即消す。
public sealed class FocusNameOverlay
{
    private static readonly TimeSpan FadeInDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(2000);
    private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan FadeOutOnHideDuration = TimeSpan.FromMilliseconds(320);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private readonly WindowArranger _windowArranger;

    private OverlayWindow? _window;
    private TextBlock? _slotLabelText;
    private TextBlock? _titleText;
    private TextBlock? _appNameText;
    private Border? _card;
    private Storyboard? _activeStoryboard;

    public FocusNameOverlay(WindowArranger windowArranger)
    {
        _windowArranger = windowArranger;
    }

    // スロット名（パネルD）・タイトル・アプリ名の 3 行を、monitorIndex（GetSlotMonitorIndex の
    // 正規化済みインデックス）の作業領域中央へ出す。フェードイン → 数秒保持 → フェードアウト。
    public void Show(string slotLabel, string title, string appName, int monitorIndex)
    {
        EnsureWindow();
        if (_window is null || _card is null
            || _slotLabelText is null || _titleText is null || _appNameText is null)
        {
            return;
        }

        _slotLabelText.Text = slotLabel;
        _titleText.Text = title;
        _appNameText.Text = appName;
        _slotLabelText.Visibility = string.IsNullOrWhiteSpace(slotLabel) ? Visibility.Collapsed : Visibility.Visible;
        _titleText.Visibility = string.IsNullOrWhiteSpace(title) ? Visibility.Collapsed : Visibility.Visible;
        _appNameText.Visibility = string.IsNullOrWhiteSpace(appName) ? Visibility.Collapsed : Visibility.Visible;

        StopActiveAnimation();

        // 中身を確定させてからサイズを測り、対象ディスプレイ作業領域の中央へ置く。
        _window.Show();
        PositionOnMonitor(monitorIndex);

        var storyboard = new Storyboard();

        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(FadeInDuration))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(FadeInDuration + HoldDuration)));
        // 消えるときは緩やかに入って緩やかに抜ける S 字カーブで、ふわっと溶けるように消す。
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(FadeInDuration + HoldDuration + FadeOutDuration))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
        Storyboard.SetTarget(fade, _card);
        Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));
        storyboard.Children.Add(fade);

        // 出るときは少し下から持ち上げ、消えるときはそのまま少し上へ抜けて余韻を出す。
        var move = new DoubleAnimationUsingKeyFrames();
        move.KeyFrames.Add(new EasingDoubleKeyFrame(10.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        move.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(FadeInDuration))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
        move.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(FadeInDuration + HoldDuration)));
        move.KeyFrames.Add(new EasingDoubleKeyFrame(-8.0, KeyTime.FromTimeSpan(FadeInDuration + HoldDuration + FadeOutDuration))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
        Storyboard.SetTarget(move, _card);
        Storyboard.SetTargetProperty(move, new PropertyPath("RenderTransform.Y"));
        storyboard.Children.Add(move);

        storyboard.Completed += (_, _) => HideImmediate();
        _activeStoryboard = storyboard;
        storyboard.Begin();
    }

    // フォーカス解除時に呼ぶ。表示中ならサッと（ごく短いフェードで）消す。
    public void Hide()
    {
        if (_window is null || _card is null || _window.Visibility != Visibility.Visible)
        {
            return;
        }

        StopActiveAnimation();

        var fadeOut = new DoubleAnimation(_card.Opacity, 0.0, FadeOutOnHideDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        fadeOut.Completed += (_, _) => HideImmediate();
        _activeStoryboard = null;
        _card.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    public void Close()
    {
        StopActiveAnimation();
        _window?.Close();
        _window = null;
    }

    private void HideImmediate()
    {
        StopActiveAnimation();
        if (_card is not null)
        {
            _card.BeginAnimation(UIElement.OpacityProperty, null);
            _card.Opacity = 0.0;
        }

        _window?.Hide();
    }

    private void StopActiveAnimation()
    {
        if (_activeStoryboard is not null)
        {
            _activeStoryboard.Stop();
            _activeStoryboard = null;
        }

        // Storyboard.Stop() はアニメ対象プロパティを基準値（Opacity=0, Y=10）へ戻してしまう。
        // 解除フェードはその直後に現在値から始めたいので、いま見えている値で固定し直して
        // カクッと戻らないようにする。ここで一度クリアし、現値をローカル値として書き戻す。
        if (_card is not null)
        {
            var opacity = _card.Opacity;
            _card.BeginAnimation(UIElement.OpacityProperty, null);
            _card.Opacity = opacity;

            if (_card.RenderTransform is TranslateTransform translate)
            {
                var y = translate.Y;
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.Y = y;
            }
        }
    }

    private void EnsureWindow()
    {
        if (_window is not null)
        {
            return;
        }

        // 1 段目: スロット名（パネルD）。アクセント緑で見出し的に控えめなサイズ。
        _slotLabelText = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x45, 0xD4, 0x83)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
        };

        // 2 段目: タイトル。最大・白で主役。
        _titleText = new TextBlock
        {
            FontSize = 42,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xEF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 8, 0, 0),
        };

        // 3 段目: アプリ名（VS Code 等）。ミュート色で小さく添える。
        _appNameText = new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeights.Normal,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA5, 0x9E)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(_slotLabelText);
        stack.Children.Add(_titleText);
        stack.Children.Add(_appNameText);

        _card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x10, 0x12, 0x11)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x45, 0xD4, 0x83)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(44, 28, 44, 30),
            Opacity = 0.0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new TranslateTransform(0, 10),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 28,
                ShadowDepth = 0,
                Opacity = 0.55,
            },
            Child = stack,
        };

        _window = new OverlayWindow
        {
            Content = _card,
        };

        _window.SourceInitialized += (_, _) => ApplyClickThroughStyles();
    }

    private void ApplyClickThroughStyles()
    {
        if (_window is null)
        {
            return;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(handle, GWL_EXSTYLE);
        exStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED;
        SetWindowLong(handle, GWL_EXSTYLE, exStyle);
    }

    // 対象ディスプレイの作業領域中央へ、物理ピクセル指定で置く。WindowArranger の座標は
    // ネイティブ px なので、DIP 変換を挟まず SetWindowPos で直接合わせるのが確実。
    private void PositionOnMonitor(int monitorIndex)
    {
        if (_window is null)
        {
            return;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero
            || !_windowArranger.TryGetMonitorWorkArea(monitorIndex, out var work)
            || work.Width <= 0 || work.Height <= 0)
        {
            return;
        }

        // 自然サイズを測ってから中央に配置する。
        _window.UpdateLayout();
        _card?.UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(_window);
        var widthPx = (int)Math.Ceiling(_window.ActualWidth * dpi.DpiScaleX);
        var heightPx = (int)Math.Ceiling(_window.ActualHeight * dpi.DpiScaleY);
        if (widthPx <= 0 || heightPx <= 0)
        {
            return;
        }

        var left = work.Left + ((work.Width - widthPx) / 2);
        var top = work.Top + ((work.Height - heightPx) / 2);

        // SWP_NOSIZE で WPF が計算した実サイズを尊重しつつ、位置だけ作業領域中央へ合わせる。
        SetWindowPos(handle, HWND_TOPMOST, left, top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
    }

    // ShowActivated=false で前面を奪わず出すための薄い Window。
    private sealed class OverlayWindow : Window
    {
        public OverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            IsHitTestVisible = false;
            Focusable = false;
            UseLayoutRounding = true;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
