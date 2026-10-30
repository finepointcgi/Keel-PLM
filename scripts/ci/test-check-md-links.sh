#!/usr/bin/env bash
# Self-test of the link checker: a broken file, a broken image, a broken anchor and a valid link are each judged correctly.
set -euo pipefail
D=$(mktemp -d); trap 'rm -rf "$D"' EXIT
CK="$(dirname "$0")/check-md-links.py"
mkdir -p "$D/docs/images"; : > "$D/docs/images/ok.png"
printf '# Title\n\n## Sub heading {#custom}\n\nSee [ok](b.md#target-heading) [anchor](#custom) ![img](images/ok.png) `[code](nothing.md)`\n\n```\n[fenced](missing.md)\n```\n' > "$D/docs/a.md"
printf '# B\n\n## Target heading\n' > "$D/docs/b.md"
"$CK" --root "$D" docs >/dev/null || { echo "FAIL: valid links reported broken" >&2; exit 1; }
printf '[x](gone.md)\n' > "$D/docs/c.md"
! "$CK" --root "$D" docs >/dev/null || { echo "FAIL: missing file not detected" >&2; exit 1; }
printf '[x](b.md#nope)\n' > "$D/docs/c.md"
! "$CK" --root "$D" docs >/dev/null || { echo "FAIL: missing anchor not detected" >&2; exit 1; }
printf '![x](images/missing.png)\n' > "$D/docs/c.md"
! "$CK" --root "$D" docs >/dev/null || { echo "FAIL: missing image not detected" >&2; exit 1; }
echo "ok   link checker self-test"
