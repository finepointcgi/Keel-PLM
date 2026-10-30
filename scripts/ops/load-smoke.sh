#!/usr/bin/env bash
# Load smoke (ADR-0060, docs/ops/performance.md): starts a throwaway stack (PostgreSQL, SeaweedFS, Keycloak, and the PRODUCTION image with the shipped resource limits),
# loads a synthetic structure, then measures end-to-end HTTP latency with real tokens through the real access filter, for list, search, create-part and BOM expand at several
# concurrencies, with the in-repo client tests/Keel.LoadTool (no third-party load tool; k6 is AGPL). Everything is removed afterwards; nothing existing is touched.
#
#   scripts/ops/load-smoke.sh [--report FILE] [--image keel/api:TAG] [--lines 50000] [--levels 8] [--duration 20] [--concurrency 1,8,32] [--app-cpus 2] [--app-memory 2g] [--pg-cpus 2] [--pg-memory 4g] [--keep]
# Every service gets the limits of the production compose bundle by default (app 2 CPU/2g, PostgreSQL 2 CPU/4g, vault 1 CPU/2g), so the numbers describe the shipped configuration.
#
# Take /tmp/keel-int.lock around it when other container work runs on the machine (until mkdir /tmp/keel-int.lock; do sleep 20; done; ...; rmdir /tmp/keel-int.lock).
# Needs: docker, dotnet SDK, curl, the pinned postgres and vault images and the Keycloak image present locally, and a built production image (scripts/package-offline.sh builds one).
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd); cd "$ROOT"
REPORT="" IMAGE=keel/api:prodready LINES=50000 LEVELS=8 DURATION=20 CONC=1,8,32 APP_CPUS=2 APP_MEM=2g PG_CPUS=2 PG_MEM=4g VAULT_CPUS=1 VAULT_MEM=2g
while [[ $# -gt 0 ]]; do
  case $1 in
    --report) REPORT=$2; shift 2 ;; --image) IMAGE=$2; shift 2 ;; --lines) LINES=$2; shift 2 ;; --levels) LEVELS=$2; shift 2 ;;
    --duration) DURATION=$2; shift 2 ;; --concurrency) CONC=$2; shift 2 ;; --app-cpus) APP_CPUS=$2; shift 2 ;; --app-memory) APP_MEM=$2; shift 2 ;; --pg-cpus) PG_CPUS=$2; shift 2 ;; --pg-memory) PG_MEM=$2; shift 2 ;;
    --keep) KEEP=true; shift ;; -h|--help) sed -n '2,12p' "$0"; exit 0 ;; *) die "unknown option $1" ;;
  esac
done
# shellcheck source=lib.sh
source "$ROOT/scripts/ops/lib.sh" load
need docker dotnet curl openssl
load_pins
docker image inspect "$IMAGE" >/dev/null 2>&1 || die "image $IMAGE not found (build it: scripts/package-offline.sh, or docker build --target runtime)"
docker image inspect "$KC_IMAGE" >/dev/null 2>&1 || die "image $KC_IMAGE not present locally"

log "building the seeder and the load client"
dotnet build -c Release -v quiet --nologo src/Keel.Seed >/dev/null
dotnet build -c Release -v quiet --nologo tests/Keel.LoadTool >/dev/null

drill_network
log "starting PostgreSQL, the vault and Keycloak ($DRILL)"
start_postgres "$DRILL-pg" --cpus "$PG_CPUS" --memory "$PG_MEM" --shm-size 256m --tmpfs /var/lib/postgresql/data "$PG_IMAGE" -c max_connections=300
start_vault "$DRILL-vault" --cpus "$VAULT_CPUS" --memory "$VAULT_MEM" --tmpfs /data
drill_container "$DRILL-idp"
docker run -d --name "$DRILL-idp" --network "$DRILL_NET" --network-alias idp -p 127.0.0.1::8080 \
  -e KC_BOOTSTRAP_ADMIN_USERNAME=admin -e KC_BOOTSTRAP_ADMIN_PASSWORD=drill-throwaway -e KC_HOSTNAME="http://$DRILL-idp:8080" -e KC_HOSTNAME_STRICT=false -e KC_HTTP_ENABLED=true \
  -v "$ROOT/deploy/compose/keycloak/keel-realm.json:/opt/keycloak/data/import/keel-realm.json:ro" \
  "$KC_IMAGE" start-dev --import-realm >/dev/null
PG_PORT=$(mapped_port "$DRILL-pg" 5432); IDP_PORT=$(mapped_port "$DRILL-idp" 8080)
APP_CS="Host=postgres;Port=5432;Database=keel;Username=keel_app;Password=keel_app_dev"
MIG_CS="Host=postgres;Port=5432;Database=keel;Username=keel_migrator;Password=keel_migrator_dev"

APPENV=(-e ASPNETCORE_ENVIRONMENT=Production -e "Keel__Database__AppConnectionString=$APP_CS" -e "Keel__Auth__Authority=http://$DRILL-idp:8080/realms/keel"
  -e Keel__Auth__RequireHttpsMetadata=false -e Keel__Auth__WebClientSecret=keel-web-dev-secret -e Keel__Security__RequireFips=false
  -e Keel__Vault__ServiceUrl=http://vault:8333 -e Keel__Vault__AccessKey=keel_vault_dev -e Keel__Vault__SecretKey=keel_vault_dev_secret -e Keel__Vault__Bucket=keel-vault
  -e Keel__Vault__Scanning__Mode=Disabled -e Keel__Vault__Scanning__AcknowledgeUnscannedUploads=true
  -e Keel__Audit__CheckpointPrivateKeyPath=/run/keys/checkpoint.pem -e Logging__LogLevel__Default=Warning)
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$DRILL_TMP/checkpoint.pem" 2>/dev/null; chmod 0644 "$DRILL_TMP/checkpoint.pem"

log "migrating with the production image"
docker run --rm --network "$DRILL_NET" --name "$DRILL-migrate" -e "Keel__Database__MigratorConnectionString=$MIG_CS" -e ASPNETCORE_ENVIRONMENT=Production "$IMAGE" --migrate >"$DRILL_TMP/migrate.log" 2>&1 \
  || { cat "$DRILL_TMP/migrate.log" >&2; die "migration failed"; }

log "seeding a synthetic structure ($LINES lines, $LEVELS levels) through the application role"
SEED_OUT=$(dotnet "$ROOT/src/Keel.Seed/bin/Release/net10.0/keel-seed.dll" --scenario scale --lines "$LINES" --levels "$LEVELS" --prefix L \
  --connection "Host=127.0.0.1;Port=$PG_PORT;Database=keel;Username=keel_app;Password=keel_app_dev" 2>&1) || { echo "$SEED_OUT" >&2; die "seeding failed"; }
echo "$SEED_OUT" | sed 's/^/   /' >&2
ROOT_PART=$(echo "$SEED_OUT" | sed -n 's/.*root \([^,]*\),.*/\1/p' | head -1)
[[ -n "$ROOT_PART" ]] || die "could not read the BOM root from the seeder output"

log "starting the production image (cpus $APP_CPUS, memory $APP_MEM, read-only root, uid 1001)"
drill_container "$DRILL-app"
docker run -d --name "$DRILL-app" --network "$DRILL_NET" -p 127.0.0.1::8080 --cpus "$APP_CPUS" --memory "$APP_MEM" --pids-limit 512 --read-only --cap-drop ALL \
  --security-opt no-new-privileges:true --user 1001:0 --tmpfs /tmp --tmpfs /var/lib/keel/dataprotection:uid=1001,gid=0,mode=0770 \
  -v "$DRILL_TMP/checkpoint.pem:/run/keys/checkpoint.pem:ro" "${APPENV[@]}" "$IMAGE" >/dev/null
APP_PORT=$(mapped_port "$DRILL-app" 8080)
wait_until "the application to be ready" 120 curl -fs "http://127.0.0.1:$APP_PORT/health/ready"
wait_until "Keycloak to serve the realm" 180 curl -fs "http://127.0.0.1:$IDP_PORT/realms/keel/.well-known/openid-configuration"

log "sampling container resources while the load runs"
( while true; do docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}}' "$DRILL-app" "$DRILL-pg" 2>/dev/null >>"$DRILL_TMP/stats.txt" || true; sleep 2; done ) &
SAMPLER=$!
set +e
dotnet "$ROOT/tests/Keel.LoadTool/bin/Release/net10.0/keel-load.dll" run --api "http://127.0.0.1:$APP_PORT" --idp "http://127.0.0.1:$IDP_PORT/realms/keel" --bom-root "$ROOT_PART" \
  --duration "$DURATION" --concurrency "$CONC" --json "$DRILL_TMP/result.json" --markdown "$DRILL_TMP/result.md"
LOAD_RC=$?
set -e
kill "$SAMPLER" 2>/dev/null || true; wait "$SAMPLER" 2>/dev/null || true

peak() { # container -> "max cpu%, max memory"
  awk -v n="$1" '$1==n { cpu=$2; sub("%","",cpu); if (cpu+0>c) c=cpu+0; mem=$3; u=mem; sub(/[A-Za-z]+$/,"",mem); unit=u; sub(/^[0-9.]+/,"",unit);
      v=mem+0; if (unit=="GiB") v*=1024; else if (unit=="KiB") v/=1024; else if (unit=="B") v/=1048576; if (v>m) m=v }
    END { printf "%.0f%% CPU (of one core), %.0f MiB", c, m }' "$DRILL_TMP/stats.txt"
}
PARTS=$(docker exec "$DRILL-pg" psql -U postgres -d keel -Atc 'select count(*) from plm.part_master')
CREATED=$(docker exec "$DRILL-pg" psql -U postgres -d keel -Atc "select count(*) from plm.part_master where name like 'Load smoke part%'")
AUDIT=$(docker exec "$DRILL-pg" psql -U postgres -d keel -Atc 'select count(*) from audit.event')
DOCKER_CPUS=$(docker info --format '{{.NCPU}}'); DOCKER_MEM=$(( $(docker info --format '{{.MemTotal}}') / 1073741824 ))
{
  echo "# Load smoke result"
  echo
  echo "Run \`$DRILL\` on $(date -u +%Y-%m-%dT%H:%M:%SZ). Image \`$IMAGE\` ($(docker image inspect "$IMAGE" --format '{{.Id}}' | cut -c1-19)), application limits cpus $APP_CPUS, memory $APP_MEM, read-only root, uid 1001; PostgreSQL cpus $PG_CPUS, memory $PG_MEM; vault cpus $VAULT_CPUS, memory $VAULT_MEM (the defaults of the production compose bundle)."
  echo "Docker VM: $DOCKER_CPUS CPUs, ${DOCKER_MEM} GiB memory; host $(uname -sm). PostgreSQL 17 and the vault in tmpfs on the same machine; the load client runs on the host (not in the VM)."
  echo "Data: $PARTS part masters (synthetic structure of $LINES lines over $LEVELS levels, root \`$ROOT_PART\`), $CREATED created by the run, $AUDIT audit events at the end. Duration per phase ${DURATION}s, closed loop."
  echo
  cat "$DRILL_TMP/result.md"
  echo
  echo "bom-expand-1 is the structure tab's first paint (one level); bom-expand-all returns every level of the root and is tens of megabytes, so it runs at 1 and 8 workers only. Latency is the time to the last byte of the body as seen by the client on the host."
  echo
  echo "Peak while loaded: application $(peak "$DRILL-app"); PostgreSQL $(peak "$DRILL-pg") (docker stats every 2 s)."
} > "$DRILL_TMP/report.md"
cat "$DRILL_TMP/report.md"
if [[ -n "$REPORT" ]]; then mkdir -p "$(dirname "$REPORT")"; cp "$DRILL_TMP/report.md" "$REPORT"; cp "$DRILL_TMP/result.json" "${REPORT%.md}.json"; fi
exit $LOAD_RC
