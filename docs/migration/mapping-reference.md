# Mapping file reference

The mapping file is JSON (version 1). Comments (`//`, `/* */`) and trailing commas are allowed. **Unknown members are errors**, so a typo can never silently weaken a rule. Source names (states, units, types, users, properties) match case-insensitively. Start from `keel-import map-template <package> --out mapping.json`: it lists every state, unit, type, user and property found in the package; obvious matches are pre-filled, everything else is left empty and fails validation until a person decides. The mapping's SHA-256 (of the canonical re-serialization, so comments and whitespace do not matter) is part of the dry-run gate.

## Sections

| Section | Meaning | Unknown or missing |
|---|---|---|
| `version` | must be `1` | error `MAP-VERSION` |
| `sourceSystem` | optional guard: must equal the package's source system | error `PKG-SOURCE-MISMATCH` |
| `auditGranularity` | `batch` (default) or `object` | error `MAP-AUDIT` |
| `lifecycle.states` | source state -> `InWork`, `UnderReview`, `Released`, `Obsolete` | error `STATE-UNMAPPED` unless `lifecycle.default` is set (then warning `STATE-DEFAULTED`) |
| `documentLifecycle` | same, for documents (default: `lifecycle`) | |
| `revisions.order` | `auto`, `source`, `created`, `label` (see below) | ambiguity is error `REV-ORDER-AMBIGUOUS` |
| `revisions.policy` | `sequence` (default) or `keepWhenValid` | |
| `revisions.explicit` | item number -> source labels in their true order | `REV-EXPLICIT-MISMATCH` if not exactly the item's labels |
| `partTypes` | `map` source type -> `Assembly`, `Component`, `Standard`, `RawMaterial`; `default` | `TYPE-UNMAPPED` |
| `assemblyWhenHasBom` | default `true`: an item with BOM lines becomes an Assembly (warning `TYPE-PROMOTED`); `false` makes it error `BOM-NOT-ASSEMBLY` | |
| `partSources` | source make/buy -> `Make`/`Buy`; `default` | `SOURCE-UNMAPPED` |
| `units` | source unit -> a Keel unit (`EA SET mm cm m in ft g kg lb mL L gal m2 m3`); a source unit that already is one needs no entry; `default` optional | error `UNIT-UNMAPPED` (hint lists the units) |
| `documentTypes` | source type -> `Specification`, `Drawing`, `CAD`, `Report`, `Procedure`, `Other`; `default` | `TYPE-UNMAPPED` |
| `attributes.map` | source property -> a Keel part attribute definition (`material finish mass cage_code critical_item criticality` in a fresh install). Values are validated against the definition (type, enum, max length, required). | `MAP-ATTR-TARGET`, `ATTR-INVALID`, `ATTR-TOO-LONG` |
| `attributes.keepUnmapped` | must be `true`: unmapped properties are always kept in `legacy_properties` and listed in the report | |
| `users.map`, `users.default` | source user -> Keel username; the default (`migration`) is used for the rest and flagged in the report; remove it to make unmapped users errors (`USER-UNMAPPED`) | |
| `markings.rules`, `markings.defaultMarkings` | see below | `MARKINGS-UNDETERMINED`, `MARKINGS-INVALID` |
| `changes.types.map`, `changes.states.<PR\|ECR\|ECN>`, `changes.classMap`, `changes.defaultClass`, `changes.defaultPriority` | change mapping | `CHG-TYPE-UNMAPPED`, `CHG-STATE-UNMAPPED`, `CHG-CLASS-MISSING` |
| `classification` | classes and class data, see [Classification](#classification) | `CLASS-*` |
| `effectivity` | line effectivity, see [Effectivity](#effectivity) | `EFF-*`, `MAP-EFF-*` |
| `limits` | `maxPropertyValueChars` (65536), `maxLegacyJsonChars` (1000000), `maxUsagesPerIteration` (5000) | `PROP-VALUE-TOO-LARGE`, `PROP-JSON-TOO-LARGE`, `BOM-TOO-MANY` |

## Revision labels
Keel labels are the ASME Y14.35 sequence (A, B, ... skipping I, O, Q, S, X, Z; then AA). The **original label is always kept** in `plm.part_revision.legacy_revision` (and `plm.document_revision.legacy_revision`), and the mapping table of every item is in the report.
* **Order.** `auto`: if every revision has a distinct created date, dates decide (and a warning `REV-ORDER-DISAGREE` appears when natural label order disagrees); otherwise, if the labels are all numeric (`01`, `1.2`) or all alphabetic (with `-` as the lowest), natural label order decides; otherwise (letters mixed with numbers, no usable dates) the order is **ambiguous and an error**. Fix it with `revisions.explicit` for the item, or set `revisions.order`. `source` trusts the order in the export; `created` requires distinct dates; `label` requires comparable labels.
* **Policy.** `sequence`: position k becomes label k (so `-`, `01`, `02` become A, B, C). `keepWhenValid`: if all of an item's labels are already valid ASME labels in increasing order (A, B, D), they are kept.
* Iteration numbers are the source's (unique, at least 1; gaps are fine).

## Markings rules {#markings}
```jsonc
"markings": {
  "rules": [
    { "name": "itar-flag",                        // shown in reports
      "appliesTo": ["part", "document", "change"], // optional; default all
      "when": { "property": "ITAR", "equals": "Y" },   // or "in": [...], "matches": "regex", "exists": true, "all": [ ... ]
      "set": { "exportJurisdiction": "Itar", "usmlCategory": "XI(d)", "cui": ["EXPT"],
               "distributionStatement": "D", "controllingOffice": "ACME", "distributionReason": "Critical technology",
               "programFromProperty": "Project",     // or "program": "ORCA"
               "owningOrg": "ENG" } },
    { "name": "explicitly-open", "when": { "property": "ITAR", "equals": "N" },
      "set": { "exportJurisdiction": "None", "distributionStatement": "A", "owningOrg": "ENG" } }
  ],
  "defaultMarkings": null   // see "Fail closed"
}
```
Properties seen by a rule are the master's, overlaid by the revision's and the iteration's, plus `$type`, `$number` and `$name`. Rule fields: `exportJurisdiction` (`None`, `Ear`, `Itar`), `eccn`, `usmlCategory`, `cui` (list), `distributionStatement` (`A`-`F`), `controllingOffice`, `distributionReason`, `program` / `programFromProperty`, `owningOrg`, `exportAuthorizations`.

* **Combination (restrictive).** Every matching rule contributes: highest jurisdiction wins (ITAR > EAR with an ECCN > EAR99 > none), CUI categories are unioned, the most restrictive distribution statement wins (F most restrictive), export authorizations are intersected. Two rules that disagree on program, owning org, USML category, ECCN, controlling office or reason are an error (`MARKINGS-INVALID`), not a guess.
* **Every iteration counts.** An object's markings are the combination over its master and all its iterations; a later iteration can only tighten.
* **Fail closed.** An object to which no rule assigns an export jurisdiction is error `MARKINGS-UNDETERMINED`. If `defaultMarkings` (same fields as `set`) is configured, it is used *only* for such objects, never merged into a rule-determined result, and every such object appears under **Defaulted markings** in the report and as warning `MARKINGS-DEFAULTED`. Choose the default as restrictive as your security office accepts (the sample uses distribution F so the objects are visible only to their program) and review the list.
* The result must satisfy the Keel invariants (`Markings.Create`): for example ITAR needs a USML category and CUI category EXPT and cannot be distribution A; statements B-F need a controlling office and reason; F needs a program. Failures are `MARKINGS-INVALID` with the reasons.
* **Changes** are also raised to cover the parts they list (warning `CHG-MARKINGS-RAISED`).

## Classification {#classification}
```jsonc
"classification": {
  "load": true,                    // default false: assignments are only staged and counted (CLASS-STAGED)
  "createMissingClasses": true,    // classes of the staged hierarchy that Keel lacks are created (parents first); false: every used class must exist
  "scope": "allIterations",        // or "latestIteration": the item-level source class goes onto every imported iteration, or only the last one
  "classMap": { "SRC-BRACKET": "FASTENERS.BRACKET" },                 // source class code -> Keel class code (unmapped codes are used as they are)
  "attributes": { "*": { "Width": "width" }, "SRC-BRACKET": { "WIDTH_MM": "width" } }   // source attribute -> class attribute, per source class or "*"
}
```
* **Hierarchy.** The source hierarchy is the optional file `classification.json` at the root of any package (format `keel-classification/1`, [classification-format.md](classification-format.md)); a canonical package may carry the same nodes inline as `classNodes` (not both).
* **What the load does.** Missing classes and their attributes are created through the same code as Administration's hierarchy import (so its rules for codes, depth and attribute names apply); **existing classes are never changed**. The class and values are then frozen into the iteration with the same rows native check-in writes (class row, typed value rows), before the revision is locked.
* **Validation.** Every assignment is validated against the *effective* attribute set of its class (own attributes plus the ancestors') with the rules native classification uses: unknown attribute, wrong type, enum, range, length, pattern and missing required values are errors with the part's source id. Empty source values are absent values. One class per part (ADM-10): the same assignment repeated (PLM XML repeats it per revision element) is one; different ones are `CLASS-MULTIPLE`.
* **Quarantine.** A part with a classification error is not imported (quarantine rules as for every other error); parts and changes that depend on it follow via `DEPENDENCY-FAILED`.
* **Dry-run.** The report lists the classes that would be created, the classified iterations per class, and conflicts.

| Code | Meaning |
|---|---|
| `CLASS-STAGED` (warning) | assignments staged, `classification.load` is false |
| `CLASS-ITEM-MISSING` (warning) | assignment for an item that is not in the package or not imported |
| `CLASS-CODE-INVALID`, `CLASS-DUP`, `CLASS-NAME` | hierarchy node with a bad or repeated code or no name |
| `CLASS-PARENT-MISSING`, `CLASS-CYCLE`, `CLASS-PARENT-FAILED`, `CLASS-PARENT-RETIRED` | hierarchy parents |
| `CLASS-ATTR-NAME`, `CLASS-ATTR-DUP`, `CLASS-ATTR-TYPE`, `CLASS-ATTR-ENUM`, `CLASS-ATTR-CLASH` | staged attribute definitions (names are unique along a path) |
| `CLASS-CONFLICT` | an existing class is under another parent in Keel |
| `CLASS-ATTR-TYPE-MISMATCH` | an existing attribute has another data type in Keel |
| `CLASS-ATTR-DIFFERS`, `CLASS-ATTR-NOT-ADDED`, `CLASS-RETIRED-IN-KEEL`, `CLASS-RETIRED-SKIPPED` (warnings) | Keel's definition wins; a staged retired class is not created |
| `CLASS-UNKNOWN`, `CLASS-RETIRED`, `CLASS-FAILED` | the class of an assignment does not exist, is retired, or failed above |
| `CLASS-ATTR-UNKNOWN`, `CLASS-VALUE-INVALID`, `CLASS-MULTIPLE` | values and one-class-per-part |

## Effectivity {#effectivity}
```jsonc
"effectivity": {
  "load": true,                                     // default false: effectivity text stays in the line notes
  "contexts": { "POD-END-ITEM": "ORCA-POD" },       // source end item / unit -> id of a Keel effectivity context (must exist; contexts are never created by an import)
  "blocks": { "ORCA-POD": { "Block 1": 1, "Block 2": 2 } },   // lot/block names -> ordinals (added to the blocks the context has in Keel)
  "sampleSerials": 5,                               // serials per context compared by the reconciliation (chosen from the data)
  "revisionLevel": false                            // true is refused (NotSupportedException, FUP-3)
}
```
* **Source.** Structured ranges per BOM line: kind `serial`, `lot` or `date`, an end item (not for dates), `from`, `to` and an optional end condition. Specified for each reader in [plmxml-subset.md](plmxml-subset.md#effectivity) and [windchill-extract-spec.md](windchill-extract-spec.md#part-uses-effectivity); canonical usages carry `effectivityRanges[]` (`kind`, `unit`, `from`, `to`, `endCondition`).
* **Rules (the native ones).** Serials and lots are whole numbers from 1 (lots may be block names), dates are ISO 8601 and are **read as UTC days** (a date-time with an offset is converted to UTC first; the end day is inclusive), From must not be after To, a range open on both ends is refused, `openEnded` ignores the end. Lines that share a find number are alternates: each needs a range, same dimension and context, and the ranges must not overlap. Violations are errors with the assembly's source id (`EFF-RANGE`, `EFF-EMPTY`, `EFF-OVERLAP`, `EFF-MIXED`, `BOM-DUP-FIND`).
* **Loading.** One Keel line per range (a line with two ranges becomes two alternates); lines are inserted in range order so the alternate ordinals follow the ranges, not the source order. Effectivity is iteration-owned like the rest of the structure.
* **Reconciliation.** Line counts with effectivity, the direct structure per iteration now including alternate ordinal and range, and the checksum of the exploded structure *as of serial* S (Released revisions, lines that apply to S) for serials chosen from the data: every range boundary and its neighbour, thinned to `sampleSerials` per context.

| Code | Meaning |
|---|---|
| `EFF-KIND`, `EFF-VALUE`, `EFF-RANGE`, `EFF-EMPTY` | the range is malformed (kind, number or date, From after To, open on both ends) |
| `EFF-CONTEXT-MISSING`, `EFF-CONTEXT-UNMAPPED`, `EFF-CONTEXT-UNKNOWN` | no end item given; end item without a mapping; Keel has no such context |
| `EFF-OVERLAP`, `EFF-MIXED`, `BOM-DUP-FIND` | alternates overlap, mix dimensions or contexts, or lack a range |
| `EFF-END-IGNORED`, `EFF-DATE-UNIT-IGNORED` (warnings) | an end of an open-ended range, or a unit on a date range, is ignored |
| `MAP-EFF-CONTEXT`, `MAP-EFF-BLOCKS`, `MAP-EFF-SAMPLES` | the mapping names a context Keel lacks, bad block numbers or sample count |

## Canonical package {#canonical-package}
`package.json` (camelCase) plus a files directory. Top level: `formatVersion` (1), `sourceSystem`, `sourceInstance`, `exportedAt`, `items`, `documents`, `changes`, `classifications`, `classNodes` (optional hierarchy), `users`. An item or document: `sourceId`, `number`, `name`, `type`, `unit`, `source`, `description`, `createdAt`, `createdBy`, `properties`, `revisions[]` (`sourceId`, `label`, `createdAt`, `createdBy`, `states[]` (`name`, `at`, `by`; the last is current), `properties`, `iterations[]` (`sequence`, `createdAt`, `createdBy`, `comment`, `description`, `properties`, `usages[]` (`childSourceId`, `quantity`, `unit`, `findNumber`, `referenceDesignators`, `notes`, `effectivity` (text), `effectivityRanges[]`), `docLinks[]` (`type`, `documentSourceId`, `documentRevisionLabel`), `files[]` (`name`, `mediaType`, `packagePath`, `sizeBytes`, `sha256`, `primary`))). A change: `sourceId`, `number`, `type` (`PR`, `ECR`, `ECN` or a source name mapped in `changes.types`), `title`, `description`, `state`, `changeClass`, `priority`, `reason`, `createdAt`, `createdBy`, `stateChangedAt`, `stateChangedBy`, `parentSourceId`, `properties`, `affected[]` (`itemSourceId`, `revisionLabel`, `resultingRevisionLabel`, `action`). For big exports, `items.jsonl`, `documents.jsonl`, `changes.jsonl`, `classifications.jsonl`, `users.jsonl` (one object per line) are read as a stream in addition to `package.json`.

## Problem codes
Every problem has a stable code, the source id, a message and a fix hint (report JSON, Markdown and CSV). Prefixes: `SRC-` the source files are unreadable or inconsistent (**blocks the whole package**, quarantine does not apply), `PKG-` and `MAP-` the package or mapping as a whole, `FILE-` file integrity, `BOM-`, `LINK-`, `CHG-`, `ATTR-`, `PROP-`, `REV-`, `STATE-`, `UNIT-`, `TYPE-`, `USER-`, `MARKINGS-`, `NUMBER-`/`DUP-` identity, `DEPENDENCY-FAILED` (an object depends on one that is not imported), `LOAD-` raised while writing (`LOAD-CONFLICT`, `LOAD-DB-REJECTED`).

## Annotated sample
`tests/Keel.Migration.Fixtures/Samples.cs` (`Samples.Mapping()`) builds the mapping used by every test; `Samples.MappingJson` serializes it.
