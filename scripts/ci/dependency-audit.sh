#!/usr/bin/env bash
# Known-vulnerability audit of the dependencies (ADR-0059). CONNECTED machines only (CI): it asks nuget.org's advisory feed and npm's advisory
# endpoint. The delivered product never calls either; air-gapped sites scan the SBOM and the image archives with their own scanner
# (docs/ops/offline-install.md). Fails on any advisory of any severity in NuGet packages (transitive included) and on high or critical npm advisories.
#
#   scripts/ci/dependency-audit.sh [--nuget-only]
set -euo pipefail
cd "$(dirname "$0")/../.."
status=0
echo "== NuGet (dotnet list package --vulnerable --include-transitive)"
out=$(dotnet list Keel.slnx package --vulnerable --include-transitive 2>&1) || { echo "$out"; echo "FAIL: dotnet list package could not run" >&2; exit 2; }
if grep -q "has the following vulnerable packages" <<<"$out"; then
  grep -E "^\s+>|has the following vulnerable|Project " <<<"$out"
  echo "FAIL: vulnerable NuGet packages (update Directory.Packages.props or pin the patched version, as System.Security.Cryptography.Pkcs is)"; status=1
else
  if grep -qiE "unable to load|failed|error" <<<"$out"; then echo "$out" | tail -5; echo "FAIL: the advisory source could not be read (an unreachable feed must not look like a clean result)" >&2; exit 2; fi
  echo "ok   no known vulnerable NuGet packages"
fi
if [[ "${1:-}" != "--nuget-only" ]]; then
  echo "== npm (tools/viewer-vendor lock file; build tooling and the vendored viewer libraries)"
  if (cd tools/viewer-vendor && npm audit --package-lock-only --audit-level=high >/tmp/keel-npm-audit.$$ 2>&1); then echo "ok   no high or critical npm advisories"
  else cat /tmp/keel-npm-audit.$$; echo "FAIL: npm advisories at high or critical level"; status=1; fi
  rm -f /tmp/keel-npm-audit.$$
fi
exit $status
