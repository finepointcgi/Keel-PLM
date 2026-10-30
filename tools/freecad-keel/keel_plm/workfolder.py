"""Work-folder handling: check a package out into a folder, push changed files, check the set in atomically, undo.

The folder holds ``.keel-cad/state.json`` in exactly the format of the ``keel-cad`` CLI (camelCase keys), so the add-on and ``keel-cad`` (for example
``keel-cad watch``) can work on the same folder.
"""
from __future__ import annotations

import hashlib
import io
import json
import os
import re
import shutil
import zipfile
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from .client import KeelClient, KeelError, sha256_file

STATE_DIR = ".keel-cad"
_FOLDER_SAFE = re.compile(r"[^A-Za-z0-9._-]+")


class WorkFolderError(KeelError):
    pass


@dataclass
class FileState:
    file_name: str
    file_role: str = "primary"
    sha256: str = ""


@dataclass
class MemberState:
    number: str
    role: str
    is_new: bool = False
    references: list[str] = field(default_factory=list)
    files: list[FileState] = field(default_factory=list)


@dataclass
class PackageState:
    checkout_id: str
    top: str
    members: list[MemberState] = field(default_factory=list)

    def member_of_file(self, file_name: str) -> MemberState | None:
        return next((m for m in self.members if any(f.file_name == file_name for f in m.files)), None)

    def files(self) -> list[str]:
        return [f.file_name for m in self.members for f in m.files]


# ---------------------------------------------------------------------- state file

def _state_path(folder: Path) -> Path:
    return Path(folder) / STATE_DIR / "state.json"


def has_state(folder: Path | str) -> bool:
    return _state_path(Path(folder)).exists()


def load_state(folder: Path | str) -> PackageState:
    path = _state_path(Path(folder))
    if not path.exists():
        raise WorkFolderError(f"{folder} is not a checked-out package folder (no {STATE_DIR}).")
    raw = json.loads(path.read_text(encoding="utf-8"))
    return PackageState(
        checkout_id=raw["checkoutId"], top=raw["top"],
        members=[MemberState(m["number"], m["role"], bool(m.get("isNew")), list(m.get("references") or []),
                             [FileState(f["fileName"], f.get("fileRole", "primary"), f.get("sha256", "")) for f in m.get("files") or []])
                 for m in raw.get("members") or []])


def save_state(folder: Path | str, state: PackageState) -> None:
    raw = {"checkoutId": state.checkout_id, "top": state.top, "members": [
        {"number": m.number, "role": m.role, "isNew": m.is_new, "references": m.references,
         "files": [{"fileName": f.file_name, "fileRole": f.file_role, "sha256": f.sha256} for f in m.files]} for m in state.members]}
    path = _state_path(Path(folder))
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(raw, indent=2), encoding="utf-8")
    os.replace(tmp, path)


def folder_for(work_root: Path | str, top: str) -> Path:
    """The folder a check-out of ``top`` uses under the work root (the number made safe for a file name)."""
    return Path(work_root) / (_FOLDER_SAFE.sub("_", top).strip("._") or "package")


def safe_path(folder: Path | str, file_name: str) -> Path:
    """Names come from the server. One that could leave the folder (or touch the state folder) is refused: the second lock after the server's own."""
    if not file_name or file_name in (".", "..") or "/" in file_name or "\\" in file_name or "\x00" in file_name or file_name.startswith(STATE_DIR) or ":" in file_name:
        raise WorkFolderError(f"'{file_name}' is not a usable file name.")
    return Path(folder) / file_name


# ---------------------------------------------------------------------- check-out

def checkout(client: KeelClient, top: str, folder: Path | str, roles: list[str] | None = None, allow_partial: bool = False) -> PackageState:
    folder = Path(folder)
    if has_state(folder):
        raise WorkFolderError(f"{folder} already holds a checked-out package; check it in or undo it first.")
    manifest = client.checkout(top, roles, allow_partial)
    state = PackageState(checkout_id=manifest["id"], top=top)
    folder.mkdir(parents=True, exist_ok=True)
    (folder / STATE_DIR).mkdir(exist_ok=True)
    seen: dict[str, str] = {}
    written: list[Path] = []
    try:
        for m in manifest["members"]:
            member = MemberState(m["number"], m["cadRole"], False, list(m.get("references") or []))
            for f in m.get("files") or []:
                fs = FileState(f["fileName"], f.get("role", "primary"), f["sha256"])
                lower = fs.file_name.lower()
                if lower in seen:
                    raise WorkFolderError(f"{member.number} and {seen[lower]} both have a file named {fs.file_name}; a package folder needs unique file names.")
                seen[lower] = member.number
                written += _download(client, state.checkout_id, member.number, fs, folder)
                member.files.append(fs)
            state.members.append(member)
    except Exception:
        # Never leave the user holding locks they cannot see: release the set when the folder could not be filled.
        try:
            client.undo(state.checkout_id)
        except KeelError:
            pass
        for p in written:
            p.unlink(missing_ok=True)
        shutil.rmtree(folder / STATE_DIR, ignore_errors=True)
        raise
    save_state(folder, state)
    return state


def _download(client: KeelClient, checkout_id: str, number: str, fs: FileState, folder: Path) -> list[Path]:
    data = client.download_member_file(checkout_id, number, fs.sha256)
    try:
        zf = zipfile.ZipFile(io.BytesIO(data))
    except zipfile.BadZipFile:
        raise WorkFolderError(f"The download of {fs.file_name} is not a ZIP archive.") from None
    with zf:
        try:
            content = zf.read(fs.file_name)  # one named entry; the archive is never extracted wholesale
        except KeyError:
            raise WorkFolderError(f"The download of {fs.file_name} did not contain the file.") from None
        target = safe_path(folder, fs.file_name)
        if hashlib.sha256(content).hexdigest() != fs.sha256:
            raise WorkFolderError(f"{fs.file_name} failed the SHA-256 check after download.", 0, "cad.integrity_mismatch")
        target.write_bytes(content)
        out = [target]
        if "MARKINGS.txt" in zf.namelist():
            # The cover sheet text travels beside the file so the markings are not lost when the folder is copied.
            marks = Path(str(target) + ".markings.txt")
            marks.write_bytes(zf.read("MARKINGS.txt"))
            out.append(marks)
        return out


# ---------------------------------------------------------------------- change detection, upload

def changed_files(folder: Path | str) -> list[tuple[MemberState, FileState, str]]:
    """Tracked files whose bytes differ from the checked-out hash, as (member, file, new hash). A missing file is an error."""
    folder = Path(folder)
    state = load_state(folder)
    result = []
    for m in state.members:
        for f in m.files:
            path = safe_path(folder, f.file_name)
            if not path.exists():
                raise WorkFolderError(f"{f.file_name} (of {m.number}) is missing from {folder}.")
            sha = sha256_file(path)
            if sha != f.sha256:
                result.append((m, f, sha))
    return result


def push_changes(client: KeelClient, folder: Path | str) -> list[str]:
    folder = Path(folder)
    state = load_state(folder)
    pushed = []
    for m, f, sha in changed_files(folder):
        client.upload_file(state.checkout_id, m.number, f.file_role, f.file_name, safe_path(folder, f.file_name), sha)
        # state is re-read and saved per file so an interrupted push resumes where it stopped
        live = load_state(folder)
        for lm in live.members:
            for lf in lm.files:
                if lm.number == m.number and lf.file_name == f.file_name:
                    lf.sha256 = sha
        save_state(folder, live)
        pushed.append(f.file_name)
    return pushed


def add_member(client: KeelClient, folder: Path | str, file_name: str, name: str, document_type: str, role: str, references: list[str] | None = None) -> MemberState:
    """Adds a new CAD file (for example a new drawing) of the folder to the package: creates the document, uploads the file, tracks it."""
    folder = Path(folder)
    state = load_state(folder)
    path = safe_path(folder, Path(file_name).name)
    if not path.exists():
        raise WorkFolderError(f"{path.name} must be in {folder} to be added.")
    if path.name in state.files():
        raise WorkFolderError(f"{path.name} is already part of the package.")
    added = client.add_member(state.checkout_id, name, document_type, role, references or [])
    member = MemberState(added["number"], role, True, list(references or []))
    sha = sha256_file(path)
    client.upload_file(state.checkout_id, member.number, "primary", path.name, path, sha)
    member.files.append(FileState(path.name, "primary", sha))
    state.members.append(member)
    save_state(folder, state)
    return member


# ---------------------------------------------------------------------- check-in, undo

def checkin(client: KeelClient, folder: Path | str, comment: str | None = None, skip_unchanged: bool = False,
            references: dict[str, list[str]] | None = None) -> dict:
    """Uploads what changed and checks the whole set in atomically. ``references`` maps a document number to the numbers it was built against
    (declaring a reference asserts that the document was regenerated against the current version of that model)."""
    folder = Path(folder)
    push_changes(client, folder)
    state = load_state(folder)
    members: list[dict[str, Any]] = []
    for m in state.members:
        refs = references[m.number] if references and m.number in references else (list(m.references) if m.is_new else None)
        entry: dict[str, Any] = {"number": m.number, "files": [{"fileName": f.file_name, "sha256": f.sha256} for f in m.files]}
        if refs is not None:
            entry["references"] = refs
        members.append(entry)
    result = client.checkin(state.checkout_id, comment, skip_unchanged, members)
    shutil.rmtree(folder / STATE_DIR, ignore_errors=True)
    return result


def undo(client: KeelClient, folder: Path | str) -> None:
    folder = Path(folder)
    state = load_state(folder)
    client.undo(state.checkout_id)
    shutil.rmtree(folder / STATE_DIR, ignore_errors=True)


def openable_files(folder: Path | str, extensions: tuple[str, ...] = (".fcstd",)) -> list[Path]:
    """The checked-out files a CAD system can open, top document first, then the rest in manifest order."""
    folder = Path(folder)
    state = load_state(folder)
    members = sorted(state.members, key=lambda m: m.number != state.top)
    return [safe_path(folder, f.file_name) for m in members for f in m.files if f.file_name.lower().endswith(extensions)]
