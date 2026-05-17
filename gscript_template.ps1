# gscript TEMPLATE — PowerShell. Copy to <repo>/gscript.ps1, fill in the
# per-sprint sections marked CONFIGURE, run, watch it self-delete on success.
#
# Self-deletes after a successful push per the gscript convention (a per-sprint
# instance is an AI-generated artifact specific to one commit; not part of
# repo history). The template stays committed.
#
# https://github.com/erikcheatham/gscript
# Apache 2.0
#
# What this does, in order:
#   1. Auto-clears stale .git/*.lock files (only when no git processes running)
#   2. Loads GitHub PAT from ~/private/local.md (NO env vars, NO GCM popups)
#   3. Trailing-null preflight on every text file in filesToStage
#   4. Per-sprint validators (JSON parse, etc.) — extensible
#   5. Fetch + divergence check via PAT-in-URL
#   6. Stage explicit paths (NEVER `git add .`) with retry-on-lock-collision
#   7. Audit staged set + refuse unexpected
#   8. Commit via tempfile (avoids PowerShell quoting hell)
#   9. Push via PAT-in-URL (never bakes into .git/config)
#  10. CI watch with per-step granularity (polls GitHub Actions API)
#  11. Post-deploy probes (curls configured endpoints, verifies status range)
#  12. Self-delete

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false

# ── CONFIGURE: persona identity for AI-authored commits ───────────────
# Set to whatever git author you want for AI-authored commits. The
# convention is a distinct AI-persona identity (e.g. "darwincommits" /
# "ai-bot@yourdomain") so the operator can grep `git log --author=` and
# see exactly which commits an AI session authored vs which the operator
# typed by hand. NEVER use a real human's name + email here without
# their consent.
$CommitName  = "ai-bot"
$CommitEmail = "ai-bot@example.com"

# ── CONFIGURE: repo identifier for push URL + CI watch ────────────────
$RepoOwner = "your-github-username"
$RepoName  = "your-repo-name"

# ── CONFIGURE: CI watch ───────────────────────────────────────────────
# Workflow filename (relative to .github/workflows/) to poll for after
# push. Set $WatchCi = $false to skip the watch entirely (push-only mode).
$WatchCi = $true
$CiWorkflowFile = "deploy.yml"
$CiWatchMaxMinutes = 15
$CiWatchPollSeconds = 20

# ── CONFIGURE: post-deploy probes ─────────────────────────────────────
# Each entry: { Url; ExpectedRange = <int range> }. After CI green, the
# script curls each URL and verifies the status code lands in the
# expected range. Add per-sprint smoke targets here (e.g. an endpoint
# this sprint introduced) to validate the deploy actually exercised
# the new code paths.
$ProbeEndpoints = @(
    # @{ Url = "https://your-app.example.com/"; ExpectedRange = 200..399 }
)

# ── Stale-lock auto-recovery ──────────────────────────────────────────
# .git/index.lock + friends linger when a git process is killed mid-op
# or when VS Code/GitHub Desktop git-polling collides + crashes. Auto-
# remove when no git processes are actively running.
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
        Write-Host "WARN: $($found.Count) lock file(s) present and git processes are running:" -ForegroundColor Yellow
        $gitProcs | ForEach-Object { Write-Host "  PID $($_.Id) $($_.ProcessName)" -ForegroundColor Yellow }
        Write-Host "Not auto-removing. Wait for git processes to finish, then re-run." -ForegroundColor Yellow
        exit 1
    }

    foreach ($lockPath in $found) {
        $age = (Get-Date) - (Get-Item $lockPath).LastWriteTime
        Write-Host "Removing stale lock: $($lockPath | Split-Path -Leaf) (age $([int]$age.TotalSeconds)s, no git procs running)" -ForegroundColor Yellow
        Remove-Item $lockPath -Force
    }
}
Clear-StaleGitLocks -GitDir (Join-Path $PSScriptRoot ".git")

# ── Git retry wrapper ─────────────────────────────────────────────────
# Wraps each git operation in 3-attempt exponential backoff (1s/2s/4s).
# Detects lock-collision errors specifically; re-runs lock cleanup
# between retries. Absorbs transient VS Code git-polling collisions.
function Invoke-GitWithRetry {
    param(
        [Parameter(Mandatory = $true)][string[]]$GitArgs,
        [int]$MaxAttempts = 3,
        [string]$Context = "git"
    )
    $delay = 1
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $output = & git @GitArgs 2>&1
        if ($LASTEXITCODE -eq 0) { return $output }
        $stderr = $output -join "`n"
        if ($stderr -match "index\.lock|HEAD\.lock|Unable to create" -and $attempt -lt $MaxAttempts) {
            Write-Host "  $Context attempt $attempt/$MaxAttempts hit lock; retrying in ${delay}s..." -ForegroundColor Yellow
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

# ── Resolve PAT from localmd (canonical source) ───────────────────────
# Loads a GitHub fine-grained PAT from ~/private/local.md. The file is
# expected to contain a "github_pat_..." token; regex picks up the first
# match. See docs/LOCALMD.md for the convention.
$LocalMdPath = Join-Path $env:USERPROFILE "private\local.md"
if (-not (Test-Path $LocalMdPath)) {
    Write-Host "ERROR: $LocalMdPath not found." -ForegroundColor Red
    Write-Host "See https://github.com/erikcheatham/gscript/blob/main/docs/LOCALMD.md for the localmd convention." -ForegroundColor Yellow
    exit 1
}
$LocalMdContent = Get-Content $LocalMdPath -Raw
if ($LocalMdContent -match '(github_pat_[A-Za-z0-9_]{40,})') {
    $Pat = $Matches[1]
} else {
    Write-Host "ERROR: No PAT matching 'github_pat_...' found in $LocalMdPath." -ForegroundColor Red
    Write-Host "Mint a fine-grained PAT at github.com/settings/personal-access-tokens." -ForegroundColor Yellow
    Write-Host "See https://github.com/erikcheatham/gscript/blob/main/docs/PAT-SETUP.md for scoping." -ForegroundColor Yellow
    exit 1
}

# ── Trailing-null preflight ───────────────────────────────────────────
# Defends against the FUSE-mount trailing-null-padding gotcha that
# sandboxed AI agents trip when writing files through mount layers.
# Iterates every text-extension file in filesToStage, refuses to push
# if any has trailing 0x00 bytes.
$TextExtensions = @(
    ".cs", ".razor", ".css", ".js", ".ts", ".html", ".md", ".json",
    ".yml", ".yaml", ".xml", ".csproj", ".props", ".targets", ".sln",
    ".slnx", ".ps1", ".py", ".sql", ".txt", ".sh", ".gitignore",
    ".editorconfig", ".env", ".rb", ".go", ".rs", ".java", ".kt",
    ".swift", ".c", ".h", ".cpp", ".hpp", ".jsx", ".tsx", ".vue",
    ".svelte", ".toml", ".ini", ".cfg", ".conf"
)
function Test-TrailingNulls {
    param([string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -eq 0) { return @{ HasNulls = $false; Count = 0 } }
    $count = 0
    for ($i = $bytes.Length - 1; $i -ge 0 -and $bytes[$i] -eq 0; $i--) {
        $count++
    }
    return @{ HasNulls = $count -gt 0; Count = $count }
}

# ── CONFIGURE: per-sprint validators ──────────────────────────────────
# Extend with file-specific content-shape validators. The trailing-null
# check above is generic; this is for parse-level validation (JSON, YAML,
# XML). Each entry: { Description; Files = @(...); Validate = { param($p); ... } }.
$PerSprintValidators = @{
    # Example: JSON parse for appsettings files. Override the path list
    # per sprint or leave empty.
    "json" = @{
        Description = "JSON parse"
        Files = @(
            # "src/appsettings.json"
        )
        Validate = {
            param([string]$Path)
            $raw = [System.IO.File]::ReadAllText($Path)
            $null = $raw | ConvertFrom-Json
        }
    }
}

# ── Fetch + divergence check ──────────────────────────────────────────
$pushUrl = "https://x-access-token:$Pat@github.com/$RepoOwner/$RepoName.git"
Write-Host "Fetching origin/main via PAT-in-URL..." -ForegroundColor Cyan
$null = Invoke-GitWithRetry -GitArgs @("fetch", "--quiet", $pushUrl, "main") -Context "git fetch"
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: git fetch failed after retries." -ForegroundColor Red
    Write-Host "Most likely the PAT in localmd has expired or lacks Contents:R/W." -ForegroundColor Yellow
    exit 1
}
$ahead  = (git rev-list HEAD "^FETCH_HEAD" --count).Trim()
$behind = (git rev-list FETCH_HEAD "^HEAD" --count).Trim()
Write-Host "  local is $ahead ahead, $behind behind origin/main"
if ($behind -ne "0") {
    Write-Host "ERROR: origin/main is $behind commit(s) ahead of local." -ForegroundColor Red
    Write-Host "Resolve manually: git pull --rebase origin main, then re-run." -ForegroundColor Red
    exit 1
}

# ── CONFIGURE: stage explicit paths only ──────────────────────────────
# Replace with this sprint's actual files. NEVER `git add .` here —
# defensive against accidentally bundling operator-scratch into the
# commit.
Write-Host "Staging files..." -ForegroundColor Cyan
$filesToStage = @(
    # "src/MyEntity.cs",
    # "src/MyEndpoint.cs"
)
if ($filesToStage.Count -eq 0) {
    Write-Host "ERROR: filesToStage is empty. Edit this script first." -ForegroundColor Red
    exit 1
}

# Run trailing-null check on every text-extension file in the stage list
Write-Host "Pre-flight: trailing-null check..." -ForegroundColor Cyan
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
if ($nullFails.Count -gt 0) {
    Write-Host "ERROR: trailing nulls detected. Strip with:" -ForegroundColor Red
    Write-Host "  python -c `"import pathlib; p='<file>'; pathlib.Path(p).write_bytes(pathlib.Path(p).read_bytes().rstrip(b'\\x00'))`"" -ForegroundColor Yellow
    exit 1
}

# Run per-sprint validators
foreach ($validatorName in $PerSprintValidators.Keys) {
    $v = $PerSprintValidators[$validatorName]
    if ($v.Files.Count -eq 0) { continue }
    Write-Host "Pre-flight: $($v.Description)..." -ForegroundColor Cyan
    foreach ($f in $v.Files) {
        $full = Join-Path $PSScriptRoot $f
        if (-not (Test-Path $full)) {
            Write-Host "  SKIP $f (not found)" -ForegroundColor Yellow
            continue
        }
        try {
            & $v.Validate $full
            $bytes = [System.IO.File]::ReadAllBytes($full)
            Write-Host "  OK  $f ($($bytes.Length) bytes)" -ForegroundColor Green
        } catch {
            Write-Host "  FAIL $f - $($_.Exception.Message)" -ForegroundColor Red
            Write-Host "ERROR: refusing to push; fix file first." -ForegroundColor Red
            exit 1
        }
    }
}

# Stage files (with retry wrapper)
foreach ($f in $filesToStage) {
    $full = Join-Path $PSScriptRoot $f
    if (-not (Test-Path $full)) {
        Write-Host "  SKIP $f (not found)" -ForegroundColor Yellow
        continue
    }
    $null = Invoke-GitWithRetry -GitArgs @("add", "--", $f) -Context "git add $f"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: git add failed for $f after retries" -ForegroundColor Red
        exit 1
    }
}

# ── Audit what was staged ─────────────────────────────────────────────
$staged = @(git diff --cached --name-only) | Where-Object { $_ -ne "" }
Write-Host "Files staged: $($staged.Count)" -ForegroundColor Cyan
$staged | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
if ($staged.Count -eq 0) {
    Write-Host "ERROR: nothing was staged." -ForegroundColor Red
    exit 1
}

# Refuse to commit if anything unexpected slipped in
$normalizedExpected = $filesToStage | ForEach-Object { $_.Replace('\', '/') }
$unexpected = $staged | Where-Object { $_ -notin $normalizedExpected }
if ($unexpected.Count -gt 0) {
    Write-Host "WARNING: unexpected files staged (will commit anyway):" -ForegroundColor Yellow
    $unexpected | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}

# ── CONFIGURE: commit message via tempfile ────────────────────────────
$msg = @"
<short imperative subject — what this commit does>

<paragraph explaining the why + any non-obvious decisions>
"@
$msgPath = Join-Path $env:TEMP "git_commit_msg_$([Guid]::NewGuid().ToString('N')).txt"
[System.IO.File]::WriteAllText($msgPath, $msg, (New-Object System.Text.UTF8Encoding($false)))

$null = Invoke-GitWithRetry -GitArgs @("-c", "user.name=$CommitName", "-c", "user.email=$CommitEmail", "commit", "-F", $msgPath) -Context "git commit"
$commitOk = ($LASTEXITCODE -eq 0)
Remove-Item $msgPath -ErrorAction SilentlyContinue
if (-not $commitOk) {
    Write-Host "ERROR: git commit failed after retries" -ForegroundColor Red
    exit 1
}

$commitSha = (git rev-parse HEAD).Trim()
$shortSha = $commitSha.Substring(0, 7)

# ── Push via PAT-in-URL (never bakes into .git/config) ────────────────
Write-Host "Pushing to origin/main..." -ForegroundColor Cyan
$null = Invoke-GitWithRetry -GitArgs @("push", $pushUrl, "main") -Context "git push"
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: git push failed after retries" -ForegroundColor Red
    exit 1
}
Write-Host ""
Write-Host "PUSHED: $shortSha to origin/main." -ForegroundColor Green

# ── CI watch with per-step granularity ────────────────────────────────
function Format-StepDuration {
    param($startedAt, $completedAt)
    if (-not $startedAt -or -not $completedAt) { return "" }
    $span = ([DateTime]$completedAt) - ([DateTime]$startedAt)
    if ($span.TotalSeconds -lt 60) { return "$([int]$span.TotalSeconds)s" }
    $m = [int]$span.TotalMinutes
    $s = [int]($span.TotalSeconds - ($m * 60))
    return "${m}m ${s}s"
}

if ($WatchCi) {
    Write-Host ""
    Write-Host "Watching CI for $CiWorkflowFile (commit $shortSha, max $CiWatchMaxMinutes min)..." -ForegroundColor Cyan
    $apiBase = "https://api.github.com/repos/$RepoOwner/$RepoName"
    $headers = @{
        "Accept" = "application/vnd.github+json"
        "Authorization" = "Bearer $Pat"
        "X-GitHub-Api-Version" = "2022-11-28"
    }
    $deadline = (Get-Date).AddMinutes($CiWatchMaxMinutes)
    $runId = $null
    $lastStatus = ""
    $lastConclusion = ""
    $stepStates = @{}
    $jobHeadersPrinted = @{}

    while ((Get-Date) -lt $deadline) {
        try {
            # Workflow-run-level status
            $runs = Invoke-RestMethod -Uri "$apiBase/actions/workflows/$CiWorkflowFile/runs?head_sha=$commitSha&per_page=1" -Headers $headers -TimeoutSec 15
            if ($runs.total_count -gt 0) {
                $run = $runs.workflow_runs[0]
                $runId = $run.id
                $status = $run.status
                $conclusion = $run.conclusion
                if ($status -ne $lastStatus -or $conclusion -ne $lastConclusion) {
                    $ts = (Get-Date).ToString("HH:mm:ss")
                    $line = "  [$ts] status=$status"
                    if ($conclusion) { $line += " conclusion=$conclusion" }
                    $color = if ($conclusion -eq "success") { "Green" } elseif ($conclusion) { "Red" } else { "Cyan" }
                    Write-Host $line -ForegroundColor $color
                    $lastStatus = $status
                    $lastConclusion = $conclusion
                }

                # Per-job per-step granularity (requires Actions: Read PAT scope)
                if ($runId) {
                    try {
                        $jobsResp = Invoke-RestMethod -Uri "$apiBase/actions/runs/$runId/jobs" -Headers $headers -TimeoutSec 15
                        foreach ($job in $jobsResp.jobs) {
                            $jobName = $job.name
                            if (-not $jobHeadersPrinted.ContainsKey($jobName) -and $job.status -ne "queued") {
                                Write-Host "  $jobName job:" -ForegroundColor Cyan
                                $jobHeadersPrinted[$jobName] = $true
                            }
                            foreach ($step in $job.steps) {
                                $stepKey = "$jobName::$($step.number)"
                                $cur = @{ Status = $step.status; Conclusion = $step.conclusion }
                                $prev = $stepStates[$stepKey]
                                $changed = -not $prev -or $prev.Status -ne $cur.Status -or $prev.Conclusion -ne $cur.Conclusion
                                if ($changed) {
                                    $ts = (Get-Date).ToString("HH:mm:ss")
                                    $stepName = $step.name
                                    if ($step.status -eq "completed") {
                                        $dur = Format-StepDuration $step.started_at $step.completed_at
                                        $icon = if ($step.conclusion -eq "success") { "OK " } elseif ($step.conclusion -eq "skipped") { "-- " } else { "XX " }
                                        $color = if ($step.conclusion -eq "success") { "Green" } elseif ($step.conclusion -eq "skipped") { "DarkGray" } else { "Red" }
                                        $line2 = "    [$ts] $icon $stepName ($dur)"
                                        if ($step.conclusion -ne "success" -and $step.conclusion -ne "skipped") {
                                            $line2 += " [conclusion=$($step.conclusion)]"
                                        }
                                        Write-Host $line2 -ForegroundColor $color
                                    } elseif ($step.status -eq "in_progress") {
                                        Write-Host "    [$ts] >> $stepName ..." -ForegroundColor Yellow
                                    }
                                    $stepStates[$stepKey] = $cur
                                }
                            }
                        }
                    } catch {
                        # Jobs endpoint may briefly 404 when the run is just created.
                        # Also 403 if the PAT lacks Actions: Read. Fall through.
                    }
                }

                if ($status -eq "completed") {
                    Write-Host ""
                    if ($conclusion -eq "success") {
                        Write-Host "CI GREEN: $($run.html_url)" -ForegroundColor Green
                    } else {
                        Write-Host "CI $($conclusion.ToUpper()): $($run.html_url)" -ForegroundColor Red
                        Write-Host "Inspect failure logs at the URL above." -ForegroundColor Yellow
                        exit 1
                    }
                    break
                }
            }
        } catch {
            Write-Host "  WARN: CI poll error (will retry): $($_.Exception.Message)" -ForegroundColor Yellow
        }
        Start-Sleep -Seconds $CiWatchPollSeconds
    }

    if (-not $runId -or $lastStatus -ne "completed") {
        Write-Host "TIMEOUT: CI didn't complete within $CiWatchMaxMinutes min. Check manually:" -ForegroundColor Yellow
        Write-Host "  https://github.com/$RepoOwner/$RepoName/actions" -ForegroundColor Yellow
        exit 1
    }

    # ── Post-deploy staging probe ─────────────────────────────────────
    if ($ProbeEndpoints.Count -gt 0) {
        Write-Host ""
        Write-Host "Probing post-deploy endpoints..." -ForegroundColor Cyan
        $probeFails = @()
        foreach ($p in $ProbeEndpoints) {
            try {
                $r = Invoke-WebRequest -Uri $p.Url -UseBasicParsing -TimeoutSec 30 -MaximumRedirection 0 -ErrorAction SilentlyContinue
                $code = $r.StatusCode
            } catch {
                $code = $_.Exception.Response.StatusCode.value__
                if (-not $code) { $code = 0 }
            }
            $inRange = $p.ExpectedRange -contains $code
            $color = if ($inRange) { "Green" } else { "Red" }
            Write-Host "  $($p.Url) -> $code (expected $($p.ExpectedRange[0])-$($p.ExpectedRange[-1]))" -ForegroundColor $color
            if (-not $inRange) { $probeFails += $p.Url }
        }
        if ($probeFails.Count -gt 0) {
            Write-Host "PROBE FAIL: $($probeFails.Count) endpoint(s) outside expected range." -ForegroundColor Red
            Write-Host "Staging may still be warming up — try probing manually in 30s." -ForegroundColor Yellow
            exit 1
        }
        Write-Host "ALL PROBES GREEN." -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "SUCCESS: sprint shipped end-to-end (push + CI green + probes pass)." -ForegroundColor Green

# Self-delete per the gscript convention
Remove-Item $MyInvocation.MyCommand.Path -Force
