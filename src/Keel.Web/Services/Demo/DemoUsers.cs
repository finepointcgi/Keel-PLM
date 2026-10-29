using System.Collections.Frozen;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;

namespace Keel.Web.Services.Demo;

/// <summary>One synthetic person of the demo, read from the dev realm file (the same file the Keycloak dev stack imports).</summary>
public sealed record DemoUser(
    string Username, string Subject, string FirstName, string LastName, string Email, IReadOnlyList<string> Roles,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes, string WhatToTry)
{
    public string DisplayName => $"{FirstName} {LastName}";

    public string Attribute(string key) => Attributes.TryGetValue(key, out var v) && v.Count > 0 ? v[0] : "";

    public string Summary
    {
        get
        {
            var programs = Attributes.TryGetValue("programs", out var p) && p.Count > 0 ? string.Join(", ", p) : "no programs";
            var us = string.Equals(Attribute("us_person"), "true", StringComparison.OrdinalIgnoreCase) ? "U.S. person" : $"foreign person ({Attribute("citizenship")})";
            return $"{us}, org {Attribute("org")}, {programs}";
        }
    }
}

/// <summary>
/// The demo identities and the principal a demo sign-in produces. The claims are exactly the claims the dev realm's mappers put in the ID token
/// (<c>sub</c>, <c>preferred_username</c>, <c>name</c>, <c>email</c>, <c>us_person</c>, <c>citizenship</c>, <c>org</c>, <c>programs</c>,
/// <c>export_authorizations</c>, <c>cui_categories</c>, <c>roles</c>, <c>auth_time</c>), so policy, markings, the idle lock and the audit log behave as with a real sign-in.
/// </summary>
public static class DemoUsers
{
    /// <summary>The integration account is a service identity, not a person: it cannot sign in to the UI.</summary>
    private static readonly string[] Excluded = ["erp-service"];

    private static readonly Dictionary<string, string> Guidance = new(StringComparer.Ordinal)
    {
        ["alice"] = "Author on ORCA. Open a part, edit it, open the Impact map tab, upload a document, start a change.",
        ["bob"] = "Approver on ORCA. Open the task inbox, review and sign (the signature asks you to confirm it is you).",
        ["carmen"] = "Manufacturing author, no program. See the MBOM views and how ORCA items stay hidden from you.",
        ["farid"] = "Foreign partner with a TAA authorization. Sees the partner interface items but not other ITAR data.",
        ["lena"] = "Foreign contractor with no authorization. ITAR items are hidden: compare with alice.",
        ["audrey"] = "Auditor. Read the audit log and verify the hash chain.",
        ["ada"] = "Administrator. Lifecycles, attributes, numbering, integration and system settings.",
        ["rex"] = "Records manager. Retention schedules, legal holds and disposition.",
        ["sup-alpha"] = "External supplier (Alpha). Lands on the supplier portal and sees only its own requests.",
        ["sup-bravo"] = "External supplier (Bravo). Compare with Alpha: each sees only its own requests.",
    };

    private static readonly Lazy<IReadOnlyList<DemoUser>> All = new(Load);

    public static IReadOnlyList<DemoUser> List => All.Value;

    /// <summary>Every subject of the dev realm (including the service account): what a demo database may contain.</summary>
    public static IReadOnlySet<string> KnownSubjects { get; } = LoadKnownSubjects();

    public static DemoUser? Find(string? username) => List.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.Ordinal));

    /// <summary>The cookie principal for <paramref name="user"/>. <c>auth_time</c> is the moment of sign-in or step-up; the session claims identify the session for the idle lock.</summary>
    public static ClaimsPrincipal CreatePrincipal(DemoUser user, DateTimeOffset authTime, string sessionId, DateTimeOffset sessionExpires)
    {
        var claims = new List<Claim>
        {
            new("sub", user.Subject),
            new("preferred_username", user.Username),
            new("name", user.DisplayName),
            new("given_name", user.FirstName),
            new("family_name", user.LastName),
            new("email", user.Email),
            new("us_person", user.Attribute("us_person")),
            new("citizenship", user.Attribute("citizenship")),
            new("org", user.Attribute("org")),
        };
        foreach (var key in new[] { "programs", "export_authorizations", "cui_categories" })
            if (user.Attributes.TryGetValue(key, out var values)) claims.AddRange(values.Select(v => new Claim(key, v)));
        claims.AddRange(user.Roles.Select(r => new Claim("roles", r)));
        claims.Add(new Claim("auth_time", authTime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        claims.Add(new Claim(KeelUi.SessionExpiresClaim, sessionExpires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        claims.Add(new Claim(KeelUi.SessionIdClaim, sessionId));
        // The same name and role claim types as the OIDC sign-in (KeelUi): NameClaimType preferred_username, RoleClaimType roles.
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Demo", "preferred_username", "roles"));
    }

    private static JsonElement Realm()
    {
        using var stream = typeof(DemoUsers).Assembly.GetManifestResourceStream("keel-realm.json")
            ?? throw new InvalidOperationException("The embedded dev realm (keel-realm.json) is missing from this build.");
        return JsonDocument.Parse(stream).RootElement.Clone();
    }

    private static FrozenSet<string> LoadKnownSubjects() =>
        Realm().GetProperty("users").EnumerateArray().Select(u => u.GetProperty("id").GetString()!)
            .Append(KrakenEngineerSubject).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The seed-only engineer who authors the KRAKEN part (Keel.Seed.RealmUsers.KrakenEngineer): present in the audit log of a seeded demo.</summary>
    private const string KrakenEngineerSubject = "6b8a7c52-0d3e-5f1a-9c44-5e2f1a0b7d90";

    private static List<DemoUser> Load()
    {
        var users = new List<DemoUser>();
        foreach (var u in Realm().GetProperty("users").EnumerateArray())
        {
            var name = u.GetProperty("username").GetString()!;
            if (Excluded.Contains(name, StringComparer.Ordinal)) continue;
            var attributes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var a in u.GetProperty("attributes").EnumerateObject())
                attributes[a.Name] = [.. a.Value.EnumerateArray().Select(e => e.GetString()!)];
            users.Add(new DemoUser(
                name, u.GetProperty("id").GetString()!, u.GetProperty("firstName").GetString()!, u.GetProperty("lastName").GetString()!,
                u.GetProperty("email").GetString()!, [.. u.GetProperty("realmRoles").EnumerateArray().Select(e => e.GetString()!)], attributes,
                Guidance.GetValueOrDefault(name, "Explore.")));
        }
        return users;
    }
}
