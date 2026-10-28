# ADR-0005: Tamper-evident audit log

**Status:** Accepted · 2026-10-27

## Context
NIST SP 800-171 3.3.x and ITAR record-keeping require a complete, protected record of reads, downloads, changes, signatures, and access denials.

## Decision
- Table `audit.event`: `seq bigint` (gapless, assigned under lock), `id uuid`, `occurred_at`, `actor`, `action`, `object_type`, `object_id`, `outcome` (`Success`/`Denied`/`Failure`), `correlation_id`, `payload text`, `prev_hash bytea`, `hash bytea`.
- `payload` is a **canonical JSON** document (fixed property order, UTF-8, no insignificant whitespace) containing every field, including the actor's attribute snapshot and the decision reasons. Indexed columns are copies for querying; the verifier checks them against the payload.
- `hash = SHA-256(prev_hash ‖ seq ‖ payload)`; genesis `prev_hash` = 32 zero bytes.
- Appends take `pg_advisory_xact_lock` on the chain and run **in the same transaction as the business change**, so a change and its audit record commit or roll back together. Reads and denials are written in their own short transaction.
- Protection: the app connects as `keel_app`, which has `INSERT, SELECT` on `audit.event` and nothing else. A trigger rejects `UPDATE`/`DELETE`/`TRUNCATE` for every role. Migrations run as a separate `keel_migrator` role.
- **Signed checkpoints:** periodically `(seq, hash, time)` is signed with an ECDSA P-256 key that the database never sees, and stored in `audit.checkpoint`. A DBA who rewrites the chain and recomputes hashes is still detected, because the checkpoint signature won't match.
- `keel-audit verify` (CLI and admin API) walks the chain and checkpoints and reports the first broken link.

## Consequences
- A single chain serializes audit inserts (measured in the 1.1 tests; thousands per second is ample for A11). If it ever bottlenecks, partition into N chains keyed by `hash(object_id) mod N`.
- A superuser can still disable triggers. That is detectable through the checkpoints and must be covered by DB-level auditing (pgAudit) in production; see the ops guide.

## Alternatives
- Writing to an external immutable store (WORM S3, ledger DB) only: it adds a dependency and splits the transaction. SIEM forwarding (A20) is complementary, not a replacement.
