import json
from pathlib import Path

import pytest
from conftest import sha

from keel_plm import workfolder
from keel_plm.client import KeelError
from keel_plm.workfolder import WorkFolderError


def test_checkout_writes_every_file_with_markings_and_a_keel_cad_compatible_state(package, client, tmp_path):
    state = workfolder.checkout(client, "D-000001", tmp_path / "pkg")
    files = {p.name for p in (tmp_path / "pkg").iterdir()}
    assert {"frame.step", "bracket.step", "frame.pdf", "frame.step.markings.txt"} <= files
    assert (tmp_path / "pkg" / "frame.step").read_bytes() == package.docs["D-000001"].files["frame.step"]
    assert {m.number for m in state.members} == {"D-000001", "D-000002", "D-000003"}  # the model, its reference and the dependent drawing
    raw = json.loads((tmp_path / "pkg" / ".keel-cad" / "state.json").read_text())
    assert set(raw) == {"checkoutId", "top", "members"}  # the CLI's camelCase format
    assert set(raw["members"][0]) == {"number", "role", "isNew", "references", "files"}
    assert set(raw["members"][0]["files"][0]) == {"fileName", "fileRole", "sha256"}
    assert all(d.locked_by == "alice" for d in package.docs.values())
    assert [p.name for p in workfolder.openable_files(tmp_path / "pkg", (".step",))][0] == "frame.step"  # top first


def test_the_cli_state_format_is_read_back(package, client, tmp_path):
    # a state file written by `keel-cad checkout` (camelCase, optional fields missing) loads
    folder = tmp_path / "f"
    (folder / ".keel-cad").mkdir(parents=True)
    (folder / ".keel-cad" / "state.json").write_text(json.dumps({"checkoutId": "abc", "top": "D-1", "members": [{"number": "D-1", "role": "model", "files": [{"fileName": "a.step", "sha256": "00"}]}]}))
    s = workfolder.load_state(folder)
    assert s.members[0].files[0].file_role == "primary" and s.members[0].is_new is False


def test_a_checkout_that_conflicts_takes_nothing_and_leaves_no_folder_state(package, client, tmp_path):
    package.docs["D-000002"].locked_by = "bob"
    with pytest.raises(KeelError) as e:
        workfolder.checkout(client, "D-000001", tmp_path / "pkg")
    assert e.value.code == "cad.locked"
    assert not workfolder.has_state(tmp_path / "pkg")
    assert package.docs["D-000001"].locked_by is None


def test_a_download_that_fails_the_hash_check_releases_the_locks_and_removes_files(package, client, tmp_path):
    package.corrupt_download = "frame.pdf"
    with pytest.raises(WorkFolderError, match="SHA-256") as e:
        workfolder.checkout(client, "D-000001", tmp_path / "pkg")
    assert e.value.code == "cad.integrity_mismatch"
    assert package.undone, "the set must be undone so the user does not hold invisible locks"
    assert all(d.locked_by is None for d in package.docs.values())
    assert not workfolder.has_state(tmp_path / "pkg")
    assert not (tmp_path / "pkg" / "frame.pdf").exists()


def test_a_zip_entry_with_a_path_never_leaves_the_folder(package, client, tmp_path):
    package.zip_traversal = True
    with pytest.raises(WorkFolderError, match="did not contain"):
        workfolder.checkout(client, "D-000001", tmp_path / "pkg")
    assert not (tmp_path / "evil").exists() and not list(tmp_path.glob("**/evil"))


@pytest.mark.parametrize("name", ["../x", "a/b", "a\\b", "..", ".keel-cad", ".keel-cad/state.json", "C:evil", "a\x00b", ""])
def test_unsafe_file_names_are_refused(name, tmp_path):
    with pytest.raises(WorkFolderError):
        workfolder.safe_path(tmp_path, name)


def test_two_documents_with_one_file_name_are_refused_case_insensitively(package, client, tmp_path):
    package.docs["D-000003"].files = {"FRAME.STEP": b"x"}
    with pytest.raises(WorkFolderError, match="unique file names"):
        workfolder.checkout(client, "D-000001", tmp_path / "pkg")
    assert package.undone


def test_changes_are_detected_by_hash_and_only_those_are_pushed(package, client, tmp_path):
    folder = tmp_path / "pkg"
    workfolder.checkout(client, "D-000001", folder)
    assert workfolder.changed_files(folder) == []
    (folder / "bracket.step").write_bytes(b"changed bracket")
    changed = workfolder.changed_files(folder)
    assert [(m.number, f.file_name) for m, f, _ in changed] == [("D-000002", "bracket.step")]
    assert workfolder.push_changes(client, folder) == ["bracket.step"]
    assert package.docs["D-000002"].working == {"bracket.step": b"changed bracket"}
    assert workfolder.push_changes(client, folder) == []  # nothing left; the state remembers the new hash
    assert workfolder.load_state(folder).member_of_file("bracket.step").files[0].sha256 == sha(b"changed bracket")


def test_a_missing_tracked_file_is_an_error_not_a_silent_skip(package, client, tmp_path):
    folder = tmp_path / "pkg"
    workfolder.checkout(client, "D-000001", folder)
    (folder / "frame.pdf").unlink()
    with pytest.raises(WorkFolderError, match="frame.pdf"):
        workfolder.changed_files(folder)


def test_checkin_sends_the_whole_set_in_one_request_with_hashes_and_references(package, client, tmp_path):
    folder = tmp_path / "pkg"
    workfolder.checkout(client, "D-000001", folder)
    (folder / "bracket.step").write_bytes(b"changed bracket")
    result = workfolder.checkin(client, folder, "Thicker wall", False, {"D-000003": ["D-000001"]})
    assert result["setClosed"]
    assert len(package.checkins) == 1  # ONE request: the server applies it atomically
    req = package.checkins[0]
    assert req["comment"] == "Thicker wall" and req["skipUnchanged"] is False
    by_number = {m["number"]: m for m in req["members"]}
    assert set(by_number) == {"D-000001", "D-000002", "D-000003"}
    assert by_number["D-000002"]["files"] == [{"fileName": "bracket.step", "sha256": sha(b"changed bracket")}]
    assert by_number["D-000003"]["references"] == ["D-000001"]
    assert "references" not in by_number["D-000001"]  # omitted: previous dependencies carry forward
    assert not workfolder.has_state(folder)
    assert all(d.locked_by is None for d in package.docs.values())
    assert package.docs["D-000002"].iteration == 2


def test_a_rejected_checkin_keeps_the_folder_and_the_locks(package, client, tmp_path):
    folder = tmp_path / "pkg"
    workfolder.checkout(client, "D-000001", folder)
    package.sets[workfolder.load_state(folder).checkout_id]["open"] = False  # the set was closed elsewhere
    with pytest.raises(KeelError) as e:
        workfolder.checkin(client, folder, "x")
    assert e.value.code == "cad.set_closed"
    assert workfolder.has_state(folder)


def test_undo_releases_everything_and_keeps_the_local_files(package, client, tmp_path):
    folder = tmp_path / "pkg"
    workfolder.checkout(client, "D-000001", folder)
    (folder / "bracket.step").write_bytes(b"local edit")
    workfolder.undo(client, folder)
    assert not workfolder.has_state(folder)
    assert (folder / "bracket.step").read_bytes() == b"local edit"
    assert all(d.locked_by is None for d in package.docs.values())


def test_a_second_checkout_into_a_used_folder_is_refused(package, client, tmp_path):
    folder = tmp_path / "pkg"
    workfolder.checkout(client, "D-000001", folder)
    with pytest.raises(WorkFolderError, match="already holds"):
        workfolder.checkout(client, "D-000001", folder)


def test_a_new_drawing_is_added_uploaded_and_checked_in_with_its_references(package, client, tmp_path):
    folder = tmp_path / "pkg"
    workfolder.checkout(client, "D-000001", folder)
    (folder / "section.FCStd").write_bytes(b"new drawing")
    member = workfolder.add_member(client, folder, "section.FCStd", "Section view", "Drawing", "drawing", ["D-000001"])
    assert member.is_new and package.docs[member.number].working == {"section.FCStd": b"new drawing"}
    workfolder.checkin(client, folder, "new drawing")
    entry = next(m for m in package.checkins[0]["members"] if m["number"] == member.number)
    assert entry["references"] == ["D-000001"]


def test_adding_a_file_twice_or_a_missing_file_is_refused(package, client, tmp_path):
    folder = tmp_path / "pkg"
    workfolder.checkout(client, "D-000001", folder)
    with pytest.raises(WorkFolderError, match="must be in"):
        workfolder.add_member(client, folder, "nope.FCStd", "x", "CAD", "model")
    with pytest.raises(WorkFolderError, match="already part"):
        workfolder.add_member(client, folder, "frame.step", "x", "CAD", "model")


def test_folder_names_are_made_safe():
    assert workfolder.folder_for("/w", "D-000123") == Path("/w/D-000123")
    assert workfolder.folder_for("/w", "../../etc/passwd").parent == Path("/w")
