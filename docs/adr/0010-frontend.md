# ADR-0010: React SPA and large-tree rendering

**Status:** Superseded by [ADR-0014](0014-blazor-server-ui.md) · 2026-10-27

## Decision
- React 19 + TypeScript (strict) + Vite, TanStack Query for server state, React Router.
- BOM tree grid: our own component on **TanStack Virtual** (open source, A16). It renders a flattened, windowed row model with lazy expansion by level, keyboard navigation and column resize. Designed for 50k+ visible rows.
- A **marking banner** component sits at the top and bottom of every page, showing the highest marking of the data on screen, plus per-object marking chips. Pages can't render object data without passing markings to the layout (enforced by the component API).
- No external fonts, CDNs or telemetry. All assets are bundled for air-gapped use.
- Accessibility to Section 508 / WCAG 2.1 AA.
