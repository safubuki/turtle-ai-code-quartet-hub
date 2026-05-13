namespace TurtleAIQuartetHub.Panel.Models;

public sealed class SlotApplicationOption
{
    public SlotApplicationOption(WindowSlot slot, LauncherApplication application, bool isSelected)
    {
        Slot = slot;
        Application = application;
        IsSelected = isSelected;
    }

    public WindowSlot Slot { get; }

    public LauncherApplication Application { get; }

    public string ApplicationId => Application.Id;

    public string DisplayName => Application.DisplayName;

    public string ShortName => Application.ShortName;

    public bool IsAvailable => Application.IsAvailable;

    public bool IsSelected { get; }

    public string ToolTip => Application.ToolTip;
}
