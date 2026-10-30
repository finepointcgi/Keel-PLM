#!/usr/bin/env bash
# Backup and restore drill (ADR-0060, docs/ops/backup-restore.md). Executed for real against throwaway containers and volumes named keel-drill-backup-*:
#
#  1. source stack: PostgreSQL on a named volume, SeaweedFS vault on a named volume, the production image migrates; the real PartService and DocumentService create
#     parts and documents with files (the vault holds real bytes), a signed audit checkpoint is written; baseline facts are taken (row counts of every table,
#     `keel-audit verify`, `keel-vault verify`);
#  2. backup, in the documented order: logical dump of the database (`pg_dump -Fc`), THEN a cold copy of the vault volume (vault stopped for the copy), the public checkpoint
#     key, and a SHA-256 manifest of every artifact; further changes are made AFTER the backup (they must not appear after the restore);
#  3. disaster: the source containers and volumes are destroyed;
#  4. restore into a NEW server and a NEW vault volume from the artifacts only (manifest verified first);
#  5. checks: row counts of every table equal the baseline, `keel-audit verify` VALID with the same event and checkpoint counts, `keel-vault verify` INTACT with the same
#     object count, every document file reads back with its recorded SHA-256, the post-backup documents are absent;
#  6. negative controls on the restored copy: a tampered audit row, a damaged vault object and a deleted vault object must each be reported.
#
#   scripts/ops/backup-restore-drill.sh [--report FILE] [--image keel/api:TAG] [--parts 40] [--documents 12] [--keep]
#
# Take /tmp/keel-int.lock around it when other container work runs (until mkdir /tmp/keel-int.lock; do sleep 20; done; ...; rmdir /tmp/keel-int.lock).
# Needs: docker, dotnet SDK, openssl, curl, the pinned postgres and vault images local, and the production image (scripts/package-offline.sh builds one).
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd); cd "$ROOT"
REPORT="" IMAGE=keel/api:prodready PARTS=40 DOCS=12
while [[ $# -gt 0 ]]; do
  case $1 in
    --report) REPORT=$2; shift 2 ;; --image) IMAGE=$2; shift 2 ;; --parts) PARTS=$2; shift 2 ;; --documents) DOCS=$2; shift 2 ;;
    --keep) KEEP=true; shift ;; -h|--help) sed -n '2,24p' "$0"; exit 0 ;; *) die "unknown option $1" ;;
  esac
done
# shellcheck source=lib.sh
source "$ROOT/scripts/ops/lib.sh" backup
need docker dotnet openssl curl
load_pins
docker image inspect "$IMAGE" >/dev/null 2>&1 || die "image $IMAGE not found"

ok=0; fail=0; STEPS=()
check() { if [[ "$2" == "$3" ]]; then echo "ok   $1" >&2; STEPS+=("| $1 | pass | $([[ $(printf '%s\n' "$2" | wc -l) -gt 1 ]] && echo "identical ($(printf '%s\n' "$2" | wc -l | tr -d ' ') lines compared)" || printf '%s' "$2" | cut -c1-70) |"); ok=$((ok+1)); else echo "FAIL $1: expected '$2', got '$3'" >&2; STEPS+=("| $1 | **FAIL** | expected $2, got $3 |"); fail=$((fail+1)); fi; }
now() { date +%s; }

log "building the populate tool"
dotnet build -c Release -v quiet --nologo tests/Keel.BackupTestTool >/dev/null
TOOL=(dotnet "$ROOT/tests/Keel.BackupTestTool/bin/Release/net10.0/Keel.BackupTestTool.dll")
BK="$DRILL_TMP/backup"; mkdir -p "$BK"
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$DRILL_TMP/checkpoint.pem" 2>/dev/null
openssl pkey -in "$DRILL_TMP/checkpoint.pem" -pubout -out "$DRILL_TMP/checkpoint.pub.pem"
mkdir -p "$DRILL_TMP/keys"; cp "$DRILL_TMP/checkpoint.pub.pem" "$DRILL_TMP/keys/"; chmod -R a+rX "$DRILL_TMP/keys"

APP_CS="Host=postgres;Port=5432;Database=keel;Username=keel_app;Password=keel_app_dev"
MIG_CS="Host=postgres;Port=5432;Database=keel;Username=keel_migrator;Password=keel_migrator_dev"
VAULT_ENV=(-e Keel__Vault__ServiceUrl=http://vault:8333 -e Keel__Vault__AccessKey=keel_vault_dev -e Keel__Vault__SecretKey=keel_vault_dev_secret -e Keel__Vault__Bucket=keel-vault)
# the tools of the production image, run as containers on the drill network exactly as an operator would
audit_verify() { docker run --rm --network "$DRILL_NET" -e "KEEL_AUDIT_CONNECTION=$APP_CS" -v "$DRILL_TMP/keys:/keys:ro" --entrypoint dotnet "$IMAGE" /app/audit/keel-audit.dll verify --public-key /keys/checkpoint.pub.pem; }
vault_verify() { docker run --rm --network "$DRILL_NET" -e "Keel__Database__AppConnectionString=$APP_CS" "${VAULT_ENV[@]}" --entrypoint dotnet "$IMAGE" /app/vault/keel-vault.dll verify --parallel 4 "$@"; }
host_cs() { echo "Host=127.0.0.1;Port=$1;Database=keel;Username=$2;Password=$3;Timeout=30"; }
tool_vault() { echo --vault-url "http://127.0.0.1:$1"; }
counts() { "${TOOL[@]}" counts --connection "$(host_cs "$1" keel_migrator keel_migrator_dev)"; }

drill_network
PGV=$DRILL-pgdata VV=$DRILL-vaultdata PGV2=$DRILL-pgdata-restored VV2=$DRILL-vaultdata-restored
drill_volume "$PGV"; drill_volume "$VV"; drill_volume "$PGV2"; drill_volume "$VV2"
docker volume create "$PGV" >/dev/null; docker volume create "$VV" >/dev/null

log "1. source stack"
start_postgres "$DRILL-pg" -v "$PGV:/var/lib/postgresql/data" "$PG_IMAGE" -c max_connections=100
start_vault "$DRILL-vault" -v "$VV:/data"
PGP=$(mapped_port "$DRILL-pg" 5432); VP=$(mapped_port "$DRILL-vault" 8333)
docker run --rm --network "$DRILL_NET" -e "Keel__Database__MigratorConnectionString=$MIG_CS" -e ASPNETCORE_ENVIRONMENT=Production "$IMAGE" --migrate >/dev/null
"${TOOL[@]}" populate --connection "$(host_cs "$PGP" keel_app keel_app_dev)" --key "$DRILL_TMP/checkpoint.pem" --parts "$PARTS" --documents "$DOCS" --manifest "$DRILL_TMP/manifest-backup.json" $(tool_vault "$VP") >&2
counts "$PGP" > "$DRILL_TMP/counts-baseline.txt"
audit_verify > "$DRILL_TMP/audit-baseline.txt" || true
vault_verify > "$DRILL_TMP/vault-baseline.txt" || true
check "source: audit chain verifies" VALID "$(awk '/RESULT:/{print $2}' "$DRILL_TMP/audit-baseline.txt")"
check "source: vault is intact" "RESULT: INTACT" "$(grep '^RESULT:' "$DRILL_TMP/vault-baseline.txt")"
EV=$(awk '/events checked/{print $3}' "$DRILL_TMP/audit-baseline.txt"); CK=$(awk '/checkpoints checked/{print $3}' "$DRILL_TMP/audit-baseline.txt")
OBJ=$(sed -n 's/.*naming \([0-9]*\) distinct.*/\1/p' "$DRILL_TMP/vault-baseline.txt"); BYTES=$(sed -n 's/^Verified: [0-9]* object(s), \([0-9]*\) bytes.*/\1/p' "$DRILL_TMP/vault-baseline.txt")
TABLES=$(wc -l < "$DRILL_TMP/counts-baseline.txt" | tr -d ' ')
echo "   baseline: $TABLES tables, $EV audit events, $CK checkpoints, $OBJ vault objects, $BYTES bytes" >&2

log "2. backup (database first, then the vault, then the manifest)"
T0=$(now)
docker exec "$DRILL-pg" pg_dump -U postgres -Fc --no-password keel > "$BK/keel.dump"
T1=$(now)
docker stop "$DRILL-vault" >/dev/null
docker run --rm -v "$VV:/data:ro" -v "$BK:/backup" "$PG_IMAGE" tar -C /data -czf /backup/vault-data.tar.gz .
docker start "$DRILL-vault" >/dev/null
T2=$(now)
cp "$DRILL_TMP/checkpoint.pub.pem" "$BK/"
cp "$DRILL_TMP/manifest-backup.json" "$BK/documents-manifest.json"
( cd "$BK" && for f in keel.dump vault-data.tar.gz checkpoint.pub.pem documents-manifest.json; do printf '%s  %s\n' "$(docker run --rm -v "$BK:/b:ro" "$PG_IMAGE" sha256sum "/b/$f" | awk '{print $1}')" "$f"; done > SHA256SUMS )
DUMP_BYTES=$(wc -c < "$BK/keel.dump" | tr -d ' '); VTAR_BYTES=$(wc -c < "$BK/vault-data.tar.gz" | tr -d ' ')
echo "   dump $DUMP_BYTES bytes in $((T1-T0)) s; vault copy $VTAR_BYTES bytes in $((T2-T1)) s (vault stopped for the copy)" >&2
VP=$(mapped_port "$DRILL-vault" 8333)   # a restarted container gets a new random host port
wait_until "the vault to answer again" 90 bash -c "[[ \$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:$VP/keel-vault) == 403 ]]"

log "   changes after the backup (must not exist after the restore)"
"${TOOL[@]}" populate --connection "$(host_cs "$PGP" keel_app keel_app_dev)" --key "$DRILL_TMP/checkpoint.pem" --parts 5 --documents 2 --content-seed 1000 --manifest "$DRILL_TMP/manifest-after.json" $(tool_vault "$VP") >&2
AFTER_PARTS=$(docker exec "$DRILL-pg" psql -U postgres -d keel -Atc 'select count(*) from plm.part_master')

log "3. disaster: destroy the source"
docker rm -f -v "$DRILL-pg" "$DRILL-vault" >/dev/null
docker volume rm "$PGV" "$VV" >/dev/null

log "4. restore into a new server and a new vault volume, from the artifacts only"
T3=$(now)
( cd "$BK" && while read -r hash name; do actual=$(docker run --rm -v "$BK:/b:ro" "$PG_IMAGE" sha256sum "/b/$name" | awk '{print $1}'); [[ "$actual" == "$hash" ]] || die "backup artifact $name does not match its manifest"; done < SHA256SUMS )
echo "   artifact hashes verified" >&2
docker volume create "$PGV2" >/dev/null; docker volume create "$VV2" >/dev/null
docker run --rm -v "$VV2:/data" -v "$BK:/backup:ro" "$PG_IMAGE" tar -C /data -xzf /backup/vault-data.tar.gz
start_postgres "$DRILL-pg-r" -v "$PGV2:/var/lib/postgresql/data" "$PG_IMAGE" -c max_connections=100   # the installer's init script creates the roles and the empty keel database
start_vault "$DRILL-vault-r" -v "$VV2:/data"
docker exec -i "$DRILL-pg-r" pg_restore -U postgres -d keel --exit-on-error --no-password < "$BK/keel.dump"
T4=$(now)
echo "   restore (new server, new vault) took $((T4-T3)) s including hash checks and container start" >&2
PGP2=$(mapped_port "$DRILL-pg-r" 5432); VP2=$(mapped_port "$DRILL-vault-r" 8333)

log "5. checks on the restored copy"
counts "$PGP2" > "$DRILL_TMP/counts-restored.txt"
check "restored row counts equal the baseline in all $TABLES tables" "$(cat "$DRILL_TMP/counts-baseline.txt")" "$(cat "$DRILL_TMP/counts-restored.txt")"
audit_verify > "$DRILL_TMP/audit-restored.txt" || true
check "restored audit chain verifies" VALID "$(awk '/RESULT:/{print $2}' "$DRILL_TMP/audit-restored.txt")"
check "restored audit event count equals the baseline ($EV)" "$EV" "$(awk '/events checked/{print $3}' "$DRILL_TMP/audit-restored.txt")"
check "restored signed checkpoints verified, same count ($CK)" "$CK" "$(awk '/checkpoints checked/{print $3}' "$DRILL_TMP/audit-restored.txt")"
vault_verify > "$DRILL_TMP/vault-restored.txt" || true
check "restored vault is intact (keel-vault verify)" "RESULT: INTACT" "$(grep '^RESULT:' "$DRILL_TMP/vault-restored.txt")"
check "restored vault holds the same objects and bytes ($OBJ objects, $BYTES bytes)" "$OBJ/$BYTES" "$(sed -n 's/.*naming \([0-9]*\) distinct.*/\1/p' "$DRILL_TMP/vault-restored.txt")/$(sed -n 's/^Verified: [0-9]* object(s), \([0-9]*\) bytes.*/\1/p' "$DRILL_TMP/vault-restored.txt")"
RC=0; "${TOOL[@]}" check-documents --connection "$(host_cs "$PGP2" keel_app keel_app_dev)" --manifest "$DRILL_TMP/manifest-backup.json" $(tool_vault "$VP2") > "$DRILL_TMP/docs-restored.txt" || RC=$?
check "every document file reads back with its recorded SHA-256 ($DOCS documents)" "documents checked: $DOCS, problems: 0" "$(tail -1 "$DRILL_TMP/docs-restored.txt")"
RC=0; "${TOOL[@]}" check-documents --connection "$(host_cs "$PGP2" keel_app keel_app_dev)" --manifest "$DRILL_TMP/manifest-after.json" $(tool_vault "$VP2") > "$DRILL_TMP/docs-after.txt" || RC=$?
check "documents created after the backup are absent from the restore" "documents checked: 2, problems: 2" "$(tail -1 "$DRILL_TMP/docs-after.txt")"
RESTORED_PARTS=$(docker exec "$DRILL-pg-r" psql -U postgres -d keel -Atc 'select count(*) from plm.part_master')
check "parts created after the backup are absent ($AFTER_PARTS before the disaster, $PARTS expected after)" "$PARTS" "$RESTORED_PARTS"
check "the restored schema migration hashes are the shipped ones" "$(ls db/migrations/*.sql | wc -l | tr -d ' ')" "$(docker exec "$DRILL-pg-r" psql -U postgres -d keel -Atc 'select count(*) from public.schema_migrations')"

log "6. negative controls on the restored copy"
FIRST_SHA=$(jq -r '.[0].sha256' "$DRILL_TMP/manifest-backup.json"); SECOND_SHA=$(jq -r '.[1].sha256' "$DRILL_TMP/manifest-backup.json")
"${TOOL[@]}" tamper-object --sha "$FIRST_SHA" $(tool_vault "$VP2") >/dev/null
"${TOOL[@]}" delete-object --sha "$SECOND_SHA" $(tool_vault "$VP2") >/dev/null
RC=0; vault_verify > "$DRILL_TMP/vault-tampered.txt" || RC=$?
check "keel-vault verify exits non-zero when objects are damaged or missing" 1 "$RC"
check "it names the damaged object" 1 "$(grep -c "PROBLEM damaged: $FIRST_SHA" "$DRILL_TMP/vault-tampered.txt")"
check "it names the missing object" 1 "$(grep -c "PROBLEM missing: $SECOND_SHA" "$DRILL_TMP/vault-tampered.txt")"
docker exec "$DRILL-pg-r" psql -U postgres -d keel -Atc "ALTER TABLE audit.event DISABLE TRIGGER USER; UPDATE audit.event SET object_id = 'tampered' WHERE seq = (SELECT min(seq) FROM audit.event WHERE object_id IS NOT NULL); ALTER TABLE audit.event ENABLE TRIGGER USER;" >/dev/null
audit_verify > "$DRILL_TMP/audit-tampered.txt" || true
check "a tampered audit row in the restored database is reported INVALID" INVALID "$(awk '/RESULT:/{print $2}' "$DRILL_TMP/audit-tampered.txt")"

{
  echo "# Backup and restore drill result"
  echo
  echo "Run \`$DRILL\` on $(date -u +%Y-%m-%dT%H:%M:%SZ), production image \`$IMAGE\` ($(docker image inspect "$IMAGE" --format '{{.Id}}' | cut -c1-19)), PostgreSQL \`$PG_IMAGE\`, vault \`$VAULT_IMAGE\`. Docker VM $(docker info --format '{{.NCPU}}') CPUs, $(( $(docker info --format '{{.MemTotal}}') / 1073741824 )) GiB; host $(uname -sm)."
  echo
  echo "Dataset: $PARTS parts and $DOCS documents with files (1 KB to about 3 MB each, random bytes) created through the real services; $TABLES tables, $EV audit events, $CK signed checkpoints, $OBJ vault objects ($BYTES bytes)."
  echo "Backup: \`pg_dump -Fc\` $DUMP_BYTES bytes in $((T1-T0)) s; vault volume copy (vault stopped) $VTAR_BYTES bytes compressed in $((T2-T1)) s. Restore into a new server and vault volume including manifest hash checks: $((T4-T3)) s. Sizes are small; these timings show the procedure works, not how long a site's data takes."
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
