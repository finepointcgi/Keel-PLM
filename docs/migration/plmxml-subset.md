# PLM XML subset read by keel-import

> **Honest status.** This is a specification *we own*. It was written from the public structure of Siemens PLM XML (see [sources.md](sources.md)) and not from a real Teamcenter export, because none was available. Element and attribute names for the core structure (Product, ProductRevision, ProductRevisionView, ProductInstance, DataSet, ExternalFile, UserData/UserValue, Person) follow the public schema; **how Teamcenter fills them (which UserData titles carry release status, sequence ids, find numbers, change relations) is our assumption and must be confirmed with real exports** (list in [README.md](README.md#what-we-need-from-you)). The reader is built so that anything it does not understand is reported, not dropped.

## Files and packaging
* A package is a directory or a `.zip` holding one or more `*.plmxml` / `*.xml` files and the files they reference (`ExternalFile/@locationRef`, relative to the package root). Several files are read as one set; IDREFs may cross files (ids must then be unique across the set).
* DTDs are prohibited and entities are not expanded. Paths that are absolute or contain `..` are refused (`SRC-UNSAFE-PATH`).
* Streaming: the root's children are read one at a time with `XmlReader`; known elements are materialized individually, unknown top-level elements are skipped without being loaded. Memory depends on the extracted data, not on the file size (measured: a 200 MB file raises the managed heap by about 47 MB).
* Namespaces: elements are matched by local name; the PLM XML namespace is accepted but not required.

## Elements

| Element | Read as | Attributes and children used |
|---|---|---|
| `PLMXML` | root | `author` (-> source instance), `date` and `time` (-> export date, used as the timestamp fallback) |
| `Product` | item (part) master, or **document** master when `subType` starts with `Document`, or a **change** when `subType` contains `ChangeNotice`, `ChangeRequest`, `ProblemReport`, `ECN` or `ECR` | `id` (source id), `productId` (number), `name`, `subType` (source type), `Description`, `UserData` |
| `ProductRevision` | one **iteration** of a revision; all `ProductRevision` elements with the same `masterRef` and `revision` form one revision | `id`, `masterRef` (IDREF to Product), `revision` (label), `name` (kept as property `revision_name` when it differs from the master's), `Description`, `UserData`, `AssociatedDataSet/@dataSetRef`, `AssociatedForm/@formRef`, `ApplicationRef`, `viewRefs` (IDREFS) |
| `ProductRevisionView` | structure of one iteration | `id`, `revisionRef` (IDREF to the owning ProductRevision), `instanceRefs` (IDREFS to ProductInstance) |
| `ProductInstance` | one BOM line | `id`, `partRef` (IDREF to a Product, ProductRevision or ProductRevisionView; resolved to the child *master*), `quantity`, `UserData`, child `Occurrence` elements |
| `Occurrence` | reference designators of an instance | child `UserData` with `UserValue title="reference_designator"` (one per designator) |
| `DataSet` | files | `id`, `name`, `type`, `UserData`, `ExternalFile` children |
| `ExternalFile` | one file | `locationRef` (path in the package), optional `format` (media type when it contains `/`), `sha256`, `size` |
| `Form` | extra properties | `id`, `name`, `UserData`; values become properties `form:<name>.<title>` of every iteration that references the form |
| `Person` | user | `userName`, `name`, `Role` children / `role` attribute |
| `SerialNumberEffectivity`, `LotEffectivity`, `DateEffectivity` | an effectivity range, see [Effectivity](#effectivity) | `id`, `contextRef`, `start`, `end`, `endCondition` |
| `ConfigurationContext` | **ignored as a configuration, counted**; its `name` resolves the `contextRef` of an effectivity | `id`, `name` |
| `Header`, `Transform`, `Bound`, `Representation`, `View`, `Site` | **ignored, counted** | geometry and presentation: not migrated |
| anything else | **unknown**: counted, first path recorded; when it is inside a Product, ProductRevision, ProductInstance or DataSet its XML (up to 16 KB) is preserved as property `xml:<Name>[n]` | |

## UserData / UserValue conventions (our assumption)
`UserData` carries `type`; its `UserValue` children carry `title` and `value`. Values are properties named `<title>` (type empty, `Attributes` or `Properties`) or `<type>.<title>`. An empty value is an absent value. These titles are consumed instead of kept as properties:

| Where | Title | Meaning |
|---|---|---|
| Product / ProductRevision | `creation_date` (ISO 8601), `owning_user` | created at / by |
| Product | `uom` or `unit`, `make_buy` | unit of measure, make/buy |
| ProductRevision | `sequence_id`, `comment` | iteration number (Teamcenter's revision sequence id) and its comment; without `sequence_id`, iterations are numbered 1, 2, ... in file order |
| ProductInstance (`type="Occurrence"` or plain) | `sequence_no` or `find_number`, `unit` or `uom`, `reference_designators` (comma/semicolon separated), `notes`, `effectivity` | BOM line fields. `effectivity` is free text: it is kept in the line's notes (and reported); structured effectivity is read from `effectivityRefs` (below). |
| DataSet | `sha256`, `size_bytes`, `media_type` | applied to the first ExternalFile |

Special `UserData` types on a ProductRevision:

| `type` | Titles | Meaning |
|---|---|---|
| `ReleaseStatus` | `name`, `date_released`, `user` | one lifecycle status; several allowed, ordered by date; the last is the current state |
| `DocumentLink` | `document` (Product id or number), `type` (`describes` or `references`), `revision` (source label, optional) | part-document link of that iteration |
| `ChangeAffected` | `item`, `revision`, `resulting_revision`, `action` (`revise`, `obsolete`, `none`) | affected object of a change |
| `Classification` | `class` + any attributes | classification assignment (loaded with `classification.load`; the sample writer puts it on the last revision element of the item; a repeated identical assignment on several revision elements counts once). The hierarchy comes from `classification.json` ([classification-format.md](classification-format.md#migration)). |

For change Products, `change_class`, `priority`, `reason` and `parent` (the source change this one descends from) are read from the revision's properties.

## Effectivity {#effectivity}
Public schema (see [sources.md](sources.md)): `Effectivity` is an abstract element with the concrete types `SerialNumberEffectivity` (attributes `start` and `end` as strings, `endCondition` `stockOut` or `openEnded`), `DateEffectivity` (`start` and `end` as `xsd:dateTime`, `endCondition`) and `LotEffectivity` (derived from the same base; its own attributes were not documented where we looked, so **we assume `start`, `end` and `endCondition` as for serial numbers**). All carry `id` and an optional `contextRef`, which limits an effectivity to a Structure, StructureRevision or ConfigurationContext.

What our reader does with it (the parts marked **assumed** are not in the public documentation and must be confirmed with real exports):
* The three elements are read as top-level elements. `contextRef` names the end item: when it refers to a `ConfigurationContext` of the package, that element's `name` is the end item, otherwise the reference text is. Dates have no end item.
* A `ProductInstance` lists its ranges in an `effectivityRefs` attribute (IDREFS). **Assumed:** the public schema documents `effectivityRefs` on `ReleaseStatus` and revision types; its use on instances is our convention. Several refs make several ranges; lines that share a find number are alternates.
* `endCondition="openEnded"`: no upper end (an `end` value is ignored with a warning). `stockOut` or absent: `end` is the last unit. **Assumed:** a date `end` is inclusive of its UTC day.
* Revision-level effectivity (`ReleaseStatus/@effectivityRefs`) is not read; it is not migrated (FUP-3).
* Mapping: end items to Keel contexts, block names to lots, UTC day handling: [mapping-reference.md](mapping-reference.md#effectivity).

## Documents and datasets
* A `Product` with a document `subType` is a document; its datasets' files belong to the iteration of the `ProductRevision` that lists them. The first file of the first dataset is the primary file; the others are attachments.
* **Decision (MIG-14):** a dataset attached to a *part* revision (a CAD file on an item) becomes a synthetic document numbered `<part number>-FILES` (type Other, same markings inputs as the part) and the part iteration gets a `describes` link to it. Nothing is lost, but the structure is ours: check it against real exports.

## Not supported (reported, not guessed)
Variants and configuration contexts beyond naming an effectivity's end item, product views other than the structure view, occurrence transforms and positions (ignored), classification beyond the `Classification` UserData above, workflow objects, markup, access-control lists, multi-site references.
