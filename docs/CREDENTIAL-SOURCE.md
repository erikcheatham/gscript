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
