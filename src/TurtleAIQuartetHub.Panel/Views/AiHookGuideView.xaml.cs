using System.Windows;
using System.Windows.Controls;

namespace TurtleAIQuartetHub.Panel.Views;

public partial class AiHookGuideView : UserControl
{
    public AiHookGuideView()
    {
        InitializeComponent();
    }

    public event EventHandler? CloseRequested;

    public void Reset()
    {
        GuideFlowTab.IsChecked = true;
        SelectSection("flow");
    }

    public void FocusReturnButton() => ReturnToSettingsButton.Focus();

    private void GuideSection_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string section }) SelectSection(section);
    }

    private void SelectSection(string section)
    {
        FlowSection.Visibility = section == "flow" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSection.Visibility = section == "settings" ? Visibility.Visible : Visibility.Collapsed;
        SignalsSection.Visibility = section == "signals" ? Visibility.Visible : Visibility.Collapsed;
        GuideScrollViewer.ScrollToTop();
    }

    private void ReturnButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
