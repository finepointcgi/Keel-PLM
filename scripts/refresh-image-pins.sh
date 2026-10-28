#!/usr/bin/env bash
# Resolves the CURRENT manifest-list digest of every pinned image in deploy/images.env and rewrites the file (connected machine).
# Review the diff, rebuild, rescan; never edit digests by hand. Needs docker buildx.
set -euo pipefail
cd "$(dirname "$0")/.."
FILE=deploy/images.env
TMP=$(mktemp)
while IFS= read -r line; do
  if [[ "$line" =~ ^([A-Z_]+)=([^@]+)@sha256:[0-9a-f]{64}$ ]]; then
    key=${BASH_REMATCH[1]} ref=${BASH_REMATCH[2]}
    digest=$(docker buildx imagetools inspect "$ref" | awk '/^Digest:/{print $2; exit}')
    [[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "could not resolve $ref" >&2; exit 1; }
    echo "$key=$ref@$digest" >> "$TMP"
    echo "$ref -> $digest" >&2
  else
    printf '%s\n' "$line" >> "$TMP"
  fi
done < "$FILE"
mv "$TMP" "$FILE"
