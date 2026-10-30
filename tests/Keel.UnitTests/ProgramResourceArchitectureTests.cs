using System.Text.RegularExpressions;

namespace Keel.UnitTests;

/// <summary>Rules for the resource and cost part of the programs module (9.5, ADR-0049): the read filter on every list, cost behind its permission, no money in audit or the feed, immutable costs.</summary>
public sealed partial class ProgramResourceArchitectureTests
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

    private static string Migration => File.ReadAllText(Path.Combine(Root, "db", "migrations", "0028_program_resources.sql"));

    [GeneratedRegex(@"(FROM|JOIN)\s+prg\.(res_pool|res_role|resource|assignment|budget_line|actual_cost|material_line)\b[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex MarkedResourceRead();

    [GeneratedRegex(@"(FROM|JOIN)\s+prg\.(role_rate|budget_log|res_counter)\b[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex ChildRead();

    // Key lookups, and the two uniqueness probes of a change (a name already taken in the program, the budget line of one type and month): they answer yes or no to a caller who was
    // already checked against the program itself, and the unique indexes say the same (A58).
    [GeneratedRegex(@"WHERE\s+(\w+\.)?(program_id|id)\s*=\s*@\w+\s+AND\s+(upper\(|lower\(name\)|cost_type\s*=\s*@t)", RegexOptions.IgnoreCase)]
    private static partial Regex KeyLookup();

    [Fact]
    public void Every_list_over_a_marked_resource_or_cost_table_applies_the_read_filter_or_is_a_key_lookup()
    {
        var offenders = new List<string>();
        var statements = 0;
        foreach (var project in new[] { "Keel.Programs", "Keel.Web", "Keel.Api" })
            foreach (var file in Sources(project))
            {
                var text = File.ReadAllText(file);
                foreach (Match m in MarkedResourceRead().Matches(text))
                {
                    statements++;
                    var statement = text.Substring(m.Index, Math.Min(600, text.Length - m.Index));
                    if (!KeyLookup().IsMatch(statement) && !statement.Contains("Filter.Sql}", StringComparison.OrdinalIgnoreCase) && !statement.Contains("filter.Sql}", StringComparison.OrdinalIgnoreCase)
                        && !statement.Contains("WHERE id = @id", StringComparison.Ordinal) && !statement.Contains("SELECT key, program_id FROM prg.resource WHERE id = @id", StringComparison.Ordinal))
                        offenders.Add($"{Path.GetRelativePath(Root, file)}: {m.Value.Trim()}");
                }
            }
        Assert.True(statements >= 10, $"Only {statements} statements found; the pattern is probably broken.");
        Assert.True(offenders.Count == 0, "Unfiltered list queries over marked resource or cost tables:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Child_rows_are_read_inside_the_module_only_and_through_readable_parents()
    {
        var statements = 0;
        foreach (var file in Sources("Keel.Programs"))
            foreach (Match m in ChildRead().Matches(File.ReadAllText(file)))
            {
                statements++;
                // role rates are read for the ids of roles the caller may read, the counter is written under the program lock, budget log rows are only inserted.
                Assert.True(m.Value.Contains("ANY(@ids)", StringComparison.Ordinal) || m.Value.Contains("role_id = @r", StringComparison.Ordinal) || m.Value.Contains("role_id = @p", StringComparison.Ordinal) || m.Value.Contains("INSERT", StringComparison.OrdinalIgnoreCase) || m.Value.Contains("res_counter", StringComparison.Ordinal), m.Value);
            }
        Assert.True(statements >= 2);
        foreach (var project in new[] { "Keel.Web", "Keel.Api", "Keel.Notifications", "Keel.Workflow", "Keel.Reports" })
            foreach (var file in Sources(project))
                Assert.DoesNotMatch(ChildRead(), File.ReadAllText(file));
    }

    [Fact]
    public void Nothing_outside_the_programs_module_queries_the_program_resource_tables()
    {
        foreach (var project in new[] { "Keel.Web", "Keel.Api", "Keel.Notifications", "Keel.Workflow", "Keel.Reports", "Keel.Integration" })
            foreach (var file in Sources(project))
                Assert.DoesNotMatch(@"prg\.(res_pool|res_role|role_rate|resource|assignment|budget_line|budget_log|actual_cost|material_line)\b", File.ReadAllText(file));
    }

    [Fact]
    public void The_migration_gives_every_marked_table_the_full_marking_contract_and_makes_costs_immutable()
    {
        var sql = Migration;
        foreach (var column in new[] { "export_jurisdiction", "eccn", "usml_category", "cui_categories", "distribution_statement", "controlling_office", "distribution_reason", "program", "owning_org", "export_authorizations" })
            Assert.Equal(7, Regex.Count(sql, $@"^\s+{column}\s", RegexOptions.Multiline));
        foreach (var trigger in new[] { "role_rate_immutable", "budget_log_immutable", "actual_immutable", "role_rate_no_truncate", "budget_log_no_truncate", "actual_no_truncate" })
            Assert.Contains(trigger, sql, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT, INSERT ON prg.role_rate, prg.budget_log, prg.actual_cost TO keel_app", sql, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"GRANT[^;]*DELETE[^;]*prg\.(role_rate|budget_log|actual_cost|budget_line|assignment)", sql);
        Assert.DoesNotContain("identity.", sql, StringComparison.Ordinal);
        Assert.Contains("assignment_guard", sql, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\(""(amount|rate|cost|total|budget|price|estimate|name|title|note|description)""\s*,")]
    private static partial Regex MoneyOrTextKey();

    [Fact]
    public void Audit_events_of_the_resource_and_cost_operations_carry_keys_and_counts_never_amounts_or_text()
    {
        var offenders = new List<string>();
        foreach (var file in new[] { "ProgramService.Resources.cs", "ProgramService.Cost.cs" }.Select(f => Path.Combine(Root, "src", "Keel.Programs", f)))
            foreach (var line in File.ReadAllLines(file))
            {
                if (!line.Contains("PStep.Done(", StringComparison.Ordinal) && !line.Contains("ProgramStore.Entry(", StringComparison.Ordinal) && !line.Contains("AuditAsync(", StringComparison.Ordinal)) continue;
                foreach (Match m in MoneyOrTextKey().Matches(line)) offenders.Add($"{Path.GetFileName(file)}: {line.Trim()} ({m.Value})");
            }
        Assert.True(offenders.Count == 0, "Audit details that would carry an amount or record text:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Every_cost_operation_checks_the_cost_permission_before_it_reads_or_writes_money()
    {
        var cost = File.ReadAllText(Path.Combine(Root, "src", "Keel.Programs", "ProgramService.Cost.cs"));
        // reads: GetCostAsync refuses before building; writes go through MutateCostAsync which refuses without the role.
        Assert.Matches(@"if \(!CanViewCost\(subject\)\)[\s\S]{0,400}Forbidden\(\[CostRoleMissing\]\)", cost);
        Assert.Contains("CanViewCost(subject) ? await body(head, conn, tx, now) : PStep.Forbid(CostRoleMissing)", cost, StringComparison.Ordinal);
        foreach (var op in new[] { "program.budget_set", "program.actual_add", "program.material_add", "program.material_remove" })
            Assert.Matches($@"MutateCostAsync[^\n]*""{Regex.Escape(op)}""|MutateCostAsync<[^>]*>\([^\n]*""{Regex.Escape(op)}""", cost);
        var resources = File.ReadAllText(Path.Combine(Root, "src", "Keel.Programs", "ProgramService.Resources.cs"));
        Assert.Contains("if (!CanViewCost(subject)) return PStep.Forbid(CostRoleMissing);", resources, StringComparison.Ordinal);
        Assert.Contains("cmd.HourlyRate is not null && !CanViewCost(subject)", resources, StringComparison.Ordinal);
        // rates leave the service only for a holder of the permission
        Assert.Contains("cost ? current : null", resources, StringComparison.Ordinal);
    }

    [Fact]
    public void Money_actions_are_hidden_from_the_activity_feed_and_every_new_action_is_classified()
    {
        var catalog = File.ReadAllText(Path.Combine(Root, "src", "Keel.Notifications", "ActivityCatalog.cs"));
        foreach (var hidden in new[] { "program.role_rate_set", "program.budget_set", "program.actual_add", "program.material_add", "program.material_remove" })
        {
            Assert.Contains($"\"{hidden}\"", catalog, StringComparison.Ordinal);
            Assert.DoesNotContain($"R(\"{hidden}\"", catalog, StringComparison.Ordinal);
        }
        foreach (var rule in new[] { "program.pool_add", "program.role_add", "program.resource_add", "program.resource_update", "program.assignment_add", "program.assignment_update", "program.assignment_remove" })
            Assert.Contains($"R(\"{rule}\"", catalog, StringComparison.Ordinal);
        foreach (var view in new[] { "program.resources", "program.load", "program.cost", "program.cost_export" })
            Assert.Contains($"[\"{view}\"]", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cost_role_is_not_a_role_of_people_in_general_and_grants_no_object_access()
    {
        Assert.DoesNotContain(Keel.Core.Roles.ProgramCost, Keel.Core.Roles.All);
        var realm = File.ReadAllText(Path.Combine(Root, "deploy", "compose", "keycloak", "keel-realm.json"));
        Assert.Contains("\"name\": \"plm-program-cost\"", realm, StringComparison.Ordinal);
    }
}
