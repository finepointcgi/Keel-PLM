using System.Collections.Immutable;

namespace Keel.Core;

/// <summary>
/// The authenticated user and the security attributes access decisions are made from (ADR-0004, A2).
/// Attributes missing from the identity provider default to the most restrictive value.
/// </summary>
public sealed record Subject
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>Human-readable login name (IdP <c>preferred_username</c>); <see cref="Id"/> is the stable key.</summary>
    public string? Username { get; init; }
    public bool UsPerson { get; init; }
    public string? Citizenship { get; init; }
    public string? Org { get; init; }
    public ImmutableSortedSet<string> Programs { get; init; } = ImmutableSortedSet<string>.Empty;
    public ImmutableSortedSet<string> ExportAuthorizations { get; init; } = ImmutableSortedSet<string>.Empty;
    public ImmutableSortedSet<string> CuiCategories { get; init; } = ImmutableSortedSet<string>.Empty;
    public ImmutableSortedSet<string> Roles { get; init; } = ImmutableSortedSet<string>.Empty;
    /// <summary>
    /// When the user last authenticated at the IdP (<c>auth_time</c>), used to require recent re-authentication for
    /// electronic signatures (A9, ADR-0007). Null when the IdP did not release it; signing then always needs step-up.
    /// </summary>
    public DateTimeOffset? AuthTime { get; init; }
}

/// <summary>Role names released by the IdP in the <c>roles</c> claim.</summary>
public static class Roles
{
    public const string Viewer = "plm-viewer";
    public const string Author = "plm-author";
    public const string Approver = "plm-approver";
    public const string Admin = "plm-admin";
    public const string Auditor = "plm-auditor";

    public static readonly ImmutableArray<string> All = [Viewer, Author, Approver, Admin, Auditor];
}
