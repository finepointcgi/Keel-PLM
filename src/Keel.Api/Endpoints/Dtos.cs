using Keel.Core;
using Keel.Security;

namespace Keel.Api.Endpoints;

public sealed record MarkingsDto(
    ExportJurisdiction ExportJurisdiction,
    DistributionStatement DistributionStatement,
    string OwningOrg,
    string? Eccn = null,
    string? UsmlCategory = null,
    string[]? CuiCategories = null,
    string? ControllingOffice = null,
    string? DistributionReason = null,
    string? Program = null,
    string[]? ExportAuthorizations = null)
{
    public Result<Markings> ToMarkings() => Markings.Create(ExportJurisdiction, DistributionStatement, OwningOrg, Eccn, UsmlCategory,
        CuiCategories, ControllingOffice, DistributionReason, Program, ExportAuthorizations);
}

public sealed record EvaluateRequest(MarkingsDto Markings, PlmAction Action);

public sealed record EvaluateResponse(bool Permitted, IReadOnlyList<string> Reasons, string Banner);

public sealed record MeResponse(
    string Id, string DisplayName, bool UsPerson, string? Citizenship, string? Org,
    IReadOnlyList<string> Programs, IReadOnlyList<string> ExportAuthorizations, IReadOnlyList<string> CuiCategories, IReadOnlyList<string> Roles);

public sealed record AuditEventDto(long Seq, Guid Id, DateTimeOffset OccurredAt, string Actor, string Action, string? ObjectType,
    string? ObjectId, string Outcome, string? CorrelationId, string? ActorName, string Payload, string Hash);

public sealed record ClientConfig(string Authority, string ClientId, string Audience);
