using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Keel.Audit;

namespace Keel.Records;

/// <summary>
/// Signs certificates of destruction, destruction confirmations and archive manifests with the audit checkpoint key (ADR-0005 pattern, ADR-0041): ECDSA P-256 over a
/// domain-separated message, so a signature made for one purpose can never be replayed as another (or as an audit checkpoint). The database never holds the key.
/// Verification uses the trusted public keys the audit verifier uses. Without a configured key nothing destructive runs: <see cref="Available"/> is false.
/// </summary>
public sealed class RecordsSigner(CheckpointSigner? signer, TrustedCheckpointKeys trusted)
{
    public const string CertificateDomain = "keel-records-certificate/v1";
    public const string ConfirmationDomain = "keel-records-confirmation/v1";
    public const string ArchiveDomain = "keel-records-archive/v1";

    public bool Available => signer is not null;
    public string KeyId => signer?.KeyId ?? throw new InvalidOperationException("No checkpoint signing key is configured (Keel:Audit:CheckpointPrivateKeyPath).");

    public static byte[] Message(string domain, string subjectHashHex, string keyId) =>
        Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{domain}\n{subjectHashHex}\n{keyId}"));

    public byte[] Sign(string domain, string subjectHashHex) =>
        (signer ?? throw new InvalidOperationException("No checkpoint signing key is configured (Keel:Audit:CheckpointPrivateKeyPath)."))
            .SignMessage(Message(domain, subjectHashHex, signer.KeyId));

    /// <summary>True when a trusted key with this id verifies the signature. <paramref name="keyKnown"/> says whether any trusted key has the id.</summary>
    public bool Verify(string domain, string subjectHashHex, string keyId, byte[] signature, out bool keyKnown) => Verify(trusted.Keys, domain, subjectHashHex, keyId, signature, out keyKnown);

    public static bool Verify(IEnumerable<ECDsa> keys, string domain, string subjectHashHex, string keyId, byte[] signature, out bool keyKnown)
    {
        keyKnown = false;
        foreach (var key in keys)
        {
            if (CheckpointKeys.KeyId(key) != keyId) continue;
            keyKnown = true;
            try
            {
                if (key.VerifyData(Message(domain, subjectHashHex, keyId), signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return true;
            }
            catch (CryptographicException) { /* a malformed signature is a failed verification */ }
        }
        return false;
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
