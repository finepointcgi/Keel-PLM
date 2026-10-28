# Setup (development)

Everything here uses **synthetic data and dev-only credentials**. Never load real export-controlled data into a dev stack.
macOS/Windows development hosts are not FIPS-validated (ADR-0008); that's expected for dev.

## Prerequisites
- .NET SDK 10.0.402+ (`global.json` pins the feature band)
- Node.js 22+ and npm
- Docker (Desktop or Engine) with Compose v2

## 1. Start the dev stack
```bash
docker compose -f deploy/compose/compose.yaml up -d
```
| Service | Address | Notes |
|---|---|---|
| PostgreSQL 17 | `127.0.0.1:55432` | Roles `keel_migrator` (owner) and `keel_app` (least privilege) created by `deploy/compose/initdb/00-roles.sql` |
| Keycloak 26.4 (dev IdP) | `http://localhost:18080` | Realm `keel` imported from `deploy/compose/keycloak/keel-realm.json`; admin console user `admin` |
| SeaweedFS 4.47 (dev vault, S3 API) | `http://localhost:18333` | `weed mini`; bucket `keel-vault`; dev-only S3 identity in `deploy/compose/seaweedfs/s3.json` (ADR-0015) |

Ports can be overridden with `KEEL_PG_PORT` / `KEEL_IDP_PORT` / `KEEL_VAULT_PORT`. If you change them, update `src/Keel.Api/appsettings.Development.json`.

SeaweedFS sizes its volume count from free disk space. If the Docker disk is nearly full it starts with zero writable
volumes and uploads fail with "no volume server capacity"; free space in Docker (or enlarge its disk) and restart `vault`.

### Vault configuration (`Keel:Vault`)
| Key | Default | Notes |
|---|---|---|
| `ServiceUrl` | – | S3 endpoint; `https://` in production. Path-style addressing is always used. |
| `AccessKey`, `SecretKey` | – | Store credentials (secrets via environment or mounted files, never images). |
| `Bucket` | `keel-vault` | Must exist; the application does not create buckets. |
| `ServerSideEncryption` | `true` (appsettings.json) | SSE-S3 (`AES256`) on every object. |
| `MaxUploadBytes` | 2147483648 (2 GiB) | At most 5 GiB. |
| `PartSizeBytes` | 16777216 | Upload part size; the most memory one upload holds. |
| `MaxPdfStampBytes` | 268435456 | Larger PDFs are delivered in a marked ZIP instead of stamped in place. |

Readiness (`/health/ready`) includes a `vault` check that lists the bucket with the configured identity.

## 2. Create the audit checkpoint key (once)
```bash
dotnet run --project src/Keel.AuditCli -- keygen --out deploy/compose/secrets
```
`deploy/compose/secrets/` is git-ignored. In production the private key lives outside the database host (ADR-0005).

## 3. Apply migrations and run the API
```bash
cd src/Keel.Api && ASPNETCORE_ENVIRONMENT=Development dotnet run -- --migrate && ASPNETCORE_ENVIRONMENT=Development dotnet run
```
The API listens on `http://localhost:5080`. Health: `/health/live`, `/health/ready` (shows FIPS posture). Metrics: `/metrics`.

## 4. Run the web app
```bash
npm --prefix web install && npm --prefix web run dev
```
Open `http://localhost:5173`. The Vite dev server proxies `/api` to the API so the browser sees a single origin.

## Synthetic users
Password for every user is `keel-dev-<username>` (dev realm only).

| User | U.S. person | Citizenship | Programs | Export authorizations | Roles | Use it to see… |
|---|---|---|---|---|---|---|
| alice | yes | USA | ORCA | – | viewer, author | normal engineer access |
| bob | yes | USA | ORCA | – | viewer, approver | approvals (1.5) |
| carmen | yes | USA | – | – | viewer, author | program need-to-know denials |
| farid | no | GBR | ORCA | TAA-0042 | viewer, author | licensed foreign-person access |
| lena | no | DEU | ORCA | – | viewer | ITAR denial |
| audrey | yes | USA | – | – | auditor | audit log |
| ada | yes | USA | ORCA | – | admin, viewer | checkpoints; admin does **not** bypass markings |

## Tests
```bash
scripts/test.sh          # everything (CI entry point); needs Docker running
scripts/smoke.sh         # against the running dev stack: real Keycloak tokens -> real API (incl. vault upload/download)
```
Verify the audit chain offline:
```bash
KEEL_AUDIT_CONNECTION="Host=localhost;Port=55432;Database=keel;Username=keel_app;Password=keel_app_dev" dotnet run --project src/Keel.AuditCli -- verify --public-key deploy/compose/secrets/checkpoint-signing.pub.pem
```

## Production notes (summary; full ops guide is Phase 5)
- Set `Keel__Security__RequireFips=true` (the default in `appsettings.json`) on FIPS-mode RHEL hosts.
- Restrict `/metrics` to the monitoring network (NetworkPolicy / host firewall).
- Enable pgAudit on PostgreSQL so superuser actions that bypass audit triggers are themselves recorded.
- Supply connection strings and key paths through environment variables or mounted secrets, never images.
