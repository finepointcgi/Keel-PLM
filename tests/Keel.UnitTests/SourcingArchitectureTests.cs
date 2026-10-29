using System.Text.RegularExpressions;

namespace Keel.UnitTests;

/// <summary>Structural rules of the sourcing module and the supplier portal (ADR-0037), enforced in CI.</summary>
public sealed partial class SourcingArchitectureTests
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

    [GeneratedRegex(@"(FROM|JOIN)\s+(src\.supplier|src\.request|plm\.part_master)\b[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex MarkedTable();

    [GeneratedRegex(@"(WHERE|AND)\s+(\w+\.)?(number|id|request_id|supplier_id|cage_code)\s*=\s*@\w+|(WHERE|AND)\s+(\w+\.)?number\s*=\s*ANY\(@\w+\)", RegexOptions.IgnoreCase)]
    private static partial Regex KeyLookup();

    [Fact]
    public void Every_list_over_a_marked_table_of_the_module_carries_the_read_filter_or_is_a_key_lookup()
    {
        var offenders = new List<string>();
        var checkedStatements = 0;
        foreach (var file in Sources("src/Keel.Sourcing"))
        {
            // EntryStore is the JOIN fragment its callers complete with the read filters; the expiry job is a system read with no end user (counts and ids of its own notices).
            if (file.EndsWith("EntryStore.cs", StringComparison.Ordinal) || file.EndsWith("CertificationExpiry.cs", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            foreach (Match m in MarkedTable().Matches(text))
            {
                checkedStatements++;
                var statement = text.Substring(m.Index, Math.Min(700, text.Length - m.Index));
                var filtered = statement.Contains("filter.Sql}", StringComparison.OrdinalIgnoreCase) || statement.Contains("Filter.Sql}", StringComparison.Ordinal);
                if (!filtered && !KeyLookup().IsMatch(statement)) offenders.Add($"{Path.GetRelativePath(Root, file)}: {m.Value}");
            }
        }
        Assert.True(checkedStatements >= 15, "The rule found too few statements; the pattern is probably broken.");
        Assert.True(offenders.Count == 0, "Unfiltered list queries over marked tables in Keel.Sourcing:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Every_portal_query_carries_the_callers_own_supplier()
    {
        var text = File.ReadAllText(Path.Combine(Root, "src", "Keel.Sourcing", "PortalService.cs"));
        var statements = Regex.Matches(text, @"new NpgsqlCommand\(\s*\$?""(?<sql>[^""]+)""", RegexOptions.Singleline).Select(m => m.Groups["sql"].Value)
            .Concat(Regex.Matches(text, "new NpgsqlCommand\\(\\s*\\$?\"\"\"(?<sql>.*?)\"\"\"", RegexOptions.Singleline).Select(m => m.Groups["sql"].Value)).ToList();
        Assert.True(statements.Count >= 10, $"The rule found {statements.Count} statements; the pattern is probably broken.");
        foreach (var sql in statements)
        {
            if (!Regex.IsMatch(sql, @"\b(FROM|UPDATE)\s+src\.", RegexOptions.IgnoreCase)) continue; // inserts name the supplier in their values
            var scoped = sql.Contains("supplier_id = @s", StringComparison.Ordinal)   // the recipient, response and file rows of the caller's supplier
                || sql.Contains("user_sub = @u", StringComparison.Ordinal)            // the portal-user link itself
                || sql.Contains("FROM src.request WHERE id = @r FOR SHARE", StringComparison.Ordinal) // the request lock of a request the caller was already shown, returns its state only
                || sql.Contains("{RequestRow.Select}", StringComparison.Ordinal) && sql.Contains("rr.supplier_id", StringComparison.Ordinal);
            Assert.True(scoped, "A portal statement is not scoped to the caller's supplier: " + sql[..Math.Min(120, sql.Length)]);
        }
        // No portal method takes a supplier from its caller.
        Assert.DoesNotMatch(@"public async Task<[^>]*> \w+\(Subject subject,[^)]*string supplier", text);
        Assert.DoesNotContain("SupplierNumber", text[text.IndexOf("class PortalService", StringComparison.Ordinal)..].Split("private sealed record Me")[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Portal_pages_use_only_the_portal_service_and_the_supplier_policy()
    {
        var portal = Sources("src/Keel.Web/Components/Portal", "*.razor").ToList();
        Assert.True(portal.Count >= 3);
        var imports = File.ReadAllText(Path.Combine(Root, "src", "Keel.Web", "Components", "Portal", "_Imports.razor"));
        Assert.Contains("Policies.Supplier", imports, StringComparison.Ordinal);
        Assert.DoesNotContain("AnyPlmRole", imports, StringComparison.Ordinal);
        var internalServices = new[] { "SupplierService", "SourcingService", "RequestService", "PartService", "BomService", "MbomService", "DocumentService", "ChangeService", "WorkflowService", "WorkspaceService", "IGlobalSearch", "WorkspaceReads" };
        foreach (var file in portal)
        {
            var text = File.ReadAllText(file);
            foreach (var s in internalServices)
                Assert.False(Regex.IsMatch(text, $@"@inject\s+{s}\b"), $"{Path.GetFileName(file)} injects {s}");
            if (text.Contains("@page", StringComparison.Ordinal)) Assert.Contains("<PageFrame", text, StringComparison.Ordinal);
        }
        // Pages of the internal area keep their role requirement (the supplier role is not one of them).
        Assert.Contains("Policies.AnyPlmRole", File.ReadAllText(Path.Combine(Root, "src", "Keel.Web", "Components", "Pages", "_Imports.razor")), StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(Root, "src", "Keel.Web", "Components", "Pages", "Sourcing"), "*.razor"), f => File.ReadAllText(f).Contains("Policies.Supplier", StringComparison.Ordinal));
    }

    [Fact]
    public void The_shell_gives_a_supplier_no_search_palette_or_notification_bell()
    {
        var layout = File.ReadAllText(Path.Combine(Root, "src", "Keel.Web", "Components", "Layout", "MainLayout.razor"));
        Assert.Contains("NavModel.ForSupplier()", layout, StringComparison.Ordinal);
        Assert.Matches(@"@if \(!locked && !supplier\)\s*\{\s*<CommandPalette />", layout);
        Assert.Contains("@if (!supplier) { <NotificationBell /> }", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_details_of_the_module_never_name_marked_or_personal_text()
    {
        var forbidden = new[] { ".Name", "Email", "Phone", ".Message", ".Title", "Description", "FileName", ".Scope", ".Issuer", ".Note", "request.Title" };
        var offenders = new List<string>();
        foreach (var file in Sources("src/Keel.Sourcing"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"(?<!static AuditEntry )\b(PartEntry|Entry)\(\w+, ""[a-z_.]+"", AuditOutcome\.\w+[^;]*;", RegexOptions.Singleline))
                foreach (var f in forbidden.Where(f => m.Value.Contains(f, StringComparison.Ordinal)))
                    offenders.Add($"{Path.GetFileName(file)}: {f} in {m.Value[..Math.Min(100, m.Value.Length)]}");
        }
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    [Fact]
    public void The_migration_registers_its_vault_references_and_hash_columns()
    {
        var sql = File.ReadAllText(Path.Combine(Root, "db", "migrations", "0020_sourcing.sql"));
        Assert.Contains("response_file", sql, StringComparison.Ordinal);
        var refs = File.ReadAllText(Path.Combine(Root, "src", "Keel.Documents", "Vault", "VaultReferences.cs"));
        Assert.Contains("new(\"src\", \"response_file\", \"sha256\"", refs, StringComparison.Ordinal);
    }
}
