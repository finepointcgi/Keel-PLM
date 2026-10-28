# ADR-0006: Data-defined workflow engine

**Status:** Accepted · 2026-10-27

## Decision
Build a focused, in-process engine:
- Templates are **data** (stored JSON, versioned, admin-editable in Phase 2): nodes are `Task` (assignee role, team or participant set), `Parallel` (vote rule: all, any, or N-of-M), `Serial`, `Condition` (on object attributes), `SetState` (lifecycle transition), `Notify`, and `End`.
- Instances persist every token and task in PostgreSQL. Each advance is one transaction that also writes audit records and lifecycle transitions.
- Signing a task calls the e-signature service (ADR-0007). Signature records are immutable and link to the exact object iterations signed.
- Delegation (an out-of-office proxy with an audit trail) and escalation timers (a background scheduler using `SKIP LOCKED`) arrive in Phase 2. Phase 1 ships serial and parallel review, approve/reject, and reassign by admin.

## Consequences
Lifecycle, change and signature semantics live in one transactional model. We own the engine's correctness, so it gets heavy test coverage.

## Alternatives
Elsa Workflows (couples to its own persistence and designer), Camunda/Flowable (JVM, licensing changes, external engine across a transaction boundary), Temporal (operationally heavy for air-gapped sites).
