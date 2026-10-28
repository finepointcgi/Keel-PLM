# ADR-0014: Blazor (Interactive Server) user interface

**Status:** Accepted · 2026-10-27 · **Supersedes** [ADR-0010](0010-frontend.md)

## Context
The product owner directed that the UI be built with Blazor instead of React. That makes the stack .NET end to end: one language, one toolchain, and one SDK to approve and patch in an air-gapped environment, with no Node/npm supply chain to vet. Blazor has two hosting models:

| | Interactive Server | WebAssembly |
|---|---|---|
| Where code and data live | Server; the browser gets rendered DOM diffs over SignalR | The .NET runtime and app DLLs download to the browser |
| Tokens in the browser | None (HTTP-only session cookie) | Access tokens held in browser memory |
| Business logic | Calls module services in-process, so the same access checks and audit as the API | Must call the HTTP API |
| Offline / latency | Needs a live connection; every interaction is a round trip | Works with intermittent connectivity |
| Scale | One circuit (memory) per user; fine for A11 (500 concurrent) | Stateless server |

## Decision
- **Blazor Web App with the Interactive Server render mode, with prerendering disabled.** Without prerendering each page loads once, so object reads are audited exactly once instead of twice.
- UI components live in a Razor class library, `Keel.Web`. The `Keel.Api` host serves both the UI and the REST API (one deployable, ADR-0001).
- Components call module services (`PartService`, …) **in-process** with the signed-in `Subject`. The services own access control and audit, so the UI and the REST API cannot diverge.
- **UI authentication:** server-side OIDC authorization code flow with PKCE as a *confidential* client, plus an HTTP-only, `Secure` (production), `SameSite=Lax` session cookie (Strict would drop the cookie on the redirect back from the IdP). No token ever reaches browser JavaScript. That's stronger than ADR-0007's SPA design and matches its documented BFF alternative.
- **API authentication is unchanged:** JWT bearer for machine clients and integrations. A policy scheme routes `/api/*` and `Authorization: Bearer` requests to JWT, and everything else to the cookie, whose challenge redirects to the IdP.
- Marking banners: every page renders through `PageFrame`, which requires a `Banner` parameter. The top and bottom banners are drawn by the layout from that value, so no page can render without one.
- Large BOM trees (increment 1.4) use `Virtualize` with server-side flattening and lazy expansion.
- The CSP stays `default-src 'self'`: Blazor Server needs no inline script or `eval`, and the SignalR WebSocket is same-origin.

## Consequences
- Each connected user holds a server circuit (a few hundred KB). Sizing is in the ops guide (Phase 5). Sticky sessions are required behind a load balancer.
- A dropped connection shows Blazor's reconnect UI. Unsaved form input in that circuit is lost, an acceptable trade-off for CUI handling.
- The React app from increments 1.1–1.2 is removed, along with the Node toolchain.
- Component tests use **bUnit**. End-to-end tests (1.7) drive the real app with Playwright for .NET.

## Alternatives
- Blazor WebAssembly: tokens in the browser and a large download. Its only advantage, offline capability, isn't a requirement.
- Keeping React: rejected by the product owner.
