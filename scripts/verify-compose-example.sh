#!/usr/bin/env bash
# Starts the compose production example with a throwaway PKI, throwaway secrets and a stub IdP, probes it from the outside,
# and removes everything it created (containers, networks, volumes, generated certs and secrets).
#
#   KEEL_APP_IMAGE=keel/api:1.7.0 scripts/verify-compose-example.sh [--keep]
#
# Needs: docker compose v2.24+, openssl, curl, python3, and an application image (scripts/package-offline.sh builds one).
# KEEL_REQUIRE_FIPS defaults to false here because the machine running this test is usually not FIPS-mode; the probe asserts
# the FIPS status reported through /health/ready instead. Set KEEL_REQUIRE_FIPS=true on a FIPS host to test enforcement.
set -euo pipefail
cd "$(dirname "$0")/.."
KEEP=false; [[ "${1:-}" == "--keep" ]] && KEEP=true
: "${KEEL_APP_IMAGE:?set KEEL_APP_IMAGE to the image to test}"
PORT=${KEEL_VERIFY_PORT:-18443}
PROJECT=keel-verify
EX=deploy/compose/prod-example
COMPOSE=(docker compose -p "$PROJECT" --env-file deploy/images.env --env-file "$EX/test/verify.env"
         -f deploy/compose/compose.prod-example.yaml -f "$EX/test/compose.test.yaml")

[[ -e "$EX/certs" || -e "$EX/secrets" ]] && { echo "refusing to run: $EX/certs or $EX/secrets already exist (remove them or use your own project)"; exit 2; }

cleanup() {
  local status=$?
  [[ $status -ne 0 ]] && "${COMPOSE[@]}" logs --tail 60 2>&1 | tail -150 || true
  if ! $KEEP; then
    "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
    rm -rf "$EX/certs" "$EX/secrets" "$EX/test/verify.env"
  fi
  exit $status
}
trap cleanup EXIT

cat > "$EX/test/verify.env" <<EOF
KEEL_APP_IMAGE=$KEEL_APP_IMAGE
KEEL_PUBLIC_HOST=keel.test
KEEL_HTTPS_PORT=$PORT
KEEL_OIDC_AUTHORITY=https://idp.test:8443/realms/keel
KEEL_REQUIRE_FIPS=${KEEL_REQUIRE_FIPS:-false}
EOF

"$EX/gen-test-pki.sh" >/dev/null
KEEL_OIDC_CLIENT_SECRET=synthetic-client-secret "$EX/gen-secrets.sh" >/dev/null

echo "== starting the stack"
"${COMPOSE[@]}" up -d --wait --wait-timeout 240

CURL=(curl -sS --max-time 20 --cacert "$EX/certs/ca.crt" --resolve "keel.test:$PORT:127.0.0.1")
BASE="https://keel.test:$PORT"
ok=0; fail=0
check() { # name expected actual
  if [[ "$2" == "$3" ]]; then echo "ok   $1"; ok=$((ok+1)); else echo "FAIL $1: expected '$2', got '$3'"; fail=$((fail+1)); fi
}
code() { "${CURL[@]}" -o /dev/null -w '%{http_code}' "$@"; }

check "liveness through the TLS proxy"        200 "$(code "$BASE/health/live")"
READY=$("${CURL[@]}" "$BASE/health/ready")
echo "ready: $READY"
field() { python3 -c 'import sys,json;d=json.loads(sys.argv[1]);print(eval(sys.argv[2]))' "$READY" "$1"; }
check "database check healthy"                Healthy "$(field "d['checks']['database']['status']")"
check "vault check healthy over TLS"          Healthy "$(field "d['checks']['vault']['status']")"
EXPECT_FIPS=Degraded; [[ "${KEEL_REQUIRE_FIPS:-false}" == "true" ]] && EXPECT_FIPS=Healthy
check "fips posture reported ($EXPECT_FIPS)"  "$EXPECT_FIPS" "$(field "d['checks']['fips']['status']")"
check "framework script served"               200 "$(code "$BASE/_framework/blazor.web.js")"
check "Keel.Web static assets served"         200 "$(code "$BASE/_content/Keel.Web/$(ls src/Keel.Web/wwwroot | head -1)" || true)" || true
check "/metrics is not published by the proxy" 404 "$(code "$BASE/metrics")"
check "API rejects anonymous callers"         401 "$(code "$BASE/api/v1/me")"
LOCATION=$("${CURL[@]}" -o /dev/null -D - "$BASE/" | tr -d '\r' | sed -n 's/^[Ll]ocation: //p')
case "$LOCATION" in
  *"redirect_uri=https%3A%2F%2Fkeel.test%3A$PORT%2Fsignin-oidc"*) check "OIDC redirect URI uses the public https origin" yes yes ;;
  *) check "OIDC redirect URI uses the public https origin" yes "no: $LOCATION" ;;
esac
HEADERS=$("${CURL[@]}" -o /dev/null -D - "$BASE/health/live")
check "HSTS header present"                   yes "$(grep -qi '^strict-transport-security' <<<"$HEADERS" && echo yes || echo no)"
check "plain http to the TLS port is not served" 400 "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "http://127.0.0.1:$PORT/health/live" || true)"

APP=$("${COMPOSE[@]}" ps -q app)
check "app runs as 1001:0"                    "1001:0" "$(docker inspect -f '{{.Config.User}}' "$APP")"
check "app root filesystem is read-only"      true "$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}}' "$APP")"
check "app drops all capabilities"            "[ALL]" "$(docker inspect -f '{{.HostConfig.CapDrop}}' "$APP")"
check "no secret in the app environment"      0 "$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$APP" | grep -ciE 'password|secret_?key|clientsecret' || true)"
check "image healthcheck reports healthy"     healthy "$(docker inspect -f '{{.State.Health.Status}}' "$APP")"
check "migrate job exited 0"                  0 "$(docker inspect -f '{{.State.ExitCode}}' "$("${COMPOSE[@]}" ps -a -q migrate)")"
# Resource limits and healthchecks of the production bundle as the runtime applied them (ADR-0060)
for svc in app postgres vault proxy; do
  cid=$("${COMPOSE[@]}" ps -q "$svc")
  check "$svc reports healthy"                  healthy "$(docker inspect -f '{{.State.Health.Status}}' "$cid")"
  check "$svc has a memory limit applied"       yes "$([[ "$(docker inspect -f '{{.HostConfig.Memory}}' "$cid")" -gt 0 ]] && echo yes || echo no)"
  check "$svc has a CPU limit applied"          yes "$([[ "$(docker inspect -f '{{.HostConfig.NanoCpus}}' "$cid")" -gt 0 ]] && echo yes || echo no)"
  check "$svc has a pids limit applied"         yes "$([[ "$(docker inspect -f '{{.HostConfig.PidsLimit}}' "$cid")" -gt 0 ]] && echo yes || echo no)"
done
check "app memory limit is the documented 2 GiB" 2147483648 "$(docker inspect -f '{{.HostConfig.Memory}}' "$APP")"

# The operator tools of the production image on the production stack, over TLS with the real secret files (docs/ops/backup-restore.md)
SECRETS="$EX/secrets"
APP_CS=$(cat "$SECRETS/Keel__Database__AppConnectionString")
AUDIT_OUT=$("${COMPOSE[@]}" run --rm --no-deps -T -e "KEEL_AUDIT_CONNECTION=$APP_CS" --entrypoint dotnet app /app/audit/keel-audit.dll verify --public-key /run/secrets/audit_checkpoint_key_pub 2>&1 || true)
check "keel-audit verify runs on the production stack" VALID "$(awk '/RESULT:/{print $2}' <<<"$AUDIT_OUT")"
VAULT_OUT=$("${COMPOSE[@]}" run --rm --no-deps -T -e "Keel__Database__AppConnectionString=$APP_CS" -e Keel__Vault__ServiceUrl=https://vault:8334 -e Keel__Vault__Bucket=keel-vault \
  -e "Keel__Vault__AccessKey=$(cat "$SECRETS/Keel__Vault__AccessKey")" -e "Keel__Vault__SecretKey=$(cat "$SECRETS/Keel__Vault__SecretKey")" -e Keel__Vault__ServerSideEncryption=false \
  --entrypoint dotnet app /app/vault/keel-vault.dll verify 2>&1 || true)
check "keel-vault verify runs on the production stack over TLS" "RESULT: INTACT" "$(grep '^RESULT:' <<<"$VAULT_OUT")"
PGOUT=$("${COMPOSE[@]}" exec -T -e PGPASSWORD=wrong postgres psql -w "host=postgres sslmode=disable user=keel_app dbname=keel" -c 'select 1' 2>&1 || true)
check "postgres rejects non-TLS clients"      yes "$(grep -qi 'no encryption' <<<"$PGOUT" && echo yes || echo no)"

echo "passed: $ok failed: $fail"
[[ $fail -eq 0 ]]
