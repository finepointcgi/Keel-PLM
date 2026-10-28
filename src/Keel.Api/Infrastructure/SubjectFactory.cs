using System.Collections.Immutable;
using System.Security.Claims;
using Keel.Core;

namespace Keel.Api.Infrastructure;

/// <summary>Maps validated token claims to a <see cref="Subject"/>. Absent security attributes fail closed (A2).</summary>
public static class SubjectFactory
{
    public static Subject? FromPrincipal(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(sub)) return null;

        return new Subject
        {
            Id = sub,
            DisplayName = principal.FindFirst("name")?.Value ?? principal.FindFirst("preferred_username")?.Value ?? sub,
            // ASSUMPTION(A2): U.S.-person status comes only from the explicit claim, not inferred from citizenship
            // (lawful permanent residents are U.S. persons under ITAR 120.62).
            UsPerson = string.Equals(principal.FindFirst("us_person")?.Value, "true", StringComparison.OrdinalIgnoreCase),
            Citizenship = principal.FindFirst("citizenship")?.Value,
            Org = principal.FindFirst("org")?.Value,
            Programs = Set(principal, "programs", upper: false),
            ExportAuthorizations = Set(principal, "export_authorizations", upper: true),
            CuiCategories = Set(principal, "cui_categories", upper: true),
            Roles = Set(principal, "roles", upper: false),
        };
    }

    public static Subject Require(HttpContext context) =>
        FromPrincipal(context.User) ?? throw new InvalidOperationException("Endpoint requires an authenticated subject.");

    private static ImmutableSortedSet<string> Set(ClaimsPrincipal p, string type, bool upper) =>
        p.FindAll(type)
            .Select(c => c.Value.Trim())
            .Where(v => v.Length > 0)
            .Select(v => upper ? v.ToUpperInvariant() : v)
            .ToImmutableSortedSet(StringComparer.Ordinal);
}
