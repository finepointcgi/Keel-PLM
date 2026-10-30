#!/usr/bin/env bash
# Upgrade rehearsal (ADR-0060, docs/ops/upgrade.md): proves that a database at the PREVIOUS migration set, holding data, survives the forward-only migrations of this release.
#
#  1. the previous migration files are read from git history (the parent of the commit that added the first migration after --through, or --previous-rev) and the
#     production image's own migrator is run over ONLY those files (a read-only bind mount over /app/migrations): a database exactly as the previous release left it;
#  2. parts (and documents when the older schema allows) are created through the real services, a checkpoint is signed; row counts, a content checksum per table
#     (over the columns that existed before) and `keel-audit verify` are recorded;
#  3. the production image's migrator is run with its shipped migrations: it applies every newer file in its own transaction;
#  4. checks: every previously existing table keeps its row count and content checksum, audit chain and checkpoints verify with the same event count, the applied
#     migration hashes equal the shipped files, a second run changes nothing, the application starts and reports ready on the upgraded schema, and the migrator REFUSES
#     a previously applied file that was edited (negative control).
#
#   scripts/ops/upgrade-rehearsal.sh [--report FILE] [--image keel/api:TAG] [--through 0027] [--previous-rev REV] [--parts 40] [--keep]
#
# Needs git history (a full clone), docker, dotnet SDK, openssl, jq, the pinned postgres image and the production image. Take /tmp/keel-int.lock around it.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd); cd "$ROOT"
REPORT="" IMAGE=keel/api:prodready THROUGH=0027 PREV_REV="" PARTS=40
while [[ $# -gt 0 ]]; do
  case $1 in
    --report) REPORT=$2; shift 2 ;; --image) IMAGE=$2; shift 2 ;; --through) THROUGH=$2; shift 2 ;; --previous-rev) PREV_REV=$2; shift 2 ;; --parts) PARTS=$2; shift 2 ;;
    --keep) KEEP=true; shift ;; -h|--help) sed -n '2,19p' "$0"; exit 0 ;; *) die "unknown option $1" ;;
  esac
done
# shellcheck source=lib.sh
source "$ROOT/scripts/ops/lib.sh" upgrade
need docker dotnet openssl jq git
load_pins
docker image inspect "$IMAGE" >/dev/null 2>&1 || die "image $IMAGE not found"
ok=0; fail=0; STEPS=()
check() { if [[ "$2" == "$3" ]]; then echo "ok   $1" >&2; STEPS+=("| $1 | pass | $([[ $(printf '%s\n' "$2" | wc -l) -gt 1 ]] && echo "identical ($(printf '%s\n' "$2" | wc -l | tr -d ' ') lines compared)" || printf '%s' "$2" | cut -c1-70) |"); ok=$((ok+1)); else echo "FAIL $1: expected '$2', got '$3'" >&2; STEPS+=("| $1 | **FAIL** | expected $2, got $3 |"); fail=$((fail+1)); fi; }

log "1. the previous migration set from git history"
if [[ -z "$PREV_REV" ]]; then
  FIRST_NEW=$(ls db/migrations | sort | awk -v t="$THROUGH" -F_ '$1>t {print; exit}')
  [[ -n "$FIRST_NEW" ]] || die "there is no migration newer than $THROUGH: nothing to rehearse"
  ADDED=$(git log --diff-filter=A --format=%H -1 -- "db/migrations/$FIRST_NEW")
  [[ -n "$ADDED" ]] || die "git history does not contain the commit that added $FIRST_NEW (use a full clone, or pass --previous-rev)"
  PREV_REV=$(git rev-parse "$ADDED^")
fi
OLD="$DRILL_TMP/old/migrations"; mkdir -p "$OLD"
git ls-tree -r --name-only "$PREV_REV" -- db/migrations | while read -r f; do git show "$PREV_REV:$f" > "$OLD/$(basename "$f")"; done
OLD_N=$(ls "$OLD" | wc -l | tr -d ' '); NEW_N=$(ls db/migrations | wc -l | tr -d ' ')
LAST_OLD=$(ls "$OLD" | sort | tail -1)
echo "   previous revision $(git rev-parse --short "$PREV_REV"): $OLD_N migration files, newest $LAST_OLD; this release ships $NEW_N" >&2
check "the previous set ends at migration $THROUGH" "$THROUGH" "${LAST_OLD%%_*}"
NEW_FILES=$(for f in db/migrations/*.sql; do n=$(basename "$f"); [[ -e "$OLD/$n" ]] || echo "${n%.sql}"; done | tr '\n' ' ' | sed 's/ $//')
OUT_OF_ORDER=$(for n in $NEW_FILES; do if [[ "$n" < "$LAST_OLD" ]]; then echo "$n"; fi; done | tr '\n' ' ' | sed 's/ $//')
echo "   new files: $NEW_FILES${OUT_OF_ORDER:+ (numbered BELOW the newest old file, applied out of order by name: $OUT_OF_ORDER)}" >&2
CHANGED=0; for f in "$OLD"/*.sql; do cmp -s "$f" "db/migrations/$(basename "$f")" || { CHANGED=$((CHANGED+1)); echo "   edited since: $(basename "$f")" >&2; }; done
check "no previously shipped migration file was edited (forward-only)" 0 "$CHANGED"

dotnet build -c Release -v quiet --nologo tests/Keel.BackupTestTool >/dev/null
TOOL=(dotnet "$ROOT/tests/Keel.BackupTestTool/bin/Release/net10.0/Keel.BackupTestTool.dll")
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$DRILL_TMP/checkpoint.pem" 2>/dev/null
openssl pkey -in "$DRILL_TMP/checkpoint.pem" -pubout -out "$DRILL_TMP/checkpoint.pub.pem"
mkdir -p "$DRILL_TMP/keys"; cp "$DRILL_TMP/checkpoint.pub.pem" "$DRILL_TMP/keys/"; chmod -R a+rX "$DRILL_TMP/keys" "$DRILL_TMP/old"
APP_CS="Host=postgres;Port=5432;Database=keel;Username=keel_app;Password=keel_app_dev"
MIG_CS="Host=postgres;Port=5432;Database=keel;Username=keel_migrator;Password=keel_migrator_dev"
host_cs() { echo "Host=127.0.0.1;Port=$1;Database=keel;Username=$2;Password=$3;Timeout=30"; }
audit_verify() { docker run --rm --network "$DRILL_NET" -e "KEEL_AUDIT_CONNECTION=$APP_CS" -v "$DRILL_TMP/keys:/keys:ro" --entrypoint dotnet "$IMAGE" /app/audit/keel-audit.dll verify --public-key /keys/checkpoint.pub.pem; }
migrate() { # extra docker args (e.g. a migrations mount)
  docker run --rm --network "$DRILL_NET" -e "Keel__Database__MigratorConnectionString=$MIG_CS" -e ASPNETCORE_ENVIRONMENT=Production "$@" "$IMAGE" --migrate
}
psql_k() { docker exec "$DRILL-pg" psql -U postgres -d keel -Atc "$1"; }
# one md5 per ROW of every table, over the columns the table had BEFORE the upgrade (so added columns do not count as changes), written one file per table
rowhashes() { # table-list-file column-list-file output-dir
  mkdir -p "$3"
  while IFS= read -r t; do
    local cols; cols=$(awk -F'|' -v t="$t" '$1==t {print $2}' "$2")
    psql_k "select md5(r::text) from (select $cols from $t) r order by 1" > "$3/$t.txt"
  done < "$1"
}

drill_network
log "2. a database exactly as the previous release left it, with data"
start_postgres "$DRILL-pg" --tmpfs /var/lib/postgresql/data "$PG_IMAGE"
migrate -v "$OLD:/app/migrations:ro" >/dev/null
check "the previous migrator state: $OLD_N migrations applied" "$OLD_N" "$(psql_k 'select count(*) from public.schema_migrations')"
PGP=$(mapped_port "$DRILL-pg" 5432)
POP_NOTE="parts and documents"
if ! "${TOOL[@]}" populate --connection "$(host_cs "$PGP" keel_app keel_app_dev)" --key "$DRILL_TMP/checkpoint.pem" --parts "$PARTS" >"$DRILL_TMP/populate.log" 2>&1; then
  cat "$DRILL_TMP/populate.log" >&2; die "populating the previous-schema database failed (the services of this release do not run on that schema: the rehearsal needs a data seed that does)"
fi
# a second wave through a different service when the older schema allows it: documents need the vault, which the rehearsal does not start, so parts only
POP_NOTE="$PARTS parts through PartService, each with its audit event, and a signed checkpoint (documents are covered by the backup and restore drill)"
psql_k "select schemaname||'.'||tablename from pg_tables where schemaname not in ('pg_catalog','information_schema') order by 1" > "$DRILL_TMP/tables.txt"
"${TOOL[@]}" counts --connection "$(host_cs "$PGP" keel_migrator keel_migrator_dev)" > "$DRILL_TMP/counts-before.txt"
# remember the columns of every table as they are now
while IFS= read -r t; do printf '%s|%s\n' "$t" "$(psql_k "select string_agg(quote_ident(column_name), ',' order by ordinal_position) from information_schema.columns where table_schema||'.'||table_name = '$t'")"; done < "$DRILL_TMP/tables.txt" > "$DRILL_TMP/columns-before.txt"
rowhashes "$DRILL_TMP/tables.txt" "$DRILL_TMP/columns-before.txt" "$DRILL_TMP/rows-before"
audit_verify > "$DRILL_TMP/audit-before.txt" || true
check "before: audit chain verifies" VALID "$(awk '/RESULT:/{print $2}' "$DRILL_TMP/audit-before.txt")"
EV=$(awk '/events checked/{print $3}' "$DRILL_TMP/audit-before.txt"); CK=$(awk '/checkpoints checked/{print $3}' "$DRILL_TMP/audit-before.txt")
TABLES_BEFORE=$(wc -l < "$DRILL_TMP/tables.txt" | tr -d ' ')
ROWS_BEFORE=$(awk -F'|' '{s+=$2} END {print s}' "$DRILL_TMP/counts-before.txt")
echo "   before: $TABLES_BEFORE tables, $ROWS_BEFORE rows, $EV audit events, $CK checkpoints" >&2

log "3. upgrade: the production image's migrator with every shipped migration"
now_s() { perl -MTime::HiRes=time -e 'printf "%.2f", time'; }
T0=$(now_s)
migrate > "$DRILL_TMP/migrate.log" 2>&1 || { cat "$DRILL_TMP/migrate.log" >&2; die "the upgrade failed"; }
T1=$(now_s)
MIG_SECS=$(perl -e "printf '%.1f', $T1 - $T0")
APPLIED=$(psql_k 'select count(*) from public.schema_migrations')
check "all $NEW_N migrations are applied after the upgrade ($((NEW_N-OLD_N)) new)" "$NEW_N" "$APPLIED"
echo "   the forward migrations took $MIG_SECS s (includes the container start)" >&2
TABLES_AFTER=$(psql_k "select count(*) from pg_tables where schemaname not in ('pg_catalog','information_schema')")

log "4. checks"
rowhashes "$DRILL_TMP/tables.txt" "$DRILL_TMP/columns-before.txt" "$DRILL_TMP/rows-after"
LOST=0 CHANGED_TABLES=0 GAINED=""
while IFS= read -r t; do
  # rows present before and not present after = lost or changed (comm needs sorted input, which `order by 1` and LC_ALL=C provide)
  gone=$(LC_ALL=C comm -23 <(LC_ALL=C sort "$DRILL_TMP/rows-before/$t.txt") <(LC_ALL=C sort "$DRILL_TMP/rows-after/$t.txt") | wc -l | tr -d ' ')
  if [[ "$gone" != 0 ]]; then LOST=$((LOST+gone)); CHANGED_TABLES=$((CHANGED_TABLES+1)); echo "   $t: $gone row(s) lost or changed" >&2; fi
  nb=$(wc -l < "$DRILL_TMP/rows-before/$t.txt" | tr -d ' '); na=$(wc -l < "$DRILL_TMP/rows-after/$t.txt" | tr -d ' ')
  [[ "$na" -gt "$nb" ]] && GAINED+="$t +$((na-nb))"$'\n'
done < "$DRILL_TMP/tables.txt"
check "no row of any of the $TABLES_BEFORE existing tables was lost or changed (per-row md5 over the old columns)" 0 "$LOST"
# a table may gain rows only through an INSERT of a newer migration (reference data seeded by a release), or the migration record itself
UNEXPLAINED=""
while IFS= read -r g; do
  [[ -n "$g" ]] || continue
  t=${g%% *}
  if [[ "$t" == public.schema_migrations ]]; then continue; fi
  newer=$(for f in db/migrations/*.sql; do n=$(basename "$f"); [[ -e "$OLD/$n" ]] || cat "$f"; done)   # every file the previous set did not have, whatever its number
  grep -Eiq "INSERT[[:space:]]+INTO[[:space:]]+${t//./\.}\b" <<<"$newer" || UNEXPLAINED+="$g "
done <<<"$GAINED"
check "tables that gained rows were seeded by an INSERT in a newer migration (listed in the report)" "" "$UNEXPLAINED"
audit_verify > "$DRILL_TMP/audit-after.txt" || true
check "after: audit chain verifies" VALID "$(awk '/RESULT:/{print $2}' "$DRILL_TMP/audit-after.txt")"
check "after: same audit event count ($EV)" "$EV" "$(awk '/events checked/{print $3}' "$DRILL_TMP/audit-after.txt")"
check "after: same signed checkpoint count ($CK)" "$CK" "$(awk '/checkpoints checked/{print $3}' "$DRILL_TMP/audit-after.txt")"
psql_k "select version||' '||sha256 from public.schema_migrations order by version" > "$DRILL_TMP/applied.txt"
for f in db/migrations/*.sql; do printf '%s %s\n' "$(basename "${f%.sql}")" "$(openssl dgst -sha256 -r "$f" | awk '{print $1}')"; done | sort > "$DRILL_TMP/shipped.txt"
check "the recorded migration hashes equal the shipped files" "$(cat "$DRILL_TMP/shipped.txt")" "$(cat "$DRILL_TMP/applied.txt")"
migrate >/dev/null
check "a second run of the migrator changes nothing" "$NEW_N" "$(psql_k 'select count(*) from public.schema_migrations')"
cp -R "db/migrations" "$DRILL_TMP/edited"; echo "-- edited after it was applied" >> "$DRILL_TMP/edited/0001_audit.sql"; chmod -R a+rX "$DRILL_TMP/edited"
RC=0; migrate -v "$DRILL_TMP/edited:/app/migrations:ro" >"$DRILL_TMP/edited.log" 2>&1 || RC=$?
check "the migrator refuses a previously applied file that was edited (exit non-zero)" 1 "$([[ $RC -ne 0 ]] && echo 1 || echo 0)"
check "and says which one" 1 "$(grep -c 'Migration 0001_audit was modified after it was applied' "$DRILL_TMP/edited.log")"

log "   the application on the upgraded schema"
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$DRILL_TMP/app-ckpt.pem" 2>/dev/null; chmod 0644 "$DRILL_TMP/app-ckpt.pem"
drill_container "$DRILL-app"
docker run -d --name "$DRILL-app" --network "$DRILL_NET" -p 127.0.0.1::8080 --read-only --cap-drop ALL --user 1001:0 --tmpfs /tmp --tmpfs /var/lib/keel/dataprotection:uid=1001,gid=0,mode=0770 \
  -v "$DRILL_TMP/app-ckpt.pem:/run/keys/checkpoint.pem:ro" -e ASPNETCORE_ENVIRONMENT=Production -e "Keel__Database__AppConnectionString=$APP_CS" \
  -e Keel__Auth__Authority=http://idp.invalid:8080/realms/keel -e Keel__Auth__RequireHttpsMetadata=false -e Keel__Auth__WebClientSecret=unused -e Keel__Security__RequireFips=false \
  -e Keel__Vault__ServiceUrl=http://vault.invalid:8333 -e Keel__Vault__AccessKey=unused -e Keel__Vault__SecretKey=unused-unused -e Keel__Vault__Bucket=keel-vault \
  -e Keel__Vault__Scanning__Mode=Disabled -e Keel__Vault__Scanning__AcknowledgeUnscannedUploads=true -e Keel__Audit__CheckpointPrivateKeyPath=/run/keys/checkpoint.pem "$IMAGE" >/dev/null
AP=$(mapped_port "$DRILL-app" 8080)
wait_until "the application to start" 90 curl -s -o /dev/null "http://127.0.0.1:$AP/health/live"
READY_BODY=$(curl -s "http://127.0.0.1:$AP/health/ready" || true)
echo "   /health/ready: $(echo "$READY_BODY" | head -c 300)" >&2
check "the new application's database check is Healthy on the upgraded schema (it is Unhealthy while shipped migrations are pending)" Healthy "$(echo "$READY_BODY" | jq -r '.checks.database.status')"
echo "   (the vault check in this readiness body is Unhealthy by design: the rehearsal starts no vault)" >&2

{
  echo "# Upgrade rehearsal result"
  echo
  echo "Run \`$DRILL\` on $(date -u +%Y-%m-%dT%H:%M:%SZ), production image \`$IMAGE\` ($(docker image inspect "$IMAGE" --format '{{.Id}}' | cut -c1-19)), PostgreSQL \`$PG_IMAGE\`. Docker VM $(docker info --format '{{.NCPU}}') CPUs, $(( $(docker info --format '{{.MemTotal}}') / 1073741824 )) GiB; host $(uname -sm)."
  echo
  echo "From: $OLD_N migrations (through \`$LAST_OLD\`, read from git revision $(git rev-parse --short "$PREV_REV")). To: $NEW_N migrations shipped by this image ($((NEW_N-OLD_N)) forward migrations: $NEW_FILES). ${OUT_OF_ORDER:+Of these, $OUT_OF_ORDER has a lower number than the newest migration the old database already had (branches merged out of order); the migrator applies by file name, not by position, so it runs after the newer ones, and the check below shows the data survives that too. } Data before the upgrade: $POP_NOTE; $TABLES_BEFORE tables, $ROWS_BEFORE rows, $EV audit events. After: $TABLES_AFTER tables. Tables that gained rows: $(printf '%s' "$GAINED" | tr '\n' ',' | sed 's/,$//; s/,/, /g'). The forward migrations took $MIG_SECS s in total, including the container start; the data volume is small, so this shows the migrations are correct on existing rows, not how long a large database takes (run the same script against a restored copy of the real database for that)."
  echo
  echo "| Check | Result | Detail |"
  echo "|---|---|---|"
  printf '%s\n' "${STEPS[@]}"
  echo
  echo "Passed: $ok, failed: $fail."
} > "$DRILL_TMP/report.md"
cat "$DRILL_TMP/report.md"
if [[ -n "$REPORT" ]]; then mkdir -p "$(dirname "$REPORT")"; cp "$DRILL_TMP/report.md" "$REPORT"; fi
[[ $fail -eq 0 ]]
