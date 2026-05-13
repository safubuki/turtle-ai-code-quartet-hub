using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TurtleAIQuartetHub.Panel.Models;

public sealed class WindowSlot : INotifyPropertyChanged
{
    public enum SlotWindowLayerMode
    {
        Topmost,
        Backmost
    }

    private IntPtr _windowHandle;
    private string _path = string.Empty;
    private string _applicationId = AppConfig.VsCodeApplicationId;
    private string _applicationDisplayName = "VS Code";
    private string _applicationShortName = "VS Code";
    private string _applicationAvailabilityText = "未確認";
    private string _applicationToolTip = string.Empty;
    private bool _isApplicationAvailable;
    private bool _isVsCodeAvailable;
    private bool _isAntigravityAvailable;
    private string _vsCodeApplicationToolTip = string.Empty;
    private string _antigravityApplicationToolTip = string.Empty;
    private string _panelTitle;
    private string _savedWorkspacePath = string.Empty;
    private bool _savedWorkspaceConfirmed;
    private string _currentWorkspacePath = string.Empty;
    private string _windowTitle = string.Empty;
    private SlotWindowStatus _windowStatus = SlotWindowStatus.Missing;
    private bool _isFocused;
    private SlotWindowLayerMode _windowLayerMode = SlotWindowLayerMode.Topmost;
    private bool _isHidden;
    private VscodeLayoutPreference _preferredLayout = VscodeLayoutPreference.Empty;

    public WindowSlot(SlotConfig config)
    {
        Name = config.Name;
        _path = NormalizeWorkspacePath(config.Path);
        _applicationId = AppConfig.NormalizeApplicationId(config.ApplicationId);
        _panelTitle = GetDefaultPanelTitle();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public string Path
    {
        get => _path;
        set
        {
            if (SetField(ref _path, NormalizeWorkspacePath(value)))
            {
                OnPropertyChanged(nameof(EffectivePath));
                OnPropertyChanged(nameof(DisplayPath));
                OnPropertyChanged(nameof(ShortPath));
                OnPropertyChanged(nameof(HasPanelContent));
            }
        }
    }

    public string EffectivePath => SavedWorkspaceConfirmed && !string.IsNullOrWhiteSpace(SavedWorkspacePath) ? SavedWorkspacePath : Path;

    public string ApplicationId
    {
        get => _applicationId;
        set
        {
            if (SetField(ref _applicationId, AppConfig.NormalizeApplicationId(value)))
            {
                OnPropertyChanged(nameof(ApplicationBadgeText));
                OnPropertyChanged(nameof(IsVsCodeApplication));
                OnPropertyChanged(nameof(IsAntigravityApplication));
                OnPropertyChanged(nameof(HasPanelContent));
            }
        }
    }

    public string ApplicationDisplayName
    {
        get => _applicationDisplayName;
        set
        {
            if (SetField(ref _applicationDisplayName, string.IsNullOrWhiteSpace(value) ? ApplicationId : value.Trim()))
            {
                OnPropertyChanged(nameof(ApplicationBadgeText));
            }
        }
    }

    public string ApplicationShortName
    {
        get => _applicationShortName;
        set
        {
            if (SetField(ref _applicationShortName, string.IsNullOrWhiteSpace(value) ? ApplicationDisplayName : value.Trim()))
            {
                OnPropertyChanged(nameof(ApplicationBadgeText));
            }
        }
    }

    public string ApplicationAvailabilityText
    {
        get => _applicationAvailabilityText;
        set => SetField(ref _applicationAvailabilityText, value ?? string.Empty);
    }

    public string ApplicationToolTip
    {
        get => _applicationToolTip;
        set => SetField(ref _applicationToolTip, value ?? string.Empty);
    }

    public bool IsApplicationAvailable
    {
        get => _isApplicationAvailable;
        set => SetField(ref _isApplicationAvailable, value);
    }

    public string ApplicationBadgeText => string.IsNullOrWhiteSpace(ApplicationShortName) ? ApplicationId : ApplicationShortName;

    public bool IsVsCodeApplication => string.Equals(ApplicationId, AppConfig.VsCodeApplicationId, StringComparison.OrdinalIgnoreCase);

    public bool IsAntigravityApplication => string.Equals(ApplicationId, "antigravity", StringComparison.OrdinalIgnoreCase);

    public bool IsVsCodeAvailable
    {
        get => _isVsCodeAvailable;
        set => SetField(ref _isVsCodeAvailable, value);
    }

    public bool IsAntigravityAvailable
    {
        get => _isAntigravityAvailable;
        set => SetField(ref _isAntigravityAvailable, value);
    }

    public string VsCodeApplicationToolTip
    {
        get => _vsCodeApplicationToolTip;
        set => SetField(ref _vsCodeApplicationToolTip, value ?? string.Empty);
    }

    public string AntigravityApplicationToolTip
    {
        get => _antigravityApplicationToolTip;
        set => SetField(ref _antigravityApplicationToolTip, value ?? string.Empty);
    }

    public string DisplayPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CurrentWorkspacePath))
            {
                return CurrentWorkspacePath;
            }

            return WindowHandle != IntPtr.Zero ? Path : EffectivePath;
        }
    }

    public string ShortPath
    {
        get
        {
            var path = DisplayPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return "-";
            }

            return WorkspacePathDisplay.GetShortPath(path);
        }
    }

    public string PanelTitle
    {
        get => _panelTitle;
        set
        {
            var normalizedValue = value ?? string.Empty;
            if (SetField(ref _panelTitle, normalizedValue))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(HasPanelContent));
            }
        }
    }

    public string DisplayTitle => string.IsNullOrWhiteSpace(PanelTitle) ? GetDefaultPanelTitle() : PanelTitle;

    public string DefaultPanelTitle => GetDefaultPanelTitle();

    public string SavedWorkspacePath
    {
        get => _savedWorkspacePath;
        set
        {
            if (SetField(ref _savedWorkspacePath, NormalizeWorkspacePath(value)))
            {
                OnPropertyChanged(nameof(EffectivePath));
                OnPropertyChanged(nameof(DisplayPath));
                OnPropertyChanged(nameof(ShortPath));
                OnPropertyChanged(nameof(HasPanelContent));
            }
        }
    }

    public bool SavedWorkspaceConfirmed
    {
        get => _savedWorkspaceConfirmed;
        set
        {
            if (SetField(ref _savedWorkspaceConfirmed, value))
            {
                OnPropertyChanged(nameof(EffectivePath));
                OnPropertyChanged(nameof(DisplayPath));
                OnPropertyChanged(nameof(ShortPath));
                OnPropertyChanged(nameof(HasPanelContent));
            }
        }
    }

    public string CurrentWorkspacePath
    {
        get => _currentWorkspacePath;
        set
        {
            if (SetField(ref _currentWorkspacePath, NormalizeWorkspacePath(value)))
            {
                OnPropertyChanged(nameof(DisplayPath));
                OnPropertyChanged(nameof(ShortPath));
                OnPropertyChanged(nameof(HasPanelContent));
            }
        }
    }

    public bool IsFocused
    {
        get => _isFocused;
        set
        {
            SetField(ref _isFocused, value);
        }
    }

    public SlotWindowLayerMode WindowLayerMode
    {
        get => _windowLayerMode;
        set
        {
            if (SetField(ref _windowLayerMode, value))
            {
                OnPropertyChanged(nameof(IsTopmostLayer));
                OnPropertyChanged(nameof(IsBackmostLayer));
            }
        }
    }

    public bool IsTopmostLayer => WindowLayerMode == SlotWindowLayerMode.Topmost;

    public bool IsBackmostLayer => WindowLayerMode == SlotWindowLayerMode.Backmost;

    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (SetField(ref _isHidden, value))
            {
                OnPropertyChanged(nameof(WindowStatusText));
            }
        }
    }

    public VscodeLayoutPreference PreferredLayout
    {
        get => _preferredLayout;
        set => _preferredLayout = value ?? VscodeLayoutPreference.Empty;
    }

    public string WindowStatusText
    {
        get
        {
            if (IsHidden && WindowStatus == SlotWindowStatus.Ready)
            {
                return "非表示";
            }

            return WindowStatus switch
            {
                SlotWindowStatus.Ready => "起動",
                SlotWindowStatus.Launching => "起動中",
                SlotWindowStatus.Missing => "停止中",
                _ => WindowStatus.ToString()
            };
        }
    }

    public bool HasPanelContent => WindowHandle != IntPtr.Zero
        || !string.IsNullOrWhiteSpace(PanelTitle)
        || !string.IsNullOrWhiteSpace(CurrentWorkspacePath)
        || !string.IsNullOrWhiteSpace(SavedWorkspacePath)
        || !string.IsNullOrWhiteSpace(Path);

    public IntPtr WindowHandle
    {
        get => _windowHandle;
        set
        {
            if (_windowHandle == value)
            {
                return;
            }

            _windowHandle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayPath));
            OnPropertyChanged(nameof(WindowHandleText));
            OnPropertyChanged(nameof(ShortPath));
            OnPropertyChanged(nameof(HasPanelContent));
        }
    }

    public string WindowHandleText => WindowHandle == IntPtr.Zero ? "-" : $"0x{WindowHandle.ToInt64():X}";

    public string WindowTitle
    {
        get => _windowTitle;
        set => SetField(ref _windowTitle, value);
    }

    public SlotWindowStatus WindowStatus
    {
        get => _windowStatus;
        set
        {
            if (SetField(ref _windowStatus, value))
            {
                OnPropertyChanged(nameof(WindowStatusText));
            }
        }
    }

    public void ClearWindow()
    {
        WindowHandle = IntPtr.Zero;
        CurrentWorkspacePath = string.Empty;
        WindowTitle = string.Empty;
        WindowStatus = SlotWindowStatus.Missing;
        IsFocused = false;
        WindowLayerMode = SlotWindowLayerMode.Topmost;
    }

    public void ApplyAssignedPanel(string? panelTitle, string? workspacePath)
    {
        ApplyAssignedPanel(panelTitle, workspacePath, ApplicationId);
    }

    public void ApplyAssignedPanel(string? panelTitle, string? workspacePath, string? applicationId)
    {
        var normalizedPath = NormalizeWorkspacePath(workspacePath);
        PanelTitle = panelTitle ?? string.Empty;
        Path = normalizedPath;
        ApplicationId = applicationId ?? AppConfig.VsCodeApplicationId;
        SavedWorkspacePath = normalizedPath;
        SavedWorkspaceConfirmed = !string.IsNullOrWhiteSpace(normalizedPath);
        CurrentWorkspacePath = string.Empty;
    }

    public void ClearAssignedPanel()
    {
        PanelTitle = string.Empty;
        Path = string.Empty;
        SavedWorkspacePath = string.Empty;
        SavedWorkspaceConfirmed = false;
        CurrentWorkspacePath = string.Empty;
    }

    private string GetDefaultPanelTitle()
    {
        return string.IsNullOrWhiteSpace(Name) ? "未設定" : $"スロット {Name}";
    }

    private static string NormalizeWorkspacePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var path = value.Trim();
        if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
        {
            path = path[1..];
        }

        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
        {
            path = path.Replace('/', System.IO.Path.DirectorySeparatorChar);
        }

        return path;
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
