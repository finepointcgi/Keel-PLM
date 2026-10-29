# ADR-0034: Automated accessibility audit and performance at scale

Status: accepted (gap wave D, 2026-10-29). Builds on ADR-0013 (testing strategy), ADR-0014 (Blazor UI, strict CSP), ADR-0028 (design system), ADR-0029 (visuals) and
ADR-0031 (list sorting; GAP-A-5).

## Context
Two promises of the design system were checked only by hand or by narrow tests: that the product meets WCAG 2.1 A and AA (the technical basis of Section 508), and that it
stays usable at the volumes PLM data reaches (tens of thousands of lines in one structure, hundreds of thousands of parts). GAP-A-5 recorded that the sorted columns have no
dedicated indexes and asked for a measurement before adding any.

## Decisions

### 1. axe-core runs in the end-to-end suite, vendored, test-only
- `tests/Keel.E2E/vendor/axe-core/axe.min.js` is axe-core 4.10.2, unmodified, with its licence text and `VENDOR.md` (version, SHA-256, upstream hash, licence). A test pins the
  hash, so replacing the file is a deliberate act. **The licence is MPL-2.0, not MIT** (the task brief assumed MIT): MPL-2.0 is file-level copyleft, the file is used unmodified
  and is never distributed with the product, so no obligation attaches to Keel. A test asserts that nothing under `src/` carries the library and the container build does not
  copy `tests/`.
- It is injected through the DevTools protocol (`page.EvaluateAsync`), not as a `<script>` element, so the strict CSP stays in force during the audit and is not weakened
  for tests. The rule set is the tags `wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa` plus `best-practice` (landmark, region and table-header rules). Every violation fails the test;
  there is no allow-list.
- Coverage: every routable page and each tab (`?tab=` deep links), the visuals in picture and table views, the file viewer, admin pages, and the overlays (palette with results,
  user menu, notification bell, confirmation dialog), in light, dark and high-contrast at 1280 px and in light and high-contrast at 375 px. The banners are asserted on every page.

### 2. Fixes made because the audit found them
- Marking banners are `role="region"` with their existing label instead of `role="note"`, so they are landmarks (rule `region`); text, tone and position are unchanged.
- Treegrid and grid bodies have `role="rowgroup"` (Blazor's `Virtualize` spacers broke the required-children rule).
- Visual detail panes and timeline groups are `role="group"` instead of nested `aside`/`section` landmarks.
- `a.button` keeps its on-accent colour inside `table.data` (the link colour won on specificity: 1.15:1 contrast).
- Empty header cells got visually hidden text; at 375 px the brand text and the user name are visually hidden, not `display:none`, so the link and the menu button keep a name.
- A table wrapper that really scrolls and has nothing focusable becomes a labelled, focusable group (`shell.js`), so keyboard users can scroll it (WCAG 2.1.1).
- The viewer toolbar moved inside the viewer's `main` landmark.

### 3. Performance is measured through the real services, in an isolated database
- `ScalePerformanceTests` builds a throwaway database in the test container (never a dev or demo database): about 108,000 parts including a ten-level 50,000-line structure,
  50,000 documents, 20,000 changes, 150,000 audit events and 20,000 notifications, and measures part search and every part sort key, the other lists, BOM explode, where-used,
  each home card, the activity feed and the notification list, as a fully authorized and as a restricted caller. `KEEL_SCALE_FACTOR=3` repeats it at about 250,000 parts.
- Budgets are generous multiples of the measured times (docs/ops/performance.md), asserted on the median of five calls, to catch an order-of-magnitude regression only.
- **No migration.** EXPLAIN shows every list query dominated by one pass over the access-filtered set, because `count(*) OVER ()` must see every readable row; a sort-key index
  would not remove that pass, and the whole pass costs 60 ms at 108,000 parts and under 300 ms at 250,000. The home cards use existing indexes (4 to 12 ms). GAP-A-5 is
  therefore closed by measurement: add indexes only if a list is measured beyond its budget. (Migration number 0018 stays free for a later need.)

### 4. The browser holds only what is in view
- The structure grid was already virtualized. The purchasing summary, where-used and the comparison tables are now paged (100 rows per page, the shared `Pager`); the
  structure-difference picture draws at most 500 lines and says so, with the paged table as the complete alternative. The treemap already folds small tiles.
- `ScaleTests` (end to end) opens a 50,000-line structure in its own database behind its own host, expands every level, scrolls to the last line, switches views and asserts the
  DOM stays under 4,000 nodes and 120 grid rows, that the banners are present, and that the audit finds nothing with the big grid on screen.

### 5. A found defect, fixed
The home card "Changes in my programs" read the 200 newest open changes the user could read and kept those of her programs afterwards, so on a busy system her own changes could
be missing. The program filter is now part of the query (`ChangeSearchQuery.Programs`, optional; the read filter still applies).

## Consequences
- A page that fails the audit fails the build of the end-to-end suite; new components must pass the same rules in all three themes.
- What an automated tool cannot judge stays on the manual list in docs/ux/accessibility.md.
- The performance test takes about 40 s and about 1 GB of the test container's memory; run it with the other integration tests, one suite at a time.
