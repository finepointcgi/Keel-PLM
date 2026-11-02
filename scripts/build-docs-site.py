#!/usr/bin/env python3
"""Builds the GitHub Pages site into an output directory (default: _site).

    python3 scripts/build-docs-site.py [out_dir]       needs: pip install markdown

  site/                  the landing page, copied to the root
  docs/manual/*.md       the user manual            -> manual/*.html  (README.md becomes manual/index.html)
  docs/setup.md          development setup          -> setup.html
  docs/ops/*.md          operations documents       -> ops/*.html
  every other relative link in those pages points at the file on GitHub.

The pages are static HTML with one small script (search, menu); nothing is loaded from another site.
"""
import html
import json
import os
import re
import shutil
import sys
from pathlib import Path

import markdown

ROOT = Path(__file__).resolve().parent.parent
OUT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else ROOT / "_site"
REPO = "https://github.com/finepointcgi/Keel-PLM"
BLOB = REPO + "/blob/main/"
TREE = REPO + "/tree/main/"

# Sidebar: (group title, [source files relative to the repo root]). The order here is the reading order (previous / next).
USER_GROUPS = [
    ("Getting around", ["01-getting-started", "02-navigating"]),
    ("Product data", ["03-parts", "04-boms-variants-mbom", "05-documents-and-vault"]),
    ("Change and release", ["06-lifecycles-workflow-signatures", "07-change-management", "08-baselines-effectivity"]),
    ("Access and trust", ["09-markings-and-access", "20-audit-and-integrity"]),
    ("Quality and supply", ["10-quality", "12-sourcing-and-portal", "13-manufacturing-process"]),
    ("Engineering", ["11-cad-connector-and-freecad", "14-requirements", "15-compliance"]),
    ("Records and programs", ["16-records", "17-programs", "18-reports-and-output", "19-notifications-and-activity"]),
    ("Connecting other systems", ["21-integration-and-tdp", "22-migration"]),
]
ADMIN_FIRST = ["manual/23-administration", "manual/24-administrator-and-operator-guide", "setup"]
OPS_GROUPS = [
    ("Install and upgrade", ["ops/compose-example", "ops/kubernetes", "ops/offline-install", "ops/upgrade", "ops/configuration", "ops/cloud-run-demo"]),
    ("Identity and security", ["ops/sso-integration", "ops/cac-piv-sign-in", "ops/hardening", "ops/package-signing", "ops/siem-onboarding"]),
    ("Run and recover", ["ops/backup-restore", "ops/disaster-recovery", "ops/performance", "ops/ci"]),
    ("Features for operators", ["ops/notifications", "ops/email-verification-and-bounces", "ops/integration", "ops/records-management"]),
    ("Compliance", ["ops/compliance-features", "ops/nist-800-171-mapping"]),
]


class Page:
    def __init__(self, key, src, out_rel):
        self.key, self.src, self.out_rel = key, src, out_rel   # key like "manual/03-parts"; src is a repo-relative Path
        self.title = ""
        self.body = ""
        self.headings = []
        self.group = ""
        self.section = ""


def collect():
    pages = {}
    for f in sorted((ROOT / "docs/manual").glob("*.md")):
        key = "manual/index" if f.name == "README.md" else "manual/" + f.stem
        pages[key] = Page(key, f.relative_to(ROOT), key + ".html")
    for f in sorted((ROOT / "docs/ops").glob("*.md")):
        pages["ops/" + f.stem] = Page("ops/" + f.stem, f.relative_to(ROOT), "ops/" + f.stem + ".html")
    pages["setup"] = Page("setup", Path("docs/setup.md"), "setup.html")
    return pages


def relpath(from_out, to_out):
    """Relative URL between two output paths, e.g. manual/a.html -> ops/b.html = ../ops/b.html."""
    a = Path(from_out).parent
    b = Path(to_out)
    parts_a = [] if str(a) == "." else list(a.parts)
    parts_b = list(b.parts)
    while parts_a and parts_b and parts_a[0] == parts_b[0] and len(parts_b) > 1:
        parts_a.pop(0)
        parts_b.pop(0)
    return "/".join([".."] * len(parts_a) + parts_b)


def rewrite_links(page, body, pages, copies):
    src_dir = (ROOT / page.src).parent

    def fix(m):
        attr, url = m.group(1), m.group(2)
        if re.match(r"^(https?:|mailto:|#|data:)", url):
            return m.group(0)
        path, _, frag = url.partition("#")
        frag = "#" + frag if frag else ""
        if not path:
            return m.group(0)
        target = (src_dir / path).resolve()
        try:
            rel_repo = target.relative_to(ROOT)
        except ValueError:
            return m.group(0)
        if attr == "src":  # an image: copy it next to the page's output tree and keep the relative URL
            copies[rel_repo] = Path(os.path.normpath(Path(page.out_rel).parent / path))
            return m.group(0)
        if target.suffix == ".md":
            for p in pages.values():
                if (ROOT / p.src).resolve() == target:
                    return f'href="{relpath(page.out_rel, p.out_rel)}{frag}"'
            return f'href="{BLOB}{rel_repo.as_posix()}{frag}"'
        if target.is_dir():
            return f'href="{TREE}{rel_repo.as_posix()}"'
        if target.suffix.lower() in (".png", ".jpg", ".svg", ".webp"):
            copies[rel_repo] = Path(os.path.normpath(Path(page.out_rel).parent / path))
            return m.group(0)
        return f'href="{BLOB}{rel_repo.as_posix()}{frag}"'

    return re.sub(r'\b(href|src)="([^"]+)"', fix, body)


def render(pages):
    copies = {}
    for page in pages.values():
        text = (ROOT / page.src).read_text(encoding="utf-8")
        md = markdown.Markdown(extensions=["tables", "fenced_code", "toc", "sane_lists", "attr_list"],
                               extension_configs={"toc": {"permalink": "#", "permalink_title": "Link to this section", "toc_depth": "2-3"}})
        body = md.convert(text)
        m = re.search(r"<h1[^>]*>(.*?)</h1>", body, re.S)
        page.title = re.sub(r"\s*#$", "", re.sub(r"<[^>]+>", "", m.group(1)).strip()) if m else page.key
        page.title = html.unescape(page.title)
        page.headings = [t["name"] for t in md.toc_tokens[0].get("children", [])] if md.toc_tokens and md.toc_tokens[0].get("level") == 1 else [t["name"] for t in md.toc_tokens]
        body = rewrite_links(page, body, pages, copies)
        body = re.sub(r"<img ", '<img loading="lazy" ', body)
        page.body = body
    return copies


def nav_order(pages):
    """(section title, [(group title, [keys])]) in reading order."""
    user = [(g, ["manual/" + k for k in keys]) for g, keys in USER_GROUPS]
    admin_extra = [k for k in ADMIN_FIRST]
    ops = [(g, keys) for g, keys in OPS_GROUPS]
    used = {k for _, ks in OPS_GROUPS for k in ks}
    rest = sorted(k for k in pages if k.startswith("ops/") and k not in used)
    if rest:
        ops.append(("More", rest))
    return [("User guide", user), ("Administrator guide", [("Start here", admin_extra)] + ops)]


def sidebar(pages, nav, current):
    out = []
    for section, groups in nav:
        out.append(f'<p class="nav-section">{html.escape(section)}</p>')
        for gtitle, keys in groups:
            keys = [k for k in keys if k in pages]
            if not keys:
                continue
            is_open = " open" if current in keys else ""
            out.append(f'<details class="nav-group"{is_open}><summary>{html.escape(gtitle)}</summary><ul>')
            for k in keys:
                cls = ' class="current" aria-current="page"' if k == current else ""
                out.append(f'<li><a{cls} href="{{ROOT}}{pages[k].out_rel}">{html.escape(short_title(pages[k].title))}</a></li>')
            out.append("</ul></details>")
    return "\n".join(out)


def short_title(title):
    return re.sub(r"^\d+\.\s*", "", title)


TEMPLATE = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{title} · Keel PLM docs</title>
<meta name="description" content="Keel PLM documentation: {title}.">
<link rel="icon" type="image/svg+xml" href="{root}logo.svg">
<link rel="stylesheet" href="{root}docs.css">
</head>
<body>
<a class="skip" href="#content">Skip to content</a>
<header class="top">
  <button class="menu" type="button" aria-label="Menu" aria-expanded="false" aria-controls="side">Menu</button>
  <a class="logo" href="{root}index.html"><img src="{root}logo.svg" alt="" width="30" height="30">Keel PLM</a>
  <nav class="top-links" aria-label="Site">
    <a href="{root}manual/index.html"{user_cur}>User guide</a>
    <a href="{root}manual/24-administrator-and-operator-guide.html"{admin_cur}>Administrator guide</a>
    <a href="{repo}">GitHub</a>
  </nav>
  <div class="search"><input id="q" type="search" placeholder="Search the docs" aria-label="Search the docs" autocomplete="off"><ul id="results" hidden></ul></div>
</header>
<div class="layout">
  <aside id="side" class="side" aria-label="Documentation">
{sidebar}
  </aside>
  <main id="content" class="content">
    <article class="doc">
{body}
    </article>
    <nav class="pager" aria-label="Previous and next">{prev}{next}</nav>
    <p class="edit"><a href="{edit}">Edit this page on GitHub</a></p>
  </main>
</div>
<footer class="foot">
  Keel PLM is free software under the <a href="{repo}/blob/main/LICENSE">GNU AGPL-3.0</a>. It is built to support export-control and CMMC work; the software itself is not certified.
  All screenshots use synthetic demo data.
</footer>
<script src="{root}docs.js" data-root="{root}"></script>
</body>
</html>
"""


def write_pages(pages, nav):
    order = [k for _, groups in nav for _, keys in groups for k in keys if k in pages]
    for k in pages:
        if k not in order:
            order.append(k)
    order.insert(0, "manual/index")
    order = list(dict.fromkeys(order))
    for key, page in pages.items():
        root = "../" * len(Path(page.out_rel).parts[:-1])
        side = sidebar(pages, nav, key).replace("{ROOT}", root)
        i = order.index(key)
        prev_p = pages[order[i - 1]] if i > 0 else None
        next_p = pages[order[i + 1]] if i + 1 < len(order) else None
        prev = f'<a class="prev" href="{root}{prev_p.out_rel}"><small>Previous</small>{html.escape(short_title(prev_p.title))}</a>' if prev_p else "<span></span>"
        nxt = f'<a class="next" href="{root}{next_p.out_rel}"><small>Next</small>{html.escape(short_title(next_p.title))}</a>' if next_p else "<span></span>"
        is_admin = key.startswith("ops/") or key in ("setup", "manual/23-administration", "manual/24-administrator-and-operator-guide")
        text = TEMPLATE.format(title=html.escape(short_title(page.title)), root=root, sidebar=side, body=page.body, prev=prev, next=nxt,
                               edit=BLOB + page.src.as_posix(), repo=REPO,
                               user_cur=' aria-current="true"' if not is_admin else "", admin_cur=' aria-current="true"' if is_admin else "")
        dest = OUT / page.out_rel
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(text, encoding="utf-8")


def search_index(pages, nav):
    items = []
    for key, page in pages.items():
        plain = re.sub(r"<(script|style)[^>]*>.*?</\1>", " ", page.body, flags=re.S)
        plain = html.unescape(re.sub(r"<[^>]+>", " ", plain))
        plain = re.sub(r"\s+", " ", plain).strip()
        items.append({"u": page.out_rel, "t": short_title(page.title), "h": " ".join(page.headings), "x": plain[:4000]})
    (OUT / "search.json").write_text(json.dumps(items, separators=(",", ":")), encoding="utf-8")


def main():
    if OUT.exists():
        shutil.rmtree(OUT)
    shutil.copytree(ROOT / "site", OUT)
    shutil.copy(ROOT / "src/Keel.Web/wwwroot/logo.svg", OUT / "logo.svg")
    shutil.copy(ROOT / "scripts/docs-site/docs.css", OUT / "docs.css")
    shutil.copy(ROOT / "scripts/docs-site/docs.js", OUT / "docs.js")
    (OUT / ".nojekyll").write_text("")
    pages = collect()
    copies = render(pages)
    nav = nav_order(pages)
    write_pages(pages, nav)
    search_index(pages, nav)
    shutil.copytree(ROOT / "docs/manual/images", OUT / "manual/images", dirs_exist_ok=True)
    for src, dest in copies.items():
        if (ROOT / src).exists():
            d = OUT / dest
            d.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy(ROOT / src, d)
    print(f"built {len(pages)} pages into {OUT}")


if __name__ == "__main__":
    main()
