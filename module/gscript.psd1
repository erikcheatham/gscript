@{
    RootModule        = 'gscript.psm1'
    ModuleVersion     = '1.1.0'
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

v1.2 banked: bash library (sourceable gscript.bash for non-PowerShell consumers).
v2.0 banked: .gscript.yaml cross-shell config.
'@
        }
    }
}
