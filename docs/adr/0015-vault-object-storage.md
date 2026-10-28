# ADR-0015: Vault object storage

**Status:** Accepted · 2026-10-27

## Context
Documents carry files (drawings, CAD, specifications, reports) up to gigabytes each, for a 50 TB vault (A11). The files
are export-controlled technical data. They must be stored durably, never altered after check-in, verifiably intact years
later, encrypted at rest, and reachable from an air-gapped site. A13 requires that code depends on the S3 API only and
that the product takes no dependency on MinIO after its 2025 licensing and distribution changes.

## Decision

### Store
- The application talks to the vault through the **S3 API only** (AWSSDK.S3, Apache-2.0) with path-style addressing,
  explicit credentials and region. Configuration: `Keel:Vault:ServiceUrl`, `AccessKey`, `SecretKey`, `Bucket`,
  `ServerSideEncryption`, `MaxUploadBytes` (default 2 GiB, at most 5 GiB because server-side copy is single-request),
  `PartSizeBytes` (default 16 MiB), `MaxPdfStampBytes` (default 256 MiB).
- **Dev and test store: SeaweedFS** (`chrislusf/seaweedfs:4.47`, Apache-2.0) in `weed mini` mode: one container with
  master, volume server, filer and S3 gateway. It supports SigV4, multipart upload, server-side copy and SSE-S3, which is
  everything we use. Credentials come from a dev-only identity file (`deploy/compose/seaweedfs/s3.json`) scoped to the
  `keel-vault` bucket. The integration tests run the same image through Testcontainers.
- Production sites can use any S3-compatible store their accreditation allows (an on-prem appliance, Ceph RGW, SeaweedFS
  cluster). Nothing in the code is SeaweedFS-specific.

### Content addressing and immutability
- The object key is the **lowercase hex SHA-256** of the bytes. Identical bytes are stored once, whichever document or
  iteration references them; check-out and check-in copy only metadata rows, never objects.
- The hash is computed **while the upload streams**. At most one part (`PartSizeBytes`) is in memory per upload:
  - a file smaller than one part is hashed first and written directly to its final key;
  - a larger file is uploaded by multipart to a `staging/<uuid>` key, then copied server side to its content address,
    and the staging object is deleted (aborted on failure).
- The database (`plm.document_file`) records size, media type and SHA-256 next to the file name. Iteration file rows are
  immutable by trigger, like iterations themselves.
- Uploads are checked against access policy and check-out ownership before any byte is accepted, and again under the row
  lock when the metadata is written. An object whose metadata write then fails is orphaned but harmless (its key is its
  content); vault garbage collection of unreferenced objects is Phase 5.

### Integrity verification
- Every download re-hashes the object and compares size and SHA-256 with the database. Files up to one part (and PDFs up
  to `MaxPdfStampBytes`) are read once into memory and verified before stamping. Larger files are verified in a full pass
  first, then streamed into the marked ZIP in a second pass that verifies again and aborts the transfer on mismatch.
- A mismatch (tampering, bit rot, a missing object) raises `VaultIntegrityException`, is audited as
  `document.download` with outcome `Failure`, and the API answers 500 "Stored file failed integrity verification". The
  file is not delivered.
- FIPS (ADR-0008): the SDK's flexible checksums (CRC32 by default; older SDK versions used MD5) are switched to
  `WHEN_REQUIRED`, so the SDK computes no MD5 or CRC. In-transit integrity comes from SigV4 (SHA-256 payload hashes,
  HMAC-SHA-256 signatures) and TLS; at-rest integrity from our own SHA-256 addressing.

### Encryption at rest
- Production sets `Keel:Vault:ServerSideEncryption=true` (the default in `appsettings.json`): every object is written
  with SSE-S3 (`AES256`). Keys are managed by the store, with site-managed keys (KMS) where the store supports them.
- In addition, the store's volumes sit on encrypted block devices (LUKS in FIPS mode), as for PostgreSQL (ADR-0008).
- TLS to the store is required in production (`https://` service URL); the dev stack uses plain HTTP on 127.0.0.1.

### Malware scanning
- Not in Phase 1. Phase 5 adds a scanning hook between "bytes in the vault" and "row in the working copy": the upload
  path already separates the two, so a scanner can quarantine an object before it becomes visible.

## Consequences
- Deduplication is free and deterministic; the same specification attached to a hundred parts is stored once.
- Objects are never overwritten by the application, so object-lock / WORM retention can be enabled on the bucket.
- Content-addressed keys reveal nothing about the document, but they let anyone with store access confirm whether a known
  file is present; store access is therefore restricted to the application identity and operators.
- Downloads of large non-PDF files read the object twice. Acceptable on-prem; revisit if vault bandwidth becomes a limit.

## Alternatives
- **MinIO:** excluded by A13.
- **Garage** (AGPL-3.0) and **Ceph RGW** (LGPL, heavy to run in a single dev container): licence or footprint worse for
  a dev default. Ceph remains a reasonable production store.
- **Files in PostgreSQL (bytea / large objects):** simple, transactional, but bloats backups and the WAL at 50 TB.
- **Random object keys plus a stored hash:** loses deduplication and makes tamper checks depend on the database alone.
