# Gotchas

The defenses in `gscript_template.ps1` and `gscript_template.sh` aren't speculative — each one exists because of a specific production bug we hit, more than once. This document is the archaeology: what bit us, why the workaround we tried first didn't work, what the canonical defense is.

If you're considering adding a new defense to the template, the entry barrier is documenting the production incident here. Defenses without an incident go in v2.0 (config-driven).

## 1. FUSE-mount trailing-null padding (the bug that made gscript what it is)

**Symptom**: Operator commits a file via an AI coding session. The AI writes the file through a sandbox mount layer (Claude in Cowork, Cursor's sandbox, GitHub Copilot Workspace, etc.). Push succeeds. CI builds the Docker image, baking the file into the image. Container boots → crashes with:

```
JsonReaderException: Expected end of string, but instead reached end of
data. LineNumber: 119 | BytePositionInLine: 12.
```

Operator opens the file in their editor. The file looks fine. They view it via `cat`, still looks fine. They view it via `xxd` and discover the file ends with 1-1143 trailing `\x00` bytes — JSON parser rejects at the first null.

**Root cause**: The sandbox FUSE mount occasionally pads writes with trailing nulls when the AI's file-write IPC handler crosses a buffer boundary. The Windows-side filesystem receives the padded bytes verbatim. The AI's Read tool, which uses a different IPC path, doesn't see the corruption — making the bug invisible in the AI's own view of the file.

**What didn't work**:
- Trusting the AI to verify file shape post-write (the AI's view is clean — the corruption is invisible to it)
- `git diff` checks before commit (git treats `\x00` as legitimate file content for JSON files; no diff signal)
- File-size checks (the truncation/padding is too small relative to file size to flag as anomalous)

**What works**: explicit byte-level scan for trailing `\x00` before committing. The gscript's `Test-TrailingNulls` / `test_trailing_nulls` function. Restricted to text-extension files because binaries (`.docx`, `.ttf`, `.zip`, `.png`) legitimately end in non-printable bytes.

**Recovery when the gate fires**: `python -c "import pathlib; p='<file>'; pathlib.Path(p).write_bytes(pathlib.Path(p).read_bytes().rstrip(b'\\x00'))"` strips the trailing nulls in-place.

## 2. Git index.lock / HEAD.lock races

**Symptom**: gscript fails at `git add` with:

```
fatal: Unable to create '<repo>/.git/index.lock': File exists.
Another git process seems to be running in this repository, e.g.
an editor opened by 'git commit'. Please make sure all processes
are terminated then try again.
```

Operator looks for the "other git process" — there isn't one. Manually removes the lock file with `Remove-Item .git\index.lock`. Re-runs gscript. Push succeeds. The next time gscript runs from a different session, same thing happens at a different lock (`HEAD.lock`, `packed-refs.lock`, etc.).

**Root cause**: Two distinct causes, both common:

1. **VS Code's git integration polls the repo every N seconds** to update its status bar. Each poll briefly acquires the index lock. If the gscript's `git add` lands during that window, the gscript fails. The poll succeeds and the lock is released ~50ms later, but by then the gscript already crashed.

2. **Prior crashed git processes leave stale locks behind.** If a git operation crashes (SIGKILL, OOM, parent process terminated), it doesn't clean up its lock file. The lock persists indefinitely until manually removed.

**What didn't work**:
- Telling the operator to close VS Code (operator forgets, VS Code restarts on system update, etc.)
- Telling the operator to `Get-Process git` before running gscript (no git processes are RUNNING when the lock is stale; the check shows nothing)
- A single retry of `git add` (the VS Code polling is too frequent — retry hits the same poll window)

**What works**:

1. **Auto-clean stale locks at script start** (`Clear-StaleGitLocks` function). Detects six known lock-file names. If `Get-Process git*` returns nothing AND a lock file exists, the lock is stale by definition — remove it. If git processes ARE running, refuse to clean and tell the operator.

2. **Retry git operations with exponential backoff** (`Invoke-GitWithRetry`). Three attempts at 1s/2s/4s. Specifically detects lock-collision error messages (regex on `index\.lock|HEAD\.lock|Unable to create`). Between retries, re-runs `Clear-StaleGitLocks` in case the collision left a new stale file behind.

Together, these absorb both stale locks (auto-cleaned at start) and VS Code polling races (retried with backoff).

## 3. PowerShell env-var staleness

**Symptom**: Operator rotates a GitHub PAT. Updates `$env:GITHUB_PAT` in PowerShell. Runs gscript in the same window. Push fails with 401. Operator double-checks: `$env:GITHUB_PAT` shows the new value. But the gscript is somehow using the OLD value.

**Root cause**: PowerShell environment-variable handling has two distinct surfaces:

- `[Environment]::SetEnvironmentVariable("PAT", "...", "User")` writes to the user registry hive. NEW PowerShell sessions inherit this on launch.
- `$env:PAT = "..."` sets the variable in the CURRENT process's env block.

These don't sync. If the operator uses the first form to "persist" the rotation, the current session doesn't see the new value. If they use the second form, the value is lost when the window closes.

Even more confusing: if a script reads via `[Environment]::GetEnvironmentVariable("PAT", "User")` (registry-side) versus `$env:PAT` (process-side), the two can disagree mid-session.

**What didn't work**:
- Documenting "close the PowerShell window after rotation" (operator forgets; rotation followed by `gscript` is a natural muscle-memory sequence)
- Using `Get-Process pwsh | Stop-Process; Start-Process pwsh` to force a restart (intrusive, kills the operator's other shell state)

**What works**: bypass env vars entirely. Read the PAT from a file at run time. File-based credentials don't have the dual-surface problem — the gscript reads the file fresh on every run. Rotation = edit the file = next run sees the new value.

## 4. PAT scope confusion: Workflows vs Actions

**Symptom**: Operator mints a PAT, sets it up correctly per a half-remembered Stack Overflow answer ("Workflows: R/W for pushing CI changes"). gscript pushes successfully. CI watch loop spins forever on 403 errors.

**Root cause**: GitHub fine-grained PATs have TWO different permissions that operators confuse:

- **Workflows: R/W** lets the PAT push commits that include changes to `.github/workflows/*.yml` files. Without this, ANY commit touching a workflow file fails.
- **Actions: R/W** lets the PAT read workflow RUN statuses (the things the CI watch polls for). Without this, the runs endpoint 403s.

The names suggest they're related; they're independent permissions for different APIs. Operators add Workflows (intuitive) and assume CI reads will work (they don't).

**What didn't work**:
- Documenting the distinction in the PAT-mint UI (GitHub does, but it's buried in the permission description text)
- Retrying the 403 (it's structural, not transient)

**What works**:
- Document the distinction explicitly in [PAT-SETUP.md](PAT-SETUP.md)
- Surface the 403 in the gscript with a hint about Actions: Read scope (the script doesn't currently do this — backlog v1.1 enhancement)
- Note that the fix is LIVE: adding Actions: Read to an existing PAT works immediately, no rotation needed

## 5. Git Credential Manager intercepts pushes mid-script

**Symptom**: Operator runs gscript. Push step opens a Windows credential dialog asking for username/password. OR a browser tab opens to authenticate.github.com asking the operator to authorize. Either way: foreground steal, mid-script, when the operator was doing something else.

**Root cause**: Git Credential Manager (GCM) is the default credential helper that ships with Git for Windows. When git encounters an authenticated operation with no cached credential, GCM intercepts and prompts the operator interactively.

GCM is great for INTERACTIVE git use (VS Code, GitHub Desktop, manual `git push` from a terminal). It's hostile to scripted flows because:

- The credential dialog steals focus when the operator wasn't expecting it
- The browser OAuth flow opens in the default browser, which sits foregrounded
- Both block the script indefinitely waiting for operator input

**What didn't work**:
- `git config --global credential.helper ""` (sometimes works, sometimes GCM re-installs itself)
- `--no-credential-helper` flag on `git push` (doesn't exist)
- Pre-caching a credential via `git credential approve` (works but requires a separate setup step per machine)

**What works**: bypass the credential helper entirely by encoding the PAT in the push URL: `https://x-access-token:<PAT>@github.com/owner/repo.git`. Git sees the URL as already-authenticated and doesn't consult the credential helper. Sister benefit: the PAT doesn't get cached in `.git/config` or in the OS credential store.

For operators who never use credential helpers: `git config --global --unset credential.helper` to disable GCM globally. Then plain `git fetch` against an authenticated URL fails with a clean stderr error instead of opening a popup. The gscript's PAT-in-URL pattern works regardless of this setting.

## 6. CI watch granularity (the polish that crystallized the design)

**Symptom**: gscript's first CI watch implementation polled the workflow-run-level endpoint and printed `status=queued`, then `status=in_progress`, then waited 5-8 minutes silently, then printed `status=completed conclusion=success`. Operators stared at the silent middle for the bulk of the wait, occasionally tabbing to GitHub to "see what's going on".

**Root cause**: workflow-run status only transitions queued → in_progress → completed. Everything between in_progress and completed is opaque from this endpoint.

**What works**: the `/actions/runs/{run_id}/jobs` endpoint returns per-job per-step state. Each job has a `steps` array, each step has `status` (queued / in_progress / completed) + `conclusion` (success / failure / cancelled / skipped / null) + `started_at` + `completed_at`. Polling this endpoint each iteration + diffing against last-seen state gives a clean live transcript matching the GitHub UI's per-step view.

Output shape (matches GitHub UI rendering, in PowerShell):

```
  [hh:mm:ss] status=queued
  [hh:mm:ss] status=in_progress
  build-and-push job:
    [hh:mm:ss] >> Set up job ...
    [hh:mm:ss] OK  Set up job (1s)
    [hh:mm:ss] >> Checkout ...
    [hh:mm:ss] OK  Checkout (2s)
    [hh:mm:ss] >> Build and push Docker image ...
    [hh:mm:ss] OK  Build and push Docker image (4m 53s)
  deploy job:
    [hh:mm:ss] >> Set up job ...
    [hh:mm:ss] OK  Set up job (1s)
    ...
  [hh:mm:ss] status=completed conclusion=success

CI GREEN: https://github.com/owner/repo/actions/runs/N
```

The diff-only approach means no full-state redraws — each line is one transition. ASCII markers (`>>` / `OK ` / `XX ` / `-- `) instead of Unicode arrows because Razor parsers + some terminal fonts mis-render Unicode arrows.

## 7. Post-deploy probe (the closing-the-loop polish)

**Symptom**: CI green doesn't mean "the staging environment actually responds." A successful deploy can still fail at the operational layer (cloudflared tunnel dropped, container crashlooping AFTER the smoke test, network blip). Operator would push, see CI green, navigate to staging, get a 502, and have to context-switch back to debug WHY a "successful" deploy didn't actually deliver.

**What works**: after CI green, the gscript curls a configured endpoint list and verifies each lands in the expected status-code range. Most basic check: `https://staging.your-app.com/ → 200..399`. Per-sprint instances can extend the list with route-specific smoke targets (e.g. a new endpoint THIS sprint introduced, verifying it actually responds after the deploy applied the migration that created its dependency).

If the probe fails, the gscript exits with a clear "PROBE FAIL" + the failing URL + a hint that staging may still be warming up. Operator can retry manually in 30s if it's a warmup-window issue; otherwise the failure is real and worth investigating.

## What's NOT yet defended (and why)

These are known issues that gscript v1 doesn't address. Each has a reason for being deferred.

- **PAT-not-readable-by-gscript edge cases.** If the operator's localmd is encrypted (e.g. via Cryptomator, EncFS), the gscript can't read it. Defenseable but adds dependencies. Deferred to v1.1 with a clear error message when the file exists but can't be read.

- **Multi-namespace PAT routing.** If localmd has multiple `github_pat_...` values for different GitHub account namespaces, the gscript picks the first match. Operators with cross-account workflows (personal repos + work org) need to either reorder localmd or override the regex. Deferred to v1.1's per-project config file.

- **Workflow-file-name auto-detection.** Per-sprint gscripts hardcode `$CiWorkflowFile = "deploy-staging.yml"`. If a repo has multiple workflows that all trigger on push, the gscript only watches one. Deferred to v1.1 — would need to either watch all matching runs in parallel or accept a list.

- **Non-main branch support.** Per-sprint gscripts hardcode `main`. Long-feature-branch workflows would need this configurable. Deferred to v1.1.

- **Linux/macOS bash port parity tests.** The PowerShell template is the canonical surface; the bash port mirrors it manually. Drift is possible. v1.1 may add a smoke-test workflow that exercises both ports against a sandboxed test repo.

If you hit one of these and it's blocking, file an issue with the production incident details — that's the entry barrier for promoting from "banked" to "shipped defense."
