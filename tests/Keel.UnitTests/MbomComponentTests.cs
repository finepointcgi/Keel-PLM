using Bunit;
using Keel.Bom;
using Keel.Core;
using Keel.Web.Components.Pages.Mbom;
using Keel.Web.Services;
using Microsoft.AspNetCore.Components.Web;

namespace Keel.UnitTests;

/// <summary>Component tests (bUnit) for the manufacturing BOM UI (2.3): the virtualized lines grid, the EBOM selector and the reconciliation report.</summary>
public sealed class MbomComponentTests : BunitContext
{
    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)",
        cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;

    public MbomComponentTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static MbomLineView Line(int find, bool redacted = false, bool only = false, IReadOnlyList<LinkView>? links = null, decimal qty = 2, decimal? scrap = null) => new(
        Guid.NewGuid(), find, 0, redacted, redacted ? null : Guid.NewGuid(), redacted ? null : $"P-{find}", redacted ? null : $"Part {find}", redacted ? null : "Component", redacted ? null : "Buy",
        qty, "EA", scrap, false, redacted ? null : "Buy", redacted ? null : "OP-10", only, [], null, null, links ?? [], redacted ? null : Itar.BannerText(), redacted ? null : Itar);

    [Fact]
    public void Only_a_window_of_a_large_mbom_is_rendered_and_the_full_size_is_announced()
    {
        var lines = Enumerable.Range(1, 10_000).Select(i => Line(i * 10)).ToList();
        var cut = Render<MbomLinesGrid>(p => p.Add(x => x.Lines, lines));
        Assert.InRange(cut.FindAll("[role=row][aria-rowindex]").Count, 1, 200);
        Assert.Equal("10001", cut.Find("[role=grid]").GetAttribute("aria-rowcount"));
    }

    [Fact]
    public void Linked_unlinked_and_mbom_only_lines_are_told_apart_and_a_restricted_line_shows_only_quantities()
    {
        var linked = Line(10, links: [new LinkView(Guid.NewGuid(), 10, LinkStatuses.Matched, null, "P-10"), new LinkView(Guid.NewGuid(), 20, LinkStatuses.Substituted, "alt", "P-20")], scrap: 0.05m);
        var cut = Render<MbomLinesGrid>(p => p.Add(x => x.Lines, [linked, Line(20), Line(900, only: true), Line(30, redacted: true, qty: 4)]));
        var rows = cut.FindAll("[role=row][aria-rowindex]").Where(r => r.GetAttribute("aria-rowindex") != "1").ToList();

        Assert.Contains("10, 20*", rows[0].QuerySelector(".cell-ebom")!.TextContent, StringComparison.Ordinal); // * marks a declared difference
        Assert.Contains("5 %", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("OP-10", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Unlinked", rows[1].QuerySelector(".cell-ebom")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("MBOM only", rows[2].QuerySelector(".cell-ebom")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("restricted", rows[3].ClassName, StringComparison.Ordinal);
        Assert.Contains("Restricted item", rows[3].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("P-30", rows[3].TextContent, StringComparison.Ordinal);
        Assert.Null(rows[3].QuerySelector("a"));
        Assert.Null(rows[3].QuerySelector(".chip"));
        Assert.Empty(rows[3].QuerySelector(".cell-ebom")!.TextContent.Trim());
        Assert.Contains("4", rows[3].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Arrow_keys_move_the_roving_focus_between_rows()
    {
        var cut = Render<MbomLinesGrid>(p => p.Add(x => x.Lines, [Line(10), Line(20), Line(30)]));
        var first = cut.FindAll("[role=row][aria-rowindex]").First(r => r.GetAttribute("aria-rowindex") == "2");
        Assert.Equal("0", first.GetAttribute("tabindex"));
        first.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        var second = cut.FindAll("[role=row][aria-rowindex]").First(r => r.GetAttribute("aria-rowindex") == "3");
        Assert.Equal("0", second.GetAttribute("tabindex"));
        second.KeyDown(new KeyboardEventArgs { Key = "End" });
        Assert.Equal("0", cut.FindAll("[role=row][aria-rowindex]").First(r => r.GetAttribute("aria-rowindex") == "4").GetAttribute("tabindex"));
    }

    [Fact]
    public void The_ebom_selector_shows_only_the_fields_the_choice_needs_and_converts_to_a_spec()
    {
        var form = new EbomSpecForm();
        var cut = Render<EbomSpecFields>(p => p.Add(x => x.Value, form).Add(x => x.Label, "Source: "));
        Assert.Single(cut.FindAll("select"));
        Assert.Empty(cut.FindAll("input"));
        Assert.Equal(EbomSpecKind.LatestReleased, form.ToSpec().Spec!.Kind);

        cut.Find("select").Change(nameof(EbomSpecKind.Baseline));
        Assert.Equal(["Source: Baseline number"], cut.FindAll("input").Select(i => i.GetAttribute("aria-label")));
        Assert.Equal("Name the baseline.", form.ToSpec().Error);
        form.Baseline = " bl-000007 ";
        Assert.Equal("BL-000007", form.ToSpec().Spec!.Baseline);

        cut.Find("select").Change(nameof(EbomSpecKind.Iteration));
        Assert.Equal("Write the version like B.1.", form.ToSpec().Error);
        form.Iteration = "b.2";
        Assert.Equal("B.2", form.ToSpec().Spec!.IterationLabel);

        cut.Find("select").Change(nameof(EbomSpecKind.Effectivity));
        Assert.Equal(4, cut.FindAll("input").Count); // context, serial, lot, date
        Assert.Equal("Give a serial number, a lot or a date.", form.ToSpec().Error);
        form.Unit.Context = "orca-pod";
        form.Unit.Serial = "50";
        var spec = form.ToSpec().Spec!;
        Assert.Equal("As of serial 50 (ORCA-POD)", spec.Describe());
    }

    [Fact]
    public void The_default_choice_sends_no_spec_so_the_service_follows_the_last_reconciliation()
    {
        var form = new EbomSpecForm { Kind = "" };
        var cut = Render<EbomSpecFields>(p => p.Add(x => x.Value, form).Add(x => x.AllowDefault, true));
        Assert.Equal("Same as the last reconciliation", cut.Find("option[value='']").TextContent);
        Assert.Equal((null, null), form.ToSpec());
        Assert.Empty(cut.FindAll("input"));
        Assert.Empty(Render<EbomSpecFields>(p => p.Add(x => x.Value, new EbomSpecForm())).FindAll("option[value='']"));
    }

    private static ReconciliationReport Report(params FindingView[] findings) => new(
        new ReconciliationRunInfo(Guid.NewGuid(), "MB-000001", "A", 2, ReconcileStatuses.NotReconciled, "Not reconciled", "Latest released (P-1 A.2)", "B.1", "A.2", 3, 4, 2, 66.67m, 2, 1, 1,
            findings.Count(f => f.Redacted), DateTimeOffset.Parse("2027-03-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), "alice", 12, new string('a', 64), true),
        findings, [new BomDiffLine(BomChangeKind.Changed, false, "P-9", "Screw", 10, 10, 4, 6, "EA", "EA", ["Quantity 4 to 6"], null, null)], false, "UNCLASSIFIED");

    private static FindingView Finding(int seq, string code, string severity, bool redacted = false) =>
        new(seq, code, severity, MbomService.FindingTitle(code), redacted, null, null, redacted ? null : "P-9", redacted ? null : "Screw", 10, 10, 4m, 3m, "EA", redacted ? null : "details text");

    [Fact]
    public void The_report_groups_findings_by_severity_links_to_the_ebom_line_and_hides_restricted_parts()
    {
        var cut = Render<ReconciliationReportView>(p => p
            .Add(x => x.Number, "MB-000001")
            .Add(x => x.Report, Report(Finding(1, FindingCodes.QuantityDifference, FindingSeverities.Error), Finding(2, FindingCodes.EbomLineMissing, FindingSeverities.Error, redacted: true),
                Finding(3, FindingCodes.Substitution, FindingSeverities.Warning), Finding(4, FindingCodes.MbomOnlyLine, FindingSeverities.Info))));
        Assert.Contains("Not reconciled", cut.Find(".badge").TextContent, StringComparison.Ordinal);
        Assert.Equal(["error", "warning", "info"], cut.FindAll("table[data-severity]").Select(t => t.GetAttribute("data-severity")!));
        Assert.Contains("Errors (2)", cut.Markup, StringComparison.Ordinal);
        var errors = cut.Find("table[data-severity=error]");
        var rows = errors.QuerySelectorAll("tbody tr");
        Assert.Contains("Quantity differs from the EBOM", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Equal("mboms/MB-000001/side-by-side#ebom-10", rows[0].QuerySelector("a[href*=side-by-side]")!.GetAttribute("href"));
        Assert.Contains("4 EA", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("details text", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("restricted", rows[1].ClassName, StringComparison.Ordinal);
        Assert.Contains("Restricted item", rows[1].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("P-9", rows[1].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("details text", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Null(rows[1].QuerySelector("a[href^='parts/']"));
        Assert.Contains("1 finding about restricted parts", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("2 of 3 EBOM lines implemented (66.67 %)", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("EBOM changes since the MBOM was derived (A.2 to B.1)", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Quantity 4 to 6", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_report_says_so_and_a_tampered_one_is_flagged()
    {
        var clean = Report();
        var cut = Render<ReconciliationReportView>(p => p.Add(x => x.Number, "MB-000001").Add(x => x.Report, clean with { EbomChangesSinceDerivation = [] }));
        Assert.Contains("No findings", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("table"));
        Assert.DoesNotContain("recorded digest", cut.Markup, StringComparison.Ordinal);
        var bad = clean with { Run = clean.Run with { IntegrityOk = false } };
        cut.Render(p => p.Add(x => x.Number, "MB-000001").Add(x => x.Report, bad));
        Assert.Contains("does not match its recorded digest", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Wording_helpers_cover_every_status_and_severity()
    {
        Assert.Equal("Reconciled with exceptions", ReconcileStatuses.Label(ReconcileStatuses.WithExceptions));
        Assert.Equal("sev-error", MbomUiText.SeverityClass(FindingSeverities.Error));
        Assert.Equal("state-released", MbomUiText.StatusClass(ReconcileStatuses.Reconciled));
        Assert.Equal("5 %", MbomUiText.Percent(0.05m));
        Assert.Equal("", MbomUiText.Percent(null));
        Assert.Equal("mboms/MB-000001", MbomUiText.Href("MB-000001"));
        Assert.All(new[] { "linked", "ebom_only", "mbom_only", "unexplained" }, s => Assert.NotEmpty(MbomUiText.RowState(s)));
        Assert.All(typeof(ProposalKinds).GetFields().Select(f => (string)f.GetValue(null)!), k => Assert.NotEqual(k, MbomUiText.ProposalKind(k)));
        Assert.All(typeof(FindingCodes).GetFields().Select(f => (string)f.GetValue(null)!), c => Assert.NotEqual(c, MbomService.FindingTitle(c)));
    }
}
