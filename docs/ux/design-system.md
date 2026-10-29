# Keel design system (UX-1)

Read with [ADR-0028](../adr/0028-ui-shell-and-workspace.md). New pages follow these rules; tests enforce the ones marked (T).

## Tokens (`src/Keel.Web/wwwroot/css/tokens.css`)
- Colour: `--c-bg`, `--c-surface`, `--c-surface-2`, `--c-text`, `--c-muted`, `--c-border`, `--c-border-strong`, `--c-accent` / `--c-on-accent`, `--c-link`, `--c-focus`,
  `--c-success|warning|danger|info` and their `-bg` tints. State colours are these (In Work = info, Under Review = warning, Released = success, Obsolete = muted).
- Marking banner tones (`--banner-*`) are fixed in every theme. Never override them. (T)
- Space `--sp-1..6`, radius `--radius-*`, elevation `--shadow-*`, type `--fs-*` (system font stack only), focus ring `--focus-ring`, density `--row-pad-*`, `--target`.
- Themes: `light`, `dark`, `high-contrast`, `system` on `<html data-theme>`; density on `data-density`. Add a colour only to the token file, in all three theme blocks. Contrast is checked from the file: 4.5:1 text pairs, 3:1 focus and input borders. (T)
- Components use `var(--…)` and no colour literals. (T)

## Components (`Components/Shared`, `Components/Layout`)
| Need | Use |
|---|---|
| A page | `<PageFrame Banner Title Crumbs>`; banner = aggregate of what the page shows, `UNCLASSIFIED` when nothing controlled is shown. One h1: never write `<h1>` in a page. (T) |
| An object page | `Header` = `ObjectHeader` (number and name as h1, `Banner` chip, `Badges`, `Steps` = `Stepper`), `div.action-bar` for primary actions, `TabSet` with panels rendered by a `switch` |
| A list page | search form + `DataTable` (stable `TableId`, `DataColumn`s) + `Pager`; give it an `EmptyState` with the next step; `Skeleton` while loading |
| Feedback | `ToastService` (`Success`, `Info`, `Warning`, `Error`); errors that block a form stay inline with `role="alert"` |
| Destructive or irreversible | `ConfirmDialog` (`Destructive`, `ReasonLabel`/`ReasonRequired`) |
| Unsaved form | `UnsavedGuard Dirty=…` |
| Icons | `<Icon Name="part" />`; add the symbol to `wwwroot/icons.svg` first (T) |
| Recently viewed / favorite | `ViewTracker`, `FavoriteButton` on object pages |

Banner and chips are never hidden, demoted or optional. Panels that show other objects report their banner and clear it when their tab is not shown.

## Rules (CSP)
No inline `<script>`, no `style=` attribute (use a class), no `on…=` HTML attributes, no external URLs or fonts, no `eval`. (T) JavaScript goes in an ES module under `wwwroot/js/`.

## Data access in components
A component never queries marked data itself: call the module service with `CurrentUser.GetAsync()`, or SQL that interpolates `AccessPolicy.BuildReadFilter` (architecture test). Personal data comes from `Keel.Workspace` and is re-authorized when shown.

## Accessibility checklist
- Landmarks: skip link, `header`, `nav aria-label="Main"`, one `main#main`; breadcrumb `nav`; tab panels labelled by their tab.
- Everything works by keyboard: palette (Ctrl/Cmd+K, `/`, arrows, Enter, Ctrl+Enter, Esc), tabs (arrows, Home, End), tables (arrows on rows, Enter), dialogs (trap, Esc, focus returns), user menu (Esc).
- Focus is visible (`:focus-visible`), moves to the h1 after navigation, and is never taken by toasts.
- Live regions: toasts use `aria-live` without roles. Do not add other always-present `role=status` or `role=alert` elements.
- Targets at least 28 px high (32 px comfortable). Works at 375 px with no sideways page scroll: wide tables scroll inside `.table-wrap` / `.dt-scroll`.
- `prefers-reduced-motion` is respected globally in `base.css`.

## Diagram kit (UX-3, [ADR-0029](../adr/0029-visual-relationships.md))
| Need | Use |
|---|---|
| Pan/zoom frame | `DiagramViewport` (toolbar +, -, Fit; patterns and arrow defs; tooltip). Content gets a `DiagramContext` for ids and `url(#pattern)`; `wwwroot/js/visuals.js` attaches behaviour |
| Node | `<g class="vis-node vis-k-KIND" id data-x data-y data-w data-h data-right/left/up/down data-href data-tip aria-label>`; one roving tab stop, Enter opens (`data-href`) or clicks |
| Legend | `Legend` with `LegendItem` (shape path + pattern + words; never colour alone) |
| Picture or table | `ViewSwitch` (aria-pressed buttons); table first below 600 px (`VisualsJs.IsNarrowAsync`) |
| Layouts | `LayeredLayout`, `SquarifiedTreemap`, `LanePacking`, `TimelineAxis` (pure, unit tested) |
| Pictures | `ImpactGraphView`, `BomTreemapView`, `StructureDiffView`, `TimelineView`, `WorkflowMapView` |
Rules: tokens only in `visuals.css`; SVG paint by class or presentation attribute, never `style=`; restricted items render no identifier and no link; the host recomputes the page banner from what is drawn.
