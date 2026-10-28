# ADR-0021: Baselines and effectivity

**Status:** Accepted · 2026-10-28 · Refines [ADR-0003](0003-bom-usage-links-and-config-specs.md) and [ADR-0017](0017-bom-structure-and-explosion.md); builds on [ADR-0018](0018-workflow-and-esignature.md) and [ADR-0019](0019-change-management.md)

## Context
Phase 2.1 delivers EIA-649C configuration management: **baselines** (frozen, numbered snapshots of an exploded structure, established at program milestones) and **effectivity** (A19: serial number first, then lot/block, then date). ADR-0003 reserved the config specs `Baseline(id)` and `Effectivity(...)`; ADR-0017 built the explosion, redaction and cycle rules they must keep. This ADR records the decisions the implementation needed. Assumption numbers are `CFG-n`, listed in [2.1-configuration.md](../increments/2.1-configuration.md); the rules they name are stated in full here.

## Decision

### Where it lives
- Baselines and effectivity are **inside `Keel.Bom`** (new files; schema `cfg`, migration `0008_configuration.sql`), not a new module. A baseline is a persisted explosion: it needs the explosion's recursive SQL, the redaction machinery, the marking cache and the part-master joins, all of which are internal to Bom. A separate module would either duplicate them or reach into Bom's internals, which ADR-0001 forbids. `Keel.Documents` and `Keel.Change` are reached only through interfaces in `Keel.Core` (`IDocumentPinProvider`, `IRevisionEffectivity`).
- `Keel.Bom` keeps referencing only Core, Security and Audit. The new tables are `cfg.*`; the architecture tests extend the filter rules (`cfg.baseline` lists, `cfg.baseline_item` reads).

### Effectivity model (A19)
- An **end-item context** (`cfg.effectivity_context`, e.g. `ORCA-POD`) gives serial numbers and lots a meaning. Contexts are data, immutable, created by anyone who may modify data, and a context tied to a **program** is visible only to holders of that program (the program name is itself a marking). Named **blocks** (`cfg.effectivity_block`: "Block 2" = 2) map names to lot numbers, so every range is a plain integer range.
- A range is `(kind, context, from, to)`: `serial` and `lot` are positive integers (1 to 9,999,999,999) in a context; `date` is a UTC day stored as the day number since 1970-01-01 (no context; the API and UI speak `yyyy-MM-dd`). One end may be open; a range open on both ends is "no effectivity" and stored as NULLs. A CHECK constraint (`cfg.effectivity_ok`) enforces the shape.
- **Lines**: `plm.bom_usage` and `plm.bom_working_usage` carry `eff_kind, eff_context, eff_from, eff_to`. They belong to the iteration like every other usage column: written through the working copy, copied forward by the `IPartIterationParticipant` hooks (check-out, check-in, revise), immutable once checked in (the existing trigger), part of the content fingerprint that change signatures cover. A line without effectivity always applies.
- **Alternatives**: lines that share a **find number** are alternatives (find 30 is part X for serials 1-100 and part Y from 101). They must not overlap: two lines conflict when either has no effectivity, when their dimension or context differs (undecidable), or when their ranges share a value. This is enforced in the editor with a message naming the reason, and in the database (a BEFORE INSERT trigger on `bom_usage`, a deferrable end-of-statement constraint trigger on the working copy so a renumber can swap numbers in one statement). `UNIQUE (parent_iteration_id, find_number)` became `UNIQUE (parent_iteration_id, find_number, alt)`: `alt` is the ordinal among the alternatives (0 for the first), assigned by the trigger.
- **Line paths** therefore gained an alternate suffix: `30` is the first line of find number 30, `30~1` the second. Paths stay the lazy-expansion address (ADR-0017); `BomRules.TryParsePath` reads them and a path without alternates is unchanged.
- Reference designators may repeat across alternatives whose ranges are provably disjoint; everything else stays unique across the parent (BOM-3). Renumbering uses `dense_rank`, so alternatives keep sharing their find number.

### The config spec `Effectivity(context, serial | lot | date)`
- The request names a context and any of serial, lot, date (`EffectivityRequest`). At each level the recursion keeps the usage lines that **apply** (no effectivity, or a range of a dimension the request gave that covers the value; a line whose dimension the request did not give is left out, CFG-5), and resolves each child master to the **Released revision whose revision effectivity covers the unit**, else the highest Released revision (the default). Obsolete revisions are never resolved: an Obsolete revision's range is no longer needed by definition. The root part is resolved the same way unless a revision is named. This is one more `LATERAL` in the existing recursive CTE; no new pass.
- **Where-used with a unit** lists only parents that contain the part through a line that applies and that are the revision the unit resolves to. **Compare units** explodes both units and diffs them (below). CSV export, summary and the page banner follow unchanged.
- Redacted lines still show their effectivity range: it is applicability, not identity, and without it two alternatives behind placeholders could not be told apart (CFG-6).

### Revision effectivity driven by a change notice
- An ECN's affected object (action `revise`) may carry "the new revision is effective **from serial N onward**" (`cfg.change_effectivity`, one row per affected object, open at the top by constraint, editable only while the notice is a draft). It is set through `ChangeService` (access, draft check, history, audit) and stored by `Keel.Bom` behind `IRevisionEffectivity`; the submission snapshot freezes it, so the signatures cover it.
- **The rule at release** (CFG-9), applied in the release transaction right after the resulting revision becomes Released, replacing the unconditional "the affected revision becomes Obsolete":
  1. The resulting revision B is recorded with its range `[N, ∞)` (`cfg.revision_effectivity`, with an append-only history table).
  2. Every **other Released revision** of the master is cut back to the units below N. A revision with no stored range serves "everything" and is closed to `[…, N-1]`; one with a range keeps its start and its end becomes `min(end, N-1)`.
  3. A revision that then serves nothing (it started at or above N, or N is the first unit there is: serial or lot 1) becomes **Obsolete** through the lifecycle's `change` transition. A revision that still serves units stays **Released**, with its closed range recorded.
  4. Without an effectivity on the affected object, every other Released revision is superseded completely and becomes Obsolete: exactly the Phase 1 behaviour.
  5. A revision's effectivity must be open at the top (units above would have no revision) and use the same type and context as the ranges already stored on the master's Released revisions; both are checked when it is set, at submission and at release. The release fails with a message instead of guessing.
  - A gap (A serves 1-100, B starts at 150) is allowed; units in the gap resolve to the default, the highest Released revision. A revision that is no longer needed is retired by a later notice or the admin override; the stored range stays as history.
- Release history and audit record which revisions were obsoleted and which stayed Released for their earlier units.

### Baselines
- A baseline (`cfg.baseline`, number `BL-000001`) is created from a **root part**, a config spec (`Latest`, `LatestReleased`, or `Effectivity` for one unit: an as-built baseline), optional **released-only** mode (every item, root included, must be Released) and optional pinned documents. It explodes the structure **at creation** with the creator's read filter and stores one row per occurrence in `cfg.baseline_item` (root = seq 0; part master, revision, iteration, path, level, parent, quantity, unit, extended quantity, find number, designators, notes, the line's effectivity). Items are bulk-loaded (binary COPY) in the creating transaction. The result is reproducible however the structure evolves.
- A baseline is **immutable**: `UPDATE`/`DELETE`/`TRUNCATE` on items and documents are rejected by trigger (and not granted to the application role), inserts are accepted only while the baseline is a Draft, and the header's content, root, spec, counts, hash and markings never change. Name, purpose and milestone change only in Draft. **Content hash**: SHA-256 over the canonical item and document text, computed at creation and recomputed on demand (`VerifyAsync`), at submission and at establishment.
- **Lifecycle as data** (`baseline-default`): Draft → UnderReview → Established → Superseded / Archived. Draft → Established (manual) and Draft → Archived are manual transitions; UnderReview transitions are the workflow's. Establishment needs a **reason**; supersession links the successor; nothing is deleted.
- **Types are data** (`cfg.baseline_type`): Design (no sign-off required), Release and Product (sign-off required). A baseline whose type requires sign-off, or that was created with "require sign-off", can only be established through the review workflow with e-signatures; a trigger rejects Draft → Established for such baselines even by SQL. Directly established baselines (Design) need Modify (authors and administrators) and a reason (CFG-3). A type that requires sign-off cannot be created without it.
- **Markings** (CFG-4): the baseline is a marked object. It starts from the root's markings (or those the caller gives); `MarkingDominance.Gaps/MinimalCover` over every item, the root and every pinned document decides whether they cover; if not, the creation is refused with the minimal cover, and only an explicit confirmation raises the markings (as for change objects). The creator needs **Read on every item and document**: when they cannot read some, creation is refused with a **count only**; unresolved or unreleased items are named, because the creator can read them. The final markings must not lock the creator out.
- **Reading**: `ExplodeAsync(Spec = Baseline)` reads the stored items through the caller's filter: identifying columns of items the caller can no longer read are NULL (placeholder: quantity, unit, find number, level, path), and the subtree below a restricted item is dropped, exactly like the live explosion. Dominance means a reader of the baseline can read its items as of creation; redaction appears when an item's markings are raised later or on a different reader's attributes. Export (CSV), summary, lazy expansion by path and the page banner (aggregate of the baseline and the displayed items) work as for live structures.
- **Documents** (`IDocumentPinProvider`, implemented in `Keel.Documents`): a `describes` link keeps its pinned revision, a `references` link is pinned to the latest Released revision (else the latest) at creation; items that share an iteration pin its documents once.
- **Establishment by signatures**: `BaselineWorkflowGateway` (object type `baseline`) lets the 1.5 engine review a baseline. The engine's revision and iteration are the baseline itself (it is immutable); the signed content is a readable summary plus the content hash **recomputed from the live rows**, so editing an item after signing makes every signature verify as changed. Submission and the final signature both refuse a baseline whose stored rows no longer match its hash. The seeded template `baseline-establish-review` is ordinary workflow data (reviewer, then all approvers); every signature needs step-up.

### Comparison
`BomDeepDiff` compares two flat structures (baseline against baseline, a baseline against the current Latest or LatestReleased structure of its root, one unit against another). A line is identified by the chain of parts from the root, plus an occurrence index for the same part under one parent; it reports added, removed and changed (revision, iteration, quantity, unit, find number, effectivity, designators, notes). A part that moves to another parent is one removal and one addition. **Restricted lines** match only the same hidden part under the same parent; a revision change of a restricted part is reported as "Restricted item changed" and nothing else, which discloses no more than the existing single-level compare already does (CFG-8). Results export to CSV with the banner first and last.

### Change module edits (kept minimal)
Interfaces in `Keel.Core` (`IRevisionEffectivity`, `IDocumentPinProvider`, `EffectivityRange`), implemented in new files of `Keel.Bom` and `Keel.Documents`. In `Keel.Change`: `AddAffectedCommand`/`AffectedObjectView` gained an optional effectivity field, `ChangeService` gained an optional dependency and `SetAffectedEffectivityAsync`, the submission snapshot carries the effectivity, and `ChangeWorkflowGateway` applies the supersession rule at release. Everything else is unchanged and every existing test passes.

## Performance (measured)
`ConfigurationPerformanceTests`, Docker Desktop Postgres 17 on an Apple-silicon laptop, the 50,000-line, 10-level structure with every revision Released, a quarter of the lines carrying serial ranges and a tenth of the revisions carrying revision effectivity:

| Case | Result |
|---|---|
| As-of-serial explosion, 34,624 of 50,000 lines apply | about 0.66 s (budget 2 s); equals an in-memory walk of the stored rows |
| Explosion without effectivity (existing gate, after the path change) | 50,000 lines in about 0.72 s |
| Where-used of the hottest shared part for one serial | about 11 ms |
| Compare serial 500 with serial 3000 (two explosions and a diff) | about 0.95 s |
| Create a baseline of 50,001 items (explosion, COPY, hash) | about 2.6 s |
| Explosion from the baseline / its CSV | about 0.30 s / 0.31 s |
| Baseline against the current structure (two 50,000-line sides) | about 1.4 s |

No index was needed beyond the covering indexes of 0005; the extra `LATERAL` runs against `(master_id, lifecycle_state)` and the tiny `cfg.revision_effectivity` table.

## Consequences
- Alternatives share a find number, so the find number alone no longer identifies a line; the path suffix `~n` does. Anything that stored a path (nothing persisted before this ADR; baseline items store theirs) keeps working because alternate 0 has no suffix.
- A revision with a stored range is stored once per revision, not per iteration: ranges describe revisions, which are the unit of release.
- Baselines of very large structures are bounded by the explosion cap (250,000 items).
- The review of a baseline reuses the workflow dialogs; their wording ("the part is locked") is generic text and is not specialised for baselines.

## Alternatives
- **A separate `Keel.Baseline` module:** rejected, see "Where it lives".
- **Effectivity as a separate table of ranges per line:** a line has at most one range in this model; alternatives with different ranges are separate lines, which keeps the structure a plain list per iteration, copy-forward trivial and the explosion join unchanged.
- **Date stored as `date` columns:** clearer in SQL, but it would double every range column and every comparison; day numbers keep one range type for all three dimensions and are converted only at the boundary.
- **Obsoleting the revised revision always, effectivity only advisory:** simpler but wrong for units already built or still being built to revision A; rejected.
- **Materializing baselines as references to iterations only:** would not reproduce paths, quantities and find numbers when an iteration is later superseded within its revision; storing the occurrences is what makes the snapshot self-contained.
