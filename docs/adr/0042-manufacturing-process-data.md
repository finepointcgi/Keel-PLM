# ADR-0042: Manufacturing process data (plans, work instructions, tooling)

Status: Accepted (2026-10-29). Notes and assumptions: [increments/8.7-process.md](../increments/8.7-process.md). Migration `0025_process.sql` (schema `proc`).

## Context
The MBOM says what is consumed; it does not say how, where and in what order. Assumption A18 left routings to ERP and MES, but a plant still needs one controlled place for
the process plan, the work instructions that operators read, and the tools and gauges they use, tied to the MBOM revision they were written against. This data is export
controlled like the product (markings, programs), changes under release control with signatures, and goes stale when the MBOM moves on. It must never weaken the
markings model, the read filter, release locks or the audit trail, and it must work in an air-gapped plant.

## Decision
1. **Module `Keel.Process`** (references Core, Security, Audit only) owns schema `proc`: work centers per plant, tools with calibration history, process plans with revisions
   and iterations, tool-use index, review bindings. The **MBOM is reached only through `IMbomProcessSource`** (Keel.Core, implemented in Keel.Bom): the MBOM header and its
   latest Released revision, a batch readability check, and the lines of a Released or Obsolete revision with redaction. The port writes no audit events (the caller audits
   its own reads; an event written on another connection would wait for the audit chain lock the caller's transaction may already hold). Attachments are reached through
   `IViewableFileSource` (the documents module authorizes, describes, and audits denials).
2. **One plan per MBOM** (hence per part and plant), `PP-nnnnnn`, with its own marking columns. A plan is created against a Released MBOM revision and must be at least as
   restrictive as its MBOM and part (`MarkingDominance`). **Reading a plan needs Read on the plan, the MBOM and the part**: the plan filter runs in SQL, the MBOM check per row
   through the port, so lowering plan markings outside the application still cannot expose a plan whose MBOM is hidden.
3. **Content is one JSON document per iteration** (routings, operations, component assignments, steps, tool references, attachments), written and read through typed records,
   normalized (routings, operations and assignments ordered) and hashed with SHA-256 over the canonical text. A check-out opens iteration n+1 as a copy owned by the
   caller; a check-in closes it with its digest. **Triggers enforce the locks**: a closed iteration is immutable; only the open iteration of an In work revision can be edited or
   created; a revision moves only InWork to UnderReview to Released to Obsolete (or back to InWork); plan identity and the MBOM binding of a locked revision are immutable; nothing is deleted.
4. **Binding and staleness.** Each revision names the MBOM revision (and its content digest) it was written against. When the MBOM's latest Released revision differs, the plan is
   **flagged stale and left unchanged**. A working revision is rebound explicitly; a new revision of a Released plan binds to the revision released at that moment. Findings then say
   which assignments no longer match. Release is refused for a stale plan.
5. **Assignments** name an MBOM line by find number and alternate (plus the component's master id, to notice a changed component). Coverage per routing is a finding: a line nobody
   consumes (warning), a line over-assigned or an assignment to a line the bound revision lacks (errors). Hidden lines are placeholders (find number and quantity) everywhere.
6. **Release through the workflow engine** (`ProcessWorkflowGateway`, object type `process_plan`, template `process-plan-release`, e-signatures with step-up). Submitting needs a
   checked-in iteration, a current binding and no error findings (calibration overdue or retired tools, quarantined attachments, missing items); it binds the content digest and the MBOM
   revision. The signatures cover a digest recomputed from the stored content; releasing re-verifies content, binding and staleness. Releasing supersedes the earlier Released revision.
7. **Work instructions** are the steps of an operation: step, note, caution, warning (safety callouts) and quality check, each with text, tool references and attachments. An
   attachment is a pointer (document number and file id) with a mandatory caption (the text alternative); it is authorized at view time through the documents module (markings,
   download right), the vault's malware scan and quarantine apply (a quarantined file blocks release and is not shown), and a restricted attachment is a neutral placeholder.
8. **Shop floor view** (`/process/plans/{n}/shop-floor`) is a read-only page of the latest Released revision, one routing at a time, resolved under the reader's access. Its banner is
   the union of the plan, MBOM and everything shown; a print stylesheet repeats the banner top and bottom of every page and drops the application chrome (only for that page).
9. **Tools, fixtures and gauges** are marked master data with calibration interval, history (append-only) and due date. A job raises notices at 30, 14 and 7 days and when overdue, once per
   tool, stage and due date, as audit events of the system and pointer-only notifications to the watchers (ADR-0030). Work centers and resources are administrator data per plant,
   retired and never deleted.
10. **Visuals:** a routing flow diagram (layered layout of the Visuals kit; inspection points by shape and pattern) with a table alternative, and an operation-by-component matrix with a
    list alternative. Neither names a component or tool the reader cannot see. Timeline sources add plan events to the plan and MBOM timelines.

## Consequences
- New awareness kinds `process_plan` and `tool`; the `ws` and `ntf` CHECK constraints are widened by pattern, idempotently.
- No ERP event is emitted for plan release in this increment; the REST API is the read contract (`docs/process/openapi.json`).
- Plan content is not queryable by SQL beyond the tool-use index; where-used of components in plans is not provided.
