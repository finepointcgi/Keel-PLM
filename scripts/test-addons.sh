#!/usr/bin/env bash
# Tests of the add-ons and connector tooling that are not .NET: the FreeCAD add-on (tools/freecad-keel), run with pytest in a throwaway virtual
# environment (nothing is installed globally). Not part of scripts/test.sh. No FreeCAD is needed: the add-on's logic has no FreeCAD import and is
# tested against a fake Keel server; the optional real-FreeCAD check runs only when FreeCADCmd is found (see docs/integration/freecad-addon.md).
#
#   scripts/test-addons.sh                      # venv in .venv-addons (created on first use)
#   KEEL_ADDON_VENV=/tmp/venv scripts/test-addons.sh
#   KEEL_ADDON_PIP_ARGS="--no-index --find-links /mirror" scripts/test-addons.sh     # air-gapped: install from a local wheel folder
#   KEEL_ADDON_PYTHON=python3.11 scripts/test-addons.sh
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$(pwd)"
VENV="${KEEL_ADDON_VENV:-$ROOT/.venv-addons}"
PY="${KEEL_ADDON_PYTHON:-python3}"

if [ ! -x "$VENV/bin/python" ]; then
  "$PY" -m venv "$VENV"
fi
# shellcheck disable=SC2086
"$VENV/bin/python" -m pip install --quiet --disable-pip-version-check ${KEEL_ADDON_PIP_ARGS:-} -r tools/freecad-keel/requirements-dev.txt

(cd tools/freecad-keel && "$VENV/bin/python" -m pytest)

# Optional: the same logic against a real FreeCAD document, only when FreeCAD is installed. Never downloaded here.
FC="$(command -v FreeCADCmd || command -v freecadcmd || true)"
if [ -z "$FC" ] && [ -x /Applications/FreeCAD.app/Contents/Resources/bin/FreeCADCmd ]; then FC=/Applications/FreeCAD.app/Contents/Resources/bin/FreeCADCmd; fi
if [ -n "$FC" ]; then
  echo "FreeCAD found at $FC: running tools/freecad-keel/scripts/freecad_headless_check.py"
  "$FC" tools/freecad-keel/scripts/freecad_headless_check.py
else
  echo "FreeCAD not installed: only the mock-based tests ran. The FreeCAD GUI layer and the object model are UNVERIFIED in a real FreeCAD."
fi
echo "add-on tests passed"
