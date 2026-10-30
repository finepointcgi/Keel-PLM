#!/usr/bin/env bash
# Maps one CAC/PIV credential to one existing Keycloak user by setting the user attribute the certificate flow matches
# (default: edipi). Run it with Keycloak's kcadm.sh like configure-cac-piv.sh:
#   docker exec -e KC_CLI_PASSWORD=... keycloak bash /overlay/map-credential.sh alice 1000000001
# The identifier is the 10-digit EDIPI that ends the CN of a CAC ("LAST.FIRST.MIDDLE.1234567890"). Keycloak refuses nothing
# here: check that the value is the EDIPI of the right person. Re-running with another value replaces it; "-" clears it.
# Environment: KC_CLI_PASSWORD (required), KC_CLI_USER, KC_URL, REALM, KCADM, CAC_ATTRIBUTE as in configure-cac-piv.sh.
set -euo pipefail
user="${1:-}"; value="${2:-}"
[ -n "$user" ] && [ -n "$value" ] || { echo "usage: $0 <keycloak-username> <edipi|->" >&2; exit 2; }
[ -n "${KC_CLI_PASSWORD:-}" ] || { echo "KC_CLI_PASSWORD is required" >&2; exit 2; }
if [ "$value" != "-" ] && ! [[ "$value" =~ ^[0-9]{10}$ ]]; then echo "the EDIPI must be exactly 10 digits" >&2; exit 2; fi
KCADM="${KCADM:-/opt/keycloak/bin/kcadm.sh}"; REALM="${REALM:-keel}"; ATTR="${CAC_ATTRIBUTE:-edipi}"
"$KCADM" config credentials --server "${KC_URL:-http://localhost:8080}" --realm master --user "${KC_CLI_USER:-admin}" >/dev/null 2>&1
id=""; while IFS= read -r line; do id="$line"; done < <("$KCADM" get users -r "$REALM" -q "username=$user" -q exact=true --fields id --format csv --noquotes)
[ -n "$id" ] || { echo "no user '$user' in realm $REALM" >&2; exit 1; }
# One identifier, one person: refuse a value another user already carries (Keycloak would refuse the certificate for both).
if [ "$value" != "-" ]; then
  while IFS= read -r other; do
    [ -z "$other" ] || [ "$other" = "$user" ] || { echo "the identifier is already assigned to user '$other'" >&2; exit 1; }
  done < <("$KCADM" get users -r "$REALM" -q "q=$ATTR:$value" --fields username --format csv --noquotes)
fi
if [ "$value" = "-" ]; then "$KCADM" update "users/$id" -r "$REALM" -s "attributes.$ATTR=[]" >/dev/null 2>&1
else "$KCADM" update "users/$id" -r "$REALM" -s "attributes.$ATTR=[\"$value\"]" >/dev/null 2>&1; fi
echo "user $user: $ATTR ${value/#-/cleared}"
