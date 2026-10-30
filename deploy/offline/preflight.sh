#!/usr/bin/env bash
# Preflight checks for an install or an upgrade of Keel PLM (ADR-0060). Read-only: it starts nothing, loads nothing and writes nothing. install.sh and
# upgrade.sh run it first (install.sh --preflight-only runs nothing else); run it by hand to see what a site must fix before the maintenance window.
#
#   ./preflight.sh --compose [--target /opt/keel] [--public-host H] [--certs-dir DIR] [--oidc-authority URL] [--oidc-client-secret-file F]
#                  [--https-port 443] [--require-fips true|false] [--upgrade] [--min-disk-gb N] [--min-memory-gb N]
#   ./preflight.sh --k8s [--namespace keel] --values site-values.yaml [--upgrade]
#
# Output: one line per check, `ok`, `WARN` (proceed, but read it) or `FAIL` (do not proceed). Exit 1 when any check fails.
# Defaults come from the environment the installer uses (KEEL_HTTPS_PORT, KEEL_REQUIRE_FIPS). Needs bash, openssl and the container runtime; curl, nc and
# kubectl are used when present.
set -uo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

MODE="" TARGET=/opt/keel PUBLIC_HOST="" CERTS_DIR="" AUTHORITY="" SECRET_FILE="" HTTPS_PORT=${KEEL_HTTPS_PORT:-443} REQUIRE_FIPS=${KEEL_REQUIRE_FIPS:-true}
UPGRADE=false NAMESPACE=keel VALUES="" MIN_DISK_GB=10 MIN_MEM_GB=6 TEST_PKI=false
while [[ $# -gt 0 ]]; do
  case $1 in
    --compose) MODE=compose; shift ;;
    --k8s) MODE=k8s; shift ;;
    --target) TARGET=$2; shift 2 ;;
    --public-host) PUBLIC_HOST=$2; shift 2 ;;
    --certs-dir) CERTS_DIR=$2; shift 2 ;;
    --test-pki) TEST_PKI=true; shift ;;
    --oidc-authority) AUTHORITY=$2; shift 2 ;;
    --oidc-client-secret-file) SECRET_FILE=$2; shift 2 ;;
    --https-port) HTTPS_PORT=$2; shift 2 ;;
    --require-fips) REQUIRE_FIPS=$2; shift 2 ;;
    --upgrade) UPGRADE=true; shift ;;
    --namespace) NAMESPACE=$2; shift 2 ;;
    --values) VALUES=$2; shift 2 ;;
    --min-disk-gb) MIN_DISK_GB=$2; shift 2 ;;
    --min-memory-gb) MIN_MEM_GB=$2; shift 2 ;;
    -h|--help) sed -n '2,13p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) die "unknown option $1 (see --help)" ;;
  esac
done
[[ -n "$MODE" ]] || die "choose --compose or --k8s"

FAILS=0 WARNS=0 OKS=0
ok()   { printf 'ok    %s\n' "$*"; OKS=$((OKS + 1)); }
warnp() { printf 'WARN  %s\n' "$*"; WARNS=$((WARNS + 1)); }
fail() { printf 'FAIL  %s\n' "$*"; FAILS=$((FAILS + 1)); }

free_gb() { # path -> whole GiB free on its filesystem (nearest existing parent)
  local p=$1; while [[ ! -e "$p" && "$p" != "/" ]]; do p=$(dirname "$p"); done
  df -Pk "$p" | awk 'NR==2 {printf "%d", $4/1048576}'
}
port_in_use() { # port -> 0 when something accepts connections on localhost
  if command -v nc >/dev/null 2>&1; then nc -z 127.0.0.1 "$1" >/dev/null 2>&1
  else (exec 3<>"/dev/tcp/127.0.0.1/$1") >/dev/null 2>&1; fi
}
days_left() { # cert file -> whole days until notAfter (negative when expired)
  local end now; end=$(openssl x509 -in "$1" -noout -enddate 2>/dev/null | sed 's/^notAfter=//') || return 1
  [[ -n "$end" ]] || return 1
  now=$(date +%s)
  local endts
  endts=$(date -d "$end" +%s 2>/dev/null || date -j -f "%b %e %T %Y %Z" "$end" +%s 2>/dev/null) || return 1
  echo $(( (endts - now) / 86400 ))
}

# ----------------------------------------------------------------------------------------------------------------- bundle
check_bundle() {
  log "-- bundle"
  [[ -f "$BUNDLE_DIR/VERSION" ]] && ok "bundle version $(cat "$BUNDLE_DIR/VERSION")" || fail "VERSION missing: this is not an intact bundle"
  if [[ -f "$BUNDLE_DIR/versions.json" ]]; then ok "versions manifest present (versions.json)"; else warnp "versions.json missing: bundle was built by an older packaging script"; fi
  local n; n=$(ls "$BUNDLE_DIR"/images/*.tar 2>/dev/null | wc -l | tr -d ' ')
  [[ "$n" -ge 4 ]] && ok "$n image archives present" || fail "expected at least 4 image archives in images/ (keel-api, postgres, vault, proxy), found $n"
}

# ----------------------------------------------------------------------------------------------------------------- host
check_runtime_compose() {
  log "-- container runtime"
  local rt; rt=$(detect_runtime 2>/dev/null) || { fail "no container runtime (docker, podman, nerdctl or ctr)"; return; }
  ok "runtime: $rt"
  [[ "$rt" == "docker" || "$rt" == "podman" ]] || { warnp "runtime $rt cannot run the compose bundle; it can only load images"; return; }
  if ! "$rt" info >/dev/null 2>&1; then fail "$rt daemon is not reachable by this user (is it running; is this user in the docker group?)"; return; fi
  ok "$rt daemon reachable"
  local cv; cv=$($rt compose version --short 2>/dev/null | sed 's/^v//')
  if [[ -z "$cv" ]]; then fail "'$rt compose' (Compose v2 plugin) not found"
  else
    local major minor; major=${cv%%.*}; minor=$(echo "$cv" | cut -d. -f2)
    if [[ "$major" -gt 2 || ( "$major" -eq 2 && "$minor" -ge 20 ) ]]; then ok "compose $cv (2.20 or later needed for --wait-timeout)"; else fail "compose $cv is too old: 2.20 or later is required"; fi
  fi
  local mem_b mem_gb
  mem_b=$($rt info --format '{{.MemTotal}}' 2>/dev/null || echo 0)
  mem_gb=$(( ${mem_b:-0} / 1073741824 ))
  if [[ "$mem_gb" -ge "$MIN_MEM_GB" ]]; then ok "memory available to containers: ${mem_gb} GiB (the default limits add up to about 8 GiB at full load; see docs/ops/performance.md)"
  elif [[ "$mem_gb" -ge 4 ]]; then warnp "memory available to containers is ${mem_gb} GiB; the default limits (app 2g, postgres 4g, vault 2g) may be hit under load. Lower the KEEL_*_MEMORY settings or add memory"
  else fail "memory available to containers is ${mem_gb} GiB: at least 4 GiB is needed to run PostgreSQL, the vault and the application"; fi
  local root; root=$($rt info --format '{{.DockerRootDir}}' 2>/dev/null || true)
  if [[ -n "$root" && -d "$root" ]]; then
    local g; g=$(free_gb "$root")
    [[ "$g" -ge "$MIN_DISK_GB" ]] && ok "free space under the container storage $root: ${g} GiB" || fail "only ${g} GiB free under $root (need at least $MIN_DISK_GB for images, the database and the vault)"
  else warnp "container storage directory not visible from this shell (remote or VM runtime); free space there was not checked"; fi
}

check_host() {
  log "-- host"
  local g; g=$(free_gb "$TARGET")
  [[ "$g" -ge "$MIN_DISK_GB" ]] && ok "free space for $TARGET: ${g} GiB" || fail "only ${g} GiB free for $TARGET (need at least $MIN_DISK_GB; a site vault needs far more: size it to the data)"
  if $UPGRADE; then
    [[ -f "$TARGET/.keel-version" ]] && ok "existing installation $(cat "$TARGET/.keel-version") at $TARGET" || fail "$TARGET is not a Keel installation (no .keel-version): use install.sh"
  else
    if [[ -f "$TARGET/.keel-version" ]]; then fail "$TARGET already holds Keel $(cat "$TARGET/.keel-version"): use upgrade.sh, or choose another --target"; else ok "$TARGET has no installation yet"; fi
    if port_in_use "$HTTPS_PORT"; then fail "port $HTTPS_PORT is already in use on this host (set KEEL_HTTPS_PORT or stop the other service)"; else ok "port $HTTPS_PORT is free"; fi
  fi
  if [[ "$REQUIRE_FIPS" == "true" ]]; then
    if [[ -r /proc/sys/crypto/fips_enabled ]]; then
      [[ "$(cat /proc/sys/crypto/fips_enabled)" == "1" ]] && ok "host is in FIPS mode (KEEL_REQUIRE_FIPS=true)" || fail "KEEL_REQUIRE_FIPS=true but the host is not in FIPS mode: the application will refuse to start. Enable FIPS mode, or set KEEL_REQUIRE_FIPS=false on a non-accredited test host"
    else fail "KEEL_REQUIRE_FIPS=true but /proc/sys/crypto/fips_enabled is not readable (not a Linux host?). Use a FIPS-mode Linux host, or set KEEL_REQUIRE_FIPS=false for a test"; fi
  else warnp "KEEL_REQUIRE_FIPS=false: the application will not enforce FIPS mode. Acceptable for a test host only"; fi
}

# ----------------------------------------------------------------------------------------------------------------- certificates
check_certs() {
  log "-- certificates and secrets"
  local dir=$CERTS_DIR
  [[ -z "$dir" && -d "$TARGET/compose/prod-example/certs" ]] && dir="$TARGET/compose/prod-example/certs"
  if [[ -z "$dir" ]]; then
    if $TEST_PKI; then warnp "--test-pki: the installer will create throwaway certificates valid for 30 days. Not for real data"; else fail "no certificates: pass --certs-dir (ca.crt proxy.* postgres.* vault.*) or --test-pki"; fi
    return
  fi
  [[ -d "$dir" ]] || { fail "certificate directory $dir does not exist"; return; }
  local f missing=0
  for f in ca.crt proxy.crt proxy.key postgres.crt postgres.key vault.crt vault.key; do [[ -f "$dir/$f" ]] || { fail "missing $dir/$f"; missing=1; }; done
  [[ $missing -eq 0 ]] || return
  local name
  for name in proxy postgres vault; do
    local crt="$dir/$name.crt" key="$dir/$name.key" d
    if ! openssl x509 -in "$crt" -noout >/dev/null 2>&1; then fail "$name.crt is not a readable PEM certificate"; continue; fi
    if openssl x509 -in "$crt" -noout -checkend 0 >/dev/null 2>&1; then
      d=$(days_left "$crt" || echo "")
      if [[ -n "$d" && "$d" -lt 30 ]]; then warnp "$name.crt expires in $d day(s): plan the renewal (certificates are files; replace them and restart the service)"; else ok "$name.crt valid${d:+, $d day(s) left}"; fi
    else fail "$name.crt has EXPIRED"; fi
    local pc kc
    pc=$(openssl x509 -in "$crt" -noout -pubkey 2>/dev/null | openssl dgst -sha256 -r | awk '{print $1}')
    kc=$(openssl pkey -in "$key" -pubout 2>/dev/null | openssl dgst -sha256 -r | awk '{print $1}')
    if [[ -n "$pc" && "$pc" == "$kc" ]]; then ok "$name.key matches $name.crt"; else fail "$name.key does not match $name.crt (or the key is unreadable)"; fi
    if openssl verify -CAfile "$dir/ca.crt" "$crt" >/dev/null 2>&1; then ok "$name.crt chains to ca.crt"; else fail "$name.crt does not verify against ca.crt (wrong CA or an incomplete chain in ca.crt)"; fi
  done
  # names: the proxy answers for the public host; the application verifies the database and the vault by their service names (verify-full)
  if [[ -n "$PUBLIC_HOST" ]]; then
    openssl x509 -in "$dir/proxy.crt" -noout -checkhost "$PUBLIC_HOST" 2>/dev/null | grep -q "does match" && ok "proxy.crt names $PUBLIC_HOST" || fail "proxy.crt has no subject alternative name for $PUBLIC_HOST: browsers and the IdP redirect would fail"
  fi
  openssl x509 -in "$dir/postgres.crt" -noout -checkhost postgres 2>/dev/null | grep -q "does match" && ok "postgres.crt names 'postgres' (the service name the application verifies)" || fail "postgres.crt needs the subject alternative name DNS:postgres (SSL Mode=VerifyFull)"
  openssl x509 -in "$dir/vault.crt" -noout -checkhost vault 2>/dev/null | grep -q "does match" && ok "vault.crt names 'vault'" || fail "vault.crt needs the subject alternative name DNS:vault"
}

check_secrets() {
  log "-- secrets"
  if [[ -n "$SECRET_FILE" ]]; then
    if [[ -s "$SECRET_FILE" ]]; then
      ok "OIDC client secret file present and not empty"
      local mode; mode=$(stat -c '%a' "$SECRET_FILE" 2>/dev/null || stat -f '%Lp' "$SECRET_FILE" 2>/dev/null || echo "")
      [[ -n "$mode" && "${mode: -1}" != "0" ]] && warnp "$SECRET_FILE is readable by other users (mode $mode): chmod 600"
    else fail "OIDC client secret file $SECRET_FILE is missing or empty"; fi
  elif ! $UPGRADE; then fail "--oidc-client-secret-file is required for an install"; fi
}

check_idp() {
  [[ -n "$AUTHORITY" ]] || { $UPGRADE || fail "--oidc-authority is required for an install"; return; }
  log "-- identity provider"
  [[ "$AUTHORITY" == https://* ]] && ok "authority is https: $AUTHORITY" || fail "authority must be an https URL (the application requires HTTPS metadata): $AUTHORITY"
  if command -v curl >/dev/null 2>&1; then
    local ca=""; local dir=${CERTS_DIR:-$TARGET/compose/prod-example/certs}
    [[ -f "$dir/ca.crt" ]] && ca=(--cacert "$dir/ca.crt")
    if curl -fsS --max-time 10 "${ca[@]+"${ca[@]}"}" "${AUTHORITY%/}/.well-known/openid-configuration" -o /dev/null 2>/dev/null; then ok "IdP discovery document reachable from this host"
    else warnp "the IdP discovery document is not reachable from this host (the containers' route may still work; if the application cannot reach it, sign-in fails)"; fi
  else warnp "curl not found: IdP reachability was not checked"; fi
}

# ----------------------------------------------------------------------------------------------------------------- upgrade state
check_running_stack() {
  $UPGRADE || return 0
  log "-- running installation"
  local rt; rt=$(detect_runtime 2>/dev/null) || return
  [[ "$rt" == "docker" || "$rt" == "podman" ]] || return
  local health; health=$($rt inspect --format '{{.State.Health.Status}}' keel-app-1 2>/dev/null || echo "absent")
  case $health in
    healthy) ok "application container is healthy before the upgrade" ;;
    absent) warnp "no container keel-app-1: the stack is down or uses another project name; the upgrade will start it" ;;
    *) warnp "application container health is '$health' before the upgrade: fix that first so a failure afterwards is attributable to the upgrade" ;;
  esac
  local n; n=$($rt exec keel-postgres-1 psql -U postgres -d keel -Atc 'select count(*) from public.schema_migrations' 2>/dev/null || true)
  if [[ -n "$n" ]]; then ok "database reachable; $n migration(s) applied (the job applies the newer ones, forward-only)"; else warnp "could not read public.schema_migrations from keel-postgres-1"; fi
}

# ----------------------------------------------------------------------------------------------------------------- kubernetes
check_k8s() {
  log "-- kubernetes"
  command -v helm >/dev/null 2>&1 && ok "helm $(helm version --short 2>/dev/null)" || fail "helm not found"
  if command -v kubectl >/dev/null 2>&1; then
    if kubectl version --request-timeout=10s >/dev/null 2>&1; then ok "cluster reachable ($(kubectl config current-context 2>/dev/null))"; else fail "kubectl cannot reach the cluster"; fi
    if $UPGRADE; then
      kubectl -n "$NAMESPACE" get deploy -l app.kubernetes.io/name=keel >/dev/null 2>&1 && ok "release found in namespace $NAMESPACE" || warnp "no Keel deployment found in namespace $NAMESPACE"
    fi
  else fail "kubectl not found"; fi
  if [[ -n "$VALUES" && -f "$VALUES" ]]; then
    local chart; chart=$(ls "$BUNDLE_DIR"/chart/keel-*.tgz 2>/dev/null | head -1)
    if [[ -n "$chart" ]] && command -v helm >/dev/null 2>&1; then
      if helm template keel "$chart" -n "$NAMESPACE" -f "$VALUES" >/dev/null 2>"${TMPDIR:-/tmp}/keel-preflight-helm.$$"; then ok "values file renders against the chart and its schema"
      else fail "values file does not render: $(head -3 "${TMPDIR:-/tmp}/keel-preflight-helm.$$" | tr '\n' ' ')"; fi
      rm -f "${TMPDIR:-/tmp}/keel-preflight-helm.$$"
    fi
  else fail "--values <site-values.yaml> is required and must exist"; fi
}

log "Keel preflight ($MODE$($UPGRADE && echo ', upgrade'))"
check_bundle
case $MODE in
  compose) check_runtime_compose; check_host; check_certs; check_secrets; check_idp; check_running_stack ;;
  k8s) check_k8s; check_running_stack ;;
esac
echo
echo "preflight: $OKS ok, $WARNS warning(s), $FAILS failure(s)"
[[ $FAILS -eq 0 ]]
