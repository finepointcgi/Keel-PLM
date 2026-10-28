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
        // Change management reaches parts, BOM structure and documents only through Keel.Core interfaces implemented by their owners (ADR-0019).
        ["Keel.Change"] = ["Keel.Core", "Keel.Security", "Keel.Audit"],
        // Migration (ADR-0023) writes plm/chg/document rows with plain SQL through its own import writer; file bytes go through the Keel.Core IContentStore.
        ["Keel.Migration"] = ["Keel.Core", "Keel.Security", "Keel.Audit"],
        // Administration (2.2) reaches workflow templates only through IWorkflowTemplateAdmin in Keel.Core, implemented by Keel.Workflow.
        ["Keel.Admin"] = ["Keel.Core", "Keel.Security", "Keel.Audit"],
        // Integration (ADR-0025) reaches parts, BOM, documents and change data only through Keel.Core interfaces implemented by their owners.
        ["Keel.Integration"] = ["Keel.Core", "Keel.Security", "Keel.Audit"],
        ["Keel.Web"] = ["Keel.Core", "Keel.Security", "Keel.Audit", "Keel.Parts", "Keel.Documents", "Keel.Bom", "Keel.Workflow", "Keel.Change", "Keel.Migration", "Keel.Admin", "Keel.Integration"],
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
            // PartChangeGateway does the same for change management: single objects by id (or a batch of ids for display) that the caller
            // evaluates with AccessPolicy on the returned markings before showing anything.
            if (file.EndsWith("PartChangeGateway.cs", StringComparison.Ordinal)) continue;
            // The outbox snapshot readers (ADR-0025) are system reads inside the releasing transaction, keyed by id; the event's markings and the
            // outbound release check decide who sees the result, and PartEvents/DocumentEvents load one revision by id.
            if (file.EndsWith("BomPublicationSource.cs", StringComparison.Ordinal)) continue;
            // The seed tool's one fresh-database probe (a yes/no administrative question with no end user); everything else it does goes through services.
            if (file.EndsWith("Keel.Seed" + Path.DirectorySeparatorChar + "DatabaseState.cs", StringComparison.Ordinal)) continue;
            // The MBOM reconciliation reads two facts about children (Make assembly? MBOM of the plant released?) as inputs of the system's own check, never shown (ADR-0024).
            if (file.EndsWith("MbomReconcileInputs.cs", StringComparison.Ordinal)) continue;
            // The migration reconciler (ADR-0023) is an administrative comparison with no end user: it counts and hashes the objects of one import
            // batch (ids, numbers, markings columns) and returns only counts and source ids, never names or descriptions. This is the ONLY file of the
            // importer that selects from a marked table; every write goes through ImportWriter.
            if (file.EndsWith("Keel.Migration" + Path.DirectorySeparatorChar + "Reconcile" + Path.DirectorySeparatorChar + "Reconciler.cs", StringComparison.Ordinal)) continue;
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
            if (file.EndsWith("MbomReconcileInputs.cs", StringComparison.Ordinal)) continue; // see List_queries_over_marked_tables_use_the_read_filter
            // The outbox snapshot reader of released assemblies (ADR-0025): a system read whose result is gated by the event's markings.
            if (file.EndsWith("BomPublicationSource.cs", StringComparison.Ordinal)) continue;
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

    /// <summary>
    /// Keel.Change owns the <c>chg</c> schema and uses the lifecycle tables of <c>plm</c>; it must never read another module's data tables
    /// (parts, documents, BOM, workflow): those go through the gateways and providers in Keel.Core (ADR-0019). The only table of the
    /// identity schema it reads is the user directory, to find task assignees.
    /// </summary>
    [Fact]
    public void Change_module_never_reads_other_modules_data_tables()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        var files = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "Keel.Change"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            files++;
            foreach (Match m in OtherModuleTables().Matches(File.ReadAllText(file)))
                offenders.Add($"{Path.GetRelativePath(root, file)}: {m.Value}");
        }
        Assert.True(files > 5);
        Assert.True(offenders.Count == 0, "Keel.Change touches other modules' tables:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// Baseline items name parts, so a statement that reads <c>cfg.baseline_item</c> must join the part master through the read filter (hidden items become
    /// placeholders, A4) or only test membership. The one exception is the owner's hash and verification read, which is never shown to a user.
    /// </summary>
    [Fact]
    public void Baseline_item_reads_apply_the_read_filter_or_stay_inside_the_module()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        var checkedStatements = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in BaselineItemRead().Matches(text))
            {
                checkedStatements++;
                if (!file.Contains("Keel.Bom", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)}: baseline items read outside Keel.Bom");
                    continue;
                }
                // BaselineStore reads the stored rows to hash and verify them; the result is a digest, never a list shown to a reader.
                if (file.EndsWith("BaselineStore.cs", StringComparison.Ordinal)) continue;
                var statement = text.Substring(m.Index, Math.Min(900, text.Length - m.Index));
                if (!statement.Contains("filter.Sql}", StringComparison.OrdinalIgnoreCase) && !m.Value.Contains("SELECT 1", StringComparison.OrdinalIgnoreCase)
                    && !text.Substring(Math.Max(0, m.Index - 12), Math.Min(m.Index, 12)).Contains("SELECT 1", StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{Path.GetRelativePath(root, file)}: {m.Value}");
            }
        }
        Assert.True(checkedStatements >= 3, "The rule found too few statements; the pattern is probably broken.");
        Assert.True(offenders.Count == 0, "Unfiltered baseline item reads:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// ADR-0024: MBOM lines, working lines and reconciliation findings name parts, so every statement that reads them for display joins the part master through the
    /// read filter. The files listed here read them as the system's own view (hashing, copying inside an authorized transaction, reconciliation inputs).
    /// </summary>
    [Fact]
    public void Mbom_structure_reads_apply_the_read_filter_or_stay_in_the_system_view_files()
    {
        var root = FindRepoRoot();
        var systemView = new[] { "MbomStore.cs", "MbomService.cs", "MbomService.Reconcile.cs", "MbomService.Lines.cs", "MbomService.Generate.cs", "MbomWorkflowGateway.cs", "MbomReconcileInputs.cs", "MbomIntegrations.cs" };
        var offenders = new List<string>();
        var checkedStatements = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "Keel.Bom"), "Mbom*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in MbomStructureRead().Matches(text))
            {
                checkedStatements++;
                var start = Math.Max(0, m.Index - 700);
                var window = text.Substring(start, Math.Min(1700, text.Length - start));
                if (!window.Contains("filter.Sql}", StringComparison.OrdinalIgnoreCase) && !systemView.Contains(Path.GetFileName(file)))
                    offenders.Add($"{Path.GetFileName(file)}: {m.Value.Trim()}");
            }
        }
        Assert.True(checkedStatements >= 10, "The rule found too few statements; the pattern is probably broken.");
        Assert.True(offenders.Count == 0, "Unfiltered MBOM structure reads outside the system-view files:\n" + string.Join("\n", offenders));
        // The reader-facing loaders carry the filter.
        Assert.Contains("LEFT JOIN plm.part_master cm ON cm.id = u.child_master_id AND {filter.Sql}", File.ReadAllText(Path.Combine(root, "src", "Keel.Bom", "MbomService.Lines.cs")), StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN plm.part_master cm ON cm.id = f.child_master_id AND {filter.Sql}", File.ReadAllText(Path.Combine(root, "src", "Keel.Bom", "MbomService.Reconcile.cs")), StringComparison.Ordinal);
    }

    /// <summary>Only the BOM module's own files touch the mbom schema (other modules use the interfaces in Keel.Core).</summary>
    [Fact]
    public void Only_the_bom_module_touches_the_mbom_schema()
    {
        var root = FindRepoRoot();
        var sep = Path.DirectorySeparatorChar;
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}Keel.Bom{sep}", StringComparison.Ordinal))
            .Where(f => MbomSchema().IsMatch(File.ReadAllText(f))).Select(f => Path.GetRelativePath(root, f)).ToList();
        Assert.True(offenders.Count == 0, "These files read the mbom schema directly: " + string.Join(", ", offenders));
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

    /// <summary>Increment 2.2: every page of the Administration section carries the administrator policy (the services check the role again).</summary>
    [Fact]
    public void Administration_pages_require_the_administrator_policy()
    {
        var root = FindRepoRoot();
        var pages = Directory.EnumerateFiles(Path.Combine(root, "src", "Keel.Web", "Components", "Pages", "Admin"), "*.razor").ToList();
        Assert.True(pages.Count >= 9, "The admin pages were not found.");
        var offenders = pages.Where(f => !File.ReadAllText(f).Contains("@attribute [Authorize(Policy = Policies.Admin)]", StringComparison.Ordinal)).Select(Path.GetFileName).ToList();
        Assert.True(offenders.Count == 0, "Admin pages without the administrator policy: " + string.Join(", ", offenders));
        // The admin routes of the API sit in one group with the same policy.
        var endpoints = File.ReadAllText(Path.Combine(root, "src", "Keel.Api", "Endpoints", "AdminEndpoints.cs"));
        Assert.Contains("MapGroup(\"/api/v1/admin\").RequireAuthorization(Policies.Admin)", endpoints, StringComparison.Ordinal);
    }

    /// <summary>
    /// Keel.Admin owns configuration, not part data. It touches the marked part table only to classify a part (key lookups by number and the
    /// filtered faceted search), to list conflicting parts under the read filter, and to check whether a generated number is taken. It never reads
    /// workflow instances, change objects or documents as lists. Aggregate counts for lifecycles live in database functions that return no marked columns.
    /// </summary>
    [Fact]
    public void Admin_module_touches_marked_tables_only_where_it_is_expected_to()
    {
        var root = FindRepoRoot();
        var allowed = new[] { "ClassificationService.cs", "ClassificationSearch.cs", "ClassificationAssignment.cs", "NumberingAdmin.cs" };
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "Keel.Admin"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            if (WorkflowInstanceOrDocuments().IsMatch(text)) offenders.Add($"{Path.GetFileName(file)} reads workflow instances");
            if (SqlFromMarkedTable().IsMatch(text) && !allowed.Contains(Path.GetFileName(file))) offenders.Add($"{Path.GetFileName(file)} reads a marked table");
        }
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    [GeneratedRegex(@"(FROM|JOIN)\s+(wf\.instance)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WorkflowInstanceOrDocuments();

    [GeneratedRegex(@"(FROM|JOIN)\s+(plm\.part_master|plm\.document_master|wf\.instance|chg\.change|cfg\.baseline|mbom\.mbom)\b[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex SqlFromMarkedTable();

    [GeneratedRegex(@"(FROM|JOIN)\s+mbom\.(usage|working_usage|reconciliation_finding)\b[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex MbomStructureRead();

    [GeneratedRegex(@"\b(FROM|JOIN|INTO|UPDATE)\s+mbom\.\w+", RegexOptions.IgnoreCase)]
    private static partial Regex MbomSchema();

    [GeneratedRegex(@"(FROM|JOIN)\s+plm\.part_master\b[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex SqlTouchesPartMaster();

    [GeneratedRegex(@"(SELECT\s+1\s+)?FROM\s+cfg\.baseline_item\b[^\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex BaselineItemRead();

    [GeneratedRegex(@"\b(plm\.(part_|document_|bom_)\w*|wf\.\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex OtherModuleTables();

    // source_id: the objects created from one already-authorized change (counted by process rules; every list shown to a user carries the filter).
    [GeneratedRegex(@"WHERE\s+(\w+\.)?(number|id|object_id|object_key|source_id|part_master_id)\s*=\s*@\w+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyLookup();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
