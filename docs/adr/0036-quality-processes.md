# ADR-0036: Quality processes: nonconformances, corrective actions, deviations and waivers

Status: accepted (increment 8.1, 2026-10-29). Builds on ADR-0004 (markings and access policy), ADR-0005 (audit), ADR-0018 (review and e-signature), ADR-0019 (change
management, the pattern of submissions that freeze a snapshot), ADR-0022 (data-defined lifecycles), ADR-0029 (timelines), ADR-0030 (notifications, activity, comments) and
ADR-0031 (list sorting). Assumptions are FEAT-QUAL-n in [increments/8.1-quality.md](../increments/8.1-quality.md).

## Context
A defense contractor has to control what it does when a part does not conform, to remove the cause, and to say in writing when it knowingly departs from a requirement. Those
three processes (nonconformance report with a material review board, corrective and preventive action, deviation and waiver) have the same shape as change management: a record
with markings, a lifecycle, owners and due dates, a signed review, links to parts and changes. The platform already has all the machinery; the decision is how to reuse it
without weakening any guarantee (markings, audit in the same transaction, no marked text in notifications or audit payloads, air-gapped, strict CSP).

## Decisions

### 1. One module, three record types, no new access model
`Keel.Quality` (schema `qual`, migration `0019_quality.sql`) references only Core, Security and Audit. It reads parts through `IChangeObjectGateway` and changes through
`IObjectAccess` (both Keel.Core, implemented by their owners), reaches the review engine through `IWorkflowObjectGateway` (one gateway per record type) and the supplier page
through `IQualityReferenceResolver` (Keel.Core): Sourcing keeps an opaque kind and number and asks the resolver what it is called, so neither module references the other.
The NCR's supplier field is an opaque reference text that is never resolved here.

### 2. Markings on every record, inherited and enforced
NCR, CAPA and deviation rows carry the ten marking columns and are read only with `AccessPolicy.BuildReadFilter`. A report's markings must cover its part's, a deviation's must
cover every part in its scope, and a link from a record to another record or part needs the same cover (changes carry their own markings and are exempt): a record is never
less restricted than what it names, so its readers can read what it names. The new-report form starts with the part's markings. Child rows (action items, analysis rows,
scope rows, links, history) carry no markings: they are read only through the readable parent, never listed on their own.
A count, a total, a position in a list, a dashboard number and a part page are all functions of rows the caller can read; the integration tests prove it with a SQL-versus-policy
oracle over nine probe users and six marking variants.

### 3. Lifecycles are data
The three lifecycles (`ncr-default`, `capa-default`, `deviation-default`) live in the shared lifecycle tables as versioned families, so a published version never changes and new
records start in the family's current version. They are view-only in administration (like the change lifecycles) because the states are bound to rules in code and triggers.
A new transition tag `system` exists for the one step only the system's own job takes (an approved deviation whose window ended becomes Expired). Database triggers refuse any
transition the lifecycle data does not contain.

### 4. Signed states are reached only through the review
Disposition of an NCR (material review board), closure of a CAPA (signed verification) and approval of a deviation or waiver are the outcomes of workflow reviews with e-signature
and step-up. A submission freezes a text snapshot (stored in `qual.submission`, immutable) of everything the signatures cover; the engine binds to an id derived from the record
and the submission number, and the snapshot hash goes into the signed content, so tampering with the stored snapshot is reported by signature verification. The states
Dispositioned, Closed, Approved and AwaitingCustomer are guarded in the database: the update fails unless an approving signature of that record exists. The content of a record
is frozen by trigger while it is under review or final.

### 5. CAPA is structured, not free text
Root cause analysis has a chain of whys (1 to 8, each with evidence), cause categories (the six fishbone categories, each cause marked root or not) and a root cause statement.
Actions have an owner and a due date (the change-task pattern: the owner is notified, starts and completes with a note). Effectiveness verification has a plan (method, criteria,
due date) and a recorded result; the record can go to the signed review only with every action finished, an analysis, a plan and an effective result, and the database refuses
review or closure otherwise. An ineffective result keeps the record open for more actions.

### 6. Deviations and waivers apply like effectivity
A scope row names a part (optionally one revision), serial numbers (a numeric range and/or a list), lots and a quantity ceiling; a record has a window of two dates and may need
the customer's approval in addition (recorded with a reference and a date after the internal review; until then the record is AwaitingCustomer and applies to nothing).
A part's page shows the records that apply to it, and an applicability query answers for a unit (serial, lot) and a date. A restriction a unit cannot be tested against (a
serial restriction and no serial given) does not match. Expiry is automatic: `QualityMaintenance` (a hosted job, also callable once) moves approved records past their window to
Expired as the system actor, audits it, notifies the requester and watchers, announces an ending window once, and reports an overdue action to its owner once.

### 7. Awareness without text
Audit events carry numbers, states, counts and codes, never a title, defect text, rationale or analysis (a test scans the module). Notifications are pointers (kind, object, actor,
reference id). History rows name no linked record (a link entry says only that a link to a part, change or report was added), because the history is shown to every reader of the
record and a link target may be unreadable to some of them. Activity catalog entries, notification kinds, watch lists, comments, the command palette, global search (prefixes
`ncr:`, `capa:`, `dev:`), recent items and the part timeline know the three kinds.

### 8. API and documentation
REST under `/api/v1/quality` with its own OpenAPI 3.1 document (`/openapi/quality.json`, committed as `docs/quality/openapi.json`, compared by a test). The review is started and
signed through the existing workflow endpoints.

## Consequences
- Nothing about cost is stored (the request is cost-free); disposition rework or repair instructions are the rationale text.
- Quality records are not emitted to the ERP outbox in this increment (no event contract was asked for).
- The lifecycles are not editable by administrators; changing them is a new lifecycle version made by a migration.
- The dashboard counts add no marking to the Home banner (a count names nothing).
