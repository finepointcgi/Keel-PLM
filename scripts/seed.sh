#!/usr/bin/env bash
# Seeds synthetic data into a MIGRATED database (increment 1.7). Writes ONLY to an explicit target: set Keel__Database__AppConnectionString / pass --connection, or pass --use-dev-stack for the dev stack;
# override with Keel__Database__AppConnectionString / Keel__Vault__* or --connection. See docs/setup.md and docs/demo-script.md.
#   scripts/seed.sh --use-dev-stack      the ORCA demo scenario on the dev stack (fresh database only)
#   scripts/seed.sh --use-dev-stack --scenario scale --lines 50000 --levels 8    bulk structure for performance demos
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ $# -eq 0 ]]; then set -- --scenario demo; fi
dotnet run --project src/Keel.Seed -- "$@"
