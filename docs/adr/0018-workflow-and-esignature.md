# ADR-0018: Review workflow engine, electronic signatures and release

**Status:** Accepted · 2026-10-27 · Implements [ADR-0006](0006-workflow-engine.md) and [ADR-0007](0007-authentication-and-esignature.md) for increment 1.5

## Context
Release must happen only through a reviewed, signed, auditable decision, atomically with the lifecycle change, for data that
may not be shown to everyone (ADR-0004). The engine must stay object-type agnostic (documents get a gateway later) and must not
reference the Parts module (ADR-0001).

## Decision

### Engine
- `Keel.Workflow` holds a pure interpreter (`WorkflowEngine`) over a template and the persisted tokens and tasks, and a service
  (`WorkflowService`) that loads state, calls the interpreter and writes the changes, the signature, the object's lifecycle change
  and the audit events in **one transaction**. The interpreter does no I/O, so vote rules are unit-tested without a database.
- **Templates are versioned JSON** in `wf.template` (`id`, `version`, one active version per id). Nodes: `Task` (one assignee: the single user of a *slot*, or a
  *role pool*: any holder of the role who may also read the object), `Parallel` (vote rule `All | Any | NofM`, over fixed `branches` or a `fanOut` of one task
  per user of a slot), `Serial` (steps in order) and `End`. The start node is a Serial ending in `End`. Validation reports every problem at once (orphans, shared
  nodes and cycles, unreachable End, rule/slot mismatches, Phase 2 node types by name). A running instance stores a copy of the definition it started with.
- **Vote semantics:** a Parallel node approves when `required` approvals are in (All = M, Any = 1, N-of-M = N) and rejects as soon as `required` is unreachable
  (rejections > M − required). The remaining branches are then *skipped* with their open tasks (short circuit). A rejected node rejects its Serial, hence the
  instance. With the seeded rule `All`, any single rejection returns the part to In Work.
- **Persistence:** `wf.instance` (bound to the exact revision and iteration, marking snapshot columns), `wf.token` (one per activated node; Serial step, Parallel branch count),
  `wf.task`, `wf.signature`, `wf.event` (human-readable history; the tamper-evident record is the audit chain). A restart loses nothing.
- **Concurrency:** every mutation locks the instance row first and then the object row (`SELECT … FOR UPDATE`), the same object row `PartService` serializes on, so signers,
  cancellation, edits and starts queue instead of racing; the lock order has no cycle. The database adds a unique partial index (one active instance per object),
  one signature per task, and immutability triggers on signatures, history, finished tasks and completed instances.

### Lifecycle integration
`IWorkflowObjectGateway` (Keel.Core) is the only way the engine touches an object: find (latest revision by key, or a bound revision), begin review, complete release,
return to work, read an iteration's content. `Keel.Parts.PartWorkflowGateway` implements it with plain SQL in the engine's transaction and checks that each
transition it takes exists in the lifecycle data and is tagged `workflow`. Migration 0006 tags **both** In Work ⇄ Under Review as `workflow` (release already was), so the
manual path can no longer enter, leave or release a review; the audited administrator "set state" override remains for corrections. If an administrator overrides a part out
of review, the engine detects it (signing returns a conflict) and *cancel* closes the orphaned instance.

### Electronic signatures
- **Meanings** are data (`wf.reason_code`): Reviewed, Approved, Approved with comments, Rejected. A comment is required for Rejected and for "with comments". Each code applies to
  review and/or approval tasks.
- **Step-up (A9):** signing requires `auth_time` within 300 s (configurable, 30 to 900), checked from the token. The service returns a distinct *step-up required*
  outcome after validation and authorization (so nobody is sent to the IdP for input that would be refused anyway). REST: HTTP 401 with an RFC 9470 challenge
  (`WWW-Authenticate: Bearer error="insufficient_user_authentication", max_age="300"`). UI: `/account/step-up?returnUrl=…` is an OIDC challenge with `prompt=login`
  and `max_age=0`; the returning principal replaces the cookie principal only if it has the **same `sub`** and an `auth_time` (otherwise the attempt is audited as denied and
  refused); the absolute session lifetime is *not* extended. `auth_time` is captured from the id token at every sign-in. The signer's comment and chosen meaning are kept in
  server memory for ten minutes, keyed by task and user (one-time), never in the URL.
- **Record and hash:** the signature stores signer, org, meaning, comment, UTC time, auth_time, the object identity, the iteration id, the marking banner and a marking snapshot, a
  **content hash** (SHA-256 over canonical JSON of description, attributes and markings; BOM and file links join it in 1.3/1.4) and a **signature hash** (SHA-256 over canonical JSON of
  object identity, iteration id, content hash, meaning, decision, signer, comment and timestamp). Timestamps are hashed at the microsecond precision the database keeps.
  `VerifyInstanceAsync` recomputes both from the stored iteration and flags changes to the iteration, to the record, or to the identity; a later change of markings is reported
  separately (it is not tampering). The hash is also in the `workflow.sign` audit event. SHA-256 only (ADR-0008).
- **Manifestation:** "Signed by <name> (<username>) on <UTC time> — <meaning>", on the task page, the workflow page and the part's release history.

### Participants, eligibility and separation of duties
- There is no local user table, so `identity.user_directory` records each user the application has seen (UI sign-in, throttled API calls) with the attribute snapshot needed to
  evaluate `AccessPolicy` (roles, U.S. person, citizenship, org, programs, export authorizations, CUI categories). Participants must exist there, hold the slot's role (`plm-viewer`
  means viewer or any higher role; auditors never review or sign) and be eligible to read the object under its markings, evaluated from the snapshot (**A50**). The same checks run
  again at sign time against the **live token**; a task whose assignee has since lost eligibility is shown redacted and cannot be signed.
- The submitter cannot be a participant and cannot sign (**A51**).

### Visibility
Nothing about an object (number, name, markings, content) is shown to someone who cannot read it: tasks, instances and verification return 404 (or a redacted placeholder for the
assignee's own task), every list is checked against the live markings, the admin list uses `BuildReadFilter` over the instance's marking snapshot plus a live check, and
administrators get no bypass. Directory pickers return only username, display name and roles.

## Consequences
- Another object type needs a gateway and a template with that `objectType`; the engine, UI inbox and signatures are reusable.
- Eligibility is as fresh as each user's last sign-in until sign time; an IdP role change is enforced at signing, not at assignment.
- Delegation, escalation timers, Condition/SetState/Notify nodes and the template editor are Phase 2 and throw `NotSupportedException` (or are rejected by validation, naming the phase).

## Alternatives
- Locking only the instance (not the object): lets a manual edit race a release; rejected.
- Hashing only the iteration id: a signature would not detect content tampering; rejected.
- Per-slot directory lookups at sign time against the IdP: needs a live admin connection from an air-gapped app; rejected in favour of snapshot at assignment plus live token at signing.
