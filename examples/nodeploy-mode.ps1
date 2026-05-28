# gscript NODEPLOY MODE EXAMPLE (v1.2.0+)
#
# Documentation pushes / IM banking / config tweaks where firing CI
# would be wasted wall-clock. The -NoDeploy switch coordinates three
# mutations so callers don't have to set them individually:
#
#   1. Auto-appends `[skip ci]` to the commit subject line. GitHub
#      Actions' canonical skip directive — causes GitHub to skip ALL
#      workflow runs for the push regardless of paths-ignore filters.
#      Idempotent: skipped if subject already carries `[skip ci]`,
#      `[ci skip]`, `[no ci]`, `[skip actions]`, or `[actions skip]`.
#   2. Forces WatchCi = $false (no point polling for a run that will
#      never appear).
#   3. Forces ProbeEndpoints = @() (no point probing when no deploy
#      happened).
#
# Total wall-clock: typically 5-10 seconds (just the git push + commit
# overhead) vs the full ~6 min the regular ceremony would consume.

$ErrorActionPreference = "Stop"

Import-Module C:\path\to\gscript\module\gscript.psd1 -Force

Invoke-Gscript @{
    RepoOwner     = 'your-github-username'
    RepoName      = 'your-repo-name'
    CommitName    = 'ai-bot'
    CommitEmail   = 'ai-bot@example.com'

    # Documentation files only — no source code, no migrations.
    FilesToStage  = @(
        'CHANGELOG.md',
        'docs/architecture.md'
    )

    CommitMessage = @"
docs(arch): bank new architectural commitment

Banks the result of today's design discussion -- updates the canonical
architecture doc + appends a CHANGELOG entry. Pure prose; no code.
"@

    # The switch that does it all.
    NoDeploy      = $true
}

# Self-delete per the gscript convention.
Remove-Item $MyInvocation.MyCommand.Path -Force
