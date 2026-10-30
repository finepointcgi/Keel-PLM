"""Keel PLM connector logic for FreeCAD (and any other Python host).

Everything in this package except ``gui.py`` is plain Python with no FreeCAD import, so it is unit-tested with pytest against a fake Keel server
(tests/). ``gui.py`` is the only module that imports FreeCAD, FreeCADGui and PySide.

The add-on is a client of the existing REST API under ``/api/v1/cad`` (docs/integration/cad-connector.md). It talks to nothing but the Keel server.
"""

__version__ = "0.1.0"

SCHEMA_ID = "keel.cad.structure/v1"
