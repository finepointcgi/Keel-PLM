# ADR-0007: OIDC authentication and e-signature step-up

**Status:** Accepted · 2026-10-27

## Decision
> **Amended by ADR-0014:** the UI is now a server-side Blazor app using a confidential OIDC client and an HTTP-only session cookie; the SPA token-handling bullets below apply only to external API clients.

- The SPA uses OIDC authorization code + PKCE against the site IdP (A1). The API is a resource server validating JWT access tokens: issuer, audience, signature (keys from the IdP JWKS, fetched on-prem), and lifetime with ≤ 60 s clock skew.
- Claims map to a `Subject` (A2). Missing security attributes fail closed (no `us_person` claim means not a U.S. person).
- SAML-only IdPs are fronted by the IdP broker (e.g. Keycloak brokering SAML), so the app speaks one protocol.
- CAC/PIV mTLS terminates at the IdP. The app never handles certificates or passwords.
- **E-signature:** the client performs a fresh authentication (`prompt=login`, `max_age=0`) and presents the new token with the signature request. The API requires `auth_time` within 300 s and the same `sub` as the session. The signature record stores signer, meaning (reason code), timestamp, signed object iteration IDs, and a SHA-256 over the canonical signed content.
- Session hardening: short access tokens (5 min), refresh in memory only (no localStorage), strict CSP, and a same-origin API, so no CORS in production.
- Dev/demo: a bundled Keycloak realm with **synthetic** users only.

## Alternatives
Cookie sessions with a BFF: viable, and may be adopted if the IdP can't issue tokens to SPAs; the API contract doesn't change.
