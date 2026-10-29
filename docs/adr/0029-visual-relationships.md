# ADR-0029: Relationships as pictures (impact map, treemap, structure diff, timeline, workflow map)

Status: accepted (UX-3, 2026-10-28). Builds on ADR-0004 (access), ADR-0017 (BOM), ADR-0018 (workflow), ADR-0019 (change), ADR-0021 (baselines), ADR-0024 (MBOM), ADR-0028 (design system).

## Context
Engineers think in graphs and trees, but where-used, impact analysis, comparisons, history and review progress were tables. The pictures must never show more than the tables do: markings, redaction (A4) and the idle lock apply unchanged, there are no CDNs, and the CSP forbids inline script and style.

## Decisions

### Security model: no new data path
- Every picture is built from the result of an existing, access-filtered service call (`ImpactAnalysisService`, `BomService.Explode/Compare*`, `MbomService.Compare/SideBySide`, `WorkflowService` views). Nothing queries marked tables from a component.
- The only new queries are the timeline sources and the workflow map. Each lives in the owning module (new files), applies the caller's access itself (`AccessPolicy.Evaluate` on a key lookup, or `BuildReadFilter` in the join) and is covered by the architecture tests.
- A hidden object is a placeholder with a fixed label ("Restricted item", "A restricted item was involved"): no number, name, banner, link or marking text. Builders ignore identifying fields of redacted input even if a source supplied them.
- Counts: the "N more" node of the impact map counts readable assemblies only; restricted ones folded beyond the level limit are neither drawn nor counted. A restricted treemap block is one block per assembly, sized by the quantities the BOM service already returns, and does not say how many lines it holds. Timeline placeholders for hidden changes, links and manufacturing BOMs replace what a privileged user sees one for one (every user gets the same number of entries); hidden baselines are omitted entirely.
- The page banner is the aggregate of what is drawn (`BannerMerge`), recomputed on every filter, drill-down and tab change. The SVG export is rebuilt on the server for the caller (`/ui/changes/{n}/impact-map.svg`, session cookie, no token in the URL, audited as `change.impact_export`) and burns in the banner (top and bottom bar) and the distribution statement.
- The idle lock removes the page from the DOM, so the pictures go with it (E2E checks this).

### Layout algorithms (pure C#, `Keel.Web/Services/Visuals`)
- `LayeredLayout`: callers assign layers; ordering inside a layer uses barycenter sweeps down and up and keeps the order with the fewest crossings; coordinates are columns left to right. Deterministic (ties by input position then id), no node overlap.
- `SquarifiedTreemap` (Bruls et al.) plus `Limit` (the largest N-1 items and one "others" item). `BomTreemapModel` nests one level inside big tiles and stays within 400 tiles.
- `LanePacking` (greedy interval partitioning, minimal lanes) for the horizontal time line labels.
- No third-party code is vendored. SVG is rendered by Razor; `wwwroot/js/visuals.js` (own ES module) does pan, zoom (+ - 0, Ctrl+wheel, drag, pinch), roving focus, tooltip and navigation.

### Accessibility
- Every picture has a table or list equivalent with the same data (map: table; treemap: table; diff: the existing tables; timeline: list; workflow map: an ordered list). Below 600 px the table is offered first and the picture stays one tap away.
- Nodes are focusable in logical order (roving tab stop; arrows follow connections or columns, Enter opens, Ctrl+Enter new tab, Space selects, Home/End), tooltips appear on focus as well as hover, legends name every kind with a shape or pattern and a word. States and marking classes have a colour AND a pattern. Diff rows and workflow statuses are an icon/glyph plus a word.
- Colours are tokens only; a test checks text on every fill used (4.5:1) and the focus colour against every fill (3:1) in light, dark and high contrast. Nothing animates.

### Not done
Baseline timelines (optional in the brief), a sunburst, unchanged lines in the diff (the comparison services do not return them).

## Consequences
New interface `ITimelineSource` (Keel.Core); `WhereUsedEntry` gained optional `UsedKey` and `Effectivity` (additive). Budgets are in docs/increments/7.2-visuals.md.
