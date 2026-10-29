# ADR-0046: A calm, token-driven visual language

Status: accepted (increment 9.1, 2026-10-29). Builds on ADR-0028 (shell and design system), ADR-0029 (diagram kit) and ADR-0034 (accessibility audit).
Decisions are UI-n in [increments/9.1-ui-polish.md](../increments/9.1-ui-polish.md).

## Context
The UX-1 design system was correct but plain: flat white boxes, tight rhythm, one blue, little hierarchy between primary and secondary content. The product is used
for hours by engineers, in a closed network, under mandatory marking banners.

## Decisions
1. **Tokens first.** Every colour, radius, shadow, duration and type step is a token in `tokens.css`; components use `var(--…)` only. New tokens: `--c-accent-bg` (tint of the accent
   for selected and hover states), `--radius-3`, `--lh-tight`, `--fw-*`, `--dur-1/2`, `--ease`, `--tracking-caps`. Every theme block carries the colour tokens.
2. **Calm palette.** Cool grey surfaces, one indigo-blue accent, soft two-layer elevation, 1 px borders. Status colours keep their meaning (In Work = info, Under Review = warning,
   Released = success, Obsolete = muted) and a state badge now also carries a dot. Light, dark and high contrast keep the AA pairs; the contrast unit test gained the accent tint pairs.
3. **Hierarchy.** Object page titles 1.75 rem with an accent top rule, card and panel titles with a divider and an icon tile, uppercase 0.75 rem labels only for rail groups.
   Buttons: primary solid, secondary outline on surface, danger solid red, link quiet; one primary per view.
4. **Density and tables.** Row padding 9/14 px (compact 3/10), sticky header, light zebra via `color-mix` of two tokens, accent-tint hover, tabular numerals everywhere.
5. **Focus.** 3 px ring in `--c-focus`, offset 2 px, with a 2 px surface halo; programmatic focus targets (h1, main) show none.
6. **Motion.** CSS only, 120 to 200 ms: hover/active transitions, toast and dialog entrance, card elevation. `base.css` already collapses all durations under `prefers-reduced-motion`.
7. **Banners are untouched.** Marking banner and demo banner rules and colours are unchanged; chips gained a small shield icon.
8. **Living style guide** at `/tools/style-guide` (Tools nav, UNCLASSIFIED): tokens, type, buttons, chips, tables, forms, banners, states and a side-by-side specimen of all three
   themes using `.sg-theme[data-sg-theme]` scopes in `tokens.css` (a unit test keeps them equal to the real theme blocks). It is part of the axe audit.

## Consequences
Pages restyle without markup changes (class names kept). A new colour means a token in four places (light, dark, system dark, high contrast) plus the three style-guide scopes.
Visual regressions are caught by the axe audit, the contrast test and the screenshots in `docs/ux/screenshots/polish-*.png`.
