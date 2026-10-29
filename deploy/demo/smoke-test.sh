#!/bin/bash
# Smoke test of a running Keel PLM demo (ADR-0045): health, the demo sign-in page, an end-to-end sign-in as alice, a part page,
# ITAR hiding for lena, and a document upload that comes back with the same hash.
#
#   deploy/demo/smoke-test.sh http://127.0.0.1:8080
#   AUTH_HEADER="Authorization: Bearer $(gcloud auth print-identity-token)" deploy/demo/smoke-test.sh https://keel-plm-demo-xxxx.a.run.app
#
# AUTH_HEADER is for a Cloud Run service that requires IAM authentication (sent on every request). Needs curl, unzip, shasum or sha256sum.
set -u
BASE="${1:-${BASE_URL:-http://127.0.0.1:8080}}"
BASE="${BASE%/}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
FAILS=0
EXTRA=()
[ -n "${AUTH_HEADER:-}" ] && EXTRA=(-H "$AUTH_HEADER")

ok() { echo "ok   - $1"; }
bad() { echo "FAIL - $1"; FAILS=$((FAILS + 1)); }
check() { if [ "$2" = "$3" ]; then ok "$1"; else bad "$1 (expected '$3', got '$2')"; fi; }
req() { curl -sS --max-time 60 ${EXTRA[@]+"${EXTRA[@]}"} "$@"; }
sha() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

signin() { # user jar
  local token
  token=$(req -c "$2" "$BASE/account/demo-signin" | grep -o 'name="__RequestVerificationToken" value="[^"]*"' | head -1 | sed 's/.*value="//;s/"//')
  req -b "$2" -c "$2" -o /dev/null -w '%{http_code}' --data-urlencode "__RequestVerificationToken=$token" -d "username=$1&returnUrl=/" "$BASE/account/demo-signin"
}

check "health/live answers 200" "$(req -o /dev/null -w '%{http_code}' "$BASE/health/live")" 200
page=$(req "$BASE/account/demo-signin")
echo "$page" | grep -q "DEMO: synthetic data only" && ok "sign-in page shows the demo banner" || bad "sign-in page shows the demo banner"
echo "$page" | grep -q 'value="alice"' && ok "sign-in page lists alice" || bad "sign-in page lists alice"
echo "$page" | grep -q 'erp-service' && bad "service account is not offered" || ok "service account is not offered"
check "anonymous API call is 401" "$(req -o /dev/null -w '%{http_code}' "$BASE/api/v1/me")" 401

A="$TMP/alice.jar"; L="$TMP/lena.jar"
check "sign in as alice redirects" "$(signin alice "$A")" 302
me=$(req -b "$A" "$BASE/api/v1/me")
echo "$me" | grep -q 'd0f8df8c-51d0-5241-8c4f-f39864b95d21' && ok "alice has the dev-realm subject id" || bad "alice has the dev-realm subject id ($me)"
check "parts list page" "$(req -b "$A" -o /dev/null -w '%{http_code}' "$BASE/parts")" 200
check "part page P-000001" "$(req -b "$A" -o /dev/null -w '%{http_code}' "$BASE/parts/P-000001")" 200
check "impact map tab" "$(req -b "$A" -o /dev/null -w '%{http_code}' "$BASE/parts/P-000001?tab=impact")" 200

alice_parts=$(req -b "$A" "$BASE/api/v1/parts?pageSize=200")
alice_itar=$(echo "$alice_parts" | grep -o '"exportJurisdiction":"Itar"' | wc -l | tr -d ' ')
check "lena signs in" "$(signin lena "$L")" 302
lena_parts=$(req -b "$L" "$BASE/api/v1/parts?pageSize=200")
lena_itar=$(echo "$lena_parts" | grep -o '"exportJurisdiction":"Itar"' | wc -l | tr -d ' ')
[ "$alice_itar" -gt 0 ] && ok "alice sees ITAR parts ($alice_itar)" || bad "alice sees ITAR parts"
check "lena sees no ITAR parts" "$lena_itar" 0

# Upload a small file as alice and download it back: the marked download is a zip holding the original bytes.
printf 'keel demo upload %s\n' "$(date +%s)" > "$TMP/note.txt"
want=$(sha "$TMP/note.txt")
created=$(req -b "$A" -F 'metadata={"name":"Smoke note","documentType":"Specification","markings":{"exportJurisdiction":"None","distributionStatement":"A","owningOrg":"ENG"}};type=application/json' -F "file=@$TMP/note.txt;type=text/plain" "$BASE/api/v1/documents")
number=$(echo "$created" | grep -o '"number":"[^"]*"' | head -1 | cut -d'"' -f4)
fileid=$(echo "$created" | grep -o '"files":\[{"id":"[^"]*"' | head -1 | sed 's/.*"id":"//;s/"//')
if [ -n "$number" ] && [ -n "$fileid" ]; then
  ok "uploaded $number"
  req -b "$A" -o "$TMP/dl.zip" "$BASE/downloads/documents/$number/files/$fileid"
  mkdir -p "$TMP/out" && unzip -qo "$TMP/dl.zip" -d "$TMP/out" 2>/dev/null
  got=$(sha "$TMP/out/note.txt" 2>/dev/null || echo none)
  check "downloaded file has the same SHA-256" "$got" "$want"
else
  bad "upload failed: $created"
fi

check "step-up goes to the confirm page" "$(req -b "$A" -o /dev/null -w '%{redirect_url}' "$BASE/account/step-up?returnUrl=/" | sed 's|.*/account/demo-signin.*stepUp=1.*|ok|')" ok

echo
[ "$FAILS" = 0 ] && echo "All checks passed." || echo "$FAILS check(s) failed."
exit $((FAILS > 0))
