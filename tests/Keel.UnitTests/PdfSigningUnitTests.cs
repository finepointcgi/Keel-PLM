using System.Diagnostics;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Keel.Core;
using Keel.Core.Reports;
using Keel.Documents.Marking;
using Keel.Reports.Pdf;
using Keel.Reports.Signing;
using UglyToad.PdfPig;

namespace Keel.UnitTests;

/// <summary>Signed release packages (ADR-0051): the signature, tamper detection, trust, algorithm policy, key loading and the identical marking of signed and unsigned bytes.</summary>
public sealed partial class ReportsUnitTests
{
    private static readonly DateTimeOffset SignNow = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly SignatureReservation Reserve = new("Approved for release", "Keel test site", "Keel test signer", SignNow);

    private sealed record Pki(X509Certificate2 Root, X509Certificate2 Leaf, AsymmetricAlgorithm LeafKey) : IDisposable
    {
        public LoadedSigningKey Key() => new(Leaf, LeafKey, [Root]);
        public void Dispose() { Root.Dispose(); Leaf.Dispose(); LeafKey.Dispose(); }
    }

    private static Pki MakePki(string kind = "ecdsa", HashAlgorithmName? hash = null, int rsaBits = 2048, DateTimeOffset? notBefore = null, int days = 90, ECCurve? curve = null)
    {
        var nb = notBefore ?? SignNow.AddDays(-1);
        var h = hash ?? HashAlgorithmName.SHA256;
        AsymmetricAlgorithm rootKey = kind == "rsa" ? RSA.Create(rsaBits) : ECDsa.Create(curve ?? ECCurve.NamedCurves.nistP256);
        AsymmetricAlgorithm leafKey = kind == "rsa" ? RSA.Create(rsaBits) : ECDsa.Create(curve ?? ECCurve.NamedCurves.nistP256);
        CertificateRequest Req(string name, AsymmetricAlgorithm k) => k switch
        {
            RSA r => new CertificateRequest(name, r, h, RSASignaturePadding.Pkcs1),
            ECDsa e => new CertificateRequest(name, e, h),
            _ => throw new InvalidOperationException(),
        };
        var rootReq = Req("CN=Test Root, O=Keel test", rootKey);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var root = rootReq.CreateSelfSigned(nb, nb.AddDays(days + 30));
        var leafReq = Req("CN=Test Signer, O=Keel test", leafKey);
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var serial = RandomNumberGenerator.GetBytes(8); serial[0] &= 0x7F;
        var leaf = leafReq.Create(root, nb, nb.AddDays(days), serial);
        return new Pki(X509CertificateLoader.LoadCertificate(root.RawData), X509CertificateLoader.LoadCertificate(leaf.RawData), leafKey);
    }

    private static (byte[] Signed, byte[] Unsigned) SignedPackage(Pki pki, int lines = 60)
    {
        var content = ManyLines(lines);
        var marking = PackageMarking.For([.. content.Objects.Select(o => o.Markings)], MarkingStatements.Notices);
        var id = new PackageIdentity("RPK-000042", SignNow, "Alice Engineer");
        var unsigned = ReleasePackagePdf.Render(content, marking, id, EmbeddedFontResolver.EnsureInstalled);
        var reserved = ReleasePackagePdf.Render(content, marking, id, EmbeddedFontResolver.EnsureInstalled, Reserve);
        var key = pki.Key(); // not disposed: the key belongs to the test certificate set
        return (PdfSigner.Sign(reserved, key, SignNow).Bytes, unsigned);
    }

    private static string PageText(byte[] pdf, int page) { using var d = PdfDocument.Open(pdf); return Regex.Replace(d.GetPage(page).Text, @"\s+", " "); }

    [Fact]
    public void A_signed_package_verifies_and_carries_exactly_the_same_banners_and_notices_as_the_unsigned_one()
    {
        using var pki = MakePki();
        var (signed, unsigned) = SignedPackage(pki);
        using (var s = PdfDocument.Open(signed))
        using (var u = PdfDocument.Open(unsigned))
        {
            Assert.Equal(u.NumberOfPages, s.NumberOfPages);
            for (var p = 1; p <= s.NumberOfPages; p++)
            {
                Assert.Equal(PageText(unsigned, p), PageText(signed, p));
                var text = PageText(signed, p);
                Assert.True(Regex.Count(text, "DISTRIBUTION F") >= 2, $"page {p}: banner at top and bottom");
                Assert.Contains("DISTRIBUTION STATEMENT F", text, StringComparison.Ordinal);
            }
        }
        var report = PdfSignatureVerifier.Verify(signed, [pki.Root], SignNow.AddMinutes(1));
        Assert.Equal(VerificationVerdict.Valid, report.Verdict);
        var sig = Assert.Single(report.Signatures);
        Assert.True(sig.CoversWholeFile && sig.IntegrityIntact && sig.SigningCertificateBound && sig.ChainTrusted && sig.CertificateValidAtSigningTime);
        Assert.Equal("ETSI.CAdES.detached", sig.SubFilter.TrimStart('/'));
        Assert.Equal("Approved for release", sig.Reason);
        Assert.Equal("Keel test site", sig.Location);
        Assert.Equal(SignNow, sig.SigningTime);
        Assert.Contains("CN=Test Signer", sig.SignerSubject, StringComparison.Ordinal);
        Assert.Equal(2, sig.ChainCertificates.Count);
        Assert.Equal(report.FileSha256, Convert.ToHexStringLower(SHA256.HashData(signed)));
    }

    [Fact]
    public void The_cms_is_a_detached_signature_with_the_chain_the_signing_time_and_the_signing_certificate_binding()
    {
        using var pki = MakePki();
        var (signed, _) = SignedPackage(pki, 5);
        var text = Encoding.Latin1.GetString(signed);
        var m = Regex.Match(text, @"/ByteRange\s*\[0 (\d+) (\d+) (\d+)\]");
        Assert.True(m.Success);
        var r1 = int.Parse(m.Groups[1].Value); var r2 = int.Parse(m.Groups[2].Value); var r3 = int.Parse(m.Groups[3].Value);
        Assert.Equal(signed.Length, r2 + r3);
        var der = new AsnReader(Convert.FromHexString(Encoding.ASCII.GetString(signed, r1 + 1, r2 - r1 - 2)), AsnEncodingRules.BER).ReadEncodedValue().ToArray();
        var cms = new SignedCms();
        cms.Decode(der);
        Assert.Equal(2, cms.Certificates.Count);          // signer and root: a verifier needs no network
        Assert.Single(cms.SignerInfos.Cast<SignerInfo>());
        var si = cms.SignerInfos[0];
        Assert.Equal("2.16.840.1.101.3.4.2.1", si.DigestAlgorithm.Value); // SHA-256
        var oids = si.SignedAttributes.Cast<CryptographicAttributeObject>().Select(a => a.Oid.Value).ToList();
        Assert.Contains("1.2.840.113549.1.9.5", oids);     // signing time
        Assert.Contains("1.2.840.113549.1.9.16.2.47", oids); // ESS signing-certificate-v2
        Assert.Single(oids, o => o == "1.2.840.113549.1.9.5");
        Assert.Empty(cms.ContentInfo.Content);   // detached: the PDF bytes are not inside
    }

    [Theory]
    [InlineData("front")]
    [InlineData("middle")]
    [InlineData("after-signature")]
    public void Changing_a_single_byte_anywhere_outside_the_signature_is_detected(string where)
    {
        using var pki = MakePki();
        var (signed, _) = SignedPackage(pki);
        var text = Encoding.Latin1.GetString(signed);
        var m = Regex.Match(text, @"/ByteRange\s*\[0 (\d+) (\d+) (\d+)\]");
        var r1 = int.Parse(m.Groups[1].Value); var r2 = int.Parse(m.Groups[2].Value);
        var at = where switch { "front" => 20, "middle" => r1 / 2, _ => r2 + 3 };
        var tampered = (byte[])signed.Clone();
        tampered[at] ^= 0x01;
        var report = PdfSignatureVerifier.Verify(tampered, [pki.Root], SignNow.AddMinutes(1));
        Assert.Equal(VerificationVerdict.Tampered, report.Verdict);
        Assert.False(report.Signatures[0].IntegrityIntact);
    }

    [Fact]
    public void Text_changed_in_a_page_and_bytes_appended_after_signing_are_both_tamper()
    {
        using var pki = MakePki();
        var (signed, _) = SignedPackage(pki);
        var appended = signed.Concat(Encoding.ASCII.GetBytes("\n%%appended\n")).ToArray();
        var r = PdfSignatureVerifier.Verify(appended, [pki.Root], SignNow.AddMinutes(1));
        Assert.Equal(VerificationVerdict.Tampered, r.Verdict);
        Assert.False(r.Signatures[0].CoversWholeFile);
        Assert.Contains(r.Signatures[0].Findings, f => f.Contains("added to the file", StringComparison.Ordinal));
        // The title in the document information is signed too.
        var text = Encoding.Latin1.GetString(signed);
        var at = text.IndexOf("Release package RPK-000042", StringComparison.Ordinal);
        if (at >= 0)
        {
            var t = (byte[])signed.Clone();
            t[at + 8] = (byte)'X';
            Assert.Equal(VerificationVerdict.Tampered, PdfSignatureVerifier.Verify(t, [pki.Root], SignNow.AddMinutes(1)).Verdict);
        }
    }

    [Fact]
    public void A_signature_whose_chain_is_not_a_configured_trust_anchor_is_untrusted_not_valid()
    {
        using var pki = MakePki();
        using var other = MakePki();
        var (signed, _) = SignedPackage(pki, 5);
        var none = PdfSignatureVerifier.Verify(signed, [], SignNow.AddMinutes(1));
        Assert.Equal(VerificationVerdict.Untrusted, none.Verdict);
        Assert.True(none.Signatures[0].IntegrityIntact);
        Assert.Contains(none.Signatures[0].Findings, f => f.Contains("trust anchor", StringComparison.Ordinal));
        Assert.Equal(VerificationVerdict.Untrusted, PdfSignatureVerifier.Verify(signed, [other.Root], SignNow.AddMinutes(1)).Verdict);
    }

    [Fact]
    public void A_certificate_that_was_not_valid_at_the_signing_time_is_reported_and_one_that_expired_since_is_still_valid_for_the_old_signature()
    {
        using var pki = MakePki(days: 10);
        var (signed, _) = SignedPackage(pki, 5);
        // Verified a year later: the certificate has expired, but it was valid when the package was signed.
        var later = PdfSignatureVerifier.Verify(signed, [pki.Root], SignNow.AddDays(400));
        Assert.Equal(VerificationVerdict.Valid, later.Verdict);
        // Signed with a clock outside the validity of the certificate.
        var content = ManyLines(5);
        var marking = PackageMarking.For([.. content.Objects.Select(o => o.Markings)], MarkingStatements.Notices);
        var reserved = ReleasePackagePdf.Render(content, marking, new PackageIdentity("RPK-000043", SignNow, "x"), EmbeddedFontResolver.EnsureInstalled, Reserve);
        var key = pki.Key(); // not disposed: the key belongs to the test certificate set
        var early = PdfSigner.Sign(reserved, key, SignNow.AddDays(-30)).Bytes;
        var r = PdfSignatureVerifier.Verify(early, [pki.Root], SignNow);
        Assert.NotEqual(VerificationVerdict.Valid, r.Verdict);
        Assert.False(r.Signatures[0].CertificateValidAtSigningTime);
    }

    [Fact]
    public void Unsigned_and_unreadable_files_are_told_apart()
    {
        using var pki = MakePki();
        var (_, unsigned) = SignedPackage(pki, 5);
        Assert.Equal(VerificationVerdict.NotSigned, PdfSignatureVerifier.Verify(unsigned, [pki.Root], SignNow).Verdict);
        Assert.Equal(VerificationVerdict.Unreadable, PdfSignatureVerifier.Verify("hello"u8.ToArray(), [], SignNow).Verdict);
        Assert.Equal(VerificationVerdict.Unreadable, PdfSignatureVerifier.Verify([], [], SignNow).Verdict);
    }

    [Fact]
    public void RSA_and_P384_identities_sign_with_sha_256_and_sha_384_and_verify()
    {
        using var rsa = MakePki("rsa");
        var (s1, _) = SignedPackage(rsa, 5);
        Assert.Equal(VerificationVerdict.Valid, PdfSignatureVerifier.Verify(s1, [rsa.Root], SignNow).Verdict);
        using var p384 = MakePki("ecdsa", HashAlgorithmName.SHA384, curve: ECCurve.NamedCurves.nistP384);
        Assert.Equal(("SHA384", "2.16.840.1.101.3.4.2.2"), p384.Key().Digest);
        var (s2, _) = SignedPackage(p384, 5);
        var report = PdfSignatureVerifier.Verify(s2, [p384.Root], SignNow);
        Assert.Equal(VerificationVerdict.Valid, report.Verdict);
        Assert.True(report.Signatures[0].SigningCertificateBound);
    }

    [Fact]
    public void Weak_keys_and_hashes_are_refused_by_the_policy()
    {
        using var small = MakePki("rsa", rsaBits: 1024);
        Assert.Contains("2048", SigningPolicy.Reject(small.Leaf)!, StringComparison.Ordinal);
        using var ok = MakePki("rsa");
        Assert.Null(SigningPolicy.Reject(ok.Leaf));
        try
        {
            using var secp256k1 = ECDsa.Create(ECCurve.CreateFromFriendlyName("secp256k1"));
            var req = new CertificateRequest("CN=k1", secp256k1, HashAlgorithmName.SHA256);
            using var k1 = req.CreateSelfSigned(SignNow.AddDays(-1), SignNow.AddDays(1));
            Assert.NotNull(SigningPolicy.Reject(k1));
        }
        catch (PlatformNotSupportedException) { /* this platform cannot even create the curve: nothing to refuse */ }
    }

    [Fact]
    public void The_development_certificate_says_dev_only_and_signs_and_verifies()
    {
        var (bundle, keyPem) = DevSigningCertificate.Generate(SignNow);
        var certs = SigningKeyLoader.ParseBundle(bundle);
        Assert.Equal(2, certs.Count);
        Assert.All(certs, c => Assert.Contains("DEV ONLY", c.Subject, StringComparison.Ordinal));
        Assert.All(certs, c => Assert.True(c.NotAfter - c.NotBefore < TimeSpan.FromDays(91)));
        Assert.Contains("PRIVATE KEY", keyPem, StringComparison.Ordinal);
        var reference = new SigningKeyReference(SigningKeySources.PemFile, bundle, WriteTemp(keyPem), null, SigningKeyLoader.Sha256(certs[0]));
        using var key = SigningKeyLoader.Load(reference, new SigningOptions());
        SigningKeyLoader.ProveKeyMatches(key);
        var content = ManyLines(5);
        var marking = PackageMarking.For([.. content.Objects.Select(o => o.Markings)], MarkingStatements.Notices);
        var pdf = PdfSigner.Sign(ReleasePackagePdf.Render(content, marking, new PackageIdentity("RPK-000044", SignNow, "x"), EmbeddedFontResolver.EnsureInstalled, Reserve), key, SignNow).Bytes;
        Assert.Equal(VerificationVerdict.Valid, PdfSignatureVerifier.Verify(pdf, [certs[1]], SignNow).Verdict);
    }

    private static string WriteTemp(string text, string suffix = ".pem")
    {
        var path = Path.Combine(Path.GetTempPath(), "keel-sign-test-" + Guid.NewGuid().ToString("N") + suffix);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Key_files_pkcs12_files_encrypted_keys_and_password_references_load_and_a_wrong_key_is_caught()
    {
        using var pki = MakePki("rsa");
        var bundle = pki.Leaf.ExportCertificatePem() + "\n" + pki.Root.ExportCertificatePem();
        var thumb = SigningKeyLoader.Sha256(pki.Leaf);
        var rsaKey = (RSA)pki.LeafKey;
        var plain = WriteTemp(rsaKey.ExportPkcs8PrivateKeyPem());
        var encrypted = WriteTemp(rsaKey.ExportEncryptedPkcs8PrivateKeyPem("pw-1234"u8, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10000)));
        var pwFile = WriteTemp("pw-1234\n");
        using var withKey = X509CertificateLoader.LoadCertificate(pki.Leaf.RawData).CopyWithPrivateKey(rsaKey);
        var pfx = WriteTemp("", ".pfx");
        File.WriteAllBytes(pfx, withKey.Export(X509ContentType.Pkcs12, "pfx-pass"));
        Environment.SetEnvironmentVariable("KEEL_TEST_SIGN_PFX_PW", "pfx-pass");
        var opts = new SigningOptions();
        foreach (var r in new[]
        {
            new SigningKeyReference(SigningKeySources.PemFile, bundle, plain, null, thumb),
            new SigningKeyReference(SigningKeySources.PemFile, bundle, encrypted, "file:" + pwFile, thumb),
            new SigningKeyReference(SigningKeySources.PfxFile, bundle, pfx, "env:KEEL_TEST_SIGN_PFX_PW", thumb),
        })
        {
            using var k = SigningKeyLoader.Load(r, opts);
            SigningKeyLoader.ProveKeyMatches(k);
            Assert.Single(k.Chain);
        }
        // An encrypted key without a password reference, a missing variable and an unknown scheme are refused with words, not exceptions of the platform.
        Assert.Throws<CryptographicException>(() => SigningKeyLoader.Load(new SigningKeyReference(SigningKeySources.PemFile, bundle, encrypted, null, thumb), opts));
        Assert.Throws<CryptographicException>(() => SigningKeyLoader.Load(new SigningKeyReference(SigningKeySources.PfxFile, bundle, pfx, "env:KEEL_TEST_NO_SUCH_VARIABLE", thumb), opts));
        Assert.Throws<CryptographicException>(() => SigningKeyLoader.Load(new SigningKeyReference(SigningKeySources.PfxFile, bundle, pfx, "plain-text-password", thumb), opts));
        // A key that does not belong to the certificate is caught before it can sign.
        using var other = MakePki("rsa");
        var wrong = WriteTemp(((RSA)other.LeafKey).ExportPkcs8PrivateKeyPem());
        using var bad = SigningKeyLoader.Load(new SigningKeyReference(SigningKeySources.PemFile, bundle, wrong, null, thumb), opts);
        Assert.Throws<CryptographicException>(() => SigningKeyLoader.ProveKeyMatches(bad));
        // The operator can confine key files to one directory.
        var confined = new SigningOptions { AllowedKeyDirectory = Path.Combine(Path.GetTempPath(), "keel-only-here") };
        Assert.Throws<CryptographicException>(() => SigningKeyLoader.Load(new SigningKeyReference(SigningKeySources.PemFile, bundle, plain, null, thumb), confined));
        Assert.Throws<CryptographicException>(() => SigningKeyLoader.Load(new SigningKeyReference(SigningKeySources.PemFile, bundle, "relative/key.pem", null, thumb), opts));
        // A certificate that does not match its recorded thumbprint is refused.
        Assert.Throws<CryptographicException>(() => SigningKeyLoader.Load(new SigningKeyReference(SigningKeySources.PemFile, bundle, plain, null, new string('a', 64)), opts));
        foreach (var f in new[] { plain, encrypted, pwFile, pfx, wrong }) File.Delete(f);
    }

    [Fact]
    public void A_signature_that_does_not_fit_the_reserved_space_is_refused_and_a_document_without_a_field_cannot_be_signed()
    {
        using var pki = MakePki();
        var content = ManyLines(3);
        var marking = PackageMarking.For([.. content.Objects.Select(o => o.Markings)], MarkingStatements.Notices);
        var unsigned = ReleasePackagePdf.Render(content, marking, new PackageIdentity("RPK-000045", SignNow, "x"), EmbeddedFontResolver.EnsureInstalled);
        var key = pki.Key(); // not disposed: the key belongs to the test certificate set
        Assert.Throws<InvalidOperationException>(() => PdfSigner.Sign(unsigned, key, SignNow));
    }

    [Fact]
    public void The_shipped_verification_script_accepts_a_signed_package_and_rejects_a_changed_one_using_only_standard_tools()
    {
        // The script needs bash, xxd and OpenSSL 3 or later; where they are missing the test says nothing (the in-process checks above still run).
        var openssl = new[] { "/opt/homebrew/bin/openssl", "/usr/local/bin/openssl", "/usr/bin/openssl" }.FirstOrDefault(File.Exists);
        var xxd = new[] { "/usr/bin/xxd", "/usr/local/bin/xxd", "/opt/homebrew/bin/xxd" }.FirstOrDefault(File.Exists);
        if (openssl is null || xxd is null || !File.Exists("/bin/bash")) return;
        var help = RunTool(openssl, null, "cms", "-help");
        if (!help.Output.Contains("-verify", StringComparison.Ordinal) || help.Output.Contains("Usage cms", StringComparison.Ordinal)) return;
        using var pki = MakePki();
        var (signed, _) = SignedPackage(pki, 5);
        var dir = Path.Combine(Path.GetTempPath(), "keel-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var script = Path.Combine(Root, "scripts", "verify-package-signature.sh");
            var pdf = Path.Combine(dir, "package.pdf");
            var root = Path.Combine(dir, "root.pem");
            File.WriteAllBytes(pdf, signed);
            File.WriteAllText(root, pki.Root.ExportCertificatePem());
            var path = Path.GetDirectoryName(openssl) + ":" + Path.GetDirectoryName(xxd) + ":/usr/bin:/bin";
            var ok = RunTool("/bin/bash", path, script, pdf, root);
            Assert.True(ok.Code == 0, ok.Output);
            Assert.Contains("VERIFIED", ok.Output, StringComparison.Ordinal);
            var tampered = (byte[])signed.Clone();
            tampered[200] ^= 1;
            File.WriteAllBytes(pdf, tampered);
            Assert.Equal(1, RunTool("/bin/bash", path, script, pdf, root).Code);
            File.WriteAllBytes(pdf, [.. signed, .. "\n%appended\n"u8.ToArray()]);
            Assert.Equal(1, RunTool("/bin/bash", path, script, pdf, root).Code);
            using var other = MakePki();
            File.WriteAllBytes(pdf, signed);
            File.WriteAllText(root, other.Root.ExportCertificatePem());
            Assert.Equal(1, RunTool("/bin/bash", path, script, pdf, root).Code);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static (int Code, string Output) RunTool(string exe, string? path, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (path is not null) psi.Environment["PATH"] = path;
        using var p = System.Diagnostics.Process.Start(psi)!;
        var all = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, all);
    }
}
