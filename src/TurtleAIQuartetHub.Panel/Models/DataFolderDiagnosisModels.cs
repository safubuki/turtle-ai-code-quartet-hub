using System.Windows.Media;
using TurtleAIQuartetHub.Panel.Services;

namespace TurtleAIQuartetHub.Panel.Models;

/// <summary>容量の解析ができるフォルダの種類。診断コードの頭文字（H / V / G）もここで決まる。</summary>
public enum DataFolderKind
{
    Hub,
    VsCode,
    Antigravity
}

/// <summary>
/// 解析の前提になる設定。Hub データの中身は「スロット別プロファイルを使うか」で意味が変わり、
/// IDE のキャッシュはハブが自動で回収している対象かどうかで原因の説明が変わる。
/// </summary>
public sealed record DataFolderAnalysisContext(
    bool UseDedicatedUserDataDirs,
    IReadOnlyCollection<string> SlotDirectoryNames,
    bool HubReclaimsIdeCaches);

/// <summary>
/// 見つかった問題 1 件。<see cref="Code"/> は口頭で伝えるための記号（H1、V3 など）で、
/// 意味は docs/data-folder-diagnostics.md の一覧と対応する。
/// </summary>
public sealed record DataFolderFinding(
    string Code,
    SystemHealthLevel Level,
    string Symptom,
    string Cause,
    string Remedy,
    long Bytes)
{
    public string LevelText => Level == SystemHealthLevel.Critical ? "要対処" : "注意";

    public string SizeText => SystemMonitorService.FormatBytes(Bytes);

    public Brush LevelBrush => SystemMonitorService.GetHealthBrush(Level);
}

/// <summary>容量の多い場所 1 件（フォルダ全体に対する割合付き）。</summary>
public sealed record DataFolderHeavyEntry(string RelativePath, string Label, long Bytes, double Percent)
{
    public string SizeText => SystemMonitorService.FormatBytes(Bytes);

    public string PercentText => $"{Percent:0} %";
}

public sealed record DataFolderDiagnosis(
    DataFolderKind Kind,
    string RootPath,
    long TotalBytes,
    int FileCount,
    IReadOnlyList<DataFolderFinding> Findings,
    IReadOnlyList<DataFolderHeavyEntry> HeavyEntries)
{
    public SystemHealthLevel Level => Findings.Count == 0
        ? SystemHealthLevel.Normal
        : Findings.Max(finding => finding.Level);

    public string ConclusionText => Level switch
    {
        SystemHealthLevel.Critical => $"対処が必要な問題があります（{Findings.Count} 件）",
        SystemHealthLevel.Warning => $"注意が必要な点があります（{Findings.Count} 件）",
        _ => "問題は見つかりませんでした"
    };

    /// <summary>口頭で伝える 1 行。例: 「V1（3.2 GB）・V3（1.4 GB）／合計 6.0 GB」。</summary>
    public string KeywordText => Findings.Count == 0
        ? $"問題なし／合計 {SystemMonitorService.FormatBytes(TotalBytes)}"
        : $"{string.Join("・", Findings.Select(finding => $"{finding.Code}（{finding.SizeText}）"))}／合計 {SystemMonitorService.FormatBytes(TotalBytes)}";
}
