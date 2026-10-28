# Compliance features: operator runbook (increment 5.0, ADR-0026)

Four controls that were open gaps in [nist-800-171-mapping.md](nist-800-171-mapping.md): idle session lock, malware scanning of uploads, vault
garbage collection, SIEM forwarding. Every setting is in [configuration.md](configuration.md). The SIEM side has its own guide:
[siem-onboarding.md](siem-onboarding.md).

## 1. Idle session lock (3.1.10, 3.1.11)

| Setting | Default | Notes |
|---|---|---|
| `Keel__Session__IdleLockMinutes` | `15` | 1 to 120 in production. `0` switches the lock off and is **refused** when `RequireFips` is true or the environment is Production. Fractions below 1 are for tests. |
| `Keel__Session__WarningSeconds` | `60` | Warning before the lock ("Stay signed in"). Shorter than the idle timeout. |
| `Keel__Session__LockedTerminateMinutes` | `15` | A session locked this long is terminated: cookie refused, user signed out of Keel and the IdP session. |
| `Keel__Session__ActivityThrottleSeconds` | `5` | How often the browser reports activity. |

How it behaves: the server holds the idle clock per sign-in and locks even if the browser is closed or blocked. While locked the page is not rendered (its data is
not in the DOM), service calls fail, and every cookie request except the page shell, framework files and `/account/*` gets 423. Unlock is a fresh login at the IdP
(`prompt=login`, `max_age=0`, same subject) and returns to the same page; unsaved input is not kept (it would mean keeping marked data in memory). Audit actions:
`session.lock` (reason `idle`/`manual`), `session.unlock`, `session.lock_expired`, `session.terminated_idle`. The REST API with bearer tokens is not affected.

Operator notes: one node or sticky sessions (already required for Blazor circuits and the shared Data Protection key ring). A restart or another replica does not
know the lock state and starts a session active; a database-backed registry is Phase 5. Cookies issued before this increment carry no session id and are refused: users sign in again once.

## 2. Malware scanning of uploads (3.14.2)

Settings (`Keel__Vault__Scanning__*`): `Mode` = `Disabled` | `Required` | `BestEffort`; `Host` (default `127.0.0.1`), `Port` (3310) or `UnixSocket`; `ConnectTimeoutSeconds`,
`IoTimeoutSeconds`, `ResultTimeoutSeconds`; `MaxScanBytes` (default 2 GiB, must not exceed clamd `StreamMaxLength`); `AllowRemotePlaintext`; `AcknowledgeUnscannedUploads`.

- **Required**: scanner down, dropped, erroring or file larger than `MaxScanBytes` -> upload refused, 503 + `Retry-After` (413-style 422 for oversize), audit `document.upload_refused`.
- **BestEffort**: accepted and recorded `unscanned` (or `skipped` for oversize).
- **Disabled**: recorded `skipped`. With `RequireFips=true` the host refuses to start unless `AcknowledgeUnscannedUploads=true` (a documented, deliberate exception).
- clamd speaks plain TCP: run it as a **sidecar on localhost** (the default `127.0.0.1` needs nothing else) or over a **UNIX socket**. Another host needs
  `AllowRemotePlaintext=true` and a link you protect yourself (compose example: the internal `backend` network). Do not expose port 3310.
- clamd settings: `StreamMaxLength` >= `MaxScanBytes`; `MaxScanSize`/`MaxFileSize` likewise (see `deploy/compose/prod-example/clamd.conf`).
- Image to use (not pulled or run here; pin by digest in your registry): `clamav/clamav` (compose override `deploy/compose/compose.clamav.yaml`).
- **Air gap: the signature databases are yours to update.** Mirror `main.cvd`, `daily.cvd|cld`, `bytecode.cvd` on an online host, carry them through the transfer
  process, copy them into the clamd database directory and restart (or `clamdscan --reload`). `/health/ready` shows the engine and signature build in the `scanner`
  check; old signatures mean a weak control, so schedule it (weekly is common) and watch `signature_db_date` in `plm.file_scan` or the coverage endpoint.

**Test with EICAR.** Create a text file containing the standard 68-character EICAR test string (see eicar.org; harmless by design) and upload it:
```
curl -s -X POST https://keel.example.mil/api/v1/documents -H "Authorization: Bearer $TOKEN" \
  -F 'metadata={"name":"EICAR test","documentType":"Other","markings":{...}};type=application/json' -F 'file=@eicar.com.txt'
# -> 422 {"title":"The upload was refused: the malware scanner detected Eicar-Signature.","signature":"Eicar-Signature"}
```
Check: audit `document.upload_infected` (Failure, file name, size, SHA-256, signature), nothing in the vault under that hash, metric `keel_upload_scans_total{result="infected"}`.

**Coverage and rescan** (administrators; coverage also for auditors): `GET /api/v1/admin/vault/scan-coverage` (counts by `clean/infected/unscanned/skipped`, quarantined, newest scanner and signature date);
`POST /api/v1/admin/vault/rescan` body `{"limit":500,"all":false,"afterSha256":null}` streams stored files through the scanner (default: only those not known clean; `all:true` after a signature update; follow the returned `continuation`).
Newly found infections are quarantined for each document holding them (downloads 409, check-in refused); `POST /api/v1/admin/vault/quarantine/release` `{"documentNumber","sha256","reason"}` releases one document's file (false positive).
Run a rescan after every signature update and after enabling scanning on a vault that has history (everything stored before is `unscanned`).

## 3. Vault garbage collection

`keel-vault` ships in the image at `/app/vault/keel-vault.dll` (`dotnet /app/vault/keel-vault.dll gc ...`). It never targets a database by default: set
`Keel__Database__AppConnectionString` (and `Keel__Vault__*`), or pass `--connection`/`--settings`; `--use-dev-stack` is explicit.
```
keel-vault gc --dry-run                          # default; report only (counts, bytes, sample keys, report hash)
keel-vault gc --delete --confirm-bucket keel-vault --max-deletions 1000
keel-vault gc --continuation <token>             # resume a capped run
keel-vault gc --prefix ab                        # sweep one slice of the key space (also keeps runs short on huge buckets)
```
An object is deleted only if no reference source names it (`plm.document_file`, `plm.document_checkout_file`), it is older than `--grace-days` (default 7, minimum 1) and
it is still unreferenced and unmarked when re-checked under the per-key lock. Procedure: dry run, review the sample and the **report hash**, then run delete with the same options
(the hash is identical if nothing changed), compare the audit event `vault.gc`. Keep the vault bucket versioned/object-locked where the store supports it as a second safety.
The admin page shows the last runs and has a **dry-run** button; deleting is deliberately not in the UI. The scheduled service is **off**
(`Keel__Vault__Gc__Enabled=false`); when enabled it dry-runs unless `Keel__Vault__Gc__Mode=Delete`, every `IntervalHours` (24), at most `MaxDeletionsPerRun` (1000) per run.
A new migration that adds a table with a content-hash column must be classified in `VaultReferences` or the integration test fails (see ADR-0026).

## 4. SIEM forwarding

Enable with `Keel__Siem__Enabled=true` and at least one destination (`Keel__Siem__Destinations__0__*`): see [siem-onboarding.md](siem-onboarding.md) for TLS, mTLS, the file sink, field mapping, Splunk/Elastic
parsing, verifying continuity, and what to do on `siem.chain_break`. Quick check:
- `/health/ready` has a `siem` check (Degraded on delivery failures, lag over 5 minutes or a chain break; never takes the app out of service).
- Metrics: `keel_siem_lag_events`, `keel_siem_lag_seconds`, `keel_siem_queue_events`, `keel_siem_connected`, `keel_siem_chain_broken`, `keel_siem_events_forwarded_total`, `keel_siem_send_failures_total`, `keel_siem_chain_breaks_total`.
- Alert on `keel_siem_chain_broken == 1`, `keel_siem_lag_seconds > 300`, and on any `siem.chain_break` message in the SIEM.

## What stays with the operator
ClamAV signature supply and its update cadence in the air gap; protecting the clamd link if it is not loopback/socket; running delete-mode GC deliberately; the SIEM, its retention and alerting; replacing the
placeholder private enterprise number 32473 with the organization's own; pgAudit for database superusers; sticky sessions for the lock; reviewing `unscanned/skipped` files and quarantines.
