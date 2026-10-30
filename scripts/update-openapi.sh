#!/usr/bin/env bash
# Regenerates docs/integration/openapi.json from the running host's /openapi/v1.json (through the integration test that compares them).
# Run after an intended change to the integration API, review the diff, and commit it with the change.
#
#   scripts/update-openapi.sh            regenerate every committed OpenAPI document
#   scripts/update-openapi.sh --check    drift check for CI: run the same comparison tests WITHOUT the update switch; exits non-zero (and the test output
#                                        shows the difference) when the running host's documents differ from docs/**/openapi.json. Changes nothing. Needs Docker.
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ "${1:-}" == "--check" ]]; then
  filters=()
  while read -r name; do filters+=(--filter-method "$name"); done < <(grep -E '^  --filter-method "' "$0" | sed 's/^  --filter-method "//; s/"$//')
  [[ ${#filters[@]} -ge 22 ]] || { echo "could not read the test names from $0" >&2; exit 2; }
  unset KEEL_UPDATE_OPENAPI
  exec dotnet test --project tests/Keel.IntegrationTests "${filters[@]}"
fi
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
# Manufacturing process data (plans, work instructions, shop floor view, tools) has its own document (docs/process/openapi.json).
KEEL_UPDATE_OPENAPI=1 dotnet test --project tests/Keel.IntegrationTests \
  --filter-method "Keel.IntegrationTests.ProcessApiTests.The_published_process_openapi_document_equals_the_committed_one_and_lists_nothing_else"
# Output and bulk work (8.9) has its own document (docs/reports/openapi.json).
KEEL_UPDATE_OPENAPI=1 dotnet test --project tests/Keel.IntegrationTests \
  --filter-method "Keel.IntegrationTests.ReportsTests.The_published_reports_openapi_document_equals_the_committed_one_and_lists_nothing_else"
git --no-pager diff --stat -- docs/reports/openapi.json || true
