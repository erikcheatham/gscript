# gscript MINIMAL EXAMPLE (PowerShell) — README typo fix.
#
# The smallest viable form: stages one file, commits, pushes, exits.
# No CI watch, no post-deploy probe. Use this shape when you're shipping
# a quick doc fix and don't need the full ceremony.
#
# This is the "I just want a defensible push" tier. The full ceremony
# example shows everything; this one is the gateway drug.

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false

$CommitName  = "ai-bot"
$CommitEmail = "ai-bot@example.com"
$RepoOwner   = "your-github-username"
$RepoName    = "your-repo-name"

# Stale-lock auto-recovery + retry wrapper — minimal versions inline
function Clear-StaleGitLocks {
    param([string]$GitDir)
    $locks = @("index.lock", "HEAD.lock", "config.lock")
    foreach ($lock in $locks) {
        $p = Join-Path $GitDir $lock
        if (Test-Path $p) {
            $gitProcs = @(Get-Process -Name git, git-* -ErrorAction SilentlyContinue)
            if ($gitProcs.Count -eq 0) {
                Write-Host "Removing stale lock: $lock" -ForegroundColor Yellow
                Remove-Item $p -Force
            } else {
                Write-Host "Locks present and git processes running. Wait + retry." -ForegroundColor Yellow
                exit 1
            }
        }
    }
}
Clear-StaleGitLocks -GitDir (Join-Path $PSScriptRoot ".git")

# PAT from localmd
$LocalMdPath = Join-Path $env:USERPROFILE "private\local.md"
if (-not (Test-Path $LocalMdPath)) {
    Write-Host "ERROR: $LocalMdPath not found." -ForegroundColor Red
    exit 1
}
$content = Get-Content $LocalMdPath -Raw
if ($content -match '(github_pat_[A-Za-z0-9_]{40,})') {
    $Pat = $Matches[1]
} else {
    Write-Host "ERROR: No PAT in localmd." -ForegroundColor Red
    exit 1
}

$pushUrl = "https://x-access-token:$Pat@github.com/$RepoOwner/$RepoName.git"

# Fetch + divergence check
Write-Host "Fetching..." -ForegroundColor Cyan
git fetch --quiet $pushUrl main
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: git fetch failed." -ForegroundColor Red; exit 1 }
$behind = (git rev-list FETCH_HEAD "^HEAD" --count).Trim()
if ($behind -ne "0") {
    Write-Host "ERROR: origin/main is $behind ahead. Pull --rebase first." -ForegroundColor Red
    exit 1
}

# Trailing-null check on the one file we're staging
$file = "README.md"
$bytes = [System.IO.File]::ReadAllBytes((Join-Path $PSScriptRoot $file))
$nullCount = 0
for ($i = $bytes.Length - 1; $i -ge 0 -and $bytes[$i] -eq 0; $i--) { $nullCount++ }
if ($nullCount -gt 0) {
    Write-Host "ERROR: $file has $nullCount trailing 0x00 bytes." -ForegroundColor Red
    exit 1
}

# Stage + commit + push
git add -- $file
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: git add failed." -ForegroundColor Red; exit 1 }

$msg = "docs: fix typo in README"
git -c "user.name=$CommitName" -c "user.email=$CommitEmail" commit -m $msg
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: git commit failed." -ForegroundColor Red; exit 1 }

Write-Host "Pushing..." -ForegroundColor Cyan
git push $pushUrl main
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: git push failed." -ForegroundColor Red; exit 1 }

Write-Host "PUSHED. Done." -ForegroundColor Green

# Self-delete
Remove-Item $MyInvocation.MyCommand.Path -Force
