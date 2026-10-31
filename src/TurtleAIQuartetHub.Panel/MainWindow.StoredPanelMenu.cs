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
            () => BeginStoredPanelTitleEdit(storedPanel, card)));

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

    // 標準・縮小のカードはそのタイトル欄で編集を始める。タイトル欄の無い控え一覧のタイルからは、
    // 下の控えパネルを該当タブへ切り替えて、そのカードのタイトル欄で編集する。
    private void BeginStoredPanelTitleEdit(StoredPanelSlot storedPanel, FrameworkElement card)
    {
        var textBox = FindStoredPanelTitleTextBox(card, storedPanel);
        if (textBox is null)
        {
            if (_statusStore.StoredPanelPages.FirstOrDefault(page => page.Slots.Contains(storedPanel)) is { } page)
            {
                _statusStore.SelectStoredPanelPage(page);
            }

            UpdateLayout();
            textBox = FindStoredPanelTitleTextBox(this, storedPanel);
        }

        if (textBox is null)
        {
            _statusStore.Message = $"{storedPanel.Label} の名前は、控え Quartet を開いてカードのタイトルをダブルクリックすると編集できます。";
            return;
        }

        // メニューが閉じてフォーカスが戻った後に編集を始めないと、すぐ確定されてしまう。
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (textBox.IsVisible && ReferenceEquals(textBox.DataContext, storedPanel))
            {
                BeginInlineTitleTextBoxEdit(textBox);
            }
        });
    }

    private TextBox? FindStoredPanelTitleTextBox(DependencyObject root, StoredPanelSlot storedPanel)
    {
        var inlineStyle = TryFindResource("InlineTitleTextBox") as Style;
        return EnumerateVisualDescendants(root)
            .OfType<TextBox>()
            .FirstOrDefault(textBox => textBox.IsVisible
                && ReferenceEquals(textBox.DataContext, storedPanel)
                && ReferenceEquals(textBox.Style, inlineStyle));
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
