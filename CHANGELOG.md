# Changelog

All notable changes to `gscript` will be documented in this file. Format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.0.0] — 2026-05-17

Initial release. Extracted from infrastructure originally built for the AllThruit project + Recto substrate; generalized into a standalone wrapper anyone can adopt.

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
