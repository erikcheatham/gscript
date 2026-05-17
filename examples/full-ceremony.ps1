# gscript FULL CEREMONY EXAMPLE (PowerShell)
#
# This is a representative per-sprint instance — what an AI session
# typically authors. Shows: multi-file stage, JSON validator on
# appsettings, CI watch with per-step granularity, multi-endpoint
# post-deploy probe, multi-paragraph commit message.
#
# Copy this as the starting point when you want the full ceremony but
# don't want to start from gscript_template.ps1's parameterized version.
# The template is more abstract; this is concrete-with-realistic-values.

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false

$CommitName  = "darwincommits"
$CommitEmail = "darwinsemailinbox@gmail.com"
$RepoOwner   = "erikcheatham"
$RepoName    = "AllThruit"
$CiWorkflowFile = "deploy-staging.yml"

$WatchCi = $true
$CiWatchMaxMinutes = 15
$CiWatchPollSeconds = 20

$ProbeEndpoints = @(
    @{ Url = "https://staging.allthruit.com/";              ExpectedRange = 200..399 }
    @{ Url = "https://staging.allthruit.com/api/v1/reviews"; ExpectedRange = 200..399 }
)

# ── Stale-lock auto-recovery ──────────────────────────────────────────
function Clear-StaleGitLocks {
    param([string]$GitDir)
    $lockNames = @("index.lock", "HEAD.lock", "config.lock", "packed-refs.lock", "shallow.lock", "fetch.lock")
    $found = @()
    foreach ($lock in $lockNames) {
        $lockPath = Join-Path $GitDir $lock
        if (Test-Path $lockPath) { $found += $lockPath }
    }
    if ($found.Count -eq 0) { return }
    $gitProcs = @(Get-Process -Name git, git-* -ErrorAction SilentlyContinue)
    if ($gitProcs.Count -gt 0) {
        Write-Host "WARN: lock files present with git processes running." -ForegroundColor Yellow
        $gitProcs | ForEach-Object { Write-Host "  PID $($_.Id) $($_.ProcessName)" -ForegroundColor Yellow }
        exit 1
    }
    foreach ($lockPath in $found) {
        Write-Host "Removing stale lock: $($lockPath | Split-Path -Leaf)" -ForegroundColor Yellow
        Remove-Item $lockPath -Force
    }
}
Clear-StaleGitLocks -GitDir (Join-Path $PSScriptRoot ".git")

# ── Git retry wrapper ─────────────────────────────────────────────────
function Invoke-GitWithRetry {
    param([string[]]$GitArgs, [int]$MaxAttempts = 3, [string]$Context = "git")
    $delay = 1
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $output = & git @GitArgs 2>&1
        if ($LASTEXITCODE -eq 0) { return $output }
        $stderr = $output -join "`n"
        if ($stderr -match "index\.lock|HEAD\.lock|Unable to create" -and $attempt -lt $MaxAttempts) {
            Write-Host "  $Context retry $attempt/$MaxAttempts in ${delay}s..." -ForegroundColor Yellow
            Start-Sleep -Seconds $delay
            Clear-StaleGitLocks -GitDir (Join-Path $PSScriptRoot ".git")
            $delay *= 2
            continue
        }
        Write-Host $stderr -ForegroundColor Red
        return $null
    }
    return $null
}

# ── PAT from localmd ──────────────────────────────────────────────────
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

# ── Trailing-null preflight ───────────────────────────────────────────
$TextExtensions = @(".cs", ".razor", ".css", ".md", ".json", ".yml", ".ps1")
function Test-TrailingNulls {
    param([string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -eq 0) { return @{ HasNulls = $false; Count = 0 } }
    $count = 0
    for ($i = $bytes.Length - 1; $i -ge 0 -and $bytes[$i] -eq 0; $i--) { $count++ }
    return @{ HasNulls = $count -gt 0; Count = $count }
}

# ── Fetch ─────────────────────────────────────────────────────────────
$pushUrl = "https://x-access-token:$Pat@github.com/$RepoOwner/$RepoName.git"
Write-Host "Fetching origin/main..." -ForegroundColor Cyan
$null = Invoke-GitWithRetry -GitArgs @("fetch", "--quiet", $pushUrl, "main") -Context "git fetch"
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: fetch failed." -ForegroundColor Red; exit 1 }
$ahead = (git rev-list HEAD "^FETCH_HEAD" --count).Trim()
$behind = (git rev-list FETCH_HEAD "^HEAD" --count).Trim()
Write-Host "  $ahead ahead, $behind behind origin/main"
if ($behind -ne "0") {
    Write-Host "ERROR: behind origin/main; pull --rebase first." -ForegroundColor Red
    exit 1
}

# ── Files to stage ────────────────────────────────────────────────────
$filesToStage = @(
    "src/Modules/MyFeature/Domain/MyEntity.cs",
    "src/Modules/MyFeature/Application/MyEntityService.cs",
    "src/Modules/MyFeature/Infrastructure/MyEntityRepository.cs",
    "src/Web/Endpoints/MyFeatureEndpoint.cs",
    "src/Web/Pages/MyFeaturePage.razor",
    "src/Web/Pages/MyFeaturePage.razor.css",
    "src/Web/appsettings.json"
)

# Trailing-null check
Write-Host "Trailing-null check..." -ForegroundColor Cyan
$nullFails = @()
foreach ($f in $filesToStage) {
    $full = Join-Path $PSScriptRoot $f
    if (-not (Test-Path $full)) { continue }
    $ext = [System.IO.Path]::GetExtension($f).ToLowerInvariant()
    if ($TextExtensions -notcontains $ext) { continue }
    $check = Test-TrailingNulls -Path $full
    if ($check.HasNulls) {
        Write-Host "  FAIL $f ($($check.Count) trailing 0x00 bytes)" -ForegroundColor Red
        $nullFails += $f
    } else {
        Write-Host "  OK  $f" -ForegroundColor Green
    }
}
if ($nullFails.Count -gt 0) { exit 1 }

# JSON parse for appsettings
Write-Host "JSON parse..." -ForegroundColor Cyan
$jsonFiles = @("src/Web/appsettings.json")
foreach ($f in $jsonFiles) {
    $full = Join-Path $PSScriptRoot $f
    if (-not (Test-Path $full)) { continue }
    try {
        $raw = [System.IO.File]::ReadAllText($full)
        $null = $raw | ConvertFrom-Json
        Write-Host "  OK  $f" -ForegroundColor Green
    } catch {
        Write-Host "  FAIL $f - $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

# Stage
Write-Host "Staging..." -ForegroundColor Cyan
foreach ($f in $filesToStage) {
    if (-not (Test-Path (Join-Path $PSScriptRoot $f))) {
        Write-Host "  SKIP $f" -ForegroundColor Yellow
        continue
    }
    $null = Invoke-GitWithRetry -GitArgs @("add", "--", $f) -Context "git add $f"
}

$staged = @(git diff --cached --name-only) | Where-Object { $_ -ne "" }
Write-Host "Files staged: $($staged.Count)" -ForegroundColor Cyan
$staged | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
if ($staged.Count -eq 0) { Write-Host "ERROR: nothing staged." -ForegroundColor Red; exit 1 }

# Commit
$msg = @"
feat(my-feature): add MyEntity with full CRUD + endpoint + UI page

Standard four-project pattern for new domain primitives:
- MyEntity domain class
- IMyEntityService application seam
- MyEntityRepository infrastructure impl
- Carter endpoint at /api/v1/my-feature
- Razor page at /my-feature with theme integration

appsettings.json gains MyFeature config section with default-on policy.

Migration not included in this commit (additive entity goes through the
auto-migrate-at-boot DbInitializer; existing rows untouched).
"@
$msgPath = Join-Path $env:TEMP "git_commit_msg_$([Guid]::NewGuid().ToString('N')).txt"
[System.IO.File]::WriteAllText($msgPath, $msg, (New-Object System.Text.UTF8Encoding($false)))
$null = Invoke-GitWithRetry -GitArgs @("-c", "user.name=$CommitName", "-c", "user.email=$CommitEmail", "commit", "-F", $msgPath) -Context "git commit"
$commitOk = ($LASTEXITCODE -eq 0)
Remove-Item $msgPath -ErrorAction SilentlyContinue
if (-not $commitOk) { Write-Host "ERROR: commit failed." -ForegroundColor Red; exit 1 }

$commitSha = (git rev-parse HEAD).Trim()
$shortSha = $commitSha.Substring(0, 7)

# Push
Write-Host "Pushing..." -ForegroundColor Cyan
$null = Invoke-GitWithRetry -GitArgs @("push", $pushUrl, "main") -Context "git push"
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: push failed." -ForegroundColor Red; exit 1 }
Write-Host "PUSHED: $shortSha" -ForegroundColor Green

# CI watch + probe
if ($WatchCi) {
    function Format-StepDuration {
        param($startedAt, $completedAt)
        if (-not $startedAt -or -not $completedAt) { return "" }
        $span = ([DateTime]$completedAt) - ([DateTime]$startedAt)
        if ($span.TotalSeconds -lt 60) { return "$([int]$span.TotalSeconds)s" }
        return "$([int]$span.TotalMinutes)m $([int]($span.TotalSeconds - ([int]$span.TotalMinutes * 60)))s"
    }

    Write-Host ""
    Write-Host "Watching CI..." -ForegroundColor Cyan
    $apiBase = "https://api.github.com/repos/$RepoOwner/$RepoName"
    $headers = @{
        "Accept"                = "application/vnd.github+json"
        "Authorization"         = "Bearer $Pat"
        "X-GitHub-Api-Version"  = "2022-11-28"
    }
    $deadline = (Get-Date).AddMinutes($CiWatchMaxMinutes)
    $runId = $null; $lastStatus = ""; $lastConclusion = ""
    $stepStates = @{}; $jobHeadersPrinted = @{}

    while ((Get-Date) -lt $deadline) {
        try {
            $runs = Invoke-RestMethod -Uri "$apiBase/actions/workflows/$CiWorkflowFile/runs?head_sha=$commitSha&per_page=1" -Headers $headers -TimeoutSec 15
            if ($runs.total_count -gt 0) {
                $run = $runs.workflow_runs[0]; $runId = $run.id
                $status = $run.status; $conclusion = $run.conclusion
                if ($status -ne $lastStatus -or $conclusion -ne $lastConclusion) {
                    $ts = (Get-Date).ToString("HH:mm:ss")
                    $line = "  [$ts] status=$status"
                    if ($conclusion) { $line += " conclusion=$conclusion" }
                    $color = if ($conclusion -eq "success") { "Green" } elseif ($conclusion) { "Red" } else { "Cyan" }
                    Write-Host $line -ForegroundColor $color
                    $lastStatus = $status; $lastConclusion = $conclusion
                }
                if ($runId) {
                    try {
                        $jobs = Invoke-RestMethod -Uri "$apiBase/actions/runs/$runId/jobs" -Headers $headers -TimeoutSec 15
                        foreach ($job in $jobs.jobs) {
                            if (-not $jobHeadersPrinted.ContainsKey($job.name) -and $job.status -ne "queued") {
                                Write-Host "  $($job.name) job:" -ForegroundColor Cyan
                                $jobHeadersPrinted[$job.name] = $true
                            }
                            foreach ($step in $job.steps) {
                                $key = "$($job.name)::$($step.number)"
                                $cur = @{ S = $step.status; C = $step.conclusion }
                                $prev = $stepStates[$key]
                                if (-not $prev -or $prev.S -ne $cur.S -or $prev.C -ne $cur.C) {
                                    $ts = (Get-Date).ToString("HH:mm:ss")
                                    if ($step.status -eq "completed") {
                                        $dur = Format-StepDuration $step.started_at $step.completed_at
                                        $icon = if ($step.conclusion -eq "success") { "OK " } elseif ($step.conclusion -eq "skipped") { "-- " } else { "XX " }
                                        $color = if ($step.conclusion -eq "success") { "Green" } elseif ($step.conclusion -eq "skipped") { "DarkGray" } else { "Red" }
                                        Write-Host "    [$ts] $icon $($step.name) ($dur)" -ForegroundColor $color
                                    } elseif ($step.status -eq "in_progress") {
                                        Write-Host "    [$ts] >> $($step.name) ..." -ForegroundColor Yellow
                                    }
                                    $stepStates[$key] = $cur
                                }
                            }
                        }
                    } catch {}
                }
                if ($status -eq "completed") {
                    Write-Host ""
                    if ($conclusion -eq "success") {
                        Write-Host "CI GREEN: $($run.html_url)" -ForegroundColor Green
                    } else {
                        Write-Host "CI $($conclusion.ToUpper()): $($run.html_url)" -ForegroundColor Red
                        exit 1
                    }
                    break
                }
            }
        } catch {
            Write-Host "  WARN: $($_.Exception.Message)" -ForegroundColor Yellow
        }
        Start-Sleep -Seconds $CiWatchPollSeconds
    }

    # Probes
    if ($ProbeEndpoints.Count -gt 0) {
        Write-Host ""
        Write-Host "Probing..." -ForegroundColor Cyan
        foreach ($p in $ProbeEndpoints) {
            try {
                $r = Invoke-WebRequest -Uri $p.Url -UseBasicParsing -TimeoutSec 30 -MaximumRedirection 0 -ErrorAction SilentlyContinue
                $code = $r.StatusCode
            } catch {
                $code = $_.Exception.Response.StatusCode.value__
                if (-not $code) { $code = 0 }
            }
            $ok = $p.ExpectedRange -contains $code
            $color = if ($ok) { "Green" } else { "Red" }
            Write-Host "  $($p.Url) -> $code" -ForegroundColor $color
            if (-not $ok) { exit 1 }
        }
    }
}

Write-Host ""
Write-Host "SUCCESS." -ForegroundColor Green
Remove-Item $MyInvocation.MyCommand.Path -Force
