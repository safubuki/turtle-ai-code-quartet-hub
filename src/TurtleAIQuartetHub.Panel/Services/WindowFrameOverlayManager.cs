using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

public sealed class WindowFrameOverlayManager : IDisposable
{
    private const int OverlayPadding = 5;
    private readonly WindowArranger _windowArranger;
    private readonly Dictionary<string, SlotFrameOverlayWindow> _overlays = new(StringComparer.OrdinalIgnoreCase);

    public WindowFrameOverlayManager(WindowArranger windowArranger)
    {
        _windowArranger = windowArranger;
    }

    public void Update(IEnumerable<WindowSlot> slots, bool overlaysVisible)
    {
        if (!overlaysVisible)
        {
            HideAll();
            return;
        }

        var visibleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in slots)
        {
            if (!ShouldShowOverlay(slot)
                || !_windowArranger.TryGetWindowBounds(slot.WindowHandle, out var bounds))
            {
                Hide(slot.Name);
                continue;
            }

            var overlay = GetOrCreate(slot.Name);
            overlay.ApplyVisual(GetVisual(slot));
            overlay.EnsureShown();

            var overlayBounds = new WindowArranger.WindowBounds(
                bounds.Left - OverlayPadding,
                bounds.Top - OverlayPadding,
                bounds.Width + OverlayPadding * 2,
                bounds.Height + OverlayPadding * 2);

            overlay.UpdateBounds(overlayBounds);
            _windowArranger.PositionOverlayAbove(overlay.Handle, slot.WindowHandle, overlayBounds);
            visibleKeys.Add(slot.Name);
        }

        foreach (var entry in _overlays)
        {
            if (!visibleKeys.Contains(entry.Key))
            {
                entry.Value.Hide();
            }
        }
    }

    public void HideAll()
    {
        foreach (var overlay in _overlays.Values)
        {
            overlay.Hide();
        }
    }

    private SlotFrameOverlayWindow GetOrCreate(string slotName)
    {
        if (_overlays.TryGetValue(slotName, out var overlay))
        {
            return overlay;
        }

        overlay = new SlotFrameOverlayWindow();
        _overlays[slotName] = overlay;
        return overlay;
    }

    private static bool ShouldShowOverlay(WindowSlot slot)
    {
        return slot.WindowHandle != IntPtr.Zero
            && slot.WindowStatus == SlotWindowStatus.Ready
            && !slot.IsHidden;
    }

    private static FrameVisual GetVisual(WindowSlot slot)
    {
        return slot.AiStatus switch
        {
            AiStatus.Running => new FrameVisual(ColorFromHex("#49E88F"), ColorFromHex("#49E88F"), 3.5, 18, slot.IsFocused ? 0.9 : 0.72),
            AiStatus.Completed => new FrameVisual(ColorFromHex("#43C8FF"), ColorFromHex("#43C8FF"), 3.2, 17, slot.IsFocused ? 0.88 : 0.68),
            AiStatus.WaitingForConfirmation => new FrameVisual(ColorFromHex("#F2CA57"), ColorFromHex("#F2CA57"), 3.4, 18, slot.IsFocused ? 0.9 : 0.74),
            AiStatus.Error => new FrameVisual(ColorFromHex("#E37B70"), ColorFromHex("#E37B70"), 3.2, 16, slot.IsFocused ? 0.84 : 0.64),
            AiStatus.NeedsAttention => new FrameVisual(ColorFromHex("#C9A441"), ColorFromHex("#C9A441"), 3.2, 16, slot.IsFocused ? 0.84 : 0.64),
            _ => new FrameVisual(ColorFromHex("#68736C"), ColorFromHex("#68736C"), slot.IsFocused ? 3.0 : 2.4, 14, slot.IsFocused ? 0.74 : 0.46)
        };
    }

    private static Color ColorFromHex(string hex)
    {
        return (Color)ColorConverter.ConvertFromString(hex);
    }

    private void Hide(string slotName)
    {
        if (_overlays.TryGetValue(slotName, out var overlay))
        {
            overlay.Hide();
        }
    }

    public void Dispose()
    {
        foreach (var overlay in _overlays.Values)
        {
            overlay.Close();
        }

        _overlays.Clear();
    }

    private readonly record struct FrameVisual(Color BorderColor, Color GlowColor, double BorderThickness, double BlurRadius, double Opacity);

    private sealed class SlotFrameOverlayWindow : Window
    {
        private readonly Border _frameBorder;
        private readonly DropShadowEffect _glowEffect;

        public SlotFrameOverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = false;
            IsHitTestVisible = false;
            Focusable = false;
            SnapsToDevicePixels = true;

            _glowEffect = new DropShadowEffect
            {
                ShadowDepth = 0,
                BlurRadius = 15,
                Opacity = 0.6
            };

            _frameBorder = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(12),
                Effect = _glowEffect
            };

            Content = new Grid
            {
                IsHitTestVisible = false,
                Margin = new Thickness(3),
                Children = { _frameBorder }
            };
        }

        public IntPtr Handle => new WindowInteropHelper(this).Handle;

        public void EnsureShown()
        {
            if (!IsVisible)
            {
                Show();
            }
        }

        public void UpdateBounds(WindowArranger.WindowBounds bounds)
        {
            Left = bounds.Left;
            Top = bounds.Top;
            Width = Math.Max(12, bounds.Width);
            Height = Math.Max(12, bounds.Height);
        }

        public void ApplyVisual(FrameVisual visual)
        {
            _frameBorder.BorderBrush = new SolidColorBrush(visual.BorderColor);
            _frameBorder.BorderThickness = new Thickness(visual.BorderThickness);
            _frameBorder.CornerRadius = new CornerRadius(12 + visual.BorderThickness);
            _frameBorder.Opacity = visual.Opacity;
            _glowEffect.Color = visual.GlowColor;
            _glowEffect.BlurRadius = visual.BlurRadius;
            _glowEffect.Opacity = Math.Min(0.92, visual.Opacity + 0.08);
        }
    }
}
