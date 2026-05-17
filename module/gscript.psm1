# gscript v1.1.0 — PowerShell module
# https://github.com/erikcheatham/gscript
# Apache 2.0
#
# Self-healing git push wrapper for AI-pair-programming workflows.
# See docs/PAT-SETUP.md, docs/LOCALMD.md, docs/GOTCHAS.md, docs/DESIGN.md.

#region helpers — private

# Format a step duration as human-readable. Steps without timestamps return "".
function script:Format-StepDuration {
    param($startedAt, $completedAt)
    if (-not $startedAt -or -not $completedAt) { return "" }
    $span = ([DateTime]$completedAt) - ([DateTime]$startedAt)
    if ($span.TotalSeconds -lt 60) { return "$([int]$span.TotalSeconds)s" }
    $m = [int]$span.TotalMinutes
    $s = [int]($span.TotalSeconds - ($m * 60))
    return "${m}m ${s}s"
}

# Resolve the caller's working directory. Walks up Get-PSCallStack to find
# the first non-module frame and uses its $PSScriptRoot. Falls back to
# (Get-Location).Path when called from interactive REPL.
function script:Resolve-CallerDirectory {
    $stack = Get-PSCallStack
    # Skip frames inside this module
    for ($i = 1; $i -lt $stack.Count; $i++) {
        $frame = $stack[$i]
        if ($frame.ScriptName -and $frame.ScriptName -notmatch '\\gscript\.psm1$') {
            return Split-Path -Parent $frame.ScriptName
        }
    }
    return (Get-Location).Path
}

# Known text-file extensions for the trailing-null check. Binary extensions
# (.docx, .ttf, .zip, .png, etc.) are intentionally excluded — they legitimately
# end in non-printable bytes and would false-positive.
$script:TextExtensions = @(
    ".cs", ".razor", ".css", ".js", ".ts", ".html", ".md", ".json",
    ".yml", ".yaml", ".xml", ".csproj", ".props", ".targets", ".sln",
    ".slnx", ".ps1", ".psm1", ".psd1", ".py", ".sql", ".txt", ".sh",
    ".gitignore", ".editorconfig", ".env", ".rb", ".go", ".rs", ".java",
    ".kt", ".swift", ".c", ".h", ".cpp", ".hpp", ".jsx", ".tsx", ".vue",
    ".svelte", ".toml", ".ini", ".cfg", ".conf"
)

# Known git lock file names. Auto-cleaned by Clear-StaleGitLocks when no
# git processes are currently running.
$script:GitLockNames = @(
    "index.lock", "HEAD.lock", "config.lock",
    "packed-refs.lock", "shallow.lock", "fetch.lock"
)

#endregion

#region public — Clear-StaleGitLocks

<#
.SYNOPSIS
Auto-recover stale git lock files from interrupted prior runs.

.DESCRIPTION
Detects six known git lock-file types (index.lock, HEAD.lock, config.lock,
packed-refs.lock, shallow.lock, fetch.lock) in the target .git directory.
Removes them ONLY when no git processes are currently running (defense
against clobbering a legitimate concurrent operation).

If git processes ARE running, exits with a clear "wait for them to finish"
message rather than risking the clobber.

.PARAMETER GitDir
Path to the .git directory. Defaults to the caller's $PSScriptRoot/.git
(or current location's .git if called outside a script).

.EXAMPLE
Clear-StaleGitLocks
# Cleans locks in the current working directory's .git folder.

.EXAMPLE
Clear-StaleGitLocks -GitDir 'C:\repos\my-repo\.git'
#>
function Clear-StaleGitLocks {
    [CmdletBinding()]
    param(
        [string]$GitDir
    )

    if (-not $GitDir) {
        $GitDir = Join-Path (script:Resolve-CallerDirectory) '.git'
    }

    if (-not (Test-Path $GitDir)) {
        Write-Verbose "Clear-StaleGitLocks: $GitDir not found, skipping."
        return
    }

    $found = @()
    foreach ($lock in $script:GitLockNames) {
        $lockPath = Join-Path $GitDir $lock
        if (Test-Path $lockPath) { $found += $lockPath }
    }
    if ($found.Count -eq 0) { return }

    $gitProcs = @(Get-Process -Name git, git-* -ErrorAction SilentlyContinue)
    if ($gitProcs.Count -gt 0) {
        Write-Host "WARN: $($found.Count) lock file(s) present and git processes are running:" -ForegroundColor Yellow
        $gitProcs | ForEach-Object { Write-Host "  PID $($_.Id) $($_.ProcessName)" -ForegroundColor Yellow }
        Write-Host "Not auto-removing. Wait for git processes to finish, then re-run." -ForegroundColor Yellow
        throw 'StaleLockClearAborted: concurrent git processes running'
    }

    foreach ($lockPath in $found) {
        $age = (Get-Date) - (Get-Item $lockPath).LastWriteTime
        Write-Host "Removing stale lock: $($lockPath | Split-Path -Leaf) (age $([int]$age.TotalSeconds)s, no git procs running)" -ForegroundColor Yellow
        Remove-Item $lockPath -Force
    }
}

#endregion

#region public — Invoke-GitWithRetry

<#
.SYNOPSIS
Run a git command with exponential-backoff retry on lock collisions.

.DESCRIPTION
Wraps a git invocation in 3-attempt exponential backoff (1s/2s/4s).
Detects lock-collision error messages specifically (regex match on
'index.lock|HEAD.lock|Unable to create'). Between retries, re-runs
Clear-StaleGitLocks to handle any new stale files. Returns the git
command's stdout/stderr; $LASTEXITCODE reflects the final attempt.

.PARAMETER GitArgs
The git arguments to invoke. E.g. @("add", "--", "file.txt").

.PARAMETER MaxAttempts
Number of attempts before giving up. Default 3.

.PARAMETER Context
Human-readable label for log output. Default "git".

.PARAMETER GitDir
Path to .git for lock recovery between retries. Defaults to caller's location.

.EXAMPLE
Invoke-GitWithRetry -GitArgs @("add", "--", "README.md") -Context "git add README.md"

.EXAMPLE
Invoke-GitWithRetry @("commit", "-m", "feat: thing")
#>
function Invoke-GitWithRetry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)][string[]]$GitArgs,
        [int]$MaxAttempts = 3,
        [string]$Context = "git",
        [string]$GitDir
    )

    if (-not $GitDir) {
        $GitDir = Join-Path (script:Resolve-CallerDirectory) '.git'
    }

    $delay = 1
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $output = & git @GitArgs 2>&1
        if ($LASTEXITCODE -eq 0) { return $output }
        $stderr = $output -join "`n"
        if ($stderr -match "index\.lock|HEAD\.lock|Unable to create" -and $attempt -lt $MaxAttempts) {
            Write-Host "  $Context attempt $attempt/$MaxAttempts hit lock; retrying in ${delay}s..." -ForegroundColor Yellow
            Start-Sleep -Seconds $delay
            try { Clear-StaleGitLocks -GitDir $GitDir } catch {}
            $delay *= 2
            continue
        }
        Write-Host $stderr -ForegroundColor Red
        return $null
    }
    return $null
}

#endregion

#region public — Test-TrailingNulls

<#
.SYNOPSIS
Test a file for trailing 0x00 bytes (FUSE-mount trailing-null gotcha defense).

.DESCRIPTION
Returns a hashtable @{ HasNulls = $bool; Count = $int }. Designed for the
FUSE-mount-rsync trailing-null padding gotcha where sandbox AI agents
occasionally append 1-1143 null bytes to files when writing through mount
layers. JSON/YAML parsers reject the file at the first null byte; this
check catches it before commit.

.PARAMETER Path
Absolute path to the file to check.

.EXAMPLE
$result = Test-TrailingNulls -Path 'C:\repo\appsettings.json'
if ($result.HasNulls) { Write-Host "$($result.Count) trailing nulls" }
#>
function Test-TrailingNulls {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path
    )
    if (-not (Test-Path $Path)) {
        return @{ HasNulls = $false; Count = 0 }
    }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -eq 0) { return @{ HasNulls = $false; Count = 0 } }
    $count = 0
    for ($i = $bytes.Length - 1; $i -ge 0 -and $bytes[$i] -eq 0; $i--) {
        $count++
    }
    return @{ HasNulls = $count -gt 0; Count = $count }
}

#endregion

#region public — Get-LocalmdPat

<#
.SYNOPSIS
Resolve a GitHub fine-grained PAT from ~/private/local.md.

.DESCRIPTION
Reads the localmd file, picks up the first match of the regex
'github_pat_[A-Za-z0-9_]{40,}'. Returns the PAT string, or throws if
the file is missing or no PAT is found.

See docs/LOCALMD.md for the localmd convention.

.PARAMETER Path
Path to localmd. Defaults to ~/private/local.md on POSIX, %USERPROFILE%\private\local.md on Windows.

.EXAMPLE
$pat = Get-LocalmdPat
# Reads from the default location

.EXAMPLE
$pat = Get-LocalmdPat -Path 'D:\my-localmd\local.md'
#>
function Get-LocalmdPat {
    [CmdletBinding()]
    param(
        [string]$Path
    )
    if (-not $Path) {
        if ($env:USERPROFILE) {
            $Path = Join-Path $env:USERPROFILE 'private\local.md'
        } else {
            $Path = Join-Path $HOME 'private/local.md'
        }
    }
    if (-not (Test-Path $Path)) {
        throw "Get-LocalmdPat: $Path not found. See https://github.com/erikcheatham/gscript/blob/main/docs/LOCALMD.md"
    }
    $content = Get-Content $Path -Raw
    if ($content -match '(github_pat_[A-Za-z0-9_]{40,})') {
        return $Matches[1]
    }
    throw "Get-LocalmdPat: no 'github_pat_...' match found in $Path. See https://github.com/erikcheatham/gscript/blob/main/docs/PAT-SETUP.md"
}

#endregion

#region public — Watch-GithubCi

<#
.SYNOPSIS
Poll GitHub Actions for a workflow run keyed on a commit SHA, with per-step output.

.DESCRIPTION
Queries /repos/{owner}/{repo}/actions/workflows/{file}/runs?head_sha={sha}
on a loop. Prints workflow-run-level status transitions AND per-job per-step
transitions as they happen. Diff-only output (no full-state redraws).

Color-coded with ASCII markers (>> / OK / XX / --) to avoid Unicode-arrow
rendering issues in some terminals + the Razor parser gotcha.

Requires PAT with Actions: Read scope (separate from Workflows: R/W).

.PARAMETER RepoOwner
GitHub user/org name.

.PARAMETER RepoName
GitHub repo name.

.PARAMETER WorkflowFile
Workflow filename in .github/workflows/ (e.g. 'deploy.yml').

.PARAMETER CommitSha
Full 40-char commit SHA to match against head_sha. Use the SHA you just pushed.

.PARAMETER Pat
GitHub PAT (Bearer token). Use Get-LocalmdPat to resolve from localmd.

.PARAMETER MaxMinutes
Watch budget. Default 15.

.PARAMETER PollSeconds
Poll interval. Default 20.

.EXAMPLE
$pat = Get-LocalmdPat
Watch-GithubCi -RepoOwner 'owner' -RepoName 'repo' -WorkflowFile 'deploy.yml' `
               -CommitSha (git rev-parse HEAD) -Pat $pat
#>
function Watch-GithubCi {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoOwner,
        [Parameter(Mandatory)][string]$RepoName,
        [Parameter(Mandatory)][string]$WorkflowFile,
        [Parameter(Mandatory)][string]$CommitSha,
        [Parameter(Mandatory)][string]$Pat,
        [int]$MaxMinutes = 15,
        [int]$PollSeconds = 20
    )

    $shortSha = $CommitSha.Substring(0, 7)
    Write-Host "Watching CI for $WorkflowFile (commit $shortSha, max $MaxMinutes min)..." -ForegroundColor Cyan

    $apiBase = "https://api.github.com/repos/$RepoOwner/$RepoName"
    $headers = @{
        "Accept"                = "application/vnd.github+json"
        "Authorization"         = "Bearer $Pat"
        "X-GitHub-Api-Version"  = "2022-11-28"
    }

    $deadline = (Get-Date).AddMinutes($MaxMinutes)
    $runId = $null
    $lastStatus = ""
    $lastConclusion = ""
    $stepStates = @{}
    $jobHeadersPrinted = @{}

    while ((Get-Date) -lt $deadline) {
        try {
            $runs = Invoke-RestMethod -Uri "$apiBase/actions/workflows/$WorkflowFile/runs?head_sha=$CommitSha&per_page=1" -Headers $headers -TimeoutSec 15
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
                                    if ($step.status -eq "completed") {
                                        $dur = script:Format-StepDuration $step.started_at $step.completed_at
                                        $icon = if ($step.conclusion -eq "success") { "OK " } elseif ($step.conclusion -eq "skipped") { "-- " } else { "XX " }
                                        $color = if ($step.conclusion -eq "success") { "Green" } elseif ($step.conclusion -eq "skipped") { "DarkGray" } else { "Red" }
                                        $line2 = "    [$ts] $icon $($step.name) ($dur)"
                                        if ($step.conclusion -ne "success" -and $step.conclusion -ne "skipped") {
                                            $line2 += " [conclusion=$($step.conclusion)]"
                                        }
                                        Write-Host $line2 -ForegroundColor $color
                                    } elseif ($step.status -eq "in_progress") {
                                        Write-Host "    [$ts] >> $($step.name) ..." -ForegroundColor Yellow
                                    }
                                    $stepStates[$stepKey] = $cur
                                }
                            }
                        }
                    } catch {
                        # Jobs endpoint may briefly 404 (new run) or 403 (PAT missing Actions:Read).
                        # Fall through; the workflow-run-level loop is the safety net.
                    }
                }

                if ($status -eq "completed") {
                    Write-Host ""
                    if ($conclusion -eq "success") {
                        Write-Host "CI GREEN: $($run.html_url)" -ForegroundColor Green
                        return [PSCustomObject]@{
                            Success    = $true
                            RunId      = $runId
                            Url        = $run.html_url
                            Conclusion = $conclusion
                        }
                    } else {
                        Write-Host "CI $($conclusion.ToUpper()): $($run.html_url)" -ForegroundColor Red
                        Write-Host "Inspect failure logs at the URL above." -ForegroundColor Yellow
                        return [PSCustomObject]@{
                            Success    = $false
                            RunId      = $runId
                            Url        = $run.html_url
                            Conclusion = $conclusion
                        }
                    }
                }
            }
        } catch {
            Write-Host "  WARN: CI poll error (will retry): $($_.Exception.Message)" -ForegroundColor Yellow
        }
        Start-Sleep -Seconds $PollSeconds
    }

    Write-Host "TIMEOUT: CI didn't complete within $MaxMinutes min." -ForegroundColor Yellow
    Write-Host "  https://github.com/$RepoOwner/$RepoName/actions" -ForegroundColor Yellow
    return [PSCustomObject]@{
        Success    = $false
        RunId      = $runId
        Url        = "https://github.com/$RepoOwner/$RepoName/actions"
        Conclusion = "timeout"
    }
}

#endregion

#region public — Test-PostDeployProbe

<#
.SYNOPSIS
Probe a list of HTTP endpoints, verify each returns a status in the expected range.

.DESCRIPTION
Curls each endpoint with -MaximumRedirection 0 (so 302s count as success
when in range). Returns a hashtable @{ Success = $bool; Failures = @() }.
Failures contains URLs that returned outside the expected range.

.PARAMETER Endpoints
Array of hashtables @{ Url = '...'; ExpectedRange = 200..399 }.

.PARAMETER TimeoutSec
Per-probe timeout. Default 30.

.EXAMPLE
$result = Test-PostDeployProbe -Endpoints @(
    @{ Url = 'https://staging.example.com/'; ExpectedRange = 200..399 }
    @{ Url = 'https://staging.example.com/api/v1/health'; ExpectedRange = 200..299 }
)
if (-not $result.Success) { Write-Host "Failed: $($result.Failures -join ', ')" }
#>
function Test-PostDeployProbe {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable[]]$Endpoints,
        [int]$TimeoutSec = 30
    )

    Write-Host "Probing post-deploy endpoints..." -ForegroundColor Cyan
    $failures = @()
    foreach ($p in $Endpoints) {
        try {
            $r = Invoke-WebRequest -Uri $p.Url -UseBasicParsing -TimeoutSec $TimeoutSec -MaximumRedirection 0 -ErrorAction SilentlyContinue
            $code = $r.StatusCode
        } catch {
            $code = $_.Exception.Response.StatusCode.value__
            if (-not $code) { $code = 0 }
        }
        $inRange = $p.ExpectedRange -contains $code
        $color = if ($inRange) { "Green" } else { "Red" }
        Write-Host "  $($p.Url) -> $code (expected $($p.ExpectedRange[0])-$($p.ExpectedRange[-1]))" -ForegroundColor $color
        if (-not $inRange) { $failures += $p.Url }
    }

    if ($failures.Count -gt 0) {
        Write-Host "PROBE FAIL: $($failures.Count) endpoint(s) outside expected range." -ForegroundColor Red
    } else {
        Write-Host "ALL PROBES GREEN." -ForegroundColor Green
    }

    return [PSCustomObject]@{
        Success  = ($failures.Count -eq 0)
        Failures = $failures
    }
}

#endregion

#region public — Invoke-Gscript

<#
.SYNOPSIS
Run the full gscript ceremony: preflight + stage + commit + push + CI watch + probe.

.DESCRIPTION
The main entry point. Three calling conventions all work:

  # 1. Inline hashtable (most natural for per-sprint scripts):
  Invoke-Gscript @{
      RepoOwner     = 'owner'
      RepoName      = 'repo'
      FilesToStage  = @('src/file.cs', 'docs/notes.md')
      CommitMessage = 'feat: thing'
  }

  # 2. PowerShell splatting (variable-based):
  $params = @{ RepoOwner = 'owner'; ... }
  Invoke-Gscript @params

  # 3. Named parameters:
  Invoke-Gscript -RepoOwner 'owner' -RepoName 'repo' `
                 -FilesToStage @('src/file.cs') -CommitMessage 'feat'

Per-sprint scripts shrink from ~400 lines (template mode) to ~15:

    Import-Module C:\path\to\gscript\module\gscript.psd1
    Invoke-Gscript @{
        RepoOwner     = 'owner'
        RepoName      = 'repo'
        FilesToStage  = @('src/file.cs', 'docs/notes.md')
        CommitMessage = 'feat: thing'
    }
    Remove-Item $MyInvocation.MyCommand.Path -Force

The function auto-detects the working directory from the caller's $PSScriptRoot
(so per-sprint scripts in the repo root work without specifying paths).

.PARAMETER Config
Hashtable with the parameter values. Convenience for inline calls like
`Invoke-Gscript @{...}`. Keys expand into the named parameters below.

.PARAMETER RepoOwner
GitHub user/org name. Required (either via -Config hashtable or -RepoOwner).

.PARAMETER RepoName
GitHub repo name. Required.

.PARAMETER FilesToStage
Array of file paths (relative to repo root) to stage. Required, non-empty.

.PARAMETER CommitMessage
Multi-line commit message. Goes through git commit -F <tempfile> internally. Required.

.PARAMETER CommitName
Git author name for the commit. Default 'ai-bot'.

.PARAMETER CommitEmail
Git author email. Default 'ai-bot@example.com'.

.PARAMETER CiWorkflowFile
Workflow filename in .github/workflows/. Default 'deploy.yml'.

.PARAMETER WatchCi
Whether to poll CI after pushing. Default $true.

.PARAMETER CiWatchMaxMinutes
CI watch budget in minutes. Default 15.

.PARAMETER CiWatchPollSeconds
CI poll interval. Default 20.

.PARAMETER ProbeEndpoints
Array of hashtables @{ Url = '...'; ExpectedRange = 200..399 } for the post-
deploy probe. Empty array (default) skips probing.

.PARAMETER LocalmdPath
Path to localmd. Default ~/private/local.md (per-OS).

.PARAMETER PerSprintValidators
Hashtable of name -> @{ Description = '...'; Files = @(); Validate = { param($path); ... } }.
Each Validate is a ScriptBlock that throws on invalid content. Default empty.

.PARAMETER WorkingDirectory
Repo root. Default: auto-detected from caller's $PSScriptRoot.

.EXAMPLE
Invoke-Gscript @{
    RepoOwner = 'erikcheatham'
    RepoName = 'gscript'
    FilesToStage = @('README.md')
    CommitMessage = 'docs: fix typo'
    WatchCi = $false
}

.EXAMPLE
Invoke-Gscript -RepoOwner 'me' -RepoName 'my-app' `
    -FilesToStage @('src/main.cs') `
    -CommitMessage 'feat: new feature' `
    -ProbeEndpoints @(
        @{ Url = 'https://staging.my-app.example.com/'; ExpectedRange = 200..399 }
    )
#>
function Invoke-Gscript {
    [CmdletBinding()]
    param(
        # Inline hashtable mode — `Invoke-Gscript @{ RepoOwner='x'; ... }` binds
        # here positionally. Keys in the hashtable expand into the named
        # parameters below at the top of the function body.
        [Parameter(Position = 0)]
        [hashtable]$Config,

        [string]$RepoOwner,
        [string]$RepoName,
        [string[]]$FilesToStage,
        [string]$CommitMessage,

        [string]$CommitName = 'ai-bot',
        [string]$CommitEmail = 'ai-bot@example.com',

        [string]$CiWorkflowFile = 'deploy.yml',
        [bool]$WatchCi = $true,
        [int]$CiWatchMaxMinutes = 15,
        [int]$CiWatchPollSeconds = 20,

        [hashtable[]]$ProbeEndpoints = @(),

        [string]$LocalmdPath,
        [hashtable]$PerSprintValidators = @{},
        [string]$WorkingDirectory
    )

    $ErrorActionPreference = "Stop"
    $script:PSNativeCommandUseErrorActionPreference = $false

    # Expand $Config hashtable into local variables. Lets callers use the
    # natural-looking inline form `Invoke-Gscript @{ RepoOwner='x'; ... }`
    # without learning PowerShell splatting syntax. Splatting still works
    # via `$p = @{...}; Invoke-Gscript @p`. Named params also work.
    #
    # Named params win over Config keys (checked via $PSBoundParameters)
    # so callers can override a single field of an otherwise-shared config:
    #     Invoke-Gscript -Config $base -CommitMessage 'override'
    if ($Config) {
        foreach ($key in $Config.Keys) {
            if (-not $PSBoundParameters.ContainsKey($key)) {
                Set-Variable -Name $key -Value $Config[$key] -Scope 0 -ErrorAction SilentlyContinue
            }
        }
    }

    # Manual validation of required fields. Moved out of [Parameter(Mandatory)]
    # to support all three calling conventions (-Config / splatting / named).
    if (-not $RepoOwner)     { throw "Invoke-Gscript: RepoOwner is required." }
    if (-not $RepoName)      { throw "Invoke-Gscript: RepoName is required." }
    if (-not $FilesToStage -or $FilesToStage.Count -eq 0) {
        throw "Invoke-Gscript: FilesToStage must contain at least one path."
    }
    if (-not $CommitMessage) { throw "Invoke-Gscript: CommitMessage is required." }

    # Resolve working directory from caller if not explicit
    if (-not $WorkingDirectory) {
        $WorkingDirectory = script:Resolve-CallerDirectory
    }

    $gitDir = Join-Path $WorkingDirectory '.git'
    if (-not (Test-Path $gitDir)) {
        throw "Invoke-Gscript: $gitDir not found. WorkingDirectory must contain a git repo."
    }

    Push-Location $WorkingDirectory
    try {
        # ── Stale-lock auto-recovery ─────────────────────────────────
        Clear-StaleGitLocks -GitDir $gitDir

        # ── PAT from localmd ─────────────────────────────────────────
        $pat = Get-LocalmdPat -Path $LocalmdPath

        # ── Trailing-null preflight ──────────────────────────────────
        Write-Host "Pre-flight: trailing-null check..." -ForegroundColor Cyan
        $nullFails = @()
        foreach ($f in $FilesToStage) {
            $full = Join-Path $WorkingDirectory $f
            if (-not (Test-Path $full)) { continue }
            $ext = [System.IO.Path]::GetExtension($f).ToLowerInvariant()
            # Treat .gitignore specially (no extension, but text)
            $isText = ($script:TextExtensions -contains $ext) -or ((Split-Path -Leaf $f) -eq '.gitignore')
            if (-not $isText) { continue }
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
            throw "Trailing nulls in $($nullFails.Count) file(s)."
        }

        # ── Per-sprint validators ────────────────────────────────────
        foreach ($validatorName in $PerSprintValidators.Keys) {
            $v = $PerSprintValidators[$validatorName]
            if (-not $v.Files -or $v.Files.Count -eq 0) { continue }
            Write-Host "Pre-flight: $($v.Description)..." -ForegroundColor Cyan
            foreach ($f in $v.Files) {
                $full = Join-Path $WorkingDirectory $f
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
                    throw "Validator '$validatorName' failed on $f"
                }
            }
        }

        # ── Fetch + divergence check ─────────────────────────────────
        $pushUrl = "https://x-access-token:$pat@github.com/$RepoOwner/$RepoName.git"
        Write-Host "Fetching origin/main via PAT-in-URL..." -ForegroundColor Cyan
        $null = Invoke-GitWithRetry -GitArgs @("fetch", "--quiet", $pushUrl, "main") -Context "git fetch" -GitDir $gitDir
        $fetchOk = ($LASTEXITCODE -eq 0)
        # Empty repos (first push) legitimately fail fetch — fall through.
        $ahead  = (git rev-list HEAD "^FETCH_HEAD" --count 2>$null)
        $behind = (git rev-list FETCH_HEAD "^HEAD" --count 2>$null)
        if ($null -ne $ahead) {
            Write-Host "  local is $ahead ahead, $behind behind origin/main"
        } elseif (-not $fetchOk) {
            Write-Host "  empty origin (first push)"
        }
        if ($null -ne $behind -and $behind -ne "0") {
            throw "origin/main is $behind commit(s) ahead. Resolve manually: git pull --rebase origin main"
        }

        # ── Stage explicit paths ─────────────────────────────────────
        Write-Host "Staging files..." -ForegroundColor Cyan
        foreach ($f in $FilesToStage) {
            $full = Join-Path $WorkingDirectory $f
            if (-not (Test-Path $full)) {
                Write-Host "  SKIP $f (not found)" -ForegroundColor Yellow
                continue
            }
            $null = Invoke-GitWithRetry -GitArgs @("add", "--", $f) -Context "git add $f" -GitDir $gitDir
            if ($LASTEXITCODE -ne 0) {
                throw "git add failed for $f after retries"
            }
        }

        # ── Audit staged ─────────────────────────────────────────────
        $staged = @(git diff --cached --name-only) | Where-Object { $_ -ne "" }
        Write-Host "Files staged: $($staged.Count)" -ForegroundColor Cyan
        $staged | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
        if ($staged.Count -eq 0) {
            throw "nothing was staged"
        }
        $normalizedExpected = $FilesToStage | ForEach-Object { $_.Replace('\', '/') }
        $unexpected = $staged | Where-Object { $_ -notin $normalizedExpected }
        if ($unexpected.Count -gt 0) {
            Write-Host "WARNING: unexpected files staged (will commit anyway):" -ForegroundColor Yellow
            $unexpected | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
        }

        # ── Commit via tempfile ──────────────────────────────────────
        $msgPath = Join-Path $env:TEMP "git_commit_msg_$([Guid]::NewGuid().ToString('N')).txt"
        [System.IO.File]::WriteAllText($msgPath, $CommitMessage, (New-Object System.Text.UTF8Encoding($false)))
        $null = Invoke-GitWithRetry -GitArgs @("-c", "user.name=$CommitName", "-c", "user.email=$CommitEmail", "commit", "-F", $msgPath) -Context "git commit" -GitDir $gitDir
        $commitOk = ($LASTEXITCODE -eq 0)
        Remove-Item $msgPath -ErrorAction SilentlyContinue
        if (-not $commitOk) {
            throw "git commit failed after retries"
        }
        $commitSha = (git rev-parse HEAD).Trim()
        $shortSha = $commitSha.Substring(0, 7)

        # ── Push ─────────────────────────────────────────────────────
        Write-Host "Pushing to origin/main..." -ForegroundColor Cyan
        $null = Invoke-GitWithRetry -GitArgs @("push", $pushUrl, "main") -Context "git push" -GitDir $gitDir
        if ($LASTEXITCODE -ne 0) {
            throw "git push failed after retries"
        }
        Write-Host ""
        Write-Host "PUSHED: $shortSha to origin/main." -ForegroundColor Green

        # ── CI watch ─────────────────────────────────────────────────
        if ($WatchCi) {
            Write-Host ""
            $ciResult = Watch-GithubCi -RepoOwner $RepoOwner -RepoName $RepoName `
                -WorkflowFile $CiWorkflowFile -CommitSha $commitSha -Pat $pat `
                -MaxMinutes $CiWatchMaxMinutes -PollSeconds $CiWatchPollSeconds
            if (-not $ciResult.Success) {
                throw "CI did not complete successfully (conclusion=$($ciResult.Conclusion))"
            }

            # ── Post-deploy probe ────────────────────────────────────
            if ($ProbeEndpoints.Count -gt 0) {
                Write-Host ""
                $probeResult = Test-PostDeployProbe -Endpoints $ProbeEndpoints
                if (-not $probeResult.Success) {
                    throw "Probe failed for: $($probeResult.Failures -join ', ')"
                }
            }
        }

        Write-Host ""
        Write-Host "SUCCESS: sprint shipped end-to-end." -ForegroundColor Green

        return [PSCustomObject]@{
            Success   = $true
            CommitSha = $commitSha
            ShortSha  = $shortSha
        }
    } finally {
        Pop-Location
    }
}

#endregion

# Explicit exports — also declared in gscript.psd1 manifest, but listed
# here for in-process Import-Module C:\path\to\gscript.psm1 usage where
# the psd1 isn't read.
Export-ModuleMember -Function @(
    'Invoke-Gscript',
    'Clear-StaleGitLocks',
    'Invoke-GitWithRetry',
    'Test-TrailingNulls',
    'Get-LocalmdPat',
    'Watch-GithubCi',
    'Test-PostDeployProbe'
)
