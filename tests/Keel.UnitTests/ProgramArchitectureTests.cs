using System.Text.RegularExpressions;

namespace Keel.UnitTests;

/// <summary>Rules for the programs module (8.8, ADR-0043): the read filter on every list, no other module's tables, no record text in audit events, signed gate approval, no membership writes.</summary>
public sealed partial class ProgramArchitectureTests
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

    [GeneratedRegex(@"(FROM|JOIN)\s+prg\.(program|item|deliverable)\b[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex MarkedProgramRead();

    [GeneratedRegex(@"(FROM|JOIN)\s+(prg\.(dependency|baseline|baseline_item|schedule_change|checklist|submission))\b[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex ChildTableRead();

    [GeneratedRegex(@"WHERE\s+(\w+\.)?(code|id|gate_number|item_id|program_id|key|number)\s*=\s*@\w+|WHERE\s+lower\((\w+\.)?code\)|WHERE\s+i\.program_id\s*=\s*@p\s+AND\s+upper", RegexOptions.IgnoreCase)]
    private static partial Regex KeyLookup();

    [GeneratedRegex(@"\b(plm\.(part_|document_|bom_)\w*|wf\.\w+|chg\.\w+|cfg\.\w+|mbom\.\w+|src\.\w+|qual\.\w+|req\.\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex OtherModuleTables();

    [Fact]
    public void Every_list_over_a_marked_program_table_applies_the_read_filter_or_is_a_key_lookup()
    {
        var offenders = new List<string>();
        var statements = 0;
        foreach (var project in new[] { "Keel.Programs", "Keel.Web", "Keel.Api" })
            foreach (var file in Sources(project))
            {
                // The maintenance job (ProgramIntegration.cs) is the system's own work: it selects ids of milestones that are due or overdue, shows them to nobody and handles each by id;
                // the gateway and store load single items by id or gate number for the workflow engine, which evaluates the policy on the returned markings.
                if (file.EndsWith("ProgramIntegration.cs", StringComparison.Ordinal)) continue;
                var text = File.ReadAllText(file);
                foreach (Match m in MarkedProgramRead().Matches(text))
                {
                    statements++;
                    var statement = text.Substring(m.Index, Math.Min(600, text.Length - m.Index));
                    if (!KeyLookup().IsMatch(statement) && !statement.Contains("filter.sql}", StringComparison.OrdinalIgnoreCase) && !statement.Contains("filter.Sql}", StringComparison.Ordinal))
                        offenders.Add($"{Path.GetRelativePath(Root, file)}: {m.Value.Trim()}");
                }
            }
        Assert.True(statements >= 10, $"Only {statements} statements found; the pattern is probably broken.");
        Assert.True(offenders.Count == 0, "Unfiltered list queries over marked program tables:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Child_rows_are_read_only_inside_the_module_through_a_parent_the_caller_may_read()
    {
        var offenders = new List<string>();
        var statements = 0;
        foreach (var file in Sources("Keel.Programs"))
            statements += ChildTableRead().Count(File.ReadAllText(file));
        Assert.True(statements >= 5);
        Assert.Empty(offenders);
        foreach (var project in new[] { "Keel.Web", "Keel.Api", "Keel.Notifications", "Keel.Workflow", "Keel.Parts", "Keel.Change", "Keel.Quality" })
            foreach (var file in Sources(project))
                Assert.DoesNotMatch(ChildTableRead(), File.ReadAllText(file));
    }

    [Fact]
    public void The_module_never_reads_the_tables_of_other_modules()
    {
        var offenders = new List<string>();
        foreach (var file in Sources("Keel.Programs"))
            foreach (Match m in OtherModuleTables().Matches(File.ReadAllText(file)))
                offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
        Assert.True(offenders.Count == 0, "Keel.Programs touches other modules' tables (use the interfaces of Keel.Core):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_module_never_writes_program_membership_or_the_user_directory()
    {
        // Membership is the identity provider's (and the user directory snapshot of the workflow module's): this module only reads the directory to find a person by login.
        foreach (var file in Sources("Keel.Programs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotMatch(@"(INSERT\s+INTO|UPDATE|DELETE\s+FROM)\s+identity\.", text);
            Assert.DoesNotContain("Programs = ", text.Replace("Programs = Set(7)", "", StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }

    [GeneratedRegex(@"\(""(title|description|text|body|note|reason|name|query|comment|label)""\s*,")]
    private static partial Regex DetailKey();

    [Fact]
    public void Audit_events_carry_keys_numbers_states_and_counts_never_the_text_of_an_item()
    {
        var offenders = new List<string>();
        foreach (var file in Sources("Keel.Programs"))
            foreach (var line in File.ReadAllLines(file))
            {
                if (!line.Contains("PStep.Done(", StringComparison.Ordinal) && !line.Contains("ProgramStore.Entry(", StringComparison.Ordinal) && !line.Contains("SystemEntry(", StringComparison.Ordinal) && !line.Contains("AuditAsync(", StringComparison.Ordinal)) continue;
                foreach (Match m in DetailKey().Matches(line))
                    if (!(m.Groups[1].Value is "reason" or "query" && line.Contains("reasons", StringComparison.Ordinal)))
                        offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
            }
        Assert.True(offenders.Count == 0, "Audit details that would carry record text:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_module_calls_the_notification_sink_with_pointers_only()
    {
        foreach (var file in Sources("Keel.Programs"))
            Assert.DoesNotMatch(@"NotificationRequest\([^)]*(Title|Description|Name)", File.ReadAllText(file));
    }

    [Fact]
    public void The_migration_guards_the_signed_gate_approval_the_baselines_and_the_marking_contract()
    {
        var sql = File.ReadAllText(Path.Combine(Root, "db", "migrations", "0026_programs.sql"));
        Assert.Contains("wf.signature", sql, StringComparison.Ordinal);
        Assert.Contains("cannot be approved without an approving signature", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (program = code)", sql, StringComparison.Ordinal);
        Assert.Contains("pg_get_constraintdef", sql, StringComparison.Ordinal); // awareness constraints widened by pattern
        Assert.Contains("baseline_item_immutable", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'supplier'", sql, StringComparison.Ordinal); // the other modules' kinds are not re-listed
        // marked tables carry the full marking contract
        foreach (var column in new[] { "export_jurisdiction", "eccn", "usml_category", "cui_categories", "distribution_statement", "controlling_office", "distribution_reason", "program", "owning_org", "export_authorizations" })
            Assert.Equal(3, Regex.Count(sql, $@"^\s+{column}\s", RegexOptions.Multiline));
    }

    [Fact]
    public void Gate_reviews_are_approved_only_through_the_workflow_gateway()
    {
        // The only statement that sets a gate to Approved is in the gateway; the service refuses to complete a gate by hand.
        foreach (var file in Sources("Keel.Programs"))
        {
            var text = File.ReadAllText(file);
            if (file.EndsWith("ProgramGateway.cs", StringComparison.Ordinal)) continue;
            Assert.DoesNotContain("gate_state = 'Approved'", text, StringComparison.Ordinal);
        }
    }
}
