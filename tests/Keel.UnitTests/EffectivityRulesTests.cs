using Keel.Bom;
using Keel.Core;

namespace Keel.UnitTests;

/// <summary>The pure rules of effectivity (ADR-0021): parsing, alternatives, supersession.</summary>
public sealed class EffectivityRulesTests
{
    private static EffectivityRange Serial(long? from, long? to, string ctx = "ORCA-POD") => new(EffectivityKinds.Serial, ctx, from, to);

    // ---------------------------------------------------------------- parsing

    [Theory]
    [InlineData("serial", "orca-pod", "1", "100", 1, 100)]
    [InlineData("serial", "ORCA-POD", "101", null, 101, null)]
    [InlineData("SERIAL", "ORCA-POD", null, "50", null, 50)]
    [InlineData("serial", "ORCA-POD", "7", "7", 7, 7)]
    public void Serial_ranges_parse_with_open_ends(string kind, string ctx, string? from, string? to, int? f, int? t)
    {
        var (range, error) = EffectivityRules.Parse(new EffectivityInput(kind, ctx, from, to));
        Assert.Null(error);
        Assert.Equal(new EffectivityRange("serial", "ORCA-POD", f, t), range);   // int? widens to long?
    }

    [Theory]
    [InlineData("serial", "ORCA-POD", "0", "5", "not a serial number")]
    [InlineData("serial", "ORCA-POD", "-3", null, "not a serial number")]
    [InlineData("serial", "ORCA-POD", "1.5", null, "not a serial number")]
    [InlineData("serial", "ORCA-POD", "abc", null, "not a serial number")]
    [InlineData("serial", "ORCA-POD", "10", "9", "must not be after")]
    [InlineData("serial", "ORCA-POD", null, null, "open on both ends")]
    [InlineData("serial", null, "1", "2", "needs an end-item context")]
    [InlineData("lot", null, "1", "2", "needs an end-item context")]
    [InlineData("date", "ORCA-POD", "2027-01-01", null, "no end-item context")]
    [InlineData("date", null, "01/02/2027", null, "not a date")]
    [InlineData("date", null, "2027-02-30", null, "not a date")]
    [InlineData("date", null, "2301-01-01", null, "between 1970")]
    [InlineData("batch", "ORCA-POD", "1", null, "serial, lot or date")]
    [InlineData("serial", "ORCA-POD", "99999999999", null, "not a serial number")]
    public void Invalid_input_is_explained(string kind, string? ctx, string? from, string? to, string expected)
    {
        var (range, error) = EffectivityRules.Parse(new EffectivityInput(kind, ctx, from, to));
        Assert.Null(range);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Lots_accept_numbers_and_the_block_names_of_the_context()
    {
        var blocks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Block 2"] = 2, ["Block 3"] = 3 };
        var (range, error) = EffectivityRules.Parse(new EffectivityInput("lot", "ORCA-POD", "block 2", "Block 3"), blocks);
        Assert.Null(error);
        Assert.Equal(new EffectivityRange("lot", "ORCA-POD", 2, 3), range);
        Assert.Equal(new EffectivityRange("lot", "ORCA-POD", 5, null), EffectivityRules.Parse(new EffectivityInput("lot", "ORCA-POD", "5", null), blocks).Range);
        Assert.Contains("block name", EffectivityRules.Parse(new EffectivityInput("lot", "ORCA-POD", "Block 9", null), blocks).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Dates_are_utc_days_and_round_trip()
    {
        var (range, error) = EffectivityRules.Parse(new EffectivityInput("date", null, "2027-03-01", "2027-06-30"));
        Assert.Null(error);
        Assert.Equal("date 2027-03-01 to 2027-06-30", range!.Describe());
        Assert.Equal(new DateOnly(2027, 3, 1), EffectivityRange.DayOf(range.From!.Value));
        Assert.True(range.Covers("date", null, EffectivityRange.DayNumber(new DateOnly(2027, 6, 30))));
        Assert.False(range.Covers("date", null, EffectivityRange.DayNumber(new DateOnly(2027, 7, 1))));
    }

    [Fact]
    public void Ranges_describe_themselves_in_words()
    {
        Assert.Equal("serial 101 and up (ORCA-POD)", Serial(101, null).Describe());
        Assert.Equal("serial up to 100 (ORCA-POD)", Serial(null, 100).Describe());
        Assert.Equal("serial 1-100 (ORCA-POD)", Serial(1, 100).Describe());
        Assert.Equal("serial 5 (ORCA-POD)", Serial(5, 5).Describe());
        Assert.Equal("date from 2027-01-01", new EffectivityRange("date", null, EffectivityRange.DayNumber(new DateOnly(2027, 1, 1)), null).Describe());
    }

    // ---------------------------------------------------------------- alternatives

    [Fact]
    public void Alternatives_of_one_find_number_conflict_unless_their_ranges_are_disjoint_in_one_dimension()
    {
        Assert.False(EffectivityRules.Conflict(Serial(1, 100), Serial(101, null)));
        Assert.False(EffectivityRules.Conflict(Serial(null, 100), Serial(101, 200)));
        Assert.True(EffectivityRules.Conflict(Serial(1, 100), Serial(100, 200)));        // share serial 100
        Assert.True(EffectivityRules.Conflict(Serial(1, null), Serial(500, 600)));
        Assert.True(EffectivityRules.Conflict(null, Serial(1, 2)));                      // a line without effectivity always applies
        Assert.True(EffectivityRules.Conflict(Serial(1, 2), null));
        Assert.True(EffectivityRules.Conflict(null, null));
        Assert.True(EffectivityRules.Conflict(Serial(1, 2), new EffectivityRange("lot", "ORCA-POD", 5, 6)));   // different dimension: undecidable
        Assert.True(EffectivityRules.Conflict(Serial(1, 2), Serial(5, 6, "KRAKEN-POD")));                       // different context: undecidable
        Assert.Contains("every unit", EffectivityRules.ConflictMessage(10, null, Serial(1, 2)), StringComparison.Ordinal);
        Assert.Contains("overlaps", EffectivityRules.ConflictMessage(10, Serial(1, 100), Serial(50, 60)), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the supersession rule (CFG-9)

    private static EffectivityRules.ReleasedRevision Rev(string label, EffectivityRange? range = null) => new(Guid.NewGuid(), label, range);

    [Fact]
    public void Without_an_effectivity_every_other_released_revision_is_superseded_completely()
    {
        var a = Rev("A");
        var plan = EffectivityRules.Supersede([a], null);
        Assert.Equal([a.Id], plan.ObsoleteRevisionIds);
        Assert.Empty(plan.Narrowed);
        Assert.Null(plan.Error);
    }

    [Fact]
    public void A_revision_effective_from_n_leaves_the_earlier_one_released_for_the_units_below_n()
    {
        var a = Rev("A");
        var plan = EffectivityRules.Supersede([a], Serial(101, null));
        Assert.Empty(plan.ObsoleteRevisionIds);
        var narrowed = Assert.Single(plan.Narrowed);
        Assert.Equal((a.Id, null, Serial(null, 100)), (narrowed.RevisionId, narrowed.Before, narrowed.After));
        Assert.Contains("stays Released", plan.Notes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Earlier_ranges_are_cut_back_and_revisions_that_serve_nothing_become_obsolete()
    {
        var a = Rev("A", Serial(null, 100));
        var b = Rev("B", Serial(101, 200));
        var c = Rev("C", Serial(201, null));
        // D from 150: C starts above 150 (obsolete), B is cut to 101-149, A is untouched.
        var plan = EffectivityRules.Supersede([a, b, c], Serial(150, null));
        Assert.Equal([c.Id], plan.ObsoleteRevisionIds);
        var cut = Assert.Single(plan.Narrowed);
        Assert.Equal((b.Id, Serial(101, 149)), (cut.RevisionId, cut.After));

        // From 101 exactly: B and C are obsolete, A stays as it is.
        var plan2 = EffectivityRules.Supersede([a, b, c], Serial(101, null));
        Assert.Equal([b.Id, c.Id], plan2.ObsoleteRevisionIds);
        Assert.Empty(plan2.Narrowed);

        // From 50: A is cut to 49 (it was 1-100), the others are obsolete.
        var plan3 = EffectivityRules.Supersede([a, b, c], Serial(50, null));
        Assert.Equal([b.Id, c.Id], plan3.ObsoleteRevisionIds);
        Assert.Equal(Serial(null, 49), Assert.Single(plan3.Narrowed).After);
    }

    [Fact]
    public void Serial_one_leaves_nothing_below_so_the_earlier_revision_becomes_obsolete()
    {
        var a = Rev("A");
        Assert.Equal([a.Id], EffectivityRules.Supersede([a], Serial(1, null)).ObsoleteRevisionIds);
        // Dates have no first unit: a revision always keeps the days before.
        var date = new EffectivityRange("date", null, EffectivityRange.DayNumber(new DateOnly(2027, 1, 1)), null);
        var plan = EffectivityRules.Supersede([a], date);
        Assert.Empty(plan.ObsoleteRevisionIds);
        Assert.Equal("date up to 2026-12-31", Assert.Single(plan.Narrowed).After.Describe());
    }

    [Fact]
    public void A_dimension_or_context_mismatch_and_a_closed_new_range_are_refused()
    {
        var a = Rev("A", Serial(null, 100));
        Assert.Contains("same type and context", EffectivityRules.Supersede([a], new EffectivityRange("lot", "ORCA-POD", 5, null)).Error, StringComparison.Ordinal);
        Assert.Contains("same type and context", EffectivityRules.Supersede([a], Serial(101, null, "KRAKEN-POD")).Error, StringComparison.Ordinal);
        Assert.Contains("open at the top", EffectivityRules.Supersede([Rev("A")], Serial(101, 200)).Error, StringComparison.Ordinal);
        Assert.Contains("open at the top", EffectivityRules.Supersede([Rev("A")], Serial(null, 200)).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_already_closed_revision_is_left_alone()
    {
        var a = Rev("A", Serial(1, 40));
        var plan = EffectivityRules.Supersede([a], Serial(101, null));
        Assert.Empty(plan.ObsoleteRevisionIds);
        Assert.Empty(plan.Narrowed);
    }

    // ---------------------------------------------------------------- requests

    [Fact]
    public void A_unit_request_needs_a_value_and_a_context_for_serials_and_lots()
    {
        Assert.NotEmpty(new EffectivityRequest(null).Validate());
        Assert.NotEmpty(new EffectivityRequest(null, Serial: 5).Validate());
        Assert.NotEmpty(new EffectivityRequest("ORCA-POD", Serial: 0).Validate());
        Assert.Empty(EffectivityRequest.ForSerial("ORCA-POD", 5).Validate());
        Assert.Empty(EffectivityRequest.ForDate(new DateOnly(2027, 5, 1)).Validate());
        Assert.NotEmpty(new EffectivityRequest(null, Day: -1).Validate());
        Assert.Equal("serial 5, date 2027-05-01 (ORCA-POD)", new EffectivityRequest("ORCA-POD", 5, null, EffectivityRange.DayNumber(new DateOnly(2027, 5, 1))).Describe());
    }
}
