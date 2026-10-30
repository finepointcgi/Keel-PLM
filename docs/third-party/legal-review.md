# Licence compliance: status and pending legal review

**Status: legal review pending. There is no legal sign-off on anything in this directory, in `THIRD-PARTY-NOTICES.md` of an offline bundle, or in ADR-0027 and ADR-0033.** The engineering work below makes the delivery technically able to meet LGPL obligations; whether the wording and the arrangement meet them is a question for counsel.

Review packets for counsel (inventory, copyleft analysis, questions, evidence locations): [docs/review/README.md](../review/README.md), [legal-open-source.md](../review/legal-open-source.md), and for the export-control official [export-control.md](../review/export-control.md). They keep every outcome at pending.

## What engineering has done (increment 7.6, ADR-0033)

| Obligation (engineering reading, not advice) | How the offline bundle meets it | Where it is checked |
|---|---|---|
| Source of the LGPL components travels with the software (an air-gapped site cannot follow a link) | `sources/` holds the occt-import-js source at tag 0.0.23, the OpenCascade Technology source at the commit that tag pins, and the npm package the shipped files were copied from. Each archive is pinned by SHA-256 in `deploy/offline/third-party-sources.json` and verified before it enters the bundle | `scripts/offline-compliance.sh` (build), `keel-sbom verify` and `scripts/verify-offline.sh` (bundle), `OfflineComplianceTests` (CI) |
| A written offer | `THIRD-PARTY-NOTICES.md`, section "Written offer and replacement". The wording is a draft; the supplier contact is the placeholder `[Company]` | Notices must contain it, or `verify-offline.sh` fails |
| The licence texts | `licenses/` and `THIRD-PARTY-NOTICES.md` section 4 (LGPL 2.1 for occt-import-js and OpenCascade, MIT, Apache-2.0, the DejaVu font licence, and the texts the NuGet packages ship) | `keel-sbom verify` |
| The user can replace the LGPL library | The two files are separate (`occt-import-js.js`, `occt-import-js.wasm`), loaded by a worker through messages. Replacement paths (volume over `/app/wwwroot/viewer/vendor/`, or `Keel:Viewer:EnableCadImport=false`) are in the notices | Notices text (not exercised on a cluster) |
| An inventory of what is shipped | CycloneDX 1.5 `sbom/keel-plm.cdx.json` generated offline from the NuGet restore results, `tools/viewer-vendor/package-lock.json`, the vendored viewer files (hashes recomputed from disk and compared with `manifest.json`), the embedded fonts and the pinned images | `OfflineComplianceTests` |

## Open items for counsel and release engineering

1. **Legal review of this arrangement.** Whether delivering the files unmodified and separately, with source in `sources/` and the offer in the notices, satisfies LGPL 2.1 section 6 for this product and customer contract. ADR-0027 section 5 and assumption VWR-12 remain engineering readings.
2. **Operating system packages in the container images (open, not covered).** The UBI 9 based application image and the PostgreSQL, SeaweedFS and Caddy images contain GPL and LGPL distribution packages. The bundle lists them (`sbom/image-*.cdx.json`) only when built with `syft`, and ships their source only if the release engineer passes `--os-sources-dir` to `scripts/package-offline.sh` (for the UBI image: the `.src.rpm` files of the packages named in the SBOM, from the Red Hat source repositories). Without it `sources/SOURCES.txt` states `os-sources: NOT INCLUDED`. This was not produced or verified in 7.6.
3. **Written offer wording and the real supplier contact** (replace `[Company]`), and the offer period (the draft says three years).
4. **Emscripten runtime libraries** compiled into the two files (listed as "unconfirmed" in the SBOM): confirm which libraries the binary contains. They are permissively licensed, so no source offer is expected, but their notices may need to be reproduced.
5. **Archive provenance.** GitHub generates the two `.tar.gz` source archives on request; their bytes could change if GitHub changes its tooling. The pins make that visible (the build stops). If it happens, confirm the commit in the archive's pax header, then update the pin in a reviewed change. Consider mirroring the archives in the organisation's artifact store.
6. **Copies of Keel's own sources for the LGPL components are not modified.** The shipped files are unmodified upstream build output. If anyone modifies and rebuilds them, the modified source must be added to `sources/` and the pins updated.
7. **Image-level notices.** The application image does not yet carry `THIRD-PARTY-NOTICES.md`; the licence texts of the viewer files are copied into it by the Dockerfile (`/app/licenses`). Add the generated notices once counsel has approved the text.

## How to refresh

- A new vendored library: run `tools/viewer-vendor` (maintainer), update `docs/third-party/viewer.md` and the manifest. `OfflineComplianceTests.Every_copyleft_licensed_vendored_viewer_file_is_covered_by_a_pinned_source_archive` fails if a GPL/LGPL file has no catalogued source.
- A new copyleft NuGet or npm package: `keel-sbom --strict` (run by `scripts/offline-compliance.sh`) fails the bundle build until `deploy/offline/third-party-sources.json` lists its source.
- To build with no network: pre-fill `~/.keel-release-sources` (or `--sources-cache`) with the archives named in the catalogue.
