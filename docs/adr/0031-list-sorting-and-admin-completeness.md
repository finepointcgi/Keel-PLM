# ADR-0031: Server-side list sorting, markings change and supply types for MBOMs, one confirmation pattern

Status: accepted (gap wave A, 2026-10-29). Builds on ADR-0004 (access and markings), ADR-0014 (Blazor UI), ADR-0024 (manufacturing BOM), ADR-0028 (shell, `DataTable`,
`ConfirmDialog`) and ADR-0030 (tab deep links by fragment).

## Context
Four gaps were left by earlier increments. (1) `DataTable` sorted only the rows of the loaded page, so "sort by name" on a 50-row page of 400 parts was wrong in a way
users could not see. (2) The manufacturing BOM had a markings-change service and supply-type endpoints but no screens. (3) Some administration pages built their own
inline "Confirm" panels instead of the shared dialog, and older panels skipped heading levels (h1 straight to h3). (4) A link that named a tab in the query string
(`?tab=impactmap`) opened Overview.

## Decisions

### 1. A sort is a lookup in a whitelist, applied inside the access-filtered query
- `Keel.Core.SortMap` holds, per list, the allowed keys and for each key the SQL expressions that implement it. The expressions are written in code (`lower(m.name)`,
  `length(r.revision), r.revision, r.latest_iteration`); the request supplies only a key, which is looked up, and a direction, which becomes the keyword `ASC` or `DESC`.
  `SortSpec` is the parsed request. An unknown key or direction is refused: `ArgumentException` from a service, HTTP 400 (`sort` validation problem listing the
  allowed keys) from the REST layer, and the default order from a page whose address was edited by hand. Nothing from a request is ever concatenated into SQL.
- The order is appended to the query that already contains `AccessPolicy.BuildReadFilter`, so unreadable rows are removed before ordering, `LIMIT/OFFSET` and `count(*) OVER ()`.
  Every key is a column of the row itself (or of a join that applies the read filter in its `ON` clause), never an aggregate or rank over rows the caller cannot read. For
  baselines the root part is ordered by the joined part number, which is NULL when the caller may not read the root, so a hidden number cannot influence the order.
  A stable tie-breaker (the unique number) ends every sorted order so paging never repeats or skips a row.
- Consequence tested as an oracle: for every key and direction, the order a restricted caller sees equals the order a full-access caller sees with the unreadable rows
  removed, the set of rows and the total do not change with the sort.
- Lists covered: parts, documents, changes, baselines, MBOMs (SQL) and the workflow task list (in memory, after redaction, from the values the caller is shown; redacted tasks
  sort last). The sort is written to the search audit event (`sort`: `name asc`). Without a sort every list keeps its previous default order.
- `DataTable` gets `ServerSort` (the order the page is in). A table with `OnServerSort` is server-sorted: only columns with a `ServerSortKey` are sortable, the rows are shown
  as returned, and a header click asks the page to re-query and writes `sort` and `dir` into the address bar with `history.replaceState` (no navigation, so focus stays on the
  header and the router's focus-on-navigate does not fire). The page reads the order from `[SupplyParameterFromQuery]`, so a reload or a shared link shows the same order.
  The saved per-user column layout no longer stores the sort of a server-sorted table.

### 2. MBOM markings and supply types reuse the existing pattern
- A "Security markings" tab on the MBOM page shows the markings and, for a user who may modify the latest In Work revision, a change form with a mandatory reason. The service
  was already fail-closed (reason, locked revisions, never less restrictive than the part, caller must keep Modify, one transaction with the audit event). No e-signature is
  asked, exactly as for parts, documents and changes (only workflow signatures need step-up).
- `/admin/supply-types` (administrator policy, in the administration navigation) lists, adds, edits and retires supply types. Retiring or restoring needs a reason (new
  `SaveSupplyTypeCommand.Reason`, also accepted by the REST endpoint); Make, Buy and Consume cannot be retired; the audit event carries label and state before and after.
- Found while testing: the "markings are unchanged" check compared `Markings` records, whose synthesized equality compared the immutable sets by reference, so it never fired for
  markings built separately. `Markings` now has value equality (sets compared element by element).

### 3. One confirmation pattern and a heading outline that is tested
Every inline confirm panel became the shared `ConfirmDialog` (focus trap, Escape, focus return, reason with the dialog's own validation): export import, class move and retire,
lifecycle publish and migration, workflow template publish, reason codes, list items, attribute retire. `EmptyState` titles are no longer headings (they are messages and would add
a level on list pages); panel headings on pages that sit directly under the page title are h2 with h3 below. `HeadingOutlineTests` reads the Razor sources, expands component tags
to their own outlines and fails a page with other than one h1 or a skipped level, and a component that skips a level inside itself.

### 4. A tab can be named by `?tab=` as well as by the fragment
`TabSet.FromUri` accepts `?tab=id` when the fragment names no tab (the fragment wins: it is what choosing a tab writes). Pages take the address's tab only when the address is new
(`TabSet.OnNavigation`), because choosing a tab rewrites the browser's address but not the circuit's copy, and a page that reloads its data after an action must keep the user's tab.

## Alternatives considered
- Passing a column name straight into `ORDER BY` after a character check: rejected; a whitelist is the only form that cannot be wrong by omission.
- Sorting in the page after loading more rows: rejected; it does not scale and still shows a wrong first page.
- `NavigationManager.NavigateTo(..., replace: true)` for the sort in the address: rejected; the router re-runs focus-on-navigate and moves focus to the heading on every sort.
- A new e-signature step for MBOM markings: rejected; no other markings change asks for one, and step-up exists to bind a signature to a workflow decision.

## Consequences
No migration. New REST query parameters `sort` and `dir` on the five list endpoints and on `GET /api/v1/workflow/tasks`; a 400 for an unknown key. The 6 list pages, the MBOM page,
the administration navigation and `ConfirmDialog` users changed. Sorting costs nothing without an index for the page sizes in use (50 rows per page, sort inside the filtered set);
very large lists would want the sorted columns indexed (not needed at current volumes, see GAP-A-5).
