# Backup and restore drill result

Run `keel-drill-backup-110943-5714` on 2026-10-30T15:10:02Z, production image `keel/api:prodready` (sha256:7ab7a6f65c9e), PostgreSQL `postgres:17-alpine`, vault `chrislusf/seaweedfs:4.47`. Docker VM 8 CPUs, 15 GiB; host Darwin arm64.

Dataset: 40 parts and 12 documents with files (1 KB to about 3 MB each, random bytes) created through the real services; 202 tables, 52 audit events, 1 signed checkpoints, 12 vault objects (7619706 bytes).
Backup: `pg_dump -Fc` 883376 bytes in 0 s; vault volume copy (vault stopped) 7643607 bytes compressed in 3 s. Restore into a new server and vault volume including manifest hash checks: 4 s. Sizes are small; these timings show the procedure works, not how long a site's data takes.

| Check | Result | Detail |
|---|---|---|
| source: audit chain verifies | pass | VALID |
| source: vault is intact | pass | RESULT: INTACT |
| restored row counts equal the baseline in all 202 tables | pass | identical (202 lines compared) |
| restored audit chain verifies | pass | VALID |
| restored audit event count equals the baseline (52) | pass | 52 |
| restored signed checkpoints verified, same count (1) | pass | 1 |
| restored vault is intact (keel-vault verify) | pass | RESULT: INTACT |
| restored vault holds the same objects and bytes (12 objects, 7619706 bytes) | pass | 12/7619706 |
| every document file reads back with its recorded SHA-256 (12 documents) | pass | documents checked: 12, problems: 0 |
| documents created after the backup are absent from the restore | pass | documents checked: 2, problems: 2 |
| parts created after the backup are absent (45 before the disaster, 40 expected after) | pass | 40 |
| the restored schema migration hashes are the shipped ones | pass | 33 |
| keel-vault verify exits non-zero when objects are damaged or missing | pass | 1 |
| it names the damaged object | pass | 1 |
| it names the missing object | pass | 1 |
| a tampered audit row in the restored database is reported INVALID | pass | INVALID |

Passed: 16, failed: 0.
