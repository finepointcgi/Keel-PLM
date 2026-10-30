"""Installs the add-on into a TEMPORARY FreeCAD user-data folder and runs ``freecad_gui_check.py`` inside the real FreeCAD GUI (Qt offscreen platform).

    python3 tools/freecad-keel/scripts/run_gui_check.py [--freecad /path/to/freecad]

The user's own FreeCAD configuration is never read or written: HOME and FREECAD_USER_HOME point at a temporary folder for the child process, and the package is
copied (as FreeCAD's add-on manager would) to ``<temp>/Mod/Keel``. Exit code 0 when every check passed, 1 when one failed, 2 when FreeCAD is not found
or did not finish. Prints the checks that failed (or a count when all passed). The environment is passed through, so the live variant (KEEL_GUI_LIVE=1,
see freecad_gui_check.py) uses the same launcher.
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
PACKAGE = HERE.parent


def find_freecad(explicit=None):
    candidates = [explicit, os.environ.get("KEEL_FREECAD_GUI"), "/Applications/FreeCAD.app/Contents/Resources/bin/freecad", shutil.which("freecad"), shutil.which("FreeCAD")]
    return next((c for c in candidates if c and Path(c).exists()), None)


def main(argv):
    explicit = argv[argv.index("--freecad") + 1] if "--freecad" in argv else None
    binary = find_freecad(explicit)
    if binary is None:
        print("FreeCAD GUI binary not found (set KEEL_FREECAD_GUI or pass --freecad): the GUI layer was NOT verified.")
        return 2
    home = Path(os.environ.get("KEEL_GUI_HOME") or tempfile.mkdtemp(prefix="keel-fc-gui-"))
    keep = bool(os.environ.get("KEEL_GUI_HOME"))
    try:
        mod = home / "Mod" / "Keel"
        shutil.copytree(PACKAGE, mod, ignore=shutil.ignore_patterns("__pycache__", ".pytest_cache", "*.pyc"), dirs_exist_ok=True)
        result = home / "gui-result.json"
        result.unlink(missing_ok=True)
        env = dict(os.environ, HOME=str(home), FREECAD_USER_HOME=str(home), QT_QPA_PLATFORM="offscreen", KEEL_GUI_RESULT=str(result))
        try:
            subprocess.run([binary, str(HERE / "freecad_gui_check.py")], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=float(os.environ.get("KEEL_GUI_TIMEOUT", "240")), check=False)
        except subprocess.TimeoutExpired:
            print("FreeCAD did not finish within the timeout.")
            return 2
        if not result.exists():
            print("FreeCAD wrote no result (the check script did not run).")
            return 2
        data = json.loads(result.read_text(encoding="utf-8"))
        if "error" in data:
            print("ERROR", data["error"])
            print(data.get("trace", ""))
        for failure in data["failures"]:
            print("FAILED", failure)
        if data["ok"]:
            print(f"freecad_gui_check: ok, {len(data['checks'])} checks in FreeCAD {data['info'].get('freecad')} (offscreen GUI)")
        keep_to = os.environ.get("KEEL_GUI_RESULT_COPY")
        if keep_to:
            Path(keep_to).write_text(json.dumps(data, indent=1), encoding="utf-8")
        return 0 if data["ok"] else 1
    finally:
        if not keep:
            shutil.rmtree(home, ignore_errors=True)


sys.exit(main(sys.argv[1:]))
