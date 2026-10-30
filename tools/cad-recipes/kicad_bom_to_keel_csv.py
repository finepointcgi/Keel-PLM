#!/usr/bin/env python3
"""Turns a KiCad BOM CSV (from `kicad-cli sch export bom` or the BOM dialog, grouped by value/footprint) into the Keel structure CSV
(docs/integration/cad-connector.md, "CSV"), so `keel-cad bom push board.csv ...` can propose the board's EBOM.

    python3 kicad_bom_to_keel_csv.py board-bom.csv --board P-000123 --pn-field KeelPN --out board-keel.csv
    keel-cad bom push board-keel.csv --title "Board rev B" --markings-from D-000123 --server https://keel.example.org

Each KiCad part needs a field with the Keel part number (default field name `KeelPN`; add it in the schematic symbol fields and include it in the
export). A row without it is refused (or skipped with --skip-unnumbered): Keel proposes only what the CAD data can identify. Rows marked DNP
(do not populate) are left out. Only the Python standard library is used. Nothing here talks to Keel.
"""
from __future__ import annotations

import argparse
import csv
import re
import sys
from pathlib import Path

DESIGNATOR = re.compile(r"^[A-Za-z0-9][A-Za-z0-9\-_./]{0,39}$")
REF_COLUMNS = ("Refs", "Reference", "References", "Designator")
QTY_COLUMNS = ("Qty", "Quantity", "Count")
NAME_COLUMNS = ("Value", "Description", "Name")


class BomError(ValueError):
    pass


def _pick(header: list[str], candidates: tuple[str, ...]) -> str | None:
    lower = {h.lower(): h for h in header}
    return next((lower[c.lower()] for c in candidates if c.lower() in lower), None)


def convert(rows: list[dict[str, str]], board: str, pn_field: str = "KeelPN", skip_unnumbered: bool = False) -> tuple[list[dict[str, str]], list[str]]:
    """Returns (Keel CSV rows, warnings). Rows of the same part number are merged: quantities add, designators are joined."""
    if not rows:
        raise BomError("The BOM has no rows.")
    header = list(rows[0].keys())
    pn = _pick(header, (pn_field,))
    if pn is None:
        raise BomError(f"The BOM has no '{pn_field}' column; add that field to the symbols and include it in the BOM export.")
    refs_col, qty_col, name_col = _pick(header, REF_COLUMNS), _pick(header, QTY_COLUMNS), _pick(header, NAME_COLUMNS)
    dnp_col = _pick(header, ("DNP", "Do not populate"))
    merged: dict[str, dict] = {}
    warnings: list[str] = []
    for i, row in enumerate(rows, start=2):
        if dnp_col and row.get(dnp_col, "").strip().lower() in ("dnp", "yes", "true", "1", "x"):
            continue
        number = (row.get(pn) or "").strip()
        if not number:
            if skip_unnumbered:
                warnings.append(f"row {i}: no {pn_field}, skipped ({(row.get(refs_col or '', '') or '').strip()[:40]})")
                continue
            raise BomError(f"Row {i} has no {pn_field} (references: {(row.get(refs_col or '', '') or '').strip()[:40]}).")
        refs = [r for r in re.split(r"[\s,;]+", (row.get(refs_col, "") if refs_col else "").strip()) if r]
        bad = [r for r in refs if not DESIGNATOR.match(r)]
        if bad:
            warnings.append(f"row {i}: reference designator(s) {bad[:3]} are not valid for Keel and were left out")
            refs = [r for r in refs if DESIGNATOR.match(r)]
        qty_text = (row.get(qty_col, "") if qty_col else "").strip()
        try:
            qty = int(qty_text) if qty_text else max(1, len(refs))
        except ValueError:
            raise BomError(f"Row {i}: quantity '{qty_text[:20]}' is not a whole number.") from None
        if qty <= 0:
            raise BomError(f"Row {i}: quantity must be above zero.")
        entry = merged.setdefault(number, {"name": (row.get(name_col, "") if name_col else "").strip() or number, "qty": 0, "refs": []})
        entry["qty"] += qty
        entry["refs"] += [r for r in refs if r not in entry["refs"]]
    if not merged:
        raise BomError("No row with a part number remained.")
    out = [{"parent": board, "child": number, "name": e["name"], "qty": str(e["qty"]), "find": str(10 * n), "refdes": " ".join(e["refs"])}
           for n, (number, e) in enumerate(merged.items(), start=1)]
    return out, warnings


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("bom", type=Path)
    ap.add_argument("--board", required=True, help="Keel part number of the board assembly (the parent of every line)")
    ap.add_argument("--pn-field", default="KeelPN")
    ap.add_argument("--skip-unnumbered", action="store_true")
    ap.add_argument("--out", type=Path, help="default: next to the input, <name>-keel.csv")
    args = ap.parse_args(argv)
    try:
        with open(args.bom, newline="", encoding="utf-8-sig") as f:
            rows = list(csv.DictReader(f))
        out, warnings = convert(rows, args.board, args.pn_field, args.skip_unnumbered)
    except (BomError, OSError, csv.Error) as e:
        print(f"error: {e}", file=sys.stderr)
        return 2
    target = args.out or args.bom.with_name(args.bom.stem + "-keel.csv")
    with open(target, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=["parent", "child", "name", "qty", "find", "refdes"], lineterminator="\n")
        w.writeheader()
        w.writerows(out)
    for w_ in warnings:
        print("warning:", w_, file=sys.stderr)
    print(f"{target}: {len(out)} line(s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
