# CAC/PIV sign-in through Keycloak (operator guide)

ADR-0058, assumptions A88 and A89. Read [ADR-0007](../adr/0007-authentication-and-esignature.md) first: Keel never sees a certificate. People sign in at the identity
provider (Keycloak) with a smart card; Keel receives an ordinary OIDC token for the Keycloak user the card belongs to.

**What is proven and what is not.** The configuration in this guide is applied by a shipped script to a real Keycloak 26.4 container and exercised with a
generated **test** PKI (`scripts/idp/generate-test-pki.sh`, subjects marked "KEEL DEV TEST PKI NOT FOR PRODUCTION"). The tests prove: the certificate maps to the right
user; a certificate with an unmapped EDIPI, an expired one, one in the CRL, one from a CA the truststore does not hold, and no certificate at all are each refused;
the optional password fallback never satisfies the certificate level; a step-up returns a fresh authentication at that level. They do **not** prove anything about real
cards, card middleware, browsers' certificate prompts, the DoD PKI (Root CA chains, DISA CRL size and schedule), OCSP, an HTTP CRL distribution point, reverse proxies that
forward the client certificate, or Keycloak running in FIPS 140 mode. Section 9 lists every gap. Do not take this guide as accreditation evidence.

## 1. What you need
- Keycloak 26.4 with the `keel` realm (the same realm the product's dev stack imports, or your own realm that carries the Keel roles and attribute mappers).
- The CA certificates of your PKI (for DoD: the DoD root and intermediate CA certificates you are authorized to trust, obtained through your PKI office).
- The CRLs of those CAs, on the Keycloak host, refreshed by your own process (section 5).
- One card per person and the **EDIPI** of each person (the 10 digits that end the subject CN of a CAC: `DOE.JOHN.Q.1234567890`).

## 2. TLS client authentication on Keycloak
Keycloak must ask the browser for a certificate and trust only your CAs. Direct (Keycloak terminates TLS):

```
kc.sh start --https-port=8443 \
  --https-certificate-file=/etc/keycloak/tls/server.crt --https-certificate-key-file=/etc/keycloak/tls/server.key \
  --https-client-auth=request \
  --truststore-paths=/etc/keycloak/truststore/dod-ca-1.pem,/etc/keycloak/truststore/dod-ca-2.pem
```
- `request` (not `required`) lets people without a card reach the realm's other pages; the *flow* decides whether a card is mandatory (section 4).
- The truststore holds the CAs of cards you accept. Anything else is refused at the TLS handshake. Do **not** import the test CA of this repository in production.
- Verified: this exact set of options (with `start-dev`) in the tests. A production `start` with `--optimized`, your hostname and proxy settings is yours to verify.

**Behind a reverse proxy.** If a proxy terminates TLS it must request the client certificate and pass it to Keycloak in a header, and Keycloak must be told how to read it
(`--spi-x509cert-lookup--provider=nginx|haproxy|apache`, plus `--spi-x509cert-lookup--<provider>--ssl-client-cert=...`). Keycloak's guide "Configuring a reverse proxy"
(X.509 section) is the authority. **Not verified here.** The proxy must also *remove* any client-supplied copy of that header, otherwise anyone can forge a certificate
header: that is the most common way this setup is broken. Keel's own proxy trust (`Keel:Proxy:*`) is separate and unaffected.

## 3. Map people to cards
The shipped regular expression takes the trailing 10 digits of the CN as the identifier and compares it with the Keycloak user attribute `edipi`.
```
docker exec -e KC_CLI_PASSWORD=... keycloak bash /overlay/map-credential.sh jdoe 1234567890     # set
docker exec -e KC_CLI_PASSWORD=... keycloak bash /overlay/map-credential.sh jdoe -               # clear
```
(Inside the container the path is wherever you mounted `deploy/compose/keycloak/overlays/cac-piv`; the test harness mounts it at `/overlay`.) Check that the number is the EDIPI
of the right person: Keycloak cannot. For many users script the call or set the attribute from your directory (LDAP/AD federation can supply `edipi` from `employeeID`).
- Another identifier: change `CAC_REGEX` and `CAC_ATTRIBUTE` (for example the UPN in the SAN, `x509-cert-auth.mapping-source-selection` = `Subject's Alternative Name otherName (UPN)`,
  by editing `x509-config.json`). Keycloak's options are the list in the Admin console under Authentication > Flows > X509 > Settings.
- Keycloak must not let people edit `edipi` themselves. In the shipped realm the user profile keeps `unmanagedAttributePolicy` at `ADMIN_VIEW`; do not set it to `ENABLED`
  (per Keycloak's documentation that lets users edit unmanaged attributes in the account console; **not tested here**).
- One EDIPI, one user. Keycloak refuses a certificate that maps to two users.

## 4. Switch the realm to the certificate flow
```
docker exec -e KC_CLI_PASSWORD=... -e CAC_FALLBACK=none keycloak bash /overlay/configure-cac-piv.sh
```
The script (idempotent) creates the browser flow `keel-cac-browser`, binds it to the realm and does not touch the stock flows or the dev users:

| Step | Requirement | What it does |
|---|---|---|
| Cookie | Alternative | An existing SSO session continues (skipped when a higher level is requested) |
| `keel-cac-level-1` | Conditional (level 1, `acr` "1") | The X.509 authenticator; with `CAC_FALLBACK=password` also the stock username/password form |
| `keel-cac-level-2` | Conditional (level 2, `acr` "2", max age 0) | The X.509 authenticator only, re-presented every time |

Knobs (environment of the script; defaults in brackets):

| Variable | Meaning |
|---|---|
| `CAC_FALLBACK` | `none` [default]: a certificate is the only way in. `password`: the password form is offered beside it at level 1 (weaker; Keel's Sign-in page warns) |
| `CAC_REGEX`, `CAC_ATTRIBUTE` | How the identifier is read from the subject DN and which user attribute holds it [10-digit EDIPI, `edipi`] |
| `CAC_CRL_FILE` | CRL file name under Keycloak's `conf` directory [`crl.pem`] |
| `CAC_CRLDP` | `true` also fetches the CRL distribution point named in the certificate; only for a site-local URL Keycloak can reach [false] |
| `CAC_OCSP_URI` | A site-local OCSP responder; turns OCSP checking on, fail closed |
| `CAC_CERT_POLICY` | Certificate policy OIDs a certificate must carry (your PKI's medium-hardware / PIV-authentication OIDs); empty = not checked |
| `CAC_SSO_MAX_AGE` | Seconds a level 1 sign-in is good for [36000] |

Fixed in `x509-config.json` (edit the file to change): timestamp validation on; key usage `digitalSignature`; extended key usage `clientAuth`; the confirmation page
("continue as ...") skipped; CRL abort if not updated (fail closed).

**What a refusal looks like.** With the fallback off, an unmapped, expired, revoked, untrusted or missing certificate ends on a Keycloak error page (or at the TLS handshake for
an untrusted or expired one). The reason is in Keycloak's log, which is where your SIEM should read it: `user_not_found` (the EDIPI is in the event), "Certificate has been
revoked", and for the others a `LOGIN_ERROR` event. With `CAC_FALLBACK=password` an unmapped certificate lands on the password form instead: that is the weakness of that
policy, not a bug.

## 5. Revocation for an air-gapped site
- **CRL from a local file (default, verified).** Put the CA's CRL (PEM) at `<keycloak>/conf/crl.pem` (name set by `CAC_CRL_FILE`). Keycloak reads it when a certificate is
  checked; the file's `nextUpdate` passes, and with `x509-cert-auth-crl-abort-if-non-updated` (on) **every** certificate is refused until you replace it. Plan a job that
  fetches the CRL where you have network access (for DoD, from the DISA publication point), carries it across your boundary by your normal transfer process, converts it
  to PEM (`openssl crl -inform DER -in x.crl -out crl.pem`) and drops it in place. One file serves one issuing CA: for several issuing CAs use the CRL distribution point.
  DoD CRLs are large; size and refresh behavior with real DoD CRLs are **not verified**.
- **CRL distribution point (switch, not verified).** `CAC_CRLDP=true` makes Keycloak fetch the URL in each certificate's CRL distribution point. In an air-gapped site that only
  works if your PKI publishes a site-local point with the same URL (split DNS) that Keycloak can reach.
- **OCSP (switch, not verified).** `CAC_OCSP_URI=https://ocsp.example.mil/` points Keycloak at a local responder; OCSP failure refuses the sign-in (fail closed).
  If you need OCSP signed by a responder certificate that is not the issuer, set it in the authenticator configuration. Neither the switch nor a responder was run in the tests.
- **Fail-closed is deliberate.** A stale CRL turns into a site-wide outage, visible within minutes, rather than into revoked cards that still work. Decide who owns the CRL job
  before go-live, and keep a break-glass path (section 6).

## 6. Fallback policy
Choose one and write it into your system security plan.
1. **Certificate only (recommended).** `CAC_FALLBACK=none`. Lost card or CRL outage: nobody signs in until fixed. Keep one or two break-glass administrators in the Keycloak
   *master* realm (they manage Keycloak, not Keel data). Test the recovery path.
2. **Password fallback.** `CAC_FALLBACK=password`. A person whose certificate fails gets a password form. Tested: the password logs in at level 1 only, and **cannot** satisfy level 2
   (a correct password with `acr_values=2` is refused). It is weaker for everything except e-signatures and applies to everyone, not just administrators. Set the Keycloak realm
   password policy and brute-force protection; declare the fallback to Keel (`Keel:Auth:SignIn:PasswordFallback=true`) so the Sign-in page keeps warning.

## 7. E-signatures with the certificate (step-up)
Set on the Keel host:
```
Keel__Auth__SignIn__Method=Certificate
Keel__Auth__StepUpAcr=2
```
Keel then (a) sends `prompt=login`, `max_age=0` and `acr_values=2` at the step-up, (b) refuses the step-up if the ID token's `acr` is not `2` (audited as `session.step_up` denied,
reason `step_up.acr_not_satisfied`), and (c) at every signature (review signatures, formal requirement verification) requires the signer's token to carry `acr` 2 within the
fresh-sign-in window (default 300 s). A password sign-in a moment ago does not count. The REST API's 401 challenge carries `acr_values="2"` (RFC 9470).
- Verified against Keycloak with the test PKI: a level 1 sign-in is `acr` 1; a step-up with `acr_values=2` and `prompt=login` returns `acr` 2, the same subject and a **newer**
  `auth_time`; a password cannot reach 2. Keel's own acceptance rules (fresh and level, same subject, refusal audited) are unit-tested.
- **Not verified:** a full browser run of Keel against this Keycloak (the unit tests cover Keel's logic, the integration tests cover Keycloak's answers; they were not joined in one
  browser test), and what the card does at the step-up. The step-up repeats TLS client authentication. Whether the person is asked for the **PIN again** depends on the card
  middleware and the browser's TLS session handling, not on Keycloak or Keel. If your policy needs a PIN entry per signature, test it with your middleware and
  disable TLS session resumption on the Keycloak/proxy side if the PIN is not being asked.

## 8. Dev and demo
The dev stack (`deploy/compose/compose.yaml`) stays password-based and is unchanged. To try the flow locally, start a Keycloak as in section 2 on ports of your choice (never
the ports of another stack), import the dev realm, and run the two scripts. The integration tests do exactly this in a throwaway container.
`scripts/idp/generate-test-pki.sh <dir>` makes the test PKI and, for the tests only, leaves the CA key beside it. Delete it when you are done.

## 9. What was not verified (explicit)
Real CAC/PIV cards and readers; card middleware (OpenSC, ActivClient, Windows minidriver) and browser certificate-selection UX; the DoD PKI chain, DISA CRL size and
refresh; OCSP and an HTTP CRL distribution point; reverse-proxy certificate forwarding (nginx, haproxy, apache) and header stripping; a production `kc.sh start` (the tests use
`start-dev`); Keycloak in FIPS 140 mode (needs the Keycloak FIPS distribution and PKCS#12/BCFIPS truststores, which this overlay does not configure); PIN re-prompt at step-up;
EDIPI collisions across multiple PKI issuers; Keycloak clustering with the CRL file; a joined browser run of Keel against this Keycloak.

## 10. Troubleshooting
| Symptom | Likely cause |
|---|---|
| Browser never asks for a certificate | `--https-client-auth` not `request`; proxy not requesting it; the card's issuing CA is not in the truststore (browsers only offer certificates that chain to the CAs the server lists) |
| Error page after choosing the card, log `user_not_found` | No user has that EDIPI in `edipi` (`map-credential.sh`), or the regular expression does not match your CN format |
| Everyone refused, log mentions the CRL | The CRL file is missing, unreadable or past `nextUpdate` (section 5) |
| One person refused, "Certificate has been revoked" | They are in the CRL (lost or replaced card) |
| Step-up fails with a denied `session.step_up` audit event | The IdP returned an `acr` other than the required one: the Keycloak flow is not the level 2 flow, or `Keel:Auth:StepUpAcr` is wrong |
