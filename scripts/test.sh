#!/usr/bin/env bash
# CI entry point: builds everything and runs all test suites. Requires Docker (Testcontainers).
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build Keel.slnx -warnaserror
dotnet test --project tests/Keel.UnitTests
dotnet test --project tests/Keel.IntegrationTests
echo "all tests passed"
