using System.Diagnostics;
using System.IO;
using System.Windows;
using TurtleAIQuartetHub.Panel.Models;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel;

// システムモニタの「アプリのデータ」行から開く容量の解析。読み取りだけで、ファイルには触れない。
// 結果は診断コード（H1・V3 など）で示し、利用者が記号と大きさを口頭で伝えれば原因が分かるようにする。
public partial class MainWindow
{
    // 数万ファイル・数十 GB でも終わる長さ。超えたら途中で打ち切って知らせる。
    private static readonly TimeSpan DataFolderAnalysisTimeout = TimeSpan.FromMinutes(2);

    private CancellationTokenSource? _dataFolderAnalysisCancellation;
    private FolderUsageItem? _dataFolderAnalysisTarget;

    private void AnalyzeDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: FolderUsageItem { CanAnalyze: true } item })
        {
            return;
        }

        SuppressFocusedSlotReassertForPanelInput();
        _dataFolderAnalysisTarget = item;
        DataFolderAnalysisOverlay.Visibility = Visibility.Visible;
        StartDataFolderAnalysis();
        CloseDataFolderAnalysisDialogButton.Focus();
    }

    private void RerunDataFolderAnalysisButton_Click(object sender, RoutedEventArgs e)
    {
        SuppressFocusedSlotReassertForPanelInput();
        StartDataFolderAnalysis();
    }

    private void CloseDataFolderAnalysisButton_Click(object sender, RoutedEventArgs e)
    {
        HideDataFolderAnalysisDialog();
    }

    private void HideDataFolderAnalysisDialog()
    {
        CancelDataFolderAnalysis();
        DataFolderAnalysisOverlay.Visibility = Visibility.Collapsed;
    }

    private void CancelDataFolderAnalysis()
    {
        _dataFolderAnalysisCancellation?.Cancel();
        _dataFolderAnalysisCancellation?.Dispose();
        _dataFolderAnalysisCancellation = null;
    }

    private async void StartDataFolderAnalysis()
    {
        if (_dataFolderAnalysisTarget is not { AnalysisKind: { } kind } target)
        {
            return;
        }

        CancelDataFolderAnalysis();
        var cancellation = new CancellationTokenSource(DataFolderAnalysisTimeout);
        _dataFolderAnalysisCancellation = cancellation;

        DataFolderAnalysisTitleText.Text = $"容量の解析: {target.Title}";
        DataFolderAnalysisStatusText.Text = "解析中...（ファイルが多いと数十秒かかります）";
        DataFolderAnalysisResultPanel.Visibility = Visibility.Collapsed;
        RerunDataFolderAnalysisButton.IsEnabled = false;

        var config = _statusStore.Config;
        var context = new DataFolderAnalysisContext(
            config.UseDedicatedUserDataDirs,
            _statusStore.Slots
                .Select(slot => Path.GetFileName(SlotUserDataPaths.GetUserDataDirectory(slot, config)))
                .ToList(),
            HubReclaimsIdeCaches: kind == DataFolderKind.VsCode);
        var rootPath = target.FolderPath;
        var progress = new Progress<int>(fileCount =>
        {
            if (ReferenceEquals(_dataFolderAnalysisCancellation, cancellation))
            {
                DataFolderAnalysisStatusText.Text = $"解析中...（{fileCount:N0} ファイルを確認）";
            }
        });

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var diagnosis = await Task.Run(
                () => DataFolderDiagnosticsService.Analyze(kind, rootPath, context, progress, cancellation.Token),
                cancellation.Token);
            if (!ReferenceEquals(_dataFolderAnalysisCancellation, cancellation))
            {
                return;
            }

            ApplyDataFolderDiagnosis(diagnosis, stopwatch.Elapsed);
            DiagnosticLog.Write(LogLevel.Info, $"Data folder analysis ({kind}): {diagnosis.KeywordText}, files={diagnosis.FileCount}, elapsed={stopwatch.Elapsed.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_dataFolderAnalysisCancellation, cancellation))
            {
                DataFolderAnalysisStatusText.Text = $"{DataFolderAnalysisTimeout.TotalMinutes:0} 分以内に解析が終わらなかったため中断しました。ファイル数が非常に多い可能性があります。";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            DiagnosticLog.Write(ex);
            if (ReferenceEquals(_dataFolderAnalysisCancellation, cancellation))
            {
                DataFolderAnalysisStatusText.Text = $"フォルダを読み取れませんでした: {ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_dataFolderAnalysisCancellation, cancellation))
            {
                RerunDataFolderAnalysisButton.IsEnabled = true;
            }
        }
    }

    private void ApplyDataFolderDiagnosis(DataFolderDiagnosis diagnosis, TimeSpan elapsed)
    {
        var levelBrush = SystemMonitorService.GetHealthBrush(diagnosis.Level);
        DataFolderAnalysisConclusionBorder.BorderBrush = levelBrush;
        DataFolderAnalysisConclusionText.Foreground = levelBrush;
        DataFolderAnalysisConclusionText.Text = diagnosis.ConclusionText;
        DataFolderAnalysisTotalText.Text = $"合計 {SystemMonitorService.FormatBytes(diagnosis.TotalBytes)}・{diagnosis.FileCount:N0} ファイル（{diagnosis.RootPath}）";
        DataFolderAnalysisKeywordText.Text = diagnosis.KeywordText;
        DataFolderAnalysisFindingsItemsControl.ItemsSource = diagnosis.Findings;
        DataFolderAnalysisFindingsItemsControl.Visibility = diagnosis.Findings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DataFolderAnalysisHeavyItemsControl.ItemsSource = diagnosis.HeavyEntries;
        DataFolderAnalysisStatusText.Text = $"解析完了（{DateTime.Now:HH:mm:ss}・{elapsed.TotalSeconds:0.0} 秒）";
        DataFolderAnalysisResultPanel.Visibility = Visibility.Visible;
    }
}
