#!/usr/bin/env python3
"""Markdown link check for the repository's documentation (ADR-0059). Offline: relative links, images and #anchors only.

External http(s) links are never fetched (the product and its documentation are for air-gapped sites, and CI must not depend on third-party
availability); they are counted and listed with --list-external. Checks, per file: the target file or directory exists (relative to the
file, or to the repository root for a leading slash), image targets exist, and a #fragment names a heading of the target Markdown file
(GitHub slug rules), an explicit `{#id}` heading suffix (used by docs/migration) or an <a id>/<a name>. Fenced code blocks and inline code are skipped.

  scripts/ci/check-md-links.py [--root DIR] [PATH ...]      default paths: docs README.md CLAUDE.md THIRD-PARTY-LICENSES.md deploy tools/freecad-keel/README.md
Exit status 1 when a link is broken. Standard library only.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path
from urllib.parse import unquote

FENCE = re.compile(r"^\s*(```|~~~)")
INLINE_CODE = re.compile(r"`+[^`\n]*`+")
LINK = re.compile(r"!?\[(?:[^\[\]]|\[[^\]]*\])*\]\(\s*(<[^>]+>|[^)\s]+)(?:\s+\"[^\"]*\")?\s*\)")
REF_DEF = re.compile(r"^\s{0,3}\[[^\]]+\]:\s*(\S+)")
HEADING = re.compile(r"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$")
HTML_ID = re.compile(r"""<a\s+[^>]*(?:id|name)=["']([^"']+)["']""", re.I)
SKIP_DIRS = {".git", "node_modules", "bin", "obj", "TestResults", ".claude"}


def slug(text: str) -> str:
    text = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", text)      # links keep their text
    text = re.sub(r"[`*_~]", "", text).strip().lower()
    text = re.sub(r"<[^>]+>", "", text)
    text = re.sub(r"[^\w\- ]", "", text, flags=re.UNICODE)
    return text.replace(" ", "-")


_anchor_cache: dict[Path, set[str]] = {}


def anchors(path: Path) -> set[str]:
    if path in _anchor_cache:
        return _anchor_cache[path]
    found: set[str] = set()
    counts: dict[str, int] = {}
    in_fence = False
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        if FENCE.match(line):
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        m = HEADING.match(line)
        if m:
            explicit = re.search(r"\s*\{#([^}\s]+)\}\s*$", m.group(2))   # the docs' own `## Title {#id}` convention
            if explicit:
                found.add(explicit.group(1))
            base = slug(re.sub(r"\s*\{#[^}]*\}\s*$", "", m.group(2)))
            n = counts.get(base, 0)
            counts[base] = n + 1
            found.add(base if n == 0 else f"{base}-{n}")
        for h in HTML_ID.findall(line):
            found.add(h)
    _anchor_cache[path] = found
    return found


def links_of(path: Path):
    in_fence = False
    for no, line in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
        if FENCE.match(line):
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        stripped = INLINE_CODE.sub("", line)
        for m in LINK.finditer(stripped):
            yield no, m.group(1).strip("<>")
        m = REF_DEF.match(stripped)
        if m:
            yield no, m.group(1).strip("<>")


def check(root: Path, files: list[Path], list_external: bool) -> int:
    broken = 0
    external = 0
    total = 0
    for f in files:
        for no, target in links_of(f):
            total += 1
            if re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]*:", target):
                if target.startswith(("http://", "https://")):
                    external += 1
                    if list_external:
                        print(f"external {f.relative_to(root)}:{no}: {target}")
                continue   # mailto:, tel: ...
            path_part, _, fragment = target.partition("#")
            path_part = unquote(path_part)
            if path_part == "":
                dest = f
            else:
                dest = (root / path_part.lstrip("/")) if path_part.startswith("/") else (f.parent / path_part)
                dest = Path(dest).resolve()
                if not dest.exists():
                    print(f"BROKEN {f.relative_to(root)}:{no}: {target} (no such file or directory)")
                    broken += 1
                    continue
            if fragment and dest.is_file() and dest.suffix.lower() == ".md":
                if unquote(fragment).lower() not in {a.lower() for a in anchors(dest)}:
                    print(f"BROKEN {f.relative_to(root)}:{no}: {target} (no heading with that anchor in {dest.relative_to(root)})")
                    broken += 1
    print(f"checked {total} links in {len(files)} files: {broken} broken, {external} external not fetched")
    return 1 if broken else 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=str(Path(__file__).resolve().parents[2]))
    ap.add_argument("--list-external", action="store_true")
    ap.add_argument("paths", nargs="*")
    a = ap.parse_args()
    root = Path(a.root).resolve()
    paths = a.paths or ["docs", "README.md", "CLAUDE.md", "THIRD-PARTY-LICENSES.md", "deploy", "tools/freecad-keel/README.md"]
    files: list[Path] = []
    for p in paths:
        full = root / p
        if full.is_file():
            files.append(full)
        elif full.is_dir():
            files += [m for m in sorted(full.rglob("*.md")) if not (set(m.relative_to(root).parts) & SKIP_DIRS)]
    if not files:
        print("no Markdown files found", file=sys.stderr)
        return 2
    return check(root, files, a.list_external)


if __name__ == "__main__":
    sys.exit(main())
