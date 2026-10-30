# Integration runbook

Applies to the outbox, ERP sinks and Technical Data Package export (ADR-0025). Settings are in [configuration.md](configuration.md#integration-keelintegration-adr-0025-runbook).

## Enabling delivery
1. Apply migrations (0012). Nothing is delivered until a sink exists and the dispatcher runs.
2. Run the dispatcher in **one** replica (or several: the claim is safe with many) by setting `Keel__Integration__DispatcherEnabled=true`.
3. Provision secrets in the secrets directory: `Keel__Integration__Secrets__<name>` (HMAC secrets), and set path settings for certificates and CA bundles
   (`Keel__Integration__ClientCertificates__<name>`, `Keel__Integration__CaBundles__<name>`). Mount the files read-only.
4. Allow the receiver: `Keel__Integration__AllowedHosts__0=erp.site.example` and/or `Keel__Integration__AllowedNetworks__0=10.20.0.0/16`. Default is deny.
5. For file drops, mount a writable volume and set `Keel__Integration__FileDropRoot=/var/lib/keel/outbound`; a sink's directory is relative to it. Compose:
   `volumes: [keel-outbound:/var/lib/keel/outbound]`; Kubernetes: a PersistentVolumeClaim mounted at the same path, writable by the service account only.
6. Create profiles and sinks in Administration, Integration (or `/api/v1/admin/integration`). Enable a sink only when its validation passes.
7. TDP export needs `Keel__Integration__TdpSigningKeyPath` (ECDSA P-256 PKCS#8 PEM, as `keel-audit keygen` makes) and, for verification of retired keys,
   `Keel__Integration__TdpTrustedPublicKeyPaths__0..n`. Without a key the export answers 503.

## Watching it
- `/metrics`: `keel_integration_deliveries{status}` (pending, failed, delivered, withheld, dead), `keel_integration_lag_seconds`,
  `keel_integration_deliveries_total{sink,result}`.
- `/health/ready`: the `integration` check is **Degraded** (HTTP 200) when the oldest due delivery waited longer than `LagDegradedSeconds` (default 300) or says how many dead letters wait.
  Alert on `keel_integration_lag_seconds` and on `status="dead"` above zero.
- Administration, Integration, Deliveries shows every delivery with its error; Outbox shows events (payload only if you may read the event's markings).

## Common situations
| Symptom | Cause and action |
|---|---|
| Deliveries stay pending, lag grows | Dispatcher not enabled in any replica; a transaction left open for long (events are visible only after older transactions end: check `pg_stat_activity` for `idle in transaction`); sink disabled. |
| Dead letters with "SSRF" | The resolved address is loopback, link-local or not allow-listed. Fix `AllowedHosts`/`AllowedNetworks` or the DNS name; then **Requeue**. |
| Dead letters with HTTP 4xx | The receiver rejected the event permanently (bad signature: secret mismatch; 410: endpoint gone). Fix, then Requeue. |
| "Connection failed" retries | TLS: the CA bundle does not cover the endpoint's certificate or name; mTLS client certificate missing. |
| Withheld deliveries | The sink's profile does not clear the event; the reasons are on the row. Change the profile only with the security office, then **Re-evaluate** (audited). Nothing is sent by changing a profile alone. |
| Secret rotation | Provision the new secret, set it as the sink's second secret, update the receiver to accept both, make it primary, retire the old one. |
| Receiver needs a replay | Requeue dead letters; for a gap, backfill with the pull API (cursor from the start). |

## Dead letters, requeue, re-evaluate
Requeue resets attempts and puts the row back; delivery order within an aggregate can then differ from commit order (receivers compare `occurredAt` and revision).
Both actions are administrator-only and audited (`integration.delivery.requeue`, `integration.delivery.reevaluate`). Dead and withheld deliveries are also audited when they happen.

## Technical Data Packages
Exports are recorded in `integration.tdp_export` (immutable). To verify a package you hold: `POST /api/v1/tdp/{id}/verify` with the ZIP as the body, as a user
who may read the export record. A refusal never names objects the exporter cannot read; the administrator reads the counts and asks the data owner.
Back up the TDP signing key like the audit checkpoint key; keep retired public keys in `TdpTrustedPublicKeyPaths` so old packages stay verifiable.

## Retention
The outbox stays immutable except for one audited path. Set `Keel__Integration__Retention__RetentionDays` (0 = off, the default; 7 or more) and the hosted job removes
**delivered** events and their delivery rows once they are older than that. It never removes an event that has a pending, failed, withheld or dead delivery, one the notification
fan-out has not passed, or (unless `IncludeUnroutedEvents=true`) one that never had a delivery row. Other settings: `IntervalHours` (6), `BatchSize` (1000), `MaxBatchesPerRun` (100),
`DryRun` (records what would go). Each batch is one transaction with its audit event `integration.outbox_retention` (counts and dates) and a row in `integration.retention_run`;
if the audit cannot be written nothing is deleted. Direct `DELETE` is refused for every role (database guard triggers); do not delete rows by hand. Receivers that backfill with the
pull API must do so inside the retention window. If the fan-out is disabled on every node its cursor stalls and retention deletes nothing: check `ntf.fanout_cursor`.

## Message broker sink (AMQP 0-9-1)
Design: [ADR-0055](../adr/0055-email-verification-bounces-and-broker-sink.md). A sink of kind **broker** publishes each event (the same signed envelope as a webhook body) to an on-premises
RabbitMQ or compatible AMQP 0-9-1 broker. Configure it under **Administration > Integration > Sinks** (choose **Message broker**) or with `PUT /api/v1/admin/integration/sinks`. Clearance profile,
event types, retry, backoff, dead letters and requeue work exactly as for webhooks and file drops: an event the sink's profile does not clear is withheld and never published.

| Setting | Meaning |
|---|---|
| `protocol` | `amqp091` (the default). `amqp10` and `kafka` are refused: not built. |
| `host`, `port` | The broker. The host must be allow-listed (`Keel:Integration:AllowedHosts` or `AllowedNetworks`), like a webhook host; loopback, link-local and metadata addresses are refused unless `AllowDangerousAddresses` (development only). Port 5671 with TLS, 5672 without. |
| `virtualHost` | Default `/`. |
| `exchange`, `routingKey` | Publish to `exchange` with `routingKey`; the routing key defaults to the event type (for example `part.released`), which suits a topic exchange. With the default (empty) exchange `routingKey` is the queue name. Publishing is **mandatory**: a message no queue accepts is retried (and finally dead-lettered), never silently dropped. |
| `declareExchange` | `true` creates a durable topic exchange if it is missing (needs the permission to). Queues and bindings are yours: Keel never creates them. |
| `usernameSecretName`, `passwordSecretName` | **Names** of secrets, provided as `Keel__Integration__Secrets__<name>` (secrets directory or environment). Never stored in the database or audit log. |
| `clientCertName`, `caBundleName` | Names configured under `Keel:Integration:ClientCertificates` (PEM with certificate and key) and `CaBundles`. With a client certificate and **no** user name the sink authenticates with SASL EXTERNAL (the broker needs its certificate-login plugin). |
| `signingSecretName` | Optional: adds `x-keel-timestamp` and `x-keel-signature` headers computed like the webhook signature. |
| `tls` | `required` (default; TLS 1.2 or 1.3, certificate verified against the CA bundle or the system store, no revocation lookups) or `none`, which needs `acknowledgeInsecureTransport: true`. |
| `timeoutSeconds` | 1 to 120 (default 10) for connect, publish and the broker's confirm. |

**Delivery semantics.** At least once: each publish waits for the broker's publisher confirm; a negative acknowledgement, an unroutable return, a timeout or a dropped connection is a retry with the
outbox's backoff, and the delivery is a dead letter after `MaxAttempts`. Messages are persistent. **Consumers must deduplicate**: the AMQP `message-id` and the header `x-keel-idempotency-key` are the
event id, the same on every retry, requeue and duplicate, and the body bytes are identical for every attempt. Other headers: `x-keel-event-type`, `x-keel-seq`, `x-keel-delivery-attempt`; properties: `type` (event type),
`app-id` `keel-plm`, `content-type` `application/json`, `timestamp` (event time). A retry can arrive after a later event; order by `x-keel-seq`. One connection per sink is kept and rebuilt after any failure;
while the broker is down events wait in the outbox (the readiness check shows the lag and dead letters).

Typical errors in a delivery's last error: "refused the credentials" (secrets or client certificate), "TLS handshake ... failed" (CA bundle or host name; the name in `host` must be in the broker certificate),
"could not route" (no queue bound for the exchange and routing key), "ACCESS_REFUSED" (the user lacks permission), "NOT_FOUND" (the exchange does not exist). Tests run against a real RabbitMQ; other
AMQP 0-9-1 brokers are expected to work but are untested.

## File-drop spool
The file-drop sink is the alternative where a broker is not wanted. Contract for the receiver: read only files that have a `.manifest.json`, verify the SHA-256, process,
then **delete the data file and its manifest** (that is the acknowledgement). Files are named by sequence number; a retry rewrites the same name, so deduplicate on event ids. A sink stops writing when
`maxPendingFiles` committed files are waiting (sink setting, default `Keel__Integration__FileDropMaxPendingFiles` = 10000, 0 = unlimited): its deliveries are deferred (no attempt counted, no dead
letter, last error "spool directory holds ... files") and resume when the receiver catches up. Alert on `keel_integration_spool_files{sink}`. Stale `*.tmp` files left by a crash are removed after an hour.
