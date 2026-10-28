using System.Collections.Immutable;
using System.Text.Json;
using Keel.Core;

namespace Keel.Seed;

/// <summary>
/// The synthetic users of the dev Keycloak realm (<c>deploy/compose/keycloak/keel-realm.json</c>, embedded at build time), as policy subjects.
/// Subject ids are the deterministic ids stored in the realm file, so the seeded audit records and directory rows line up with what the
/// real sign-in produces. Passwords are never read.
/// </summary>
public static class RealmUsers
{
    public static IReadOnlyDictionary<string, Subject> Load()
    {
        using var stream = typeof(RealmUsers).Assembly.GetManifestResourceStream("keel-realm.json")
            ?? throw new InvalidOperationException("The embedded dev realm (keel-realm.json) is missing from this build.");
        return Parse(JsonDocument.Parse(stream).RootElement);
    }

    public static IReadOnlyDictionary<string, Subject> Parse(JsonElement realm)
    {
        var users = new Dictionary<string, Subject>(StringComparer.Ordinal);
        foreach (var u in realm.GetProperty("users").EnumerateArray())
        {
            var name = u.GetProperty("username").GetString()!;
            var attrs = u.GetProperty("attributes");
            string[] Many(string key) => attrs.TryGetProperty(key, out var v) ? [.. v.EnumerateArray().Select(e => e.GetString()!)] : [];
            string? One(string key) => Many(key).FirstOrDefault();
            users[name] = new Subject
            {
                Id = u.GetProperty("id").GetString()!,
                Username = name,
                DisplayName = $"{u.GetProperty("firstName").GetString()} {u.GetProperty("lastName").GetString()}",
                UsPerson = string.Equals(One("us_person"), "true", StringComparison.OrdinalIgnoreCase),
                Citizenship = One("citizenship"),
                Org = One("org"),
                Programs = Many("programs").ToImmutableSortedSet(StringComparer.Ordinal),
                ExportAuthorizations = Many("export_authorizations").ToImmutableSortedSet(StringComparer.Ordinal),
                CuiCategories = Many("cui_categories").ToImmutableSortedSet(StringComparer.Ordinal),
                Roles = [.. u.GetProperty("realmRoles").EnumerateArray().Select(e => e.GetString()!)],
            };
        }
        return users;
    }

    /// <summary>Returns the subject with an <c>auth_time</c> of now, which is what a fresh IdP login (step-up) produces.</summary>
    public static Subject Fresh(Subject s) => s with { AuthTime = DateTimeOffset.UtcNow };

    /// <summary>
    /// A seed-only engineer who is on both ORCA and KRAKEN. The realm has no one on KRAKEN, which is the point: the part this
    /// person owns is invisible to every realm user. Not a login; it exists only as an author in the audit log.
    /// </summary>
    public static Subject KrakenEngineer { get; } = new()
    {
        Id = "6b8a7c52-0d3e-5f1a-9c44-5e2f1a0b7d90",
        Username = "kraken-eng",
        DisplayName = "Kai Kraken (seed only)",
        UsPerson = true,
        Citizenship = "USA",
        Org = "ENG",
        Programs = ["KRAKEN", "ORCA"],
        CuiCategories = ["CTI", "EXPT"],
        Roles = [Roles.Author, Roles.Viewer],
    };
}
