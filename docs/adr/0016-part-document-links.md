# ADR-0016: Part–document link semantics

**Status:** Accepted · 2026-10-27

## Context
Parts are described by drawings and conform to specifications. A first design linked masters to masters and resolved
both ends to "latest" at read time. That rewrites history: released part revision A would later show drawing revision B,
which is not what was released. Windchill (described-by / reference links) and Teamcenter (relations on the item
revision) both attach these links to a specific version of the part.

## Decision
- Links are owned by the **part iteration**, like BOM usage links (ADR-0003). Table `plm.part_document_link`
  (`part_iteration_id`, `link_type`, `document_master_id`, `document_revision_id`, `created_at`, `created_by`). Rows are
  immutable (UPDATE/DELETE rejected by trigger), because they are part of an immutable iteration, and cannot be inserted
  while the part revision is locked.
- Link types:
  - **describes**: the document describes the part (its drawing, its model). It **pins a document revision**, by default
    the document's latest revision at link time; the caller may name a revision. Readers are told when the document has a
    newer revision than the pinned one (`newerRevisionAvailable`).
  - **references**: the part conforms to the document (a specification, a process standard). It points at the document
    **master** and resolves at read time to the latest Released revision, or the latest revision if none is released
    (assumption A23; a simple configuration specification until Phase 2 effectivity and baselines).
- A database CHECK enforces "describes ⇔ revision present", and a composite foreign key makes the pinned revision belong
  to the linked document.
- **Links change only through the part's check-out working copy** (`plm.part_checkout_document_link`, deleted with the
  check-out). Check-out copies the latest iteration's links into the working copy; check-in writes the working links into
  the new iteration; undo check-out discards them. Viewing an older iteration shows exactly the links it was checked in with.
- Adding or removing a link requires holding the part's check-out (which implies Modify on the part; it is re-evaluated)
  and Read on the document. A document the caller cannot read is reported as "not found", the same as a missing one.
  Audited as `part.link_document` / `part.unlink_document` against the part.
- Reading: the links of one iteration (default: latest iteration of the latest revision) plus, for the check-out owner,
  the working links flagged as such. Documents the caller cannot read are **redacted placeholders** (link id and type
  only; A4); the SQL never returns their columns. The reverse listing (parts whose latest iteration links to a document)
  redacts parts the same way.
- Module boundaries: the operations live in `Keel.Documents`, which must not reference `Keel.Parts`. Part rows are read
  with key lookups on `plm.part_master` plus the marking-column contract; the link carry-forward on check-out/check-in is
  plain SQL inside `PartService` (no reference to Documents).

## Consequences
- Released data is stable: what a released part iteration says it is described by never changes.
- **Increment 1.6:** revising a part (A → B inside an ECN) must copy the last iteration's links into B.1, exactly as
  check-in does today.
- Updating a drawing (new document revision) does not silently change the parts it describes; someone checks the part
  out and moves the pin, which is visible in history.

## Alternatives
- Master-to-master links resolved to latest: rejected (rewrites history).
- Pinning both link types: specifications would need re-linking on every part whenever a spec is revised; the
  read-time resolution of `references` mirrors how conformance documents are cited (by number, current issue).
