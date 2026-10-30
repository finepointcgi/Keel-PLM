# Developer guide: commercial CAD plug-ins for Keel

**Nothing in this guide was built or tested for a commercial CAD system.** No SolidWorks, NX, Creo or CATIA plug-in exists in this repository, none of their SDKs were available, and the vendor facts below are a planning summary from general knowledge of those products. Verify each against the vendor's current documentation and licence terms before committing to a design. Product names belong to their owners.

What does exist and is tested: the Keel side of the protocol ([cad-connector.md](cad-connector.md)), the reference tool `keel-cad`, and one open-source connector, the [FreeCAD add-on](freecad-addon.md), which is the closest worked example of the structure an add-in needs (settings, token, client, work folder, structure extraction, thin GUI).

## What each vendor requires (to verify)

| System | Extension mechanism | Language and host | What you need that we do not have |
|---|---|---|---|
| SolidWorks | SOLIDWORKS API add-in (COM, loaded by SolidWorks) | .NET (C# or VB.NET) class library implementing the add-in interface, COM-registered; interop assemblies of the API SDK; Windows only | SolidWorks installation and licence, the API SDK/interop assemblies, a Windows build and test machine |
| NX | NX Open (journals and compiled libraries) | .NET, Python, Java or C++ run inside an NX session (for example from a menu or a start-up library) | NX licence including the NX Open entitlement, an NX installation to build and test against |
| Creo Parametric | J-Link (Java, in process), Web.Link (JavaScript in the embedded browser), Creo Toolkit (C) | Java, JavaScript or C | Creo licence plus the API entitlements for the interface you choose (the Toolkit needs registration with the application) |
| CATIA (V5) | Automation API (COM) for scripts and external programs; CAA (C++) for in-process components | VBA/.NET/Python over COM, or C++ with the CAA framework | CATIA licence, and for CAA the separate CAA development entitlement and toolchain; 3DEXPERIENCE uses different, server-oriented interfaces |

Practical consequences for all four: the add-in runs on the engineer's workstation, in the CAD process or beside it, on the same network as Keel (no internet needed). Decide early between **thin** (the add-in calls the `keel-cad` command-line tool installed on the workstation; the least code, and `keel-cad` is tested) and **native** (the add-in speaks HTTP itself, as the FreeCAD add-on does; a tidier user experience, more to maintain). Either way the CAD-specific code is small: read the reference table of the open document, read custom properties, and read the product structure.

## What the plug-in itself must do

1. **Sign in.** Obtain a JWT bearer token of your identity provider for the signed-in engineer; never handle a password. Use the same sources as `keel-cad`: `KEEL_CAD_TOKEN`, a token file (mode 600), or a command that prints a token; re-read it for each request because tokens are short-lived.
2. **Check out** a package: write each file into a work folder under its `fileName`, verify the SHA-256, keep the manifest (`checkoutId`, members, hashes). Open the model.
3. **Detect changes** by hashing the files in the folder against the manifest. Do not rely on the CAD system's "modified" flag alone, and never save a document that the engineer did not change (many CAD systems rewrite the file on every save).
4. **Declare references**: for each saved document, which other documents of the package it was built against (the CAD system's reference table; for a drawing, its model). Map file names to Keel document numbers through the manifest.
5. **Check in** all changed files and the references as one atomic set, with a comment.
6. **Send the structure** as a proposal in `keel.cad.structure/v1` JSON, with quantities from usage counts, part numbers from a custom property, and the markings of the package's top document.
7. **Show errors in words** (the `code` member, [table](cad-connector.md#error-codes)), and never retry a refused request.

## Skeleton project layout (a plan, not code that exists)

```
keel-<cadsystem>/
  README.md                  what is verified in a real CAD system, and what is not
  src/
    Core/                    NO CAD references: unit-testable without the CAD system
      Settings.*             server URL (https), token source, work folder; never a password
      Auth.*                 token from env / file / command; expiry check
      KeelClient.*           the HTTP calls below, bearer header, no redirects, problem documents to errors
      WorkFolder.*           check-out, hash, upload, check-in, undo, .keel-cad/state.json (keel-cad's format)
      Structure.*            neutral JSON model, builder, validator
    Cad/                     the ONLY code that touches the vendor API
      PartNumbers.*          read the Keel part-number property of a component
      StructureWalker.*      component tree -> Structure (counts of usages, find numbers)
      References.*           reference table / drawing views -> file names
    Ui/                      commands, ribbon/menu, dialogs: thin
  tests/
    Core.Tests/              against a fake Keel server (see tools/freecad-keel/tests/fake_server.py) and a schema check
    Cad.Tests/               against stand-in objects; plus a manual script for the real CAD system
```

Per system, the host project differs: a .NET class library with COM registration (SolidWorks), a .NET or Python library started from an NX menu or start-up folder (NX Open), a Java application registered with Creo (J-Link) or a registered C library (Toolkit), a COM automation client or a CAA component (CATIA). Keep `Core` free of the vendor assemblies so the part that matters is testable on a build server without a CAD licence.

## The exact calls (HTTP, with the `keel-cad` equivalent)

Base path `/api/v1/cad`. All requests: `Authorization: Bearer <token>`. Errors are problem documents with a stable `code` ([error table](cad-connector.md#error-codes)).

| Step | HTTP | `keel-cad` |
|---|---|---|
| Check out | `POST /packages/checkout` body `{"top":"D-000123","includeDependents":true,"allowPartial":false}` gives `201` with `id` and `members[]` (`number`, `cadRole`, `references`, `files[]` with `fileName`, `role`, `sha256`) | `keel-cad checkout D-000123 --dir ./work` |
| Download a file | `GET /checkouts/{id}/members/{number}/files/{sha256}` gives a ZIP: the stored file under its `fileName` plus `MARKINGS.txt`; verify the SHA-256 of the file | (part of checkout) |
| Upload a changed file | `PUT /checkouts/{id}/members/{number}/files?fileName=asm.sldasm&role=primary`, raw body, header `X-Content-SHA256: <hex>` | `keel-cad push --dir ./work` |
| Add a new document (a new drawing) | `POST /checkouts/{id}/members` body `{"name":"Section","documentType":"Drawing","cadRole":"drawing","references":["D-000123"]}`, then upload its file as above | `keel-cad add ./work/s.slddrw --dir ./work --name Section --role drawing --type Drawing --references D-000123` |
| Check in the set | `POST /checkouts/{id}/checkin` body `{"comment":"...","skipUnchanged":false,"members":[{"number":"D-000124","references":["D-000123"],"files":[{"fileName":"s.slddrw","sha256":"..."}]}]}` | `keel-cad checkin --dir ./work --comment "..." --refs-file refs.json` |
| Undo | `POST /checkouts/{id}/undo` | `keel-cad undo --dir ./work` |
| Register a document's CAD role | `PUT /documents/{number}/role` body `{"role":"model","cadSystem":"SolidWorks"}` | none (use the API) |
| Send the structure | `POST /bom-proposals`, multipart: field `metadata` FIRST, `{"title":"...","format":"json","markings":{...},"rootNumber":"P-000123"}`, then field `file` (the structure JSON) | `keel-cad bom push s.json --title ... --markings-from D-000123` |
| Markings for the proposal | `GET /api/v1/documents/{number}` and copy the `markings` members | (done by `--markings-from`) |
| Record model-of / drawing-of | `POST /associations` body `{"documentNumber":"D-000124","partNumber":"P-000123","kind":"drawing-of"}`; `409` means already recorded | none |
| Stale drawings | `GET /stale?limit=200`; per package `GET /documents/{number}/package` | none |

Rules to respect: a package check-out is all-or-nothing and a `cad.locked` answer takes nothing; every member gets exactly one new iteration at check-in; a declared reference asserts the document was regenerated against the current model; the server never reads native files, so everything about structure and references is what the plug-in declares; related documents the user may not see are counted, never named; markings and audit rules are those of the rest of Keel and are not weakened by CAD. Use the JSON Schema `docs/integration/cad/structure-v1.schema.json` (also at `GET /schemas/structure-v1`) to validate before sending.

## Testing without the CAD system

Build `Core` against a fake server and the published schema, as `tools/freecad-keel/tests` does (84 tests; the fake models the documented protocol and is not the Keel server). Then test the CAD layer in the real system by hand with a short script, and record plainly in the plug-in's README what was run in a real CAD system and what was not. The server side is covered by the .NET `CadApiTests` and `CadServiceTests`.
