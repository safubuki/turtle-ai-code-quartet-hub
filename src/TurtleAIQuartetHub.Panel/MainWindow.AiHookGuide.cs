using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace TurtleAIQuartetHub.Panel;

public partial class MainWindow
{
    private double _aiHookGuideSettingsScrollOffset;
    private FrameworkElement? _aiHookGuideReturnFocus;
    private int _aiHookGuideNavigationVersion;

    private void OpenAiHookGuideButton_Click(object sender, RoutedEventArgs e) => ShowAiHookGuide();

    // 設定の編集状態をそのまま保持し、同じ枠の中で説明ページへ切り替える。
    private void ShowAiHookGuide()
    {
        if (_isBusy || SettingsOverlay.Visibility != Visibility.Visible
            || AiHookGuidePanel.Visibility == Visibility.Visible
            || AiHookSettingsPreviewOverlay.Visibility == Visibility.Visible) return;

        SuppressFocusedSlotReassertForPanelInput();
        _aiHookGuideSettingsScrollOffset = SettingsScrollViewer.VerticalOffset;
        _aiHookGuideReturnFocus = OpenAiHookGuideButton;
        var navigationVersion = ++_aiHookGuideNavigationVersion;
        SettingsMainContentGrid.Visibility = Visibility.Collapsed;
        AiHookGuidePanel.Visibility = Visibility.Visible;
        AiHookGuidePanel.Reset();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (navigationVersion == _aiHookGuideNavigationVersion
                && SettingsOverlay.Visibility == Visibility.Visible
                && AiHookGuidePanel.Visibility == Visibility.Visible)
            {
                AiHookGuidePanel.FocusReturnButton();
            }
        });
    }

    private void AiHookGuide_CloseRequested(object? sender, EventArgs e) => HideAiHookGuide();

    private void HideAiHookGuide(bool restoreFocus = true)
    {
        var navigationVersion = ++_aiHookGuideNavigationVersion;
        var wasOpen = AiHookGuidePanel.Visibility == Visibility.Visible;
        var returnFocus = _aiHookGuideReturnFocus;
        _aiHookGuideReturnFocus = null;
        AiHookGuidePanel.Visibility = Visibility.Collapsed;
        SettingsMainContentGrid.Visibility = Visibility.Visible;
        if (!restoreFocus || !wasOpen) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (navigationVersion != _aiHookGuideNavigationVersion
                || SettingsOverlay.Visibility != Visibility.Visible
                || AiHookGuidePanel.Visibility == Visibility.Visible) return;

            SettingsScrollViewer.ScrollToVerticalOffset(_aiHookGuideSettingsScrollOffset);
            if (returnFocus is { IsVisible: true })
            {
                FocusManager.SetFocusedElement(this, returnFocus);
                returnFocus.Focus();
            }
        });
    }
}
