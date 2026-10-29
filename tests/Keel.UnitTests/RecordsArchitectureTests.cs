using System.Text.RegularExpressions;

namespace Keel.UnitTests;

/// <summary>Structural rules of records management (ADR-0041), enforced in CI.</summary>
public sealed partial class RecordsArchitectureTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }

    private static IEnumerable<string> Sources(string relativeDir, string pattern = "*.cs") =>
        Directory.EnumerateFiles(Path.Combine(Root, relativeDir), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [Fact]
    public void The_module_references_only_core_security_and_audit()
    {
        var text = File.ReadAllText(Path.Combine(Root, "src", "Keel.Records", "Keel.Records.csproj"));
        var refs = Regex.Matches(text, @"ProjectReference Include=""\.\.\\(?<n>Keel\.\w+)\\").Select(m => m.Groups["n"].Value).ToList();
        Assert.Equal(["Keel.Audit", "Keel.Core", "Keel.Security"], refs.Order(StringComparer.Ordinal));
    }

    [GeneratedRegex(@"(FROM|JOIN)\s+(plm\.part_master|plm\.document_master|rec\.disposition|rec\.certificate|rec\.archive|\{table\}|\{m\})\b[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex MarkedTable();

    [GeneratedRegex(@"(WHERE|AND)\s+(\w+\.)?(number|id|object_id)\s*=\s*@\w+|x\.number\s*=\s*@n|d\.number\s*=\s*@k|\.id\s*=\s*@id|\.id\s*>\s*@after|a\.package_sha256\s*=\s*@h|i\.object_id\s*=\s*c\.object_id", RegexOptions.IgnoreCase)]
    private static partial Regex KeyLookup();

    /// <summary>
    /// Every statement of the module over a table that carries markings is a key lookup, takes the caller's read filter, or is one of the listed system statements:
    /// holds are evaluated over ALL objects by design (a hold must protect what its author cannot see), the classification run is a system job, and key lookups happen
    /// after an explicit access evaluation on the loaded markings.
    /// </summary>
    [Fact]
    public void Every_list_over_a_marked_table_carries_the_read_filter_or_is_a_key_lookup_or_a_listed_system_statement()
    {
        var offenders = new List<string>();
        var checkedStatements = 0;
        foreach (var file in Sources("src/Keel.Records"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in MarkedTable().Matches(text))
            {
                checkedStatements++;
                var statement = text.Substring(m.Index, Math.Min(900, text.Length - m.Index));
                var filtered = statement.Contains("f.Sql}", StringComparison.Ordinal) || statement.Contains("filter.Sql}", StringComparison.OrdinalIgnoreCase);
                var system = statement.Contains("INSERT INTO rec.hold_member", StringComparison.Ordinal) // the hold evaluation over every object (documented)
                          || statement.Contains("x.id > @after", StringComparison.Ordinal);            // the classification pass: a system job writing to a cache
                if (!filtered && !system && !KeyLookup().IsMatch(statement)) offenders.Add($"{Path.GetRelativePath(Root, file)}: {m.Value}");
            }
        }
        Assert.True(checkedStatements >= 10, "The rule found too few statements; the pattern is probably broken.");
        Assert.True(offenders.Count == 0, "Unfiltered list queries over marked tables in Keel.Records:\n" + string.Join("\n", offenders));
    }

    [GeneratedRegex(@"\b(UPDATE|DELETE\s+FROM)\s+rec\.(?<t>\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex Mutation();

    [Fact]
    public void Records_that_must_stay_are_never_updated_or_deleted_by_the_module()
    {
        // Tables the application may change or delete rows of (drafts and derived caches); everything else is insert-only, also enforced by triggers.
        var mutable = new HashSet<string>(["schedule", "record_class", "hold", "disposition", "disposition_item", "classification"], StringComparer.Ordinal);
        var offenders = new List<string>();
        var seen = 0;
        foreach (var file in Sources("src/Keel.Records"))
            foreach (Match m in Mutation().Matches(File.ReadAllText(file)))
            {
                seen++;
                if (!mutable.Contains(m.Groups["t"].Value)) offenders.Add($"{Path.GetRelativePath(Root, file)}: {m.Value}");
            }
        Assert.True(seen >= 10);
        Assert.True(offenders.Count == 0, "Mutation of an insert-only records table:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Records_management_does_not_weaken_a_guard_of_another_module()
    {
        var sql = File.ReadAllText(Path.Combine(Root, "db", "migrations", "0024_records.sql"));
        Assert.DoesNotContain("DROP TRIGGER", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DISABLE TRIGGER", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE plm.", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE audit.", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECURITY DEFINER", sql, StringComparison.OrdinalIgnoreCase); // nobody gets a path around the existing guards
        // The only triggers it adds to other schemas freeze tombstoned objects.
        foreach (Match m in Regex.Matches(sql, @"CREATE TRIGGER (\w+) [^;]*? ON (plm|audit|wf|src)\.\w+", RegexOptions.Singleline))
            Assert.Equal("zz_rec_frozen", m.Groups[1].Value);
    }

    /// <summary>Audit details carry identifiers, codes and counts only: never a name, a reason typed by a person, or a marking (FEAT-REC-12).</summary>
    [Fact]
    public void Audit_details_of_records_management_are_codes_and_counts_only()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "reasons", "version", "classesCopied", "class", "event", "occurredOn", "scope", "members", "added", "items", "scheduleVersion", "examined", "classified", "action", "certificate",
            "disposition", "destroyed", "transferred", "retained", "skipped", "state", "objects", "files", "worm", "valid", "path",
        };
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Sources("src/Keel.Records"))
        {
            var text = File.ReadAllText(file);
            foreach (Match call in Regex.Matches(text, @"RecordsStore\.Entry\("))
            {
                var window = text.Substring(call.Index, Math.Min(500, text.Length - call.Index));
                var end = window.IndexOf("ct);", StringComparison.Ordinal);
                if (end >= 0) window = window[..end];
                foreach (Match m in Regex.Matches(window, @"\(""(?<k>[A-Za-z]+)"",")) keys.Add(m.Groups["k"].Value);
            }
        }
        Assert.NotEmpty(keys);
        var unknown = keys.Where(k => !allowed.Contains(k)).ToList();
        Assert.True(unknown.Count == 0, "New audit detail keys need a decision (codes, identifiers and counts only): " + string.Join(", ", unknown));
    }
}
