# Continuous integration

Decision record: [ADR-0059](../adr/0059-ci-and-supply-chain.md). The pipeline files are for GitHub Actions (the vendor's or the customer's GitHub environment). Nothing in them runs on a customer site and the application makes no external call.

## What runs when
| Trigger | Workflow | Content | Time (expected) |
|---|---|---|---|
| Pull request | `pr.yml` -> `fast.yml` | build with `-warnaserror`; unit tests; integration tests (Docker); OpenAPI drift; Markdown links; secret scan; workflow lint; licence inventory and policy; dependency advisories; SBOM; Helm chart and compose bundle checks | about 15 min, jobs in parallel |
| Pull request touching `src/Keel.Web/**` or the axe test | `axe.yml` -> `e2e.yml` | the accessibility class only (light, dark, high contrast, 375 px) | about 10 min |
| Push to `main` | `main.yml` | the fast set, the whole browser suite (`e2e.yml`, about 19 min, includes axe), the production image and the compose bundle smoke test (`image.yml`) | about 45 min |
| Nightly 02:17 UTC and manual | `nightly.yml` | everything `main.yml` runs, plus the operations proof (`drills.yml`: backup and restore, upgrade rehearsal, load smoke) | about 1.5 h |

The expected times are estimates for GitHub-hosted `ubuntu-24.04` runners; the workflows have not been run on GitHub yet, so no run time is recorded here. The 19 minutes of the browser suite is a measurement from the developer machine (docs/CLAUDE.md).

## The checks are scripts
Each runs the same locally and in CI, so a failure can be reproduced with one command.

| Check | Command | Needs |
|---|---|---|
| Build and unit | `dotnet build Keel.slnx -warnaserror && dotnet test --project tests/Keel.UnitTests` | .NET SDK from `global.json` |
| Integration | `dotnet test --project tests/Keel.IntegrationTests` | Docker |
| OpenAPI drift | `scripts/update-openapi.sh --check` (regenerate after an intended change with `scripts/update-openapi.sh`) | Docker |
| Browser suite | `KEEL_E2E=1 dotnet test --project tests/Keel.E2E` (`scripts/e2e.sh`); axe only: add `--filter-class "*AccessibilityTests"` | Docker, Chromium (cached in CI by the Playwright version in `Directory.Packages.props`) |
| Markdown links | `scripts/ci/check-md-links.py` (self-test: `scripts/ci/test-check-md-links.sh`) | python3 only |
| Secret scan | `scripts/ci/secret-scan.py [--history]` (self-test: `--self-test`) | python3, git |
| Workflow lint | `scripts/ci/lint-workflows.rb` (self-test: `scripts/ci/test-lint-workflows.sh`) | ruby |
| Licence inventory and policy | `scripts/generate-sbom.sh --check`; regenerate with `scripts/generate-sbom.sh --licenses` | .NET SDK |
| SBOM | `scripts/generate-sbom.sh --out sbom-out --version X` | .NET SDK, jq |
| Dependency advisories | `scripts/ci/dependency-audit.sh` | network (nuget.org advisory feed, npm) |
| Helm | `scripts/verify-helm.sh` | helm |
| Compose bundle and preflight | `scripts/verify-packaging.sh` | docker (config only), openssl, jq |
| Compose smoke | `KEEL_APP_IMAGE=... scripts/verify-compose-example.sh` | Docker, the production image |
| Operations proof | `scripts/ops/backup-restore-drill.sh`, `scripts/ops/upgrade-rehearsal.sh`, `scripts/ops/load-smoke.sh` | Docker, the production image; see [drill-results](drill-results/) |

Run suites one at a time on a shared machine: the integration and browser suites and every drill start their own containers.

## What the checks enforce
- **Secrets:** private keys, cloud and source-host tokens, JWTs and passwords in connection strings or assignments in tracked files and in the history. Development-only credentials (the dev compose stack, the synthetic realm) are listed with a reason in `.secret-scan-allowlist`; an entry that no longer matches fails the scan, so the list cannot rot.
- **Licences:** `THIRD-PARTY-LICENSES.md` is generated and committed. A new NuGet package, npm package, vendored file, font, image pin or Python requirement without a regenerated file fails `ThirdPartyLicensesTests` and `keel-sbom licenses --check`. Policy: every component declares a licence; AGPL, SSPL, BUSL, non-commercial and plain GPL are refused; delivered copyleft needs a pinned source archive.
- **Supply chain of the pipeline itself:** every action is pinned to a commit SHA, workflows have read-only permissions and no `pull_request_target`, no expression from the event payload is interpolated into a script. `scripts/ci/lint-workflows.rb` checks this together with missing scripts, unknown `needs` and undeclared reusable inputs.
- **Documentation:** relative links, images and anchors in `docs/`, the manual, `deploy/` and the root documents. External links are not fetched (air-gapped product; CI must not depend on third-party availability).

## What was and was not validated
- Every script above was run locally (results in the increment notes). The workflow files were checked with the in-repo linter, **not with actionlint** (not installed, not downloaded) and were not run on GitHub: a first run may need small fixes (for example the Playwright system libraries step, or the `pwsh` path).
- `dependency-audit.sh` reads advisory feeds; its result changes without a commit, which is why it also runs nightly.
- Pinned action SHAs were resolved on 2026-10-30 from the major-version tags (`actions/checkout`, `setup-dotnet`, `cache`, `upload-artifact`, all v4). Refresh them deliberately and re-run the linter.

## Adding a dependency
1. Add it. 2. `scripts/generate-sbom.sh --licenses`. 3. Read the diff of `THIRD-PARTY-LICENSES.md` (the licence is now in front of the reviewer). 4. If it is copyleft, add its source archive to `deploy/offline/third-party-sources.json` and record the decision in `docs/review/legal-open-source.md` (status of that packet stays with counsel). 5. Commit both.
