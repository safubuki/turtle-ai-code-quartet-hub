using System.Windows;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

public partial class MainWindow
{
    private const string VscodeRecoveryWarning = "未保存の編集が失われたり、ターミナルや AI の処理が中断したりする可能性があります。必要な作業を保存してから実行してください。";
    private VscodeProcessRecoveryService? _vscodeProcessRecoveryService;
    private CancellationTokenSource? _vscodeProcessRecoveryScanCancellation;
    private IReadOnlyList<VscodeProcessInfo> _pendingVscodeProcessRecovery = [];

    private async void OpenVscodeProcessRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _vscodeProcessRecoveryScanCancellation is not null) return;

        SuppressFocusedSlotReassertForPanelInput();
        _pendingVscodeProcessRecovery = [];
        VscodeProcessRecoveryConfirmTitleText.Text = "VS Code を強制終了しますか？";
        VscodeProcessRecoveryConfirmMessageText.Text = "起動中の VS Code を確認しています...";
        VscodeProcessRecoveryConfirmDetailText.Text = "プロセス情報を取得中...";
        VscodeProcessRecoveryConfirmIssuesText.Text = string.Empty;
        VscodeProcessRecoveryWarningText.Text = VscodeRecoveryWarning;
        ConfirmVscodeProcessRecoveryButton.IsEnabled = false;
        ConfirmVscodeProcessRecoveryButton.Visibility = Visibility.Visible;
        CancelVscodeProcessRecoveryButton.Content = "キャンセル";
        VscodeProcessRecoveryConfirmOverlay.Visibility = Visibility.Visible;
        OpenVscodeProcessRecoveryButton.IsEnabled = false;
        CancelVscodeProcessRecoveryButton.Focus();

        var cancellation = new CancellationTokenSource();
        _vscodeProcessRecoveryScanCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            _vscodeProcessRecoveryService ??= new VscodeProcessRecoveryService();
            var scan = await _vscodeProcessRecoveryService.ScanAsync(token);
            if (token.IsCancellationRequested || !ReferenceEquals(_vscodeProcessRecoveryScanCancellation, cancellation)) return;

            _pendingVscodeProcessRecovery = scan.Processes.ToArray();
            VscodeProcessRecoveryConfirmMessageText.Text = scan.Processes.Count > 0
                ? $"現在のユーザーの VS Code {scan.Processes.Count} プロセスを強制終了します。"
                : scan.Issues.Count > 0 ? "終了対象を確認できませんでした。" : "起動中の VS Code は見つかりませんでした。";
            VscodeProcessRecoveryConfirmDetailText.Text = scan.Processes.Count > 0
                ? string.Join(Environment.NewLine + Environment.NewLine, scan.Processes.Select(process => $"{process.DisplayTitle}{Environment.NewLine}{process.ExecutablePath}"))
                : "確認できた終了対象はありません。";
            VscodeProcessRecoveryConfirmIssuesText.Text = FormatVscodeScanIssues(scan);
            ConfirmVscodeProcessRecoveryButton.IsEnabled = scan.Processes.Count > 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // キャンセル後に戻った一覧は確認画面へ反映しない。
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            if (token.IsCancellationRequested || !ReferenceEquals(_vscodeProcessRecoveryScanCancellation, cancellation)) return;
            VscodeProcessRecoveryConfirmMessageText.Text = "VS Code のプロセスを確認できませんでした。";
            VscodeProcessRecoveryConfirmDetailText.Text = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_vscodeProcessRecoveryScanCancellation, cancellation))
            {
                _vscodeProcessRecoveryScanCancellation = null;
            }
            cancellation.Dispose();
            OpenVscodeProcessRecoveryButton.IsEnabled = !_isBusy && _vscodeProcessRecoveryScanCancellation is null;
        }
    }

    private void CancelVscodeProcessRecoveryButton_Click(object sender, RoutedEventArgs e)
        => HideVscodeProcessRecoveryConfirmDialog();

    private void HideVscodeProcessRecoveryConfirmDialog()
    {
        // CTS の破棄は非同期ハンドラの finally が担当する。
        _vscodeProcessRecoveryScanCancellation?.Cancel();
        _vscodeProcessRecoveryScanCancellation = null;
        _pendingVscodeProcessRecovery = [];
        ConfirmVscodeProcessRecoveryButton.IsEnabled = false;
        VscodeProcessRecoveryConfirmOverlay.Visibility = Visibility.Collapsed;
        OpenVscodeProcessRecoveryButton.IsEnabled = !_isBusy;
        if (SystemMonitorOverlay.Visibility == Visibility.Visible) OpenVscodeProcessRecoveryButton.Focus();
    }

    private async void ConfirmVscodeProcessRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _vscodeProcessRecoveryService is null || _pendingVscodeProcessRecovery.Count == 0) return;

        var confirmed = _pendingVscodeProcessRecovery.ToArray();
        HideVscodeProcessRecoveryConfirmDialog();
        VscodeProcessRecoveryStatusText.Text = $"確認した {confirmed.Length} プロセスを終了しています...";
        await RunBusyAsync(async () =>
        {
            // ウィンドウが消える前に、現在のワークスペースと復帰に必要な情報を保存する。
            _statusStore.SaveCurrentSettings();
            var result = await _vscodeProcessRecoveryService.TerminateAsync(confirmed);
            var summary = $"終了 {result.TerminatedCount} 件・既に終了 {result.AlreadyExitedCount} 件・終了できなかったもの {result.FailedCount} 件。";
            summary += result.HasRemainingProcesses
                ? $" VS Code が {result.RemainingScan.Processes.Count} プロセス残っています。対象を確認して再実行してください。"
                : result.RemainingScan.Issues.Count > 0
                    ? " 確認できないプロセスがあるため、全終了は確認できません。"
                    : " VS Code のプロセスが残っていないことを確認しました。更新を再試行できます。";
            VscodeProcessRecoveryStatusText.Text = summary;
            _statusStore.Message = summary;

            VscodeProcessRecoveryConfirmTitleText.Text = "VS Code の終了結果";
            VscodeProcessRecoveryConfirmMessageText.Text = summary;
            VscodeProcessRecoveryConfirmDetailText.Text = string.Join(Environment.NewLine,
                result.Results.Select(item => $"{item.Process.DisplayTitle}: {item.Message}"));
            VscodeProcessRecoveryConfirmIssuesText.Text = FormatVscodeScanIssues(result.RemainingScan);
            VscodeProcessRecoveryWarningText.Text = "確認後に起動したプロセスは、自動では終了しません。VS Code の再起動は更新完了後に行ってください。";
            CancelVscodeProcessRecoveryButton.Content = "閉じる";
            ConfirmVscodeProcessRecoveryButton.Visibility = Visibility.Collapsed;

            foreach (var item in result.Results)
            {
                DiagnosticLog.Write(LogLevel.Info, $"VS Code recovery PID {item.Process.ProcessId}: {item.Status} ({item.Message})");
            }
            try
            {
                await _statusStore.RefreshWindowStatusesAsync(_windowEnumerator, _refreshCancellation.Token);
                RefreshVisibilityAfterVscodeRecovery();
                RefreshAuxiliaryUi();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
                // 終了結果は維持する。管理窓の再検出は次の周期更新でも行われる。
            }
        });

        if (ConfirmVscodeProcessRecoveryButton.Visibility == Visibility.Collapsed
            && SystemMonitorOverlay.Visibility == Visibility.Visible)
        {
            VscodeProcessRecoveryConfirmOverlay.Visibility = Visibility.Visible;
            CancelVscodeProcessRecoveryButton.Focus();
        }
        else if (VscodeProcessRecoveryStatusText.Text.EndsWith("終了しています...", StringComparison.Ordinal))
        {
            VscodeProcessRecoveryStatusText.Text = "終了処理を完了できませんでした。プロセスを再確認してください。";
        }
    }

    private static string FormatVscodeScanIssues(VscodeProcessScan scan)
        => scan.Issues.Count == 0 ? string.Empty
            : string.Join(Environment.NewLine, scan.Issues.Take(3))
              + (scan.Issues.Count > 3 ? $"{Environment.NewLine}ほか {scan.Issues.Count - 3} 件は情報を確認できませんでした。" : string.Empty);

    private void RefreshVisibilityAfterVscodeRecovery()
    {
        foreach (var slot in _statusStore.Slots.Where(slot => slot.WindowHandle == IntPtr.Zero))
        {
            slot.IsHidden = false;
        }
        RefreshHiddenWindowsState();
        UpdateVisibilityButtonVisual();
    }
}
