#!/usr/bin/env bash
# CI entry point: builds everything and runs all test suites. Requires Docker (Testcontainers) and Node 22+.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build Keel.slnx -warnaserror
dotnet test --project tests/Keel.UnitTests
dotnet test --project tests/Keel.IntegrationTests
(cd web && npm ci --no-audit --no-fund && npm run build && npm test)
echo "all tests passed"
