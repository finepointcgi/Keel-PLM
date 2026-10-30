"""The FreeCAD GUI layer: commands for the toolbar and menu. THIN by design: it only collects input, calls the plain modules, and shows the result.

This is the only module that imports FreeCAD, FreeCADGui and PySide. It has NOT been run in a real FreeCAD by the authors (FreeCAD was not installed
where it was written; see docs/increments/9.2-open-source-cad.md). The logic it calls is tested (tests/), the widgets and command registration are not.
"""
from __future__ import annotations

import os
from pathlib import Path

import FreeCAD  # type: ignore[import-not-found]
import FreeCADGui  # type: ignore[import-not-found]

try:
    from PySide import QtWidgets as QW  # type: ignore[import-not-found]  # FreeCAD 1.0 (PySide2/6 shim)
except ImportError:  # older FreeCAD with PySide 1 keeps the widgets in QtGui
    from PySide import QtGui as QW  # type: ignore[import-not-found,no-redef]

from . import auth, fcdoc, operations, settings as settings_mod, workfolder
from .client import KeelClient, KeelError
from .structure import StructureError

ICONS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "resources", "icons")


def _icon(name: str) -> str:
    return os.path.join(ICONS, name + ".svg")


def _info(title: str, text: str) -> None:
    QW.QMessageBox.information(FreeCADGui.getMainWindow(), title, text)


def _error(title: str, text: str) -> None:
    QW.QMessageBox.critical(FreeCADGui.getMainWindow(), title, text)


def _ask_text(title: str, label: str, default: str = "") -> str | None:
    value, ok = QW.QInputDialog.getText(FreeCADGui.getMainWindow(), title, label, QW.QLineEdit.Normal, default)
    return value.strip() if ok and value.strip() else None


def _confirm(title: str, text: str) -> bool:
    return QW.QMessageBox.question(FreeCADGui.getMainWindow(), title, text) == QW.QMessageBox.Yes


def _settings() -> settings_mod.Settings:
    return settings_mod.load()


def _client() -> KeelClient:
    s = _settings()
    return KeelClient.from_settings(s, lambda: auth.read_token(s))


def _work_root(s: settings_mod.Settings) -> Path:
    return Path(s.work_root).expanduser() if s.work_root else Path.home() / "KeelWork"


def _guarded(fn):
    """Shows every expected failure in words; an unexpected one is shown with its type and logged to the FreeCAD report view."""
    def wrapper(*a, **k):
        try:
            return fn(*a, **k)
        except (KeelError, auth.TokenError, settings_mod.SettingsError, workfolder.WorkFolderError, StructureError, fcdoc.ExtractionError) as e:
            _error("Keel PLM", str(e))
        except Exception as e:  # noqa: BLE001
            FreeCAD.Console.PrintError(f"Keel PLM: unexpected {type(e).__name__}: {e}\n")
            _error("Keel PLM", f"Unexpected error ({type(e).__name__}); see the Report view.")
    return wrapper


def _active_folder() -> Path | None:
    doc = FreeCAD.ActiveDocument
    return operations.find_folder(doc.FileName) if doc is not None and doc.FileName else None


def _save_modified_documents(folder: Path) -> None:
    """Saves open documents of the folder that were changed in this session. A document is not saved when untouched: FreeCAD rewrites the file
    (new time stamps) on every save, which would make an unchanged file look changed."""
    for name, doc in FreeCAD.listDocuments().items():
        if not doc.FileName or Path(doc.FileName).resolve().parent != folder.resolve():
            continue
        gui_doc = FreeCADGui.getDocument(name)
        if gui_doc is not None and getattr(gui_doc, "Modified", False):
            doc.save()


def _dependencies(folder: Path) -> dict[str, list[str]]:
    deps = {}
    for doc in FreeCAD.listDocuments().values():
        if doc.FileName and Path(doc.FileName).resolve().parent == folder.resolve():
            deps[os.path.basename(doc.FileName)] = fcdoc.document_dependencies(doc)
    return deps


def _pick_root(doc):
    sel = [o for o in FreeCADGui.Selection.getSelection() if fcdoc.is_part(o)]
    if sel:
        return sel[0]
    roots = fcdoc.root_candidates(doc)
    if not roots:
        raise fcdoc.ExtractionError("The active document has no part or assembly to take the structure from.")
    if len(roots) == 1:
        return roots[0]
    labels = [f"{r.Label} ({r.Name})" for r in roots]
    item, ok = QW.QInputDialog.getItem(FreeCADGui.getMainWindow(), "Keel PLM", "Top of the structure:", labels, 0, False)
    if not ok:
        raise fcdoc.ExtractionError("Cancelled.")
    return roots[labels.index(item)]


class _Command:
    name = ""
    menu = ""
    tip = ""
    icon = "keel"

    def GetResources(self):
        return {"Pixmap": _icon(self.icon), "MenuText": self.menu, "ToolTip": self.tip}

    def IsActive(self):
        return True


class Settings(_Command):
    name, menu, tip, icon = "Keel_Settings", "Keel settings...", "Server URL, token file and work folder", "settings"

    @_guarded
    def Activated(self):
        s = _settings()
        url = _ask_text("Keel settings", "Keel server URL (https://...):", s.server_url)
        if url is None:
            return
        s.server_url = settings_mod.validate_server_url(url, s.allow_insecure_http)
        s.token_file = _ask_text("Keel settings", "Token file (a file holding your Keel bearer token, mode 600; leave empty to use KEEL_CAD_TOKEN):", s.token_file) or ""
        s.work_root = _ask_text("Keel settings", "Work folder for check-outs:", s.work_root or str(_work_root(s))) or ""
        path = settings_mod.save(s)
        _info("Keel settings", f"Saved to {path}.\nThe token itself is never stored by this add-on.")


class Checkout(_Command):
    name, menu, tip, icon = "Keel_Checkout", "Check out package...", "Check out a CAD document and its dependents into the work folder", "checkout"

    @_guarded
    def Activated(self):
        number = _ask_text("Check out", "Top document number (for example D-000123):")
        if number is None:
            return
        s = _settings()
        folder = workfolder.folder_for(_work_root(s), number)
        state = workfolder.checkout(_client(), number, folder)
        for path in workfolder.openable_files(folder):
            FreeCAD.openDocument(str(path))
        _info("Check out", f"Checked out {len(state.members)} document(s) into\n{folder}")


class Checkin(_Command):
    name, menu, tip, icon = "Keel_Checkin", "Check in...", "Check the whole package in as one set", "checkin"

    @_guarded
    def Activated(self):
        folder = _active_folder()
        if folder is None:
            raise workfolder.WorkFolderError("The active document is not in a checked-out package folder.")
        comment = _ask_text("Check in", "Comment for the new iterations:")
        if comment is None:
            return
        _save_modified_documents(folder)
        state = workfolder.load_state(folder)
        refs = operations.references_for_checkin(state, _dependencies(folder))
        result = workfolder.checkin(_client(), folder, comment, False, refs)
        _info("Check in", f"Checked in {len(result.get('created', []))} document(s); released {len(result.get('released', []))} unchanged.")


class Undo(_Command):
    name, menu, tip, icon = "Keel_Undo", "Undo check-out", "Release every lock of the package; your local files stay", "undo"

    @_guarded
    def Activated(self):
        folder = _active_folder()
        if folder is None:
            raise workfolder.WorkFolderError("The active document is not in a checked-out package folder.")
        if _confirm("Undo check-out", "Release all locks of this package? Changes are not sent to Keel."):
            workfolder.undo(_client(), folder)
            _info("Undo check-out", "Released. The files in the folder were kept.")


class PushBom(_Command):
    name, menu, tip, icon = "Keel_PushBom", "Push BOM to Keel...", "Send the assembly structure to Keel as a proposal for review", "bom"

    @_guarded
    def Activated(self):
        doc = FreeCAD.ActiveDocument
        if doc is None:
            raise fcdoc.ExtractionError("There is no active document.")
        root = _pick_root(doc)
        extraction = fcdoc.extract_structure(root)
        summary = operations.summarize(extraction)
        text = f"{summary.parts} part(s), {summary.lines} usage line(s)."
        if summary.warnings:
            text += "\n\n" + "\n".join(summary.warnings[:10])
        if not _confirm("Push BOM", text + "\n\nCreate a proposal in Keel? Nothing changes in the BOM until someone reviews and applies it."):
            return
        folder = _active_folder()
        marks_from = workfolder.load_state(folder).top if folder else _ask_text("Push BOM", "Document whose markings the proposal takes (for example D-000123):")
        if marks_from is None:
            return
        s = _settings()
        proposal = operations.push_bom(_client(), extraction, f"{root.Label} from FreeCAD", markings_from=marks_from, require_part_numbers=s.require_part_numbers)
        _info("Push BOM", f"Created proposal {proposal.get('number')}. Review it in Keel under CAD structures.")


class Status(_Command):
    name, menu, tip, icon = "Keel_Status", "Keel status", "Local changes, lock owners and stale drawings", "status"

    @_guarded
    def Activated(self):
        folder = _active_folder()
        if folder is None:
            raise workfolder.WorkFolderError("The active document is not in a checked-out package folder.")
        report = operations.status(_client(), folder)
        lines = [f"Package {report.top} ({report.banner})"]
        for m in report.members:
            lock = f"locked by {m.locked_by}{' (you)' if m.mine else ''}" if m.locked_by else "not locked"
            lines.append(f"{m.number}  {m.role}  {lock}" + (f"  CHANGED: {', '.join(m.changed)}" if m.changed else ""))
        lines.append(f"Stale: {report.stale_count}" + "".join(f"\n  {x['number']} {x['name']}" for x in report.stale))
        _info("Keel status", "\n".join(lines))


class Associate(_Command):
    name, menu, tip, icon = "Keel_Associate", "Mark model-of / drawing-of", "Record which part this model is, and which parts the drawings show", "link"

    @_guarded
    def Activated(self):
        doc = FreeCAD.ActiveDocument
        folder = _active_folder()
        if doc is None or folder is None:
            raise workfolder.WorkFolderError("The active document is not in a checked-out package folder.")
        state = workfolder.load_state(folder)
        root = fcdoc.root_candidates(doc)
        number = fcdoc.part_number(root[0]) if len(root) == 1 else None
        plan = operations.plan_associations(state, os.path.basename(doc.FileName), number, fcdoc.find_drawings(doc))
        text = "\n".join(f"{d} is the {k} {p}" for d, p, k in plan.items) or "Nothing to record."
        if plan.unresolved:
            text += "\n\nSkipped:\n" + "\n".join(plan.unresolved)
        if plan.items and _confirm("Associations", text + "\n\nRecord these in Keel?"):
            report = operations.apply_associations(_client(), plan)
            _info("Associations", f"Recorded {len(report.created)}, already recorded {len(report.existing)}, failed {len(report.failed)}.")
        elif not plan.items:
            _info("Associations", text)


class SetPartNumber(_Command):
    name, menu, tip, icon = "Keel_SetPartNumber", "Set Keel part number", "Add the KeelPartNumber property to the selected part", "number"

    def IsActive(self):
        return bool(FreeCADGui.Selection.getSelection())

    @_guarded
    def Activated(self):
        obj = FreeCADGui.Selection.getSelection()[0]
        current = fcdoc.part_number(obj) or ""
        value = _ask_text("Keel part number", f"Part number of '{obj.Label}':", current)
        if value is None:
            return
        if fcdoc.PART_NUMBER not in obj.PropertiesList:
            obj.addProperty("App::PropertyString", fcdoc.PART_NUMBER, "Keel", "Keel part number")
        setattr(obj, fcdoc.PART_NUMBER, value)


COMMANDS = [Settings, Checkout, Checkin, Undo, PushBom, Status, Associate, SetPartNumber]
TOOLBAR = [c.name for c in COMMANDS]


def register() -> list[str]:
    for cls in COMMANDS:
        FreeCADGui.addCommand(cls.name, cls())
    return TOOLBAR
