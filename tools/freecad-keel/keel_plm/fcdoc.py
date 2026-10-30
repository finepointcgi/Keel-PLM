"""Reads the product structure out of a FreeCAD document, WITHOUT importing FreeCAD.

The functions work on any object that looks like a FreeCAD document or document object (``TypeId``, ``Label``, ``Name``, ``Group``, ``PropertiesList``,
``LinkedObject``, ``OutList``, ``Views``/``Source`` for TechDraw), so the logic is unit-tested with small stand-in objects (tests/fakes.py) and, where
FreeCAD is installed, against a real document (scripts/freecad_headless_check.py). The module that touches the real FreeCAD is ``gui.py`` only.

What a "part" is in a FreeCAD document:

* an ``App::Part`` or an ``Assembly::AssemblyObject`` (a container of parts and links),
* a ``PartDesign::Body`` (a leaf part), or any object that carries the custom property ``KeelPartNumber``;
* ``App::Link`` objects point to a part (in this or another document); ``ElementCount`` of a link array counts as that many usages;
* plain ``App::DocumentObjectGroup`` folders are transparent; loose geometry (``Part::Box`` and the like) is part of the part that contains it.

Custom properties read (all optional except the number you want matched in Keel): ``KeelPartNumber``, ``KeelPartName`` (default: the label),
``KeelUnit``, ``KeelSource`` (Make or Buy), ``KeelAttr_<attribute>`` (an administrator-defined part attribute), and on a link ``KeelFindNumber``,
``KeelRefDes`` (reference designators) and ``KeelExclude`` (leave this usage out).
"""
from __future__ import annotations

import os
import re
from typing import Any

from .structure import DESIGNATOR, MAX_DEPTH, DrawingInfo, Extraction, LineInfo, PartInfo

PART_NUMBER = "KeelPartNumber"
CONTAINERS = {"App::Part", "Assembly::AssemblyObject", "Assembly::AssemblyLink"}
BODIES = {"PartDesign::Body"}
FOLDERS = {"App::DocumentObjectGroup"}
LINKS = {"App::Link", "App::LinkElement"}
LINK_GROUPS = {"App::LinkGroup"}
PAGES = {"TechDraw::DrawPage"}


class ExtractionError(ValueError):
    pass


def prop(obj: Any, name: str, default: Any = None) -> Any:
    try:
        if name in list(getattr(obj, "PropertiesList", [])):
            value = getattr(obj, name)
            return default if value is None else value
    except Exception:  # a real FreeCAD property getter can raise for unusual types
        return default
    return default


def _text(value: Any) -> str | None:
    if value is None:
        return None
    s = str(value).strip()
    return s or None


def type_id(obj: Any) -> str:
    return str(getattr(obj, "TypeId", ""))


def is_part(obj: Any) -> bool:
    return type_id(obj) in CONTAINERS or type_id(obj) in BODIES or _text(prop(obj, PART_NUMBER)) is not None


def part_number(obj: Any) -> str | None:
    return _text(prop(obj, PART_NUMBER))


def part_name(obj: Any) -> str:
    return _text(prop(obj, "KeelPartName")) or _text(getattr(obj, "Label", None)) or str(getattr(obj, "Name", "part"))


def _doc_name(obj: Any) -> str:
    return str(getattr(getattr(obj, "Document", None), "Name", "doc"))


def _resolve(link: Any) -> Any:
    """Follows a chain of links to the object they finally point at."""
    seen = 0
    while type_id(link) in LINKS and seen < 20:
        target = getattr(link, "LinkedObject", None)
        if target is None:
            return None
        link = target
        seen += 1
    return link


def _members(container: Any) -> list[Any]:
    if type_id(container) in BODIES:
        return []  # the features of a body are not parts
    if type_id(container) in LINK_GROUPS:
        return list(getattr(container, "ElementList", None) or [])
    return list(getattr(container, "Group", None) or [])


def _usages(container: Any, found: list[tuple[Any, Any, int]], depth: int = 0) -> None:
    """Appends (target, usage object, count) for every part usage directly in ``container`` (folders and link groups are transparent)."""
    if depth > 8:
        return
    for child in _members(container):
        t = type_id(child)
        if prop(child, "KeelExclude") in (True, "True", "true", 1):
            continue
        if t in LINKS:
            target = _resolve(child)
            if target is None:
                continue
            count = int(prop(child, "ElementCount", 0) or 0)
            found.append((target, child, max(1, count)))
        elif t in FOLDERS or t in LINK_GROUPS:
            _usages(child, found, depth + 1)
        elif is_part(child):
            found.append((child, child, 1))


def _ref_of(obj: Any) -> str:
    n = part_number(obj)
    return n if n else f"fc:{_doc_name(obj)}/{getattr(obj, 'Name', id(obj))}"


def root_candidates(doc: Any) -> list[Any]:
    """The parts of a document that no other part of the document contains or links: the possible tops of a BOM."""
    objects = list(getattr(doc, "Objects", []))
    used: set[int] = set()
    for obj in objects:
        if type_id(obj) in CONTAINERS or type_id(obj) in FOLDERS:
            found: list[tuple[Any, Any, int]] = []
            _usages(obj, found)
            used.update(id(t) for t, _, _ in found)
    parts = [o for o in objects if is_part(o) and type_id(o) not in LINKS and id(o) not in used]
    tops = [o for o in parts if type_id(o) in CONTAINERS] or parts
    return tops


def extract_structure(root_obj: Any, *, system_files: set[str] | None = None) -> Extraction:
    """Walks the structure below ``root_obj`` and returns parts, lines (quantities are usage counts) and warnings."""
    if not is_part(root_obj):
        raise ExtractionError(f"'{getattr(root_obj, 'Label', '?')}' is not a part (an App::Part, assembly, body, or an object with {PART_NUMBER}).")
    ex = Extraction(root=_ref_of(root_obj))
    seen_refs: dict[str, PartInfo] = {}
    expanded: set[str] = set()

    def register(obj: Any) -> str:
        ref = _ref_of(obj)
        if ref not in seen_refs:
            p = PartInfo(ref=ref, number=part_number(obj), name=part_name(obj), unit=_text(prop(obj, "KeelUnit")))
            src = _text(prop(obj, "KeelSource"))
            if src:
                if src.lower() in ("make", "buy"):
                    p.source = src.capitalize()
                else:
                    ex.warnings.append(f"'{p.name}': KeelSource '{src}' is not Make or Buy and was ignored.")
            for name in list(getattr(obj, "PropertiesList", [])):
                if name.startswith("KeelAttr_") and len(name) > 9 and (v := _text(prop(obj, name))) is not None:
                    p.attributes[name[9:]] = v
            seen_refs[ref] = p
            ex.parts.append(p)
            if not p.number:
                ex.warnings.append(f"'{p.name}' ({_doc_name(obj)}/{getattr(obj, 'Name', '?')}) has no {PART_NUMBER}; Keel will propose it as a new part.")
            if system_files is not None and getattr(getattr(obj, "Document", None), "FileName", ""):
                system_files.add(os.path.basename(obj.Document.FileName))
        return ref

    def walk(obj: Any, path: tuple[str, ...]) -> None:
        ref = register(obj)
        if ref in expanded:
            return
        expanded.add(ref)
        if len(path) + 1 >= MAX_DEPTH:  # this part is at level len(path) + 1; its children would be one level deeper than Keel accepts
            probe: list[tuple[Any, Any, int]] = []
            _usages(obj, probe)
            if probe:
                ex.warnings.append(f"The structure is deeper than {MAX_DEPTH} levels below '{part_name(obj)}'; deeper levels were left out.")
            return
        found: list[tuple[Any, Any, int]] = []
        _usages(obj, found)
        by_child: dict[str, dict[str, Any]] = {}
        order: list[str] = []
        for target, usage, count in found:
            cref = _ref_of(target)
            if cref == ref or cref in path:
                ex.warnings.append(f"'{part_name(target)}' would contain itself through '{part_name(obj)}'; that usage was left out.")
                continue
            if cref not in by_child:
                by_child[cref] = {"target": target, "count": 0, "find": None, "refdes": [], "notes": []}
                order.append(cref)
            agg = by_child[cref]
            agg["count"] += count
            fn = prop(usage, "KeelFindNumber")
            if agg["find"] is None and fn not in (None, "", 0):
                try:
                    agg["find"] = int(fn)
                except (TypeError, ValueError):
                    ex.warnings.append(f"KeelFindNumber '{fn}' of '{part_name(target)}' is not a whole number and was ignored.")
            for d in re.split(r"[\s,;]+", _text(prop(usage, "KeelRefDes")) or ""):
                if d:
                    (agg["refdes"] if DESIGNATOR.match(d) else agg["notes"]).append(d)
        used = {a["find"] for a in by_child.values() if a["find"] is not None}
        next_find = 10
        for cref in order:
            agg = by_child[cref]
            if agg["find"] is None:
                while next_find in used:
                    next_find += 10
                agg["find"] = next_find
                used.add(next_find)
                next_find += 10
            walk(agg["target"], (*path, ref))
            ex.lines.append(LineInfo(parent=ref, child=cref, quantity=agg["count"], find_number=agg["find"],
                                     reference_designators=list(dict.fromkeys(agg["refdes"])),
                                     notes=("reference: " + ", ".join(agg["notes"])) if agg["notes"] else None))

    walk(root_obj, ())
    return ex


# ---------------------------------------------------------------------------- drawings (TechDraw)

def _owning_part(source: Any) -> Any:
    source = _resolve(source) or source
    cur = source
    for _ in range(20):
        if cur is None:
            break
        if is_part(cur):
            return cur
        getter = getattr(cur, "getParentGeoFeatureGroup", None)
        cur = getter() if callable(getter) else None
    return source


def find_drawings(doc: Any) -> list[DrawingInfo]:
    """Every TechDraw page of the document with the parts its views show (through the ``Source`` of each view)."""
    drawings = []
    file_name = os.path.basename(getattr(doc, "FileName", "") or "") or None
    for page in (o for o in getattr(doc, "Objects", []) if type_id(o) in PAGES):
        info = DrawingInfo(page=str(getattr(page, "Label", getattr(page, "Name", "page"))), file=file_name)
        for view in getattr(page, "Views", None) or []:
            for src in getattr(view, "Source", None) or []:
                owner = _owning_part(src)
                if owner is None:
                    continue
                n = part_number(owner)
                if n:
                    if n not in info.part_numbers:
                        info.part_numbers.append(n)
                elif part_name(owner) not in info.unnumbered:
                    info.unnumbered.append(part_name(owner))
                f = os.path.basename(getattr(getattr(owner, "Document", None), "FileName", "") or "")
                if f and f not in info.source_files:
                    info.source_files.append(f)
        drawings.append(info)
    return drawings


def document_dependencies(doc: Any) -> list[str]:
    """File names (not paths) of the OTHER documents this one is built against: external links and TechDraw sources in other files."""
    deps: list[str] = []
    for obj in getattr(doc, "Objects", []):
        for o in getattr(obj, "OutList", None) or []:
            d = getattr(o, "Document", None)
            fn = getattr(d, "FileName", "") if d is not None else ""
            if d is not None and d is not doc and fn:
                base = os.path.basename(fn)
                if base not in deps:
                    deps.append(base)
    return deps
