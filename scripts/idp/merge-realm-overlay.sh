#!/usr/bin/env bash
# Merges an opt-in overlay (deploy/compose/keycloak/overlays/...) into a Keycloak realm file, ready for `--import-realm`.
# Arrays are concatenated (identityProviders, identityProviderMappers, ...), objects are merged, other values are replaced.
# Every __NAME__ placeholder of the overlay must be given with NAME=value; the script refuses to write a realm that still has one,
# and refuses an identity provider alias the base realm already has. The base file is never modified.
#
# Usage: scripts/idp/merge-realm-overlay.sh <base-realm.json> <overlay.json> [NAME=value ...] > merged-realm.json
# Needs: bash, jq. Secrets: pass NAME=@/path/to/file to read the value from a file (kept off the process list).
#   scripts/idp/merge-realm-overlay.sh deploy/compose/keycloak/keel-realm.json \
#     deploy/compose/keycloak/overlays/idp-broker/oidc-broker.overlay.json \
#     DISPLAY_NAME="Contoso" ISSUER=https://login.example.com/ ... CLIENT_SECRET=@/run/secrets/idp-client-secret > keel-realm-sso.json
set -euo pipefail
base="${1:-}"; overlay="${2:-}"; shift 2 2>/dev/null || true
[ -f "$base" ] && [ -f "$overlay" ] || { echo "usage: $0 <base-realm.json> <overlay.json> [NAME=value ...]" >&2; exit 2; }
command -v jq >/dev/null || { echo "jq is required" >&2; exit 2; }

text="$(jq 'del(._comment)' "$overlay")"   # the explanatory _comment is not part of the realm
for pair in "$@"; do
  name="${pair%%=*}"; value="${pair#*=}"
  [[ "$name" =~ ^[A-Z0-9_]+$ ]] || { echo "bad placeholder name '$name'" >&2; exit 2; }
  if [[ "$value" == @* ]]; then value="$(<"${value:1}")"; fi
  # JSON-escape the value, then drop the surrounding quotes
  esc="$(jq -rn --arg v "$value" '$v|tojson' | sed -e 's/^"//' -e 's/"$//')"
  text="${text//__${name}__/$esc}"
done
left="$(grep -o '__[A-Z0-9_]*__' <<<"$text" | sort -u | tr '\n' ' ' || true)"
[ -z "$left" ] || { echo "placeholders without a value: $left" >&2; exit 1; }
echo "$text" | jq empty || { echo "the overlay is not valid JSON after substitution" >&2; exit 1; }

dups="$(jq -nr --slurpfile b "$base" --argjson o "$text" '[($b[0].identityProviders // [])[].alias] as $have | ($o.identityProviders // [])[] | select(.alias as $a | $have | index($a)) | .alias')"
[ -z "$dups" ] || { echo "the base realm already has identity provider(s): $dups" >&2; exit 1; }

jq -n --slurpfile b "$base" --argjson o "$text" '
  def merge($a; $b):
    if ($a|type)=="object" and ($b|type)=="object" then
      reduce ($b|keys_unsorted[]) as $k ($a; .[$k] = (if has($k) then merge(.[$k]; $b[$k]) else $b[$k] end))
    elif ($a|type)=="array" and ($b|type)=="array" then $a + $b
    else $b end;
  merge($b[0]; $o)'
