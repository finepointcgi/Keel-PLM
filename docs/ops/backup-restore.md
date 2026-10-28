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
- After a restore, run a full integrity pass: every referenced object must exist and hash to its key (downloads verify this on
  access; a bulk verifier is Phase 5, until then sample or script `GET` + SHA-256 against `plm.document_file`).
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
It does not cover the vault, the operator tooling you choose, or encryption of backup media.
