namespace TurtleAIQuartetHub.Panel.Models;

/// <summary>確認画面を開いた時点のプロセス識別情報。終了直前に同一性を再確認する。</summary>
public sealed record VscodeProcessInfo(
    int ProcessId,
    string ProcessName,
    string ExecutablePath,
    DateTime StartTimeUtc,
    int SessionId,
    string UserSid,
    string WindowTitle)
{
    public string DisplayTitle => $"{(ProcessName.Equals("Code - Insiders", StringComparison.OrdinalIgnoreCase) ? "VS Code Insiders" : "VS Code")}（PID {ProcessId}）"
        + (string.IsNullOrWhiteSpace(WindowTitle) ? "" : $"  {WindowTitle}");
}

public sealed record VscodeProcessScan(
    IReadOnlyList<VscodeProcessInfo> Processes,
    IReadOnlyList<string> Issues);

public enum VscodeProcessRecoveryStatus
{
    Terminated,
    AlreadyExited,
    IdentityChanged,
    AccessDenied,
    TimedOut,
    Failed
}

public sealed record VscodeProcessRecoveryItem(
    VscodeProcessInfo Process,
    VscodeProcessRecoveryStatus Status,
    string Message);

public sealed record VscodeProcessRecoveryResult(
    IReadOnlyList<VscodeProcessRecoveryItem> Results,
    VscodeProcessScan RemainingScan)
{
    public int TerminatedCount => Results.Count(result => result.Status == VscodeProcessRecoveryStatus.Terminated);
    public int AlreadyExitedCount => Results.Count(result => result.Status == VscodeProcessRecoveryStatus.AlreadyExited);
    public int FailedCount => Results.Count - TerminatedCount - AlreadyExitedCount;
    public bool HasRemainingProcesses => RemainingScan.Processes.Count > 0;
}
