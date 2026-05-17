# PAT setup

`gscript` authenticates to GitHub via a fine-grained personal access token (PAT) loaded from `~/private/local.md` at run time. This document explains how to mint a correctly-scoped PAT and what each permission is for.

## TL;DR

Mint a fine-grained PAT at https://github.com/settings/personal-access-tokens with these scopes:

| Permission | Access | Why |
|---|---|---|
| **Repository: Contents** | Read and write | Read the repo state (`git fetch`); write commits (`git push`) |
| **Repository: Actions** | Read | Poll workflow run statuses for the CI watch |
| **Repository: Workflows** | Read and write | Lets you push commits that modify `.github/workflows/*.yml` files (without this, the push fails when ANY workflow file changes — even if the change is unrelated to CI) |
| **Repository: Metadata** | Read-only | Required (GitHub auto-adds this; you can't deselect it) |

That's the **minimum** set. The rest of the canonical AllThruit-grade scoping adds:

| Permission | Access | Why |
|---|---|---|
| **Repository: Pull requests** | Read and write | Future v1.1 enhancement: open auto-PRs instead of pushing direct to main |
| **Repository: Deployments** | Read and write | Future v1.1 enhancement: report deploy status from the post-deploy probe back to GitHub's Deployments tab |
| **Repository: Environments** | Read and write | Sister of Deployments — read environment protection rules + report status |
| **Repository: Secrets** | Read and write | Future v1.1 enhancement: rotate per-environment secrets as part of the gscript ceremony (with operator approval) |
| **Repository: Variables** | Read and write | Sister of Secrets for repo/environment variables |
| **Repository: Webhooks** | Read and write | Future v1.1 enhancement: provision/audit webhooks as part of repo-setup ceremony |
| **Repository: Security advisories** | Read and write | Sister capability; surfaces Dependabot alerts in the post-deploy probe |

## The "Actions" vs "Workflows" distinction (read this carefully)

This trips operators up consistently because the GitHub UI puts them next to each other but they mean different things:

- **Workflows: Read and write** — lets the PAT push commits that modify `.github/workflows/*.yml` files. Without this, ANY push that touches a workflow file fails with a permission error, even if the operator never intended to "use" CI permissions. Almost every project needs this once for the initial CI setup commit.
- **Actions: Read** — lets the PAT read workflow RUN statuses (the things the CI watch polls for). Without this, the CI-watch loop 403s every poll. The PAT can still push code; it just can't see the runs that result.

You need BOTH for the full ceremony. Adding only Workflows (a common first-mint mistake) means pushes work but the CI watch is stuck in 403 retry loop.

**Live-flip note:** if you've already minted a PAT and forgot Actions: Read, you do NOT need to regenerate the token. Go to the PAT's edit page, add Actions: Read, click Update. GitHub recognizes the new permissions on the very NEXT API call. No script restart, no PAT rotation, no localmd update — same token value just has more scope server-side.

## Token expiration policy

Fine-grained PATs expire (max 1 year, with options at 7/30/60/90 days and custom). The trade-off:

- **Shorter (30-90 days)**: tighter blast radius if leaked; forces an audit on every rotation. Recommended for daily-use PATs.
- **Longer (1 year)**: less rotation ceremony. Recommended for low-blast-radius scopes only.

For gscript usage, **90 days is the sweet spot** — enough headroom to not rotate every week, short enough that a leaked token has a known expiration. Mint with a calendar reminder for the rotation.

## Per-namespace scoping

Fine-grained PATs are scoped to a SINGLE GitHub account namespace (your user account OR an organization). If you push to repos under multiple accounts (e.g. `your-personal-account/Recto` AND `your-organization/CompanyProject`), you need TWO PATs — one per namespace. Document the mapping in localmd so gscripts can pick the right one.

## Storage

Per the localmd convention (see [LOCALMD.md](LOCALMD.md)), the PAT lives in a single file at `~/private/local.md`. The gscript regex matches `github_pat_[A-Za-z0-9_]{40,}` and picks the first match. If you have multiple PATs in localmd (for separate namespaces, historical references, etc.), either:

1. Make sure the FIRST one is the right one for the gscript's target repo (simplest), OR
2. Override the regex in the per-sprint gscript to anchor on a specific section heading.

## When to rotate

- **Calendar trigger**: 7 days before PAT expiration. Don't wait for the gscript to fail.
- **Incident trigger**: if the PAT was pasted into a chat transcript, a Slack message, a screenshot, a shared screen — rotate immediately. Treat the token as compromised even if you "think" only trusted parties saw it.
- **Scope change**: if you're adding permissions (e.g. Actions: Read for the first time), you can EDIT the existing PAT in place — no rotation needed. GitHub picks up the new scope live.

## Rotation ceremony

1. Mint the new PAT at https://github.com/settings/personal-access-tokens with the same scopes as the old one.
2. Copy the new value (only shown once).
3. Open `~/private/local.md` and replace the old `github_pat_...` value with the new one.
4. (Optional) Delete the old PAT at https://github.com/settings/personal-access-tokens to revoke it server-side. If you skip this, the old PAT remains valid until its natural expiration — which is fine if you're rotating proactively, NOT fine if you're rotating because of a leak.

That's it. The next gscript run picks up the new value from localmd; no env-var dance, no shell restart, no `.git/config` cleanup.
