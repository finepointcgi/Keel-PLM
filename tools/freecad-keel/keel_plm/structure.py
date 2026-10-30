"""The neutral structure (``keel.cad.structure/v1``): plain data classes, the payload builder and a validator that mirrors the server's rules.

No FreeCAD import. ``fcdoc.py`` turns a FreeCAD document into an :class:`Extraction`; this module turns an Extraction into the JSON that Keel
reads (docs/integration/cad/structure-v1.schema.json).
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import Any

from . import SCHEMA_ID, __version__

MAX_PARTS = 20_000
MAX_LINES = 100_000
MAX_DEPTH = 30
DESIGNATOR = re.compile(r"^[A-Za-z0-9][A-Za-z0-9\-_./]{0,39}$")


@dataclass
class PartInfo:
    ref: str
    number: str | None
    name: str
    unit: str | None = None
    source: str | None = None
    attributes: dict[str, str] = field(default_factory=dict)


@dataclass
class LineInfo:
    parent: str
    child: str
    quantity: int
    find_number: int | None = None
    reference_designators: list[str] = field(default_factory=list)
    notes: str | None = None


@dataclass
class DrawingInfo:
    """A TechDraw page: the part numbers its views show, the document files of the models behind them."""
    page: str
    file: str | None
    part_numbers: list[str] = field(default_factory=list)
    unnumbered: list[str] = field(default_factory=list)
    source_files: list[str] = field(default_factory=list)


@dataclass
class Extraction:
    root: str
    parts: list[PartInfo] = field(default_factory=list)
    lines: list[LineInfo] = field(default_factory=list)
    drawings: list[DrawingInfo] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)

    def part(self, ref: str) -> PartInfo:
        return next(p for p in self.parts if p.ref == ref)

    @property
    def unnumbered(self) -> list[PartInfo]:
        return [p for p in self.parts if not p.number]


class StructureError(ValueError):
    pass


def build_payload(extraction: Extraction, system: str = "FreeCAD", tool: str | None = None) -> dict[str, Any]:
    """The JSON document for ``POST /bom-proposals`` (format ``json``). Quantities are the counts of usages found in the CAD document."""
    parts = []
    for p in extraction.parts:
        item: dict[str, Any] = {"ref": p.ref, "name": p.name[:200] or p.ref}
        if p.number:
            item["number"] = p.number
        if p.unit:
            item["unit"] = p.unit
        if p.source:
            item["source"] = p.source
        if p.attributes:
            item["attributes"] = dict(p.attributes)
        parts.append(item)
    lines = []
    for ln in extraction.lines:
        item = {"parent": ln.parent, "child": ln.child, "quantity": ln.quantity}
        if ln.find_number is not None:
            item["findNumber"] = ln.find_number
        if ln.reference_designators:
            item["referenceDesignators"] = list(ln.reference_designators)
        if ln.notes:
            item["notes"] = ln.notes[:1000]
        lines.append(item)
    payload = {"schema": SCHEMA_ID, "source": {"system": system, "tool": tool or f"keel-freecad {__version__}"}, "root": extraction.root, "parts": parts, "lines": lines}
    validate_payload(payload)
    return payload


def validate_payload(payload: dict[str, Any]) -> None:
    """The same rules as the server's StructureValidator; raises StructureError naming the first problems."""
    errors: list[str] = []

    def err(m: str) -> None:
        if len(errors) < 20:
            errors.append(m)

    if payload.get("schema") != SCHEMA_ID:
        err(f"schema must be {SCHEMA_ID}")
    parts = payload.get("parts") or []
    lines = payload.get("lines") or []
    if not parts:
        err("The structure has no parts.")
    if len(parts) > MAX_PARTS:
        err(f"The structure has more than {MAX_PARTS} parts.")
    if len(lines) > MAX_LINES:
        err(f"The structure has more than {MAX_LINES} usage lines.")
    refs: set[str] = set()
    for p in parts:
        ref = p.get("ref") or ""
        if not ref or len(ref) > 200:
            err("A part has no key or a key longer than 200 characters.")
        elif ref in refs:
            err(f"The part key '{ref[:40]}' appears twice.")
        refs.add(ref)
        if not p.get("name") or len(p["name"]) > 200:
            err(f"Part '{ref[:40]}' needs a name of at most 200 characters.")
    if payload.get("root") not in refs:
        err("The root is not a part of the structure.")
    edges: dict[str, list[str]] = {}
    children: set[str] = set()
    for ln in lines:
        if ln["parent"] not in refs or ln["child"] not in refs:
            err("A usage line names a part that is not in the structure.")
            continue
        if ln["parent"] == ln["child"]:
            err(f"Part '{ln['parent'][:40]}' contains itself.")
        q = ln["quantity"]
        if not (0 < q <= 1_000_000):
            err(f"A usage of '{ln['child'][:40]}' has a quantity outside 0 to 1,000,000.")
        f = ln.get("findNumber")
        if f is not None and not (1 <= f <= 999_999):
            err(f"A usage of '{ln['child'][:40]}' has a find number outside 1 to 999999.")
        for d in ln.get("referenceDesignators", []):
            if not DESIGNATOR.match(d):
                err(f"'{d[:40]}' is not a valid reference designator.")
                break
        children.add(ln["child"])
        edges.setdefault(ln["parent"], []).append(ln["child"])
    if payload.get("root") in children:
        err("The root part is used by another part; choose the top assembly as the root.")
    if not errors and _has_cycle(edges):
        err("The structure contains a cycle (a part contains itself through other parts).")
    if not errors and _depth(edges, payload["root"]) > MAX_DEPTH:
        err(f"The structure is deeper than {MAX_DEPTH} levels.")
    if errors:
        raise StructureError(" ".join(errors))


def _has_cycle(edges: dict[str, list[str]]) -> bool:
    state: dict[str, int] = {}
    for start in edges:
        if start in state:
            continue
        stack = [(start, 0)]
        state[start] = 1
        while stack:
            node, i = stack.pop()
            kids = edges.get(node, [])
            if i >= len(kids):
                state[node] = 2
                continue
            stack.append((node, i + 1))
            child = kids[i]
            s = state.get(child, 0)
            if s == 1:
                return True
            if s == 0:
                state[child] = 1
                stack.append((child, 0))
    return False


def _depth(edges: dict[str, list[str]], root: str) -> int:
    best: dict[str, int] = {}
    deepest = 0
    queue = [(root, 1)]
    while queue:
        node, level = queue.pop()
        if best.get(node, 0) >= level:
            continue
        best[node] = level
        deepest = max(deepest, level)
        if deepest > MAX_DEPTH:
            return deepest
        queue.extend((c, level + 1) for c in edges.get(node, []))
    return deepest


def explode(extraction: Extraction) -> dict[str, int]:
    """Total piece count of every part under the root (what an exploded BOM would show), for the summary the user sees before pushing."""
    kids: dict[str, list[LineInfo]] = {}
    for ln in extraction.lines:
        kids.setdefault(ln.parent, []).append(ln)
    totals: dict[str, int] = {}

    def walk(ref: str, factor: int) -> None:
        for ln in kids.get(ref, []):
            totals[ln.child] = totals.get(ln.child, 0) + ln.quantity * factor
            walk(ln.child, ln.quantity * factor)

    walk(extraction.root, 1)
    return totals
