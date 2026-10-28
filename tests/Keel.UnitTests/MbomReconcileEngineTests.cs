using Keel.Bom;
using Keel.Core;

namespace Keel.UnitTests;

/// <summary>The reconciliation engine and the MBOM rules, with exact expected outputs (ADR-0024).</summary>
public sealed class MbomReconcileEngineTests
{
    private static readonly Guid Screw = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Plate = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Sub = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid EbomIter = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid OldIter = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static int _n;

    private static Guid Id() => new(++_n, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);

    private static EbomLineData E(int find, Guid child, decimal qty, string unit = "EA", EffectivityRange? eff = null, int alt = 0) => new(Id(), find, alt, child, qty, unit, eff);
    private static MbomLineData M(int find, Guid child, decimal qty, string unit = "EA", decimal? scrap = null, bool only = false, string supply = "Buy", EffectivityRange? eff = null) =>
        new(Id(), find, 0, child, qty, unit, scrap, false, supply, only, eff);
    private static LinkData L(MbomLineData m, EbomLineData e, string status = LinkStatuses.Matched, string? reason = null) => new(m.Id, e.Id, status, reason);

    private static ReconcileOutcome Run(IEnumerable<EbomLineData> ebom, IEnumerable<MbomLineData> mbom, IEnumerable<LinkData> links, Guid? derived = null, IEnumerable<EbomLineData>? elsewhere = null,
        ISet<Guid>? make = null, Dictionary<Guid, ChildMbomInfo>? children = null) =>
        ReconcileEngine.Run(new ReconcileInput([.. ebom], [.. mbom], [.. links], [.. elsewhere ?? []], derived ?? EbomIter, derived is null ? "A.1" : "A.1", EbomIter, "A.1",
            make is null ? new HashSet<Guid>() : new HashSet<Guid>(make), children ?? []));

    private static EffectivityRange Serial(long? from, long? to) => new("serial", "CTX", from, to);

    [Fact]
    public void A_one_to_one_structure_is_reconciled()
    {
        var e = E(10, Screw, 4);
        var m = M(10, Screw, 4);
        var r = Run([e], [m], [L(m, e)]);
        Assert.Equal(ReconcileStatuses.Reconciled, r.Status);
        Assert.Empty(r.Findings);
        Assert.Equal((1, 1, 1, 100m), (r.EbomLines, r.MbomLines, r.CoveredLines, r.CoveragePct));
    }

    [Fact]
    public void A_missing_ebom_line_is_an_error_and_lowers_coverage()
    {
        var e1 = E(10, Screw, 4);
        var e2 = E(20, Plate, 1);
        var m = M(10, Screw, 4);
        var r = Run([e1, e2], [m], [L(m, e1)]);
        Assert.Equal(ReconcileStatuses.NotReconciled, r.Status);
        Assert.Equal(50m, r.CoveragePct);
        var f = Assert.Single(r.Findings);
        Assert.Equal((FindingCodes.EbomLineMissing, FindingSeverities.Error, 20, (int?)null, Plate), (f.Code, f.Severity, f.EbomFind, f.MbomFind, f.ChildMasterId));
        Assert.Equal("No MBOM line implements this EBOM line.", f.Detail);
    }

    [Fact]
    public void A_missing_line_names_an_unlinked_mbom_line_of_the_same_part()
    {
        var e = E(10, Screw, 4);
        var m = M(15, Screw, 4);
        var r = Run([e], [m], []);
        Assert.Contains(r.Findings, f => f.Code == FindingCodes.EbomLineMissing && f.Detail == "MBOM line 15 is the same part but is not linked to this EBOM line.");
        Assert.Contains(r.Findings, f => f.Code == FindingCodes.MbomLineUnexplained && f.MbomFind == 15);
    }

    [Fact]
    public void An_mbom_line_without_a_source_is_unexplained_unless_flagged_mbom_only()
    {
        var e = E(10, Screw, 4);
        var m = M(10, Screw, 4);
        var glue = M(900, Plate, 0.05m, "L", only: true, supply: "Consume");
        var stray = M(910, Sub, 1);
        var r = Run([e], [m, glue, stray], [L(m, e)]);
        Assert.Equal(ReconcileStatuses.NotReconciled, r.Status);
        Assert.Equal(
            [(FindingCodes.MbomLineUnexplained, FindingSeverities.Error, 910), (FindingCodes.MbomOnlyLine, FindingSeverities.Info, 900)],
            r.Findings.Select(f => (f.Code, f.Severity, f.MbomFind ?? 0)).ToArray());
        var onlyFlagged = Run([e], [m, glue], [L(m, e)]);
        Assert.Equal(ReconcileStatuses.WithExceptions, onlyFlagged.Status);
        Assert.Equal((0, 0, 1), (onlyFlagged.Errors, onlyFlagged.Warnings, onlyFlagged.Infos));
    }

    [Fact]
    public void Quantity_differences_are_errors_with_the_expected_and_actual_values()
    {
        var e = E(10, Screw, 4);
        var m = M(10, Screw, 3);
        var f = Assert.Single(Run([e], [m], [L(m, e)]).Findings);
        Assert.Equal((FindingCodes.QuantityDifference, FindingSeverities.Error, 4m, 3m, "EA"), (f.Code, f.Severity, f.Expected, f.Actual, f.Unit));
        Assert.Equal("Expected 4 EA (EBOM), the MBOM has 3.", f.Detail);
    }

    [Fact]
    public void Scrap_is_planned_loss_on_top_of_the_net_quantity_and_never_a_difference()
    {
        var e = E(10, Screw, 4);
        var net = M(10, Screw, 4, scrap: 0.05m);
        Assert.Equal(ReconcileStatuses.Reconciled, Run([e], [net], [L(net, e)]).Status);
        Assert.Equal(4.2m, MbomRules.Gross(4, 0.05m));

        // The engineer entered the scrap-inflated quantity: 4 * 1.05 = 4.2 is not the net quantity, and the finding says so.
        var inflated = M(10, Screw, 4.2m, "EA", scrap: 0.05m);
        var f = Assert.Single(Run([e], [inflated], [L(inflated, e)]).Findings);
        Assert.Equal(FindingCodes.QuantityDifference, f.Code);
        Assert.Equal("The MBOM quantity equals the EBOM quantity plus the declared scrap (0.05); enter the net quantity and keep the scrap factor.", f.Detail);
    }

    [Fact]
    public void A_declared_quantity_adjustment_is_an_information_finding_with_its_reason()
    {
        var e = E(10, Screw, 4);
        var m = M(10, Screw, 6);
        var r = Run([e], [m], [L(m, e, LinkStatuses.QuantityAdjusted, "Two spares per kit")]);
        var f = Assert.Single(r.Findings);
        Assert.Equal((FindingCodes.QuantityAdjusted, FindingSeverities.Info, "Declared: Two spares per kit"), (f.Code, f.Severity, f.Detail));
        Assert.Equal(ReconcileStatuses.WithExceptions, r.Status);
    }

    [Fact]
    public void Splits_and_merges_are_compared_as_groups()
    {
        // 1:n: one EBOM line of 4 is built as two MBOM lines of 2 (two stations).
        var e = E(10, Screw, 4);
        var m1 = M(10, Screw, 2);
        var m2 = M(11, Screw, 2);
        Assert.Equal(ReconcileStatuses.Reconciled, Run([e], [m1, m2], [L(m1, e), L(m2, e)]).Status);

        // n:1: two EBOM lines of 1 are one MBOM line of 2.
        var e1 = E(10, Screw, 1);
        var e2 = E(20, Screw, 1);
        var merged = M(10, Screw, 2);
        Assert.Equal(ReconcileStatuses.Reconciled, Run([e1, e2], [merged], [L(merged, e1), L(merged, e2)]).Status);

        // The same split with one station short is one finding for the group.
        var short2 = M(11, Screw, 1);
        var f = Assert.Single(Run([e], [m1, short2], [L(m1, e), L(short2, e)]).Findings);
        Assert.Equal((FindingCodes.QuantityDifference, 4m, 3m), (f.Code, f.Expected, f.Actual));
    }

    [Fact]
    public void Units_are_never_converted()
    {
        var e = E(10, Plate, 2, "m");
        var m = M(10, Plate, 2000, "mm");
        var f = Assert.Single(Run([e], [m], [L(m, e)]).Findings);
        Assert.Equal((FindingCodes.UnitMismatch, FindingSeverities.Error, "EBOM unit m; MBOM unit mm. Units are not converted."), (f.Code, f.Severity, f.Detail));
        var declared = Assert.Single(Run([e], [m], [L(m, e, LinkStatuses.QuantityAdjusted, "Cut from stock in millimetres")]).Findings);
        Assert.Equal(FindingCodes.QuantityAdjusted, declared.Code);
    }

    [Fact]
    public void Substitutions_are_warnings_and_a_matched_link_between_different_parts_is_an_error()
    {
        var e = E(10, Screw, 4);
        var m = M(10, Plate, 4);
        var sub = Assert.Single(Run([e], [m], [L(m, e, LinkStatuses.Substituted, "Alternate vendor part")]).Findings);
        Assert.Equal((FindingCodes.Substitution, FindingSeverities.Warning, "Alternate vendor part"), (sub.Code, sub.Severity, sub.Detail));
        Assert.Equal(ReconcileStatuses.WithExceptions, Run([e], [m], [L(m, e, LinkStatuses.Substituted, "Alternate vendor part")]).Status);

        var wrong = Assert.Single(Run([e], [m], [L(m, e)]).Findings);
        Assert.Equal((FindingCodes.LinkChildMismatch, FindingSeverities.Error), (wrong.Code, wrong.Severity));

        var same = M(10, Screw, 4);
        Assert.Equal(FindingCodes.SubstitutionSamePart, Assert.Single(Run([e], [same], [L(same, e, LinkStatuses.Substituted, "x")]).Findings).Code);
    }

    [Fact]
    public void Effectivity_gaps_are_errors_and_excess_is_a_warning()
    {
        var e = E(10, Screw, 4, eff: Serial(1, 100));
        var m = M(10, Screw, 4, eff: Serial(1, 80));
        var r = Run([e], [m], [L(m, e)]);
        var gap = Assert.Single(r.Findings);
        Assert.Equal((FindingCodes.EffectivityGap, FindingSeverities.Error), (gap.Code, gap.Severity));
        Assert.Equal("EBOM applies to serial 1-100 (CTX); the MBOM to serial 1-80 (CTX). Not covered by the MBOM: serial 81-100 (CTX).", gap.Detail);
        Assert.Equal(ReconcileStatuses.NotReconciled, r.Status);

        var wide = M(10, Screw, 4, eff: Serial(1, 120));
        var excess = Assert.Single(Run([e], [wide], [L(wide, e)]).Findings);
        Assert.Equal((FindingCodes.EffectivityExcess, FindingSeverities.Warning), (excess.Code, excess.Severity));
        Assert.Equal("EBOM applies to serial 1-100 (CTX); the MBOM to serial 1-120 (CTX). Covered by the MBOM beyond the EBOM: serial 101-120 (CTX).", excess.Detail);
    }

    [Fact]
    public void Effectivity_of_split_lines_is_compared_as_a_union_and_an_open_ebom_line_against_a_bounded_mbom_line_has_a_gap()
    {
        var e = E(10, Screw, 4, eff: Serial(1, 100));
        var a = M(10, Screw, 4, eff: Serial(1, 50));
        var b = M(11, Screw, 4, eff: Serial(51, 100));
        Assert.DoesNotContain(Run([e], [a, b], [L(a, e), L(b, e)]).Findings, f => f.Code.StartsWith("effectivity", StringComparison.Ordinal));

        var always = E(10, Screw, 4);
        var bounded = M(10, Screw, 4, eff: Serial(1, 80));
        Assert.Equal("EBOM applies to serial 1 and up (CTX); the MBOM to serial 1-80 (CTX). Not covered by the MBOM: serial 81 and up (CTX).",
            Assert.Single(Run([always], [bounded], [L(bounded, always)]).Findings).Detail);

        var other = E(10, Screw, 4, eff: new EffectivityRange("lot", "CTX", 1, 2));
        Assert.Equal(FindingCodes.EffectivityDimension, Assert.Single(Run([other], [bounded], [L(bounded, other)]).Findings).Code);
    }

    [Fact]
    public void Interval_arithmetic_unions_and_subtracts()
    {
        var u = ReconcileEngine.Union([Serial(1, 10), Serial(11, 20), Serial(30, 40)], 1);
        Assert.Equal([(1L, 20L), (30L, 40L)], u);
        Assert.Equal([(21L, 29L)], ReconcileEngine.Subtract([(1L, 40L)], u));
        Assert.Equal([(1L, 4L), (8L, 10L)], ReconcileEngine.Subtract([(1L, 10L)], [(5L, 7L)]));
        Assert.Empty(ReconcileEngine.Subtract([(5L, 7L)], [(1L, 10L)]));
    }

    [Fact]
    public void Revision_drift_alone_is_stale_and_with_other_errors_is_not_reconciled()
    {
        var e = E(10, Screw, 4);
        var m = M(10, Screw, 4);
        var stale = Run([e], [m], [L(m, e)], derived: OldIter);
        var f = Assert.Single(stale.Findings);
        Assert.Equal((FindingCodes.RevisionDrift, FindingSeverities.Error), (f.Code, f.Severity));
        Assert.Equal("The MBOM was derived from EBOM A.1; it is reconciled against A.1. Regenerate or confirm the changes.", f.Detail);
        Assert.Equal(ReconcileStatuses.Stale, stale.Status);

        var changed = E(10, Screw, 5);
        var both = Run([changed], [m], [L(m, changed)], derived: OldIter);
        Assert.Equal(ReconcileStatuses.NotReconciled, both.Status);
        Assert.Equal(2, both.Errors);
    }

    [Fact]
    public void Links_to_a_line_of_an_earlier_ebom_iteration_are_carried_over_by_identity()
    {
        var old = E(10, Screw, 4);
        var current = E(10, Screw, 4);
        var m = M(10, Screw, 4);
        var r = Run([current], [m], [L(m, old)], derived: OldIter, elsewhere: [old]);
        Assert.Equal([FindingCodes.RevisionDrift], r.Findings.Select(f => f.Code).ToArray());
        Assert.Equal(100m, r.CoveragePct);
        // An old line with no counterpart now carries nothing: the MBOM line is unexplained.
        var gone = E(30, Plate, 1);
        var m2 = M(30, Plate, 1);
        var r2 = Run([current], [m, m2], [L(m, old), L(m2, gone)], derived: OldIter, elsewhere: [old, gone]);
        Assert.Contains(r2.Findings, f => f.Code == FindingCodes.MbomLineUnexplained && f.MbomFind == 30);
    }

    [Fact]
    public void Make_sub_assemblies_need_a_released_mbom_of_the_plant()
    {
        var e1 = E(10, Sub, 1);
        var e2 = E(20, Screw, 4);
        var m1 = M(10, Sub, 1, supply: "Make");
        var m2 = M(20, Screw, 4);
        var missing = Run([e1, e2], [m1, m2], [L(m1, e1), L(m2, e2)], make: new HashSet<Guid> { Sub });
        var f = Assert.Single(missing.Findings);
        Assert.Equal((FindingCodes.ChildMbomMissing, FindingSeverities.Error, Sub), (f.Code, f.Severity, f.ChildMasterId));
        var unreleased = Run([e1, e2], [m1, m2], [L(m1, e1), L(m2, e2)], make: new HashSet<Guid> { Sub }, children: new() { [Sub] = new ChildMbomInfo(true, false) });
        Assert.Equal((FindingCodes.ChildMbomUnreleased, FindingSeverities.Warning), (unreleased.Findings.Single().Code, unreleased.Findings.Single().Severity));
        var released = Run([e1, e2], [m1, m2], [L(m1, e1), L(m2, e2)], make: new HashSet<Guid> { Sub }, children: new() { [Sub] = new ChildMbomInfo(true, true) });
        Assert.Equal(ReconcileStatuses.Reconciled, released.Status);
        // A bought item stays a leaf: no MBOM is expected for it.
        var buy = M(10, Sub, 1, supply: "Buy");
        Assert.Equal(ReconcileStatuses.Reconciled, Run([e1], [buy], [L(buy, e1)], make: new HashSet<Guid> { Sub }).Status);
    }

    [Fact]
    public void An_mbom_with_no_ebom_source_reports_that_it_is_not_derived()
    {
        var only = M(10, Screw, 1, only: true);
        var r = ReconcileEngine.Run(new ReconcileInput([], [only], [], [], null, null, EbomIter, "A.1", new HashSet<Guid>(), new Dictionary<Guid, ChildMbomInfo>()));
        Assert.Equal([FindingCodes.NotDerived, FindingCodes.MbomOnlyLine], r.Findings.Select(f => f.Code).OrderBy(c => c == FindingCodes.NotDerived ? 0 : 1).ToArray());
        Assert.Equal(100m, r.CoveragePct);
    }

    [Fact]
    public void Results_are_deterministic_and_ordered_by_severity()
    {
        var e1 = E(10, Screw, 4);
        var e2 = E(20, Plate, 1);
        var m1 = M(10, Plate, 4);
        var a = Run([e1, e2], [m1], [L(m1, e1, LinkStatuses.Substituted, "alt")]);
        var b = Run([e2, e1], [m1], [L(m1, e1, LinkStatuses.Substituted, "alt")]);
        Assert.Equal(a.Findings.Select(f => (f.Code, f.Severity, f.EbomFind)), b.Findings.Select(f => (f.Code, f.Severity, f.EbomFind)));
        Assert.Equal([FindingSeverities.Error, FindingSeverities.Warning], a.Findings.Select(f => f.Severity).ToArray());
    }

    // ---------------------------------------------------------------- line and link rules, hashing

    [Fact]
    public void Line_validation_checks_scrap_operation_reference_supply_type_and_units()
    {
        string[] units = ["EA", "mm"], supply = ["Make", "Buy", "Consume"];
        Assert.Empty(MbomRules.ValidateLine(4, "EA", ["R1", "R2"], null, 10, 0.05m, "OP-10/A", "Make", units, supply).Errors);
        Assert.Contains(MbomRules.ValidateLine(4, "EA", null, null, 10, -0.1m, null, "Make", units, supply).Errors, e => e.StartsWith("The scrap factor", StringComparison.Ordinal));
        Assert.Contains(MbomRules.ValidateLine(4, "EA", null, null, 10, 11m, null, "Make", units, supply).Errors, e => e.StartsWith("The scrap factor", StringComparison.Ordinal));
        Assert.Contains(MbomRules.ValidateLine(4, "EA", null, null, 10, null, "bad op!", "Make", units, supply).Errors, e => e.StartsWith("The operation reference", StringComparison.Ordinal));
        Assert.Contains(MbomRules.ValidateLine(4, "EA", null, null, 10, null, null, "Scavenge", units, supply).Errors, e => e.StartsWith("Supply type", StringComparison.Ordinal));
        Assert.Contains(MbomRules.ValidateLine(2.5m, "EA", null, null, 10, null, null, "Make", units, supply).Errors, e => e.Contains("whole number", StringComparison.Ordinal));
        // Unlike the EBOM, designators need not match the quantity.
        Assert.Empty(MbomRules.ValidateLine(4, "EA", ["R1"], null, 10, null, null, "Make", units, supply).Errors);
    }

    [Fact]
    public void Link_validation_demands_reasons_for_declared_differences_and_refuses_links_on_mbom_only_lines()
    {
        Assert.Empty(MbomRules.ValidateLink(LinkStatuses.Matched, null, false));
        Assert.Single(MbomRules.ValidateLink(LinkStatuses.QuantityAdjusted, " ", false));
        Assert.Single(MbomRules.ValidateLink(LinkStatuses.Substituted, null, false));
        Assert.Single(MbomRules.ValidateLink(LinkStatuses.Matched, "why", false));
        Assert.Single(MbomRules.ValidateLink(LinkStatuses.Matched, null, true));
        Assert.Single(MbomRules.ValidateLink("approximately", null, false));
    }

    [Fact]
    public void The_content_hash_covers_lines_links_and_the_derivation_and_ignores_row_ids_and_order()
    {
        var ebom = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var line = new MbomRules.HashLine(10, 0, Screw, 4, "EA", "", null, false, "Buy", null, false, null, "");
        var line2 = new MbomRules.HashLine(20, 0, Plate, 1, "EA", "", 0.05m, false, "Make", "OP10", false, "n", "");
        var link = new MbomRules.HashLink(10, 0, ebom, LinkStatuses.Matched, null);
        var baseHash = MbomRules.ContentHash(EbomIter, [line, line2], [link]);
        Assert.Equal(baseHash, MbomRules.ContentHash(EbomIter, [line2, line], [link]));
        Assert.NotEqual(baseHash, MbomRules.ContentHash(EbomIter, [line with { Quantity = 5 }, line2], [link]));
        Assert.NotEqual(baseHash, MbomRules.ContentHash(EbomIter, [line, line2], []));
        Assert.NotEqual(baseHash, MbomRules.ContentHash(EbomIter, [line, line2], [link with { Status = LinkStatuses.Substituted, Reason = "x" }]));
        Assert.NotEqual(baseHash, MbomRules.ContentHash(OldIter, [line, line2], [link]));
        Assert.NotEqual(baseHash, MbomRules.ContentHash(EbomIter, [line, line2 with { Scrap = 0.06m }], [link]));
        Assert.Equal(64, baseHash.Length);
        Assert.NotEqual(MbomRules.ReviewHash(baseHash, Guid.Empty, baseHash), MbomRules.ReviewHash(baseHash, Guid.NewGuid(), baseHash));
    }

    [Fact]
    public void Default_supply_types_follow_the_child_part()
    {
        Assert.Equal("Consume", MbomRules.DefaultSupplyType("Consumable", "Buy"));
        Assert.Equal("Buy", MbomRules.DefaultSupplyType("Component", "Buy"));
        Assert.Equal("Make", MbomRules.DefaultSupplyType("Assembly", "Make"));
    }

    [Fact]
    public void An_ebom_selection_is_validated()
    {
        Assert.Empty(EbomSpec.Released.Validate());
        Assert.Single(new EbomSpec(EbomSpecKind.Effectivity).Validate());
        Assert.Single(new EbomSpec(EbomSpecKind.Baseline).Validate());
        Assert.Single(new EbomSpec(EbomSpecKind.Iteration, IterationLabel: "x").Validate());
        Assert.Empty(new EbomSpec(EbomSpecKind.Iteration, IterationLabel: "B.1").Validate());
        Assert.Equal("As of serial 5 (CTX)", new EbomSpec(EbomSpecKind.Effectivity, EffectivityRequest.ForSerial("CTX", 5)).Describe());
    }
}
