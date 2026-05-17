# The localmd convention

`gscript` reads its GitHub PAT from `~/private/local.md` at run time. This document explains why that path, why a markdown file, and the broader convention this fits into.

## The path

| OS | Path |
|---|---|
| Windows | `%USERPROFILE%\private\local.md` (typically `C:\Users\<you>\private\local.md`) |
| macOS / Linux | `~/private/local.md` |

The directory name is **`private/`** (not `.private/` or `secrets/`) because the contents are meant to be HUMAN-EDITED, OPERATOR-PRIVATE notes that include credentials as a subset. It's a personal scratchpad with a "Secrets and API tokens" section, NOT a credential vault.

## The shape

`local.md` is just a markdown file. The gscript regex `github_pat_[A-Za-z0-9_]{40,}` matches a fine-grained PAT anywhere in the file. The recommended structure:

```markdown
# Local operator notes

This file is the operator's private scratchpad. Never committed anywhere.
Contains: workflow notes, infrastructure-specific gotchas only this operator
hits, credential index for AI-authored gitscripts.

## Machine identification

Working from: HECATE (Windows dev workstation, path C:\Users\eriki\)

## Secrets and API tokens

### DARWIN_HUB_V3 — GitHub fine-grained PAT (90-day, expires 2026-07-30)

Scopes: Contents R/W, Actions: Read, Workflows R/W, Pull requests R/W,
Deployments R/W, Environments R/W, Secrets R/W, Variables R/W,
Webhooks R/W, Security advisories R/W, Metadata Read.

Repo access: All repositories.

Used by: AI-authored gscripts across all my repos.

`github_pat_11ABCDEFG0_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx`

### Other credentials

[other tokens, API keys, etc.]
```

The gscript's regex picks up the first `github_pat_...` match. If you have multiple PATs (for different GitHub namespaces, or a historical token kept for reference), the first one wins — make sure that's the right one, OR override the regex in your per-sprint gscript.

## Why a single file (not a vault, not env vars, not a credential manager)

We tried each of those. Each has a specific failure mode the localmd convention sidesteps.

### Why not env vars

PowerShell's environment-variable model is the well-known footgun:

- `[Environment]::SetEnvironmentVariable("PAT", "...", "User")` writes to the user registry hive.
- New PowerShell windows pick up the new value at launch.
- The CURRENT window does NOT — its process env was populated at launch and isn't refreshed.

Operator workflow when env vars are the source of truth:

1. Rotate PAT at github.com
2. Update env var via `[Environment]::SetEnvironmentVariable(...)`
3. Run gscript in the SAME PowerShell window — still uses the OLD value
4. Push fails with 401 — confusing because the value "should" be the new one
5. Operator closes the window, opens a fresh one — gscript finally works

Localmd sidesteps this entirely: the gscript reads the file fresh at run time. Rotation = edit the file = the next run picks it up. No shell restart.

### Why not a credential manager (GCM, Keychain, DPAPI)

OS-level credential managers solve real problems for INTERACTIVE git operations (`git push` from VS Code, GitHub Desktop, etc.) but they're hostile to scripted flows:

- **GCM**: when no cached credential exists, GCM intercepts `git push` with either a Windows credential dialog OR a browser OAuth flow. Both steal focus mid-script. The browser OAuth flow opens in your default browser, which then sits foregrounded waiting for you to click "Authorize" — and if you're running the script while doing other work, this is intrusive at exactly the wrong moment.
- **macOS Keychain**: works for INTERACTIVE Terminal sessions. Non-interactive SSH sessions (e.g. running a script via `ssh user@host "script.sh"`) hit "Device not configured" errors because the Keychain helper needs a desktop session to talk to.
- **DPAPI**: works for the user that encrypted the secret. If you run a script under a different user account or as a service, the DPAPI-encrypted value is unreadable.

Localmd has none of these constraints — it's a plain file with plain bytes. Any process under the operator's user account can read it.

### Why not a "real" vault (Vault, AWS Secrets Manager, 1Password CLI)

Real vaults are appropriate when you have multiple parties needing access, audit requirements, automated rotation policies, or cross-machine sync. For a single operator's AI-pair-programming PAT, they're heavyweight:

- Vault: requires running a Vault server, authentication setup, token-renewal logic in every consumer. Worth it at team scale; overkill solo.
- AWS Secrets Manager: ties you to AWS credentials being available on every machine. Adds a network round-trip per gscript run.
- 1Password CLI: works well but requires the operator to be signed into 1Password during every script run; sometimes the 1P session expires mid-week and the gscript fails with a cryptic "session lock" error.

The localmd convention is sized for: ONE operator, ONE primary machine (occasionally synced to others manually), small set of credentials, manual rotation cadence. When any of those assumptions breaks, graduate to a vault. Until then, localmd is the right scope.

## Multi-machine sync

If you operate from multiple machines, you need a sync strategy. Options ranked by ceremony:

1. **Manual scp** (`scp ~/private/local.md user@other-machine:~/private/local.md`) — simplest, fine for occasional sync.
2. **Private git repo named e.g. `localmd`** — clone to `~/private/localmd/` on each machine, edit, commit, pull on the others. Trades scp friction for git-flow friction.
3. **Syncthing / Resilio** — peer-to-peer file sync, lower friction but adds a daemon to each machine.
4. **OneDrive / iCloud Drive / Google Drive** — works but stores the PAT in cloud-provider plaintext (encrypted-at-rest by the provider but readable to their staff). Acceptable IF you trust the provider, NOT recommended for higher-stakes credentials.

The right choice depends on threat model. For most solo operators, options 1 or 2 are fine.

## What NEVER goes in localmd

- Production database passwords for systems you're paid to operate (those belong in the production vault).
- Secrets your employer owns (those belong in the employer's vault).
- Credentials for systems you don't have administrative authority to rotate (you can't fix it if leaked).

localmd is for credentials YOU mint, YOU own, YOU can rotate. The PAT is the canonical example.

## Backups

If you lose localmd, you lose the PAT — but the PAT exists in github.com/settings/personal-access-tokens and can be regenerated. So localmd loss is annoying (re-mint + re-copy) but NOT catastrophic.

What IS catastrophic: losing localmd AND the operator's BIP-39 mnemonic / Recto enclave key / 2FA backup codes if those are stored only in localmd. Those credentials should NOT be only in localmd — they belong in physical-form backups (paper, metal plate, hardware safe). localmd is the "I need this every day" tier; long-term recovery credentials live elsewhere.

## See also

- [PAT-SETUP.md](PAT-SETUP.md) — how to mint the GitHub PAT that goes in localmd
- [GOTCHAS.md](GOTCHAS.md) — the production bugs that drove each gscript defense
