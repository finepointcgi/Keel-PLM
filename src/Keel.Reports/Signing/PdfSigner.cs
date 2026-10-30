using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfSharp.Pdf;

namespace Keel.Reports.Signing;

/// <summary>What the signature dictionary says about the signing act. The signing time is the time of the Keel clock; the CMS carries the same time as a signed attribute.</summary>
public sealed record SignatureReservation(string Reason, string Location, string SignerName, DateTimeOffset SigningTime);

/// <summary>The outcome of signing: the signed bytes and what was signed.</summary>
public sealed record SignedPdf(byte[] Bytes, string SignedDigest, string Algorithm);

/// <summary>
/// PDF signature in the PAdES baseline style (ADR-0051): an invisible signature field whose value is a detached CMS (SubFilter ETSI.CAdES.detached) over every byte of the file except the
/// signature itself (/ByteRange), carrying the signing certificate chain, the signing time and the ESS signing-certificate-v2 attribute. Only platform cryptography is used
/// (RSA PKCS#1 or ECDSA with SHA-256 or stronger). The page content is not touched: the banners and notices that were rendered are exactly the bytes that are signed.
/// </summary>
public static class PdfSigner
{
    /// <summary>Space reserved for the hexadecimal CMS (bytes). A chain of three certificates with RSA-4096 needs about 6 KB.</summary>
    public const int ReservedBytes = 12288;

    private const string ByteRangePlaceholder = "0000000000";
    private const string EtsiSigningCertificateV2 = "1.2.840.113549.1.9.16.2.47";

    /// <summary>Adds the invisible signature field, with room for the signature, before the document is saved.</summary>
    internal static void Reserve(PdfDocument doc, SignatureReservation r)
    {
        var sig = new PdfDictionary(doc);
        doc.Internals.AddObject(sig);
        sig.Elements.SetName("/Type", "/Sig");
        sig.Elements.SetName("/Filter", "/Adobe.PPKLite");
        sig.Elements.SetName("/SubFilter", "/ETSI.CAdES.detached");
        sig.Elements["/ByteRange"] = new PdfLiteral($"[0 {ByteRangePlaceholder} {ByteRangePlaceholder} {ByteRangePlaceholder}]");
        sig.Elements["/Contents"] = new PdfLiteral("<" + new string('0', ReservedBytes * 2) + ">");
        sig.Elements.SetString("/Reason", r.Reason);
        sig.Elements.SetString("/Location", r.Location);
        sig.Elements.SetString("/Name", r.SignerName);
        sig.Elements.SetString("/M", "D:" + r.SigningTime.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z");

        var page = doc.Pages[0];
        var widget = new PdfDictionary(doc);
        doc.Internals.AddObject(widget);
        widget.Elements.SetName("/Type", "/Annot");
        widget.Elements.SetName("/Subtype", "/Widget");
        widget.Elements.SetName("/FT", "/Sig");
        widget.Elements.SetString("/T", "Signature1");
        widget.Elements.SetInteger("/F", 132); // print + locked
        widget.Elements["/Rect"] = new PdfLiteral("[0 0 0 0]");
        widget.Elements.SetReference("/V", sig);
        widget.Elements.SetReference("/P", page);
        var annots = new PdfArray(doc);
        annots.Elements.Add(widget.Reference!);
        page.Elements["/Annots"] = annots;

        var form = new PdfDictionary(doc);
        var fields = new PdfArray(doc);
        fields.Elements.Add(widget.Reference!);
        form.Elements["/Fields"] = fields;
        form.Elements.SetInteger("/SigFlags", 3); // signatures exist, appending only
        doc.Internals.Catalog.Elements["/AcroForm"] = form;
    }

    /// <summary>Signs a document that was saved with <see cref="Reserve"/>. Throws when the key is not acceptable or the signature does not fit.</summary>
    public static SignedPdf Sign(byte[] pdf, LoadedSigningKey key, DateTimeOffset signingTime)
    {
        var file = (byte[])pdf.Clone();
        var (rangeAt, rangeLen) = FindByteRange(file);
        var (open, close) = FindContents(file);
        var br = $"[0 {open.ToString("D10", CultureInfo.InvariantCulture)} {(close + 1).ToString("D10", CultureInfo.InvariantCulture)} {(file.Length - close - 1).ToString("D10", CultureInfo.InvariantCulture)}]";
        if (br.Length != rangeLen) throw new InvalidOperationException("The reserved ByteRange has an unexpected width.");
        Encoding.ASCII.GetBytes(br).CopyTo(file, rangeAt);

        var signed = new byte[file.Length - (close + 1 - open)];
        Buffer.BlockCopy(file, 0, signed, 0, open);
        Buffer.BlockCopy(file, close + 1, signed, open, file.Length - close - 1);

        var (digestName, digestOid) = key.Digest;
        var cms = new SignedCms(new ContentInfo(signed), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, key.Certificate, key.PrivateKey)
        {
            DigestAlgorithm = new Oid(digestOid),
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        foreach (var c in key.Chain) signer.Certificates.Add(c);
        signer.SignedAttributes.Add(new Pkcs9SigningTime(signingTime.UtcDateTime));
        signer.SignedAttributes.Add(SigningCertificateV2(key.Certificate, digestName));
        cms.ComputeSignature(signer, silent: true);
        var der = cms.Encode();
        if (der.Length > ReservedBytes) throw new InvalidOperationException($"The signature needs {der.Length} bytes; {ReservedBytes} are reserved. Use a shorter certificate chain.");
        var hex = Convert.ToHexString(der);
        Encoding.ASCII.GetBytes(hex).CopyTo(file, open + 1);

        return new SignedPdf(file, Convert.ToHexStringLower(SHA256.HashData(signed)), key.AlgorithmLabel);
    }

    /// <summary>ESS signing-certificate-v2 (RFC 5035): binds the signature to exactly one signing certificate by its hash, as PAdES requires.</summary>
    private static AsnEncodedData SigningCertificateV2(X509Certificate2 cert, string digestName)
    {
        var hash = digestName switch
        {
            "SHA256" => SHA256.HashData(cert.RawData),
            "SHA384" => SHA384.HashData(cert.RawData),
            _ => SHA512.HashData(cert.RawData),
        };
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence()) // SigningCertificateV2
        using (w.PushSequence()) // certs
        using (w.PushSequence()) // ESSCertIDv2
        {
            if (digestName != "SHA256")
                using (w.PushSequence()) { w.WriteObjectIdentifier(digestName == "SHA384" ? "2.16.840.1.101.3.4.2.2" : "2.16.840.1.101.3.4.2.3"); } // default is SHA-256; others are written
            w.WriteOctetString(hash);
        }
        return new AsnEncodedData(new Oid(EtsiSigningCertificateV2), w.Encode());
    }

    private static (int At, int Length) FindByteRange(byte[] file)
    {
        var placeholder = $"[0 {ByteRangePlaceholder} {ByteRangePlaceholder} {ByteRangePlaceholder}]";
        var at = Find(file, Encoding.ASCII.GetBytes("/ByteRange"), 0);
        if (at < 0) throw new InvalidOperationException("The signature placeholder (ByteRange) is missing from the document.");
        var p = at + "/ByteRange".Length;
        while (file[p] == (byte)' ') p++;
        if (!Encoding.ASCII.GetString(file, p, placeholder.Length).Equals(placeholder, StringComparison.Ordinal)) throw new InvalidOperationException("The reserved ByteRange has an unexpected form.");
        return (p, placeholder.Length);
    }

    private static (int Open, int Close) FindContents(byte[] file)
    {
        var at = Find(file, Encoding.ASCII.GetBytes("/Contents"), 0);
        while (at >= 0)
        {
            var p = at + "/Contents".Length;
            while (file[p] == (byte)' ') p++;
            if (file[p] == (byte)'<' && Encoding.ASCII.GetString(file, p + 1, 64) == new string('0', 64))
            {
                var close = p + 1 + ReservedBytes * 2;
                if (file[close] != (byte)'>') throw new InvalidOperationException("The reserved signature space has an unexpected size.");
                return (p, close);
            }
            at = Find(file, Encoding.ASCII.GetBytes("/Contents"), at + 1);
        }
        throw new InvalidOperationException("The signature placeholder (Contents) is missing from the document.");
    }

    internal static int Find(byte[] data, byte[] needle, int from)
    {
        for (var i = from; i <= data.Length - needle.Length; i++)
        {
            if (data[i] != needle[0]) continue;
            var ok = true;
            for (var j = 1; j < needle.Length; j++) if (data[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}
