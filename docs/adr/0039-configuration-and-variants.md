# ADR-0039: Configuration and variants (options, variants, 150% BOM)

Status: Accepted (2026-10-29). Notes and assumptions: [increments/8.4-variants.md](../increments/8.4-variants.md). Migration `0022_variants.sql`.

## Context
One product comes in several configurations (Engine A or B, Payload X or Y). Teamcenter and Windchill model this as a 150% BOM: one structure whose lines are used only
when an option condition holds, resolved to a 100% BOM for one variant. Keel already has EBOM, MBOM, effectivity and baselines; options must sit on top of them
without changing what they mean when no option exists, and without weakening markings, release locks or the read filter.

## Decision
1. **Module `Keel.Variants`** (references Core, Security, Audit only) owns option families and values per product, option rules as data, named variants with a
   Draft, Released, Retired lifecycle, the deterministic rule engine and the REST/UI services. The **condition language** (`Keel.Core.Variants.OptionExpr`) lives in
   Core because `Keel.Bom` evaluates it too.
2. **Product = an Assembly part.** An option model is readable and changeable exactly as the part is (`AccessPolicy` on the part's markings). No marking columns of its
   own: a markings change applies to options, rules and variants at once. A product the caller cannot read answers as missing on every route; lists go through
   `AccessPolicy.BuildReadFilter`; counts exist only for readable products.
3. **Rules as data** (`requires`, `excludes`, `mutex`), evaluated by `RuleEngine`: model validation (unknown options, contradictions, one-family contradictions, duplicates,
   options no valid combination can use, empty space), configuration check (`Check`), enumeration with early pruning and a work limit (250,000 nodes; beyond it the
   analysis says it is limited). A rule that adds an error or makes an option unusable is refused.
4. **Conditions on lines** are stored as canonical text plus a parsed tree (`opt_cond`, `opt_ast` on `plm.bom_usage` and `plm.bom_working_usage`). Text is parsed once
   (hand-written parser, size/depth limits, reserved words); only the tree is evaluated, in C# and by the IMMUTABLE database function `var.eval_cond` (walks JSON,
   no dynamic SQL). A property test compares text, tree and SQL on generated expressions.
5. **Resolution** adds a variant selection to `BomQuery`/`MbomQuery`. The predicate `@var_sel IS NULL OR opt_ast IS NULL OR var.eval_cond(...)` sits in the same
   recursive, read-filtered statement, beside the effectivity predicate; with no variant it is trivially true, so structures without conditions behave and export
   byte for byte as before. MBOM lines inherit the conditions of the EBOM lines they implement through the existing links (MBOM lines carry no condition of their own);
   manufacturing-only lines apply to every variant.
6. **Locks.** Conditions change only on the check-out owner's working copy; stored iterations are already immutable and locked with their revision, so a released
   iteration changes only by revising (the revise copy carries conditions). Released variants are frozen (trigger); a model change that would invalidate one is refused.
   Check-in content fingerprints include conditions (only when present, so existing hashes stand).
7. **Integration.** New additive event `variant.released` (schema `variant.released.v1.json`), pull API `?variant=` and `condition` on 150% lines, `variant` on Technical
   Data Package requests for a part (through `IVariantSelectionSource` in Core; the TDP module does not reference Variants).
8. **Composition in Keel.Web:** `VariantWhereUsedService` ("which variants use this part") explodes the 150% structure once through the read filter and decides each
   variant from the condition chain down to the part.

## Consequences
- No new marked table; no new awareness kind (so no change to the `ws`/`ntf` CHECK constraints). Activity feed, timeline and audit use the product part's identity.
- Alternates of one find number remain distinguished by effectivity only; option alternatives use distinct find numbers (see FEAT-VAR-5).
- Baselines are frozen lists and are refused a variant.
