using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel;

// 控えのカード名・区切り線の名前の入力ダイアログ（本体パネルの StoredRenameOverlay）。
// 控え一覧は別ウィンドウのポップアップで、自分からはアクティブにならない。本体以外（AI が動いている VS Code など）が
// 前面にあるとキー入力が一覧の入力欄に届かず、IME の変換欄も入力欄の外に出る。そのため名前の入力は、
// 本体パネルを前面に出してから本体の入力欄で行う。
public partial class MainWindow
{
    private object? _storedRenameTarget;

    private void ShowStoredRenameDialog(StoredPanelSlot storedPanel)
    {
        if (!storedPanel.HasContent)
        {
            return;
        }

        OpenStoredRenameDialog(
            storedPanel,
            "控えの名前を変更",
            $"{storedPanel.Label} · {storedPanel.ShortPath}",
            storedPanel.PanelTitle.Length > 0 ? storedPanel.PanelTitle : storedPanel.DisplayTitle,
            "Enter で変更・Esc で取消・空欄でフォルダ名に戻す");
    }

    private void ShowStoredDividerRenameDialog(StoredPanelPage page)
    {
        OpenStoredRenameDialog(
            page,
            page.HasDivider ? "区切り線の名前を変更" : "名前付きの区切り線を引く",
            $"{page.RowRangeText} の行の上",
            page.CustomHeader,
            page.HasDivider ? "Enter で変更・Esc で取消・空欄で線だけにする" : "Enter で引く・Esc で取消");
    }

    private void OpenStoredRenameDialog(object target, string title, string subtitle, string text, string hint)
    {
        _storedRenameTarget = target;
        SelectOverviewStoredPanel(null);
        StoredRenameTitleText.Text = title;
        StoredRenameSubtitleText.Text = subtitle;
        StoredRenameHintText.Text = hint;
        StoredRenameTextBox.Text = text;
        StoredRenameOverlay.Visibility = Visibility.Visible;

        // 一覧（ポップアップ）を押した直後でも、本体を前面に出してから入力欄へキーボードを向ける。
        ActivatePanelWindow();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (StoredRenameOverlay.Visibility != Visibility.Visible)
            {
                return;
            }

            Activate();
            StoredRenameTextBox.Focus();
            Keyboard.Focus(StoredRenameTextBox);
            StoredRenameTextBox.SelectAll();
        });
    }

    private void CommitStoredRename()
    {
        var target = _storedRenameTarget;
        var text = StoredRenameTextBox.Text.Trim();
        HideStoredRenameDialog();
        switch (target)
        {
            case StoredPanelSlot storedPanel when storedPanel.HasContent:
            {
                if (string.Equals(storedPanel.PanelTitle, text, StringComparison.Ordinal))
                {
                    return;
                }

                // 名前を変えると StatusStore が保存する。空欄ならフォルダ名（既定の表示）に戻る。
                storedPanel.PanelTitle = text;
                _statusStore.Message = text.Length > 0
                    ? $"控え {storedPanel.Label} の名前を「{text}」にしました。"
                    : $"控え {storedPanel.Label} の名前を消し、フォルダ名で表示します。";
                break;
            }
            case StoredPanelPage page:
            {
                var hadDivider = page.HasDivider;
                var previous = page.CustomHeader;
                if (!hadDivider && text.Length == 0)
                {
                    // 名前なしで「名前付きの区切り線」を確定したときは、線だけ引く。
                    _statusStore.SetStoredPanelPageDivider(page, true);
                    _statusStore.Message = "区切り線を引きました。";
                }
                else
                {
                    _statusStore.RenameStoredPanelPage(page, text);
                    if (!hadDivider)
                    {
                        _statusStore.Message = $"区切り線「{page.CustomHeader}」を引きました。";
                    }
                    else if (!string.Equals(previous, page.CustomHeader, StringComparison.Ordinal))
                    {
                        _statusStore.Message = page.HasCustomHeader
                            ? $"区切り線の名前を「{page.CustomHeader}」にしました。"
                            : "区切り線の名前を消しました（線は残ります）。";
                    }
                }

                // 区切り線が増えると一覧の高さが変わるので、画面からはみ出さない位置へ合わせ直す。
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RepositionStoredOverview);
                break;
            }
        }
    }

    private void HideStoredRenameDialog()
    {
        _storedRenameTarget = null;
        StoredRenameOverlay.Visibility = Visibility.Collapsed;
        if (StoredRenameTextBox.IsKeyboardFocusWithin)
        {
            Keyboard.ClearFocus();
        }
    }

    private void StoredRenameTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // IME の変換確定の Enter は Key.ImeProcessed で届くので、ここには来ない。
        if (e.Key is Key.Enter or Key.Return)
        {
            CommitStoredRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideStoredRenameDialog();
            e.Handled = true;
        }
    }

    private void ConfirmStoredRenameButton_Click(object sender, RoutedEventArgs e)
    {
        CommitStoredRename();
    }

    private void CancelStoredRenameButton_Click(object sender, RoutedEventArgs e)
    {
        HideStoredRenameDialog();
    }

    // ダイアログの外側（暗い部分）を押したら取り消す。
    private void StoredRenameOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HideStoredRenameDialog();
        e.Handled = true;
    }

    private void StoredRenameCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }
}
