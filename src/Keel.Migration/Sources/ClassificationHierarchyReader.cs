using System.Text.Json;
using System.Text.Json.Serialization;
using Keel.Migration.Canonical;

namespace Keel.Migration.Sources;

/// <summary>
/// Reads the optional classification hierarchy sidecar <c>classification.json</c> (format <c>keel-classification/1</c>, docs/migration/classification-format.md) from
/// the root of any package, whatever its source format. It is part of the package, so the package hash covers it. The canonical package may carry the same nodes
/// inline as "classNodes".
/// </summary>
public static class ClassificationHierarchyReader
{
    public const string FileName = "classification.json";
    public const string Format = "keel-classification/1";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed class Doc
    {
        public string Format { get; set; } = "";
        public List<CClassNode> Nodes { get; set; } = [];
    }

    public static async Task ReadAsync(PackageSource package, StagingSink sink, CancellationToken ct)
    {
        if (!package.Exists(FileName)) return;
        Doc? doc;
        try
        {
            await using var s = package.OpenRead(FileName);
            doc = await JsonSerializer.DeserializeAsync<Doc>(s, Options, ct);
        }
        catch (JsonException e)
        {
            sink.Report(Keel.Migration.Canonical.Severity.Error, "SRC-PARSE", "package", FileName, $"classification.json is not a valid hierarchy document: {e.Message}", "Use the keel-classification/1 JSON format.");
            return;
        }
        if (doc is null || doc.Format != Format)
        {
            sink.Report(Keel.Migration.Canonical.Severity.Error, "SRC-CLASS-FORMAT", "package", FileName, $"classification.json must have format '{Format}'.");
            return;
        }
        foreach (var n in doc.Nodes) sink.Add(n);
    }
}
