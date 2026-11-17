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

    // まだ見ていない AI の完了（青）。同じウィンドウ（WindowHandle）が再起動後も開いているときだけ戻す。
    public string? AiDoneTool { get; set; }

    public DateTimeOffset? AiDoneAt { get; set; }

    // 完了したタスクにかかった時間（秒）。止まった時計として戻す。
    public double? AiDoneDurationSeconds { get; set; }

    // 完了したときのワークスペース。再起動後にそのウィンドウが別のフォルダを開いていたら、戻した完了を捨てる。
    public string? AiDoneWorkspacePath { get; set; }

    public VscodeLayoutPreference? PreferredLayout { get; set; }
}
