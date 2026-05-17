# Changelog

All notable changes to `gscript` will be documented in this file. Format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.1.0] — 2026-05-17

PowerShell module shape ships. Per-sprint scripts shrink from ~400 lines (template mode) to ~15 lines (module mode).

### Added

- **`module/gscript.psd1`** — PowerShell module manifest. Targets PS 5.1+. Declares seven exports + tags + license/project URIs for future PSGallery publish.
- **`module/gscript.psm1`** — single-file module with all functions:
  - **`Invoke-Gscript`** — main entry. Accepts three calling conventions: (a) inline hashtable `Invoke-Gscript @{...}` (binds to `-Config` positionally; most natural for per-sprint scripts), (b) PowerShell splatting `$p = @{...}; Invoke-Gscript @p` (for sharing config across calls), (c) named parameters `Invoke-Gscript -RepoOwner 'x' ...` (explicit, verbose). Auto-detects the caller's `$PSScriptRoot` as the working directory so per-sprint scripts in repo root just work. Required fields validated manually in the function body (not via `[Parameter(Mandatory)]`) so PowerShell doesn't interactively prompt — fail-loud with a clear error message when missing.
  - **`Clear-StaleGitLocks`** — standalone stale-lock recovery. Throws on concurrent-git-detected; returns clean otherwise.
  - **`Invoke-GitWithRetry`** — wraps any `git` invocation in 3-attempt exponential-backoff retry against lock collisions.
  - **`Test-TrailingNulls`** — returns `@{ HasNulls = $bool; Count = $int }` for one file. Standalone FUSE-mount-trailing-null defense.
  - **`Get-LocalmdPat`** — reads `~/private/local.md` (per-OS path), picks up the first `github_pat_...` match, returns the token string. Throws on missing file or no match.
  - **`Watch-GithubCi`** — polls the workflow run keyed on a commit SHA + diffs per-job per-step state. Returns `@{ Success; RunId; Url; Conclusion }`. Callable standalone after a manual `git push`.
  - **`Test-PostDeployProbe`** — curls a list of endpoints, returns `@{ Success; Failures }`. Standalone post-deploy verifier.
- **`README.md`** — new "Module API" section + "Pick a mode" structure that documents module mode (recommended for new consumers) alongside template mode (still supported).
- **Repository layout** — new `module/` directory housing the manifest + body.

### Changed

- **Versions section** — v1.0 marked shipped, v1.1 marked current, v1.1.1 banked for PSGallery publish, v1.2 banked for bash library at parity with the PS module, v2.0 banked for cross-shell `.gscript.yaml` config.

### Unchanged (template mode still ships)

- `gscript_template.ps1` and `gscript_template.sh` continue to ship as the canonical single-file mode. The two modes co-exist. Module mode is recommended for new consumers because of the 25x line reduction in per-sprint scripts; template mode is preserved for consumers who want a single-file artifact with no module dependency.
- All defenses identical between the two modes. The module's functions ARE the template's logic, just refactored.

### Defense behavior

No defense behavior changes. The same six gotcha defenses ship: stale-lock auto-recovery, retry-on-collision, FUSE trailing-null preflight, fetch+divergence check, post-push CI watch with per-step granularity, post-deploy probe. The module just exposes them as callable functions instead of inlining them in a per-sprint script.

## [1.0.2] — 2026-05-17

Final operator-genericization pass. `docs/LOCALMD.md` had operator-specific values left over:

- Machine identification example: "HECATE (Windows dev workstation, path C:\Users\eriki\\)" → "workstation (Windows dev workstation, path C:\Users\username\\)".
- PAT section heading: "### DARWIN_HUB_V3 — GitHub fine-grained PAT (90-day, expires 2026-07-30)" → "### GITHUB_PAT — GitHub fine-grained PAT (90-day, expires YYYY-MM-DD)".

Both example markdown values; no template behavior changes.

## [1.0.1] — 2026-05-17

Genericization patch — v1.0.0 leaked dead links to a private repo and operator-specific identity values into the public template.

### Changed

- **`README.md`**: dropped dead link to private `erikcheatham/AllThruit` repo in the "Authorship + lineage" section. Rephrased as generic "multi-repo .NET stack with multi-machine deployment".
- **`CHANGELOG.md`**: same drop in the v1.0.0 entry.
- **`docs/PAT-SETUP.md`**: "AllThruit-grade scoping" → "full-ceremony scoping" (operator-internal naming → generic).
- **`docs/DESIGN.md`**: v2.0 `.gscript.yaml` example uses generic placeholders (`your-github-username`, `your-repo-name`, `ai-bot`, `your-app.example.com`) instead of the private repo's literal values.
- **`gscript_template.ps1`**: persona-identity comment example uses generic `ai-bot` instead of an operator-specific persona name.
- **`examples/full-ceremony.{ps1,sh}`**: `$RepoOwner`, `$RepoName`, persona values, and `$ProbeEndpoints` URLs all genericized.

All remaining `erikcheatham/*` references in public files point to `erikcheatham/Recto` (public OSS) or `erikcheatham/gscript` (self-reference) — both valid for external readers.

No template behavior changes — pure docs/examples polish.

## [1.0.0] — 2026-05-17

Initial release. Extracted from infrastructure originally built for a multi-module .NET project + the Recto substrate; generalized into a standalone wrapper anyone can adopt.

### Added

- **PowerShell template** (`gscript_template.ps1`) — canonical reusable shape with:
  - Stale-lock auto-recovery (`Clear-StaleGitLocks` — detects six known lock file types, auto-removes when no git processes running)
  - Git retry wrapper (`Invoke-GitWithRetry` — 3-attempt exponential backoff against lock-collision errors)
  - PAT resolution from `~/private/local.md` (no env vars, no GCM popups)
  - Trailing-null preflight on every text-extension file (defends against FUSE-mount trailing-null gotcha)
  - Extensible per-sprint validators (JSON parse hook included; YAML/XML can be added per sprint)
  - Fetch + divergence check (refuses to commit on top of a stale tree)
  - Stage explicit paths only — NEVER `git add .`
  - Audit staged set + warn on unexpected
  - Commit via tempfile (avoids PowerShell quoting hell on multi-paragraph commit messages)
  - Push via PAT-in-URL (never bakes into `.git/config`)
  - CI watch with per-step granularity (polls `/actions/workflows/{file}/runs` + `/actions/runs/{id}/jobs`; surfaces transitions diff-only)
  - Post-deploy probe (curls configurable endpoint list, verifies status-code ranges)
  - Self-delete on success per the gscript convention

- **Bash port** (`gscript_template.sh`) — feature-parity with the PowerShell template. Requires bash 4+, curl, jq, python3. Tested on macOS 14.x and Ubuntu 22.04.

- **Documentation**:
  - `README.md` — quickstart + feature table + lineage
  - `docs/PAT-SETUP.md` — GitHub fine-grained PAT scoping (with the Workflows-vs-Actions distinction explicit)
  - `docs/LOCALMD.md` — the localmd convention rationale
  - `docs/GOTCHAS.md` — production-incident archaeology for each defense
  - `docs/DESIGN.md` — philosophy + future direction (v1.1 module, v2.0 config-driven)

- **Examples**:
  - `examples/minimal-push.ps1` / `examples/minimal-push.sh` — smallest viable form (single file, no CI watch, no probes)
  - `examples/full-ceremony.ps1` / `examples/full-ceremony.sh` — full sprint ceremony (multi-file stage, JSON validator, CI watch, multi-endpoint probe)

### Notes

- Apache 2.0 licensed (matches Recto's posture).
- v1's single-file template shape is intentional — see `docs/DESIGN.md` for the rationale (discoverability + modifiability + auditability).
- Module shape (`Import-Module gscript`) banked for v1.1.
- Config-driven cross-shell shape (`.gscript.yaml`) banked for v2.0.

## [Unreleased]

Things in flight or recently considered but not yet shipped:

- PowerShell module packaging for PSGallery publish
- Multi-workflow CI watch (track parallel workflows triggered by one push)
- Per-namespace PAT routing in localmd (when one operator has PATs for multiple GitHub orgs)
- Pre-commit lint hook (configurable per-sprint)
- Deployment status reporting to GitHub Deployments UI
- Slack/Discord webhook notifications on success/failure
