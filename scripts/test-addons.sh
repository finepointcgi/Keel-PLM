#!/usr/bin/env bash
# Tests of the add-ons and connector tooling that are not .NET: the FreeCAD add-on (tools/freecad-keel), run with pytest in a throwaway virtual
# environment (nothing is installed globally). Not part of scripts/test.sh. No FreeCAD is needed for the pytest stage: the add-on's logic has no FreeCAD import
# and is tested against a fake Keel server; the real-FreeCAD stages at the end run only when FreeCAD is found (see docs/integration/freecad-addon.md).
#
#   scripts/test-addons.sh                      # venv in .venv-addons (created on first use)
#   KEEL_ADDON_VENV=/tmp/venv scripts/test-addons.sh
#   KEEL_ADDON_PIP_ARGS="--no-index --find-links /mirror" scripts/test-addons.sh     # air-gapped: install from a local wheel folder
#   KEEL_ADDON_PYTHON=python3.11 scripts/test-addons.sh
#   KEEL_FREECAD_CMD=/path/to/freecadcmd KEEL_FREECAD_GUI=/path/to/freecad scripts/test-addons.sh
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

# Optional stages in a real FreeCAD, only when it is installed (never downloaded; KEEL_FREECAD_CMD / KEEL_FREECAD_GUI override the search):
#  1. the extraction logic against real FreeCAD documents, headless (FreeCADCmd);
#  2. the GUI layer inside the real FreeCAD GUI on Qt's offscreen platform: the add-on is copied to a TEMPORARY user-data folder and loaded by FreeCAD itself
#     (Init.py, InitGui.py); commands, toolbar, menu, icons and the command objects are checked. HOME and FREECAD_USER_HOME point at that folder, so your own
#     FreeCAD configuration and documents are never touched.
# The add-on against a real Keel server (real Keycloak, real bearer tokens, real FreeCAD documents, the GUI commands) is the E2E class AddOnLiveTests:
#   KEEL_E2E=1 dotnet test --project tests/Keel.E2E --filter-class "*AddOnLiveTests"
BIN=/Applications/FreeCAD.app/Contents/Resources/bin
FC="${KEEL_FREECAD_CMD:-$(command -v FreeCADCmd || command -v freecadcmd || true)}"
if [ -z "$FC" ] && [ -x "$BIN/freecadcmd" ]; then FC="$BIN/freecadcmd"; fi
FCGUI="${KEEL_FREECAD_GUI:-$(command -v freecad || true)}"
if [ -z "$FCGUI" ] && [ -x "$BIN/freecad" ]; then FCGUI="$BIN/freecad"; fi

if [ -n "$FC" ]; then
  echo "FreeCAD found at $FC: running tools/freecad-keel/scripts/freecad_headless_check.py"
  # FreeCAD prints progress with carriage returns; the script's own verdict is the line that matters (the script exits 1 on failure).
  OUT="$("$FC" tools/freecad-keel/scripts/freecad_headless_check.py 2>&1 | tr '\r' '\n' | grep -v '%)' || true)"
  echo "$OUT" | grep 'freecad_headless_check' || true
  echo "$OUT" | grep -q 'freecad_headless_check: ok' || { echo "FreeCAD headless check FAILED:"; echo "$OUT"; exit 1; }
else
  echo "FreeCAD not installed: the FreeCAD object model is UNVERIFIED on this machine."
fi

if [ -n "$FCGUI" ]; then
  echo "FreeCAD GUI binary found at $FCGUI: running tools/freecad-keel/scripts/run_gui_check.py (offscreen, temporary user-data folder)"
  "$VENV/bin/python" tools/freecad-keel/scripts/run_gui_check.py --freecad "$FCGUI"
else
  echo "FreeCAD GUI binary not found: the GUI layer is UNVERIFIED on this machine."
fi
echo "add-on tests passed"
