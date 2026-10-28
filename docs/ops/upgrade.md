# Upgrade policy

## Migrations
- SQL files in `db/migrations/NNNN_name.sql`, applied in name order by `Keel.Api --migrate` as the schema owner `keel_migrator`,
  each in its own transaction. The SHA-256 of every applied file is stored in `public.schema_migrations`.
- **Forward-only.** There are no down-migrations. An applied file whose content changed is a hard error (never silently re-run).
- **Hash-checked.** `/health/ready` is unhealthy while shipped migrations are missing from the database, so a pod of a newer version
  never takes traffic against an old schema.
- **Job before rollout.** Compose: the `migrate` service must exit 0 before `app` starts. Helm: pre-install/pre-upgrade hook Job; if it
  fails the release fails and no pod is replaced. Only the migration job holds the migrator credential.
- During a rolling upgrade old and new pods briefly share the new schema. Migrations must therefore be additive in the release that
  introduces them (add columns/tables, backfill) and destructive changes ship one release later. Reviewers enforce this; nothing in
  the tooling does.

## Rollback = restore
Because migrations cannot be undone, the only rollback after a migration has run is restoring the backup taken just before
(backup-restore.md) together with the previous image. If the migration job itself fails, its transaction is rolled back; fix the cause and
re-run. Application-only rollbacks (no migration in the release) are `helm rollback` or re-pointing `KEEL_APP_IMAGE`.

## Procedure
1. Read the release notes; check for new settings (configuration.md).
2. Take a base backup and confirm WAL archiving is current; export/copy secrets if rotated.
3. Verify and transfer the new bundle (offline-install.md).
4. `upgrade.sh ... --backup-done`. It refuses downgrades and same-version re-installs.
5. Check `/health/ready`, run `keel-audit verify`, sign in, open a part and a document.
6. Keep the previous bundle and backup until the next maintenance window.

## Cadence
Rebuild and re-scan monthly on the UBI base image refresh and immediately for critical CVEs (hardening.md). The pinned digests in
`deploy/images.env` change only through `scripts/refresh-image-pins.sh` plus review.
