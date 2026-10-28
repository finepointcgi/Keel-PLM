using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keel.Migration.Mapping;

/// <summary>
/// The mapping file (JSON, comments and trailing commas allowed; version 1). Everything the importer decides is either in this file or
/// reported as an error: unknown lifecycle states, units and users are errors unless a default is configured, and objects without
/// determinable markings are errors unless <see cref="MarkingsMapping.DefaultMarkings"/> is configured (ADR-0023, docs/migration/mapping-reference.md).
/// </summary>
public sealed class MappingConfig
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public string? Name { get; set; }
    /// <summary>When set, must equal the package's source system (guards against loading a mapping built for another system).</summary>
    public string? SourceSystem { get; set; }
    /// <summary>"batch" (start/end + per-entity summaries) or "object" (additionally one audit event per imported object).</summary>
    public string AuditGranularity { get; set; } = "batch";
    public LifecycleMapping Lifecycle { get; set; } = new();
    /// <summary>Documents use <see cref="Lifecycle"/> unless this is set.</summary>
    public LifecycleMapping? DocumentLifecycle { get; set; }
    public RevisionMapping Revisions { get; set; } = new();
    public ValueMap PartTypes { get; set; } = new() { Default = "Component" };
    public bool AssemblyWhenHasBom { get; set; } = true;
    public ValueMap PartSources { get; set; } = new() { Default = "Make" };
    public ValueMap Units { get; set; } = new();
    public ValueMap DocumentTypes { get; set; } = new() { Default = "Other" };
    public AttributeMapping Attributes { get; set; } = new();
    public UserMapping Users { get; set; } = new();
    public MarkingsMapping Markings { get; set; } = new();
    public ChangeMapping Changes { get; set; } = new();
    public ClassificationMapping Classification { get; set; } = new();
    public LimitsMapping Limits { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Parses a mapping file; unknown members are errors so a typo in a rule can never silently weaken it.</summary>
    public static MappingConfig Parse(string json)
    {
        try
        {
            var cfg = JsonSerializer.Deserialize<MappingConfig>(json, Json) ?? throw new MappingException("The mapping file is empty.");
            cfg.Normalize();
            return cfg;
        }
        catch (JsonException e)
        {
            throw new MappingException($"The mapping file is not valid: {e.Message}", e);
        }
    }

    /// <summary>The JSON reader creates case-sensitive dictionaries; the mapping is case-insensitive on source names.</summary>
    public void Normalize()
    {
        static Dictionary<string, T> Ci<T>(Dictionary<string, T> d) => new(d, StringComparer.OrdinalIgnoreCase);
        Lifecycle.States = Ci(Lifecycle.States);
        if (DocumentLifecycle is not null) DocumentLifecycle.States = Ci(DocumentLifecycle.States);
        PartTypes.Map = Ci(PartTypes.Map);
        PartSources.Map = Ci(PartSources.Map);
        Units.Map = Ci(Units.Map);
        DocumentTypes.Map = Ci(DocumentTypes.Map);
        Attributes.Map = Ci(Attributes.Map);
        Users.Map = Ci(Users.Map);
        Changes.Types.Map = Ci(Changes.Types.Map);
        Changes.ClassMap = Ci(Changes.ClassMap);
        Changes.States = new Dictionary<string, Dictionary<string, string>>(Changes.States.ToDictionary(k => k.Key, k => Ci(k.Value)), StringComparer.OrdinalIgnoreCase);
    }

    public static MappingConfig Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>SHA-256 of the canonical (re-serialized, comment-free) mapping: whitespace and comments do not change it.</summary>
    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Json))));
}

public sealed class MappingException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class LifecycleMapping
{
    /// <summary>Source state name (case-insensitive) to Keel state (InWork, UnderReview, Released, Obsolete).</summary>
    public Dictionary<string, string> States { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Keel state for source states and revisions without a state; unset means such revisions are errors.</summary>
    public string? Default { get; set; }
}

public sealed class RevisionMapping
{
    /// <summary>"auto" (created dates when all distinct, else natural label order, else ambiguous = error), "source", "created", "label".</summary>
    public string Order { get; set; } = "auto";
    /// <summary>"sequence" (position k becomes ASME label k: A, B, C ...) or "keepWhenValid" (keep the source label when all labels of the item are valid ASME labels in increasing order).</summary>
    public string Policy { get; set; } = "sequence";
    /// <summary>Item number to the source labels in their true order. Overrides <see cref="Order"/> for that item and resolves ambiguity.</summary>
    public Dictionary<string, List<string>> Explicit { get; set; } = [];
}

public sealed class ValueMap
{
    /// <summary>Source value (case-insensitive) to Keel value.</summary>
    public Dictionary<string, string> Map { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Default { get; set; }
}

public sealed class AttributeMapping
{
    /// <summary>Source property name (case-insensitive) to the name of a Keel attribute definition for parts.</summary>
    public Dictionary<string, string> Map { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Properties without a mapping are kept in legacy_properties (true, the only supported value) and reported.</summary>
    public bool KeepUnmapped { get; set; } = true;
}

public sealed class UserMapping
{
    /// <summary>Source user name (case-insensitive) to a Keel username.</summary>
    public Dictionary<string, string> Map { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Keel username for source users without a mapping (flagged in the report); unset means unmapped users are errors.</summary>
    public string? Default { get; set; } = "migration";
}

public sealed class MarkingsMapping
{
    public List<MarkingRule> Rules { get; set; } = [];
    /// <summary>Used only for objects to which no rule assigns an export jurisdiction. Imported objects that use it are listed for review.</summary>
    public MarkingSpec? DefaultMarkings { get; set; }
}

public sealed class MarkingRule
{
    public string Name { get; set; } = "";
    /// <summary>"part", "document", "change"; empty means all.</summary>
    public List<string> AppliesTo { get; set; } = [];
    public MarkingCondition When { get; set; } = new();
    public MarkingSpec Set { get; set; } = new();
}

public sealed class MarkingCondition
{
    public string? Property { get; set; }
    [JsonPropertyName("equals")]
    public string? EqualTo { get; set; }
    public List<string>? In { get; set; }
    public string? Matches { get; set; }
    public bool? Exists { get; set; }
    /// <summary>All of these must hold as well (an AND of conditions).</summary>
    public List<MarkingCondition>? All { get; set; }
}

public sealed class MarkingSpec
{
    public string? ExportJurisdiction { get; set; }
    public string? Eccn { get; set; }
    public string? UsmlCategory { get; set; }
    public List<string>? Cui { get; set; }
    public string? DistributionStatement { get; set; }
    public string? ControllingOffice { get; set; }
    public string? DistributionReason { get; set; }
    public string? Program { get; set; }
    /// <summary>Take the program from this source property.</summary>
    public string? ProgramFromProperty { get; set; }
    public string? OwningOrg { get; set; }
    public List<string>? ExportAuthorizations { get; set; }
}

public sealed class ChangeMapping
{
    /// <summary>Source change type to PR, ECR or ECN.</summary>
    public ValueMap Types { get; set; } = new();
    /// <summary>Per Keel change type (PR/ECR/ECN): source state to Keel state of that lifecycle.</summary>
    public Dictionary<string, Dictionary<string, string>> States { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Change class (I or II) for source changes without one; unset means a missing class on an ECN is an error.</summary>
    public string? DefaultClass { get; set; }
    public string DefaultPriority { get; set; } = "Medium";
    /// <summary>Source class value to I or II.</summary>
    public Dictionary<string, string> ClassMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ClassificationMapping
{
    /// <summary>true is refused until classification services exist (Phase 2b): NotSupportedException.</summary>
    public bool Load { get; set; }
}

public sealed class LimitsMapping
{
    public int MaxPropertyValueChars { get; set; } = 65536;
    public int MaxLegacyJsonChars { get; set; } = 1_000_000;
    public int MaxUsagesPerIteration { get; set; } = 5000;
}
