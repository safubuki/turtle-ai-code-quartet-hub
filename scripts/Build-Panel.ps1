param(
    [string]$ProjectPath = ".\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj",
    [string]$ArtifactsRoot = ".\.build-tmp\panel-dev",
    [string]$Configuration = "Debug",
    [switch]$Run
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Resolve-RepoPath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repoRoot $Path
}

function New-ArtifactPath {
    param([string]$RootPath)

    $timestamp = Get-Date -Format "yyyyMMdd-HHmmssfff"
    $path = Join-Path $RootPath $timestamp
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    return $path
}

function Find-BuiltExecutable {
    param([string]$ArtifactsPath)

    $candidate = Get-ChildItem -Path $ArtifactsPath -Recurse -Filter "TurtleAIQuartetHub.exe" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($null -eq $candidate) {
        throw "ビルド済みの TurtleAIQuartetHub.exe を見つけられませんでした: $ArtifactsPath"
    }

    return $candidate.FullName
}

$projectFullPath = Resolve-RepoPath $ProjectPath
$artifactsRootFullPath = Resolve-RepoPath $ArtifactsRoot

if (-not (Test-Path -LiteralPath $projectFullPath)) {
    throw "WPF プロジェクトが見つかりません: $projectFullPath"
}

New-Item -ItemType Directory -Path $artifactsRootFullPath -Force | Out-Null
$artifactsPath = New-ArtifactPath -RootPath $artifactsRootFullPath

Write-Host "ArtifactsPath: $artifactsPath"

& dotnet build $projectFullPath -c $Configuration --artifacts-path $artifactsPath
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$exePath = Find-BuiltExecutable -ArtifactsPath $artifactsPath
Write-Host "BuiltExe: $exePath"

if ($Run) {
    Start-Process -FilePath $exePath | Out-Null
    Write-Host "Started: $exePath"
}