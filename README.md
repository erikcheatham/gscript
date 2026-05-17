# gscript

> AI-authored git push wrappers that self-heal, fail-loud, and walk the operator through every sprint-completion ceremony in one command.

`gscript` is a single-file shell script (PowerShell on Windows, bash on Linux/macOS) that wraps `git push` with the defensive plumbing every AI-pair-programming session keeps needing: localmd-resolved auth, trailing-null preflight, stale-lock auto-recovery, retry-with-backoff against transient lock collisions, post-push CI watch with per-step granularity, post-deploy probes. Then it self-deletes.

It exists because the alternative is "git push, then alt-tab to GitHub, then wait, then alt-tab to staging, then curl, then update an IM file by hand, then realize the PAT expired, then..." and we'd rather have one command.

## Quickstart

1. **One-time setup** — see [docs/PAT-SETUP.md](docs/PAT-SETUP.md) for the GitHub PAT scoping (Contents R/W + Actions: Read + Workflows: R/W) and [docs/LOCALMD.md](docs/LOCALMD.md) for the localmd convention.

2. **Pick a mode:**

   **Module mode (PowerShell, recommended)** — `Import-Module` the module shipped in this repo + call `Invoke-Gscript` with a hashtable. Per-sprint scripts shrink from ~400 lines to ~15:

   ```powershell
   # In <your-repo>/gscript.ps1:
   Import-Module C:\path\to\gscript\module\gscript.psd1

   Invoke-Gscript @{
       RepoOwner     = 'your-username'
       RepoName      = 'your-repo'
       FilesToStage  = @('src/file.cs', 'docs/notes.md')
       CommitMessage = "feat: thing`n`nDetailed description."
   }

   Remove-Item $MyInvocation.MyCommand.Path -Force
   ```

   Three calling conventions all work, pick whichever reads cleanest:

   ```powershell
   # 1. Inline hashtable (above) — natural for per-sprint scripts
   Invoke-Gscript @{ RepoOwner='owner'; RepoName='repo'; ... }

   # 2. Named parameters — verbose but explicit
   Invoke-Gscript -RepoOwner 'owner' -RepoName 'repo' `
                  -FilesToStage @('src/file.cs') -CommitMessage 'feat'

   # 3. PowerShell splatting — when sharing a config across calls
   $params = @{ RepoOwner='owner'; ... }
   Invoke-Gscript @params
   ```

   **Template mode (PowerShell or bash, alt single-file)** — copy `gscript_template.ps1` (or `gscript_template.sh`) into your repo root as `gscript.ps1` (or `gscript.sh`), fill in the per-sprint sections (files-to-stage list + commit message + per-env config), then:

   ```powershell
   # Windows PowerShell
   cd C:\path\to\your-repo
   .\gscript.ps1
   ```

   ```bash
   # Linux / macOS (bash template only — module is PowerShell-only at v1.1)
   cd /path/to/your-repo
   ./gscript.sh
   ```

3. **Watch it work** — preflight checks run, files stage, commit lands, push fires, CI watch surfaces every job + step transition live in your shell, post-deploy probes verify the endpoint is responding, script self-deletes. One command, full sprint shipped end-to-end.

## Module API (v1.1+)

The module exports seven functions. `Invoke-Gscript` is the main entry; the rest are standalone helpers callable on their own.

| Function | Purpose |
|---|---|
| `Invoke-Gscript` | Main entry. Runs the full ceremony from a hashtable or parameter set. |
| `Clear-StaleGitLocks` | Auto-clean stale `.git/*.lock` files when no git processes are running. |
| `Invoke-GitWithRetry` | Run any git command with 3-attempt exponential backoff against lock collisions. |
| `Test-TrailingNulls` | Check one file for trailing `\x00` bytes (FUSE-mount gotcha defense). |
| `Get-LocalmdPat` | Resolve a GitHub PAT from `~/private/local.md`. |
| `Watch-GithubCi` | Poll GitHub Actions for a workflow run, print per-step transitions. |
| `Test-PostDeployProbe` | Curl an endpoint list, verify each returns a status in the expected range. |

Each function has full PowerShell help: `Get-Help Invoke-Gscript -Full`.

Installation:

```powershell
# In-repo (clone gscript repo, point Import-Module at the manifest)
git clone https://github.com/erikcheatham/gscript.git
Import-Module C:\path\to\gscript\module\gscript.psd1

# Future: PSGallery (banked for v1.1.1+)
# Install-Module gscript -Scope CurrentUser
```

## What it does (in order)

| Step | What | Why |
|---|---|---|
| 1 | Stale-lock auto-recovery | `.git/index.lock` and friends get left behind when a prior git process crashed or VS Code's git-polling collided. Detects + auto-removes when no `git` processes are actively running. Self-heals from prior crashes. |
| 2 | PAT resolution from localmd | Reads `~/private/local.md` for a `github_pat_...` value. No env vars (they go stale across PowerShell windows). No GCM popups (intercept the operator at the wrong moment). One canonical source. |
| 3 | Trailing-null preflight | Iterates every text-extension file in `filesToStage`. Refuses to commit if any has trailing `\x00` bytes — the FUSE-mount-rsync-trailing-null gotcha that bites sandboxed AI agents writing files through mount layers. |
| 4 | Per-sprint validators | JSON parse on appsettings, YAML parse on workflows, XML parse on csproj — fail loudly Windows-side before committing corrupt content. Extensible per project. |
| 5 | Fetch + divergence check | `git fetch` via PAT-in-URL, verify local isn't behind origin/main. Refuses to commit on top of a stale tree. |
| 6 | Stage explicit paths | Each path in `filesToStage` gets a `git add -- <path>`. NEVER `git add .` — defensive against accidentally bundling operator-scratch into commits. Wrapped in retry-with-backoff to absorb transient lock collisions. |
| 7 | Audit staged set | Lists what got staged; warns on unexpected files; fails-loud if nothing staged. |
| 8 | Commit via tempfile | Multi-paragraph commit message goes through `git commit -F <tempfile>` (avoids PowerShell quoting hell). Identity baked in via `-c user.name -c user.email` so the per-repo `.git/config` author identity stays clean. |
| 9 | Push via PAT-in-URL | Inline PAT in the push URL — never bakes into `.git/config`, no credential-helper cache, no GCM dialog. |
| 10 | CI watch (per-step) | Polls GitHub Actions API every 20s (configurable). Surfaces workflow-run-level status transitions AND per-job per-step transitions with timing. Diff-only output (no full-state redraws). Color-coded ASCII markers (`>>` / `OK ` / `XX ` / `-- `) to avoid Unicode-arrow rendering issues. |
| 11 | Post-deploy probe | Curls a configurable endpoint list, verifies each lands in the expected status-code range. Closes the loop: push → CI green → staging actually responds. |
| 12 | Self-delete | Per-sprint `gscript.ps1` removes itself on success. The script is an AI-generated artifact specific to one sprint; not part of repo history. The template stays committed at `gscript_template.ps1`. |

## The defenses by gotcha

Each defense exists because of a specific bug we hit, in production, more than once. See [docs/GOTCHAS.md](docs/GOTCHAS.md) for the full archaeology. Highlights:

- **FUSE-mount trailing-null trap.** Sandbox AI agents (Claude in Cowork, Cursor's sandbox, GitHub Copilot Workspace) write files through mount layers that occasionally append 1-1143 trailing `\x00` bytes. JSON/YAML parsers reject; the operator gets cryptic deploy failures. Trailing-null preflight catches it before commit.
- **Git lock-file races.** VS Code's git integration polls every N seconds; if your `git add` lands in that window, it fails with "Unable to create '.git/index.lock'". Stale-lock auto-recovery + retry-with-backoff absorbs the collision.
- **PowerShell env-var staleness.** `[Environment]::SetEnvironmentVariable("PAT", ..., "User")` writes to registry but doesn't update the CURRENT shell. Operators rotate the PAT, run the script in the same window, fail mysteriously. Solution: localmd-only, no env vars, no rotation ceremony per-window.
- **PAT scope confusion.** GitHub's "Workflows" permission lets you edit `.yml` files; "Actions" permission lets you READ run statuses. They're different. Operators add Workflows and assume CI watching will work; it 403s every poll. Documented separately, surfaced in the error message.
- **CI watch granularity.** Operators want to know "is build-and-push done?" vs "is deploy mid-flight?" not just "workflow=in_progress". Per-step polling surfaces the same view the GitHub UI renders, in your shell.
- **GCM browser popups.** Git Credential Manager intercepts `git fetch` with a Windows credential dialog or browser OAuth flow if no cached cred. Steals focus mid-script. Solution: PAT-in-URL bypasses the credential helper entirely; recommend `git config --global --unset credential.helper` to remove GCM from the path.

## Repository layout

```
gscript/
├── README.md                       # this file
├── LICENSE                         # Apache 2.0
├── CHANGELOG.md                    # version history
├── module/
│   ├── gscript.psd1                # PowerShell module manifest (v1.1+)
│   └── gscript.psm1                # PowerShell module body (seven exports)
├── gscript_template.ps1            # canonical PowerShell template (alt single-file mode)
├── gscript_template.sh             # canonical bash port (single-file only at v1.1)
├── examples/
│   ├── module-mode.ps1             # v1.1+ module-mode (~15-line per-sprint script)
│   ├── minimal-push.ps1            # smallest viable example (PS template mode)
│   ├── full-ceremony.ps1           # full sprint ceremony (PS template mode)
│   ├── minimal-push.sh             # smallest viable example (bash)
│   └── full-ceremony.sh            # full sprint ceremony (bash)
└── docs/
    ├── PAT-SETUP.md                # GitHub PAT scoping guide
    ├── LOCALMD.md                  # the localmd convention explained
    ├── GOTCHAS.md                  # the production-history archaeology
    └── DESIGN.md                   # why each decision
```

## Versions

- **v1.0** (shipped) — Canonical template + bash port + docs + examples. Single-file mode for either shell. Consumers copy the template into their own project, customize.
- **v1.1** (current) — PowerShell **module** shape. `Import-Module` + `Invoke-Gscript @{...}` reduces per-sprint scripts from ~400 lines to ~15. Seven functions exported: `Invoke-Gscript`, `Clear-StaleGitLocks`, `Invoke-GitWithRetry`, `Test-TrailingNulls`, `Get-LocalmdPat`, `Watch-GithubCi`, `Test-PostDeployProbe`. Template mode still supported; bash stays at template-only.
- **v1.1.1** (banked) — PSGallery publish: `Install-Module gscript` directly.
- **v1.2** (banked) — Bash sourceable library at parity with the PowerShell module: `source gscript.bash; invoke_gscript` for non-PowerShell consumers.
- **v2.0** (banked) — Cross-shell config file (`.gscript.yaml` at repo root) so per-sprint customization is data-not-code. PowerShell + bash both read the same config; ceremony stays language-portable.

## Lineage

`gscript` was extracted from infrastructure originally built across a multi-repo .NET stack with a multi-machine deployment (dev workstation + staging server + build host). The same wrapper pattern works for any GitHub-hosted project where an AI session is authoring commits via a sandboxed file layer.

Sister project: [Recto](https://github.com/erikcheatham/Recto) — an operator-phone-as-root-of-trust capability substrate. `gscript`'s localmd PAT convention is sized to be small enough that future versions can migrate to a Recto-vault-backed PAT-resolver without changing the per-sprint contract.

## License

Apache 2.0. See [LICENSE](LICENSE).

## Contributing

Issues + PRs welcome. The hard rules are:

1. **No new external dependencies** at v1. Stdlib only (PowerShell built-ins, bash + coreutils + curl). The "single file you can drop into any repo" property is load-bearing.
2. **Every new defense traces back to a real bug.** Add a row in `docs/GOTCHAS.md` explaining the production incident the new check defends against. Speculative defenses go in v2.0 (config-driven).
3. **Per-shell ports stay in sync.** A defense added to the PowerShell template gets ported to the bash template in the same PR. v1.1's module shape may unify these; until then, parity is enforced by review.
