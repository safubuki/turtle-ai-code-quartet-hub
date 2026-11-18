using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;
using System.Windows.Threading;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

// 控えカード（標準・縮小・控え一覧のタイル）の右クリックメニュー。
// 各操作はカード上のボタンと同じ処理を呼び、メニューは開くたびに作り直して
// フォルダの有無や GitHub リモートの最新状態を反映する。
public partial class MainWindow
{
    private static readonly Brush StoredMenuIconBrush = CreateFrozenBrush("#9AA59E");
    private static readonly Brush StoredMenuAccentBrush = CreateFrozenBrush("#45D483");
    private static readonly Brush StoredMenuDangerBrush = CreateFrozenBrush("#F2B7AF");

    private void StoredPanelCard_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: StoredPanelSlot storedPanel } card)
        {
            return;
        }

        // 名前の編集中は、入力欄の切り取り・貼り付けメニューを優先する。
        if (FindAncestorOrSelf<TextBox>(e.OriginalSource as DependencyObject) is { IsReadOnly: false })
        {
            return;
        }

        e.Handled = true;
        if (_isBusy)
        {
            return;
        }

        SuppressFocusedSlotReassertForPanelInput();
        storedPanel.RefreshWorkspaceLinks();
        var menu = BuildStoredPanelContextMenu(storedPanel, card);
        menu.PlacementTarget = card;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private ContextMenu BuildStoredPanelContextMenu(StoredPanelSlot storedPanel, FrameworkElement card)
    {
        var menu = new ContextMenu();
        if (TryFindResource("PanelContextMenuStyle") is Style style)
        {
            menu.Style = style;
        }

        var hasContent = storedPanel.HasContent;
        foreach (var slot in _statusStore.Slots)
        {
            var targetSlot = slot;
            var item = CreateStoredMenuItem(
                $"スロット {slot.Name} に表示",
                CreateStoredMenuLetterIcon(slot.Name),
                hasContent,
                async () => await ShowStoredPanelAsync(storedPanel, targetSlot));
            // 配置先に今ある内容を右端に出す（表示すると、その内容は控えに戻る）。
            item.InputGestureText = slot.HasPanelContent ? slot.DisplayTitle : "空き";
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateStoredMenuItem(
            hasContent ? "フォルダを変更..." : "フォルダを登録...",
            CreateStoredMenuGlyphIcon("", StoredMenuIconBrush),
            isEnabled: true,
            () => SelectStoredPanelFolder(storedPanel)));
        menu.Items.Add(CreateStoredMenuItem(
            "名前を編集",
            CreateStoredMenuGlyphIcon("", StoredMenuIconBrush),
            hasContent,
            () => ShowStoredRenameDialog(storedPanel)));

        menu.Items.Add(new Separator());
        var openFolderItem = CreateStoredMenuItem(
            "エクスプローラーで開く",
            CreateStoredMenuGlyphIcon("", StoredMenuIconBrush),
            storedPanel.CanOpenWorkspaceFolder,
            () => OpenStoredPanelFolder(storedPanel));
        openFolderItem.ToolTip = storedPanel.CanOpenWorkspaceFolder
            ? $"エクスプローラで開く: {storedPanel.ExplorerWorkspacePath}"
            : hasContent
                ? "ローカルフォルダが見つからないか、リモートのワークスペースです。"
                : "フォルダが登録されていません。";
        menu.Items.Add(openFolderItem);

        var gitHubItem = CreateStoredMenuItem(
            "GitHub を開く",
            CreateStoredMenuGitHubIcon(),
            storedPanel.CanOpenGitHub,
            () => OpenStoredPanelGitHub(storedPanel));
        gitHubItem.ToolTip = storedPanel.GitHubToolTip;
        menu.Items.Add(gitHubItem);

        if (_statusStore.StoredPanelPages.FirstOrDefault(page => page.Slots.Contains(storedPanel)) is { } row)
        {
            menu.Items.Add(new Separator());
            AddStoredDividerMenuItems(menu, row, fromDivider: false);
        }

        menu.Items.Add(new Separator());
        var clearItem = CreateStoredMenuItem(
            "控えをクリア...",
            CreateStoredMenuGlyphIcon("", StoredMenuDangerBrush),
            hasContent,
            () => ShowClearStoredPanelDialog(storedPanel));
        clearItem.Foreground = StoredMenuDangerBrush;
        clearItem.ToolTip = "この控えの登録内容を空きに戻します（フォルダは削除されません）。";
        menu.Items.Add(clearItem);
        return menu;
    }

    // 控え一覧の区切り線の右クリック。名前の変更・削除と、区切り線そのものの削除。
    private void StoredOverviewDivider_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: StoredPanelPage page } divider)
        {
            return;
        }

        // 名前の編集中は、入力欄の切り取り・貼り付けメニューを優先する。
        if (FindAncestorOrSelf<TextBox>(e.OriginalSource as DependencyObject) is { IsReadOnly: false })
        {
            return;
        }

        e.Handled = true;
        if (_isBusy)
        {
            return;
        }

        var menu = new ContextMenu();
        if (TryFindResource("PanelContextMenuStyle") is Style style)
        {
            menu.Style = style;
        }

        AddStoredDividerMenuItems(menu, page, fromDivider: true);
        menu.PlacementTarget = divider;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    // 区切り線は「段（4 枚の行）の上」に引く。カードからは自分の行の上、区切り線からはその線を操作する。
    private void AddStoredDividerMenuItems(ContextMenu menu, StoredPanelPage row, bool fromDivider)
    {
        if (!row.HasDivider)
        {
            menu.Items.Add(CreateStoredMenuItem(
                "この行の上に区切り線を引く",
                CreateStoredMenuGlyphIcon("", StoredMenuIconBrush),
                isEnabled: true,
                () => SetStoredDivider(row, true)));
            menu.Items.Add(CreateStoredMenuItem(
                "この行の上に名前付きの区切り線を引く...",
                CreateStoredMenuGlyphIcon("", StoredMenuIconBrush),
                isEnabled: true,
                () => BeginStoredPanelPageHeaderEdit(row)));
            return;
        }

        menu.Items.Add(CreateStoredMenuItem(
            row.HasCustomHeader ? "区切り線の名前を変更..." : "区切り線に名前を付ける...",
            CreateStoredMenuGlyphIcon("", StoredMenuIconBrush),
            isEnabled: true,
            () => BeginStoredPanelPageHeaderEdit(row)));
        if (fromDivider && row.HasCustomHeader)
        {
            menu.Items.Add(CreateStoredMenuItem(
                "名前だけ消す",
                CreateStoredMenuGlyphIcon("", StoredMenuIconBrush),
                isEnabled: true,
                () =>
                {
                    _statusStore.ResetStoredPanelPageHeader(row);
                    _statusStore.Message = "区切り線の名前を消しました（線は残ります）。";
                }));
        }

        var removeItem = CreateStoredMenuItem(
            fromDivider ? "区切り線を消す" : "この行の上の区切り線を消す",
            CreateStoredMenuGlyphIcon("", StoredMenuDangerBrush),
            isEnabled: true,
            () => SetStoredDivider(row, false));
        removeItem.Foreground = StoredMenuDangerBrush;
        removeItem.ToolTip = "区切り線と、その名前を消します（控えカードはそのまま残ります）。";
        menu.Items.Add(removeItem);
    }

    private void SetStoredDivider(StoredPanelPage row, bool hasDivider)
    {
        var name = row.CustomHeader;
        _statusStore.SetStoredPanelPageDivider(row, hasDivider);
        _statusStore.Message = hasDivider
            ? "区切り線を引きました。ダブルクリックで名前を付けられます。"
            : string.IsNullOrEmpty(name) ? "区切り線を消しました。" : $"区切り線「{name}」を消しました。";
        // 一覧の高さが変わるので、画面の下にはみ出さないよう位置を合わせ直す。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RepositionStoredOverview);
    }

    private MenuItem CreateStoredMenuItem(string header, object icon, bool isEnabled, Action onClick)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = icon,
            IsEnabled = isEnabled
        };
        item.Click += (_, _) =>
        {
            if (_isBusy)
            {
                return;
            }

            onClick();
        };
        return item;
    }

    private static TextBlock CreateStoredMenuGlyphIcon(string glyph, Brush foreground)
    {
        return new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static TextBlock CreateStoredMenuLetterIcon(string letter)
    {
        return new TextBlock
        {
            Text = letter,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = StoredMenuAccentBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private ShapePath CreateStoredMenuGitHubIcon()
    {
        return new ShapePath
        {
            Data = TryFindResource("GitHubMarkGeometry") as Geometry,
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            Fill = StoredMenuIconBrush
        };
    }

    private void OpenStoredPanelFolder(StoredPanelSlot storedPanel)
    {
        var explorerPath = storedPanel.ExplorerWorkspacePath;
        if (string.IsNullOrWhiteSpace(explorerPath))
        {
            _statusStore.Message = $"{storedPanel.Label} のフォルダが見つかりません: {storedPanel.WorkspacePath}";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = QuoteExplorerArgument(explorerPath),
                UseShellExecute = false
            });
            _statusStore.Message = $"{storedPanel.Label} のフォルダを開きました: {explorerPath}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DiagnosticLog.Write(ex);
            _statusStore.Message = $"フォルダを開けませんでした: {explorerPath}";
        }
    }

    private void OpenStoredPanelGitHub(StoredPanelSlot storedPanel)
    {
        var webUrl = storedPanel.GitHubWebUrl;
        if (string.IsNullOrEmpty(webUrl))
        {
            _statusStore.Message = storedPanel.GitHubToolTip;
            return;
        }

        if (OpenInBrowser(webUrl))
        {
            _statusStore.Message = $"{storedPanel.Label} の GitHub を開きました: {webUrl}";
        }
    }

    private static T? FindAncestorOrSelf<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }

    private static IEnumerable<DependencyObject> EnumerateVisualDescendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in EnumerateVisualDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static Brush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
