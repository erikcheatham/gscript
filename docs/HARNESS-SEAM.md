# The harness seam — gscript as the vocabulary, not the empire (design brief v0.1, 2026-08-07)

**Status:** OPEN. Extends `docs/CREDENTIAL-SOURCE.md` the way Part 2 extended
Part 1 — same method: name the seam, keep the default behaviour, let repos
opt in.

## The observation

gscript already implements a capability harness. It was built one incident at
a time and never called that, but the primitives are all here:

| harness concept | what gscript already ships |
|---|---|
| **an ordered LANE TABLE, first-available, announced at use** | `credentialSource` — declared per repo, resolved at run time, and (since the lanes learned to announce themselves) it says which one it took |
| **a PROBE that resolves what this seat can actually do** | `cred test` — reports the configured order, the key's provider, that a signature round-trips, that the private key refuses export; `--mint` proves the whole chain live |
| **an INVARIANT REGISTRY enforced mechanically** | the gate pipeline — trailing-null, size/shrink, structured-file, markdown, leak-check. Every gate earned by a real incident; fail-closed and loud |
| **a CONTRACT LINTER over a declared structure** | `im lint` — budget, stale-path scan, cross-ref resolution; exit 1 gates a ceremony |
| **a tier-B AUTHORITY LOOP (propose → approve → execute → audit)** | the task bus — `post → approve → run`, with the result and CI status written BACK into the record |

The last row is the one that matters most, and it was hiding in plain sight.

## The identification

**A bus record and an approval card are the same object at different
fidelities.** Both are: a scoped request, naming a target, awaiting a
deliberate human approval, producing an audited result. One is JSON on a
disk; the other is a card in a pocket.

The record schema already models the whole loop — its `history` array carries
`created → approved → started → result`, each with an actor and a timestamp.
**`approved` is already an event.** So delivering records to a phone does not
require a new system, a new schema, or a new authority model. It requires one
change: *a second place the `approved` event can originate.*

That is the whole design. Everything below is consequence.

## The three moves

**1. `credentialSource` becomes one instance of `lanes`.**
Today the resolver answers exactly one question ("what token pushes this
repo?"). The same ordered, announcing, first-available machinery answers any
capability. Key it by capability; `repo.push` keeps resolving exactly as it
does now, so nothing existing changes behaviour.

**2. The record becomes the universal capability-request schema.**
It already is one — it just only knows about pushes. Generalise `target` from
`{repo, files, message}` to `{capability, target, params}` with the push shape
as the first and default capability. Existing records stay valid.

**3. `gscript harness probe` + order-header validation.**
The probe answers "what can this seat do *here*" in structured form. A work
order declares the capabilities it needs; the executing seat validates that
header against its own probe result **before step one** and refuses at write
time with a clean handoff, instead of blocking three phases in. (That failure
happened on 2026-08-07: an order was written by a seat holding network reach
the executing seat did not have. The executing seat behaved correctly. The
order was the defect. This is that defect fixed in the tool rather than in
prose.)

## Delivering records to the phone

**What moves: the notification and the decision. Not the work.**

    post (seat)  →  relay  →  card on the device  →  signed approval  →  relay
                                                            ↓
                                        runner on the machine executes, writes result

Design constraints, each with its reason:

- **Approve and run stay separate events.** They already are. The phone
  approves; the machine that holds the working tree and the sealed key runs.
  A mis-tap can never execute, and authority never leaves the device that
  holds it.
- **An approval is SIGNED, not a boolean.** If approval is a flag in a
  relay message, anything that can write to the relay can approve. The
  approval must be a capability the device mints, verified by the runner
  against the same verify-side machinery used elsewhere — reuse it, do not
  write a second verifier.
- **The card renders from the RECORD, in trusted chrome.** Repo, capability,
  target, file list, gate flags. The record's free text (`description`,
  `message`) is author-supplied and must be visibly subordinate — quoted,
  never in a position where it can impersonate the card's own labels. A
  request rendered on a phone is a phishing surface aimed at a thumb.
- **The relay is a lane, and the terminal is the fallback lane.** If the
  device is unreachable, `gscript task approve` at a terminal still works,
  announces that it took the local lane, and the loop is unchanged. No new
  single point of failure.
- **Nothing about the payload transits the device.** Diffs, files and
  credentials stay on the machine. The device sees a summary and returns a
  decision.

**What this buys, concretely:** the operator stops re-typing task ids into a
terminal to approve work they have already read. Approval moves to the device
they already carry, where it is one deliberate tap on a rendered card — and
the audit trail improves, because the approval event now carries a signature
instead of "whoever had the shell."

## Batching, and why the seat stops blocking

Two consequences the operator named, both worth designing for explicitly:

**The seat stops waiting.** Posting is already non-blocking — a seat writes a
record and continues. What blocks today is the *approval*, because it needs a
terminal the operator is sitting at. Moving approval to the device removes the
last synchronous point: work queues, the operator clears it when convenient,
and neither side idles on the other.

**Approval becomes a batch gesture** — and this is the part that needs a rule,
because batch approval is exactly how deliberate consent decays into
rubber-stamping. The mitigation is the same shape used everywhere else here:
**batch by CLASS, break out by RISK.**

- A batch may cover records that are **uniform on every risk-bearing
  dimension**: same repo, same visibility, docs-only, no-deploy, no new
  capability, all gates green on pre-flight.
- Any record that **differs on a risk-bearing dimension breaks out of the
  batch and is approved individually.** Code behaviour rather than docs. A
  deploy rather than `--no-deploy`. A public tree. A capability the requester
  does not already hold. A gate override such as a shrink allowance.
- The card **states what is uniform and shows what differs.** "7 records ·
  same repo · docs-only · no-deploy · gates green" is a decision a person can
  actually make. A list of seven opaque ids is not — it is a prompt to tap
  through.
- **One signature per batch is acceptable; one signature for a mixed batch is
  not.** The signed approval names exactly which record ids it covers, so the
  audit trail never says "approved everything that was pending."

The principle underneath: batching is safe where the records are
*interchangeable* with respect to consequence. The moment they are not, the
convenience is buying exactly the attention the approval exists to spend.

## Scope discipline (read this before adding anything)

**gscript grows a SEAM, not an empire.** Its value is that it is small,
dependency-free, and every gate in it was earned by a real failure. "The
framework for everything" is how good tools die. The test for any addition:
*does this generalise a mechanism that already exists, or does it add a
mechanism?* Move 1 generalises. Move 2 generalises. Move 3 adds one verb.

**Vocabulary is public; topology is not.** This repo is public. The capability
names, the record schema, the resolver and the probe belong here. Lane tables,
endpoints, identifiers and machine inventories stay in the operator's private
configuration, as `githubApp` ids already do.

**A vocabulary, not a DSL.** Verbs and a record schema are cheap and
defensible. Syntax is a decade-long commitment. Do not invent a language.

**No dependency may enter the credential or approval path.** Same argument as
the hand-rolled App JWT: a supply-chain edge on the component holding
source-writing authority is exactly the risk this design exists to shrink.

## Owed

- Decide the relay: reuse the existing comms path rather than adding one.
- Approval-capability shape (audience, TTL, single-use, replay guard) —
  it should be the same shape already used for other capabilities, not a
  sibling.
- Record-schema v2 with the push shape as default; a migration that leaves
  existing records valid and readable.
- `harness probe` output format, and the order-header grammar it validates.
- **Sequencing note:** none of this ships before the record schema is settled.
  A relay built against a schema that then changes is rework, and the schema
  is the cheap part.

Cross-refs: `docs/CREDENTIAL-SOURCE.md` (the resolver this generalises) ·
`docs/LOCALMD.md` (why configuration lives where it lives).
