#!/usr/bin/env bash
# Verifies the digital signature of a Keel release package with standard tools only (bash, grep, tail, head, xxd, openssl 3 or later).
# It does not use Keel. It checks that the CMS signature is valid over every byte of the PDF except the signature itself, and that the
# signer's certificate chain ends in the root certificate you give it. Revocation is not checked.
#
#   scripts/verify-package-signature.sh RPK-000123.pdf keel-root.pem
#
# keel-root.pem is the root certificate of your PKI (the last certificate of the bundle the administrator registered; see docs/ops/package-signing.md).
# Exit codes: 0 verified, 1 not verified (changed file, wrong root, bad signature), 2 usage or missing tool.
set -euo pipefail
pdf="${1:-}"; root="${2:-}"
[[ -f "$pdf" && -f "$root" ]] || { echo "usage: $0 package.pdf root-certificate.pem" >&2; exit 2; }
for tool in grep tail head xxd openssl cat; do command -v "$tool" >/dev/null || { echo "missing tool: $tool" >&2; exit 2; }; done
openssl cms -help 2>&1 | grep -q -- '-verify' || { echo "this openssl has no cms command (use OpenSSL 3 or later, not LibreSSL)" >&2; exit 2; }

work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
range="$(LC_ALL=C grep -a -o '/ByteRange *\[[0-9 ]*\]' "$pdf" | head -1 | tr -dc '0-9 ')"
[[ -n "$range" ]] || { echo "NOT VERIFIED: the file carries no signature" >&2; exit 1; }
read -r r0 r1 r2 r3 <<<"$range"
r0=$((10#$r0)); r1=$((10#$r1)); r2=$((10#$r2)); r3=$((10#$r3)) # the numbers are zero padded: decimal, not octal
size="$(wc -c < "$pdf" | tr -d ' ')"
[[ "$r0" == 0 && $((r2 + r3)) -eq "$size" ]] || { echo "NOT VERIFIED: the signature does not cover the whole file (bytes were added or removed)" >&2; exit 1; }

# The signature value is the hexadecimal string between the two signed ranges: <hex>.
head -c $((r2 - 1)) "$pdf" | tail -c $((r2 - r1 - 2)) | tr -d ' \r\n' | xxd -r -p > "$work/signature.der"
# The signed bytes are the file without that value.
{ head -c "$r1" "$pdf"; tail -c +$((r2 + 1)) "$pdf"; } > "$work/signed.bin"

# -no_check_time: judge the certificates at the signing time that is inside the signature, not today (a package outlives its certificate).
if openssl cms -verify -inform DER -in "$work/signature.der" -content "$work/signed.bin" -binary -CAfile "$root" -purpose any -no_check_time -out /dev/null 2> "$work/err"; then
  echo "VERIFIED: the signature is valid over the whole file and chains to $root"
  openssl cms -cmsout -inform DER -in "$work/signature.der" -print 2>/dev/null | grep -E 'signingTime|commonName|organizationName|UTCTIME' | head -12 || true
else
  echo "NOT VERIFIED: $(tr '\n' ' ' < "$work/err")" >&2
  exit 1
fi
