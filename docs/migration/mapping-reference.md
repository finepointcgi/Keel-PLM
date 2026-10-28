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
| `classification.load` | `true` is refused: `NotSupportedException` naming Phase 2b | exit code 6 |
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

## Canonical package {#canonical-package}
`package.json` (camelCase) plus a files directory. Top level: `formatVersion` (1), `sourceSystem`, `sourceInstance`, `exportedAt`, `items`, `documents`, `changes`, `classifications`, `users`. An item or document: `sourceId`, `number`, `name`, `type`, `unit`, `source`, `description`, `createdAt`, `createdBy`, `properties`, `revisions[]` (`sourceId`, `label`, `createdAt`, `createdBy`, `states[]` (`name`, `at`, `by`; the last is current), `properties`, `iterations[]` (`sequence`, `createdAt`, `createdBy`, `comment`, `description`, `properties`, `usages[]` (`childSourceId`, `quantity`, `unit`, `findNumber`, `referenceDesignators`, `notes`, `effectivity`), `docLinks[]` (`type`, `documentSourceId`, `documentRevisionLabel`), `files[]` (`name`, `mediaType`, `packagePath`, `sizeBytes`, `sha256`, `primary`))). A change: `sourceId`, `number`, `type` (`PR`, `ECR`, `ECN` or a source name mapped in `changes.types`), `title`, `description`, `state`, `changeClass`, `priority`, `reason`, `createdAt`, `createdBy`, `stateChangedAt`, `stateChangedBy`, `parentSourceId`, `properties`, `affected[]` (`itemSourceId`, `revisionLabel`, `resultingRevisionLabel`, `action`). For big exports, `items.jsonl`, `documents.jsonl`, `changes.jsonl`, `classifications.jsonl`, `users.jsonl` (one object per line) are read as a stream in addition to `package.json`.

## Problem codes
Every problem has a stable code, the source id, a message and a fix hint (report JSON, Markdown and CSV). Prefixes: `SRC-` the source files are unreadable or inconsistent (**blocks the whole package**, quarantine does not apply), `PKG-` and `MAP-` the package or mapping as a whole, `FILE-` file integrity, `BOM-`, `LINK-`, `CHG-`, `ATTR-`, `PROP-`, `REV-`, `STATE-`, `UNIT-`, `TYPE-`, `USER-`, `MARKINGS-`, `NUMBER-`/`DUP-` identity, `DEPENDENCY-FAILED` (an object depends on one that is not imported), `LOAD-` raised while writing (`LOAD-CONFLICT`, `LOAD-DB-REJECTED`).

## Annotated sample
`tests/Keel.Migration.Fixtures/Samples.cs` (`Samples.Mapping()`) builds the mapping used by every test; `Samples.MappingJson` serializes it.
