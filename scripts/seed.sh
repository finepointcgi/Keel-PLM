#!/usr/bin/env bash
# Seeds synthetic data into a MIGRATED database (increment 1.7). Defaults to the dev stack (src/Keel.Api/appsettings.Development.json);
# override with Keel__Database__AppConnectionString / Keel__Vault__* or --connection. See docs/setup.md and docs/demo-script.md.
#   scripts/seed.sh                      the ORCA demo scenario (fresh database only)
#   scripts/seed.sh --scenario scale --lines 50000 --levels 8    bulk structure for performance demos
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ $# -eq 0 ]]; then set -- --scenario demo; fi
dotnet run --project src/Keel.Seed -- "$@"
