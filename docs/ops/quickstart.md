# Quickstart: host Keel on one machine

This starts Keel PLM from its published image together with PostgreSQL, the document vault and a sign-in service, using Docker Compose. It takes about two minutes and is meant for **trials and evaluation**: the sign-in is a development one with synthetic users and fixed passwords, so **never put real data in it**. For a real site use the [production Compose example](compose-example.md) and your own identity provider.

## Start it

You need Docker with Compose. Download the [`deploy/quickstart`](https://github.com/finepointcgi/Keel-PLM/tree/main/deploy/quickstart) folder (or clone the repository) and run:

```bash
cd deploy/quickstart
docker compose up -d
```

The first start pulls the images, creates the database and applies the migrations, which takes about a minute. Then open <http://localhost:8080> and sign in as `alice`. The demo accounts and what each can do are in [Getting started](../manual/01-getting-started.md).

**Ports.** The app uses 8080 and the sign-in service 18080. If either is taken, set `KEEL_PORT` and `KEEL_IDP_PORT`; a changed app port also needs a copy of `keel-realm.json` with that port in the `keel-web` client's redirect addresses, passed as `KEEL_REALM_FILE`.

**Apple Silicon and other Arm machines.** The published image is built for x86-64, so Docker runs it under emulation on Arm (slower, with a platform warning).

## What runs

| Service | What it is |
|---|---|
| `keel` | The Keel PLM web app and API, from `ghcr.io/finepointcgi/keel-plm` on port 8080 |
| `migrate` | A one-shot job that applies database migrations, then exits |
| `postgres` | PostgreSQL 17 for all records and the audit chain |
| `vault` | SeaweedFS, the S3-compatible store for document files |
| `keycloak` | Development sign-in on port 18080, with synthetic users |
| `keys` | A one-shot job that creates the audit signing key on first start |

Data lives in Docker volumes (`pgdata`, `vaultdata`, `keys`), so it survives restarts and upgrades.

## Choose a version, upgrade, stop

```bash
KEEL_IMAGE=ghcr.io/finepointcgi/keel-plm:0.1.0 docker compose up -d   # pin a release
docker compose pull && docker compose up -d                            # move to the latest
docker compose down                                                    # stop, keep the data
docker compose down -v                                                 # stop and DELETE all data
```

## Next steps

The quickstart is not hardened. Before real use: connect your own identity provider ([Single sign-on and CAC/PIV](sso-integration.md)), use real secrets and TLS ([production example](compose-example.md)), and read the [administrator guide](../manual/24-administrator-and-operator-guide.md).
