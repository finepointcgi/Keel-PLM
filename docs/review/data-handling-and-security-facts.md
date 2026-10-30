# Appendix: data handling and security facts (control-to-evidence map)

Status: **pending review. For counsel and assessors to decide: whether any row is sufficient for any assessment.** This appendix points to evidence in the repository. It is not an assessment, not a System Security Plan, and not a statement of compliance with NIST SP 800-171 or CMMC. The authoritative mapping is [../ops/nist-800-171-mapping.md](../ops/nist-800-171-mapping.md) ("a starting point for the SSP, not an assessment"). Control numbers below are copied from that file; whether the control wording is right is to be confirmed by the assessor.

Tests named exist in the repository (file names checked). "Run in this packet" is **no** for all integration and E2E tests: this packet ran only the build and the unit suite (see `docs/increments/9.3-review-packets.md`).

| Control topic (id from the mapping file) | What the repository claims | Evidence | Status stated in the mapping | Open or unverified |
|---|---|---|---|---|
| Access limited to authorized users (3.1.1) | OIDC for every endpoint, JWT for API, session cookie for UI | ADR-0007, ADR-0014; `scripts/verify-compose-example.sh` (401 check) | Supported | demo mode intentionally weakens this (ADR-0045) |
| Authorized transactions (3.1.2) | roles plus per-object ABAC for every action, denials audited | `AccessPolicyTests.cs`, `AccessFilterAgreementTests.cs` | Supported | |
| CUI flow (3.1.3) | markings, SQL read filter, policy check, marked downloads | ADR-0004, `MarkingOutputTests.cs`, `src/Keel.Documents/Marking/` | Supported | unstamped channels: CAD connector, 3D/image viewer (see export packet) |
| Least privilege (3.1.5) | `keel_app` DB role least privilege, separate migrator, admin does not bypass markings | `db/migrations/0001_audit.sql`, `deploy/compose/initdb/00-roles.sql`, `AccessPolicyTests.cs` | Supported | DBA/superuser access is outside the app |
| Idle lock and termination (3.1.10, 3.1.11) | server-side idle lock default 15 min, 8 h absolute, unlock by IdP re-authentication | ADR-0026; `SessionLockTests.cs` (unit and integration), `tests/Keel.E2E/SessionLockTests.cs` | Supported | multi-node needs sticky sessions |
| Cryptography of remote sessions and CUI in transit (3.1.13, 3.13.8) | TLS 1.2+ with GCM suites in shipped proxy configs; verify-full to PostgreSQL; https to vault and IdP | `deploy/compose/prod-example/Caddyfile`, `nginx.conf`, `docs/ops/hardening.md`, `verify-compose-example.sh` | Supported (config) | proxy-to-app hop is plain HTTP by default |
| FIPS-validated cryptography (3.13.11) | FIPS-approved algorithms only in code; relies on RHEL OpenSSL FIPS provider; startup refusal when the kernel is not in FIPS mode | ADR-0008, `appsettings.json` `RequireFips`, `/health/ready` fips check | Partial | **unverified: FIPS host.** The check proves the kernel flag only. Container OpenSSL provider not verified |
| Audit records, trace to users, protect audit (3.3.1, 3.3.2, 3.3.8) | hash-chained insert-only log, actor snapshot, signed checkpoints, verifier, restore test shows tampering detected | ADR-0005, `AuditLogTests.cs`, `scripts/backup-restore-test.sh`, `src/Keel.AuditCli` | Supported | checkpoint key custody is the operator's; superusers can disable triggers (pgAudit advised) |
| SIEM forwarding, alerts (3.3.3, 3.3.4, A20) | RFC 5424 over TLS or NDJSON; chain data; `siem.chain_break` | ADR-0026, `docs/ops/siem-onboarding.md`, `SiemTests.cs`, `SiemForwardingTests.cs` (test collector) | Supported (forwarding) | **unverified: real SIEM.** Placeholder enterprise number 32473 |
| Least functionality, baselines (3.4.1, 3.4.2, 3.4.6, 3.4.8) | pinned digests, signed bundle, SBOMs, no package manager in image, setuid cleared | `deploy/images.env`, `Dockerfile`, `docs/ops/hardening.md`, `scripts/verify-offline.sh`, `OfflineComplianceTests.cs` | Supported (packaging) | **unverified: UBI image build and scan** ("not built or scanned in this environment", hardening.md) |
| Identification, MFA, replay (3.5.x) | OIDC, claims fail closed; step-up for e-signature; code plus PKCE | ADR-0007, `SignatureTests.cs` | Supported | MFA, CAC/PIV, lockout and passwords are the IdP's |
| Malicious code protection (3.14.2) | uploads streamed through clamd; infected refused; modes Required, BestEffort, Disabled; Disabled refused in production without acknowledgement; quarantine | ADR-0026, `ScanningTests.cs`, `ClamdScannerTests.cs` (protocol-faithful stand-in) | Supported (scanner and signatures are the operator's) | **unverified: real clamd.** `compose.clamav.yaml` was not run; signature updates in the air gap are the operator's; INSTREAM is unencrypted; demo scanning is off |
| CUI at rest (3.13.16) | SSE-S3 requested, SHA-256 content addressing verified on download | ADR-0015, `VaultOptions.cs`, `appsettings.json` | Partial | LUKS and store keys are the operator's; store support for SSE not verified here |
| Session authenticity (3.13.15) | HttpOnly, Secure, SameSite=Lax cookie with Data Protection; antiforgery; CSP | ADR-0014, UI auth tests | Supported | key ring volume protection is the operator's |
| Vault garbage collection | delete only unreferenced objects past a grace period, audited | ADR-0026, `VaultGcTests.cs` | Supported | records disposal also releases files (ADR-0041) |
| Outbound channels | outbound release check, withheld events, TDP completeness, recorded exports | ADR-0025, `IntegrationTests.cs` (unit, oracle), `TdpTests.cs`, `IntegrationDispatcherTests.cs`, `IntegrationPullTests.cs` | not in the 800-171 table | INT-6, INT-12 need export-compliance review (A34) |
| Supplier isolation and foreign ITAR rule | scoped portal, clearance profile, DB triggers | ADR-0037, migration 0020, `PortalTests.cs`, `SourcingOracleTests.cs`, `SourcingRulesTests.cs` | not in the table | merged but end-to-end state unverified at merge time (`docs/CLAUDE.md`) |
| Backups (3.8.6, 3.8.9) | documented as CUI, encrypted media and key separation advised | `docs/ops/backup-restore.md`, `scripts/backup-restore-test.sh` | Operator | operator procedure |
| Vulnerability scanning, flaw remediation (3.11.2, 3.14.1) | SBOMs, pinned images, rebuild and redeliver process | `docs/ops/hardening.md`, `docs/ops/upgrade.md`, `src/Keel.SbomTool` | Operator | image SBOMs only if `syft` is present; none in the repo |
| Boundary protection (3.13.1, 3.13.5) | NetworkPolicies, internal compose network, only the proxy publishes a port | `deploy/helm/keel/templates/networkpolicy.yaml`, `scripts/verify-helm.sh`, `scripts/verify-compose-example.sh` | Supported (packaging) | enforcing CNI is the operator's; Helm install on a cluster not verified (`docs/CLAUDE.md`) |

## Items called out as unverified (collected)

| Item | What is known |
|---|---|
| FIPS host | no FIPS host available; the Docker VM reports FIPS disabled, and that produced Degraded readiness and a startup refusal when required (`docs/ops/hardening.md`). A FIPS-enabled run has not been done |
| UBI image | build and scan not done in the environment that wrote `hardening.md`; digest pins were resolved against the registry |
| Real SIEM | only a test TLS collector exists |
| clamd | `deploy/compose/compose.clamav.yaml` header: image not pulled, file not started |
| Cloud Build and Cloud Run | not verified by the work that added them (ADR-0045 "Not verified by this work") |
| Helm install on a cluster, Kubernetes enforcement | not verified (`docs/CLAUDE.md`) |

## Facts counsel or assessors often ask for

| Question | Answer from the repository |
|---|---|
| Does the product call out to the internet at run time? | Design says no (air gap). Viewer assets are same-origin; CSP `connect-src` for the viewer names no origin (`ViewerCsp.cs`). Not tested end to end against a network sniffer: **unverified** |
| Are there default passwords? | No local passwords in the product (IdP). The dev realm has synthetic users; demo sign-in has none by design (ADR-0045) |
| Where are secrets? | files in `KEEL_SECRETS_DIR` or environment, never the database (ADR-0025, `docs/ops/configuration.md`) |
| What data is in the audit log? | see export packet 4.7 and 7.3 |
| Retention | audit 7 years online (A20); outbox retention off by default; records module governs documents and parts (ADR-0041) |
| Incident handling, personnel, physical | operator (800-171 mapping) |
