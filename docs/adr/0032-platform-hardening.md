# ADR-0032: Platform hardening for production (gap wave B)

Status: accepted (2026-10-29). Builds on ADR-0005 (audit), ADR-0008 (FIPS), ADR-0011 and ADR-0025 (outbox, sinks), ADR-0026 (idle lock) and ADR-0030
(notifications). Notes and assumptions GAP-B-1 to GAP-B-14: [7.5-platform.md](../increments/7.5-platform.md). Migration `0016_platform_hardening.sql`.

## Context
Four things stood between the first production deployment and a second replica or a second year of operation: the idle lock lived in one process's memory, the
outbox grew without bound, notifications could not leave the application, and the broker sink was a named hole. The constraints did not change: air-gapped (no
external calls), FIPS posture unchanged, access and marking rules and the audit log never weakened, no marked data in audit payloads or logs.

## Decisions

### 1. The lock registry lives in PostgreSQL; the server stays authoritative
`identity.session_lock` holds one row per UI session, keyed by the SHA-256 of the session id (the id itself is never stored). Every transition is one conditional
`UPDATE ... RETURNING` (`active -> locked` when idle or on request, `locked -> active` on re-authentication, `locked -> terminated`), so any number of nodes can tick
and each transition happens, and is audited, exactly once. The state machine in `SessionLockRegistry` is unchanged; it now talks to an `ISessionLockStore`
(`PostgresSessionLockStore` in the application, `InMemorySessionLockStore` in unit tests and development; the memory store is refused in production). Reads on the request
path come from a cache that every node refreshes from the table each tick (one second), so a lock made on node A is enforced on node B within about a second. Activity
reports update the cache at once and are written to the table on the next tick. Before the first load completes an unknown session is read from the table, and a table
that cannot be read means locked: the registry fails closed. A restart no longer unlocks anything. Sticky sessions are still required for Blazor circuits and the Data
Protection key ring; they are no longer needed for the lock.

Rejected: reading the table on every request (a round trip per request for a decision that changes once in minutes); LISTEN/NOTIFY (needs a dedicated connection per node
and a fallback poll anyway); Redis (a new component).

### 2. Retention of delivered outbox rows: a single audited path, off by default
The outbox was immutable by trigger, so retention cannot be an ordinary `DELETE`. Migration 0016 keeps `UPDATE` forbidden and allows `DELETE` of outbox and delivery rows
only inside `integration.purge_delivered_outbox`, a `SECURITY DEFINER` function owned by the migrator that the application role may execute (it has no `DELETE` privilege on either
table). The function selects, oldest first and in batches, events that occurred before the cutoff, whose every delivery row is `delivered` (a pending, failed, withheld or dead
row blocks the event, and the foreign key makes the delete fail if one appears), whose last delivery is also older than the cutoff, and that the notification fan-out cursor
has passed. Two guard triggers stop any other deletion, for the owner and a superuser as well. Events that never had a delivery (written while no sink was enabled) are kept unless
the operator sets `IncludeUnroutedEvents`. `OutboxRetentionService` runs a batch in one transaction together with its audit event (`integration.outbox_retention`: counts and
dates, never an object) and an append-only `integration.retention_run` row, so a purge that cannot be audited is rolled back. The job is off until `RetentionDays` is set (7 or more);
`DryRun` records what would go. Several nodes may run it (advisory lock). Receivers that backfill with the pull API must do so inside the retention window.

### 3. E-mail notifications over an on-premises SMTP relay
- **Pointer-only.** The channel interface has no kind, object or text: `INotificationChannel.DeliverAsync(recipientSub)`. The mail is a constant subject ("You have a
  notification in Keel PLM") and a constant body with two links (`/notifications`, `/notifications/preferences`). It carries no count, kind, number, name, actor or marking;
  the link lands on the notifications page, which re-authorizes every row with the viewer's live attributes (ADR-0030). A unit test pins the text and the signature.
- **Opt-in, per user, through the existing preferences.** `ntf.preference` gets `email_enabled` (default false) and `email_address`; `ntf.kind_preference` gets `email`
  (default true: which kinds also send mail once the user opted in). Nothing is sent to anyone who did not switch it on. A user can switch off the mail of a kind that
  cannot be switched off in the application, because mail is a convenience and the bell is what cannot be missed.
- **The address is the user's own entry.** The directory snapshot holds no e-mail (the IdP's e-mail claim is not mapped), so the user types it on the preferences page. It is not
  verified. To keep mail inside the organization `AllowedRecipientDomains` is mandatory when the channel is on, the database refuses addresses with whitespace or header
  characters, and the address is never written to the audit log, the queue's error text or the log.
- **Queue.** The writer queues one row per user in `ntf.email_queue` in the notification's transaction (at most one pending per user; further notifications add nothing), held back
  until `MinIntervalMinutes` (10) after the last mail. `EmailDispatchService` claims rows with `SKIP LOCKED` on any node, backs off, and ends as `failed` after `MaxAttempts` or a permanent
  refusal. Sent mails and final failures are audited (`notification.email.sent`, `notification.email.failed`; user id and attempts only). The relay's error text is scrubbed of addresses.
- **Own SMTP client, no new dependency.** `SmtpSender` (about 250 lines) speaks EHLO, STARTTLS or implicit TLS, AUTH PLAIN/LOGIN and DATA. `System.Net.Mail.SmtpClient` is
  discouraged by its maintainers, has no implicit TLS and validates certificates through a process-wide callback; MailKit would add MimeKit and BouncyCastle for one plain-text
  message. TLS 1.2 or 1.3 only, certificate and name verified (optionally against `CaBundlePath`), no revocation fetches, a fresh reader after STARTTLS (no injected plaintext).
- **TLS required unless acknowledged.** `Security` defaults to `StartTls`; a relay that does not offer it is refused, never used in clear text. `None` is a startup error unless
  `AcknowledgeInsecureTransport=true`. Credentials are sent only after TLS (or under that acknowledgement).
- **No external hosts by default.** The relay must be loopback, private (RFC 1918, unique-local, link-local) or listed in `AllowedHosts` / `AllowedNetworks`. Literals are checked at
  startup; names are resolved at connect time, every resolved address is checked, and the connection is made to the address that passed (no rebinding between check and use).
  The cloud metadata address is never accepted.
- **Tests** use an in-process SMTP server in `Keel.TestSupport` (`TestSmtpServer`: plain, STARTTLS, implicit TLS, credentials, refused recipients, temporary failures, a relay that hides
  STARTTLS), with the existing throwaway CA.

### 4. Broker sink: deferred; the spool directory is hardened instead
_Update: the AMQP 0-9-1 broker sink was delivered by [ADR-0055](0055-email-verification-bounces-and-broker-sink.md); the reasoning below explains why it waited, and Kafka and AMQP 1.0 remain unbuilt._
No broker client is added. The candidates and why none is chosen now:

| Option | Licence (to be confirmed by procurement at selection time) | Weight | Issue |
|---|---|---|---|
| Kafka (`Confluent.Kafka`) | Apache-2.0 wrapper over `librdkafka` | native library with its own bundled crypto | Breaks the FIPS posture (ADR-0008): the TLS and hashing would not be the host's validated modules. Excluded until a FIPS review. |
| AMQP 0-9-1 (`RabbitMQ.Client`) | Apache-2.0 and MPL-2.0 | moderate, managed | No site has named RabbitMQ; protocol and exchange topology are unknown (A14). |
| AMQP 1.0 (`AMQPNetLite`) | Apache-2.0 | small, managed | Same. |
| MQTT (`MQTTnet`) | MIT | small, managed | Same; MQTT is not what ERP/MES integrations on premises usually use. |

The deciding facts: no target broker is known; a correct broker sink needs a broker to test against (publisher confirms, reconnects, mTLS), which means a broker image in the
offline bundle and in the test stack that nobody has asked for; and the outbox already hands every event, with its clearance decision, to an `IEventSink`, so adding a sink later
is additive. Until a site names a broker and protocol, the supported on-premises alternative is the **file-drop spool directory**, which most brokers can ingest (file
connectors, a watcher) and which already respects clearance profiles because withholding happens before any sink runs. It was hardened: file names derive from sequence
numbers (a retry rewrites the same file), write-through plus rename plus manifest as the commit marker (as before), stale temporary files are removed, and the spool is bounded:
at `maxPendingFiles` (default 10,000 committed files) the sink answers the new `Defer` outcome, which waits without counting an attempt and never dead-letters, while
`keel_integration_spool_files{sink}` shows the depth. The broker kind keeps refusing configuration, naming Phase 4b.

## Consequences
- Migration 0016 (plain SQL, forward-only): `identity.session_lock`, outbox retention function, triggers and `integration.retention_run`, e-mail columns and `ntf.email_queue`.
  The outbox is still immutable for everyone except the audited function.
- `Keel.Web` gained `SessionLockStore.cs` and the registry now needs a database for production (`Keel:Session:Store=Database`, the default); `Keel.Notifications` gained
  `Email/` and one extra statement in the writer; `Keel.Integration` gained `OutboxRetention.cs` and the spool changes. No new package.
- Operators decide: the retention period, the SMTP relay (and its CA bundle), the recipient domains, and the spool consumer.
- Not built: bounce processing, e-mail address verification, e-mail sourced from the IdP, OAuth or NTLM for SMTP, a broker sink.
