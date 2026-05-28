@{
    RootModule        = 'gscript.psm1'
    ModuleVersion     = '1.4.0'
    GUID              = 'c240d8ba-d2a3-426b-9558-da78611f2f54'
    Author            = 'Erik Cheatham'
    Copyright         = '(c) 2026 Erik Cheatham. Apache 2.0.'
    Description       = 'Self-healing git push wrapper with localmd PAT, FUSE-mount trailing-null preflight, stale-lock auto-recovery, retry-on-collision, post-push CI watch with per-step granularity, and post-deploy probes. Authored for AI-pair-programming workflows where commits land via a sandboxed file layer.'

    PowerShellVersion = '5.1'

    FunctionsToExport = @(
        'Invoke-Gscript',
        'Clear-StaleGitLocks',
        'Invoke-GitWithRetry',
        'Test-TrailingNulls',
        'Get-LocalmdPat',
        'Watch-GithubCi',
        'Test-PostDeployProbe'
    )
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()

    PrivateData = @{
        PSData = @{
            Tags         = @('git', 'github', 'ci-cd', 'ai-tools', 'devops', 'powershell', 'gscript')
            LicenseUri   = 'https://github.com/erikcheatham/gscript/blob/main/LICENSE'
            ProjectUri   = 'https://github.com/erikcheatham/gscript'
            ReleaseNotes = @'
v1.4.0 — `-NoDeploy` switch for documentation pushes.

NEW: `Invoke-Gscript -NoDeploy` (or `@{ NoDeploy = $true }` in hashtable
form). Three coordinated mutations:
  1. Auto-appends `[skip ci]` to the commit subject line (idempotent —
     skipped if subject already carries any canonical skip directive:
     `[skip ci]`, `[ci skip]`, `[no ci]`, `[skip actions]`,
     `[actions skip]`). GitHub Actions skips ALL workflow runs for the
     push regardless of paths-ignore filters.
  2. Forces `-WatchCi = $false` (no point polling for a run that will
     never appear).
  3. Forces `-ProbeEndpoints = @()` (no point probing when no deploy
     happened).
Overrides any explicit values the caller passed for WatchCi or
ProbeEndpoints, so callers don't have to coordinate three params.
Banner prints `[NODEPLOY mode]` lines for operator clarity.

Use cases: IM banking / CLAUDE.md edits / architecture write-ups /
calendar restructuring / any commit that produces no user-facing
behavior change. Saves the full CI cycle wall-clock vs `WatchCi=$false`
alone (which still pays the 60s skip-detect poll).

    # Minimal NoDeploy push:
    Invoke-Gscript @{
        RepoOwner = 'owner'
        RepoName = 'repo'
        FilesToStage = @('CHANGELOG.md')
        CommitMessage = 'docs: bank architectural commitment'
        NoDeploy = $true
    }

Backward-compatible: default `$NoDeploy = $false` preserves existing
behavior. v1.1.0 callers don't need any changes.

------------------------------------------------------------------------
v1.1.0 — module shape.

Functions exported:
- Invoke-Gscript            — main entry, runs the full ceremony from hashtable
- Clear-StaleGitLocks       — standalone stale-lock recovery
- Invoke-GitWithRetry       — standalone retry-on-collision wrapper
- Test-TrailingNulls        — standalone file preflight
- Get-LocalmdPat            — standalone PAT resolver
- Watch-GithubCi            — standalone CI poll (callable after manual push)
- Test-PostDeployProbe      — standalone endpoint probe

Per-sprint scripts shrink from ~400 lines to ~15:
    Import-Module gscript
    Invoke-Gscript @{
        RepoOwner    = 'owner'
        RepoName     = 'repo'
        FilesToStage = @('file1', 'file2')
        CommitMessage = 'feat: thing'
    }
    Remove-Item $MyInvocation.MyCommand.Path -Force

Template-shape (gscript_template.ps1) stays in repo as the alt single-file
mode. Module ships as the recommended path going forward.

Future:
- Bash library (sourceable gscript.bash for non-PowerShell consumers).
- .gscript.yaml cross-shell config.
'@
        }
    }
}
