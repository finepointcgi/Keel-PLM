"""Steps of the live check of the add-on against a REAL Keel server (tests/Keel.E2E/AddOnLiveTests.cs runs them). Not a test by itself: the E2E test
creates the server, the documents and the token, calls one step at a time and asserts what the server holds after each.

It runs under plain Python (the steps that need no FreeCAD) and under FreeCAD's own Python (``freecadcmd``, the steps that open FreeCAD documents), so the
add-on's Python client is exercised in both interpreters. Arguments come from environment variables (``freecadcmd`` has no reliable argv):

    KEEL_LIVE_CMD      build | edit | checkout | status | checkin | undo | push
    KEEL_LIVE_SERVER   base URL of the host (http://localhost:PORT; loopback http is allowed by the add-on)
    KEEL_CAD_TOKEN     the bearer token (the add-on's own mechanism, keel_plm.auth)
    KEEL_LIVE_RESULT   file the step writes its JSON result to ({"ok": true, ...} or {"ok": false, "error": ..., "code": ...})
    KEEL_LIVE_FOLDER   work folder of the step;  KEEL_LIVE_TOP  top document number;  KEEL_LIVE_COMMENT  check-in comment
    KEEL_LIVE_OUT      build: folder to write the FreeCAD files to
    KEEL_LIVE_DECLARE  checkin under FreeCAD: "1" declares references from the open documents (the add-on's behaviour), "0" leaves them out

``build``/``edit``/``push`` and ``checkin`` with KEEL_LIVE_DECLARE=1 need FreeCAD.
"""
import hashlib
import json
import os
import sys
import traceback
from pathlib import Path

sys.path.insert(0, str(Path(os.path.abspath(__file__ if "__file__" in globals() else os.getcwd())).parent.parent))

from keel_plm import auth, fcdoc, operations, settings as settings_mod, workfolder  # noqa: E402
from keel_plm.client import KeelClient, KeelError  # noqa: E402
from keel_plm.structure import build_payload, explode, validate_payload  # noqa: E402

try:
    import FreeCAD  # type: ignore[import-not-found]
except ImportError:
    FreeCAD = None


def env(name, default=None):
    value = os.environ.get(name, default)
    if value is None:
        raise SystemExit(f"{name} is not set")
    return value


def client():
    s = settings_mod.Settings(server_url=env("KEEL_LIVE_SERVER"))
    return KeelClient.from_settings(s, lambda: auth.read_token(s))  # token from KEEL_CAD_TOKEN


def numbered(doc, kind, name, number):
    obj = doc.addObject(kind, name)
    obj.addProperty("App::PropertyString", "KeelPartNumber", "Keel", "Keel part number")
    obj.KeelPartNumber = number
    return obj


def link(doc, container, target, name, count=1):
    lk = doc.addObject("App::Link", name)
    lk.setLink(target)
    if count > 1:
        lk.ElementCount = count
    container.addObject(lk)
    return lk


# The same structure as the STEP fixture frame-asm-ap242.step (tests/Keel.UnitTests/Fixtures/cad): Frame KA-3000 holds 2 x bracket assembly KA-3001,
# 1 x plate KP-1002 and 2 x screw KF-2001; KA-3001 holds 1 x bracket KP-1001, 4 x screw KF-2001 and 4 x nut KF-2002.
FRAME = {("KA-3000", "KA-3001"): 2, ("KA-3000", "KP-1002"): 1, ("KA-3000", "KF-2001"): 2,
         ("KA-3001", "KP-1001"): 1, ("KA-3001", "KF-2001"): 4, ("KA-3001", "KF-2002"): 4}


def step_build(out: Path):
    out.mkdir(parents=True, exist_ok=True)
    out = out.resolve()  # a symlinked folder (macOS /var -> /private/var) makes FreeCAD store a ../../..-style path in the drawing's external link
    model_path, drawing_path = out / "frame.FCStd", out / "frame-drw.FCStd"
    doc = FreeCAD.newDocument("Frame")
    bracket = numbered(doc, "PartDesign::Body", "Bracket", os.environ.get("KEEL_LIVE_BRACKET_NUMBER", "KP-1001"))
    plate = numbered(doc, "PartDesign::Body", "Plate", "KP-1002")
    screw = numbered(doc, "PartDesign::Body", "Screw", "KF-2001")
    nut = numbered(doc, "PartDesign::Body", "Nut", "KF-2002")
    sub = numbered(doc, "App::Part", "BracketAsm", "KA-3001")
    link(doc, sub, bracket, "L_bracket")
    link(doc, sub, screw, "L_screw", 4)
    link(doc, sub, nut, "L_nut", 4)
    top = numbered(doc, "App::Part", "FrameAsm", "KA-3000")
    for i in range(2):
        link(doc, top, sub, f"L_sub{i}")
    link(doc, top, plate, "L_plate")
    link(doc, top, screw, "L_screw2", 2)
    doc.recompute()
    doc.saveAs(str(model_path))
    first = hashlib.sha256(model_path.read_bytes()).hexdigest()
    doc.save()  # OSS-CAD-7: does saving an unchanged document change the file?
    resave_changes_bytes = hashlib.sha256(model_path.read_bytes()).hexdigest() != first
    # A drawing in its own file: a TechDraw page whose view shows the bracket through an external App::Link (the link must exist before TechDraw can
    # use a part of another file; saving the drawing first is what lets FreeCAD store the file reference).
    drw = FreeCAD.newDocument("FrameDrw")
    drw.saveAs(str(drawing_path))
    page = drw.addObject("TechDraw::DrawPage", "Page")
    page.Template = drw.addObject("TechDraw::DrawSVGTemplate", "Template")
    view = drw.addObject("TechDraw::DrawViewPart", "View")
    page.addView(view)
    ext = drw.addObject("App::Link", "BracketLink")
    ext.setLink(bracket)
    view.Source = [ext]
    drw.recompute()
    drw.save()
    return {"model": model_path.name, "drawing": drawing_path.name, "resaveChangesBytes": resave_changes_bytes, "drawings": [(d.page, d.part_numbers, d.source_files) for d in fcdoc.find_drawings(drw)],
            "dependencies": fcdoc.document_dependencies(drw)}


def step_edit(folder: Path):
    """What a user does after the check-out: open the model, change it (the screws of the frame go from 2 to 3), save. The drawing is left alone."""
    doc = FreeCAD.openDocument(str(folder / "frame.FCStd"))
    lk = doc.getObject("L_screw2")
    lk.ElementCount = 3
    doc.recompute()
    doc.save()
    roots = fcdoc.root_candidates(doc)
    ex = fcdoc.extract_structure(roots[0])
    return {"roots": [r.Name for r in roots], "lines": sorted(((ex.part(l.parent).number, ex.part(l.child).number, l.quantity) for l in ex.lines))}


def open_folder_documents(folder: Path):
    docs = []
    for path in workfolder.openable_files(folder):
        docs.append(FreeCAD.openDocument(str(path)))
    return docs


def step_checkin(c, folder: Path, comment: str, declare: bool):
    refs = None
    if declare:
        open_folder_documents(folder)
        deps = {os.path.basename(d.FileName): fcdoc.document_dependencies(d) for d in FreeCAD.listDocuments().values() if d.FileName}
        refs = operations.references_for_checkin(workfolder.load_state(folder), deps)
    result = workfolder.checkin(c, folder, comment, False, refs)
    return {"created": result.get("created", []), "released": result.get("released", []), "setClosed": result.get("setClosed"), "references": refs}


def step_push(c, folder: Path):
    """The add-on's Push BOM: open the checked-out model, take its single top part, extract, build the payload (validated against the schema)."""
    docs = open_folder_documents(folder)
    doc = next(d for d in docs if os.path.basename(d.FileName) == "frame.FCStd")
    roots = fcdoc.root_candidates(doc)
    assert [r.Name for r in roots] == ["FrameAsm"], [r.Name for r in roots]
    ex = fcdoc.extract_structure(roots[0])
    summary = operations.summarize(ex)
    validate_payload(build_payload(ex))
    state = workfolder.load_state(folder)
    proposal = operations.push_bom(c, ex, "Frame from FreeCAD", markings_from=state.top, require_part_numbers=True, tool=f"FreeCAD {'.'.join(FreeCAD.Version()[:3])}")
    return {"proposal": proposal["number"], "parts": summary.parts, "lines": summary.lines, "totals": summary.totals, "warnings": summary.warnings}


def step_status(c, folder: Path):
    report = operations.status(c, folder)
    return {"top": report.top, "banner": report.banner, "staleCount": report.stale_count, "stale": report.stale,
            "members": [{"number": m.number, "role": m.role, "files": m.files, "changed": m.changed, "lockedBy": m.locked_by, "mine": m.mine} for m in report.members]}


def run():
    cmd = env("KEEL_LIVE_CMD")
    folder = Path(env("KEEL_LIVE_FOLDER", "")) if os.environ.get("KEEL_LIVE_FOLDER") else None
    if cmd == "build":
        return step_build(Path(env("KEEL_LIVE_OUT")))
    if cmd == "edit":
        return step_edit(folder)
    c = client()
    if cmd == "checkout":
        state = workfolder.checkout(c, env("KEEL_LIVE_TOP"), folder)
        return {"checkoutId": state.checkout_id, "members": [{"number": m.number, "role": m.role, "files": [(f.file_name, f.sha256) for f in m.files]} for m in state.members]}
    if cmd == "status":
        return step_status(c, folder)
    if cmd == "undo":
        workfolder.undo(c, folder)
        return {"undone": True, "stateRemoved": not workfolder.has_state(folder)}
    if cmd == "checkin":
        return step_checkin(c, folder, os.environ.get("KEEL_LIVE_COMMENT", "live check"), os.environ.get("KEEL_LIVE_DECLARE", "0") == "1")
    if cmd == "push":
        return step_push(c, folder)
    raise SystemExit(f"unknown KEEL_LIVE_CMD {cmd}")


def main():
    result_path = Path(env("KEEL_LIVE_RESULT"))
    try:
        result = {"ok": True, "freecad": ".".join(FreeCAD.Version()[:3]) if FreeCAD else None, **run()}
    except KeelError as e:
        result = {"ok": False, "error": str(e), "code": e.code, "status": e.status, "trace": traceback.format_exc()}
    except BaseException as e:  # noqa: BLE001 - the result file is how the caller learns what happened
        result = {"ok": False, "error": f"{type(e).__name__}: {e}", "trace": traceback.format_exc()}
    result_path.write_text(json.dumps(result, default=str), encoding="utf-8")


main()
