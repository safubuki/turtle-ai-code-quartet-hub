using System.Diagnostics;
using System.IO;
using TurtleAIQuartetHub.Panel.Services;

internal static class PublishStopPanelTests
{
    public const int Count = 6;

    public static async Task RunAsync(List<string> failures)
    {
        VerifyExitCommandMatching(failures);
        await VerifyExitPipeDeliveryAsync(failures);

        var scriptPath = FindStopRunningPanelScriptPath();
        if (scriptPath is null)
        {
            failures.Add("配布前終了スクリプト: scripts/Stop-RunningPanel.ps1 が見つかりません。");
            return;
        }

        await VerifyNoRunningProcessExitsZeroAsync(scriptPath, failures);
        await VerifyGracefulPipeExitBeforeForceStopAsync(scriptPath, failures);
        await VerifyForceStopFallbackWhenPipeUnavailableAsync(scriptPath, failures);
        await VerifyFileUnlockCheckAsync(scriptPath, failures);
    }

    private static void VerifyExitCommandMatching(List<string> failures)
    {
        var ok = SingleInstanceCoordinator.IsExitCommand(["--exit"])
            && SingleInstanceCoordinator.IsExitCommand(["--EXIT"])
            && !SingleInstanceCoordinator.IsExitCommand(null)
            && !SingleInstanceCoordinator.IsExitCommand([])
            && !SingleInstanceCoordinator.IsExitCommand(["--activate"])
            && !SingleInstanceCoordinator.IsExitCommand(["--exit", "extra"])
            && !SingleInstanceCoordinator.IsExitCommand([""]);

        Check(failures, "IsExitCommand は単独の --exit（大文字小文字不問）だけを受け付ける", ok);
    }

    private static async Task VerifyExitPipeDeliveryAsync(List<string> failures)
    {
        var token = Guid.NewGuid().ToString("N");
        var mutexName = @"Local\TurtleHubExitTest_" + token;
        var pipeName = "TurtleHubExitPipe_" + token;
        using var coordinator = new SingleInstanceCoordinator(mutexName, pipeName);
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.CommandReceived += args => received.TrySetResult(args);

        var sent = await coordinator.SendToPrimaryAsync([SingleInstanceCoordinator.ExitCommandName]);
        var delivered = await Task.WhenAny(received.Task, Task.Delay(3000)) == received.Task
            ? await received.Task
            : null;

        Check(
            failures,
            "名前付きパイプ経由で --exit コマンドを受信できる",
            sent && SingleInstanceCoordinator.IsExitCommand(delivered),
            $"sent={sent}, delivered={string.Join(",", delivered ?? [])}");
    }

    private static async Task VerifyNoRunningProcessExitsZeroAsync(string scriptPath, List<string> failures)
    {
        var token = Guid.NewGuid().ToString("N");
        var result = await RunPowerShellFileAsync(
            scriptPath,
            $"-ProcessName \"TurtleMissingProc_{token}\" -PipeName \"TurtleMissingPipe_{token}\"");

        Check(
            failures,
            "アプリ未起動時は終了処理スクリプトがエラーなく終了コード 0 で完了する",
            result.ExitCode == 0,
            $"exit={result.ExitCode}, stdout={result.Stdout}, stderr={result.Stderr}");
    }

    private static async Task VerifyGracefulPipeExitBeforeForceStopAsync(string scriptPath, List<string> failures)
    {
        var token = Guid.NewGuid().ToString("N");
        var mutexName = @"Local\TurtleHubGracefulTest_" + token;
        var pipeName = "TurtleHubGracefulPipe_" + token;
        using var coordinator = new SingleInstanceCoordinator(mutexName, pipeName);
        using var dummyProcess = StartSleepingPowerShell();
        if (dummyProcess is null)
        {
            Check(failures, "パイプ経由の正常終了で強制終了せずプロセスを閉じられる", false, "ダミープロセス起動失敗");
            return;
        }

        try
        {
            var receivedExit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.CommandReceived += args =>
            {
                if (SingleInstanceCoordinator.IsExitCommand(args))
                {
                    try
                    {
                        if (!dummyProcess.HasExited)
                        {
                            dummyProcess.Kill(entireProcessTree: false);
                        }
                    }
                    catch
                    {
                    }

                    receivedExit.TrySetResult(true);
                }
            };

            var command = $". '{EscapePsSingleQuoted(scriptPath)}'; "
                + $"$res = Invoke-StopRunningPanel -ProcessName 'powershell' -PipeName '{pipeName}' "
                + $"-TargetProcessIds @({dummyProcess.Id}) -GracefulTimeoutMs 4000 -TerminateTimeoutMs 2000; "
                + "Write-Output \"RESULT:$($res.FoundCount):$($res.GracefulCount):$($res.TerminatedCount)\"";

            var psResult = await RunPowerShellCommandAsync(command);
            var gotPipe = await Task.WhenAny(receivedExit.Task, Task.Delay(1000)) == receivedExit.Task && await receivedExit.Task;
            var exited = dummyProcess.HasExited || dummyProcess.WaitForExit(1000);

            Check(
                failures,
                "パイプ応答時は強制終了に落ちず正常終了経路で終了する",
                psResult.ExitCode == 0
                && gotPipe
                && exited
                && psResult.Stdout.Contains("RESULT:1:1:0", StringComparison.Ordinal),
                $"exit={psResult.ExitCode}, gotPipe={gotPipe}, exited={exited}, stdout={psResult.Stdout}, stderr={psResult.Stderr}");
        }
        finally
        {
            CleanupProcess(dummyProcess);
        }
    }

    private static async Task VerifyForceStopFallbackWhenPipeUnavailableAsync(string scriptPath, List<string> failures)
    {
        var token = Guid.NewGuid().ToString("N");
        var missingPipeName = "TurtleHubMissingPipe_" + token;
        using var dummyProcess = StartSleepingPowerShell();
        if (dummyProcess is null)
        {
            Check(failures, "パイプ未応答時はプロセス終了フォールバックで停止できる", false, "ダミープロセス起動失敗");
            return;
        }

        try
        {
            var command = $". '{EscapePsSingleQuoted(scriptPath)}'; "
                + $"$res = Invoke-StopRunningPanel -ProcessName 'powershell' -PipeName '{missingPipeName}' "
                + $"-TargetProcessIds @({dummyProcess.Id}) -GracefulTimeoutMs 200 -TerminateTimeoutMs 4000; "
                + "Write-Output \"RESULT:$($res.FoundCount):$($res.GracefulCount):$($res.TerminatedCount)\"";

            var psResult = await RunPowerShellCommandAsync(command);
            var exited = dummyProcess.HasExited || dummyProcess.WaitForExit(1000);

            Check(
                failures,
                "パイプ未応答時はプロセス終了フォールバックで確実に停止できる",
                psResult.ExitCode == 0
                && exited
                && psResult.Stdout.Contains("RESULT:1:0:1", StringComparison.Ordinal),
                $"exit={psResult.ExitCode}, exited={exited}, stdout={psResult.Stdout}, stderr={psResult.Stderr}");
        }
        finally
        {
            CleanupProcess(dummyProcess);
        }
    }

    private static async Task VerifyFileUnlockCheckAsync(string scriptPath, List<string> failures)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"turtle-lock-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempFile, "lock-test");
            var escapedScript = EscapePsSingleQuoted(scriptPath);
            var escapedFile = EscapePsSingleQuoted(tempFile);
            var token = Guid.NewGuid().ToString("N");

            (int ExitCode, string Stdout, string Stderr) lockedResult;
            using (var lockStream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var command = $". '{escapedScript}'; "
                    + $"Invoke-StopRunningPanel -ProcessName 'TurtleMissing_{token}' -PipeName 'TurtleMissing_{token}' "
                    + $"-CheckPaths @('{escapedFile}') -FileUnlockTimeoutMs 250 | Out-Null";
                lockedResult = await RunPowerShellCommandAsync(command);
            }

            var unlockedCommand = $". '{escapedScript}'; "
                + $"$res = Invoke-StopRunningPanel -ProcessName 'TurtleMissing_{token}' -PipeName 'TurtleMissing_{token}' "
                + $"-CheckPaths @('{escapedFile}') -FileUnlockTimeoutMs 1000; "
                + "Write-Output \"UNLOCKED:$($res.FoundCount)\"";
            var unlockedResult = await RunPowerShellCommandAsync(unlockedCommand);

            Check(
                failures,
                "対象ファイルがロック中は待機タイムアウトで失敗し、解放後は成功する",
                lockedResult.ExitCode != 0
                && unlockedResult.ExitCode == 0
                && unlockedResult.Stdout.Contains("UNLOCKED:0", StringComparison.Ordinal),
                $"lockedExit={lockedResult.ExitCode}, unlockedExit={unlockedResult.ExitCode}, unlockedOut={unlockedResult.Stdout}, unlockedErr={unlockedResult.Stderr}");
        }
        finally
        {
            try { File.Delete(tempFile); } catch (IOException) { }
        }
    }

    private static Process? StartSleepingPowerShell()
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static void CleanupProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                process.WaitForExit(1000);
            }
        }
        catch
        {
        }
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunPowerShellFileAsync(string scriptPath, string scriptArguments)
        => RunPowerShellInternalAsync($"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" {scriptArguments}");

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunPowerShellCommandAsync(string command)
        => RunPowerShellInternalAsync($"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"");

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPowerShellInternalAsync(string arguments)
    {
        using var ps = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        ps.Start();
        var stdoutTask = ps.StandardOutput.ReadToEndAsync();
        var stderrTask = ps.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(10000);
        await ps.WaitForExitAsync(cts.Token);
        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();
        return (ps.ExitCode, stdout, stderr);
    }

    private static string EscapePsSingleQuoted(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string? FindStopRunningPanelScriptPath()
    {
        foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(seed);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "scripts", "Stop-RunningPanel.ps1");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }
        }

        return null;
    }

    private static void Check(List<string> failures, string name, bool condition, string? detail = null)
    {
        if (!condition)
        {
            failures.Add(string.IsNullOrWhiteSpace(detail)
                ? $"配布前アプリ終了: {name}"
                : $"配布前アプリ終了: {name} ({detail})");
        }
    }
}
