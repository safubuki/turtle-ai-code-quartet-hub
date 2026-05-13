namespace TurtleAIQuartetHub.Panel.Models;

public enum ApplicationKind
{
    WorkspaceIde,
    WorkspaceCli,
    SingleWindowAgent
}

public enum ApplicationAvailabilityStatus
{
    Unknown,
    Installed,
    NotFound,
    ConfiguredButMissing
}

public sealed class ToolApplicationConfig
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string ShortName { get; set; } = string.Empty;

    public ApplicationKind Kind { get; set; } = ApplicationKind.WorkspaceIde;

    public string Command { get; set; } = string.Empty;

    public List<string> Arguments { get; set; } = [];

    public bool SupportsMultipleWindows { get; set; } = true;

    public ApplicationDetectionConfig Detection { get; set; } = new();
}

public sealed class ApplicationDetectionConfig
{
    public List<string> Commands { get; set; } = [];

    public List<string> ProcessNames { get; set; } = [];

    public List<string> StartMenuNames { get; set; } = [];

    public List<string> AppPathNames { get; set; } = [];
}
