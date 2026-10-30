using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Keel.Reports.Signing;

public static class VerificationVerdict
{
    public const string NotSigned = "not_signed";
    public const string Valid = "valid";
    public const string Untrusted = "untrusted";
    public const string Tampered = "tampered";
    public const string Unreadable = "unreadable";
}

/// <summary>One signature of a PDF and everything checked about it.</summary>
public sealed record SignatureCheck(
    int Index, string SubFilter, bool CoversWholeFile, bool IntegrityIntact, bool SigningCertificateBound, string SignerSubject, string IssuerSubject, string Thumbprint,
    DateTimeOffset? SigningTime, string? Reason, string? Location, bool ChainTrusted, bool CertificateValidAtSigningTime, IReadOnlyList<string> ChainCertificates, IReadOnlyList<string> Findings, string Verdict);

public sealed record VerificationReport(string Verdict, string FileSha256, long FileBytes, IReadOnlyList<SignatureCheck> Signatures, IReadOnlyList<string> Findings);

/// <summary>
/// Verifies the signatures of a PDF without trusting anything in it (ADR-0051): the byte ranges must cover the whole file, the CMS must verify over exactly those bytes (any changed
/// byte breaks the digest), the signing-certificate attribute must name the signer, and the certificate chain must reach a trust anchor the operator configured, checked at the
/// signing time. Revocation is not checked: an air-gapped host has no responder, so the report says so. The checks work on any PDF signed with adbe.pkcs7.detached or ETSI.CAdES.detached.
/// </summary>
public static class PdfSignatureVerifier
{
    public const int MaxBytes = 64 * 1024 * 1024;

    public static VerificationReport Verify(byte[] pdf, IReadOnlyList<X509Certificate2> trustAnchors, DateTimeOffset now)
    {
        var fileHash = Convert.ToHexStringLower(SHA256.HashData(pdf));
        if (pdf.Length < 8 || pdf.Length > MaxBytes || !Encoding.ASCII.GetString(pdf, 0, 5).Equals("%PDF-", StringComparison.Ordinal))
            return new VerificationReport(VerificationVerdict.Unreadable, fileHash, pdf.Length, [], ["The file is not a PDF document, or is larger than the limit of 64 MB."]);
        var latin = Encoding.Latin1.GetString(pdf);
        var found = new List<(int[] Range, string SubFilter, string? Reason, string? Location)>();
        var at = 0;
        while ((at = latin.IndexOf("/ByteRange", at, StringComparison.Ordinal)) >= 0)
        {
            var open = latin.IndexOf('[', at);
            var close = open < 0 ? -1 : latin.IndexOf(']', open);
            if (open < 0 || close < 0 || close - open > 200) { at += 10; continue; }
            var parts = latin[(open + 1)..close].Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 && parts.All(p => long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v <= int.MaxValue))
            {
                // The signature dictionary surrounds the Contents value: read its neighbours (the keys before and after it) within a bounded window.
                var ints = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
                var r1 = Math.Clamp(ints[1], 0, latin.Length);
                var r2 = Math.Clamp(ints[2], 0, latin.Length);
                var window = string.Concat(latin.AsSpan(Math.Max(0, r1 - 1500), Math.Min(1500, r1)), " ", latin.AsSpan(r2, Math.Min(1500, latin.Length - r2)));
                found.Add((ints, Name(window, "/SubFilter") ?? "", Text(window, "/Reason"), Text(window, "/Location")));
            }
            at = close;
        }
        if (found.Count == 0) return new VerificationReport(VerificationVerdict.NotSigned, fileHash, pdf.Length, [], ["The document carries no digital signature."]);

        var checks = new List<SignatureCheck>();
        for (var i = 0; i < found.Count; i++) checks.Add(CheckOne(i + 1, pdf, found[i], trustAnchors, now));
        var verdict = checks.Any(c => c.Verdict == VerificationVerdict.Unreadable) ? VerificationVerdict.Unreadable
            : checks.Any(c => c.Verdict == VerificationVerdict.Tampered) ? VerificationVerdict.Tampered
            : checks.Any(c => c.Verdict == VerificationVerdict.Untrusted) ? VerificationVerdict.Untrusted
            : VerificationVerdict.Valid;
        return new VerificationReport(verdict, fileHash, pdf.Length, checks, ["Revocation was not checked: this host has no network access to certificate revocation services."]);
    }

    private static SignatureCheck CheckOne(int index, byte[] pdf, (int[] Range, string SubFilter, string? Reason, string? Location) sig, IReadOnlyList<X509Certificate2> anchors, DateTimeOffset now)
    {
        var findings = new List<string>();
        SignatureCheck Fail(string verdict, string finding, bool covers = false)
        {
            findings.Add(finding);
            return new SignatureCheck(index, sig.SubFilter, covers, false, false, "", "", "", null, sig.Reason, sig.Location, false, false, [], findings, verdict);
        }
        var r = sig.Range;
        long len = pdf.Length;
        if (r[0] != 0 || r[1] < 1 || r[2] < r[1] + 2 || (long)r[2] + r[3] > len || r[3] < 0)
            return Fail(VerificationVerdict.Tampered, "The byte ranges of the signature do not describe the file.");
        var covers = (long)r[2] + r[3] == len;
        if (pdf[r[1]] != (byte)'<' || pdf[r[2] - 1] != (byte)'>')
            return Fail(VerificationVerdict.Tampered, "The signature value is not where the byte ranges say it is.", covers);
        if (!covers) findings.Add("Bytes were added to the file after it was signed.");

        byte[] der;
        try
        {
            var hex = Encoding.ASCII.GetString(pdf, r[1] + 1, r[2] - r[1] - 2).Trim();
            var raw = Convert.FromHexString(hex.Length % 2 == 0 ? hex : hex + "0");
            var reader = new AsnReader(raw, AsnEncodingRules.BER);
            der = reader.ReadEncodedValue().ToArray(); // trailing zero padding is ignored
        }
        catch (Exception e) when (e is FormatException or AsnContentException or CryptographicException) { return Fail(VerificationVerdict.Unreadable, "The signature value cannot be read.", covers); }

        var signedData = new byte[r[1] + r[3]];
        Buffer.BlockCopy(pdf, 0, signedData, 0, r[1]);
        Buffer.BlockCopy(pdf, r[2], signedData, r[1], r[3]);

        var cms = new SignedCms(new ContentInfo(signedData), detached: true);
        try { cms.Decode(der); }
        catch (CryptographicException) { return Fail(VerificationVerdict.Unreadable, "The signature is not a valid CMS structure.", covers); }
        if (cms.SignerInfos.Count != 1) return Fail(VerificationVerdict.Unreadable, "The signature must have exactly one signer.", covers);
        var si = cms.SignerInfos[0];
        var cert = si.Certificate;
        if (cert is null) return Fail(VerificationVerdict.Unreadable, "The signature does not carry the certificate of the signer.", covers);

        var intact = true;
        try { si.CheckSignature(verifySignatureOnly: true); }
        catch (CryptographicException) { intact = false; findings.Add("The content or the signature was changed: the signature does not verify over the signed bytes."); }

        DateTimeOffset? signingTime = null;
        var bound = true;
        foreach (var a in si.SignedAttributes)
        {
            if (a.Oid?.Value == "1.2.840.113549.1.9.5")
                foreach (var v in a.Values) if (v is Pkcs9SigningTime t) signingTime = new DateTimeOffset(t.SigningTime.ToUniversalTime(), TimeSpan.Zero);
            if (a.Oid?.Value == "1.2.840.113549.1.9.16.2.47") bound = CertificateIsBound(a.Values[0].RawData, cert);
        }
        if (!bound) findings.Add("The signing-certificate attribute does not name the certificate that signed.");
        if (signingTime is null) findings.Add("The signature carries no signing time.");
        else if (signingTime > now.AddMinutes(10)) findings.Add("The signing time is in the future.");

        // Chain at the signing time, from the certificates in the signature only (no downloads), to the operator's trust anchors.
        var chainNames = new List<string>();
        var trusted = false;
        var validAtSigning = false;
        using (var chain = new X509Chain())
        {
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationTime = (signingTime ?? now).UtcDateTime;
            foreach (var a in anchors) chain.ChainPolicy.CustomTrustStore.Add(a);
            foreach (var c in cms.Certificates) chain.ChainPolicy.ExtraStore.Add(c);
            var built = chain.Build(cert);
            foreach (var e in chain.ChainElements) chainNames.Add(e.Certificate.Subject);
            var bad = chain.ChainStatus.Where(s => s.Status != X509ChainStatusFlags.NoError).ToList();
            validAtSigning = !bad.Any(s => (s.Status & (X509ChainStatusFlags.NotTimeValid | X509ChainStatusFlags.NotTimeNested)) != 0) && signingTime is not null
                && cert.NotBefore.ToUniversalTime() <= signingTime && signingTime <= cert.NotAfter.ToUniversalTime();
            trusted = built && bad.Count == 0;
            if (!trusted)
                foreach (var s in bad) findings.Add(s.Status == X509ChainStatusFlags.UntrustedRoot ? "The certificate chain does not end in a trust anchor configured on this Keel server." : $"Certificate chain: {s.StatusInformation.Trim()}");
        }
        if (!validAtSigning && signingTime is not null) findings.Add("The signing certificate was not valid at the signing time.");

        var verdict = !intact || !covers || !bound ? VerificationVerdict.Tampered : !trusted || !validAtSigning ? VerificationVerdict.Untrusted : VerificationVerdict.Valid;
        return new SignatureCheck(index, sig.SubFilter, covers, intact, bound, cert.Subject, cert.Issuer, LoadedHash(cert), signingTime, sig.Reason, sig.Location, trusted, validAtSigning, chainNames, findings, verdict);
    }

    private static string LoadedHash(X509Certificate2 c) => Convert.ToHexStringLower(SHA256.HashData(c.RawData));

    private static bool CertificateIsBound(byte[] attrValue, X509Certificate2 cert)
    {
        try
        {
            var seq = new AsnReader(attrValue, AsnEncodingRules.BER).ReadSequence();
            var certs = seq.ReadSequence();
            var id = certs.ReadSequence();
            var alg = "2.16.840.1.101.3.4.2.1";
            if (id.PeekTag().TagValue == 16) { var ai = id.ReadSequence(); alg = ai.ReadObjectIdentifier(); }
            var expected = id.ReadOctetString();
            byte[]? actual = alg switch
            {
                "2.16.840.1.101.3.4.2.1" => SHA256.HashData(cert.RawData),
                "2.16.840.1.101.3.4.2.2" => SHA384.HashData(cert.RawData),
                "2.16.840.1.101.3.4.2.3" => SHA512.HashData(cert.RawData),
                _ => null,
            };
            return actual is not null && CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception e) when (e is AsnContentException or InvalidOperationException) { return false; }
    }

    private static string? Name(string window, string key)
    {
        var at = window.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return null;
        var p = at + key.Length;
        while (p < window.Length && window[p] == ' ') p++;
        if (p >= window.Length || window[p] != '/') return null;
        var q = p + 1;
        while (q < window.Length && !char.IsWhiteSpace(window[q]) && window[q] is not ('/' or '>' or '<' or '[' or ']' or '(')) q++;
        return window[p..q];
    }

    /// <summary>A literal string value of the signature dictionary, as written by the signer (ASCII or UTF-16BE with byte order mark).</summary>
    private static string? Text(string window, string key)
    {
        var at = window.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return null;
        var p = at + key.Length;
        while (p < window.Length && window[p] == ' ') p++;
        if (p >= window.Length) return null;
        if (window[p] == '(')
        {
            var sb = new StringBuilder();
            for (var q = p + 1; q < window.Length && window[q] != ')'; q++)
            {
                if (window[q] == '\\' && q + 1 < window.Length) q++;
                sb.Append(window[q]);
            }
            var s = sb.ToString();
            if (s.Length >= 2 && s[0] == 'þ' && s[1] == 'ÿ') return Encoding.BigEndianUnicode.GetString(Encoding.Latin1.GetBytes(s[2..]));
            return s.Length > 300 ? s[..300] : s;
        }
        if (window[p] == '<')
        {
            var end = window.IndexOf('>', p);
            if (end < 0 || end - p > 1200) return null;
            try
            {
                var bytes = Convert.FromHexString(window[(p + 1)..end]);
                return bytes is [0xFE, 0xFF, ..] ? Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2) : Encoding.Latin1.GetString(bytes);
            }
            catch (FormatException) { return null; }
        }
        return null;
    }
}
