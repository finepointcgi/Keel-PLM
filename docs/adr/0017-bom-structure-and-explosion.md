# ADR-0017: BOM structure storage, explosion and redaction

**Status:** Accepted · 2026-10-27 · Refines [ADR-0003](0003-bom-usage-links-and-config-specs.md)

## Context
ADR-0003 fixed the model (usage links from a parent iteration to a child master, config specs resolved at read time, one recursive CTE, redacted placeholders). Increment 1.4 implements it. This ADR records the decisions that implementation needed: where editable and frozen rows live, how the check-out cycle reaches a module `Keel.Parts` does not know, how access is applied inside a recursion, how cycles are prevented, what the performance gate measured, and how the grid works under the CSP.

## Decision

### Storage and the check-out cycle
- `plm.bom_usage` holds the usage lines of **one parent iteration** (`parent_iteration_id`), pointing at a child **master**. `structure_type` is `EBOM` only (CHECK); the manufacturing BOM arrives in Phase 2. Rows are immutable: a trigger rejects `UPDATE`/`DELETE` for every role and `keel_app` has only `SELECT, INSERT`. Inserts into a locked revision, from a non-Assembly parent, or of a part into itself are rejected by trigger as well. The EA rules (whole number, designator count = quantity) are CHECK constraints too.
- `plm.bom_working_usage` is the private working copy, keyed by the check-out (`checkout_revision_id` references `plm.part_checkout ON DELETE CASCADE`). Undo-check-out therefore discards it with no code. Its find-number uniqueness is `DEFERRABLE` so renumbering can swap numbers in one statement.
- `Keel.Parts` stays unaware of BOMs. `Keel.Core` defines `IPartIterationParticipant` (`OnCheckOut`, `OnCheckIn`, optional `OnUndoCheckOut`); `PartService` invokes the registered participants inside its existing transactions and maps an `IterationVetoException` from a participant to a 409 conflict. `Keel.Bom.BomParticipant` copies the latest iteration's rows into the working copy at check-out and freezes the working rows into the new iteration at check-in. Every iteration owns a full copy of its lines (bounded by 5,000 per parent), so history is a plain read of that iteration.
- Edits serialize on `SELECT … FOR UPDATE` of the part master, the same lock `PartService` takes, so edits, check-in and check-out of one part cannot interleave. Edits require the check-out owner; the working copy is invisible to everyone else.

### Config specs
- `Latest`: the master's `is_latest` revision at its latest iteration. `LatestReleased`: the highest revision whose state key is `Released`, ordered by `(length(revision), revision)`, which is exactly ASME Y14.35 order (A…Y, AA…). None exists: the line is returned flagged `Unresolved`, never dropped, and not expanded. `Effectivity` and `Baseline` exist in the enum and throw `NotSupportedException` naming Phase 2 (the API answers 501).
- The spec is resolved per level, inside the recursion, as a `LATERAL` subquery, so a structure can mix revisions.

### Explosion
One `WITH RECURSIVE` statement seeded by a pseudo row (the root iteration or one lazily expanded line):
- The **read filter is evaluated inside the recursion** (`CROSS JOIN LATERAL (SELECT <filter> AS ok)`), and the recursive term only continues from rows with `ok`. A subtree below a line the caller cannot read is never traversed and never read.
- Identifying columns of an unreadable line (number, name, revision, state, markings, designators, notes, `has_children`) are produced as `NULL` by the select list, so they never leave the database. Quantity, unit, find number, level, path and the rolled-up quantity remain (A4); the rollup is computed from visible quantities only.
- The result is one flat, depth-first list ordered by the `int[]` find-number path. `ext = round(LEAST(parent_ext * qty, 1e18), 6)` per level (units are not converted; the product is expressed in the line's own unit).
- **Cycle guard at read time:** each row carries the array of ancestor master ids; a child already on the path is returned flagged `Circular` and not expanded. Structures that pass the edit-time checks can still loop under `LatestReleased` (an old released revision contains a part whose latest structure contains the parent), and an unbounded recursion must be impossible.
- A hard cap of 250,000 lines per explosion (and 100,000 where-used rows) returns a clear "request fewer levels" error instead of exhausting memory.
- Lazy expansion addresses a line by its **find-number path** (`10.20`), because a shared sub-assembly appears under several parents and a usage id would be ambiguous. The server walks the path hop by hop, applying the filter and the spec at each hop, then explodes one level from there with the correct absolute level, path and rolled-up quantity.
- Marking columns are read once per distinct value (`MarkingCache`), so a 50,000-line result builds a handful of `Markings` objects. The page banner is `Markings.AggregateBanner` over the root and the readable lines actually returned; restricted lines contribute nothing, so the banner never hints at hidden content.

### Where-used, summary, compare
- **Where-used** starts at the child master and walks up through *parent revisions whose latest iteration contains it*. A `chain` CTE of distinct masters per level expands only through parents the caller can read (filter inside the recursion, `UNION` dedupes, the level cap bounds any cycle); the select then lists every parent usage and hides unreadable parents. Levels 1…10.
- **Summary** (indented to single level) sums extended quantities per (part, unit) in memory from the explosion result; default is leaf lines only (assemblies expanded below are not double counted). Restricted lines are never merged: merging would reveal that two hidden lines are the same part.
- **Compare** diffs the direct lines of two iterations (labels like `A.2`). Lines match on (part, find number), then on part alone (a renumber). Hidden lines match on find number and only when the hidden part is unchanged, so a replaced hidden part appears as one removed and one added placeholder; nothing about its identity is revealed, but quantity and unit changes remain visible (A4).

### Cycle prevention at edit time
The structure is a DAG. Adding a child is refused when the child is the parent or already contains the parent through its **latest-iteration** structure. The check reads the *ancestors* of the parent (small) rather than the descendants of the child (possibly 50,000 lines): `FindCycleAsync`. The error names the path (`A -> B -> C -> A`); parts the caller cannot read appear as `(restricted item)`. Because two check-outs can each add a line the other would close into a loop, **check-in re-runs the check under a global advisory lock** and vetoes the check-in with a message that names only the find number, never other parts.

### Access, audit
Every call evaluates the policy and audits in the same transaction: reads record root, spec, depth, parent line and counts (never a row per line); CSV export records the line count and the aggregate banner; denials are audited with reason codes; a root the caller cannot read is `404` and indistinguishable from a missing part. Validation and conflict failures of edits are audited as `Failure`. CSV export uses the `Download` action, applies the same filter and redaction, stamps the banner as first and last line, and neutralizes spreadsheet formulas.

### Performance gate (measured)
`BomPerformanceTests` generates 38,115 parts and 50,000 lines over 10 levels with bulk SQL (`SyntheticBomGenerator`), then explodes through the real SQL filter after a warm-up call. Docker Desktop Postgres 17 on an Apple-silicon laptop, one container, defaults:

| Case | Result |
|---|---|
| Full explosion, full access | 50,000 lines in about 0.55-0.6 s (budget 2 s) |
| Restricted user (non-U.S. person), redaction path | 1,248 lines (426 placeholders) in about 20 ms |
| U.S. person outside program ORCA | measured in the same test, below budget |
| Where-used of the most used standard part, 1 level / 10 levels | 346 parents in about 10 ms / 2,265 rows in about 30 ms |
| Summary / CSV over the 50,000 lines | about 0.6 s each |

The query needed no tuning beyond what the migration already contains: covering indexes `bom_usage (parent_iteration_id) INCLUDE (child_master_id, find_number, quantity, unit)` and `(child_master_id) INCLUDE (parent_iteration_id, …)`, and `part_revision (master_id, lifecycle_state)` for `LatestReleased`. The materialization cost was the real risk (building `Markings` per row); the marking cache removed it. The test asserts the budget and compares the SQL result with an in-memory walk using `AccessPolicy.Evaluate` for several subjects, so the gate cannot pass by returning the wrong rows.

### UI under the CSP
- The grid flattens server-side and uses Blazor `Virtualize` (fixed 32 px rows, `role="treegrid"`, roving tabindex, Up/Down/Left/Right/Home/End/Enter), loading one level per expansion.
- **Finding:** `Virtualize` positions its spacers with inline `style` attributes, which the CSP `default-src 'self'` blocks (verified in the browser: spacer heights stayed 0, so rows beyond the first window were unreachable). Rather than weaken the CSP, `BomTreeGrid.razor.js` (served from `'self'`) mirrors each `style` attribute Blazor sets onto `element.style`; CSSOM writes are exempt from CSP. The browser console still logs the blocked attribute once per spacer update; the behaviour is correct. If the coordinator prefers, a `style-src-attr` relaxation would remove the shim and the console noise; that is a security decision for the CSP owner.
- **CSV download:** `/api/*` accepts bearer tokens only, so the UI cannot link to the API. A plain `<a download href="ui/parts/{n}/bom.csv?…">` points at `GET /ui/parts/{number}/bom.csv`, a cookie-authenticated route in `BomEndpoints` that calls the same service (same filter, redaction, audit). No script, no blob URL, nothing the CSP blocks.

## Consequences
- `plm.bom_usage` grows by one copy of a parent's lines per iteration; at the 5,000-line cap this is acceptable and keeps history reads trivial. If measurements ever show otherwise, store deltas behind the same service.
- Cycle prevention is advisory at edit time and authoritative at check-in under one global lock, which serializes only check-ins that carry BOM lines.
- Where-used and explosion read `latest_iteration` of the revisions. Where-used of historical iterations and baselines is Phase 2.

## Alternatives
- **Materialized exploded BOM tables:** fast reads, painful invalidation; not needed at 0.6 s per 50,000 lines (ADR-0003 kept it as an option).
- **Filtering after the recursion:** simpler SQL, but it traverses and reads hidden subtrees, and hidden data would have crossed into the application.
- **Cycle check by walking the child's descendants:** correct but proportional to the child's whole subtree on every add.
- **Own windowing instead of `Virtualize`:** avoids inline styles but reimplements scroll tracking and needs more JavaScript than the 30-line shim.
