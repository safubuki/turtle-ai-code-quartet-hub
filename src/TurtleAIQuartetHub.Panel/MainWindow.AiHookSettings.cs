using System.Diagnostics;
using System.IO;
using System.Windows;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

public partial class MainWindow
{
    private CancellationTokenSource? _aiHookSettingsPreviewCancellation;
    private FrameworkElement? _aiHookSettingsPreviewReturnFocus;

    private void AiNotifySettingsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AiNotifyHookLocation location }) return;

        if (!Directory.Exists(location.FolderPath))
        {
            ShowAiNotifyAccessMessage("設定フォルダはまだありません。フックを設定してから確認してください。");
            RefreshAiNotifySettingsStatus();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = QuoteExplorerArgument(location.FolderPath),
                UseShellExecute = false
            });
            ShowAiNotifyAccessMessage($"{location.DisplayName} の設定フォルダを開きました。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DiagnosticLog.Write(ex);
            ShowAiNotifyAccessMessage("設定フォルダを開けませんでした。パスとアクセス権を確認してください。");
        }
    }

    private async void AiNotifyViewSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AiNotifyHookLocation location } source) return;

        HideAiHookSettingsPreviewDialog();
        _aiHookSettingsPreviewReturnFocus = source;
        AiHookSettingsPreviewTitleText.Text = $"{location.DisplayName} のフック設定";
        AiHookSettingsPreviewPathTextBox.Text = location.SettingsPath;
        AiHookSettingsPreviewStatusText.Text = "設定ファイルを読み込んでいます...";
        AiHookSettingsPreviewContentTextBox.Text = string.Empty;
        AiHookSettingsPreviewOpenFolderButton.Tag = location;
        AiHookSettingsPreviewOpenFolderButton.IsEnabled = Directory.Exists(location.FolderPath);
        AiHookSettingsPreviewOverlay.Visibility = Visibility.Visible;
        CloseAiHookSettingsPreviewButton.Focus();

        var cancellation = new CancellationTokenSource();
        _aiHookSettingsPreviewCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            var preview = await Task.Run(() => _aiNotifyHookInstaller.ReadSettingsPreview(location), token);
            if (token.IsCancellationRequested || !ReferenceEquals(_aiHookSettingsPreviewCancellation, cancellation)) return;
            ApplyAiHookSettingsPreview(location, preview);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 閉じた後や、別のツールを開いた後に戻った結果を反映しない。
        }
        catch (Exception ex)
        {
            // 設定ファイルの内容やパスを例外ログへ流さない。
            DiagnosticLog.Write(LogLevel.Warn, $"AI hook settings preview failed ({ex.GetType().Name}).");
            if (token.IsCancellationRequested || !ReferenceEquals(_aiHookSettingsPreviewCancellation, cancellation)) return;
            AiHookSettingsPreviewStatusText.Text = "設定ファイルを読み取れませんでした。フォルダからファイルの状態を確認してください。";
        }
        finally
        {
            if (ReferenceEquals(_aiHookSettingsPreviewCancellation, cancellation)) _aiHookSettingsPreviewCancellation = null;
            cancellation.Dispose();
        }
    }

    // プレビューは読み取り専用。ファイルの原文を表示し、整形や保存は行わない。
    private void ApplyAiHookSettingsPreview(AiNotifyHookLocation location, AiNotifyHookSettingsPreview preview)
    {
        AiHookSettingsPreviewTitleText.Text = $"{location.DisplayName} のフック設定";
        AiHookSettingsPreviewPathTextBox.Text = location.SettingsPath;
        AiHookSettingsPreviewContentTextBox.Text = preview.Content;
        AiHookSettingsPreviewContentTextBox.CaretIndex = 0;
        AiHookSettingsPreviewContentTextBox.ScrollToHome();
        AiHookSettingsPreviewStatusText.Text = preview.Succeeded
            ? "読み取り専用です。設定ファイルの内容をそのまま表示しています。"
              + (preview.Content.Length == 0 ? " このファイルは空です。" : string.Empty)
              + (string.IsNullOrEmpty(preview.Message) ? string.Empty : Environment.NewLine + preview.Message)
            : preview.Message;
        AiHookSettingsPreviewOpenFolderButton.Tag = location;
        AiHookSettingsPreviewOpenFolderButton.IsEnabled = Directory.Exists(location.FolderPath);
    }

    private void ShowAiNotifyAccessMessage(string message)
    {
        AiNotifyActionStatusText.Text = message;
        AiNotifyActionStatusText.Visibility = Visibility.Visible;
        if (AiHookSettingsPreviewOverlay.Visibility == Visibility.Visible)
        {
            AiHookSettingsPreviewStatusText.Text = message;
        }
    }

    private void CloseAiHookSettingsPreviewButton_Click(object sender, RoutedEventArgs e)
        => HideAiHookSettingsPreviewDialog();

    private void HideAiHookSettingsPreviewDialog()
    {
        _aiHookSettingsPreviewCancellation?.Cancel();
        _aiHookSettingsPreviewCancellation = null;
        AiHookSettingsPreviewOverlay.Visibility = Visibility.Collapsed;
        AiHookSettingsPreviewContentTextBox.Clear();
        AiHookSettingsPreviewPathTextBox.Clear();
        if (_aiHookSettingsPreviewReturnFocus is { IsVisible: true } source) source.Focus();
        _aiHookSettingsPreviewReturnFocus = null;
    }
}
