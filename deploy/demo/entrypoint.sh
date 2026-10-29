#!/bin/bash
# Keel PLM demo entrypoint (ADR-0045, docs/ops/cloud-run-demo.md). One container, three processes:
#   PostgreSQL 17 (127.0.0.1:5432)  ->  SeaweedFS S3 vault (127.0.0.1:8333)  ->  the Keel host on $PORT (Cloud Run sets 8080).
# Starts as root (Cloud Run does) only to prepare /data and hand each process to its own non-root user (postgres, app).
# This script stays PID 1 as the supervisor: it forwards SIGTERM to all three and exits if any of them dies, so Cloud Run restarts a broken instance.
# Everything here is DEMO ONLY: random credentials per start, fsync off (the data is disposable by design), no external network use.
set -euo pipefail

START=$(date +%s.%N)
log() { awk -v s="$START" -v n="$(date +%s.%N)" -v m="$*" 'BEGIN { printf "[keel-demo +%5.1fs] %s\n", n - s, m }'; }

PORT="${PORT:-8080}"
DATA="${KEEL_DEMO_DATA:-/data}"
RUN="${KEEL_DEMO_RUN:-/run/keel-demo}"
PGBIN=/usr/lib/postgresql/17/bin
PGPORT=5432
S3PORT=8333
BUCKET=keel-vault

if [ "$(id -u)" != 0 ]; then
  echo "keel-demo must start as root (it drops to postgres and app itself); run the image without --user." >&2
  exit 1
fi
# Defence in depth: the host refuses this anyway, but do not even begin outside the Demo environment.
if [ "${ASPNETCORE_ENVIRONMENT:-}" != "Demo" ] || [ "${Keel__Demo__Enabled:-}" != "true" ]; then
  echo "keel-demo needs ASPNETCORE_ENVIRONMENT=Demo and Keel__Demo__Enabled=true (both are set in the image)." >&2
  exit 1
fi

as_postgres() { setpriv --reuid=postgres --regid=postgres --init-groups "$@"; }
as_app() { setpriv --reuid=app --regid=app --init-groups "$@"; }

rand() { openssl rand -hex 16; }
PG_MIGRATOR_PW="$(rand)"; PG_APP_PW="$(rand)"; S3_ACCESS="$(rand)"; S3_SECRET="$(rand)$(rand)"

mkdir -p "$DATA/pg" "$DATA/vault" "$DATA/dp" "$DATA/drop" "$RUN" /var/run/postgresql
chown postgres:postgres "$DATA/pg" /var/run/postgresql
chown app:app "$DATA/vault" "$DATA/dp" "$DATA/drop"
chmod 0700 "$DATA/pg"
install -d -m 0750 -o root -g app "$RUN"

# Keys: a fresh audit-checkpoint signing key and TDP signing key per start (ECDSA P-256); the app user may read them, nobody else.
openssl ecparam -name prime256v1 -genkey -noout -out "$RUN/checkpoint-signing.pem" 2>/dev/null
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$RUN/tdp-signing.pem" 2>/dev/null
cat > "$RUN/s3.json" <<JSON
{ "identities": [ { "name": "keel-app",
  "credentials": [ { "accessKey": "$S3_ACCESS", "secretKey": "$S3_SECRET" } ],
  "actions": ["Read:$BUCKET", "Write:$BUCKET", "List:$BUCKET", "Tagging:$BUCKET"] } ] }
JSON
chown root:app "$RUN"/*.pem "$RUN/s3.json"; chmod 0640 "$RUN"/*.pem "$RUN/s3.json"

PIDS=()
shutdown() {
  log "stopping"
  trap - TERM INT
  # The host and the vault first; PostgreSQL last with a fast shutdown (SIGINT) so it does not wait for open connections.
  for pid in "${PIDS[@]:1}"; do kill -TERM "$pid" 2>/dev/null || true; done
  sleep 1
  kill -INT "${PIDS[0]}" 2>/dev/null || true
  wait 2>/dev/null || true
  exit 0
}
trap shutdown TERM INT

# ---- PostgreSQL ---------------------------------------------------------------------------------------------------------------------------
if [ ! -f "$DATA/pg/PG_VERSION" ]; then
  if [ -d /opt/keel-demo/pgdata ] && [ -n "$(ls -A /opt/keel-demo/pgdata 2>/dev/null)" ]; then
    log "restoring the pre-seeded database snapshot"
    cp -a /opt/keel-demo/pgdata/. "$DATA/pg/"
    [ -d /opt/keel-demo/vault ] && cp -a /opt/keel-demo/vault/. "$DATA/vault/" && chown -R app:app "$DATA/vault"
    chown -R postgres:postgres "$DATA/pg"
  else
    log "initdb (first start of this instance)"
    as_postgres "$PGBIN/initdb" -D "$DATA/pg" -U postgres --encoding=UTF8 --locale=C.UTF-8 --auth-local=peer --auth-host=scram-sha-256 >/dev/null
  fi
fi
# The data is disposable by design (it is reseeded on every cold start), so durability settings are traded for speed and memory.
as_postgres "$PGBIN/postgres" -D "$DATA/pg" \
  -c listen_addresses=127.0.0.1 -c port=$PGPORT -c unix_socket_directories=/var/run/postgresql \
  -c max_connections=150 -c shared_buffers=128MB -c work_mem=4MB -c maintenance_work_mem=32MB -c effective_cache_size=512MB \
  -c fsync=off -c synchronous_commit=off -c full_page_writes=off -c wal_level=minimal -c max_wal_senders=0 -c max_wal_size=256MB \
  -c log_min_messages=warning -c logging_collector=off -c timezone=UTC &
PIDS+=($!)
for _ in $(seq 1 120); do pg_isready -q -h 127.0.0.1 -p $PGPORT && break; sleep 0.5; done
pg_isready -q -h 127.0.0.1 -p $PGPORT || { echo "PostgreSQL did not become ready" >&2; exit 1; }
log "postgres ready"

psql_admin() { as_postgres psql -X -q -v ON_ERROR_STOP=1 -h /var/run/postgresql -p $PGPORT -U postgres "$@"; }
psql_admin -d postgres <<SQL
DO \$\$ BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'keel_migrator') THEN CREATE ROLE keel_migrator LOGIN; END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'keel_app') THEN CREATE ROLE keel_app LOGIN; END IF;
END \$\$;
ALTER ROLE keel_migrator PASSWORD '$PG_MIGRATOR_PW';
ALTER ROLE keel_app PASSWORD '$PG_APP_PW';
SELECT 'CREATE DATABASE keel OWNER keel_migrator' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'keel')\gexec
REVOKE ALL ON DATABASE keel FROM PUBLIC;
GRANT CONNECT ON DATABASE keel TO keel_app;
SQL
psql_admin -d keel <<SQL
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO keel_migrator;
SQL

# ---- SeaweedFS S3 vault (loopback only) ---------------------------------------------------------------------------------------------------
(cd "$DATA/vault" && GOMEMLIMIT=256MiB exec setpriv --reuid=app --regid=app --init-groups \
  weed mini -dir="$DATA/vault" -ip=127.0.0.1 -ip.bind=127.0.0.1 -s3.config="$RUN/s3.json" -bucket=$BUCKET -admin.ui=false \
  -master.volumeSizeLimitMB=128 -master.telemetry=false >"$RUN/weed.log" 2>&1) &
PIDS+=($!)
for _ in $(seq 1 120); do
  code=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$S3PORT/" || true)
  [ "$code" = 403 ] || [ "$code" = 200 ] && break
  sleep 0.5
done
[ "$code" = 403 ] || [ "$code" = 200 ] || { echo "the S3 vault did not become ready (last status $code)" >&2; tail -n 30 "$RUN/weed.log" >&2; exit 1; }
log "vault ready"

# ---- Keel configuration (only fills what the operator did not set) ---------------------------------------------------------------------------
setdef() { if [ -z "${!1:-}" ]; then export "$1=$2"; fi; }
setdef ASPNETCORE_URLS "http://0.0.0.0:$PORT"
setdef Keel__Database__AppConnectionString "Host=127.0.0.1;Port=$PGPORT;Database=keel;Username=keel_app;Password=$PG_APP_PW;Maximum Pool Size=40"
setdef Keel__Database__MigratorConnectionString "Host=127.0.0.1;Port=$PGPORT;Database=keel;Username=keel_migrator;Password=$PG_MIGRATOR_PW"
setdef Keel__Vault__ServiceUrl "http://127.0.0.1:$S3PORT"
setdef Keel__Vault__AccessKey "$S3_ACCESS"
setdef Keel__Vault__SecretKey "$S3_SECRET"
setdef Keel__Vault__Bucket "$BUCKET"
setdef Keel__Vault__ServerSideEncryption false
# Malware scanning is OFF in the demo and the site-level acknowledgement is set on purpose; the sign-in page says so.
setdef Keel__Vault__Scanning__Mode Disabled
setdef Keel__Vault__Scanning__AcknowledgeUnscannedUploads true
setdef Keel__Security__RequireFips false
setdef Keel__Auth__RequireHttpsMetadata false
setdef Keel__DataProtection__KeyDirectory "$DATA/dp"
setdef Keel__Audit__CheckpointPrivateKeyPath "$RUN/checkpoint-signing.pem"
setdef Keel__Audit__CheckpointInterval "00:05:00"
setdef Keel__Integration__TdpSigningKeyPath "$RUN/tdp-signing.pem"
setdef Keel__Integration__FileDropRoot "$DATA/drop"
# Cloud Run (or any HTTPS front end) terminates TLS in front of this container, which only the front end can reach: trust its forwarding headers.
setdef Keel__Proxy__Enabled true
setdef Keel__Proxy__KnownNetworks__0 "0.0.0.0/0"
setdef Keel__Proxy__KnownNetworks__1 "::/0"
export DOTNET_gcServer=0 HOME=/tmp/keel-home
mkdir -p "$HOME" && chown app:app "$HOME"

cd /app
log "migrating"
as_app dotnet /app/Keel.Api.dll --migrate

SEEDED=$(psql_admin -d keel -tAc "SELECT EXISTS (SELECT 1 FROM plm.part_master)")
if [ "$SEEDED" = "f" ]; then
  log "seeding the ORCA demo data"
  as_app dotnet /app/seed/keel-seed.dll --scenario demo >"$RUN/seed.log" 2>&1 || { cat "$RUN/seed.log" >&2; echo "seeding failed" >&2; exit 1; }
  grep -E "ORCA demo data created|Audit chain" "$RUN/seed.log" || true
  log "seeded"
else
  log "database already holds data from an earlier start of this instance; not reseeding"
fi

log "starting the Keel host on port $PORT"
as_app dotnet /app/Keel.Api.dll &
APP_PID=$!
PIDS+=($APP_PID)
( for _ in $(seq 1 240); do
    if curl -fsS -o /dev/null "http://127.0.0.1:$PORT/health/live" 2>/dev/null; then log "host answering /health/live (cold start complete)"; exit 0; fi
    sleep 0.5
  done ) &

# Supervise: any of the three ending ends the container.
wait -n "${PIDS[@]}" || true
log "a process exited; stopping the instance"
shutdown
