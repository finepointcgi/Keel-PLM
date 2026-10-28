using System.Security.Cryptography;

namespace Keel.Audit;

public sealed class TrustedCheckpointKeys(IReadOnlyCollection<ECDsa> keys)
{
    public IReadOnlyCollection<ECDsa> Keys { get; } = keys;
}
