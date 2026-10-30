#!/usr/bin/env bash
# Opt-in overlay: X.509 client certificate (CAC/PIV) browser sign-in for the Keel realm (ADR-0058).
# Idempotent: running it again replaces the flow with the current settings. Run it with Keycloak's own kcadm.sh, e.g.
#   docker exec -e KC_CLI_PASSWORD=... keycloak bash /overlay/configure-cac-piv.sh
# (the directory holding this script must be visible inside the container; deploy/compose/compose.cac-piv.yaml mounts it
# at /overlay). The shipped dev realm is not changed by this file: a site that does not run it keeps its current sign-in.
# Operator guide: docs/ops/cac-piv-sign-in.md.
#
# Flow it creates and binds as the realm's browser flow (keel-cac-browser):
#   Cookie                          ALTERNATIVE   an existing SSO session continues the sign-in
#   keel-cac-levels                 ALTERNATIVE
#     keel-cac-level-1              CONDITIONAL   level 1 (acr "1"): the certificate, plus the password form when CAC_FALLBACK=password
#     keel-cac-level-2              CONDITIONAL   level 2 (acr "2"): the certificate ONLY, always re-presented (max age 0)
# Keel asks for level 2 (acr_values=2, prompt=login, max_age=0) for an e-signature and, when Keel:Auth:StepUp:RequiredAcr=2,
# refuses a signature whose token does not carry acr 2. So a password fallback can never satisfy a signature.
#
# Environment:
#   KC_CLI_PASSWORD   (required) password of the Keycloak admin; read from the environment, never from the command line
#   KC_CLI_USER       admin user, default admin
#   KC_URL            Keycloak base URL as seen from where this runs, default http://localhost:8080
#   REALM             realm to configure, default keel
#   KCADM             path to kcadm.sh, default /opt/keycloak/bin/kcadm.sh
#   CAC_FALLBACK      none (default: certificate only) | password (adds the stock username/password form at level 1)
#   CAC_CRL_FILE      CRL file name relative to Keycloak's conf directory, default crl.pem
#   CAC_CRLDP         true | false: also fetch the CRL distribution point named inside the certificate (default false; only
#                     enable when that URL is a site-local one Keycloak can reach)
#   CAC_OCSP_URI      local OCSP responder URL; when set OCSP checking is switched on and fails closed
#   CAC_REGEX         regular expression applied to the subject DN; group 1 is the identifier (default: the trailing 10-digit EDIPI)
#   CAC_ATTRIBUTE     Keycloak user attribute that must equal that identifier (default edipi)
#   CAC_CERT_POLICY   certificate policy OIDs a certificate must carry, comma separated; empty = not checked
#   CAC_SSO_MAX_AGE   seconds a level 1 sign-in satisfies a request for level 1, default 36000
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
KCADM="${KCADM:-/opt/keycloak/bin/kcadm.sh}"
KC_URL="${KC_URL:-http://localhost:8080}"
REALM="${REALM:-keel}"
FALLBACK="${CAC_FALLBACK:-none}"
FLOW="keel-cac-browser"
[ -n "${KC_CLI_PASSWORD:-}" ] || { echo "KC_CLI_PASSWORD is required (the Keycloak admin password)" >&2; exit 2; }
case "$FALLBACK" in none|password) ;; *) echo "CAC_FALLBACK must be none or password" >&2; exit 2;; esac

kc() { "$KCADM" "$@"; }
kc config credentials --server "$KC_URL" --realm master --user "${KC_CLI_USER:-admin}" >/dev/null 2>&1 \
  || { echo "cannot log in to $KC_URL as the Keycloak admin" >&2; exit 1; }
kc get "realms/$REALM" --fields realm >/dev/null 2>&1 || { echo "realm $REALM not found" >&2; exit 1; }

# --- authenticator configuration: the shipped JSON with the site settings applied (plain bash, the image has no awk/jq)
cfg="$(<"$HERE/x509-config.json")"
setkey() { # key value : replace the value of an existing key, or add the key
  local k="$1" v="$2" esc="${2//\\/\\\\}"; esc="${esc//\"/\\\"}"
  if [[ "$cfg" == *"\"$k\":"* ]]; then
    local pre="${cfg%%\"$k\":*}" post="${cfg#*\"$k\":}"; post="${post#*\"}"; post="${post#*\"}"
    cfg="${pre}\"$k\": \"$esc\"${post}"
  else
    cfg="${cfg/\"x509-cert-auth.confirmation-page-disallowed\"/\"$k\": \"$esc\",
    \"x509-cert-auth.confirmation-page-disallowed\"}"
  fi
}
[ -z "${CAC_CRL_FILE:-}" ]     || setkey x509-cert-auth.crl-relative-path "$CAC_CRL_FILE"
[ -z "${CAC_CRLDP:-}" ]        || setkey x509-cert-auth.crldp-checking-enabled "$CAC_CRLDP"
[ -z "${CAC_ATTRIBUTE:-}" ]    || setkey x509-cert-auth.mapper-selection.user-attribute-name "$CAC_ATTRIBUTE"
[ -z "${CAC_REGEX:-}" ]        || setkey x509-cert-auth.regular-expression "$CAC_REGEX"
if [ -n "${CAC_OCSP_URI:-}" ]; then
  setkey x509-cert-auth.ocsp-checking-enabled true
  setkey x509-cert-auth.ocsp-responder-uri "$CAC_OCSP_URI"
fi
if [ -n "${CAC_CERT_POLICY:-}" ]; then
  setkey x509-cert-auth.certificate-policy "$CAC_CERT_POLICY"
  setkey x509-cert-auth.certificate-policy-mode All
fi
tmp="$(mktemp)"; trap 'rm -f "$tmp" "$tmp.cond1" "$tmp.cond2"' EXIT
printf '%s\n' "$cfg" > "$tmp"
printf '{"alias":"keel-cac-level-1","config":{"loa-condition-level":"1","loa-max-age":"%s"}}\n' "${CAC_SSO_MAX_AGE:-36000}" > "$tmp.cond1"
printf '{"alias":"keel-cac-level-2","config":{"loa-condition-level":"2","loa-max-age":"0"}}\n' > "$tmp.cond2"

# --- (re)create the flow. The realm is first bound back to the stock flow so the old one can be deleted.
kc update "realms/$REALM" -s browserFlow=browser >/dev/null
while IFS=, read -r id alias; do [ "$alias" = "$FLOW" ] && kc delete "authentication/flows/$id" -r "$REALM" >/dev/null 2>&1; done \
  < <(kc get authentication/flows -r "$REALM" --fields id,alias --format csv --noquotes)

exec_ids() { # flow alias -> "id,providerId,displayName" lines
  kc get "authentication/flows/$1/executions" -r "$REALM" --fields id,providerId,displayName --format csv --noquotes
}
find_exec() { # flow alias, match (providerId or displayName) -> id of the first match
  local i p d; while IFS=, read -r i p d; do if [ "$p" = "$2" ] || [ "$d" = "$2" ]; then echo "$i"; return; fi; done < <(exec_ids "$1")
}
set_req() { kc update "authentication/flows/$1/executions" -r "$REALM" -b "{\"id\":\"$2\",\"requirement\":\"$3\"}" >/dev/null; }
add_exec() { kc create "authentication/flows/$1/executions/execution" -r "$REALM" -s "provider=$2" >/dev/null 2>&1; }
add_flow() { kc create "authentication/flows/$1/executions/flow" -r "$REALM" -s "alias=$2" -s type=basic-flow -s provider=registration-page-form >/dev/null 2>&1; }

kc create authentication/flows -r "$REALM" -s "alias=$FLOW" -s providerId=basic-flow -s topLevel=true -s builtIn=false \
  -s 'description=Keel: X.509 client certificate (CAC/PIV) sign-in with a certificate-only level 2 for e-signatures (ADR-0058)' >/dev/null 2>&1

add_exec "$FLOW" auth-cookie;           set_req "$FLOW" "$(find_exec "$FLOW" auth-cookie)" ALTERNATIVE
add_flow "$FLOW" keel-cac-levels;       set_req "$FLOW" "$(find_exec "$FLOW" keel-cac-levels)" ALTERNATIVE

level() { # alias, config file, "fallback"?
  add_flow keel-cac-levels "$1"; set_req keel-cac-levels "$(find_exec keel-cac-levels "$1")" CONDITIONAL
  add_exec "$1" conditional-level-of-authentication
  local cond; cond="$(find_exec "$1" conditional-level-of-authentication)"
  set_req "$1" "$cond" REQUIRED
  kc create "authentication/executions/$cond/config" -r "$REALM" -f "$2" >/dev/null 2>&1
}

# level 1: certificate (and the optional password fallback) satisfy an ordinary sign-in
level keel-cac-level-1 "$tmp.cond1"
if [ "$FALLBACK" = password ]; then
  add_flow keel-cac-level-1 keel-cac-level-1-methods; set_req keel-cac-level-1 "$(find_exec keel-cac-level-1 keel-cac-level-1-methods)" REQUIRED
  add_exec keel-cac-level-1-methods auth-x509-client-username-form
  x1="$(find_exec keel-cac-level-1-methods auth-x509-client-username-form)"; set_req keel-cac-level-1-methods "$x1" ALTERNATIVE
  add_exec keel-cac-level-1-methods auth-username-password-form
  set_req keel-cac-level-1-methods "$(find_exec keel-cac-level-1-methods auth-username-password-form)" ALTERNATIVE
else
  add_exec keel-cac-level-1 auth-x509-client-username-form
  x1="$(find_exec keel-cac-level-1 auth-x509-client-username-form)"; set_req keel-cac-level-1 "$x1" REQUIRED
fi
kc create "authentication/executions/$x1/config" -r "$REALM" -f "$tmp" >/dev/null 2>&1

# level 2: the certificate only, whatever level 1 allowed
level keel-cac-level-2 "$tmp.cond2"
add_exec keel-cac-level-2 auth-x509-client-username-form
x2="$(find_exec keel-cac-level-2 auth-x509-client-username-form)"; set_req keel-cac-level-2 "$x2" REQUIRED
sed 's/"keel-x509-cac-piv"/"keel-x509-cac-piv-level-2"/' "$tmp" > "$tmp.l2" && kc create "authentication/executions/$x2/config" -r "$REALM" -f "$tmp.l2" >/dev/null 2>&1
rm -f "$tmp.l2"

kc update "realms/$REALM" -s "browserFlow=$FLOW" >/dev/null
echo "Realm '$REALM' now signs in with the flow $FLOW (password fallback: $FALLBACK; level 2 = certificate only)."
