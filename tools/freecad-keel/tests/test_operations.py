import json
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest
from conftest import SCHEMA
from fakes import Doc, build_frame_model, part

from keel_plm import fcdoc, operations, workfolder
from keel_plm.client import KeelClient, KeelError
from keel_plm.structure import DrawingInfo, StructureError

ROOT = Path(__file__).resolve().parents[1]


def test_push_bom_sends_a_proposal_with_metadata_first_markings_of_the_top_document_and_a_valid_structure(package, client):
    doc, top = build_frame_model()
    ex = fcdoc.extract_structure(top)
    result = operations.push_bom(client, ex, "Frame from FreeCAD", markings_from="D-000001")
    assert result["number"] == "SP-000001"
    sent = package.proposals[0]
    assert sent["meta"]["title"] == "Frame from FreeCAD" and sent["meta"]["format"] == "json"
    assert sent["meta"]["rootNumber"] == "KA-3000"
    assert sent["meta"]["markings"]["distributionStatement"] == "A" and sent["meta"]["markings"]["owningOrg"] == "ACME"
    assert sent["structure"]["schema"] == "keel.cad.structure/v1"
    assert {(ln["parent"], ln["child"], ln["quantity"]) for ln in sent["structure"]["lines"]} >= {("KA-3000", "KA-3001", 2), ("KA-3001", "KF-2001", 4)}


def test_push_bom_can_refuse_unnumbered_parts(package, client):
    doc = Doc("D")
    top = part(doc, "Top", "T-1")
    from fakes import link

    top.contain(link(doc, "l", doc.add("PartDesign::Body", "Mystery", "Mystery")))
    ex = fcdoc.extract_structure(top)
    with pytest.raises(StructureError, match="no KeelPartNumber"):
        operations.push_bom(client, ex, "t", markings_from="D-000001", require_part_numbers=True)
    assert package.proposals == []
    operations.push_bom(client, ex, "t", markings_from="D-000001")  # allowed by default: proposed as a new part
    assert len(package.proposals) == 1


def test_a_server_refusal_of_the_proposal_is_reported(package, client):
    doc, top = build_frame_model()
    with pytest.raises(KeelError) as e:
        operations.push_bom(client, fcdoc.extract_structure(top), "t", markings_from="D-NOPE")
    assert e.value.status == 404


def test_summary_counts_exploded_totals():
    doc, top = build_frame_model()
    s = operations.summarize(fcdoc.extract_structure(top))
    assert (s.parts, s.lines) == (6, 6)
    assert s.totals["KF-2001"] == 10 and s.totals["KF-2002"] == 8


def test_check_in_references_map_files_to_document_numbers(package, client, tmp_path):
    folder = tmp_path / "pkg"
    state = workfolder.checkout(client, "D-000001", folder)
    refs = operations.references_for_checkin(state, {
        "frame.pdf": ["frame.step", "elsewhere.FCStd"],  # a drawing built against the model; a file outside the package is ignored
        "frame.step": ["bracket.step", "frame.step"],  # a model referencing its part; never itself
        "unknown.step": ["bracket.step"],  # not in the package at all
    })
    assert refs == {"D-000003": ["D-000001"], "D-000001": ["D-000002"]}


def test_associations_are_planned_from_the_files_and_pages_and_applied_idempotently(package, client, tmp_path):
    folder = tmp_path / "pkg"
    state = workfolder.checkout(client, "D-000001", folder)
    drawings = [DrawingInfo("Sheet 1", "frame.pdf", ["KA-3000", "KP-1001"], ["Loose box"]), DrawingInfo("Sheet 2", "elsewhere.FCStd", ["KP-1002"])]
    plan = operations.plan_associations(state, "frame.step", "KA-3000", drawings)
    assert plan.items == [("D-000001", "KA-3000", "model-of"), ("D-000003", "KA-3000", "drawing-of"), ("D-000003", "KP-1001", "drawing-of")]
    assert any("Loose box" in u for u in plan.unresolved) and any("elsewhere.FCStd" in u for u in plan.unresolved)
    first = operations.apply_associations(client, plan)
    assert len(first.created) == 3 and not first.existing and not first.failed
    again = operations.apply_associations(client, plan)
    assert len(again.existing) == 3 and not again.created  # the server's 409 means "already recorded"
    bad = operations.plan_associations(state, "frame.step", "MISSING-1", [])
    report = operations.apply_associations(client, bad)
    assert len(report.failed) == 1 and "MISSING-1" in report.failed[0][1]


def test_status_shows_local_changes_lock_owners_and_stale_drawings(package, client, tmp_path):
    folder = tmp_path / "pkg"
    workfolder.checkout(client, "D-000001", folder)
    (folder / "frame.pdf").write_bytes(b"edited")
    package.stale_numbers = ["D-000003"]
    r = operations.status(client, folder)
    assert r.top == "D-000001" and r.stale_count == 1 and r.stale[0]["number"] == "D-000003"
    by = {m.number: m for m in r.members}
    assert by["D-000003"].changed == ["frame.pdf"] and by["D-000001"].changed == []
    assert all(m.locked_by == "alice" and m.mine for m in r.members)
    package.docs["D-000002"].locked_by = "bob"
    assert operations.lock_owner(client, "D-000002") == ("bob", False)
    package.docs["D-000002"].locked_by = None
    assert operations.lock_owner(client, "D-000002") == (None, False)


def test_find_folder_walks_up_to_the_package_folder(package, client, tmp_path):
    folder = tmp_path / "pkg"
    workfolder.checkout(client, "D-000001", folder)
    assert operations.find_folder(folder / "frame.step") == folder.resolve()
    assert operations.find_folder(tmp_path / "other.FCStd") is None


# ------------------------------------------------------------------ layout of the add-on itself

def test_package_xml_names_the_workbench_class_that_InitGui_defines():
    pkg = ET.parse(ROOT / "package.xml").getroot()
    ns = {"p": "https://wiki.freecad.org/Package_Metadata"}
    cls = pkg.find("p:content/p:workbench/p:classname", ns).text
    assert f"class {cls}(" in (ROOT / "InitGui.py").read_text()
    assert (ROOT / "Init.py").exists()


def test_every_command_has_an_icon_and_every_python_file_compiles():
    gui = (ROOT / "keel_plm" / "gui.py").read_text()
    import re

    icons = set(re.findall(r'icon = "([a-z]+)"|, "([a-z]+)"\n', gui))
    names = {a or b for a, b in icons} | {"keel"}
    for n in names:
        assert (ROOT / "resources" / "icons" / f"{n}.svg").exists(), n
    for f in [*ROOT.glob("*.py"), *(ROOT / "keel_plm").glob("*.py")]:
        compile(f.read_text(), str(f), "exec")  # syntax check, including gui.py and InitGui.py that need FreeCAD to run


def test_only_gui_py_imports_freecad():
    for f in (ROOT / "keel_plm").glob("*.py"):
        text = f.read_text()
        imports_fc = any(line.startswith(("import FreeCAD", "from FreeCAD", "from PySide", "import PySide")) for line in text.splitlines())
        assert imports_fc == (f.name == "gui.py"), f.name


def test_the_addon_only_talks_to_the_configured_server():
    # air-gapped runtime: the only network code is client.py, and it never builds a URL from anything but the configured base URL
    text = (ROOT / "keel_plm" / "client.py").read_text()
    assert text.count("urllib.request.Request(") == 1 and "self.base_url + path" in text
    for f in (ROOT / "keel_plm").glob("*.py"):
        if f.name != "client.py":
            assert "urllib.request" not in f.read_text() and "http.client" not in f.read_text() and "socket" not in f.read_text(), f.name
