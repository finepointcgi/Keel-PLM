"""Checks of the add-on's GUI layer inside a REAL FreeCAD with its GUI (Qt "offscreen" platform: no window is shown). Run by ``run_gui_check.py``, which installs
the add-on into a temporary FreeCAD user-data folder first, so that FreeCAD itself loads ``Init.py`` and ``InitGui.py`` from the package layout.

Not run directly. Environment: KEEL_GUI_RESULT (file for the JSON result). With KEEL_GUI_LIVE=1 it also drives the commands against a real Keel server
(KEEL_GUI_SERVER, KEEL_GUI_TOKEN_FILE, KEEL_GUI_TOP, KEEL_GUI_WORK): the command objects are run through ``FreeCADGui.runCommand`` exactly as a toolbar click would,
with the message boxes and input dialogs replaced by recorders (a modal dialog cannot be answered headless). What is NOT covered is therefore the look of those
dialogs and real mouse clicks on the toolbar buttons.
"""
import json
import os
import sys
import traceback

RESULT = os.environ["KEEL_GUI_RESULT"]
res = {"ok": False, "failures": [], "checks": {}, "info": {}}


def check(name, condition, detail=""):
    res["checks"][name] = bool(condition)
    if not condition:
        res["failures"].append(f"{name}: {detail}")


def finish():
    res["ok"] = not res["failures"] and "error" not in res
    with open(RESULT, "w", encoding="utf-8") as f:
        json.dump(res, f, default=str)
    sys.stdout.flush()
    os._exit(0)  # the GUI event loop would otherwise keep the process alive


def static_checks():
    import FreeCAD
    import FreeCADGui
    from PySide import QtGui, QtWidgets

    res["info"]["freecad"] = ".".join(FreeCAD.Version()[:3])
    res["info"]["userAppData"] = FreeCAD.getUserAppDataDir()
    mod = os.path.join(FreeCAD.getUserAppDataDir(), "Mod", "Keel")
    check("package is in the (temporary) user Mod folder", os.path.isfile(os.path.join(mod, "InitGui.py")), mod)
    check("FreeCAD put the package on sys.path", any(os.path.normpath(p) == os.path.normpath(mod) for p in sys.path), str(sys.path[:6]))
    check("workbench registered by InitGui.py", "KeelWorkbench" in FreeCADGui.listWorkbenches())
    wb = FreeCADGui.listWorkbenches().get("KeelWorkbench")
    res["info"]["workbench"] = {"MenuText": getattr(wb, "MenuText", None), "ToolTip": getattr(wb, "ToolTip", None), "Icon": getattr(wb, "Icon", None)}
    check("workbench menu text", getattr(wb, "MenuText", "") == "Keel PLM")
    icon = getattr(wb, "Icon", "") or ""
    check("workbench icon is a real file", icon and os.path.isfile(icon), repr(icon))

    names_before = set(FreeCADGui.listCommands())
    check("commands are registered lazily (not before the workbench is first used)", not any(n.startswith("Keel_") for n in names_before))
    FreeCADGui.activateWorkbench("KeelWorkbench")
    check("workbench is the active one", FreeCADGui.activeWorkbench().name() == "KeelWorkbench", FreeCADGui.activeWorkbench().name())
    from keel_plm import gui

    expected = [c.name for c in gui.COMMANDS]
    registered = set(FreeCADGui.listCommands())
    for name in expected:
        check(f"command {name} registered", name in registered)
        info = FreeCADGui.Command.get(name).getInfo()
        check(f"command {name} has menu text and tooltip", bool(info.get("menuText")) and bool(info.get("toolTip")), str(info))
        pixmap = info.get("pixmap") or ""
        check(f"command {name} icon file exists", os.path.isfile(pixmap), repr(pixmap))
        check(f"command {name} icon loads in Qt", pixmap and not QtGui.QIcon(pixmap).pixmap(16, 16).isNull(), repr(pixmap))
    check("eight commands", len(expected) == 8, str(expected))

    mw = FreeCADGui.getMainWindow()
    toolbars = [t for t in mw.findChildren(QtWidgets.QToolBar) if t.objectName() == "Keel PLM" or t.windowTitle() == "Keel PLM"]
    check("toolbar 'Keel PLM' exists", len(toolbars) == 1, str([t.objectName() for t in mw.findChildren(QtWidgets.QToolBar)]))
    if toolbars:
        actions = [a.objectName() for a in toolbars[0].actions() if not a.isSeparator()]
        check("toolbar holds the eight commands in order", actions == expected, str(actions))
    menus = [a for a in mw.menuBar().actions() if a.text().replace("&", "") == "Keel PLM"]
    check("menu 'Keel PLM' exists", len(menus) == 1, str([a.text() for a in mw.menuBar().actions()]))
    if menus:
        texts = [a.text() for a in menus[0].menu().actions() if not a.isSeparator()]
        res["info"]["menu"] = texts
        check("menu holds the eight commands", len(texts) == 8, str(texts))
    return gui


class Dialogs:
    """Recorders in place of the modal dialogs of gui.py (the only code that shows any)."""

    def __init__(self, gui):
        self.gui = gui
        self.info, self.error, self.asked, self.answers, self.confirm = [], [], [], [], True
        gui._info = lambda title, text: self.info.append((title, text))
        gui._error = lambda title, text: self.error.append((title, text))
        gui._confirm = lambda title, text: (self.asked.append(("confirm", title, text)), self.confirm)[1]
        gui._ask_text = self._ask

    def _ask(self, title, label, default=""):
        self.asked.append(("ask", title, label))
        return self.answers.pop(0) if self.answers else None

    def reset(self):
        self.info.clear(); self.error.clear(); self.asked.clear(); self.answers.clear(); self.confirm = True


def behaviour_checks(gui):
    import FreeCAD
    import FreeCADGui

    d = Dialogs(gui)
    # Commands that need a context refuse in words, never with a traceback.
    for name in ("Keel_Status", "Keel_Checkin", "Keel_Undo", "Keel_Associate"):
        d.reset()
        FreeCADGui.runCommand(name)
        check(f"{name} without a checked-out folder shows a message", len(d.error) == 1 and "package folder" in d.error[0][1], str(d.error))
    d.reset()
    FreeCADGui.runCommand("Keel_PushBom")
    check("Push BOM without a document shows a message", len(d.error) == 1, str(d.error))

    # Set Keel part number: inactive without a selection, and writes the property of the selected object.
    doc = FreeCAD.newDocument("GuiCheck")
    body = doc.addObject("PartDesign::Body", "Body")
    check("Set part number is inactive without a selection", not FreeCADGui.Command.get("Keel_SetPartNumber").isActive() if hasattr(FreeCADGui.Command.get("Keel_SetPartNumber"), "isActive") else True)
    FreeCADGui.Selection.addSelection(body)
    d.reset(); d.answers.append("KP-777")
    FreeCADGui.runCommand("Keel_SetPartNumber")
    check("Set part number adds KeelPartNumber", "KeelPartNumber" in body.PropertiesList and body.KeelPartNumber == "KP-777", str(body.PropertiesList))
    FreeCADGui.Selection.clearSelection()
    FreeCAD.closeDocument(doc.Name)

    # Settings: dialogs answered, file written under the temporary home, never a token in it.
    from keel_plm import settings as settings_mod
    d.reset(); d.answers.extend(["http://localhost:5999", "", os.path.join(os.environ["HOME"], "work")])
    FreeCADGui.runCommand("Keel_Settings")
    path = settings_mod.default_settings_path()
    res["info"]["settingsPath"] = str(path)
    check("settings are saved under the temporary HOME, not the real one", str(path).startswith(os.environ["HOME"]) and path.exists(), str(path))
    text = path.read_text(encoding="utf-8") if path.exists() else ""
    check("settings hold the URL and no token or password", "http://localhost:5999" in text and "eyJ" not in text and "password" not in text.lower().replace("token_file", ""), text)
    path.unlink(missing_ok=True)


def live_checks(gui):
    """The commands against the real server, through the command objects of the toolbar. The test that starts the server verifies the result there too."""
    import FreeCAD
    import FreeCADGui
    from keel_plm import fcdoc, settings as settings_mod, workfolder

    server, token_file, top, work = (os.environ[k] for k in ("KEEL_GUI_SERVER", "KEEL_GUI_TOKEN_FILE", "KEEL_GUI_TOP", "KEEL_GUI_WORK"))
    d = Dialogs(gui)
    d.answers.extend([server, token_file, work])
    FreeCADGui.runCommand("Keel_Settings")
    check("live: settings saved", not d.error, str(d.error))
    d.reset()

    d.answers.append(top)
    FreeCADGui.runCommand("Keel_Checkout")
    check("live: checkout succeeded", not d.error and any("Checked out 2 document(s)" in t for _, t in d.info), str((d.info, d.error)))
    folder = workfolder.folder_for(work, top)
    res["info"]["openFileNames"] = sorted(x.FileName for x in FreeCAD.listDocuments().values())
    names = sorted(os.path.basename(x.FileName) for x in FreeCAD.listDocuments().values())
    res["info"]["openDocuments"] = names
    check("live: both FreeCAD files opened (once each)", names == ["frame-drw.FCStd", "frame.FCStd"], str(res["info"]["openFileNames"]))
    check("live: folder holds the state", workfolder.has_state(folder))

    model_doc = next(x for x in FreeCAD.listDocuments().values() if x.FileName.endswith("frame.FCStd"))
    drawing_doc = next(x for x in FreeCAD.listDocuments().values() if x.FileName.endswith("frame-drw.FCStd"))
    d.reset()
    FreeCAD.setActiveDocument(drawing_doc.Name)
    FreeCADGui.runCommand("Keel_Associate")
    check("live: associate (drawing-of) ran", not d.error and any("Recorded 1" in t for _, t in d.info), str((d.info, d.error)))
    d.reset()
    FreeCAD.setActiveDocument(model_doc.Name)
    FreeCADGui.runCommand("Keel_Status")
    check("live: status shows both locks", not d.error and d.info and d.info[0][1].count("locked by alice (you)") == 2, str((d.info, d.error)))

    # The user edits the model in the GUI document (screws of the frame 2 -> 3).
    model_doc.getObject("L_screw2").ElementCount = 3
    model_doc.recompute()
    gui_doc = FreeCADGui.getDocument(model_doc.Name)
    res["info"]["guiDocModified"] = getattr(gui_doc, "Modified", None)
    d.reset()
    FreeCADGui.runCommand("Keel_PushBom")
    ok_push = not d.error and any("Created proposal CBP-" in t for _, t in d.info)
    check("live: push BOM created a proposal", ok_push, str((d.info, d.error, d.asked)))
    res["info"]["push"] = [t for _, t in d.info] + [a[2] for a in d.asked if a[0] == "confirm"]

    d.reset(); d.answers.append("Third screw from the GUI")
    FreeCADGui.runCommand("Keel_Checkin")
    check("live: check in succeeded", not d.error and any("Checked in 2 document(s)" in t for _, t in d.info), str((d.info, d.error)))
    check("live: state removed after check-in", not workfolder.has_state(folder))

    # Second round: check out again and undo.
    d.reset(); d.answers.append(top)
    for doc in list(FreeCAD.listDocuments().values()):
        FreeCAD.closeDocument(doc.Name)
    FreeCADGui.runCommand("Keel_Checkout")
    check("live: second checkout succeeded", not d.error, str((d.info, d.error)))
    d.reset()
    FreeCADGui.runCommand("Keel_Undo")
    check("live: undo released the locks", not d.error and any("Released" in t for _, t in d.info), str((d.info, d.error)))
    check("live: state removed after undo", not workfolder.has_state(folder))


def main():
    try:
        gui = static_checks()
        behaviour_checks(gui)
        if os.environ.get("KEEL_GUI_LIVE") == "1":
            live_checks(gui)
    except BaseException as e:  # noqa: BLE001
        res["error"] = f"{type(e).__name__}: {e}"
        res["trace"] = traceback.format_exc()
    finish()


main()
