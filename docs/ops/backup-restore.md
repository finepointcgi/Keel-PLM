# Backup and restore

Three things must be backed up together, because the audit chain, the document rows and the vault objects refer to each other:

| Asset | What | How |
|---|---|---|
| PostgreSQL | all data incl. `audit.event` chain and checkpoints | physical base backup + continuous WAL archiving (point-in-time recovery) |
| Vault | content-addressed objects (key = SHA-256) | bucket versioning + replication or periodic copy to a second store |
| Keys | audit checkpoint **private** key, public keys (current and retired), Data Protection key ring, TLS/CA material, secrets | offline, encrypted, separate from the database backups |

## PostgreSQL
- **Base backup:** `pg_basebackup -D <dir> -Ft -X stream -c fast` (tar format, WAL included), at least daily; verify with
  `pg_verifybackup` (the example test keeps `backup_manifest`). Encrypt backup media (LUKS or backup-tool encryption with FIPS algorithms).
- **WAL archiving:** `archive_mode=on`, `archive_command` to a store on another host or failure domain (pgBackRest or Barman
  recommended: retention, parallelism, verification). `archive_timeout` bounds RPO when the system is idle (proposal: 60 s).
- **Operators:** CloudNativePG and Crunchy PGO implement this natively; use their backup resources and test restores the same way.
- **pgAudit** on the server records superuser actions that bypass the audit triggers (ADR-0005); ship its log to the SIEM.
- Backups contain export-controlled data: same marking, access control and encryption as the live database; restore only into
  an accredited environment.

## Vault
- Enable **bucket versioning** (and object lock where the store supports it). Keel never overwrites or deletes objects except its own
  `staging/` keys, so versioning plus replication protects against operator error and ransomware.
- **Replicate** the bucket to a second store (store-native replication or `rclone sync` with `--immutable`). Back up on the same
  schedule as the database: a database restored to time T needs every object referenced up to T, and objects are only ever added, so
  a vault backup taken **after** the database backup contains everything the database refers to.
- After a restore, run a full integrity pass with `keel-vault verify` (below): every object the database refers to must exist and hash to its key at the recorded size.
  Downloads also verify on access.
- Garbage collection of unreferenced objects is not implemented (ADR-0015, Phase 5); nothing is deleted, which keeps restores simple.

## Audit chain and checkpoint keys
- The chain is part of the database backup. `keel-audit verify --public-key <current> --public-key <retired>...` after every restore
  proves the restored chain is unbroken and matches the signed checkpoints.
- The **private** checkpoint key never lives next to the database. Keep an offline encrypted copy (two custodians). If it is lost, new
  checkpoints need a new key: add the old public key to `Keel__Audit__CheckpointPublicKeyPaths` forever and generate a new pair.
- Events written after the last checkpoint and before a failure are protected only by the hash chain until the next checkpoint
  (interval 15 min by default); a restore that loses WAL beyond the last checkpoint loses those events, which is why WAL
  archiving matters.

## SIEM cursor
`siem.cursor` (forwarding position per destination) and `vault.pending_upload` / `vault.gc_run` are ordinary tables in the database backup; nothing else to save. After a restore the forwarder resumes from the restored cursor and re-sends events the SIEM already has (deduplicate on `seq`+`id`, siem-onboarding.md); if the restored cursor is ahead of the restored log, the tail check reports `tail_changed`: investigate, then replay or clear as described there. Scan results (`plm.file_scan`) and quarantines (`plm.file_quarantine`) are restored with the documents.

## Logical backup of a compose site and the vault integrity check (ADR-0060)
For the compose production bundle (`/opt/keel/compose`; `P` is the compose command with the project's files, as `upgrade.sh` builds it) the same assets are saved like this, in this order (**database first, then the vault**, because objects are only ever added):
```bash
cd /opt/keel/compose
P="docker compose --project-directory . -p keel --env-file ../images.env --env-file .env -f compose.prod-example.yaml"
$P exec -T postgres pg_dump -U postgres -Fc keel > /backup/keel.dump                      # 1. database (custom format, restorable with pg_restore)
$P stop vault                                                                              # 2. vault: a cold copy of its volume (or your store's replication/snapshots)
docker run --rm -v keel_vaultdata:/data:ro -v /backup:/backup "$(. ../images.env; echo ${KEEL_POSTGRES_IMAGE%@*})" tar -C /data -czf /backup/vault-data.tar.gz .
$P start vault
cp prod-example/secrets/audit_checkpoint_key.pub.pem /backup/                              # 3. public checkpoint key (the PRIVATE key is kept offline, separately)
(cd /backup && sha256sum keel.dump vault-data.tar.gz audit_checkpoint_key.pub.pem > SHA256SUMS)   # 4. manifest
```
Restore into a new server (the installer created the roles and the empty `keel` database from the secrets; the migration job is a no-op afterwards):
```bash
(cd /backup && sha256sum -c SHA256SUMS)
$P up -d postgres
$P exec -T postgres pg_restore -U postgres -d keel --exit-on-error < /backup/keel.dump
docker run --rm -v keel_vaultdata:/data -v /backup:/backup:ro "$(. ../images.env; echo ${KEEL_POSTGRES_IMAGE%@*})" tar -C /data -xzf /backup/vault-data.tar.gz
$P up -d
```
Check the result with the tools of the production image (they run in the `app` service's configuration, over TLS, with the secret files):
```bash
APP_CS=$(cat prod-example/secrets/Keel__Database__AppConnectionString)
$P run --rm --no-deps -T -e "KEEL_AUDIT_CONNECTION=$APP_CS" --entrypoint dotnet app \
   /app/audit/keel-audit.dll verify --public-key /run/secrets/audit_checkpoint_key_pub              # RESULT: VALID, events and checkpoints checked
$P run --rm --no-deps -T -e "Keel__Database__AppConnectionString=$APP_CS" -e Keel__Vault__ServiceUrl=https://vault:8334 -e Keel__Vault__Bucket=keel-vault \
   -e "Keel__Vault__AccessKey=$(cat prod-example/secrets/Keel__Vault__AccessKey)" -e "Keel__Vault__SecretKey=$(cat prod-example/secrets/Keel__Vault__SecretKey)" \
   --entrypoint dotnet app /app/vault/keel-vault.dll verify                                         # RESULT: INTACT
```
`keel-vault verify [--parallel N] [--json]` is read-only (no audit event, no deletion). It reads every object that `plm.document_file`, `plm.document_checkout_file`, `src.response_file`, `rec.archive` and `rpt.artifact` refer to (honouring the retention release predicate) and checks existence, recorded size and the SHA-256 of every byte. It exits 1 and prints `PROBLEM missing|damaged: <sha256> ... referenced by <columns>` otherwise. It reads the whole vault once, so run it in a quiet period on a large vault and raise `--parallel` only as far as the store tolerates. If a PostgreSQL restore is point-in-time (WAL), the vault copy must be taken after the database state you restore to.

`keel-audit verify` and `keel-vault verify` were run on the production TLS compose stack by `scripts/verify-compose-example.sh` (empty system); the drill below runs them on restored data without TLS.

## The restore drill (executed, repeatable)
`scripts/ops/backup-restore-drill.sh` does the above for real against throwaway containers and volumes named `keel-drill-backup-*` (removed afterwards; nothing existing is touched): real services create parts and documents with files and a signed checkpoint; `pg_dump -Fc`, then a cold copy of the vault volume, then a SHA-256 manifest; more changes are made after the backup; the source is destroyed; a new server and a new vault volume are restored from the artifacts only; then it checks that row counts of all tables equal the baseline, the audit chain and signed checkpoints verify with the same counts, `keel-vault verify` is INTACT with the same objects and bytes, every document file reads back with its recorded SHA-256, the changes made after the backup are absent, and (negative controls) a tampered audit row, a damaged vault object and a deleted vault object are each reported. The result of the last run, the dataset and the timings are in [drill-results/backup-restore.md](drill-results/backup-restore.md). The data is small: the timings show the procedure works, not how long your data takes; time a restore of your own backup quarterly.

## Restore procedure
1. Provision a fresh PostgreSQL server (same major version) and, if needed, a fresh vault bucket restored from replica.
2. Unpack the base backup into an empty data directory; set `restore_command` to fetch archived WAL; create `recovery.signal`; for PITR
   set `recovery_target_time` (UTC) and `recovery_target_action = 'promote'`; start the server and wait for promotion.
3. Roles (`keel_app`, `keel_migrator`) come with the data; confirm the passwords match the secrets or rotate them.
4. Point the application at it, `--migrate` (no-op when current), start.
5. Run `keel-audit verify` with all trusted public keys: result must be `VALID`. Record event and checkpoint counts.
6. Sign in, open recent parts/documents, download a file (integrity check).
7. Reopen the service; record the incident and the restore evidence (NIST 3.6, 3.8.9).

## Automated test
`scripts/backup-restore-test.sh` exercises the database half end to end against throwaway tmpfs PostgreSQL containers (nothing
persistent, nothing existing is touched): real migrator, 25 synthetic parts through the real `PartService` (each with its audit event)
and a signed checkpoint, base backup with WAL archiving on, 5 more parts, then two restores into fresh servers (to the end of the
WAL, and to a point in time before the later parts) and these checks: all-table row counts equal, `keel-audit verify` VALID,
event and checkpoint counts equal, and a negative control (a tampered restored row is reported INVALID). Run it before every
release and after changing the PostgreSQL major version; run the same procedure against your real backup tooling quarterly.
It does not cover the vault (the drill above does), the operator tooling you choose, or encryption of backup media.
