#!/usr/bin/env python3
"""Generates the real STEP test files under tests/Keel.UnitTests/Fixtures/cad/.

The files are produced by a real geometry kernel (Open CASCADE, through CadQuery's OCP bindings), not written by hand, so the Keel STEP structure reader
is tested against what a real exporter emits (entity order, line wrapping, long entities, shared product definitions, AP214/AP242/AP203 headers).

How to run (never globally; a throwaway venv):

    python3.11 -m venv /tmp/cqvenv && /tmp/cqvenv/bin/pip install cadquery
    /tmp/cqvenv/bin/python tools/freecad-keel/make_fixtures.py            # writes the fixtures
    /tmp/cqvenv/bin/python tools/freecad-keel/make_fixtures.py --check    # compares with the committed files (exit 1 on a difference)

What is generated (all synthetic, no real data):

    bracket.step, plate.step, bolt.step      one product each (parts), AP214
    bracket-asm.step                         2 levels: KA-3001 = 1 bracket + 4 screws + 4 nuts, AP214
    frame-asm-ap214.step                     3 levels: KA-3000 = 2 x KA-3001 (the SAME sub-assembly twice) + plate + 2 screws, AP214
    frame-asm-ap242.step                     the same model written as AP242 (managed model based 3D engineering)
    frame-asm-ap203.step                     the same model written as AP203 (config control design)
    unicode-name-ap242.step                  an assembly whose name has non-ASCII characters (raw UTF-8 in the file)
    two-products-one-number.step             two PRODUCT entities with the same id (2 + 1 pieces of one part number)

Product ids are the part numbers (KP-, KF-, KA- prefix), the names are descriptive. Open CASCADE writes id and name from one label name, so this script
rewrites the PRODUCT names afterwards (so nobody thinks the exporter did it) and pins the header time stamp so the output is reproducible.
Instances are real XCAF components: a sub-assembly used twice is ONE product with TWO usage occurrences.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

import cadquery as cq
from OCP.IFSelect import IFSelect_RetDone
from OCP.Interface import Interface_Static
from OCP.STEPCAFControl import STEPCAFControl_Writer
from OCP.STEPControl import STEPControl_AsIs
from OCP.TCollection import TCollection_ExtendedString
from OCP.TDataStd import TDataStd_Name
from OCP.TDocStd import TDocStd_Document
from OCP.XCAFApp import XCAFApp_Application
from OCP.XCAFDoc import XCAFDoc_DocumentTool

OUT = Path(__file__).resolve().parents[2] / "tests" / "Keel.UnitTests" / "Fixtures" / "cad"

NAMES = {
    "KP-1001": "Bracket, L-shaped",
    "KP-1002": "Mounting plate",
    "KF-2001": "Screw M5x16",
    "KF-2002": "Nut M5",
    "KA-3001": "Bracket assembly",
    "KA-3000": "Frame assembly",
    "KA-4000": "Halterung für Ø5 Welle",  # non-ASCII name (u-umlaut, O-slash)
    "KA-5000": "Variant assembly",
}

SCHEMAS = {"ap214": "AP214IS", "ap242": "AP242DIS", "ap203": "AP203"}


def screw() -> cq.Workplane:
    return cq.Workplane("XY").polygon(6, 8).extrude(3).faces(">Z").workplane().circle(2.5).extrude(16)


def nut() -> cq.Workplane:
    return cq.Workplane("XY").polygon(6, 8).extrude(4).faces(">Z").workplane().hole(5)


def bracket() -> cq.Workplane:
    return cq.Workplane("XY").box(40, 20, 3).faces(">Z").workplane().rect(30, 10, forConstruction=True).vertices().hole(5.2)


def plate() -> cq.Workplane:
    return cq.Workplane("XY").box(120, 80, 4).faces(">Z").workplane().rect(100, 60, forConstruction=True).vertices().hole(5.2)


class Builder:
    def __init__(self) -> None:
        app = XCAFApp_Application.GetApplication_s()
        self.doc = TDocStd_Document(TCollection_ExtendedString("MDTV-XCAF"))
        app.NewDocument(TCollection_ExtendedString("MDTV-XCAF"), self.doc)
        self.tool = XCAFDoc_DocumentTool.ShapeTool_s(self.doc.Main())
        self.labels: dict[str, object] = {}

    def part(self, number: str, wp: cq.Workplane) -> None:
        label = self.tool.AddShape(wp.val().wrapped, False)
        TDataStd_Name.Set_s(label, TCollection_ExtendedString(number))
        self.labels[number] = label

    def assembly(self, number: str) -> None:
        label = self.tool.NewShape()
        TDataStd_Name.Set_s(label, TCollection_ExtendedString(number))
        self.labels[number] = label

    def use(self, parent: str, child: str, x: float = 0, y: float = 0, z: float = 0, count: int = 1) -> None:
        for i in range(count):
            loc = cq.Location(cq.Vector(x + i * 6, y, z)).wrapped
            self.tool.AddComponent(self.labels[parent], self.labels[child], loc)
        self.tool.UpdateAssemblies()

    def write(self, path: Path, schema: str) -> None:
        writer = STEPCAFControl_Writer()
        writer.SetNameMode(True)
        Interface_Static.SetCVal_s("write.step.schema", schema)
        Interface_Static.SetCVal_s("write.step.unit", "MM")
        writer.Transfer(self.doc, STEPControl_AsIs)
        if writer.Write(str(path)) != IFSelect_RetDone:
            raise SystemExit(f"STEP write failed: {path}")
        normalise(path)


def normalise(path: Path) -> None:
    """Gives PRODUCT entities their descriptive names and pins the header time stamp."""
    text = path.read_text(encoding="latin-1")
    for number, name in NAMES.items():
        # Open CASCADE writes non-ASCII names as raw UTF-8 bytes; the file is handled as latin-1 here so those bytes pass through untouched.
        text = text.replace(f"PRODUCT('{number}','{number}',", f"PRODUCT('{number}','{name.encode('utf-8').decode('latin-1')}',")
    text = re.sub(r"'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}'", "'2026-10-29T00:00:00'", text)
    path.write_text(text, encoding="latin-1")


def single(number: str, wp: cq.Workplane, schema: str, path: Path) -> None:
    b = Builder()
    b.part(number, wp)
    b.write(path, schema)


def bracket_asm(b: Builder) -> None:
    b.part("KP-1001", bracket())
    b.part("KF-2001", screw())
    b.part("KF-2002", nut())
    b.assembly("KA-3001")
    b.use("KA-3001", "KP-1001")
    b.use("KA-3001", "KF-2001", 5, 5, 3, count=4)
    b.use("KA-3001", "KF-2002", 5, 5, -4, count=4)


def frame_asm(b: Builder) -> None:
    bracket_asm(b)
    b.part("KP-1002", plate())
    b.assembly("KA-3000")
    b.use("KA-3000", "KA-3001", 0, 0, 4)
    b.use("KA-3000", "KA-3001", 0, 40, 4)  # the SAME sub-assembly a second time: one product, two occurrences
    b.use("KA-3000", "KP-1002")
    b.use("KA-3000", "KF-2001", 50, 30, 4, count=2)  # a part used at two levels


def unicode_asm(b: Builder) -> None:
    """KA-4000 has a non-ASCII name, which Open CASCADE writes as raw UTF-8 in the STEP text (reader bug found with this file)."""
    b.part("KP-1002", plate())
    b.assembly("KA-4000")
    b.use("KA-4000", "KP-1002")


def two_products_one_number(b: Builder) -> None:
    """Two DIFFERENT shapes carry the number KF-2001 (what an exporter writes for two configurations of one part): two PRODUCT entities, one part number."""
    b.part("KF-2001", screw())
    short = b.labels["KF-2001"]
    b.part("KF-2001", cq.Workplane("XY").polygon(6, 8).extrude(3).faces(">Z").workplane().circle(2.5).extrude(24))
    long = b.labels["KF-2001"]
    b.assembly("KA-5000")
    b.tool.AddComponent(b.labels["KA-5000"], short, cq.Location(cq.Vector(0, 0, 0)).wrapped)
    b.tool.AddComponent(b.labels["KA-5000"], short, cq.Location(cq.Vector(10, 0, 0)).wrapped)
    b.tool.AddComponent(b.labels["KA-5000"], long, cq.Location(cq.Vector(20, 0, 0)).wrapped)
    b.tool.UpdateAssemblies()


def main() -> int:
    check = "--check" in sys.argv
    target = Path("/tmp/keel-fixtures-check") if check else OUT
    target.mkdir(parents=True, exist_ok=True)
    single("KP-1001", bracket(), SCHEMAS["ap214"], target / "bracket.step")
    single("KP-1002", plate(), SCHEMAS["ap214"], target / "plate.step")
    single("KF-2001", screw(), SCHEMAS["ap214"], target / "bolt.step")
    b = Builder()
    bracket_asm(b)
    b.write(target / "bracket-asm.step", SCHEMAS["ap214"])
    for key in ("ap214", "ap242", "ap203"):
        b = Builder()
        frame_asm(b)
        b.write(target / f"frame-asm-{key}.step", SCHEMAS[key])
    b = Builder()
    unicode_asm(b)
    b.write(target / "unicode-name-ap242.step", SCHEMAS["ap242"])
    b = Builder()
    two_products_one_number(b)
    b.write(target / "two-products-one-number.step", SCHEMAS["ap214"])
    if check:
        bad = [f.name for f in target.glob("*.step") if not (OUT / f.name).exists() or (OUT / f.name).read_bytes() != f.read_bytes()]
        print("differs:", bad if bad else "none")
        return 1 if bad else 0
    for f in sorted(OUT.glob("*.step")):
        print(f"{f.name}: {f.stat().st_size} bytes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
