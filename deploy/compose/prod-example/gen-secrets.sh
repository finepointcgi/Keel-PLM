#!/usr/bin/env bash
# Creates the secret files the compose production example mounts (docs/ops/configuration.md, docs/ops/compose-example.md).
# Random credentials are generated for the database roles and the vault; the OIDC client secret is whatever the site IdP
# issued for the confidential client "keel-web" and is read from KEEL_OIDC_CLIENT_SECRET or a hidden prompt.
# The audit checkpoint key is generated with keel-audit keygen when the keel image is available, otherwise with openssl
# (both give an ECDSA P-256 PKCS#8 PEM). Nothing here is written to git: secrets/ is git-ignored.
#
#   gen-secrets.sh [secrets-dir]        default: ./secrets
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
OUT=${1:-"$here/secrets"}
umask 077
mkdir -p "$OUT"
cd "$OUT"

rand() { openssl rand -hex 24; }
put() { # name value ; never overwrite an existing secret
  if [[ -e "$1" ]]; then echo "keep   $1"; else printf '%s' "$2" > "$1"; echo "create $1"; fi
}

put pg_superuser_password "$(rand)"
migrator_pw=$(cat pg_migrator_password 2>/dev/null || rand); put pg_migrator_password "$migrator_pw"
app_pw=$(cat pg_app_password 2>/dev/null || rand);           put pg_app_password "$app_pw"
vault_access=$(cat vault_access_key 2>/dev/null || echo "keel$(openssl rand -hex 6)"); put vault_access_key "$vault_access"
vault_secret=$(cat vault_secret_key 2>/dev/null || rand);    put vault_secret_key "$vault_secret"

if [[ ! -e oidc_client_secret ]]; then
  secret=${KEEL_OIDC_CLIENT_SECRET:-}
  if [[ -z "$secret" ]]; then read -r -s -p "OIDC client secret for keel-web (from the site IdP): " secret; echo; fi
  [[ -n "$secret" ]] || { echo "an OIDC client secret is required" >&2; exit 1; }
  put oidc_client_secret "$secret"
fi

if [[ ! -e audit_checkpoint_key.pem ]]; then
  openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out audit_checkpoint_key.pem 2>/dev/null
  openssl pkey -in audit_checkpoint_key.pem -pubout -out audit_checkpoint_key.pub.pem
  echo "create audit_checkpoint_key.pem (+ .pub.pem: ship the PUBLIC key to the auditors; back the private key up offline)"
fi

rm -f Keel__* vault_s3.json
# Files in the shape the application reads from KEEL_SECRETS_DIR (file name = setting name with "__" for ":").
db="Database=keel;SSL Mode=VerifyFull;Root Certificate=/etc/keel/ca/ca.crt"
printf 'Host=postgres;Port=5432;%s;Username=keel_app;Password=%s' "$db" "$app_pw" > Keel__Database__AppConnectionString
printf 'Host=postgres;Port=5432;%s;Username=keel_migrator;Password=%s' "$db" "$migrator_pw" > Keel__Database__MigratorConnectionString
cp oidc_client_secret Keel__Auth__WebClientSecret
cp vault_access_key Keel__Vault__AccessKey
cp vault_secret_key Keel__Vault__SecretKey
# Vault identity file for the SeaweedFS store (the store's own credential format).
cat > vault_s3.json <<JSON
{"identities":[{"name":"keel-app","credentials":[{"accessKey":"$vault_access","secretKey":"$vault_secret"}],"actions":["Read:keel-vault","Write:keel-vault","List:keel-vault","Tagging:keel-vault"]}]}
JSON
# Readable inside the containers through bind mounts; the directory is 0700 on the host.
chmod 0444 ./*
chmod 0700 .
echo "secrets are in $OUT (directory mode 0700, files 0444 so the container users can read them through bind mounts)"
