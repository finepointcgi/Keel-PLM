using Bunit;
using Keel.Core;
using Keel.Sourcing;
using Keel.Web.Components.Shared;
using Keel.Web.Components.Visuals;
using Keel.Web.Services;

namespace Keel.UnitTests;

/// <summary>ADR-0054: the pure rules of RFQ quotes (tiers, comparison) and of the supplier scorecard, and the components that show them.</summary>
public sealed class RfqScorecardUnitTests : BunitContext
{
    private static readonly DateOnly Today = new(2026, 10, 30);
    private static readonly Markings Open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;
    private static QuoteLine Q(string part = "P-1", decimal? price = null, string? cur = "USD", int? lead = null, decimal? moq = null) => new(part, price, cur, lead, moq);
    private static QuoteLine Tiered(params (decimal, decimal)[] tiers) => new("P-1", null, "USD", null, null, [.. tiers.Select(t => new PriceBreak(t.Item1, t.Item2))]);

    // ---------------------------------------------------------------- price at a quantity

    [Fact]
    public void A_single_price_applies_at_any_quantity() => Assert.Equal(2.5m, SourcingRules.PriceAt(Q(price: 2.5m), 1));

    [Fact]
    public void The_tier_with_the_largest_quantity_not_above_the_asked_quantity_decides()
    {
        var line = Tiered((100m, 2.5m), (500m, 2.1m), (1000m, 1.9m));
        Assert.Equal(2.5m, SourcingRules.PriceAt(line, 100));
        Assert.Equal(2.5m, SourcingRules.PriceAt(line, 499));
        Assert.Equal(2.1m, SourcingRules.PriceAt(line, 500));
        Assert.Equal(1.9m, SourcingRules.PriceAt(line, 5000));
    }

    [Fact]
    public void Below_the_smallest_tier_the_quote_has_no_price_and_tiers_beat_the_single_price()
    {
        var line = Tiered((100m, 2.5m)) with { UnitPrice = 9m };
        Assert.Null(SourcingRules.PriceAt(line, 99));
        Assert.True(SourcingRules.BelowSmallestTier(line, 99));
        Assert.False(SourcingRules.BelowSmallestTier(line, 100));
        Assert.Equal(2.5m, SourcingRules.PriceAt(line, 100));
        Assert.Null(SourcingRules.PriceAt(Q(), 5));
    }

    [Fact]
    public void Tiers_are_taken_in_quantity_order_even_when_listed_out_of_order() =>
        Assert.Equal(2.1m, SourcingRules.PriceAt(Tiered((500m, 2.1m), (100m, 2.5m)), 600));

    [Fact]
    public void Quote_extras_are_validated()
    {
        Assert.Empty(SourcingRules.ValidateQuoteExtras(Q(price: 1m)));
        Assert.NotEmpty(SourcingRules.ValidateQuoteExtras(Q(price: 1m, cur: null)));               // a price needs a currency
        Assert.NotEmpty(SourcingRules.ValidateQuoteExtras(Tiered((100m, 2m), (100m, 1m)))); // quantities must increase
        Assert.NotEmpty(SourcingRules.ValidateQuoteExtras(Tiered((0m, 2m))));
        Assert.NotEmpty(SourcingRules.ValidateQuoteExtras(Tiered((1m, -2m))));
        Assert.NotEmpty(SourcingRules.ValidateQuoteExtras(Tiered([.. Enumerable.Range(1, 11).Select(i => ((decimal)i, 1m))])));
        Assert.NotEmpty(SourcingRules.ValidateQuoteExtras(Q(price: 1m) with { Note = new string('x', 501) }));
        Assert.NotEmpty(SourcingRules.ValidateQuoteExtras(Q(price: 1m) with { Exception = new string('x', 501) }));
        Assert.Empty(SourcingRules.ValidateQuoteExtras(Q(price: null, cur: null, lead: 5) with { Exception = "Cannot meet the date" })); // no price, no currency needed
    }

    [Fact]
    public void A_quote_with_tiers_notes_and_exceptions_survives_its_json_form()
    {
        var line = Q("P-1", 2m, "USD", 14, 50m) with { Tiers = [new PriceBreak(100m, 1.8m), new PriceBreak(500m, 1.5m)], Note = "Lot pricing", Exception = "Cannot ship before June" };
        var back = RequestService.ParseQuote(RequestService.QuoteJson([line, Q("P-2", 1m)])).ToList();
        Assert.Equal(2, back.Count);
        Assert.Equal(line.Tiers!.Select(t => (t.Quantity, t.UnitPrice)), back[0].Tiers!.Select(t => (t.Quantity, t.UnitPrice)));
        Assert.Equal("Lot pricing", back[0].Note);
        Assert.Equal("Cannot ship before June", back[0].Exception);
        Assert.Null(back[1].Tiers);
        Assert.Null(back[1].Note);
    }

    // ---------------------------------------------------------------- comparison

    [Fact]
    public void The_lowest_price_and_the_shortest_lead_time_are_marked_within_one_currency()
    {
        var cells = SourcingRules.CompareLine(100, null, Today, [("A", Q(price: 2m, lead: 30)), ("B", Q(price: 1.5m, lead: 45)), ("C", null)], null);
        Assert.False(cells[0].Lowest);
        Assert.True(cells[1].Lowest);
        Assert.True(cells[0].Fastest);
        Assert.False(cells[1].Fastest);
        Assert.False(cells[2].Quoted);
        Assert.Equal(150m, cells[1].ExtendedPrice);
    }

    [Fact]
    public void Quotes_in_different_currencies_are_not_ranked()
    {
        var cells = SourcingRules.CompareLine(10, null, Today, [("A", Q(price: 2m)), ("B", Q(price: 1m, cur: "EUR"))], null);
        Assert.DoesNotContain(cells, c => c.Lowest);
    }

    [Fact]
    public void Tiers_are_applied_at_the_line_quantity_and_a_quote_below_its_smallest_tier_has_no_price()
    {
        var cells = SourcingRules.CompareLine(600, null, Today, [("A", Tiered((100m, 2.5m), (500m, 2.1m))), ("B", Tiered((1000m, 1m)))], null);
        Assert.Equal(2.1m, cells[0].UnitPrice);
        Assert.Null(cells[1].UnitPrice);
        Assert.True(cells[1].BelowSmallestTier);
        Assert.True(cells[0].Lowest);
    }

    [Fact]
    public void Need_by_is_met_when_an_order_placed_today_arrives_in_time()
    {
        var needBy = Today.AddDays(30);
        var cells = SourcingRules.CompareLine(1, needBy, Today, [("A", Q(price: 1m, lead: 30)), ("B", Q(price: 1m, lead: 31)), ("C", Q(price: 1m))], "B");
        Assert.True(cells[0].MeetsNeedBy);
        Assert.False(cells[1].MeetsNeedBy);
        Assert.Null(cells[2].MeetsNeedBy);
        Assert.True(cells[1].Awarded);
        Assert.False(cells[0].Awarded);
    }

    private static RequestDetail Detail(RequestPartView[] parts, RequestRecipientView[] recipients, IReadOnlyList<ResponseView> responses, bool canModify = true, string state = RequestStates.Issued, IReadOnlyList<AwardView>? awards = null) =>
        new(new RequestSummary(Guid.NewGuid(), "RFQ-000001", "rfq", "t", state, null, parts.Length, recipients.Length, 0, "UNCLASSIFIED", Open, DateTimeOffset.UnixEpoch, "alice"), "", parts, recipients, responses, canModify, [], awards);

    private static ResponseView Response(string supplier, int seq, bool declined, DateOnly? valid, params QuoteLine[] lines) =>
        new(Guid.NewGuid(), supplier, supplier + " Inc", seq, declined, "", lines, DateTimeOffset.UnixEpoch, "x", [], valid);

    [Fact]
    public void The_comparison_uses_each_suppliers_latest_response_and_adds_up_the_priced_lines()
    {
        var parts = new[] { new RequestPartView("P-1", "One", 10, "EA", null, "U"), new RequestPartView("P-2", "Two", 5, "EA", null, "U") };
        var recipients = new[] { new RequestRecipientView("A", "A Inc", "Responded", null, true, null), new RequestRecipientView("B", "B Inc", "Declined", null, true, null), new RequestRecipientView("C", "C Inc", "Sent", null, true, null) };
        var responses = new[]
        {
            Response("A", 1, false, null, Q("P-1", 9m), Q("P-2", 9m)),
            Response("A", 2, false, Today.AddDays(-1), Q("P-1", 2m), Q("P-2", 4m, lead: 10)), // latest, expired yesterday
            Response("B", 1, false, null, Q("P-1", 1m)),
            Response("B", 2, true, null),                                                       // a later decline withdraws the quote
        };
        var c = RequestService.BuildComparison(Detail(parts, recipients, responses), Today);
        Assert.Equal(["A", "B", "C"], c.Suppliers.Select(s => s.Number));
        Assert.True(c.Suppliers[0].QuoteExpired);
        Assert.True(c.Suppliers[1].Declined);
        Assert.Equal(2m, c.Lines[0].Cells[0].UnitPrice);
        Assert.False(c.Lines[0].Cells[1].Quoted);
        var total = Assert.Single(c.Totals);
        Assert.Equal("A", total.SupplierNumber);
        Assert.Equal(40m, total.Total); // 10 * 2 + 5 * 4
        Assert.True(total.Complete);
        Assert.True(c.CanAward);
    }

    [Fact]
    public void A_total_over_part_of_the_lines_says_so_and_a_reader_who_cannot_modify_cannot_award()
    {
        var parts = new[] { new RequestPartView("P-1", "One", 10, "EA", null, "U"), new RequestPartView("P-2", "Two", 5, "EA", null, "U") };
        var c = RequestService.BuildComparison(Detail(parts, [new RequestRecipientView("A", "A Inc", "Responded", null, true, null)], [Response("A", 1, false, null, Q("P-1", 2m))], canModify: false), Today);
        Assert.False(Assert.Single(c.Totals).Complete);
        Assert.False(c.CanAward);
        Assert.False(RequestService.BuildComparison(Detail(parts, [], [], state: RequestStates.Draft), Today).CanAward);
    }

    // ---------------------------------------------------------------- scorecard formula

    private static SupplierQualityFact Ncr(string severity, DateOnly opened, bool cancelled = false) =>
        new("ncr", "NCR-" + Guid.NewGuid().ToString("N")[..6], new DateTimeOffset(opened.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), null, severity, Open, cancelled);

    private static SupplierQualityFact Capa(DateOnly opened, DateOnly? closed = null) =>
        new("capa", "CAPA-" + Guid.NewGuid().ToString("N")[..6], new DateTimeOffset(opened.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), closed is null ? null : new DateTimeOffset(closed.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), null, Open);

    private static ScorecardComponentView Component(ScorecardView v, string key) => v.Components.Single(c => c.Key == key);

    [Fact]
    public void Nonconformances_cost_points_by_severity_inside_the_last_twelve_months()
    {
        var facts = new[] { Ncr("Minor", Today.AddDays(-10)), Ncr("Major", Today.AddDays(-20)), Ncr("Critical", Today.AddMonths(-13)), Ncr("Major", Today.AddDays(-5), cancelled: true) };
        var card = ScorecardRules.Compute(Today, facts, 4, [], [], Today);
        var ncr = Component(card, "ncr");
        Assert.True(ncr.Available);
        Assert.Equal(60m, ncr.Score); // 1 + 3 = 4 points = 40 off; the critical one is outside the window, the cancelled one is not counted
        Assert.Contains("2 in the last 12 months", ncr.Measure, StringComparison.Ordinal);
        Assert.Equal(0m, Component(ScorecardRules.Compute(Today, [Ncr("Critical", Today.AddDays(-1)), Ncr("Critical", Today.AddDays(-2))], 2, [], [], Today), "ncr").Score); // never below zero
    }

    [Fact]
    public void A_supplier_with_no_linked_quality_record_has_those_measures_unmeasured_not_perfect()
    {
        var card = ScorecardRules.Compute(Today, [], 0, [], [new CertificationFact(Today.AddYears(-1), Today.AddYears(1), false)], Today);
        Assert.False(Component(card, "ncr").Available);
        Assert.False(Component(card, "capa").Available);
        Assert.Equal(100m, card.Score); // only the certification is measured
        Assert.StartsWith("Not measured", Component(card, "ncr").Measure, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_corrective_actions_are_counted_as_of_the_date()
    {
        var facts = new[] { Capa(Today.AddDays(-30)), Capa(Today.AddDays(-60), Today.AddDays(-40)), Capa(Today.AddDays(-5)) };
        Assert.Equal(50m, Component(ScorecardRules.Compute(Today, facts, 3, [], [], Today), "capa").Score); // two are open
        Assert.Equal(75m, Component(ScorecardRules.Compute(Today.AddDays(-45), facts, 3, [], [], Today), "capa").Score); // 45 days ago only the 60-day-old one was open... and the 30-day-old one did not exist yet
    }

    [Fact]
    public void Responsiveness_is_the_share_of_requests_answered_on_time()
    {
        var requests = new[]
        {
            new RequestOutcome(Today.AddDays(-40), Today.AddDays(-30), Today.AddDays(-35)), // on time
            new RequestOutcome(Today.AddDays(-40), Today.AddDays(-30), Today.AddDays(-20)), // late
            new RequestOutcome(Today.AddDays(-40), Today.AddDays(-30), null),               // never answered, due
            new RequestOutcome(Today.AddDays(-5), Today.AddDays(10), null),                  // not due yet: not counted
            new RequestOutcome(Today.AddDays(-5), null, Today.AddDays(-3)),                  // no due date, answered: on time
        };
        var rfq = Component(ScorecardRules.Compute(Today, [], 0, requests, [], Today), "rfq");
        Assert.Equal(50m, rfq.Score); // 2 of 4
        Assert.Contains("2 of 4", rfq.Measure, StringComparison.Ordinal);
        Assert.False(Component(ScorecardRules.Compute(Today, [], 0, [new RequestOutcome(Today.AddDays(-5), Today.AddDays(10), null)], [], Today), "rfq").Available);
    }

    [Fact]
    public void Certifications_count_when_not_revoked_and_not_expired_on_the_date()
    {
        var certs = new[] { new CertificationFact(Today.AddYears(-2), Today.AddYears(1), false), new CertificationFact(Today.AddYears(-2), Today.AddDays(-1), false), new CertificationFact(Today.AddYears(-1), Today.AddYears(1), true), new CertificationFact(Today.AddDays(1), Today.AddYears(1), false) };
        var c = Component(ScorecardRules.Compute(Today, [], 0, [], certs, Today), "certs");
        Assert.Equal(33.3m, c.Score); // 1 of 3 (the last one is not issued yet)
    }

    [Fact]
    public void Delivery_is_never_measured_and_its_weight_is_left_out_of_the_score()
    {
        var card = ScorecardRules.Compute(Today, [], 0, [], [new CertificationFact(Today.AddYears(-1), Today.AddYears(1), false)], Today);
        var delivery = Component(card, "delivery");
        Assert.False(delivery.Available);
        Assert.Null(delivery.Score);
        Assert.Equal(100m, card.Score);
    }

    [Fact]
    public void The_score_is_the_weighted_average_of_the_measured_components()
    {
        // ncr 40 (score 70), capa 10 (100), rfq 20 (50), certs 20 (100): (2800 + 1000 + 1000 + 2000) / 90 = 75.6
        var facts = new[] { Ncr("Major", Today.AddDays(-3)) };
        var requests = new[] { new RequestOutcome(Today.AddDays(-40), Today.AddDays(-30), Today.AddDays(-35)), new RequestOutcome(Today.AddDays(-40), Today.AddDays(-30), null) };
        var certs = new[] { new CertificationFact(Today.AddYears(-1), Today.AddYears(1), false) };
        var card = ScorecardRules.Compute(Today, facts, 1, requests, certs, Today);
        Assert.Equal(75.6m, card.Score);
        Assert.Equal("Watch", card.Band);
    }

    [Fact]
    public void Bands_and_the_empty_card()
    {
        Assert.Equal("Good", ScorecardRules.Band(85));
        Assert.Equal("Watch", ScorecardRules.Band(84.9m));
        Assert.Equal("Watch", ScorecardRules.Band(65));
        Assert.Equal("Poor", ScorecardRules.Band(64.9m));
        Assert.Equal("Not enough data", ScorecardRules.Band(null));
        var empty = ScorecardRules.Compute(Today, [], 0, [], [], Today);
        Assert.Null(empty.Score);
        Assert.Equal("Not enough data", empty.Band);
    }

    [Fact]
    public void The_trend_has_twelve_monthly_points_ending_today_and_uses_the_same_formula()
    {
        var dates = ScorecardRules.TrendDates(Today);
        Assert.Equal(12, dates.Count);
        Assert.Equal(Today, dates[^1]);
        Assert.Equal(new DateOnly(2025, 11, 30), dates[0]);
        Assert.Equal(new DateOnly(2026, 9, 30), dates[^2]);
        var facts = new[] { Ncr("Major", new DateOnly(2026, 8, 15)) };
        var card = ScorecardRules.Compute(Today, facts, 1, [], [], Today);
        Assert.Equal(12, card.Trend.Count);
        Assert.Equal(100m, card.Trend.First(p => p.AsOf == new DateOnly(2026, 7, 31)).Score); // before the nonconformance
        Assert.Equal(76m, card.Trend.First(p => p.AsOf == new DateOnly(2026, 8, 31)).Score);  // after it: (40 * 70 + 10 * 100) / 50
        Assert.Equal(card.Score, card.Trend[^1].Score);
    }

    [Fact]
    public void The_same_inputs_give_the_same_card()
    {
        var facts = new[] { Ncr("Minor", Today.AddDays(-9)), Capa(Today.AddDays(-9)) };
        var a = ScorecardRules.Compute(Today, facts, 2, [], [], Today);
        var b = ScorecardRules.Compute(Today, facts, 2, [], [], Today);
        Assert.Equal(a.Score, b.Score);
        Assert.Equal(a.Trend.Select(t => t.Score), b.Trend.Select(t => t.Score));
        Assert.Equal(ScorecardRules.Formula.Count, a.Formula.Count);
        Assert.Equal(100, ScorecardRules.Weights.Values.Sum());
    }

    // ---------------------------------------------------------------- the lines of a new request

    [Fact]
    public void Request_lines_parse_with_an_optional_need_by_date_and_format_back()
    {
        var (lines, error) = RfqLines.Parse("P-000001 100 2026-12-01\nP-000002, 2.5\n");
        Assert.Null(error);
        Assert.Equal(2, lines.Count);
        Assert.Equal(new DateOnly(2026, 12, 1), lines[0].NeedBy);
        Assert.Equal(2.5m, lines[1].Quantity);
        Assert.Null(lines[1].NeedBy);
        Assert.Equal("P-000001 100 2026-12-01\nP-000002 2.5", RfqLines.Format(lines));
        Assert.NotNull(RfqLines.Parse("P-000001").Error);
        Assert.NotNull(RfqLines.Parse("P-000001 abc").Error);
        Assert.NotNull(RfqLines.Parse("P-000001 0").Error);
        Assert.NotNull(RfqLines.Parse("P-000001 5 01/12/2026").Error);
        Assert.Empty(RfqLines.Parse("").Lines);
    }

    [Fact]
    public void Price_tiers_parse_from_text()
    {
        Assert.Null(RfqLines.ParseTiers("  ").Tiers);
        var (tiers, error) = RfqLines.ParseTiers("100:2.50, 500:2.10");
        Assert.Null(error);
        Assert.Equal([(100m, 2.5m), (500m, 2.1m)], tiers!.Select(t => (t.Quantity, t.UnitPrice)));
        Assert.NotNull(RfqLines.ParseTiers("100-2.5").Error);
        Assert.NotNull(RfqLines.ParseTiers("x:2").Error);
    }

    [Fact]
    public void The_purchased_parts_of_a_structure_become_request_lines_added_up_and_multiplied()
    {
        CoverageLine Line(string number, decimal qty, bool required) => new(1, 10, "10", number, number + " name", "A", qty, "EA", null, required, "covered", 1);
        var report = new CoverageReport("ebom", "P-000003", "Pod", [Line("P-2", 4, true), Line("P-1", 2, true), Line("P-2", 1, true), Line("P-9", 7, false)], 3, 0, 0, 1, "UNCLASSIFIED");
        var lines = RfqLines.FromCoverage(report, 3, "2026-12-01");
        Assert.Equal(["P-1", "P-2"], lines.Select(l => l.PartNumber));
        Assert.Equal([6m, 15m], lines.Select(l => l.Quantity));
        Assert.All(lines, l => Assert.Equal(new DateOnly(2026, 12, 1), l.NeedBy));
    }

    // ---------------------------------------------------------------- the chart and its table alternative

    [Fact]
    public void The_trend_chart_is_an_image_with_a_description_and_the_table_holds_the_same_numbers()
    {
        var points = new[] { new TrendChartView.TrendPoint("Aug 26", 100m), new TrendChartView.TrendPoint("Sep 26", null), new TrendChartView.TrendPoint("Oct 26", 70.5m) };
        var cut = Render<TrendChartView>(p => p.Add(x => x.Title, "Score over time").Add(x => x.Points, points));
        var svg = cut.Find("svg.vis-trend");
        Assert.Equal("img", svg.GetAttribute("role"));
        Assert.NotNull(svg.GetAttribute("aria-labelledby"));
        Assert.Contains("2 of 3 points measured", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[style]"));
        cut.Find("button[data-view=table]").Click();
        Assert.Empty(cut.FindAll("svg.vis-trend"));
        var rows = cut.FindAll("tbody tr");
        Assert.Equal(3, rows.Count);
        Assert.Contains("not measured", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("70.5", rows[2].TextContent, StringComparison.Ordinal);
        Assert.Equal("true", cut.Find("button[data-view=table]").GetAttribute("aria-pressed"));
    }

    [Fact]
    public void The_scorecard_card_shows_the_score_the_basis_of_each_measure_and_the_formula()
    {
        var card = ScorecardRules.Compute(Today, [Ncr("Minor", Today.AddDays(-9))], 1, [], [new CertificationFact(Today.AddYears(-1), Today.AddYears(1), false)], Today);
        var cut = Render<ScorecardCard>(p => p.Add(x => x.Card, card));
        Assert.Contains("out of 100", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Nonconformances", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Not measured: Keel records no deliveries yet.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("How the score is calculated", cut.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("details li"));
    }
}
