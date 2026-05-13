using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TurtleAIQuartetHub.Panel.Models;

public sealed class LauncherApplication : INotifyPropertyChanged
{
    private ApplicationAvailabilityStatus _availabilityStatus = ApplicationAvailabilityStatus.Unknown;
    private string _resolvedCommand = string.Empty;
    private string _availabilityDetail = string.Empty;
    private bool _isSelected;

    public LauncherApplication(ToolApplicationConfig config)
    {
        Id = config.Id;
        DisplayName = config.DisplayName;
        ShortName = string.IsNullOrWhiteSpace(config.ShortName) ? config.DisplayName : config.ShortName;
        Kind = config.Kind;
        Command = config.Command;
        Arguments = config.Arguments.ToList();
        SupportsMultipleWindows = config.SupportsMultipleWindows;
        Detection = config.Detection;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string DisplayName { get; }

    public string ShortName { get; }

    public ApplicationKind Kind { get; }

    public string Command { get; }

    public IReadOnlyList<string> Arguments { get; }

    public bool SupportsMultipleWindows { get; }

    public ApplicationDetectionConfig Detection { get; }

    public bool IsVsCode => string.Equals(Id, AppConfig.VsCodeApplicationId, StringComparison.OrdinalIgnoreCase);

    public bool IsWorkspaceIde => Kind == ApplicationKind.WorkspaceIde;

    public bool IsSingleWindowAgent => Kind == ApplicationKind.SingleWindowAgent;

    public IReadOnlyList<string> ProcessNames => Detection.ProcessNames;

    public ApplicationAvailabilityStatus AvailabilityStatus
    {
        get => _availabilityStatus;
        private set
        {
            if (SetField(ref _availabilityStatus, value))
            {
                OnPropertyChanged(nameof(IsAvailable));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ToolTip));
            }
        }
    }

    public bool IsAvailable => AvailabilityStatus == ApplicationAvailabilityStatus.Installed;

    public string ResolvedCommand
    {
        get => _resolvedCommand;
        private set
        {
            if (SetField(ref _resolvedCommand, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(ToolTip));
            }
        }
    }

    public string AvailabilityDetail
    {
        get => _availabilityDetail;
        private set
        {
            if (SetField(ref _availabilityDetail, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(ToolTip));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string StatusText => AvailabilityStatus switch
    {
        ApplicationAvailabilityStatus.Installed => "起動可",
        ApplicationAvailabilityStatus.ConfiguredButMissing => "設定パスなし",
        ApplicationAvailabilityStatus.NotFound => "未検出",
        _ => "未確認"
    };

    public string ToolTip
    {
        get
        {
            if (IsAvailable)
            {
                return string.IsNullOrWhiteSpace(ResolvedCommand)
                    ? $"{DisplayName} は起動できます。"
                    : $"{DisplayName}: {ResolvedCommand}";
            }

            return string.IsNullOrWhiteSpace(AvailabilityDetail)
                ? $"{DisplayName} は検出できません。設定で実行ファイルまたはコマンドを指定してください。"
                : AvailabilityDetail;
        }
    }

    public void ApplyAvailability(ApplicationAvailabilityStatus status, string resolvedCommand, string detail)
    {
        AvailabilityStatus = status;
        ResolvedCommand = resolvedCommand;
        AvailabilityDetail = detail;
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
