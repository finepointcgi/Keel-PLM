# ADR-0023: Migration importers (Teamcenter, Windchill, neutral package)

**Status:** Accepted · 2026-10-28 (Phase 3)

## Context
The product brief makes migration a first-class feature: importers for PLM XML and Windchill export formats that preserve revision history, BOM structure, lifecycle state, change history and file links, with dry-run, validation reports and reconciliation counts. Three facts shape the design:

1. **Nothing in Keel is ever deleted.** A wrong import cannot be rolled back, so every control has to sit *before* the load: validation, a dry-run that is provably the same code as the load, and a gate that refuses to load without a successful dry-run of exactly this package and mapping.
2. **The data is export-controlled.** Markings decide who can see an object, so an import that guesses markings is a disclosure risk. Markings mapping must fail closed.
3. **We have no real Teamcenter or Windchill export.** The formats are implemented as documented *subsets* that we specify ourselves (docs/migration/plmxml-subset.md, windchill-extract-spec.md). They must be validated and extended against real exports before a production migration (see docs/migration/README.md, "What we need from you").

## Decision

### Pipeline
`parse -> canonical staging model -> map -> validate -> dry-run -> load -> reconcile -> report`.

* **Readers** (`Keel.Migration.Sources`) emit the canonical model (`Keel.Migration.Canonical`, versioned, JSON-serializable) and a source inventory: PLM XML (streaming `XmlReader`, DTDs prohibited, IDREFs resolved after all files are read), Windchill OData-shaped JSON, Windchill loader-style CSV, and the neutral *Keel canonical package* (`package.json` plus a files directory, optionally `*.jsonl`), which is also the format for scripted exports from other systems.
* Unknown elements are never dropped silently: inside a known owner they are preserved as `xml:*` properties (and end up in `legacy_properties`), top-level unknown elements are counted with their first path, known-but-not-migrated elements (geometry) are counted as ignored, and unknown members of the canonical format are errors.
* **Mapping** is a versioned JSON file (comments allowed; unknown members are errors so a typo can never silently weaken a rule). `Mapper` is a pure function of (package, mapping, reference data): no clock, no database. That is what makes the dry-run report hash meaningful.
* **Reference data** (lifecycle states, attribute definitions, user directory, existing numbers) comes from the target database; `validate --offline` uses the built-in defaults of migrations 0002/0003/0007 and says so.

### Markings (the security-critical part)
* Rules map source properties to markings contributions. Every matching rule contributes; contributions combine **restrictively**: highest jurisdiction, union of CUI categories, most restrictive distribution statement, intersection of export authorizations. Two rules that disagree on a scalar (program, owning org, USML category, ECCN, controlling office, reason) are an error, never a guess.
* Every property context of an object (master, each iteration) is evaluated and the results combine the same way, so a later iteration can only tighten.
* **Fail closed:** an object to which no rule assigns an export jurisdiction has no determinable markings and is a validation error, unless `markings.defaultMarkings` is configured. The default is used *only* when no rule set a jurisdiction (it is never merged into a rule-determined result), and every object that uses it is listed in a "defaulted markings" report section for review.
* The result must pass `Markings.Create`. An object that fails stays unimported (quarantined with the reason). Imported changes are raised with `MarkingDominance.MinimalCover` so their markings cover every part they list (ADR-0019), and the raise is reported.
* The dry-run cannot weaken anything because the dry-run *is* the load code over a rolled-back transaction, and the plan (including markings) is computed once, before either.

### History-preserving load
* Bulk, history-preserving import cannot use the interactive services (they stamp "now" and the current user), so `Keel.Migration` writes plm/chg/document rows with plain SQL through one narrow internal class (`ImportWriter`). It never disables a trigger and never needs a superuser: the CLI runs as `keel_app`. The writer inserts in the order the guards of 0002/0003/0005/0007 accept: master; revision in its *initial* state; iterations; BOM usages; part-document links; then the final lifecycle state last (Released revisions arrive locked, with their original release date). Changes are inserted in their initial state with affected objects and then walked to the source state along the lifecycle's own transitions (every guard checks every step); an ECR with a Class I notice is held at Approved until the notice is inserted.
* Migration `0010` adds nullable provenance columns (`imported_batch_id`, `source_id`, `legacy_revision`, `legacy_properties`, `legacy_number`) and the `mig` schema (`batch`, `id_map`, `quarantine`, `report`). Native rows and behaviour are unchanged. `mig.id_map` rows, quarantine rows and reports cannot be updated or deleted; a batch can only move from `Running` to a final status.
* **Idempotent and resumable.** `mig.id_map` maps (source system, entity type, source id) to the Keel id and stores a hash of the *mapped* object. A rerun skips what is mapped, completes what is missing (each part chain, document or change tree is atomic), and reports a changed source (or mapping) for an imported object as `LOAD-CONFLICT` without overwriting anything: released data is immutable. A new revision of an already imported item is appended if it sorts after the imported ones.
* Transactions: chunked (default 100 objects) with a savepoint per object, so with `--quarantine` one failing object is skipped and listed. A dry-run runs the same code in one transaction that is always rolled back; the vault is checked for presence but not written.
* **Files** stream into the vault through `IContentStore` (Keel.Core; implemented in Keel.Documents by `VaultContentStore`), content-addressed, with the SHA-256 verified against the package when it gives one (mismatch is an error) and computed otherwise (reported).
* **Change history** is imported as historical records (no workflow instances, no tasks, no e-signature claims): state and class as in the source, numbers drawn from the Keel sequences with the source number kept in `legacy_number`, and one `chg.history` row `imported` ("migrated record, signatures not reproduced").
* **Audit.** `batch` granularity (default): one event at start, one per entity family with counts, one at the end, all carrying the package manifest hash. `object` granularity adds one event per imported part, document and change, in the same transactions. Measured cost: 0.44 ms per object (2,000 parts: +0.9 s). Recommendation: `batch` for routine imports (the id map and `imported_batch_id` already identify every object a batch created); `object` when the program office wants every object visible in the audit search.

### Gate, reports, reconciliation
* `dry-run` writes a report (JSON, Markdown, problems CSV) whose hash covers package SHA-256, mapping SHA-256, quarantine mode, planned counts and every problem, quarantined and defaulted object, and none of the times or database state. `load` requires that report: it must be unmodified, from a dry-run, successful, for the same package manifest hash and mapping hash and quarantine setting, and the validation result at load time must still hash the same.
* `reconcile` re-reads and re-maps the package as it is *now* and compares it with the database: counts per entity, BOM direct lines per assembly iteration and an exploded Merkle checksum per assembly "as imported", per-file SHA-256 (and vault presence, or a full read with `--deep`), lifecycle and markings distributions, id-map completeness, imported objects the package no longer plans, workflow instances for imported objects (must be zero) and the audit chain. Any difference exits non-zero.
* There is no implicit database target (same rule as keel-seed): `--connection`, a settings file, or an environment variable; the dev stack only with `--use-dev-stack`.
* Reports are CUI-handling artifacts (they list ids, numbers and markings decisions). The admin page `/admin/migration` is UNCLASSIFIED and shows counts, codes and source ids only; problem messages (which can quote source values) and object names stay in the files.

### Not in this phase
Effectivity (loaded with Phase 2a: source effectivity text is preserved in the usage notes and reported), classification (staged and counted; `IClassificationLoader` throws `NotSupportedException` naming Phase 2b until Keel.Admin registers an implementation), workflow history and signatures, change tasks, CAD structure semantics (family tables), markup/redlines, passwords, Teamcenter workflow tasks. See docs/migration/README.md.

## Consequences
* A migration is a process, not a button: assess, map, dry-run, review, load, reconcile, parallel run, cutover.
* The importer duplicates a small amount of SQL knowledge (column lists of plm/chg tables). Tests in `MigrationLoadTests` fail loudly if a later migration changes those tables incompatibly, and the architecture test allows the importer to select from marked tables in exactly one file (`Reconciler.cs`).
* Because quarantine and conflicts leave objects out, a migration can finish with issues; exit code 3 and the report say so.

## Alternatives considered
* **Importing through the services** (PartService, BomService, ...): rejected; no way to set original timestamps, authors or states, and every revision would need a workflow.
* **Disabling triggers during import:** rejected; the guards are the definition of what Keel will accept, and a superuser connection would defeat least privilege.
* **A deletable batch / rollback of a load:** rejected; nothing in Keel is deleted, and a partial rollback of released data would break the audit story. The dry-run gate replaces it.
* **YAML mapping files:** JSON with comments needs no extra dependency in an air-gapped build.
