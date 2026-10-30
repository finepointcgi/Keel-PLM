#!/usr/bin/env python3
"""Secret scan of the tracked files (ADR-0059). In-repo, offline, standard library only (no external scanner has to be downloaded or trusted).

Looks for private key blocks, cloud and source-host tokens, JWTs, and passwords written into connection strings or assignments. Findings that are
deliberate (development-only credentials of the dev compose stack and test fixtures) are listed in `.secret-scan-allowlist`, one per line:

    <rule>  <path glob>  # reason (required)

A line that matches nothing any more is reported as stale, so the allow-list cannot silently grow. `--self-test` proves each rule fires.

  scripts/ci/secret-scan.py [--root DIR] [--self-test] [--history]       --history also scans every blob of `git log -p` (slow)
"""
from __future__ import annotations

import argparse
import fnmatch
import re
import subprocess
import sys
from pathlib import Path

RULES: dict[str, re.Pattern[str]] = {
    "private-key": re.compile(r"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED |PGP )?PRIVATE KEY(?: BLOCK)?-----"),
    "aws-access-key": re.compile(r"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b"),
    "github-token": re.compile(r"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{50,})\b"),
    "slack-token": re.compile(r"\bxox[baprs]-[0-9A-Za-z-]{10,}\b"),
    "google-api-key": re.compile(r"\bAIza[0-9A-Za-z_-]{35}\b"),
    "jwt": re.compile(r"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b"),
    "connection-string-password": re.compile(r"(?i)\b(?:password|pwd)=(?!\s*[;'\"$<{(])(?!\$)[^;'\"\s]{4,}"),
    "password-assignment": re.compile(r"""(?i)\b(?:password|passwd|secret|api[_-]?key|client[_-]?secret)\b["']?\s*[:=]\s*["'](?![$<{(])[^"'\s]{8,}["']"""),
}
PSQL_VARIABLE = re.compile(r"(?i)\bPASSWORD\s+:'")
SKIP_SUFFIX = {".png", ".jpg", ".jpeg", ".gif", ".ico", ".woff", ".woff2", ".ttf", ".wasm", ".zip", ".gz", ".tar", ".pdf", ".stl", ".step", ".stp", ".3mf", ".obj", ".xlsx", ".docx", ".snk"}
SKIP_NAMES = {"package-lock.json", "axe.min.js"}


def tracked_files(root: Path) -> list[Path]:
    out = subprocess.run(["git", "-C", str(root), "ls-files", "-z"], check=True, capture_output=True).stdout.decode()
    return [root / p for p in out.split("\0") if p]


def load_allowlist(root: Path) -> list[tuple[str, str, str, int]]:
    f = root / ".secret-scan-allowlist"
    entries = []
    if not f.exists():
        return entries
    for no, raw in enumerate(f.read_text().splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        body, _, reason = line.partition("#")
        parts = body.split()
        if len(parts) != 2 or not reason.strip():
            raise SystemExit(f".secret-scan-allowlist:{no}: expected '<rule> <path glob>  # reason'")
        if parts[0] not in RULES:
            raise SystemExit(f".secret-scan-allowlist:{no}: unknown rule {parts[0]}")
        entries.append((parts[0], parts[1], reason.strip(), no))
    return entries


def scan_text(rel: str, text: str):
    for no, line in enumerate(text.splitlines(), 1):
        if len(line) > 4000:   # minified content: only the key-block rule is meaningful
            if RULES["private-key"].search(line):
                yield "private-key", rel, no
            continue
        if PSQL_VARIABLE.search(line):   # `CREATE ROLE x PASSWORD :'var'` is psql interpolation of a secret passed at run time, not a literal
            continue
        for rule, rx in RULES.items():
            if rx.search(line):
                yield rule, rel, no


def scan(root: Path):
    for f in tracked_files(root):
        if f.suffix.lower() in SKIP_SUFFIX or f.name in SKIP_NAMES or not f.is_file() or f.is_symlink():
            continue
        try:
            data = f.read_bytes()
        except OSError:
            continue
        if b"\0" in data[:4096]:
            continue
        yield from scan_text(str(f.relative_to(root)), data.decode("utf-8", errors="replace"))


def self_test() -> int:
    samples = {
        "private-key": "-----BEGIN PRIVATE KEY-----",
        "aws-access-key": "key = AKIAABCDEFGHIJKLMNOP",
        "github-token": "token ghp_" + "a" * 36,
        "slack-token": "xoxb-1234567890-abcdefghij",
        "google-api-key": "AIza" + "B" * 35,
        "jwt": "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnop",
        "connection-string-password": "Host=db;Username=u;Password=hunter2hunter2;",
        "password-assignment": 'client_secret = "abcdefgh12345678"',
    }
    bad = 0
    for rule, text in samples.items():
        hits = {r for r, _, _ in scan_text("x", text)}
        if rule not in hits:
            print(f"FAIL self-test: rule {rule} did not fire on its sample"); bad += 1
    clean = ["CREATE ROLE keel_migrator LOGIN PASSWORD :'migrator_pw';", 'Password=${PGPASSWORD};', 'Password=<set me>', 'password: "$KEEL"', "Password=;", 'var password = "";']
    for text in clean:
        if list(scan_text("x", text)):
            print(f"FAIL self-test: false positive on {text!r}"); bad += 1
    print("self-test: " + ("failed" if bad else f"all {len(samples)} rules fire and {len(clean)} clean samples pass"))
    return 1 if bad else 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=str(Path(__file__).resolve().parents[2]))
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--history", action="store_true")
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    root = Path(a.root).resolve()
    allow = load_allowlist(root)
    used: set[int] = set()
    findings = []
    for rule, rel, no in scan(root):
        hit = next((e for e in allow if e[0] == rule and fnmatch.fnmatch(rel, e[1])), None)
        if hit:
            used.add(hit[3])
        else:
            findings.append((rule, rel, no))
    for rule, rel, no in findings:
        print(f"SECRET? {rel}:{no} [{rule}]")
    stale = [e for e in allow if e[3] not in used]
    for rule, glob, reason, no in stale:
        print(f"STALE allow-list entry (.secret-scan-allowlist:{no}): {rule} {glob} matches nothing; remove it")
    if a.history:
        # every line ever added, with the path it was added to, judged by the same rules and the same allow-list (a secret that was committed and later removed is still leaked)
        out = subprocess.run(["git", "-C", str(root), "log", "-p", "HEAD", "--no-color", "-U0"], capture_output=True).stdout.decode("utf-8", errors="replace")
        path = ""
        hist: set[tuple[str, str]] = set()
        for line in out.splitlines():
            if line.startswith("+++ "):
                path = line[6:] if line.startswith("+++ b/") else ""
                continue
            if not line.startswith("+") or not path or Path(path).suffix.lower() in SKIP_SUFFIX or Path(path).name in SKIP_NAMES:
                continue
            for rule, _, _ in scan_text(path, line[1:]):
                if not any(e[0] == rule and fnmatch.fnmatch(path, e[1]) for e in allow):
                    hist.add((rule, path))
        for rule, hp in sorted(hist):
            print(f"SECRET? in git history [{rule}] {hp} (review with: git log -p -S ...)")
        findings += [("history", r, 0) for r, _ in hist]
    print(f"secret scan: {len(findings)} finding(s), {len(allow)} allow-list entries ({len(used)} used), {len(stale)} stale")
    return 1 if findings or stale else 0


if __name__ == "__main__":
    sys.exit(main())
