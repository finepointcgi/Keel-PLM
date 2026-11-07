#!/usr/bin/env bash
# Prepares the quickstart: random secrets for every service and one administrator account of your own. Run it once, before `docker compose up -d`.
#   ./setup.sh                       asks for the administrator's name, e-mail and password (the password is not shown)
#   KEEL_ADMIN_USER=pat KEEL_ADMIN_EMAIL=pat@example.org ./setup.sh     ask only for the password
# Writes .env (mode 600) and generated/. It refuses to run twice, because new secrets would not match the data already in the volumes; to start over,
# `docker compose down -v` (this deletes the data), remove .env and generated/, and run it again.
set -euo pipefail
cd "$(dirname "$0")"

command -v openssl >/dev/null || { echo "openssl is needed to make the secrets." >&2; exit 1; }
command -v python3 >/dev/null || { echo "python3 is needed to write the sign-in realm." >&2; exit 1; }
if [ -e .env ] || [ -d generated ]; then
  echo "This folder is already set up (.env or generated/ exists). See the header of this script to start over." >&2
  exit 1
fi

rand() { openssl rand -hex 24; }

user="${KEEL_ADMIN_USER:-}"
email="${KEEL_ADMIN_EMAIL:-}"
[ -n "$user" ] || { read -r -p "Administrator user name [admin]: " user; user="${user:-admin}"; }
[ -n "$email" ] || { read -r -p "Administrator e-mail: " email; }
[[ "$user" =~ ^[a-z][a-z0-9._-]{1,30}$ ]] || { echo "The user name is lower-case letters, digits, dot, dash or underscore, starting with a letter." >&2; exit 1; }
[[ "$email" == *@*.* ]] || { echo "That does not look like an e-mail address." >&2; exit 1; }

pw="${KEEL_ADMIN_PASSWORD:-}"
if [ -z "$pw" ]; then
  while true; do
    read -r -s -p "Administrator password (at least 12 characters): " pw; echo
    read -r -s -p "Again: " pw2; echo
    [ "$pw" = "$pw2" ] || { echo "The two do not match." >&2; continue; }
    [ "${#pw}" -ge 12 ] || { echo "Use at least 12 characters." >&2; continue; }
    break
  done
fi
[ "${#pw}" -ge 12 ] || { echo "The password needs at least 12 characters." >&2; exit 1; }

port="${KEEL_PORT:-8080}"
idp_port="${KEEL_IDP_PORT:-18080}"
pg_migrator="$(rand)"; pg_app="$(rand)"; pg_super="$(rand)"
vault_access="$(rand)"; vault_secret="$(rand)"; web_secret="$(rand)"; kc_admin="$(rand)"

umask 077
mkdir generated
cat > .env <<ENV
# Written by setup.sh. Holds the secrets of this installation: keep it private and back it up with the data.
KEEL_PORT=$port
KEEL_IDP_PORT=$idp_port
KEEL_PG_SUPERUSER_PASSWORD=$pg_super
KEEL_PG_MIGRATOR_PASSWORD=$pg_migrator
KEEL_PG_APP_PASSWORD=$pg_app
KEEL_VAULT_ACCESS_KEY=$vault_access
KEEL_VAULT_SECRET_KEY=$vault_secret
KEEL_WEB_CLIENT_SECRET=$web_secret
KEEL_KEYCLOAK_ADMIN_PASSWORD=$kc_admin
ENV

cat > generated/00-roles.sql <<SQL
CREATE ROLE keel_migrator LOGIN PASSWORD '$pg_migrator';
CREATE ROLE keel_app      LOGIN PASSWORD '$pg_app';
CREATE DATABASE keel OWNER keel_migrator;
REVOKE ALL ON DATABASE keel FROM PUBLIC;
GRANT CONNECT ON DATABASE keel TO keel_app;
\connect keel
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO keel_migrator;
SQL

cat > generated/s3.json <<JSON
{ "identities": [ { "name": "keel-app",
  "credentials": [ { "accessKey": "$vault_access", "secretKey": "$vault_secret" } ],
  "actions": ["Read:keel-vault", "Write:keel-vault", "List:keel-vault", "Tagging:keel-vault"] } ] }
JSON

# The realm: the template without its synthetic users, the dev-only command-line client removed, the web client given this site's address and secret,
# and one administrator. The password travels in the environment, never on a command line.
KEEL_SETUP_PW="$pw" KEEL_SETUP_USER="$user" KEEL_SETUP_EMAIL="$email" KEEL_SETUP_PORT="$port" KEEL_SETUP_SECRET="$web_secret" python3 - <<'PY'
import json, os
r = json.load(open("keel-realm.json"))
port = os.environ["KEEL_SETUP_PORT"]
r["displayName"] = "Keel PLM"
r["clients"] = [c for c in r["clients"] if c["clientId"] == "keel-web"]
web = r["clients"][0]
web["secret"] = os.environ["KEEL_SETUP_SECRET"]
web["redirectUris"] = [f"http://localhost:{port}/signin-oidc"]
web["webOrigins"] = [f"http://localhost:{port}"]
web["attributes"]["post.logout.redirect.uris"] = f"http://localhost:{port}/signout-callback-oidc"
r["users"] = [{
    "username": os.environ["KEEL_SETUP_USER"], "enabled": True, "emailVerified": True,
    "firstName": "Site", "lastName": "Administrator", "email": os.environ["KEEL_SETUP_EMAIL"],
    "attributes": {"us_person": ["true"], "citizenship": ["USA"], "org": ["IT"], "programs": [], "export_authorizations": [], "cui_categories": ["EXPT", "CTI"]},
    "credentials": [{"type": "password", "value": os.environ["KEEL_SETUP_PW"], "temporary": False}],
    "realmRoles": ["plm-admin", "plm-viewer", "plm-author", "plm-approver"],
}]
json.dump(r, open("generated/keel-realm.json", "w"), indent=2)
PY

# The containers run as their own users and read these through bind mounts, so they cannot be private to this account; .env stays mode 600.
chmod 0755 generated
chmod 0644 generated/*

echo
echo "Done. Next:"
echo "  docker compose up -d"
echo "  open http://localhost:$port and sign in as $user"
echo "The sign-in service's own console is http://keel.localhost:$idp_port (user admin; its password is KEEL_KEYCLOAK_ADMIN_PASSWORD in .env)."
