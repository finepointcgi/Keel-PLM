# ADR-0044: Output and bulk work: release package PDFs, Excel BOM exchange, mass change, report builder

Status: accepted (increment 8.9, 2026-10-29). Builds on ADR-0004 (markings and access policy), ADR-0005 (audit), ADR-0015 (vault), ADR-0017 (BOM), ADR-0035 (CAD BOM proposals),
ADR-0039 (variants) and the marking stamping of A10. Assumptions are FEAT-OUT-n in [increments/8.9-output-bulk.md](../increments/8.9-output-bulk.md).

## Context
People need to take product data out of Keel as a document (a release package for a customer or an audit), to move bills of materials in and out of Excel, to change many objects at once
and to answer ad-hoc questions with tables. Each of these is a new way to read or change marked data in bulk, so each must go through the same access checks, audit and release rules as
the screens do, and none may become a way around them.

## Decisions

### 1. One module, ports for everything it touches
`Keel.Reports` (schema `rpt`, migration `0027_reports.sql`) references Core, Security and Audit only. Parts, BOMs, documents, workflow signatures, changes and the report datasets are reached
through ports in `Keel.Core.Reports` (`IReleasePackageSource`, `IBomExchangeSource`, `IMassChangeTarget`, `IReportDataset`) plus the existing `ICadPartPort`. The host (`Keel.Api`) implements them
over the owning modules' own read-filtered services. The module reads no marked table: a test reads its sources and fails if any statement touches a schema other than `rpt`.

### 2. PDF generation: PDFsharp 6.2.4 (MIT), reused
Candidates: QuestPDF, PDFsharp/MigraDoc, and a hand-written PDF writer.
- **QuestPDF**: the Community license is conditional (a revenue threshold, then a commercial license), which an on-prem product with unknown customers cannot promise; it also ships native
  SkiaSharp binaries (size and a supply-chain surface for FIPS hosts). Rejected.
- **Hand-written writer**: no dependency, but fonts, encoding, wrapping and page numbering would be ours to keep correct. Rejected.
- **PDFsharp (MIT)**: already a dependency of `Keel.Documents` for marking stamps, already patched (the `System.Security.Cryptography.Pkcs` override), no native code, works offline with
  the embedded DejaVu Sans faces (Bitstream Vera license, already in the offline bundle notices). Cryptography is not used by the package renderer (the SHA-256 is computed with
  `System.Security.Cryptography`, FIPS-approved, ADR-0008). MigraDoc was not needed: layout here is a small table and paragraph engine over `XGraphics`, two passes so every page says "page n of m".
`Keel.Reports` links the same two font files (`EmbeddedResource ... LogicalName`); the host passes `EmbeddedFontResolver.EnsureInstalled` in, so there is one font resolver per process and no
reference between modules. Nothing is loaded from a CDN.

### 3. Release packages: refuse, never redact; the artifact is immutable and content addressed
A package is made for a released part or assembly, a released or closed ECN, or an established baseline. The host collects the content as the caller; **any** placeholder (an unreadable BOM
line, document, affected object or change) refuses the whole package with a count and no names (`package.unreadable_objects:n`, audited as a denial). The aggregate banner is the union of every
CUI category and export control over all included objects, with the most restrictive distribution statement and every program; the notices are those of each distinct marking
(`MarkingStatements.Notices`), printed at the top and bottom of every page together with page numbers and a trace line. The PDF is stored through `IContentStore` (SHA-256 is the address; malware
scanning applies) and recorded in `rpt.artifact`, which a trigger makes immutable. The row carries **no marking columns**: it lists the objects whose data it contains, and listing and download
re-check read access to **every** one (key lookups through `IObjectAccess`), answering "not found" otherwise. `rpt.artifact.sha256` is registered with the vault references so garbage
collection keeps it. At most two packages render at once.

### 4. Excel and CSV exchange: in-house .xlsx, neutralized and limited
No third-party spreadsheet library: the writer (a ZIP of a few XML parts, inline strings, a bold header) and the reader (first sheet only) are about 400 lines over `System.IO.Compression` and
`System.Xml`. This avoids a dependency to license, patch and vendor, and keeps the attack surface ours to limit. **Export** writes the banner and a description above the header, a markings column
per line, restricted placeholders for lines the exporter cannot read, and neutralizes every text cell that could be read as a formula (`=`, `+`, `-`, `@`, tab, carriage return get a leading
apostrophe, in .xlsx and CSV); numbers are numbers. **Import** reads with: 5 MB file limit, 60 parts, 40 MB per decompressed part and 80 MB in total counted **while reading** (the stream is cut, the central
directory is not trusted), a compression-ratio check, no DTDs or external entities, no macros, external links or embedded objects, formulas never evaluated (cached text only), at most 20,000 rows,
40 columns and 4,000 characters per cell. A workbook becomes a **proposal** (`rpt.bom_import`, owned by its creator) with a row-level dry run; applying goes through `ICadPartPort.ApplyBomAsync`
(check-out, BOM working-copy rules, check-in), so release locks, iteration rules and audit are the normal ones. Applying re-validates every row first, applies assembly by assembly, records which
assemblies are done, and is safe to repeat.

### 5. Mass change: preview, per-object apply, undo
A preview asks the object kind's `IMassChangeTarget` for a verdict per object without changing anything (readability, role, release lock, markings authority) and stores it. An unreadable object
and a missing one are refused with the same words. Apply runs object by object through the owning service (part check-out/edit/check-in, markings change, review start, watch, favorite), each
service writing its own audit event, all under the operation's one correlation id; failures are recorded per object and reported (409 with the failed count), apply can be repeated and
touches only objects not yet changed, and a step-up requirement stops the run with the rest pending. Reversible operations (attribute, markings, watch, favorite, baseline proposal) store
what is needed to undo; undo goes through the same services and writes new iterations (history is never rewritten) and refuses when the value changed since. Caps: 500 objects, 20
operations per user per hour. A mass change is visible only to its creator and its objects are re-authorized whenever shown.

### 6. Report builder: definitions are data; datasets are existing queries
A definition is a dataset id, columns, filters, one sort, one grouping, text and an optional input. The dataset catalog is fixed in code (parts, BOM, changes, baselines, quality records,
requirement sets, compliance declarations, retention schedules); each dataset calls the owning module's access-filtered search page by page up to 5,000 rows and returns each row with its markings.
Filtering, sorting (validated by a `SortMap` of the dataset's sortable columns, looked up and never used as SQL), grouping and counting are applied in memory to **those rows only**, so no
step can reach a row the query did not return; no user text reaches SQL. The result banner is the aggregate of the rows shown, so filtering away a marked row lowers it. Export to CSV, .xlsx and
PDF repeats the banner on the file. Definitions can be shared read-only with everyone; whoever runs one sees their own readable rows. Scheduling is out of scope.

### 7. Audit
Actions `output.*` (package generate/download, BOM export/import proposal/apply/discard, mass preview/apply/undo, report save/run/export/delete) carry numbers, counts, formats, hashes and banners,
never names or marked text; denials are audited. They are hidden from the activity feed (prefix rule) because they describe a person's own output.

## Consequences
- A package of a large structure is refused above 5,000 lines (use the Excel export).
- In-memory filtering means a report over more than 5,000 readable rows covers the first 5,000 in the service's default order and says so; narrow it with the search text.
- Mass change covers parts only; other object kinds join by registering another `IMassChangeTarget`.
