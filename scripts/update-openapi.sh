#!/usr/bin/env bash
# Regenerates docs/integration/openapi.json from the running host's /openapi/v1.json (through the integration test that compares them).
# Run after an intended change to the integration API, review the diff, and commit it with the change.
set -euo pipefail
cd "$(dirname "$0")/.."
KEEL_UPDATE_OPENAPI=1 dotnet test --project tests/Keel.IntegrationTests \
  --filter-method "Keel.IntegrationTests.IntegrationApiTests.The_published_openapi_document_equals_the_committed_one_and_is_3_1"
# The CAD connector API has its own document (docs/integration/cad-openapi.json).
KEEL_UPDATE_OPENAPI=1 dotnet test --project tests/Keel.IntegrationTests \
  --filter-method "Keel.IntegrationTests.CadApiTests.The_cad_openapi_document_equals_the_committed_one_and_is_3_1"
git --no-pager diff --stat -- docs/integration/cad-openapi.json || true
KEEL_UPDATE_OPENAPI=1 dotnet test --project tests/Keel.IntegrationTests \
  --filter-method "Keel.IntegrationTests.SourcingApiTests.The_published_sourcing_openapi_document_equals_the_committed_one_and_lists_no_internal_surface"
git --no-pager diff --stat -- docs/integration/openapi.json docs/sourcing/openapi.json || true
# Records management (8.6) has its own document (docs/records/openapi.json).
KEEL_UPDATE_OPENAPI=1 dotnet test --project tests/Keel.IntegrationTests \
  --filter-method "Keel.IntegrationTests.RecordsApiTests.The_published_records_openapi_document_equals_the_committed_one_and_lists_no_other_surface"
git --no-pager diff --stat -- docs/records/openapi.json || true
