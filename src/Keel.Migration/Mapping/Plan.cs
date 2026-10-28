using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Keel.Core;
using Keel.Migration.Canonical;
using Npgsql;

namespace Keel.Migration.Mapping;

/// <summary>An admin-defined attribute of parts (table plm.attribute_definition).</summary>
public sealed record AttributeDef(string Name, string DataType, IReadOnlyList<string>? EnumValues, bool Required, int? MaxLength);

/// <summary>
/// What the mapping is checked against: Keel's lifecycles, attribute definitions and user directory. Loaded from the target database,
/// or the built-in defaults of migrations 0002/0003/0007 for offline validation (reported as such).
/// </summary>
public sealed class ReferenceData
{
    public bool FromDatabase { get; init; }
    public Dictionary<string, HashSet<string>> LifecycleStates { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, AttributeDef> PartAttributes { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, (string Sub, string Username)> Directory { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static readonly string[] PartTypes = ["Assembly", "Component", "Standard", "RawMaterial"];
    public static readonly string[] DocumentTypes = ["Specification", "Drawing", "CAD", "Report", "Procedure", "Other"];

    public static ReferenceData Defaults() => new()
    {
        LifecycleStates =
        {
            ["part-default"] = ["InWork", "UnderReview", "Released", "Obsolete"],
            ["document-default"] = ["InWork", "UnderReview", "Released", "Obsolete"],
            ["pr-default"] = ["Open", "Evaluating", "Resolved", "Rejected", "Closed"],
            ["ecr-default"] = ["Draft", "UnderReview", "Approved", "Rejected", "Implemented", "Closed"],
            ["ecn-default"] = ["Draft", "UnderReview", "Released", "Implemented", "Cancelled"],
        },
        PartAttributes =
        {
            ["material"] = new("material", "string", null, false, 120),
            ["finish"] = new("finish", "string", null, false, 120),
            ["mass"] = new("mass", "number", null, false, null),
            ["cage_code"] = new("cage_code", "string", null, false, 5),
            ["critical_item"] = new("critical_item", "boolean", null, false, null),
            ["criticality"] = new("criticality", "enum", ["Standard", "Major", "Critical safety"], false, null),
        },
    };

    public static async Task<ReferenceData> LoadAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        var rd = new ReferenceData { FromDatabase = true };
        await using (var c = ds.CreateCommand("SELECT lifecycle_id, state FROM plm.lifecycle_state"))
        await using (var r = await c.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                if (!rd.LifecycleStates.TryGetValue(r.GetString(0), out var set)) rd.LifecycleStates[r.GetString(0)] = set = [];
                set.Add(r.GetString(1));
            }
        await using (var c = ds.CreateCommand("SELECT name, data_type, enum_values, required, max_length FROM plm.attribute_definition WHERE object_type = 'part'"))
        await using (var r = await c.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                rd.PartAttributes[r.GetString(0)] = new AttributeDef(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetFieldValue<string[]>(2), r.GetBoolean(3), r.IsDBNull(4) ? null : r.GetInt32(4));
        await using (var c = ds.CreateCommand("SELECT username, sub FROM identity.user_directory"))
        await using (var r = await c.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) rd.Directory[r.GetString(0)] = (r.GetString(1), r.GetString(0));
        return rd;
    }
}

// ------------------------------------------------------------------------------------------------ the plan (mapped, validated objects)
public sealed class Plan
{
    public string SourceSystem { get; set; } = "";
    public List<PPart> Parts { get; set; } = [];
    public List<PDocument> Documents { get; set; } = [];
    public List<PChange> Changes { get; set; } = [];
    public List<ClassAssignmentPlan> Classifications { get; set; } = [];
    public List<Problem> Problems { get; set; } = [];
    /// <summary>(entity type, top-level source id) of every object with at least one error: these are not imported.</summary>
    public HashSet<string> Failed { get; set; } = new(StringComparer.Ordinal);
    public List<DefaultedMarking> Defaulted { get; set; } = [];
    public List<RevisionMapEntry> RevisionMaps { get; set; } = [];
    public SortedDictionary<string, long> UnmappedProperties { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> UserFallbacks { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> Decisions { get; set; } = new(StringComparer.Ordinal);
    public List<string> Notes { get; set; } = [];
    /// <summary>Errors found by the reader (SRC-*): they block the whole package, they are not attributable to one object.</summary>
    public int SourceErrors { get; set; }

    public static string Key(string entityType, string sourceId) => entityType + "\u001f" + sourceId;
    public bool IsFailed(string entityType, string sourceId) => Failed.Contains(Key(entityType, sourceId));
}

/// <summary>Numbers that already exist in Keel and belong to other objects (not to the same source object): importing them would collide.</summary>
public sealed class ExistingKeys
{
    public HashSet<string> PartNumbers { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> DocumentNumbers { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> ChangeNumbers { get; init; } = new(StringComparer.Ordinal);
}

public sealed record DefaultedMarking(string EntityType, string SourceId, string Number, string Banner);
public sealed record RevisionMapEntry(string ItemNumber, string SourceLabel, string KeelLabel, int Position);
public sealed record ClassAssignmentPlan(string ItemSourceId, string PartNumber, string ClassCode, Dictionary<string, string?> Attributes);

public sealed class PPart
{
    public string SourceId { get; set; } = "";
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public string PartType { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Source { get; set; } = "";
    public Markings Markings { get; set; } = null!;
    public bool Defaulted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public string LegacyJson { get; set; } = "{}";
    public List<PRevision> Revisions { get; set; } = [];
}

public sealed class PRevision
{
    public string SourceId { get; set; } = "";
    public string Label { get; set; } = "";
    public string LegacyLabel { get; set; } = "";
    public string State { get; set; } = "";
    public DateTimeOffset StateChangedAt { get; set; }
    public string StateChangedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public string LegacyJson { get; set; } = "{}";
    public List<PIteration> Iterations { get; set; } = [];
}

public sealed class PIteration
{
    public int Sequence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public string? Comment { get; set; }
    public string Description { get; set; } = "";
    public string AttributesJson { get; set; } = "{}";
    public string LegacyJson { get; set; } = "{}";
    public List<PUsage> Usages { get; set; } = [];
    public List<PLink> Links { get; set; } = [];
}

public sealed class PUsage
{
    public string ChildSourceId { get; set; } = "";
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "";
    public int FindNumber { get; set; }
    public List<string> Designators { get; set; } = [];
    public string? Notes { get; set; }
}

public sealed class PLink
{
    public string Type { get; set; } = "";
    public string DocumentSourceId { get; set; } = "";
    /// <summary>Keel label of the pinned document revision (describes), null for references.</summary>
    public string? DocumentRevisionLabel { get; set; }
}

public sealed class PDocument
{
    public string SourceId { get; set; } = "";
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public string DocumentType { get; set; } = "";
    public Markings Markings { get; set; } = null!;
    public bool Defaulted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public string LegacyJson { get; set; } = "{}";
    public List<PDocRevision> Revisions { get; set; } = [];
}

public sealed class PDocRevision
{
    public string SourceId { get; set; } = "";
    public string Label { get; set; } = "";
    public string LegacyLabel { get; set; } = "";
    public string State { get; set; } = "";
    public DateTimeOffset StateChangedAt { get; set; }
    public string StateChangedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public string LegacyJson { get; set; } = "{}";
    public List<PDocIteration> Iterations { get; set; } = [];
}

public sealed class PDocIteration
{
    public int Sequence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public string? Comment { get; set; }
    public string Description { get; set; } = "";
    public string LegacyJson { get; set; } = "{}";
    public List<PFile> Files { get; set; } = [];
}

public sealed class PFile
{
    public string Name { get; set; } = "";
    public string MediaType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string PackagePath { get; set; } = "";
    public bool Primary { get; set; }
}

public sealed class PChange
{
    public string SourceId { get; set; } = "";
    /// <summary>PR, ECR or ECN.</summary>
    public string ChangeType { get; set; } = "";
    /// <summary>Null when the source number does not fit the Keel format: the loader assigns the next number from the sequence.</summary>
    public string? Number { get; set; }
    public string LegacyNumber { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Priority { get; set; } = "";
    public string? ChangeClass { get; set; }
    public string Reason { get; set; } = "";
    public string State { get; set; } = "";
    public string? ParentSourceId { get; set; }
    public Markings Markings { get; set; } = null!;
    public bool Defaulted { get; set; }
    public bool MarkingsRaised { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset StateChangedAt { get; set; }
    public string StateChangedBy { get; set; } = "";
    public string LegacyJson { get; set; } = "{}";
    public List<PAffected> Affected { get; set; } = [];
}

public sealed class PAffected
{
    public string ItemSourceId { get; set; } = "";
    public string RevisionSourceId { get; set; } = "";
    public string Action { get; set; } = "none";
    public string? ResultingRevisionSourceId { get; set; }
}

/// <summary>Hashing of planned objects: the hash covers the mapped result, so a changed source or a changed mapping is detected on a rerun.</summary>
public static class PlanHash
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new MarkingsConverter() }, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };

    public static string Of(object plan) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan, plan.GetType(), Options))));

    public static string Of(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class MarkingsConverter : System.Text.Json.Serialization.JsonConverter<Markings>
    {
        public override Markings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, Markings v, JsonSerializerOptions options) =>
            writer.WriteStringValue($"{v.ExportJurisdiction}|{v.Eccn}|{v.UsmlCategory}|{string.Join(',', v.CuiCategories)}|{v.DistributionStatement}|{v.ControllingOffice}|{v.DistributionReason}|{v.Program}|{v.OwningOrg}|{string.Join(',', v.ExportAuthorizations)}");
    }
}
