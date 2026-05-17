# gscript MODULE MODE EXAMPLE (PowerShell, v1.1+)
#
# This is the canonical v1.1+ per-sprint script shape — what an AI session
# typically authors. ~15 lines of actual work (the file list + commit
# message + the Invoke-Gscript call).
#
# Compare with examples/full-ceremony.ps1 (~280 lines, template mode) to
# see the line-count delta. Both modes ship the same defenses; module
# mode just doesn't repeat them in every per-sprint instance.

$ErrorActionPreference = "Stop"

# Adjust the path to wherever you cloned the gscript repo, OR install
# the module to a $env:PSModulePath location and just `Import-Module gscript`.
Import-Module C:\path\to\gscript\module\gscript.psd1 -Force

Invoke-Gscript @{
    RepoOwner     = 'your-github-username'
    RepoName      = 'your-repo-name'
    CommitName    = 'ai-bot'
    CommitEmail   = 'ai-bot@example.com'
    CiWorkflowFile = 'deploy.yml'

    FilesToStage  = @(
        'src/Modules/MyFeature/Domain/MyEntity.cs',
        'src/Modules/MyFeature/Application/MyEntityService.cs',
        'src/Modules/MyFeature/Infrastructure/MyEntityRepository.cs',
        'src/Web/Endpoints/MyFeatureEndpoint.cs',
        'src/Web/Pages/MyFeaturePage.razor',
        'src/Web/Pages/MyFeaturePage.razor.css',
        'src/Web/appsettings.json'
    )

    # Per-sprint JSON validator — extends the trailing-null preflight with
    # content-shape validation. Each Validate is a ScriptBlock invoked
    # with the file's full path; throw on invalid.
    PerSprintValidators = @{
        json = @{
            Description = 'JSON parse'
            Files = @('src/Web/appsettings.json')
            Validate = {
                param([string]$Path)
                $raw = [System.IO.File]::ReadAllText($Path)
                $null = $raw | ConvertFrom-Json
            }
        }
    }

    ProbeEndpoints = @(
        @{ Url = 'https://staging.your-app.example.com/';              ExpectedRange = 200..399 }
        @{ Url = 'https://staging.your-app.example.com/api/v1/health'; ExpectedRange = 200..399 }
    )

    CommitMessage = @"
feat(my-feature): add MyEntity with full CRUD + endpoint + UI page

Standard four-project pattern for new domain primitives:
- MyEntity domain class
- IMyEntityService application seam
- MyEntityRepository infrastructure impl
- Carter endpoint at /api/v1/my-feature
- Razor page at /my-feature

appsettings.json gains MyFeature config section with default-on policy.
"@
}

# Self-delete per the gscript convention
Remove-Item $MyInvocation.MyCommand.Path -Force
