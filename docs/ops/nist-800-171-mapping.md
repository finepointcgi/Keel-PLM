# NIST SP 800-171 Rev. 2 control mapping (starting point)

Scope: what Keel (application + packaging) supports, what the operator owns. Only behaviour the code and tests demonstrate is
claimed; "Open" marks gaps. This is a starting point for the SSP, not an assessment. Evidence references: ADR numbers, `docs/increments/`,
tests (`tests/Keel.IntegrationTests`, `tests/Keel.UnitTests`) and the scripts in `scripts/`.

| Family / control | How Keel supports it | Operator responsibility | Status |
|---|---|---|---|
| 3.1.1 Limit access to authorized users | OIDC authentication required for every endpoint (fallback policy), JWT for API, session cookie for UI (ADR-0007, ADR-0014); unauthenticated requests rejected (compose verify: 401) | Provision users only at the IdP; disable accounts | Supported |
| 3.1.2 Limit to authorized transactions and functions | Role policies (viewer, author, approver, auditor, admin) and per-object ABAC for every action, with audited denials (ADR-0004; `AccessFilterAgreementTests`, API tests) | Role assignment in the IdP | Supported |
| 3.1.3 Control flow of CUI | Markings on every object; read filter in SQL and policy check on single objects; marked downloads (banner header, stamped PDF or marked ZIP) (ADR-0004, 1.3) | Network segmentation; no export of data outside Keel's controls | Supported |
| 3.1.5 Least privilege | `keel_app` DB role has least privilege, migrator separate and only in the job; admin does not bypass markings (ADR-0004, ADR-0005) | Privileged accounts at OS/DB/K8s | Supported |
| 3.1.7 Prevent non-privileged users executing privileged functions; log it | Role policies with `access.denied` audit events | | Supported |
| 3.1.8 Limit unsuccessful logons | Delegated to the IdP | Configure lockout at the IdP | Operator |
| 3.1.10 Session lock after inactivity | **Not implemented**; the UI cookie has an 8 h absolute lifetime and is revalidated each minute (ADR-0014) | IdP/browser session policy as compensating control | **Open** |
| 3.1.11 Terminate session after a period | 8 h absolute cookie lifetime; access tokens short-lived at the IdP | IdP session limits | Partial |
| 3.1.12-3.1.15 Remote access | Single HTTPS entry point; metrics blocked at proxy/ingress | VPN, remote access policy, SIEM | Operator |
| 3.1.13 Cryptographic protection of remote sessions | TLS at proxy/ingress (TLS 1.2+, GCM suites in the shipped configs); FIPS posture reported | FIPS-mode hosts, CA, certificates (hardening.md) | Supported (config) |
| 3.1.22 Control CUI posted publicly | No public endpoints; anonymous access only to health/metrics | Do not expose the service publicly | Supported |
| 3.2.1-3.2.3 Awareness and training | n/a | Training program | Operator |
| 3.3.1 Create and retain audit records | Hash-chained `audit.event` for reads, downloads, changes, signatures, denials; 7-year online retention assumption A20 (ADR-0005) | Retention, storage sizing | Supported |
| 3.3.2 Trace actions to users | Actor snapshot (IdP subject and attributes) in every event | Stable `sub` at the IdP | Supported |
| 3.3.3-3.3.4 Review, alert on failure | Audit queries and admin UI; readiness reports failures | SIEM alerting; syslog forwarding is Phase 5 | Partial |
| 3.3.5 Correlate | Correlation id in logs and audit events | SIEM correlation | Supported |
| 3.3.7 Time stamps | UTC timestamps from the host clock | NTP/chrony from an authoritative source | Operator |
| 3.3.8 Protect audit information | Insert-only role, immutability triggers, signed checkpoints, `keel-audit verify`; restore test shows tampering is detected (`backup-restore-test.sh`) | Checkpoint private key custody; pgAudit for superusers | Supported |
| 3.3.9 Limit audit management to privileged users | Auditor and admin roles for audit endpoints | | Supported |
| 3.4.1-3.4.2 Baselines and configuration | Pinned image digests, signed bundles, documented settings (configuration.md), chart values schema | Site baseline, change control | Supported (packaging) |
| 3.4.6 Least functionality | Minimal image, no package manager, dev settings removed, only needed ports | Disable unused services on hosts | Supported |
| 3.4.8-3.4.9 Software restrictions, user-installed software | Signed offline bundle, verify-before-load, SBOMs | Allow-listing, registry admission policy | Supported |
| 3.5.1-3.5.2 Identify and authenticate | OIDC; claims map to `Subject`, missing attributes fail closed (ADR-0007) | IdP | Supported |
| 3.5.3 Multifactor | CAC/PIV or MFA at the IdP; e-signature requires step-up re-authentication within 300 s (ADR-0007, 1.5) | IdP MFA | Supported |
| 3.5.4 Replay-resistant authentication | Authorization code + PKCE, signed JWTs with lifetime checks | | Supported |
| 3.5.7-3.5.11 Passwords | Not applicable in Keel (no local passwords); obscured feedback is IdP's | IdP password policy | Operator |
| 3.6.1-3.6.3 Incident handling | Audit and logs support investigation | IR plan, testing | Operator |
| 3.7 Maintenance | Upgrade and patch procedure (upgrade.md) | Maintenance windows, media sanitization | Operator |
| 3.8.1-3.8.2 Protect and limit access to media | Backups documented as CUI | Physical and logical media control | Operator |
| 3.8.3 Sanitize media | n/a | Sanitization procedure | Operator |
| 3.8.4 Mark media | Downloads carry markings | Marking of physical media | Partial |
| 3.8.6, 3.8.9 Protect backups | Encrypted media and key separation documented (backup-restore.md) | Encrypt and control backups | Operator |
| 3.9 Personnel | n/a | Screening, termination | Operator |
| 3.10 Physical protection | n/a | Facility controls | Operator |
| 3.11.2 Vulnerability scanning | SBOMs, pinned images, scan-ready archives | Scan monthly, remediate | Operator |
| 3.12 Security assessment | Tests and verification scripts provide evidence | Assessments, SSP, POA&M | Operator |
| 3.13.1 Boundary protection | NetworkPolicies (chart), internal compose network, proxy | Firewalls, boundary devices | Supported (packaging) |
| 3.13.5 Public-access subnetworks | Only the proxy/ingress publishes a port | | Supported |
| 3.13.8 Cryptography for CUI in transit | TLS 1.2+/1.3; verify-full to PostgreSQL; https to vault and IdP (compose verify) | Certificates | Supported |
| 3.13.11 FIPS-validated cryptography | FIPS-approved algorithms only in code; relies on RHEL OpenSSL FIPS provider; startup refusal when the kernel is not in FIPS mode (ADR-0008) | FIPS-mode RHEL 9 hosts; confirm container OpenSSL provider (not verified here) | Partial |
| 3.13.15 Protect session authenticity | HTTP-only, Secure, SameSite=Lax cookie protected by Data Protection keys; antiforgery; CSP (ADR-0014) | Protect the key ring volume | Supported |
| 3.13.16 CUI at rest | SSE-S3 on vault objects (default on); SHA-256 content addressing with verification on download (ADR-0015) | LUKS volumes, store key management | Partial (operator completes) |
| 3.14.1 Flaw remediation | Rebuild/redeliver process (hardening.md, upgrade.md) | Patch cadence | Operator |
| 3.14.2 Malicious code protection | **No malware scanning of uploads** (ADR-0015; quarantine hook point exists) | Endpoint protection on hosts; scan bundles at transfer | **Open** |
| 3.14.3-3.14.4 Alerts, update protection | Image/dependency SBOMs support advisories | Subscribe to advisories | Operator |
| 3.14.6-3.14.7 Monitor communications, detect attacks | Metrics and logs exposed | IDS/SIEM | Operator |
| Vault garbage collection / retention of unreferenced objects | **Not implemented** (objects only accumulate; ADR-0015) | Capacity planning | **Open** (Phase 5) |
| Audit forwarding to SIEM (A20) | **Not implemented** | Collect stdout logs meanwhile | **Open** (Phase 5) |
