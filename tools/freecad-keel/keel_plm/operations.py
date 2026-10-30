"""The commands of the add-on as plain functions: push a BOM, derive check-in references, mark associations, show status. No FreeCAD import."""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from . import workfolder
from .client import KeelClient, KeelError
from .structure import DrawingInfo, Extraction, StructureError, build_payload, explode

MARKING_KEYS = ("exportJurisdiction", "distributionStatement", "owningOrg", "eccn", "usmlCategory", "cuiCategories", "controllingOffice",
                "distributionReason", "program", "exportAuthorizations")


# ---------------------------------------------------------------------- push BOM

def markings_of_document(client: KeelClient, number: str) -> dict[str, Any]:
    """A proposal is marked like the data it comes from (FEAT-CAD-6): take the markings of the top document of the package."""
    m = client.document(number).get("markings") or {}
    return {k: m[k] for k in MARKING_KEYS if k in m and m[k] is not None}


@dataclass
class BomSummary:
    parts: int
    lines: int
    unnumbered: list[str]
    totals: dict[str, int]
    warnings: list[str]


def summarize(extraction: Extraction) -> BomSummary:
    totals = explode(extraction)
    name = {p.ref: (p.number or p.name) for p in extraction.parts}
    return BomSummary(len(extraction.parts), len(extraction.lines), [p.name for p in extraction.unnumbered], {name[r]: q for r, q in totals.items()}, list(extraction.warnings))


def push_bom(client: KeelClient, extraction: Extraction, title: str, *, markings_from: str, apply_removals: bool = False,
             require_part_numbers: bool = False, tool: str | None = None) -> dict:
    """Builds ``keel.cad.structure/v1`` and creates a structure proposal. Nothing in the BOM changes until a person reviews and applies it in Keel."""
    if require_part_numbers and extraction.unnumbered:
        names = ", ".join(p.name for p in extraction.unnumbered[:5])
        raise StructureError(f"{len(extraction.unnumbered)} part(s) have no KeelPartNumber ({names}); set it or switch off 'require part numbers'.")
    payload = build_payload(extraction, tool=tool)
    markings = markings_of_document(client, markings_from)
    root_number = extraction.part(extraction.root).number
    return client.create_proposal(payload, title, markings, root_number=root_number, apply_removals=apply_removals)


# ---------------------------------------------------------------------- check-in references

def references_for_checkin(state: workfolder.PackageState, dependencies: dict[str, list[str]]) -> dict[str, list[str]]:
    """``dependencies`` maps a file name of the folder to the file names it is built against (``fcdoc.document_dependencies``).
    The result maps document numbers to document numbers, in the form ``workfolder.checkin`` takes. Files outside the package are ignored (the server would
    refuse a reference it cannot resolve) and a document never references itself."""
    out: dict[str, list[str]] = {}
    for file_name, deps in dependencies.items():
        member = state.member_of_file(file_name)
        if member is None:
            continue
        numbers = []
        for d in deps:
            dep = state.member_of_file(d)
            if dep is not None and dep.number != member.number and dep.number not in numbers:
                numbers.append(dep.number)
        out.setdefault(member.number, [])
        out[member.number] = list(dict.fromkeys(out[member.number] + numbers))
    return out


# ---------------------------------------------------------------------- associations

@dataclass
class AssociationPlan:
    items: list[tuple[str, str, str]] = field(default_factory=list)  # (document number, part number, kind)
    unresolved: list[str] = field(default_factory=list)


def plan_associations(state: workfolder.PackageState, model_file: str | None, model_part_number: str | None, drawings: list[DrawingInfo]) -> AssociationPlan:
    """``model-of``: the document of ``model_file`` is the model of ``model_part_number``. ``drawing-of``: the document that holds a TechDraw page is the
    drawing of every numbered part its views show. Files that are not part of the checked-out package are reported, not guessed."""
    plan = AssociationPlan()

    def add(doc: str, part: str, kind: str) -> None:
        if (doc, part, kind) not in plan.items:
            plan.items.append((doc, part, kind))

    if model_file and model_part_number:
        member = state.member_of_file(model_file)
        if member is None:
            plan.unresolved.append(f"{model_file} is not part of the checked-out package.")
        else:
            add(member.number, model_part_number, "model-of")
    for d in drawings:
        member = state.member_of_file(d.file) if d.file else None
        if member is None:
            plan.unresolved.append(f"Drawing page '{d.page}': its file ({d.file or 'not saved'}) is not part of the checked-out package.")
            continue
        for n in d.part_numbers:
            add(member.number, n, "drawing-of")
        for label in d.unnumbered:
            plan.unresolved.append(f"Drawing page '{d.page}' shows '{label}', which has no KeelPartNumber.")
    return plan


@dataclass
class AssociationReport:
    created: list[tuple[str, str, str]] = field(default_factory=list)
    existing: list[tuple[str, str, str]] = field(default_factory=list)
    failed: list[tuple[tuple[str, str, str], str]] = field(default_factory=list)


def apply_associations(client: KeelClient, plan: AssociationPlan) -> AssociationReport:
    report = AssociationReport()
    for item in plan.items:
        try:
            client.add_association(*item)
            report.created.append(item)
        except KeelError as e:
            if e.status == 409:
                report.existing.append(item)
            else:
                report.failed.append((item, str(e)))
    return report


# ---------------------------------------------------------------------- status

@dataclass
class MemberStatus:
    number: str
    role: str
    files: list[str]
    changed: list[str]
    locked_by: str | None
    mine: bool
    is_new: bool


@dataclass
class StatusReport:
    top: str
    members: list[MemberStatus] = field(default_factory=list)
    stale: list[dict] = field(default_factory=list)
    stale_count: int = 0
    redacted_count: int = 0
    banner: str = ""


def _stale_nodes(nodes: list[dict], out: list[dict]) -> None:
    for n in nodes or []:
        if n.get("stale") and not n.get("redacted"):
            out.append({"number": n.get("number"), "name": n.get("name"), "role": n.get("cadRole"), "seen": n.get("seenRevision"), "seenIteration": n.get("seenIteration")})
        _stale_nodes(n.get("children") or [], out)


def status(client: KeelClient, folder: Path | str) -> StatusReport:
    """Local changes, who holds each lock, and stale drawings of the package of a checked-out folder."""
    folder = Path(folder)
    state = workfolder.load_state(folder)
    changed = {(m.number, f.file_name) for m, f, _ in workfolder.changed_files(folder)}
    report = StatusReport(top=state.top)
    for m in state.members:
        doc = client.document(m.number)
        co = doc.get("checkout") or {}
        report.members.append(MemberStatus(m.number, m.role, [f.file_name for f in m.files], [fn for (n, fn) in sorted(changed) if n == m.number],
                                           co.get("userName"), bool(co.get("isMine")), m.is_new))
    view = client.package_view(state.top)
    report.stale_count = int(view.get("staleCount") or 0)
    report.redacted_count = int(view.get("redactedCount") or 0)
    report.banner = view.get("banner") or ""
    _stale_nodes(view.get("uses") or [], report.stale)
    _stale_nodes(view.get("usedBy") or [], report.stale)
    return report


def lock_owner(client: KeelClient, number: str) -> tuple[str | None, bool]:
    """(user name, is it me) of the check-out of a document, or (None, False) when nobody holds it."""
    co = client.document(number).get("checkout") or {}
    return co.get("userName"), bool(co.get("isMine"))


def find_folder(file_path: Path | str) -> Path | None:
    """The package folder (the nearest folder with a ``.keel-cad`` state) that holds a file, or None."""
    p = Path(file_path).resolve()
    for d in [p.parent, *p.parent.parents][:6]:
        if workfolder.has_state(d):
            return d
    return None
