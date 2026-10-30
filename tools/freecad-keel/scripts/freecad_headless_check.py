"""Optional check of the add-on's extraction logic against a REAL FreeCAD document. Run with FreeCAD's command-line binary:

    FreeCADCmd tools/freecad-keel/scripts/freecad_headless_check.py

It builds a three-level assembly (App::Part with App::Link objects, a link array, a body), saves two files, and asserts what keel_plm.fcdoc reads from it.
It needs no Keel server. NOT YET RUN by its authors: FreeCAD was not installed where it was written (docs/increments/9.2-open-source-cad.md, OSS-CAD-6).
If FreeCAD's object model differs from what fcdoc assumes (property names of links or TechDraw), this script is where that shows up."""
import os
import sys
import tempfile

import FreeCAD  # type: ignore[import-not-found]

HERE = os.path.dirname(os.path.abspath(__file__)) if "__file__" in globals() else os.getcwd()
sys.path.insert(0, os.path.dirname(HERE))

from keel_plm import fcdoc  # noqa: E402
from keel_plm.structure import build_payload, validate_payload  # noqa: E402


def numbered(obj, number):
    obj.addProperty("App::PropertyString", "KeelPartNumber", "Keel", "Keel part number")
    obj.KeelPartNumber = number
    return obj


def main():
    work = tempfile.mkdtemp(prefix="keel-fc-")
    doc = FreeCAD.newDocument("Frame")
    screw = numbered(doc.addObject("PartDesign::Body", "Screw"), "KF-2001")
    bracket = numbered(doc.addObject("PartDesign::Body", "Bracket"), "KP-1001")
    sub = numbered(doc.addObject("App::Part", "BracketAsm"), "KA-3001")
    for target, count in ((bracket, 1), (screw, 4)):
        link = doc.addObject("App::Link", "L_" + target.Name)
        link.setLink(target)
        if count > 1:
            link.ElementCount = count
        sub.addObject(link)
    top = numbered(doc.addObject("App::Part", "FrameAsm"), "KA-3000")
    for i in range(2):
        link = doc.addObject("App::Link", f"L_sub{i}")
        link.setLink(sub)
        top.addObject(link)
    doc.recompute()
    doc.saveAs(os.path.join(work, "frame.FCStd"))

    roots = fcdoc.root_candidates(doc)
    assert [o.Name for o in roots] == ["FrameAsm"], [o.Name for o in roots]
    ex = fcdoc.extract_structure(roots[0])
    got = {(ex.part(l.parent).number, ex.part(l.child).number): l.quantity for l in ex.lines}
    expected = {("KA-3000", "KA-3001"): 2, ("KA-3001", "KP-1001"): 1, ("KA-3001", "KF-2001"): 4}
    assert got == expected, got
    validate_payload(build_payload(ex))
    print("freecad_headless_check: ok", FreeCAD.Version()[:3], got)


main()
