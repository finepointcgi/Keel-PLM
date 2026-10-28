#!/usr/bin/env bash
# Verifies an offline bundle end to end without a network and without starting Keel:
#   - the bundle verifies, and every kind of tampering is detected before anything would be loaded or run,
#   - install.sh --no-start prepares a compose installation (images loaded, secrets, .env, validated compose config),
#   - install.sh and upgrade.sh refuse unsafe invocations (missing certificates, no backup confirmation, downgrade).
#
#   scripts/verify-offline.sh deploy/offline/out/keel-offline-1.7.0.tar [--keep]
set -euo pipefail
cd "$(dirname "$0")/.."
SRC=${1:?usage: verify-offline.sh <bundle.tar|bundle-dir> [--keep]}
KEEP=false; [[ "${2:-}" == "--keep" ]] && KEEP=true
WORK=$(mktemp -d)
cleanup() { $KEEP && echo "kept $WORK" || rm -rf "$WORK"; }
trap cleanup EXIT

ok=0; fail=0
check() { # name, expected exit status ("pass" or "fail"), command...
  local name=$1 want=$2; shift 2
  local out status=0
  out=$("$@" 2>&1) || status=$?
  if { [[ $want == pass && $status -eq 0 ]] || [[ $want == fail && $status -ne 0 ]]; }; then echo "ok   $name"; ok=$((ok+1))
  else echo "FAIL $name (exit $status, wanted $want)"; echo "$out" | tail -5 | sed 's/^/     /'; fail=$((fail+1)); fi
}

if [[ -d "$SRC" ]]; then B=$SRC; else tar -xf "$SRC" -C "$WORK"; B=$(echo "$WORK"/keel-offline-*); fi
[[ -f "$B/VERSION" ]] || { echo "not a bundle: $B" >&2; exit 2; }
VERSION=$(cat "$B/VERSION")
FP=$(openssl pkey -pubin -in "$B/signing/release-public.pem" -outform DER | openssl dgst -sha256 -r | awk '{print $1}')
echo "bundle $VERSION, signing key fingerprint $FP"

check "bundle verifies"                                  pass "$B/verify.sh"
check "bundle verifies with the pinned fingerprint"      pass "$B/verify.sh" --fingerprint "$FP"
check "wrong fingerprint is refused"                     fail "$B/verify.sh" --fingerprint 0000000000000000000000000000000000000000000000000000000000000000
openssl ecparam -name prime256v1 -genkey -noout -out "$WORK/other.pem"; openssl pkey -in "$WORK/other.pem" -pubout -out "$WORK/other.pub.pem"
check "a different public key is refused"                fail "$B/verify.sh" --pubkey "$WORK/other.pub.pem"

# --- tampering: each case changes one thing, expects verification to fail, then restores it ---
tamper_file() { # relative path
  cp "$B/$1" "$WORK/saved"; printf 'x' >> "$B/$1"
  check "modified $1 is detected"                        fail "$B/verify.sh"
  cp "$WORK/saved" "$B/$1"
}
tamper_file chart/keel-$VERSION.tgz
tamper_file compose/compose.prod-example.yaml
tamper_file install.sh
tamper_file images.env
cp "$B/SHA256SUMS" "$WORK/saved"; sed '1s/^./0/' "$B/SHA256SUMS" > "$WORK/edited" && cp "$WORK/edited" "$B/SHA256SUMS"
check "edited manifest breaks the signature"             fail "$B/verify.sh"
cp "$WORK/saved" "$B/SHA256SUMS"
cp "$B/SHA256SUMS.sig" "$WORK/saved"; openssl dgst -sha256 -sign "$WORK/other.pem" -out "$B/SHA256SUMS.sig" "$B/SHA256SUMS"
check "manifest signed by another key is refused"        fail "$B/verify.sh"
cp "$WORK/saved" "$B/SHA256SUMS.sig"
echo planted > "$B/planted.sh"
check "an extra file is detected"                        fail "$B/verify.sh"
rm "$B/planted.sh"
mv "$B/VERSION" "$WORK/VERSION.saved"
check "a missing file is detected"                       fail "$B/verify.sh"
mv "$WORK/VERSION.saved" "$B/VERSION"
mv "$B/SHA256SUMS.sig" "$WORK/sig.saved"
check "a missing signature is refused"                   fail "$B/verify.sh"
mv "$WORK/sig.saved" "$B/SHA256SUMS.sig"
check "bundle verifies again after restoring"            pass "$B/verify.sh"

# --- install preparation (no network, nothing started) ---
printf 'synthetic-oidc-secret' > "$WORK/oidc.secret"
T="$WORK/install"
check "install refuses without certificates"             fail "$B/install.sh" --compose --target "$WORK/noinstall" --public-host keel.test --oidc-authority https://idp.test/realms/keel --oidc-client-secret-file "$WORK/oidc.secret" --no-start
if [[ "${KEEL_SKIP_LOAD:-0}" == 1 ]]; then echo "skip install --no-start (KEEL_SKIP_LOAD=1)"; else
  check "install --no-start prepares a compose install"  pass "$B/install.sh" --compose --target "$T" --public-host keel.test --oidc-authority https://idp.test/realms/keel --oidc-client-secret-file "$WORK/oidc.secret" --test-pki --no-start
  check "installed compose file validates"               pass docker compose --project-directory "$T/compose" -p keel-offline-verify --env-file "$T/images.env" --env-file "$T/compose/.env" -f "$T/compose/compose.prod-example.yaml" config -q
  check "app image reference in .env is the bundled tag" pass grep -qx "KEEL_APP_IMAGE=keel/api:$VERSION" "$T/compose/.env"
  check "secrets directory is 0700"                      pass test "$(stat -f %Lp "$T/compose/prod-example/secrets" 2>/dev/null || stat -c %a "$T/compose/prod-example/secrets")" = 700
  check "the loaded app image is present in the runtime" pass docker image inspect "keel/api:$VERSION"
fi
check "upgrade refuses without a backup confirmation"    fail "$B/upgrade.sh" --compose --target "$T" --dry-run
echo 9.9.9 > "$T/.keel-version" 2>/dev/null || true
check "upgrade refuses a downgrade"                      fail "$B/upgrade.sh" --compose --target "$T" --backup-done --dry-run
echo 0.0.1 > "$T/.keel-version" 2>/dev/null || true
if [[ -d "$T/compose" ]]; then
  check "upgrade from an older version (dry run)"        pass "$B/upgrade.sh" --compose --target "$T" --backup-done --dry-run
fi

echo "passed: $ok failed: $fail"
[[ $fail -eq 0 ]]
