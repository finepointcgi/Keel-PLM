# ADR-0048: MudBlazor for the navigation rail, with collapsible groups

Status: Accepted

## Context
The navigation rail was hand-built: every group was always open, which makes a long rail (up to ten groups for an administrator) hard to scan. The user asked for collapsible group headers and a better look through MudBlazor.

## Decision
- Material 3 look: the rail follows the M3 navigation drawer (stadium items, state layers, accent container for the current page; icon pills when collapsed), shaped in `mud-keel.css` from the existing tokens. MudBlazor has no M3 theme of its own, and a theme provider is ruled out below.
- MudBlazor 9.12.0 (MIT) is a package of `Keel.Web`. This increment uses it for the rail only: `MudNavLink` for links and `MudIcon` for the group chevron. Other pages move over one at a time.
- Each group header is a disclosure button (`aria-expanded`, `aria-controls`). A group opens when it holds the current page, the first group (Work) starts open, and a choice made by hand is kept until the user goes to a page in that group. A group with one link named like the group (Programs) is a plain link. The collapsed icon rail is unchanged.
- **No `MudThemeProvider` and no `MudNavGroup`.** The strict CSP (`default-src 'self'`, no inline styles, air-gapped) blocks the inline `<style>` the theme provider writes and the inline style that `MudCollapse` animates with, so the group would never open. `wwwroot/css/mud-keel.css` sets the `--mud-*` variables from the Keel tokens instead, so MudBlazor follows light, dark, high contrast and density. `MudBlazor.min.js` is not loaded (not needed for links and icons; it was the source of CSP violations in the console).
- The CSP is not relaxed. A later page that needs a MudBlazor component with inline styles (dialogs, popovers, data grid) needs its own decision here.

## Consequences
- MudBlazor is a new third-party dependency: add it to the SBOM and the licence list (MIT, no network access at run time; the assets are served from the package's static web assets).
- The rail unit tests assert groups by `.rail-group` and open closed groups before looking for links.
