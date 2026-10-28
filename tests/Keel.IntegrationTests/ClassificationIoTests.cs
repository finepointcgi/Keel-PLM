using Keel.Admin;
using Keel.Core;

namespace Keel.IntegrationTests;

/// <summary>Class hierarchy import and export as JSON and CSV with a dry run that is exactly the apply, rolled back (docs/migration/classification-format.md).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ClassificationIoTests(PostgresFixture pg)
{
    private static readonly Subject Ada = AdminEnv.Ada;

    private static ClassHierarchyDoc Sample() => new(ClassHierarchyDoc.CurrentFormat,
    [
        new ClassNodeDoc("FAST", "Fasteners, general", "Bolts, \"screws\", nuts", "MECH", 10, "active",
        [
            new AttributeDoc("diameter", "Diameter", "number", "mm", Min: 1, Max: 100, Required: true, SortOrder: 10),
            new AttributeDoc("thread", "Thread", "string", MaxLength: 20, Pattern: "M[0-9]+x[0-9.]+", HelpText: "e.g. M8x1.25, metric", SortOrder: 20),
        ]),
        new ClassNodeDoc("MECH", "Mechanical", "Teamcenter-style root", null, 10, "active",
            [new AttributeDoc("grade", "Material grade", "enum", EnumValues: ["304", "316"], SortOrder: 10, Group: "Material")]),
    ]);

    [Fact]
    public async Task A_hierarchy_imports_children_before_parents_in_any_order_and_exports_back_identically()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var report = AdminEnv.Ok(await env.Classes.ImportHierarchyAsync(Ada, Sample(), false, "Initial load", "c-imp"));
        Assert.Equal((2, 0, 0), (report.Created, report.Updated, report.Unchanged));
        Assert.Equal(new[] { "MECH", "FAST" }, report.Lines.Select(l => l.Code)); // parents first, whatever the file order
        Assert.Equal("/MECH/FAST/", await env.TextAsync("SELECT path FROM cfg.class_node WHERE code = 'FAST'"));
        Assert.Equal(["grade", "diameter", "thread"], (await env.Classes.GetAsync("FAST"))!.Effective.Select(e => e.Definition.Name));

        var exported = AdminEnv.Ok(await env.Classes.ExportHierarchyAsync(Ada));
        Assert.Equal(["MECH", "FAST"], exported.Nodes.Select(n => n.Code)); // parents first
        var again = AdminEnv.Ok(await env.Classes.ImportHierarchyAsync(Ada, exported, false, "Again", null));
        Assert.Equal((0, 0, 2), (again.Created, again.Updated, again.Unchanged)); // idempotent

        Assert.Contains((await env.AuditAsync()), e => e.Action == "admin.class_import" && e.CorrelationId == "c-imp" && e.Payload.Contains("Initial load", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_dry_run_reports_exactly_what_the_apply_does_and_writes_nothing()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var dry = AdminEnv.Ok(await env.Classes.ImportHierarchyAsync(Ada, Sample(), true, null, null));
        Assert.True(dry.DryRun);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM cfg.class_node"));
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM audit.event WHERE action LIKE 'admin.class_import%'"));
        var applied = AdminEnv.Ok(await env.Classes.ImportHierarchyAsync(Ada, Sample(), false, "Load", null));
        Assert.Equal((dry.Created, dry.Updated, dry.Unchanged, dry.Sha256), (applied.Created, applied.Updated, applied.Unchanged, applied.Sha256));
        Assert.Equal(dry.Lines.Select(l => (l.Code, l.Action, l.Detail)), applied.Lines.Select(l => (l.Code, l.Action, l.Detail)));

        // A later import updates names and attributes and reports them.
        var changed = Sample() with
        {
            Nodes = [.. Sample().Nodes.Select(n => n.Code == "FAST" ? n with { Name = "Fasteners", Attributes = [.. n.Attributes, new AttributeDoc("coating", "Coating", "string")] } : n)],
        };
        var update = AdminEnv.Ok(await env.Classes.ImportHierarchyAsync(Ada, changed, false, "Rename", null));
        Assert.Equal((0, 1, 1), (update.Created, update.Updated, update.Unchanged));
        Assert.Contains("attribute coating created", update.Lines.Single(l => l.Code == "FAST").Detail, StringComparison.Ordinal);
        Assert.Equal("Fasteners", await env.TextAsync("SELECT name FROM cfg.class_node WHERE code = 'FAST'"));
    }

    [Fact]
    public async Task An_import_with_any_error_applies_nothing_and_never_moves_or_retires_classes()
    {
        var env = await AdminEnv.CreateAsync(pg);
        var bad = Sample() with
        {
            Nodes = [.. Sample().Nodes, new ClassNodeDoc("ORPHAN", "Orphan", null, "NO-SUCH-PARENT", 0, "active", []),
                new ClassNodeDoc("BADATTR", "Bad attr", null, null, 0, "active", [new AttributeDoc("Bad Name", "x", "string")])],
        };
        var result = await env.Classes.ImportHierarchyAsync(Ada, bad, false, "Load", null);
        Assert.Equal(OutcomeKind.Invalid, result.Kind);
        var text = AdminEnv.Errors(result);
        Assert.Contains("parent 'NO-SUCH-PARENT' does not exist", text, StringComparison.Ordinal);
        Assert.Contains("BADATTR/Bad Name", text, StringComparison.Ordinal);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM cfg.class_node")); // MECH and FAST were rolled back too

        // Existing classes are not moved or retired by an import; the report says to use the checked operations.
        AdminEnv.Ok(await env.Classes.ImportHierarchyAsync(Ada, Sample(), false, "Load", null));
        var moved = Sample() with { Nodes = [.. Sample().Nodes.Select(n => n.Code == "FAST" ? n with { Parent = null } : n)] };
        Assert.Contains("use Move", AdminEnv.Errors(await env.Classes.ImportHierarchyAsync(Ada, moved, false, "x", null)), StringComparison.Ordinal);
        var retired = Sample() with { Nodes = [.. Sample().Nodes.Select(n => n.Code == "FAST" ? n with { Status = "retired" } : n)] };
        Assert.Contains("use Retire or Restore", AdminEnv.Errors(await env.Classes.ImportHierarchyAsync(Ada, retired, false, "x", null)), StringComparison.Ordinal);
        // Cycles and duplicates in the file.
        var cycle = new ClassHierarchyDoc(ClassHierarchyDoc.CurrentFormat, [new ClassNodeDoc("P", "P", null, "Q", 0, "active", []), new ClassNodeDoc("Q", "Q", null, "P", 0, "active", [])]);
        Assert.Contains("cycle", string.Join(" ", AdminEnv.Ok(await env.Classes.ImportHierarchyAsync(Ada, cycle, true, null, null)).Errors), StringComparison.Ordinal); // a dry run reports errors in the report
        var dup = new ClassHierarchyDoc(ClassHierarchyDoc.CurrentFormat, [new ClassNodeDoc("X", "X", null, null, 0, "active", []), new ClassNodeDoc("x", "X2", null, null, 0, "active", [])]);
        Assert.Contains("more than once", string.Join(" ", AdminEnv.Ok(await env.Classes.ImportHierarchyAsync(Ada, dup, true, null, null)).Errors), StringComparison.Ordinal);
        // Reason required for an apply, role required always.
        Assert.Equal(OutcomeKind.Invalid, (await env.Classes.ImportHierarchyAsync(Ada, Sample(), false, null, null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Classes.ImportHierarchyAsync(AdminEnv.Alice, Sample(), true, null, null)).Kind);
    }

    [Fact]
    public async Task Json_and_csv_round_trip_including_quotes_commas_and_enum_values()
    {
        var env = await AdminEnv.CreateAsync(pg);
        AdminEnv.Ok(await env.Classes.ImportHierarchyAsync(Ada, Sample(), false, "Load", null));
        var exported = AdminEnv.Ok(await env.Classes.ExportHierarchyAsync(Ada));

        var (fromJson, jsonError) = ClassificationIO.FromJson(ClassificationIO.ToJson(exported));
        Assert.Null(jsonError);
        Assert.Equal(ClassificationIO.ToJson(exported), ClassificationIO.ToJson(fromJson!));

        var csv = ClassificationIO.ToCsv(exported);
        Assert.StartsWith("record,code,parent,name,description,sort_order,status,attr_name", csv, StringComparison.Ordinal);
        Assert.Contains("\"Bolts, \"\"screws\"\", nuts\"", csv, StringComparison.Ordinal);
        var (fromCsv, csvError) = ClassificationIO.FromCsv(csv);
        Assert.Null(csvError);
        Assert.Equal(ClassificationIO.ToJson(exported), ClassificationIO.ToJson(fromCsv!));
        Assert.Equal(["304", "316"], fromCsv!.Nodes.Single(n => n.Code == "MECH").Attributes.Single().EnumValues);

        // The CSV imports into a fresh environment and gives the same hierarchy.
        var other = await AdminEnv.CreateAsync(pg);
        AdminEnv.Ok(await other.Classes.ImportHierarchyAsync(Ada, fromCsv, false, "From CSV", null));
        Assert.Equal(ClassificationIO.ToJson(exported), ClassificationIO.ToJson(AdminEnv.Ok(await other.Classes.ExportHierarchyAsync(Ada))));
    }

    [Fact]
    public void Malformed_documents_are_reported_not_thrown()
    {
        Assert.NotNull(ClassificationIO.FromJson("{ nope").Error);
        Assert.Contains("Unsupported format", ClassificationIO.FromJson("""{"format":"other","nodes":[]}""").Error, StringComparison.Ordinal);
        Assert.Contains("Missing column", ClassificationIO.FromCsv("record,code\nclass,A").Error, StringComparison.Ordinal);
        Assert.Contains("record type", ClassificationIO.FromCsv(string.Join(',', new[] { "record", "code", "parent", "name", "description", "sort_order", "status", "attr_name", "attr_label", "data_type", "unit", "enum_values", "required", "max_length", "pattern", "min", "max", "help_text", "group", "attr_sort", "retired" }) + "\nfoo,A,,,,,,,,,,,,,,,,,,,").Error, StringComparison.Ordinal);
        Assert.Contains("no class row", ClassificationIO.FromCsv(string.Join(',', new[] { "record", "code", "parent", "name", "description", "sort_order", "status", "attr_name", "attr_label", "data_type", "unit", "enum_values", "required", "max_length", "pattern", "min", "max", "help_text", "group", "attr_sort", "retired" }) + "\nattribute,Z,,,,,,x,X,string,,,false,,,,,,,0,false").Error, StringComparison.Ordinal);
        Assert.Equal([["a", "b,c", "d\"e", ""], ["x"]], ClassificationIO.ParseCsv("a,\"b,c\",\"d\"\"e\",\r\nx\n"));
    }
}
