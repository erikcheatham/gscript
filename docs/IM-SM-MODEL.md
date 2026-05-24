# IM + SM — the unified context model for AI-pair-programming sessions

Operator-side architectural framework for organizing project knowledge so an
AI agent can context-switch across many projects with the lowest possible
cold-start cost while still loading deep architectural detail on demand when
a question lands at a specific surface.

Lives in the `gscript` library because it's tooling-adjacent — the same
substrate that ships PowerShell + bash push wrappers also describes the
context model those scripts presuppose. Every AI session reading the
operator's repos benefits from this convention regardless of which project
the session is working on.

## The problem this solves

An operator with N active projects (AllThruit, Recto, Verso, AllThruitCoin,
dynamicquery, gscript, etc.) accumulates substantial architectural
knowledge per project: hard rules, gotchas, evolutionary history,
foundational primitives, sprint state, cross-cutting commitments. Two failure
modes recur in practice:

1. **Cold-start cost dominates.** If every AI session reads every artifact
   at startup, sessions take minutes to bootstrap before useful work begins.
   The operator's wall-clock budget gets spent on context loading.

2. **Per-file depth is invisible.** Single-file architectural rationale
   (decision history, parser-fragility traps, threading model, why this
   primitive is shaped THIS way) gets buried in 4000-line source files
   where it competes with the implementation noise. Future contributors
   see the WHAT, not the WHY.

These two pull in opposite directions. The unified model resolves the
tension by structuring knowledge into FOUR TIERS with different load
cadences.

## The four-tier model

```
┌─────────────────────────────────────────────────────────────────┐
│ Tier 0 — Machine gate                                           │
│   Path check at session start: which physical host am I on?     │
│   ALWAYS first action. Determines role + scope of authority.    │
└─────────────────────────────────────────────────────────────────┘
              │
              ▼
┌─────────────────────────────────────────────────────────────────┐
│ Tier 1 — Intelligence Model (IM)                                │
│   Per-project `CLAUDE.md`. Cross-cutting truth:                 │
│     • Hard rules                                                │
│     • Architectural commitments (platform-level invariants)     │
│     • Gotchas index (debugging lessons)                         │
│     • Active sprint pointer ("Pending dev-side follow-ups")     │
│     • Conventions (naming, identity, secrets storage)           │
│   ALWAYS loaded at session start. Single canonical statement.   │
└─────────────────────────────────────────────────────────────────┘
              │
              ▼
┌─────────────────────────────────────────────────────────────────┐
│ Tier 2 — Topic docs                                             │
│   `docs/architecture.md`, `docs/backlog.md`, `docs/changelog.md`,│
│   `docs/auth.md`, `docs/deployment.md`, etc.                    │
│   Cross-cutting BUT topic-scoped. Loaded BY INTENT:             │
│     • "Where is X architected?" → architecture.md               │
│     • "What's banked for next?" → backlog.md                    │
│     • "Did we ship Y?" → changelog.md                           │
│   Optional at session start; canonical when intent matches.     │
└─────────────────────────────────────────────────────────────────┘
              │
              ▼
┌─────────────────────────────────────────────────────────────────┐
│ Tier 3 — Sidecar Model (SM)                                     │
│   `docs/code-notes/<mirror-of-source-path>.md`                  │
│   Per-file substantive architectural prose:                     │
│     • Decision history with dates                               │
│     • Evolutionary arcs (v1 → v2 → v3 with rationale)           │
│     • Foundational primitive code-block examples                │
│     • Parser-fragility traps + recovery patterns                │
│     • Cross-references to sister surfaces                       │
│   ON-DEMAND. Loaded when touching a known-tricky surface.       │
│   Discoverable via pointer comments in source + INDEX file.     │
└─────────────────────────────────────────────────────────────────┘
              │
              ▼
┌─────────────────────────────────────────────────────────────────┐
│ Tier 4 — Source files                                           │
│   The implementation truth. `<summary>` one-liners only;        │
│   substantive prose lives in Tier 3 via pointer comments.       │
│   Loaded when editing.                                          │
└─────────────────────────────────────────────────────────────────┘
```

## Session-start ritual (the canonical fast path)

For the canonical fresh-conversation trigger ("You are MAC/HECATE/DARWIN
let's continue" or equivalent), the AI silently performs:

1. **Tier 0** — path check (`\Users\TERMINAL1\` → DARWIN; `/Users/eic/` →
   MAC; `\Users\eriki\` → HECATE; else ask).
2. **Tier 1** — load `CLAUDE.md` end-to-end.
3. **Tier 1.5** (if present) — load `~/private/local.md` for
   operator-specific overrides (PATs, git author identity, machine
   registry, secret rotation pointers).
4. **Tier 2** — optional skim of `docs/architecture.md` if the project
   carries one and it's small enough (<2k lines).

**DO NOT** pre-load Tier 3 sidecars. They're the on-demand depth surface;
loading them at startup defeats their purpose. Tier 3 loads when the AI
encounters a specific surface that has one.

After the ritual, the AI surfaces the active sprint state ("Pending
dev-side follow-ups" top entry in `CLAUDE.md`) and waits for the
operator's actual instruction.

## Per-surface ritual (when touching a known-tricky file)

When the AI is about to edit a file OR answer a question about a specific
surface:

1. **Run `notes.ps1 get <substring>`** (or equivalent) to discover whether
   the surface has a Tier 3 sidecar.
2. **If yes**: Read the sidecar FIRST. The sidecar carries the WHY +
   evolutionary history + parser-fragility traps that the source's pointer
   comments only gesture at.
3. **Then read source** (Tier 4) for the WHAT.
4. **Then edit** (Tier 4) with full context loaded.

This ordering is load-bearing — editing a 4000-line file without reading
its sidecar first often rederives an architectural decision the sidecar
already documented + sometimes contradicts a banked commitment without
realizing it.

## Knowledge-placement decision matrix

When new architectural knowledge accumulates during a session, the
operator-canonical home depends on the knowledge SHAPE:

| Knowledge kind                                     | Tier | Why                                            |
|----------------------------------------------------|------|------------------------------------------------|
| Cross-cutting rule (applies platform-wide)         | 1    | Single canonical statement; Hard Rule entry    |
| Architectural commitment (platform invariant)      | 1    | Single canonical list; numbered commitments    |
| Cross-cutting gotcha (debugging lesson)            | 1    | Discoverable via grep across one file          |
| Topic-scoped architecture (auth, deployment, etc.) | 2    | Bounded scope; single discoverable doc         |
| Sprint state + active work pointer                 | 1    | Continuity across sessions; pickup queue       |
| Per-file architectural rationale                   | 3    | Tied to a specific surface; discoverable here  |
| Per-file decision history with dates               | 3    | Tied to surface evolution                      |
| Per-file evolution arc (v1 → v2)                   | 3    | Tied to surface evolution                      |
| Cross-reference graph between surfaces             | 3    | Sidecar's own "Cross-references" section       |
| Code itself + brief inline `//` hints              | 4    | Implementation truth + IDE IntelliSense        |
| Operator-private detail (machine names, PATs)      | -    | `~/private/local.md` ONLY, never in repo tree  |

## Discoverability primitives

### Sidecar discovery: `notes.ps1 get <substring>`

If the project ships a `scripts/notes.ps1` helper (or equivalent), it
takes a substring of a source path and returns matching sidecar paths.
Convention: every project that adopts the SM should ship this helper at
its canonical scripts location.

Implementation sketch (per-project):

```powershell
# scripts/notes.ps1
param([string]$Verb = 'get', [string]$Substring)
$root = Join-Path $PSScriptRoot '..\docs\code-notes'
Get-ChildItem -Recurse $root -Filter *.md |
    Where-Object FullName -match $Substring |
    ForEach-Object { Resolve-Path -Relative $_.FullName }
```

### Sidecar discovery: pointer comments in source

Every source file with a sidecar carries pointer comments at the relevant
surface (class header, method, CSS block, Razor `@*..*@`):

```csharp
// see docs/code-notes/<mirror-path>.md
```

```razor
@* see docs/code-notes/<mirror-path>.md *@
```

```css
/* see docs/code-notes/<mirror-path>.md */
```

A future AI session editing the file sees the pointer and can navigate to
the sidecar with one Read tool call. The pointer IS the discovery
mechanism for AI sessions that don't run notes.ps1 explicitly.

### Sidecar discovery: INDEX file

Each project's `docs/code-notes/README.md` (or `INDEX.md`) lists every
sidecar with a 1-line scope summary. Lets a session-start scan find "is
there a sidecar for the file I'm about to touch?" in one Read call. Sister
of how `docs/architecture.md`'s table of contents serves Tier 2 discovery.

## The fundamental architectural insight

**The IM is the shallow always-loaded layer. The SM is the deep
on-demand layer. Together they describe a context surface that scales
to N projects without bottlenecking on cold-start cost.**

A fresh AI session given the IM + a question about a specific surface
will:
1. Load the IM (fast — single file)
2. Run sidecar discovery for the surface (fast — one shell call)
3. Read the relevant sidecar (deep — substantive architectural prose)
4. Answer the question with full context

The total time-to-useful-answer scales with QUERY DEPTH, not with
PROJECT SIZE. Adding a 10th project to the operator's portfolio doesn't
slow down sessions on any of the prior 9 — each project's IM is
independent, each project's sidecars are independent, and the AI loads
only what the current question requires.

## The complement that makes it work: Hard Rule #32

Sidecars are only useful if they actually carry the substantive prose.
That means source files MUST migrate their multi-paragraph rationale OUT
of inline comments and INTO the sidecars, with one-line pointer comments
remaining in source as discovery markers.

The migration discipline (per Hard Rule #32 in any project's `CLAUDE.md`
that adopts the SM):

- Source files carry `<summary>` one-liners + brief `//` markers only.
- Multi-paragraph rationale, decision history, foundational primitive
  examples, cross-references — ALL move to sidecar.
- CSS comments inside `<style>` blocks that contain `<` / `>` / Unicode
  arrows / literal HTML element-names MIGRATE to sidecar (the canonical
  Razor-parser-fragility trap retires by construction).
- C# verbatim-string-quote-break trap (single `"` inside `@"..."` prose
  comments) retires for the same reason.
- Sidecars use full markdown — fenced code blocks, cross-reference
  links, tables, embedded examples — without parser constraints.

## Verification discipline after a sidecar migration

After migrating N source files to sidecars (typical sweep operation), the
canonical verification sequence is:

1. **Count pointer comments in each source via Read/Grep tool**, NOT bash
   grep. Bash mount can serve FUSE-stale views of files freshly edited
   via the AI's Edit tool. See `gscript/docs/GOTCHAS.md` entry 8.
2. **Confirm every sidecar exists on disk via Windows-truth Glob**.
3. **Verify file end-cleanly** (`tail -1` should be `}` or `</tag>` or
   matching close).
4. **Razor parser fragility scan**: grep each migrated Razor file for
   CSS comments containing `<` / `>` / Unicode arrows. None should remain.
5. **Spot-check business-logic untouched** by reading a few key methods
   pre + post — the migration should change ONLY comments.

This becomes the canonical "second-pass review before gscript ship" for
any large-scale sidecar migration sweep.

## Cross-references

- **Hard Rule #32** (sidecar-prose-everywhere) — the migration discipline
  this model presupposes. Every project that adopts SM banks Hard Rule
  #32 in its `CLAUDE.md`.
- **`gscript/docs/GOTCHAS.md` entry 8** — post-migration verification
  trap (bash mount lies; use Read/Grep tools).
- **`gscript_template.ps1`'s `Test-StructuredFile` `.ps1` parse branch**
  — the canonical defense that the sidecar-pointer-only source still
  parses correctly post-migration.
- **AllThruit `CLAUDE.md` Hard Rule #32** — first canonical reference
  implementation of the SM at scale (24+ sidecars covering ~10,000 lines
  of source).
- **`scripts/notes.ps1`** convention — recommended per-project helper.

## History

This framework crystallized 2026-05-24 during an empirical test: two AI
conversations were asked the same question ("give me a full breakdown of
the application's logic based on the sidecars"). One had ~24 sidecars
already authored in working memory; the other started fresh and had to
read all sidecars via Read tool.

The fresh conversation produced a RICHER per-file architectural answer
because it was forced to load the depth. The session-state-rich
conversation produced a richer cross-cutting synthesis because it had
the migration arc + active sprint state in working memory.

The two outputs were complementary, not redundant. The IM + SM tiers
formalize what made each output strong: the IM carries the cross-cutting
synthesis well; the SM carries per-file depth well. Each tier loads on
its own cadence; together they describe a context surface that scales.
