using Keel.Bom;
using Keel.Core;

namespace Keel.UnitTests;

public sealed class BomDomainTests
{
    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)",
        cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;

    // ---------------------------------------------------------------- line validation

    [Fact]
    public void A_valid_EA_line_with_matching_designators_passes_and_trims()
    {
        var (errors, refs) = BomRules.ValidateLine(3, "EA", [" R1", "R2 ", "", "R3"], "note", 10);
        Assert.Empty(errors);
        Assert.Equal(["R1", "R2", "R3"], refs);
    }

    [Theory]
    [InlineData(2.5, "EA", "whole number")]
    [InlineData(0, "EA", "greater than zero")]
    [InlineData(-1, "mm", "greater than zero")]
    [InlineData(1_000_001, "EA", "at most")]
    [InlineData(1.1234567, "mm", "6 decimal places")]
    public void Quantity_rules(double qty, string unit, string expected)
    {
        var (errors, _) = BomRules.ValidateLine((decimal)qty, unit, null, null, 10);
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Non_EA_units_accept_fractions_and_ignore_the_designator_count_rule()
    {
        Assert.Empty(BomRules.ValidateLine(1.25m, "m", null, null, 10).Errors);
        Assert.Empty(BomRules.ValidateLine(2.5m, "kg", ["A1"], null, 10).Errors);
    }

    [Fact]
    public void EA_designator_count_must_equal_the_quantity()
    {
        var (errors, _) = BomRules.ValidateLine(3, "EA", ["R1", "R2"], null, 10);
        Assert.Contains(errors, e => e.Contains("2 reference designator(s)", StringComparison.Ordinal) && e.Contains("quantity is 3", StringComparison.Ordinal));
        Assert.Empty(BomRules.ValidateLine(3, "EA", null, null, 10).Errors); // none given: no count rule
    }

    [Fact]
    public void Designators_must_be_unique_within_the_line_ignoring_case_and_well_formed()
    {
        var (dup, _) = BomRules.ValidateLine(2, "EA", ["R1", "r1"], null, 10);
        Assert.Contains(dup, e => e.Contains("unique within the line", StringComparison.Ordinal));
        var (bad, _) = BomRules.ValidateLine(1, "EA", ["R 1;drop"], null, 10);
        Assert.Contains(bad, e => e.Contains("may contain only", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("EACH")]
    [InlineData("ea")]
    [InlineData(null)]
    [InlineData("")]
    public void Unit_must_be_in_the_units_of_measure_list(string? unit) =>
        Assert.Contains(BomRules.ValidateLine(1, unit, null, null, 10).Errors, e => e.Contains("is not valid", StringComparison.Ordinal));

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_000)]
    public void Find_number_is_bounded(int find) =>
        Assert.Contains(BomRules.ValidateLine(1, "EA", null, null, find).Errors, e => e.Contains("Find number must be", StringComparison.Ordinal));

    [Fact]
    public void Notes_are_limited_to_1000_characters() =>
        Assert.Contains(BomRules.ValidateLine(1, "EA", null, new string('x', 1001), 10).Errors, e => e.Contains("Notes", StringComparison.Ordinal));

    [Fact]
    public void Next_find_number_is_the_next_multiple_of_ten()
    {
        Assert.Equal(10, BomRules.NextFindNumber([]));
        Assert.Equal(20, BomRules.NextFindNumber([10]));
        Assert.Equal(30, BomRules.NextFindNumber([10, 20, 15]));
        Assert.Equal(30, BomRules.NextFindNumber([21]));
        Assert.Equal(20, BomRules.NextFindNumber([10, 11]));
    }

    [Fact]
    public void Designator_clashes_ignore_case()
    {
        Assert.Equal(["R2"], BomRules.DesignatorClashes(["R1", "R2"], ["r2", "C1"]));
        Assert.Empty(BomRules.DesignatorClashes(["R1"], ["C1"]));
    }

    [Fact]
    public void Paths_parse_and_reject_junk()
    {
        Assert.Equal([10, 20, 30], BomRules.ParsePath("10.20.30")!);
        Assert.Empty(BomRules.ParsePath(null)!);
        Assert.Null(BomRules.ParsePath("10..20"));
        Assert.Null(BomRules.ParsePath("10.x"));
        Assert.Null(BomRules.ParsePath("-5"));
    }

    [Fact]
    public void Cycle_message_names_the_path()
    {
        Assert.Equal("P-1 cannot contain itself.", BomRules.CycleMessage("P-1", "P-1", ["P-1"]));
        Assert.Equal("Adding P-3 under P-1 would create a circular structure: P-3 -> P-2 -> P-1 -> P-3.",
            BomRules.CycleMessage("P-1", "P-3", ["P-3", "P-2", "P-1", "P-3"]));
    }

    // ---------------------------------------------------------------- CSV

    private static BomLine Line(string path, int level, string? number, string? name, bool redacted = false, Markings? markings = null, decimal qty = 2, string unit = "EA") =>
        new(Guid.NewGuid(), null, level, path, int.Parse(path.Split('.')[^1], System.Globalization.CultureInfo.InvariantCulture), qty, unit, qty,
            redacted, false, false, false, redacted ? null : Guid.NewGuid(), number, name, redacted ? null : "Component", redacted ? null : "A", redacted ? null : 1,
            redacted ? null : "InWork", redacted ? null : "In Work", [], null, markings?.BannerText(), markings);

    [Fact]
    public void Csv_has_the_banner_as_first_and_last_line_and_redacts_restricted_items()
    {
        var banner = "CUI//EXPT · ITAR · DISTRIBUTION D";
        var csv = BomCsv.Render(banner, [Line("10", 1, "P-000002", "Bracket", markings: Itar), Line("20", 1, null, null, redacted: true)]);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(banner, lines[0].Trim('"'));
        Assert.Equal(banner, lines[^1].Trim('"'));
        Assert.Contains("P-000002", lines[2], StringComparison.Ordinal);
        Assert.Contains("Restricted item", lines[3], StringComparison.Ordinal);
        Assert.Contains(",restricted", lines[3], StringComparison.Ordinal);
        Assert.DoesNotContain("Bracket", lines[3], StringComparison.Ordinal);
        Assert.Equal(5, lines.Length);
    }

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-1+2", "'-1+2")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    public void Csv_cells_are_quoted_and_neutralize_formulas(string input, string expected) => Assert.Equal(expected, BomCsv.Cell(input));

    // ---------------------------------------------------------------- diff

    private static BomDiff.Side Side(Guid master, int find, decimal qty = 1, string unit = "EA", string refs = "", bool readable = true, string? number = null) =>
        new(master, find, qty, unit, refs, null, readable, readable ? number ?? "P-" + find : null, readable ? "Name" : null, null, null);

    [Fact]
    public void Diff_reports_added_removed_quantity_and_find_number_changes()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();
        var (lines, unchanged) = BomDiff.Compute(
            [Side(a, 10), Side(b, 20, 2), Side(c, 30), Side(d, 40)],
            [Side(a, 10), Side(b, 20, 5), Side(c, 35), Side(Guid.NewGuid(), 50, 1, "EA", "", true, "NEW")]);

        Assert.Equal(1, unchanged);
        Assert.Equal(["Quantity 2 to 5"], lines.Single(l => l.FromFindNumber == 20).Changes);
        Assert.Equal(["Find number 30 to 35"], lines.Single(l => l.FromFindNumber == 30).Changes);
        Assert.Equal(BomChangeKind.Removed, lines.Single(l => l.FromFindNumber == 40).Kind);
        Assert.Equal(BomChangeKind.Added, lines.Single(l => l.ToFindNumber == 50).Kind);
    }

    [Fact]
    public void Diff_never_pairs_hidden_lines_across_different_parts_or_find_numbers()
    {
        var h1 = Guid.NewGuid();
        var h2 = Guid.NewGuid();
        // Same find number but the hidden part was replaced: shown as removed + added placeholders, identity not revealed.
        var (replaced, _) = BomDiff.Compute([Side(h1, 10, readable: false)], [Side(h2, 10, readable: false)]);
        Assert.Equal([BomChangeKind.Added, BomChangeKind.Removed], replaced.Select(l => l.Kind).Order());
        Assert.All(replaced, l => { Assert.True(l.Redacted); Assert.Null(l.Number); Assert.Null(l.Name); });
        // Same hidden part renumbered: not matched across find numbers.
        var (renumbered, _) = BomDiff.Compute([Side(h1, 10, readable: false)], [Side(h1, 20, readable: false)]);
        Assert.Equal(2, renumbered.Count);
        // Same hidden part, quantity changed: visible, because quantity is visible on placeholders (A4).
        var (qty, _) = BomDiff.Compute([Side(h1, 10, 1, readable: false)], [Side(h1, 10, 3, readable: false)]);
        var change = Assert.Single(qty);
        Assert.Equal(["Quantity 1 to 3"], change.Changes);
        Assert.Null(change.Number);
    }

    [Fact]
    public void Config_specs_for_phase_two_are_named_as_such()
    {
        var e = Assert.Throws<NotSupportedException>(() => BomSql.Resolve(BomConfigSpec.Effectivity));
        Assert.Contains("Phase 2", e.Message, StringComparison.Ordinal);
        Assert.Contains("Effectivity", e.Message, StringComparison.Ordinal);
    }
}
