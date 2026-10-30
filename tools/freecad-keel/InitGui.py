"""FreeCAD loads this file at start-up (GUI) to register the workbench."""
import FreeCADGui  # type: ignore[import-not-found]


class KeelWorkbench(Workbench):  # noqa: F821 - Workbench is injected by FreeCAD
    MenuText = "Keel PLM"
    ToolTip = "Check out, check in and send BOMs to Keel PLM"
    # FreeCAD executes this file without defining __file__ (found in a real FreeCAD 1.1: the icon was empty), and names defined at the top of the file are not
    # visible inside the class body, so the folder is found through the package (FreeCAD puts the Mod folder of the add-on on sys.path) with imports done here.
    Icon = ""
    try:
        import os as _os

        import keel_plm as _keel_plm

        Icon = _os.path.join(_os.path.dirname(_os.path.dirname(_os.path.abspath(_keel_plm.__file__))), "resources", "icons", "keel.svg")
    except ImportError:
        pass

    def Initialize(self):
        from keel_plm import gui

        names = gui.register()
        self.appendToolbar("Keel PLM", names)
        self.appendMenu("Keel PLM", names)

    def GetClassName(self):
        return "Gui::PythonWorkbench"


FreeCADGui.addWorkbench(KeelWorkbench())
