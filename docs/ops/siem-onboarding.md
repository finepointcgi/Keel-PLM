# SIEM onboarding (NIST SP 800-171 3.3.x, ADR-0005, ADR-0026)

Keel forwards its tamper-evident audit log to an on-prem SIEM. Delivery is ordered and at least once, resumes after a restart, never blocks audit writes, and carries the
chain data so the SIEM can prove nothing was removed. UDP is not supported (no delivery guarantee, no encryption).

## Configuration (`Keel:Siem`)
| Setting | Default | Notes |
|---|---|---|
| `Enabled` | `false` | |
| `Hostname`, `AppName` | machine name, `keel-plm` | syslog HOSTNAME and APP-NAME |
| `EnterpriseNumber` | `32473` | Private enterprise number in the SD-ID `keel@PEN`. **32473 is the IANA documentation placeholder (RFC 5612).** Set your organization's PEN when you have one, and tell the SIEM parser. |
| `Facility` | `13` | log audit |
| `BatchSize`, `QueueBatches`, `PollIntervalMilliseconds` | `200`, `4`, `1000` | the queue between reader and sender is bounded; the backlog stays in PostgreSQL |
| `Destinations__N__Name` | required | `a-z0-9_-`, unique; keys the cursor |
| `Destinations__N__Type` | `SyslogTls` | or `File` |
| `...__Host`, `__Port` (6514), `__ServerName` | | server name the certificate must carry (default Host) |
| `...__Transport` | `Tls` | `Tcp` only with `AllowInsecureTcp=true` and never with `RequireFips=true`; `Udp` stops the host with NotSupportedException |
| `...__CaBundlePath` | system store | PEM CA(s) that sign the SIEM certificate; relative paths resolve against `KEEL_SECRETS_DIR` |
| `...__ClientCertificatePath`, `__ClientKeyPath`, `__ClientKeyPassword` | | mutual TLS; the password is a secret file `Keel__Siem__Destinations__0__ClientKeyPassword` |
| `...__FilePath` | | NDJSON file for a forwarder |
| `...__ConnectTimeoutSeconds`, `__SendTimeoutSeconds`, `__BackoffMinSeconds`, `__BackoffMaxSeconds` | 10, 30, 1, 60 | exponential backoff with jitter |

TLS 1.2 and 1.3 only, ciphers from the host's (FIPS) crypto policy; revocation is not checked (no OCSP/CRL path in an air gap): rotate certificates on a short life instead.

## Message format
RFC 5424 over TLS, RFC 5425 octet counting (`<length> <message>`):
```
<110>1 2026-10-28T09:00:03.000000Z keel-host keel-plm 4242 document.download [keel@32473 seq="4711" hash="9f..64hex" prevHash="3c..64hex" id="0198..." actor="u-alice" action="document.download" outcome="Denied" object="document:D-000012" corr="0198..."] document.download Denied object=document:D-000012 actor=u-alice
```
PRI = facility*8 + severity: Failure = 3 (err), Denied = 4 (warning), Success and others = 6 (info). SD values escape `"`, `\` and `]` with a backslash (RFC 5424 6.3.3).
Other message types: `audit.checkpoint` (`checkpoint`, `seq`, `hash`, `keyId`: a signed checkpoint was created), `audit.verify` (the verification event as any other, outcome Failure when it found problems),
`siem.chain_break` (critical, PRI 106: `kind`, `seq`, `detail`). The file sink writes one JSON object per line with the same fields (`@timestamp, stream, seq, id, hash, prevHash, actor, action, outcome, object, corr, summary, severity, host, app`).

**Never forwarded:** object names, descriptions, markings, event details, the actor's attributes or name, file names. Those are in the audit payload (the hash input), which stays in PostgreSQL.
Object references are identifiers (numbers, `METHOD /path` for endpoint denials); keep names out of identifiers.

## Parsing
Splunk (`props.conf`): `SHOULD_LINEMERGE=false`, `TIME_PREFIX=^<\d+>1\s`, `TIME_FORMAT=%Y-%m-%dT%H:%M:%S.%6N%Z`, extraction
`EXTRACT-keel = \[keel@\d+ seq="(?<seq>\d+)" hash="(?<hash>[0-9a-f]{64})" prevHash="(?<prevHash>[0-9a-f]{64})" id="(?<event_id>[^"]*)" actor="(?<actor>(?:[^"\\\]]|\\.)*)" action="(?<action>[^"]*)" outcome="(?<outcome>[^"]*)" object="(?<object>(?:[^"\\\]]|\\.)*)" corr="(?<corr>[^"]*)"`.
For the NDJSON file use `INDEXED_EXTRACTIONS=json` with a universal forwarder monitoring the path.
Elastic: filebeat `filestream` input with `parsers: - ndjson: {target: "", add_error_key: true}` for the file sink, or the `syslog` input (TLS) with an ingest pipeline `grok`/`dissect` on the SD element; map `seq` to a long and `hash`/`prevHash` to keyword.
rsyslog: `imtcp` with `StreamDriver="gtls"` and `StreamDriver.Mode="1"`, `StreamDriver.AuthMode="x509/name"`, framing is octet-counted by default; or `imfile` for the NDJSON file.

## Verifying continuity in the SIEM
Per source host, sort by `seq`. A healthy stream has: `seq` increasing by exactly 1 with no hole; every event's `prevHash` equal to the previous event's `hash`; the first event's `prevHash` 64 zeros. Alert on a missing `seq`,
a `prevHash` mismatch, and on any `siem.chain_break`. Duplicates (same `seq` and `hash`) are normal after a restart or reconnect (at-least-once): deduplicate on `seq`+`id`.
The SIEM cannot recompute a hash (the payload is not forwarded); Keel does, while reading, and sends `siem.chain_break` (`gap`, `link_mismatch`, `hash_mismatch`, `tail_changed`) when the log in the database
disagrees. Run `keel-audit verify --public-key ...` for the full signed-checkpoint verification when you see one.

**This closes the "deletion after the last checkpoint" gap** (ADR-0005, docs/CLAUDE.md 1.1): checkpoints cannot reveal events removed before the next checkpoint is signed, but the SIEM already holds
them, and the forwarder reports `tail_changed` when something it forwarded is no longer in the database. It does so only while the forwarder and the SIEM are themselves out of the reach of whoever tampers: keep SIEM retention and access separate from the database administrators.

## Operations
- Health `siem` check and metrics: see compliance-features.md. `siem.chain_break` is also written to the audit log once per destination; the persisted break keeps health Degraded until a database owner clears it after investigation:
  `UPDATE siem.cursor SET chain_break_seq = NULL, chain_break_at = NULL, chain_break_note = NULL WHERE destination = '<name>';` (run as `keel_migrator`/DBA, record why).
- **Replay** (the SIEM lost data, TCP gives no ack): stop forwarding or accept duplicates, then as a DBA set the cursor back:
  `UPDATE siem.cursor SET last_seq = N-1, last_hash = COALESCE((SELECT hash FROM audit.event WHERE seq = N-1), decode(repeat('00',32),'hex')) WHERE destination = '<name>' AND stream = 'audit';` (restart the application to reload it).
- Delivery limit: the sender refuses to reuse a connection the collector closed and checks the socket after each write, but a collector that accepts bytes and loses them before storing them is only detectable by the continuity check above.
- Backup: `siem.cursor` is in the database backup (backup-restore.md); after a restore the forwarder resumes from the restored cursor and re-sends what the SIEM already has.
- Test: `tests/Keel.IntegrationTests/SiemForwardingTests.cs` runs a real TLS collector with a test CA (framing, ordering, restart, reconnect, mTLS, tamper detection, backpressure).
