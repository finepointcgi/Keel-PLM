"""FreeCAD loads this file at start-up (GUI) to register the workbench."""
import os

import FreeCADGui  # type: ignore[import-not-found]


class KeelWorkbench(Workbench):  # noqa: F821 - Workbench is injected by FreeCAD
    MenuText = "Keel PLM"
    ToolTip = "Check out, check in and send BOMs to Keel PLM"
    Icon = os.path.join(os.path.dirname(os.path.abspath(__file__)), "resources", "icons", "keel.svg") if "__file__" in globals() else ""

    def Initialize(self):
        from keel_plm import gui

        names = gui.register()
        self.appendToolbar("Keel PLM", names)
        self.appendMenu("Keel PLM", names)

    def GetClassName(self):
        return "Gui::PythonWorkbench"


FreeCADGui.addWorkbench(KeelWorkbench())
