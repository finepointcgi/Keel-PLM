using System.Globalization;
using System.Text.RegularExpressions;
using Bunit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Web.Components.Visuals;
using Keel.Web.Services;
using Keel.Web.Services.Visuals;
using Keel.Workflow;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.UnitTests;

/// <summary>Component tests of the relationship pictures (UX-3, ADR-0029): keyboard contract, legend, table alternative, redacted items, banner and filters.</summary>
public sealed class VisualsComponentTests : BunitContext
{
    private static readonly Markings Open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;
    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"],
        controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;

    public VisualsComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped<VisualsJs>();
        Services.AddScoped<ShellJs>();
    }

    private static ImpactGraph Graph(bool withMore = false)
    {
        var nodes = new List<GraphNode>
        {
            new("chg", GraphNodeKind.Change, "ECN-000001", "Revise bracket", "changes/ECN-000001", 0, "Change notice ECN-000001", Open.BannerText()),
            new("a:P-1", GraphNodeKind.Affected, "P-1", "Bracket · A → B", "parts/P-1", 1, "Affected part P-1", Open.BannerText(), 0, false, [new("Revision", "A to B")]),
            new("p:P-2", GraphNodeKind.Parent, "P-2", "Module · A", "parts/P-2", 2, "Assembly P-2 uses it at level 1", Open.BannerText(), 1),
            new("pr:0:0", GraphNodeKind.Restricted, ImpactGraphBuilder.RestrictedLabel, null, null, 2, "Restricted assembly, level 1 where used", null, 1),
            new("d:D-1", GraphNodeKind.Document, "D-1", "Drawing · A", "documents/D-1", 2, "Document D-1", Open.BannerText()),
            new("c:ECR-9", GraphNodeKind.Conflict, "ECR-000009", "Rework · Draft", "changes/ECR-000009", 3, "Conflict", Open.BannerText(), 0, true),
        };
        var edges = new List<GraphEdge> { new("chg", "a:P-1", "Revise"), new("a:P-1", "p:P-2", "2 EA · find 10"), new("a:P-1", "pr:0:0", "1 EA · find 30"), new("a:P-1", "d:D-1", "describes"), new("p:P-2", "c:ECR-9", "uses an affected part") };
        if (withMore)
        {
            nodes.Add(new GraphNode("more", GraphNodeKind.More, "3 more", "levels 2 to 4", null, 4, "3 more", null));
            edges.Add(new GraphEdge("p:P-2", "more", "more levels"));
        }
        return new ImpactGraph(nodes, edges, false, withMore ? 3 : 0, 1, Open.BannerText());
    }

    private IRenderedComponent<ImpactGraphView> RenderGraph(ImpactGraph g, bool table = false, Action? more = null) =>
        Render<ImpactGraphView>(p => p.Add(x => x.Graph, g).Add(x => x.ShowTable, table).Add(x => x.OnShowMoreLevels, EventCallback.Factory.Create(this, () => more?.Invoke())));

    [Fact]
    public void The_graph_is_a_labelled_diagram_with_a_roving_tab_stop_and_arrow_key_neighbours()
    {
        var cut = RenderGraph(Graph());
        Assert.NotNull(cut.Find("div.vis-viewport"));
        Assert.Equal("group", cut.Find("svg.vis-svg").GetAttribute("role"));
        var nodes = cut.FindAll("g.vis-node");
        Assert.Equal(6, nodes.Count);
        Assert.Equal(1, nodes.Count(n => n.GetAttribute("tabindex") == "0"));
        Assert.Equal("0", nodes[0].GetAttribute("tabindex")); // the first item in the focus order is the change itself
        Assert.All(nodes, n => Assert.False(string.IsNullOrWhiteSpace(n.GetAttribute("aria-label"))));
        var change = nodes[0];
        var affectedId = nodes.Single(n => n.GetAttribute("aria-label")!.StartsWith("Affected part", StringComparison.Ordinal)).Id;
        Assert.Equal(affectedId, change.GetAttribute("data-right"));
        Assert.EndsWith("-chg", cut.Find("[data-href='changes/ECN-000001']").Id, StringComparison.Ordinal);
        var affected = cut.Find($"[id='{affectedId}']");
        Assert.Equal(change.Id, affected.GetAttribute("data-left"));
        Assert.Equal(3, affected.GetAttribute("data-right")!.Split(' ').Length); // the assembly, the restricted assembly and the document
        // Up and down move inside a column.
        var column2 = cut.FindAll("g.vis-node").Where(n => n.GetAttribute("data-up") is not null || n.GetAttribute("data-down") is not null).ToList();
        Assert.True(column2.Count >= 2);
        // Enter opens (data-href), Ctrl+Enter is handled by the module; every openable node has a link role and a target.
        Assert.All(nodes.Where(n => n.GetAttribute("role") == "link"), n => Assert.False(string.IsNullOrEmpty(n.GetAttribute("data-href"))));
        // Tooltips come from data-tip for focus as well as hover (the module reads the same attribute).
        Assert.All(nodes, n => Assert.False(string.IsNullOrEmpty(n.GetAttribute("data-tip"))));
        // The frame offers zoom, fit and its keyboard instructions.
        Assert.Equal(["in", "out", "fit"], cut.FindAll("[data-vis-action]").Select(b => b.GetAttribute("data-vis-action")).ToArray());
        Assert.Contains("arrow", cut.Find(".vis-hint").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Diagram. Focus an item", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_legend_names_every_kind_with_a_shape_and_pattern_not_only_a_colour()
    {
        var cut = RenderGraph(Graph());
        var legend = cut.Find(".vis-legend");
        foreach (var name in new[] { "Change", "Affected part", "Linked document", "Stale manufacturing BOM", "Other open change", "Restricted item", "More levels" })
            Assert.Contains(name, legend.TextContent, StringComparison.Ordinal);
        var swatches = cut.FindAll(".vis-legend-swatch");
        Assert.Equal(7, swatches.Count);
        // Every swatch has an outline of its own; patterned kinds (dots, hatch) also carry a fill pattern.
        Assert.True(swatches.Select(s => s.QuerySelector("path.vis-shape")!.GetAttribute("d")).Distinct().Count() >= 6); // six different outlines; the seventh kind (restricted) shares the plain one but is hatched
        Assert.True(swatches.Count(s => s.QuerySelector("pattern") is not null) >= 3);
        Assert.True(legend.QuerySelectorAll("svg").All(s => s.GetAttribute("aria-hidden") == "true")); // the words carry the meaning for assistive technology
        // Shapes differ by kind on the nodes too (the outline path of a document is not the outline of a part).
        var outlines = cut.FindAll("g.vis-node").GroupBy(n => n.ClassList.First(c => c.StartsWith("vis-k-", StringComparison.Ordinal))).Select(g => g.First().QuerySelector("path.vis-shape")!.GetAttribute("d")).ToList();
        Assert.Equal(outlines.Count, outlines.Distinct().Count());
    }

    [Fact]
    public void A_restricted_node_shows_no_identifier_and_cannot_be_opened()
    {
        var cut = RenderGraph(Graph());
        var restricted = cut.FindAll("g.vis-node").Single(n => n.ClassList.Contains("vis-k-restricted"));
        Assert.Contains(ImpactGraphBuilder.RestrictedLabel, restricted.TextContent, StringComparison.Ordinal);
        Assert.False(restricted.HasAttribute("data-href"));
        Assert.Equal("button", restricted.GetAttribute("role"));
        Assert.Contains("You may not open it", restricted.GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.NotNull(restricted.QuerySelector("path.vis-hatch")); // hatched, not just grey
        restricted.Click();
        var side = cut.Find("aside.vis-side");
        Assert.Contains("not authorized", side.TextContent, StringComparison.Ordinal);
        Assert.Empty(side.QuerySelectorAll("a"));
        Assert.DoesNotContain("ORCA", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Selecting_an_item_shows_its_details_with_a_link_and_the_more_item_expands_a_level()
    {
        var more = 0;
        var cut = RenderGraph(Graph(withMore: true), more: () => more++);
        cut.FindAll("g.vis-node").Single(n => n.GetAttribute("aria-label")!.StartsWith("Affected part", StringComparison.Ordinal)).Click();
        var side = cut.Find("aside.vis-side");
        Assert.Contains("P-1", side.TextContent, StringComparison.Ordinal);
        Assert.Equal("parts/P-1", side.QuerySelector("a.button")!.GetAttribute("href"));
        Assert.Equal("_blank", side.QuerySelectorAll("a")[^1].GetAttribute("target"));
        Assert.Contains("is-selected", cut.FindAll("g.vis-node").Single(n => n.GetAttribute("aria-label")!.StartsWith("Affected part", StringComparison.Ordinal)).ClassName, StringComparison.Ordinal);
        // The edges of the selected item show their labels.
        Assert.NotEmpty(cut.FindAll("g.vis-edge.is-active text"));
        cut.FindAll("g.vis-node").Single(n => n.ClassList.Contains("vis-k-more")).Click();
        Assert.Equal(1, more);
    }

    [Fact]
    public void The_table_view_has_the_same_items_as_the_map_and_no_diagram()
    {
        var g = Graph();
        var cut = RenderGraph(g, table: true);
        Assert.Empty(cut.FindAll("svg.vis-svg"));
        var rows = cut.FindAll("tbody tr");
        Assert.Equal(g.Nodes.Count, rows.Count);
        foreach (var n in g.Nodes.Where(n => !n.IsRestricted)) Assert.Contains(rows, r => r.TextContent.Contains(n.Label, StringComparison.Ordinal));
        var restricted = rows.Single(r => r.ClassList.Contains("restricted"));
        Assert.Contains(ImpactGraphBuilder.RestrictedLabel, restricted.TextContent, StringComparison.Ordinal);
        Assert.Empty(restricted.QuerySelectorAll("a"));
        Assert.Contains("describes", cut.Markup, StringComparison.Ordinal); // how each item connects
        Assert.NotNull(cut.Find("caption.sr-only"));
    }

    [Fact]
    public async Task The_viewport_attaches_its_behaviour_and_removes_it_when_it_goes_away()
    {
        var module = JSInterop.SetupModule("./_content/Keel.Web/js/visuals.js");
        module.Mode = JSRuntimeMode.Loose;
        var cut = RenderGraph(Graph());
        module.VerifyInvoke("attach");
        await DisposeComponentsAsync();
        module.VerifyInvoke("detach");
    }

    // ---------------------------------------------------------------- treemap

    private static BomLine Line(string path, string? number, decimal ext, bool hasChildren = false, bool redacted = false, string state = "Released", Markings? m = null)
    {
        var parts = path.Split('.');
        return new BomLine(Guid.NewGuid(), null, parts.Length, path, int.Parse(parts[^1], CultureInfo.InvariantCulture), 1, "EA", ext, redacted, false, false, hasChildren && !redacted,
            redacted ? null : Guid.NewGuid(), redacted ? null : number, redacted ? null : $"Name {number}", "Component", redacted ? null : "A", redacted ? null : 1, redacted ? null : state, redacted ? null : state, [], null,
            redacted ? null : (m ?? Open).BannerText(), redacted ? null : m ?? Open);
    }

    private static List<BomLine> Structure() =>
    [
        Line("10", "P-A", 1, hasChildren: true), Line("10.10", "P-B", 30), Line("10.20", "P-C", 10, state: "InWork"), Line("20", "P-D", 45, m: Itar),
        Line("30", null, 20, redacted: true), Line("40", "P-E", 5),
    ];

    private IRenderedComponent<BomTreemapView> RenderTreemap(List<BomLine> lines, Action<IReadOnlyList<Markings>>? shown = null) =>
        Render<BomTreemapView>(p => p.Add(x => x.RootNumber, "P-TOP").Add(x => x.RootName, "Top").Add(x => x.Lines, lines)
            .Add(x => x.OnShown, EventCallback.Factory.Create<IReadOnlyList<Markings>>(this, m => shown?.Invoke(m))));

    [Fact]
    public void The_treemap_draws_one_tile_per_child_with_a_hatched_restricted_block_and_a_pattern_legend()
    {
        IReadOnlyList<Markings>? shown = null;
        var cut = RenderTreemap(Structure(), m => shown = m);
        var tiles = cut.FindAll("g.vis-tile");
        Assert.True(tiles.Count >= 5);
        var restricted = tiles.Single(t => t.ClassList.Contains("vis-k-restricted"));
        Assert.NotNull(restricted.QuerySelector("rect.vis-overlay")); // hatched
        Assert.False(restricted.HasAttribute("data-href"));
        Assert.DoesNotContain("P-A", restricted.TextContent, StringComparison.Ordinal);
        Assert.Contains("Restricted", restricted.TextContent, StringComparison.Ordinal);
        Assert.Contains("hatch", cut.Find(".vis-legend").InnerHtml, StringComparison.Ordinal);
        Assert.Contains("Lifecycle state", cut.Find("select[aria-label='Colour and pattern show']").TextContent, StringComparison.Ordinal);
        // The banner covers exactly the tiles that are drawn: the ITAR part is one of them.
        Assert.Contains(Itar, shown!);
        // Colour mode switches the legend and the classes.
        cut.Find("select[aria-label='Colour and pattern show']").Change(TreemapColour.Markings.ToString());
        Assert.Contains("ITAR (crosshatch)", cut.Find(".vis-legend").TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("g.vis-k-itar"));
    }

    [Fact]
    public void Click_drills_into_an_assembly_with_a_breadcrumb_and_backspace_goes_up()
    {
        IReadOnlyList<Markings>? shown = null;
        var cut = RenderTreemap(Structure(), m => shown = m);
        Assert.Empty(cut.FindAll("nav.tm-crumbs button"));
        var tile = cut.FindAll("g.vis-tile.vis-d1").Single(t => t.GetAttribute("aria-label")!.StartsWith("P-A", StringComparison.Ordinal));
        Assert.Equal("button", tile.GetAttribute("role")); // an assembly drills, a part opens
        Assert.Contains("drill", tile.GetAttribute("aria-label"), StringComparison.OrdinalIgnoreCase);
        tile.Click();
        Assert.Equal("P-A", cut.Find("nav.tm-crumbs [aria-current='location']").TextContent);
        Assert.Single(cut.FindAll("nav.tm-crumbs button"));
        Assert.Equal(2, cut.FindAll("g.vis-tile").Count); // B and C
        Assert.DoesNotContain(Itar, shown!); // the ITAR part is no longer on screen, so it no longer raises the banner
        Assert.All(cut.FindAll("g.vis-tile"), t => Assert.Equal("link", t.GetAttribute("role")));
        Assert.StartsWith("parts/", cut.FindAll("g.vis-tile")[0].GetAttribute("data-href"), StringComparison.Ordinal); // a leaf opens with Enter
        cut.Find("div.vis-split").KeyDown(new KeyboardEventArgs { Key = "Backspace" });
        Assert.Empty(cut.FindAll("nav.tm-crumbs button"));
        Assert.True(cut.FindAll("g.vis-tile").Count >= 5);
        // The breadcrumb button goes up too.
        cut.FindAll("g.vis-tile.vis-d1").Single(t => t.GetAttribute("aria-label")!.StartsWith("P-A", StringComparison.Ordinal)).Click();
        cut.Find("nav.tm-crumbs button").Click();
        Assert.Empty(cut.FindAll("nav.tm-crumbs button"));
    }

    [Fact]
    public void The_treemap_table_lists_the_children_of_the_assembly_in_view_and_the_metric_can_change()
    {
        var cut = RenderTreemap(Structure());
        cut.Find("[data-view='table']").Click();
        Assert.Empty(cut.FindAll("svg.vis-svg"));
        var rows = cut.FindAll("table.tm-table tbody tr");
        Assert.Equal(4, rows.Count); // A, D, the restricted block, E ... and nothing from below A
        Assert.Equal("P-D", rows[0].QuerySelectorAll("td")[1].TextContent.Trim()); // the largest first
        Assert.Contains(rows, r => r.ClassList.Contains("restricted") && r.TextContent.Contains("Restricted item", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.TextContent.Contains("P-B", StringComparison.Ordinal));
        cut.Find("select[aria-label='Area shows']").Change(TreemapMetric.DescendantCount.ToString());
        Assert.Equal("P-A", cut.FindAll("table.tm-table tbody tr")[0].QuerySelectorAll("td")[1].TextContent.Trim()); // two parts below beat everything else
        cut.FindAll("table.tm-table button.link").Single(b => b.TextContent == "P-A").Click();
        Assert.Equal(2, cut.FindAll("table.tm-table tbody tr").Count);
    }

    // ---------------------------------------------------------------- diff

    private static DiffModel Diff() => StructureDiffModel.FromDeep(new DeepComparison("Compare units", "serial 100", "serial 101",
    [
        new(BomChangeKind.Changed, false, 1, "10", "P-A", "Alpha", "A.1", "B.1", 10, 10, 2, 3, "EA", ["Revision A.1 to B.1", "Quantity 2 to 3"], null, null),
        new(BomChangeKind.Added, false, 2, "10.20", "P-N", "New part", null, "A.1", null, 20, null, 1, "EA", [], null, null),
        new(BomChangeKind.Removed, false, 2, "30.10", "P-R", "Old part", "A.1", null, 10, null, 4, null, "EA", [], null, null),
        new(BomChangeKind.Changed, true, 1, "40", null, null, null, null, 40, 40, null, null, "EA", ["Restricted item changed"], null, null),
    ], 12, 1, 1, 2, "UNCLASSIFIED"));

    [Fact]
    public void The_diff_shows_every_change_as_an_icon_and_a_word_with_counts_and_filter_chips()
    {
        var cut = Render<StructureDiffView>(p => p.Add(x => x.Model, Diff()));
        var chips = cut.FindAll("button.chip-toggle");
        Assert.Equal(["Added 1", "Removed 1", "Changed 2", "Unchanged 12"], chips.Select(c => Regex.Replace(c.TextContent.Trim(), @"\s+", " ")).ToArray());
        var rows = cut.FindAll("div.diff-row:not(.diff-head)");
        Assert.Equal(5, rows.Count);
        foreach (var row in rows.Where(r => r.HasAttribute("data-change")))
        {
            Assert.NotNull(row.QuerySelector("svg.icon")); // icon...
            Assert.Contains(new[] { "Added", "Removed", "Changed" }, w => row.QuerySelector(".diff-kind")!.TextContent.Contains(w, StringComparison.Ordinal)); // ...and word
        }
        Assert.Contains("Qty 2 → 3; Rev A.1 → B.1", cut.Markup, StringComparison.Ordinal);
        var restricted = rows.Single(r => r.ClassList.Contains("restricted"));
        Assert.Contains("Restricted item", restricted.TextContent, StringComparison.Ordinal);
        Assert.Empty(restricted.QuerySelectorAll("a"));
        Assert.Contains("12 unchanged lines are not listed", cut.Markup, StringComparison.Ordinal);
        // The context assembly can be collapsed and takes its changed child with it; a chip hides a kind.
        var contextRow = rows.Single(r => r.TextContent.Contains("Unchanged assembly", StringComparison.Ordinal));
        contextRow.QuerySelector("button.twisty")!.Click();
        Assert.Equal(4, cut.FindAll("div.diff-row:not(.diff-head)").Count);
        chips[0].Click();
        Assert.DoesNotContain("New part", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("false", cut.FindAll("button.chip-toggle")[0].GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Next_and_previous_change_move_focus_between_changed_rows_and_n_and_p_do_the_same()
    {
        var module = JSInterop.SetupModule("./_content/Keel.Web/js/shell.js");
        module.Mode = JSRuntimeMode.Loose;
        var cut = Render<StructureDiffView>(p => p.Add(x => x.Model, Diff()));
        var changeRows = cut.FindAll("div.diff-row[data-change]");
        Assert.Equal(4, changeRows.Count);
        cut.FindAll("button.secondary").Single(b => b.TextContent == "Next change").Click();
        Assert.Contains("Change 1 of 4", cut.Markup, StringComparison.Ordinal);
        var focused = module.Invocations.Where(i => i.Identifier == "focusSelector").ToList();
        Assert.Single(focused);
        cut.FindAll("button.secondary").Single(b => b.TextContent == "Next change").Click();
        Assert.Contains("Change 2 of 4", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("div.diff-row[data-change]")[1].KeyDown(new KeyboardEventArgs { Key = "p" });
        Assert.Contains("Change 1 of 4", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("div.diff-row[data-change]")[0].KeyDown(new KeyboardEventArgs { Key = "n" });
        Assert.Contains("Change 2 of 4", cut.Markup, StringComparison.Ordinal);
        // Previous from the first change wraps to the last.
        cut.FindAll("button.secondary").Single(b => b.TextContent == "Previous change").Click();
        cut.FindAll("button.secondary").Single(b => b.TextContent == "Previous change").Click();
        Assert.Contains("Change 4 of 4", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(6, module.Invocations.Count(i => i.Identifier == "focusSelector"));
    }

    // ---------------------------------------------------------------- timeline

    private static TimelineEvent Ev(int day, string kind, string title, string? revision = "A", int rank = 10, long order = 0, bool redacted = false, string? href = "parts/P-1") =>
        new(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero).AddDays(day), kind, redacted ? "A restricted item was involved" : title, null, redacted ? null : "alice", redacted ? null : revision, redacted ? null : href, redacted, rank, order);

    private static IReadOnlyList<TimelineEvent> Events() =>
    [
        Ev(0, TimelineKinds.Revision, "Revision A created", "A", order: 0), Ev(0, TimelineKinds.Iteration, "Version A.1 checked in", "A", order: 1),
        Ev(5, TimelineKinds.Signature, "Rita signed: Reviewed", "A", rank: 30), Ev(6, TimelineKinds.Restricted, "x", redacted: true, rank: 40),
        Ev(40, TimelineKinds.Change, "Listed in ECN-000001", "A", rank: 40), Ev(41, TimelineKinds.Revision, "Revision B created", "B", order: 2),
    ];

    [Fact]
    public void The_timeline_lists_events_oldest_first_grouped_by_revision_with_a_neutral_restricted_entry()
    {
        var cut = Render<TimelineView>(p => p.Add(x => x.Events, Events()));
        Assert.Equal(["Revision A", "Revision B"], cut.FindAll(".tl-group h4").Select(h => h.TextContent).ToArray());
        var items = cut.FindAll("li.tl-item");
        Assert.Equal(6, items.Count);
        Assert.Equal("Revision A created", items[0].QuerySelector(".tl-title strong, .tl-title a")!.TextContent);
        var restricted = items.Single(i => i.ClassList.Contains("restricted"));
        Assert.Contains("A restricted item was involved", restricted.TextContent, StringComparison.Ordinal);
        Assert.Empty(restricted.QuerySelectorAll("a"));
        Assert.DoesNotContain("alice", restricted.TextContent, StringComparison.Ordinal);
        Assert.Equal("parts/P-1", items[1].QuerySelector("a")!.GetAttribute("href"));
        Assert.All(items, i => Assert.Equal("0", i.GetAttribute("tabindex"))); // every entry can be reached by keyboard in the list
        cut.Find("select[aria-label='Group by']").Change("month");
        Assert.Equal(["2026-03", "2026-04"], cut.FindAll(".tl-group h4").Select(h => h.TextContent).ToArray());
    }

    [Fact]
    public void Filter_chips_hide_kinds_and_the_time_line_view_shows_the_same_events()
    {
        var cut = Render<TimelineView>(p => p.Add(x => x.Events, Events()));
        var chips = cut.FindAll("button.chip-toggle");
        Assert.Contains(chips, c => c.TextContent.Contains("Signatures", StringComparison.Ordinal));
        chips.Single(c => c.TextContent.Contains("Signatures", StringComparison.Ordinal)).Click();
        Assert.Equal(5, cut.FindAll("li.tl-item").Count);
        Assert.Contains("5 of 6 events shown", cut.Markup, StringComparison.Ordinal);
        cut.Find("[data-view='line']").Click();
        var nodes = cut.FindAll("g.tl-node");
        Assert.Equal(5, nodes.Count);
        Assert.Equal(1, nodes.Count(n => n.GetAttribute("tabindex") == "0"));
        Assert.All(nodes, n => Assert.False(string.IsNullOrEmpty(n.GetAttribute("aria-label"))));
        Assert.Contains("A restricted item was involved", cut.Markup, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("div.vis-viewport"));
    }

    // ---------------------------------------------------------------- workflow map

    [Fact]
    public void The_workflow_map_shows_steps_vote_rule_avatars_and_a_word_for_every_status_and_hides_participants_when_redacted()
    {
        var stages = new List<MapStage>
        {
            new("tech", "Technical review", "Task", null, 1, 1, "Approved", [new(Guid.NewGuid(), "Technical review", "review", "Signed", "Reviewed", new MapPerson("Rita Reviewer", "RR", false), null, DateTimeOffset.UnixEpoch, null, false)]),
            new("vote", "Approval", "Parallel", "2 of 3", 2, 3, "Active",
            [
                new(Guid.NewGuid(), "Approval", "approval", "Signed", "Approved", new MapPerson("Bob Approver", "BA", false), null, DateTimeOffset.UnixEpoch, null, false),
                new(Guid.NewGuid(), "Approval", "approval", "Pending", null, new MapPerson("Carol Approver", "CA", false), null, null, null, true),
                new(Guid.NewGuid(), "Approval", "approval", "Rejected", "Rejected", new MapPerson("Dave Approver", "DA", false), null, DateTimeOffset.UnixEpoch, null, false),
            ]),
            new("end", "Release", "End", null, 0, 0, "Waiting", []),
        };
        var map = new WorkflowMap(Guid.NewGuid(), "Part release review", "Active", false, stages, "UNCLASSIFIED");
        var cut = Render<WorkflowMapView>(p => p.Add(x => x.Map, map));
        var items = cut.FindAll("ol.wm > li");
        Assert.Equal(3, items.Count);
        Assert.Contains("Parallel group · 2 of 3 must approve", items[1].TextContent, StringComparison.Ordinal);
        Assert.Equal(["RR"], items[0].QuerySelectorAll(".wm-avatar").Select(a => a.TextContent).ToArray());
        var text = items[1].TextContent;
        Assert.Contains("Signed: Approved", text, StringComparison.Ordinal);
        Assert.Contains("Pending, overdue", text, StringComparison.Ordinal);
        Assert.Contains("Rejected: Rejected", text, StringComparison.Ordinal);
        Assert.Contains("Waiting", items[2].TextContent, StringComparison.Ordinal);
        Assert.All(cut.FindAll(".wm-glyph"), g => Assert.Equal("true", g.GetAttribute("aria-hidden") ?? "true")); // glyphs are decoration; the words carry the status
        Assert.All(cut.FindAll("li.wm-stage"), s => Assert.False(string.IsNullOrEmpty(s.GetAttribute("aria-label"))));

        var hidden = map with { Redacted = true, Stages = [.. stages.Select(s => s with { Tasks = [.. s.Tasks.Select(t => t with { Person = new MapPerson("Participant", "?", true), Meaning = null, At = null })] })] };
        var redacted = Render<WorkflowMapView>(p => p.Add(x => x.Map, hidden));
        Assert.DoesNotContain("Rita", redacted.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Approver", redacted.Markup, StringComparison.Ordinal);
        Assert.Equal(4, redacted.FindAll(".wm-name").Count(n => n.TextContent == "Participant"));
        Assert.Contains("participants are hidden", redacted.Markup, StringComparison.Ordinal);
    }
}

/// <summary>The design tokens the pictures use stay readable in every theme (4.5:1 for text on the tinted fills, 3:1 for the focus colour), and the new assets obey the CSP rules.</summary>
public sealed partial class VisualsContrastTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }

    private static string Web => Path.Combine(Root, "src", "Keel.Web", "wwwroot");

    [GeneratedRegex(@"(?<n>--[a-z0-9-]+):\s*(?<v>#[0-9a-fA-F]{6})\s*;")]
    private static partial Regex Declaration();

    private static Dictionary<string, string> Block(string anchor)
    {
        var css = File.ReadAllText(Path.Combine(Web, "css", "tokens.css"));
        var start = css.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, anchor);
        var open = css.IndexOf('{', start);
        var close = css.IndexOf('}', open);
        return Declaration().Matches(css[open..close]).ToDictionary(m => m.Groups["n"].Value, m => m.Groups["v"].Value, StringComparer.Ordinal);
    }

    public static TheoryData<string> Themes => new() { "light", "dark", "high-contrast" };

    private static Dictionary<string, string> Theme(string name) => name switch
    {
        "light" => Block(":root,\n:root[data-theme=\"light\"]"),
        "dark" => Block(":root[data-theme=\"dark\"] {"),
        _ => Block(":root[data-theme=\"high-contrast\"] {"),
    };

    [Theory]
    [MemberData(nameof(Themes))]
    public void Text_on_every_fill_the_pictures_use_has_at_least_4_5_to_1(string theme)
    {
        var t = Theme(theme);
        var fills = new[] { "--c-surface", "--c-surface-2", "--c-info-bg", "--c-warning-bg", "--c-danger-bg", "--c-success-bg" };
        var failures = new List<string>();
        foreach (var fill in fills)
            foreach (var text in new[] { "--c-text", "--c-muted" })
            {
                var ratio = ThemeContrastTests.Contrast(t[text], t[fill]);
                // Muted text is only used on plain surfaces; the node, tile, row and avatar text on tinted fills is --c-text.
                var needed = text == "--c-muted" && fill is not ("--c-surface" or "--c-surface-2") ? 0 : 4.5;
                if (ratio < needed) failures.Add($"{text} on {fill} = {ratio:F2}");
            }
        Assert.True(failures.Count == 0, $"{theme}: {string.Join("; ", failures)}");
        // Tooltip: the inverse pair.
        Assert.True(ThemeContrastTests.Contrast(t["--c-bg"], t["--c-text"]) >= 4.5, $"{theme}: tooltip");
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_focus_colour_and_the_outlines_stand_out_from_every_fill_with_at_least_3_to_1(string theme)
    {
        var t = Theme(theme);
        foreach (var fill in new[] { "--c-surface", "--c-surface-2", "--c-info-bg", "--c-warning-bg", "--c-danger-bg", "--c-success-bg" })
            Assert.True(ThemeContrastTests.Contrast(t["--c-focus"], t[fill]) >= 3.0, $"{theme}: focus on {fill} = {ThemeContrastTests.Contrast(t["--c-focus"], t[fill]):F2}");
        foreach (var outline in new[] { "--c-success", "--c-danger", "--c-warning", "--c-info", "--c-border-strong" })
            Assert.True(ThemeContrastTests.Contrast(t[outline], t["--c-surface"]) >= 3.0, $"{theme}: {outline} on the surface = {ThemeContrastTests.Contrast(t[outline], t["--c-surface"]):F2}");
    }

    [Fact]
    public void The_stylesheet_uses_only_tokens_no_inline_style_no_external_resource_and_no_animation()
    {
        var css = File.ReadAllText(Path.Combine(Web, "css", "visuals.css"));
        Assert.Empty(Regex.Matches(css, @"#[0-9a-fA-F]{3,8}\b").Select(m => m.Value));
        Assert.DoesNotContain("url(http", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", css, StringComparison.Ordinal);
        Assert.DoesNotContain("@font-face", css, StringComparison.Ordinal);
        Assert.DoesNotContain("animation", css, StringComparison.Ordinal);
        Assert.DoesNotContain("transition", css, StringComparison.Ordinal);
        // The banner tones are not touched.
        Assert.DoesNotContain("--banner", css, StringComparison.Ordinal);
        var js = File.ReadAllText(Path.Combine(Web, "js", "visuals.js"));
        Assert.DoesNotMatch(@"\beval\s*\(|new\s+Function\s*\(|innerHTML|insertAdjacentHTML|document\.write", js);
        Assert.DoesNotMatch(@"(https?:)?//[a-z0-9.-]+\.[a-z]{2,}", js.Replace("w3.org", ""));
        // The page shell loads the stylesheet.
        Assert.Contains("_content/Keel.Web/css/visuals.css", File.ReadAllText(Path.Combine(Root, "src", "Keel.Web", "Components", "App.razor")), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_icon_the_pictures_name_exists_in_the_sprite()
    {
        var sprite = File.ReadAllText(Path.Combine(Web, "icons.svg"));
        var ids = Regex.Matches(sprite, "<symbol id=\"i-([a-z-]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        var names = new[] { "change", "part", "document", "mbom", "warning", "lock", "plus", "revision", "checkin", "lifecycle", "signature", "baseline", "link", "markings", "close", "check" };
        Assert.All(names, n => Assert.Contains(n, ids));
        foreach (var kind in TimelineKinds.All) Assert.Contains(TimelineView.KindIcon(kind), ids);
        foreach (var kind in Enum.GetValues<GraphNodeKind>()) Assert.Contains(GraphGeometry.IconFor(kind), ids);
    }
}
