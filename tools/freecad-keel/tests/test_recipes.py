import csv
import importlib.util
from pathlib import Path

import pytest
from conftest import FIXTURES, REPO

spec = importlib.util.spec_from_file_location("kicad_bom_to_keel_csv", REPO / "tools" / "cad-recipes" / "kicad_bom_to_keel_csv.py")
recipe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recipe)


def read(path: Path) -> list[dict[str, str]]:
    with open(path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def test_the_committed_sample_converts_to_the_committed_keel_csv(tmp_path):
    out = tmp_path / "out.csv"
    assert recipe.main([str(FIXTURES / "kicad-board-bom.csv"), "--board", "KA-7000", "--out", str(out)]) == 0
    # the same file the .NET CadStepFixtureTests read with the real CSV importer
    assert out.read_text() == (FIXTURES / "kicad-board-keel.csv").read_text()


def test_same_part_number_rows_merge_and_dnp_rows_are_left_out():
    rows, warnings = recipe.convert(read(FIXTURES / "kicad-board-bom.csv"), "KA-7000")
    by = {r["child"]: r for r in rows}
    assert by["KR-0603-10K"]["qty"] == "3" and by["KR-0603-10K"]["refdes"] == "R1 R2 R3"
    assert "KT-PAD-1" not in by and warnings == []


def test_a_row_without_a_part_number_is_refused_or_skipped():
    rows = [{"Refs": "R9", "Qty": "1", "Value": "1k", "KeelPN": ""}]
    with pytest.raises(recipe.BomError, match="KeelPN"):
        recipe.convert(rows, "B")
    with pytest.raises(recipe.BomError, match="No row"):
        recipe.convert(rows, "B", skip_unnumbered=True)
    ok, warnings = recipe.convert(rows + [{"Refs": "R1", "Qty": "1", "Value": "1k", "KeelPN": "KR-1"}], "B", skip_unnumbered=True)
    assert len(ok) == 1 and "skipped" in warnings[0]


def test_invalid_designators_are_dropped_with_a_warning_and_a_missing_column_is_explained():
    rows, warnings = recipe.convert([{"Refs": "R1, ~bad!", "Qty": "2", "KeelPN": "KR-1"}], "B")
    assert rows[0]["refdes"] == "R1" and "not valid" in warnings[0]
    with pytest.raises(recipe.BomError, match="no 'KeelPN' column"):
        recipe.convert([{"Refs": "R1", "Qty": "1"}], "B")
    with pytest.raises(recipe.BomError, match="whole number"):
        recipe.convert([{"Refs": "R1", "Qty": "two", "KeelPN": "K"}], "B")
