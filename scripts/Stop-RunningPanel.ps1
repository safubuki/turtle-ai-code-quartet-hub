param(
    [string]$ProcessName = "TurtleAIQuartetHub",
    [string]$PipeName = "TurtleAIQuartetHub.Commands",
    [string]$OutputExecutablePath = "",
    [string[]]$LockCheckPaths = @(),
    [int]$GracefulTimeoutMs = 3000,
    [int]$TerminateTimeoutMs = 5000,
    [int]$FileUnlockTimeoutMs = 5000,
    [switch]$SkipPipeExit,
    [switch]$SkipDefaultLockCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-DefaultLockCheckPaths {
    param(
        [string]$RepoRoot,
        [string]$ProcessName = "TurtleAIQuartetHub",
        [string]$OutputExecutablePath,
        [string[]]$ExplicitPaths,
        [switch]$SkipDefaultLockCheck
    )

    $paths = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($OutputExecutablePath)) {
        $candidates.Add($OutputExecutablePath)
    }

    if (-not $SkipDefaultLockCheck -and
        $ProcessName -eq "TurtleAIQuartetHub" -and
        -not [string]::IsNullOrWhiteSpace($RepoRoot)) {
        $candidates.Add((Join-Path $RepoRoot "dist\turtle-ai-quartet-hub\TurtleAIQuartetHub.exe"))
        $candidates.Add((Join-Path $RepoRoot "src\TurtleAIQuartetHub.Panel\bin\Release\net10.0-windows\win-x64\TurtleAIQuartetHub.exe"))
        $candidates.Add((Join-Path $RepoRoot "src\TurtleAIQuartetHub.Panel\bin\Release\net10.0-windows\win-x64\publish\TurtleAIQuartetHub.exe"))
    }

    if ($ExplicitPaths) {
        foreach ($path in $ExplicitPaths) {
            if (-not [string]::IsNullOrWhiteSpace($path)) {
                $candidates.Add($path)
            }
        }
    }

    foreach ($candidate in $candidates) {
        try {
            $full = [System.IO.Path]::GetFullPath($candidate)
        } catch {
            $full = $candidate
        }

        if ($seen.Add($full)) {
            $paths.Add($full)
        }
    }

    return $paths.ToArray()
}

function Get-RunningTargetProcesses {
    param(
        [string]$ProcessName,
        [int[]]$TargetProcessIds = @()
    )

    $currentPid = [System.Diagnostics.Process]::GetCurrentProcess().Id
    $currentSessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId

    if ($TargetProcessIds -and $TargetProcessIds.Count -gt 0) {
        $matched = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
        foreach ($targetPid in ($TargetProcessIds | Select-Object -Unique)) {
            if ($targetPid -eq $currentPid) {
                continue
            }

            $proc = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
            if ($null -eq $proc) {
                continue
            }

            try {
                if ($proc.SessionId -eq $currentSessionId) {
                    $matched.Add($proc)
                }
            } catch {
            }
        }

        return $matched.ToArray()
    }

    $candidates = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    $result = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
    foreach ($proc in $candidates) {
        if ($null -eq $proc -or $proc.Id -eq $currentPid) {
            continue
        }

        try {
            if ($proc.SessionId -eq $currentSessionId) {
                $result.Add($proc)
            }
        } catch {
        }
    }

    return $result.ToArray()
}

function Send-PanelExitCommand {
    param(
        [string]$PipeName,
        [int]$ConnectTimeoutMs = 500
    )

    if ([string]::IsNullOrWhiteSpace($PipeName)) {
        return $false
    }

    $pipe = $null
    $writer = $null
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
            ".",
            $PipeName,
            [System.IO.Pipes.PipeDirection]::Out)
        $pipe.Connect($ConnectTimeoutMs)

        $encoding = New-Object System.Text.UTF8Encoding($false)
        $writer = New-Object System.IO.StreamWriter($pipe, $encoding, 1024, $true)
        $writer.AutoFlush = $true
        $writer.Write('["--exit"]')
        $writer.Flush()
        return $true
    } catch {
        return $false
    } finally {
        if ($null -ne $writer) {
            try { $writer.Dispose() } catch {}
        }
        if ($null -ne $pipe) {
            try { $pipe.Dispose() } catch {}
        }
    }
}

function Wait-ProcessIdsExited {
    param(
        [int[]]$ProcessIds,
        [int]$TimeoutMs
    )

    $remaining = @($ProcessIds | Select-Object -Unique)
    if ($remaining.Count -eq 0) {
        return @()
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $stillRunning = [System.Collections.Generic.List[int]]::new()
        foreach ($processId in $remaining) {
            $proc = Get-Process -Id $processId -ErrorAction SilentlyContinue
            if ($null -ne $proc) {
                $stillRunning.Add($processId)
            }
        }

        $remaining = $stillRunning.ToArray()
        if ($remaining.Count -eq 0 -or $stopwatch.ElapsedMilliseconds -ge $TimeoutMs) {
            return $remaining
        }

        Start-Sleep -Milliseconds 100
    }
}

function Wait-FilesUnlocked {
    param(
        [string[]]$Paths,
        [int]$TimeoutMs
    )

    if (-not $Paths -or $Paths.Count -eq 0) {
        return
    }

    $existing = @($Paths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($existing.Count -eq 0) {
        return
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    foreach ($path in $existing) {
        while ($true) {
            $stream = $null
            try {
                $stream = [System.IO.File]::Open(
                    $path,
                    [System.IO.FileMode]::Open,
                    [System.IO.FileAccess]::ReadWrite,
                    [System.IO.FileShare]::None)
                break
            } catch {
                if ($stopwatch.ElapsedMilliseconds -ge $TimeoutMs) {
                    throw "File is still locked by another process: $path ($($_.Exception.Message))"
                }

                Start-Sleep -Milliseconds 100
            } finally {
                if ($null -ne $stream) {
                    try { $stream.Dispose() } catch {}
                }
            }
        }
    }
}

function Invoke-StopRunningPanel {
    param(
        [string]$ProcessName = "TurtleAIQuartetHub",
        [string]$PipeName = "TurtleAIQuartetHub.Commands",
        [string[]]$CheckPaths = @(),
        [int[]]$TargetProcessIds = @(),
        [int]$GracefulTimeoutMs = 3000,
        [int]$TerminateTimeoutMs = 5000,
        [int]$FileUnlockTimeoutMs = 5000,
        [switch]$SkipPipeExit
    )

    $processes = @(Get-RunningTargetProcesses -ProcessName $ProcessName -TargetProcessIds $TargetProcessIds)
    $gracefulCount = 0
    $terminatedCount = 0

    if ($processes.Count -eq 0) {
        Write-Host "[INFO] No running $ProcessName process found."
    } else {
        $targetIds = @($processes | ForEach-Object { $_.Id })
        Write-Host "[INFO] Found running $ProcessName process(es): $($targetIds -join ', '). Stopping before publish..."

        $remainingIds = $targetIds
        if (-not $SkipPipeExit -and $GracefulTimeoutMs -gt 0) {
            if (Send-PanelExitCommand -PipeName $PipeName) {
                $remainingIds = @(Wait-ProcessIdsExited -ProcessIds $targetIds -TimeoutMs $GracefulTimeoutMs)
                $gracefulCount = $targetIds.Count - $remainingIds.Count
                if ($remainingIds.Count -eq 0) {
                    Write-Host "[INFO] $ProcessName exited gracefully."
                }
            }
        }

        if ($remainingIds.Count -gt 0) {
            Write-Host "[INFO] Force-stopping remaining $ProcessName process(es): $($remainingIds -join ', ')..."
            foreach ($processId in $remainingIds) {
                try {
                    # Terminate only the app process itself; never kill child IDE/CLI processes.
                    Stop-Process -Id $processId -Force -ErrorAction Stop
                } catch {
                    if ($_.FullyQualifiedErrorId -like "NoProcessFoundForGivenId*") {
                        continue
                    }

                    throw "Failed to stop $ProcessName (PID $processId): $($_.Exception.Message)"
                }
            }

            $stillAlive = @(Wait-ProcessIdsExited -ProcessIds $remainingIds -TimeoutMs $TerminateTimeoutMs)
            if ($stillAlive.Count -gt 0) {
                throw "$ProcessName process(es) did not exit within $TerminateTimeoutMs ms: $($stillAlive -join ', ')"
            }

            $terminatedCount = $remainingIds.Count
            Write-Host "[INFO] Stopped $ProcessName process(es)."
        }
    }

    Wait-FilesUnlocked -Paths $CheckPaths -TimeoutMs $FileUnlockTimeoutMs

    return [pscustomobject]@{
        FoundCount      = $processes.Count
        GracefulCount   = $gracefulCount
        TerminatedCount = $terminatedCount
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
        $resolvedPaths = Resolve-DefaultLockCheckPaths `
            -RepoRoot $repoRoot `
            -ProcessName $ProcessName `
            -OutputExecutablePath $OutputExecutablePath `
            -ExplicitPaths $LockCheckPaths `
            -SkipDefaultLockCheck:$SkipDefaultLockCheck

        $null = Invoke-StopRunningPanel `
            -ProcessName $ProcessName `
            -PipeName $PipeName `
            -CheckPaths $resolvedPaths `
            -GracefulTimeoutMs $GracefulTimeoutMs `
            -TerminateTimeoutMs $TerminateTimeoutMs `
            -FileUnlockTimeoutMs $FileUnlockTimeoutMs `
            -SkipPipeExit:$SkipPipeExit
        exit 0
    } catch {
        Write-Host "[ERROR] $($_.Exception.Message)"
        exit 1
    }
}
