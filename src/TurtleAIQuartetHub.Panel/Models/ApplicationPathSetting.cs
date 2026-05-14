using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TurtleAIQuartetHub.Panel.Models;

public sealed class ApplicationPathSetting : INotifyPropertyChanged
{
    private string _command = string.Empty;

    public ApplicationPathSetting(LauncherApplication application)
    {
        Id = application.Id;
        DisplayName = application.DisplayName;
        ShortName = application.ShortName;
        Kind = application.Kind;
        Command = application.Command;
        ResolvedCommand = application.ResolvedCommand;
        StatusText = application.StatusText;
        ToolTip = application.ToolTip;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string DisplayName { get; }

    public string ShortName { get; }

    public ApplicationKind Kind { get; }

    public string KindText => Kind switch
    {
        ApplicationKind.WorkspaceIde => "IDE",
        ApplicationKind.WorkspaceCli => "CLI",
        ApplicationKind.SingleWindowAgent => "Windows",
        _ => Kind.ToString()
    };

    public string Command
    {
        get => _command;
        set => SetField(ref _command, value ?? string.Empty);
    }

    public string ResolvedCommand { get; }

    public string StatusText { get; }

    public string ToolTip { get; }

    public string DisplayCommand => string.IsNullOrWhiteSpace(Command) ? "(自動検出)" : Command;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName == nameof(Command))
        {
            OnPropertyChanged(nameof(DisplayCommand));
        }

        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
