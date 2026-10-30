# ADR-0058: Production sign-in: CAC/PIV through Keycloak, customer IdP brokering, and a read-only status page

**Status:** Accepted · 2026-10-30 · Assumptions A88 to A90 · Extends ADR-0007 (OIDC and e-signature step-up) and ADR-0014 (Blazor OIDC session). No schema change.

## Context
ADR-0007 decided that Keel speaks one protocol (OIDC) and that CAC/PIV mutual TLS ends at the identity provider. It shipped a development Keycloak realm with
synthetic users and passwords. A production site needs three things the repository did not have: a tested way to sign people in with CAC/PIV (X.509 client
certificates), a guide to connect a customer's own IdP, and a way for an administrator to see how a running host is set up and to be warned when it is weaker than
it should be. Whatever is added must not weaken markings, audit, FIPS, the CSP or the air-gap.

## Decision
1. **Keel's code does not learn about certificates.** It stays an OIDC relying party. CAC/PIV is an opt-in **overlay** to the Keycloak realm
   (`deploy/compose/keycloak/overlays/cac-piv/`): a script that uses Keycloak's own `kcadm.sh` to create a browser flow with the X.509 authenticator, and a second script
   that maps one credential (the EDIPI, the 10 digits ending the CN of a CAC) to one Keycloak user. The certificate selects an existing Keycloak user; roles and the ABAC
   attributes (`us_person`, `citizenship`, `org`, `programs`, ...) still come from that user, exactly as for a password sign-in. Nothing in a certificate is trusted as an
   attribute.
2. **Revocation and trust are the site's PKI, locally.** The overlay turns CRL checking on and fails closed (a stale CRL refuses everybody, which is visible and safe) with
   the CRL read from a local file; a site-local CRL distribution point and a local OCSP responder are switches, not defaults. Trust is the IdP truststore, holding only the
   CAs the site names.
3. **Fallback policy is explicit.** Default `CAC_FALLBACK=none`: a certificate is the only way in. `password` adds the stock username/password form beside it at
   authentication level 1 and is a weaker, flagged policy.
4. **E-signature step-up can require the certificate.** The overlay maps the sign-in to two Keycloak levels of authentication: level 1 (`acr` "1": certificate, and the
   password form when the fallback is on) and level 2 (`acr` "2": the certificate only, always re-presented). A new setting `Keel:Auth:StepUpAcr` (for example `2`) makes the
   step-up send `acr_values`, requires the returned ID token to carry that `acr` (otherwise the step-up is refused and audited), and makes every signing decision (review
   signatures, requirement verification) demand a sign-in with that `acr` as well as a fresh `auth_time`. Without it, A9 is unchanged. The requirement is enforced where the
   signature is accepted, not only where the browser is redirected, so a password sign-in made a minute ago cannot sign.
5. **Customer IdPs go through Keycloak brokering.** Templates for a generic OIDC and a generic SAML broker are realm overlays merged into the realm by a script
   (`scripts/idp/merge-realm-overlay.sh`) with every site value named and no default for a secret. Group claims map to Keel roles and attribute claims map to the ABAC
   attributes, with the IdP authoritative at every login. The template grants nothing to an unmapped person, never infers U.S.-person status, never trusts the e-mail
   address, and never auto-links to an existing local account. Keel keeps no user-provisioning rules of its own: a person appears in Keel's directory when they first
   sign in with a token that carries a plm role, and has no standing otherwise.
6. **An admin page shows the effective configuration, read-only.** Administration > Sign-in lists the settings that decide how people sign in, the level required for
   signatures, and warnings (password sign-in declared in Production, a password fallback, a password that could sign, demo sign-in, http IdP, FIPS not required, a
   token missing security claims, ...). A button reads the IdP's public discovery document at the configured authority only. The page cannot change anything and shows no
   secret. What the operator **declares** (`Keel:Auth:SignIn:Method`, `PasswordFallback`) is a statement used for the warnings; Keel cannot see inside the IdP's flows and
   does not pretend to.
7. **Test PKI is generated, labelled and never shipped as credentials.** `scripts/idp/generate-test-pki.sh` makes a throwaway CA and client certificates (valid, second
   user, unknown, expired, revoked, untrusted) whose subjects say "KEEL DEV TEST PKI NOT FOR PRODUCTION". The integration tests run a real Keycloak container with it.

## Consequences
- A site that does nothing keeps today's behavior; the status page then says "Password" and warns in Production, which is the truth.
- The CAC/PIV configuration is repeatable (the script replaces its own flow) and tested for mapping and refusal. It is **not** tested with real cards, DoD PKI chains, a
  real CRL/OCSP service, reverse-proxy certificate forwarding or FIPS-mode Keycloak; the ops guide lists each gap.
- `Subject` gains an optional `Acr`; `StepUp.IsFresh` gains an overload; `WorkflowOptions` and `RequirementOptions` gain `StepUpRequiredAcr`; the 401 step-up challenge adds
  `acr_values` (RFC 9470) when a level is required. All additive.
- Requiring an `acr` makes the IdP part of the signing trust: an IdP that releases `acr` 2 for a password sign-in defeats it. The shipped Keycloak flow does not; a customer IdP
  must be checked (docs/ops/sso-integration.md, checklist).

## Alternatives considered
- **Terminating TLS client authentication in Keel's host.** Rejected: it would put certificate parsing, trust stores and revocation in the application, outside the IdP that
  already owns identity, and would have to be rebuilt for every proxy topology. ADR-0007 already chose the IdP.
- **A full exported realm with the X.509 flow.** Rejected: an import that supplies authentication flows must supply all of them (direct grant, reset credentials, first broker
  login, ...), so a hand-trimmed export silently breaks brokering. Applying a flow with `kcadm` leaves the stock flows untouched.
- **Mapping by certificate serial or thumbprint.** Rejected as the default: it needs a change on every card renewal. The EDIPI is stable across renewals; a site that prefers
  another source changes `CAC_REGEX` or the mapper in `x509-config.json`.
- **Putting the password-fallback decision in Keel.** Rejected: Keel cannot see whether the IdP offers a password; it can require the certificate level for signatures, which it does.
