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
    /// <summary>
    /// The authentication context class reference (<c>acr</c>) the IdP released for this sign-in, for example "2" for a client certificate
    /// (CAC/PIV) sign-in at the Keycloak flow of ADR-0058. Null when the IdP did not release it. Used only to require a stronger
    /// authentication for e-signatures (<c>Keel:Auth:StepUpAcr</c>); it grants no access by itself.
    /// </summary>
    public string? Acr { get; init; }
}

/// <summary>Role names released by the IdP in the <c>roles</c> claim.</summary>
public static class Roles
{
    public const string Viewer = "plm-viewer";
    public const string Author = "plm-author";
    public const string Approver = "plm-approver";
    public const string Admin = "plm-admin";
    public const string Auditor = "plm-auditor";
    /// <summary>
    /// Service accounts of connected systems (ERP reconciliation, Phase 4). Deliberately not in <see cref="All"/>: it grants the integration pull
    /// API and read access under the account's own markings, and no access to the interactive API or the UI.
    /// </summary>
    public const string Integration = "plm-integration";
    /// <summary>
    /// An external supplier's user (8.2 supplier portal). Deliberately not in <see cref="All"/> and not granted any <c>PlmAction</c>: the role opens the portal
    /// pages and API only, scoped to the one supplier the user is linked to, and no internal object can be read with it (the access policy refuses it).
    /// </summary>
    public const string Supplier = "plm-supplier";
    /// <summary>
    /// Records management (8.6): may manage retention schedules, legal holds, disposition and archives. Deliberately not in <see cref="All"/> and not granted any
    /// <c>PlmAction</c>: it opens the records pages and API only; what the holder may SEE of the objects is still decided by the viewer role and the markings.
    /// </summary>
    public const string RecordsManager = "plm-records";

    /// <summary>The roles of people. <see cref="Integration"/> is a service role and is not part of this set.</summary>
    public static readonly ImmutableArray<string> All = [Viewer, Author, Approver, Admin, Auditor];
}
