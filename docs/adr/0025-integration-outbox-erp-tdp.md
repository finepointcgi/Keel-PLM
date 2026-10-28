# ADR-0025: Integrations: transactional outbox, ERP events and MIL-STD-31000 Technical Data Package export

**Status:** Accepted · 2026-10-28 · builds on [ADR-0011](0011-integration-outbox.md), [ADR-0004](0004-abac-and-markings.md), [ADR-0005](0005-tamper-evident-audit.md), [ADR-0008](0008-cryptography-fips.md)

## Context
ADR-0011 fixed the shape (REST plus a transactional outbox, at-least-once delivery, consumers deduplicate by event id, an outbound release check, no runtime
call to anything outside the site). Phase 4 builds it for the ERP integration ("publish released EBOM and change notices through a documented interface") and
for the Technical Data Package (TDP) export aligned with MIL-STD-31000. Both move export-controlled data out of the system, so the design is dominated by one
question: how do we make sure data leaves only toward a receiver that is cleared for it, and prove afterwards what left.

## Decision

### Outbox
- `integration.outbox` is written **in the transaction of the business change** by the owning module through `IDomainEventSink` (Keel.Core, implemented by
  Keel.Integration). A rolled-back change leaves no event; a committed one leaves exactly one. The table is immutable (trigger, and the application role has
  no UPDATE/DELETE).
- Every event carries the **markings that cover its payload** (the ten marking columns of ADR-0004), so clearance and access are decided without a join.
- Events are versioned (`event_version`); each type has a JSON Schema under `docs/integration/schemas/`, and a test validates every emitted payload.
  Adding a type (`mbom.released`) is additive: a constant in `EventTypes`, a schema file, an emission point.
- **Visibility.** Readers (dispatcher, pull API) only see events whose writing transaction is older than every in-flight transaction
  (`created_in_tx < pg_snapshot_xmin(pg_current_snapshot())`) and page by `(created_in_tx, seq)`. A cursor therefore cannot skip an event that commits late.
- Released assemblies produce `bom.published` (and, for sinks that opted in, `bom.structure.published`) inside the same transaction through
  `IBomPublicationSource` (Keel.Core, implemented in Keel.Bom). The event takes the assembly's markings; a child line those markings do not **cover**
  (`MarkingDominance.Covers`) is written as an explicit placeholder (`restricted: true`, find number, quantity and unit only: A4) and its subtree is left
  out. We chose placeholders over withholding the whole event because one restricted screw must not stop the ERP from learning the rest of a released BOM,
  and because the rule is the one users already know. The placeholder is counted in the payload and never silent. A structure larger than the configured limit
  is skipped and audited, never truncated.

### Delivery
- One `integration.delivery` row per event and enabled, subscribed sink, created with the event. Statuses: `pending`, `failed` (will retry), `delivered`,
  `withheld`, `dead`.
- The dispatcher claims due rows with `FOR UPDATE SKIP LOCKED` in a short transaction that writes a **lease**; the HTTP call or file write happens outside any
  transaction; the result is a second short transaction. A crashed dispatcher's lease expires and the row is retried, which is what at-least-once means here
  (consumers deduplicate on the event id). Graceful shutdown abandons the in-flight call, releases the lease and does not count an attempt.
- Backoff is exponential with jitter (`base * 2^(n-1)` capped, then 50-100 percent of it). After `MaxAttempts`, or on a permanent error, the row is a dead
  letter; an administrator requeues it. Metrics and a readiness check report backlog, lag and dead letters.
- **Ordering:** per aggregate within a sink (a delivery is not claimed while an earlier event of the same aggregate is pending or failed for that sink). No
  global ordering is promised. A dead letter stops blocking its aggregate; a requeue can then deliver out of order, so consumers compare `occurredAt` and the
  revision.
- Sinks: webhook (signed HTTPS POST), file drop (atomic files with a manifest and SHA-256 per file), and the `IEventSink` interface. A broker sink is Phase 4b
  and throws `NotSupportedException` naming it until then.

### Outbound release check
- `OutboundRelease.Evaluate(profile, markings)` in Keel.Security is a pure function with stable reason codes, the counterpart of `AccessPolicy` for data leaving
  the system. A `ClearanceProfile` declares the recipient category (public, USG, DoD, USG contractor, DoD contractor, foreign), whether the receiving system is
  operated by U.S. persons only, allowed jurisdictions and ECCN/USML patterns, CUI categories, programs, distribution statements and the export
  authorizations the recipient holds.
- DoDI 5230.24: A public; B USG; C USG and contractors; D DoD and DoD contractors; E DoD components; **F never released automatically**. A foreign recipient is
  cleared for B to E only through an export authorization that the object itself lists and the recipient holds.
- An event the sink is not cleared for is **withheld**: no request, no partial payload; the delivery row records the reasons, an audit event is written and a
  metric counted. Changing a profile never sends anything: only an explicit, audited administrator action ("re-evaluate") puts withheld rows back in the queue,
  and the dispatcher evaluates them again before anything is sent.
- The same function evaluates every object of a TDP against the recipient profile.

### Sinks and secrets
- Sinks and profiles are database configuration with validation. Secrets, client certificates and CA bundles are referenced **by name**; the values come from
  `Keel:Integration:Secrets:*` (files in `KEEL_SECRETS_DIR` or environment) and path settings, never from the database.
- Webhook: TLS 1.2+ verified against the configured CA bundle (system store otherwise), optional mTLS, no redirects, no proxy, bounded time. Headers
  `X-Keel-Event-Id`, `-Event-Type`, `-Timestamp`, `-Signature: v1=<hex>[,v1=<hex>]` (HMAC-SHA-256 over `timestamp.body`; two values while a secret rotates).
- **SSRF:** sink URLs must be https and match an allow-list of host names or CIDR ranges (default deny). Loopback, link-local, metadata and unspecified
  addresses are refused whatever the lists say unless a development switch is on. The check runs at configuration time and again on every resolved address
  when the connection is opened, so DNS rebinding does not help.

### Pull API
`/api/v1/integration/events` (cursor, filtered by the caller's own markings: ABAC applies as for users), `/boms/{number}` and `/changes/{number}`, for
reconciliation and backfill. The caller is a service account with the new `plm-integration` role. The role grants Read under the account's own markings and
nothing else: it is not part of `Roles.All`, so it passes no interactive policy, and it has no Download, Modify, Sign or Administer. OpenAPI 3.1 is published at
`/openapi/v1.json` (the integration surface only) and committed as `docs/integration/openapi.json`; a test fails on drift.

### Technical Data Package
- `TdpService` builds a ZIP for a part revision (structure at LatestReleased, documents linked to each part) or an established baseline (frozen items and
  pinned documents): cover sheet, `part.json`, BOM as CSV and JSON, every document file through the existing marked-download pipeline, a manifest with a SHA-256
  per file, provenance and reconciled counts, and a detached **ECDSA P-256** signature over the manifest with the public key included. Documents are organized by
  MIL-STD-31000 element categories through an administrator-editable mapping; unmapped documents go to "Other technical data" and are reported.
- **Complete or refused.** The exporter needs Read on the part and Download on every document; every object must be releasable to the declared recipient
  profile. If anything is missing the export is refused with reasons and **counts only**, never names. The package is streamed (no whole-package buffer).
- The package's distribution statement and markings are the minimal cover of its contents (the most restrictive statement; programs must agree).
- `integration.tdp_export` is immutable and stores the exact manifest text, its hash and the signature, never the package bytes. The record and its audit event
  commit before the manifest is released. `verify` checks a ZIP against its record: manifest hash, signature under a **key this installation trusts** (the key a
  package carries proves nothing), every file hash, no unlisted files.

## Consequences
- Every new outbound path must go through `IEventSink` and the outbound check; the architecture tests pin the integration module's marked-table reads and its
  isolation from other modules' tables.
- The outbox grows without bound until Phase 5 provides retention (the table is immutable by design; purging needs an audited maintenance procedure).
- A TDP can be re-verified after the fact, but not re-downloaded: marked downloads stamp the exporter and the time, so a new export is a new package.
- Remaining Phase 4 follow-ups are listed in `docs/increments/4.0-integration.md`: broker sink (4b), MBOM events once the MBOM source exists, the document
  review workflow gateway (documents are released by override today).
