#!/usr/bin/env bash
# Static verification of the production bundle and its preflight (ADR-0060). Needs docker (compose v2 config only: nothing is started), openssl, jq, python3.
# Part 1 renders the compose production file with throwaway certificates and secrets and asserts what the bundle promises for EVERY long-running service:
# a healthcheck, a memory and CPU limit, a pids limit, non-root, read-only root filesystem (or the one documented exception), dropped capabilities, no-new-privileges,
# image digests (no floating tags), secrets from files, no published port except the proxy's. Part 2 runs preflight.sh against a fake bundle directory and
# proves that each failure it exists to catch is caught and a clean setup passes.
#
#   scripts/verify-packaging.sh
set -euo pipefail
cd "$(dirname "$0")/.."
for t in docker openssl jq python3; do command -v "$t" >/dev/null || { echo "$t is required" >&2; exit 2; }; done
W=$(mktemp -d); trap 'rm -rf "$W"' EXIT
fail=0
ok() { echo "ok   $1"; }
bad() { echo "FAIL $1"; fail=1; }
check() { local what=$1; shift; if "$@" >/dev/null 2>&1; then ok "$what"; else bad "$what"; fi; }

# ------------------------------------------------------------------------------------------------------------- part 1: the compose file
cp -R deploy "$W/deploy"
EX="$W/deploy/compose/prod-example"
"$EX/gen-test-pki.sh" "$EX/certs" keel.test >/dev/null
KEEL_OIDC_CLIENT_SECRET=throwaway "$EX/gen-secrets.sh" "$EX/secrets" >/dev/null
cat > "$W/test.env" <<'ENV'
KEEL_PUBLIC_HOST=keel.test
KEEL_OIDC_AUTHORITY=https://idp.example.test/realms/keel
KEEL_APP_IMAGE=keel/api:verify
ENV
CFG="$W/config.json"
( cd "$W/deploy/compose" && docker compose --project-directory . -p keel-verify-static --env-file ../images.env --env-file "$W/test.env" -f compose.prod-example.yaml config --format json ) > "$CFG" \
  || { echo "compose config failed"; exit 1; }
ok "compose file renders (merge keys, interpolation, secrets files)"

svc() { jq -r ".services[\"$1\"]$2" "$CFG"; }
for s in proxy postgres vault; do
  [[ "$(svc "$s" '.healthcheck.test|length')" != "0" && "$(svc "$s" '.healthcheck')" != "null" ]] && ok "$s has a healthcheck" || bad "$s has a healthcheck"
done
check "app has a healthcheck (the HEALTHCHECK of its image, which compose's service_healthy uses)" test "$(grep -c '^HEALTHCHECK' Dockerfile)" -ge 1
for s in proxy app postgres vault migrate; do
  mem=$(svc "$s" '.deploy.resources.limits.memory'); cpu=$(svc "$s" '.deploy.resources.limits.cpus'); pids=$(svc "$s" '.deploy.resources.limits.pids')
  [[ "$mem" != "null" && "$cpu" != "null" && "$pids" != "null" ]] && ok "$s limits: memory $mem, cpus $cpu, pids $pids" || bad "$s has memory, cpu and pids limits"
done
for s in proxy app migrate; do [[ "$(svc "$s" '.read_only')" == "true" ]] && ok "$s root filesystem is read-only" || bad "$s root filesystem is read-only"; done
[[ "$(svc postgres '.read_only')" == "true" ]] && ok "postgres root filesystem is read-only" || bad "postgres root filesystem is read-only"
[[ "$(svc vault '.read_only')" =~ ^(false|null)$ ]] && ok "vault is the one documented read-write exception (docs/ops/hardening.md)" || bad "vault read_only expectation"
for s in proxy app postgres vault migrate; do
  [[ "$(svc "$s" '.cap_drop|index("ALL")')" != "null" ]] && ok "$s drops all capabilities" || bad "$s drops all capabilities"
  svc "$s" '.security_opt|join(",")' | grep -q 'no-new-privileges' && ok "$s sets no-new-privileges" || bad "$s sets no-new-privileges"
done
[[ "$(svc app '.user')" == "1001:0" && "$(svc migrate '.user')" == "1001:0" && "$(svc proxy '.user')" == "65532:0" ]] && ok "app, migrate and proxy run as non-root users" || bad "non-root users"
[[ "$(jq -r '.services|to_entries[]|select(.value.image|test("@sha256:")|not)|.key' "$CFG" | grep -Ev '^(app|migrate)$' | wc -l | tr -d ' ')" == "0" ]] && ok "every third-party image is pinned by digest" || bad "every third-party image is pinned by digest"
published=$(jq -r '[.services|to_entries[]|select(.value.ports!=null)|.key]|join(",")' "$CFG")
[[ "$published" == "proxy" ]] && ok "only the proxy publishes a port" || bad "only the proxy publishes a port (got: $published)"
[[ "$(jq -r '.networks.backend.internal' "$CFG")" == "true" ]] && ok "the backend network is internal (no route out)" || bad "the backend network is internal"
[[ "$(jq -r '[.services|to_entries[]|.value.environment//{}|to_entries[]|select((.key|test("PASSWORD|SECRET|ACCESS_KEY|SECRET_KEY|ConnectionString")) and (.key|test("_(FILE|DIR)$")|not))]|length' "$CFG")" == "0" ]] && ok "no secret is passed as an environment variable (files only)" || bad "a secret is passed as an environment variable"
[[ "$(jq -r '.services.app.depends_on.vault.condition' "$CFG")" == "service_healthy" && "$(jq -r '.services.app.depends_on.migrate.condition' "$CFG")" == "service_completed_successfully" ]] && ok "start order: vault healthy, migration finished, then the app" || bad "start order"

# ------------------------------------------------------------------------------------------------------------- part 2: preflight
B="$W/bundle"; mkdir -p "$B/images" "$W/inst"
cp deploy/offline/lib.sh deploy/offline/preflight.sh "$B/"; echo 9.9.9 > "$B/VERSION"
for n in keel-api postgres vault proxy; do : > "$B/images/$n.tar"; done
CERTS="$EX/certs"
pf() { "$B/preflight.sh" --compose --target "$W/inst" --public-host keel.test --oidc-authority https://idp.example.test/realms/keel --oidc-client-secret-file "$W/oidc" --require-fips false --https-port "${PORT:-18911}" "$@"; }
echo throwaway > "$W/oidc"; chmod 600 "$W/oidc"
expect() { # description, expected exit (0|1), command...
  local what=$1 want=$2; shift 2
  set +e; "$@" >"$W/pf.out" 2>&1; local rc=$?; set -e
  if [[ $rc -eq $want ]]; then ok "preflight: $what"; else bad "preflight: $what (exit $rc, wanted $want)"; sed 's/^/     /' "$W/pf.out" | grep -E 'FAIL|WARN' | head -5; fi
}
expect "a clean setup passes" 0 pf --certs-dir "$CERTS"
expect "a wrong public host is refused (proxy certificate has no such name)" 1 pf --certs-dir "$CERTS" --public-host other.example
expect "a missing certificate directory is refused" 1 pf --certs-dir "$W/nope"
mkdir -p "$W/c2"; cp "$CERTS"/* "$W/c2/"; cp -f "$CERTS/postgres.key" "$W/c2/vault.key" 2>/dev/null || { rm -f "$W/c2/vault.key"; cp "$CERTS/postgres.key" "$W/c2/vault.key"; }
expect "a key that does not match its certificate is refused" 1 pf --certs-dir "$W/c2"
mkdir -p "$W/c3"; cp "$CERTS"/* "$W/c3/"
openssl req -new -key "$W/c3/proxy.key" -subj "/CN=proxy" -out "$W/c3/p.csr"
printf 'subjectAltName=DNS:keel.test\n' > "$W/c3/p.ext"
rm -f "$W/c3/proxy.crt"
openssl x509 -req -in "$W/c3/p.csr" -CA "$W/c3/ca.crt" -CAkey "$W/c3/ca.key" -CAcreateserial -days 1 -extfile "$W/c3/p.ext" -out "$W/c3/proxy.crt" 2>/dev/null
# an expired certificate: sign with a backdated window using the openssl `ca`-free route (-not_before/-not_after, OpenSSL 3.4 and later) when available
if openssl x509 -help 2>&1 | grep -q -- '-not_after'; then
  rm -f "$W/c3/proxy.crt"
  openssl x509 -req -in "$W/c3/p.csr" -CA "$W/c3/ca.crt" -CAkey "$W/c3/ca.key" -CAcreateserial -extfile "$W/c3/p.ext" -not_before 20200101000000Z -not_after 20200102000000Z -out "$W/c3/proxy.crt" 2>/dev/null
  expect "an expired certificate is refused" 1 pf --certs-dir "$W/c3"
else echo "skip preflight: expired certificate (this openssl has no -not_after)"; fi
mkdir -p "$W/c4"; cp "$CERTS"/* "$W/c4/"; rm -f "$W/c4/ca.crt"
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -keyout /dev/null -subj "/CN=other CA" -days 5 -out "$W/c4/ca.crt" 2>/dev/null
expect "certificates that do not chain to ca.crt are refused" 1 pf --certs-dir "$W/c4"
python3 -m http.server 18911 --bind 127.0.0.1 >/dev/null 2>&1 & SRV=$!
sleep 1
expect "a port already in use is refused" 1 pf --certs-dir "$CERTS"
kill "$SRV" 2>/dev/null || true; wait "$SRV" 2>/dev/null || true
pf_existing() { echo 1.0.0 > "$W/inst/.keel-version"; pf --certs-dir "$CERTS"; }
expect "an install over an existing installation is refused" 1 pf_existing
rm -f "$W/inst/.keel-version"
expect "an upgrade without an installation is refused" 1 "$B/preflight.sh" --compose --upgrade --target "$W/inst" --require-fips false
if [[ -r /proc/sys/crypto/fips_enabled && "$(cat /proc/sys/crypto/fips_enabled)" == "1" ]]; then want=0; else want=1; fi
expect "KEEL_REQUIRE_FIPS=true is judged by the host's real FIPS state" "$want" pf --certs-dir "$CERTS" --require-fips true
expect "a missing OIDC secret file is refused" 1 pf --certs-dir "$CERTS" --oidc-client-secret-file "$W/missing"
: > "$W/empty"
expect "an empty OIDC secret file is refused even with --test-pki" 1 pf --test-pki --oidc-client-secret-file "$W/empty"
rm -f "$B/images/proxy.tar"
expect "an incomplete bundle (a missing image archive) is refused" 1 pf --certs-dir "$CERTS"

echo
[[ $fail -eq 0 ]] && echo "packaging checks passed" || echo "packaging checks FAILED"
exit $fail
