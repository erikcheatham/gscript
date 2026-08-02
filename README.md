# gscript

> The push ceremony as a tool: a self-healing, fail-loud git push/pull/release CLI with content gates, run-time credentials, CI watching, and a file-backed task bus — built for repos where an AI session authors the commits and an operator approves the ships.

`gscript` is a cross-platform .NET global tool (`dotnet tool install -g gscrpt`) that wraps every git operation a dev-ops ceremony needs: explicit-file staging (never `git add .`), content-shape preflight gates, leak checking on public trees, stale-lock auto-recovery, divergence guards, PAT-or-App-token auth resolved fresh at run time (no env vars, no credential-manager dialogs, ever), per-step CI watching, post-deploy probes, and a task bus so an agent can propose a push and a human can approve and run it.

It exists because the alternative is "git push, then alt-tab to GitHub, then wait, then curl staging, then realize the PAT expired, then discover the file the sandbox wrote was full of trailing NULs" — and we'd rather have one command that refuses loudly before any of that ships.

## Install

```
dotnet tool install -g gscrpt --prerelease
```

Update: `dotnet tool update -g gscrpt --prerelease --no-http-cache`.

One-time setup per machine:

1. A GitHub fine-grained PAT (Contents R/W + Actions: Read + Workflows: R/W) in a private localmd file — see [docs/PAT-SETUP.md](docs/PAT-SETUP.md) and [docs/LOCALMD.md](docs/LOCALMD.md). Or skip PATs entirely with a GitHub App + TPM-sealed key — see [docs/CREDENTIAL-SOURCE.md](docs/CREDENTIAL-SOURCE.md).
2. Tell the tool where that private folder lives on this machine:

```
gscript config set localmdPath <path-to-your-private-folder>
```

That's the machine-level config (`%APPDATA%\gscript\config.json`; POSIX `~/.config/gscript/config.json`) — a pointer, not a secret. It makes every verb resolve correctly from any directory. `gscript config show` reads back what resolves.

## Verbs

```
gscript push [options]        stage explicit files, gate, commit, push, watch CI, probe
gscript pull [options]        fetch + fast-forward through the ceremony (refuses over local commits)
gscript release [--tag vX.Y.Z]  lockstep-checked tag + tag-push + publish-workflow report
gscript task <post|list|show|approve|reject|run>   the file-backed task bus
gscript im <lint|digest>      the IM hub linter/digest
gscript cred test             credential read-back / TPM seal check
gscript config <show|set>     machine-level config (where localmd lives on THIS machine)
```

A typical sprint push:

```
gscript push --files src/Thing.cs,docs/notes.md -m "feat: the thing"
```

Doc-only work: add `--no-deploy` (appends `[skip ci]`, skips CI watch + probes). `--dry-run` runs gates + divergence check and stops.

## What push does, in order

| Step | What | Why |
|---|---|---|
| 1 | Stale-lock auto-recovery | `.git/index.lock` and friends get left behind by crashed processes and editor git-polling races. Cleared when no git process is running; every git call retries with backoff. |
| 2 | Credential resolution | Resolved fresh at run time through the ordered `credentialSource` list: localmd PAT, or a TPM-minted GitHub App installation token. No env vars (stale across shells), no GCM dialogs (non-interactive is pinned on every child git), nothing baked into `.git/config`. |
| 3 | Content gates | Trailing-NUL check (the sandbox-mount trap), file-size sanity + markdown shrink gates (truncation defense), structured-file parse (JSON/XML/YAML), leak-check against operator-defined patterns on public repos. |
| 4 | Fetch + divergence guard | Refuses to commit on a stale tree. If origin advanced disjointly from `--files`, auto-fast-forwards; a real overlap refuses (`--no-sync` opts out of the auto-FF). |
| 5 | Explicit staging | Each `--files` path gets its own `git add -- <path>`. Never `git add .`. A loose-file audit warns about modified files outside the set (`--require-clean` fails hard). |
| 6 | Commit via tempfile | Multi-paragraph messages through `git commit -F`; identity via `-c user.name/user.email`, never the repo config. |
| 7 | Push + honest refs | PAT/token-in-URL push, then the tracking ref is refreshed so `git status` tells the truth (the stale-`origin/main` phantom, mechanized away). |
| 8 | CI watch | Per-job, per-step transitions polled live. A secondary workflow can be reported (bounded, never gating) with MODE honesty: "the suite ran and failed" and "the suite NEVER RAN" are different sentences. |
| 9 | Post-deploy probes | Curls a configured endpoint list; push → CI green → the endpoint actually answers. |

`pull` rides the same machinery with a read-only credential (`contents:read` App mints — the token that fetches cannot write). `release` refuses unless every `releaseLockstepFiles` entry literally contains the version being tagged, refuses to release unpushed work, and cleans up its own tag on a failed push.

## The task bus

An agent seat that must never run mutating git can still ship: it edits files, then posts a task record (JSON: repo, working dir, files, message) to a `tasks/` folder beside your private localmd. The operator runs `gscript task list` → `approve` → `run`; the run executes the full push ceremony and writes the resulting sha (or the failure) back into the record. "Reported done" and "shipped" stop being the same claim.

## Configuration

Three tiers, narrowest wins:

| Tier | File | Holds |
|---|---|---|
| Repo | `./gscript.json` | Owner/name, workflow files, gates, probes, visibility, `credentialSource` |
| Machine | `%APPDATA%\gscript\config.json` | `localmdPath` — where the private folder lives on this machine |
| CLI | flags | Per-sprint data (`--files`, `-m`) + any override |

The public tree never holds an operator path; the machine file holds facts, not secrets; secrets live in the operator's private localmd (or a TPM-sealed App key) and are read fresh per run.

## Repository layout

```
gscript/
├── README.md                  # this file
├── LICENSE                    # Apache 2.0
├── CHANGELOG.md               # version history (2.0.0-alpha.*)
├── gscript.json               # this repo's own ceremony config (it ships itself)
├── src/Gscript/               # the C# CLI
│   ├── Program.cs             # verb dispatch + options
│   ├── GscriptRunner.cs       # the push ceremony
│   ├── Pull/ Release/         # the pull + release ceremonies
│   ├── Gates/                 # content gates (NUL, size, structure, markdown, leak)
│   ├── Git/                   # lock-clearing, retrying git runner
│   ├── Credentials/           # localmd PAT + GitHub App/TPM sources, resolver
│   ├── Ci/                    # per-step CI watch
│   ├── Tasks/                 # the file-backed task bus
│   ├── Im/                    # IM hub lint/digest
│   └── Local/                 # localmd + machine config
└── docs/
    ├── PAT-SETUP.md           # PAT scoping guide
    ├── LOCALMD.md             # the localmd convention
    ├── CREDENTIAL-SOURCE.md   # GitHub App + TPM design (PAT-less operation)
    ├── GOTCHAS.md             # the production-history archaeology
    └── DESIGN.md              # why each decision
```

## History

`1.x` was a PowerShell module + single-file templates (see git history and [CHANGELOG.md](CHANGELOG.md)); `2.0.0-alpha.*` is the C# rewrite as a dotnet global tool, which replaced it entirely — one binary, every OS, no per-repo script copies. The PowerShell surface has been removed from the tree; its lineage and lessons live on in `docs/GOTCHAS.md`.

## The defenses by gotcha

Every check traces to a real production bug, documented in [docs/GOTCHAS.md](docs/GOTCHAS.md). Highlights: sandbox mount layers appending trailing `\x00` bytes to written files; editor git-polling lock collisions; env-var credential staleness across shells; the Actions-vs-Workflows PAT scope confusion that 403s CI watching; GCM dialogs stealing focus mid-script; the tracking ref lying after PAT-URL pushes; a green primary workflow masking a test suite that never ran.

## Lineage

Extracted from infrastructure built across a multi-repo .NET stack with a multi-machine deployment (dev workstation + staging + build host + generation server). The same ceremony works for any GitHub-hosted project where an AI session authors commits through a sandboxed file layer and a human owns the ship decision.

Sister project: [Recto](https://github.com/erikcheatham/Recto) — an operator-phone-as-root-of-trust capability substrate. gscript's credential model is sized so a future resolver can ride a Recto vault without changing the per-sprint contract.

## License

Apache 2.0. See [LICENSE](LICENSE).

## Contributing

Issues + PRs welcome. The hard rules:

1. **No new external dependencies.** The tool is stdlib-only (.NET BCL + child `git`). Single-binary portability is load-bearing.
2. **Every new defense traces to a real bug.** Add the incident to `docs/GOTCHAS.md` in the same PR. Speculative defenses don't ship.
3. **Fail loud, never silent.** A gate that can't run is a red result, not a skipped one; a report never invents a mode it didn't verify.
