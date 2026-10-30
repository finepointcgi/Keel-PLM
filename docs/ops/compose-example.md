# Compose production example (small sites)

`deploy/compose/compose.prod-example.yaml` runs Keel on one host: TLS proxy, application, a one-shot migration job, PostgreSQL and
an S3-compatible vault. The identity provider is **external** (the site IdP); Keycloak exists only in the dev stack. The dev
stack (`compose.yaml`) is unchanged and is not a production deployment.

```
browser --TLS--> proxy (Caddy) --http--> app --TLS verify-full--> postgres
                                          \--TLS--> vault (S3)      \--TLS--> site IdP
```
`backend` is an internal Docker network (no route out); `edge` (fixed subnet 172.28.40.0/24) carries proxy and app and is the only
path to the IdP. The application trusts `X-Forwarded-*` only from that subnet (`Keel__Proxy__KnownNetworks__0`).

## Steps
```bash
cd deploy/compose
# 1. certificates. TEST ONLY: a throwaway CA. A real site requests certificates from its internal CA (see below).
prod-example/gen-test-pki.sh prod-example/certs keel.example.mil
# 2. secrets: random DB and vault credentials, audit checkpoint key; the OIDC secret is the one the IdP issued for client keel-web
KEEL_OIDC_CLIENT_SECRET='...' prod-example/gen-secrets.sh
# 3. settings
cp prod-example/env.example .env && $EDITOR .env
# 4. start (images come from deploy/images.env; the Keel image from the offline bundle or a local build)
docker compose --env-file ../images.env --env-file .env -f compose.prod-example.yaml up -d
```
Start order is enforced: postgres healthy, vault healthy, then `migrate` exits 0, then app healthy, then proxy (healthy when its TLS listener answers the application's liveness endpoint).

## Production properties (ADR-0060; `scripts/verify-packaging.sh` asserts them)
- **Resource limits** on every service (`deploy.resources.limits` and `pids_limit`): app 2 CPUs and 2 GiB, PostgreSQL 2 CPUs and 4 GiB (`shm_size` 256 MB), vault 1 CPU and 2 GiB, proxy 1 CPU and 256 MiB, migration job 1 GiB. Override in `.env` with `KEEL_APP_MEMORY`, `KEEL_APP_CPUS`, `KEEL_POSTGRES_MEMORY`, `KEEL_POSTGRES_CPUS`, `KEEL_VAULT_MEMORY`, `KEEL_VAULT_CPUS`, `KEEL_PROXY_MEMORY`, `KEEL_PROXY_CPUS`, `KEEL_MIGRATE_MEMORY` (`env.example`). These are starting values, see [performance.md](performance.md) for what one machine measured with the application at these limits.
- **Healthchecks** on postgres (`pg_isready`), vault (the store's master `/cluster/status`), app (the image's own probe) and proxy.
- Non-root users for app, migration job and proxy, read-only root filesystems (PostgreSQL's data and the vault's `/data` are volumes; the vault is the one documented read-write root), all capabilities dropped (the few the stock images need are added back explicitly), `no-new-privileges`, no core dumps, secrets only as files, only the proxy publishes a port, the backend network has no route out.
- Install and upgrade with one command and preflight checks: [offline-install.md](offline-install.md).

## Malware scanning, SIEM, session lock (increment 5.0; ClamAV override NOT RUN)
With `KEEL_REQUIRE_FIPS=true` the app refuses to start unless scanning is configured: `KEEL_SCAN_MODE=Required` plus the override `-f compose.clamav.yaml` (clamav on the internal backend network, signatures from your offline mirror into the `clamav-db` volume, `KEEL_CLAMAV_IMAGE` pinned by digest),
or the explicit `KEEL_SCAN_ACK_UNSCANNED=true`. Details: compliance-features.md. `verify-compose-example.sh` runs with `KEEL_REQUIRE_FIPS=false` and scanning disabled, as before; it was not re-run for this increment.

## Certificates
Files needed in `prod-example/certs/`: `ca.crt` (CA chain that signed everything), `proxy.crt/.key` (public name), `postgres.crt/.key`
(SAN `postgres`), `vault.crt/.key` (SAN `vault`). A real site issues them from its internal CA (DoD PKI or enterprise CA) as ECDSA
P-256 or RSA 3072 or larger; `gen-test-pki.sh` shows the openssl commands (30-day validity, EC P-256). The application trusts only
`ca.crt` (`SSL_CERT_FILE`), so the IdP's certificate must chain to the same bundle (concatenate chains if needed). Rotation: replace the
files and `docker compose restart proxy postgres vault app`.

## Secrets
`gen-secrets.sh` writes `prod-example/secrets/` (directory 0700 on the host, files 0444 so container users can read the bind mounts):
`pg_*_password`, `vault_access_key`, `vault_secret_key`, `oidc_client_secret`, `audit_checkpoint_key.pem` (+ `.pub.pem`), and the
`Keel__*` files the application reads through `KEEL_SECRETS_DIR` (configuration.md). Nothing secret is in the compose file, the
environment or an image. **Back up `audit_checkpoint_key.pem` offline** and give auditors the public key. For stronger handling use
Docker/Podman secrets from a secret manager and point the `secrets:` entries at them.

## Blazor circuits and replicas
Each browser tab holds a server-side circuit on one app instance. The example runs one replica. With several, the proxy must pin a
browser to its replica (Caddy `lb_policy cookie keel_affinity`, nginx `hash $cookie_keel_affinity consistent`) **and** every replica
must share the Data Protection key ring (`dpkeys` volume on shared storage), otherwise cookies and antiforgery tokens fail on the
other replica. Use the Helm chart when you need more than one replica.

## FIPS
`Keel__Security__RequireFips` defaults to `true` in this example. On a RHEL 9 host in FIPS mode the kernel flag is visible in the
container and `/health/ready` reports it. Caddy bundles Go's own TLS stack; where the proxy hop needs a FIPS-validated OpenSSL, use
`prod-example/nginx.conf` with `registry.access.redhat.com/ubi9/nginx-126` (pin its digest, mount the file as `/etc/nginx/nginx.conf`
in place of the Caddyfile; not exercised by the repository's tests). Likewise swap the PostgreSQL image for a RHEL-based or vendor
FIPS build. See hardening.md.

## Verified behaviour
`scripts/verify-compose-example.sh` starts this stack with a throwaway PKI, a stub IdP and tmpfs data and checks: health through TLS,
database and vault checks over TLS (verify-full), FIPS posture reporting, `/_framework/blazor.web.js` and `_content/Keel.Web`
assets, `/metrics` blocked at the proxy, anonymous API 401, OIDC redirect URI built from the public https origin, HSTS, non-root user,
read-only root, dropped capabilities, no secrets in the environment, healthy image healthcheck, migration job exit 0, PostgreSQL
refusing non-TLS clients.

The vault container keeps `CHOWN/SETUID/SETGID/DAC_OVERRIDE/FOWNER` because the SeaweedFS entrypoint fixes `/data` ownership and
drops to uid 1000; PostgreSQL keeps the same set for its entrypoint. Neither is reachable from outside the internal network.
The dev compose file starts SeaweedFS without `-master.telemetry=false`; the production example sets it (air-gapped sites must not report statistics).
