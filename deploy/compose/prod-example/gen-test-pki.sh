#!/usr/bin/env bash
# Generates a throwaway internal CA and server certificates for the compose production example (docs/ops/compose-example.md).
# TEST ONLY. A real site uses its own internal CA (DoD PKI / enterprise CA): request one certificate per service name, and
# put the CA chain in certs/ca.crt. Keys are ECDSA P-256 (FIPS 186-4 approved); certificates are valid for 30 days.
#
#   gen-test-pki.sh [output-dir] [public-host]       defaults: ./certs, keel.test
set -euo pipefail
OUT=${1:-"$(dirname "$0")/certs"}
HOST=${2:-keel.test}
mkdir -p "$OUT"
umask 077
cd "$OUT"

if [[ ! -f ca.key ]]; then
  openssl ecparam -name prime256v1 -genkey -noout -out ca.key
  openssl req -new -x509 -key ca.key -sha256 -days 30 -subj "/O=Keel PLM test/CN=Keel test internal CA" \
    -addext "basicConstraints=critical,CA:TRUE,pathlen:0" -addext "keyUsage=critical,keyCertSign,cRLSign" -out ca.crt
fi

issue() { # name, comma-separated SANs (DNS names or IP:x.x.x.x)
  local name=$1 sans=$2
  [[ -f "$name.crt" ]] && return 0
  openssl ecparam -name prime256v1 -genkey -noout -out "$name.key"
  openssl req -new -key "$name.key" -subj "/O=Keel PLM test/CN=$name" -out "$name.csr"
  printf 'basicConstraints=CA:FALSE\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=serverAuth\nsubjectAltName=%s\n' "$sans" > "$name.ext"
  openssl x509 -req -in "$name.csr" -CA ca.crt -CAkey ca.key -CAcreateserial -sha256 -days 30 -extfile "$name.ext" -out "$name.crt" 2>/dev/null
  rm -f "$name.csr" "$name.ext"
}

issue proxy    "DNS:$HOST,DNS:localhost,IP:127.0.0.1"
issue postgres "DNS:postgres"
issue vault    "DNS:vault"
issue idp      "DNS:idp.test"

# Keys are readable by the container users through bind mounts; the directory itself stays 0700 on the host.
chmod 0444 ./*.crt ./*.key
chmod 0700 .
echo "wrote CA and certificates for $HOST, postgres, vault and the test IdP to $OUT"
