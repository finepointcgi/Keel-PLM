# Watch-folder and export recipes: connecting any CAD system that can write files

A CAD system without a plug-in can still feed Keel if it can write STEP, CSV or its own files into a folder. There are two different jobs, and `keel-cad` has a different command for each. Read this before choosing, because "watch" does less than the name suggests.

| Job | Command | What it does |
|---|---|---|
| **Send a structure (BOM)** exported as CSV, STEP, XML or JSON | `keel-cad bom push <file> --title ... --markings-from <document>` | One shot. Creates a structure proposal that a person reviews and applies in Keel. Nothing is changed in the BOM directly. |
| **Version files** (native files, STEP, PDF) of a document that exists in Keel | `keel-cad checkout`, then `keel-cad watch`, then `keel-cad checkin` | `watch` uploads **changed tracked files** of a checked-out package to the working copy, every few seconds (polling, default 5 s). It never checks in, and it does **not** discover new files: add those with `keel-cad add`. |

Both need `--server` (or `KEEL_CAD_URL`) and a token (`--token-file` or `KEEL_CAD_TOKEN`); see [cad-connector.md](cad-connector.md). Neither tool reads native CAD files: Keel stores the bytes and the meaning (what references what) is what you declare.

## The pattern for versioning files

```
keel-cad checkout D-000123 --dir ./work/D-000123          # the files appear; you hold the locks
# ... the CAD system exports/overwrites files with THE SAME NAMES in ./work/D-000123 ...
keel-cad watch --dir ./work/D-000123 --interval 5        # leave running; uploads changes to the working copy
keel-cad add ./work/D-000123/new-section.pdf --dir ./work/D-000123 --name "Section view" --role derived-pdf --type CAD --references D-000123
keel-cad checkin --dir ./work/D-000123 --comment "Rev B geometry" [--refs-file refs.json]
# or: keel-cad undo --dir ./work/D-000123
```

Rules that matter: file names inside a package folder must be unique and the CAD export must write **the same file name** as the checked-out file (a new name is a new file, which needs `add`). `checkin` uploads whatever changed since the last upload, verifies the hashes, and writes one new iteration of every member atomically. `refs.json` maps a document number to the numbers it was built against, for example `{ "D-000124": ["D-000123"] }` for a PDF made from the model; declaring a reference says the PDF was regenerated against the current model, which is what clears a "stale" flag.

## The pattern for sending a structure

Export the BOM from the CAD system, convert if needed to the CSV below (or the neutral JSON), and push:

```
keel-cad bom push board-keel.csv --title "Board rev B" --markings-from D-000123 --server https://keel.example.org
```

The proposal is marked like the document named in `--markings-from` (a proposal is marked like the data it comes from) and shows up under **CAD structures** in Keel. CSV columns: `parent`, `child` (the part number), `name`, `qty`, `find`, `refdes`, and optional `unit`, `type`, `source`, `attr.<attribute>`. Quantities, find numbers and reference designators come from the file. Full format: [cad-connector.md](cad-connector.md#structure-formats).

A script that runs after each export is enough: `export.sh` (CAD batch export), then `keel-cad bom push`, then done. No daemon is needed for structures.

## KiCad (electronics)

KiCad is a good fit: the BOM is data, not geometry.

1. In the schematic editor add a symbol field **`KeelPN`** holding the Keel part number of each component (Schematic Setup, Field Name Templates, so every new symbol gets it).
2. Export the BOM grouped by value and footprint, including `KeelPN`. With KiCad 8 or newer from the command line: `kicad-cli sch export bom --fields 'Reference,Value,Footprint,${QUANTITY},KeelPN,${DNP}' --labels 'Refs,Value,Footprint,Qty,KeelPN,DNP' --group-by 'Value,Footprint' --output board-bom.csv board.kicad_sch` (option names as of KiCad 8; check `kicad-cli sch export bom --help` on your version).
3. Convert and push with the recipe in this repository, `tools/cad-recipes/kicad_bom_to_keel_csv.py` (Python standard library only; tested):

```
python3 tools/cad-recipes/kicad_bom_to_keel_csv.py board-bom.csv --board P-000123 --pn-field KeelPN --out board-keel.csv
keel-cad bom push board-keel.csv --title "Board rev B" --markings-from D-000123
```

`--board` is the Keel part number of the board assembly, the parent of every line. Rows with the same part number merge (quantities add, reference designators join), rows marked DNP are dropped, a row without `KeelPN` stops the run (or is skipped with `--skip-unnumbered` and a warning), and reference designators Keel would refuse are dropped with a warning. The sample input (`tests/Keel.UnitTests/Fixtures/cad/kicad-board-bom.csv`) was written by hand in the format above, **not produced by a real KiCad**; the converted file is read by the real CSV importer in a unit test.

Native KiCad files (`.kicad_sch`, `.kicad_pcb`) and the Gerber/PDF outputs can be versioned with the file pattern above (check out the document, let a release script overwrite the files, `watch`, `checkin`).

**KiCad Python API sketch (not run).** KiCad 7 and newer can run a Python script in the PCB editor (the `pcbnew` module) or a BOM plug-in. A sketch that reads footprints and writes the CSV the recipe expects, to adapt, not to trust:

```python
import csv, pcbnew
board = pcbnew.GetBoard()
rows = {}
for fp in board.GetFootprints():
    pn = fp.GetFieldByName("KeelPN").GetText() if fp.HasFieldByName("KeelPN") else ""
    if not pn or fp.IsDNP():
        continue
    r = rows.setdefault(pn, {"Refs": [], "Value": fp.GetValue()})
    r["Refs"].append(fp.GetReference())
with open("board-bom.csv", "w", newline="") as f:
    w = csv.writer(f)
    w.writerow(["Refs", "Value", "Qty", "KeelPN", "DNP"])
    for pn, r in rows.items():
        w.writerow([", ".join(sorted(r["Refs"])), r["Value"], len(r["Refs"]), pn, ""])
```

The scripting API changes between KiCad versions (`HasFieldByName`, `IsDNP` and so on); check it against your version's documentation.

## OpenSCAD and CadQuery (code-CAD)

Build scripts are a natural place to call Keel: the part and its structure are produced by code, so the BOM can be produced by the same code.

- **CadQuery**: `tools/freecad-keel/make_fixtures.py` is a working example of building named parts and assemblies and exporting STEP (AP214, AP242 or AP203). Part numbers go in as the STEP product id, so `keel-cad bom push frame.step --root KA-3000 ...` reads the structure (quantities are the counts of occurrences). Give every part its number as the label name when you export; the example shows the labels. For a STEP of a sub-assembly use `--root`.
- **OpenSCAD** exports STL, 3MF, DXF, SVG and similar, none of which carries structure. Write the BOM yourself: have the build script print the neutral JSON or CSV next to the model (a `module part(number, name)` helper that echoes a line is a common trick), then `keel-cad bom push`. The files themselves (`.scad`, the STL/3MF) are versioned with the file pattern above; Keel measures STL, OBJ and 3MF on upload (model versions, ADR-0046).

A build recipe, in `Makefile` form:

```make
D ?= D-000123
all: model.step bom.csv
model.step: model.py ; python3 model.py            # writes model.step with product ids = part numbers
publish: all
	keel-cad bom push bom.csv --title "Build $(shell git rev-parse --short HEAD)" --markings-from $(D)
	cp model.step work/$(D)/ && keel-cad push --dir work/$(D)   # after: keel-cad checkout $(D) --dir work/$(D)
```

## Onshape and Fusion (cloud CAD, export-only)

Both are cloud services and are **not reachable from an air-gapped site**: nothing here can call them from inside the Keel network. What works is a person (or a machine on the connected side) exporting and carrying the files across your normal transfer process, then using the patterns above on the Keel side:

- **Onshape**: export the assembly as STEP (Part Studio or Assembly, AP242 or AP214 as offered) and its BOM table as CSV. Push the BOM CSV, or the STEP, with `keel-cad bom push`; the STEP needs part numbers as product ids, which depends on what the assembly's part-number property holds. Check a sample before relying on it.
- **Fusion**: export the assembly as STEP (File, Export) and the parts list as CSV. Same treatment. Component names, not part numbers, end up in a STEP unless you set them as such first.

No claim is made that either export has been tried here. If the BOM CSV has other column names, a short script (or the KiCad converter as a template) maps them to `parent,child,qty,...`.

## Checks before you rely on a recipe

- Push to a test Keel or look at the proposal in the review page first: the diff against the current EBOM shows exactly what would change, and nothing changes until someone applies it.
- Part numbers must be the ones Keel knows; unknown numbers are proposed as new parts.
- Markings: the proposal takes the markings of `--markings-from`; the operator must be allowed to modify data under them, and the usual access rules apply to everything.
