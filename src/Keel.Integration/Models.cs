using System.Collections.Immutable;
using System.Data.Common;
using System.Text.Json.Nodes;
using Keel.Core;
using Keel.Security;

namespace Keel.Integration;

public static class SinkKinds
{
    public const string Webhook = "webhook";
    public const string FileDrop = "filedrop";
    public const string Broker = "broker";
    public static readonly IReadOnlyList<string> All = [Webhook, FileDrop, Broker];
}

public static class DeliveryStatus
{
    public const string Pending = "pending";
    public const string Delivered = "delivered";
    public const string Failed = "failed";
    public const string Withheld = "withheld";
    public const string Dead = "dead";
    public static readonly IReadOnlyList<string> All = [Pending, Delivered, Failed, Withheld, Dead];
}

/// <summary>A clearance profile as stored. Also the recipient profile of a TDP export.</summary>
public sealed record ProfileRecord(
    Guid Id, string Name, string Label, string Description, string RecipientCategory, bool UsPersonsOnly,
    IReadOnlyList<string> AllowedJurisdictions, IReadOnlyList<string> AllowedEccns, IReadOnlyList<string> AllowedUsml,
    IReadOnlyList<string> AllowedCui, IReadOnlyList<string> AllowedPrograms, IReadOnlyList<string> AllowedStatements,
    IReadOnlyList<string> ExportAuthorizations, bool Active, int Revision)
{
    public const string Columns =
        "p.id, p.name, p.label, p.description, p.recipient_category, p.us_persons_only, p.allowed_jurisdictions, p.allowed_eccns, p.allowed_usml, " +
        "p.allowed_cui, p.allowed_programs, p.allowed_statements, p.export_authorizations, p.active, p.revision";

    public static ProfileRecord Read(DbDataReader r, int o = 0) => new(
        r.GetGuid(o), r.GetString(o + 1), r.GetString(o + 2), r.GetString(o + 3), r.GetString(o + 4), r.GetBoolean(o + 5),
        (string[])r.GetValue(o + 6), (string[])r.GetValue(o + 7), (string[])r.GetValue(o + 8), (string[])r.GetValue(o + 9),
        (string[])r.GetValue(o + 10), (string[])r.GetValue(o + 11), (string[])r.GetValue(o + 12), r.GetBoolean(o + 13), r.GetInt32(o + 14));

    private static ImmutableSortedSet<string> Set(IEnumerable<string> v) => v.ToImmutableSortedSet(StringComparer.Ordinal);

    public ClearanceProfile ToProfile() => new(
        Name, Enum.Parse<RecipientCategory>(RecipientCategory), UsPersonsOnly, Set(AllowedJurisdictions), Set(AllowedEccns), Set(AllowedUsml),
        Set(AllowedCui), Set(AllowedPrograms), AllowedStatements.Select(Enum.Parse<DistributionStatement>).ToImmutableSortedSet(), Set(ExportAuthorizations));
}

/// <summary>Input of a create or update of a clearance profile. Codes are normalized (trimmed; CUI, statements and authorizations upper-case).</summary>
public sealed record ProfileInput(
    string Name, string Label, string? Description, string RecipientCategory, bool UsPersonsOnly,
    IReadOnlyList<string> AllowedJurisdictions, IReadOnlyList<string>? AllowedEccns, IReadOnlyList<string>? AllowedUsml,
    IReadOnlyList<string>? AllowedCui, IReadOnlyList<string>? AllowedPrograms, IReadOnlyList<string> AllowedStatements,
    IReadOnlyList<string>? ExportAuthorizations, bool Active = true);

public sealed record SinkRecord(
    Guid Id, string Name, string Label, string Kind, bool Enabled, Guid ProfileId, string ProfileName, JsonObject Config,
    IReadOnlyList<string> EventTypes, bool FullStructure, int Revision)
{
    public const string Columns = "s.id, s.name, s.label, s.kind, s.enabled, s.profile_id, p.name, s.config::text, s.event_types, s.full_structure, s.revision";

    public static SinkRecord Read(DbDataReader r, int o = 0) => new(
        r.GetGuid(o), r.GetString(o + 1), r.GetString(o + 2), r.GetString(o + 3), r.GetBoolean(o + 4), r.GetGuid(o + 5), r.GetString(o + 6),
        JsonNode.Parse(r.GetString(o + 7))!.AsObject(), (string[])r.GetValue(o + 8), r.GetBoolean(o + 9), r.GetInt32(o + 10));

    public string? ConfigString(string key) => Config[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public int? ConfigInt(string key) => Config[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}

public sealed record SinkInput(
    string Name, string Label, string Kind, bool Enabled, string ProfileName, JsonObject Config, IReadOnlyList<string>? EventTypes = null, bool FullStructure = false);

/// <summary>One row of the outbox with everything needed to deliver or show it.</summary>
public sealed record OutboxRow(
    Guid Id, long Seq, string EventType, int EventVersion, string AggregateType, Guid? AggregateId, string AggregateNumber, string? AggregateRevision,
    DateTimeOffset OccurredAt, string PayloadJson, Markings Markings, string? CorrelationId, string Actor)
{
    public const string Columns =
        "o.id, o.seq, o.event_type, o.event_version, o.aggregate_type, o.aggregate_id, o.aggregate_number, o.aggregate_revision, o.occurred_at, o.payload::text, " +
        "o.correlation_id, o.actor";

    /// <summary>Reads the columns of <see cref="Columns"/> followed by the ten marking columns.</summary>
    public static OutboxRow Read(DbDataReader r, int o = 0) => new(
        r.GetGuid(o), r.GetInt64(o + 1), r.GetString(o + 2), r.GetInt32(o + 3), r.GetString(o + 4), r.IsDBNull(o + 5) ? null : r.GetGuid(o + 5), r.GetString(o + 6),
        r.IsDBNull(o + 7) ? null : r.GetString(o + 7), r.GetFieldValue<DateTimeOffset>(o + 8), r.GetString(o + 9), MarkingColumns.Read(r, o + 12),
        r.IsDBNull(o + 10) ? null : r.GetString(o + 10), r.GetString(o + 11));
}
