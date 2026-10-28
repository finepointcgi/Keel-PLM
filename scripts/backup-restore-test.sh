#!/usr/bin/env bash
# Automated backup and restore test (docs/ops/backup-restore.md). Against throwaway PostgreSQL containers whose data lives in
# tmpfs it: migrates a database with the real migrator, populates it with synthetic parts through the real services (each
# with its audit event) and a signed audit checkpoint, takes a physical base backup while WAL archiving is on, makes more
# changes, then restores into a FRESH server twice:
#   1. to the end of the archived WAL   -> row counts equal the source, `keel-audit verify` says VALID
#   2. to a point in time before the later changes (PITR) -> row counts of that moment, `keel-audit verify` says VALID
# Everything it creates is removed afterwards; it never touches existing containers, volumes or ports.
#
#   scripts/backup-restore-test.sh [--keep]
# Needs: docker, dotnet SDK (builds the migrator, the audit CLI and the populate tool), the pinned postgres image locally.
set -euo pipefail
cd "$(dirname "$0")/.."
KEEP=false; [[ "${1:-}" == "--keep" ]] && KEEP=true
# shellcheck source=../deploy/images.env
set -a; source deploy/images.env; set +a
PG_IMAGE=${KEEL_POSTGRES_IMAGE%@*}
docker image inspect "$PG_IMAGE" >/dev/null 2>&1 || { echo "image $PG_IMAGE not present locally (docker pull $KEEL_POSTGRES_IMAGE)" >&2; exit 2; }

RUN=keel-brt-$$
SRC=$RUN-source
RESTORE_A=$RUN-restore-latest
RESTORE_B=$RUN-restore-pitr
TMP=$(mktemp -d)
trap '$KEEP || { docker rm -f "$SRC" "$RESTORE_A" "$RESTORE_B" >/dev/null 2>&1 || true; rm -rf "$TMP"; }' EXIT
ok=0; fail=0
check() { if [[ "$2" == "$3" ]]; then echo "ok   $1"; ok=$((ok+1)); else echo "FAIL $1: expected '$2', got '$3'"; fail=$((fail+1)); fi; }

echo "== building the tools"
dotnet build -c Release -v quiet tests/Keel.BackupTestTool >/dev/null
dotnet build -c Release -v quiet src/Keel.AuditCli >/dev/null
dotnet publish -c Release -v quiet src/Keel.Api -o "$TMP/api" -p:UseAppHost=false >/dev/null
TOOL=(dotnet tests/Keel.BackupTestTool/bin/Release/net10.0/Keel.BackupTestTool.dll)
AUDIT=(dotnet src/Keel.AuditCli/bin/Release/net10.0/keel-audit.dll)
"${AUDIT[@]}" keygen --out "$TMP/keys" >/dev/null

# connection strings use the dev-only role passwords from deploy/compose/initdb/00-roles.sql (throwaway databases)
cs() { echo "Host=127.0.0.1;Port=$1;Database=keel;Username=$2;Password=$3;Timeout=15"; }
port_of() { docker port "$1" 5432/tcp | head -1 | awk -F: '{print $NF}'; }
wait_ready() { for _ in $(seq 1 120); do docker exec "$1" pg_isready -U postgres >/dev/null 2>&1 && return 0; sleep 1; done; echo "$1 did not become ready" >&2; return 1; }
verify() { KEEL_AUDIT_CONNECTION=$(cs "$1" keel_app keel_app_dev) "${AUDIT[@]}" verify --public-key "$TMP/keys/checkpoint-signing.pub.pem"; }

echo "== starting the source server (WAL archiving on, data in tmpfs)"
docker run -d --name "$SRC" -p 127.0.0.1::5432 \
  --tmpfs /var/lib/postgresql/data --tmpfs /backup:mode=1777 --tmpfs /archive:mode=1777 \
  -e POSTGRES_PASSWORD=throwaway \
  -v "$PWD/deploy/compose/initdb/00-roles.sql:/docker-entrypoint-initdb.d/00-roles.sql:ro" \
  "$PG_IMAGE" -c wal_level=replica -c archive_mode=on \
  -c "archive_command=test ! -f /archive/%f && cp %p /archive/%f" -c archive_timeout=30 >/dev/null
wait_ready "$SRC"
# the entrypoint restarts the server once after initialisation: wait until the keel database answers on the mapped port
for _ in $(seq 1 60); do docker exec "$SRC" psql -U postgres -d keel -c 'select 1' >/dev/null 2>&1 && break; sleep 1; done
SRC_PORT=$(port_of "$SRC")

echo "== migrating (the real migrator) and populating (the real services)"
Keel__Database__MigratorConnectionString=$(cs "$SRC_PORT" keel_migrator keel_migrator_dev) dotnet "$TMP/api/Keel.Api.dll" --migrate
"${TOOL[@]}" populate --connection "$(cs "$SRC_PORT" keel_app keel_app_dev)" --key "$TMP/keys/checkpoint-signing.pem" --parts 25
verify "$SRC_PORT" >"$TMP/verify-source-1.txt" && check "source audit chain verifies before the backup" VALID "$(awk '/RESULT:/{print $2}' "$TMP/verify-source-1.txt")"
"${TOOL[@]}" counts --connection "$(cs "$SRC_PORT" keel_migrator keel_migrator_dev)" >"$TMP/counts-at-backup.txt"
PITR_TIME=$(docker exec "$SRC" psql -U postgres -d keel -Atc "select to_char(clock_timestamp() at time zone 'UTC','YYYY-MM-DD HH24:MI:SS.US')||'+00'")
sleep 1

echo "== physical base backup (pg_basebackup, tar format, WAL streamed)"
docker exec -u postgres "$SRC" pg_basebackup -D /backup -Ft -X stream -c fast -U postgres
docker exec "$SRC" ls /backup | tr '\n' ' '; echo

echo "== more changes after the backup, then archive the WAL"
"${TOOL[@]}" populate --connection "$(cs "$SRC_PORT" keel_app keel_app_dev)" --key "$TMP/keys/checkpoint-signing.pem" --parts 5
docker exec "$SRC" psql -U postgres -d keel -Atc "select pg_switch_wal()" >/dev/null
for _ in $(seq 1 60); do [[ $(docker exec "$SRC" sh -c 'ls /archive | wc -l') -ge 2 ]] && break; sleep 1; done
sleep 3   # let the archiver finish the last segment
"${TOOL[@]}" counts --connection "$(cs "$SRC_PORT" keel_migrator keel_migrator_dev)" >"$TMP/counts-final.txt"
verify "$SRC_PORT" >"$TMP/verify-source-2.txt" || true
SRC_EVENTS=$(awk '/events checked/{print $3}' "$TMP/verify-source-2.txt")
echo "source: $(grep -c . "$TMP/counts-final.txt") tables, $SRC_EVENTS audit events, $(grep -c . "$TMP/counts-at-backup.txt") tables at backup time"

restore() { # container-name recovery-settings...
  local name=$1; shift
  docker run -d --name "$name" -p 127.0.0.1::5432 \
    --tmpfs /var/lib/postgresql/data:uid=70,gid=70,mode=0700 --tmpfs /backup:mode=1777 --tmpfs /archive:mode=1777 \
    --entrypoint sleep "$PG_IMAGE" infinity >/dev/null
  # Fresh server, nothing of the source: backup and archived WAL are streamed in, as from a backup store.
  docker exec "$SRC" tar -C /backup -c . | docker exec -i "$name" tar -C /backup -x
  docker exec "$SRC" tar -C /archive -c . | docker exec -i "$name" tar -C /archive -x
  local settings=""
  for s in "$@"; do settings+="$s\n"; done
  docker exec -u postgres "$name" sh -c "
    set -e
    tar -xf /backup/base.tar -C \"\$PGDATA\"
    tar -xf /backup/pg_wal.tar -C \"\$PGDATA/pg_wal\"
    chmod 700 \"\$PGDATA\"
    printf \"restore_command = 'cp /archive/%%f %%p'\\nrecovery_target_action = 'promote'\\n$settings\" >> \"\$PGDATA/postgresql.auto.conf\"
    touch \"\$PGDATA/recovery.signal\"
    pg_ctl -D \"\$PGDATA\" -o '-c listen_addresses=* -c archive_mode=off' -w -t 120 start >/dev/null"
  # recovery promotes automatically; wait until writes are accepted
  for _ in $(seq 1 120); do [[ $(docker exec "$name" psql -U postgres -Atc 'select pg_is_in_recovery()' 2>/dev/null) == f ]] && return 0; sleep 1; done
  echo "$name did not finish recovery" >&2; return 1
}

echo "== restore 1: fresh server, base backup + archived WAL to the end"
restore "$RESTORE_A"
PORT_A=$(port_of "$RESTORE_A")
"${TOOL[@]}" counts --connection "$(cs "$PORT_A" keel_migrator keel_migrator_dev)" >"$TMP/counts-restore-latest.txt"
check "restored row counts equal the source (all tables)" "$(cat "$TMP/counts-final.txt")" "$(cat "$TMP/counts-restore-latest.txt")"
verify "$PORT_A" >"$TMP/verify-restore-1.txt" || true
check "restored audit chain verifies" VALID "$(awk '/RESULT:/{print $2}' "$TMP/verify-restore-1.txt")"
check "restored audit event count equals the source" "$SRC_EVENTS" "$(awk '/events checked/{print $3}' "$TMP/verify-restore-1.txt")"
check "restored checkpoints are verified" "$(awk '/checkpoints checked/{print $3}' "$TMP/verify-source-2.txt")" "$(awk '/checkpoints checked/{print $3}' "$TMP/verify-restore-1.txt")"

echo "== restore 2: fresh server, point in time $PITR_TIME (before the later changes)"
restore "$RESTORE_B" "recovery_target_time = '$PITR_TIME'"
PORT_B=$(port_of "$RESTORE_B")
"${TOOL[@]}" counts --connection "$(cs "$PORT_B" keel_migrator keel_migrator_dev)" >"$TMP/counts-restore-pitr.txt"
check "point-in-time row counts equal the state at that moment" "$(cat "$TMP/counts-at-backup.txt")" "$(cat "$TMP/counts-restore-pitr.txt")"
verify "$PORT_B" >"$TMP/verify-restore-2.txt" || true
check "point-in-time audit chain verifies" VALID "$(awk '/RESULT:/{print $2}' "$TMP/verify-restore-2.txt")"

echo "== negative control: tampering with a restored row must be caught"
docker exec "$RESTORE_A" psql -U postgres -d keel -Atc "ALTER TABLE audit.event DISABLE TRIGGER USER; UPDATE audit.event SET object_id = 'tampered' WHERE seq = (SELECT min(seq) FROM audit.event WHERE object_id IS NOT NULL); ALTER TABLE audit.event ENABLE TRIGGER USER;" >/dev/null
verify "$PORT_A" >"$TMP/verify-tampered.txt" || true
check "tampered restore is reported INVALID" INVALID "$(awk '/RESULT:/{print $2}' "$TMP/verify-tampered.txt")"

echo "passed: $ok failed: $fail"
[[ $fail -eq 0 ]]
