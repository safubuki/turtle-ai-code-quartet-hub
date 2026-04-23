param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('A', 'B', 'C', 'D')]
    [string]$Slot,

    [Parameter(Mandatory = $false)]
    [string]$Prompt = 'status smoke test',

    [int]$TimeoutSeconds = 120,
    [int]$PollIntervalSeconds = 2,
    [switch]$WatchOnly,
    [switch]$DumpUiHints
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$signature = @"
using System;
using System.Runtime.InteropServices;

public static class NativeMethods
{
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
}
"@

Add-Type -TypeDefinition $signature | Out-Null

function Get-ProbeResult {
    $json = dotnet run --project .\tools\AiStatusSmoke\AiStatusSmoke.csproj -- --slot $Slot --json
    $result = $json | ConvertFrom-Json
    if ($result -is [System.Array]) {
        return $result[0]
    }

    return $result
}

function Find-ChatInputElement {
    param([IntPtr]$Handle)

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($Handle)
    if ($null -eq $root) {
        return $null
    }

    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $queue = [System.Collections.Generic.Queue[System.Windows.Automation.AutomationElement]]::new()
    $queue.Enqueue($root)
    $best = $null
    $bestScore = [double]::MinValue
    $inspected = 0

    while ($queue.Count -gt 0 -and $inspected -lt 2400) {
        $element = $queue.Dequeue()
        $inspected++

        try {
            $controlType = $element.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $true)
            $isEnabled = $element.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::IsEnabledProperty, $true)
            $isOffscreen = $element.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::IsOffscreenProperty, $true)
            $rect = $element.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::BoundingRectangleProperty, $true)

            if (($controlType -eq [System.Windows.Automation.ControlType]::Edit -or $controlType -eq [System.Windows.Automation.ControlType]::Document) `
                -and ($isEnabled -isnot [bool] -or $isEnabled) `
                -and -not ($isOffscreen -is [bool] -and $isOffscreen) `
                -and $rect -is [System.Windows.Rect] `
                -and $rect.Width -gt 80 `
                -and $rect.Height -gt 24) {
                $score = ($rect.Y + $rect.Height) + ($rect.Width / 200.0)
                if ($score -gt $bestScore) {
                    $best = $element
                    $bestScore = $score
                }
            }
        } catch {
        }

        try {
            $child = $walker.GetFirstChild($element)
            while ($null -ne $child) {
                $queue.Enqueue($child)
                $child = $walker.GetNextSibling($child)
            }
        } catch {
        }
    }

    return $best
}

function Show-UiHints {
    param([IntPtr]$Handle)

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($Handle)
    if ($null -eq $root) {
        Write-Host "UI hints: root not available."
        return
    }

    $keywords = '思考','考え','Working','Running','Generating','Thinking','Stop','停止','Continue','Allow','承認','Try Again','実行中','待機中','完了','codex','copilot','agent'
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $queue = [System.Collections.Generic.Queue[System.Windows.Automation.AutomationElement]]::new()
    $queue.Enqueue($root)
    $inspected = 0
    $printed = 0

    while ($queue.Count -gt 0 -and $inspected -lt 2400 -and $printed -lt 60) {
        $element = $queue.Dequeue()
        $inspected++

        try {
            $name = [string]$element.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty, $true)
            $automationId = [string]$element.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $true)
            $className = [string]$element.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::ClassNameProperty, $true)
            $isOffscreen = $element.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::IsOffscreenProperty, $true)
            $joined = "$name $automationId $className"

            if (-not ($isOffscreen -is [bool] -and $isOffscreen) -and ($keywords | Where-Object { $joined -like "*$_*" })) {
                Write-Host ("UI hint name=[{0}] id=[{1}] class=[{2}]" -f $name, $automationId, $className)
                $printed++
            }
        } catch {
        }

        try {
            $child = $walker.GetFirstChild($element)
            while ($null -ne $child) {
                $queue.Enqueue($child)
                $child = $walker.GetNextSibling($child)
            }
        } catch {
        }
    }
}

function Focus-WindowAndInput {
    param(
        [IntPtr]$Handle,
        [string]$PromptText
    )

    [NativeMethods]::ShowWindowAsync($Handle, 9) | Out-Null
    Start-Sleep -Milliseconds 150
    [NativeMethods]::SetForegroundWindow($Handle) | Out-Null
    Start-Sleep -Milliseconds 250

    $input = Find-ChatInputElement -Handle $Handle
    if ($null -eq $input) {
        throw "チャット入力欄を見つけられませんでした。対象ウィンドウでチャット入力欄を開いた状態にしてください。"
    }

    $input.SetFocus()
    Start-Sleep -Milliseconds 150
    Set-Clipboard -Value $PromptText

    $shell = New-Object -ComObject WScript.Shell
    $shell.SendKeys('^a')
    Start-Sleep -Milliseconds 100
    $shell.SendKeys('^v')
    Start-Sleep -Milliseconds 100
    $shell.SendKeys('~')
}

$probe = Get-ProbeResult
if ($null -eq $probe) {
    throw "スロット $Slot の検証情報を取得できませんでした。"
}

if (-not [bool]$probe.Resolved -or [int64]$probe.WindowHandle -eq 0) {
    throw "スロット $Slot の VS Code ウィンドウを解決できませんでした。現在のウィンドウタイトルと保存済みワークスペース名を確認してください。"
}

$handle = [IntPtr]([int64]$probe.WindowHandle)
if (-not $WatchOnly) {
    Focus-WindowAndInput -Handle $handle -PromptText $Prompt
}

if ($DumpUiHints) {
    Show-UiHints -Handle $handle
}

$startAt = Get-Date
$lastStatus = $null

while (((Get-Date) - $startAt).TotalSeconds -lt $TimeoutSeconds) {
    $probe = Get-ProbeResult
    if ($null -eq $probe) {
        Start-Sleep -Seconds $PollIntervalSeconds
        continue
    }

    if (-not [bool]$probe.Resolved) {
        Write-Host "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') [$Slot] unresolved - VS Code ウィンドウを再解決できませんでした。"
        break
    }

    $status = [string]$probe.Status
    if ($status -ne $lastStatus) {
        $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
        Write-Host "$timestamp [$Slot] $status - $($probe.Detail)"
        $lastStatus = $status
    }

    if ($status -in @('Completed', 'Error', 'NeedsAttention')) {
        break
    }

    Start-Sleep -Seconds $PollIntervalSeconds
}
