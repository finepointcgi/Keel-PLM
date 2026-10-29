#!/usr/bin/env bash
# Adds the licence and source part to an offline bundle's staging directory (ADR-0033, docs/ops/offline-install.md, docs/third-party/):
#   sources/<archives>     corresponding source of every LGPL/GPL component Keel ships (deploy/offline/third-party-sources.json), each verified
#                          against its pinned SHA-256, plus sources/SOURCES.txt (sha256sum format, and the state of the operating system package sources)
#   sbom/keel-plm.cdx.json CycloneDX 1.5 inventory from the NuGet restore results, the npm lock file, the vendored viewer files, fonts and image pins
#   THIRD-PARTY-NOTICES.md notices, written offer and licence texts
#   licenses/              licence texts of the viewer files and the embedded font
# Runs on the connected build machine (called by scripts/package-offline.sh; can be run alone). Fetching a missing archive needs the network once;
# archives are kept in a cache, so a second build and a build behind a mirror need none. Nothing here is needed on the target.
#
#   scripts/offline-compliance.sh --stage DIR --version 1.7.0 [--sources-cache DIR] [--os-sources-dir DIR] [--timestamp ISO8601]
#
#   --sources-cache DIR   where fetched archives are kept (default: $KEEL_SOURCES_CACHE or ~/.keel-release-sources); pre-fill it to build with no network
#   --os-sources-dir DIR  source archives of the operating system packages of the images (for example the .src.rpm files of the UBI 9 packages in the
#                         image SBOM), copied to sources/os/ with a SHA256SUMS. Without it the bundle says they are NOT included (open item).
set -euo pipefail
cd "$(dirname "$0")/.."

STAGE="" VERSION="" CACHE=${KEEL_SOURCES_CACHE:-$HOME/.keel-release-sources} OSDIR="" TIMESTAMP=""
while [[ $# -gt 0 ]]; do
  case $1 in
    --stage) STAGE=$2; shift 2 ;;
    --version) VERSION=$2; shift 2 ;;
    --sources-cache) CACHE=$2; shift 2 ;;
    --os-sources-dir) OSDIR=$2; shift 2 ;;
    --timestamp) TIMESTAMP=$2; shift 2 ;;
    -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
[[ -n "$STAGE" && -n "$VERSION" ]] || { echo "--stage and --version are required" >&2; exit 2; }
log() { printf '== %s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }
command -v dotnet >/dev/null || die "dotnet is required (keel-sbom reads the lock files and project files)"
sha256_of() { if command -v sha256sum >/dev/null; then sha256sum "$1" | awk '{print $1}'; else shasum -a 256 "$1" | awk '{print $1}'; fi; }
size_of() { wc -c < "$1" | tr -d ' '; }

mkdir -p "$STAGE/sources" "$STAGE/sbom" "$STAGE/licenses" "$CACHE"
SBOM=(dotnet run --project src/Keel.SbomTool -c Release --)

# 1. restore the shipped projects so the SBOM sees the resolved (transitive) packages and their licence files
log "restoring the shipped projects for the SBOM"
for p in Keel.Api Keel.AuditCli Keel.VaultTool; do dotnet restore "src/$p/$p.csproj" >/dev/null; done

# 2. source archives: cache, else fetch once; always verified against the pin before they enter the bundle
log "collecting source archives of the LGPL components"
MANIFEST="$STAGE/sources/SOURCES.txt"
{
  echo "# Corresponding source of the copyleft components delivered with Keel PLM $VERSION (LGPL 2.1 section 6: delivered with the software)."
  echo "# One line per archive in sha256sum format. The text of the offer is in ../THIRD-PARTY-NOTICES.md."
} > "$MANIFEST"
CATALOGUE=$("${SBOM[@]}" sources --root .) || die "could not read deploy/offline/third-party-sources.json"
[[ -n "$CATALOGUE" ]] || die "deploy/offline/third-party-sources.json lists no source archive"
while IFS=$'\t' read -r id file url sha size; do
  [[ -n "$id" ]] || continue
  cached="$CACHE/$file"
  if [[ ! -f "$cached" ]]; then
    command -v curl >/dev/null || die "$file is not in the cache $CACHE and curl is not available; copy it there ($url)"
    log "fetching $file"
    curl -fsSL --retry 3 --connect-timeout 20 -o "$cached.part" "$url" || { rm -f "$cached.part"; die "could not fetch $url; fetch it elsewhere and place it at $cached"; }
    mv "$cached.part" "$cached"
  fi
  [[ "$(sha256_of "$cached")" == "$sha" ]] || die "$cached does not match the pinned SHA-256 $sha (id $id). Do not edit the pin to make this pass: check that the file is the one named at $url (for a GitHub archive confirm the commit in its pax header), then update deploy/offline/third-party-sources.json in a reviewed change."
  [[ "$(size_of "$cached")" == "$size" ]] || die "$cached has the pinned SHA-256 but not the catalogued size $size"
  cp "$cached" "$STAGE/sources/$file"
  printf '%s  %s\n' "$sha" "$file" >> "$MANIFEST"
done <<< "$CATALOGUE"

# 3. operating system package sources (optional input of the release engineer)
if [[ -n "$OSDIR" ]]; then
  [[ -d "$OSDIR" ]] || die "--os-sources-dir $OSDIR is not a directory"
  mkdir -p "$STAGE/sources/os"
  : > "$STAGE/sources/os/SHA256SUMS"
  count=0
  while IFS= read -r f; do
    base=$(basename "$f")
    [[ "$base" =~ ^[A-Za-z0-9._+-]+$ ]] || die "operating system source file name '$base' has characters the manifest does not allow"
    cp "$f" "$STAGE/sources/os/$base"
    printf '%s  %s\n' "$(sha256_of "$f")" "$base" >> "$STAGE/sources/os/SHA256SUMS"
    count=$((count+1))
  done < <(find "$OSDIR" -maxdepth 1 -type f | LC_ALL=C sort)
  [[ $count -gt 0 ]] || die "--os-sources-dir $OSDIR contains no files"
  echo "os-sources: INCLUDED ($count files under sources/os/, listed with SHA-256 in sources/os/SHA256SUMS)" >> "$MANIFEST"
else
  echo "os-sources: NOT INCLUDED (open item: source of the GPL/LGPL operating system packages in the container images was not supplied to this build; see THIRD-PARTY-NOTICES.md, section 2)" >> "$MANIFEST"
fi

# 4. licence texts
log "copying licence texts"
cp docs/third-party/viewer-licenses/*.txt "$STAGE/licenses/"
cp src/Keel.Documents/Marking/Fonts/DejaVu-LICENSE.txt "$STAGE/licenses/"

# 5. SBOM and notices (strict: a component without a licence or a copyleft component without a source archive fails the build)
log "generating the SBOM and THIRD-PARTY-NOTICES.md"
args=(generate --root . --version "$VERSION" --out "$STAGE" --strict)
[[ -n "$TIMESTAMP" ]] && args+=(--timestamp "$TIMESTAMP")
"${SBOM[@]}" "${args[@]}"

# 6. the same checks verify-offline.sh runs on the finished bundle
"${SBOM[@]}" verify --bundle "$STAGE"
