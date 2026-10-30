using System.Collections.Immutable;
using System.Security.Claims;
using Keel.Core;

namespace Keel.Security;

/// <summary>
/// Maps validated identity claims (JWT bearer or OIDC cookie session) to a <see cref="Subject"/>.
/// Absent security attributes fail closed (A2).
/// </summary>
public static class SubjectClaims
{
    public static Subject? FromPrincipal(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return null;
        var sub = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(sub)) return null;

        return new Subject
        {
            Id = sub,
            DisplayName = principal.FindFirst("name")?.Value ?? principal.FindFirst("preferred_username")?.Value ?? sub,
            Username = principal.FindFirst("preferred_username")?.Value,
            // ASSUMPTION(A2): U.S.-person status comes only from the explicit claim, not inferred from citizenship
            // (lawful permanent residents are U.S. persons under ITAR 120.62).
            UsPerson = string.Equals(principal.FindFirst("us_person")?.Value, "true", StringComparison.OrdinalIgnoreCase),
            Citizenship = principal.FindFirst("citizenship")?.Value,
            Org = principal.FindFirst("org")?.Value,
            Programs = Set(principal, "programs", upper: false),
            ExportAuthorizations = Set(principal, "export_authorizations", upper: true),
            CuiCategories = Set(principal, "cui_categories", upper: true),
            Roles = Set(principal, "roles", upper: false),
            AuthTime = AuthTimeOf(principal),
            Acr = principal.FindFirst("acr")?.Value is { Length: > 0 } acr ? acr : null,
        };
    }

    /// <summary><c>auth_time</c> is seconds since the Unix epoch (OIDC Core 2). Anything unparsable counts as absent.</summary>
    private static DateTimeOffset? AuthTimeOf(ClaimsPrincipal p) =>
        long.TryParse(p.FindFirst("auth_time")?.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
        && seconds > 0 && seconds < 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    private static ImmutableSortedSet<string> Set(ClaimsPrincipal p, string type, bool upper) =>
        p.FindAll(type)
            .Select(c => c.Value.Trim())
            .Where(v => v.Length > 0)
            .Select(v => upper ? v.ToUpperInvariant() : v)
            .ToImmutableSortedSet(StringComparer.Ordinal);
}
