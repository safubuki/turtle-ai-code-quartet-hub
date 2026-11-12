using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

internal interface IVscodeRecoveryElevation
{
    Task<IReadOnlyList<VscodeProcessRecoveryItem>> TerminateAsync(
        IReadOnlyList<VscodeProcessInfo> processes,
        CancellationToken cancellationToken);
}

internal sealed class VscodeRecoveryElevationCanceledException : Exception;

/// <summary>
/// 終了権限だけを要求する。PROCESS_ALL_ACCESS はシステム版や高い整合性レベルの VS Code で拒否される。
/// </summary>
internal static class WindowsProcessTermination
{
    private const uint ProcessTerminate = 0x0001;
    private const uint Synchronize = 0x00100000;
    private const uint QueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;

    public static SafeProcessHandle OpenForTerminate(int processId, out bool canSynchronize)
    {
        var full = OpenProcess(ProcessTerminate | Synchronize | QueryLimitedInformation, false, processId);
        if (!full.IsInvalid)
        {
            canSynchronize = true;
            return full;
        }

        var error = Marshal.GetLastWin32Error();
        full.Dispose();
        if (error != 5) throw new Win32Exception(error);

        var queryAndTerminate = OpenProcess(ProcessTerminate | QueryLimitedInformation, false, processId);
        if (!queryAndTerminate.IsInvalid)
        {
            canSynchronize = false;
            return queryAndTerminate;
        }

        error = Marshal.GetLastWin32Error();
        queryAndTerminate.Dispose();
        if (error != 5) throw new Win32Exception(error);

        var terminateOnly = OpenProcess(ProcessTerminate, false, processId);
        if (!terminateOnly.IsInvalid)
        {
            canSynchronize = false;
            return terminateOnly;
        }

        error = Marshal.GetLastWin32Error();
        terminateOnly.Dispose();
        throw new Win32Exception(error);
    }

    public static void Terminate(SafeProcessHandle handle)
    {
        if (TerminateProcess(handle, 1)) return;
        var error = Marshal.GetLastWin32Error();
        if (error == 5 && GetExitCodeProcess(handle, out var code) && code != StillActive) return;
        throw new Win32Exception(error);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle processHandle, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle processHandle, out uint exitCode);
}

/// <summary>
/// 通常権限で終了できない VS Code を、確認済みの識別情報だけを渡して一度だけ昇格終了する。
/// 単一インスタンスの本流より前で処理し、昇格プロセスがパネルを開いたり既存インスタンスへ転送したりしない。
/// </summary>
internal static class VscodeProcessRecoveryElevation
{
    internal const string CommandName = "--vscode-recovery-elevated";
    private static readonly Regex RequestIdPattern = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan ElevatedWaitTimeout = TimeSpan.FromMinutes(3);

    public static bool IsElevatedRequest(string[]? args) => TryGetRequestId(args, out _);

    public static int RunElevated(string[] args)
    {
        if (!TryGetRequestId(args, out var requestId)) return 2;
        var paths = Paths(requestId);
        try
        {
            if (!File.Exists(paths.Request)) return 2;
            var processes = JsonSerializer.Deserialize<List<VscodeProcessInfo>>(File.ReadAllText(paths.Request), JsonOptions) ?? [];
            var service = new VscodeProcessRecoveryService(new WindowsVscodeRecoveryProcessAccess(), TimeSpan.FromSeconds(5));
            var result = service.TerminateAsync(processes).GetAwaiter().GetResult();
            WriteAtomically(paths.Result, JsonSerializer.Serialize(result.Results, JsonOptions));
            return result.FailedCount == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            try
            {
                WriteAtomically(paths.Result, JsonSerializer.Serialize(Array.Empty<VscodeProcessRecoveryItem>(), JsonOptions));
            }
            catch (Exception writeEx)
            {
                DiagnosticLog.Write(writeEx);
            }
            return 1;
        }
        finally
        {
            TryDelete(paths.Request);
            TryDelete(paths.RequestTemp);
        }
    }

    private static bool TryGetRequestId(string[]? args, out string requestId)
    {
        requestId = string.Empty;
        if (args is not { Length: 2 } || !string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase)) return false;
        var candidate = args[1].Trim().ToLowerInvariant();
        if (!RequestIdPattern.IsMatch(candidate)) return false;
        requestId = candidate;
        return true;
    }

    private static RecoveryPaths Paths(string requestId) => new(
        Path.Combine(RecoveryDirectory(), requestId + ".request.json"),
        Path.Combine(RecoveryDirectory(), requestId + ".request.json.tmp"),
        Path.Combine(RecoveryDirectory(), requestId + ".result.json"));

    private static string RecoveryDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurtleAIQuartetHub", "recovery");

    private static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write(ex);
        }
    }

    private sealed record RecoveryPaths(string Request, string RequestTemp, string Result);
}

internal sealed class WindowsVscodeRecoveryElevation : IVscodeRecoveryElevation
{
    public async Task<IReadOnlyList<VscodeProcessRecoveryItem>> TerminateAsync(
        IReadOnlyList<VscodeProcessInfo> processes,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurtleAIQuartetHub", "recovery");
        Directory.CreateDirectory(directory);
        var requestPath = Path.Combine(directory, requestId + ".request.json");
        var resultPath = Path.Combine(directory, requestId + ".result.json");
        var requestTemp = requestPath + ".tmp";
        try
        {
            var requestJson = JsonSerializer.Serialize(processes);
            File.WriteAllText(requestTemp, requestJson);
            File.Move(requestTemp, requestPath, overwrite: true);

            var executable = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("昇格して起動する実行ファイルを確認できませんでした。");
            try
            {
                using var elevated = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = $"{VscodeProcessRecoveryElevation.CommandName} {requestId}",
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (elevated is null) throw new InvalidOperationException("管理者権限の終了プロセスを開始できませんでした。");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new VscodeRecoveryElevationCanceledException();
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(resultPath))
                {
                    var json = await File.ReadAllTextAsync(resultPath, cancellationToken);
                    return JsonSerializer.Deserialize<List<VscodeProcessRecoveryItem>>(json) ?? [];
                }
                await Task.Delay(100, cancellationToken);
            }

            throw new TimeoutException("管理者権限での終了結果が返りませんでした。");
        }
        finally
        {
            foreach (var path in new[] { requestTemp, requestPath, resultPath, resultPath + ".tmp" })
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Write(ex);
                }
            }
        }
    }
}
