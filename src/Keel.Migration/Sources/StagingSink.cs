using Keel.Migration.Canonical;

namespace Keel.Migration.Sources;

/// <summary>What a reader found: counts per entity, distinct values to be mapped, unknown and ignored elements, referenced files.</summary>
public sealed class SourceInventory
{
    public string SourceSystem { get; set; } = "";
    public string Format { get; set; } = "";
    public SortedDictionary<string, long> Counts { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> ItemStates { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> DocumentStates { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> ChangeStates { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> Units { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> ItemTypes { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> DocumentTypes { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> ChangeTypes { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> Sources { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> Properties { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> Users { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Elements the reader does not interpret. Inside a known owner they are preserved as properties; top-level ones are counted only.</summary>
    public SortedDictionary<string, UnknownElement> UnknownElements { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Elements the reader recognises but deliberately does not migrate (for example geometry transforms).</summary>
    public SortedDictionary<string, long> IgnoredElements { get; set; } = new(StringComparer.Ordinal);
    public long FilesReferenced { get; set; }
    public long FilesBytesDeclared { get; set; }

    public void Bump(SortedDictionary<string, long> map, string? key, long by = 1)
    {
        key = string.IsNullOrEmpty(key) ? "(none)" : key;
        map[key] = map.GetValueOrDefault(key) + by;
    }
}

public sealed class UnknownElement
{
    public long Count { get; set; }
    public string FirstPath { get; set; } = "";
    public bool Preserved { get; set; }
}

/// <summary>
/// Receives the objects a reader produces. With <see cref="Retain"/> the objects are kept in <see cref="Package"/>; without it only
/// the inventory is computed (the streaming path of <c>inspect</c>, which keeps memory flat for very large files).
/// </summary>
public sealed class StagingSink(bool retain = true)
{
    public bool Retain { get; } = retain;
    public CanonicalPackage Package { get; } = new();
    public SourceInventory Inventory { get; } = new();
    public List<Problem> Problems { get; } = [];

    public void Add(CObject item, bool isDocument)
    {
        var inv = Inventory;
        var prefix = isDocument ? "document" : "item";
        inv.Bump(inv.Counts, isDocument ? "documents" : "items");
        inv.Bump(isDocument ? inv.DocumentTypes : inv.ItemTypes, item.Type);
        if (!isDocument)
        {
            inv.Bump(inv.Units, item.Unit);
            inv.Bump(inv.Sources, item.Source);
        }
        foreach (var k in item.Properties.Keys) inv.Bump(inv.Properties, k);
        if (item.CreatedBy is not null) inv.Bump(inv.Users, item.CreatedBy);
        foreach (var r in item.Revisions)
        {
            inv.Bump(inv.Counts, prefix + "Revisions");
            if (r.States.Count > 0) inv.Bump(isDocument ? inv.DocumentStates : inv.ItemStates, r.States[^1].Name);
            foreach (var k in r.Properties.Keys) inv.Bump(inv.Properties, k);
            foreach (var s in r.States) if (s.By is not null) inv.Bump(inv.Users, s.By);
            foreach (var it in r.Iterations)
            {
                inv.Bump(inv.Counts, prefix + "Iterations");
                foreach (var k in it.Properties.Keys) inv.Bump(inv.Properties, k);
                if (it.CreatedBy is not null) inv.Bump(inv.Users, it.CreatedBy);
                foreach (var u in it.Usages) { inv.Bump(inv.Counts, "usages"); inv.Bump(inv.Units, u.Unit); }
                inv.Bump(inv.Counts, "documentLinks", it.DocLinks.Count);
                foreach (var f in it.Files)
                {
                    inv.Bump(inv.Counts, "files");
                    inv.FilesReferenced++;
                    inv.FilesBytesDeclared += f.SizeBytes ?? 0;
                }
            }
        }
        if (Retain) (isDocument ? Package.Documents : Package.Items).Add(item);
    }

    public void Add(CChange change)
    {
        Inventory.Bump(Inventory.Counts, "changes");
        Inventory.Bump(Inventory.Counts, "affectedObjects", change.Affected.Count);
        Inventory.Bump(Inventory.ChangeStates, change.State);
        Inventory.Bump(Inventory.ChangeTypes, change.Type);
        foreach (var k in change.Properties.Keys) Inventory.Bump(Inventory.Properties, k);
        if (change.CreatedBy is not null) Inventory.Bump(Inventory.Users, change.CreatedBy);
        if (Retain) Package.Changes.Add(change);
    }

    public void Add(CClassAssignment c)
    {
        Inventory.Bump(Inventory.Counts, "classifications");
        if (Retain) Package.Classifications.Add(c);
    }

    public void Add(CUser u)
    {
        Inventory.Bump(Inventory.Counts, "users");
        if (Retain) Package.Users.Add(u);
    }

    public void Unknown(string element, string path, bool preserved)
    {
        if (!Inventory.UnknownElements.TryGetValue(element, out var u))
            Inventory.UnknownElements[element] = u = new UnknownElement { FirstPath = path, Preserved = preserved };
        u.Count++;
        u.Preserved &= preserved;
    }

    public void Ignored(string element) => Inventory.Bump(Inventory.IgnoredElements, element);

    public void Report(Severity severity, string code, string entityType, string sourceId, string message, string? hint = null) =>
        Problems.Add(new Problem(severity, code, entityType, sourceId, message, hint));
}

/// <summary>A reader for one source format.</summary>
public interface IPackageReader
{
    string Format { get; }
    string DefaultSourceSystem { get; }
    Task ReadAsync(PackageSource package, StagingSink sink, CancellationToken ct);
}

public static class PackageReaders
{
    public static readonly string[] Formats = ["canonical", "plmxml", "windchill-json", "windchill-csv"];

    public static IPackageReader Create(string format) => format switch
    {
        "canonical" => new CanonicalPackageReader(),
        "plmxml" => new PlmXmlReader(),
        "windchill-json" => new WindchillJsonReader(),
        "windchill-csv" => new WindchillCsvReader(),
        _ => throw new ArgumentException($"Unknown format '{format}'. Known formats: {string.Join(", ", Formats)}."),
    };

    public static string Detect(PackageSource package)
    {
        var files = package.ListFiles().ToList();
        bool Has(string name) => files.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
        if (Has(CanonicalPackageReader.FileName)) return "canonical";
        if (Has("parts.json") || Has("documents.json") && !Has("documents.csv") && !Has("parts.csv")) return "windchill-json";
        if (Has("parts.csv")) return "windchill-csv";
        if (files.Any(f => f.EndsWith(".plmxml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))) return "plmxml";
        throw new InvalidOperationException(
            "Cannot detect the package format. Expected package.json (canonical), parts.json (Windchill JSON), parts.csv (Windchill CSV) or *.plmxml / *.xml (PLM XML). Pass --format to choose.");
    }
}
