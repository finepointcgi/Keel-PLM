using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Keel.Reports.Signing;

/// <summary>
/// DEVELOPMENT ONLY. Generates a throw-away two-certificate chain (a root and a signing certificate, ECDSA P-256, SHA-256) so a developer can see signing work without an operator PKI.
/// Both subjects say "DEV ONLY", the certificates are valid for 90 days, and the page that offers this is off unless the host is a development or demo host. Never use it for real packages:
/// a real deployment gets its certificate from the operator's own PKI (docs/ops/package-signing.md).
/// </summary>
public static class DevSigningCertificate
{
    public const string RootName = "CN=Keel DEV ONLY Root - NOT FOR PRODUCTION, O=Keel development";
    public const string SignerName = "CN=Keel DEV ONLY Package Signing - NOT FOR PRODUCTION, O=Keel development";

    /// <summary>The signing certificate first, then the root, as a PEM bundle; and the PKCS#8 private key of the signing certificate.</summary>
    public static (string BundlePem, string KeyPem) Generate(DateTimeOffset now)
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootReq = new CertificateRequest(RootName, rootKey, HashAlgorithmName.SHA256);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootReq.PublicKey, false));
        using var root = rootReq.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(90));

        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafReq = new CertificateRequest(SignerName, leafKey, HashAlgorithmName.SHA256);
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        leafReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(leafReq.PublicKey, false));
        leafReq.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var leaf = leafReq.Create(root, now.AddMinutes(-5), now.AddDays(90), serial);

        var sb = new StringBuilder();
        sb.Append(leaf.ExportCertificatePem()).Append('\n').Append(root.ExportCertificatePem()).Append('\n');
        return (sb.ToString(), leafKey.ExportPkcs8PrivateKeyPem());
    }
}
