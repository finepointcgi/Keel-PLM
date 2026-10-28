# UX roadmap: from working tool to a product people like using

Written 2026-10-28 after reviewing the merged UI in a browser. Functionality is ahead of presentation: every
page works and enforces markings, but navigation, hierarchy and visual feedback are still developer-grade.
Hard constraints for every item: no CDNs or external fonts (air-gapped), strict CSP (no inline script/style), marking
banners always present, WCAG 2.1 AA / Section 508, usable at 375 px, access filter applies to everything shown.

## Observed problems (from the current build)
1. **Developer jargon in user-facing text.** Messages say "(increment 1.5)", "(increment 1.6)" (see `ViaMessage` in `Keel.Parts/PartService.cs`, `PartWorkflowGateway`, `UiText`). Users should read "Submit the part for review" instead.
2. **Link contrast on dark theme** is poor (default blue on near-black); several in-panel links are hard to read.
3. **Generic titles:** the part page `<h1>` is "Part", the ECN page "Change notice". The object number and name should be the heading.
4. **No landing page.** `/` just redirects to Parts. There is no "what needs me" view (tasks, changes awaiting review, recent items).
5. **Flat navigation.** Eight top-level links in one row, no grouping, no breadcrumbs, no global search.
6. **Long linear pages.** The part page stacks Work, Review, Changes, BOM, Documents, Classification, History as one scroll; users must scroll to find things.
7. **State is text, not shape.** Lifecycle, workflow progress and ECN status are badges and sentences; there is no visual stepper or timeline.
8. **Relationships are tables, not pictures.** Where-used, impact analysis and BOM structure are rows; engineers think in graphs and trees.
9. **No feedback layer.** No toasts, no inline success states, no empty-state guidance, no skeleton loading.
10. **No theme control.** Dark/light follow the OS only; no density or high-contrast option.
11. **Tables lack sorting, column choice, saved filters.**
12. **No preview of files.** A drawing or STEP file is a name and a download link.

## Wave UX-1: shell, home, search, object pages (one agent; touches Keel.Web widely)
- Design tokens + component library in `Keel.Web/wwwroot` (spacing, type scale, elevation, state colours, focus rings; light, dark, high-contrast; user preference stored server-side per user); icon set as inline SVG sprite (no icon fonts).
- App shell: grouped navigation (Work, Product data, Change, Configuration, Admin), breadcrumbs, user menu, session indicator, lock button.
- **Home dashboard**: my tasks and review requests, changes awaiting me, recently viewed (per user), favorites, saved searches, active ECNs in my programs, audit-lite activity. Everything access-filtered.
- **Command palette (Ctrl/Cmd+K)** and global search across parts, documents, changes, baselines, MBOMs with typed prefixes (`p:`, `d:`, `ecn:`), recent items, and keyboard navigation. Counts never reveal hidden objects.
- **Part, document, change and baseline pages redesigned**: header with number, name, markings chip, big lifecycle stepper; tabs or an in-page section nav (Overview, Structure, Documents, Changes, History, Security); sticky action bar; consistent panels.
- Toasts, empty states, skeletons, confirm dialogs, form validation summaries.
- Remove developer jargon from all user-facing strings and add a unit test that fails if a UI string contains "increment" or an ADR number.
- Tables: sortable columns, column chooser, density toggle, persisted per user.

## Wave UX-2: visual file preview (separate module `Keel.Viewer`; parallel-safe)
- In-browser 3D viewer for STEP (AP203/214/242 via a bundled WebAssembly OpenCascade importer), STL, glTF; 2D viewer for PDF and common images; bundled locally (three.js + wasm shipped as static assets, hashed in the offline bundle, CSP-compatible: workers and wasm need explicit `script-src 'wasm-unsafe-eval'` review: decide and document).
- Access-controlled streaming through the vault path (Download permission rules apply; "view only" mode when the user may Read but not Download? decide), marking banner and distribution statement overlaid on the canvas and in exported screenshots, audit event per view.
- Measure, section, explode by BOM level when the STEP assembly structure matches the PLM BOM; thumbnails generated server-side for lists (offline, no cloud).

## Wave UX-3: relationships as pictures (visual analytics; parallel-safe with UX-2)
- Impact graph for ECNs (affected parts, where-used parents, linked documents, open conflicting changes) as an SVG/Canvas graph with redacted nodes, keyboard accessible with a table alternative.
- BOM treemap/sunburst by cost-free proxies available in data (quantity, level, state, markings) and a structure diff visual for compare views.
- Revision and change timeline per part (revisions, ECNs, baselines, signatures) from existing history.
- Workflow progress map with who has signed and who is pending.

## Wave UX-4: collaboration and awareness
- In-app notification center (tasks assigned, review outcomes, ECN decisions, baseline events) driven by the outbox events; optional on-prem SMTP later.
- Per-object activity feed from the audit log, filtered by what the viewer may read; comments and @mentions on changes (marked objects: comments inherit the object's markings).
- Saved views, favorites, watch lists.

## Ground rules for every UX change
- Banner and markings first: a prettier page must never hide or demote the banner, chips or redaction.
- Prove no-leak: new aggregate widgets (counts, graphs, search suggestions) get an SQL-oracle test against `AccessPolicy.Evaluate` like facets did.
- Keep E2E green, and add Playwright checks for each new surface at 375 px and with keyboard only.
