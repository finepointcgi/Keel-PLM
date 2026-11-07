# Quickstart: host Keel on one machine

This starts Keel PLM from its published image together with PostgreSQL, the document vault and a sign-in service, using Docker Compose. A setup script makes **random secrets for every service and one administrator account of your own**, so nothing ships with a known password. It takes about five minutes.

It suits a trial or a small private team on one machine. It is plain HTTP and the sign-in service runs in Keycloak's development mode, so for a site that others reach over a network use the [production Compose example](compose-example.md) and your own identity provider.

## Start it

You need Docker with Compose, `openssl` and `python3`. Download the [`deploy/quickstart`](https://github.com/finepointcgi/Keel-PLM/tree/main/deploy/quickstart) folder (or clone the repository) and run:

```bash
cd deploy/quickstart
./setup.sh
docker compose up -d
```

`setup.sh` asks for the administrator's user name, e-mail and password (at least 12 characters; the password is not shown). It writes `.env` (your secrets, mode 600) and a `generated/` folder, and it refuses to run a second time so the secrets always match the data.

The first start pulls the images, creates the database and applies the migrations, which takes about a minute. Then open <http://localhost:8080> and sign in with the account you chose. The system starts empty: use Administration to add people and settings, then create your first part. To explore with sample data instead, use the one-line demo in the [README](../../README.md#try-it-in-one-minute).

**Keep `.env` private and back it up with the data.** Without it the volumes cannot be opened again.

**Ports.** The app uses 8080 and the sign-in service 18080. If either is taken, set `KEEL_PORT` and `KEEL_IDP_PORT` when you run `./setup.sh` (for example `KEEL_PORT=8090 ./setup.sh`); it writes them to `.env` and into the sign-in settings.

**Apple Silicon and other Arm machines.** The published image is built for x86-64, so Docker runs it under emulation on Arm (slower, with a platform warning).

## What runs

| Service | What it is |
|---|---|
| `keel` | The Keel PLM web app and API, from `ghcr.io/finepointcgi/keel-plm` on port 8080 |
| `migrate` | A one-shot job that applies database migrations, then exits |
| `postgres` | PostgreSQL 17 for all records and the audit chain |
| `vault` | SeaweedFS, the S3-compatible store for document files |
| `keycloak` | The sign-in service on port 18080; it holds your administrator and the people you add |
| `keys` | A one-shot job that creates the audit signing key on first start |

Data lives in Docker volumes (`pgdata`, `vaultdata`, `keycloakdata`, `keys`), so it survives restarts and upgrades.

## Choose a version, upgrade, stop

```bash
KEEL_IMAGE=ghcr.io/finepointcgi/keel-plm:0.1.0 docker compose up -d   # pin a release
docker compose pull && docker compose up -d                            # move to the latest
docker compose down                                                    # stop, keep the data
docker compose down -v                                                 # stop and DELETE all data (then remove .env and generated/ to run setup.sh again)
```

## Next steps

The quickstart is not hardened for a network. Before others use it: connect your own identity provider ([Single sign-on and CAC/PIV](sso-integration.md)), use real secrets and TLS ([production example](compose-example.md)), and read the [administrator guide](../manual/24-administrator-and-operator-guide.md).
