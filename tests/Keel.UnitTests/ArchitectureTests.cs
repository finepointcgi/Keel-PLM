using System.Reflection;
using System.Text.RegularExpressions;

namespace Keel.UnitTests;

/// <summary>Module boundary and access-filter rules from ADR-0001 and ADR-0004, enforced in CI.</summary>
public sealed partial class ArchitectureTests
{
    private static readonly Dictionary<string, string[]> AllowedKeelReferences = new()
    {
        ["Keel.Core"] = [],
        ["Keel.Security"] = ["Keel.Core"],
        ["Keel.Audit"] = ["Keel.Core"],
        ["Keel.Parts"] = ["Keel.Core", "Keel.Security", "Keel.Audit"],
        // Documents reads part rows only through key lookups and the marking contract; it never references Keel.Parts.
        ["Keel.Documents"] = ["Keel.Core", "Keel.Security", "Keel.Audit"],
        ["Keel.Bom"] = ["Keel.Core", "Keel.Security", "Keel.Audit"],
        ["Keel.Web"] = ["Keel.Core", "Keel.Security", "Keel.Audit", "Keel.Parts", "Keel.Documents", "Keel.Bom", "Keel.Workflow"],
        ["Keel.Workflow"] = ["Keel.Core", "Keel.Security", "Keel.Audit"],
    };

    [Fact]
    public void Modules_reference_only_allowed_modules()
    {
        foreach (var (module, allowed) in AllowedKeelReferences)
        {
            var refs = Assembly.Load(module).GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("Keel.", StringComparison.Ordinal));
            var illegal = refs.Except(allowed).ToList();
            Assert.True(illegal.Count == 0, $"{module} must not reference {string.Join(", ", illegal)}");
        }
    }

    /// <summary>
    /// Any query that lists marked rows (FROM or JOIN a marked table without a key lookup) must apply AccessPolicy.BuildReadFilter.
    /// Key lookups ("WHERE m.number = @n", "WHERE id = @id") are allowed because the caller evaluates the single object.
    /// </summary>
    [Fact]
    public void List_queries_over_marked_tables_use_the_read_filter()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            // PartWorkflowGateway loads ONE object by id for the workflow engine, which evaluates AccessPolicy on the
            // returned markings (live subject) before acting; its WHERE clause is appended by callers.
            if (file.EndsWith("PartWorkflowGateway.cs", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            foreach (Match m in SqlFromMarkedTable().Matches(text))
            {
                var statement = text.Substring(m.Index, Math.Min(600, text.Length - m.Index));
                var keyed = KeyLookup().IsMatch(statement);
                // The filter must be interpolated into this statement itself, not merely used elsewhere in the file.
                var filtered = statement.Contains("filter.Sql}", StringComparison.OrdinalIgnoreCase);
                if (!keyed && !filtered)
                    offenders.Add($"{Path.GetRelativePath(root, file)}: {m.Value}");
            }
        }
        Assert.True(offenders.Count == 0, "Unfiltered list queries over marked tables:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// Keel.Bom joins plm.part_master from structure tables as well as selecting from it. Every such statement must carry the read
    /// filter (so hidden parts become redacted placeholders) or be a key lookup of a single object the caller evaluates (ADR-0004, ADR-0017).
    /// </summary>
    [Fact]
    public void Bom_queries_that_touch_the_part_master_use_the_read_filter()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        var checkedStatements = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "Keel.Bom"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in SqlTouchesPartMaster().Matches(text))
            {
                checkedStatements++;
                var statement = text.Substring(m.Index, Math.Min(600, text.Length - m.Index));
                if (!KeyLookup().IsMatch(statement) && !statement.Contains("filter.sql}", StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{Path.GetRelativePath(root, file)}: {m.Value}");
            }
        }
        Assert.True(checkedStatements >= 8, "The rule found too few statements; the pattern is probably broken.");
        Assert.True(offenders.Count == 0, "Unfiltered BOM queries over plm.part_master:\n" + string.Join("\n", offenders));
    }

    /// <summary>ADR-0014: every routable page renders through PageFrame, which requires a marking banner.</summary>
    [Fact]
    public void Every_page_renders_through_page_frame()
    {
        var root = FindRepoRoot();
        var pages = Directory.EnumerateFiles(Path.Combine(root, "src", "Keel.Web"), "*.razor", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("@page \"", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(pages);
        var offenders = pages.Where(f =>
        {
            var text = File.ReadAllText(f);
            return !text.Contains("<PageFrame", StringComparison.Ordinal) && !text.Contains("<AccessDenied", StringComparison.Ordinal);
        }).Select(f => Path.GetRelativePath(root, f)).ToList();
        Assert.True(offenders.Count == 0, "Pages without PageFrame: " + string.Join(", ", offenders));
    }

    [GeneratedRegex(@"(FROM|JOIN)\s+(plm\.part_master|plm\.document_master|wf\.instance)\b[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex SqlFromMarkedTable();

    [GeneratedRegex(@"(FROM|JOIN)\s+plm\.part_master\b[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex SqlTouchesPartMaster();

    [GeneratedRegex(@"WHERE\s+(\w+\.)?(number|id|object_id|object_key)\s*=\s*@\w+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyLookup();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
