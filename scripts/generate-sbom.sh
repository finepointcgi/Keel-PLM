#!/usr/bin/env bash
# Software bill of materials and licence inventory from the real dependency graph (ADR-0059). No network once the NuGet cache is populated.
#
#   scripts/generate-sbom.sh --out DIR [--version V]   writes DIR/sbom/keel-plm.cdx.json (what is delivered) and
#                                                      DIR/sbom/keel-plm-full.cdx.json (plus build/test/maintenance components, scope "excluded"),
#                                                      both CycloneDX 1.5 with SHA-256/SHA-512 hashes, and DIR/THIRD-PARTY-NOTICES.md
#   scripts/generate-sbom.sh --licenses                rewrites THIRD-PARTY-LICENSES.md (commit the result)
#   scripts/generate-sbom.sh --check                   fails when THIRD-PARTY-LICENSES.md is stale or a licence policy rule is broken (CI)
#
# Inputs: NuGet restore results of every project (dotnet restore), tools/viewer-vendor/package-lock.json, the vendored viewer files (hashes recomputed
# from disk), the embedded fonts, deploy/images.env, the Python add-on (tools/freecad-keel) and deploy/offline/third-party-sources.json.
set -euo pipefail
cd "$(dirname "$0")/.."
MODE=""; OUT=""; VERSION="0.0.0-dev"
while [[ $# -gt 0 ]]; do
  case $1 in
    --out) MODE=sbom; OUT=$2; shift 2 ;;
    --version) VERSION=$2; shift 2 ;;
    --licenses) MODE=licenses; shift ;;
    --check) MODE=check; shift ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
[[ -n "$MODE" ]] || { sed -n 2,12p "$0" | sed 's/^# \{0,1\}//'; exit 2; }

dotnet restore Keel.slnx -v quiet >/dev/null
dotnet build src/Keel.SbomTool -c Release -v quiet --nologo >/dev/null
SBOM=(dotnet src/Keel.SbomTool/bin/Release/net10.0/keel-sbom.dll)
TS=${SOURCE_DATE_EPOCH:+--timestamp $(date -u -r "$SOURCE_DATE_EPOCH" +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -d "@$SOURCE_DATE_EPOCH" +%Y-%m-%dT%H:%M:%SZ)}

case $MODE in
  licenses) "${SBOM[@]}" licenses --root . --out THIRD-PARTY-LICENSES.md ;;
  check) "${SBOM[@]}" licenses --root . --check ;;
  sbom)
    mkdir -p "$OUT"
    # shellcheck disable=SC2086
    "${SBOM[@]}" generate --root . --version "$VERSION" --out "$OUT" --strict ${TS:-}
    # shellcheck disable=SC2086
    "${SBOM[@]}" generate --root . --version "$VERSION" --out "$OUT/full" --include-development ${TS:-} >/dev/null
    mv "$OUT/full/sbom/keel-plm-full.cdx.json" "$OUT/sbom/keel-plm-full.cdx.json"; rm -rf "$OUT/full"
    for f in "$OUT"/sbom/keel-plm.cdx.json "$OUT"/sbom/keel-plm-full.cdx.json; do
      jq -e '.bomFormat=="CycloneDX" and .specVersion=="1.5" and (.components|length>0)' "$f" >/dev/null || { echo "invalid SBOM $f" >&2; exit 1; }
      echo "$f: $(jq '.components|length' "$f") components, $(jq '[.components[]|select(.hashes)]|length' "$f") with hashes, $(jq '[.components[]|select(.scope=="excluded")]|length' "$f") excluded"
    done
    ;;
esac
