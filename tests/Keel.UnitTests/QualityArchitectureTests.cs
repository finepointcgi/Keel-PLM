using System.Text.RegularExpressions;

namespace Keel.UnitTests;

/// <summary>Rules for the quality module (8.1, ADR-0036): the read filter on every list, no other module's tables, no marked text in audit events.</summary>
public sealed partial class QualityArchitectureTests
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

    private static IEnumerable<string> Sources(string project) =>
        Directory.EnumerateFiles(Path.Combine(Root, "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [GeneratedRegex(@"(FROM|JOIN)\s+qual\.(ncr|capa|deviation)\b[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex MarkedQualityRead();

    [GeneratedRegex(@"(FROM|JOIN)\s+(qual\.(action|link|deviation_scope|rca_why|rca_cause|history|submission))\b[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex ChildTableRead();

    [GeneratedRegex(@"WHERE\s+(\w+\.)?(number|id|object_id|capa_id|deviation_id|parent_id)\s*=\s*@\w+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyLookup();

    [GeneratedRegex(@"\b(plm\.(part_|document_|bom_)\w*|wf\.\w+|chg\.\w+|cfg\.\w+|mbom\.\w+|src\.\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex OtherModuleTables();

    [Fact]
    public void Every_list_over_a_marked_quality_table_applies_the_read_filter_or_is_a_key_lookup()
    {
        var offenders = new List<string>();
        var statements = 0;
        foreach (var project in new[] { "Keel.Quality", "Keel.Web", "Keel.Api" })
            foreach (var file in Sources(project))
            {
                // The maintenance job is the system's own work: it selects ids of records whose window ended or whose action is overdue, shows them to nobody,
                // and handles each one by key in its own transaction.
                if (file.EndsWith("QualityModule.cs", StringComparison.Ordinal)) continue;
                var text = File.ReadAllText(file);
                foreach (Match m in MarkedQualityRead().Matches(text))
                {
                    statements++;
                    var statement = text.Substring(m.Index, Math.Min(600, text.Length - m.Index));
                    if (!KeyLookup().IsMatch(statement) && !statement.Contains("filter.sql}", StringComparison.OrdinalIgnoreCase))
                        offenders.Add($"{Path.GetRelativePath(Root, file)}: {m.Value.Trim()}");
                }
            }
        Assert.True(statements >= 8, $"Only {statements} statements found; the pattern is probably broken.");
        Assert.True(offenders.Count == 0, "Unfiltered list queries over marked quality tables:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Child_rows_are_read_only_through_a_parent_the_caller_may_read()
    {
        // Actions, scope rows, links and history carry no markings: a statement that lists them either joins the marked parent with its filter or is keyed by the parent
        // id inside a transaction that already established read access (QualityRunner) or runs for the system.
        var allowed = new[] { "QualityShared.cs", "QualityStore.cs", "QualityActions.cs", "CapaService.cs", "DeviationService.cs", "NcrService.cs", "QualityWorkflowGateway.cs", "QualityModule.cs", "QualityIntegration.cs", "QualityQueries.cs" };
        var offenders = new List<string>();
        var statements = 0;
        foreach (var file in Sources("Keel.Quality"))
            foreach (Match m in ChildTableRead().Matches(File.ReadAllText(file)))
            {
                statements++;
                if (!allowed.Contains(Path.GetFileName(file))) offenders.Add($"{Path.GetFileName(file)}: {m.Value.Trim()}");
            }
        Assert.True(statements >= 8);
        Assert.Empty(offenders);
        // Outside the module nobody reads these tables except through the services.
        foreach (var project in new[] { "Keel.Web", "Keel.Api", "Keel.Notifications", "Keel.Workflow", "Keel.Parts", "Keel.Change", "Keel.Sourcing" })
            foreach (var file in Sources(project))
                Assert.DoesNotMatch(ChildTableRead(), File.ReadAllText(file));
    }

    [Fact]
    public void The_module_never_reads_the_tables_of_other_modules()
    {
        var offenders = new List<string>();
        foreach (var file in Sources("Keel.Quality"))
            foreach (Match m in OtherModuleTables().Matches(File.ReadAllText(file)))
                offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
        Assert.True(offenders.Count == 0, "Keel.Quality touches other modules' tables (use the interfaces of Keel.Core):\n" + string.Join("\n", offenders));
    }

    [GeneratedRegex(@"\(""(title|description|text|body|note|reason|rationale|rootCause|result|justification|requirement|defect|why|cause|name|query)""\s*,")]
    private static partial Regex DetailKey();

    [Fact]
    public void Audit_events_carry_numbers_states_and_counts_never_the_text_of_a_record()
    {
        // The only free text a detail may carry is the reason for a markings change, which is process text by the convention of every module (and refusals' reason codes).
        var offenders = new List<string>();
        foreach (var file in Sources("Keel.Quality"))
            foreach (var line in File.ReadAllLines(file))
            {
                if (!line.Contains("QStep.Done(", StringComparison.Ordinal) && !line.Contains("QualityStore.Entry(", StringComparison.Ordinal) && !line.Contains("SystemEntry(", StringComparison.Ordinal) && !line.Contains("AuditAsync(", StringComparison.Ordinal)) continue;
                foreach (Match m in DetailKey().Matches(line))
                    if (!(m.Groups[1].Value is "reason" or "query" && (line.Contains("markings_change", StringComparison.Ordinal) || line.Contains("reasons", StringComparison.Ordinal) || line.Contains("search", StringComparison.Ordinal) || line.Contains("(\"reason\", reason.Trim())", StringComparison.Ordinal))))
                        offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
            }
        Assert.True(offenders.Count == 0, "Audit details that would carry record text:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_module_calls_the_notification_sink_with_pointers_only()
    {
        // NotificationRequest has no text field by construction; this guards the module against building one from record data in another way.
        foreach (var file in Sources("Keel.Quality"))
            Assert.DoesNotMatch(@"NotificationRequest\([^)]*(Title|Description|Rationale|RootCause)", File.ReadAllText(file));
    }

    [Fact]
    public void The_migration_guards_the_states_that_only_a_signed_review_reaches()
    {
        var sql = File.ReadAllText(Path.Combine(Root, "db", "migrations", "0019_quality.sql"));
        Assert.Contains("wf.signature", sql, StringComparison.Ordinal);
        Assert.Contains("'Dispositioned', 'Closed', 'Approved', 'AwaitingCustomer'", sql, StringComparison.Ordinal);
        Assert.Contains("verification_effective IS TRUE", sql, StringComparison.Ordinal); // a CAPA cannot reach review or closure with an ineffective result
        // the awareness constraints are widened by pattern, so the order with 0020 does not matter
        Assert.Contains("pg_get_constraintdef", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'supplier'", sql, StringComparison.Ordinal); // 0019 does not re-list the other modules' kinds
        // marked tables carry the full marking contract
        foreach (var column in new[] { "export_jurisdiction", "eccn", "usml_category", "cui_categories", "distribution_statement", "controlling_office", "distribution_reason", "program", "owning_org", "export_authorizations" })
            Assert.Equal(3, Regex.Count(sql, $@"^\s+{column}\s", RegexOptions.Multiline));
    }
}
