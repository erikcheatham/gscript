# Changelog

All notable changes to `gscript` will be documented in this file. Format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

> Versions `2.0.0-alpha.*` are the C# CLI/dotnet-tool successor to the PowerShell module (`1.x` below). The intervening alpha.1–alpha.4 notes live in `Gscript.csproj` `<PackageReleaseNotes>`.

## [2.0.0-alpha.9] — 2026-07-15

The `gscript im` command family — the IM index/linter ("imindex") planned in the hub's tooling canon. First lint target: the operator hub `CLAUDE.md` itself.

### Added

- **`gscript im lint`** — three checks over the institutional-memory hub: (1) **line-budget enforcement** (default 450, `--budget`; ERROR when over, WARN at `--warn-pct` (default 90%) of budget); (2) **stale-path scan** — every absolute Windows path token in the hub (and, with `--deep`, in `localmd/*.md` beside it) is existence-checked when it lives under the canonical `C:\work\` root (missing → ERROR); pre-relocation **archived roots** are WARN-only (a TODO may name them deliberately) and are operator data, so they are never hardcoded — they load from `<hubDir>\im.json` `archivedRoots` (private by construction) plus repeatable `--archived-root` flags; everything else — other machines' paths, ProgramData, placeholders — is unverifiable and listed only under `--verbose`; (3) **broken cross-ref detection** — backtick `localmd/….md` refs must resolve beside the hub (missing → ERROR). Exit 1 on any ERROR so the lint can gate a ceremony. On non-Windows hosts, existence checks are skipped (budget + cross-refs still enforced). False-positive guards earned in the first dogfood run: URL tails (`https://…`) can't match as fake drive letters, and space-containing paths truncated at the token boundary are prefix-checked against the parent directory instead of ERRORing.
- **`gscript im digest`** — generated index of the hub: budget usage, the "Last substantive edit" stamp, and a heading map with line numbers + per-section line counts.
- **Hub resolution** — `--hub <path>` wins; otherwise the hub defaults to `CLAUDE.md` in the directory of the repo's `gscript.json` `localmdPath`/`patFile`, so `gscript im lint` works bare from any consuming repo.

### Backward compatibility

Purely additive: a new top-level command; `push` and `task` are untouched.

## [2.0.0-alpha.8] — 2026-07-01

### Fixed

- **`StructuredFileGate.CheckPs1` never received the file path — every `.ps1` failed the gate.** The parse-lint shells out to `pwsh -Command "<script>"` and passed the target path as a trailing process argument, expecting it in `$args[0]`. With a STRING `-Command`, trailing arguments do NOT populate `$args` (per about_pwsh they're consumed as command text), so the script saw `$null` and `ParseFile` reported `[line 0 col 0] The file could not be read: Cannot process argument because the value of argument "path" is not valid` for EVERY `.ps1`, healthy or not — absolute path made no difference (the interim alpha.7 rooted-path fix was a misdiagnosis; superseded within the hour, never pushed). Fix: the path now travels via the `GSCRIPT_PS1_LINT_PATH` environment variable on the child process (quoting-proof, pwsh-version-proof) and is rooted via `Path.GetFullPath` (`ParseFile` wants a full path; `--files` delivers repo-relative shapes). Latent since alpha.1 — this branch had never run: the first `.ps1` staged through the C# tool was a consumer repo's WAF-updater script on 2026-07-01.

## [2.0.0-alpha.6] — 2026-06-22

Concurrent-work + runner-tree hygiene. Three features that let two agents (or two machines) push to the same repo concurrently without manual pull-dances, and that defend a *runner-shared checkout* — one where a CI runner's deploy tree is also a dev/authoring clone — from having its `git pull --ff-only` blocked by loose working-tree files.

### Added

- **Auto-sync (`SyncWithOrigin`)** — when `origin/main` advanced while you were editing, the divergence guard no longer hard-refuses. It diffs the incoming commits against `--files`: if they're **disjoint** and it's a pure fast-forward, gscript auto-`merge --ff-only`s your tree first, then commits + pushes — so two agents editing *different* files integrate automatically. It refuses (with a manual-reconcile hint) only on a real **`--files` overlap** (content conflict) or **true divergence** (you also have local commits ahead). `--no-sync` opts out.
- **Post-push tracking-ref refresh** — a PAT-in-URL push does **not** update `refs/remotes/origin/main`, which leaves the misleading "ahead by N" phantom *and* arms a divergence-trap for the next push. gscript now runs `git fetch <pushUrl> main:refs/remotes/origin/main` after a successful push (the refspec forces the just-pushed tip onto the tracking ref despite the embedded-PAT URL) so `git status` is honest. Best-effort — a refresh failure warns but never fails the already-succeeded push.
- **Loose-file guard (`CheckLooseFiles`)** — before pushing, lists files modified/untracked **outside** `--files`. On a runner-shared checkout (dev clone == the CI runner's deploy tree) those can block the *next* deploy's `git pull --ff-only`. Warns by default; **`--require-clean`** fails hard.

### Fixed

- `--version` was stale at `2.0.0-alpha.4` (the const in `Program.cs` lagged the package version). Synced to `2.0.0-alpha.6`.

### Backward compatibility

Additive. Auto-sync only ever *fast-forwards* (never merges/rebases content) and only when the incoming commits don't touch your `--files`, so it can't silently integrate conflicting work; the old hard-refuse behavior is one `--no-sync` away. The loose-file guard is warn-only unless you pass `--require-clean`.

## [2.0.0-alpha.5] — 2026-06-22

Split-aware localmd resolution + a smarter file-size gate. Both lessons banked from the 2026-06-22 ApiZone-56 push, where (a) `--localmd C:\…\private\local.md` failed because the PAT had moved to `localmd/githubPAT.md` after the localmd single-file → topic-file split, and (b) the size gate refused a legitimate 28% deletion as "possible FUSE-truncation," and its reported working size (5565 CRLF bytes) didn't match the committed blob (5454 LF bytes), forcing a confusing post-hoc reconciliation.

### Changed

- **`Localmd.ResolvePat` is split-aware.** `--localmd` (and the `localmdPath` / `patFile` config) may now point at `local.md`, the `private/` root, the `localmd/` dir, OR `githubPAT.md` directly — all resolve. The given path is honored first; if it carries no PAT, the localmd root is searched (`localmd/githubPAT.md` preferred, then `githubPAT.md`, `local.md`, then remaining name-sorted `*.md` under `localmd/` and the root) for the first `github_pat_…` match. Single-PAT first-match (multi-account resolve-by-owner stays deferred). The "not found" error now lists every file searched.
- **Leak-pattern loading is split-aware too.** The `## Leak patterns` section is now discovered across the same candidate files, so it can live in a topic file post-split instead of silently vacating — which would have made public-repo leak-checks pass vacuously (a real security regression). Fail-open posture for the no-section case is unchanged.
- **`FileSizeSanityGate` is CRLF-normalized.** Shrink % is computed on the LF-normalized working size vs the (already-LF) HEAD blob, so the reported working size matches what git actually commits — no more "5565 vs committed 5454" gap (that 111-byte delta was exactly 111 CRLFs).
- **`FileSizeSanityGate` discriminates truncation from deletion.** On a >threshold shrink the gate still REFUSES (fail-safe; `--allow-shrink` is the explicit override), but the message now corroborates structurally: trailing NULs or a mid-content ending → "LIKELY TRUNCATION; verify the tail before --allow-shrink"; a clean terminator (`}` `]` `)` `;` `>` quote backtick `.`) with no NULs → "likely a legitimate deletion; re-run with --allow-shrink \"<path>\" if intended." The heuristic only shapes the message, never the pass/fail decision.

### Backward compatibility

Purely additive. Existing `--localmd`/`localmdPath`/`patFile` values that point straight at a PAT-bearing file keep working (honored first). The gate's threshold and `--allow-shrink`/`--max-shrink-pct` semantics are unchanged; only the measurement (LF-normalized) and the failure message improved.

## [1.4.0] — 2026-05-28

Ships `-NoDeploy` switch on `Invoke-Gscript`. Three coordinated mutations behind one parameter: auto-append `[skip ci]` to the commit subject (idempotent), force `WatchCi = $false`, force `ProbeEndpoints = @()`. Use for documentation pushes / IM banking / config tweaks where firing CI would be wasted wall-clock. Total wall-clock for a NoDeploy push drops to typically 5-10 seconds vs the ~6 min the regular ceremony would consume.

### Added

- **`Invoke-Gscript -NoDeploy`** — single switch coordinating the three mutations needed for "commit lands on origin/main but no CI fires and we don't waste wall-clock waiting for it." GitHub Actions' canonical `[skip ci]` directive skips ALL workflow runs for the push regardless of paths-ignore filters. Idempotent — re-append is skipped if subject already carries any canonical skip token (`[skip ci]`, `[ci skip]`, `[no ci]`, `[skip actions]`, `[actions skip]`). Overrides any explicit `-WatchCi` / `-ProbeEndpoints` values the caller passed so callers can't half-configure it. Banner prints `[NODEPLOY mode]` lines for operator clarity.
- **`examples/nodeploy-mode.ps1`** — minimal example showing the NoDeploy flag in hashtable form (`@{ NoDeploy = $true }`).
- **CHANGELOG + module psd1 ReleaseNotes** — full v1.4.0 entry with use cases + idempotency contract + back-compat note.

### Why this matters

`-WatchCi = $false` alone gets you part of the way — the script doesn't poll the workflow-runs endpoint waiting for a result. But without `[skip ci]` in the commit message, GitHub Actions STILL fires the workflow against the new commit; the deploy still happens in the background; CI minutes still get consumed. For docs-only pushes that's pure waste.

The `-NoDeploy` switch coordinates the three settings (`[skip ci]` + `WatchCi=false` + `ProbeEndpoints=@()`) under a single semantic flag so callers don't have to remember all three. The banner makes the mode obvious in the operator's terminal.

### Backward compatibility

Default `$NoDeploy = $false` preserves existing behavior. v1.1.0 / v1.2.0 / v1.3.0 callers don't need any changes. The switch is purely additive.

### Sister change in consumer projects

The canonical consumer-side pattern lands as a sister `gscript_nodeploy.ps1` trigger wrapper at the consumer's repo root, which finds the latest `gscript_nodeploy_*.ps1` per-sprint script in `scripts/gscripts/` (sister of the existing `gscript.ps1` trigger wrapper). The two wrappers' globs are disjoint so they don't fight. v1.4.0+ per-sprint scripts in either flavor can just call `Invoke-Gscript @{ ...; NoDeploy = $true }` instead of inlining the template body.

## [1.3.0] — 2026-05-24

Ships [`docs/IM-SM-MODEL.md`](docs/IM-SM-MODEL.md) — operator-side architectural framework documenting how the Intelligence Model (cross-cutting `CLAUDE.md`) composes with the Sidecar Model (per-file `docs/code-notes/<mirror>.md`) into a unified four-tier context model. AI sessions across the operator's project portfolio load only what the current question requires; cold-start cost stays bounded; per-surface depth is discoverable on-demand.

### Added

- **`docs/IM-SM-MODEL.md`** — the canonical four-tier model: machine gate (Tier 0) → IM `CLAUDE.md` (Tier 1, always-loaded) → topic docs (Tier 2, by intent) → sidecars (Tier 3, on-demand) → source (Tier 4, when editing). Session-start ritual + per-surface ritual + knowledge-placement decision matrix + discoverability primitives (notes.ps1, pointer comments, INDEX). Includes the canonical post-migration verification sequence.
- **GOTCHAS entry 8** — Post-migration sidecar verification: bash mount lies, use Read/Grep tools. Sister-defense of entry 1's FUSE-mount trailing-null trap but in the opposite direction (bash sees EMPTY where Read tool sees CONTENT, vs entry 1 where bash sees CORRUPTION the Read tool doesn't).

### History — earned from the empirical IM-vs-SM comparison test (2026-05-24)

The framework crystallized during an A/B comparison: two AI conversations were asked the same question about an application's architecture, one with session-state-rich working memory (the migration session that authored 24 sidecars) and one fresh with zero working memory but the sidecars on disk.

The fresh conversation produced a RICHER per-file architectural answer because it was forced to load the depth via Read tool. The session-state-rich conversation produced a richer cross-cutting synthesis because it had the migration arc + active sprint state in working memory. The two outputs were complementary, not redundant. The IM + SM tiers formalize what made each output strong: the IM carries cross-cutting synthesis well; the SM carries per-file depth well. Each tier loads on its own cadence; together they describe a context surface that scales to N projects without bottlenecking on cold-start cost.

### Architectural notes

The model presupposes **Hard Rule #32** (sidecar-prose-everywhere) in any project that adopts it. Source files carry `<summary>` one-liners + brief `//` markers only; multi-paragraph rationale, decision history, parser-fragility traps, foundational primitive examples, and cross-references migrate to the sidecar. CSS comments inside Razor `<style>` blocks that contain `<` / `>` / Unicode arrows / literal HTML element-name tags MIGRATE because the sidecar is markdown — Razor parser fragility (RZ9980 / RZ10007) retires by construction.

First canonical reference implementation at scale: AllThruit's 24+ sidecars covering ~10,000 lines of source across the chat-tier civic-office model + Creator's Corner v1.1 + Theme System V2 + capability JWS verification + AI provider routing + personalization layer + In Theaters region-aware filter.

### Defense behavior

Same as v1.1 / v1.2 — the docs ship pure; the gscript engine itself unchanged. v1.3 is documentation-and-discipline, not new defenses. The new GOTCHAS entry 8 codifies the post-migration verification sequence as canonical operator-side knowledge.

### Companion: AI session startup ritual extends naturally

Existing CLAUDE.md "Conversation startup ritual" sections in operator projects already specify Tier 0 + Tier 1 reads. The framework formalizes the implicit decision NOT to pre-load Tier 3 sidecars at startup — they're the on-demand depth surface. Sessions stay fast; depth loads when a specific surface is touched. The discoverability primitives (pointer comments in source + `notes.ps1 get <substring>` + per-project `docs/code-notes/README.md` INDEX) make on-demand sidecar loading low-friction.

## [1.2.0] — 2026-05-20

Ships `authorship.ps1` — canonical authorship-cleanup tool for rewriting commit author identities across multiple repos via a git mailmap. Earned the hard way 2026-05-20 night during a cross-repo cleanup of an accidental `<NumericID>+<username>@users.noreply.github.com` noreply-binding bug where the wrong numeric GitHub user ID was embedded in a gitscript's author override, causing 5 of an operator's repos to attribute commits to a completely unrelated GitHub user.

### Added

- **`authorship.ps1`** — parameterized authorship-cleanup tool wrapping `git filter-repo --mailmap`. Pre-flight checks (git-filter-repo installed, mailmap exists + non-empty, every repo path is a valid git repo). Per-repo cleanup loop: capture pre-rewrite SHA to `<repo>/.git/authorship-pre-rewrite-sha.txt` as a rollback anchor, capture origin URL before filter-repo strips it, run filter-repo with `--force`, **immediately re-add origin BEFORE the SHA-check + push decision** (the load-bearing bug-fix from the previous round where the script bailed-without-restoring-origin on the SHA-unchanged path), verify HEAD actually changed before force-pushing, force-push to origin/main. Post-run summary table + author-diagnostic verification loop. Supports `-DryRun` (preview matches without modifying), `-SkipPush` (rewrite locally without pushing), `-GitHubOwner` (origin URL fallback prefix).
- Persistent infrastructure (NOT self-deleting like per-sprint `gscript_*` scripts). Recurring tool for whenever authorship-cleanup is needed.

### Architectural notes

The mailmap file itself is the operator's per-cleanup artifact; `authorship.ps1` is the canonical execution engine. Mailmap format (`git-mailmap(5)`):

```
<Proper Name> <proper@email> <Commit Name> <commit@email>
```

Every commit whose author matches the right-hand side gets rewritten to the left-hand side. The script does NOT prescribe what canonical identity should be — that's mailmap-driven and operator-controlled. The pattern works for any cleanup shape: collapse multiple AI-tool identities to a single human author, normalize email-domain migrations, fix noreply-ID-binding bugs like the one this version was earned from, etc.

### Defense behavior

Same six gotcha defenses as v1.1's module + template ship (stale-lock auto-recovery, retry-on-collision, FUSE trailing-null preflight, fetch+divergence check, post-push CI watch with per-step granularity, post-deploy probe) — but `authorship.ps1` doesn't run them as a normal commit flow. Its specific defenses are: pre-flight git-filter-repo install check, mailmap non-emptiness verification, repo-path validation before iterating, rollback-anchor file write per repo, **origin re-add ordering fix** (the canonical bug from the previous round), HEAD-change verification before push.

### Companion: GitHub UI cache caveat

Earned alongside the script: rewriting commits + force-pushing fixes the per-repo contributor graph at `/graphs/contributors`, but does NOT clear:
- The repo's main-page sidebar "Contributors N" widget (separate cache that retains "ever-contributed" identities)
- The profile-page "Built by" avatars on `github.com/<user>` (separate cache, refreshes least frequently)

Recreate the repo on GitHub (delete + create fresh with same name + privacy + `git push -u origin main` from local) to nuke all caches simultaneously. For repos with self-hosted GitHub Actions runners, plan for runner re-registration ceremony as part of the recreate.

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
