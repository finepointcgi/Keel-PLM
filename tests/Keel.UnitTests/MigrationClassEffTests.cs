using Keel.Core;
using Keel.Migration;
using Keel.Migration.Canonical;
using Keel.Migration.Fixtures;
using Keel.Migration.Mapping;

namespace Keel.UnitTests;

/// <summary>Mapping and validation of classification and effectivity in the migration importer (FUP-2, FUP-3). Offline: no database needed.</summary>
public sealed class MigrationClassEffTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keel-mig-ce-" + Guid.NewGuid().ToString("N"));

    public MigrationClassEffTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private string Dir(string name) => Path.Combine(_root, name);

    private static async Task<PreparedMigration> Prepare(string dir, MappingConfig cfg, ReferenceData? rd = null) =>
        await MigrationPipeline.PrepareAsync(dir, null, cfg, rd ?? ReferenceData.Defaults(), CancellationToken.None);

    private static List<Problem> AllErrors(PreparedMigration p) => [.. p.Plan.Problems.Where(x => x.Severity == Severity.Error)];

    /// <summary>The errors of the feature under test: a failed part also cascades (DEPENDENCY-FAILED, CHG-AFFECTED-MISSING) into the objects that need it.</summary>
    private static List<Problem> Errors(PreparedMigration p) => [.. AllErrors(p).Where(x => x.Code is not ("DEPENDENCY-FAILED" or "CHG-AFFECTED-MISSING"))];

    private async Task<PreparedMigration> PrepareCanonical(SampleProduct product, MappingConfig cfg, ReferenceData? rd = null)
    {
        var dir = Dir(Guid.NewGuid().ToString("N"));
        Samples.WriteCanonical(product, dir);
        return await Prepare(dir, cfg, rd);
    }

    private string WriteAll(SampleProduct p)
    {
        Samples.WriteCanonical(p, Dir("canonical"));
        Samples.WritePlmXml(p, Dir("plmxml"));
        Samples.WriteWindchillJson(p, Dir("wcjson"));
        Samples.WriteWindchillCsv(p, Dir("wccsv"));
        return _root;
    }

    // ---------------------------------------------------------------- classification

    [Fact]
    public async Task Every_reader_stages_the_same_hierarchy_and_assignments_and_the_plan_creates_the_classes_parents_first()
    {
        WriteAll(Samples.BuildClassified());
        var signatures = new List<string>();
        foreach (var f in new[] { "canonical", "plmxml", "wcjson", "wccsv" })
        {
            using var p = await Prepare(Dir(f), Samples.ClassificationMapping());
            Assert.True(AllErrors(p).Count == 0, f + ": " + string.Join("; ", AllErrors(p).Select(e => e.Code + " " + e.SourceId + " " + e.Message)));
            var plan = p.Plan;
            Assert.Equal(["ELEC", "ELEC.RES", "FASTENERS", "FASTENERS.BRACKET", "MECH"], plan.ClassesToCreate.Select(c => c.Code).Order(StringComparer.Ordinal));
            var order = plan.ClassesToCreate.Select(c => c.Code).ToList();
            Assert.All(plan.ClassesToCreate.Where(c => c.ParentCode is not null), c => Assert.True(order.IndexOf(c.ParentCode!) < order.IndexOf(c.Code), c.Code)); // parents first
            Assert.Equal(["P-2001", "P-2005"], plan.Classifications.Select(c => c.PartNumber).Order(StringComparer.Ordinal));
            // P-2001 has three iterations (A.1, A.2, B.1), P-2005 one: the item-level class goes onto every iteration.
            Assert.Equal(3, plan.ClassIterations["FASTENERS.BRACKET"]);
            Assert.Equal(1, plan.ClassIterations["ELEC.RES"]);
            var bracket = plan.Classifications.Single(c => c.PartNumber == "P-2001");
            Assert.Equal("""{"grade":"316","width":20}""", bracket.AttributesJson); // normalized: mapped name "width", number as number, stable key order
            Assert.All(plan.Parts.Single(x => x.Number == "P-2001").Revisions.SelectMany(r => r.Iterations), i => Assert.Equal("FASTENERS.BRACKET", i.ClassCode));
            Assert.All(plan.Parts.Where(x => x.Number != "P-2001" && x.Number != "P-2005").SelectMany(x => x.Revisions).SelectMany(r => r.Iterations), i => Assert.Null(i.ClassCode));
            signatures.Add(string.Join("|", plan.ClassesToCreate.Select(c => $"{c.Code}:{c.ParentCode}:{string.Join(",", c.Attributes.Select(a => a.Name + a.DataType))}")) + string.Join(",", plan.Classifications.Select(c => c.PartNumber + c.AttributesJson)));
        }
        Assert.Single(signatures.Distinct());
    }

    [Fact]
    public async Task Without_classification_load_assignments_are_only_staged()
    {
        using var p = await PrepareCanonical(Samples.BuildClassified(), Samples.Mapping());
        Assert.Empty(Errors(p));
        Assert.Contains(p.Plan.Problems, x => x.Code == "CLASS-STAGED");
        Assert.Empty(p.Plan.ClassesToCreate);
        Assert.All(p.Plan.Parts.SelectMany(x => x.Revisions).SelectMany(r => r.Iterations), i => Assert.Null(i.ClassCode));
    }

    [Fact]
    public async Task Invalid_values_are_validation_errors_with_the_source_id_and_the_part_is_quarantined()
    {
        var product = Samples.BuildClassified();
        product.Package.Classifications[0].Attributes["Width"] = "wide";
        using var p = await PrepareCanonical(product, Samples.ClassificationMapping());
        var e = Assert.Single(Errors(p));
        Assert.Equal(("CLASS-VALUE-INVALID", "part", "P-2001"), (e.Code, e.EntityType, e.SourceId));
        Assert.Contains("Width must be a number", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(p.Plan.Parts, x => x.Number == "P-2001"); // the part is not imported with a guessed class
        Assert.Contains(p.Plan.Parts, x => x.Number == "P-2005");
    }

    [Theory]
    [InlineData("Width", "600", "CLASS-VALUE-INVALID", "at most 500")]
    [InlineData("grade", "999", "CLASS-VALUE-INVALID", "one of: 304, 316")]
    [InlineData("coating", "this coating text is far too long for the limit", "CLASS-VALUE-INVALID", "at most 20")]
    [InlineData("colour", "red", "CLASS-ATTR-UNKNOWN", "colour")]
    public async Task Values_are_checked_against_the_effective_attribute_set_including_inherited_attributes(string attribute, string value, string code, string text)
    {
        var product = Samples.BuildClassified();
        product.Package.Classifications[0].Attributes[attribute] = value;
        using var p = await PrepareCanonical(product, Samples.ClassificationMapping());
        var e = Assert.Single(Errors(p));
        Assert.Equal(code, e.Code);
        Assert.Contains(text, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_required_value_and_an_unknown_class_are_errors()
    {
        var product = Samples.BuildClassified();
        product.Package.Classifications[0].Attributes.Remove("Width");
        product.Package.Classifications[1].ClassCode = "NO.SUCH.CLASS";
        using var p = await PrepareCanonical(product, Samples.ClassificationMapping());
        Assert.Equal(["CLASS-UNKNOWN", "CLASS-VALUE-INVALID"], Errors(p).Select(x => x.Code).Order(StringComparer.Ordinal));
        Assert.Contains(Errors(p), x => x.Code == "CLASS-VALUE-INVALID" && x.Message.Contains("Width is required", StringComparison.Ordinal));
    }

    [Fact]
    public async Task One_class_per_part_and_a_repeated_identical_assignment_is_one()
    {
        var product = Samples.BuildClassified();
        product.Package.Classifications.Add(new CClassAssignment { ItemSourceId = "P-2005", ClassCode = "ELEC.RES", Attributes = { ["voltage"] = "5" } }); // PLM XML repeats it per revision element
        using (var p = await PrepareCanonical(product, Samples.ClassificationMapping())) Assert.Empty(Errors(p));
        product.Package.Classifications.Add(new CClassAssignment { ItemSourceId = "P-2005", ClassCode = "FASTENERS", Attributes = { } });
        using var q = await PrepareCanonical(product, Samples.ClassificationMapping());
        Assert.Equal("CLASS-MULTIPLE", Assert.Single(Errors(q)).Code);
    }

    [Fact]
    public async Task Class_and_attribute_mapping_and_the_latest_iteration_scope_are_applied()
    {
        var product = Samples.BuildClassified();
        product.Package.Classifications[0].ClassCode = "SRC-BRACKET";
        product.Package.Classifications[0].Attributes.Remove("Width");
        product.Package.Classifications[0].Attributes["WIDTH_MM"] = "35";
        var cfg = Samples.ClassificationMapping();
        cfg.Classification.ClassMap["src-bracket"] = "FASTENERS.BRACKET";
        cfg.Classification.Attributes["SRC-BRACKET"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["WIDTH_MM"] = "width" };
        cfg.Classification.Scope = "latestIteration";
        using var p = await PrepareCanonical(product, cfg);
        Assert.Empty(Errors(p));
        Assert.Equal("""{"grade":"316","width":35}""", p.Plan.Classifications.Single(c => c.PartNumber == "P-2001").AttributesJson);
        Assert.Equal(1, p.Plan.ClassIterations["FASTENERS.BRACKET"]);
        var iterations = p.Plan.Parts.Single(x => x.Number == "P-2001").Revisions.SelectMany(r => r.Iterations).ToList();
        Assert.Equal([null, null, "FASTENERS.BRACKET"], iterations.Select(i => i.ClassCode)); // only the last iteration of the last revision
    }

    [Fact]
    public async Task Hierarchy_errors_are_reported_per_class_and_stop_the_assignments_that_need_them()
    {
        var product = Samples.BuildClassified();
        product.Package.ClassNodes.Add(new CClassNode { Code = "ORPHAN", Name = "Orphan", Parent = "NOWHERE" });
        product.Package.ClassNodes.Add(new CClassNode { Code = "CYC-A", Name = "A", Parent = "CYC-B" });
        product.Package.ClassNodes.Add(new CClassNode { Code = "CYC-B", Name = "B", Parent = "CYC-A" });
        product.Package.ClassNodes.Add(new CClassNode { Code = "elec", Name = "Duplicate by case" });
        product.Package.ClassNodes.Add(new CClassNode { Code = "BAD CODE", Name = "x" });
        product.Package.ClassNodes.Add(new CClassNode { Code = "CLASH", Name = "Clash", Parent = "MECH", Attributes = [new CClassAttribute { Name = "grade", DataType = "string" }] });
        product.Package.ClassNodes.Add(new CClassNode { Code = "RETIRED-ONE", Name = "Retired", Status = "retired" });
        product.Package.Classifications.Add(new CClassAssignment { ItemSourceId = "P-2003", ClassCode = "RETIRED-ONE" });
        using var p = await PrepareCanonical(product, Samples.ClassificationMapping());
        var codes = Errors(p).Select(x => x.Code).ToList();
        Assert.Contains("CLASS-PARENT-MISSING", codes);
        Assert.Contains("CLASS-CYCLE", codes);
        Assert.Contains("CLASS-DUP", codes);
        Assert.Contains("CLASS-CODE-INVALID", codes);
        Assert.Contains("CLASS-ATTR-CLASH", codes);
        Assert.Contains(Errors(p), x => x.Code == "CLASS-RETIRED" && x.SourceId == "P-2003");
        Assert.Contains(p.Plan.Problems, x => x.Code == "CLASS-RETIRED-SKIPPED");
        // The good part of the hierarchy is still planned; the failed classes are not created.
        Assert.Contains(p.Plan.ClassesToCreate, c => c.Code == "ELEC.RES");
        Assert.DoesNotContain(p.Plan.ClassesToCreate, c => c.Code is "ORPHAN" or "CYC-A" or "CLASH" or "RETIRED-ONE");
    }

    [Fact]
    public async Task Existing_classes_win_conflicts_are_errors_and_differences_are_warnings()
    {
        var product = Samples.BuildClassified();
        var rd = ReferenceData.Defaults();
        // Keel already has MECH (same), FASTENERS under another parent (conflict), FASTENERS.BRACKET with width as text (type mismatch) and ELEC retired.
        rd.Classes["MECH"] = new ClassDefinition("MECH", null, true, [new AttributeSpec("grade", "Material grade", "enum", null, ["304", "316", "17-4"], false, null, 0)]);
        rd.Classes["FASTENERS"] = new ClassDefinition("FASTENERS", "ELEC", true, []);
        rd.Classes["ELEC"] = new ClassDefinition("ELEC", null, false, []);
        rd.Classes["FASTENERS.BRACKET"] = new ClassDefinition("FASTENERS.BRACKET", "FASTENERS", true, [new AttributeSpec("width", "Width", "string", null, null, false, 10, 0)]);
        using var p = await PrepareCanonical(product, Samples.ClassificationMapping(), rd);
        Assert.Contains(Errors(p), x => x.Code == "CLASS-CONFLICT" && x.SourceId == "FASTENERS");
        Assert.Contains(Errors(p), x => x.Code == "CLASS-ATTR-TYPE-MISMATCH" && x.SourceId == "FASTENERS.BRACKET");
        Assert.Contains(p.Plan.Problems, x => x.Code == "CLASS-ATTR-DIFFERS" && x.SourceId == "MECH" && x.Severity == Severity.Warning);
        Assert.Contains(p.Plan.Problems, x => x.Code == "CLASS-RETIRED-IN-KEEL" && x.SourceId == "ELEC");
        // The assignment into a class that failed cannot be loaded; neither can one into the retired class itself.
        Assert.Contains(Errors(p), x => x.Code == "CLASS-FAILED" && x.SourceId == "P-2001");
        Assert.Contains(Errors(p), x => x.Code == "CLASS-FAILED" && x.SourceId == "P-2005"); // ELEC.RES sits under the retired ELEC
        Assert.Contains(Errors(p), x => x.Code == "CLASS-PARENT-RETIRED" && x.SourceId == "ELEC.RES");
        product.Package.Classifications.Add(new CClassAssignment { ItemSourceId = "P-2003", ClassCode = "ELEC" });
        using var q = await PrepareCanonical(product, Samples.ClassificationMapping(), rd);
        Assert.Contains(Errors(q), x => x.Code == "CLASS-RETIRED" && x.SourceId == "P-2003");
        // Nothing existing is created again.
        Assert.DoesNotContain(p.Plan.ClassesToCreate, c => c.Code is "MECH" or "FASTENERS" or "ELEC" or "FASTENERS.BRACKET");
    }

    [Fact]
    public async Task Without_createMissingClasses_every_used_class_must_already_exist()
    {
        var cfg = Samples.ClassificationMapping();
        cfg.Classification.CreateMissingClasses = false;
        using var p = await PrepareCanonical(Samples.BuildClassified(), cfg);
        Assert.Equal(["CLASS-UNKNOWN", "CLASS-UNKNOWN"], Errors(p).Select(x => x.Code));
        Assert.Empty(p.Plan.ClassesToCreate);
    }

    // ---------------------------------------------------------------- effectivity

    private static CObject Item(SampleProduct s, string n) => s.Package.Items.First(i => i.Number == n);

    private static string Sig(Plan p) => string.Join("\n", p.Parts.SelectMany(x => x.Revisions.SelectMany(r => r.Iterations.Select(i =>
        $"{x.Number} {r.Label}.{i.Sequence}: " + string.Join(";", i.Usages.Select(u => $"{u.FindNumber}>{u.ChildSourceId}x{u.Quantity}[{u.Effectivity?.Describe()}]{u.Notes}"))))));

    [Fact]
    public async Task Every_reader_maps_serial_lot_and_date_effectivity_to_the_same_plan()
    {
        WriteAll(Samples.BuildEffective());
        var signatures = new List<string>();
        foreach (var f in new[] { "canonical", "plmxml", "wcjson", "wccsv" })
        {
            using var p = await Prepare(Dir(f), Samples.EffectivityMapping());
            Assert.True(Errors(p).Count == 0, f + ": " + string.Join("; ", Errors(p).Select(e => e.Code + " " + e.Message)));
            signatures.Add(Sig(p.Plan));
            var a1 = p.Plan.Parts.Single(x => x.Number == "A-1000").Revisions[0].Iterations[0].Usages.Where(u => u.FindNumber == 30).ToList();
            Assert.Equal(["serial 1-100 (ORCA-POD)", "serial 101 and up (ORCA-POD)"], a1.Select(u => u.Effectivity!.Describe()));
            Assert.Equal(["P-2001", "P-2003"], a1.Select(u => u.ChildSourceId));
            var date = p.Plan.Parts.Single(x => x.Number == "A-1000").Revisions[1].Iterations[1].Usages.Single(u => u.FindNumber == 40).Effectivity!;
            Assert.Equal(("date", null, EffectivityRange.DayNumber(new DateOnly(2027, 3, 1)), EffectivityRange.DayNumber(new DateOnly(2027, 6, 30))), (date.Kind, date.Context, date.From, date.To));
            var lots = p.Plan.Parts.Single(x => x.Number == "A-1200").Revisions[0].Iterations[0].Usages.Where(u => u.FindNumber == 10).Select(u => u.Effectivity!.Describe()).ToList();
            Assert.Equal(["lot 1 (ORCA-POD)", "lot 2 (ORCA-POD)"], lots);
            // text-only effectivity stays in the notes of the line
            var text = p.Plan.Parts.Single(x => x.Number == "A-1100").Revisions[0].Iterations[0].Usages.Single(u => u.FindNumber == 20);
            Assert.Null(text.Effectivity);
            Assert.Contains("units 1-50 only (free text)", text.Notes, StringComparison.Ordinal);
            Assert.Equal([1L, 100L, 101L], p.Plan.EffectivitySerials["ORCA-POD"]); // boundaries and their neighbours, chosen from the data
        }
        Assert.Single(signatures.Distinct());
    }

    [Fact]
    public async Task Without_effectivity_load_the_text_is_kept_in_the_notes_and_alternates_need_the_option()
    {
        var cfg = Samples.EffectivityMapping();
        cfg.Effectivity.Load = false;
        using var p = await PrepareCanonical(Samples.BuildEffective(), cfg);
        Assert.Contains(Errors(p), e => e.Code == "BOM-DUP-FIND" && e.Message.Contains("find number 30", StringComparison.Ordinal));
        var one = Samples.BuildEffective();
        foreach (var o in one.Package.Items) foreach (var r in o.Revisions) foreach (var i in r.Iterations) i.Usages.RemoveAll(u => u.FindNumber is 30 or 10 && u.EffectivityRanges.Count > 0 && u.ChildSourceId is "P-2003" or "P-2005");
        using var q = await PrepareCanonical(one, cfg);
        Assert.Empty(Errors(q));
        var line = q.Plan.Parts.Single(x => x.Number == "A-1000").Revisions[0].Iterations[0].Usages.Single(u => u.FindNumber == 30);
        Assert.Null(line.Effectivity);
        Assert.Contains("serial 1-100 (POD-END-ITEM)", line.Notes, StringComparison.Ordinal);
        Assert.Contains("effectivity.load is false", line.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_effectivity_is_reported_per_source_id_with_the_native_rules()
    {
        async Task<List<Problem>> Run(Action<SampleProduct> tweak, MappingConfig? cfg = null)
        {
            var s = Samples.BuildEffective();
            tweak(s);
            using var p = await PrepareCanonical(s, cfg ?? Samples.EffectivityMapping());
            return Errors(p);
        }
        static CUsage Find30(SampleProduct s, string child) => Item(s, "A-1000").Revisions[0].Iterations[0].Usages.Single(u => u.ChildSourceId == child);

        var overlap = await Run(s => Find30(s, "P-2003").EffectivityRanges = [Samples.Serial(Samples.EndItem, "100", null)]);
        var e = Assert.Single(overlap);
        Assert.Equal(("EFF-OVERLAP", "A-1000"), (e.Code, e.SourceId));
        Assert.Contains("find number 30", e.Message, StringComparison.Ordinal);

        Assert.Equal("EFF-MIXED", Assert.Single(await Run(s => Find30(s, "P-2003").EffectivityRanges = [Samples.Lot(Samples.EndItem, "1")])).Code);
        Assert.Equal("EFF-RANGE", Assert.Single(await Run(s => Find30(s, "P-2003").EffectivityRanges = [Samples.Serial(Samples.EndItem, "500", "200")])).Code);
        Assert.Equal("EFF-EMPTY", Assert.Single(await Run(s => Find30(s, "P-2003").EffectivityRanges = [Samples.Serial(Samples.EndItem, null, null)])).Code);
        Assert.Equal("EFF-KIND", Assert.Single(await Run(s => Find30(s, "P-2003").EffectivityRanges = [new CEffectivity { Kind = "batch", Unit = Samples.EndItem, From = "1" }])).Code);
        Assert.Equal("EFF-VALUE", Assert.Single(await Run(s => Find30(s, "P-2003").EffectivityRanges = [Samples.Serial(Samples.EndItem, "0", null)])).Code);
        Assert.Equal("EFF-VALUE", Assert.Single(await Run(s => Find30(s, "P-2003").EffectivityRanges = [Samples.Serial(Samples.EndItem, "abc", null)])).Code);
        Assert.Equal("EFF-VALUE", Assert.Single(await Run(s => Find30(s, "P-2003").EffectivityRanges = [Samples.Lot(Samples.EndItem, "Block 9")])).Code); // unknown block name
        Assert.Equal("EFF-VALUE", Assert.Single(await Run(s => Find30(s, "P-2003").EffectivityRanges = [Samples.DateRange("2300-01-01", null)])).Code);
        // a unit with no mapping (unknown context)
        Assert.Equal("EFF-CONTEXT-UNMAPPED", Assert.Single(await Run(s => Find30(s, "P-2003").EffectivityRanges = [Samples.Serial("OTHER-END-ITEM", "101", null)])).Code);
        Assert.Equal("EFF-CONTEXT-MISSING", Assert.Single(await Run(s => Find30(s, "P-2003").EffectivityRanges = [Samples.Serial("", "101", null)])).Code);
        // the same find number without a range on one of the lines
        Assert.Equal("BOM-DUP-FIND", Assert.Single(await Run(s => Find30(s, "P-2003").EffectivityRanges = [])).Code);
    }

    [Fact]
    public async Task Context_blocks_open_ends_and_dates_are_mapped_in_utc_days()
    {
        var s = Samples.BuildEffective();
        var line = s.Package.Items.First(i => i.Number == "A-1000").Revisions[1].Iterations[1].Usages.Single(u => u.FindNumber == 40);
        // 23:30 at UTC-5 on 30 June is 04:30 UTC on 1 July: the UTC day is 1 July
        line.EffectivityRanges = [Samples.DateRange("2027-03-01", "2027-06-30T23:30:00-05:00")];
        var open = s.Package.Items.First(i => i.Number == "A-1000").Revisions[0].Iterations[0].Usages.Single(u => u.ChildSourceId == "P-2003");
        open.EffectivityRanges = [Samples.Serial(Samples.EndItem, "101", "999", "openEnded")]; // the end is ignored for an open-ended range
        using var p = await PrepareCanonical(s, Samples.EffectivityMapping(), null);
        Assert.Empty(Errors(p));
        var date = p.Plan.Parts.Single(x => x.Number == "A-1000").Revisions[1].Iterations[1].Usages.Single(u => u.FindNumber == 40).Effectivity!;
        Assert.Equal(EffectivityRange.DayNumber(new DateOnly(2027, 7, 1)), date.To);
        var serial = p.Plan.Parts.Single(x => x.Number == "A-1000").Revisions[0].Iterations[0].Usages.Single(u => u.ChildSourceId == "P-2003").Effectivity!;
        Assert.Null(serial.To);
        Assert.Contains(p.Plan.Problems, x => x.Code == "EFF-END-IGNORED");
    }

    [Fact]
    public async Task Contexts_must_exist_in_keel_and_revision_level_effectivity_is_not_supported()
    {
        var rd = ReferenceData.Defaults();
        rd.EffectivityContexts["OTHER"] = new Dictionary<string, int>();
        var withDb = new ReferenceData { FromDatabase = true, LifecycleStates = rd.LifecycleStates, PartAttributes = rd.PartAttributes, EffectivityContexts = rd.EffectivityContexts };
        var product = Samples.BuildEffective();
        var dir = Dir("ctx");
        Samples.WriteCanonical(product, dir);
        using var p = await MigrationPipeline.PrepareAsync(dir, null, Samples.EffectivityMapping(), withDb, CancellationToken.None);
        Assert.Contains(p.Plan.Problems, x => x.Code == "MAP-EFF-CONTEXT"); // ORCA-POD is not a context of this database
        Assert.Contains(p.Plan.Problems, x => x.Code == "MAP-EFF-BLOCKS");

        var cfg = Samples.EffectivityMapping();
        cfg.Effectivity.RevisionLevel = true;
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => Prepare(dir, cfg));
        Assert.Contains("FUP-3", ex.Message, StringComparison.Ordinal);
    }
}
