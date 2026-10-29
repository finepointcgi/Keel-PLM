# Compliance exports

Both files are built from the compliance roll-up of an assembly (`GET /api/v1/compliance/rollup/export`, or the links on the roll-up page) and contain only what the caller may read.

## Conflict minerals (CSV, `format=cmrt`)
One row per purchased part (a line without components). Columns: Part number, Revision, Description, Quantity, Unit, Declaration, Declared on, Expires on, Supplier reference,
Manufacturer part reference, Tin, Tantalum, Tungsten, Gold, Conflict-free status. A mineral reads No, Yes (conflict-free), Yes (not conflict-free), Yes (status unknown) or
No declaration. The overall status is Not conflict-free if any mineral is, Unknown if any is unknown or undeclared, else Conflict-free. Smelter data is not held: this is the
part-level shape of a conflict minerals report, not a CMRT workbook.

## Material declaration (XML, `format=ipc1752`)
`MaterialDeclaration` (format `keel-ipc1752-like/1`, asOf, marking) with `Product`, `Regulations` (code, name, kind, version, effectiveFrom, standing) and `Items`. Each `Item`
(path, partNumber, revision, name, quantity, unit) has one `Compliance` per regulation (standing and `Finding` children), and `Declaration` children (number, dates, supplierRef,
manufacturerPartRef) with a `Composition` of `Substance` elements (cas, name, massPercent, massMg, exemption, conflictFree). This is NOT a conformant IPC-1752A or B document.
