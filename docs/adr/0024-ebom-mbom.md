# ADR-0024: EBOM and MBOM as linked structures, and reconciliation

**Status:** Accepted · 2026-10-28 · Builds on [ADR-0003](0003-bom-usage-links-and-config-specs.md), [ADR-0017](0017-bom-structure-and-explosion.md), [ADR-0018](0018-workflow-and-esignature.md), [ADR-0021](0021-baselines-and-effectivity.md)

## Context
The product requirement is "EBOM and MBOM as distinct but linked structures with reconciliation". Assumption A18: routings and operations belong to ERP/MES, this system owns the MBOM *structure* only. A19: effectivity applies. Manufacturing engineering must be able to adapt the structure for a plant (quantities, scrap, substitutes, consumables, phantom items) without a design change, yet must be told when the engineering structure moves, and a release must prove that the two agree.

## Decision

### An MBOM is its own versioned object per (part master, plant)
- `mbom.mbom` is the header: number `MB-nnnnnn` (numbering is administrator data like every other type), the part master, the plant, markings, lifecycle id. `UNIQUE (part_master_id, plant)`; only Assembly parts have one. `mbom.revision` / `mbom.iteration` follow the part pattern (ASME revision letters, iterations inside a revision, immutable once superseded). Lines are `mbom.usage` rows owned by one **iteration**; the editable copy is `mbom.working_usage` tied to the check-out (`mbom.checkout`), exactly the part pattern. Undo discards the working copy by cascade; check-in writes the next iteration through one write path (`MbomStore.WriteIterationAsync`) that also serves generate, regenerate and revise.
- The MBOM is revised **independently** of the design revision (`Revise` on a Released MBOM starts revision B in work; releasing B makes A Obsolete). It has no ECN binding: manufacturing engineering owns it. What it does record is `derived_part_iteration_id`, the EBOM part iteration it was derived from, on every iteration and on the check-out.
- Lifecycle `mbom-default` (In Work, Under Review, Released, Obsolete) is data in `plm.lifecycle*`; review states are taken only through the workflow (`workflow` transitions), Obsolete is manual with a reason. The family is view-only in the administration UI (its transitions are enforced by database triggers and the gateway, like the baseline family); making it editable needs `mbom` added to the versioned object types of `Keel.Admin`.
- **Markings** of the MBOM dominate the part's markings (`MarkingDominance`): creation refuses a cover gap unless the caller confirms the minimal cover; a markings change must still cover the part. The lines are not covered by dominance (a child can be more restrictive than its parent), so lines of unreadable parts are redacted placeholders (A4), exactly as in the EBOM. Reading an MBOM needs Read on the MBOM **and** its part.
- **Plants** are `mbom.plant` (code, name, active, `release_allows_warnings`), administrator data with its own table because each plant carries a release policy that `cfg.ref_item` cannot hold. Supply types are `mbom.supply_type` (Make, Buy, Consume are system codes the reconciliation refers to and cannot be retired). MBOM-only items (adhesives, packaging, tooling kits) are ordinary parts of the seeded part type `Consumable`, flagged `mbom_only` on the line.

### Line model
Child part master, quantity, unit, find number (+ alternate ordinal for effectivity alternatives, same rules and triggers as the EBOM), reference designators (not tied to the quantity), scrap factor, phantom flag, supply type, operation reference (free text key of the ERP/MES routing operation, 60 characters, not modelled further: A18), MBOM-only flag, notes, effectivity (the `cfg` ranges and validation of ADR-0021).

**Quantity convention.** `quantity` is the **net** quantity per assembly, in the line's own unit (units are never converted, as in ADR-0017). `scrap_factor` *s* is planned loss as a fraction (0.05 means 5 percent): the requirement is `quantity * (1 + s)`. The explosion rolls both up per level (`ext` net, `gross` compounding the scrap of every level above, because more of the parent must be made).

### EBOM links
`mbom.usage_link (mbom_usage_id, ebom_usage_id, status, reason)` is the equivalence: 1:1, split (several MBOM lines to one EBOM line) and merge (one MBOM line to several EBOM lines) are all just rows. EBOM usage rows are immutable, so a link stays meaningful as the EBOM moves on. Status: `matched` (same part, same quantity, no reason), `quantity_adjusted` (declared difference, reason required), `substituted` (a different part is built, reason required). A line with no link is manufacturing-only and must carry the `mbom_only` flag (a link needs two ends, so "manufacturing-only" is a line flag, not a link status); an MBOM-only line cannot have links. Links are checked when set and again at check-in: the EBOM line must belong to the derivation iteration, matched means same part. Messages name find numbers only.

### Generate and regenerate
- **Generate** from an EBOM selection (`Latest`, `LatestReleased`, `Effectivity` unit, `Baseline` number, explicit iteration `B.1`) creates the MBOM with every EBOM line copied, 1:1 matched links and a default supply type from the child (Consumable consumes, Buy buys, anything else is made). Multi-level also generates an MBOM for every Make sub-assembly (type Assembly, source Make) of the same plant; an existing MBOM is kept and reported; Buy items stay leaves. One transaction; refused when the EBOM contains parts the caller cannot read (like baselines and copy-structure).
- **Regenerate** never overwrites manufacturing edits. `ProposeRegenerationAsync` compares the latest iteration with the EBOM a spec selects and returns items: add line, remove line (its EBOM line is gone), change quantity / unit / effectivity (only where the MBOM line still equals the old EBOM line and is `matched`), and *review* items (declared adjustments and substitutions are only reported). Lines without an EBOM source (MBOM-only, manual) are always kept. `ApplyRegenerationAsync` applies the accepted items into a NEW iteration, carries every link to the line of the new EBOM iteration that is the same line (part, find number, alternate; else part and find number; else the only line of the part), and records the new derivation. Unaccepted changes surface as reconciliation findings. Applying twice is a no-op (no new iteration when the derivation is unchanged and nothing is accepted).

### Reconciliation
`ReconcileEngine` is pure and deterministic: input is the EBOM lines of the compared iteration, the MBOM lines and links, and two facts per child (Make assembly? MBOM of this plant exists and is released?). Lines joined by links form groups (union-find); each group is compared as a whole.

| Check | Rule | Severity |
|---|---|---|
| EBOM line without any MBOM line | not covered | error `ebom_line_missing` (hints at an unlinked MBOM line of the same part) |
| MBOM line without a source | not flagged MBOM-only | error `mbom_line_unexplained`; flagged: info `mbom_only_line` (warning if it also has links) |
| Quantity | `sum(EBOM qty) = sum(MBOM net qty)` per group, within 0.00001, one unit | error `quantity_difference`; a declared `quantity_adjusted` link makes it info `quantity_adjusted` with the reason; a group that differs by exactly the declared scrap is told so |
| Units | one unit per side and equal; never converted | error `unit_mismatch` (info if declared) |
| Substitution | `substituted` link between different parts | warning; same part: warning; `matched` link between different parts: error `link_child_mismatch` |
| Effectivity | union of the EBOM ranges vs union of the MBOM ranges in one dimension | gap (EBOM applies, MBOM does not): error `effectivity_gap`; excess: warning `effectivity_excess`; different dimension or context: warning |
| Revision drift | derivation iteration differs from the compared EBOM iteration | error `revision_drift` (with the BOM compare listed in the report) |
| Multi-level | a line with supply type Make whose child is a Make assembly needs an MBOM for the plant | missing: error `child_mbom_missing`; not released yet: warning `child_mbom_unreleased` |

Overall status: any error gives **Not reconciled**, except that revision drift as the only error gives **Stale**; no error but any warning or note gives **Reconciled with exceptions** (declared deviations); otherwise **Reconciled**. Coverage is the share of EBOM lines with at least one link. The comparison runs over complete data whatever the caller may read (a reader cannot hide an error by lacking access), is persisted, and **reading** a run applies the part filter: a finding about an unreadable part keeps severity, code, find numbers and quantities, and nothing else (A4), and the run counts how many were redacted. `MbomReconcileInputs` is the one file that reads the two child facts unfiltered; the architecture tests name it.

**Runs are immutable evidence**: `mbom.reconciliation_run` and `mbom.reconciliation_finding` reject UPDATE, DELETE and TRUNCATE; each run stores who, when, against which EBOM iteration and spec, the derivation it saw, counts, coverage and a SHA-256 over its header and findings, re-verified whenever it is read.

### Release gate and the signed hash
Submitting an MBOM through the workflow (`IWorkflowObjectGateway` for object type `mbom`, template `mbom-release-review`) requires the latest run of the latest iteration to exist, to have no errors, to have no warnings where the plant's policy says so, to be **fresh** (for Latest, LatestReleased and Effectivity the spec must still resolve to the EBOM iteration the run used; Baseline and explicit iterations are immutable) and to verify against its own digest. The submission writes an immutable `mbom.review_binding` (iteration digest, run id, run digest, a review SHA-256). The signatures cover `LoadIterationContentAsync`, which recomputes the iteration digest (lines, links, derivation) and the run digest **from the live rows** and states the run id; editing a line, a link or the run afterwards (even with a superuser connection) makes verification fail, and `CompleteRelease` recomputes the review digest and refuses to release when it differs from the binding. A trigger on `mbom.revision` is the safety net: Released needs a binding whose run has no errors. Releasing a revision supersedes the Released one before it.

### Explosion, queries, change impact, integration
- Multi-level explosion per plant is one recursive CTE modelled on ADR-0017: part read filter and MBOM read filter inside the recursion, unreadable lines nulled in the select list, find-number path addressing for lazy expansion, cycle guard (`Circular`), effectivity as-of a unit filters lines, children resolve to the released MBOM of the same plant (or the latest with an option; a child whose MBOM is missing or unreadable is a leaf). Summary (net and gross per part, unit and supply type; restricted lines never merged), CSV (banner first and last, formulas neutralized, audited as a download), compare of two iterations, where-used by plant, and the EBOM|MBOM side-by-side rows reuse the same filters.
- `IManufacturingBomSource` (Keel.Core, implemented in `MbomIntegrations.cs`) returns the released MBOM of a part in a plant with children resolved; ERP publishing is the integration module's. `IManufacturingImpactProvider` lets the ECN impact analysis list the MBOMs derived from a revised EBOM, per plant, with a count of the ones the caller cannot read.

## Consequences
- Manufacturing can adapt a plant's structure without a design change, and every deviation is declared (link status, MBOM-only flag, scrap) or reported.
- A superseded EBOM does not silently invalidate released MBOMs: they become Stale in reconciliation and in the ECN impact list until regenerated.
- Each iteration owns a full copy of its lines (bounded at 5,000), as for the EBOM; history is a plain read.
- The engine compares in memory after two set reads (two statements for 5,000 + 5,000 lines) rather than in SQL: interval arithmetic and group comparison are testable as pure functions with exact expected outputs, and the measured cost is small.

### Performance (measured, Docker Desktop PostgreSQL 17, Apple-silicon laptop, warm runs)
| Gate | Budget | Result |
|---|---|---|
| Reconcile 5,000 MBOM lines against 5,000 EBOM lines (157 findings stored and read back), end to end through the service | 1 s | about 0.23 s (engine and SQL about 0.21 s) |
| Full explosion of a 10-level, 50,000-line MBOM (one released MBOM per assembly), full access | 2 s | about 0.65 s |
| Generate an MBOM of 5,000 lines from the EBOM | none | about 1.1 s |
The EBOM explosion and effectivity gates are unchanged and pass.

## Alternatives
- **MBOM lines on the same part iteration as the EBOM** (a `structure_type` of MBOM in `bom_usage`, which ADR-0003/0017 anticipated): simplest storage, but every manufacturing edit would be a part revision under change control, there is no place for plants, and the release of the design and of the manufacturing structure could not be separated. Rejected; `bom_usage.structure_type` stays `EBOM` only.
- **Fixed equivalence by find number only** instead of persisted links: cannot express splits, merges or substitutions, and breaks on renumbering.
- **Quantity includes scrap** (gross in `quantity`): then reconciliation must guess the scrap; keeping net plus an explicit factor makes the comparison exact and the requirement derivable.
- **Plants and supply types in `cfg.ref_item`**: needs a CHECK change, Keel.Admin changes and still has no place for the plant release policy.
- **Reconciling in SQL**: possible for coverage, impossible to keep readable for interval gaps and group quantities; rejected for testability.
