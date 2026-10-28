#!/usr/bin/env bash
# Verifies this offline bundle before anything is loaded or run: manifest signature, every file hash, no extra files.
#
#   ./verify.sh [--pubkey release-public.pem] [--fingerprint <sha256-hex>]
#
# Pass the public key you received out of band (--pubkey) or pin the fingerprint announced by the release manager
# (--fingerprint). Without either, only internal consistency with the key shipped in the bundle can be shown.
set -euo pipefail
# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
verify_bundle "$@"
log "bundle $(cat "$BUNDLE_DIR/VERSION") verified"
