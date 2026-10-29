# ADR-0041: Records management: retention schedules, legal hold, disposition and WORM-style archive

Status: accepted (increment 8.6, 2026-10-29). Builds on ADR-0004 (markings and access policy), ADR-0005 (tamper-evident audit and checkpoints), ADR-0015 (vault),
ADR-0018 (review and e-signature), ADR-0019 (marking dominance) and ADR-0026 (vault garbage collection).
Assumptions are FEAT-REC-n in [increments/8.6-records.md](../increments/8.6-records.md). Operations guide: [ops/records-management.md](../ops/records-management.md).

## Context
Released product data must be kept for a stated time, kept longer while a legal matter needs it, and disposed of afterwards in a way that can be shown later. Three existing
guarantees make this harder than a delete button: the audit log is hash-chained and must stay verifiable, rows reference each other (revisions, links, signatures), and the
database refuses deletes and edits of released data on purpose. Retention must also respect the markings: a person must never learn, from a hold or a queue, that an object they
cannot see exists, and marked content must be destroyed only by someone allowed to see it.

## Decisions

### 1. One module, one schema, and nothing removed from anyone else
`Keel.Records` (schema `rec`, migration `0024_records.sql`) references Core, Security and Audit only. It reads documents and parts through their marking columns, reaches the review
engine through `IWorkflowObjectGateway`, the vault through `IContentStore`/`IContentReader` and S3 Object Lock through the new `IWormStorage` (Core). It adds triggers to other
schemas only to **freeze** disposed objects; it drops, disables and weakens nothing, and has no `SECURITY DEFINER` path (tests read the migration to prove it).

### 2. Retention schedules are signed, versioned data
A schedule version holds record classes: match criteria (object kinds, types, programs, export jurisdictions, CUI categories, distribution statements), a retention period in months,
a trigger event (release, obsolescence, program close, contract end), a disposition (destroy, transfer to archive, review), a citation and notes. The first matching class by priority
(then code) decides an object's class. Publishing is a review through the workflow engine with signatures over every class (`retention-schedule-publication`); a published version is
immutable (triggers) and the next version starts as a copy of it. Program events (close, contract end) are append-only; the latest entry counts. Classification is a cache
(`rec.classification`) for the queue; **every destructive step evaluates the rules again from the object's own facts**.

### 3. Legal hold: membership is materialized, widens, and is checked under one lock
A hold names objects, a program or a query. Members are written when the hold is placed and again at each re-evaluation (a hold only grows until it is released), and program and
query scopes see **every** object, because a hold must protect what its author cannot see. A hold overrides disposition at five places: the queue leaves held objects out, a request
cannot list one, execution skips one, a database trigger refuses a tombstone for one, and the garbage collector's view of "referenced" keeps the content of a held disposed object.
Placing a hold, re-evaluating holds and executing a disposition take the same advisory lock, so a hold committed before a disposition commits is always seen by it. Release is a
review with a signature (`legal-hold-release`); the submitter cannot sign.

### 4. Disclosure: filtered by the reader, never counted
Only records roles (records manager, administrator, auditor read-only) see holds. Everything shown to a person is filtered by that person's read filter: a hold's page lists and counts
only the members the person may read; the queue counts held objects only among readable ones; the chip on an object's page appears only for a records role who can read the object; an object
a person names but cannot read is "not found", word for word like a missing one. The hold record itself carries no markings and no object names (a warning on the form says so).
Audit details of records management are identifiers, codes and counts only.

### 5. Disposition: tombstone, certificate, sweep, confirmation
A request lists objects due and unheld; its markings are the minimal cover of the objects (ADR-0019), so one request is one program, and approval is a review (`disposition-approval`)
whose approvers must be able to read what they sign. Execution (records manager only; an administrator alone cannot destroy; for marked content the requester cannot also execute)
re-evaluates every item and then, in one transaction with its audit events:
- inserts an immutable **tombstone** per object (an explicit, trigger-guarded path: only for an approved request that lists the object, never for a held one), freezing it further;
- issues one immutable **certificate of destruction** (canonical JSON listing objects, classes, content hashes and sizes and the approval's signatures), hashed and signed with the audit checkpoint key under its own domain string;
- releases the vault content to the **existing sweeper**: `VaultReferences` gained an optional live-row predicate so the files of a disposed (and unheld) document stop counting as references. The sweeper's grace period, per-key lock and integrity checks decide when bytes go; shared content stays because another row still references it.
Rows are never deleted: master, revisions, iterations, links, signatures and the audit log stay, so no foreign key and no hash chain is touched. A later **confirmation** records, signed, that the vault no longer has a file (or that another record still uses it).

### 6. WORM-style archive, with an honest statement of what it guarantees
An archive is one ZIP: released and obsolete revisions with their metadata, the files (content-addressed), the workflow signatures and an extract of the audit log (payload, hashes, the first covering checkpoint). A manifest lists the SHA-256 and size of every entry and is signed with the checkpoint key.
The package is stored in the vault under its own SHA-256 and registered in an insert-only table. The application guarantees: no code path writes or deletes the package or its record, triggers refuse
changes (also for the database owner, who would have to disable them visibly), and the garbage collector counts the package as referenced forever. It **cannot** guarantee that someone with
write access to the storage cannot overwrite or delete the object; the signed manifest and the hash address make that detectable (verification fails), not impossible. When the vault bucket supports
S3 Object Lock, the application asks (`IWormStorage.DetectAsync`, never assumed) and sets a retention period (governance or compliance) on the object; the result is recorded per archive (`worm_mode`). `Keel:Records:ObjectLock`
is `Off`, `IfAvailable` (default) or `Required`. Verification works offline (`keel-vault verify-archive`) and in the application (the read-only view).

## Consequences
- Disposal frees storage but keeps identity: a disposed object's number, name, revision history, markings and signatures remain readable, by design; destruction applies to stored files (FEAT-REC-3).
- One request, one archive and one certificate cover one program; mixed-program selections are refused with a message.
- Classification and holds are evaluated by the application; there is no SQL-only path that disposes of an object.
- The certificate proves what the application did and approved; it cannot prove that a storage administrator did not copy bytes first. That is a procedural control (docs/ops/records-management.md).
