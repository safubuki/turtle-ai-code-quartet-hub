using System.Windows;
using System.Windows.Input;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel;

// 縮小表示の控えエリア。控えカードを選ぶと上のスロット A〜D が配置先（▶ ここに表示）に変わり、
// 押したスロットにその控えを出す。カードごとの A〜D ボタンは持たない。
// 選択は標準表示の控え一覧と同じ _selectedOverviewStoredPanel を使う（表示モードを切り替えると解除）。
public partial class MainWindow
{
    private bool IsCompactStoredAssignArmed => IsCompactMode && _selectedOverviewStoredPanel is not null;

    private void CompactStoredCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // ドラッグ開始位置の記録は通常の控えカードと同じ。
        StoredPanelCard_PreviewMouseLeftButtonDown(sender, e);

        // ダブルクリック（名前の編集）の 2 回目や、カード内のボタン・編集中の名前は選択の切り替えに使わない。
        if (e.ClickCount != 1
            || _isBusy
            || sender is not FrameworkElement { Tag: StoredPanelSlot storedPanel }
            || IsInteractiveCardChild(e.OriginalSource as DependencyObject, sender as DependencyObject))
        {
            return;
        }

        SelectOverviewStoredPanel(storedPanel.HasContent && !ReferenceEquals(_selectedOverviewStoredPanel, storedPanel)
            ? storedPanel
            : null);
    }

    private void CancelCompactStoredAssignButton_Click(object sender, RoutedEventArgs e)
    {
        SelectOverviewStoredPanel(null);
    }

    private async void ShowSelectedStoredPanelInCompactSlot(WindowSlot targetSlot)
    {
        if (_selectedOverviewStoredPanel is not { } storedPanel)
        {
            return;
        }

        // 選んだあとにクリアされたカードは配置できないので、選択だけ解く。
        if (!storedPanel.HasContent)
        {
            SelectOverviewStoredPanel(null);
            return;
        }

        await ShowStoredPanelFromOverviewAsync(storedPanel, targetSlot);
    }

    private void UpdateCompactStoredAssignState()
    {
        var isArmed = IsCompactStoredAssignArmed;
        SlotAssignTarget.SetIsArmed(CompactBarPanel, isArmed);
        CompactStoredTabsRow.Visibility = isArmed ? Visibility.Collapsed : Visibility.Visible;
        CompactStoredAssignHintRow.Visibility = isArmed ? Visibility.Visible : Visibility.Collapsed;
        if (isArmed)
        {
            CompactStoredAssignHintText.Text = $"「{_selectedOverviewStoredPanel!.DisplayTitle}」の配置先を、上のスロット A〜D から選ぶ";
            CompactStoredAssignHintText.ToolTip = CompactStoredAssignHintText.Text + "（Esc で取消）";
        }
    }
}
