# ADR-0038: Requirements and the digital thread

Status: accepted (increment 8.3, 2026-10-29). Builds on ADR-0002 (versioned objects), ADR-0004 (markings and access policy), ADR-0005 (audit), ADR-0018 (review and e-signature),
ADR-0019 (change management, marking dominance), ADR-0024 (impact providers), ADR-0029 (relationship pictures), ADR-0030 (notifications) and ADR-0031 (list sorting).
Assumptions are FEAT-REQ-n in [increments/8.3-requirements.md](../increments/8.3-requirements.md).

## Context
Parts, documents and changes say what the product is and how it changed; they do not say why. Programs need the requirements, the parts that implement them, the evidence
that they are met, and a way to see what a change touches. Requirement text is often the most sensitive data a program holds, so the same guarantees as every other module apply
(markings on each row, audit in the same transaction, nothing marked in notifications, audit payloads or logs), and a restricted requirement must never be discoverable
through a count, a link list, a graph or an impact report.

## Decisions

### 1. A specification is a revisioned object; a requirement is a permanent identity
`Keel.Requirements` (schema `req`, migration `0021_requirements.sql`) references only Core, Security and Audit. A specification (`RS-nnnnnn`) has revisions (A, B, ...) and iterations
like parts and MBOMs, a data-defined lifecycle (`reqspec-default`, view-only in administration) and is released through the workflow engine
(`IWorkflowObjectGateway`, template `reqspec-release-review`). A requirement keeps its key (`R-0001`) for ever; each iteration holds one immutable version row per requirement
(content, place in the hierarchy, its own markings). Requirements are never deleted, only withdrawn, so links and verification records never dangle. An edit is a batch applied to
the latest iteration under a lock of the specification row and produces exactly one new iteration (all or nothing); the caller names the iteration it read and a newer one is a
conflict. Released and Under Review revisions are locked by the service and by database triggers (even for the table owner); a release needs a review binding for the latest iteration.
The review signs a digest recomputed from the live rows, so a row edited after signing makes the release refuse. The signed text carries counts and the digest, never requirement text.

### 2. Markings on the set and on every requirement, redaction in place
The specification and each requirement version carry the ten marking columns. A requirement is at least as restrictive as its specification (`MarkingDominance`, with an explicit
"raise" confirmation), so whoever reads a requirement can read its set. Reads join the version rows through `AccessPolicy.BuildReadFilter`. In a structure a requirement the reader
cannot read is a placeholder with its outline number and depth only (the standard redaction of ADR-0004: BOM lines, where-used). Every number the module returns (requirements in a
specification, coverage, matrix cells, trace rows) is computed over what the reader can read, so no total reveals a restricted item; the SQL oracle tests prove it for six subjects.

### 3. Links are admin-defined and visible only when both ends are
Link types are administrator data (`req.link_type`: code, labels, target kind, coverage role). Five system types ship: derives-from and refines (requirement to requirement),
allocated-to (part), verified-by (document) and changed-by (change). A link is read only when its source requirement and its other end are readable; an unreadable end is a placeholder
with no type, kind, number or name. The other end is joined through the read filter of its own table (parts, documents, changes) or `req.current_requirement`.

### 4. Suspect links
A link records the content hash of its source requirement, of a requirement target, and the revision of a part or document target as last reviewed. A link is suspect when the source or a requirement
target changed (marked in the transaction of the edit, one audit event per link), or when a part or document target has a newer revision (visible at once on every read, persisted with an
audit event by the sweep: an administrator action and an optional background job). A suspect link stays suspect until someone with modify access reviews it (audited).

### 5. Verification is a record, not a flag
Verification records (pass, fail, waived) are immutable and refer to the content hash of the requirement they judged: the newest record for the current content decides, records for
older content are stale. A formal verification needs the approver role, a fresh authentication (the same step-up as review signatures) and evidence documents the signer may read; it
carries its own signature row (`wf.signature` is bound to workflow tasks). Notes and waiver reasons are marked data: stored, never audited or notified.

### 6. Coverage, matrix, graph
Coverage (allocation, planned and recorded verification, gaps, suspect links) is computed from one trace of a revision; coverage within a baseline counts allocations to parts that the reader can
read in the baseline (`IBaselineMembership`, implemented by Keel.Bom, the only module that reads baseline items). The matrix and the graph are built from the same trace. The graph reuses the visuals kit
(`ImpactGraphView`, new node kinds, its own legend, table alternative, restricted nodes).

### 7. The digital thread
A Trace tab on parts, documents and changes lists the requirements that trace to the object with their link and verification state (restricted ones as placeholders). Change impact analysis
lists the requirements of each affected part through `IRequirementImpactProvider` (Core), additive like the MBOM impact.

### 8. Exchange
Import and export of CSV, spreadsheet CSV and a ReqIF-style XML. An import is previewed (a plan with a digest) and applied only with the digest of its preview, as one iteration; a row naming a requirement the reader cannot
read is a new requirement, exactly like one naming an unknown id. Exports contain only readable requirements and carry the marking banner. The XML reader refuses DTDs; spreadsheet cells are guarded against formula injection.

### 9. Integration
`reqspec` is an awareness kind (follow, comment, recent, favorite) and a workspace kind; notifications are pointer-only (`INotificationSink`), the activity catalog and the timeline source cover the specification,
search finds specifications (`rs:`), the palette has "Go to Requirements", the REST API and its own OpenAPI document (`/openapi/requirements.json`, committed as `docs/requirements/openapi.json`) mirror the services.

## Consequences
- Requirement text appears only in responses to callers the policy lets read it, and nowhere else (audit, notifications, logs, signed review text); architecture tests enforce the audit rule.
- Because requirements are never removed, a specification only grows; withdrawn requirements stay visible and out of coverage.
- Coverage is the reader's coverage: a requirement whose only allocation is a part the reader cannot read shows as unallocated to that reader. This is deliberate (no leak) and documented in the page.
- A future quality module can add its records as further link types without changing this module.
