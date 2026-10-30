# ADR-0051: Digitally signed release packages (PAdES-style CMS signature, operator PKI, verification)

Status: accepted (increment 9.5, 2026-10-30). Builds on ADR-0008 (FIPS-capable cryptography), ADR-0005 (audit), ADR-0004 (markings), ADR-0044 (release packages) and ADR-0015 (vault).
Assumptions are A64-A66 in [assumptions.md](../assumptions.md); details in [increments/9.5-pdf-signing.md](../increments/9.5-pdf-signing.md).

## Context
A release package leaves Keel as a file. Until now its integrity record was the SHA-256 in the vault and the artifact table, which proves nothing to a customer or auditor who holds only the
PDF. They need to know who produced it and that no byte has changed since, with standard tools and without access to Keel. The signature must not weaken anything the package already does:
the aggregate banner and every distribution notice on every page, the refusal when an object is unreadable, the immutable artifact row and the audit of generation and download.

## Decisions

### 1. Format: an invisible signature field with a detached CMS, PAdES baseline style
The package PDF is rendered with an invisible signature field (an `/AcroForm` with one `/Sig` widget of zero size on page 1, `SigFlags 3`) whose value dictionary reserves 12 KB for the
signature and a fixed-width `/ByteRange`. After saving, the byte range is filled in (every byte of the file except the hexadecimal signature itself) and a detached CMS (`SubFilter
ETSI.CAdES.detached`) is computed over those bytes and written into the reserved space. The CMS carries: the signing certificate and the whole configured chain (so a verifier needs no
network and nothing downloaded), the signing time, the content type and message digest, and the ESS signing-certificate-v2 attribute that binds the signature to exactly one certificate by hash (PAdES
baseline B-B). The dictionary also records reason, location and signer name. Because the page content is rendered before and unchanged by signing, the banners and notices that were rendered
are exactly the bytes that are signed; a test compares the text of every page of a signed and an unsigned render.
The PDF is built with PDFsharp (already used); the signature code is ours (about 150 lines) because PDFsharp has no signing support. Rejected: a PDF library with built-in signing (iText is AGPL, others
add native code or licence limits); signing a wrapper (CMS "enveloping" the PDF) which standard PDF readers cannot show; embedding an additional visible signature stamp (would change the pages the
banner rule protects).

### 2. Algorithms: platform cryptography only, FIPS-capable
`System.Security.Cryptography` and `.Pkcs` only (no new package, no managed crypto): RSA PKCS#1 v1.5 with at least 2048 bits, or ECDSA on NIST P-256, P-384 or P-521; digests SHA-256 (RSA, P-256), SHA-384 (P-384),
SHA-512 (P-521). Every certificate of the bundle must itself be signed with SHA-256 or stronger (RSA, RSASSA-PSS or ECDSA). A certificate that fails is refused when the identity is added, with the reason in
words. No network call is made at signing or verification: chain building has downloads disabled and revocation checking off (see 6).

### 3. Key and certificate management: the database holds the public part and a reference
`rpt.signing_identity` (migration 0030) stores the certificate bundle (signing certificate first, then intermediates, the root last), a key source and a **key reference**, a password **reference**, the
reason and location texts, and the certificate facts for display. Key sources: a PKCS#8 PEM file, a PKCS#12 file, the certificate store of the service account (CurrentUser or LocalMachine; this reaches
CNG, Keychain or driver-backed HSMs), and an OpenSSL 3 provider key URI (`provider|key-uri`, for PKCS#11 HSMs, where .NET uses OpenSSL). A password is named as `env:NAME` or `file:/path`
(a Kubernetes/Compose secret mount works), never typed. The private key and any password never enter the database, the audit log, a response or a log line. Adding an identity loads the key and
proves by a probe signature that it belongs to the certificate; activating repeats the proof. The operator can confine file references to one directory (`Keel:Reports:Signing:AllowedKeyDirectory`).
At most one identity is active (partial unique index); rows are never deleted and only activation and retirement change (trigger); a retired identity never signs again. A singleton policy row can
make signatures **required**.
Signing never silently degrades: with signing required and no identity active, or with an identity active whose key cannot be used now (file gone, HSM down, certificate expired), the package is
**refused** (503, audited as a failed signing) and no artifact is stored. Without an active identity and without the requirement, packages are made unsigned as before.
A development generator (admin page and `POST /api/v1/output/signing/identities/development`) creates a throw-away root and signing certificate (ECDSA P-256, 90 days, subjects containing "DEV ONLY
- NOT FOR PRODUCTION"), writes the key mode 0600 and registers the identity inactive. It is offered only when the host runs in Development or Demo (the configuration can switch it off, never on);
elsewhere the endpoint answers 403 and the page does not show it.

### 4. Where signing happens, and what is stored
Signing happens inside `ReleasePackageService.GenerateAsync`: decide the identity first, render with the reservation, sign, store the **signed** bytes content-addressed (the artifact `sha256` is the hash
of the signed file, so download and the vault hash agree), and in the same transaction insert the immutable `rpt.artifact_signature` row (identity, certificate fingerprint, subject, algorithm, signing time,
reason, location, SHA-256 of the signed byte ranges) and the audit events. Packages made before signing was configured stay as they were: unsigned, never re-signed. Download is unchanged (same
read check on every included object, same audit). The signing time is the Keel server clock (A65).

### 5. Verification: in Keel and with standard tools
`PdfSignatureVerifier` trusts nothing in the file: it finds every `/ByteRange`; requires the ranges to describe the file and to **cover all of it** (appended bytes are flagged); decodes the CMS; checks the
signature over exactly the covered bytes (any changed byte fails the message digest); checks the signing-certificate attribute names the signer; and builds the chain from the certificates in the signature to
the **trust anchors** (the self-signed roots of every configured identity, retired ones included so old packages stay verifiable), at the **signing time** and not today, so a package outlives the certificate that
signed it. Verdicts: valid; untrusted (intact, but the chain does not end in a configured root or the certificate was not valid at signing time); tampered; not signed; unreadable. The report lists signer,
issuer, signing time, reason, location, each check and the chain. It works for any PDF with an `adbe.pkcs7.detached` or `ETSI.CAdES.detached` signature, but only roots configured here are trusted.
The page **Output > Verify a package** and `POST /api/v1/output/packages/verify` (any signed-in role; body = the file; 64 MB limit) run it. The cryptographic result concerns the file the caller already holds. When the
file's SHA-256 is that of a package Keel made, the report also names the package (number, subject, author, time, banner) **only if the caller may read every object in it** (the download rule), so verification
reveals nothing about marked data that the caller could not already read; the audit entry names no package for such a caller (only a hash prefix). The file is not stored.
`scripts/verify-package-signature.sh package.pdf root.pem` repeats the check with bash, `xxd` and OpenSSL 3 (`openssl cms -verify`), independently of Keel; PDF readers such as Adobe Acrobat and
`pdfsig` show the signature when the PKI root is trusted in them (docs/ops/package-signing.md).

### 6. What a signature here does not prove
No trusted timestamp (an air-gapped host has no time-stamping authority) and no revocation evidence (no OCSP or CRL access): the signing time is Keel's own clock, and a certificate revoked after signing
is not detected by Keel's verifier. These make the format PAdES-B-B, not B-T/B-LT/B-LTA. An operator with an internal TSA and OCSP can add them later without changing the field layout (a new
decision). A verifier that is not Keel needs the operator's root certificate.

### 7. Audit
New actions, all under `output.` (hidden from the activity feed by the prefix rule, classified by the catalog test): `output.package.sign` (success with thumbprint, algorithm, signed digest, time; failure
with a reason code), `output.package.verify` (verdict, file hash, signature count, signer fingerprint), `output.signing.identity_add`, `identity_activate`, `identity_retire`, `policy_set`, `dev_generate`.
`output.package.generate` gains `signed=true|false`. Details hold fingerprints, algorithm names, hashes and numbers: never key material, passwords, paths or certificate text.

## Consequences
- The 12 KB reservation limits the chain to about three RSA-4096 certificates; a longer chain is refused at signing with a clear message.
- A package signed under a certificate whose root is later removed from Keel (not possible: identities are never deleted) would verify as untrusted; the roots of retired identities stay anchors.
- Signing adds one extra private-key operation per package (and an HSM round trip where one is used).
- The OpenSSL-provider and certificate-store key sources are implemented on the platform APIs but were not exercised against real HSM hardware in this increment.
