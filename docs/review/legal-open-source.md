# Packet 1: open-source and third-party licence inventory (for counsel)

Status: **pending counsel review. For counsel to decide: every conclusion in this packet.** This is an engineering inventory with source references, not legal advice, and nothing in it says a licence obligation is or is not met. See [README.md](README.md) for how the packets are used and what they do not do.

Inventory date: 2026-10-29, repository state of branch `worktree-agent-a6ba74fa378bd8b25` (created from `main`). Marks used below: **verified** = read in a file of this repository, or produced by a tool run on it (named); **unverified** = stated from general public knowledge or not checkable here, to be confirmed.

## 1. How this inventory was built

| Source read | What it gave |
|---|---|
| `Directory.Packages.props`, every `src/**/*.csproj` and `tests/**/*.csproj` | NuGet packages and which project references them |
| `src/Keel.SbomTool` run on this tree (`dotnet run --project src/Keel.SbomTool -- --root . --version 9.3-review --out <dir>`) | CycloneDX 1.5 SBOM of 78 components, of which 39 are marked as delivered; licences read from the NuGet packages' own metadata, the npm lock file and the vendored files. Result recorded in section 3 |
| `tools/viewer-vendor/package.json`, `package-lock.json` | npm inputs of the vendored browser files (maintainer tool, never run at product run time) |
| `src/Keel.Web/wwwroot/viewer/vendor/`, `manifest.json`, `docs/third-party/viewer.md`, `docs/third-party/viewer-licenses/` | vendored browser files, hashes, licence texts |
| `src/Keel.Documents/Marking/Fonts/` (and the link in `src/Keel.Reports`) | embedded fonts and the font licence text |
| `tests/Keel.E2E/vendor/axe-core/` (`VENDOR.md`, `LICENSE`) | test-only vendored library |
| `Dockerfile`, `deploy/images.env`, `deploy/compose/*.yaml`, `deploy/helm/keel/values.yaml` | base images and third-party images |
| `deploy/offline/third-party-sources.json`, `scripts/offline-compliance.sh`, `scripts/package-offline.sh` | pinned corresponding source, bundled-but-not-locked components, OS-layer statement |
| `docs/third-party/legal-review.md`, `viewer.md`, ADR-0027, ADR-0033, `docs/increments/7.6-doc-review-licenses.md`, ADR-0044, ADR-0045, ADR-0020 | the engineering readings that counsel is asked to confirm or reject |
| `~/.nuget/packages/*/*.nuspec` on the developer machine | licence expressions of the test-only packages (the SBOM tool excludes tests; not a repository file) |

To reproduce the SBOM part: `dotnet build Keel.slnx` (so that `project.assets.json` exists), then the `keel-sbom` command above. `OfflineComplianceTests` (`tests/Keel.UnitTests/OfflineComplianceTests.cs`) exercises the generator.

## 2. Summary: components that need counsel's attention

| # | Component | Licence (SPDX) | Why it is flagged | Section |
|---|---|---|---|---|
| 1 | occt-import-js 0.0.23 (with OpenCascade Technology compiled in) | `LGPL-2.1` (occt-import-js); `LGPL-2.1-only WITH OCCT-exception-1.0` (OCCT, as written in the source catalogue) | Weak copyleft, delivered to customers, runs in the user's browser as WebAssembly | 5.1 |
| 2 | Emscripten 3.1.69 runtime pieces inside the two files above | `MIT AND Apache-2.0 WITH LLVM-exception` (as written in the catalogue) | Recorded as **unconfirmed**: the binary was not inspected | 5.2 |
| 3 | Operating-system packages of the container images (UBI 9, PostgreSQL, SeaweedFS, Caddy images; demo base image) | mixed, includes GPL and LGPL packages | Source for these is **not** in the bundle unless `--os-sources-dir` is given; open item | 5.3 |
| 4 | axe-core 4.10.2 | `MPL-2.0` | File-level copyleft; test-only, claimed not shipped | 5.4 |
| 5 | ClamAV | `GPL-2.0` (stated in `deploy/compose/compose.clamav.yaml`) | Optional separate container, not in the Keel image or bundle manifest | 5.5 |
| 6 | DejaVu Sans, DejaVu Sans Bold (embedded in the server assemblies) | Bitstream Vera licence with DejaVu public-domain changes (SBOM id `Bitstream-Vera`) | Not copyleft, but has a notice condition and a rename condition for modified fonts | 5.6 |
| 7 | PDFsharp 6.2.4 | `MIT` | Permissive; recorded because it is the PDF engine | 5.7 |
| 8 | SeaweedFS 4.47, Keycloak 26.4, PostgreSQL 17, Caddy 2.10 | not stated in the repository except SeaweedFS (Apache-2.0) | Third-party services delivered as images or used in dev/demo | 5.8 |
| 9 | Keel's own licence | none declared | There is no `LICENSE` file in the repository root (checked: `ls`) | 6 |

## 3. Inventory

### 3.1 Browser: vendored files served from the application's own origin (verified)

All five files are under `src/Keel.Web/wwwroot/viewer/vendor/`, produced by `tools/viewer-vendor` from `package-lock.json`, and recorded with SHA-256 in `docs/third-party/viewer.md` and `src/Keel.Web/wwwroot/viewer/manifest.json`. A unit test (`tests/Keel.UnitTests/ViewerCspAndAssetsTests.cs`) fails if a served file differs from the manifest.

| Component | Version | Licence | File(s) | Ships where | How loaded | What the licence requires (engineering reading; counsel to decide) |
|---|---|---|---|---|---|---|
| three.js (with OrbitControls, STLLoader, OBJLoader, GLTFLoader) | 0.186.1 | `MIT` | `three-bundle.js` (esbuild 0.25.12 bundle) | browser (and in the server image as a static file; bundle and image) | script loaded by the viewer page; same origin | keep the copyright and permission notice (`docs/third-party/viewer-licenses/three-MIT.txt`, copied to `/app/licenses` by the Dockerfile) |
| pdf.js (`pdfjs-dist`) | 4.10.38 | `Apache-2.0` | `pdf.min.mjs`, `pdf.worker.min.mjs` | browser | module and a Web Worker, same origin | licence text, notices, state changes if modified (`pdfjs-Apache-2.0.txt`); the repo does not say whether pdf.js ships a NOTICE file: **unverified** |
| occt-import-js | 0.0.23 | `LGPL-2.1` | `occt-import-js.js` (96,871 bytes), `occt-import-js.wasm` (7,604,031 bytes) | browser | `importScripts` plus WebAssembly instantiation inside the Web Worker `step-worker.js`; talks to the rest of the viewer only by `postMessage` | LGPL text, notices, corresponding source or written offer, ability to replace the library; see 5.1 |
| OpenCascade Technology | commit `d2abb6d844231cb8f29be6894440874a4700e4a5` | `LGPL-2.1-only WITH OCCT-exception-1.0` (as catalogued) | compiled into `occt-import-js.wasm` | browser | as above | as above, plus the OCCT exception text |
| Emscripten runtime libraries | 3.1.69 | `MIT AND Apache-2.0 WITH LLVM-exception` | compiled into both files | browser | as above | **unconfirmed** which libraries are present; see 5.2 |

Not vendored (documented): pdf.js standard-font and CMap data, annotation layer, Draco/KTX2/meshopt decoders (`docs/third-party/viewer.md`, "Not vendored").

Own first-party scripts in `src/Keel.Web/wwwroot` (`viewer/*.js`, `js/*.js`, `session-lock.js`) and style sheets are Keel's code. The Blazor framework script `_framework/blazor.web.js` comes from the ASP.NET Core shared framework (the SBOM lists `Microsoft.AspNetCore.App.Internal.Assets` 10.0.13, `MIT`).

### 3.2 Fonts embedded in the server (verified)

| Component | Licence | Files | Where | How used | Requirement |
|---|---|---|---|---|---|
| DejaVu Sans, DejaVu Sans Bold | Bitstream Vera licence plus DejaVu public-domain changes; text in `src/Keel.Documents/Marking/Fonts/DejaVu-LICENSE.txt`. SBOM id `Bitstream-Vera` | `DejaVuSans.ttf`, `DejaVuSans-Bold.ttf` | server assemblies (`Keel.Documents`, linked as embedded resources by `Keel.Reports` with a `LogicalName`) | loaded by `EmbeddedFontResolver` so PDFsharp can stamp marking banners and render release-package PDFs; used by the server, drawn into PDFs that go to users | include the copyright and permission notice in copies of the font software; modified fonts must be renamed (the text says so). The repo does not modify the fonts (no evidence either way besides the file hashes in the SBOM: **unverified** that the files are unmodified upstream) |

The marking PDFs embed (a subset of) glyph data from these fonts in the output files; whether that makes the generated PDFs "distribution" of the font software is a question for counsel (section 6, Q9).

### 3.3 Server: NuGet packages that ship (verified by the SBOM run; versions from `Directory.Packages.props` or resolved transitively)

Direct references (project in brackets):

| Package | Version | Licence | Project | Linking |
|---|---|---|---|---|
| Npgsql | 10.0.3 | `PostgreSQL` | every data module | managed assembly, static reference, in the process |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.14 | `MIT` | `Keel.Api` | managed assembly |
| Microsoft.AspNetCore.Authentication.OpenIdConnect | 10.0.14 | `MIT` | `Keel.Web` | managed assembly |
| Microsoft.AspNetCore.OpenApi | 10.0.14 | `MIT` | `Keel.Api` | managed assembly (OpenAPI document generation) |
| prometheus-net.AspNetCore (and prometheus-net) | 8.2.1 | `MIT` | `Keel.Api`, `Keel.Integration` | managed assembly |
| AWSSDK.S3 (and AWSSDK.Core 4.0.102.8) | 4.0.104 | `Apache-2.0` | `Keel.Documents` | managed assembly, talks S3 API to the vault |
| PDFsharp | 6.2.4 | `MIT` | `Keel.Documents`, `Keel.Reports` | managed assembly; the "Core" build (no GDI/WPF), no native code per ADR-0044 |
| PdfPig (UglyToad.PdfPig and its Core, Fonts, Tokenization, Tokens and DocumentLayoutAnalysis assemblies, one package) | 0.1.16 | `Apache-2.0` | `Keel.Documents` | managed assembly, no native code; reads the text layer of PDFs for the redline comparison (ADR-0053, increment 9.5). Also used by the tests. Apache-2.0 notice and any NOTICE text to be carried: **unverified** whether the package ships a NOTICE file |
| System.Security.Cryptography.Pkcs | 10.0.14 | `MIT` | `Keel.Documents`, `Keel.Reports` | pinned patched servicing version, transitive of PDFsharp (comment in `Directory.Packages.props`) |

Transitive packages found by the SBOM run, all `MIT`: Microsoft.Bcl.Cryptography 10.0.2, Microsoft.Extensions.DependencyInjection.Abstractions 10.0.0, Microsoft.Extensions.Logging.Abstractions 10.0.0, Microsoft.IdentityModel.Abstractions / JsonWebTokens / Logging / Protocols / Protocols.OpenIdConnect / Tokens 8.19.2, System.IdentityModel.Tokens.Jwt 8.19.2, Microsoft.OpenApi 2.12.2.

Not in the SBOM: the .NET runtime and the ASP.NET Core shared framework themselves. They come from the base image (`ubi9/dotnet-100-aspnet`, or `mcr.microsoft.com/dotnet/aspnet` in the demo). Their licence is not stated in the repository: **unverified** (generally published as MIT; counsel/engineering to confirm from the images).

The Keel host also contains Keel's own modules (`src/Keel.*`) and three command-line tools published into the same image: `keel-audit` (`Keel.AuditCli`), `keel-vault` (`Keel.VaultTool`) and the migrator mode of `Keel.Api` (Dockerfile). `Keel.Import`, `Keel.CadAgent` (`keel-cad`) and `Keel.Seed` are separate programs (the seeder only in the demo image).

### 3.4 Test-only components (not shipped; claim to be checked by counsel against the release process)

| Component | Version | Licence | Source of the licence fact | Use |
|---|---|---|---|---|
| xunit.v3 | 4.0.2 | `Apache-2.0` | developer NuGet cache nuspec | unit, integration, E2E test runner |
| bunit | 2.13.3 | `MIT` | nuspec | Blazor component tests |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.14 | `MIT` | nuspec | host tests |
| Testcontainers, Testcontainers.PostgreSql | 4.16.0 | `MIT` | nuspec | integration tests start PostgreSQL and SeaweedFS containers |
| Microsoft.Playwright | 1.61.0 | `MIT` | nuspec | E2E browser tests (`tests/Keel.E2E`). Playwright downloads browser builds at set-up time; those browsers' licences are **unverified** and outside the repository |
| PdfPig | 0.1.16 | `Apache-2.0` | nuspec | reads text of marked PDFs in tests (since 9.5 it is also a delivered package of `Keel.Documents`, see the delivered table) |
| axe-core (Deque Systems) | 4.10.2 | `MPL-2.0` | `tests/Keel.E2E/vendor/axe-core/VENDOR.md` and `LICENSE` | accessibility audit, injected through the DevTools protocol |

Evidence that the test-only items do not ship: the SBOM generator marks them out of scope and lists build tooling as `excluded`; `tests/Keel.E2E/vendor/axe-core/VENDOR.md` says "a test asserts both" (not referenced by `src/`, not copied into the image or bundle). The Dockerfile copies only `src`, `db/migrations`, two named input files and (demo stage) `tests/Keel.TestSupport` (see 3.7). **`tests/Keel.TestSupport` is copied into the demo build stage and the seeder links it**: it references `Npgsql` and the ASP.NET Core framework only (verified in its csproj); whether it contains other third-party code is **unverified**.

### 3.5 Build tooling of the vendored files (not shipped; verified in `package-lock.json`)

esbuild 0.25.12 (`MIT`) and its platform packages (`@esbuild/*`, `MIT`, optional, dev); `@napi-rs/canvas` 0.1.100 and platform packages (`MIT`, optional, pulled by pdfjs-dist, not delivered). Listed in the SBOM with scope `excluded`.

### 3.6 Container images (verified: pins in `deploy/images.env`, `Dockerfile`, compose files)

| Image | Pin | Role | Where it ships | Licence statement in the repo | Notes |
|---|---|---|---|---|---|
| `registry.access.redhat.com/ubi9/dotnet-100` | `latest@sha256:6938571c...` | build stage | not delivered (build only) | none | SDK stage of `Dockerfile` |
| `registry.access.redhat.com/ubi9/dotnet-100-aspnet` | `latest@sha256:1c10d6cf...` | **runtime base of the production image** | offline bundle (the Keel image is built on it) | none; ADR-0012/ADR-0020 only name UBI 9 | Red Hat UBI terms and the package licences of the base: **unverified** here; the legal-review packet lists this as open item 2 |
| `postgres:17-alpine` | `@sha256:266d3c25...` | database | offline bundle (`save_pinned postgres`); compose and chart dev values | none | Alpine userland packages are GPL/LGPL/MIT mixtures: **unverified** |
| `chrislusf/seaweedfs:4.47` | `@sha256:ce9e796f...` | S3 vault | offline bundle; compose; demo image takes only the `weed` binary | ADR-0045 and increment notes: Apache-2.0 | the Go dependencies compiled into `weed` are not inventoried: **unverified** |
| `caddy:2.10` | `@sha256:c3d7ee5d...` | TLS proxy of the compose example | offline bundle | ADR-0020: "permissive licence" (no SPDX id) | licence id **unverified** from the repo |
| `quay.io/keycloak/keycloak:26.4` | no digest | identity provider for dev and tests (`deploy/compose/compose.yaml`) | **not** in the offline bundle (the bundle saves postgres, vault, proxy: `scripts/package-offline.sh` lines 86-88); not in the demo image | none | licence **unverified** from the repo. The synthetic realm file `deploy/compose/keycloak/keel-realm.json` is Keel's own data, and the demo image embeds it |
| `clamav/clamav` (`KEEL_CLAMAV_IMAGE`, operator pins it) | not pinned in the repo | optional malware scanner | not in the bundle manifest | `deploy/compose/compose.clamav.yaml`: "Cisco Talos, GPL-2.0"; the compose file states it was **not run** in that increment | see 5.5 |
| `mcr.microsoft.com/dotnet/aspnet:10.0-noble` | tag, no digest (`DEMO_BASE_IMAGE`) | base of the **demo** image only | Cloud Run demo only | none | the repo calls it "Debian (noble)" (DEMO-8); `noble` is, in public naming, an Ubuntu release codename: engineering to confirm what the base really is. Installs `ca-certificates`, `curl`, `openssl`, `util-linux`, `postgresql-common`, `postgresql-17` (PGDG apt repository, key fetched at build time) |

Image SBOMs: `scripts/package-offline.sh` generates `sbom/image-*.cdx.json` only when `syft` is installed; otherwise the bundle says so. No image SBOM exists in the repository: package-level licences of the images are therefore **unverified** (the legal-review packet records the same).

### 3.7 Where each ships (summary)

| Delivery channel | Contents with third-party licences |
|---|---|
| Production image `keel/api` (`--target runtime`) | UBI 9 .NET aspnet base layers; the NuGet packages of 3.3; vendored viewer files (3.1); fonts (3.2); `/app/licenses` (four licence texts) |
| Offline bundle (`scripts/package-offline.sh`) | the image above, PostgreSQL, SeaweedFS and Caddy images, `sources/` (three pinned archives), `sbom/keel-plm.cdx.json`, `THIRD-PARTY-NOTICES.md`, `licenses/`, signed checksums, image SBOMs if `syft` was present, OS sources only with `--os-sources-dir` |
| Demo image (last `Dockerfile` stage, Cloud Run) | production application files, seeder, `weed`, PostgreSQL 17 from PGDG, Debian-family base packages. Never customer data (ADR-0045) |
| Source repository (GitHub, `origin` is `finepointcgi/Keel-PLM`) | Keel source, vendored viewer files, fonts, axe-core under `tests/` |

Whether the GitHub repository is public or private: **unverified** (not recorded in the repository).

## 4. Where the source and licence texts are (evidence)

| Item | Location |
|---|---|
| Source catalogue and SHA-256 pins | `deploy/offline/third-party-sources.json` (occt-import-js tag 0.0.23 `.tar.gz`, OCCT at the pinned commit, the npm package) |
| Fetch and verify | `scripts/offline-compliance.sh` |
| Bundle checks | `scripts/verify-offline.sh`, `keel-sbom verify` (`src/Keel.SbomTool/BundleVerifier.cs`) |
| Draft written offer | generated into `THIRD-PARTY-NOTICES.md`; template in `src/Keel.SbomTool/NoticesWriter.cs` ("Written offer and replacement") |
| LGPL texts | `docs/third-party/viewer-licenses/occt-import-js-LGPL-2.1.txt`, `opencascade-LGPL-2.1.txt` |
| Other licence texts | `three-MIT.txt`, `pdfjs-Apache-2.0.txt` (same folder), `src/Keel.Documents/Marking/Fonts/DejaVu-LICENSE.txt`, `tests/Keel.E2E/vendor/axe-core/LICENSE` |
| Engineering reading of the LGPL arrangement | ADR-0027 section 5 (VWR-12), ADR-0033 section 4, `docs/third-party/legal-review.md` |
| Checked by tests | `OfflineComplianceTests`, `ViewerCspAndAssetsTests` (`tests/Keel.UnitTests`) |

## 5. Copyleft and special-licence components in detail

### 5.1 occt-import-js and OpenCascade Technology (LGPL)

Facts (verified):
- The two files are described as unmodified upstream build output (`docs/third-party/viewer.md`); the shipped SHA-256 values are recorded; the npm tarball they came from is pinned (`occt-import-js-npm-package` in the catalogue) so a recipient can compare bytes.
- They are separate files, kept apart from Keel code, loaded by `src/Keel.Web/wwwroot/viewer/step-worker.js` through `importScripts` and WebAssembly, and used only by messages (ADR-0027 section 5; `viewer.md`).
- The worker runs under its own Content-Security-Policy: `script-src 'self' 'unsafe-eval' 'wasm-unsafe-eval'` (`src/Keel.Viewer/ViewerCsp.cs`, constant `StepWorker`). The application's global policy is not loosened (ADR-0027 consequences).
- Source in the bundle: occt-import-js source at tag 0.0.23, OCCT source at the commit that tag's submodule pins, and the npm package; each pinned by SHA-256; the build stops on a mismatch.
- Replacement path in the draft notices: place two replacement files over `/app/wwwroot/viewer/vendor/` through a volume or configMap. `manifest.json` is a repository control; the ADR states nothing at run time refuses a replaced file.
- Opt-out: `Keel:Viewer:EnableCadImport=false`, or delete the two files (`docs/third-party/viewer.md`).
- The written offer is a draft with the supplier contact as the placeholder `[Company]` and a three-year period (`NoticesWriter.cs`, `legal-review.md` item 3).
- The archives come from GitHub and npm URLs; GitHub generates the two `.tar.gz` files on request, so bytes could change (`legal-review.md` item 5).
- ADR-0027 states that occt-import-js "contains no cryptography": engineering claim, **unverified** (the binary was not inspected).

Not verified here: whether Keel's own files are treated as a "work that uses the library" rather than a combined work under the licence text; whether loading code into the customer's browser from the application's origin counts as distribution by the supplier or by the customer; whether an air-gapped customer needs more than the bundle offers.

### 5.2 Emscripten runtime libraries (unconfirmed entry)

`deploy/offline/third-party-sources.json`, `bundled[1]`: "Status: unconfirmed ... The binary was not inspected to confirm which of them it contains." `sourceId` is `null`. The catalogue states `MIT AND Apache-2.0 WITH LLVM-exception` and "no source offer is needed", which is a repository claim, **unverified**. The SBOM carries the entry as a component of kind bundled.

### 5.3 Operating-system packages in container images

`third-party-sources.json` (`osLayers.statement`) and `legal-review.md` item 2 say the images contain GPL and LGPL packages (examples named: glibc, bash, coreutils), that their source is published by the image publishers, and that it is **not** in the bundle unless the release engineer passes `--os-sources-dir`; without it `sources/SOURCES.txt` states `os-sources: NOT INCLUDED`. This was not produced or verified. The Dockerfile removes `microdnf`, `dnf`, `yum` and `curl` and clears setuid bits in the production image, but leaves bash and coreutils of the base (`Dockerfile`, `docs/ops/hardening.md`). The demo image adds Debian-family packages (3.6).

### 5.4 axe-core (MPL-2.0), test-only

`tests/Keel.E2E/vendor/axe-core/VENDOR.md`: version 4.10.2, `axe.min.js` unmodified (553,290 bytes), SHA-256 recorded, licence file unmodified, used only by `tests/Keel.E2E/AccessibilityTests.cs`, "never distributed with the product". The statement that no `src/` project references it and that it is not copied into the image or bundle is verified by the Dockerfile's `COPY` lines (no `tests/Keel.E2E`), and VENDOR.md says a test asserts it; that test was not located by name: **unverified** which test. The file is in the GitHub source tree under `tests/`, so the source repository itself carries MPL-2.0 text and file.

### 5.5 ClamAV (GPL-2.0), optional separate process

Verified: `deploy/compose/compose.clamav.yaml` defines an optional service from an operator-pinned image `clamav/clamav` (comment: "Cisco Talos, GPL-2.0"); Keel talks to it over the clamd INSTREAM protocol on TCP (`Keel__Vault__Scanning__Host`), implemented in Keel's own client (`ClamdScannerTests` uses a protocol-faithful stand-in). The image is **not** in `scripts/package-offline.sh` (only postgres, vault, proxy are saved). ClamAV is not linked into the Keel executables. Not run: the compose file header says the image was not pulled and the file was not started. The signature databases are loaded by the operator (air gap): their licence is **unverified**. Whether the combination counts as an aggregate of separate programs is a question for counsel. The demo image does not contain ClamAV (ADR-0045 decision 8).

### 5.6 DejaVu fonts

See 3.2. Notice condition and rename condition read from `DejaVu-LICENSE.txt`. The licence texts are in the offline bundle notices (ADR-0044 says "already in the offline bundle notices"); the SBOM run lists the two font files with licence id `Bitstream-Vera`.

### 5.7 PDFsharp (MIT)

`Directory.Packages.props` comment and ADR-0044 decision 2: PDFsharp 6.2.4, MIT, Core build, no GDI/WPF. ADR-0044 records why QuestPDF was rejected (conditional community licence, native Skia binaries); that comparison is the engineering team's statement about a third party's terms: **unverified**.

### 5.8 SeaweedFS, Keycloak, PostgreSQL, Caddy

- SeaweedFS: "Apache-2.0" in ADR-0045 and the compose and chart settings; it runs as a separate container (production) or a separate process started by the demo entrypoint; Keel reaches it only through the S3 API (ADR-0015). Verified usage; licence id taken from repo text, not from the image.
- Keycloak 26.4: used by `deploy/compose/compose.yaml` for dev and tests; the bundled synthetic realm; ADR-0007: "Dev/demo: a bundled Keycloak realm with synthetic users only". Not delivered in the bundle. Licence **unverified**.
- PostgreSQL 17: separate container (production) or process (demo). Npgsql's own licence id in the SBOM is `PostgreSQL`; the server image's licence is **unverified**.
- Caddy 2.10: separate container in the compose example. Licence id **unverified**.

## 6. What each licence requires, in one table (engineering reading; for counsel to decide)

| Component | Notice / attribution | Source offer | Relinking or replacement | Other |
|---|---|---|---|---|
| occt-import-js, OCCT (LGPL-2.1, OCCT exception) | licence text and notices in image and bundle (`/app/licenses`, `licenses/`) | source in `sources/` plus draft three-year written offer | two separate files replaceable by volume; manifest is a repo control only | `[Company]` contact placeholder; archive provenance |
| Emscripten runtime | unconfirmed | none expected (unconfirmed) | n/a | confirm binary contents |
| three.js, PDFsharp, ASP.NET Core packages, esbuild | MIT notice | none | none | none |
| pdf.js, AWSSDK, xunit.v3, PdfPig | Apache-2.0 text and any NOTICE | none | none | state changes if modified (none known) |
| Npgsql | PostgreSQL licence notice | none | none | |
| DejaVu fonts | Bitstream Vera notice | none | rename if modified | |
| axe-core (MPL-2.0) | licence file stays with the file | MPL source availability applies to distributed files; it is not distributed with the product (claim) | none | test-only |
| ClamAV (GPL-2.0) | by the operator's image | by the image publisher | none | separate process, optional |
| OS packages of images | by image publishers | **not provided** unless `--os-sources-dir` | none | open item |

## 7. Questions for counsel

Each is **for counsel to decide**. References point to evidence in section 4.

| # | Question | Context and references |
|---|---|---|
| Q1 | Does delivering occt-import-js and OCCT as two unmodified, separate files that run in the user's browser through a worker, with source in `sources/` and the offer in the notices, satisfy the LGPL for this product and the customer contract? Who is the distributor, the supplier or the customer, when the files are served from the customer's own server to the customer's own staff? | `docs/third-party/legal-review.md` item 1; ADR-0027 section 5; VWR-12 in `docs/increments/6.0-viewer.md` |
| Q2 | Is the draft written-offer wording acceptable (three years, "no more than the cost of physically performing the distribution", medium and contact), and what should replace `[Company]`? Should the offer wording sit in the contract, in the notices, or both? | `src/Keel.SbomTool/NoticesWriter.cs`; `legal-review.md` item 3 |
| Q3 | Is the replacement path sufficient (volume or configMap over `/app/wwwroot/viewer/vendor/`; `manifest.json` only guards the repository), or must the product offer a documented relinking or run-time check bypass? | ADR-0027 (consequence on replaceability); `docs/third-party/viewer.md` |
| Q4 | What is needed for the unconfirmed Emscripten entry: inspection of the binary by engineering, then reproduction of notices? Is a `MIT AND Apache-2.0 WITH LLVM-exception` reading adequate meanwhile? | `third-party-sources.json` `bundled[1]`; `legal-review.md` item 4 |
| Q5 | Container base images: is the delivery of the UBI 9 image layers, and the PostgreSQL (Alpine), SeaweedFS and Caddy images, acceptable without OS package sources? If sources are required, which (the `.src.rpm` set named by the image SBOM, others) and for what period? What are the Red Hat UBI terms for redistribution inside a customer bundle? | `legal-review.md` item 2; `osLayers` in the catalogue; `scripts/package-offline.sh` |
| Q6 | Demo image: it is built from a Microsoft base tag plus PGDG packages and published from a GitHub repository to Cloud Run. Does serving it publicly (or to invited users) raise any obligation beyond those in Q1 to Q5? Is the base image's name and nature as stated in the repo right? | ADR-0045; `Dockerfile` demo stage; DEMO-8 |
| Q7 | Keel's own licence: no licence file exists. What licence or terms should apply to the source, the images and the bundle, and how should the notice that "Keel code is not covered by the third-party licences" be worded? Does the repository's visibility (public or private, unverified) change this? | repository root has no `LICENSE`; ADR-0027 says Keel code "stays under our own terms" |
| Q8 | Is axe-core (MPL-2.0) in the GitHub tree under `tests/` acceptable as test-only, and does MPL file-level terms require anything for the source repository? | `tests/Keel.E2E/vendor/axe-core/VENDOR.md` |
| Q9 | Do the DejaVu glyphs embedded in generated marking PDFs and release packages need any notice in those PDFs? Are the font files unmodified? | 3.2; ADR-0044 decision 2 |
| Q10 | Test-only dependency set (xunit.v3, bunit, Testcontainers, Playwright and the browsers it downloads, PdfPig): any concerns because the tests live in the same source repository? | 3.4 |
| Q11 | Is a CycloneDX SBOM plus `THIRD-PARTY-NOTICES.md` an acceptable inventory format for customers? Should the notices also be copied into the application image (open item 7)? | `legal-review.md` item 7 |
| Q12 | Archive provenance: GitHub-generated tarballs and npm tarball as the source of record; should the supplier mirror them? | `legal-review.md` item 5 |

## 8. Unverified list (collected)

1. Licence of the .NET runtime and ASP.NET Core shared framework in the base images.
2. Package-level licences of every container image (no image SBOM in the repository).
3. Licence ids of Caddy, Keycloak, PostgreSQL image and Alpine layers; Red Hat UBI terms.
4. Contents of `occt-import-js.wasm` (Emscripten runtime libraries; "no cryptography" claim).
5. Whether the font files are unmodified; pdf.js NOTICE content.
6. Which unit test asserts that axe-core is not shipped.
7. Go dependencies compiled into `weed`; signature database terms for ClamAV; ClamAV image not run.
8. Whether `tests/Keel.TestSupport` contains third-party code beyond its two package references.
9. Visibility of the GitHub repository.
10. Licences of the browsers that Playwright downloads for E2E.
