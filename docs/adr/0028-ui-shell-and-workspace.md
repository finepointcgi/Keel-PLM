# ADR-0028: UI shell, design system and personal workspace data

Status: accepted (UX-1, 2026-10-28). Builds on ADR-0004 (access), ADR-0014 (Blazor Server), ADR-0026 (idle lock).

## Context
The UI worked but felt like a developer tool: a flat row of links, object pages that were one long scroll, a redirect instead of a home page, no
search across object types, no feedback layer, and styling that followed the operating system only. The constraints did not change: strict CSP
(`default-src 'self'`, so no inline script and no inline `style` attributes), no CDNs or external fonts, marking banners always present, an idle lock
that removes the page from the DOM, and nothing may reveal that an object exists to a user who cannot read it.

## Decisions

### Design system (`wwwroot/css/*`)
- Four files: `tokens.css` (colour, spacing, radius, elevation, type, focus, density), `base.css` (elements), `layout.css` (shell), `components.css`.
  Components use only `var(--…)`. A test (`ThemeContrastTests`) reads the real token file and checks WCAG 2.1 AA pairs in every theme (4.5:1 text,
  3:1 focus ring and input borders) and that the dark theme chosen explicitly equals the dark theme followed from the system.
- Themes: light, dark, high contrast and "system". The user's choice is stored server side (`ws.preference`). The static page shell (`App.razor`)
  renders `data-theme` and `data-density` on `<html>` from the stored preference, so the first paint is right without inline script. "System" has no
  attribute rule: `prefers-color-scheme` decides in CSS. Changing the theme in the user menu applies it through the shell module (`applyPreferences`) and saves it.
- Marking banner tones are defined once, outside the theme blocks, and a test fails if a theme redefines them: a banner means the same in every theme.
- Icons are an inline SVG sprite served from `wwwroot/icons.svg` (`<use href>`, same origin). The sprite carries no `style` attribute (the CSP applies to it as a document).
- Only one piece of JavaScript was added: `wwwroot/js/shell.js`, an ES module served same-origin like `session-lock.js` (focus moves and traps, the palette
  shortcuts, the tab fragment, the unsaved-changes `beforeunload`, a dismiss helper). It decides nothing about access. No third-party code was added, so the
  vendored-asset hash discipline of the viewer does not apply.

### App shell
- `MainLayout` renders skip link, top banner, top bar (search button, session status, user menu), grouped rail navigation (`NavModel`, filtered by roles),
  `<main id="main">`, bottom banner, toast container and the command palette. Below 768 px the rail is a drawer behind a menu button.
- The idle lock is unchanged: while locked the layout renders `LockedPanel` instead of the page and renders no rail, no palette, no user menu. Tests assert this.
- `PageFrame` keeps `Banner` and `Title` required and adds optional `Crumbs` and `Header`. A page has exactly one `h1`: the plain title, or the number and name
  inside `ObjectHeader` (a test forbids `<h1>` in pages). `<PageTitle>` carries the same text.

### Object pages
Parts, documents, changes, baselines and manufacturing BOMs share the pattern: `ObjectHeader` (number and name as h1, the object's own marking chip, version and
state badges, favorite star, lifecycle `Stepper` built from the data-defined lifecycle), a sticky action bar for the primary actions, and `TabSet` tabs. Tabs are
the WAI-ARIA pattern (roving tab index, arrows, Home/End), the selected tab is mirrored into the URL fragment with `history.replaceState` (deep-linkable, no
navigation, scroll position kept) and only the selected tab's panel is rendered. A panel that reports a banner (BOM rows, changes, impact analysis, TDP preview)
contributes to the page banner only while its tab is on screen: the page banner is still the aggregate of what is shown.

### Personal workspace data and the module `Keel.Workspace`
Migration `0014_workspace.sql` adds schema `ws`: `preference`, `table_preference`, `recent_item`, `favorite`, `saved_search`. Every table is keyed by the
IdP subject (`user_sub`). The module `Keel.Workspace` (references Core, Security, Audit only) owns persistence; aggregation and re-authorization stay in
`Keel.Web/Services` because they must call the other modules. Reasons for a module rather than Web-internal SQL: the personal data has its own tables,
caps and audit rules and deserves integration tests that do not need the UI; Web stays composition.
- Ownership is structural: every method takes the signed-in `Subject` and every statement filters on its id. A unit test checks that no workspace statement lacks `user_sub`.
- Stored pointers hold only (kind, number, time). No name, markings or content. The tables are low-value personal data; back up with the database, restore is optional.
- Caps, enforced under a per-user advisory lock in the same transaction: 50 recent items (one row per object, repeat views within 60 s ignored), 100 favorites,
  50 saved searches, 200 table layouts. Concurrency is tested.
- Audit: saved-search create, replace and delete are audited with the scope and a SHA-256 of the text, never the text. Views, favorites, theme and layouts are not audited
  individually (the read of the object itself is audited by its module). Dashboard cards write one `workspace.read` event naming the numbers returned, like the module searches do.

### Search, palette and dashboard security model
- No new place may answer a question the services would answer differently. The palette and `/search` call `PartService.SearchAsync`, `DocumentService.SearchAsync`,
  `ChangeService.SearchAsync`, `BaselineService.SearchAsync` and `MbomService.SearchAsync` (read filter in SQL, audited). Merge and rank (exact number, number prefix, text) happen
  in `GlobalSearchService`; per-kind limits; the palette debounces and cancels stale requests; typed prefixes narrow the kinds.
  Totals are counts of what the user may read. An exact number of an unreadable object returns nothing, exactly like a number that does not exist.
- Recent items and favorites are re-resolved on every render with SQL that carries `AccessPolicy.BuildReadFilter` for the signed-in user (architecture test), so an object whose markings were raised,
  or whose program the user left, disappears silently: no "no longer available" row with a number. (Manufacturing BOM pointers are resolved through `MbomService`, because only Keel.Bom may read its schema.)
- Home cards load independently, fail independently and report the aggregate banner of exactly the rows they show; the page banner is the union. Workflow tasks on objects the user can
  no longer read are neutral "restricted" rows with no number, name or markings.
- Oracle tests compare resolve, released, program-change, search results and totals for six kinds of user against `AccessPolicy.Evaluate`.
- The palette and shortcuts exist only in the unlocked layout; `CurrentUser` still fails closed.

### Feedback layer and tables
`ToastService`/`ToastContainer` (two `aria-live` regions without roles, so they never collide with `role=status`/`alert` of pages; auto-dismiss, pause on hover and focus, queue of 4, never focus),
`ConfirmDialog` (modal, focus trap, destructive variant starts on Cancel, optional required reason), `EmptyState`, `Skeleton`, `UnsavedGuard`. `DataTable` gives sortable headers (`aria-sort`),
a column chooser, density, a sticky header inside its own scroll container, arrow-key row navigation and loading, error and empty states, with per-user layouts per table id.
Paging and filtering stay on the server in the owning page. The existing services do not take a sort order, so sorting is within the page of results shown and the header tooltip says so
("Sort the rows of this page"); a column can declare a server sort key when a service gains one.

## Consequences
- A change to a token or component restyles every page; the contrast and CSP-hygiene tests catch the regressions that matter.
- Hidden tabs are not rendered, so their data is not loaded or audited until opened (the part page loads less on first view).
- The palette sends one search per kind per pause in typing; each is audited by its module (as any search is). Short queries (under two characters) are not sent.
- Sorting a table sorts only the current page. Server-side sort needs a sort parameter in each module search and is a follow-up.
- Not delivered (see docs/increments/7.0-ux-shell.md): relationship graphs (UX-3), notification center and comments (UX-4), a server-side sort order in the module searches.
