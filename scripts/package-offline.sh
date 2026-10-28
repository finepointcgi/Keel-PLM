#!/usr/bin/env bash
# Builds the signed offline install bundle (ADR-0012, ADR-0020, docs/ops/offline-install.md).
# Runs on a connected build machine; the result is carried into the air gap. Nothing here is needed on the target.
#
#   scripts/package-offline.sh [--version 1.7.0] [--image REF] [--out DIR] [--pull] [--no-sbom] [--no-third-party]
#
#   --image REF         package an image that already exists locally instead of building the Dockerfile (used by CI that builds
#                       elsewhere, and for bundle verification on machines that cannot build the UBI image)
#   --pull              pull missing third-party images (otherwise they must already be present locally)
#   --no-third-party    leave out postgres/vault/proxy archives (sites that bring their own)
#
# Signing: an ECDSA P-256 key pair is generated PER RELEASE under $KEEL_SIGNING_DIR (default ~/.keel-release-signing/<version>)
# unless KEEL_SIGNING_KEY points at an existing private key. The public key ships in the bundle; announce its fingerprint
# through a separate channel. Production signing keys belong to the customer: pass their key (an HSM-backed key via an
# openssl engine/provider works the same way) with KEEL_SIGNING_KEY.
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=1.7.0 IMAGE="" OUT=deploy/offline/out PULL=false SBOM=true THIRD_PARTY=true
while [[ $# -gt 0 ]]; do
  case $1 in
    --version) VERSION=$2; shift 2 ;;
    --image) IMAGE=$2; shift 2 ;;
    --out) OUT=$2; shift 2 ;;
    --pull) PULL=true; shift ;;
    --no-sbom) SBOM=false; shift ;;
    --no-third-party) THIRD_PARTY=false; shift ;;
    -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
log() { printf '== %s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }
for tool in docker helm openssl tar awk; do command -v "$tool" >/dev/null || die "$tool is required"; done

# shellcheck source=../deploy/images.env
set -a; source deploy/images.env; set +a
NAME=keel-offline-$VERSION
STAGE=$OUT/$NAME
rm -rf "$STAGE"; mkdir -p "$STAGE"/{images,chart,compose,sbom,signing,docs}

sha256_of() { if command -v sha256sum >/dev/null; then sha256sum "$1" | awk '{print $1}'; else shasum -a 256 "$1" | awk '{print $1}'; fi; }
repo_tag() { local ref=${1%@*}; echo "$ref"; }   # repo:tag without the digest

# 1. application image ---------------------------------------------------------------------------------------------------
APP_REF="$KEEL_APP_REPOSITORY:$VERSION"
if [[ -n "$IMAGE" ]]; then
  docker image inspect "$IMAGE" >/dev/null 2>&1 || die "image $IMAGE not found locally"
  [[ "$IMAGE" == "$APP_REF" ]] || docker tag "$IMAGE" "$APP_REF"
  log "packaging existing image $IMAGE as $APP_REF"
else
  log "docker system df before the build (the UBI build needs about 2 GB free in the Docker VM)"
  docker system df
  log "building $APP_REF from the Dockerfile on the pinned UBI base images"
  REVISION=$(git rev-parse --short=12 HEAD 2>/dev/null || echo unknown)
  docker build -t "$APP_REF" \
    --build-arg SDK_IMAGE="$SDK_IMAGE" --build-arg RUNTIME_IMAGE="$RUNTIME_IMAGE" \
    --build-arg KEEL_VERSION="$VERSION" --build-arg KEEL_REVISION="$REVISION" .
fi
docker save "$APP_REF" -o "$STAGE/images/keel-api-$VERSION.tar"

# 2. third-party images (pinned by digest in deploy/images.env) ------------------------------------------------------------
save_pinned() { # name, pinned ref
  local name=$1 ref=$2 digest=${2#*@} tagref; tagref=$(repo_tag "$ref")
  if ! docker image inspect "$tagref" >/dev/null 2>&1; then
    $PULL || die "$tagref is not present locally (use --pull on a connected machine)"
    docker pull "$ref"
  fi
  # The local copy must be the pinned one: its manifest-list digest has to be among the image's repo digests.
  if ! docker image inspect "$tagref" --format '{{join .RepoDigests " "}}' | grep -qF "$digest"; then
    die "local $tagref does not match the pinned digest $digest; pull the pinned reference ($ref)"
  fi
  docker save "$tagref" -o "$STAGE/images/$name.tar"
}
if $THIRD_PARTY; then
  log "saving pinned third-party images"
  save_pinned postgres "$KEEL_POSTGRES_IMAGE"
  save_pinned vault "$KEEL_VAULT_IMAGE"
  save_pinned proxy "$KEEL_PROXY_IMAGE"
fi

# 3. chart ---------------------------------------------------------------------------------------------------------------
log "packaging the Helm chart"
helm package deploy/helm/keel --version "$VERSION" --app-version "$VERSION" --destination "$STAGE/chart" >/dev/null

# 4. compose example and ops documents (no certificates, no secrets, no test overlay) --------------------------------------
log "copying the compose example"
cp deploy/compose/compose.prod-example.yaml "$STAGE/compose/"
mkdir -p "$STAGE/compose/prod-example"
for f in Caddyfile nginx.conf env.example gen-secrets.sh gen-test-pki.sh postgres-entrypoint.sh; do cp "deploy/compose/prod-example/$f" "$STAGE/compose/prod-example/"; done
mkdir -p "$STAGE/compose/prod-example/initdb"; cp deploy/compose/prod-example/initdb/00-roles.sh "$STAGE/compose/prod-example/initdb/"
cp deploy/images.env "$STAGE/images.env"
cp docs/ops/*.md "$STAGE/docs/"

# 5. SBOMs ----------------------------------------------------------------------------------------------------------------
SBOM_NOTES=""
if $SBOM; then
  TOOLDIR=$(mktemp -d)
  if command -v dotnet >/dev/null && dotnet tool install CycloneDX --version 6.3.1 --tool-path "$TOOLDIR" >/dev/null 2>&1; then
    log "generating the CycloneDX SBOM for the .NET application"
    dotnet restore src/Keel.Api/Keel.Api.csproj >/dev/null
    "$TOOLDIR/dotnet-cyclonedx" src/Keel.Api/Keel.Api.csproj -o "$STAGE/sbom" -fn keel-api.cdx.json -F Json --set-name "keel-api" --set-version "$VERSION" >/dev/null
    "$TOOLDIR/dotnet-cyclonedx" src/Keel.AuditCli/Keel.AuditCli.csproj -o "$STAGE/sbom" -fn keel-audit.cdx.json -F Json --set-name "keel-audit" --set-version "$VERSION" >/dev/null
    SBOM_NOTES="dotnet: CycloneDX 6.3.1 (keel-api, keel-audit)"
  else
    SBOM_NOTES="dotnet: NOT generated (CycloneDX tool unavailable)"
    echo "warning: CycloneDX tool could not be installed; no .NET SBOM in this bundle" >&2
  fi
  rm -rf "$TOOLDIR"
  if command -v syft >/dev/null; then
    log "generating image SBOMs with syft"
    for archive in "$STAGE"/images/*.tar; do
      syft "docker-archive:$archive" -o cyclonedx-json="$STAGE/sbom/image-$(basename "$archive" .tar).cdx.json" >/dev/null
    done
    SBOM_NOTES="$SBOM_NOTES; images: syft $(syft version | awk '/^Version/{print $2}')"
  else
    SBOM_NOTES="$SBOM_NOTES; images: NOT generated (syft not installed; scan the archives with the site's scanner)"
    echo "warning: syft not found; no image SBOMs in this bundle" >&2
  fi
fi

# 6. scripts, version, build info -----------------------------------------------------------------------------------------
cp deploy/offline/{lib.sh,verify.sh,install.sh,upgrade.sh} "$STAGE/"
chmod +x "$STAGE"/*.sh "$STAGE"/compose/prod-example/*.sh "$STAGE"/compose/prod-example/initdb/*.sh
echo "$VERSION" > "$STAGE/VERSION"
{
  echo "keel-offline $VERSION"
  echo "git-revision $(git rev-parse HEAD 2>/dev/null || echo unknown)"
  echo "built-at $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "app-image $APP_REF $(docker image inspect "$APP_REF" --format '{{.Id}}')"
  echo "sbom $SBOM_NOTES"
  echo "helm $(helm version --short)"
  echo "docker $(docker version --format '{{.Server.Version}}' 2>/dev/null || echo unknown)"
} > "$STAGE/BUILD-INFO"

# 7. signing key and manifest ---------------------------------------------------------------------------------------------
KEYDIR=${KEEL_SIGNING_DIR:-$HOME/.keel-release-signing/$VERSION}
if [[ -n "${KEEL_SIGNING_KEY:-}" ]]; then
  KEY=$KEEL_SIGNING_KEY
  [[ -f "$KEY" ]] || die "KEEL_SIGNING_KEY $KEY not found"
  log "signing with the supplied key $KEY"
else
  KEY=$KEYDIR/release-private.pem
  if [[ ! -f "$KEY" ]]; then
    log "generating the per-release ECDSA P-256 signing key in $KEYDIR (keep the private key OFF the bundle and off shared media)"
    umask 077; mkdir -p "$KEYDIR"
    openssl ecparam -name prime256v1 -genkey -noout -out "$KEY"
  fi
fi
openssl pkey -in "$KEY" -pubout -out "$STAGE/signing/release-public.pem"
FINGERPRINT=$(openssl pkey -pubin -in "$STAGE/signing/release-public.pem" -outform DER | openssl dgst -sha256 -r | awk '{print $1}')

log "writing SHA256SUMS and the detached signature"
( cd "$STAGE" && find . -type f ! -name SHA256SUMS ! -name SHA256SUMS.sig | LC_ALL=C sort | while IFS= read -r f; do
    printf '%s  %s\n' "$(sha256_of "$f")" "$f"
  done > SHA256SUMS )
openssl dgst -sha256 -sign "$KEY" -out "$STAGE/SHA256SUMS.sig" "$STAGE/SHA256SUMS"
openssl dgst -sha256 -verify "$STAGE/signing/release-public.pem" -signature "$STAGE/SHA256SUMS.sig" "$STAGE/SHA256SUMS" >/dev/null

# 8. archive --------------------------------------------------------------------------------------------------------------
log "creating the archive"
( cd "$OUT" && COPYFILE_DISABLE=1 tar -cf "$NAME.tar" "$NAME" )
( cd "$OUT" && printf '%s  %s\n' "$(sha256_of "$NAME.tar")" "$NAME.tar" > "$NAME.tar.sha256" )
echo "$FINGERPRINT" > "$OUT/$NAME.signing-key.fingerprint"

echo
echo "bundle:       $OUT/$NAME.tar ($(du -h "$OUT/$NAME.tar" | awk '{print $1}'))"
echo "archive hash: $OUT/$NAME.tar.sha256"
echo "signing key:  fingerprint $FINGERPRINT (announce it out of band; private key at $KEY)"
echo "sbom:         $SBOM_NOTES"
