using Bunit;
using Keel.Audit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Parts;
using Keel.Web.Components.Pages.Bom;
using Keel.Web.Components.Pages.Change;
using Keel.Web.Components.Pages.Configuration;
using Keel.Web.Services;
using Keel.Workflow;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Keel.UnitTests;

/// <summary>Component tests (bUnit) for the configuration management UI (2.1): effectivity inputs, comparison tables, the effectivity column and its form state.</summary>
public sealed class ConfigurationComponentTests : BunitContext
{
    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)",
        cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;

    public ConfigurationComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var ds = NpgsqlDataSource.Create("Host=unused.invalid;Database=none");
        var audit = new PostgresAuditLog(ds, TimeProvider.System);
        Services.AddSingleton(new ChangeService(ds, audit, [], TimeProvider.System));
        Services.AddSingleton(new PartService(ds, audit, TimeProvider.System));
        Services.AddScoped<CurrentUser>();
        AddAuthorization().SetAuthorized("alice");
    }

    // ---------------------------------------------------------------- effectivity fields

    [Fact]
    public void The_effectivity_editor_shows_only_the_fields_the_chosen_type_needs()
    {
        var form = new EffectivityForm();
        var cut = Render<EffectivityFields>(p => p.Add(x => x.Value, form).Add(x => x.Label, "Line: "));
        Assert.Single(cut.FindAll("select"));
        Assert.Empty(cut.FindAll("input"));

        cut.Find("select").Change("serial");
        Assert.Equal("serial", form.Kind);
        Assert.Equal(["Line: End-item context", "Line: Effective from", "Line: Effective to"], cut.FindAll("input").Select(i => i.GetAttribute("aria-label")));

        cut.Find("select").Change("date");   // dates have no end-item context
        Assert.DoesNotContain(cut.FindAll("input"), i => i.GetAttribute("aria-label")!.Contains("context", StringComparison.Ordinal));
        Assert.Equal("From yyyy-MM-dd", cut.FindAll("input")[0].GetAttribute("placeholder"));

        cut.Render(p => p.Add(x => x.Value, form).Add(x => x.ShowTo, false));   // open at the top: the revision a change notice creates
        Assert.Single(cut.FindAll("input"));
    }

    [Fact]
    public void The_unit_selector_offers_serial_lot_and_date_in_a_context()
    {
        var form = new UnitForm();
        var cut = Render<UnitFields>(p => p.Add(x => x.Value, form));
        Assert.Equal(["End-item context", "Serial number", "Lot or block number", "Date"], cut.FindAll("input").Select(i => i.GetAttribute("aria-label")));
        cut.Find("input[aria-label='Serial number']").Change("150");
        Assert.Equal("150", form.Serial);
    }

    [Fact]
    public void Form_state_turns_text_into_inputs_and_requests_and_explains_mistakes()
    {
        Assert.Null(new EffectivityForm().ToInput());
        Assert.Equal(new EffectivityInput("serial", "ORCA-POD", "101", null), new EffectivityForm { Kind = "serial", Context = " ORCA-POD ", From = "101" }.ToInput());
        var round = EffectivityForm.FromRange(new EffectivityRange("date", null, EffectivityRange.DayNumber(new DateOnly(2027, 3, 1)), null));
        Assert.Equal(("date", "", "2027-03-01", ""), (round.Kind, round.Context, round.From, round.To));

        Assert.Contains("serial number, a lot or a date", new UnitForm().ToRequest().Error, StringComparison.Ordinal);
        Assert.Contains("whole number", new UnitForm { Serial = "x" }.ToRequest().Error, StringComparison.Ordinal);
        Assert.Contains("yyyy-MM-dd", new UnitForm { Date = "tomorrow" }.ToRequest().Error, StringComparison.Ordinal);
        Assert.Contains("context", new UnitForm { Serial = "5" }.ToRequest().Error, StringComparison.Ordinal);
        var ok = new UnitForm { Context = "orca-pod", Serial = "100", Date = "2027-01-02" }.ToRequest();
        Assert.Null(ok.Error);
        Assert.Equal("ORCA-POD", ok.Request!.Context);
        Assert.Equal((100, EffectivityRange.DayNumber(new DateOnly(2027, 1, 2))), (ok.Request.Serial, ok.Request.Day));
        Assert.Equal("context=ORCA-POD&serialTo=100&lotTo=&dateTo=", new UnitForm { Context = "ORCA-POD", Serial = "100" }.Query("To"));
    }

    // ---------------------------------------------------------------- comparison table

    private static DeepDiffLine Diff(BomChangeKind kind, string path, string? number, bool redacted = false, string[]? changes = null) =>
        new(kind, redacted, path.Count(c => c == '.') + 1, path, number, number is null ? null : $"Name of {number}", kind == BomChangeKind.Added ? null : "A.1", kind == BomChangeKind.Removed ? null : "B.1",
            10, 10, 2m, kind == BomChangeKind.Changed ? 4m : 2m, "EA", changes ?? [], redacted ? null : Itar.BannerText(), redacted ? null : Itar);

    [Fact]
    public void The_comparison_table_lists_changes_and_never_names_a_restricted_item()
    {
        var result = new DeepComparison("Baseline comparison", "BL-000001", "BL-000002",
        [
            Diff(BomChangeKind.Added, "10", "P-000010"),
            Diff(BomChangeKind.Removed, "20", null, redacted: true),
            Diff(BomChangeKind.Changed, "30.10", "P-000030", changes: ["Quantity 2 to 4"]),
        ], 5, 1, 1, 1, Itar.BannerText());
        var cut = Render<DeepDiffTable>(p => p.Add(x => x.Result, result));

        Assert.Contains("1 added, 1 removed, 1 changed", cut.Find("p").TextContent, StringComparison.Ordinal);
        Assert.Contains("5 unchanged", cut.Find("p").TextContent, StringComparison.Ordinal);
        var rows = cut.FindAll("tbody tr");
        Assert.Equal(3, rows.Count);
        Assert.Contains("P-000010", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("restricted", rows[1].ClassName, StringComparison.Ordinal);
        Assert.Contains("Restricted item", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("Quantity 2 to 4", rows[2].TextContent, StringComparison.Ordinal);
        Assert.Contains("A.1 to B.1", rows[2].TextContent, StringComparison.Ordinal);
        Assert.Equal(2, cut.FindAll("a[href^='parts/']").Count);   // the restricted row has no link

        var none = Render<DeepDiffTable>(p => p.Add(x => x.Result, new DeepComparison("Units", "serial 1", "serial 2", [], 3, 0, 0, 0, Itar.BannerText())));
        Assert.Contains("No differences", none.Markup, StringComparison.Ordinal);
        Assert.Empty(none.FindAll("table"));
    }

    // ---------------------------------------------------------------- structure grid

    [Fact]
    public void The_structure_grid_shows_the_effectivity_of_each_line()
    {
        var eff = new EffectivityRange("serial", "ORCA-POD", 101, null);
        var line = new BomLine(Guid.NewGuid(), null, 1, "10~1", 10, 1m, "EA", 1m, false, false, false, false, Guid.NewGuid(), "P-000002", "Plug Y", "Component", "A", 1, "Released", "Released", [], null,
            null, null, eff);
        var cut = Render<BomTreeGrid>(p => p.Add(x => x.Lines, [line]));
        Assert.Contains("Effectivity", cut.Find(".bom-head").TextContent, StringComparison.Ordinal);
        Assert.Equal("serial 101 and up (ORCA-POD)", cut.Find(".cell-eff").TextContent);
        Assert.Equal("10~1", cut.Find("[data-path]").GetAttribute("data-path"));   // the second alternative of find number 10
    }

    // ---------------------------------------------------------------- change notice

    [Fact]
    public void An_affected_revision_shows_the_effectivity_and_a_draft_notice_can_set_it()
    {
        var affected = new AffectedObjectView(Guid.NewGuid(), false, "part", "P-000001", "Bracket", "A", "Released", ChangeActions.Revise, "B", "In Work", Itar.BannerText(),
            DateTimeOffset.UnixEpoch, "alice", "serial 101 and up (ORCA-POD)");
        ChangeDetail Detail(bool editable) => new(Guid.NewGuid(), ChangeTypes.Ecn, "ECN-000001", "Revise", "d", "High", "II", "r", "Alice Example", "alice", "Draft", "Draft", !editable, false,
            Itar, Itar.BannerText(), null, [], [affected], [], [], [], new ChangePermissions(true, false, editable, []), null, null, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [], [Itar]);

        var draft = Render<AffectedPanel>(p => p.Add(x => x.Detail, Detail(true)));
        Assert.Contains("Effective serial 101 and up (ORCA-POD)", draft.Markup, StringComparison.Ordinal);
        Assert.Contains(draft.FindAll("select"), s => s.GetAttribute("aria-label")!.Contains("new revision", StringComparison.Ordinal));
        Assert.Contains(draft.FindAll("button"), b => b.TextContent == "Set");

        var frozen = Render<AffectedPanel>(p => p.Add(x => x.Detail, Detail(false)));
        Assert.Contains("Effective serial 101 and up (ORCA-POD)", frozen.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(frozen.FindAll("button"), b => b.TextContent == "Set");
    }
}
