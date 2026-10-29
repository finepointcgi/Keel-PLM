using System.Text.RegularExpressions;
using Keel.Notifications;

namespace Keel.UnitTests;

/// <summary>Structural rules of the compliance module (ADR-0040), enforced in CI.</summary>
public sealed partial class ComplianceArchitectureTests
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

    [GeneratedRegex(@"(FROM|JOIN)\s+plm\.part_master\b[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex PartMasterRead();

    [GeneratedRegex(@"(WHERE|AND)\s+(\w+\.)?(number|id|part_master_id)\s*=\s*@\w+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyLookup();

    [GeneratedRegex(@"""(compliance\.[a-z_]+)""")]
    private static partial Regex ActionLiteral();

    [Fact]
    public void Every_read_of_the_part_master_in_the_module_carries_the_read_filter_or_is_a_key_lookup()
    {
        var offenders = new List<string>();
        var checkedStatements = 0;
        foreach (var file in Sources("src/Keel.Compliance"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in PartMasterRead().Matches(text))
            {
                checkedStatements++;
                var statement = text.Substring(m.Index, Math.Min(700, text.Length - m.Index));
                if (!statement.Contains("filter.Sql}", StringComparison.OrdinalIgnoreCase) && !KeyLookup().IsMatch(statement)) offenders.Add($"{Path.GetRelativePath(Root, file)}: {m.Value}");
            }
        }
        Assert.True(checkedStatements >= 8, "The rule found too few statements; the pattern is probably broken.");
        Assert.True(offenders.Count == 0, "Unfiltered reads of plm.part_master in Keel.Compliance:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Declarations_carry_no_markings_of_their_own_and_are_reached_only_through_a_part()
    {
        var sql = File.ReadAllText(Path.Combine(Root, "db", "migrations", "0023_compliance.sql"));
        var declaration = sql[sql.IndexOf("CREATE TABLE cmp.declaration (", StringComparison.Ordinal)..sql.IndexOf("CREATE TABLE cmp.declaration_line", StringComparison.Ordinal)];
        Assert.DoesNotContain("export_jurisdiction", declaration, StringComparison.Ordinal);
        Assert.Contains("REFERENCES plm.part_master (id)", declaration, StringComparison.Ordinal);
        // Every statement of the services that lists declarations joins the part master (and so takes the part's filter); the stored evaluation is only ever read by declaration id.
        foreach (var file in new[] { "DeclarationService.cs", "ComplianceReportService.cs" })
        {
            var text = File.ReadAllText(Path.Combine(Root, "src", "Keel.Compliance", file));
            foreach (Match m in Regex.Matches(text, @"FROM\s+cmp\.declaration\s+d\b[^\n]*"))
                Assert.Contains("JOIN plm.part_master", m.Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_migration_does_not_touch_the_constraints_other_modules_widen_and_has_no_hash_columns()
    {
        var sql = File.ReadAllText(Path.Combine(Root, "db", "migrations", "0023_compliance.sql"));
        Assert.DoesNotContain("DROP CONSTRAINT", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE ws.", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE ntf.", sql, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?im)^\s*\w*sha256\w*\s", sql); // a hash column would have to be registered with the vault references
        Assert.Contains("'reg_list_version'", sql, StringComparison.Ordinal);
        Assert.Contains("BEFORE UPDATE OR DELETE ON cmp.reg_list_version", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_details_of_the_module_never_name_marked_or_free_text()
    {
        var forbidden = new[] { ".Name", "Note", "SupplierRef", "MpnRef", "Description", "SourceNote", "reason)", "Reason)" };
        var offenders = new List<string>();
        foreach (var file in Sources("src/Keel.Compliance"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"(?<!static AuditEntry )\b(PartEntry|Entry|SystemEntry)\((?:\w+, )?""[a-z_.]+""[^;]*;", RegexOptions.Singleline))
                foreach (var f in forbidden.Where(f => m.Value.Contains(f, StringComparison.Ordinal)))
                    offenders.Add($"{Path.GetFileName(file)}: {f} in {m.Value[..Math.Min(110, m.Value.Length)]}");
        }
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    [Fact]
    public void Every_audit_action_of_the_module_is_in_the_activity_catalog()
    {
        var actions = Sources("src/Keel.Compliance").SelectMany(f => ActionLiteral().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).Distinct().ToList();
        Assert.True(actions.Count >= 20, $"Found only {actions.Count} actions; the pattern is probably broken.");
        Assert.All(actions, a => Assert.True(ActivityCatalog.IsKnown(a), a));
        // The events of a part's declarations are in the feed; the reference data is administration; views are views.
        foreach (var a in new[] { "compliance.declaration_create", "compliance.declaration_verify", "compliance.status_change", "compliance.expiry_notice" }) Assert.NotNull(ActivityCatalog.Find(a));
        foreach (var a in new[] { "compliance.list_publish", "compliance.substance_create" }) Assert.True(ActivityCatalog.IsHidden(a));
        foreach (var a in new[] { "compliance.rollup", "compliance.export", "compliance.part_read" }) Assert.True(ActivityCatalog.Views.ContainsKey(a));
    }

    [Fact]
    public void The_pages_use_the_page_frame_and_the_new_notification_kinds_lead_to_the_compliance_tab()
    {
        var pages = Sources("src/Keel.Web/Components/Pages/Compliance", "*.razor").Where(f => File.ReadAllText(f).Contains("@page", StringComparison.Ordinal)).ToList();
        Assert.True(pages.Count >= 8);
        foreach (var page in pages) Assert.Contains("<PageFrame", File.ReadAllText(page), StringComparison.Ordinal);
        foreach (var code in new[] { Keel.Core.NotificationKindCodes.ComplianceNonCompliant, Keel.Core.NotificationKindCodes.ComplianceDeclarationExpired, Keel.Core.NotificationKindCodes.ComplianceDeclarationExpiring, Keel.Core.NotificationKindCodes.ComplianceSupplierDeclaration })
            Assert.Equal("parts/P-000015?tab=compliance", NotificationKinds.Href(NotificationKinds.Find(code)!, "part", "P-000015", null));
    }
}
