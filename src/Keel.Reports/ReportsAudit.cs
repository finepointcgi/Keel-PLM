using Keel.Audit;
using Keel.Core;

namespace Keel.Reports;

/// <summary>Audit entries of the output module. Actions start with "output."; details carry numbers, counts and hashes, never names or marked text.</summary>
internal static class ReportsAudit
{
    public static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string? objectType, string? objectId, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s),
        Action = action,
        Outcome = outcome,
        ObjectType = objectType ?? "output",
        ObjectId = objectId,
        CorrelationId = correlationId,
        Details = AuditDetails.Of(details),
    };
}

public static class ReportsLimits
{
    public const int MaxPackageBomLines = 5000;
    public const int MaxConcurrentPackages = 2;
    public const int MaxBomExportLines = 100_000;
    public const int MaxImportRows = 5000;
    public const int MaxMassObjects = 500;
    public const int MaxMassPerHour = 20;
    public const int MaxReportRows = 5000;
    public const int MaxDefinitionsPerUser = 100;
    public const int MaxFilters = 12;
}
