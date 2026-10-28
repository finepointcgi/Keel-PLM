# Setup (development)

Everything here uses **synthetic data and dev-only credentials**. Never load real export-controlled data into a dev stack.
macOS/Windows development hosts are not FIPS-validated (ADR-0008); that's expected for dev.

## Prerequisites
- .NET SDK 10.0.402+ (`global.json` pins the feature band)
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
The host serves both the Blazor UI and the REST API on `http://localhost:5080` (ADR-0014).
Open `http://localhost:5080` and sign in at the dev Keycloak. Health: `/health/live`, `/health/ready` (shows FIPS posture). Metrics: `/metrics`.

The UI signs in server-side as the confidential OIDC client `keel-web` (dev secret `keel-web-dev-secret`, configured in
`appsettings.Development.json` and the dev realm). In production supply `Keel__Auth__WebClientSecret` from a secret store.

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

## Demo data (`keel-seed`, increment 1.7)
`src/Keel.Seed` fills a **migrated** database with synthetic data, entirely through the module services (so every action is authorized, validated and audited, and the audit chain verifies at the end). It needs the vault (documents have real PDFs).
```bash
scripts/seed.sh                                   # = dotnet run --project src/Keel.Seed -- --scenario demo
dotnet run --project src/Keel.Seed -- --scenario scale --lines 50000 --levels 8 --allow-non-empty   # bulk structure for performance demos (off by default)
```
| Option | Meaning |
|---|---|
| `--scenario demo` | The fictional ORCA sensor pod: 63 parts, 74 BOM lines over 5 levels, 12 documents (generated PDFs plus a CAD placeholder), 58 released revisions with e-signatures, and changes in flight (PR, ECR, ECNs, tasks). It prints the numbers the [demo script](demo-script.md) uses. |
| `--scenario scale` | Wraps the performance-gate generator (`--lines`, `--levels`, `--root-children`, `--released`, `--prefix`). Bulk SQL as `keel_app`: guards apply, but generated rows have no per-object audit events. |
| `--allow-non-empty` | The tool refuses when `plm.part_master` already has rows (exit code 2); this bypasses that explicitly. |
| `--connection`, `--settings`, `--public-key`, `--manifest` | Override the connection, add an appsettings file, verify checkpoint signatures, write the summary as JSON. |

The connection is resolved like the API's: `src/Keel.Api/appsettings.Development.json` (found from the working directory), then environment variables with the same keys (`Keel__Database__AppConnectionString`, `Keel__Vault__ServiceUrl`, …), then `--connection`. **It defaults to the dev stack, so set the variables when you mean another database.**
Users come from the dev realm itself (the realm JSON is embedded in the tool, so ids and attributes cannot drift) and are recorded in `identity.user_directory` through the same upsert the application runs at sign-in; passwords are never read. One seed-only identity, `kraken-eng` (programs KRAKEN and ORCA), authors the KRAKEN part and is not a login. Exit codes: 0 ok, 1 failure, 2 refused (non-empty), 3 audit chain invalid.
Reset the dev database before re-seeding: see "Reset" in [demo-script.md](demo-script.md).

## End-to-end tests (Playwright, increment 1.7)
```bash
scripts/e2e.sh                                       # builds, then runs tests/Keel.E2E with KEEL_E2E=1
scripts/e2e.sh -- --filter-class "*HappyPathTests"   # one class
```
The suite is skipped unless `KEEL_E2E=1`, so `dotnet test` of the other projects stays fast. It needs Docker and, once, a Chromium download (Playwright's installer runs from the test setup; the product never needs the network). The fixture starts **its own** PostgreSQL, SeaweedFS and Keycloak containers (tmpfs, random host ports, dev realm imported with a patched redirect URI), applies migrations with `Keel.Api --migrate`, seeds the ORCA data, and runs the real `Keel.Api` host as a child process (`Keel__Workflow__StepUpMaxAgeSeconds=30`, the product minimum, so the step-up round trip is testable) on a free port. It never uses ports 5080, 18080, 55432 or 18333, and every container is removed at the end. Logs and, for failures, screenshots and Playwright traces are written to `TestResults/e2e/` (`npx playwright show-trace` or <https://trace.playwright.dev> opens a trace zip).

## Production notes (summary; full ops guide is Phase 5)
- Set `Keel__Security__RequireFips=true` (the default in `appsettings.json`) on FIPS-mode RHEL hosts.
- Restrict `/metrics` to the monitoring network (NetworkPolicy / host firewall).
- Persist and share ASP.NET Data Protection keys across replicas (cookie sessions and antiforgery depend on them), and use sticky sessions for Blazor circuits.
- Dev realm users have fixed IDs (`sub`) so re-importing the realm keeps audit records and check-outs attributable; production IdPs must likewise never reissue a user's `sub`.
- Enable pgAudit on PostgreSQL so superuser actions that bypass audit triggers are themselves recorded.
- Supply connection strings and key paths through environment variables or mounted secrets, never images.
