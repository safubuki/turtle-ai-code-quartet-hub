using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace TurtleAIQuartetHub.Panel.Models;

public enum DiskCleanupSafety
{
    // 再取得できる更新ファイルで、しばらく使われていない。既定で選択する。
    Safe,

    // 再取得はできるが、更新処理中・直後の可能性がある。既定では選択しない。
    Caution,

    // 実行中のアプリが使っている、または保持すべき最新版。削除できない。
    Protected
}

public enum DiskCleanupCategory
{
    CopilotCliVersion,
    TempInstaller
}

public sealed class DiskCleanupCandidate : INotifyPropertyChanged
{
    private static readonly Brush SafeBackground = CreateFrozenBrush("#16301F");
    private static readonly Brush SafeBorder = CreateFrozenBrush("#2F7A4C");
    private static readonly Brush SafeForeground = CreateFrozenBrush("#6FE3A0");
    private static readonly Brush CautionBackground = CreateFrozenBrush("#332A12");
    private static readonly Brush CautionBorder = CreateFrozenBrush("#8A7230");
    private static readonly Brush CautionForeground = CreateFrozenBrush("#F2CA57");
    private static readonly Brush ProtectedBackground = CreateFrozenBrush("#23292A");
    private static readonly Brush ProtectedBorder = CreateFrozenBrush("#465052");
    private static readonly Brush ProtectedForeground = CreateFrozenBrush("#A9B4B6");

    private bool _isSelected;

    public DiskCleanupCandidate(
        DiskCleanupCategory category,
        string path,
        bool isDirectory,
        string title,
        string reasonText,
        long sizeBytes,
        DateTime lastWriteTime,
        DiskCleanupSafety safety)
    {
        Category = category;
        Path = path;
        IsDirectory = isDirectory;
        Title = title;
        ReasonText = reasonText;
        SizeBytes = sizeBytes;
        LastWriteTime = lastWriteTime;
        Safety = safety;
        _isSelected = safety == DiskCleanupSafety.Safe;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DiskCleanupCategory Category { get; }

    public string Path { get; }

    public bool IsDirectory { get; }

    public string Title { get; }

    public string ReasonText { get; }

    public long SizeBytes { get; }

    public DateTime LastWriteTime { get; }

    public DiskCleanupSafety Safety { get; }

    public bool CanDelete => Safety != DiskCleanupSafety.Protected;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            var next = value && CanDelete;
            if (_isSelected == next)
            {
                return;
            }

            _isSelected = next;
            OnPropertyChanged();
        }
    }

    public string SizeText => Services.SystemMonitorService.FormatBytes(SizeBytes);

    public string LastWriteText => $"更新 {LastWriteTime:yyyy/MM/dd HH:mm}";

    public string SafetyText => Safety switch
    {
        DiskCleanupSafety.Safe => "安全",
        DiskCleanupSafety.Caution => "注意",
        _ => "保持"
    };

    public Brush SafetyBackgroundBrush => Safety switch
    {
        DiskCleanupSafety.Safe => SafeBackground,
        DiskCleanupSafety.Caution => CautionBackground,
        _ => ProtectedBackground
    };

    public Brush SafetyBorderBrush => Safety switch
    {
        DiskCleanupSafety.Safe => SafeBorder,
        DiskCleanupSafety.Caution => CautionBorder,
        _ => ProtectedBorder
    };

    public Brush SafetyForegroundBrush => Safety switch
    {
        DiskCleanupSafety.Safe => SafeForeground,
        DiskCleanupSafety.Caution => CautionForeground,
        _ => ProtectedForeground
    };

    public string ToolTipText => $"{Path}\n{ReasonText}";

    private static Brush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record DiskCleanupScanResult(
    IReadOnlyList<DiskCleanupCandidate> CopilotCandidates,
    IReadOnlyList<DiskCleanupCandidate> TempCandidates,
    bool IsCopilotRunning,
    bool HasCopilotPackageRoot)
{
    public IEnumerable<DiskCleanupCandidate> All => CopilotCandidates.Concat(TempCandidates);

    public long DeletableBytes => All.Where(candidate => candidate.CanDelete).Sum(candidate => candidate.SizeBytes);

    public long SafeBytes => All.Where(candidate => candidate.Safety == DiskCleanupSafety.Safe).Sum(candidate => candidate.SizeBytes);
}

public sealed record DiskCleanupDeleteResult(long ReclaimedBytes, int DeletedCount, int SkippedCount);

public enum PagefileManagementMode
{
    // 自動管理（全ドライブ、または指定ドライブのシステム管理サイズ）。メモリ逼迫時に拡張される。
    SystemManaged,

    // 初期サイズと最大サイズを指定。最大が初期より大きければその範囲で拡張される。
    Custom,

    // ページファイルなし。
    None,

    Unknown
}

public sealed record PagefileSettings(PagefileManagementMode Mode, long? InitialBytes, long? MaximumBytes)
{
    public bool CanGrow => Mode == PagefileManagementMode.SystemManaged
        || (Mode == PagefileManagementMode.Custom && MaximumBytes > InitialBytes);
}

public enum PagefileAssessmentLevel
{
    Normal,
    Warning,
    Critical
}

public sealed record PagefileAssessment(PagefileAssessmentLevel Level, string BadgeText, string HintText);

public sealed record PagefileSnapshot(
    ulong PhysicalMemoryBytes,
    ulong CommitLimitBytes,
    ulong CommittedBytes,
    long PagefileAllocatedBytes,
    long PagefileInUseBytes,
    long PagefilePeakBytes,
    long? GrowthSinceHubStartBytes,
    PagefileSettings Settings)
{
    public double CommitPercent => CommitLimitBytes > 0
        ? Math.Clamp((double)CommittedBytes * 100d / CommitLimitBytes, 0d, 100d)
        : 0d;
}
