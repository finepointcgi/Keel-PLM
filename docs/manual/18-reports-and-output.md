# 18. Reports, release packages, Excel BOM and mass change

The **Output** group holds five tools: reports, release packages, verifying a package, BOM in Excel and mass change.

## Report builder

**Output > Reports** builds a report over one of eight datasets: parts, bill of materials, changes, baselines, quality records, requirement sets, compliance declarations and retention schedules.

1. Choose the **dataset**.
2. Tick the **columns** you want (at least one).
3. Add a **filter** (column, value), a **sort**, and optionally **group by** a column; groups show counts.
4. Select **Run**. The banner is the aggregate of the rows shown: a filter that matches nothing leaves no marked row and the banner drops to unclassified.
5. Export as **CSV**, **.xlsx** or **PDF**. Definitions can be saved and shared.

![A report run grouped by state](images/output-reports.png)

## Release packages (PDF)

For a released part or assembly, an ECN or an established baseline, **Output > Release packages** produces a PDF with: a cover sheet, the BOM report, the document list with file SHA-256, a signature page (signer, meaning, decision, UTC time, comment) and the change summary.

- The aggregate banner and every distribution notice are on the top and bottom of every page.
- If any included object is unreadable to you, the package is **refused**, never redacted.
- The result is stored in the vault, listed with its hash, and downloadable only while you may read every included object. Creating and downloading is audited.

- When an administrator has set up a signing identity ([chapter 23](23-administration.md)), the package is also **digitally signed**, and the **Signature** column says who signed and when ("Signed 2026-10-30 09:12 UTC by ..."). The signature changes nothing on the pages: the same banners and distribution notices are signed with them. If signing is required and cannot be done (no active identity, or its key is unavailable), the package is refused with a message instead of being made unsigned.

![Release packages, with the signature of each package](images/output-packages.png)

## Verify a package

A recipient, an auditor or you can prove that a downloaded package is the one Keel made and that no byte has changed.

1. Open **Output > Verify a package**.
2. Under **Choose the file**, select the package PDF (at most 32 MB). Nothing is stored; the check is recorded in the audit log.
3. Read the verdict:
   - **Valid**: the signature is intact over the whole file and the signer's certificate chains to a root this server trusts.
   - **Intact, but not trusted**: nothing was changed, but the certificate does not chain to a configured root, or was not valid when it signed.
   - **Changed**: the file was altered after signing (even one byte, or bytes added), or the signature does not match it. Do not rely on it.
   - **Not signed**: the file carries no signature. Treat it as unverified.
   - **Unreadable**: the file is not a PDF Keel can check.
4. The table shows who signed, who issued the certificate, the signing time, the reason and location, each check (bytes unchanged, signature names this certificate, certificate valid at signing time, chain reaches a trusted root) and the certificate chain.
5. If the file is a package that Keel made and you may read every object in it, the result also names it (number, made when and by whom, markings). Otherwise it does not: verification never shows you what you could not already read.

![A valid package](images/output-verify-valid.png)
![A package with one changed byte](images/output-verify-changed.png)

Limits to know: the signing time is the Keel server's clock and revocation is not checked, because the server has no network access to a time-stamping or revocation service. Without Keel, `scripts/verify-package-signature.sh package.pdf root.pem` checks the same signature with OpenSSL, and PDF readers such as Adobe Acrobat show it once your organization's root certificate is trusted there ([docs/ops/package-signing.md](../ops/package-signing.md)).

## BOM in Excel

**Output > BOM in Excel** exchanges the bill of materials with spreadsheets.

1. **Export**: choose the part, the EBOM (latest, released, or as designed for one unit) or MBOM, and optionally a variant. The workbook has banner rows on top, find numbers, effectivity, option conditions and a markings column. Cells starting with a formula character are neutralized.
2. **Template**: download an empty import template.
3. **Import**: upload the filled workbook. Keel shows a reviewable proposal with a row-level dry run. **Apply** goes through the normal BOM working copy and check-in; you can apply repeatedly.
4. A bad workbook is reported in words (size and ZIP/XML-bomb limits apply).

![BOM exchange](images/output-bom-excel.png)

## Mass change

**Output > Mass change** applies one operation to many parts: set an attribute, change markings (within your authority), add to a baseline proposal, submit for review, watch, or favorite.

1. Paste part numbers, one per line (at most 500).
2. Choose the **operation**.
3. Select **Preview**: each object shows *Will change*, or the reason it will not ("Not found, or you may not read it.").
4. Select **Apply to n part(s)**. Each object goes through the normal services under one correlation id; a partial failure is reported.
5. **Undo** reverses reversible operations; the status then reads Undone.

![A mass change preview: two parts will change, one is refused](images/output-mass-change.png)

## Excel/PDF are generated on the server

Nothing leaves the host: exports are built from data you may read, at the time you ask.
