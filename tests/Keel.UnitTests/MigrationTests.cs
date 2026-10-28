using Keel.Core;
using Keel.Migration;
using Keel.Migration.Canonical;
using Keel.Migration.Fixtures;
using Keel.Migration.Mapping;
using Keel.Migration.Reports;
using Keel.Migration.Sources;

namespace Keel.UnitTests;

/// <summary>Readers, mapping, validation and the markings rules of the migration importer (ADR-0023). No database needed.</summary>
public sealed class MigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keel-mig-unit-" + Guid.NewGuid().ToString("N"));

    public MigrationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private string Dir(string name) => Path.Combine(_root, name);

    private static async Task<PreparedMigration> Prepare(string dir, MappingConfig? cfg = null, string? format = null) =>
        await MigrationPipeline.PrepareAsync(dir, format, cfg ?? Samples.Mapping(), ReferenceData.Defaults(), CancellationToken.None);

    private static IEnumerable<string> Errors(PreparedMigration p) => p.Plan.Problems.Where(x => x.Severity == Severity.Error).Select(x => x.Code);

    private string WriteAll(Defect defects = Defect.None)
    {
        var p = Samples.Build(defects);
        Samples.WriteCanonical(p, Dir("canonical"));
        Samples.WritePlmXml(p, Dir("plmxml"), defects);
        Samples.WriteWindchillJson(p, Dir("wcjson"));
        Samples.WriteWindchillCsv(p, Dir("wccsv"));
        return _root;
    }

    // ------------------------------------------------------------------ readers
    [Theory]
    [InlineData("canonical", "canonical")]
    [InlineData("plmxml", "plmxml")]
    [InlineData("wcjson", "windchill-json")]
    [InlineData("wccsv", "windchill-csv")]
    public async Task Every_reader_produces_the_same_inventory_counts_for_the_same_logical_product(string folder, string format)
    {
        WriteAll();
        var inspect = await MigrationPipeline.InspectAsync(Dir(folder), null, CancellationToken.None);
        Assert.Equal(format, inspect.Format);
        Assert.Empty(inspect.Problems);
        var c = inspect.Inventory.Counts;
        Assert.Equal(13, c["items"]);
        Assert.Equal(2, c["documents"]);
        Assert.Equal(3, c["changes"]);
        Assert.Equal(16, c["itemRevisions"]);
        Assert.Equal(18, c["itemIterations"]);
        Assert.Equal(17, c["usages"]);
        Assert.Equal(3, c["documentRevisions"]);
        Assert.Equal(4, c["documentIterations"]);
        Assert.Equal(5, c["files"]);
        Assert.Equal(3, c["documentLinks"]);
        Assert.Equal(2, c["affectedObjects"]);
        Assert.Equal(1, c.GetValueOrDefault("classifications")); // PLM XML carries it as UserData type Classification on the last revision element
    }

    [Fact]
    public async Task Every_format_maps_to_the_same_plan()
    {
        WriteAll();
        var plans = new List<(string, Keel.Migration.Mapping.Plan)>();
        foreach (var f in new[] { "canonical", "plmxml", "wcjson", "wccsv" })
        {
            using var p = await Prepare(Dir(f));
            Assert.True(!Errors(p).Any(), f + ": " + string.Join("; ", p.Plan.Problems.Where(x => x.Severity == Severity.Error).Select(x => x.Code + " " + x.Message)));
            plans.Add((f, p.Plan));
        }
        static string Sig(Keel.Migration.Mapping.Plan p)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var x in p.Parts)
            {
                sb.Append(x.Number).Append('|').Append(x.PartType).Append('|').Append(x.Unit).Append('|').Append(x.Markings.BannerText());
                foreach (var r in x.Revisions)
                {
                    sb.Append(' ').Append(r.Label).Append('=').Append(r.LegacyLabel).Append(':').Append(r.State);
                    foreach (var i in r.Iterations)
                    {
                        sb.Append(" [").Append(i.Sequence).Append(':');
                        foreach (var u in i.Usages) sb.Append(u.FindNumber).Append('>').Append(u.ChildSourceId).Append('x').Append(u.Quantity).Append(u.Unit).Append(';');
                        sb.Append(']');
                    }
                }
                sb.AppendLine();
            }
            foreach (var d in p.Documents)
            {
                sb.Append(d.Number).Append('|').Append(d.Markings.BannerText());
                foreach (var r in d.Revisions)
                {
                    sb.Append(' ').Append(r.Label).Append(':').Append(r.State);
                    foreach (var i in r.Iterations) foreach (var f in i.Files) sb.Append(' ').Append(f.Sha256[..8]);
                }
                sb.AppendLine();
            }
            foreach (var c in p.Changes) sb.Append(c.ChangeType).Append('|').Append(c.LegacyNumber).Append('|').Append(c.State).Append('|').Append(c.ChangeClass).Append('|').Append(c.Affected.Count).AppendLine();
            return sb.ToString();
        }
        var reference = Sig(plans[0].Item2);
        foreach (var (name, plan) in plans.Skip(1)) Assert.True(reference == Sig(plan), $"{name} differs from canonical:\n{Sig(plan)}\n---\n{reference}");
    }

    [Fact]
    public async Task Plm_xml_reports_unknown_elements_and_preserves_the_ones_inside_known_owners()
    {
        WriteAll(Defect.UnknownElement);
        var inspect = await MigrationPipeline.InspectAsync(Dir("plmxml"), null, CancellationToken.None);
        Assert.Equal(1, inspect.Inventory.UnknownElements["Annotation"].Count);
        Assert.False(inspect.Inventory.UnknownElements["Annotation"].Preserved);
        Assert.True(inspect.Inventory.UnknownElements["Weird"].Preserved);
        Assert.True(inspect.Inventory.IgnoredElements.ContainsKey("Transform"));
        using var p = await Prepare(Dir("plmxml"));
        Assert.Contains(p.Sink.Package.Items.First(i => i.Number == "P-2001").Properties.Keys, k => k.StartsWith("xml:Weird", StringComparison.Ordinal));
        Assert.Contains(p.Plan.UnmappedProperties.Keys, k => k.StartsWith("xml:Weird", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plm_xml_with_a_dangling_idref_is_reported_as_a_source_error()
    {
        WriteAll(Defect.DanglingIdRef);
        using var p = await Prepare(Dir("plmxml"));
        Assert.Contains(p.Plan.Problems, x => x.Code == "SRC-DANGLING-REF" && x.Severity == Severity.Error);
        Assert.True(p.Plan.SourceErrors > 0);
        Assert.False(ReportBuilder_Succeeded(p));
    }

    [Fact]
    public async Task Plm_xml_with_dtd_is_refused_not_expanded()
    {
        Directory.CreateDirectory(Dir("evil"));
        await File.WriteAllTextAsync(Path.Combine(Dir("evil"), "x.plmxml"), "<?xml version=\"1.0\"?><!DOCTYPE a [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><PLMXML>&x;</PLMXML>");
        var inspect = await MigrationPipeline.InspectAsync(Dir("evil"), null, CancellationToken.None);
        Assert.Contains(inspect.Problems, p => p.Code == "SRC-PARSE");
    }

    [Fact]
    public async Task Csv_reader_handles_quotes_commas_and_embedded_newlines()
    {
        var rows = await WindchillCsvReader.ParseAsync(new StringReader("a,b,c\n1,\"x, y\",\"he said \"\"hi\"\"\nthere\"\n2,,z\n"), CancellationToken.None);
        Assert.Equal(3, rows.Count);
        Assert.Equal(["1", "x, y", "he said \"hi\"\nthere"], rows[1]);
        Assert.Equal(["2", "", "z"], rows[2]);
    }

    [Fact]
    public async Task Canonical_reader_rejects_unknown_members()
    {
        Directory.CreateDirectory(Dir("c2"));
        await File.WriteAllTextAsync(Path.Combine(Dir("c2"), "package.json"), "{\"formatVersion\":1,\"sourceSystem\":\"x\",\"items\":[],\"surprise\":1}");
        var inspect = await MigrationPipeline.InspectAsync(Dir("c2"), null, CancellationToken.None);
        Assert.Contains(inspect.Problems, p => p.Code == "SRC-PARSE");
    }

    [Fact]
    public void Package_paths_that_leave_the_package_are_refused()
    {
        Assert.Null(PackageSource.Normalize("../secret"));
        Assert.Null(PackageSource.Normalize("/etc/passwd"));
        Assert.Null(PackageSource.Normalize("C:\\x"));
        Assert.Equal("files/a.pdf", PackageSource.Normalize("files\\a.pdf"));
    }

    [Fact]
    public async Task Zip_packages_are_read_like_directories()
    {
        WriteAll();
        var zip = Path.Combine(_root, "p.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(Dir("canonical"), zip);
        using var p = await Prepare(zip, format: "canonical");
        Assert.Empty(Errors(p));
        using var dir = await Prepare(Dir("canonical"));
        Assert.Equal(dir.Manifest.Sha256, p.Manifest.Sha256);
    }

    // ------------------------------------------------------------------ mapping and validation
    [Fact]
    public async Task Clean_sample_validates_and_maps_revision_labels_in_source_order_keeping_the_legacy_label()
    {
        WriteAll();
        using var p = await Prepare(Dir("canonical"));
        Assert.Empty(Errors(p));
        var p2002 = p.Plan.Parts.Single(x => x.Number == "P-2002");
        Assert.Equal(["A", "B"], p2002.Revisions.Select(r => r.Label));
        Assert.Equal(["01", "02"], p2002.Revisions.Select(r => r.LegacyLabel));
        Assert.Contains(p.Plan.RevisionMaps, m => m is { ItemNumber: "P-2002", SourceLabel: "02", KeelLabel: "B" });
        Assert.Equal("Assembly", p.Plan.Parts.Single(x => x.Number == "A-1100").PartType);
        Assert.Equal("RawMaterial", p.Plan.Parts.Single(x => x.Number == "P-3001").PartType);
        Assert.Equal("kg", p.Plan.Parts.Single(x => x.Number == "P-3001").Unit);
        Assert.Equal("EA", p.Plan.Parts.Single(x => x.Number == "A-1000").Unit);
        Assert.Equal("alice", p.Plan.Parts.Single(x => x.Number == "A-1000").CreatedBy);
        Assert.Contains(p.Plan.Problems, x => x.Code == "CLASS-STAGED");
    }

    [Theory]
    [InlineData(Defect.DuplicateNumber, "DUP-NUMBER")]
    [InlineData(Defect.UnknownState, "STATE-UNMAPPED")]
    [InlineData(Defect.BadUnit, "UNIT-UNMAPPED")]
    [InlineData(Defect.BomCycle, "BOM-CYCLE")]
    [InlineData(Defect.MissingFile, "FILE-MISSING")]
    [InlineData(Defect.ShaMismatch, "FILE-SHA-MISMATCH")]
    [InlineData(Defect.MarkingsMissing, "MARKINGS-UNDETERMINED")]
    [InlineData(Defect.MarkingsInvalid, "MARKINGS-INVALID")]
    [InlineData(Defect.RevisionAmbiguous, "REV-ORDER-AMBIGUOUS")]
    [InlineData(Defect.HugeAttribute, "ATTR-TOO-LONG")]
    public async Task Malformed_variants_are_reported_with_their_code_and_the_object_is_not_planned(Defect defect, string code)
    {
        WriteAll(defect);
        using var p = await Prepare(Dir("canonical"));
        Assert.Contains(code, Errors(p));
        // the offending object is quarantined, never planned
        Assert.NotEmpty(p.Plan.Failed);
        foreach (var key in p.Plan.Failed.Where(k => k.StartsWith("part", StringComparison.Ordinal)))
            Assert.DoesNotContain(p.Plan.Parts, x => Plan.Key("part", x.SourceId) == key);
    }

    [Fact]
    public async Task Huge_properties_are_reported_and_a_failing_item_cascades_to_the_assemblies_that_use_it()
    {
        WriteAll(Defect.HugeAttribute);
        using var p = await Prepare(Dir("canonical"));
        Assert.Contains("PROP-VALUE-TOO-LARGE", Errors(p));
        // P-2001 failed, A-1000 uses it: A-1000 must not be imported with a silently missing line
        Assert.Contains(p.Plan.Problems, x => x is { Code: "DEPENDENCY-FAILED", SourceId: "A-1000" });
        Assert.DoesNotContain(p.Plan.Parts, x => x.Number is "P-2001" or "A-1000");
    }

    [Fact]
    public async Task Windchill_formats_report_the_same_codes_as_the_canonical_package()
    {
        WriteAll(Defect.BadUnit | Defect.UnknownState);
        foreach (var f in new[] { "wcjson", "wccsv", "plmxml" })
        {
            using var p = await Prepare(Dir(f));
            Assert.Contains("UNIT-UNMAPPED", Errors(p));
            Assert.Contains("STATE-UNMAPPED", Errors(p));
        }
    }

    [Fact]
    public async Task Default_markings_import_the_object_and_list_it_for_review_but_never_override_a_rule()
    {
        WriteAll(Defect.MarkingsMissing);
        using var p = await Prepare(Dir("canonical"), Samples.Mapping(defaultMarkings: true));
        Assert.DoesNotContain("MARKINGS-UNDETERMINED", Errors(p));
        var defaulted = Assert.Single(p.Plan.Defaulted);
        Assert.Equal("P-2008", defaulted.Number);
        // objects that rules determined keep exactly the rules' markings (the default is not merged into them)
        var top = p.Plan.Parts.Single(x => x.Number == "A-1000");
        Assert.Equal(DistributionStatement.D, top.Markings.DistributionStatement);
        Assert.False(top.Defaulted);
        var open = p.Plan.Parts.Single(x => x.Number == "A-1200");
        Assert.Equal(ExportJurisdiction.None, open.Markings.ExportJurisdiction);
    }

    [Fact]
    public async Task Imported_changes_are_raised_to_cover_the_parts_they_list()
    {
        WriteAll();
        var cfg = Samples.Mapping();
        // the change itself is classified open by its own rule input, but it lists ITAR parts: it must be raised, never weaker than its parts
        var pkg = Samples.Build();
        foreach (var c in pkg.Package.Changes) { c.Properties["ITAR"] = "N"; c.Properties.Remove("Project"); }
        Samples.WriteCanonical(pkg, Dir("raised"));
        using var p = await Prepare(Dir("raised"), cfg);
        var ecn = p.Plan.Changes.Single(c => c.ChangeType == "ECN");
        Assert.True(ecn.MarkingsRaised);
        Assert.Equal(ExportJurisdiction.Itar, ecn.Markings.ExportJurisdiction);
        Assert.Equal("ORCA", ecn.Markings.Program);
        Assert.Equal(DistributionStatement.D, ecn.Markings.DistributionStatement);
    }

    // ------------------------------------------------------------------ the markings rules in isolation
    private static Dictionary<string, string?> Ctx(params (string, string?)[] kv) => kv.ToDictionary(k => k.Item1, k => k.Item2);

    private static MarkingsMapper MapperWith(MarkingSpec? defaults, params MarkingRule[] rules) =>
        new(new MarkingsMapping { Rules = [.. rules], DefaultMarkings = defaults });

    private static MarkingRule Rule(string name, string prop, string equals, MarkingSpec set) =>
        new() { Name = name, When = new MarkingCondition { Property = prop, EqualTo = equals }, Set = set };

    private static readonly MarkingSpec ItarSpec = new() { ExportJurisdiction = "Itar", UsmlCategory = "XI(d)", Cui = ["EXPT"], DistributionStatement = "D", ControllingOffice = "ACME", DistributionReason = "r", Program = "ORCA", OwningOrg = "ENG" };
    private static readonly MarkingSpec OpenSpec = new() { ExportJurisdiction = "None", DistributionStatement = "A", OwningOrg = "ENG" };

    [Fact]
    public void An_object_without_determinable_markings_fails_closed()
    {
        var d = MapperWith(null, Rule("itar", "ITAR", "Y", ItarSpec)).Decide([Ctx(("Other", "x"))], "part");
        Assert.Null(d.Markings);
        Assert.NotEmpty(d.Errors);
        // unknown property values do not match "N" either: no rule, no markings
        d = MapperWith(null, Rule("itar", "ITAR", "Y", ItarSpec), Rule("open", "ITAR", "N", OpenSpec)).Decide([Ctx(("ITAR", "maybe"))], "part");
        Assert.Null(d.Markings);
    }

    [Fact]
    public void Contradicting_rules_never_loosen_and_a_later_iteration_can_only_tighten()
    {
        var m = MapperWith(null, Rule("itar", "ITAR", "Y", ItarSpec), Rule("open", "ITAR", "N", OpenSpec));
        // iteration 1 says open, iteration 2 says ITAR: the object is ITAR
        var d = m.Decide([Ctx(("ITAR", "N")), Ctx(("ITAR", "Y"))], "part");
        Assert.Equal(ExportJurisdiction.Itar, d.Markings!.ExportJurisdiction);
        // and the other way round
        d = m.Decide([Ctx(("ITAR", "Y")), Ctx(("ITAR", "N"))], "part");
        Assert.Equal(ExportJurisdiction.Itar, d.Markings!.ExportJurisdiction);
    }

    [Fact]
    public void Two_rules_that_disagree_on_a_scalar_are_an_error_not_a_guess()
    {
        var other = new MarkingSpec { ExportJurisdiction = "Itar", UsmlCategory = "VIII(h)", Cui = ["EXPT"], DistributionStatement = "D", ControllingOffice = "ACME", DistributionReason = "r", Program = "ORCA", OwningOrg = "ENG" };
        var d = MapperWith(null, Rule("a", "X", "1", ItarSpec), Rule("b", "Y", "1", other)).Decide([Ctx(("X", "1"), ("Y", "1"))], "part");
        Assert.Null(d.Markings);
        Assert.Contains(d.Errors, e => e.Contains("USML", StringComparison.Ordinal));
    }

    [Fact]
    public void The_default_is_applied_only_when_no_rule_sets_a_jurisdiction_and_never_weakens_a_rule()
    {
        var def = new MarkingSpec { ExportJurisdiction = "Itar", UsmlCategory = "XI(d)", Cui = ["EXPT"], DistributionStatement = "F", ControllingOffice = "ACME", DistributionReason = "r", Program = "ORCA", OwningOrg = "ENG" };
        var m = MapperWith(def, Rule("open", "ITAR", "N", OpenSpec));
        var matched = m.Decide([Ctx(("ITAR", "N"))], "part");
        Assert.False(matched.Defaulted);
        Assert.Equal(ExportJurisdiction.None, matched.Markings!.ExportJurisdiction);
        var unmatched = m.Decide([Ctx(("ITAR", "?"))], "part");
        Assert.True(unmatched.Defaulted);
        Assert.Equal(DistributionStatement.F, unmatched.Markings!.DistributionStatement);
    }

    [Fact]
    public void Randomized_rule_sets_never_produce_markings_weaker_than_any_matching_rule()
    {
        var rng = new Random(20260428);
        var specs = new[]
        {
            ItarSpec, OpenSpec,
            new MarkingSpec { ExportJurisdiction = "Ear", Eccn = "9A610.x", Cui = ["EXPT"], DistributionStatement = "C", ControllingOffice = "ACME", DistributionReason = "r", OwningOrg = "ENG" },
            new MarkingSpec { ExportJurisdiction = "Ear", Eccn = "EAR99", DistributionStatement = "B", ControllingOffice = "ACME", DistributionReason = "r", OwningOrg = "ENG" },
        };
        static int Rank(Markings m) => m.ExportJurisdiction == ExportJurisdiction.Itar ? 3 : m.ExportJurisdiction == ExportJurisdiction.Ear && m.Eccn != Markings.Ear99 ? 2 : m.ExportJurisdiction == ExportJurisdiction.Ear ? 1 : 0;
        var checkedObjects = 0;
        for (var round = 0; round < 500; round++)
        {
            var rules = Enumerable.Range(0, 4).Select(i => Rule("r" + i, "P" + i, "1", specs[rng.Next(specs.Length)])).ToArray();
            var mapper = MapperWith(null, rules);
            var contexts = Enumerable.Range(0, 1 + rng.Next(3)).Select(_ => Ctx(Enumerable.Range(0, 4).Where(_ => rng.Next(2) == 0).Select(i => ("P" + i, (string?)"1")).ToArray())).ToList();
            var d = mapper.Decide(contexts, "part");
            var matchedSpecs = contexts.SelectMany(c => rules.Where(r => c.ContainsKey(r.When.Property!))).Select(r => r.Set).ToList();
            if (d.Markings is null) continue; // an error is always allowed: it imports nothing
            checkedObjects++;
            var maxRule = matchedSpecs.Max(s => s == ItarSpec ? 3 : s.ExportJurisdiction == "Ear" && s.Eccn != "EAR99" ? 2 : s.ExportJurisdiction == "Ear" ? 1 : 0);
            Assert.True(Rank(d.Markings) >= maxRule, "result is weaker than a matching rule");
            var maxDist = matchedSpecs.Max(s => Enum.Parse<DistributionStatement>(s.DistributionStatement!));
            Assert.True(d.Markings.DistributionStatement >= maxDist);
            if (matchedSpecs.Any(s => s.Cui is { Count: > 0 })) Assert.Contains("EXPT", d.Markings.CuiCategories);
        }
        Assert.True(checkedObjects > 50, "the randomized test checked too few objects to mean anything");
    }

    // ------------------------------------------------------------------ mapping file
    [Fact]
    public void Mapping_file_with_a_typo_is_an_error_and_names_are_case_insensitive_after_parsing()
    {
        Assert.Throws<MappingException>(() => MappingConfig.Parse("{ \"lifecyle\": {} }"));
        var cfg = MappingConfig.Parse("""
            { // comments are allowed
              "lifecycle": { "states": { "RELEASED": "Released" }, },
              "units": { "map": { "Each": "EA" } } }
            """);
        Assert.Equal("Released", cfg.Lifecycle.States["released"]);
        Assert.Equal("EA", cfg.Units.Map["EACH"]);
        Assert.Equal(cfg.Hash(), MappingConfig.Parse(MappingConfig_Json(cfg)).Hash());
    }

    private static string MappingConfig_Json(MappingConfig cfg) => System.Text.Json.JsonSerializer.Serialize(cfg, MappingConfig.Json);

    [Fact]
    public void Mapping_targets_are_checked_against_keel_reference_data()
    {
        var cfg = Samples.Mapping();
        cfg.Lifecycle.States["Weird"] = "Frozen";
        cfg.Units.Map["x"] = "furlong";
        cfg.Attributes.Map["Foo"] = "nonexistent";
        cfg.Markings.Rules.Add(new MarkingRule { Name = "bad", When = new MarkingCondition { Property = "a", Matches = "(" }, Set = new MarkingSpec() });
        var codes = Mapper.ValidateConfig(cfg, ReferenceData.Defaults()).Select(p => p.Code).ToList();
        Assert.Contains("MAP-STATE-TARGET", codes);
        Assert.Contains("MAP-UNIT-TARGET", codes);
        Assert.Contains("MAP-ATTR-TARGET", codes);
        Assert.Contains("MAP-RULE-REGEX", codes);
    }

    [Fact]
    public async Task Report_hash_is_stable_and_detects_edits()
    {
        WriteAll();
        using var a = await Prepare(Dir("canonical"));
        using var b = await Prepare(Dir("canonical"));
        var pipeline = new MigrationPipeline(null, null, null, TimeProvider.System);
        var ra = pipeline.Validate(a, new Keel.Migration.Load.LoadOptions());
        var rb = pipeline.Validate(b, new Keel.Migration.Load.LoadOptions());
        Assert.Equal(ra.Hash, rb.Hash);
        Assert.True(ra.HashIsValid());
        var edited = MigrationReport.FromJson(ra.ToJson());
        Assert.True(edited.HashIsValid());
        edited.Planned["parts"]++;
        Assert.False(edited.HashIsValid());
        edited = MigrationReport.FromJson(ra.ToJson());
        edited.Problems.Add(new Problem(Severity.Error, "X", "part", "y", "z"));
        Assert.False(edited.HashIsValid());
        Assert.StartsWith("severity,code", ra.ToCsv(), StringComparison.Ordinal);
        Assert.Contains("CUI-handling artifact", ra.ToMarkdown(), StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_output_neutralizes_spreadsheet_formulas()
    {
        var r = new MigrationReport { Problems = [new Problem(Severity.Error, "X", "part", "=cmd|' /C calc'!A0", "m")] };
        Assert.Contains("'=cmd", r.ToCsv(), StringComparison.Ordinal);
    }

    private static bool ReportBuilder_Succeeded(PreparedMigration p) =>
        new MigrationPipeline(null, null, null, TimeProvider.System).Validate(p, new Keel.Migration.Load.LoadOptions()).Succeeded;
}
