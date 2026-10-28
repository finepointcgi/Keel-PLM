using System.Collections.Immutable;

namespace Keel.Audit;

public static class AuditDetails
{
    public static ImmutableSortedDictionary<string, string?> Of(params (string Key, string? Value)[] items) =>
        items.ToImmutableSortedDictionary(i => i.Key, i => i.Value, StringComparer.Ordinal);
}
