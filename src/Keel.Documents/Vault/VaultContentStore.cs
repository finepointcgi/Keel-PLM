using Keel.Core;

namespace Keel.Documents.Vault;

/// <summary>The vault as an <see cref="IContentStore"/> for the migration importer (ADR-0023). Hashing and verification stay in <see cref="ObjectVault"/>.</summary>
public sealed class VaultContentStore(ObjectVault vault) : IContentStore
{
    public async Task<ContentInfo> PutAsync(Stream content, CancellationToken ct = default)
    {
        var stored = await vault.PutAsync(content, ct);
        return new ContentInfo(stored.Sha256, stored.SizeBytes);
    }

    public Task<bool> ExistsAsync(string sha256, long sizeBytes, CancellationToken ct = default) => vault.ExistsAsync(sha256, sizeBytes, ct);

    public Task VerifyAsync(string sha256, long sizeBytes, CancellationToken ct = default) => vault.VerifyAsync(sha256, sizeBytes, ct);
}
