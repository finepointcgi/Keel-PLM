#!/usr/bin/env bash
# Upgrades an existing Keel installation from this offline bundle. Needs no network.
#
#   ./upgrade.sh --compose [--target /opt/keel] [--backup-done] [--pubkey PEM | --fingerprint HEX]
#   ./upgrade.sh --k8s --namespace keel --values site-values.yaml [--registry host:port] [--backup-done] [--dry-run]
#
# Policy (docs/ops/upgrade.md): migrations are forward-only and hash-checked, so the way back from a bad upgrade is a restore.
# The script therefore refuses to run until you state that a verified backup exists (--backup-done), refuses downgrades, and
# runs the migration job before any new application pod or container serves traffic.
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

MODE="" TARGET=/opt/keel BACKUP_DONE=false NAMESPACE=keel VALUES="" REGISTRY="" DRY_RUN=false VERIFY_ARGS=()
while [[ $# -gt 0 ]]; do
  case $1 in
    --compose) MODE=compose; shift ;;
    --k8s) MODE=k8s; shift ;;
    --target) TARGET=$2; shift 2 ;;
    --backup-done) BACKUP_DONE=true; shift ;;
    --namespace) NAMESPACE=$2; shift 2 ;;
    --values) VALUES=$2; shift 2 ;;
    --registry) REGISTRY=$2; shift 2 ;;
    --dry-run) DRY_RUN=true; shift ;;
    --pubkey|--fingerprint) VERIFY_ARGS+=("$1" "$2"); shift 2 ;;
    -h|--help) sed -n '2,10p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) die "unknown option $1 (see --help)" ;;
  esac
done
[[ -n "$MODE" ]] || die "choose --compose or --k8s"
$BACKUP_DONE || die "take and verify a backup first (docs/ops/backup-restore.md), then pass --backup-done. Rollback after a migration is a restore."

VERSION=$(cat "$BUNDLE_DIR/VERSION")
REPO=$(env_value KEEL_APP_REPOSITORY "$BUNDLE_DIR/images.env")
run() { if $DRY_RUN; then printf '+ %s\n' "$*"; else "$@"; fi; }

# Version ordering with sort -V (GNU) or a numeric fallback: refuse equal or older bundles unless KEEL_ALLOW_REINSTALL=1.
version_lt() { [[ "$1" != "$2" && "$(printf '%s\n%s\n' "$1" "$2" | sort -t. -k1,1n -k2,2n -k3,3n | head -1)" == "$1" ]]; }

log "== 1/4 verifying bundle $VERSION"
verify_bundle "${VERIFY_ARGS[@]+"${VERIFY_ARGS[@]}"}"

RUNTIME=$(detect_runtime)
log "== 2/4 loading images with $RUNTIME"
if $DRY_RUN; then log "(dry run: images not loaded)"; else load_all_images "$RUNTIME"; fi

case $MODE in
  compose)
    [[ -f "$TARGET/.keel-version" ]] || die "$TARGET is not a Keel installation (no .keel-version)"
    INSTALLED=$(cat "$TARGET/.keel-version")
    log "installed $INSTALLED, bundle $VERSION"
    if [[ "$INSTALLED" == "$VERSION" && "${KEEL_ALLOW_REINSTALL:-0}" != 1 ]]; then die "version $VERSION is already installed"; fi
    if version_lt "$VERSION" "$INSTALLED"; then die "refusing to downgrade from $INSTALLED to $VERSION: schema migrations are forward-only; restore a backup instead"; fi
    read -ra compose <<<"${KEEL_COMPOSE:-docker compose}"
    base=("${compose[@]}" --project-directory "$TARGET/compose" -p keel --env-file "$TARGET/images.env" --env-file "$TARGET/compose/.env" -f "$TARGET/compose/compose.prod-example.yaml")
    log "== 3/4 updating configuration files (certificates, secrets and .env are kept)"
    for f in compose.prod-example.yaml prod-example/Caddyfile prod-example/nginx.conf prod-example/postgres-entrypoint.sh prod-example/initdb/00-roles.sh prod-example/env.example prod-example/gen-secrets.sh prod-example/gen-test-pki.sh; do
      run cp "$BUNDLE_DIR/compose/$f" "$TARGET/compose/$f"
    done
    run cp "$BUNDLE_DIR/images.env" "$TARGET/images.env"
    run sed -i.bak "s|^KEEL_APP_IMAGE=.*|KEEL_APP_IMAGE=$REPO:$VERSION|" "$TARGET/compose/.env"
    log "== 4/4 migration job first, then the application"
    run "${base[@]}" config -q
    run "${base[@]}" up -d --pull never --no-deps postgres vault
    run "${base[@]}" run --rm --no-deps migrate
    run "${base[@]}" up -d --pull never --wait --wait-timeout 300
    $DRY_RUN || echo "$VERSION" > "$TARGET/.keel-version"
    ;;
  k8s)
    [[ -n "$VALUES" && -f "$VALUES" ]] || die "--values <site-values.yaml> is required"
    chart=$(ls "$BUNDLE_DIR"/chart/keel-*.tgz | head -1)
    image_repo="$REPO"
    if [[ -n "$REGISTRY" ]]; then
      image_repo="$REGISTRY/$REPO"
      log "== 3/4 pushing image to $REGISTRY"
      run "$RUNTIME" tag "$REPO:$VERSION" "$image_repo:$VERSION"
      run "$RUNTIME" push "$image_repo:$VERSION"
    else
      log "== 3/4 no --registry: the image must already be present on every node"
    fi
    log "== 4/4 helm upgrade (the migration job runs as a pre-upgrade hook; a failed migration aborts the rollout)"
    run helm upgrade keel "$chart" -n "$NAMESPACE" -f "$VALUES" --reuse-values \
      --set image.repository="$image_repo" --set image.tag="$VERSION" --wait --timeout 15m
    ;;
esac
log "upgrade to $VERSION finished"
