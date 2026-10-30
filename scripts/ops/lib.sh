#!/usr/bin/env bash
# Shared helpers of the operations-proof scripts (ADR-0060): throwaway containers and volumes with unique keel-drill-* names, cleanup that removes only
# what the run created, readiness waits. Sourced, never executed. The scripts never touch an existing container, volume, network or port: every name
# carries the run's own prefix and host ports are assigned by Docker (127.0.0.1::port).
# shellcheck shell=bash

DRILL_ID=${KEEL_DRILL_ID:-$(date +%H%M%S)-$$}
DRILL=keel-drill-$1-$DRILL_ID          # $1 = drill name (load, backup, upgrade)
DRILL_NET=$DRILL-net
DRILL_TMP=$(mktemp -d)
DRILL_CONTAINERS=() DRILL_VOLUMES=()
KEEP=${KEEP:-false}

log() { printf '== %s\n' "$*" >&2; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }
need() { for t in "$@"; do command -v "$t" >/dev/null 2>&1 || die "$t is required"; done; }

drill_container() { DRILL_CONTAINERS+=("$1"); }
drill_volume() { DRILL_VOLUMES+=("$1"); }

drill_cleanup() {
  local rc=$?
  if [[ $rc -ne 0 ]]; then
    for c in ${DRILL_CONTAINERS[@]+"${DRILL_CONTAINERS[@]}"}; do
      if docker inspect "$c" >/dev/null 2>&1; then echo "--- last log lines of $c" >&2; docker logs --tail 15 "$c" >&2 2>&1 || true; fi
    done
  fi
  if [[ "$KEEP" == true ]]; then log "--keep: leaving $DRILL-* containers, volumes and the network in place (remove them yourself)"; return; fi
  for c in ${DRILL_CONTAINERS[@]+"${DRILL_CONTAINERS[@]}"}; do docker rm -f -v "$c" >/dev/null 2>&1 || true; done
  for v in ${DRILL_VOLUMES[@]+"${DRILL_VOLUMES[@]}"}; do
    # only volumes this run created: the name must start with the drill prefix
    [[ "$v" == keel-drill-* ]] && docker volume rm -f "$v" >/dev/null 2>&1 || true
  done
  docker network rm "$DRILL_NET" >/dev/null 2>&1 || true
  rm -rf "$DRILL_TMP"
}
trap drill_cleanup EXIT

drill_network() { docker network create "$DRILL_NET" >/dev/null; }

mapped_port() { docker port "$1" "$2/tcp" | head -1 | awk -F: '{print $NF}'; }

wait_until() { # description, attempts, command...
  local what=$1 n=$2; shift 2
  for _ in $(seq 1 "$n"); do "$@" >/dev/null 2>&1 && return 0; sleep 1; done
  die "timed out waiting for $what"
}

# Pinned images from deploy/images.env, by repo:tag (the digests are verified when they were pinned; the drills use what is already local, never pull)
load_pins() {
  set -a; source "$ROOT/deploy/images.env"; set +a
  PG_IMAGE=${KEEL_POSTGRES_IMAGE%@*}; VAULT_IMAGE=${KEEL_VAULT_IMAGE%@*}
  KC_IMAGE=${KEEL_KEYCLOAK_IMAGE:-quay.io/keycloak/keycloak:26.4}
  for i in "$PG_IMAGE" "$VAULT_IMAGE"; do docker image inspect "$i" >/dev/null 2>&1 || die "image $i not present locally (docker pull it; the drills never pull)"; done
}

start_postgres() { # name, extra docker args... ; data in tmpfs unless a -v is given in the extra args
  local name=$1; shift
  drill_container "$name"
  docker run -d --name "$name" --network "$DRILL_NET" --network-alias postgres -p 127.0.0.1::5432 -e POSTGRES_PASSWORD=drill-throwaway \
    -v "$ROOT/deploy/compose/initdb/00-roles.sql:/docker-entrypoint-initdb.d/00-roles.sql:ro" "$@" >/dev/null
  wait_until "$name to accept connections" 120 docker exec "$name" psql -U postgres -d keel -c 'select 1'
}

start_vault() { # name, volume-or-tmpfs flag(s)...
  local name=$1; shift
  drill_container "$name"
  docker run -d --name "$name" --network "$DRILL_NET" --network-alias vault -p 127.0.0.1::8333 \
    -v "$ROOT/deploy/compose/seaweedfs/s3.json:/etc/seaweedfs/s3.json:ro" "$@" \
    "$VAULT_IMAGE" mini -dir=/data -s3.config=/etc/seaweedfs/s3.json -bucket=keel-vault -admin.ui=false -master.volumeSizeLimitMB=64 -master.telemetry=false >/dev/null
  local port; port=$(mapped_port "$name" 8333)
  # anonymous requests get 403 once the S3 gateway and its identities are loaded
  wait_until "the vault S3 gateway" 90 bash -c "[[ \$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:$port/keel-vault) == 403 ]]"
}
