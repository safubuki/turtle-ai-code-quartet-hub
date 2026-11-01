using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel;

// 縮小表示のスロットカードの右クリックメニュー。縮小表示に無い標準表示のボタン
// （IDE / CLI の切り替え、フォルダ・GitHub を開く、ディスプレイ移動、控え移動、クリア）を
// 同じ処理で呼べるようにする。メニューは開くたびに作り直して最新の状態を反映する。
public partial class MainWindow
{
    private void CompactSlotCard_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WindowSlot slot } card)
        {
            return;
        }

        // 名前の編集中は、入力欄の切り取り・貼り付けメニューを優先する。
        if (FindAncestorOrSelf<TextBox>(e.OriginalSource as DependencyObject) is { IsReadOnly: false })
        {
            return;
        }

        e.Handled = true;
        if (_isBusy || IsCompactStoredAssignArmed)
        {
            return;
        }

        SuppressFocusedSlotReassertForPanelInput();
        var menu = BuildCompactSlotContextMenu(slot, card);
        menu.PlacementTarget = card;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private ContextMenu BuildCompactSlotContextMenu(WindowSlot slot, FrameworkElement card)
    {
        var menu = new ContextMenu();
        if (TryFindResource("PanelContextMenuStyle") is Style style)
        {
            menu.Style = style;
        }

        menu.Items.Add(CreateSlotApplicationSubmenu("IDE", "", slot, slot.IdeApplicationOptions));
        menu.Items.Add(CreateSlotApplicationSubmenu("CLI", "", slot, slot.CliApplicationOptions));

        menu.Items.Add(new Separator());
        var openFolderItem = CreateStoredMenuItem(
            "エクスプローラーで開く",
            CreateStoredMenuGlyphIcon("", StoredMenuIconBrush),
            slot.CanOpenWorkspaceFolder,
            () => OpenSlotWorkspaceFolderButton_Click(card, new RoutedEventArgs()));
        openFolderItem.ToolTip = slot.WorkspaceFolderToolTip;
        menu.Items.Add(openFolderItem);

        var gitHubItem = CreateStoredMenuItem(
            "GitHub を開く",
            CreateStoredMenuGitHubIcon(),
            slot.CanOpenGitHub,
            () => OpenSlotGitHubButton_Click(card, new RoutedEventArgs()));
        gitHubItem.ToolTip = slot.GitHubToolTip;
        menu.Items.Add(gitHubItem);

        var monitorCount = _windowArranger.GetMonitorCount();
        var displayItem = CreateStoredMenuItem(
            "次のディスプレイへ移動",
            CreateStoredMenuGlyphIcon("", StoredMenuIconBrush),
            slot.WindowStatus == SlotWindowStatus.Ready && monitorCount > 1,
            () => MoveSlotToNextDisplayButton_Click(card, new RoutedEventArgs()));
        displayItem.InputGestureText = slot.HasDisplayBadge ? slot.DisplayBadgeText : string.Empty;
        displayItem.ToolTip = monitorCount <= 1
            ? "利用可能なディスプレイが1枚のため移動できません。"
            : slot.WindowStatus == SlotWindowStatus.Ready
                ? "このパネルだけ次のディスプレイへ移動（ベースに戻すまで巡回）"
                : "起動中のウィンドウがありません。";
        menu.Items.Add(displayItem);

        menu.Items.Add(new Separator());
        var storeItem = CreateStoredMenuItem(
            "控えへ移動",
            CreateStoredMenuGlyphIcon("", StoredMenuIconBrush),
            slot.HasPanelContent,
            () => StoreSlotButton_Click(card, new RoutedEventArgs()));
        storeItem.ToolTip = "ウィンドウを閉じて、このパネルの内容を控えに移します。";
        menu.Items.Add(storeItem);

        var clearItem = CreateStoredMenuItem(
            "登録内容をクリア...",
            CreateStoredMenuGlyphIcon("", StoredMenuDangerBrush),
            slot.HasPanelContent,
            () => ClearSlotPanelInfoButton_Click(card, new RoutedEventArgs()));
        clearItem.Foreground = StoredMenuDangerBrush;
        clearItem.ToolTip = "このスロットの登録内容を空きに戻します（フォルダは削除されません）。";
        menu.Items.Add(clearItem);
        return menu;
    }

    private MenuItem CreateSlotApplicationSubmenu(
        string header,
        string glyph,
        WindowSlot slot,
        IEnumerable<SlotApplicationOption> options)
    {
        var submenu = new MenuItem
        {
            Header = header,
            Icon = CreateStoredMenuGlyphIcon(glyph, StoredMenuIconBrush)
        };

        // インストールされていない（未検出の）アプリは選べないので、メニューには出さない。
        foreach (var option in options.Where(option => option.IsAvailable))
        {
            var applicationId = option.ApplicationId;
            var item = CreateStoredMenuItem(
                option.ShortName,
                option.IsSelected ? CreateStoredMenuGlyphIcon("", StoredMenuAccentBrush) : new TextBlock(),
                isEnabled: true,
                async () => await SelectSlotWorkspaceApplicationAsync(slot, applicationId));
            item.ToolTip = option.ToolTip;
            if (option.IsSelected)
            {
                item.InputGestureText = "選択中";
                submenu.InputGestureText = option.ShortName;
            }

            submenu.Items.Add(item);
        }

        submenu.IsEnabled = submenu.Items.Count > 0;
        return submenu;
    }
}
