using System.Security.Cryptography;

namespace Keel.Api.Infrastructure;

public sealed class TrustedCheckpointKeys(IReadOnlyCollection<ECDsa> keys)
{
    public IReadOnlyCollection<ECDsa> Keys { get; } = keys;
}
