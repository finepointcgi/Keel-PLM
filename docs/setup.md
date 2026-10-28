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

Ports can be overridden with `KEEL_PG_PORT` / `KEEL_IDP_PORT`. If you change them, update `src/Keel.Api/appsettings.Development.json`.

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
scripts/smoke.sh         # against the running dev stack: real Keycloak tokens -> real API
```
Verify the audit chain offline:
```bash
KEEL_AUDIT_CONNECTION="Host=localhost;Port=55432;Database=keel;Username=keel_app;Password=keel_app_dev" dotnet run --project src/Keel.AuditCli -- verify --public-key deploy/compose/secrets/checkpoint-signing.pub.pem
```

## Production notes (summary; full ops guide is Phase 5)
- Set `Keel__Security__RequireFips=true` (the default in `appsettings.json`) on FIPS-mode RHEL hosts.
- Restrict `/metrics` to the monitoring network (NetworkPolicy / host firewall).
- Persist and share ASP.NET Data Protection keys across replicas (cookie sessions and antiforgery depend on them), and use sticky sessions for Blazor circuits.
- Dev realm users have fixed IDs (`sub`) so re-importing the realm keeps audit records and check-outs attributable; production IdPs must likewise never reissue a user's `sub`.
- Enable pgAudit on PostgreSQL so superuser actions that bypass audit triggers are themselves recorded.
- Supply connection strings and key paths through environment variables or mounted secrets, never images.
