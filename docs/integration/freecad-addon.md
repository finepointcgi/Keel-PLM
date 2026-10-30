# FreeCAD add-on for Keel PLM

An open-source CAD connector built on [FreeCAD](https://www.freecad.org/): a workbench that checks packages out and in, sends the assembly structure to Keel as a BOM proposal, shows lock and stale state, and records which drawing shows which part. It is a client of the existing CAD API ([cad-connector.md](cad-connector.md)); it adds nothing to the server and talks to the Keel server only (air-gapped by construction).

Source: `tools/freecad-keel/`. Decision: [ADR-0047](../adr/0047-open-source-cad-connector.md). Notes and assumptions (OSS-CAD-n): [increments/9.2-open-source-cad.md](../increments/9.2-open-source-cad.md).

## Status: read this first

| Part | Verified how |
|---|---|
| Logic (settings, token handling, HTTPS client, work folder, structure extraction, payload, validation, associations, status), 84 pytest tests | Run, against a **fake Keel server on real sockets** and **stand-in FreeCAD objects**. The fake models the documented protocol; it is not the Keel server. |
| The payload it builds | Validated in the tests against the published JSON Schema (`docs/integration/cad/structure-v1.schema.json`). The .NET side reads real STEP files end to end (see the notes), and `keel-cad`'s state file format is reused and tested. |
| **FreeCAD object model assumptions** (`App::Link` / `ElementCount`, TechDraw `Source`, external links, `getParentGeoFeatureGroup`) | **Verified in real FreeCAD 1.1.4 (macOS, 2026-10-29)**: `scripts/freecad_headless_check.py` (headless `freecadcmd`) and the live E2E test, which builds a six-line assembly with link arrays and a TechDraw drawing in a separate file linked through an external `App::Link`, extracts it and gets the expected quantities, part numbers and file dependencies. Not tried: FreeCAD 1.0, Windows, Linux, `Assembly::AssemblyObject` with joints, `App::LinkGroup`, real-world models. |
| **The FreeCAD GUI layer** (`keel_plm/gui.py`, `InitGui.py`, `Init.py`, `package.xml`) | **Verified in the real FreeCAD GUI on Qt's offscreen platform** (`scripts/run_gui_check.py`, 53 checks): the package is copied to a temporary user-data `Mod/Keel` and loaded by FreeCAD itself; the workbench registers, commands register when the workbench is first used, all 8 commands have text, tooltip and icons that load, the toolbar and menu hold them, and the commands run through `FreeCADGui.runCommand`. The live E2E test then drives Settings, Check out, Associate, Status, Push BOM and Check in, plus Check out and Undo, through those command objects against the real server. **Not verified**: how the dialogs look (they are replaced by recorders because a modal dialog cannot be answered headless), real mouse clicks on toolbar buttons, a visible window, Windows and Linux, the add-on manager install (only the copy-to-`Mod` layout). |
| **Against the real Keel API over HTTP** | **Run** (`tests/Keel.E2E/AddOnLiveTests.cs`, `KEEL_E2E=1`): real Keycloak bearer tokens of the dev user (password grant of the dev client), real host, PostgreSQL and vault. The add-on's Python client runs under plain Python and inside FreeCAD's own Python: check-out with hash verification, change, atomic check-in as a set, stale-drawing status, undo, a refusal with its `cad.locked` code, push of a structure extracted from a real FreeCAD document, review and apply through the API, EBOM lines and quantities compared with those of the STEP export of the same structure. `keel-cad` is run the same way, including real STEP fixtures (AP214 and AP242) to an applied EBOM. Not run: HTTPS with a private CA, the token command, tokens near expiry, large files. |

Findings from these runs are listed under "Found by the real runs" below.

## Install

1. Copy (or symlink) `tools/freecad-keel` to FreeCAD's `Mod` folder as `Keel` (for example `~/.local/share/FreeCAD/Mod/Keel` on Linux, `%APPDATA%\FreeCAD\Mod\Keel` on Windows, `~/Library/Application Support/FreeCAD/Mod/Keel` on macOS), or install it with the add-on manager from your internal add-on repository (`package.xml` is in the standard layout). FreeCAD 1.0 or newer is assumed (Link and Assembly objects).
2. Restart FreeCAD and select the **Keel PLM** workbench. The commands are in its toolbar and menu.
3. **Keel settings**: the server URL (`https://...`; plain `http` is refused except to `localhost`), the work folder, and where your token comes from.

The add-on needs only the Python standard library that ships with FreeCAD. No pip install, no internet.

## Signing in (no passwords)

Keel never sees passwords and neither does the add-on. It uses the same bearer-token mechanism as the `keel-cad` command-line tool: a JWT issued by your site's identity provider. In the order tried:

1. the environment variable `KEEL_CAD_TOKEN`;
2. the **token file** from the settings (a file holding the token; on Linux and macOS it must not be readable by others: `chmod 600`, as for an SSH key);
3. the **token command** from the settings: a program that prints a token, run without a shell. Use it to wrap whatever your site uses to sign in and renew.

The token is read again for every request, so a renewed file is picked up. `keel-cad` has no device-code or interactive sign-in flow today, so the add-on has none either; the settings file stores paths and flags, never a token or password (a test checks this). If your identity provider offers a device flow, a small script that completes it and writes the token file is the intended integration.

TLS: use `https` and, when the server certificate comes from a private CA (typical on an air-gapped site), set the **CA file** in the settings. A redirect from the server is refused, so the token cannot be sent to another host.

## Marking parts in FreeCAD

Keel matches parts by number. Give each part a custom property **`KeelPartNumber`** (select the object, command **Set Keel part number**). A part is an `App::Part`, an Assembly object, a `PartDesign::Body`, or any object that has the property. Optional properties:

| Property | On | Meaning |
|---|---|---|
| `KeelPartNumber` | part | the Keel part number (matched by number) |
| `KeelPartName` | part | name for a new part (default: the object's label) |
| `KeelUnit`, `KeelSource` | part | unit; `Make` or `Buy` |
| `KeelAttr_<attribute>` | part | an administrator-defined attribute, used when Keel creates the part |
| `KeelFindNumber`, `KeelRefDes` | link | find number (default 10, 20, ... in order of appearance); reference designators |
| `KeelExclude` | link or part | leave this usage out of the BOM |

Quantities are **counts of usages**: two `App::Link` objects to the same part under one assembly are quantity 2; a link array counts its `ElementCount`. Plain `App::DocumentObjectGroup` folders are transparent; loose geometry inside a part is part of that part, not a BOM line. A link to a part in another document is followed (an external link), so a multi-file assembly gives one structure. A part with no `KeelPartNumber` is still sent (Keel proposes it as a new part) with a warning, unless **require part numbers** is set.

## Commands

| Command | What it does |
|---|---|
| **Check out package** | Asks for the top document number, checks out the package (the model, everything it references, and the drawings made from them) as one atomic set, writes the files to `<work folder>/<number>` verifying every SHA-256, and opens the FreeCAD files. A conflict (someone else holds a lock) takes nothing. If the folder cannot be filled, the set is undone so you never hold invisible locks. |
| **Check in** | Saves the open documents you changed (an untouched document is not saved: FreeCAD rewrites a file on every save, which would make it look changed), asks for a comment, and checks the whole set in atomically. References between documents are declared from FreeCAD's external links and TechDraw sources; declaring a reference says the document was regenerated against the current model, so check in a drawing only after you opened and updated it. |
| **Undo check-out** | Releases every lock; the local files stay. |
| **Push BOM to Keel** | Walks the selected part (or the single top part of the document; asks when there are several), shows part and line counts and warnings, and creates a **structure proposal** in Keel, marked like the package's top document. Nothing changes in the BOM until someone reviews and applies it in Keel under CAD structures. |
| **Keel status** | Local changes, who holds each lock, stale drawings of the package (as in the CAD tab of the document page). |
| **Mark model-of / drawing-of** | Records that this file is the model of the part, and that the file that holds a TechDraw page is the drawing of every numbered part the page's views show. Already-recorded pairs are fine. |

The structure format has no place for drawings (the schema is closed, see [cad-connector.md](cad-connector.md)), so drawing relations travel as check-in references (stale detection) and associations, not inside the BOM proposal.

The work folder is the same folder format `keel-cad` uses (`.keel-cad/state.json`), so `keel-cad watch`, `keel-cad status`-style tooling and the add-on can work on one folder.

## Tests

```
scripts/test-addons.sh
```

Creates a virtual environment (`.venv-addons`, or `KEEL_ADDON_VENV`), installs `pytest` and `jsonschema` into it (never globally; `KEEL_ADDON_PIP_ARGS="--no-index --find-links DIR"` installs from a local folder on an air-gapped machine), runs the tests, then two optional stages when FreeCAD is found (`/Applications/FreeCAD.app`, `PATH`, or `KEEL_FREECAD_CMD` / `KEEL_FREECAD_GUI`; never downloaded): `scripts/freecad_headless_check.py` under `freecadcmd`, and `scripts/run_gui_check.py`, which copies the add-on to a temporary user-data folder and runs `scripts/freecad_gui_check.py` inside the real FreeCAD GUI with `QT_QPA_PLATFORM=offscreen`. HOME and `FREECAD_USER_HOME` point at the temporary folder, so your own FreeCAD configuration and documents are not touched. The script says plainly when FreeCAD is missing and what that leaves unverified. It is not part of `scripts/test.sh`.

Against a real server (needs Docker; starts its own throwaway stack): `KEEL_E2E=1 dotnet test --project tests/Keel.E2E --filter-class "*AddOnLiveTests"`. Three tests: the Python client and `keel-cad` with a real STEP assembly (no FreeCAD needed), the real FreeCAD document through the add-on, and the GUI commands in the real FreeCAD GUI. The FreeCAD tests skip with a message when FreeCAD is not installed.

### Found by the real runs (2026-10-29)

- **Status missed stale drawings of a model.** The server's `staleCount` counts only what the top document was built against, so for a model whose drawing is out of date it is 0; the add-on trusted it. Fixed (`operations.status` counts the stale nodes of both directions); the fake server had the same wrong model and was corrected.
- **The workbench had no icon.** FreeCAD runs `InitGui.py` without `__file__`, and names defined at the top of the file are not visible in the class body. Fixed in `InitGui.py`.
- **Saving an unchanged document did not change its bytes** in this FreeCAD (OSS-CAD-7 assumed it might). Check-in still saves only documents modified in the session; one sample is not a guarantee.
- **TechDraw and other files:** a drawing in its own file cannot take a part of another file in `View.Source` directly; use an external `App::Link` in the drawing's document (`fcdoc` follows it).
- **Symlinked folders (macOS `/var` to `/private/var`)** made FreeCAD store a `../../..` path in a drawing's external link, so after a check-out FreeCAD opened a second copy of the model from the old location. The add-on now opens files by their real path; keep work folders off symlinked paths.

What the tests cover: URL, settings and token rules; the HTTP client (bearer header, problem documents to errors with the `cad.*` code, redirect refused, unreachable server); check-out, change detection by hash, upload, atomic check-in with hashes and references, undo, conflicts, hash-mismatch cleanup, zip entries and file names that try to leave the folder; structure extraction (quantities, link arrays, folders, excluded usages, external links, cycles, depth, missing numbers, TechDraw sources); the payload against the JSON Schema and the server's validation rules; push BOM, associations, status; the package layout (`package.xml` against `InitGui.py`, icons, that only `gui.py` imports FreeCAD, that only `client.py` touches the network).

Regenerating the STEP test files: `tools/freecad-keel/make_fixtures.py` (needs CadQuery in a throwaway venv; the script documents how).
