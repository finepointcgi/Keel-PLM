# Third-party components of the file viewer (increment 6.0)

All files below are vendored under `src/Keel.Web/wwwroot/viewer/vendor/` and served from the application's own origin. Nothing is loaded from a CDN or the internet at run time. They are produced by `tools/viewer-vendor` (maintainer tool: `npm ci` there, `node build.mjs`, then `node manifest.mjs`), from the exact versions pinned in `tools/viewer-vendor/package.json` and `package-lock.json`. A unit test (`ViewerCspAndAssetsTests`) fails if a served file differs from `manifest.json`, or if the SHA-256 and licence of a vendored file below is out of date.

| Component | Version | Licence | Used for |
|---|---|---|---|
| three.js (with `OrbitControls`, `STLLoader`, `OBJLoader`, `GLTFLoader` from its examples) | 0.186.1 | MIT | 3D rendering and loaders; bundled into one file with esbuild 0.25.12 |
| occt-import-js (OpenCascade Technology compiled to WebAssembly, Emscripten 3.1.69) | 0.0.23 | **LGPL-2.1** | STEP and IGES conversion, in a Web Worker |
| pdf.js (`pdfjs-dist`) | 4.10.38 | Apache-2.0 | PDF rendering in a worker |

Licence texts: [viewer-licenses/](viewer-licenses/) (`three-MIT.txt`, `pdfjs-Apache-2.0.txt`, `occt-import-js-LGPL-2.1.txt`, `opencascade-LGPL-2.1.txt`). They must be copied into every container image and offline bundle that contains the viewer.

## Vendored files

| File | Size (bytes) | SHA-256 | License |
|---|---|---|---|
| `vendor/occt-import-js.js` | 96871 | `3fb44ce11d00611f9b3f3c5775d520ebab48930c1f08279b7b1316f05f0d3379` | LGPL-2.1 |
| `vendor/occt-import-js.wasm` | 7604031 | `33391fc9d94ea5c869a6718488bf0a9a464222bac9bdc764dfe1690cef281952` | LGPL-2.1 |
| `vendor/pdf.min.mjs` | 352645 | `27fc2a057a00f92a4334ad06e17dbd7259912954e9fb7f76400bcca5fd190a9c` | Apache-2.0 |
| `vendor/pdf.worker.min.mjs` | 1375838 | `1baa1844c89c80a5b2797c916e75ab29254be46d8e9cb53cb6364d7aad84be36` | Apache-2.0 |
| `vendor/three-bundle.js` | 651894 | `1b5e810aa9149dcccff96fd1a4246290f3753aad64b88ce10236a1a65b09f99e` | MIT |

## LGPL-2.1 component: occt-import-js and OpenCascade Technology

- Copyright holders: the occt-import-js authors (https://github.com/kovacsv/occt-import-js) and Open Cascade SAS (OpenCascade Technology). Both licence texts are the files named above.
- The two files `occt-import-js.js` and `occt-import-js.wasm` are **unmodified upstream build output**, kept as separate files, loaded by `step-worker.js` through `importScripts` and WebAssembly and used only through messages. They can be replaced by a modified build of the same interface by replacing the two files (and updating `manifest.json` and this table, which only guards the repository).
- **Corresponding source** (for deliveries and for the written offer of LGPL-2.1 section 6): occt-import-js tag v0.0.23 (https://github.com/kovacsv/occt-import-js) with the OpenCascade revision pinned by its `occt` submodule, built with Emscripten 3.1.69 using the scripts in its `tools` folder. **Source is now in the offline bundle (increment 7.6, ADR-0033):** `scripts/package-offline.sh` adds `sources/` with the occt-import-js source at tag 0.0.23, the OpenCascade source at the pinned commit and the npm package the files came from, each verified against the SHA-256 pins in `deploy/offline/third-party-sources.json`, plus `THIRD-PARTY-NOTICES.md` (written offer) and a CycloneDX SBOM; `scripts/verify-offline.sh` fails a bundle without them. **Legal review of this arrangement is still pending** ([legal-review.md](legal-review.md)); source for the operating system packages of the container images is a separate open item there.
- A site that does not want the component sets `Keel:Viewer:EnableCadImport=false` (STEP and IGES then show file details and the download action only) and may delete the two files.
- Engineering reading of the licence, not legal advice: see ADR-0027, section 5.

## Not vendored
pdf.js standard-font and CMap data, the pdf.js annotation/form layer and viewer UI, Draco/KTX2/meshopt decoders for glTF. Their absence is a documented limit (`docs/increments/6.0-viewer.md`).
