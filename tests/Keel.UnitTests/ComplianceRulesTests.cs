using System.Text.RegularExpressions;
using Keel.Compliance;
using Keel.Core;
using Keel.Web.Services;
using Keel.Web.Services.Visuals;

namespace Keel.UnitTests;

/// <summary>The pure rules of product compliance (ADR-0040): CAS numbers, evaluation against a list version, statuses, standings, expiry stages, exports and the treemap tree.</summary>
public sealed class ComplianceRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 29);
    private static readonly Guid Pb = Guid.NewGuid(), Cd = Guid.NewGuid(), Au = Guid.NewGuid();

    private static ListVersionData Rohs(params ListExemption[] exemptions) => new("rohs", "RoHS", ListKinds.Restricted, Guid.NewGuid(), 1, new DateOnly(2020, 1, 1),
        new Dictionary<Guid, ListEntry>
        {
            [Pb] = new(Pb, "SUB-000001", "7439-92-1", "Lead", 0.1m, null),
            [Cd] = new(Cd, "SUB-000003", "7440-43-9", "Cadmium", 0.01m, null),
        }, exemptions);

    private static readonly ListVersionData Conflict = new("conflict_minerals", "Conflict minerals", ListKinds.Conflict, Guid.NewGuid(), 1, new DateOnly(2020, 1, 1),
        new Dictionary<Guid, ListEntry> { [Au] = new(Au, "SUB-000016", "7440-57-5", "Gold", 0m, "gold") }, []);

    [Theory]
    [InlineData("7439-92-1", true)]
    [InlineData("50-00-0", true)]
    [InlineData("7440-57-5", true)]
    [InlineData("7439-92-2", false)]   // wrong check digit
    [InlineData("7439921", false)]
    [InlineData("7439-9-1", false)]
    [InlineData("abcd-ef-g", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_CAS_number_needs_its_pattern_and_a_right_check_digit(string? cas, bool valid) => Assert.Equal(valid, ComplianceRules.IsValidCas(cas));

    [Fact]
    public void Every_substance_the_migration_seeds_has_a_valid_CAS_number()
    {
        var sql = File.ReadAllText(Path.Combine(Root(), "db", "migrations", "0023_compliance.sql"));
        var cas = Regex.Matches(sql, @"\('(\d{2,7}-\d{2}-\d)', '[^']+', (NULL|'[a-z]+')\)").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(16, cas.Count);
        Assert.All(cas, c => Assert.True(ComplianceRules.IsValidCas(c), c));
    }

    [Theory]
    [InlineData(0.05, false)]
    [InlineData(0.1, false)]   // at the threshold is not above it
    [InlineData(0.11, true)]
    [InlineData(5, true)]
    public void A_restricted_substance_is_a_violation_above_its_threshold_only(double percent, bool violation)
    {
        var e = ComplianceRules.Evaluate(Rohs(), [new ExpandedLine(1, Pb, (decimal)percent, null, null)], Today);
        Assert.Equal(violation ? EvaluationResults.NonCompliant : EvaluationResults.Compliant, e.Result);
        Assert.Equal(violation, e.Violations > 0);
    }

    [Fact]
    public void Each_line_is_its_own_homogeneous_material_and_a_substance_not_on_the_list_is_ignored()
    {
        var other = Guid.NewGuid();
        var e = ComplianceRules.Evaluate(Rohs(), [new ExpandedLine(1, Pb, 0.06m, null, null), new ExpandedLine(2, Pb, 0.06m, null, null), new ExpandedLine(3, other, 90m, null, null)], Today);
        Assert.Equal(EvaluationResults.Compliant, e.Result); // 0.12 % in total, but never above 0.1 % in one material
        Assert.Empty(e.Findings);
    }

    [Fact]
    public void An_exemption_must_exist_apply_to_the_substance_and_not_have_expired()
    {
        var list = Rohs(new ListExemption("7(a)", Pb, null), new ListExemption("OLD", Pb, Today.AddDays(-1)), new ListExemption("CD-ONLY", Cd, null), new ListExemption("ANY", null, Today));
        string Reason(string code) => ComplianceRules.Evaluate(list, [new ExpandedLine(1, Pb, 85m, code, null)], Today).Findings.Single().Reason;
        Assert.Equal(FindingReasons.Exempt, Reason("7(a)"));
        Assert.Equal(FindingReasons.Exempt, Reason("7(A)")); // codes compare without regard to case
        Assert.Equal(FindingReasons.ExemptionExpired, Reason("OLD"));
        Assert.Equal(FindingReasons.ExemptionNotApplicable, Reason("CD-ONLY"));
        Assert.Equal(FindingReasons.ExemptionUnknown, Reason("NOPE"));
        Assert.Equal(FindingReasons.Exempt, Reason("ANY")); // expires today: still valid today
        var result = ComplianceRules.Evaluate(list, [new ExpandedLine(1, Pb, 85m, "7(a)", null)], Today);
        Assert.Equal(EvaluationResults.Exempt, result.Result);
        Assert.Equal(0, result.Violations);
    }

    [Theory]
    [InlineData(true, EvaluationResults.Compliant)]
    [InlineData(false, EvaluationResults.NonCompliant)]
    [InlineData(null, EvaluationResults.Unverified)]
    public void A_conflict_mineral_is_judged_by_its_conflict_free_statement(bool? conflictFree, string expected) =>
        Assert.Equal(expected, ComplianceRules.Evaluate(Conflict, [new ExpandedLine(1, Au, 0.3m, null, conflictFree)], Today).Result);

    [Fact]
    public void A_conflict_mineral_that_is_not_present_needs_no_statement() =>
        Assert.Equal(EvaluationResults.Compliant, ComplianceRules.Evaluate(Conflict, [new ExpandedLine(1, Au, 0m, null, null)], Today).Result);

    [Theory]
    [InlineData(null, 5.0, 1000.0, 0.5)]
    [InlineData(0.2, 5.0, 1000.0, 0.2)]     // a stated percent wins
    [InlineData(null, 5.0, null, null)]
    [InlineData(null, null, 100.0, null)]
    public void A_mass_becomes_a_percent_through_the_mass_of_the_item(double? percent, double? mass, double? item, double? expected)
    {
        var p = ComplianceRules.LinePercent((decimal?)percent, (decimal?)mass, (decimal?)item);
        Assert.Equal((decimal?)expected, p);
    }

    [Fact]
    public void A_declaration_is_validated_before_it_is_stored()
    {
        DeclarationInput Make(Func<DeclarationInput, DeclarationInput>? change = null)
        {
            var good = new DeclarationInput(null, "internal", null, null, null, Today, Today.AddDays(30), [new DeclarationLineInput("7439-92-1", null, 0.5m, null)], null);
            return change is null ? good : change(good);
        }
        Assert.Empty(ComplianceRules.ValidateDeclaration(Make(), Today));
        Assert.NotEmpty(ComplianceRules.ValidateDeclaration(Make(d => d with { Lines = [] }), Today));
        Assert.NotEmpty(ComplianceRules.ValidateDeclaration(Make(d => d with { ExpiresOn = Today.AddDays(-1) }), Today));
        Assert.NotEmpty(ComplianceRules.ValidateDeclaration(Make(d => d with { DeclaredOn = Today.AddDays(30), ExpiresOn = Today.AddDays(60) }), Today));
        Assert.NotEmpty(ComplianceRules.ValidateDeclaration(Make(d => d with { Source = "supplier" }), Today)); // no supplier named
        Assert.Empty(ComplianceRules.ValidateDeclaration(Make(d => d with { Source = "supplier", SupplierRef = "SUP-000001" }), Today));
        Assert.NotEmpty(ComplianceRules.ValidateDeclaration(Make(d => d with { RevisionScope = "a1" }), Today));
        Assert.NotEmpty(ComplianceRules.ValidateDeclaration(Make(d => d with { SupplierRef = "<script>" }), Today));
        Assert.NotEmpty(ComplianceRules.ValidateDeclaration(Make(d => d with { Lines = [new DeclarationLineInput("7439-92-1", "STEEL", 1, null)] }), Today)); // both
        Assert.NotEmpty(ComplianceRules.ValidateDeclaration(Make(d => d with { Lines = [new DeclarationLineInput("7439-92-2", null, 1, null)] }), Today)); // CAS check digit
        Assert.NotEmpty(ComplianceRules.ValidateDeclaration(Make(d => d with { Lines = [new DeclarationLineInput("7439-92-1", null, 101, null)] }), Today));
        Assert.NotEmpty(ComplianceRules.ValidateDeclaration(Make(d => d with { Lines = [new DeclarationLineInput("7439-92-1", null, null, 5)] }), Today)); // mass without item mass
        Assert.NotEmpty(ComplianceRules.ValidateDeclaration(Make(d => d with { ItemMassMg = 1, Lines = [new DeclarationLineInput("7439-92-1", null, null, 5)] }), Today)); // more than the item
        Assert.NotEmpty(ComplianceRules.ValidateDeclaration(Make(d => d with { Lines = [new DeclarationLineInput("7439-92-1", null, null, null)] }), Today));
    }

    [Fact]
    public void The_status_of_a_declaration_follows_one_order_of_precedence()
    {
        Assert.Equal(DeclarationStatuses.Withdrawn, ComplianceRules.DeclarationStatus(true, true, Today.AddDays(9), Today, true));
        Assert.Equal(DeclarationStatuses.Expired, ComplianceRules.DeclarationStatus(false, true, Today.AddDays(-1), Today, true));
        Assert.Equal(DeclarationStatuses.NonCompliant, ComplianceRules.DeclarationStatus(false, true, Today, Today, true)); // expires today: still valid today
        Assert.Equal(DeclarationStatuses.Verified, ComplianceRules.DeclarationStatus(false, true, Today, Today, false));
        Assert.Equal(DeclarationStatuses.Declared, ComplianceRules.DeclarationStatus(false, false, Today, Today, false));
    }

    [Theory]
    [InlineData(100, null)]
    [InlineData(61, null)]
    [InlineData(60, "d60")]
    [InlineData(31, "d60")]
    [InlineData(30, "d30")]
    [InlineData(8, "d30")]
    [InlineData(7, "d7")]
    [InlineData(0, "d7")]
    [InlineData(-1, "expired")]
    public void Expiry_notices_come_at_60_30_and_7_days_and_on_expiry(int daysLeft, string? stage) => Assert.Equal(stage, ComplianceRules.NoticeStage(Today.AddDays(daysLeft), Today));

    [Fact]
    public void A_part_stands_at_the_worst_of_its_current_declarations_and_a_missing_one_is_a_gap()
    {
        CurrentDeclaration D(string result, int days = 30, string? scope = null, string status = DeclarationStatuses.Declared) =>
            new("CD-1", scope, Today.AddDays(days), status, new Dictionary<string, string> { ["rohs"] = result });
        string Standing(string? revision, params CurrentDeclaration[] d) => ComplianceRules.PartStanding(d, "rohs", revision, Today);
        Assert.Equal(Standings.Gap, Standing("A"));
        Assert.Equal(Standings.Compliant, Standing("A", D(EvaluationResults.Compliant)));
        Assert.Equal(Standings.Exempt, Standing("A", D(EvaluationResults.Compliant), D(EvaluationResults.Exempt)));
        Assert.Equal(Standings.NonCompliant, Standing("A", D(EvaluationResults.Compliant), D(EvaluationResults.NonCompliant)));
        Assert.Equal(Standings.Unverified, Standing("A", D(EvaluationResults.Compliant), D(EvaluationResults.Unverified)));
        Assert.Equal(Standings.NonCompliant, Standing("A", D(EvaluationResults.Unverified), D(EvaluationResults.NonCompliant)));
        Assert.Equal(Standings.Expired, Standing("A", D(EvaluationResults.Compliant, -3)));
        Assert.Equal(Standings.Compliant, Standing("A", D(EvaluationResults.Compliant, -3), D(EvaluationResults.Compliant)));
        Assert.Equal(Standings.Gap, Standing("A", D(EvaluationResults.Compliant, 30, "B")));          // another revision
        Assert.Equal(Standings.Gap, Standing(null, D(EvaluationResults.Compliant, 30, "A")));         // an unknown revision matches unscoped declarations only
        Assert.Equal(Standings.Compliant, Standing(null, D(EvaluationResults.Compliant, 30, null)));
        Assert.Equal(Standings.Gap, Standing("A", D(EvaluationResults.Compliant, 30, null, DeclarationStatuses.Withdrawn)));
        Assert.Equal(Standings.Gap, Standing("A", new CurrentDeclaration("CD-2", null, Today.AddDays(5), DeclarationStatuses.Declared, new Dictionary<string, string>()))); // not evaluated for this regulation
    }

    [Fact]
    public void An_assembly_is_non_compliant_if_one_line_is_incomplete_if_one_is_missing_and_compliant_only_when_all_are()
    {
        Assert.Equal(Standings.AssemblyNone, ComplianceRules.AssemblyStanding([]));
        Assert.Equal(Standings.AssemblyCompliant, ComplianceRules.AssemblyStanding([Standings.Compliant, Standings.Compliant]));
        Assert.Equal(Standings.AssemblyExempt, ComplianceRules.AssemblyStanding([Standings.Compliant, Standings.Exempt]));
        Assert.Equal(Standings.AssemblyIncomplete, ComplianceRules.AssemblyStanding([Standings.Compliant, Standings.Gap]));
        Assert.Equal(Standings.AssemblyIncomplete, ComplianceRules.AssemblyStanding([Standings.Exempt, Standings.Expired]));
        Assert.Equal(Standings.AssemblyIncomplete, ComplianceRules.AssemblyStanding([Standings.Unverified]));
        Assert.Equal(Standings.AssemblyNonCompliant, ComplianceRules.AssemblyStanding([Standings.Gap, Standings.NonCompliant, Standings.Compliant]));
    }

    [Fact]
    public void Materials_and_lists_are_validated()
    {
        Assert.Empty(ComplianceRules.ValidateMaterial("AL6061-T6", "Aluminium", [("7439-92-1", 0.4m)]));
        Assert.NotEmpty(ComplianceRules.ValidateMaterial("al", "x", []));
        Assert.NotEmpty(ComplianceRules.ValidateMaterial("OK1", "x", [("7439-92-1", 60m), ("7440-31-5", 50m)]));
        Assert.NotEmpty(ComplianceRules.ValidateMaterial("OK1", "x", [("7439-92-1", 1m), ("7439-92-1", 2m)]));
        Assert.Empty(ComplianceRules.ValidateListVersion([new("7439-92-1", 0.1m)], [new("7(a)", "Solder", "7439-92-1", null)], ListKinds.Restricted));
        Assert.NotEmpty(ComplianceRules.ValidateListVersion([new("7439-92-1", 0.1m), new("7439-92-1", 0.2m)], [], ListKinds.Restricted));
        Assert.NotEmpty(ComplianceRules.ValidateListVersion([new("7439-92-1", 101m)], [], ListKinds.Restricted));
        Assert.NotEmpty(ComplianceRules.ValidateListVersion([new("7439-92-1", 0.1m)], [new("7(a)", "Solder", "7440-43-9", null)], ListKinds.Restricted)); // not on the list
        Assert.NotEmpty(ComplianceRules.ValidateListVersion([new("7440-57-5", 0m)], [new("X", "x", null, null)], ListKinds.Conflict)); // conflict lists have no exemptions
        Assert.NotEmpty(ComplianceRules.ValidateListVersion([], [new("7(a)", "x", null, null), new("7(A)", "y", null, null)], ListKinds.Restricted));
    }

    [Fact]
    public void Material_composition_expands_into_substance_lines_with_the_exemption_of_the_line()
    {
        var steel = Guid.NewGuid();
        var lines = new List<LineRow> { new(1, null, null, null, null, steel, "STEEL", "Steel", 50m, null, "6(a)", null), new(2, Pb, "SUB-1", "7439-92-1", "Lead", null, null, null, null, 2m, null, null) };
        var expanded = ComplianceEvaluator.Expand(lines, 100m, new Dictionary<Guid, List<(Guid, decimal)>> { [steel] = [(Pb, 0.3m), (Cd, 0.005m)] });
        Assert.Equal([(1, Pb, 0.15m, "6(a)"), (1, Cd, 0.0025m, "6(a)"), (2, Pb, 2m, null)], expanded.Select(e => (e.Ordinal, e.SubstanceId, e.Percent, e.ExemptionCode)));
    }

    // ---------------------------------------------------------------- exports

    private static ComplianceReport Report(string partName = "Terminal", string? supplier = "SUP-000001")
    {
        var standing = new Dictionary<string, LineStanding> { ["rohs"] = new(Standings.Compliant, []) };
        var line = new ReportLine(1, "10", "P-000001", partName, "A", 2m, "EA", true, standing, ["CD-000001"],
            new Dictionary<string, string> { ["tin"] = "conflict_free", ["tantalum"] = "none", ["tungsten"] = "unknown", ["gold"] = "not_conflict_free" },
            [new CompositionRow("CD-000001", "SUB-000013", "7440-31-5", "Tin", 1.5m, null, null, "tin", true)], supplier, "MPN, 1\"", new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));
        var asm = new ReportLine(0, "", "P-000009", "Assembly", "A", 1m, "EA", false, new Dictionary<string, LineStanding> { ["rohs"] = new(Standings.NotRequired, []) }, [], new Dictionary<string, string>(), [], null, null, null, null);
        return new ComplianceReport("ebom", "P-000009", "Assembly", "UNCLASSIFIED · DISTRIBUTION A", Today, [new ReportRegulation("rohs", "RoHS", ListKinds.Restricted, 1, new DateOnly(2020, 1, 1))], [asm, line],
            [new RegulationSummary("rohs", "RoHS", ListKinds.Restricted, 1, Standings.AssemblyCompliant, 1, 0, 0, 0, 0, 0, 1, [])]);
    }

    [Fact]
    public void The_conflict_mineral_csv_has_one_row_per_purchased_part_and_quotes_what_needs_it()
    {
        var csv = ComplianceExport.Cmrt(Report());
        Assert.EndsWith("-cmrt.csv", csv.FileName, StringComparison.Ordinal);
        var rows = csv.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, rows.Length); // header and the one purchased part; the assembly is not a row
        Assert.StartsWith("Part number,Revision,Description,Quantity,Unit,Declaration", rows[0], StringComparison.Ordinal);
        Assert.Contains("\"MPN, 1\"\"\"", rows[1], StringComparison.Ordinal);
        Assert.Contains("Yes, conflict-free", rows[1], StringComparison.Ordinal);
        Assert.EndsWith("Not conflict-free", rows[1], StringComparison.Ordinal); // gold is not conflict-free: the part is not
    }

    [Theory]
    [InlineData("=cmd|' /C calc'!A0", "'=cmd")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-2", "'-2")]
    [InlineData("@SUM(A1)", "'@SUM")]
    [InlineData("plain", "plain")]
    public void A_cell_that_a_spreadsheet_would_run_as_a_formula_is_neutralized(string value, string expectedStart) =>
        Assert.StartsWith(expectedStart, ComplianceExport.Cell(value), StringComparison.Ordinal);

    [Fact]
    public void The_material_declaration_is_well_formed_xml_without_characters_xml_cannot_carry()
    {
        var xml = ComplianceExport.Ipc1752(Report("Bracket <&> \u0001 \"quoted\""));
        var doc = System.Xml.Linq.XDocument.Parse(xml.Content);
        var item = Assert.Single(doc.Descendants("Item"));
        Assert.Equal("Bracket <&>  \"quoted\"", (string?)item.Attribute("name"));
        Assert.Equal("1.5", (string?)Assert.Single(doc.Descendants("Substance")).Attribute("massPercent"));
        Assert.Equal("keel-ipc1752-like/1", (string?)doc.Root!.Attribute("format"));
        Assert.DoesNotContain("DOCTYPE", xml.Content, StringComparison.Ordinal);
        Assert.EndsWith("-ipc1752.xml", xml.FileName, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the treemap tree

    [Fact]
    public void The_treemap_tree_gives_an_assembly_the_worst_standing_below_it_and_drills_by_path()
    {
        ReportLine L(string path, string number, bool required, string standing, decimal qty = 1) => new(path.Count(c => c == '.') + 1, path, number, number, "A", qty, "EA", required,
            new Dictionary<string, LineStanding> { ["rohs"] = new(required ? standing : Standings.NotRequired, []) }, [], new Dictionary<string, string>(), [], null, null, null, null);
        var lines = new List<ReportLine>
        {
            new(0, "", "TOP", "Top", "A", 1, "EA", false, new Dictionary<string, LineStanding> { ["rohs"] = new(Standings.NotRequired, []) }, [], new Dictionary<string, string>(), [], null, null, null, null),
            L("1", "SUB", false, ""), L("1.1", "A", true, Standings.Compliant, 4), L("1.2", "B", true, Standings.Gap, 1), L("2", "C", true, Standings.NonCompliant, 2), L("3", "D", true, Standings.Exempt, 1),
        };
        var report = new ComplianceReport("ebom", "TOP", "Top", "UNCLASSIFIED", Today, [new ReportRegulation("rohs", "RoHS", ListKinds.Restricted, 1, Today)], lines, []);
        var root = ComplianceTreemapModel.Build(report, "rohs");
        Assert.Equal(Standings.NonCompliant, root.Standing);
        var sub = ComplianceTreemapModel.Find(root, "1")!;
        Assert.Equal(Standings.Gap, sub.Standing); // worst below: no declaration beats compliant
        Assert.Equal(2, sub.Children.Count);
        Assert.Equal(5.0, sub.Value(false));
        Assert.Equal(2.0, sub.Value(true));
        Assert.Equal(Standings.Exempt, ComplianceTreemapModel.Find(root, "3")!.Standing);
        var tiles = ComplianceTreemapModel.Layout(root, 960, 540, false);
        Assert.Equal(3, tiles.Count);
        Assert.Equal(960.0 * 540.0, tiles.Sum(t => t.Width * t.Height), 3);
        Assert.True(tiles.Single(t => t.Node.Path == "1").Drillable);
        // Every standing has a colour class AND a pattern or is plain, so the picture reads without colour.
        Assert.Equal(("unresolved", "cross"), ComplianceTreemapModel.Style(Standings.NonCompliant));
        Assert.Equal(("released", "dots"), ComplianceTreemapModel.Style(Standings.Exempt));
        Assert.Equal(new[] { "unresolved", "released", "released", "underreview", "obsolete", "inwork" }.Order(),
            new[] { Standings.NonCompliant, Standings.Compliant, Standings.Exempt, Standings.Gap, Standings.Expired, Standings.Unverified }.Select(s => ComplianceTreemapModel.Style(s).Class).Order());
        // A line whose parent the reader cannot see (it is not in the report) hangs from the root.
        var orphan = new ComplianceReport("ebom", "TOP", "Top", "U", Today, report.Regulations, [lines[0], L("9.1", "X", true, Standings.Compliant)], []);
        Assert.Single(ComplianceTreemapModel.Build(orphan, "rohs").Children);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
