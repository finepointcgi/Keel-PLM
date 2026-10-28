# ADR-0019: Change management (PR, ECR, ECN, change tasks), revise-in-change and marking dominance

**Status:** Accepted · 2026-10-28 · Implements A8 for increment 1.6; builds on [ADR-0002](0002-versioned-object-model.md), [ADR-0004](0004-abac-and-markings.md), [ADR-0016](0016-part-document-links.md) and [ADR-0018](0018-workflow-and-esignature.md)

## Context
Released data is changed only through a controlled process: a problem report (PR) leads to a change request (ECR), the CCB approves it, a change notice (ECN) revises the affected parts and is released by a signed review, and change tasks track the follow-up work (A8).
The process must not leak: a reader of a change learns which objects it affects, so the change's markings have to cover those objects (ADR-0004). It must also be atomic: releasing a notice releases every new revision and obsoletes every old one, or nothing.

## Decision

### Data
- **One table for PR, ECR and ECN** (`chg.change`, type column) because they share numbering (`PR-/ECR-/ECN-000001`, one sequence each), the ten marking columns (so `AccessPolicy.BuildReadFilter` applies), a data-defined lifecycle each (`pr-default`, `ecr-default`, `ecn-default` in the shared `plm.lifecycle*` tables, transitions tagged `manual`, `workflow` or `change`), search, links (`source_id`: PR → ECR → ECN) and the workflow gateway. **Change tasks** have their own table (`chg.change_task`, `CT-000001`) and lifecycle (`ct-default`); they inherit visibility from their notice.
- `chg.affected_object`: object type, master, revision, `change_action` (`revise|obsolete|none`), the resulting revision of a `revise` row, and a `binding` flag. A partial unique index on `(object_type, revision_id) WHERE binding` allows **one open notice per revision**. Rows are never deleted (`removed_at`).
- `chg.history` (immutable), `chg.change_submission` (the frozen review snapshot, immutable), `chg.reason_code` (data).
- `plm.lifecycle_state.is_final` marks the states that end the work; "open change" queries use it instead of hard-coded state names.
- Database guards (defence in depth): no deletes or truncation; content of a change is frozen in locked states (a released notice cannot change); state changes must follow `plm.lifecycle_transition`; a Class I notice needs a Class I ECR in state Approved (insert trigger); an ECN cannot become Implemented while tasks are open; binding rows are never re-established.

### Process rules (service and data)
- Class I: the ECN must descend from an ECR that the CCB approved through the 1.5 engine (template `ecr-ccb-review`: parallel approval, rule All of the chosen CCB members). Class II: an ECN may be opened directly, or from a Draft or Approved ECR. An ECN takes the class of its ECR. An ECR is classified before it is submitted.
- PR → ECR and ECR → ECN copy title, description, priority, reason, markings (never lower than the source's) and the listed objects.
- Leaving a lifecycle without completing it (reject, close, cancel) needs a reason code from `chg.reason_code`. A PR is resolved only when its ECRs are finished and one was implemented; an ECR is closed only when its notices were cancelled.
- States reached only by review or release (`workflow`, `change` tags) are refused for manual transitions with a message that says how to get there.

### Revise in change
- Adding a Released, latest part revision to an ECN with action `revise` **creates the resulting revision at that moment** (`IChangeObjectGateway.ReviseAsync`, implemented by `Keel.Parts.PartChangeGateway`): next ASME label after the highest revision the master ever had, iteration 1 copied from the affected revision's latest iteration (description, attributes), document links copied (ADR-0016) and BOM lines copied through the new `IPartIterationParticipant.OnReviseAsync`, state In Work, the new latest revision. The old revision stays Released and unchanged.
- Engineers check the new revision out, edit and check it in as usual. While an open notice holds it, the part's own review is refused (`IChangeBindingGuard`, implemented by Keel.Change, consumed by `PartWorkflowGateway`), so release happens only with the notice.
- Revising or obsoleting an unreleased or non-latest revision is refused. Removing an object abandons an untouched resulting revision (Obsolete via the `change` transition added to the part lifecycle, the old revision latest again); one that was worked on cannot be dropped silently.

### Marking dominance (security-critical)
`MarkingDominance` (Keel.Security, pure): a change covers an object when
CUI(change) ⊇ CUI(object); jurisdiction rank (None and EAR99 = 0, other EAR = 1, ITAR = 2) of the change ≥ the object's; the program equals the object's when the object has one; the distribution statement (A < B < … < F) is at least as restrictive; and, for export-controlled objects, the change's export authorizations are a subset of the object's (so a non-U.S. reader admitted to the change by an authorization is admitted to the object by it).
The unit tests prove over a generated space of valid markings and subjects that whoever can read a covering change can read the object, and that the computed minimal cover covers, is minimal and idempotent.
- **Adding an object whose markings are not covered is refused**, naming the markings that would be needed; with the explicit flag the service **raises the change's markings to the minimal cover**, records a history entry and an audit event, and returns a visible notice. Two programs cannot be covered by one change. The actor must still be able to modify the raised change, and every assignee of an open task must still be able to read it.
- Adding an object requires Read and Modify on it; an object the caller cannot read gives the same answer as a missing one, and the attempt is audited.
- Markings can still change after an object was added (a revision in work may be tightened). **Readers see a redacted placeholder** (action only) for an object they cannot read now; impact analysis, where-used and documents redact the same way. Submitting re-checks coverage against the live markings of every affected and resulting object.

### ECN release through the workflow engine
- `ChangeWorkflowGateway` implements `IWorkflowObjectGateway` for object types `ecr` and `ecn` (new file in Keel.Change). A change has no iterations, so the engine's iteration is the **submission**: submitting freezes a snapshot (`chg.change_submission`): the change's content, the affected objects, and the exact resulting iterations with a **content fingerprint** (SHA-256 over description, attributes, document links and the data participants own, such as BOM lines). The engine binds to an id derived from (change, submission number).
- The signed content is the readable snapshot text plus, as attributes, the format version, the snapshot hash and the **live** fingerprint of every resulting iteration. Editing a resulting iteration after signing therefore changes the hash and verification reports it; the release itself refuses content that no longer matches the snapshot. Part hashes are unchanged (the part gateway is untouched).
- Submit preconditions: every resulting revision is In Work and checked in; each affected revision still Released; markings covered; a Class I notice's ECR still Approved. The resulting revisions go Under Review (locked) with the notice.
- Final approval, in one transaction: resulting revisions Released, affected revisions Obsolete (`change` transition), notice Released, bindings released, the ECR Implemented when it was the last open notice, history and audit events. Rejection or review cancellation returns the notice to Draft and its revisions to In Work. A rejected ECR becomes Rejected through the new default method `IWorkflowObjectGateway.CompleteRejectionAsync` (the engine calls it on rejection; parts keep returning to In Work).
- A Draft notice is cancelled (reason code): resulting revisions are abandoned as above, any open check-out is discarded, nothing is deleted. Under review it cannot be cancelled; the review is cancelled first.

### Impact analysis
`ImpactAnalysisService` (Keel.Change, Core interfaces only): where-used of each affected part through `IStructureImpactProvider` (Keel.Bom; parents are revisions whose latest iteration contains the part; unreadable parents are placeholders), linked documents through `ILinkedDocumentProvider` (Keel.Documents), structure comparison old versus new iteration for revised assemblies, and other open changes (read-filtered, not final) that list the same objects or any readable parent, excluding the change's own ancestors and descendants. The aggregate banner covers everything shown; the analysis is audited with counts.

## Consequences
- Parts, documents and BOM are reached only through Core interfaces implemented by their owners; an architecture test forbids Keel.Change from reading other modules' tables.
- The workflow engine and its inbox are reused unchanged apart from the `CompleteRejectionAsync` hook; a change object needs only a gateway and a template.
- Abandoned revisions consume revision letters, which keeps history honest.

## Alternatives
- A table per change type: more migrations and gateways for identical machinery; rejected.
- Creating the resulting revision when the notice is submitted: engineers could not work on it before review; rejected.
- Auto-raising markings without a flag: silently widens who must be trusted with a change, and can lock out readers; rejected in favour of an explicit, audited raise.
- Hashing only the notice text: would not prove what part content was approved; rejected.
