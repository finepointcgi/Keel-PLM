using System.Text.RegularExpressions;

namespace Keel.UnitTests;

/// <summary>Rules of the requirements module (8.3, ADR-0038) enforced in CI: marked requirement rows are read through the filter, nothing else touches the schema, text stays out of audit details.</summary>
public sealed partial class RequirementsArchitectureTests
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

    private static IEnumerable<string> Sources(string folder) =>
        Directory.EnumerateFiles(Path.Combine(Root, "src", folder), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [GeneratedRegex(@"(FROM|JOIN)\s+req\.(requirement_version|current_requirement)\b[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex RequirementRead();

    [GeneratedRegex(@"WHERE\s+(\w+\.)?(iteration_id|requirement_id|id)\s*(=|=\s*ANY)\s*\(?@\w+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyedByIdentity();

    [GeneratedRegex(@"\b(FROM|JOIN|INTO|UPDATE)\s+req\.\w+", RegexOptions.IgnoreCase)]
    private static partial Regex ReqSchema();

    [Fact]
    public void Requirement_rows_are_read_through_the_read_filter_or_by_the_system_view_of_an_authorized_transaction()
    {
        // System views: they run inside a transaction that already evaluated the policy on the specification (and, where one requirement is meant, on that requirement), and they are
        // used to copy forward, hash, verify a signed digest, or read the verification state of requirements already read through the filter.
        var systemView = new[] { "RequirementStore.cs", "RequirementWorkflowGateway.cs", "RequirementService.Trace.cs", "RequirementService.Verification.cs", "RequirementService.Links.cs" };
        var checkedStatements = 0;
        var offenders = new List<string>();
        foreach (var file in Sources("Keel.Requirements"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in RequirementRead().Matches(text))
            {
                checkedStatements++;
                var window = text.Substring(Math.Max(0, m.Index - 500), Math.Min(1300, text.Length - Math.Max(0, m.Index - 500)));
                var filtered = window.Contains("ilter.Sql}", StringComparison.Ordinal);
                if (!filtered && !systemView.Contains(Path.GetFileName(file)) && !KeyedByIdentity().IsMatch(text.AsSpan(m.Index, Math.Min(500, text.Length - m.Index))))
                    offenders.Add($"{Path.GetFileName(file)}: {m.Value.Trim()}");
            }
        }
        Assert.True(checkedStatements >= 8, "The rule found too few statements; the pattern is probably broken.");
        Assert.True(offenders.Count == 0, "Unfiltered reads of requirement rows:\n" + string.Join("\n", offenders));
        // The reader-facing loaders carry the filter.
        var service = File.ReadAllText(Path.Combine(Root, "src", "Keel.Requirements", "RequirementService.cs"));
        Assert.Contains("LEFT JOIN req.requirement_version v ON v.id = p.id AND {vFilter.Sql}", service, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN req.current_requirement cr ON cr.requirement_id = l.source_requirement_id AND {crFilter.Sql}", File.ReadAllText(Path.Combine(Root, "src", "Keel.Requirements", "RequirementService.Trace.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Link_ends_that_are_parts_documents_or_changes_are_joined_through_the_read_filter()
    {
        var links = File.ReadAllText(Path.Combine(Root, "src", "Keel.Requirements", "RequirementService.Links.cs"));
        foreach (var table in new[] { "plm.part_master", "plm.document_master", "chg.change" })
            Assert.Contains($"LEFT JOIN {table} tm ON tm.number = l.target_number AND {{tmFilter.Sql}}", links, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN req.current_requirement sr ON sr.requirement_id = l.source_requirement_id AND {srFilter.Sql}", links, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN req.current_requirement tr ON tr.requirement_id = l.target_requirement_id AND {trFilter.Sql}", links, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_requirements_module_touches_the_req_schema()
    {
        var sep = Path.DirectorySeparatorChar;
        var offenders = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}Keel.Requirements{sep}", StringComparison.Ordinal))
            .Where(f => ReqSchema().IsMatch(File.ReadAllText(f))).Select(f => Path.GetRelativePath(Root, f)).ToList();
        Assert.True(offenders.Count == 0, "These files read the req schema directly: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Audit_details_never_name_requirement_text()
    {
        // The details of an audit event are keys, numbers, codes, hashes and counts. A statement that builds one never mentions a text-bearing field.
        var forbidden = new[] { ".Title", ".Text", ".Body", ".Rationale", ".Notes", "WaiverReason", "cmd.Name", "input.Title", "Input.Text" };
        var offenders = new List<string>();
        var checkedStatements = 0;
        foreach (var file in Sources("Keel.Requirements"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"(?<![A-Za-z])(Entry|SystemEntry|ObjectEntry)\("))
            {
                var end = text.IndexOf(';', m.Index);
                var statement = text.Substring(m.Index, Math.Min(end < 0 ? 600 : end - m.Index, 900));
                checkedStatements++;
                foreach (var f in forbidden)
                    if (statement.Contains(f, StringComparison.Ordinal)) offenders.Add($"{Path.GetFileName(file)}: {f} in {statement[..Math.Min(80, statement.Length)]}");
            }
        }
        Assert.True(checkedStatements >= 20, $"The rule found only {checkedStatements} audit statements; the pattern is probably broken.");
        Assert.True(offenders.Count == 0, "Audit events that may carry requirement text:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Notification_requests_carry_pointers_only()
    {
        foreach (var file in Sources("Keel.Requirements"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"new NotificationRequest\([^;]*;"))
                Assert.DoesNotContain("Title", m.Value, StringComparison.Ordinal);
    }
}
