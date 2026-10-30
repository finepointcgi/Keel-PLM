"""Stand-ins for FreeCAD documents and objects (duck typing: the attributes keel_plm.fcdoc reads). They are NOT FreeCAD: tests using them show that the
extraction logic is right for the object model it assumes, not that the assumption matches every FreeCAD version (see scripts/freecad_headless_check.py)."""
from __future__ import annotations

from typing import Any


class Doc:
    def __init__(self, name: str, file_name: str = ""):
        self.Name, self.FileName, self.Objects = name, file_name, []

    def add(self, type_id: str, name: str, label: str | None = None, **props: Any) -> "Obj":
        o = Obj(self, type_id, name, label or name, props)
        self.Objects.append(o)
        return o


class Obj:
    def __init__(self, doc: Doc, type_id: str, name: str, label: str, props: dict[str, Any]):
        self.Document, self.TypeId, self.Name, self.Label = doc, type_id, name, label
        self._props = dict(props)
        self.Group: list[Obj] = []
        self.parent: Obj | None = None

    # FreeCAD exposes custom and built-in properties as attributes and lists their names
    @property
    def PropertiesList(self) -> list[str]:
        return list(self._props)

    def __getattr__(self, name: str) -> Any:
        if name.startswith("_"):
            raise AttributeError(name)
        if name in self._props:
            return self._props[name]
        raise AttributeError(name)

    def set(self, **props: Any) -> "Obj":
        self._props.update(props)
        return self

    def contain(self, *kids: "Obj") -> "Obj":
        for k in kids:
            self.Group.append(k)
            k.parent = self
        return self

    def getParentGeoFeatureGroup(self) -> "Obj | None":
        return self.parent

    @property
    def OutList(self) -> list["Obj"]:
        out = list(self.Group)
        for key in ("LinkedObject", "Source"):
            v = self._props.get(key)
            out += list(v) if isinstance(v, list) else ([v] if v is not None else [])
        for key in ("Views",):
            out += list(self._props.get(key) or [])
        return out


def part(doc: Doc, name: str, number: str | None = None, label: str | None = None, **props: Any) -> Obj:
    if number:
        props["KeelPartNumber"] = number
    return doc.add("App::Part", name, label, **props)


def link(doc: Doc, name: str, target: Obj, **props: Any) -> Obj:
    return doc.add("App::Link", name, name, LinkedObject=target, **props)


def build_frame_model() -> tuple[Doc, Obj]:
    """The model of the STEP fixture frame-asm-*.step: KA-3000 = 2 x KA-3001 + 1 plate + 2 screws; KA-3001 = 1 bracket + 4 screws + 4 nuts."""
    doc = Doc("Frame", "/work/KA-3000/frame.FCStd")
    screw = doc.add("PartDesign::Body", "Screw", "Screw M5x16", KeelPartNumber="KF-2001", KeelSource="Buy")
    nut = doc.add("PartDesign::Body", "Nut", "Nut M5", KeelPartNumber="KF-2002", KeelSource="Buy")
    bracket = doc.add("PartDesign::Body", "Bracket", "Bracket, L-shaped", KeelPartNumber="KP-1001")
    plate = doc.add("PartDesign::Body", "Plate", "Mounting plate", KeelPartNumber="KP-1002")
    sub = part(doc, "BracketAsm", "KA-3001", "Bracket assembly")
    sub.contain(link(doc, "L_bracket", bracket), *[link(doc, f"L_screw{i}", screw) for i in range(4)], *[link(doc, f"L_nut{i}", nut) for i in range(4)])
    top = part(doc, "FrameAsm", "KA-3000", "Frame assembly")
    top.contain(link(doc, "L_sub1", sub), link(doc, "L_sub2", sub), link(doc, "L_plate", plate), link(doc, "L_screwA", screw), link(doc, "L_screwB", screw))
    return doc, top
