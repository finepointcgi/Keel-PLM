using System.Security.Cryptography.X509Certificates;

namespace Keel.Integration;

/// <summary>
/// Resolves the names a sink row refers to (secrets, client certificates, CA bundles) from configuration. Values live in the secrets
/// directory or environment (<c>Keel__Integration__Secrets__{name}</c>), never in the database (ADR-0025, ADR-0020).
/// </summary>
public sealed class SecretResolver(IntegrationOptions options)
{
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 41 && char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public string? Secret(string name) => options.Secrets.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    public bool SecretExists(string name) => Secret(name) is not null;

    public bool ClientCertificateExists(string name) => options.ClientCertificates.TryGetValue(name, out var p) && File.Exists(p);

    public bool CaBundleExists(string name) => options.CaBundles.TryGetValue(name, out var p) && File.Exists(p);

    /// <summary>The client certificate with its private key, loaded from a PEM file (certificate and key in one file).</summary>
    public X509Certificate2 ClientCertificate(string name)
    {
        if (!options.ClientCertificates.TryGetValue(name, out var path)) throw new InvalidOperationException($"Client certificate '{name}' is not configured (Keel:Integration:ClientCertificates:{name}).");
        using var pem = X509Certificate2.CreateFromPemFile(path);
        // Re-import through PKCS#12 so the private key is usable by TLS on every platform.
        var password = Guid.NewGuid().ToString("N"); // some platforms refuse a PKCS#12 without a password
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx, password), password);
    }

    public X509Certificate2Collection CaBundle(string name)
    {
        if (!options.CaBundles.TryGetValue(name, out var path)) throw new InvalidOperationException($"CA bundle '{name}' is not configured (Keel:Integration:CaBundles:{name}).");
        var bundle = new X509Certificate2Collection();
        bundle.ImportFromPemFile(path);
        if (bundle.Count == 0) throw new InvalidOperationException($"CA bundle '{name}' contains no certificates.");
        return bundle;
    }
}
