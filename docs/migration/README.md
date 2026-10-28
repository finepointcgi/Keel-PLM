# Migrating from Teamcenter and Windchill

`keel-import` moves parts, documents, files, BOM structure, revision and iteration history, lifecycle state, change history and file links into Keel, with a dry-run, validation reports and reconciliation. Design: [ADR-0023](../adr/0023-migration-importers.md). Commands and exit codes: [runbook.md](runbook.md).

> **Read this first.** The importers read formats that *we specified* from public documentation ([plmxml-subset.md](plmxml-subset.md), [windchill-extract-spec.md](windchill-extract-spec.md), sources in [sources.md](sources.md)). We have **not** seen a real Teamcenter or Windchill export. Everything is tested against synthetic packages. **Real exports from your systems are required to validate and extend the mappings before a production migration**; see [What we need from you](#what-we-need-from-you). The neutral *canonical package* ([mapping-reference.md](mapping-reference.md#canonical-package)) is the format for scripted exports from any system and the safe bridge when the native formats do not fit.

## Process
1. **Assess.** Export a representative sample (see below). `keel-import inspect <package>` lists counts, lifecycle states, units, types, users, properties, unknown and ignored elements. Decide scope (all released data? active programs only?).
2. **Map.** `keel-import map-template` writes a starter mapping from the inventory. Decide every empty value: lifecycle states, units, types, users, attributes, **markings rules** (with the security office), change states and classes. See [mapping-reference.md](mapping-reference.md).
3. **Validate.** `validate --offline` while you iterate, then against a rehearsal database (real reference data and collision checks).
4. **Dry-run.** The full load is simulated in a transaction that is always rolled back; the report lists every problem with a code and fix hint, defaulted markings, quarantined objects, unmapped properties, the revision label mapping, file checks and an estimate.
5. **Review.** Security office: markings distribution and the defaulted-markings list. Engineering: revision mapping, unmapped properties, users replaced by the fallback user. Data owners: quarantined objects.
6. **Load.** Allowed only with the successful dry-run report of *exactly this* package and mapping, and `--yes`. Idempotent and resumable.
7. **Reconcile.** Counts, BOM structure checksums, file hashes, lifecycle and markings distributions, id map, audit chain. Exit code 0 is the acceptance criterion.
8. **Parallel run.** Keep the source system read-only for the migrated scope while users work in Keel; re-export and rerun for the delta (new revisions of imported items are appended; changes to already imported objects are reported as conflicts and go through change control).
9. **Cutover.** Freeze the source, final export, dry-run, load, reconcile, sign-off, switch.

## Roles
* **Migration lead** runs the tool and owns the mapping file.
* **Security / export-control office** owns the markings rules and reviews the defaulted list and the markings distribution. Nothing is loaded without their sign-off on the dry-run report.
* **Source-system administrator** produces exports and answers questions about states, units, custom attributes and revision schemes.
* **Keel administrator** provides the target (database connection as `keel_app`, vault credentials), checks `/admin/migration` and the audit chain.
* **Data owners** resolve quarantined objects.

## What is preserved
| Source | In Keel |
|---|---|
| Item / document master | part / document master with the original number, name, created at/by; `imported_batch_id`, `source_id`, `legacy_properties` |
| Revisions | revisions labeled A, B, C ... **in source order** (rules in the mapping reference); the original label in `legacy_revision`; original created at/by |
| Iterations | the original iteration sequence, author, time, comment, description; attributes mapped to Keel attribute definitions; every other property in `legacy_properties` |
| Lifecycle | the current state is loaded directly (Released revisions arrive locked with their original release date and user); the source's full state history is kept in the revision's `legacy_properties`. Under Review is imported without a workflow instance (warning). |
| BOM | usage lines on the correct parent iteration (quantity, unit, find number, reference designators); find numbers are assigned when missing (counted) |
| Effectivity | with `effectivity.load`: structured serial, lot and date ranges become real line effectivity (alternates of one find number, ordinals in range order); text-only effectivity stays in the line notes ([mapping-reference.md](mapping-reference.md#effectivity)) |
| Files | streamed into the vault, content-addressed, SHA-256 verified against the package (computed and reported when the source gives none) |
| Part-document links | `describes` pinned to the matching document revision, `references` to the master |
| Change history | PR / ECR / ECN as historical records: state and class as in the source, affected and resulting objects, the PR -> ECR -> ECN chain, original dates and authors; flagged by `imported_batch_id` and a history entry "migrated record, signatures not reproduced"; no workflow instances, no tasks, no signatures |
| Users | names only, mapped to Keel usernames (or the flagged fallback user `migration`); the original name stays in `legacy_properties` |
| Markings | from your rules, fail closed |
| Classification | with `classification.load`: missing classes are created from the staged hierarchy (`classification.json`), existing classes are never changed, and the class and its values are written onto the part iterations before their revision is locked, in the same rows native check-in writes ([classification-format.md](classification-format.md#migration)) |

## What is NOT migrated yet
* **Revision-level effectivity** (the effectivity a change notice gives a released revision): no supported source format specifies it, so `effectivity.revisionLevel: true` is refused with `NotSupportedException` (FUP-3). Line effectivity is migrated; see the mapping reference.
* **Effectivity and classification are opt-in** (`effectivity.load`, `classification.load`): with the options off, source effectivity text stays in the line notes (`effectivity-preserved-in-notes`) and class assignments are only staged and counted (`CLASS-STAGED`).
* **Class hierarchy deltas**: an import creates missing classes but never moves, retires or alters existing ones (conflicts are reported); effectivity contexts are never created by an import (create them in Administration first).
* **Workflow history and signatures** (Teamcenter workflow tasks, Windchill workflow history): not reproduced, by design; imported changes carry the "signatures not reproduced" flag.
* **Change tasks.**
* **Intermediate lifecycle states and iteration-level states:** only the current state is loaded (history kept in `legacy_properties`).
* **CAD structure semantics** (family tables, flexible components, configurations, occurrence positions): ignored and counted.
* **Markup and redlines, user passwords, access-control lists, saved searches, subscriptions, notes on objects.**
* Document-to-document relations other than part links.

## Risks
| Risk | Mitigation |
|---|---|
| The native format of your system differs from our subset | get real samples early (below); the reader reports what it does not understand; the canonical package is the fallback |
| Wrong markings | fail closed, restrictive combination, review of defaulted markings, reconciliation of the markings distribution; the import runs as `keel_app` and every object passes `Markings.Create` |
| Wrong revision order | `REV-ORDER-AMBIGUOUS` is an error; the mapping table is in the report; originals kept in `legacy_revision` |
| Irreversible load | dry-run gate, rehearsal on a database copy, quarantine instead of partial guesses, conflicts never overwrite |
| Export packages as CUI | the package, mapping, reports and any log are export-controlled artifacts: store and transfer them like the data (the tool makes no network calls besides the database and the vault) |
| Source changes during migration | the package hash and mapping hash are part of the gate; reconcile re-reads the package and shows drift |
| Vault or database failure mid-load | transactions per chunk, per-object atomicity, resumable by rerun |
| Large exports | PLM XML is streamed; a 200 MB file raises the managed heap by about 47 MB (see [../increments/3.0-migration.md](../increments/3.0-migration.md)) |

## What we need from you {#what-we-need-from-you}
Synthetic samples are not a substitute. Please provide **sanitized** (markings-reviewed) samples, small enough to inspect by hand, plus the information below.

**Teamcenter**
1. A PLM XML export (with its files folder) of a released multi-level assembly (3-4 levels) with the revision rule and transfer mode you actually use, and the Teamcenter and PLM XML schema versions.
2. An item with several revisions and several revision sequences (versions), including how the export names `sequence_id` and the revision's status, dates and users.
3. Items with datasets (PDF and a CAD format) and Document items with their revisions.
4. Release status objects: names, how multiple statuses are exported, dates.
5. Change objects as exported (ECN/ECR/problem report item types and revisions): state, class, priority, reason, solution/problem items, impacted items, parent relations.
6. The properties that carry export control (ITAR flag, ECCN, USML category, project/program, distribution or security class): names, value spellings, where they live (item, revision, form).
7. Classification (ICS) assignments if you want them migrated, BOM lines with effectivity, variant or option data (to confirm it is out of scope), and the list of custom attributes with their types.
8. Users and groups as exported; the list of lifecycle states, units of measure and part types in use.
9. One large export (hundreds of MB) for a performance and memory check.

**Windchill**
1. The Windchill release and Windchill REST Services version; sanitized JSON responses (all pages) for ProdMgmt `Parts` with `Uses` and `PartDocAssociations` expanded, DocMgmt `Documents` with content information, and ChangeMgmt `ChangeNotices`, `ChangeRequests` and `ProblemReports` with affected and resulting objects; or the loader/CSV extracts you use.
2. Proof that **iteration history** (every iteration of every version) can be extracted, and how versions are labeled (A, B / 01, 02 / 1.2) including your lifecycle templates and their state names.
3. How file content is obtained (download URLs or the content store), with hashes if available.
4. Export-control attributes (soft attributes / IBAs: names and values), context and organization names, units of measure and part sources in use.
5. Users and groups, and the list of custom attributes with types.
6. A large extract for a performance check.

**Both:** which scope is to be migrated (all released data, active programs, a date cut-off), whether line effectivity (serial, lot, date ranges per end item) and classification (hierarchy, assignments, attribute values) must come along, the parallel-run and cutover plan, and who signs off the markings rules.

## Measured behavior
Numbers measured on the development machine (Apple silicon, Docker Postgres 17 on tmpfs and SeaweedFS): 10,000 parts, 18,000 revisions, 26,000 iterations, 40,000 BOM lines and 500 files load in about 33 s (about 3,000 rows per second), the dry-run takes about 32 s, parsing and mapping 0.7 s, reconciliation 0.5 s. Details and caveats: [../increments/3.0-migration.md](../increments/3.0-migration.md). Real throughput depends on your database host, the vault and the size of the files.
