#!/usr/bin/env bash
# Installs Keel PLM from this offline bundle. Needs no network: it verifies the bundle, loads the image archives into the local
# container runtime, creates the secrets, runs the database migration job and starts the stack.
#
#   Compose (small sites, one host):
#     ./install.sh --compose --public-host keel.example.mil --oidc-authority https://idp.example.mil/realms/keel \
#                  --oidc-client-secret-file /secure/keel-web.secret [--certs-dir /secure/certs | --test-pki]
#                  [--target /opt/keel] [--pubkey release-public.pem | --fingerprint <hex>] [--no-start]
#   Kubernetes (Helm chart; images go to the cluster registry you name, or are preloaded on the nodes):
#     ./install.sh --k8s --namespace keel --values site-values.yaml [--registry registry.example.mil:5000]
#                  [--secrets-dir /secure/keel-secrets] [--dry-run]
#
# Order of operations (both modes): verify signature and hashes -> preflight checks -> load images -> secrets -> migration job -> start.
# --preflight-only stops after the checks (nothing is loaded or written); --skip-preflight is for sites that ran preflight.sh separately.
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

MODE="" TARGET=/opt/keel PUBLIC_HOST="" AUTHORITY="" SECRET_FILE="" CERTS_DIR="" TEST_PKI=false NO_START=false DRY_RUN=false
NAMESPACE=keel VALUES="" REGISTRY="" SECRETS_DIR="" VERIFY_ARGS=() PREFLIGHT_ONLY=false SKIP_PREFLIGHT=false
while [[ $# -gt 0 ]]; do
  case $1 in
    --compose) MODE=compose; shift ;;
    --k8s) MODE=k8s; shift ;;
    --target) TARGET=$2; shift 2 ;;
    --public-host) PUBLIC_HOST=$2; shift 2 ;;
    --oidc-authority) AUTHORITY=$2; shift 2 ;;
    --oidc-client-secret-file) SECRET_FILE=$2; shift 2 ;;
    --certs-dir) CERTS_DIR=$2; shift 2 ;;
    --test-pki) TEST_PKI=true; shift ;;
    --no-start) NO_START=true; shift ;;
    --preflight-only) PREFLIGHT_ONLY=true; shift ;;
    --skip-preflight) SKIP_PREFLIGHT=true; shift ;;
    --dry-run) DRY_RUN=true; shift ;;
    --namespace) NAMESPACE=$2; shift 2 ;;
    --values) VALUES=$2; shift 2 ;;
    --registry) REGISTRY=$2; shift 2 ;;
    --secrets-dir) SECRETS_DIR=$2; shift 2 ;;
    --pubkey|--fingerprint) VERIFY_ARGS+=("$1" "$2"); shift 2 ;;
    -h|--help) sed -n '2,17p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) die "unknown option $1 (see --help)" ;;
  esac
done
[[ -n "$MODE" ]] || die "choose --compose or --k8s"

VERSION=$(cat "$BUNDLE_DIR/VERSION")
IMAGES_ENV="$BUNDLE_DIR/images.env"
REPO=$(env_value KEEL_APP_REPOSITORY "$IMAGES_ENV")
run() { if $DRY_RUN; then printf '+ %s\n' "$*"; else "$@"; fi; }

log "== 1/5 verifying bundle $VERSION"
verify_bundle "${VERIFY_ARGS[@]+"${VERIFY_ARGS[@]}"}"

if $SKIP_PREFLIGHT; then warn "--skip-preflight: the host, certificates and settings were not checked by this run"
else
  log "== preflight checks"
  if [[ $MODE == compose ]]; then
    pf=(--compose --target "$TARGET" --public-host "$PUBLIC_HOST" --oidc-authority "$AUTHORITY" --oidc-client-secret-file "$SECRET_FILE")
    if [[ -n "$CERTS_DIR" ]]; then pf+=(--certs-dir "$CERTS_DIR"); elif $TEST_PKI; then pf+=(--test-pki); fi
  else pf=(--k8s --namespace "$NAMESPACE" --values "$VALUES"); fi
  "$BUNDLE_DIR/preflight.sh" "${pf[@]}" || die "preflight failed: fix the FAIL lines above and run again (nothing was loaded or changed)"
fi
$PREFLIGHT_ONLY && { log "--preflight-only: stopping here"; exit 0; }

RUNTIME=$(detect_runtime)
log "== 2/5 loading images with $RUNTIME"
if $DRY_RUN; then log "(dry run: images not loaded)"; else load_all_images "$RUNTIME"; fi

install_compose() {
  [[ -n "$PUBLIC_HOST" && -n "$AUTHORITY" ]] || die "--public-host and --oidc-authority are required"
  local ex="$TARGET/compose/prod-example"
  log "== 3/5 preparing $TARGET"
  mkdir -p "$TARGET/compose"
  cp -R "$BUNDLE_DIR/compose/." "$TARGET/compose/"
  cp "$IMAGES_ENV" "$TARGET/images.env"
  echo "$VERSION" > "$TARGET/.keel-version"
  chmod 0700 "$TARGET"

  # certificates: from the site CA, or a throwaway test PKI (never for real data)
  if [[ -n "$CERTS_DIR" ]]; then
    mkdir -p "$ex/certs"; cp "$CERTS_DIR"/* "$ex/certs/"
  elif $TEST_PKI; then
    warn "--test-pki: certificates from a throwaway CA, valid 30 days. Not for real data."
    "$ex/gen-test-pki.sh" "$ex/certs" "$PUBLIC_HOST" >/dev/null
  fi
  local missing=0 f
  for f in ca.crt proxy.crt proxy.key postgres.crt postgres.key vault.crt vault.key; do
    [[ -f "$ex/certs/$f" ]] || { warn "missing certificate file $ex/certs/$f"; missing=1; }
  done
  [[ $missing -eq 0 ]] || die "provide the certificates (--certs-dir) or use --test-pki; see docs/ops/offline-install.md"
  chmod 0444 "$ex"/certs/*; chmod 0700 "$ex/certs"

  # secrets: random database/vault credentials, the OIDC client secret you supply, a fresh audit checkpoint key
  [[ -n "$SECRET_FILE" && -f "$SECRET_FILE" ]] || die "--oidc-client-secret-file is required (the secret issued by the site IdP for client keel-web)"
  KEEL_OIDC_CLIENT_SECRET=$(cat "$SECRET_FILE") "$ex/gen-secrets.sh" "$ex/secrets"

  cat > "$TARGET/compose/.env" <<EOF
KEEL_PUBLIC_HOST=$PUBLIC_HOST
KEEL_HTTPS_PORT=${KEEL_HTTPS_PORT:-443}
KEEL_OIDC_AUTHORITY=$AUTHORITY
KEEL_OIDC_AUDIENCE=${KEEL_OIDC_AUDIENCE:-keel-api}
KEEL_OIDC_CLIENT_ID=${KEEL_OIDC_CLIENT_ID:-keel-web}
KEEL_REQUIRE_FIPS=${KEEL_REQUIRE_FIPS:-true}
KEEL_APP_IMAGE=$REPO:$VERSION
EOF
  chmod 0600 "$TARGET/compose/.env"

  local compose; read -ra compose <<<"${KEEL_COMPOSE:-docker compose}"
  local base=("${compose[@]}" --project-directory "$TARGET/compose" -p keel --env-file "$TARGET/images.env" --env-file "$TARGET/compose/.env" -f "$TARGET/compose/compose.prod-example.yaml")
  log "== 4/5 validating the compose configuration"
  run "${base[@]}" config -q
  if $NO_START; then log "== 5/5 --no-start: configuration written to $TARGET, nothing started"; return; fi
  log "== 5/5 migration job, then the stack"
  run "${base[@]}" up -d --pull never --wait --wait-timeout 300
  log "Keel is starting at https://$PUBLIC_HOST (published port ${KEEL_HTTPS_PORT:-443}). Back up $ex/secrets and the audit checkpoint key now."
}

install_k8s() {
  [[ -n "$VALUES" && -f "$VALUES" ]] || die "--values <site-values.yaml> is required"
  local chart image_repo="$REPO"
  chart=$(ls "$BUNDLE_DIR"/chart/keel-*.tgz | head -1)
  if [[ -n "$REGISTRY" ]]; then
    image_repo="$REGISTRY/$REPO"
    log "== 3/5 pushing images to $REGISTRY"
    run "$RUNTIME" tag "$REPO:$VERSION" "$image_repo:$VERSION"
    run "$RUNTIME" push "$image_repo:$VERSION"
  else
    log "== 3/5 no --registry: the image $REPO:$VERSION must already be present on every node (ctr -n k8s.io images import)"
  fi
  log "== 4/5 secrets in namespace $NAMESPACE"
  run kubectl create namespace "$NAMESPACE" --dry-run=client -o yaml | run kubectl apply -f -
  if [[ -n "$SECRETS_DIR" ]]; then
    kubectl -n "$NAMESPACE" get secret keel-app >/dev/null 2>&1 || run kubectl -n "$NAMESPACE" create secret generic keel-app \
      --from-file=Keel__Database__AppConnectionString="$SECRETS_DIR/Keel__Database__AppConnectionString" \
      --from-file=Keel__Auth__WebClientSecret="$SECRETS_DIR/Keel__Auth__WebClientSecret" \
      --from-file=Keel__Vault__AccessKey="$SECRETS_DIR/Keel__Vault__AccessKey" \
      --from-file=Keel__Vault__SecretKey="$SECRETS_DIR/Keel__Vault__SecretKey" \
      --from-file=audit_checkpoint_key.pem="$SECRETS_DIR/audit_checkpoint_key.pem" \
      --from-file=audit_checkpoint_key.pub.pem="$SECRETS_DIR/audit_checkpoint_key.pub.pem"
    kubectl -n "$NAMESPACE" get secret keel-migrator >/dev/null 2>&1 || run kubectl -n "$NAMESPACE" create secret generic keel-migrator \
      --from-file=Keel__Database__MigratorConnectionString="$SECRETS_DIR/Keel__Database__MigratorConnectionString"
  else
    log "no --secrets-dir: the Secrets named in your values (secrets.app.existingSecret, secrets.migrator.existingSecret) must exist"
  fi
  log "== 5/5 helm upgrade --install (the migration job runs as a pre-install/pre-upgrade hook)"
  run helm upgrade --install keel "$chart" -n "$NAMESPACE" -f "$VALUES" \
    --set image.repository="$image_repo" --set image.tag="$VERSION" --wait --timeout 15m
}

case $MODE in
  compose) install_compose ;;
  k8s) install_k8s ;;
esac
log "done"
