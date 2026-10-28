# ADR-0008: Cryptography and FIPS 140-3 posture

**Status:** Accepted · 2026-10-27

## Decision
- .NET on Linux delegates cryptography to the platform OpenSSL. FIPS 140-3 validation therefore comes from **RHEL 9 in FIPS mode** (validated OpenSSL provider) on both host and UBI base images. We don't ship our own crypto.
- Only FIPS-approved algorithms in code: SHA-256/384, AES-GCM, ECDSA P-256/P-384, RSA ≥ 2048, HMAC-SHA-256, TLS 1.2/1.3 with approved suites. No MD5/SHA-1, not even for non-security checksums; an analyzer rule bans them.
- In transit: TLS everywhere (ingress, API→Postgres `sslmode=verify-full`, API→object store, API→IdP).
- At rest: Postgres on encrypted volumes (LUKS in FIPS mode) and object store server-side encryption with site-managed keys.
- A startup self-check reports whether the process is running with an OpenSSL FIPS provider, exposed in `/health/ready` details and the admin page. Production config can require it (`Keel:Security:RequireFips=true` refuses to start otherwise).

## Consequences
macOS/Windows dev builds are **not** FIPS-validated. That's fine for synthetic data and documented in setup.
