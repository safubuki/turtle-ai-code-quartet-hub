using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

/// <summary>
/// 同一ユーザー・同一ログオンセッションの VS Code だけを、確認済みの識別情報に従って終了する。
/// 起動中の他アプリや新規プロセスを巻き込まないよう、子プロセスツリーは終了しない。
/// </summary>
public sealed class VscodeProcessRecoveryService
{
    private readonly IVscodeRecoveryProcessAccess _processAccess;
    private readonly TimeSpan _exitTimeout;

    public VscodeProcessRecoveryService()
        : this(new WindowsVscodeRecoveryProcessAccess(), TimeSpan.FromSeconds(5))
    {
    }

    internal VscodeProcessRecoveryService(IVscodeRecoveryProcessAccess processAccess, TimeSpan exitTimeout)
    {
        ArgumentNullException.ThrowIfNull(processAccess);
        if (exitTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(exitTimeout));
        _processAccess = processAccess;
        _exitTimeout = exitTimeout;
    }

    public Task<VscodeProcessScan> ScanAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(cancellationToken), cancellationToken);

    public Task<VscodeProcessRecoveryResult> TerminateAsync(
        IReadOnlyList<VscodeProcessInfo> confirmedProcesses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmedProcesses);
        // 呼び出し元による一覧変更で、確認していない対象が途中から入らないよう固定する。
        var targets = confirmedProcesses.DistinctBy(process => process.ProcessId).ToArray();
        return Task.Run(async () =>
        {
            var results = await Task.WhenAll(targets.Select(process => TerminateOneAsync(process, cancellationToken)));
            // 残留・確認後に起動したものは報告のみ。再検出したプロセスは自動終了しない。
            var remaining = Scan(CancellationToken.None);
            return new VscodeProcessRecoveryResult(results, remaining);
        }, cancellationToken);
    }

    private VscodeProcessScan Scan(CancellationToken cancellationToken)
    {
        var items = new List<VscodeProcessInfo>();
        var issues = new List<string>();
        IReadOnlyList<int> processIds;
        try
        {
            processIds = _processAccess.EnumerateProcessIds();
        }
        catch (Exception ex) when (IsProcessAccessException(ex))
        {
            return new VscodeProcessScan([], [$"VS Code のプロセス一覧を取得できませんでした: {ex.Message}"]);
        }

        foreach (var processId in processIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = _processAccess.Open(processId);
                if (process is null || process.HasExited) continue;
                var info = process.ReadInfo();
                if (IsTarget(info)) items.Add(info);
            }
            catch (ArgumentException)
            {
                // 一覧取得直後に自然終了したプロセス。
            }
            catch (Exception ex) when (IsProcessAccessException(ex))
            {
                issues.Add($"PID {processId}: 情報を確認できないため終了対象に含めません（{ex.Message}）。");
            }
        }

        return new VscodeProcessScan(items.OrderBy(process => process.ProcessName).ThenBy(process => process.ProcessId).ToArray(), issues);
    }

    private async Task<VscodeProcessRecoveryItem> TerminateOneAsync(VscodeProcessInfo confirmed, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsTarget(confirmed))
            {
                return Result(VscodeProcessRecoveryStatus.IdentityChanged, "VS Code の対象条件に一致しないため終了しませんでした。");
            }

            using var process = _processAccess.Open(confirmed.ProcessId);
            if (process is null || process.HasExited)
            {
                return Result(VscodeProcessRecoveryStatus.AlreadyExited, "既に終了しています。");
            }

            var actual = process.ReadInfo();
            if (!IsTarget(actual) || !HasSameIdentity(confirmed, actual))
            {
                return Result(VscodeProcessRecoveryStatus.IdentityChanged, "確認時からプロセスが変わったため終了しませんでした。");
            }

            // ReadInfo と同じ、ハンドルを保持したプロセスだけを終了する。子プロセスは対象外。
            process.Kill(entireProcessTree: false);
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCancellation.CancelAfter(_exitTimeout);
            try
            {
                await process.WaitForExitAsync(waitCancellation.Token);
                return Result(VscodeProcessRecoveryStatus.Terminated, "終了を確認しました。");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return process.HasExited
                    ? Result(VscodeProcessRecoveryStatus.Terminated, "終了を確認しました。")
                    : Result(VscodeProcessRecoveryStatus.TimedOut, "終了要求後もプロセスが残っています。");
            }
        }
        catch (ArgumentException)
        {
            return Result(VscodeProcessRecoveryStatus.AlreadyExited, "既に終了しています。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return Result(VscodeProcessRecoveryStatus.AccessDenied, "権限がないため終了できませんでした。管理者として起動した VS Code の可能性があります。");
        }
        catch (UnauthorizedAccessException)
        {
            return Result(VscodeProcessRecoveryStatus.AccessDenied, "権限がないため終了できませんでした。");
        }
        catch (OperationCanceledException)
        {
            return Result(VscodeProcessRecoveryStatus.Failed, "終了確認を中止しました。プロセスの状態を再確認してください。");
        }
        catch (Exception ex) when (IsProcessAccessException(ex))
        {
            return Result(VscodeProcessRecoveryStatus.Failed, $"終了を確認できませんでした: {ex.Message}");
        }

        VscodeProcessRecoveryItem Result(VscodeProcessRecoveryStatus status, string message) => new(confirmed, status, message);
    }

    private bool IsTarget(VscodeProcessInfo info)
        => info.ProcessId > 0
           && info.SessionId == _processAccess.SessionId
           && !string.IsNullOrWhiteSpace(info.UserSid)
           && string.Equals(info.UserSid, _processAccess.UserSid, StringComparison.OrdinalIgnoreCase)
           && IsSupportedExecutable(info.ProcessName, info.ExecutablePath);

    internal static bool IsSupportedExecutable(string processName, string executablePath)
        => !string.IsNullOrWhiteSpace(executablePath)
           && Path.IsPathFullyQualified(executablePath)
           && (processName.Equals("Code", StringComparison.OrdinalIgnoreCase)
               && Path.GetFileName(executablePath).Equals("Code.exe", StringComparison.OrdinalIgnoreCase)
               || processName.Equals("Code - Insiders", StringComparison.OrdinalIgnoreCase)
               && Path.GetFileName(executablePath).Equals("Code - Insiders.exe", StringComparison.OrdinalIgnoreCase));

    private static bool HasSameIdentity(VscodeProcessInfo confirmed, VscodeProcessInfo actual)
        => confirmed.ProcessId == actual.ProcessId
           && confirmed.StartTimeUtc == actual.StartTimeUtc
           && confirmed.SessionId == actual.SessionId
           && string.Equals(confirmed.UserSid, actual.UserSid, StringComparison.OrdinalIgnoreCase)
           && string.Equals(confirmed.ProcessName, actual.ProcessName, StringComparison.OrdinalIgnoreCase)
           && string.Equals(confirmed.ExecutablePath, actual.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    private static bool IsProcessAccessException(Exception ex)
        => ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException or NotSupportedException;
}

internal interface IVscodeRecoveryProcessAccess
{
    int SessionId { get; }
    string UserSid { get; }
    IReadOnlyList<int> EnumerateProcessIds();
    IVscodeRecoveryProcess? Open(int processId);
}

internal interface IVscodeRecoveryProcess : IDisposable
{
    bool HasExited { get; }
    VscodeProcessInfo ReadInfo();
    void Kill(bool entireProcessTree);
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal sealed class WindowsVscodeRecoveryProcessAccess : IVscodeRecoveryProcessAccess
{
    public int SessionId { get; }
    public string UserSid { get; }

    public WindowsVscodeRecoveryProcessAccess()
    {
        using var current = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        SessionId = current.SessionId;
        UserSid = identity.User?.Value ?? throw new InvalidOperationException("現在の Windows ユーザーを確認できませんでした。");
    }

    public IReadOnlyList<int> EnumerateProcessIds()
    {
        var ids = new List<int>();
        foreach (var name in new[] { "Code", "Code - Insiders" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process) ids.Add(process.Id);
            }
        }
        return ids;
    }

    public IVscodeRecoveryProcess? Open(int processId)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            return new WindowsVscodeRecoveryProcess(process);
        }
        catch (ArgumentException)
        {
            process?.Dispose();
            return null;
        }
        catch
        {
            process?.Dispose();
            throw;
        }
    }
}

internal sealed class WindowsVscodeRecoveryProcess : IVscodeRecoveryProcess
{
    private readonly Process _process;
    private readonly SafeProcessHandle _queryHandle;

    public WindowsVscodeRecoveryProcess(Process process)
    {
        _process = process;
        // 一覧取得には終了権限を要求しない。他ユーザーのプロセスも SID を読めれば静かに除外できる。
        // このハンドルを Kill 完了まで保持し、PID が再利用された別プロセスへ切り替わらないようにする。
        _queryHandle = OpenProcess(0x1000, false, process.Id);
        if (_queryHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            _queryHandle.Dispose();
            throw new Win32Exception(error);
        }
    }

    public bool HasExited
    {
        get
        {
            if (!GetExitCodeProcess(_queryHandle, out var code)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return code != 259; // STILL_ACTIVE
        }
    }

    public VscodeProcessInfo ReadInfo()
    {
        if (!OpenProcessToken(_queryHandle, 0x0008, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        string userSid;
        int sessionId;
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            userSid = identity.User?.Value ?? throw new InvalidOperationException("プロセスの Windows ユーザーを確認できませんでした。");
            if (!GetTokenInformation(token, 12, out sessionId, sizeof(int), out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        var pathBuffer = new StringBuilder(32768);
        var pathLength = pathBuffer.Capacity;
        if (!QueryFullProcessImageName(_queryHandle, 0, pathBuffer, ref pathLength))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var path = pathBuffer.ToString();
        if (!GetProcessTimes(_queryHandle, out var creationTime, out _, out _, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        string title;
        try { title = _process.MainWindowTitle; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { title = string.Empty; }
        return new VscodeProcessInfo(_process.Id, Path.GetFileNameWithoutExtension(path), path,
            DateTime.FromFileTimeUtc(creationTime), sessionId, userSid, title);
    }

    public void Kill(bool entireProcessTree)
    {
        // 限定照会ハンドルが PID を固定している間に、.NET の終了用ハンドルも保持する。
        _ = _process.SafeHandle;
        _process.Kill(entireProcessTree);
    }
    public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);
    public void Dispose()
    {
        _process.Dispose();
        _queryHandle.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle processHandle, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle processHandle, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle processHandle, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle tokenHandle, int informationClass, out int information, int informationLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);
}
