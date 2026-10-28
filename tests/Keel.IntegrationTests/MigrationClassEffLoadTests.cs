using Keel.Admin;
using Keel.Bom;
using Keel.Core;
using Keel.Migration;
using Keel.Migration.Canonical;
using Keel.Migration.Fixtures;
using Keel.Migration.Load;
using Keel.Migration.Reconcile;
using Keel.Parts;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Loading classification and effectivity through the importer into real Postgres and the vault (FUP-2, FUP-3): dry-run equals load, idempotence, conflicts, reconciliation.</summary>
[Collection(PostgresCollection.Name)]
public sealed class MigrationClassEffLoadTests(PostgresFixture pg)
{
    private static readonly Subject Admin = ConfigEnv.Ada;

    private static async Task Exec(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<Dictionary<string, long>> CountsAsync(MigrationKit k) => new()
    {
        ["class_node"] = await k.Count("SELECT count(*) FROM cfg.class_node"),
        ["class_attribute"] = await k.Count("SELECT count(*) FROM cfg.class_attribute"),
        ["part_classification"] = await k.Count("SELECT count(*) FROM cfg.part_classification"),
        ["part_class_value"] = await k.Count("SELECT count(*) FROM cfg.part_class_value"),
        ["part_iteration"] = await k.Count("SELECT count(*) FROM plm.part_iteration"),
    };

    // ---------------------------------------------------------------- classification

    [Fact]
    public async Task Dry_run_writes_nothing_and_equals_the_load_which_creates_the_hierarchy_and_classifies_every_iteration()
    {
        using var k = await MigrationKit.Create(pg);
        var dir = k.Package(Samples.BuildClassified(MigrationKit.Salt()));
        var before = await CountsAsync(k);
        using var prepared = await k.Prepare(dir, Samples.ClassificationMapping());
        var dry = await k.Pipeline.DryRunAsync(prepared, new LoadOptions { Operator = "tester" }, CancellationToken.None);
        Assert.True(dry.Succeeded, string.Join("; ", dry.Blocking));
        Assert.Equal(before, await CountsAsync(k)); // the classes and values the dry run created were rolled back with it
        Assert.Equal(5, dry.Results["classes"].Created);
        Assert.Equal(4, dry.Results["classifiedIterations"].Created);
        Assert.Equal(["ELEC", "ELEC.RES", "FASTENERS", "FASTENERS.BRACKET", "MECH"], dry.ClassesToCreate.Select(c => c.Split(' ')[0]).Order(StringComparer.Ordinal));

        var (report, result) = await k.Pipeline.LoadAsync(prepared, dry, new LoadOptions { Operator = "tester" }, CancellationToken.None);
        Assert.Equal("Completed", result.Status);
        foreach (var (key, planned) in dry.Results) Assert.True(planned.Created == report.Results[key].Created, $"{key}: dry-run {planned.Created}, load {report.Results[key].Created}");

        // the hierarchy: parents, attributes (with the types of the staged definitions)
        Assert.Equal("FASTENERS", await k.Text("SELECT p.code FROM cfg.class_node c JOIN cfg.class_node p ON p.id = c.parent_id WHERE c.code = 'FASTENERS.BRACKET'"));
        Assert.Equal("number:mm:true", await k.Text("SELECT a.data_type || ':' || a.unit || ':' || a.required FROM cfg.class_attribute a JOIN cfg.class_node c ON c.id = a.node_id WHERE c.code = 'FASTENERS.BRACKET' AND a.name = 'width'"));
        Assert.Equal("enum", await k.Text("SELECT a.data_type FROM cfg.class_attribute a JOIN cfg.class_node c ON c.id = a.node_id WHERE c.code = 'MECH' AND a.name = 'grade'"));
        // the class data: the same rows native check-in writes, on every iteration of the part, typed value rows for the facet search
        Assert.Equal(3, await k.Count("SELECT count(*) FROM cfg.part_classification pc JOIN plm.part_iteration i ON i.id = pc.iteration_id JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'P-2001' AND pc.class_code = 'FASTENERS.BRACKET'"));
        Assert.Equal(3, await k.Count("SELECT count(*) FROM cfg.part_class_value v JOIN plm.part_iteration i ON i.id = v.iteration_id JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'P-2001' AND v.attr = 'width' AND v.num = 20"));
        Assert.Equal(3, await k.Count("SELECT count(*) FROM cfg.part_class_value v JOIN plm.part_iteration i ON i.id = v.iteration_id JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'P-2001' AND v.attr = 'grade' AND v.txt = '316'"));
        Assert.Equal(1, await k.Count("SELECT count(*) FROM cfg.part_classification pc JOIN plm.part_iteration i ON i.id = pc.iteration_id JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'P-2005'"));
        Assert.Equal(0, await k.Count("SELECT count(*) FROM cfg.part_classification pc JOIN plm.part_iteration i ON i.id = pc.iteration_id JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'P-2003'"));
        // the native view of the imported (Released, locked) part
        var view = await k.Classes.GetForPartAsync(MigrationKit.Alice, "P-2001", null);
        Assert.True(view.IsOk, string.Join("; ", view.Errors));
        Assert.Equal("FASTENERS.BRACKET", view.Value!.ClassCode);
        Assert.Equal(20m, view.Value.Attributes["width"]!.GetValue<decimal>());
        Assert.Equal(["Mechanical", "Fasteners", "Brackets"], view.Value.PathNames);
        // the audit chain is intact and the batch recorded the classes it created
        Assert.True((await k.VerifyAudit()).Valid);
        Assert.Equal(1, await k.Count("SELECT count(*) FROM audit.event WHERE action = 'migration.classification.classes' AND correlation_id = @b", ("b", result.BatchId.ToString())));
    }

    [Fact]
    public async Task A_rerun_is_idempotent_and_an_imported_in_work_part_continues_natively_with_its_class()
    {
        using var k = await MigrationKit.Create(pg);
        var product = Samples.BuildClassified(MigrationKit.Salt());
        product.Package.Classifications.Add(new CClassAssignment { ItemSourceId = "P-2002", ClassCode = "ELEC.RES", Attributes = { ["voltage"] = "12" } }); // revision 02 of P-2002 is In Work
        var dir = k.Package(product);
        var first = await k.DryRunAndLoad(dir, Samples.ClassificationMapping());
        Assert.Equal("Completed", first.Result.Status);
        var counts = await CountsAsync(k);
        var again = await k.DryRunAndLoad(dir, Samples.ClassificationMapping());
        Assert.Equal(0, again.Result.Created.GetValueOrDefault("classes"));
        Assert.Equal(0, again.Result.Created.GetValueOrDefault("classifiedIterations"));
        Assert.Equal(counts, await CountsAsync(k));

        // native check-out and check-in of the imported In Work revision copies the class forward through the classification participant
        var parts = new PartService(k.Ds, k.Audit, TimeProvider.System, [new BomParticipant(), new ClassificationParticipant()]);
        Assert.True((await parts.CheckOutAsync(MigrationKit.Alice, "P-2002", null)).IsOk);
        var working = await k.Classes.GetForPartAsync(MigrationKit.Alice, "P-2002", null);
        Assert.Equal("ELEC.RES", working.Value!.ClassCode);
        Assert.True((await parts.CheckInAsync(MigrationKit.Alice, "P-2002", "native edit", null)).IsOk);
        Assert.Equal(2, await k.Count("SELECT count(*) FROM cfg.part_classification pc JOIN plm.part_iteration i ON i.id = pc.iteration_id JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'P-2002' AND r.revision = 'B'"));
    }

    [Fact]
    public async Task Existing_classes_are_never_changed_conflicts_block_the_dry_run_and_retired_classes_are_refused()
    {
        using var k = await MigrationKit.Create(pg);
        // Keel already has the bracket class, with "width" as text, and a retired resistor class.
        Assert.True((await k.Classes.CreateNodeAsync(Admin, new ClassNodeInput("MECH", "Mechanical (native)", null, null), null)).IsOk);
        Assert.True((await k.Classes.CreateNodeAsync(Admin, new ClassNodeInput("FASTENERS", "Fasteners (native)", null, "MECH"), null)).IsOk);
        Assert.True((await k.Classes.CreateNodeAsync(Admin, new ClassNodeInput("FASTENERS.BRACKET", "Brackets (native)", null, "FASTENERS"), null)).IsOk);
        Assert.True((await k.Classes.SaveAttributeAsync(Admin, "FASTENERS.BRACKET", new AttributeDefinitionInput("width", "Width", "string", MaxLength: 10), true, null)).IsOk);
        Assert.True((await k.Classes.CreateNodeAsync(Admin, new ClassNodeInput("ELEC", "Electronics (native)", null, null), null)).IsOk);
        Assert.True((await k.Classes.CreateNodeAsync(Admin, new ClassNodeInput("ELEC.RES", "Resistors (native)", null, "ELEC"), null)).IsOk);
        Assert.True((await k.Classes.SetStatusAsync(Admin, "ELEC.RES", false, "obsolete class", null)).IsOk);
        var before = await CountsAsync(k);

        var dir = k.Package(Samples.BuildClassified(MigrationKit.Salt()));
        using var prepared = await k.Prepare(dir, Samples.ClassificationMapping());
        var dry = await k.Pipeline.DryRunAsync(prepared, new LoadOptions { Operator = "tester" }, CancellationToken.None);
        Assert.False(dry.Succeeded);
        Assert.Contains(dry.Problems, p => p.Code == "CLASS-ATTR-TYPE-MISMATCH" && p.SourceId == "FASTENERS.BRACKET");
        Assert.Contains(dry.Problems, p => p.Code == "CLASS-RETIRED" && p.SourceId == "P-2005");
        Assert.Contains(dry.Problems, p => p.Code == "CLASS-FAILED" && p.SourceId == "P-2001");
        Assert.Equal(before["part_classification"], await k.Count("SELECT count(*) FROM cfg.part_classification"));
        // with quarantine the rest loads: the existing classes keep their definitions and the good classes are created
        using var q = await k.Prepare(dir, Samples.ClassificationMapping());
        var dryQ = await k.Pipeline.DryRunAsync(q, new LoadOptions { Operator = "tester", Quarantine = true }, CancellationToken.None);
        Assert.True(dryQ.Succeeded, string.Join("; ", dryQ.Blocking));
        var (_, result) = await k.Pipeline.LoadAsync(q, dryQ, new LoadOptions { Operator = "tester", Quarantine = true }, CancellationToken.None);
        Assert.Equal("CompletedWithIssues", result.Status);
        Assert.Contains(result.Quarantined, x => x.EntityType == "part" && x.SourceId == "P-2001");
        Assert.Equal("string", await k.Text("SELECT a.data_type FROM cfg.class_attribute a JOIN cfg.class_node c ON c.id = a.node_id WHERE c.code = 'FASTENERS.BRACKET' AND a.name = 'width'"));
        Assert.Equal("Brackets (native)", await k.Text("SELECT name FROM cfg.class_node WHERE code = 'FASTENERS.BRACKET'"));
    }

    [Fact]
    public async Task Invalid_values_quarantine_the_part_and_a_value_rejected_by_the_database_side_validation_is_a_load_error()
    {
        using var k = await MigrationKit.Create(pg);
        var product = Samples.BuildClassified(MigrationKit.Salt());
        product.Package.Classifications[0].Attributes["Width"] = "wide";
        var dir = k.Package(product);
        using var prepared = await k.Prepare(dir, Samples.ClassificationMapping());
        var blocked = await k.Pipeline.DryRunAsync(prepared, new LoadOptions { Operator = "tester" }, CancellationToken.None);
        Assert.False(blocked.Succeeded);
        var e = blocked.Problems.Single(p => p.Code == "CLASS-VALUE-INVALID");
        Assert.Equal("P-2001", e.SourceId);
        var loaded = await k.DryRunAndLoad(dir, Samples.ClassificationMapping(), quarantine: true);
        Assert.Contains(loaded.Report.Quarantined, x => x.SourceId == "P-2001" && x.Code == "CLASS-VALUE-INVALID");
        Assert.Equal(0, await k.Count("SELECT count(*) FROM plm.part_master WHERE number = 'P-2001'"));
    }

    [Fact]
    public async Task Reconciliation_covers_classification_and_detects_an_altered_class_value()
    {
        using var k = await MigrationKit.Create(pg);
        var dir = k.Package(Samples.BuildClassified(MigrationKit.Salt()));
        var cfg = Samples.ClassificationMapping();
        await k.DryRunAndLoad(dir, cfg);
        using (var prepared = await k.Prepare(dir, cfg))
        {
            var ok = await k.Pipeline.ReconcileAsync(prepared, new ReconcileOptions(), CancellationToken.None);
            Assert.True(ok.Ok, string.Join("; ", ok.Checks.Where(c => !c.Ok).Select(c => c.Name + " " + string.Join("|", c.Details))));
            Assert.Equal("4", ok.Checks.Single(c => c.Name == "classified part iterations").Actual);
            Assert.Equal("7", ok.Checks.Single(c => c.Name.StartsWith("class attribute values", StringComparison.Ordinal)).Actual);
            Assert.Contains(ok.Checks, c => c.Name.StartsWith("class and values per iteration", StringComparison.Ordinal) && c.Ok);
            Assert.Contains(ok.Checks, c => c.Name.StartsWith("classes used or created exist", StringComparison.Ordinal) && c.Ok);
        }
        // someone with database superpowers changes one stored value (the trigger makes class data immutable; the importer never does this)
        await Exec(k.Db.SuperuserConnectionString,
            "ALTER TABLE cfg.part_classification DISABLE TRIGGER part_classification_immutable; UPDATE cfg.part_classification SET attributes = jsonb_set(attributes, '{width}', '99') WHERE iteration_id = (SELECT i.id FROM plm.part_iteration i JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'P-2001' AND r.revision = 'B' LIMIT 1); ALTER TABLE cfg.part_classification ENABLE TRIGGER part_classification_immutable;");
        using var again = await k.Prepare(dir, cfg);
        var bad = await k.Pipeline.ReconcileAsync(again, new ReconcileOptions(), CancellationToken.None);
        Assert.False(bad.Ok);
        var check = bad.Checks.Single(c => c.Name.StartsWith("class and values per iteration", StringComparison.Ordinal));
        Assert.False(check.Ok);
        Assert.Contains(check.Details, d => d.StartsWith("P-2001 B 1", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- effectivity

    private static async Task CreateContextAsync(MigrationKit k, string id = "ORCA-POD")
    {
        var r = await new EffectivityService(k.Ds, k.Audit, TimeProvider.System).CreateContextAsync(MigrationKit.Alice,
            new CreateContextCommand(id, "Synthetic end item " + id, "", null, [new EffectivityBlock("Block 1", 1), new EffectivityBlock("Block 2", 2)]), null);
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
    }

    private static string Rows(string parent, string rev, int iteration, int find) =>
        $"SELECT string_agg(c.number || ':' || u.alt || ':' || COALESCE(u.eff_kind, '-') || ':' || COALESCE(u.eff_context, '-') || ':' || COALESCE(u.eff_from::text, '') || ':' || COALESCE(u.eff_to::text, ''), ',' ORDER BY u.alt) " +
        $"FROM plm.bom_usage u JOIN plm.part_master c ON c.id = u.child_master_id JOIN plm.part_iteration i ON i.id = u.parent_iteration_id JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id " +
        $"WHERE m.number = '{parent}' AND r.revision = '{rev}' AND i.iteration = {iteration} AND u.find_number = {find}";

    [Fact]
    public async Task Effectivity_loads_into_iteration_owned_rows_with_alternate_ordinals_in_range_order_and_dry_run_equals_load()
    {
        using var k = await MigrationKit.Create(pg);
        await CreateContextAsync(k);
        var product = Samples.BuildEffective(MigrationKit.Salt());
        // the open-ended alternate is listed first in the source: ordinals follow the ranges, not the source order
        var lines = product.Package.Items.First(i => i.Number == "A-1000").Revisions[0].Iterations[0].Usages;
        lines.Reverse();
        var dir = k.Package(product);
        var cfg = Samples.EffectivityMapping();
        using var prepared = await k.Prepare(dir, cfg);
        var before = await k.RowCounts();
        var dry = await k.Pipeline.DryRunAsync(prepared, new LoadOptions { Operator = "tester" }, CancellationToken.None);
        Assert.True(dry.Succeeded, string.Join("; ", dry.Blocking) + string.Join("; ", dry.Problems.Where(p => p.Severity == Severity.Error).Select(p => p.Code + " " + p.Message)));
        Assert.Equal(before, await k.RowCounts());
        Assert.Equal(5, dry.Planned["effectivityLines"]);
        var (report, result) = await k.Pipeline.LoadAsync(prepared, dry, new LoadOptions { Operator = "tester" }, CancellationToken.None);
        Assert.Equal("Completed", result.Status);
        Assert.Equal(5, result.Created["effectivityLines"]);
        foreach (var (key, planned) in dry.Results) Assert.True(planned.Created == report.Results[key].Created, $"{key}: dry-run {planned.Created}, load {report.Results[key].Created}");

        Assert.Equal("P-2001:0:serial:ORCA-POD:1:100,P-2003:1:serial:ORCA-POD:101:", await k.Text(Rows("A-1000", "A", 1, 30)));
        Assert.Equal($"P-2004:0:lot:ORCA-POD:1:1,P-2005:1:lot:ORCA-POD:2:2", await k.Text(Rows("A-1200", "A", 1, 10)));
        var day = (await k.Text($"SELECT {EffectivityRange.DayNumber(new DateOnly(2027, 3, 1))}"))!;
        Assert.Equal($"P-2002:0:date:-:{day}:{EffectivityRange.DayNumber(new DateOnly(2027, 6, 30))}", await k.Text(Rows("A-1000", "B", 2, 40)));
        // text-only source effectivity stays in the notes and the line applies to every unit
        Assert.Equal("-", await k.Text("SELECT COALESCE(u.eff_kind, '-') FROM plm.bom_usage u JOIN plm.part_iteration i ON i.id = u.parent_iteration_id JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'A-1100' AND u.find_number = 20"));
        Assert.Contains("units 1-50 only (free text)", (await k.Text("SELECT u.notes FROM plm.bom_usage u JOIN plm.part_iteration i ON i.id = u.parent_iteration_id JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = 'A-1100' AND u.find_number = 20"))!, StringComparison.Ordinal);

        // the native explosion as of a serial resolves the alternates the way the ranges say
        var bom = new BomService(k.Ds, k.Audit, TimeProvider.System);
        async Task<string> ChildAt(long serial)
        {
            var o = await bom.ExplodeAsync(MigrationKit.Alice, new BomQuery("A-1000", null, null, BomConfigSpec.Effectivity, 1, null, null, EffectivityRequest.ForSerial("ORCA-POD", serial)), null);
            Assert.True(o.IsOk, string.Join("; ", o.Errors));
            return o.Value!.Lines.Single(l => l.FindNumber == 30).Number!;
        }
        Assert.Equal("P-2001", await ChildAt(1));
        Assert.Equal("P-2001", await ChildAt(100));
        Assert.Equal("P-2003", await ChildAt(101));
        Assert.Equal("P-2003", await ChildAt(100_000));
        Assert.True((await k.VerifyAudit()).Valid);
    }

    [Fact]
    public async Task A_rerun_of_an_effectivity_package_is_idempotent()
    {
        using var k = await MigrationKit.Create(pg);
        await CreateContextAsync(k);
        var dir = k.Package(Samples.BuildEffective(MigrationKit.Salt()));
        var cfg = Samples.EffectivityMapping();
        await k.DryRunAndLoad(dir, cfg);
        var counts = await k.Count("SELECT count(*) FROM plm.bom_usage WHERE eff_kind IS NOT NULL");
        var again = await k.DryRunAndLoad(dir, cfg);
        Assert.Equal(0, again.Result.Created.GetValueOrDefault("bomUsages"));
        Assert.Equal(counts, await k.Count("SELECT count(*) FROM plm.bom_usage WHERE eff_kind IS NOT NULL"));
    }

    [Fact]
    public async Task An_unknown_context_blocks_the_dry_run_and_contexts_are_never_created_by_an_import()
    {
        using var k = await MigrationKit.Create(pg); // no context exists
        var dir = k.Package(Samples.BuildEffective(MigrationKit.Salt()));
        using var prepared = await k.Prepare(dir, Samples.EffectivityMapping());
        var dry = await k.Pipeline.DryRunAsync(prepared, new LoadOptions { Operator = "tester" }, CancellationToken.None);
        Assert.False(dry.Succeeded);
        Assert.Contains(dry.Problems, p => p.Code == "MAP-EFF-CONTEXT");
        Assert.Equal(0, await k.Count("SELECT count(*) FROM cfg.effectivity_context"));
        // a known Keel context that the mapping does not name for this end item
        await CreateContextAsync(k, "OTHER-POD");
        var cfg = Samples.EffectivityMapping();
        cfg.Effectivity.Contexts.Clear();
        cfg.Effectivity.Blocks.Clear();
        using var q = await k.Prepare(dir, cfg);
        Assert.Contains(q.Plan.Problems, p => p.Code == "EFF-CONTEXT-UNMAPPED");
    }

    [Fact]
    public async Task Overlapping_alternates_quarantine_the_assembly_and_the_rest_is_imported()
    {
        using var k = await MigrationKit.Create(pg);
        await CreateContextAsync(k);
        var product = Samples.BuildEffective(MigrationKit.Salt());
        product.Package.Items.First(i => i.Number == "A-1200").Revisions[0].Iterations[0].Usages.First(u => u.ChildSourceId == "P-2005").EffectivityRanges = [Samples.Lot(Samples.EndItem, "Block 1")]; // same block twice
        var dir = k.Package(product);
        var blocked = await k.Prepare(dir, Samples.EffectivityMapping());
        var dry = await k.Pipeline.DryRunAsync(blocked, new LoadOptions { Operator = "tester" }, CancellationToken.None);
        Assert.False(dry.Succeeded);
        var e = Assert.Single(dry.Problems, p => p.Code == "EFF-OVERLAP");
        Assert.Equal("A-1200", e.SourceId);
        blocked.Dispose();
        var loaded = await k.DryRunAndLoad(dir, Samples.EffectivityMapping(), quarantine: true);
        Assert.Contains(loaded.Report.Quarantined, q => q.SourceId == "A-1200");
        Assert.Equal(0, await k.Count("SELECT count(*) FROM plm.part_master WHERE number = 'A-1200'"));
    }

    [Fact]
    public async Task Reconciliation_compares_effectivity_and_the_as_of_serial_explosion_and_detects_an_altered_range()
    {
        using var k = await MigrationKit.Create(pg);
        await CreateContextAsync(k);
        var dir = k.Package(Samples.BuildEffective(MigrationKit.Salt()));
        var cfg = Samples.EffectivityMapping();
        await k.DryRunAndLoad(dir, cfg);
        using (var prepared = await k.Prepare(dir, cfg))
        {
            var ok = await k.Pipeline.ReconcileAsync(prepared, new ReconcileOptions(), CancellationToken.None);
            Assert.True(ok.Ok, string.Join("; ", ok.Checks.Where(c => !c.Ok).Select(c => c.Name + " " + string.Join("|", c.Details))));
            Assert.Equal("5", ok.Checks.Single(c => c.Name.StartsWith("BOM lines with effectivity", StringComparison.Ordinal)).Actual);
            var serialChecks = ok.Checks.Where(c => c.Name.StartsWith("as-of-serial explosion checksum", StringComparison.Ordinal)).ToList();
            Assert.Equal(3, serialChecks.Count); // serials 1, 100 and 101, chosen from the data
            Assert.All(serialChecks, c => Assert.True(c.Ok));
        }
        // someone with database superpowers widens one range: line counts stay the same, the structure and the sampled explosions differ
        await Exec(k.Db.SuperuserConnectionString,
            "ALTER TABLE plm.bom_usage DISABLE TRIGGER bom_usage_immutable; UPDATE plm.bom_usage SET eff_to = 150 WHERE eff_kind = 'serial' AND eff_to = 100; ALTER TABLE plm.bom_usage ENABLE TRIGGER bom_usage_immutable;");
        using var again = await k.Prepare(dir, cfg);
        var bad = await k.Pipeline.ReconcileAsync(again, new ReconcileOptions(), CancellationToken.None);
        Assert.False(bad.Ok);
        Assert.False(bad.Checks.Single(c => c.Name.StartsWith("BOM direct", StringComparison.Ordinal)).Ok);
        Assert.Contains(bad.Checks, c => c.Name.StartsWith("as-of-serial explosion checksum", StringComparison.Ordinal) && !c.Ok);
    }
}
