# Windchill extract formats read by keel-import

> **Honest status.** Like the PLM XML subset, this is a specification *we own*. The shapes follow the public Windchill REST Services domains (ProdMgmt: Parts and their uses; DocMgmt: documents and content; ChangeMgmt: change objects; see [sources.md](sources.md)), but property names are our contract: **we have not seen a real extract from your Windchill.** Real exports must be compared with this spec and the spec (and reader) extended (see [README.md](README.md#what-we-need-from-you)). A small conversion script that renames fields is the expected bridge.

Two equivalent forms carry the same tables. Column names are case-insensitive. Every column that is not listed is **kept** as a property (and reported as unmapped unless the mapping maps it).

## Row model
Parts and documents are listed **one row per iteration** (Number + Version + Iteration), so history can be preserved. The master's identity is the number. Source ids are numbers (`Number` for parts and documents, `Number` for changes); revision source ids are `<Number>/<Version>`.

## Form 1: OData-shaped JSON (a directory)
Files: `parts.json`, `part-uses.json`, `documents.json`, `part-doc-links.json`, `change-notices.json`, `change-requests.json`, `problem-reports.json`, `users.json`, `classifications.json`. Each is an OData response `{ "@odata.context": ..., "value": [ ... ] }` or a plain array. If `@odata.nextLink` is present a warning (`SRC-PAGED`) says only that page is read: export all pages into one file. Other `.json` files and other top-level OData members are listed as unknown. Enumeration-style values `{ "Value": "RELEASED", "Display": "Released" }` are read as `Value`; other nested objects are kept as compact JSON text.

`documents.json` rows may nest `"Files": [ { ... } ]`, and change rows may nest `"AffectedItems": [ { ... } ]` (the CSV form uses separate tables).

## Form 2: loader-style CSV (a directory)
Files: `parts.csv`, `part_uses.csv`, `documents.csv`, `document_files.csv`, `part_doc_links.csv`, `change_notices.csv`, `change_requests.csv`, `problem_reports.csv`, `change_affected.csv`, `users.csv`, `classifications.csv`. RFC 4180: header row, UTF-8 (BOM allowed), quotes, doubled quotes, line breaks inside quoted fields. A record with the wrong number of fields is an error (`SRC-PARSE`).

## Tables

### parts (`parts.json`, `parts.csv`)
| Column | Use |
|---|---|
| `Number` (required), `Name`, `Version` (revision label), `Iteration` (integer) | identity; a missing `Iteration` numbers rows 1, 2, ... in file order |
| `State` | lifecycle state of the row; the revision's state is that of its highest iteration |
| `StateChangedOn`, `StateChangedBy` | release/state date and user (else `LastModified`, `ModifiedBy`) |
| `CreatedOn`, `CreatedBy` | created at/by of the master (the earliest row) and of the revision |
| `LastModified`, `ModifiedBy` | created at/by of this iteration |
| `DefaultUnit` (or `Unit`), `Source`, `PartType` (or `Type`), `Description`, `Comment` | unit, make/buy, type, description, iteration comment |
| anything else (`Context`, `Organization`, export-control attributes, custom IBAs ...) | properties (inputs for attribute and markings mappings) |

### part-uses (`part-uses.json`, `part_uses.csv`): BOM lines
`ParentNumber`, `ParentVersion`, `ParentIteration` (blank = the version's last iteration), `ChildNumber`, `Quantity`, `Unit` (or `QuantityUnit`), `FindNumber` (or `LineNumber`), `ReferenceDesignators` (comma or semicolon separated), `Notes`, `Effectivity` (free text, kept in the line notes), and the structured effectivity columns of [below](#part-uses-effectivity).

#### Part-uses effectivity {#part-uses-effectivity}
Columns of `part-uses` (all optional; blank when the line has no structured effectivity): `EffectivityType` (`serial`, `lot` or `date`), `EffectivityUnit` (or `EffectivityContext`: the end item or serial scope in Windchill), `EffectivityFrom`, `EffectivityTo` (whole numbers, a lot/block name, or ISO 8601 for dates), `EffectivityEndCondition` (`openEnded`, `stockOut`). **One range per row**: a line that has several ranges, or alternates of one find number, is several rows with the same parent, child and `FindNumber`. These columns are our contract (no public property list for Windchill effectivity was found); a conversion script maps the real fields. Mapping: [mapping-reference.md](mapping-reference.md#effectivity).

### documents and document files
`documents.*` has the part columns with `DocumentType` instead of `PartType` and no unit/source. `document_files.csv` (or nested `Files`): `DocumentNumber`, `DocumentVersion`, `DocumentIteration` (nested rows get these from the document row), `Name` (or `FileName`), `ContentType` (or `MimeType`), `Path` (relative to the package, the file content), `FileSize`, `Sha256` (optional; verified when given, computed and reported otherwise), `Role` (`PRIMARY` marks the primary file; without one the first file is primary).

### part-doc-links
`PartNumber`, `PartVersion`, `PartIteration` (blank = last), `DocumentNumber`, `DocumentVersion` (for *describe* links, the pinned document version; blank = the document's last), `LinkType` (`Describe` or `Reference`).

### changes
`change_notices.*` -> ECN, `change_requests.*` -> ECR, `problem_reports.*` -> PR. Columns: `Number`, `Name` (title), `Description`, `State`, `ChangeClass`, `Priority`, `Reason`, `CreatedOn`, `CreatedBy`, `StateChangedOn`, `StateChangedBy`, `ParentNumber` (the PR an ECR came from, the ECR an ECN came from), other columns are properties. Affected objects (`change_affected.csv` or nested `AffectedItems`): `ChangeNumber`, `ItemNumber`, `Version`, `ResultingVersion`, `Action` (`revise`, `obsolete`, `none`).

### users, classifications
`users.*`: `Name`, `FullName`, `Groups`. `classifications.*`: `PartNumber`, `ClassCode`, other columns are class attributes (blank values are absent values). Loaded with `classification.load`. The class hierarchy is the optional file `classification.json` (`keel-classification/1`) at the root of the directory ([classification-format.md](classification-format.md#migration)).

## Getting data into this shape
Practical paths (see [README.md](README.md)): (1) call the ProdMgmt, DocMgmt and ChangeMgmt domains of Windchill REST Services with `$expand` for uses, associations and content info and save the pages as the files above (a script that renames properties to this contract is usually needed); (2) use the loader/export tools your Windchill administrators already run and convert to the CSV form; (3) build the neutral canonical package from your own tooling (`package.json`, see the canonical format in [mapping-reference.md](mapping-reference.md#canonical-package)). In all cases, ask for **all iterations** of every version: a latest-only export loses iteration history.
