import json

import pytest
from conftest import SCHEMA
from fakes import Doc, Obj, build_frame_model, link, part

from keel_plm import fcdoc
from keel_plm.structure import Extraction, LineInfo, PartInfo, StructureError, build_payload, explode, validate_payload

jsonschema = pytest.importorskip("jsonschema")


def lines_by_number(ex: Extraction) -> dict[tuple[str, str], int]:
    num = {p.ref: (p.number or p.name) for p in ex.parts}
    return {(num[ln.parent], num[ln.child]): ln.quantity for ln in ex.lines}


def test_the_three_level_assembly_has_the_quantities_of_the_step_fixture():
    # the same model as tests/Keel.UnitTests/Fixtures/cad/frame-asm-*.step, whose quantities the .NET CadStepFixtureTests assert
    doc, top = build_frame_model()
    ex = fcdoc.extract_structure(top)
    assert ex.root == "KA-3000"
    assert lines_by_number(ex) == {
        ("KA-3000", "KA-3001"): 2, ("KA-3000", "KP-1002"): 1, ("KA-3000", "KF-2001"): 2,
        ("KA-3001", "KP-1001"): 1, ("KA-3001", "KF-2001"): 4, ("KA-3001", "KF-2002"): 4}
    assert ex.warnings == []
    totals = {p.number: explode(ex).get(p.ref) for p in ex.parts}
    assert totals["KF-2001"] == 10 and totals["KF-2002"] == 8


def test_the_payload_is_valid_against_the_published_schema():
    doc, top = build_frame_model()
    payload = build_payload(fcdoc.extract_structure(top))
    jsonschema.validate(payload, json.loads(SCHEMA.read_text()))
    assert payload["schema"] == "keel.cad.structure/v1" and payload["source"]["system"] == "FreeCAD"
    bolt = next(p for p in payload["parts"] if p.get("number") == "KF-2001")
    assert bolt["source"] == "Buy" and bolt["name"] == "Screw M5x16"


def test_find_numbers_are_assigned_by_order_and_explicit_ones_win():
    doc = Doc("D")
    a, b, c = (doc.add("PartDesign::Body", n, n, KeelPartNumber=n) for n in ("A", "B", "C"))
    top = part(doc, "Top", "TOP").contain(link(doc, "la", a), link(doc, "lb", b, KeelFindNumber=10), link(doc, "lc", c))
    ex = fcdoc.extract_structure(top)
    finds = {ex.part(ln.child).number: ln.find_number for ln in ex.lines}
    assert finds == {"B": 10, "A": 20, "C": 30}  # B asked for 10; A and C take the next free ones


def test_link_arrays_count_their_elements_and_reference_designators_are_checked():
    doc = Doc("D")
    r = doc.add("PartDesign::Body", "R", "Resistor", KeelPartNumber="R-10K")
    top = part(doc, "Board", "PCB-1").contain(link(doc, "arr", r, ElementCount=5, KeelRefDes="R1 R2,R3"), link(doc, "one", r, KeelRefDes="R 4/odd?"))
    ex = fcdoc.extract_structure(top)
    line = ex.lines[0]
    assert line.quantity == 6  # 5 from the array + 1
    assert line.reference_designators == ["R1", "R2", "R3", "R"]  # "4/odd?" is not a designator: goes to the notes
    assert line.notes and "4/odd?" in line.notes
    validate_payload(build_payload(ex))


def test_folders_are_transparent_and_excluded_usages_are_left_out():
    doc = Doc("D")
    x = doc.add("PartDesign::Body", "X", "X", KeelPartNumber="X-1")
    y = doc.add("PartDesign::Body", "Y", "Y", KeelPartNumber="Y-1")
    folder = doc.add("App::DocumentObjectGroup", "Folder").contain(link(doc, "lx", x), link(doc, "ly", y, KeelExclude=True))
    top = part(doc, "Top", "T-1").contain(folder)
    ex = fcdoc.extract_structure(top)
    assert lines_by_number(ex) == {("T-1", "X-1"): 1}


def test_loose_geometry_inside_a_part_is_not_a_part():
    doc = Doc("D")
    top = part(doc, "Top", "T-1")
    top.contain(doc.add("Part::Box", "Box"), doc.add("Part::Cylinder", "Cyl"))
    ex = fcdoc.extract_structure(top)
    assert ex.lines == [] and len(ex.parts) == 1


def test_a_part_without_a_number_is_kept_with_a_warning_and_a_unique_key():
    doc = Doc("D")
    top = part(doc, "Top", "T-1").contain(link(doc, "l1", doc.add("PartDesign::Body", "Mystery", "Mystery body")))
    ex = fcdoc.extract_structure(top)
    mystery = next(p for p in ex.parts if p.name == "Mystery body")
    assert mystery.number is None and mystery.ref.startswith("fc:")
    assert any("KeelPartNumber" in w for w in ex.warnings)
    assert [p.name for p in ex.unnumbered] == ["Mystery body"]
    payload = build_payload(ex)
    assert "number" not in next(p for p in payload["parts"] if p["name"] == "Mystery body")


def test_a_link_to_a_part_in_another_document_is_followed():
    other = Doc("Other", "/work/pkg/bracket.FCStd")
    bracket = other.add("PartDesign::Body", "Bracket", "Bracket", KeelPartNumber="KP-1001")
    doc = Doc("Main", "/work/pkg/main.FCStd")
    top = part(doc, "Top", "T-1").contain(link(doc, "ext", bracket))
    ex = fcdoc.extract_structure(top)
    assert lines_by_number(ex) == {("T-1", "KP-1001"): 1}
    assert fcdoc.document_dependencies(doc) == ["bracket.FCStd"]  # the link's OutList reaches into the other file
    assert fcdoc.document_dependencies(other) == []


def test_a_cycle_is_cut_with_a_warning_not_followed_forever():
    doc = Doc("D")
    a = part(doc, "A", "A-1")
    b = part(doc, "B", "B-1")
    a.contain(link(doc, "ab", b))
    b.contain(link(doc, "ba", a))
    ex = fcdoc.extract_structure(a)
    assert lines_by_number(ex) == {("A-1", "B-1"): 1}
    assert any("contain itself" in w for w in ex.warnings)
    validate_payload(build_payload(ex))


def test_a_very_deep_structure_stops_at_the_server_limit():
    doc = Doc("D")
    parts = [part(doc, f"P{i}", f"P-{i}") for i in range(40)]
    for i in range(39):
        parts[i].contain(link(doc, f"l{i}", parts[i + 1]))
    ex = fcdoc.extract_structure(parts[0])
    assert any("deeper" in w for w in ex.warnings)
    validate_payload(build_payload(ex))


def test_root_candidates_are_the_parts_nothing_contains():
    doc, top = build_frame_model()
    assert [o.Name for o in fcdoc.root_candidates(doc)] == ["FrameAsm"]
    doc.Objects.append(part(doc, "Loose", "L-1"))
    assert {o.Name for o in fcdoc.root_candidates(doc)} == {"FrameAsm", "Loose"}


def test_the_root_must_be_a_part():
    doc = Doc("D")
    with pytest.raises(fcdoc.ExtractionError):
        fcdoc.extract_structure(doc.add("Part::Box", "Box"))


def test_attributes_unit_and_source_come_from_custom_properties():
    doc = Doc("D")
    top = part(doc, "Top", "T-1", KeelUnit="EA", KeelSource="make", KeelAttr_cage_code="1ABC2", KeelPartName="Top assembly")
    ex = fcdoc.extract_structure(top)
    p = ex.parts[0]
    assert (p.unit, p.source, p.attributes, p.name) == ("EA", "Make", {"cage_code": "1ABC2"}, "Top assembly")
    bad = fcdoc.extract_structure(part(doc, "Top2", "T-2", KeelSource="Lease"))
    assert bad.parts[0].source is None and any("Make or Buy" in w for w in bad.warnings)


def test_drawings_are_found_through_the_sources_of_techdraw_views():
    doc, top = build_frame_model()
    doc.FileName = "/work/pkg/frame.FCStd"
    body = next(o for o in doc.Objects if o.Name == "Bracket")
    view1 = Obj(doc, "TechDraw::DrawViewPart", "View", "View", {"Source": [body]})
    inner = Obj(doc, "Part::Feature", "Pad", "Pad", {})
    body.contain(inner)
    view2 = Obj(doc, "TechDraw::DrawViewPart", "View2", "View2", {"Source": [inner]})
    loose = Obj(doc, "Part::Box", "Loose", "Loose box", {})
    view3 = Obj(doc, "TechDraw::DrawViewPart", "View3", "View3", {"Source": [loose]})
    doc.add("TechDraw::DrawPage", "Page", "Sheet 1", Views=[view1, view2, view3])
    [d] = fcdoc.find_drawings(doc)
    assert d.page == "Sheet 1" and d.file == "frame.FCStd"
    assert d.part_numbers == ["KP-1001"]  # the view of the pad resolves to its body, no duplicate
    assert d.unnumbered == ["Loose box"]


@pytest.mark.parametrize("mutate,message", [
    (lambda p: p["parts"].append(dict(p["parts"][0])), "appears twice"),
    (lambda p: p.update(root="nope"), "root is not a part"),
    (lambda p: p["lines"].append({"parent": "KA-3001", "child": "KA-3000", "quantity": 1}), "root part is used|cycle"),
    (lambda p: p["lines"][0].update(quantity=0), "quantity"),
    (lambda p: p["lines"][0].update(findNumber=0), "find number"),
    (lambda p: p["lines"][0].update(referenceDesignators=["bad one!"]), "designator"),
    (lambda p: p["lines"].append({"parent": "KA-3000", "child": "ghost", "quantity": 1}), "not in the structure"),
    (lambda p: p.update(schema="other"), "schema"),
])
def test_the_validator_refuses_what_the_server_would_refuse(mutate, message):
    doc, top = build_frame_model()
    payload = build_payload(fcdoc.extract_structure(top))
    mutate(payload)
    with pytest.raises(StructureError, match=message):
        validate_payload(payload)


def test_a_hand_built_cycle_is_caught():
    ex = Extraction(root="a", parts=[PartInfo("a", "A", "A"), PartInfo("b", "B", "B"), PartInfo("c", "C", "C")],
                    lines=[LineInfo("a", "b", 1), LineInfo("b", "c", 1), LineInfo("c", "b", 1)])
    with pytest.raises(StructureError, match="cycle"):
        build_payload(ex)
