namespace TurtleAIQuartetHub.Panel.Models;

public sealed class SavedSlotState
{
    public string Name { get; set; } = string.Empty;

    public string RuntimeSlotName { get; set; } = string.Empty;

    public string PanelTitle { get; set; } = string.Empty;

    public string AssignedPath { get; set; } = string.Empty;

    public string ApplicationId { get; set; } = string.Empty;

    public string SavedWorkspacePath { get; set; } = string.Empty;

    public bool SavedWorkspaceConfirmed { get; set; }

    public long WindowHandle { get; set; }

    public VscodeLayoutPreference? PreferredLayout { get; set; }
}
