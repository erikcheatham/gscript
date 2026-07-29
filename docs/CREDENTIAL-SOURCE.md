# Credential source — the agent-session axis (design brief)

**Status:** OPEN, raised 2026-07-28 after three PAT rotations in one evening.
Amends — does not overturn — `docs/LOCALMD.md` § "Why a single file (not a vault, not
env vars, not a credential manager)".

## What LOCALMD.md already gets right

That document is a good decision record. It states its scope explicitly: *"sized for
ONE operator, ONE primary machine, small set of credentials, manual rotation cadence.
When any of those assumptions breaks, graduate to a vault."* And its rejections are
grounded in real failure modes, not aesthetics:

- **env vars** — the current PowerShell window keeps the stale value after a rotation,
  producing a 401 that looks impossible. Correct, and still true.
- **GCM / Keychain** — interactive prompts and desktop-session dependencies that break
  scripted flows. Correct.
- **Real vaults** — server, auth setup, token renewal, network round-trip per run.
  Correct at solo scale.

None of that is wrong. The convention has been right for its stated scope.

## The assumption that broke (and isn't on the list)

**The file is now inside an AI coding session's blast radius.**

`C:\work\private` is mounted into Cowork sessions. Anything in a mounted folder is
readable by the session, and — the part that turns this from theoretical to
demonstrated — *editing* `localmd/githubPAT.md` produces a file-change notification
carrying the file's contents into the session transcript. On 2026-07-28 that happened
without any command being run, after two earlier exposures that day:

1. The operator pasted a live PAT into a prompt to set `$env:PUSHER` for a plain-git
   fallback push. (Note the irony: env vars are discouraged by LOCALMD.md *precisely
   because* they go stale — so the fallback asked for the one mechanism the design
   already warns about.)
2. A verification scan intended to prove no secrets had been added used `grep -o` over
   a diff that included the secrets file, and printed what it matched.
3. The rotation edit itself echoed the whole file.

Rotations 1 and 2 were mistakes with obvious fixes (banked in the operator's
`gotchas.md` §PAT-in-chat). **Rotation 3 had no mistake in it.** Careful operation
does not fix an exposure that requires no action beyond editing the file.

That is a new axis, orthogonal to everything LOCALMD.md weighed: not "is this
convenient", not "does this survive rotation", but *"is the plaintext at rest inside
something that reads and echoes files by design?"*

## Why DPAPI specifically dodges its own objection here

> **Superseded by Part 2 (2026-07-29):** the argument below was sound for a *PAT*
> stored at rest, but the credential became a GitHub App key sealed in the **TPM**
> (non-exportable), which beats DPAPI on the one axis DPAPI still lost — a DPAPI blob
> is decryptable by user-context malware, a TPM key is not. Read this section as the
> reasoning that led to the `ICredentialSource` seam, not as the storage that shipped.

LOCALMD.md rejects DPAPI on the grounds that it *"works for the user that encrypted
the secret. If you run a script under a different user account or as a service, the
DPAPI-encrypted value is unreadable."*

That objection is sound in general and does not bind this case. gscript runs
**interactively, as the operator, on the operator's machine** — the exact context
where DPAPI works. The objection describes service accounts and cross-user execution,
neither of which is the gscript push ceremony. (Recto's vault already relies on this
same property from the other direction: DPAPI-machine blobs are deliberately
undecryptable off-box, which is a feature there.)

Similarly, the vault objections (server, auth, network round-trip) do not apply to a
local Windows credential store: no daemon, no network, no session to expire.

## Proposal — a source seam, not a replacement

Keep localmd as the default. Add a seam so the value can live somewhere a session
cannot echo.

1. **`ICredentialSource`** with three implementations: `LocalMdSource` (today's
   behavior, unchanged default), `DpapiSource` / `CredManSource` (Windows credential
   store, read at run time), and `EnvSource` (last resort, documented as stale-prone).
2. **Resolution order** configurable in `gscript.json`: e.g.
   `"credentialSource": ["credman", "localmd"]` — try the store, fall back to the file.
   A repo that has migrated sets `["credman"]` only.
3. **`gscript cred set|test`** — a one-command write into the store and a read-back
   check, so migration is not a hand-rolled DPAPI incantation.
4. **Keep `githubPAT.md`** as the metadata record it is genuinely good at: scope,
   expiry, blast radius, rotation cadence, migration target. Delete only the `Value:`
   lines. That preserves everything the file is useful for and removes the only part
   that is dangerous to have in a watched folder.

## Rollout

Non-breaking by construction: the default resolution order keeps `localmd` first, so
every existing consumer behaves identically until its `gscript.json` opts in.

## The generalizable rule

**Any file an agent session can read is a file an agent session can disclose — whether
or not it reads it deliberately.** The vault-only posture already adopted for provider
keys (Anthropic, xAI, Stripe, Cloudflare …) was correct for exactly this reason. The
operator's own git credentials are the remaining exception, and they are the ones that
reach the deploy path: a PAT with Contents R+W triggers the self-hosted runner, which
builds and deploys to the fleet. See the operator's `githubPAT.md` §PUSHER blast-radius
correction — "code-push only, recoverable via reset+force" understates it, because
reset+force does not un-run what already deployed.

---

# Part 2 — GitHub App installation tokens (operator ruling, 2026-07-29)

**Status:** RATIFIED as the target credential; the reference build is complete on
the writer seats (operator-specific record — app id, install set, per-machine seal
dates — lives in the private hub, NOT this public tree). Only the gscript minting
code (Phases A–C below) remains. Part 1 (the `ICredentialSource` seam) is the
*plumbing*; Part 2 decides *what secret that seam holds and how long the thing on
the wire lives.*
Operator constraint, verbatim intent: **the source-writing credential must not live
in the shared secret store — a store that also writes source code is a
single-compromise supply-chain vector.** That rules out the Recto DPAPI vault AND
any synced file (`localmd/githubPAT.md`) for this credential class — and, per the
07-29 build decision, **DPAPI itself** (a DPAPI blob is decryptable by any process
running as the user, so a user-context compromise still walks away with the key).

## Why a PAT can't satisfy the constraint and an App key can

A PAT is a *standing* credential: whatever holds it can push until it expires or is
revoked, and it must be stored somewhere readable to be used. That is the exact
shape the constraint forbids. The `x-access-token:` URL form is **not** the
problem (the username is a placeholder; GitHub authenticates on the token) — and it
is in fact the *required* username for App installation tokens, so the push URL
shape does not change at all.

A GitHub App inverts the custody:

- The **standing secret is the App private key**, which does exactly one thing:
  sign a ≤10-minute App JWT. It is **least-privilege by construction** — the App is
  granted only `Contents: Read and write` on the named repos, so a leaked key
  cannot change settings, cannot admin, cannot delete a repo, cannot touch a repo
  the App is not installed on. (Contrast the `DEV_OPS` admin PAT, whose leak is
  catastrophic — which is precisely why DEV_OPS must NEVER enter the push path.)
- What touches the wire is a **1-hour installation token**, mint-on-demand, and the
  mint request can scope it further to the single target repo + `contents:write`.
- **No rotation treadmill and no chicken-and-egg.** There is no PAT expiry to
  hand-roll every few weeks; the multi-seat drift that strands a seat when a shared
  token rotates disappears because each seat mints its own token locally from a key
  it already holds — nothing is fetched over git to bootstrap git.
- **One-place revocation:** delete the App installation → all minting dies
  instantly, fleet-wide.

## Where the private key lives — TPM, non-exportable, per machine

**A non-exportable RSA key held in each writer seat's TPM** (Microsoft Platform
Crypto Provider). The key signs the App JWT on request but never leaves the chip as
bytes, so a machine compromise can *use* it while live but cannot *exfiltrate* it —
the exact gap DPAPI left open. A reader-only seat needs nothing.

**Per-machine keys, not one key copied around.** A GitHub App allows multiple active
private keys, so each seat gets its OWN key: generated locally, imported into that
box's TPM, plaintext deleted — **no source-write key ever crosses the network** and
each is revocable per machine. Each writer seat does this independently.

**Import mechanism (record, since it's fiddly):** GitHub issues the App key as a
PEM, which cannot be generated in-TPM, so it is *imported*: wrap the PEM in a
throwaway self-signed cert → PFX (openssl, or a pure-.NET `CertificateRequest` in
PowerShell 7 when openssl is absent) → `certutil -user -csp "Microsoft Platform
Crypto Provider" -importPFX My <pfx> NoExport` → verify `RSACng.SignData` returns and
`.Key.Provider` reads `Microsoft Platform Crypto Provider` → **only then** delete the
PEM + PFX + throwaway cert and `cipher /w`. Verify-before-delete is load-bearing; a
lost TPM key is recoverable only by regenerating a fresh App key.

**Lookup by subject, not thumbprint.** Each machine's key has a different thumbprint,
so the minter locates it by cert **subject `CN=<your-app-name>`** — one `gscript.json`
works identically on every seat, nothing per-machine to track. The two non-secret
identifiers — **App ID** and **Installation ID** — live in `gscript.json`.

## Residual risk, stated honestly

A machine-RCE on a writer seat could *use* the TPM key to mint a push token
**while it is live on that box** — the TPM stops key theft, not live misuse. It
cannot exfiltrate the key for later or off-box abuse (the DPAPI gap, now closed);
least-privilege stops any misuse becoming an admin compromise; App-installation
deletion is the fleet-wide kill switch. For a small writer-seat fleet this is
proportionate. **The only thing that removes even live misuse** is operator-presence
per mint (a QR/biometric analog-proof approval) — deliberately deferred: scope it to
*deploying* pushes if adopted, so `--no-deploy` docs stay frictionless.
**Higher-assurance future option (NOT this sprint):** centralize minting on one
orchestrator host so dev seats hold no key at all and request scoped tokens over the
private network. Named so the door stays open; not built now (adds a runtime
dependency: the authority must be up to push).

## Runtime mint flow (gscript, at push time)

1. Open the TPM signing key: load the cert from `Cert:\CurrentUser\My` by subject
   `CN=<your-app-name>`, take its `RSACng` private key (TPM-backed, non-exportable).
2. Build an App JWT: RS256, `iss` = App ID, `iat`/`exp` ≤ 10 min — signed by the TPM
   key (`RSACng.SignData`, SHA256 + Pkcs1). The key material never enters process
   memory as bytes.
3. `POST https://api.github.com/app/installations/{InstallationId}/access_tokens`
   with `Authorization: Bearer <app-jwt>`; optional body scopes it to
   `{ "repositories": ["<repo>"], "permissions": { "contents": "write" } }`.
4. Read the returned installation token (≈1 h TTL); inject as
   `https://x-access-token:<token>@github.com/...` — the existing URL shape,
   unchanged.
5. Token is ephemeral: never written to disk, never logged (extend the GitRunner
   `Redact` to cover it), discarded at process exit.

## Build sequence (gscript repo → NuGet; keep Program.cs version const in lockstep)

- **Phase A — the `ICredentialSource` seam.** `ICredentialSource` + a
  `TpmCertSource` (opens the `CN=<your-app-name>` cert's `RSACng` key from the user
  store; supersedes the DPAPI plan now that the key is TPM-resident) + `gscript cred
  test` (read-back/sign check — no `set`, since sealing is the certutil import done
  out-of-band). Non-breaking: default resolution order keeps `localmd` first until a
  repo opts in.
- **Phase B — GitHubApp provider.** Signs the App JWT with the TPM key, reads
  App/Installation IDs from `gscript.json`, mints the installation token, injects in
  URL. Opt in with `"credentialSource": ["githubapp"]`.
- **Phase C — fail clean, not modal.** Set `GIT_TERMINAL_PROMPT=0` +
  `credential.interactive=false` (and GCM `guiPrompt=false`) on gscript's git
  invocations, so a mint/auth failure surfaces a one-line error instead of the
  blocking dialog that hangs the terminal. This is the fix for the "dumb dialog"
  directly — it can even ship ahead of A/B as a standalone hardening. (Also decide:
  does the TPM key sign silently, or prompt per use? If it prompts, that's fine for
  interactive pushes but blocks unattended — reseal without the UI policy if needed.)
- **Then:** strip the `Value:` lines from `localmd/githubPAT.md`, keeping the
  metadata record (scope, expiry, blast radius, migration target) per Part 1 §4.

## Provisioning checklist (App owner does this; the key is never handled by an agent)

1. **Create a GitHub App** (Settings → Developer settings → GitHub Apps → New).
   Homepage URL can be any repo; **uncheck webhook Active** (else a Webhook URL is
   forced); no user-OAuth / device-flow. The App's bot is the recorded *pusher*;
   commit author/committer stays whatever git config sets.
2. **Permissions → Repository:** `Contents: Read and write` + `Metadata: Read`
   (mandatory/auto). Add `Workflows: Read and write` ONLY if the tool will push
   files under `.github/workflows/` — else leave off (least privilege). Nothing else.
3. **Generate a private key** (PEM, downloaded once) and note the **App ID**.
   **Install** the App on the target repos ("Only select repositories"); note the
   **Installation ID** from the install URL. Both IDs are non-secret → `gscript.json`.
4. **Seal the key into each writer seat's TPM** (per-machine key; see the import
   mechanism above), verify signing, then delete the PEM/PFX/cert and `cipher /w`.

**Re-key / new-seat runbook:** generate a NEW private key **on that machine**, run
the wrap→`certutil … NoExport`→verify→delete sequence with subject `CN=<your-app-name>`,
done — no key crosses the network, nothing to sync. Retire a machine by deleting its
key in the App settings. Per-machine keys need no backup: a lost TPM key is replaced
by generating a fresh one. **The PEM never enters a synced folder, a chat prompt, a
git working tree, or the shared vault** — born and destroyed locally.

Cross-refs: Part 1 above · `docs/LOCALMD.md` · operator `githubPAT.md` §PUSHER
blast-radius · `sandbox-perimeter-sprint.md` (authority-plane doctrine — the
centralized-mint future option is an orchestrator-plane concern).
