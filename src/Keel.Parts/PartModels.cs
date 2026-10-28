using System.Text.Json.Nodes;
using Keel.Core;

namespace Keel.Parts;

public static class PartTypes
{
    public static readonly IReadOnlyList<string> All = ["Assembly", "Component", "Standard", "RawMaterial"];
}

public static class PartSources
{
    public static readonly IReadOnlyList<string> All = ["Make", "Buy"];
}

public sealed record CreatePartCommand(
    string Name,
    string PartType,
    string DefaultUnit,
    string Source,
    Markings Markings,
    string? Description = null,
    JsonObject? Attributes = null,
    // ASSUMPTION(A6): explicit numbers are for migration and restricted to administrators.
    string? Number = null);

public sealed record PartSummary(
    Guid Id, string Number, string Name, string PartType, string Revision, int Iteration, string State, string Banner, Markings Markings);

public sealed record PartSearchResult(IReadOnlyList<PartSummary> Items, long Total, string PageBanner);

public sealed record RevisionView(Guid Id, string Revision, string State, string StateLabel, bool IsLatest, bool IsLocked,
    int LatestIteration, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset StateChangedAt, string StateChangedBy);

public sealed record IterationView(Guid Id, string Revision, int Iteration, string Label, string Description, JsonObject Attributes,
    string? Comment, DateTimeOffset CreatedAt, string CreatedBy);

public sealed record CheckoutView(string UserId, string? UserName, DateTimeOffset CheckedOutAt, bool IsMine,
    string? Description, JsonObject? Attributes);

public sealed record TransitionView(string To, string Label, string Via, bool Allowed, string? Reason);

public sealed record PartPermissions(bool CanModify, bool CanAdminister, IReadOnlyList<string> ModifyDeniedReasons);

public sealed record PartDetail(
    Guid Id,
    string Number,
    string Name,
    string PartType,
    string DefaultUnit,
    string Source,
    string LifecycleId,
    Markings Markings,
    string Banner,
    RevisionView Revision,
    IterationView Iteration,
    IReadOnlyList<RevisionView> Revisions,
    IReadOnlyList<IterationView> History,
    CheckoutView? Checkout,
    IReadOnlyList<TransitionView> Transitions,
    PartPermissions Permissions,
    DateTimeOffset CreatedAt,
    string CreatedBy);
