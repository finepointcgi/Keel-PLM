#!/usr/bin/env bash
# Smoke test against the running dev stack (docs/setup.md): real Keycloak tokens -> real API.
# Uses the dev-only keel-dev-cli client and the synthetic users from deploy/compose/keycloak/keel-realm.json.
set -euo pipefail
API=${KEEL_API:-http://localhost:5080}
IDP=${KEEL_IDP:-http://localhost:18080/realms/keel}

token() {
  curl -sf "$IDP/protocol/openid-connect/token" -d grant_type=password -d client_id=keel-dev-cli \
    -d username="$1" -d password="keel-dev-$1" -d scope=openid | python3 -c 'import sys,json;print(json.load(sys.stdin)["access_token"])'
}
check() { # name expected actual
  if [[ "$2" == "$3" ]]; then echo "ok   $1"; else echo "FAIL $1: expected $2, got $3"; exit 1; fi
}
status() { curl -s -o /dev/null -w '%{http_code}' "$@"; }

ITAR='{"markings":{"exportJurisdiction":"Itar","distributionStatement":"D","owningOrg":"ENG","usmlCategory":"XI(d)","cuiCategories":["EXPT"],"controllingOffice":"ACME","distributionReason":"Critical technology","program":"ORCA","exportAuthorizations":["TAA-0042"]},"action":"Read"}'
evaluate() { curl -sf -H "Authorization: Bearer $1" -H 'Content-Type: application/json' -d "$ITAR" "$API/api/v1/access/evaluate" | python3 -c 'import sys,json;print(json.load(sys.stdin)["permitted"])'; }

check "ready"                         200   "$(status "$API/health/ready")"
check "anonymous /me rejected"        401   "$(status "$API/api/v1/me")"
ALICE=$(token alice); FARID=$(token farid); LENA=$(token lena); AUDREY=$(token audrey); ADA=$(token ada)
check "alice /me"                     200   "$(status -H "Authorization: Bearer $ALICE" "$API/api/v1/me")"
check "alice (US person) reads ITAR"  True  "$(evaluate "$ALICE")"
check "farid (TAA-0042) reads ITAR"   True  "$(evaluate "$FARID")"
check "lena (no authorization) denied" False "$(evaluate "$LENA")"
check "alice cannot read audit log"   403   "$(status -H "Authorization: Bearer $ALICE" "$API/api/v1/audit/events")"
check "ada signs checkpoint"          200   "$(status -X POST -H "Authorization: Bearer $ADA" "$API/api/v1/audit/checkpoints")"
VALID=$(curl -sf -X POST -H "Authorization: Bearer $AUDREY" "$API/api/v1/audit/verify" | python3 -c 'import sys,json;print(json.load(sys.stdin)["valid"])')
check "audit chain verifies"          True  "$VALID"
echo "smoke test passed"
