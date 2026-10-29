# Records management: operations guide

Design: ADR-0041. Settings under `Keel:Records`:

| Setting | Default | Meaning |
|---|---|---|
| `ObjectLock` | `IfAvailable` | `Off`, `IfAvailable` (use S3 Object Lock when the bucket has it) or `Required` (refuse to archive without it) |
| `ObjectLockMode` | `governance` | or `compliance` (nobody can shorten it) |
| `ObjectLockDays` | 3650 | retention applied to archive packages |
| `MaxArchiveBytes` | 2 GiB | largest archive |
| `MaxItemsPerRequest` | 500 | objects per request or archive |
| `BackgroundMinutes` | 0 (off) | interval for re-evaluating holds and refreshing classification |

A signing key is required for certificates, confirmations and archives: `Keel:Audit:CheckpointPrivateKeyPath`. Without it those actions answer "unavailable" and nothing is disposed of.

## Roles
`plm-records` (records manager) plus `plm-viewer` and `plm-author` (to start reviews). Administrators manage but cannot execute; auditors read. The role grants no access to objects: markings still decide what a person sees.

## Workflow
1. Draft a schedule version (classes), submit; reviewer and approvers sign; it publishes and supersedes the previous one. Record program events if classes use them. Press "Re-classify" to refresh the queue.
2. The queue lists readable, due, unheld objects. Create a request (one program), submit, approvers sign.
3. A records manager (not the requester, for marked data) carries it out. Items under hold, no longer due or still used are left alone and reported. A signed certificate is issued.
4. Run the vault sweeper (`keel-vault gc --delete --confirm-bucket NAME`, or the configured service). Content goes after the grace period (minimum 1 day). "Check the vault now" on the certificate records signed confirmations.

## Legal holds
Place by objects, program or query. The page shows only what the reader may see. Release needs a signature. A hold placed after approval still stops execution. A hold on a disposed document keeps its content from the sweeper until the hold is released.

## Archives
`keel-vault verify-archive --package FILE --public-key PEM [--extract SHA256 --out FILE]` verifies offline (exit 0 intact). The application verifies on every open.

### What immutability the application gives, and what only storage can give
Application: no code path updates or deletes an archive or its package; database triggers refuse changes (the database owner would have to disable them, which is a visible schema change); the sweeper counts the package as referenced for ever; the manifest is signed with the checkpoint key and every entry is hashed, so any change is detected on verification; the package key is its own SHA-256.
Not given by the application: protection against someone with write access to the object store or the database host deleting or overwriting objects (detected, not prevented), against loss (back up the bucket), or against copies made before disposal. Storage-level protection is S3 Object Lock: the vault asks the bucket (`GetObjectLockConfiguration`); only an explicit "enabled" answer counts. Create the bucket with object lock enabled (a bucket cannot be changed later) and versioning, set `ObjectLock` to `Required` to refuse archives otherwise. Each archive records `worm_mode` (`application`, `s3_governance`, `s3_compliance`). The bundled test store (SeaweedFS) does not provide it; the fall back is stated on the archive page.

## Checks for auditors
Audit actions `records.*`; certificates and confirmations verify against the trusted checkpoint public keys; `keel-audit verify` still passes after disposals because no audit row is touched.
