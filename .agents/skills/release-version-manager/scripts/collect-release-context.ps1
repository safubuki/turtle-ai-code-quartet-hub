param(
  [string]$Repo = '.',
  [int]$MaxCommits = 50,
  [int]$MaxFiles = 200
)

$ErrorActionPreference = 'Stop'

function Invoke-Git {
  param(
    [string]$RepoRoot,
    [string[]]$GitArgs,
    [switch]$AllowFailure
  )

  $safeRepo = $RepoRoot -replace '\\', '/'
  $output = & git -c "safe.directory=$safeRepo" @GitArgs 2>&1
  if ($LASTEXITCODE -ne 0) {
    if ($AllowFailure) {
      return $null
    }
    throw ($output -join "`n")
  }
  return ($output -join "`n").Trim()
}

$repoRoot = (Resolve-Path $Repo).Path
$versionPath = Join-Path $repoRoot 'version.json'
$versionData = Get-Content -Raw -Encoding utf8 $versionPath | ConvertFrom-Json

$insideWorkTree = Invoke-Git -RepoRoot $repoRoot -GitArgs @('rev-parse', '--is-inside-work-tree') -AllowFailure
if ($insideWorkTree -ne 'true') {
  [ordered]@{
    repoRoot = ($repoRoot -replace '\\', '/')
    gitAvailable = $false
    versionJsonVersion = $versionData.version
    historyPreviousVersion = $versionData.history.previousVersion
    latestTag = $null
    commits = @()
    changedFiles = @()
    dirtyWorkingTree = $false
    workingTree = @()
  } | ConvertTo-Json -Depth 6
  exit 0
}

$latestTag = Invoke-Git -RepoRoot $repoRoot -GitArgs @('describe', '--tags', '--abbrev=0') -AllowFailure
$revisionRange = if ($latestTag) { "$latestTag..HEAD" } else { 'HEAD' }

$commitsRaw = Invoke-Git -RepoRoot $repoRoot -GitArgs @('log', '--no-merges', "--pretty=format:%H`t%an`t%ad`t%s", '--date=short', $revisionRange) -AllowFailure
$changedFilesRaw = if ($latestTag) {
  Invoke-Git -RepoRoot $repoRoot -GitArgs @('diff', '--name-only', $revisionRange) -AllowFailure
} else {
  Invoke-Git -RepoRoot $repoRoot -GitArgs @('ls-files') -AllowFailure
}
$workingTreeRaw = Invoke-Git -RepoRoot $repoRoot -GitArgs @('status', '--short') -AllowFailure

$commits = @()
if ($commitsRaw) {
  $commits = $commitsRaw -split "`r?`n" |
    Where-Object { $_ } |
    Select-Object -First $MaxCommits |
    ForEach-Object {
      $parts = $_ -split "`t", 4
      [ordered]@{
        hash = if ($parts[0].Length -ge 7) { $parts[0].Substring(0, 7) } else { $parts[0] }
        author = if ($parts.Count -ge 2) { $parts[1] } else { '' }
        date = if ($parts.Count -ge 3) { $parts[2] } else { '' }
        subject = if ($parts.Count -ge 4) { $parts[3] } else { '' }
      }
    }
}

$changedFiles = @()
if ($changedFilesRaw) {
  $changedFiles = $changedFilesRaw -split "`r?`n" |
    Where-Object { $_ } |
    Select-Object -First $MaxFiles
}

$workingTree = @()
if ($workingTreeRaw) {
  $workingTree = $workingTreeRaw -split "`r?`n" | Where-Object { $_ }
}

[ordered]@{
  repoRoot = ($repoRoot -replace '\\', '/')
  gitAvailable = $true
  versionJsonVersion = $versionData.version
  historyPreviousVersion = $versionData.history.previousVersion
  latestTag = if ($latestTag) { $latestTag } else { $null }
  commits = $commits
  changedFiles = $changedFiles
  dirtyWorkingTree = ($workingTree.Count -gt 0)
  workingTree = $workingTree
} | ConvertTo-Json -Depth 6
