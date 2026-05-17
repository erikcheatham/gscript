# Design philosophy

This document explains the architectural choices behind `gscript`. Read this if you're contributing, forking, or trying to understand why the wrapper looks the way it does.

## The shape: one file, one commit, one ceremony

`gscript` is intentionally a **single shell script** that consumers copy into their repo, fill in, and run once. It is NOT a CLI tool, NOT a daemon, NOT a service, NOT a CI plugin. The single-file shape is load-bearing for three reasons:

1. **Discoverability.** An operator can read the whole script top-to-bottom in 10 minutes. Every defense is in their face. No magic happens in a library they don't have time to read.

2. **Modifiability.** When a defense doesn't fit their workflow, they can edit the script in place. No fork, no PR, no waiting for upstream. The cost of customization is a few-line diff.

3. **Auditability.** When the script does something surprising, the operator can `cat gscript.ps1 | less` and find the line. There's nowhere for behavior to hide.

The trade-off: per-sprint instances are 400-500 lines of copy-pasted-from-template code. That's intentional. The template stays in sync via "copy the new version" instead of "package upgrade." v1.1's module shape will reduce the per-sprint footprint, but ONLY if the template's surface has stabilized.

## The self-delete: per-sprint artifacts ≠ repo history

A per-sprint `gscript.ps1` is an **AI-generated artifact specific to one commit**. It contains: the exact file list for THIS commit, the exact commit message, the exact probe endpoints for THIS sprint's smoke. None of that belongs in repo history — it's ephemeral plumbing.

So per-sprint instances self-delete on success. The template (`gscript_template.ps1`) stays committed. The pattern:

1. AI session authors `gscript.ps1` from the template + this sprint's specifics
2. Operator runs `.\gscript.ps1`
3. Script self-deletes after success
4. Next sprint: AI authors a fresh `gscript.ps1`, repeat

Failures are deliberately not self-cleaning. If the script fails (preflight, push, CI, probe), it stays on disk so the operator can read the error, fix the underlying issue, and re-run. The script is idempotent on retry (stage is idempotent; commit re-uses the working-tree state; push is idempotent if the commit hasn't changed).

## The defenses: each one traces to a production bug

See [GOTCHAS.md](GOTCHAS.md) for the archaeology. The principle:

> A defense without a documented production incident is speculation. Speculation goes in v2.0's config-driven layer where operators can opt-in. v1's defenses are all earned.

This keeps the surface from accreting "defensive code" that nobody can explain the rationale for. Every preflight check answers a specific question: "what bug would have happened without this?"

## localmd over env vars / vaults / credential managers

Detailed rationale in [LOCALMD.md](LOCALMD.md). The short version:

- **Env vars**: PowerShell's dual-surface (registry vs current-process) creates a confusion that bites operators on every rotation
- **Vaults**: appropriate at team scale, overkill for solo operators with one primary machine
- **Credential managers (GCM, Keychain)**: optimized for interactive use, hostile to scripted flows

localmd is a plain markdown file. The PAT is one line in it. Reading the file is instant. Editing the file is instant. Rotation = edit the file. No daemon, no server, no setup ceremony.

## PowerShell-first, bash port at parity

The original `gscript` was authored on a Windows-primary developer workstation where PowerShell is the default shell. The bash port came second because:

1. **The PowerShell surface needed to settle first.** Adding the bash port before the defenses had stabilized would have meant double-maintenance every time a new defense landed.

2. **Bash on Windows has weird edge cases.** Git Bash, WSL, MSYS — each has its own subtle differences. PowerShell on Windows is the canonical Windows surface for the operator's machine.

3. **Linux/macOS operators get a port that exactly mirrors PS.** Not a "bash version that does slightly different things" — a literal translation, defense-for-defense, error-message-for-error-message.

The hard rule: **a defense added to one port is added to the other in the same PR**. Drift is enforced by review.

v1.1's PowerShell module may obviate the bash port question (we'd just ship the module, and bash users would call into it via `pwsh -Command`). v2.0's config-driven design unifies them via a shared YAML/JSON config that both shells read.

## What gscript IS NOT

- **Not a replacement for code review.** The script doesn't read the diff or assess code quality. It checks that bytes are well-formed, but not that they're correct. Code review happens before invoking the script.
- **Not a security boundary.** The PAT in localmd is plaintext on disk. Anyone who can read the operator's filesystem can read the PAT. If you need vault-grade secret protection, use a vault.
- **Not a CI/CD platform.** It pushes to GitHub and watches GitHub's CI. The CI itself runs on whatever provider you've configured (GitHub Actions, etc.). gscript is the local-side ceremony, not the cloud-side execution.
- **Not a deploy tool.** It watches deploys (via CI watch + post-deploy probe). It doesn't perform them. Deployment logic lives in your CI workflow.

## Future direction

### v1.1 — PowerShell module shape

```powershell
Install-Module gscript -Scope CurrentUser  # from PSGallery

# In a per-sprint gscript.ps1:
Import-Module gscript
Invoke-Gscript -Config .gscript.json
```

Exported functions become the per-sprint contract:

- `Invoke-Gscript` — runs the full ceremony from a config file
- `Test-TrailingNulls` — standalone preflight check
- `Clear-StaleGitLocks` — standalone lock recovery
- `Get-LocalmdPat` — standalone PAT resolver
- `Invoke-GitWithRetry` — standalone retry wrapper
- `Watch-GithubCi` — standalone CI watch (callable without the full ceremony)
- `Test-PostDeployProbe` — standalone probe check

Per-sprint scripts shrink from 400 lines to 5 lines + a config file.

### v2.0 — config-driven, cross-shell

```yaml
# .gscript.yaml in the repo root
repo:
  owner: your-github-username
  name: your-repo-name
ci:
  workflow: deploy.yml
  watch: true
  max_minutes: 15
probes:
  - url: https://staging.your-app.example.com/
    expected: 200..399
defaults:
  commit_author:
    name: ai-bot
    email: ai-bot@example.com

# .gscript-sprint.yaml — overridden per-sprint
files:
  - src/MyEntity.cs
  - src/MyEndpoint.cs
message: |
  feat: my feature

  Detailed description here.
```

Both `gscript.ps1` and `gscript.sh` (or eventually a single `gscript` binary) read the same config. Per-sprint customization is data, not code. AI sessions author `.gscript-sprint.yaml`, run `gscript apply`, repo-level config stays untouched.

This is the long-horizon shape. v1's template-copy pattern works for current needs without requiring this; v2 unlocks team-scale usage where the config-as-data property starts mattering.

### Speculative defenses banked for v2.0

These are reasonable defenses without enough production-incident traction to ship in v1:

- **Pre-commit lint hook** (run a configurable linter against staged files before committing)
- **CodeQL / security scan integration** (poll a separate workflow for security alerts)
- **Slack/Discord webhook notifications** (post the success/failure message to a chat channel)
- **PR creation mode** (push to a feature branch + auto-open a PR instead of direct-to-main)
- **Multi-workflow CI watch** (track multiple workflows triggered by one push)
- **Deployment status reporting** (mark the commit's deployment as success/failure in GitHub's Deployments UI)

Each of these is fine as an opt-in via v2's config. None ships in v1 because the bar is "this defense has bitten me, in production, more than once."

## Contributing

If you've hit a bug `gscript` doesn't defend against:

1. Document the production incident in [GOTCHAS.md](GOTCHAS.md) (what failed, what didn't work as a fix, what does work)
2. Add the defense to both PowerShell and bash templates in the same PR
3. Add a row to the README's "What it does" table
4. Update CHANGELOG.md with the incident reference

PRs that add defenses without the incident archaeology will be asked to add it before merge. The discipline keeps the wrapper's surface principled.
