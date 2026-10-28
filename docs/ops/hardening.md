# Hardening notes (DISA container and Kubernetes STIG, practical level)

This is a working mapping, not an accreditation artifact: it states what the image, compose example and chart already do (verified
by the scripts named in each row) and what the operator must still do. It does not replace running the STIG checklists and
SCAP/OpenSCAP scans against your environment.

## What the image already does
| Control | State | Evidence |
|---|---|---|
| UBI 9 base, pinned by digest | yes (`deploy/images.env`) | `package-offline.sh` refuses unpinned or mismatching third-party images; the UBI pins were resolved against the registry but the UBI image build itself was not run here (disk) |
| Non-root numeric user (1001:0) | yes | `verify-compose-example.sh` (checked on a substitute base image) |
| Read-only root filesystem friendly | yes: writes only to `/tmp` and `/var/lib/keel/dataprotection` | compose example and chart run with `read_only`/`readOnlyRootFilesystem` |
| No package manager or downloader | `microdnf/dnf/yum/curl` removed, setuid/setgid bits cleared | Dockerfile; bash and coreutils of the UBI base remain (the entrypoint does not use them) |
| No secrets or dev settings baked in | `appsettings.Development.json` removed from the image; secrets via files | Dockerfile, `configuration.md` |
| Healthcheck without extra tools | `--healthcheck` mode of the host | compose verify: container reports healthy |
| No core dumps, no diagnostics socket | `DOTNET_EnableDiagnostics=0`, fatal startup errors exit 1 instead of aborting, compose `ulimits core 0` | startup error path tested |
| Capabilities dropped, no privilege escalation, seccomp default | compose (`cap_drop ALL`, `no-new-privileges`) and chart | `verify-compose-example.sh`, `verify-helm.sh` |
| Secrets not in env or manifests | file-based (`KEEL_SECRETS_DIR`) | both verify scripts check this |
| SBOM and signed delivery | CycloneDX for .NET; image SBOMs when syft is present; signed manifest | `package-offline.sh`, `verify-offline.sh` |

## What the operator must do
| Area | Requirement |
|---|---|
| Hosts | RHEL 9 in **FIPS mode** (`fips-mode-setup --enable`, then reboot) on every node/host running Keel, PostgreSQL and the vault. Keel reads `/proc/sys/crypto/fips_enabled` (`RequireFips=true` refuses to start otherwise) and reports it in `/health/ready`. The check proves the kernel flag; it does not prove the container's OpenSSL runs the FIPS provider. Run containers with podman on RHEL (it passes the host crypto policy into the container) or mount the host's `/etc/crypto-policies` read-only; verify with `openssl list -providers` inside a debug container. This was **not verified here** (no FIPS host; the Docker VM reports FIPS disabled, which was verified to produce Degraded readiness and a refusal when `RequireFips=true`). |
| Disk encryption | LUKS (FIPS mode) for volumes holding PostgreSQL data and WAL, the vault, backups and the Data Protection key ring (the framework stores those keys unencrypted). |
| TLS everywhere | Users to proxy/ingress (TLS 1.2+ with GCM suites, set in the example Caddyfile and nginx.conf); app to PostgreSQL `SSL Mode=VerifyFull`; app to vault `https://`; app to IdP `https://`. The last hop proxy to app is plain HTTP inside a private network (compose `edge`, pod network); where policy forbids that, enable Kestrel TLS (configuration.md) or a service mesh. Certificates from the internal CA; track expiry. |
| PostgreSQL | `hostssl` only (the compose example enforces it), SCRAM-SHA-256, roles `keel_app` (least privilege) and `keel_migrator` (job only), **pgAudit** enabled and shipped to the SIEM (superusers can disable the audit triggers; ADR-0005), `log_connections`, data checksums, RHEL-based or vendor-FIPS server build where the accreditation requires a validated OpenSSL in the DB process. |
| Vault | TLS, versioning, SSE-S3 with site-managed keys, access limited to the application identity and operators, telemetry off (`-master.telemetry=false`). |
| Kubernetes | Enforcing CNI for NetworkPolicy (the chart's policies are default-deny and do nothing on a CNI that ignores them), Pod Security Admission `restricted` on the namespace (the chart's pod spec satisfies it), etcd encryption at rest for Secrets or an external secret store, audit logging on the API server, RBAC with no cluster-admin for application operators, image pull only from the site registry by digest, admission policy rejecting unsigned or unpinned images. |
| Key management | Audit checkpoint private key offline with two custodians; separate keys per environment; rotate by adding a new key and keeping every retired **public** key (`CheckpointPublicKeyPaths`). Database, vault and OIDC credentials rotate by replacing the secret files and restarting; the example `gen-secrets.sh` never overwrites existing secrets. Bundle signing key: the customer's own. |
| IdP | CAC/PIV, MFA, account lockout, password policy, session policy and SSO lifetime are enforced by the IdP; Keel does not implement them (ADR-0007). Never reissue a user's `sub`. |
| Patching cadence | Rebuild and rescan monthly on the UBI refresh, within 72 hours for critical CVEs in the image or the .NET runtime (proposal; set by the site's ISSM). Patching means a new signed bundle through `upgrade.sh`; PostgreSQL, vault, OS and Kubernetes follow the site's schedule. |
| Logging | JSON logs with correlation ids on stdout; collect with the site log pipeline (audit events: RFC 5424 over TLS or NDJSON file via `Keel:Siem`, see siem-onboarding.md). `/metrics` only from the monitoring network. |
| ClamAV | Run clamd as a localhost sidecar or on a UNIX socket (INSTREAM is unencrypted); `StreamMaxLength` >= `MaxScanBytes`; carry signature databases in by the transfer process and reload; image `clamav/clamav` pinned by digest (not run here). |
| SIEM | TLS 1.2+ collector with a certificate from the internal CA (mTLS recommended); retention and access separate from DB admins; alert on `siem.chain_break` and gaps (siem-onboarding.md). |
| Backups | backup-restore.md; encrypted media; tested restores. |

## Known gaps in this increment
- The UBI image was not built or scanned in this environment; scan it with the site scanner before accrediting.
- Idle session lock, upload malware scanning, vault garbage collection and SIEM forwarding are implemented (increment 5.0, compliance-features.md). Still the operator's: ClamAV signature updates, the SIEM, sticky sessions (the lock registry is in memory; database-backed is Phase 5).
- Production must set `Keel__Vault__Scanning__Mode` (or `AcknowledgeUnscannedUploads`) and cannot set `Keel__Session__IdleLockMinutes=0`; hosts that start without them are refused.
- Proxy-to-app hop is unencrypted by default (see above).
- The compose example's PostgreSQL and proxy images are the tested open-source ones, not RHEL-based FIPS builds.
