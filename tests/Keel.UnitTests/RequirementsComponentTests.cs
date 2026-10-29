using Bunit;
using Keel.Core;
using Keel.Requirements;
using Keel.Web.Components.Pages.Requirements;
using Keel.Web.Components.Visuals;
using Keel.Web.Services;
using Keel.Web.Services.Visuals;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.UnitTests;

/// <summary>Component and model tests for the requirements UI (8.3): the outline, restricted placeholders, escaping, the trace graph and its table, the matrix.</summary>
public sealed class RequirementsComponentTests : BunitContext
{
    private static readonly Markings Open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;
    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"], controllingOffice: "ACME",
        distributionReason: "Critical technology", program: "ORCA").Value;

    public RequirementsComponentTests()
    {
        _ = ShellTestKit.Register(this, Roles.Author, Roles.Viewer);
        Services.AddSingleton(new RequirementService(null!, null!, TimeProvider.System));
        Services.AddScoped<VisualsJs>();
    }

    private static RequirementRow Row(string key, string title, string text, string outline = "1", int depth = 0, string? parent = null, string status = "approved", Markings? m = null) =>
        new(false, outline, depth, key, parent, title, text, "functional", "high", "test", "why", status, "sha", (m ?? Open).BannerText(), m ?? Open);

    private static RequirementRow Hidden(string outline) => new(true, outline, 0, null, null, null, null, null, null, null, null, null, null, null, null);

    private static SpecDetail Detail(bool editable = true, bool canModify = true)
    {
        var rev = new SpecRevisionInfo(Guid.NewGuid(), "A", editable ? "InWork" : "Released", editable ? "In Work" : "Released", true, 3, DateTimeOffset.UnixEpoch, "alice");
        return new SpecDetail(new SpecSummary(Guid.NewGuid(), "RS-000001", "Pod", null, "A", 3, rev.State, rev.StateLabel, 2, Open.BannerText(), Open), "", [rev], rev, "", DateTimeOffset.UnixEpoch, "alice",
            canModify, false, [], editable);
    }

    [Fact]
    public void The_outline_shows_readable_requirements_escaped_and_a_restricted_one_as_a_bare_placeholder()
    {
        var set = new RequirementSet("RS-000001", "A", 3, "InWork", true, [Row("R-0001", "<script>alert(1)</script> Depth", "x <b>bold</b>", "1"), Hidden("2"), Row("R-0003", "Child", "c", "2.1", 1, "R-0001")], Open.BannerText());
        var cut = Render<RequirementOutline>(p => p.Add(x => x.Detail, Detail()).Add(x => x.Set, set));
        Assert.DoesNotContain("<script>", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", cut.Markup, StringComparison.Ordinal);
        var rows = cut.FindAll("tbody tr");
        Assert.Equal(3, rows.Count);
        Assert.Contains("restricted", rows[1].ClassName, StringComparison.Ordinal);
        Assert.Contains("Restricted requirement", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Null(rows[1].QuerySelector("button"));
        Assert.Null(rows[1].QuerySelector("a"));
        Assert.Contains("d1", rows[2].QuerySelector(".req-title")!.ClassName, StringComparison.Ordinal);
        Assert.Contains("2 of 2 readable", cut.Find("p[aria-live]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Editing_actions_appear_only_when_the_revision_is_editable_and_the_user_may_modify()
    {
        var set = new RequirementSet("RS-000001", "A", 3, "InWork", true, [Row("R-0001", "One", "t")], Open.BannerText());
        Assert.Contains(Render<RequirementOutline>(p => p.Add(x => x.Detail, Detail()).Add(x => x.Set, set)).FindAll("button"), b => b.TextContent.Contains("Edit", StringComparison.Ordinal));
        var locked = Render<RequirementOutline>(p => p.Add(x => x.Detail, Detail(editable: false)).Add(x => x.Set, set));
        Assert.DoesNotContain(locked.FindAll("button"), b => b.TextContent.Contains("Edit", StringComparison.Ordinal) || b.TextContent.Contains("Add requirement", StringComparison.Ordinal));
        Assert.Contains("cannot be edited", locked.Markup, StringComparison.Ordinal);
        var readOnly = Render<RequirementOutline>(p => p.Add(x => x.Detail, Detail(canModify: false)).Add(x => x.Set, set));
        Assert.DoesNotContain(readOnly.FindAll("button"), b => b.TextContent.Contains("Add child", StringComparison.Ordinal));
    }

    [Fact]
    public void Filtering_never_matches_a_restricted_placeholder_and_selecting_shows_the_text()
    {
        var set = new RequirementSet("RS-000001", "A", 3, "InWork", true, [Row("R-0001", "Depth", "Rated to 6000 m."), Hidden("2"), Row("R-0003", "Gimbal", "Pointing range.", "3")], Open.BannerText());
        var cut = Render<RequirementOutline>(p => p.Add(x => x.Detail, Detail()).Add(x => x.Set, set));
        cut.Find("input[aria-label='Filter requirements']").Input("gimbal");
        Assert.Single(cut.FindAll("tbody tr"));
        cut.Find("input[aria-label='Filter requirements']").Input("");
        Assert.Equal(3, cut.FindAll("tbody tr").Count);
    }

    // ---------------------------------------------------------------- the picture

    private static SpecTrace Trace(IReadOnlyList<RequirementRow> rows, IReadOnlyList<SpecLink> links, Dictionary<string, string>? verification = null) =>
        new("RS-000001", "A", 3, rows, links, verification ?? [], new Dictionary<string, VerificationSummary>(), Markings.AggregateBanner([Open]));

    private static SpecLink Link(string source, string type, string kind, string? number, string? key = null, bool redacted = false, bool suspect = false, string? name = "Name") =>
        new(Guid.NewGuid(), source, redacted ? "" : type, redacted ? "" : type, "none", redacted ? "" : kind, redacted, redacted ? null : number, key, redacted ? null : name, "A", suspect, suspect ? SuspectReasons.TargetRevised : null, redacted ? null : Open.BannerText(), redacted ? null : Open);

    [Fact]
    public void The_graph_puts_derived_requirements_to_the_right_and_restricted_ends_as_bare_nodes()
    {
        var rows = new[] { Row("R-0001", "Parent", "p"), Row("R-0002", "Child", "c", "2"), Hidden("3") };
        var links = new[]
        {
            Link("R-0002", "Derives from", "requirement", "RS-000001", "R-0001"),
            Link("R-0002", "Allocated to", "part", "P-000001"),
            Link("R-0001", "x", "part", null, redacted: true),
            Link("R-0001", "Verified by", "document", "D-000001", suspect: true),
        };
        var g = TraceGraphBuilder.Build(Trace(rows, links, new() { ["R-0002"] = VerificationStates.Failed }));
        Assert.Equal(["r:R-0001", "r:R-0002", "p:P-000001", "d:D-000001"], g.Nodes.Where(n => !n.IsRestricted).Select(n => n.Id));
        Assert.Equal(0, g.Nodes.Single(n => n.Id == "r:R-0001").Layer);
        Assert.Equal(1, g.Nodes.Single(n => n.Id == "r:R-0002").Layer);
        Assert.Equal(2, g.Nodes.Single(n => n.Id == "p:P-000001").Layer);
        Assert.Contains(g.Edges, e => e.From == "r:R-0001" && e.To == "r:R-0002");
        Assert.True(g.Nodes.Single(n => n.Id == "r:R-0002").Highlight); // failed verification
        Assert.True(g.Nodes.Single(n => n.Id == "r:R-0001").Highlight); // a suspect link
        var restricted = Assert.Single(g.Nodes, n => n.IsRestricted);
        Assert.Equal(TraceGraphBuilder.RestrictedLabel, restricted.Label);
        Assert.Null(restricted.Sublabel);
        Assert.Null(restricted.Href);
        Assert.Null(restricted.Banner);
        Assert.DoesNotContain(g.Nodes, n => n.Label == "R-0003");
        Assert.Contains(g.Edges, e => e.Label.Contains("suspect", StringComparison.Ordinal));
    }

    [Fact]
    public void The_graph_has_a_table_alternative_and_a_cap()
    {
        var rows = Enumerable.Range(1, 600).Select(i => Row($"R-{i:D4}", "T" + i, "x", i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
        var capped = TraceGraphBuilder.Build(Trace(rows, []));
        Assert.True(capped.Truncated);
        Assert.Equal(500, capped.Nodes.Count);
        var small = TraceGraphBuilder.Build(Trace([Row("R-0001", "Only", "t")], [Link("R-0001", "Allocated to", "part", "P-000001")]));
        var cut = Render<ImpactGraphView>(p => p.Add(x => x.Graph, small).Add(x => x.ShowTable, true).Add(x => x.LegendItems, TraceGraphBuilder.Legend).Add(x => x.MapLabel, "Traceability graph"));
        Assert.Contains("R-0001", cut.Find("table.data").TextContent, StringComparison.Ordinal);
        Assert.Contains("P-000001", cut.Find("table.data").TextContent, StringComparison.Ordinal);
        var picture = Render<ImpactGraphView>(p => p.Add(x => x.Graph, small).Add(x => x.LegendItems, TraceGraphBuilder.Legend));
        Assert.Equal(2, picture.FindAll(".vis-node").Count);
        Assert.Contains("Requirement", picture.Find(".vis-legend").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_matrix_lists_readable_ends_across_and_caps_the_columns()
    {
        var type = new LinkTypeInfo("allocated-to", "Allocated to", "x", "part", "allocation", true, true, 30);
        var rows = new[] { Row("R-0001", "One", "o"), Row("R-0002", "Two", "t", "2") };
        var links = Enumerable.Range(1, 45).Select(i => Link("R-0001", "allocated-to", "part", $"P-{i:D6}")).Concat([Link("R-0002", "allocated-to", "part", "P-000001"), Link("R-0002", "allocated-to", "part", null, redacted: true)]).ToList();
        var m = TraceMatrixBuilder.Build(Trace(rows, links), type);
        Assert.Equal(RequirementService.MatrixColumnCap, m.Columns.Count);
        Assert.True(m.ColumnsTruncated);
        Assert.Contains(m.Columns, c => c.Id == "part:P-000001"); // the most linked part is kept
        Assert.DoesNotContain(m.Cells, c => c.ColumnId.Contains("null", StringComparison.Ordinal));
        var none = TraceMatrixBuilder.Build(Trace(rows, []), type);
        Assert.Empty(none.Columns);
        Assert.Equal("Allocated to", none.LinkLabel);
    }

    [Fact]
    public void Markings_of_restricted_rows_never_reach_the_page_banner_inputs()
    {
        var set = new RequirementSet("RS-000001", "A", 3, "InWork", true, [Row("R-0001", "Open", "t"), Hidden("2")], Markings.AggregateBanner([Open]));
        Assert.DoesNotContain("ITAR", set.Banner, StringComparison.Ordinal);
        Assert.DoesNotContain("ITAR", Itar.BannerText().Replace("ITAR", "", StringComparison.Ordinal) + set.Banner, StringComparison.Ordinal);
    }
}
