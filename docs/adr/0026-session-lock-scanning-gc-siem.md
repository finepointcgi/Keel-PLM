# ADR-0026: Idle session lock, upload malware scanning, vault garbage collection, SIEM forwarding

**Status:** Accepted · 2026-10-28

## Context
Four gaps were recorded in `docs/ops/nist-800-171-mapping.md` when Phase 1 closed: no session lock after inactivity (NIST SP 800-171
3.1.10, with 3.1.11 session termination), no malware scanning of uploads (3.14.2), no garbage collection of orphaned vault objects
(ADR-0015), and no forwarding of the audit log to a SIEM (3.3.x, ADR-0005, assumption A20). All four have to hold in an air gap, with no
outbound calls from the product, and must not weaken the CSP or the cryptography rules (ADR-0008, ADR-0014).

## Decision

### 1. Idle session lock (3.1.10) and termination (3.1.11)
- **The server is authoritative.** `SessionLockRegistry` (singleton, in memory) keeps one entry per UI session, keyed by `keel_session_id`, a claim
  minted at sign-in and carried across a step-up. It holds the last activity and a phase `Active -> Locked -> Terminated`. A one-second timer
  (`SessionLockService`, a hosted service) locks a session that was idle for `Keel:Session:IdleLockMinutes` (default 15) whether or not any circuit or
  browser is connected, and terminates one that stayed locked for `LockedTerminateMinutes` (default 15). The browser only reports activity.
- **Browser script** (`wwwroot/session-lock.js`, an ES module from the application's own origin: no inline code, CSP unchanged) reports pointer, key,
  touch, wheel and scroll activity at most every `ActivityThrottleSeconds` through JS interop, asks for a refresh on `visibilitychange` (not counted as
  activity) and traps keyboard focus in the lock screen.
- **The lock replaces the page.** While locked, `MainLayout` renders `LockedPanel` instead of `@Body` and hides the navigation, so the page's components
  are disposed: nothing of it is left in the DOM or the circuit (verified in a browser). The panel is a modal `role="dialog"` with `aria-modal`, labelled and
  described, with a focus trap and native buttons. A warning (`role="alert"`, with "Stay signed in") shows during the last `WarningSeconds`. The layout has
  "Lock now" and a "Session expires in N min" hint (the absolute lifetime, A-claim `keel_session_expires`).
- **The circuit and the HTTP pipeline fail closed.** `CurrentUser.GetAsync` (the first line of every UI service call) throws `SessionLockedException`
  while the session is locked or cannot be identified, and the authentication state carries a `keel_session_locked` claim. A middleware refuses every cookie
  request of a locked session except the application shell (GET of a Razor component page), framework files, `/account/*`, health and metrics, with
  **423 Locked**. It is deny-by-default, so cookie endpoints added later (exports, downloads) are covered without being listed. Bearer-token API
  requests have no session and are governed by the token lifetime.
- **Unlocking is a fresh login.** The lock screen sends the user through the existing step-up (`prompt=login`, `max_age=0`), which already requires the
  same `sub`. The OIDC callback (`OnTokenValidated`) releases the lock of the carried session id after that check: nothing else releases it, and input in
  the browser cannot. The user returns to the same address; the page is rebuilt from the services under the user's access (CMP-3: unsaved input is not kept).
- **Termination.** A session locked past the grace is marked `Terminated`. Its cookie is refused by `OnValidatePrincipal` from then on (any tab, any
  client), circuits navigate to `/account/session-ended`, which signs out the cookie and the IdP session only for a session the registry ended (it cannot be
  used to log somebody out), and the next visit needs a full login.
- **Audit:** `session.lock` (reason `idle` or `manual`), `session.unlock` (via `step-up`), `session.lock_expired` (the lock grace passed) followed by
  `session.terminated_idle`. Objects are referenced by a hash prefix of the session id, never the id.
- **Scope of the registry (superseded by ADR-0032: the registry is now database-backed and shared by all replicas; the text below is the original decision).** One node, or sticky sessions (which Blazor circuits and the Data Protection key ring already require). A restart or a second
  replica does not know a session's lock state: it starts active with a fresh idle clock. A database-backed registry (and a cluster-wide lock) is the
  Phase 5 step for multi-replica production; CMP-4 states the limits.
- **Configuration refuses unsafe values.** `IdleLockMinutes = 0` is refused when `RequireFips` is true or the environment is Production; below one minute
  is only for development and tests.

### 2. Malware scanning of uploads (3.14.2)
- `IUploadScanner` / `IUploadScan` in Keel.Core: begin, write chunks, finish -> `Clean | Infected(signature) | Error(reason)`. `ClamdScanner` (Keel.Documents)
  speaks the clamd INSTREAM protocol (NUL-terminated `zINSTREAM`, chunks with a 4-byte big-endian length, zero terminator) over TCP or a UNIX socket, with
  connect, IO and result timeouts; `NoScanner` never claims a stream is clean (it always ends in `Error`).
- **Scanning happens during the upload.** `ObjectVault.PutAsync` feeds each buffer it hashes to the scan session (no second pass, no whole-file buffering);
  the verdict is awaited **before** the object is published under its content address (for large files, before the server-side copy out of `staging/`).
  An infected result throws, nothing is stored under the content address, the staging object is removed, and the document service answers 422 with the
  signature name (to the uploader who passed the policy check) after writing `document.upload_infected` (Failure: file name, size, SHA-256, signature).
- **Modes** (`Keel:Vault:Scanning:Mode`): `Required` refuses an upload (503 with `Retry-After`, audit `document.upload_refused`) when the scanner is down,
  drops mid-stream, errors, or the file is larger than `MaxScanBytes`; `BestEffort` accepts it recorded as `unscanned` (or `skipped` when too large);
  `Disabled` records `skipped`. `Disabled` is refused at startup when `RequireFips` is true unless `AcknowledgeUnscannedUploads` is set, so production
  cannot silently run without scanning. A scanner on another host than the application needs `AllowRemotePlaintext`: INSTREAM is not encrypted
  (loopback sidecar or UNIX socket recommended).
- **Coverage is recorded.** `plm.file_scan` is an append-only log per content hash (status, scanner name and version, signature database date, source
  `upload|rescan|import`); the view `plm.document_file_scan_status` gives the newest status per file row; `GET /api/v1/admin/vault/scan-coverage` counts them.
- **Rescan and quarantine.** An administrator's rescan streams stored files back through the scanner (verifying hash and size on the way). Content found
  infected is **quarantined per document** (`plm.file_quarantine`: document + content hash, released only by an administrator with a reason): downloads of
  those bytes through that document answer 409 and check-in refuses them, while bytes and trail stay. The flag is per document and content, not per vault
  object, because another document sharing the object may be fine, and a check-in copies file rows (CMP-5).
- **Imports.** `keel-import` builds its vault with the same scanner when `Keel:Vault:Scanning:Mode` is not `Disabled`; an infected or unscannable file is a
  rejected object (quarantined in the batch report). The seed tool is exempt (it builds an unscanned vault).

### 3. Vault garbage collection
- Document file rows are immutable and never deleted, so true orphans come from uploads whose metadata never committed and from `staging/` leftovers.
  `VaultGarbageCollector` lists the bucket page by page and deletes an object only when it is (a) named by no reference source, (b) older than the grace
  period (default 7 days, at least 1 day enforced) and (c) re-checked inside a transaction that holds the per-key advisory lock.
- **Reference registry.** `VaultReferences.Sources` lists the columns that keep objects alive (`plm.document_file.sha256`,
  `plm.document_checkout_file.sha256`); the sweeper's SQL is built from it. `VaultReferences.NotVaultContent` lists every other hash column with the reason it
  is ignored. An integration test reads the live schema and fails when a migration adds a hash or digest column that is in neither list, so a new table
  that holds vault content cannot be forgotten.
- **The race with uploads** is closed by one lock and a marker. The vault calls `IVaultPendingGuard.MarkPendingAsync` once the content address is known
  and before it looks for or writes the object: under `pg_advisory_xact_lock(hashtextextended('keel-vault:' || key))` it records a pending marker
  (`vault.pending_upload`, lease 2 hours). The sweeper takes the same lock for its final reference check, marker check, age check and delete. So an upload
  of identical bytes during a sweep either leaves a marker the sweeper sees (the object is kept), or waits for the sweeper to finish and then finds the
  object gone and writes it again. The concurrency tests prove both orders, including an upload that arrives while the sweeper is inside its critical section.
- **Modes and controls.** Dry run is the default and deletes nothing; delete is `keel-vault gc --delete --confirm-bucket <bucket>` or the hosted service with
  `Keel:Vault:Gc:Mode=Delete` (the service is off by default). A run is capped (`MaxDeletionsPerRun`, counted over candidates in both modes so a dry run
  previews exactly what a delete run does), resumable with a continuation token, single-instance (a session advisory lock), and produces a report hash over
  the candidate keys and the counts: the hash a reviewer saw in the dry run equals the hash of the delete run when nothing changed. One audit event per run
  (`vault.gc`: counts, bytes, report hash, never keys) and a row in `vault.gc_run`.
- **Surfaces.** `keel-vault` (new console project, no implicit database target: the connection must be named, `--use-dev-stack` is explicit), an admin
  page section with the last reports and a **dry-run button**. Deleting is deliberately **not** offered in the UI: it is a shell command or a configured
  service, so a click cannot delete export-controlled content (CMP-7).

### 4. SIEM forwarding (3.3.x, A20)
- New module **Keel.Siem** (references Keel.Core, Keel.Security, Keel.Audit only). A background service reads `audit.event` by sequence number, separate from
  the audit writers (it never blocks an append) and forwards to configured destinations: **RFC 5424 syslog over TLS with RFC 5425 octet-counting** (TLS 1.2 or
  1.3, CA bundle or system trust, optional client certificate for mutual TLS, no revocation check in an air gap) and an **NDJSON file** for forwarders (Splunk
  UF, filebeat, rsyslog imfile), written with one append per batch and reopened per batch so external rotation is safe. UDP is not supported
  (`NotSupportedException` naming why); plain TCP needs `AllowInsecureTcp` and is refused when `RequireFips` is true.
- **What a message carries.** The structured-data element `[keel@<PEN> seq hash prevHash id actor action outcome object corr]` (PEN is the IANA
  documentation placeholder 32473 until the organization sets its own), the event time, hostname, app name, process id, and as text the action, outcome,
  object reference and actor id. **Never** object names, descriptions, markings, details or the actor's attributes: those stay in the audit payload, which is
  not forwarded. Severity: Failure = 3 (err), Denied = 4 (warning), everything else 6 (info); facility configurable (default 13, log audit).
- **Tamper evidence downstream.** Each message has the event's `hash` and `prevHash`, so the SIEM can check that sequence numbers are gapless and every
  `prevHash` equals the previous event's `hash`. It cannot recompute a hash (the payload is not forwarded), so the forwarder does that: it recomputes each
  hash from the stored payload as it reads, and checks the chain, and raises an in-band critical `siem.chain_break` message, a metric, a persisted flag, an
  audit event and a degraded health check on a gap, a changed link, a row that no longer matches its hash, or a tail it already forwarded that has since been
  deleted or rewritten. The last check **closes the "deletion after the last checkpoint" gap** of ADR-0005: events newer than the last signed checkpoint
  that someone deletes are still in the SIEM, and the forwarder reports that they are gone.
- **Delivery.** `siem.cursor` (migration 0013) holds, per destination and stream (`audit`, `checkpoint`), the last forwarded sequence, its hash and a count;
  the cursor advances only after the destination took the batch: at least once, in order, resuming after a restart. A bounded queue between the reader and
  the sender (`QueueBatches`) applies backpressure; the backlog stays in the database. Retries use exponential backoff with jitter and reconnect. Checkpoint
  creations are forwarded as `audit.checkpoint` messages; `audit.verify` results are ordinary audit events and are forwarded with their outcome.
- **Limit.** TCP gives no acknowledgement. The sender refuses to reuse a connection the peer closed and checks the socket after each write, which catches a
  collector that is gone, but a collector that accepts data and loses it before persisting it is only detected by the SIEM's continuity check (CMP-9). The
  cursor can be rewound to replay (docs/ops/siem-onboarding.md).

## Consequences
- A production host must now state its position on scanning (a scanner, or an explicit acknowledgement) and cannot turn the idle lock off. Existing
  cookies from before the lock existed have no session id and are refused until the user signs in again.
- The in-memory lock registry is the one single-node component; multi-replica production needs sticky sessions (already required) and, for restart
  resilience, the Phase 5 database-backed registry.
- ClamAV signature updates in an air gap, the SIEM itself and its alerting, pgAudit, and the operator's decision to run the sweeper in delete mode remain
  operator responsibilities (docs/ops/compliance-features.md).

## Alternatives
- A client-only idle timer: bypassable by anyone with the browser dev tools, and dead when the tab is suspended. Rejected; the client only reports.
- Hiding the page under an overlay: the data stays in the DOM and the circuit. Rejected; the page is not rendered while locked.
- Scanning after the upload (a second pass over the vault object): doubles IO for gigabyte files and leaves infected bytes under a content address that
  other uploads might reference in between. Rejected.
- Per-object quarantine in the vault: one document's infection would block unrelated documents that share the bytes, and the vault is immutable by design.
- Deleting in the UI, or deleting without the key lock: a misclick or a lost race would destroy export-controlled content. Rejected.
- UDP syslog or syslog without TLS: silent loss and no confidentiality for an audit trail. Rejected.
