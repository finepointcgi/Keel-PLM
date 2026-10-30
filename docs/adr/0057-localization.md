# ADR-0057: Localization foundation: per-user language, resource files, culture-aware display

Status: Accepted

## Context
Keel's screen text was written in English inside Razor files and C# helpers. Teams that work in another language cannot use the product comfortably, and adding a language
meant finding every string by hand. The platform is on-prem and air-gapped, so nothing may call a translation or culture service, and the images run with
invariant globalization (`Directory.Build.props`): there is no operating-system culture database to depend on. Marking banners, portion marks and classification text are
legal handling text and must read exactly as the policy dictates in every language.

## Decision
- **Standard .NET resources, no service.** Screen text lives in `.resx` files under `src/Keel.Web/Resources` (`ShellStrings`: rail, user menu, palette; `CommonStrings`:
  shared controls, states, kinds, reasons, formats, units; `PageStrings`: the converted pages). Satellite assemblies (`es/Keel.Web.resources.dll`) are built by the SDK.
  Components ask for `IStringLocalizer<T>`. The implementation (`KeelLocalizer<T>`) is a thin layer over `ResourceManager` that reads the circuit's language instead of the thread culture.
- **The language is a per-user display preference**, `ws.preference.language` (migration 0036, default `'en'`, the CHECK bounds only the shape of the tag; which tags are shipped is the
  application's list in `Locales`, so a new language needs no migration). Choosing a language in the **user menu** saves it and reloads the page, so the new circuit starts in it
  (no language switches half way through a page). The static page shell reads the same value for `<html lang>`. A stored tag that is not shipped reads English.
- **The language is not the thread culture.** `CurrentCulture` stays invariant, so parsing, CSV and Excel export, SQL parameters, hashes and audit details never change with
  the language. Only display formatting follows the language, through `UiLanguage` (`Utc`, `Date`, `LongDate`, `Number`, `Quantity`, `Unit`), whose `CultureInfo` is built
  from resource strings (`Format.*`: separators, date patterns, month and day names, first day of the week) so it works without ICU. `PredefinedCulturesOnly=false` lets the runtime create `CultureInfo("es")`
  without OS data; only tags from the shipped list are ever created. Times stay in UTC (there is no per-user time zone yet).
- **Adding a language is a translation job.** Add `<Name>.<tag>.resx` next to each English file (including the `Format.*` keys), then one line in `Locales.Shipped`. The
  parity test fails until every key exists with the same `{n}` placeholders, no empty text and no orphan keys; a second test fails for a file without a `Locales` entry.
- **Sentences stay whole.** One resource string per sentence, with `{0}` placeholders. Plurals are two whole sentences (`Key.One`, `Key.Other`, `Count()` helper). A sentence with a
  link or emphasis uses `Rich.With` / `Rich.Strong`, which fill placeholders inside the translated sentence, so a language can reorder them. A scan test fails on `S["a"] + x`, `x + S["a"]`
  and interpolation of a translated string. Labels that come from data (rail, palette) use a key built from the English text (`Nav.item.my-tasks`) and fall back to English when none exists; a test
  proves every current label has a key in every language.
- **Pseudo-locale.** `qps-ploc` is generated from the English text at run time (accents, about a third longer, bracketed, placeholders kept), never a file that can drift. It is offered
  in the picker only when `Keel:Localization:PseudoLocale` is `true` (the E2E host turns it on). It makes untranslated text, clipped layouts and glued sentences visible; the E2E audit runs axe and a sideways-scroll check on it at desktop and phone width.
- **Marking banners and classification text are never translated.** They are produced by `Markings` and rendered from data. `MarkingBanner` and `MarkingChip` do not use a localizer
  (a test checks the source), no resource value may contain `UNCLASSIFIED`, `DISTRIBUTION`, `NOFORN` or `//` (a test checks every locale), and the E2E test compares the banner text in English and Spanish
  character for character. Reason codes in the audit log, audit actions, object numbers and administrator-defined labels are data and stay as stored.
- **Staged and honest.** Converted: the shell (skip link, top bar, rail groups and links, user menu, command palette, error banner), Home and its cards, Parts list, create and the part page's
  edit/save/check-out controls, Documents list and create, My tasks and the task (approval) page with the sign panel, the shared table, pager, skeleton, confirmation and breadcrumb controls, the not-found
  and access-denied pages, and the reason texts of those flows. Everything else is English until converted (see the increment note for the list).
- **Right to left is out of scope.** `dir="ltr"` is set explicitly. Layout issues a right-to-left language would meet are listed in the increment note.

## Consequences
- Components that use a localizer need `AddKeelLocalization()` in a bUnit test. The small shared controls (breadcrumbs, pager, skeleton, confirmation dialog, table, sign panel, object card) resolve it through
  `Loc.Strings<T>`, which falls back to English when it is not registered, so other tests that render them keep working.
- An English string that moves to a resource must keep its exact text: existing E2E tests look at English text.
- Text that arrives from services (outcome messages of a module's validation, workflow event details, attention-card detail lines) is the module's own English until the module is localized; the Spanish screens show it as written.
- Assumptions A85-A87 in `docs/assumptions.md`.
