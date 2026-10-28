using System.Text.Json;
using Keel.Migration.Canonical;

namespace Keel.Migration.Sources;

/// <summary>
/// Reader and writer of the neutral "Keel canonical package": <c>package.json</c> (a serialized <see cref="CanonicalPackage"/>) plus a
/// files directory referenced by <see cref="CFile.PackagePath"/>. Large exports may add line-delimited <c>items.jsonl</c>,
/// <c>documents.jsonl</c>, <c>changes.jsonl</c>, <c>classifications.jsonl</c>, <c>users.jsonl</c> (one object per line), which are read as a stream.
/// Unknown members are errors, never silently ignored.
/// </summary>
public sealed class CanonicalPackageReader : IPackageReader
{
    public const string FileName = "package.json";
    public string Format => "canonical";
    public string DefaultSourceSystem => "canonical";

    private static readonly JsonSerializerOptions Strict = new(CanonicalPackage.Json) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public async Task ReadAsync(PackageSource package, StagingSink sink, CancellationToken ct)
    {
        CanonicalPackage header;
        try
        {
            await using var s = package.OpenRead(FileName);
            header = await JsonSerializer.DeserializeAsync<CanonicalPackage>(s, Strict, ct) ?? new CanonicalPackage();
        }
        catch (JsonException e)
        {
            sink.Report(Severity.Error, "SRC-PARSE", "package", FileName, $"package.json is not a valid canonical package: {e.Message}", "Fix the JSON or remove members the canonical format does not define (docs/migration/README.md).");
            return;
        }
        if (header.FormatVersion != CanonicalPackage.CurrentVersion)
        {
            sink.Report(Severity.Error, "SRC-VERSION", "package", FileName, $"Canonical format version {header.FormatVersion} is not supported (this tool reads {CanonicalPackage.CurrentVersion}).");
            return;
        }
        sink.Package.SourceSystem = header.SourceSystem;
        sink.Package.SourceInstance = header.SourceInstance;
        sink.Package.ExportedAt = header.ExportedAt;
        sink.Inventory.SourceSystem = header.SourceSystem;
        sink.Inventory.Format = Format;
        foreach (var o in header.Items) sink.Add(o, false);
        foreach (var o in header.Documents) sink.Add(o, true);
        foreach (var c in header.Changes) sink.Add(c);
        foreach (var c in header.Classifications) sink.Add(c);
        foreach (var n in header.ClassNodes) sink.Add(n);
        foreach (var u in header.Users) sink.Add(u);

        await ReadLinesAsync<CObject>(package, "items.jsonl", sink, o => sink.Add(o, false), ct);
        await ReadLinesAsync<CObject>(package, "documents.jsonl", sink, o => sink.Add(o, true), ct);
        await ReadLinesAsync<CChange>(package, "changes.jsonl", sink, sink.Add, ct);
        await ReadLinesAsync<CClassAssignment>(package, "classifications.jsonl", sink, sink.Add, ct);
        await ReadLinesAsync<CUser>(package, "users.jsonl", sink, sink.Add, ct);
    }

    private static async Task ReadLinesAsync<T>(PackageSource package, string file, StagingSink sink, Action<T> add, CancellationToken ct)
    {
        if (!package.Exists(file)) return;
        await using var s = package.OpenRead(file);
        using var reader = new StreamReader(s);
        var line = 0;
        while (await reader.ReadLineAsync(ct) is { } text)
        {
            line++;
            if (string.IsNullOrWhiteSpace(text)) continue;
            try
            {
                add(JsonSerializer.Deserialize<T>(text, Strict) ?? throw new JsonException("null"));
            }
            catch (JsonException e)
            {
                sink.Report(Severity.Error, "SRC-PARSE", "package", $"{file}:{line}", $"Line is not a valid {typeof(T).Name}: {e.Message}");
            }
        }
    }

    /// <summary>Writes a canonical package (package.json) into <paramref name="directory"/>; file bytes are the caller's job.</summary>
    public static void Write(CanonicalPackage package, string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, FileName), JsonSerializer.Serialize(package, CanonicalPackage.Json));
    }
}
