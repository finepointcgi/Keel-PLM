# Signing release packages: operator guide

Keel can digitally sign every release package PDF with a certificate from your organization's PKI (ADR-0051). A customer or auditor can then prove who produced a package and that no byte has changed since, without access to Keel.
This page is for the administrator who sets it up and for anyone who verifies a package outside Keel.

## What you need
1. **A signing certificate** from your PKI, with its issuing chain up to and including the **root certificate**, as one PEM bundle: signing certificate first, root last.
   - Key: RSA 2048 bits or more, or ECDSA on NIST P-256, P-384 or P-521. Certificates signed with SHA-256 or stronger. Nothing else is accepted.
   - Key usage `digitalSignature` (or `nonRepudiation`). Document-signing or no extended key usage; Keel does not require a particular extended key usage.
   - The bundle is stored in Keel and embedded in every signature, so a verifier needs no network. Keep it to three certificates or so (the signature space is 12 KB).
2. **The private key, where Keel's service account can use it.** Pick one source:

| Key source | Key reference | Use when |
|---|---|---|
| PEM file | Absolute path of the PKCS#8 private key (`BEGIN PRIVATE KEY`, or `BEGIN ENCRYPTED PRIVATE KEY` with a password reference) | Key kept as a file on the host or a mounted secret (mode 0600, owned by the Keel service account) |
| PKCS#12 file | Absolute path of the `.pfx`; password reference required if it has one | Your PKI hands out `.p12` files |
| Certificate store | `CurrentUser/My` or `LocalMachine/My` | Windows or macOS key store, or an HSM whose driver registers keys there (CNG/KSP); Keel finds the certificate by its SHA-256 fingerprint |
| OpenSSL provider key | `provider\|key-uri`, for example `pkcs11\|pkcs11:token=Keel;object=package-signing` | PKCS#11 HSM through an OpenSSL 3 provider (Linux). The key never leaves the HSM |

A **password reference** is `env:NAME` (an environment variable of the service) or `file:/absolute/path` (for example a mounted Kubernetes or Compose secret). The password is never typed into Keel or stored in its database.
To confine key files to one directory set `Keel__Reports__Signing__AllowedKeyDirectory=/etc/keel/signing`.

## Set it up (Administration > Package signing)
1. Under **Add an identity**: name it, choose where the private key is kept, paste the certificate bundle, give the key reference (and password reference), say what a signature means (for example "Approved for release") and the signing location (your site).
   Keel loads the key, proves that it belongs to the certificate, and refuses a weak algorithm or a bundle that does not end in a root. Nothing is stored on failure.
2. Select **Activate** on the identity. New packages are signed from then on; the previous identity (if any) goes on standby.
3. Optional: **Require signatures**. While required and no identity is active, or when the active key cannot be used, packages are refused with a message instead of being made unsigned.
4. **Rotation**: add the new certificate, activate it, retire the old one when its time has come. Retired identities never sign again but stay as trust anchors, so packages they signed still verify. Identities are never deleted.

Every step is in the audit log (`output.signing.*`, `output.package.sign`, `output.package.verify`) with fingerprints and hashes, never keys, passwords or paths.

### Development only
On Development and Demo hosts the page also offers **Generate a development certificate**: a throw-away root and signing certificate (valid 90 days, subjects "Keel DEV ONLY ... NOT FOR PRODUCTION") with a key file under `Keel:Reports:Signing:DevelopmentKeyDirectory` (default: a `keel-dev-signing` folder in the temporary directory). It proves nothing about who made a package. Production hosts do not offer it.
To try the whole path with OpenSSL instead (a stand-in for your PKI):

```
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out ca.key.pem
openssl req -x509 -new -key ca.key.pem -sha256 -days 90 -subj "/CN=Test root" -addext basicConstraints=critical,CA:TRUE -addext keyUsage=critical,keyCertSign -out ca.pem
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out signer.key.pem
openssl req -new -key signer.key.pem -subj "/CN=Test package signer" -out signer.csr
printf 'basicConstraints=CA:FALSE\nkeyUsage=critical,digitalSignature\n' > ext.cnf
openssl x509 -req -in signer.csr -CA ca.pem -CAkey ca.key.pem -CAcreateserial -sha256 -days 30 -extfile ext.cnf -out signer.pem
cat signer.pem ca.pem > bundle.pem      # paste bundle.pem into Keel; key reference: the path of signer.key.pem
```

## Verify a package
**In Keel**: Output > Verify a package, choose the PDF. The result says valid, intact but not trusted, changed, not signed or unreadable, and lists signer, issuer, signing time, reason, location and every check. For a package Keel made, it also names the package, if you may read everything in it.

**With standard tools (no Keel)**: `scripts/verify-package-signature.sh RPK-000123.pdf root.pem`. It needs bash, `xxd` and OpenSSL 3 or later (not LibreSSL, whose `cms` command differs). `root.pem` is the last certificate of the registered bundle. It checks that the signature is valid over every byte but the signature, that the file is not extended, and that the chain ends in your root; exit code 0 means verified. The signing time is judged from the signature itself, not today.
By hand, the signed bytes are the file with the hexadecimal string between the second and third `/ByteRange` numbers removed, and the signature is that string decoded (`xxd -r -p`), a DER CMS: `openssl cms -verify -inform DER -in sig.der -content signed.bin -binary -CAfile root.pem -purpose any -no_check_time`.

**PDF readers**: Adobe Acrobat, `pdfsig` (poppler) and similar show the signature, its signer and "document has not been modified since this signature was applied". They show it as trusted only when your root is in their trust list (Acrobat: Preferences > Signatures > Identities and Trusted Certificates; add the root). Without it they say the signer's identity is unknown, which is expected for a private PKI. The signature field is invisible and the pages are the ones that Keel rendered, with the same banners and distribution notices.

## What a signature does not prove
- **No trusted time**: the signing time is the Keel server's clock; an air-gapped host has no time-stamping authority.
- **No revocation check**: Keel and the script do not look up revocation. If the signing key is compromised, retire the identity, tell recipients, and treat packages after the compromise as unreliable.
- **Only roots configured in Keel are trusted by Keel's verifier.** A package signed on another site shows "intact, not trusted" until that site's root is registered (add it as an identity bundle you never activate is not possible, since an identity needs a key: use the script or a PDF reader for foreign packages).

## Settings
| Key | Meaning |
|---|---|
| `Keel__Reports__Signing__AllowedKeyDirectory` | If set, PEM, PKCS#12 and password files must be below this directory. |
| `Keel__Reports__Signing__AllowDevelopmentCertificate` | `false` switches the generator off on a development host. It can never switch it on elsewhere. |
| `Keel__Reports__Signing__DevelopmentKeyDirectory` | Where the generator writes keys (development hosts only). |
