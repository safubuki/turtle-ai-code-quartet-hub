using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace TurtleAIQuartetHub.Panel.Models;

public sealed record SystemResourceSnapshot(
    double CpuUsagePercent,
    string CpuUsageText,
    string CpuDetailText,
    Brush CpuBarBrush,
    ulong TotalMemoryBytes,
    ulong UsedMemoryBytes,
    ulong AvailableMemoryBytes,
    double MemoryUsagePercent,
    string MemoryUsagePercentText,
    string MemoryDetailText,
    string MemoryAvailableText,
    Brush MemoryBarBrush);

public enum SystemHealthLevel
{
    Normal,
    Warning,
    Critical
}

public sealed record SystemHealthIssue(SystemHealthLevel Level, string Text);

/// <summary>
/// システムモニタの各項目（CPU / メモリ / ドライブ / ページファイル）の状態を集約した結果。
/// 最も悪い項目の状態を <see cref="Level"/> に持ち、注意・警告の原因を悪い順に <see cref="Issues"/> に並べる。
/// </summary>
public sealed record SystemHealthSummary(SystemHealthLevel Level, IReadOnlyList<SystemHealthIssue> Issues);

public sealed record DriveUsageItem(
    string RootPath,
    string DisplayTitle,
    string DriveTypeLabel,
    long TotalBytes,
    long UsedBytes,
    long FreeBytes,
    double UsagePercent,
    string UsagePercentText,
    string CapacityDetailText,
    string FreeSpaceText,
    Brush BarBrush)
{
    public string OpenFolderToolTip => $"エクスプローラで開く: {RootPath}";

    // 光学ドライブ（常に満杯）やネットワークドライブは、アイコン色の集約に含めない。
    public bool IsFixedDrive { get; init; }
}

internal sealed record FolderUsageMeasurement(
    long? SizeBytes,
    int FileCount = 0,
    long? DriveTotalBytes = null,
    string? UnavailableReason = null);

public sealed class FolderUsageItem : INotifyPropertyChanged
{
    private static readonly Brush DefaultBarBrush = CreateFrozenBrush("#45D483");
    private static readonly Brush MutedBarBrush = CreateFrozenBrush("#2E4236");
    private static readonly Brush SlotBadgeBackground = CreateFrozenBrush("#223027");
    private static readonly Brush SlotBadgeBorder = CreateFrozenBrush("#335A43");
    private static readonly Brush SlotBadgeForeground = CreateFrozenBrush("#45D483");
    private static readonly Brush AppBadgeBackground = CreateFrozenBrush("#162834");
    private static readonly Brush AppBadgeBorder = CreateFrozenBrush("#2C5D7A");
    private static readonly Brush AppBadgeForeground = CreateFrozenBrush("#8AD0F5");
    private static readonly Brush StoredBadgeBackground = CreateFrozenBrush("#1E2622");
    private static readonly Brush StoredBadgeBorder = CreateFrozenBrush("#3A4C42");
    private static readonly Brush StoredBadgeForeground = CreateFrozenBrush("#A9BFB0");
    private static readonly Brush AgentBadgeBackground = CreateFrozenBrush("#241C30");
    private static readonly Brush AgentBadgeBorder = CreateFrozenBrush("#5B4A7A");
    private static readonly Brush AgentBadgeForeground = CreateFrozenBrush("#C9B3F0");

    private string _sizeText;
    private string _subDetailText;
    private long? _sizeBytes;
    private int _fileCount;
    private bool _isCalculating;
    private bool _isDriveUsage;
    private double _usageRatio;
    private Brush _barBrush = DefaultBarBrush;

    public FolderUsageItem(
        string badgeText,
        string categoryKind,
        string title,
        string folderPath,
        string displayPath,
        string initialSizeText,
        bool isCalculating)
    {
        BadgeText = badgeText;
        CategoryKind = categoryKind;
        Title = title;
        FolderPath = folderPath;
        DisplayPath = displayPath;
        _sizeText = initialSizeText;
        _subDetailText = string.Empty;
        _isCalculating = isCalculating;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string BadgeText { get; }

    public string CategoryKind { get; }

    public string Title { get; }

    public string FolderPath { get; }

    public string DisplayPath { get; }

    // 未指定なら一括計測の既定タイムアウトを使う。AI エージェントのデータなど大きいフォルダだけ延ばす。
    public TimeSpan? MeasurementTimeout { get; init; }

    public bool CanOpenFolder => !string.IsNullOrWhiteSpace(FolderPath) && Directory.Exists(FolderPath);

    // Hub データ・VS Code / Antigravity IDE のデータだけ、容量の内訳と原因を解析できる。
    public DataFolderKind? AnalysisKind { get; init; }

    public bool CanAnalyze => AnalysisKind is not null && CanOpenFolder;

    public string OpenFolderToolTip => CanOpenFolder
        ? $"エクスプローラで開く: {FolderPath}"
        : "開けるローカルフォルダがありません";

    // システムモニタのフォルダ一覧を、この見出しごとの枠にまとめて表示する。
    public string GroupTitle => CategoryKind switch
    {
        "Slot" => "表のワークスペース（A〜D）",
        "Stored" => "控えのワークスペース",
        "App" => "アプリのデータ（ハブ・IDE）",
        "Agent" => "AI エージェントのデータと一時フォルダ",
        _ => "その他"
    };

    public string GroupDescriptionText => CategoryKind switch
    {
        "Slot" => "メインの 4 枚のカードに登録している作業フォルダです。",
        "Stored" => "控えに保存している作業フォルダです。",
        "App" => "ハブ・VS Code・Antigravity IDE の設定とキャッシュです。",
        "Agent" => "AI エージェントのチャット履歴・セッション・本体と、Temp です。このうち Copilot CLI の旧バージョンと Temp の古いインストーラーは、上の「ディスク整理」で安全に削除できます。",
        _ => string.Empty
    };

    public Brush BadgeBackgroundBrush => CategoryKind switch
    {
        "App" => AppBadgeBackground,
        "Agent" => AgentBadgeBackground,
        "Stored" => StoredBadgeBackground,
        _ => SlotBadgeBackground
    };

    public Brush BadgeBorderBrush => CategoryKind switch
    {
        "App" => AppBadgeBorder,
        "Agent" => AgentBadgeBorder,
        "Stored" => StoredBadgeBorder,
        _ => SlotBadgeBorder
    };

    public Brush BadgeForegroundBrush => CategoryKind switch
    {
        "App" => AppBadgeForeground,
        "Agent" => AgentBadgeForeground,
        "Stored" => StoredBadgeForeground,
        _ => SlotBadgeForeground
    };

    public string SizeText
    {
        get => _sizeText;
        set => SetField(ref _sizeText, value);
    }

    public string SubDetailText
    {
        get => _subDetailText;
        set
        {
            if (SetField(ref _subDetailText, value))
            {
                OnPropertyChanged(nameof(HasSubDetail));
            }
        }
    }

    public bool HasSubDetail => !string.IsNullOrWhiteSpace(_subDetailText);

    public long? SizeBytes
    {
        get => _sizeBytes;
        set => SetField(ref _sizeBytes, value);
    }

    public int FileCount
    {
        get => _fileCount;
        set => SetField(ref _fileCount, value);
    }

    public bool IsCalculating
    {
        get => _isCalculating;
        set => SetField(ref _isCalculating, value);
    }

    public bool IsDriveUsage
    {
        get => _isDriveUsage;
        private set => SetField(ref _isDriveUsage, value);
    }

    public double UsageRatio
    {
        get => _usageRatio;
        set => SetField(ref _usageRatio, Math.Clamp(value, 0d, 100d));
    }

    public Brush BarBrush
    {
        get => _barBrush;
        set => SetField(ref _barBrush, value ?? DefaultBarBrush);
    }

    public void ApplyMeasuredSize(long sizeBytes, int fileCount, string formattedSize)
    {
        IsDriveUsage = false;
        SizeBytes = sizeBytes;
        FileCount = fileCount;
        IsCalculating = false;
        SizeText = formattedSize;
        SubDetailText = $"{fileCount:N0} ファイル";
        BarBrush = sizeBytes > 0 ? DefaultBarBrush : MutedBarBrush;
    }

    public void ApplyDriveUsage(long usedBytes, long totalBytes, string formattedSize)
    {
        ApplyMeasuredSize(usedBytes, 0, formattedSize);
        IsDriveUsage = true;
        SubDetailText = "ドライブ使用量";
        UsageRatio = totalBytes > 0 ? (double)usedBytes * 100d / totalBytes : 0d;
    }

    public void ApplyUnavailable(string reasonText)
    {
        IsDriveUsage = false;
        SizeBytes = null;
        FileCount = 0;
        IsCalculating = false;
        SizeText = reasonText;
        SubDetailText = string.Empty;
        UsageRatio = 0;
        BarBrush = MutedBarBrush;
    }

    private static Brush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
