# CAD connector developer guide

This guide is for the person who connects a CAD system to Keel: a vendor plug-in author, or someone writing a script. It describes the protocol that Keel offers, the neutral structure format, the error codes, and what Keel does and does not provide. The machine-readable description of the API is [cad-openapi.json](cad-openapi.json) (OpenAPI 3.1). The reference implementation is the `keel-cad` command-line tool (`src/Keel.CadAgent`); it uses nothing but this API.

## What is built, and what is not

**Built and tested, vendor-neutral:**

- The REST API under `/api/v1/cad` (check-out and check-in of a package, uploads and downloads, dependency graph, stale detection, associations, structure proposals).
- A neutral structure format (`keel.cad.structure/v1`, JSON Schema in [cad/structure-v1.schema.json](cad/structure-v1.schema.json)) and importers for STEP product structure (AP242, AP214, AP203), CSV and XML.
- The reference command-line tool `keel-cad`.
- The pages: the CAD tab of a document, the list of structure proposals and the review page.

**NOT built (and not claimed):**

- No plug-in for NX, Creo, SolidWorks, CATIA or any other CAD system. Each needs the vendor's SDK and licence, which we do not have. Nothing in this repository has been run against a real CAD system.
- No reading of CAD native files. Keel never opens a `.prt`, `.asm`, `.sldprt`, `.CATPart` and so on. It stores the bytes, hashes them, scans them for malware and hands them back. All meaning (which file references which) is declared by the connector.
- No geometry, no PMI, no thumbnails, no viewer support for native files (the existing viewer shows STEP, PDF and images only).
- STEP: explicit quantity entities (`QUANTIFIED_ASSEMBLY_COMPONENT_USAGE`), placements and complex-entity instances are not read. The quantity of a child is the number of usage occurrences under its parent.
- No automatic release: nothing in this protocol releases, signs or approves anything.

**What a plug-in author needs to implement** (the plug-in is a thin client of the API below):

1. Authentication: obtain a JWT bearer token for the signed-in user from your identity provider and send it as `Authorization: Bearer ...`. Keel never sees passwords. Tokens are short-lived: renew before they expire (`keel-cad` takes a token from `--token`, `--token-file` or `KEEL_CAD_TOKEN`).
2. Check-out: call `POST /packages/checkout` for the top document, write every file of the manifest into the CAD working folder under its `fileName`, and verify each against its `sha256`.
3. A way to find out which files changed (hash them, as `keel-cad` does) and which documents each file references (the plug-in knows this from the CAD system's own reference table).
4. Check-in: upload changed files (`PUT .../files`, with the SHA-256 header), add new documents (`POST .../members`), then `POST .../checkin` with the references of every member.
5. Send the product structure as a proposal (`POST /bom-proposals`) in the neutral JSON, or in STEP, CSV or XML.
6. Show the error codes below to the user in words, and never retry a request that Keel refused.

## Concepts

- A **CAD document** is an ordinary Keel document (type `CAD` or `Drawing`). Its native file is its primary file; extra outputs may be attachments. Markings, lifecycle, revisions, iterations, malware scanning and audit are exactly those of every other document.
- A **package** is a set of documents checked out together: the top document, everything it references (recursively), and the derived documents (drawings, PDFs, STEP files) that were made from any of them. Each member has a **CAD role**: `model`, `drawing`, `derived-pdf` or `step`. A document with no registered role is a drawing if its type is Drawing, a model otherwise; roles are registered at the first check-in or with `PUT /documents/{number}/role`.
- The **lock** is the document check-out. A package check-out takes the lock of every member in one transaction: either all of them or none. While you hold it, nobody else can check out, change or release those documents.
- A **dependency** says: iteration D-000002 A.3 was built against document D-000001 at revision A, iteration 2. Dependencies are written by the check-in, in the same transaction as the iterations, and never change.
- A document is **stale** when a document it was built against has a later iteration than the one recorded. Stale drawings and PDFs are listed at `GET /stale` and flagged on the document page.
- An **association** says that a document is the `model-of` or the `drawing-of` a part.
- A **structure proposal** is a proposed engineering BOM made from a CAD structure. It is reviewed against the current EBOM and applied through the normal part and BOM services.

## Authentication and access

Every call needs a valid bearer token and a Keel role (`plm-viewer` to read, `plm-author` to check out and change). The access rules are those of the rest of Keel and are never weakened by CAD:

- A document or part the user may not see does not exist for them: Keel answers `404` exactly as for a missing one, and audits the attempt. Related documents they may not see are counted (`redactedCount`) but never named.
- A package check-out that would leave out documents the user cannot see is refused with `cad.package_incomplete` unless `allowPartial` is set.
- New documents created by `POST .../members` take the markings of the top document.
- Downloads come as the marked ZIP of ordinary document downloads (`MARKINGS.pdf`, `MARKINGS.txt` and the stored file, unchanged). A PDF is not stamped in place for connectors, so the bytes you get back are the bytes that were stored. Keep the markings text next to the file; `keel-cad` writes `<file>.markings.txt`. The `X-Keel-Markings` header carries the banner.
- Everything is audited. Audit entries hold document and part numbers, counts and hashes, never names, titles or attribute values.

## Sequence: check-out, work, check-in

```
CAD plug-in                                  Keel
    |  POST /packages/checkout {top}           |
    |----------------------------------------->|  authorize top (404 if hidden)
    |                                          |  resolve members (graph, role filter)
    |                                          |  lock ALL members in one transaction
    |  201 {id, members[files, sha256], banner}|
    |<-----------------------------------------|
    |  GET /checkouts/{id}/members/{n}/files/{sha}   (per file)
    |----------------------------------------->|  verify vault object, audit download
    |  200 ZIP (cover sheet + file)            |
    |<-----------------------------------------|
    |  ... user edits in the CAD system ...    |
    |  PUT /checkouts/{id}/members/{n}/files?fileName=..   X-Content-SHA256: ..
    |----------------------------------------->|  stream through the vault, hash, scan;
    |                                          |  a hash that differs ends the upload
    |  200 file  | 422 cad.integrity_mismatch  |
    |<-----------------------------------------|
    |  POST /checkouts/{id}/members {new drawing}   (optional)
    |  POST /checkouts/{id}/checkin {members[references, files[sha]]}
    |----------------------------------------->|  verify expected hashes, validate references
    |                                          |  ONE transaction: an iteration of every member,
    |                                          |  dependency rows, set closed, audit
    |  200 {created[], released[], setClosed}  |
    |<-----------------------------------------|
```

Abandoning work: `POST /checkouts/{id}/undo` releases every lock (the working copies are discarded). An open set that belongs to you can be found again with `GET /checkouts`.

## Sequence: structure to proposal to BOM

```
CAD plug-in / script        Keel                              Reviewer
    | POST /bom-proposals (multipart: metadata, file)           |
    |---------------------->| parse, validate, match parts      |
    |  201 proposal         | (by number, then attribute)       |
    |<----------------------| store as draft; nothing changes   |
    |                       |                                   |
    |                       |<-- open /cad/bom-proposals/{n} ---|
    |                       |    see matches and the diff       |
    |                       |<-- PUT .../decisions -------------|  choose, create or skip
    |                       |<-- POST .../apply ----------------|
    |                       | create missing parts; for each changed parent:
    |                       |   check out, edit lines, check in (new working iteration)
    |                       |   refused when the parent is released or checked out by someone else
```

Nothing is auto-released. A released assembly is never changed: the proposal reports it as blocked and nothing at all is changed until the cause is removed (use a change notice to revise it). If an apply stops half way (for example a part could not be created), the proposal stays a draft with a stored report, parts already created are remembered, and applying again continues without duplicates.

## Endpoints

All paths are below `/api/v1/cad`. Bodies are JSON unless stated.

| Method and path | Purpose |
|---|---|
| `POST /packages/checkout` `{top, roles?, includeDependents?, allowPartial?}` | Check out a package. 201 with the manifest. |
| `GET /checkouts` | Your open sets. |
| `GET /checkouts/{id}` | The manifest of a set you own (administrators may read any). |
| `POST /checkouts/{id}/members` `{name, documentType, cadRole, description?, references?}` | Add a new document to an open set; it is checked out to you. |
| `PUT /checkouts/{id}/members/{number}/files?fileName=..&role=primary\|attachment` | Upload one file. Raw body. Header `X-Content-SHA256` (required). |
| `GET /checkouts/{id}/members/{number}/files/{sha256}` | Download one file (marked ZIP). |
| `POST /checkouts/{id}/checkin` `{comment?, skipUnchanged?, members?[{number, comment?, references?, files?[{fileName, sha256}]}]}` | Check the set in atomically. |
| `POST /checkouts/{id}/undo` | Release every lock of the set. |
| `PUT /documents/{number}/role` `{role, cadSystem?}` | Register the CAD role of a document. |
| `GET /documents/{number}/package?levels=` | Dependency view: uses, used by, parts, stale count. |
| `GET /documents/{number}/where-used?levels=` | Where a CAD file is used. |
| `GET /stale?limit=` | Stale drawings, PDFs and STEP files you may see. |
| `POST /associations` `{documentNumber, partNumber, kind}` / `DELETE /associations/{id}` | `model-of` or `drawing-of`. |
| `GET /parts/{number}/documents` | The CAD documents associated with a part. |
| `POST /bom-proposals` (multipart) | Create a proposal. Field `metadata` (JSON, first), field `file`. |
| `GET /bom-proposals?status=` / `GET /bom-proposals/{number}` | List; read with parts, lines and the diff. |
| `PUT /bom-proposals/{number}/decisions` `{decisions[{ref, decision, matchNumber?}], applyRemovals?}` | `decision` is `match`, `create` or `skip`. |
| `POST /bom-proposals/{number}/apply` / `.../discard` | Apply to the engineering BOMs; or discard. |
| `GET /schemas/structure-v1` | The JSON Schema of the neutral structure. |

### Check-in semantics

- `members` omitted: every open member is checked in. Each gets exactly one new iteration (an unchanged member too), unless `skipUnchanged` is true: then a member whose working copy equals its latest iteration, and whose declared references are unchanged and fresh, is released instead.
- `references` (document numbers) is what the new iteration was built against. For a member checked in together with a referenced member, the dependency records the referenced member's new iteration. If `references` is omitted for an existing member, its previous dependencies carry forward unchanged: a drawing stays stale until the connector declares the reference again, which asserts that the drawing was regenerated against the current model. Declaring a reference that is stale counts as a change, so it is never skipped.
- `files` (optional) is a list of `{fileName, sha256}` the connector expects to be in the working copy; a difference refuses the whole check-in with `cad.integrity_mismatch`.
- If any member is refused (locked revision, quarantined file, missing primary file), nothing is checked in.

## Error codes

Errors are RFC 9457 problem documents. A refusal that belongs to the CAD protocol carries a stable `code` member; the `title` is a sentence for people. Other refusals use the ordinary status codes of the Keel API.

| Status | `code` | Meaning | What to do |
|---|---|---|---|
| 409 | `cad.locked` | A member is checked out by someone else, or its revision is locked (under review, released, obsolete). Nothing was locked. | Show the title; ask the holder, or use a change notice for a released document. |
| 409 | `cad.package_incomplete` | Some referenced documents are not visible to you. | Check out with `allowPartial`, or ask for access. |
| 422 | `cad.integrity_mismatch` | The bytes received, or the file in the working copy, do not match the SHA-256 you gave. | Re-send. Never ignore it. |
| 409 | `cad.set_closed` | The set was already checked in or undone, belongs to someone else, or a member is not checked out to you. | Start again with a new check-out. |
| 400 | `cad.member_unknown` | A number in the request is not an open member of the set. | Fix the request. |
| 400 | `cad.reference_unknown` | A declared reference does not exist or is not visible to you (the two look the same, on purpose). | Fix the references. |
| 400 / 409 | `cad.proposal` | The structure file is invalid (the title says which row or entity), or the proposal cannot be applied now (undecided parts, blocked parents). | Fix the file or the decisions. |
| 400 | none | Validation (names, roles, formats, size). | Fix the request. |
| 401 | none | Missing or expired token. | Renew the token. |
| 403 | none | Role missing or markings not permitted; `reasons` lists codes. | Show to the user. |
| 404 | none | Not found, or not visible to you. | |
| 422 | none | The malware scanner flagged an upload (`signature` member). The file was not stored. | Tell the user. |
| 503 | none | The vault or the malware scanner is unavailable (`Retry-After`). Nothing was changed. | Retry later. |

## Structure formats

A proposal is created from one of four formats; the format is named in the `metadata` field (`json`, `step`, `csv`, `xml`). The limits are 64 MiB, 20,000 parts, 100,000 lines and 30 levels. Every format becomes the same neutral model and the same validation applies: unique part keys, no part inside itself, no cycles, quantities above zero and at most 1,000,000, find numbers 1 to 999999, reference designators of letters, digits and `- _ . /`.

### Neutral JSON (`keel.cad.structure/v1`)

```json
{
  "schema": "keel.cad.structure/v1",
  "source": { "system": "NX" },
  "root": "asm",
  "parts": [
    { "ref": "asm", "number": "P-000123", "name": "Gimbal assembly" },
    { "ref": "bolt", "name": "Bolt M3x8", "unit": "EA", "source": "Buy",
      "attributes": { "cage_code": "1ABC2" },
      "matchOn": [ { "attribute": "cage_code", "value": "1ABC2" } ] }
  ],
  "lines": [
    { "parent": "asm", "child": "bolt", "quantity": 4, "findNumber": 10, "referenceDesignators": ["B1", "B2", "B3", "B4"] }
  ]
}
```

### STEP (ISO 10303-21; AP242, AP214, AP203)

Keel reads the product structure only: `PRODUCT` (id becomes the number, name the name), `PRODUCT_DEFINITION_FORMATION` (a `make_or_buy` value becomes Make or Buy), `PRODUCT_DEFINITION` and `NEXT_ASSEMBLY_USAGE_OCCURRENCE`. Occurrences of the same child under the same parent are counted into one line with that quantity. The occurrence id is the find number when it is a unique whole number under its parent; the reference designator is kept when it has the shape of one, and goes to the line's notes otherwise. When the file has several top-level products, name the one to import as `rootNumber`. The reader is written for Keel (no third-party parser and no geometry kernel); everything else in the file is skipped without being read.

### CSV

Header row required; comma, semicolon or tab; quotes as in RFC 4180. Columns (case-insensitive): `parent`, `child` (or `part_number`) required; `name`, `quantity` (or `qty`), `unit`, `find`, `refdes` (space or comma separated), `notes`, `type`, `source`, `attr.<attribute>`, `match.<attribute>`. Without a `parent` column the file is a flat BOM of the part named as `rootNumber`.

### XML

```xml
<structure root="asm" system="Script">
  <part ref="asm" number="P-000123" name="Gimbal assembly"/>
  <part ref="bolt" name="Bolt" unit="EA"><attribute key="cage_code" value="1ABC2"/><match attribute="cage_code" value="1ABC2"/></part>
  <line parent="asm" child="bolt" quantity="4" find="10" refdes="B1 B2 B3 B4"/>
</structure>
```

DTDs and external entities are refused.

### Matching

A part is matched by `number` against the parts you can see. Without a number match, each `matchOn` rule is tried in order: exactly one part with that attribute value is a match; several are ambiguous and need a decision in the review; none means the part is proposed as new. A part you cannot see is never matched, so a hidden part looks the same as a missing one. A new part is created with the markings of the proposal and the type, unit and source of the file (defaults: Assembly or Component, EA, Make for assemblies and Buy otherwise); the administrator's lists and attribute definitions apply, and a violation is reported when the proposal is applied.

## The reference tool

```
keel-cad checkout D-000123 --dir ./work               # take the package; files + <file>.markings.txt
keel-cad push --dir ./work                            # upload what changed
keel-cad add ./work/new.drw --dir ./work --name "Section view" --role drawing --type Drawing --references D-000123
keel-cad checkin --dir ./work --comment "Wall thickness" [--skip-unchanged] [--refs-file refs.json]
keel-cad undo --dir ./work
keel-cad watch --dir ./work --interval 5              # uploads changed files; never checks in
keel-cad bom push ./gimbal.stp --title "Gimbal" --markings-from D-000123 [--root P-000123]
```

`--server` (or `KEEL_CAD_URL`) names the Keel host and `--token`/`--token-file`/`KEEL_CAD_TOKEN` carries the bearer token. Exit codes: 0 done, 1 usage, 2 refused by the server (the code is printed), 3 integrity check failed, 4 server not reachable. State lives in `<dir>/.keel-cad/state.json`. The tool runs on Windows, macOS and Linux with the .NET runtime. A package folder is flat, so file names must be unique inside a package; the tool refuses a package that would overwrite a file.

## Operating notes

- Uploads stream through the vault with the same size limit, SHA-256 addressing and malware scanning as every document upload (ADR-0015, ADR-0026). Identical bytes are stored once.
- The check-out of a set and its record commit together. The check-in of a set, the dependency rows and the audit entries commit together.
- An abandoned check-out is released with `undo` (by the owner or an administrator); there is no timeout.
