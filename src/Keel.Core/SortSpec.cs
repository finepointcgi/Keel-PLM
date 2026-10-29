using System.Text;

namespace Keel.Core;

/// <summary>
/// A requested list order: a key from a module's whitelist and a direction (GAP-A-1). The key is only ever a lookup into a
/// <see cref="SortMap"/>; it never reaches SQL text.
/// </summary>
public sealed record SortSpec(string Key, bool Descending)
{
    /// <summary>"asc" or "desc" (any case; empty means ascending). Anything else is not a direction.</summary>
    public static bool TryParseDirection(string? direction, out bool descending)
    {
        descending = false;
        if (string.IsNullOrWhiteSpace(direction) || direction.Equals("asc", StringComparison.OrdinalIgnoreCase)) return true;
        if (direction.Equals("desc", StringComparison.OrdinalIgnoreCase)) { descending = true; return true; }
        return false;
    }

    public string DirectionText => Descending ? "desc" : "asc";
}

/// <summary>
/// The whitelist of sort keys a list query accepts. Each key maps to SQL expressions written in code (columns of rows the
/// query has ALREADY filtered with the access read filter, never aggregates over rows the caller cannot read), so ordering
/// reveals nothing beyond what the visible rows show. An unknown key is refused, never passed through.
/// </summary>
public sealed class SortMap
{
    private readonly Dictionary<string, string[]> keys;

    public SortMap(params (string Key, string[] Expressions)[] entries) =>
        keys = entries.ToDictionary(e => e.Key, e => e.Expressions, StringComparer.Ordinal);

    public IReadOnlyCollection<string> Keys => keys.Keys;

    public bool Allows(string? key) => key is not null && keys.ContainsKey(key);

    /// <summary>Parses a request's key and direction. Null when no sort was asked for; <see cref="ArgumentException"/> when the key or direction is not allowed.</summary>
    public SortSpec? Resolve(string? key, string? direction)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var k = key.Trim();
        if (!Allows(k)) throw new ArgumentException($"Cannot sort by '{k}'. Allowed: {string.Join(", ", keys.Keys.Order(StringComparer.Ordinal))}.", nameof(key));
        if (!SortSpec.TryParseDirection(direction, out var descending)) throw new ArgumentException("Direction must be asc or desc.", nameof(direction));
        return new SortSpec(k, descending);
    }

    /// <summary>Like <see cref="Resolve"/> but returns an error message instead of throwing (for the REST layer and the UI).</summary>
    public bool TryResolve(string? key, string? direction, out SortSpec? spec, out string? error)
    {
        try { spec = Resolve(key, direction); error = null; return true; }
        catch (ArgumentException e) { spec = null; error = e.Message; return false; }
    }

    /// <summary>
    /// The text after ORDER BY. Without a spec it is <paramref name="defaultOrder"/>. With one it is the whitelisted expressions in the
    /// requested direction (nulls last either way), then <paramref name="tieBreaker"/> so paging is stable.
    /// </summary>
    public string OrderBy(SortSpec? spec, string defaultOrder, string tieBreaker)
    {
        if (spec is null) return defaultOrder;
        if (!keys.TryGetValue(spec.Key, out var expressions)) throw new ArgumentException($"Cannot sort by '{spec.Key}'.", nameof(spec));
        var dir = spec.Descending ? "DESC" : "ASC";
        var sb = new StringBuilder();
        foreach (var e in expressions) sb.Append(e).Append(' ').Append(dir).Append(" NULLS LAST, ");
        return sb.Append(tieBreaker).ToString();
    }
}
