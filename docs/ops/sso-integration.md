# Connecting a customer's identity provider (SSO integration guide)

ADR-0058, assumption A90. For the customer's own IdP (Entra ID, AD FS, Okta, Ping, another Keycloak) in front of Keel. For smart-card sign-in see
[cac-piv-sign-in.md](cac-piv-sign-in.md); the two combine (the customer IdP may itself use CAC/PIV).

**What is proven and what is not.** The OIDC template (`deploy/compose/keycloak/overlays/idp-broker/oidc-broker.overlay.json`) was run end to end against a real second Keycloak
standing in for the customer's IdP: just-in-time creation, group-to-role mapping, attribute mapping, no grant for an unmapped person, and no account takeover through a matching
e-mail address. The SAML template (`saml-broker.overlay.json`) was only **imported** into a real Keycloak (Keycloak accepts it and publishes service-provider metadata); no SAML
sign-in was run. The per-product notes in section 5 are configuration pointers written from general knowledge of those products; **none was tested against Entra ID, AD FS, Okta
or Ping.** Treat section 7 as the acceptance test you run against your own IdP.

## 1. Shape
```
customer IdP  --OIDC or SAML-->  Keycloak (realm keel, identity provider "customer-oidc" / "customer-saml")  --OIDC-->  Keel
```
Keel speaks only OIDC to Keycloak ([ADR-0007](../adr/0007-authentication-and-esignature.md)). Brokering through Keycloak keeps every claim rule in one place, turns SAML into OIDC for Keel,
and lets the customer's groups be mapped to Keel's roles without Keel knowing the customer's naming. Keel's `Authority` points at the Keycloak realm, never at the customer IdP.

## 2. What Keel reads from the token (A2)
Everything Keel decides comes from these claims in the Keycloak token. **An absent claim is the most restrictive value.**

| Claim | Type | Used for | If absent |
|---|---|---|---|
| `sub` | string | Stable user key (audit, ownership). Must never change or be reused | No sign-in |
| `preferred_username`, `name` | string | Display and audit snapshots | Falls back to `sub` |
| `roles` | string array | The Keel roles (below) | No access: Keel denies a user with no `plm-*` role |
| `us_person` | boolean | ITAR/EAR access (A3). **Explicit only**, never inferred from citizenship (lawful permanent residents are U.S. persons) | Not a U.S. person |
| `citizenship` | string (ISO 3166 alpha-3) | Export decisions, display | Unknown |
| `org` | string | The owning organization of marked data; the partner organization for external users | None |
| `programs` | string array | Program-scoped markings | No program access |
| `export_authorizations` | string array | License / agreement IDs that open ITAR data to a non-U.S. person (A3) | None |
| `cui_categories` | string array | CUI category access | None |
| `auth_time`, `acr` | number, string | E-signature step-up ([cac-piv-sign-in.md](cac-piv-sign-in.md) section 7) | Signing always asks for a fresh sign-in |

Roles: `plm-viewer`, `plm-author`, `plm-approver`, `plm-admin`, `plm-auditor`, plus `plm-records`, `plm-integration` (service accounts) and `plm-supplier` (external). Roles open features;
the markings and attributes above decide which data. A role never overrides a marking. For an external **partner** (supplier) give the person `plm-supplier` and the partner's `org`; which
supplier they belong to is linked by a Keel administrator on the supplier's Portal access tab ([chapter 12](../manual/12-sourcing-and-portal.md)), never from a claim.

## 3. The template overlay
`scripts/idp/merge-realm-overlay.sh` merges an overlay into a realm file for `--import-realm` (new realm) and refuses to write anything while a `__PLACEHOLDER__` has no value or the alias
already exists:
```
scripts/idp/merge-realm-overlay.sh deploy/compose/keycloak/keel-realm.json \
  deploy/compose/keycloak/overlays/idp-broker/oidc-broker.overlay.json \
  DISPLAY_NAME="Contoso" ISSUER=https://login.contoso.example/ AUTHORIZATION_URL=... TOKEN_URL=... JWKS_URL=... \
  CLIENT_ID=keel-broker CLIENT_SECRET=@/run/secrets/idp-client-secret \
  GROUP_VIEWER="Keel Viewers" GROUP_AUTHOR="Keel Authors" GROUP_APPROVER="Keel Approvers" GROUP_ADMIN="Keel Admins" GROUP_AUDITOR="Keel Auditors" \
  > keel-realm-sso.json
```
`NAME=@file` reads a secret from a file so it is not on the process list. Needs `jq` (on an air-gapped host, carry it in with your tooling). For a realm that already runs, apply the same
identity provider and mappers through the Keycloak admin console or `kcadm.sh create identity-provider/instances -r keel -f ...` (the merged file contains the objects).

What the template sets, and why:
- **Signature validation on** (`validateSignature`, JWKS URL; SAML: `validateSignature`, `wantAssertionsSigned`, the IdP signing certificate), **PKCE S256** towards the IdP, `trustEmail: false`.
- **Group to role mappers**, one per Keel role, `syncMode FORCE`: the customer's group decides the role at **every** sign-in, so removing someone from the group removes the role at their next sign-in.
- **Attribute mappers** for `us_person`, `citizenship`, `org`, `programs`, `export_authorizations`, `cui_categories`, `syncMode FORCE`: the customer IdP is authoritative.
  Delete a mapper for an attribute your IdP cannot be trusted to assert, and manage that attribute in Keycloak instead. An attribute that arrives is not validated by Keel
  beyond its type; a customer who can assert `us_person=true` can open ITAR data to that person, so only accept it from an IdP that is authoritative for it.
- **No default role.** A person the mappers do not match has no `plm-*` role: Keycloak creates the user and Keel denies it.
- Group claim name: the template reads the claim `groups` (a list of names). If your IdP sends GUIDs (Entra ID) or another claim name, edit `claim`/`claim.value` in the overlay.

## 4. Just-in-time provisioning: what it does and its limits
- **Creation.** The first sign-in creates the Keycloak user (username from `preferred_username`, e-mail, name) via Keycloak's *first broker login* flow. Keel then records the person in its
  directory the first time they sign in with a token (A50).
- **No auto-linking (tested).** If the upstream e-mail equals a local user's e-mail, Keycloak does **not** merge them: the sign-in stops at its "account already exists" step, which needs
  the local account's own credentials. Do not add `idp-auto-link` or `trustEmail`: with them, anyone who can create an upstream account with someone else's address becomes that person,
  administrators included. Keep local accounts and federated accounts on different e-mail domains, or give the local accounts no e-mail.
- **Username collisions.** Two upstream people with the same `preferred_username` are not merged but the second one fails; use a unique, stable claim for the username (UPN, employee ID).
- **No standing without a role.** Provisioning grants nothing by itself. Roles and attributes follow the claims at each sign-in.
- **No automatic de-provisioning.** Removing a person upstream stops new sign-ins. It does **not** delete the Keycloak user, end sessions already open (access tokens live five minutes; the
  SSO session until its idle/maximum time, 15 minutes idle in the shipped realm; Keel's own session cookie up to 8 hours and its idle lock) or remove their Keel directory entry (audit
  records are permanent by design). To cut access immediately, disable the Keycloak user and end their sessions in the Keycloak admin console. Review brokered users on a schedule.
- **Group size.** Some IdPs truncate large group lists (Entra ID returns a pointer instead of the groups above 200). Use app roles or filter the groups claim to the Keel groups.
- **Profile updates.** The template leaves the stock *first broker login* flow in place (it reviews a profile only if required data is missing).

## 5. Per-product pointers (not tested; check against your product's documentation)
- **Microsoft Entra ID (OIDC).** Register an app (web, redirect URI `https://<keycloak>/realms/keel/broker/customer-oidc/endpoint`), create a client secret, use the v2.0 issuer
  `https://login.microsoftonline.com/<tenant>/v2.0`. Emit groups as the `groups` claim (group object IDs by default: put those IDs in the `GROUP_*` values, or use App roles and the `roles`
  claim, then change the mapper claim). Put `us_person`/`citizenship`/`org` in optional claims or directory extension attributes and map them with a claims mapping policy.
- **AD FS (OIDC or SAML).** For OIDC create an application group (server application); add an issuance transform rule that sends AD groups as `groups`. For SAML give AD FS the Keycloak SP
  metadata and add claim rules for the attributes. AD FS signs with its token-signing certificate: put it in `SIGNING_CERTIFICATE` (SAML) or let the JWKS URL deliver it (OIDC).
- **Okta (OIDC or SAML).** Create an OIDC web app; add a `groups` claim with a filter that matches only the Keel groups on the authorization server you use; add custom claims for the
  attributes from the Okta profile. For SAML use attribute statements and group attribute statements.
- **Ping (PingFederate / PingOne).** Define the attribute contract (`groups`, `us_person`, ...) on the OIDC policy or the SAML connection and fulfil it from the data store.
- **Another Keycloak (federation).** This is the case the tests run: a client with a group-membership mapper (`groups`, full path off) and attribute mappers. See
  `tests/Keel.IntegrationTests/Idp/upstream-realm.json`.
- **Air gap.** The broker needs the IdP's endpoints reachable from Keycloak. If your IdP is a cloud service, SSO to it is not possible from an air-gapped enclave: use an on-premises IdP or a
  site Keycloak that federates inside the boundary.

## 6. Sign-in hardening that belongs with this
Declare how the IdP is set up so Administration > Sign-in tells the truth: `Keel__Auth__SignIn__Method=Federated`, and `Keel__Auth__SignIn__PasswordFallback=true` if the IdP also offers
passwords. If your customer IdP can prove a stronger method for e-signatures, express it as an `acr` the realm returns and set `Keel__Auth__StepUpAcr` to it; check that the IdP cannot return that
level for a password sign-in (section 7, item 9). Realm settings that matter: access token lifespan (5 minutes shipped), SSO idle (15 minutes shipped), brute-force protection (on in the shipped realm).

## 7. Verification checklist (run against your IdP before go-live)
1. Keycloak imported the realm: Admin console > Identity providers lists your alias; **Mappers** lists five role and six attribute mappers.
2. Administration > Sign-in in Keel shows the declared method, the authority and the warnings you expect; click **Check the identity provider** and read the result.
3. A test user in a mapped group signs in. Open Administration > Sign-in: **Your current sign-in** lists the security claims present. Confirm `us_person`, `citizenship`, `org` and `programs`
   show as present and, in the Keycloak admin console (Users > the user > Attributes), have the right values.
4. The same user sees what their roles allow and nothing else (an author cannot reach Administration; a viewer cannot edit).
5. A test user in **no** mapped group signs in at Keycloak and Keel denies them (access denied, the denial is in the audit log).
6. A non-U.S.-person test user (`us_person=false` or absent) cannot see an ITAR-marked object (Access check page, chapter 9).
7. A test user's group is removed upstream: after their next sign-in the role is gone (FORCE sync).
8. An upstream account with the e-mail of a local user does **not** sign in as that local user.
9. If `Keel:Auth:StepUpAcr` is set: a password-only upstream sign-in must not produce the required `acr` and an e-signature must be refused; a sign-in with the strong method must produce it and the
   signature must be accepted. (Keel refuses a step-up whose token lacks the `acr`; this item checks that your IdP does not release it too freely.)
10. Sign out in Keel ends the Keycloak session; an unplugged IdP gives a clear failure, not a Keel 500 for signed-in users already holding a session (new sign-ins fail until it returns).
11. The audit log shows `session.step_up` and the sign-in-related denials for the tests above, with your SIEM receiving them ([siem-onboarding.md](siem-onboarding.md)).
