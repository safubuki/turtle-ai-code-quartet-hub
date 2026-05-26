namespace TurtleAIQuartetHub.Panel.Services;

internal enum WindowSlotRefreshState
{
    NoWindow,
    Missing,
    Ready
}

internal sealed record WindowSlotStatusSnapshot(
    string Name,
    string RuntimeSlotName,
    string ApplicationId,
    IReadOnlyList<string> ProcessNames,
    IntPtr WindowHandle,
    string WindowTitle,
    string CurrentWorkspacePath,
    DateTimeOffset? WorkspaceRefreshedAt);

internal sealed record WindowSlotStatusRefreshResult(
    string SlotName,
    IntPtr WindowHandle,
    WindowSlotRefreshState State,
    WindowInfo? Window,
    string? CurrentWorkspacePath,
    DateTimeOffset? WorkspaceRefreshedAt,
    long ElapsedMilliseconds);
