using System.Windows;
using System.Windows.Input;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel;

// 控え一覧からスロットへの配置。一覧でカードを選ぶと、本体のスロット A〜D が配置先（▶ ここに表示）に変わり、
// 押したスロットにその控えを出す。標準表示はカード全体に重ねた案内、縮小表示はカード右端の案内で示す。
// 選択は _selectedOverviewStoredPanel で持ち、表示モードを切り替えたり一覧を閉じたりすると解除する。
public partial class MainWindow
{
    private bool IsStoredAssignArmed => _displayMode != DisplayMode.Micro && _selectedOverviewStoredPanel is not null;

    private async void ShowSelectedStoredPanelInSlot(WindowSlot targetSlot)
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

    // 標準表示のスロットカードに重ねた「ここに表示」。中のボタンやカード本体のクリック（フォーカス切替）へは渡さない。
    private void SlotAssignOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isBusy
            || IsSlotFocusSuppressedByDrag()
            || !IsStoredAssignArmed
            || sender is not FrameworkElement { Tag: WindowSlot slot })
        {
            return;
        }

        ShowSelectedStoredPanelInSlot(slot);
    }

    private void UpdateStoredAssignState()
    {
        var isArmed = IsStoredAssignArmed;
        SlotAssignTarget.SetIsArmed(StandardSlotsGrid, isArmed && _displayMode == DisplayMode.Standard);
        SlotAssignTarget.SetIsArmed(CompactBarPanel, isArmed && _displayMode == DisplayMode.Compact);
    }
}
