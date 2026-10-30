# ADR-0055: E-mail address verification, bounce handling and the message-broker sink

Status: Accepted (increment 9.6, migration 0034, assumptions A79-A81)
Supersedes in part: [ADR-0032](0032-platform-hardening.md) section 4 (broker sink deferred) and its "Not built" list (bounce processing, e-mail verification).

## Context
ADR-0032 shipped the e-mail channel with the user's own typed address: nobody proved the mailbox was theirs, a dead address was mailed forever, and the relay's reputation had no
protection. The broker sink was a named hole: the broker kind refused configuration. Constraints did not change: air-gapped, no CDN, pointer-only notification content, markings,
audit and CSP untouched, secrets never in the database, export control decided by the clearance profile before any sink is called.

## Decision

### 1. Address verification (A79)
- Mail goes only to an address that **equals the address the user verified** (`ntf.preference.email_verified_address`) and is not suppressed. The notification writer does not even
  queue a mail otherwise, and the channel re-checks at send time (an address that changed or bounced after queueing is skipped).
- A **one-time token** (256 random bits, base64url) is mailed as a link `/notifications/verify-email/{token}` through the existing SMTP sink. Only its SHA-256 is stored
  (`ntf.email_verification`); the token and the address never reach the audit log (a 12-character hash identifies an address there).
- The link lands on a page that asks for a **button press**: opening a link confirms nothing, so mail scanners that fetch links cannot verify an address. Redeeming needs the **same
  signed-in user** the link was made for, an unused, unreplaced, unexpired row (24 hours by default) and the address that is saved now. Every failure gives one generic answer.
- **Rate limits** (all configurable): one link per user per minute, five per user per day, three per address per day across all users. The per-address limit and a suppressed address both
  answer exactly like a sent link and send nothing, so a caller learns nothing about other accounts, other users' requests or bounce history (**no enumeration**). The preferences page
  shows a suppression notice only for an address the same user had verified.
- A changed address is not verified; saving the same address keeps its verification. A user who already had mail on before this migration must confirm once (forward-only migration; the
  alternative, grandfathering unverified addresses, would keep exactly the hole this closes).
- The verification mail is a link and constant text; the pointer-only rule for notification mail is unchanged (tests assert that no object, kind or actor text appears and that the
  notification mail never contains a verification link).

### 2. Bounces and complaints (A80)
- **REST endpoint** `POST /api/v1/notifications/email/bounces`, authenticated like the integration pull API (service account, JWT bearer, role `plm-integration`; no new role, no shared
  secret in the database). Body: up to 100 reports `{address, kind: hard|soft|complaint, status?, diagnostic?}`. The answer is totals only. Documented in
  [ops/email-verification-and-bounces.md](../ops/email-verification-and-bounces.md).
- **Mailbox reader** (optional, no network client): the relay drops mail for Keel's sender address in a directory (Maildir or spool folder); Keel parses RFC 3464 delivery status
  notifications and RFC 5965 abuse reports by hand (no MIME library). Because anybody can mail that address, a report counts only if the returned headers carry
  `X-Keel-Bounce-Tag`, an HMAC-SHA256 (secret from environment/secrets directory, minimum 32 characters, required to turn the reader on) of the recipient that every mail Keel sends
  carries. A tag seen on one mail cannot suppress another address. Unusable files move to `rejected/` and are purged after 14 days.
- **Policy:** only addresses that exist in this system are recorded (the tables stay bounded and cannot be filled from outside); soft bounces and mailbox-full (5.2.2) are logged and never
  suppress; **two hard bounces** (configurable 1 to 10) or **one complaint** suppress. A suppressed address receives nothing, including verification links.
- **Administration:** `/admin/email` lists suppressed addresses with reason, count, date and affected users, and the 100 latest reports. **Lifting** a suppression resets the counter and
  clears the verification of every user of that address (**re-verify flow**): mail resumes only after each user confirms again; an address that keeps bouncing is suppressed again.
  Administrators only; denials are audited.
- **Audit actions** (all classified as hidden in `ActivityCatalog`, never in an object's story): `notification.email.verification_sent`, `.verification_throttled`, `.verified`,
  `.verify_failed`, `.bounce_report`, `.suppressed`, `.suppression_cleared`, `.health_list` (denials), plus the existing `.sent`, `.failed`, `.preference`.

### 3. Message-broker sink (A81)
- **Protocol: AMQP 0-9-1** (RabbitMQ and compatible brokers) through **RabbitMQ.Client 7.2.0**, dual-licensed **Apache-2.0 OR MPL-2.0** (we take it under Apache-2.0), managed code only
  (transitive: System.IO.Pipelines, System.Threading.RateLimiting, Microsoft.Bcl.AsyncInterfaces, System.Diagnostics.DiagnosticSource, System.Threading.Channels, all MIT); added to
  `docs/review/legal-open-source.md` and picked up by the SBOM generator from the restored assets. **AMQP 1.0 and Kafka are not built** (Kafka's mainstream .NET client wraps a native
  library, which needs a FIPS and supply-chain review of its own; ADR-0032's note stands). The configuration names the protocol (`"protocol": "amqp091"`), and `amqp10` or `kafka`
  are refused with that statement, so a second protocol is additive.
- It is an `IEventSink` of kind `broker` in the existing outbox framework. **Everything around it is reused unchanged:** fan-out to enabled sinks, clearance profile evaluation (an event the
  profile does not clear is withheld and **never reaches the broker**; this is tested with a broker-side message count), claim with SKIP LOCKED, backoff with jitter, dead letters,
  requeue, metrics, health.
- **At least once:** publisher confirms on a mandatory publish with persistent messages; a nack, an unroutable return, a timeout or a dropped link is a retry with the outbox's backoff
  and ends in a dead letter after `MaxAttempts`. **Idempotency:** AMQP `message-id` and header `x-keel-idempotency-key` are the event id, identical on every retry, requeue and
  duplicate; the body (the signed envelope) is byte-identical per attempt and also carries the id. Other headers: event type, sequence, attempt, and with a signing secret
  `x-keel-timestamp` and `x-keel-signature` (the webhook signature scheme). The routing key is the configured key or the event type.
- **Configuration via admin settings, secrets by reference:** host, port, virtual host, exchange (or the default exchange with a queue name as routing key), optional exchange
  declaration (durable topic), user name and password **secret names** (resolved from `Keel:Integration:Secrets`, environment or secrets directory), client certificate and CA bundle names
  (files), signing secret name. Never a default account; authentication is required (password or SASL EXTERNAL with a client certificate).
- **TLS:** required by default, TLS 1.2 or 1.3, server certificate verified against the named CA bundle or the system store, no revocation lookups, optional mutual TLS. Plain AMQP needs
  `tls: none` plus `acknowledgeInsecureTransport: true`.
- **On-premises only:** the host passes the webhook host rules (allow-list or allowed networks; loopback, link-local and metadata refused unless the development switch is on) at
  configuration time, and the resolved addresses are checked again at every connection; the connection goes to the checked address and the name is used only to validate the certificate.
- **Tests against a real broker:** Testcontainers with the official `rabbitmq:4.1-alpine` image (the broker is MPL-2.0; the image is a test dependency and never ships), plain and TLS
  listeners with a throwaway CA, SASL EXTERNAL. Tests skip visibly (never pass silently) when no container can be started.

## Consequences
- Existing opted-in users stop getting mail until they confirm their address once. The release notes and the manual say so.
- Bounce handling needs an operator action to be useful: point the relay's bounces at the endpoint or the mailbox directory. Without either nothing is ever suppressed (there is no manual
  suppression); verification still protects against typos and other people's addresses.
- A broker consumer must deduplicate on `message-id`; delivery order per aggregate is kept per sink but a retry can reorder around a failed message (documented in ops/integration.md).
- Not built: AMQP 1.0, Kafka, publisher-confirm batching (one confirm per message is simple and correct), DKIM/SPF alignment, VERP return paths, an inbound SMTP listener in Keel itself.
