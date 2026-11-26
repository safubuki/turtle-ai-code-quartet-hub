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
        if (_isBusy || IsStoredAssignArmed)
        {
            return;
        }

        SuppressFocusedSlotReassertForPanelInput();
        slot.RefreshWorkspaceLinks();
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

        PopulateSlotContextMenuItems(menu, slot, card, includePowerAction: false);
        return menu;
    }

    // 極小表示（円形オービット・ダイヤル）の右クリックメニュー。
    // 扇（スロット）上では先頭に「起動する／閉じる」を置き、続けて縮小表示と同じスロット操作一式を並べ、
    // 末尾に極小固有の「普段は透過する（10% 刻みサブメニュー）」「画面の中央へ戻す」を添える。
    // 扇以外（中央ボタン等）では極小固有の 2 項目のみを表示する。
    internal ContextMenu BuildMicroDialContextMenu(
        WindowSlot? slot,
        bool ghostOpacityEnabled,
        int ghostOpacityPercent,
        Action<bool, int> onGhostOpacityChanged,
        Action onRecenter)
    {
        var menu = new ContextMenu();
        if (TryFindResource("PanelContextMenuStyle") is Style style)
        {
            menu.Style = style;
        }

        SuppressFocusedSlotReassertForPanelInput();
        if (slot is not null)
        {
            slot.RefreshWorkspaceLinks();
            var tagHost = new Border { Tag = slot };
            PopulateSlotContextMenuItems(menu, slot, tagHost, includePowerAction: true);
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(CreateMicroGhostOpacitySubmenu(ghostOpacityEnabled, ghostOpacityPercent, onGhostOpacityChanged));

        var recenterItem = new MenuItem
        {
            Header = "画面の中央へ戻す",
            Icon = CreateStoredMenuGlyphIcon("\uE81D", StoredMenuIconBrush)
        };
        recenterItem.Click += (_, _) => onRecenter();
        menu.Items.Add(recenterItem);

        return menu;
    }

    private MenuItem CreateMicroGhostOpacitySubmenu(
        bool ghostOpacityEnabled,
        int ghostOpacityPercent,
        Action<bool, int> onGhostOpacityChanged)
    {
        var normalizedPercent = AppConfig.NormalizeMicroGhostOpacityPercent(ghostOpacityPercent);
        var submenu = new MenuItem
        {
            Header = "普段は透過する（ゴースト表示）",
            Icon = ghostOpacityEnabled ? CreateStoredMenuGlyphIcon("\uE73E", StoredMenuAccentBrush) : null,
            InputGestureText = ghostOpacityEnabled ? $"{normalizedPercent}%" : "OFF",
            ToolTip = "マウスが乗っていない間の極小表示の濃さ（不透明度）を 10% 刻みで選びます。"
        };

        for (var percent = AppConfig.MinMicroGhostOpacityPercent;
             percent <= AppConfig.MaxMicroGhostOpacityPercent;
             percent += AppConfig.MicroGhostOpacityStepPercent)
        {
            var targetPercent = percent;
            var isSelected = ghostOpacityEnabled && normalizedPercent == targetPercent;
            var item = CreateStoredMenuItem(
                FormatMicroGhostOpacityLabel(targetPercent),
                isSelected ? CreateStoredMenuGlyphIcon("\uE73E", StoredMenuAccentBrush) : new TextBlock(),
                isEnabled: true,
                () => onGhostOpacityChanged(true, targetPercent));
            if (isSelected)
            {
                item.InputGestureText = "選択中";
            }

            submenu.Items.Add(item);
        }

        submenu.Items.Add(new Separator());

        var disableSelected = !ghostOpacityEnabled;
        var disableItem = CreateStoredMenuItem(
            "透過しない（100%）",
            disableSelected ? CreateStoredMenuGlyphIcon("\uE73E", StoredMenuAccentBrush) : new TextBlock(),
            isEnabled: true,
            () => onGhostOpacityChanged(false, normalizedPercent));
        if (disableSelected)
        {
            disableItem.InputGestureText = "選択中";
        }

        submenu.Items.Add(disableItem);
        return submenu;
    }

    internal static string FormatMicroGhostOpacityLabel(int percent) => percent switch
    {
        10 => "10%（もっとも薄い）",
        20 => "20%（かなり薄い）",
        30 => "30%（標準・薄め）",
        50 => "50%（半透明）",
        90 => "90%（ほぼくっきり）",
        _ => $"{percent}%"
    };

    private void PopulateSlotContextMenuItems(
        ContextMenu menu,
        WindowSlot slot,
        FrameworkElement card,
        bool includePowerAction)
    {
        if (includePowerAction)
        {
            var powerItem = CreateSlotPowerMenuItem(slot, card);
            menu.Items.Add(powerItem);
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(CreateSlotApplicationSubmenu("IDE", "", slot, slot.IdeApplicationOptions));
        menu.Items.Add(CreateSlotApplicationSubmenu("CLI", "", slot, slot.CliApplicationOptions));

        menu.Items.Add(new Separator());
        var openFolderItem = CreateStoredMenuItem(
            "エクスプローラーで開く",
            CreateStoredMenuGlyphIcon("", StoredMenuIconBrush),
            slot.CanOpenWorkspaceFolder,
            () => OpenSlotWorkspaceFolderButton_Click(card, new RoutedEventArgs(MenuItem.ClickEvent, card)));
        openFolderItem.ToolTip = slot.WorkspaceFolderToolTip;
        menu.Items.Add(openFolderItem);

        var gitHubItem = CreateStoredMenuItem(
            "GitHub を開く",
            CreateStoredMenuGitHubIcon(),
            slot.CanOpenGitHub,
            () => OpenSlotGitHubButton_Click(card, new RoutedEventArgs(MenuItem.ClickEvent, card)));
        gitHubItem.ToolTip = slot.GitHubToolTip;
        menu.Items.Add(gitHubItem);

        var monitorCount = _windowArranger.GetMonitorCount();
        var displayItem = CreateStoredMenuItem(
            "次のディスプレイへ移動",
            CreateStoredMenuGlyphIcon("", StoredMenuIconBrush),
            slot.WindowStatus == SlotWindowStatus.Ready && monitorCount > 1,
            () => MoveSlotToNextDisplayButton_Click(card, new RoutedEventArgs(MenuItem.ClickEvent, card)));
        displayItem.InputGestureText = slot.HasDisplayBadge ? slot.DisplayBadgeText : string.Empty;
        displayItem.ToolTip = monitorCount <= 1
            ? "利用可能なディスプレイが1枚のため移動できません。"
            : slot.WindowStatus == SlotWindowStatus.Ready
                ? "このパネルだけ次のディスプレイへ移動（ベースに戻すまで巡回）"
                : "起動中のウィンドウがありません。";
        menu.Items.Add(displayItem);

        menu.Items.Add(new Separator());
        // 標準表示のカードでクリアの横に出す停止ボタンと同じ。作業中のときだけ出す。
        if (slot.AiActivity == AiActivityState.Working)
        {
            var stopAiItem = CreateStoredMenuItem(
                "AI の作業中の表示を止める",
                CreateStoredMenuGlyphIcon("\uE71A", StoredMenuIconBrush),
                isEnabled: true,
                () => StopSlotAiActivity(slot));
            stopAiItem.ToolTip = "AI の処理は止まりません。AI 側で停止したあとに押してください。";
            menu.Items.Add(stopAiItem);
        }

        var storeItem = CreateStoredMenuItem(
            "控えへ移動",
            CreateStoredMenuGlyphIcon("", StoredMenuIconBrush),
            slot.HasPanelContent,
            () => StoreSlotButton_Click(card, new RoutedEventArgs(MenuItem.ClickEvent, card)));
        storeItem.ToolTip = "ウィンドウを閉じて、このパネルの内容を控えに移します。";
        menu.Items.Add(storeItem);

        var clearItem = CreateStoredMenuItem(
            "登録内容をクリア...",
            CreateStoredMenuGlyphIcon("", StoredMenuDangerBrush),
            slot.HasPanelContent,
            () => ClearSlotPanelInfoButton_Click(card, new RoutedEventArgs(MenuItem.ClickEvent, card)));
        clearItem.Foreground = StoredMenuDangerBrush;
        clearItem.ToolTip = "このスロットの登録内容を空きに戻します（フォルダは削除されません）。";
        menu.Items.Add(clearItem);
    }

    private MenuItem CreateSlotPowerMenuItem(WindowSlot slot, FrameworkElement card)
    {
        if (slot.WindowStatus == SlotWindowStatus.Ready)
        {
            var closeItem = CreateStoredMenuItem(
                "閉じる",
                CreateStoredMenuGlyphIcon("\uE7E8", StoredMenuDangerBrush),
                isEnabled: !_isBusy,
                () => SlotMainActionButton_Click(card, new RoutedEventArgs(MenuItem.ClickEvent, card)));
            closeItem.Foreground = StoredMenuDangerBrush;
            closeItem.ToolTip = $"スロット {slot.Name} のウィンドウを閉じます。";
            return closeItem;
        }

        if (slot.WindowStatus == SlotWindowStatus.Launching)
        {
            var launchingItem = CreateStoredMenuItem(
                "起動中...",
                CreateStoredMenuGlyphIcon("\uE7E8", StoredMenuIconBrush),
                isEnabled: false,
                () => { });
            launchingItem.ToolTip = $"スロット {slot.Name} を起動しています。";
            return launchingItem;
        }

        var app = _statusStore.FindApplication(slot.ApplicationId);
        var canLaunch = !_isBusy && app is { IsAvailable: true };
        var launchItem = CreateStoredMenuItem(
            "起動する",
            CreateStoredMenuGlyphIcon("\uE768", StoredMenuAccentBrush),
            isEnabled: canLaunch,
            () => SlotMainActionButton_Click(card, new RoutedEventArgs(MenuItem.ClickEvent, card)));
        launchItem.ToolTip = canLaunch
            ? $"スロット {slot.Name} の {app!.DisplayName} を起動します。"
            : app?.ToolTip ?? $"スロット {slot.Name} の起動対象アプリが利用できません。";
        return launchItem;
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
