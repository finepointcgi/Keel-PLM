#!/usr/bin/env bash
# End-to-end browser tests (increment 1.7). Starts its own throwaway PostgreSQL, SeaweedFS and Keycloak containers (tmpfs, random ports),
# seeds the ORCA demo data, starts the real Keel.Api host as a child process and drives it with Playwright (Chromium).
# Needs Docker. The first run downloads Chromium (Playwright's own installer); the product itself never needs the network.
# It never touches the dev stack (ports 5080, 18080, 55432, 18333). Failure traces and screenshots: TestResults/e2e/.
#   scripts/e2e.sh                          run everything
#   scripts/e2e.sh -- --filter-class "*HappyPathTests"   pass options through to the test runner
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build Keel.slnx -warnaserror
KEEL_E2E=1 dotnet test --project tests/Keel.E2E "$@"
