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
# The options and variants API has its own document (docs/variants/openapi.json).
KEEL_UPDATE_OPENAPI=1 dotnet test --project tests/Keel.IntegrationTests \
  --filter-method "Keel.IntegrationTests.VariantsApiTests.The_published_variants_openapi_document_equals_the_committed_one_and_lists_nothing_else"
# Records management (8.6) has its own document (docs/records/openapi.json).
KEEL_UPDATE_OPENAPI=1 dotnet test --project tests/Keel.IntegrationTests \
  --filter-method "Keel.IntegrationTests.RecordsApiTests.The_published_records_openapi_document_equals_the_committed_one_and_lists_no_other_surface"
git --no-pager diff --stat -- docs/records/openapi.json || true
# The quality API has its own document (docs/quality/openapi.json).
KEEL_UPDATE_OPENAPI=1 dotnet test --project tests/Keel.IntegrationTests \
  --filter-method "Keel.IntegrationTests.QualityApiTests.The_published_quality_openapi_document_equals_the_committed_one_and_lists_nothing_else"
git --no-pager diff --stat -- docs/quality/openapi.json || true
# The product compliance API has its own document (docs/compliance/openapi.json).
KEEL_UPDATE_OPENAPI=1 dotnet test --project tests/Keel.IntegrationTests \
  --filter-method "Keel.IntegrationTests.ComplianceApiTests.The_published_compliance_openapi_document_equals_the_committed_one_and_lists_no_other_surface"
git --no-pager diff --stat -- docs/compliance/openapi.json || true
KEEL_UPDATE_OPENAPI=1 dotnet test --project tests/Keel.IntegrationTests \
  --filter-method "Keel.IntegrationTests.RequirementsApiTests.The_published_requirements_openapi_document_equals_the_committed_one"
git --no-pager diff --stat -- docs/requirements/openapi.json || true
# The programs API has its own document (docs/programs/openapi.json).
KEEL_UPDATE_OPENAPI=1 dotnet test --project tests/Keel.IntegrationTests \
  --filter-method "Keel.IntegrationTests.ProgramApiTests.The_published_programs_openapi_document_equals_the_committed_one_and_lists_nothing_else"
git --no-pager diff --stat -- docs/programs/openapi.json || true
