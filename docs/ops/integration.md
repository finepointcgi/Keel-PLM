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

## File-drop spool and message brokers
A broker sink is deferred (ADR-0032); the file-drop sink is the on-premises alternative. Contract for the receiver: read only files that have a `.manifest.json`, verify the SHA-256, process,
then **delete the data file and its manifest** (that is the acknowledgement). Files are named by sequence number; a retry rewrites the same name, so deduplicate on event ids. A sink stops writing when
`maxPendingFiles` committed files are waiting (sink setting, default `Keel__Integration__FileDropMaxPendingFiles` = 10000, 0 = unlimited): its deliveries are deferred (no attempt counted, no dead
letter, last error "spool directory holds ... files") and resume when the receiver catches up. Alert on `keel_integration_spool_files{sink}`. Stale `*.tmp` files left by a crash are removed after an hour.
