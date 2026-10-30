using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Keel.Reports.Signing;

public static class SigningKeySources
{
    public const string PemFile = "pem_file";
    public const string PfxFile = "pfx_file";
    public const string X509Store = "x509_store";
    public const string OpensslProvider = "openssl_provider";
    public static readonly IReadOnlyList<string> All = [PemFile, PfxFile, X509Store, OpensslProvider];

    public static string Label(string source) => source switch
    {
        PemFile => "PEM file (private key on this host)",
        PfxFile => "PKCS#12 file (private key on this host)",
        X509Store => "Certificate store (operating system key store or HSM driver)",
        OpensslProvider => "OpenSSL provider key (for example PKCS#11 HSM, Linux)",
        _ => source,
    };
}

/// <summary>Host settings of the signing feature (Keel:Reports:Signing). All optional.</summary>
public sealed class SigningOptions
{
    public const string Section = "Keel:Reports:Signing";

    /// <summary>When set, PEM and PKCS#12 key files and password files must lie below this directory.</summary>
    public string? AllowedKeyDirectory { get; set; }

    /// <summary>
    /// Lets the administrator page generate a development-only certificate. The host sets it to true only in the Development and Demo environments; the configuration value can only
    /// switch it off there (a production host never offers the generator, whatever the configuration says).
    /// </summary>
    public bool AllowDevelopmentCertificate { get; set; } = true;

    /// <summary>Where generated development keys are written (mode 0600). Defaults to a "keel-dev-signing" directory under the temporary directory.</summary>
    public string? DevelopmentKeyDirectory { get; set; }
}

/// <summary>The stored description of an identity that the key loader needs (the database row, without any secret).</summary>
public sealed record SigningKeyReference(string KeySource, string CertificatePem, string KeyReference, string? PasswordReference, string Thumbprint);

/// <summary>A signing certificate with the private key that belongs to it, ready to sign. Dispose to release a key held in memory.</summary>
public sealed class LoadedSigningKey(X509Certificate2 certificate, AsymmetricAlgorithm privateKey, IReadOnlyList<X509Certificate2> chain, IDisposable? owner = null) : IDisposable
{
    public X509Certificate2 Certificate { get; } = certificate;
    public AsymmetricAlgorithm PrivateKey { get; } = privateKey;
    /// <summary>The other certificates of the bundle: intermediates and the root, embedded in every signature so a verifier needs no network.</summary>
    public IReadOnlyList<X509Certificate2> Chain { get; } = chain;

    public (string Name, string Oid) Digest => SigningPolicy.DigestFor(Certificate);
    public string AlgorithmLabel => SigningPolicy.Describe(Certificate);

    public void Dispose()
    {
        PrivateKey.Dispose();
        owner?.Dispose();
    }
}

/// <summary>Which keys and certificates the product accepts for signing: platform-approved algorithms only (ADR-0008, ADR-0051).</summary>
public static class SigningPolicy
{
    private static readonly HashSet<string> CertificateSignatureAlgorithms =
    [
        "1.2.840.113549.1.1.11", "1.2.840.113549.1.1.12", "1.2.840.113549.1.1.13", // sha256/384/512 with RSA
        "1.2.840.113549.1.1.10",                                                   // RSASSA-PSS
        "1.2.840.10045.4.3.2", "1.2.840.10045.4.3.3", "1.2.840.10045.4.3.4",       // ecdsa-with-SHA256/384/512
    ];

    /// <summary>Null when the certificate may be used; otherwise the reason in words.</summary>
    public static string? Reject(X509Certificate2 cert)
    {
        if (cert.SignatureAlgorithm.Value is not { } sig || !CertificateSignatureAlgorithms.Contains(sig))
            return $"The certificate {cert.Subject} is signed with {cert.SignatureAlgorithm.FriendlyName ?? cert.SignatureAlgorithm.Value}; only SHA-256 or stronger with RSA or ECDSA is accepted.";
        using var rsa = cert.GetRSAPublicKey();
        if (rsa is not null) return rsa.KeySize >= 2048 ? null : $"The RSA key of {cert.Subject} has {rsa.KeySize} bits; at least 2048 are required.";
        using var ec = cert.GetECDsaPublicKey();
        if (ec is not null)
        {
            var oid = ec.ExportParameters(false).Curve.Oid?.Value;
            return oid is "1.2.840.10045.3.1.7" or "1.3.132.0.34" or "1.3.132.0.35" ? null : $"The elliptic curve of {cert.Subject} is not NIST P-256, P-384 or P-521.";
        }
        return $"The key of {cert.Subject} is neither RSA nor ECDSA.";
    }

    public static (string Name, string Oid) DigestFor(X509Certificate2 cert)
    {
        using var ec = cert.GetECDsaPublicKey();
        var curve = ec?.ExportParameters(false).Curve.Oid?.Value;
        return curve switch
        {
            "1.3.132.0.34" => ("SHA384", "2.16.840.1.101.3.4.2.2"),
            "1.3.132.0.35" => ("SHA512", "2.16.840.1.101.3.4.2.3"),
            _ => ("SHA256", "2.16.840.1.101.3.4.2.1"),
        };
    }

    public static string Describe(X509Certificate2 cert)
    {
        using var rsa = cert.GetRSAPublicKey();
        if (rsa is not null) return $"RSA {rsa.KeySize} with SHA-256";
        using var ec = cert.GetECDsaPublicKey();
        var (name, _) = DigestFor(cert);
        var curve = ec?.ExportParameters(false).Curve.Oid?.Value switch { "1.3.132.0.34" => "P-384", "1.3.132.0.35" => "P-521", _ => "P-256" };
        return $"ECDSA {curve} with {name.Insert(3, "-")}";
    }
}

/// <summary>Loads the private key an identity points to. Secrets are read at the moment of use from the environment or a file named in the identity; none is stored in the database.</summary>
public static class SigningKeyLoader
{
    public static IReadOnlyList<X509Certificate2> ParseBundle(string pem)
    {
        var list = new List<X509Certificate2>();
        const string begin = "-----BEGIN CERTIFICATE-----", end = "-----END CERTIFICATE-----";
        var at = 0;
        while ((at = pem.IndexOf(begin, at, StringComparison.Ordinal)) >= 0)
        {
            var stop = pem.IndexOf(end, at, StringComparison.Ordinal);
            if (stop < 0) throw new CryptographicException("A certificate in the bundle is not terminated.");
            list.Add(X509Certificate2.CreateFromPem(pem.AsSpan(at, stop + end.Length - at)));
            at = stop + end.Length;
        }
        if (list.Count == 0) throw new CryptographicException("The text holds no PEM certificate.");
        if (list.Count > 8) throw new CryptographicException("A bundle holds at most 8 certificates.");
        return list;
    }

    public static string Sha256(X509Certificate2 c) => Convert.ToHexStringLower(SHA256.HashData(c.RawData));

    public static LoadedSigningKey Load(SigningKeyReference r, SigningOptions options)
    {
        var bundle = ParseBundle(r.CertificatePem);
        var leaf = bundle[0];
        if (!Sha256(leaf).Equals(r.Thumbprint, StringComparison.Ordinal)) throw new CryptographicException("The stored certificate does not match its recorded thumbprint.");
        var chain = bundle.Skip(1).ToList();
        var password = ReadSecret(r.PasswordReference, options);
        switch (r.KeySource)
        {
            case SigningKeySources.PemFile:
            {
                var text = File.ReadAllText(CheckedPath(r.KeyReference, options));
                AsymmetricAlgorithm key = leaf.GetRSAPublicKey() is not null ? RSA.Create() : ECDsa.Create();
                try
                {
                    if (text.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal))
                    {
                        if (password is null) throw new CryptographicException("The private key is encrypted and the identity names no password reference.");
                        key.ImportFromEncryptedPem(text, password);
                    }
                    else key.ImportFromPem(text);
                }
                catch { key.Dispose(); throw; }
                return new LoadedSigningKey(leaf, key, chain);
            }
            case SigningKeySources.PfxFile:
            {
                var pfx = X509CertificateLoader.LoadPkcs12FromFile(CheckedPath(r.KeyReference, options), password, OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
                if (!Sha256(pfx).Equals(r.Thumbprint, StringComparison.Ordinal)) { pfx.Dispose(); throw new CryptographicException("The PKCS#12 file holds a different certificate than the identity."); }
                AsymmetricAlgorithm? key = (AsymmetricAlgorithm?)pfx.GetRSAPrivateKey() ?? pfx.GetECDsaPrivateKey();
                if (key is null) { pfx.Dispose(); throw new CryptographicException("The PKCS#12 file holds no usable private key."); }
                return new LoadedSigningKey(leaf, key, chain, pfx);
            }
            case SigningKeySources.X509Store:
            {
                var parts = r.KeyReference.Split('/', 2);
                if (parts.Length != 2 || !Enum.TryParse<StoreLocation>(parts[0], true, out var location) || !Enum.TryParse<StoreName>(parts[1], true, out var name))
                    throw new CryptographicException("The store reference must read CurrentUser/My or LocalMachine/My.");
                using var store = new X509Store(name, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                foreach (var c in store.Certificates)
                {
                    if (!Sha256(c).Equals(r.Thumbprint, StringComparison.Ordinal)) { c.Dispose(); continue; }
                    AsymmetricAlgorithm? key = (AsymmetricAlgorithm?)c.GetRSAPrivateKey() ?? c.GetECDsaPrivateKey();
                    if (key is null) { c.Dispose(); throw new CryptographicException("The store holds the certificate but gives no access to its private key."); }
                    return new LoadedSigningKey(leaf, key, chain, c);
                }
                throw new CryptographicException("The certificate is not in the named store.");
            }
            case SigningKeySources.OpensslProvider:
            {
                if (OperatingSystem.IsWindows()) throw new CryptographicException("OpenSSL provider keys are available where .NET uses OpenSSL (Linux, macOS).");
                return OpenProviderKey(r, leaf, chain);
            }
            default:
                throw new CryptographicException("Unknown key source.");
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static LoadedSigningKey OpenProviderKey(SigningKeyReference r, X509Certificate2 leaf, List<X509Certificate2> chain)
    {
        var parts = r.KeyReference.Split('|', 2);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) throw new CryptographicException("The reference must read provider|key-uri, for example pkcs11|pkcs11:token=Keel;object=sign.");
        var handle = SafeEvpPKeyHandle.OpenKeyFromProvider(parts[0], parts[1]);
        AsymmetricAlgorithm key = leaf.GetRSAPublicKey() is not null ? new RSAOpenSsl(handle) : new ECDsaOpenSsl(handle);
        return new LoadedSigningKey(leaf, key, chain, handle);
    }

    /// <summary>Proves that the key belongs to the certificate by signing a probe and verifying it with the public key of the certificate.</summary>
    public static void ProveKeyMatches(LoadedSigningKey key)
    {
        var probe = "keel-signing-probe"u8.ToArray();
        var (name, _) = key.Digest;
        var hash = new HashAlgorithmName(name);
        switch (key.PrivateKey)
        {
            case RSA rsa:
            {
                var sig = rsa.SignData(probe, hash, RSASignaturePadding.Pkcs1);
                using var pub = key.Certificate.GetRSAPublicKey() ?? throw new CryptographicException("The certificate has no RSA key.");
                if (!pub.VerifyData(probe, sig, hash, RSASignaturePadding.Pkcs1)) throw new CryptographicException("The private key does not belong to the certificate.");
                break;
            }
            case ECDsa ec:
            {
                var sig = ec.SignData(probe, hash);
                using var pub = key.Certificate.GetECDsaPublicKey() ?? throw new CryptographicException("The certificate has no ECDSA key.");
                if (!pub.VerifyData(probe, sig, hash)) throw new CryptographicException("The private key does not belong to the certificate.");
                break;
            }
            default: throw new CryptographicException("Unsupported key type.");
        }
    }

    private static string CheckedPath(string path, SigningOptions options)
    {
        var full = Path.GetFullPath(path);
        if (!Path.IsPathRooted(path)) throw new CryptographicException("Key and password files must be named with an absolute path.");
        if (options.AllowedKeyDirectory is { Length: > 0 } root)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootFull, StringComparison.Ordinal)) throw new CryptographicException("The file is outside the directory the operator allows for signing keys.");
        }
        return full;
    }

    private static string? ReadSecret(string? reference, SigningOptions options)
    {
        if (reference is null) return null;
        if (reference.StartsWith("env:", StringComparison.Ordinal))
            return Environment.GetEnvironmentVariable(reference[4..]) ?? throw new CryptographicException("The environment variable named for the key password is not set.");
        if (reference.StartsWith("file:", StringComparison.Ordinal)) return File.ReadAllText(CheckedPath(reference[5..], options)).TrimEnd('\r', '\n');
        throw new CryptographicException("A password reference must start with env: or file:.");
    }
}
