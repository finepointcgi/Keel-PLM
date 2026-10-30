# 24. Administrator and operator guide

This chapter is for the people who install, configure, back up and keep Keel running. It is a map: every setting, script and
procedure lives in the operations documents under [docs/ops](../ops/), and this chapter points to them instead of repeating them. It
adds nothing that those documents do not say. Where something is not shipped or not verified, this chapter says so.

Day-to-day configuration of the product itself (attributes, lifecycles, numbering, classification) is a different job, done in the
browser by an administrator; that is [chapter 23](23-administration.md).

## What you are running

Keel is one web host (`Keel.Api`) that serves the browser UI and the REST API, plus three things it needs and does not bundle:

| Piece | What it is | Notes |
|---|---|---|
| Keel host | .NET 10 application, Blazor Interactive Server UI and REST API in one process | Container image on a UBI 9 base, runs as a non-root user |
| PostgreSQL 17 | All data, including the audit chain | Two roles: `keel_migrator` (owns the schema, used only by the migration job) and `keel_app` (least privilege, used by the host) |
| Vault | An S3-compatible object store (SeaweedFS in development) | Content-addressed by SHA-256; the application never creates the bucket |
| Identity provider | Your site IdP (CAC/PIV, MFA); Keycloak with synthetic users in development | Keel never sees a password and does not implement MFA or lockout |

## Install

Three supported shapes, in order of size. All three are described step by step in the linked documents.

| Shape | Use it for | Document |
|---|---|---|
| Development stack (Docker Compose: PostgreSQL, Keycloak, SeaweedFS) and `dotnet run` | A developer or demo machine. Synthetic data and dev-only credentials only | [setup.md](../setup.md) |
| Compose production example (proxy, application, one-shot migration job, PostgreSQL, vault; external IdP) | A small site on one host | [ops/compose-example.md](../ops/compose-example.md) |
| Helm chart (`deploy/helm/keel`; PostgreSQL, vault and IdP external) | A Kubernetes site | [ops/kubernetes.md](../ops/kubernetes.md) |

Things to know before you start:

- **Start order is enforced.** PostgreSQL healthy, then the `migrate` service or hook job exits 0, then the application starts. Only the
  migration job receives the migrator credential.
- **Production refuses to start in unsafe configurations.** `RequireFips` is true by default (the host checks that the kernel is in FIPS
  mode); upload scanning must be configured or explicitly acknowledged; the idle session lock cannot be switched off. See
  [ops/configuration.md](../ops/configuration.md) and [ops/compliance-features.md](../ops/compliance-features.md).
- **Production image.** Build with `--target runtime`; the last Dockerfile stage `demo` is the single-container demo image and is not for production
  ([ops/cloud-run-demo.md](../ops/cloud-run-demo.md)).
- **Verification status.** The Helm chart was validated by `scripts/verify-helm.sh` but not installed on a cluster; the UBI image build and the
  FIPS-enabled startup on a real FIPS host were not run in the build environment ([increments/1.7-packaging.md](../increments/1.7-packaging.md),
  [ops/hardening.md](../ops/hardening.md)). Treat both as items for your own acceptance test.

## Configuration

Settings come from three places, in increasing precedence: `appsettings.json` (shipped, secure defaults), environment variables
(`Keel__Section__Key`), and files in the directory named by `KEEL_SECRETS_DIR` (one file per setting; this is the shape of a Docker or
Kubernetes secret mount, and the way credentials should be supplied).

The complete reference with defaults and whether production needs each value is
[ops/configuration.md](../ops/configuration.md). The groups you will touch first:

| Group | What it controls |
|---|---|
| `Keel:Database` | Connection strings (app role for the host, migrator role for the migration job only) |
| `Keel:Auth` | The IdP authority, the confidential client `keel-web` and its secret, the API audience |
| `Keel:Audit` | The checkpoint signing key and the public keys trusted for verification |
| `Keel:Security` | `RequireFips` |
| `Keel:DataProtection` | The key ring directory; it must be the same for every replica and survive restarts |
| `Keel:Proxy` | Honour `X-Forwarded-*` from named networks (required behind a TLS proxy or ingress) |
| `Keel:Vault` (and `Scanning`, `Gc`) | Store URL and credentials, encryption, upload limits, malware scanning, garbage collection |
| `Keel:Session` | Idle lock (default 15 minutes, 60 second warning, locked sessions end after 15 minutes) |
| `Keel:Integration` | Outbox dispatcher, allowed hosts and networks, file-drop root, TDP signing key |
| `Keel:Siem` | Forwarding of the audit log |
| `Keel:Workflow` | How recently a signer must have authenticated (default 5 minutes) |
| `Keel:Notifications` | Retention, fan-out and the optional e-mail channel ([ops/notifications.md](../ops/notifications.md)) |

Register `https://<public host>/signin-oidc` and `https://<public host>/signout-callback-oidc` as redirect URIs of the `keel-web` client at your IdP.

## Day-two operations

| Task | Where it is described |
|---|---|
| Health and readiness (`/health/live`, `/health/ready`), metrics (`/metrics`, reachable only from the monitoring network) | [ops/configuration.md](../ops/configuration.md), [ops/hardening.md](../ops/hardening.md) |
| Upgrades: forward-only SQL migrations, job before rollout, rollback means restoring the backup taken just before | [ops/upgrade.md](../ops/upgrade.md) |
| Backup and restore: PostgreSQL base backup plus WAL, the vault, and the keys, all together | [ops/backup-restore.md](../ops/backup-restore.md) |
| Disaster recovery options and proposed targets (proposals for the product owner to confirm, not agreed targets) | [ops/disaster-recovery.md](../ops/disaster-recovery.md) |
| Audit chain verification after every restore | [chapter 20](20-audit-and-integrity.md), `keel-audit verify` |
| Vault garbage collection (`keel-vault gc`, dry run by default) and archive verification (`keel-vault verify-archive`) | [ops/compliance-features.md](../ops/compliance-features.md), [ops/records-management.md](../ops/records-management.md) |
| SIEM forwarding of audit events (TLS syslog or NDJSON file; UDP is refused) | [ops/siem-onboarding.md](../ops/siem-onboarding.md) |
| ERP outbox, webhooks, file drops, dead letters | [ops/integration.md](../ops/integration.md), [chapter 21](21-integration-and-tdp.md) |
| E-mail notifications over an on-premises SMTP relay | [ops/notifications.md](../ops/notifications.md) |
| Performance measurements and the scale tests | [ops/performance.md](../ops/performance.md) |
| Security hardening mapping and what the operator must still do | [ops/hardening.md](../ops/hardening.md), [ops/nist-800-171-mapping.md](../ops/nist-800-171-mapping.md) |

### The three things to back up together

Back up PostgreSQL, the vault and the keys as one set, because the audit chain, the document rows and the vault objects refer to each
other. The keys include the audit checkpoint **private** key (offline, two custodians, never next to the database), the retired public
keys (keep them forever: old checkpoints need them), the Data Protection key ring and the TLS and CA material. Losing the key ring signs
everybody out. Details and the restore procedure: [ops/backup-restore.md](../ops/backup-restore.md). A restore into an environment that
is not accredited for the data is not allowed: backups hold export-controlled data and carry the same handling rules as the live system.

### Seeing what the system is doing

- **Idle session lock.** The lock state lives in the database, so restarts and several replicas share it. The REST API with bearer tokens is not affected.
- **Upload scanning.** `Required` refuses an upload the scanner cannot judge; `BestEffort` accepts it as unscanned. Signature databases for ClamAV are yours to carry in
  and reload on an air-gapped site; old signatures mean a weak control.
- **Integration lag.** Readiness reports Degraded when the oldest due delivery waited longer than `LagDegradedSeconds`.

## Air-gapped installation

Keel is designed for networks with no internet. The product itself never needs the network (the viewer, fonts and scripts are vendored in the build).

The offline bundle (`keel-offline-<version>.tar`) contains the image archives, the Helm chart, the compose example, the SBOMs, the corresponding
source of the LGPL components, licence texts, a signed SHA-256 manifest and the install and upgrade scripts, which use no network at any point.

| Step | Detail |
|---|---|
| Build (connected machine) | `scripts/package-offline.sh --version <version>`; the signing key is generated per release unless you supply your own with `KEEL_SIGNING_KEY` |
| Transfer | Carry the tar and its `.sha256`; announce the signing key fingerprint through a separate channel |
| Verify | `verify.sh` checks every file against the manifest and the manifest against the release public key |
| Install | `install.sh` (compose or Kubernetes). Upgrades use `upgrade.sh ... --backup-done`, which refuses downgrades and same-version re-installs |
| Licences and sources | `sources/`, `licenses/`, `THIRD-PARTY-NOTICES.md` and `sbom/keel-plm.cdx.json` (generated offline) |

Full description: [ops/offline-install.md](../ops/offline-install.md). Other air-gap notes from the existing documents:

- ClamAV signatures, the audit and TDP signing keys and any CA bundles arrive through your own transfer process.
- The FreeCAD add-on needs only the Python that ships with FreeCAD; its tests can install from a local wheel folder
  (`KEEL_ADDON_PIP_ARGS="--no-index --find-links DIR"`, see [chapter 11](11-cad-connector-and-freecad.md)).
- Image SBOMs of the OS packages are produced only when `syft` is installed on the build machine; otherwise the bundle says so in `BUILD-INFO`.
- The identity provider must be reachable from the Keel host (discovery and key set are fetched at runtime). It is the one dependency that is
  on your network by design.

## What is not shipped or not verified

Said plainly, from the existing documents:

- **High availability and failover automation**, a bulk vault integrity verifier and load-tested recovery figures are open items ([ops/disaster-recovery.md](../ops/disaster-recovery.md)).
- **CAC/PIV sign-in** is the site identity provider's job; the development realm uses passwords.
- **Commercial CAD plug-ins** (SolidWorks, NX, Creo, CATIA) were not built ([integration/cad-plugins.md](../integration/cad-plugins.md)).
- **The legal and export-control review packets** (`docs/review/`) are all pending reviewer sign-off and are not legal advice.
- **AMQP 1.0 and Kafka sinks** for the outbox are not built; webhooks, file drops and AMQP 0-9-1 brokers are shipped ([ops/integration.md](../ops/integration.md)).
- **A formal accessibility report** (VPAT/ACR) needs a human tester; the automated audit is a self-assessment ([ux/accessibility.md](../ux/accessibility.md)).
