#!/usr/bin/env bash
# Shared helpers for the offline bundle scripts (verify.sh, install.sh, upgrade.sh). Needs only bash, openssl, tar, awk and a
# container runtime. Nothing here touches the network.
# shellcheck shell=bash

BUNDLE_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

log() { printf '%s\n' "$*"; }
warn() { printf 'warning: %s\n' "$*" >&2; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then shasum -a 256 "$1" | awk '{print $1}'
  else openssl dgst -sha256 -r "$1" | awk '{print $1}'; fi
}

# Fingerprint of a public key: SHA-256 over its DER encoding, shown as lowercase hex. Compare it with the value the release
# manager announced through a separate channel (signed e-mail, printed hand-over sheet).
pubkey_fingerprint() { openssl pkey -pubin -in "$1" -outform DER 2>/dev/null | openssl dgst -sha256 -r | awk '{print $1}'; }

# verify_bundle [--pubkey PEM] [--fingerprint HEX]
# 1. the manifest signature verifies with the public key (bundled one unless --pubkey is given; pin it with --fingerprint),
# 2. every file listed in SHA256SUMS exists and matches, 3. no file exists that the manifest does not list.
verify_bundle() {
  local pub="$BUNDLE_DIR/signing/release-public.pem" want_fp="" failures=0
  while [[ $# -gt 0 ]]; do
    case $1 in
      --pubkey) pub=$2; shift 2 ;;
      --fingerprint) want_fp=$2; shift 2 ;;
      *) die "verify_bundle: unknown option $1" ;;
    esac
  done
  [[ -f "$BUNDLE_DIR/SHA256SUMS" ]] || die "SHA256SUMS is missing: this is not an intact bundle"
  [[ -f "$BUNDLE_DIR/SHA256SUMS.sig" ]] || die "SHA256SUMS.sig is missing: the bundle is not signed"
  [[ -f "$pub" ]] || die "public key $pub not found"

  local fp; fp=$(pubkey_fingerprint "$pub") || die "cannot read public key $pub"
  log "signing key fingerprint (SHA-256 of the DER public key): $fp"
  if [[ -n "$want_fp" ]]; then
    [[ "$fp" == "$(printf '%s' "$want_fp" | tr 'A-F' 'a-f')" ]] || die "signing key fingerprint does not match the expected value ($want_fp)"
    log "fingerprint matches the pinned value"
  else
    warn "no --fingerprint given: the signature only proves the bundle is internally consistent with the key shipped in it. Compare the fingerprint above with the one announced out of band, or pass --pubkey/--fingerprint."
  fi

  openssl dgst -sha256 -verify "$pub" -signature "$BUNDLE_DIR/SHA256SUMS.sig" "$BUNDLE_DIR/SHA256SUMS" >/dev/null 2>&1 \
    || die "signature of SHA256SUMS is INVALID: do not use this bundle"
  log "manifest signature: valid"

  local hash path actual count=0
  while read -r hash path; do
    [[ -n "$hash" ]] || continue
    path=${path#\*}                              # sha256sum's binary-mode marker
    [[ "$path" == ./* ]] || die "manifest entry '$path' is not a relative ./ path"
    case "$path" in */../*|*/..) die "manifest entry '$path' escapes the bundle" ;; esac
    if [[ ! -f "$BUNDLE_DIR/$path" ]]; then warn "missing: $path"; failures=$((failures + 1)); continue; fi
    actual=$(sha256_of "$BUNDLE_DIR/$path")
    if [[ "$actual" != "$hash" ]]; then warn "hash mismatch: $path"; failures=$((failures + 1)); fi
    count=$((count + 1))
  done < "$BUNDLE_DIR/SHA256SUMS"

  # Anything not in the manifest is unexpected (a planted file would be executed by install.sh or loaded by the runtime).
  local listed present extra
  listed=$(mktemp); present=$(mktemp)
  awk '{p=$2; sub(/^[*]/,"",p); print p}' "$BUNDLE_DIR/SHA256SUMS" | LC_ALL=C sort > "$listed"
  (cd "$BUNDLE_DIR" && find . -type f ! -name SHA256SUMS ! -name SHA256SUMS.sig | LC_ALL=C sort) > "$present"
  while IFS= read -r extra; do
    warn "not in the manifest: $extra"; failures=$((failures + 1))
  done < <(LC_ALL=C comm -13 "$listed" "$present")
  rm -f "$listed" "$present"

  [[ $failures -eq 0 ]] || die "$failures integrity problem(s): do not install this bundle"
  log "file hashes: $count files verified, nothing extra"
}

# Container runtime for image archives: docker, podman, or containerd (ctr / nerdctl). KEEL_RUNTIME overrides.
detect_runtime() {
  if [[ -n "${KEEL_RUNTIME:-}" ]]; then echo "$KEEL_RUNTIME"; return; fi
  for r in docker podman nerdctl ctr; do command -v "$r" >/dev/null 2>&1 && { echo "$r"; return; }; done
  die "no container runtime found (docker, podman, nerdctl or ctr)"
}

load_image_archive() { # runtime archive
  case $1 in
    docker|podman|nerdctl) "$1" load -i "$2" ;;
    ctr) ctr -n "${KEEL_CTR_NAMESPACE:-k8s.io}" images import "$2" ;;
    *) die "unsupported runtime $1" ;;
  esac
}

load_all_images() {
  local runtime=$1 archive
  for archive in "$BUNDLE_DIR"/images/*.tar; do
    log "loading $(basename "$archive")"
    load_image_archive "$runtime" "$archive" >/dev/null
  done
}

# value of KEY in an env-style file (no export, no quoting rules beyond optional surrounding quotes)
env_value() { awk -F= -v k="$1" '$1==k {sub(/^[^=]*=/,""); gsub(/^"|"$/,""); print; exit}' "$2"; }
