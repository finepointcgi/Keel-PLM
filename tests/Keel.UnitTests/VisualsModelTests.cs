using System.Text.Json;
using System.Xml.Linq;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Web.Services.Visuals;
using Keel.Workflow;

namespace Keel.UnitTests;

/// <summary>Impact graph, SVG export, treemap and diff models and the workflow progress map (UX-3, ADR-0029): hidden items stay placeholders, counts count only what is readable.</summary>
public sealed class VisualsModelTests
{
    private static readonly Markings Open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;
    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"],
        controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;

    private const string HiddenPart = "P-SECRET-9";
    private const string HiddenName = "Hidden gearbox";

    private static ChangeDetail Detail(params AffectedObjectView[] affected) => new(
        Guid.NewGuid(), "ECN", "ECN-000001", "Revise bracket", "", "Medium", "II", "", "Alice", "alice", "InWork", "In Work", false, false, Open, Open.BannerText(), null, [],
        affected, [], [], [], new ChangePermissions(true, false, true, []), null, null, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [], []);

    private static WhereUsedEntry Parent(int level, string key, string used, Markings? m = null, int find = 10, string? effectivity = null) =>
        new(level, false, find, 2, "EA", Guid.NewGuid(), key, $"Name of {key}", "A", "Released", (m ?? Open).BannerText(), m ?? Open, used, effectivity);

    private static WhereUsedEntry Restricted(int level, string? leakedKey = HiddenPart, string? used = "P-1") =>
        new(level, true, 30, 1, "EA", null, leakedKey, HiddenName, "A", "Released", Itar.BannerText(), Itar, used);

    private static ImpactReport Report(Guid affectedId, IReadOnlyList<WhereUsedEntry> where, IReadOnlyList<LinkedDocument>? docs = null, IReadOnlyList<ImpactConflict>? conflicts = null,
        IReadOnlyList<StaleMbom>? mboms = null, int hiddenMboms = 0, bool hiddenAffected = false) =>
        new("ECN-000001",
            [hiddenAffected
                ? new ImpactObject(affectedId, true, "revise", null, null, null, null, [], 0, 0, [], null, null)
                : new ImpactObject(affectedId, false, "revise", "P-1", "Bracket", "A", Open.BannerText(), where, where.Count(w => w.Redacted), where.Count == 0 ? 0 : where.Max(w => w.Level), docs ?? [], null, null, mboms, hiddenMboms)],
            conflicts ?? [], new ImpactSummary(1, 0, 0, 0, 0, 0, 0, 0), Open.BannerText(), []);

    private static (ChangeDetail Detail, Guid Id) Standard()
    {
        var id = Guid.NewGuid();
        return (Detail(new AffectedObjectView(id, false, "part", "P-1", "Bracket", "A", "Released", "revise", "B", "InWork", Open.BannerText(), DateTimeOffset.UnixEpoch, "alice", "from serial 101")), id);
    }

    private static string Everything(ImpactGraph g) => JsonSerializer.Serialize(new { g.Nodes, g.Edges, g.Banner });

    [Fact]
    public void The_graph_has_the_change_at_the_left_then_affected_parts_parents_by_level_documents_mboms_and_conflicts()
    {
        var (detail, id) = Standard();
        var where = new List<WhereUsedEntry> { Parent(1, "P-2", "P-1"), Parent(2, "P-3", "P-2", find: 20), Restricted(2, used: "P-2"), Parent(1, "P-4", "P-1", effectivity: "serial 100 and up") };
        var docs = new[] { new LinkedDocument(false, "describes", "D-1", "Drawing", "Drawing", "A", "Released", Open.BannerText(), Open), new LinkedDocument(true, "references", null, null, null, null, null, null, null) };
        var conflicts = new[] { new ImpactConflict("uses an affected part", "ECN", "ECN-000007", "Rework P-3", "Draft", "P-3", Open.BannerText()) };
        var mboms = new[] { new StaleMbom("MB-1", "P-1", "PLANT-A", "A", 1, "Released", "Released", "A.1", "A.1", false, Open.BannerText(), Open) };
        var g = ImpactGraphBuilder.Build(detail, Report(id, where, docs, conflicts, mboms, hiddenMboms: 1));

        var byId = g.Nodes.ToDictionary(n => n.Id);
        Assert.Equal(0, byId["chg"].Layer);
        Assert.Equal(1, byId["a:P-1"].Layer);
        Assert.Equal("A → B", byId["a:P-1"].Sublabel![^5..]);
        Assert.Equal([2, 3], [byId["p:P-2"].Layer, byId["p:P-3"].Layer]);
        Assert.Equal(2, byId["d:D-1"].Layer);
        Assert.Equal(GraphNodeKind.Mbom, byId["m:MB-1"].Kind);
        Assert.Equal(4, byId["c:ECN-000007"].Layer); // one column to the right of the parent it touches
        Assert.Contains(g.Edges, e => e is { From: "chg", To: "a:P-1" } && e.Label == "Revise · effective from serial 101");
        Assert.Contains(g.Edges, e => e is { From: "p:P-2", To: "p:P-3" });
        Assert.Contains(g.Edges, e => e is { From: "a:P-1", To: "p:P-4" } && e.Label.Contains("serial 100 and up", StringComparison.Ordinal));
        Assert.Contains(g.Edges, e => e is { From: "p:P-3", To: "c:ECN-000007" });
        Assert.Equal(3, g.Nodes.Count(n => n.Kind == GraphNodeKind.Restricted)); // a parent, a document and a manufacturing BOM the viewer may not read
        Assert.All(g.Edges, e => { Assert.True(byId.ContainsKey(e.From)); Assert.True(byId.ContainsKey(e.To)); });
    }

    [Fact]
    public void Restricted_items_carry_no_identifier_label_banner_or_link_even_if_a_source_leaked_one()
    {
        var (detail, id) = Standard();
        var g = ImpactGraphBuilder.Build(detail, Report(id, [Restricted(1, used: "P-1"), Restricted(2, used: "P-9")],
            [new LinkedDocument(true, "describes", "D-LEAK", "Leaked drawing", "Drawing", "A", "Released", Itar.BannerText(), Itar)]));
        var text = Everything(g);
        Assert.DoesNotContain(HiddenPart, text, StringComparison.Ordinal);
        Assert.DoesNotContain(HiddenName, text, StringComparison.Ordinal);
        Assert.DoesNotContain("D-LEAK", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Leaked", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ORCA", text, StringComparison.Ordinal); // the hidden markings are not in the banner either
        foreach (var n in g.Nodes.Where(n => n.IsRestricted))
        {
            Assert.Equal(ImpactGraphBuilder.RestrictedLabel, n.Label);
            Assert.Null(n.Sublabel);
            Assert.Null(n.Href);
            Assert.Null(n.Banner);
            Assert.False(n.Openable);
        }
        Assert.Equal(3, g.Nodes.Count(n => n.IsRestricted));
        Assert.Equal(Open.BannerText(), g.Banner);
        // A restricted affected item is a placeholder too.
        var hiddenAffected = ImpactGraphBuilder.Build(Detail(), Report(Guid.NewGuid(), [], hiddenAffected: true));
        Assert.Equal(2, hiddenAffected.Nodes.Count);
        Assert.Contains(hiddenAffected.Nodes, n => n.IsRestricted && n.Href is null && n.Layer == 1);
    }

    [Fact]
    public void Levels_beyond_the_limit_fold_into_a_more_item_that_counts_only_readable_parents()
    {
        var (detail, id) = Standard();
        var where = new List<WhereUsedEntry> { Parent(1, "P-2", "P-1") };
        for (var i = 0; i < 4; i++) where.Add(Parent(2, $"P-3{i}", "P-2"));
        where.Add(Parent(3, "P-40", "P-30"));
        where.Add(Restricted(2, used: "P-2"));
        where.Add(Restricted(3, used: "P-30"));
        where.Add(Restricted(3, used: "P-30"));
        var g = ImpactGraphBuilder.Build(detail, Report(id, where), new ImpactGraphOptions(ShowLevels: 1));
        var more = Assert.Single(g.Nodes, n => n.Kind == GraphNodeKind.More);
        Assert.Equal("5 more", more.Label); // 4 parents at level 2 and 1 at level 3; the three restricted ones are not counted
        Assert.Equal(5, g.MoreParents);
        Assert.DoesNotContain(g.Nodes, n => n.Id.StartsWith("p:P-3", StringComparison.Ordinal));
        Assert.Equal(0, g.Nodes.Count(n => n.IsRestricted && n.Id.StartsWith("pr:", StringComparison.Ordinal))); // folded restricted ones are neither drawn nor counted
        Assert.Contains(g.Edges, e => e.To == "more" && e.From == "p:P-2");
        Assert.DoesNotContain("restricted", more.Description.Replace("restricted ones are not counted", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        // Showing every level removes the more item.
        var all = ImpactGraphBuilder.Build(detail, Report(id, where), new ImpactGraphOptions(ShowLevels: 10));
        Assert.DoesNotContain(all.Nodes, n => n.Kind == GraphNodeKind.More);
        Assert.Equal(0, all.MoreParents);
        // With nothing readable folded there is no more item, however many restricted items are folded.
        var onlyHidden = ImpactGraphBuilder.Build(detail, Report(id, [Parent(1, "P-2", "P-1"), Restricted(2, used: "P-2")]), new ImpactGraphOptions(ShowLevels: 1));
        Assert.DoesNotContain(onlyHidden.Nodes, n => n.Kind == GraphNodeKind.More);
    }

    [Fact]
    public void Kind_filters_and_conflicts_only_narrow_the_picture_and_the_banner_follows_what_is_drawn()
    {
        var (detail, id) = Standard();
        var cui = Markings.Create(ExportJurisdiction.None, DistributionStatement.B, "ENG", cuiCategories: ["PROPIN"], controllingOffice: "ACME", distributionReason: "Proprietary").Value;
        var where = new[] { Parent(1, "P-2", "P-1"), Parent(1, "P-3", "P-1") };
        var docs = new[] { new LinkedDocument(false, "describes", "D-1", "Drawing", "Drawing", "A", "Released", cui.BannerText(), cui) };
        var conflicts = new[] { new ImpactConflict("uses an affected part", "ECR", "ECR-000002", "Parent rework", "Draft", "P-3", Open.BannerText()) };
        var report = Report(id, where, docs, conflicts);

        var all = ImpactGraphBuilder.Build(detail, report);
        Assert.Contains("PROPIN", all.Banner, StringComparison.Ordinal);
        var noDocs = ImpactGraphBuilder.Build(detail, report, new ImpactGraphOptions(HiddenKinds: new HashSet<GraphNodeKind> { GraphNodeKind.Document }));
        Assert.DoesNotContain(noDocs.Nodes, n => n.Kind == GraphNodeKind.Document);
        Assert.DoesNotContain(noDocs.Edges, e => e.To == "d:D-1");
        Assert.DoesNotContain("PROPIN", noDocs.Banner, StringComparison.Ordinal);

        var only = ImpactGraphBuilder.Build(detail, report, new ImpactGraphOptions(OnlyConflicts: true));
        Assert.Equal(new[] { "chg", "a:P-1", "p:P-3", "c:ECR-000002" }.Order(), only.Nodes.Select(n => n.Id).Order());
        Assert.All(only.Edges, e => Assert.Contains(e.From, only.Nodes.Select(n => n.Id)));
        Assert.True(only.Nodes.First(n => n.Kind == GraphNodeKind.Conflict).Highlight);
    }

    [Fact]
    public void A_very_large_impact_is_cut_at_the_node_cap_with_a_flag_and_no_count()
    {
        var (detail, id) = Standard();
        var where = Enumerable.Range(0, 700).Select(i => Parent(1, $"P-{1000 + i}", "P-1", find: i + 1)).ToList();
        var g = ImpactGraphBuilder.Build(detail, Report(id, where));
        Assert.True(g.Truncated);
        Assert.Equal(500, g.Nodes.Count);
        Assert.Contains(g.Nodes, n => n.Kind == GraphNodeKind.Change);
        Assert.Contains(g.Nodes, n => n.Kind == GraphNodeKind.Affected);
        Assert.All(g.Edges, e => Assert.Contains(g.Nodes, n => n.Id == e.To));
        Assert.False(ImpactGraphBuilder.Build(detail, Report(id, where.Take(100).ToList())).Truncated);
    }

    [Fact]
    public void The_exported_svg_is_self_contained_names_the_banner_and_distribution_statement_and_hides_what_is_hidden()
    {
        var (detail, id) = Standard();
        var g = ImpactGraphBuilder.Build(detail, Report(id, [Parent(1, "P-2", "P-1"), Restricted(1, used: "P-1")]));
        var layout = LayeredLayout.Compute([.. g.Nodes.Select(n => { var (w, h) = GraphGeometry.Size(n.Kind); return new LayoutNodeInput(n.Id, n.Layer, w, h); })], [.. g.Edges.Select(e => new LayoutEdgeInput(e.From, e.To))]);
        var svg = GraphSvgWriter.Write(g, layout, "Impact map ECN-000001", "alice", new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));

        var doc = XDocument.Parse(svg); // well-formed
        XNamespace ns = "http://www.w3.org/2000/svg";
        var texts = doc.Descendants(ns + "text").Select(t => t.Value).ToList();
        Assert.Equal(2, texts.Count(t => t == g.Banner)); // top bar and bottom bar
        Assert.Contains(texts, t => t.StartsWith("DISTRIBUTION STATEMENT A", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("Restricted item", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("P-2", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("2026-10-28", StringComparison.Ordinal));
        Assert.DoesNotContain(HiddenPart, svg, StringComparison.Ordinal);
        Assert.DoesNotContain(HiddenName, svg, StringComparison.Ordinal);
        Assert.DoesNotContain("ORCA", svg, StringComparison.Ordinal);
        // Nothing active or external: no script, style sheet, style attribute, link or remote reference.
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", svg.Replace("http://www.w3.org/2000/svg", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

        // An ITAR picture carries the export warning and its own banner colour tone.
        var itar = new ImpactGraph([new GraphNode("chg", GraphNodeKind.Change, "ECN-2", "t", null, 0, "d", Itar.BannerText())], [], false, 0, 0, Markings.AggregateBanner([Itar]));
        var itarSvg = GraphSvgWriter.Write(itar, LayeredLayout.Compute([new("chg", 0, 196, 64)], []), "x", "alice", DateTimeOffset.UnixEpoch);
        Assert.Contains("DISTRIBUTION STATEMENT D", itarSvg, StringComparison.Ordinal);
        Assert.Contains("Arms Export Control Act", itarSvg, StringComparison.Ordinal);
        Assert.Contains("#502b85", itarSvg, StringComparison.Ordinal);
    }

    [Fact]
    public void Keyboard_navigation_follows_edges_to_the_right_and_left_and_columns_up_and_down()
    {
        var (detail, id) = Standard();
        var g = ImpactGraphBuilder.Build(detail, Report(id, [Parent(1, "P-2", "P-1"), Parent(1, "P-3", "P-1")]));
        var layout = LayeredLayout.Compute([.. g.Nodes.Select(n => new LayoutNodeInput(n.Id, n.Layer, 100, 40))], [.. g.Edges.Select(e => new LayoutEdgeInput(e.From, e.To))]);
        var nav = GraphGeometry.Navigation(layout);
        Assert.Equal(["a:P-1"], nav["chg"].Right);
        Assert.Equal(["chg"], nav["a:P-1"].Left);
        Assert.Equal(2, nav["a:P-1"].Right.Count);
        var (top, bottom) = nav["p:P-2"].Down == "p:P-3" ? ("p:P-2", "p:P-3") : ("p:P-3", "p:P-2");
        Assert.Equal(bottom, nav[top].Down);
        Assert.Equal(top, nav[bottom].Up);
        Assert.Null(nav[top].Up);
        Assert.Equal(g.Nodes.Count, GraphGeometry.FocusOrder(layout).Count);
        Assert.Equal("chg", GraphGeometry.FocusOrder(layout)[0]);
    }

    // ---------------------------------------------------------------- treemap

    private static BomLine Line(string path, string? number, decimal qty, decimal ext, bool redacted = false, bool hasChildren = false, string state = "Released", Markings? m = null)
    {
        var parts = path.Split('.');
        return new BomLine(Guid.NewGuid(), null, parts.Length, path, int.Parse(parts[^1].Split('~')[0], System.Globalization.CultureInfo.InvariantCulture), qty, "EA", ext, redacted, false, false, hasChildren,
            redacted ? null : Guid.NewGuid(), redacted ? null : number, redacted ? null : $"Name {number}", "Component", redacted ? null : "A", redacted ? null : 1, redacted ? null : state, redacted ? null : state, [], null,
            redacted ? null : (m ?? Open).BannerText(), redacted ? null : m ?? Open);
    }

    [Fact]
    public void The_treemap_tree_merges_restricted_siblings_into_one_block_and_adds_up_assemblies()
    {
        var lines = new List<BomLine>
        {
            Line("10", "P-A", 1, 1, hasChildren: true), Line("10.10", "P-B", 2, 2), Line("10.20", "P-C", 3, 3), Line("10.30", null, 4, 4, redacted: true), Line("10.40", null, 5, 5, redacted: true),
            Line("20", "P-D", 1, 1), Line("30", null, 7, 7, redacted: true),
        };
        var root = BomTreemapModel.Build("P-TOP", "Top", lines);
        Assert.Equal(["10", "20", "~restricted"], root.Children.Select(c => c.Path));
        var assembly = root.Children[0];
        Assert.Equal(3, assembly.Children.Count);
        var block = assembly.Children.Single(c => c.Redacted);
        Assert.Equal(9, block.ExtendedQuantity); // 4 + 5, sizes the service already exposes
        Assert.Equal(BomTreemapModel.RestrictedLabel, block.Label);
        Assert.Null(block.Number);
        Assert.Null(block.Banner);
        Assert.Equal(14, assembly.Value(TreemapMetric.ExtendedQuantity)); // 2 + 3 + 9
        Assert.Equal(3, assembly.Value(TreemapMetric.DescendantCount)); // two parts and the restricted block, one each: the block never says how many lines it holds
        Assert.Equal(14 + 1 + 7, root.Value(TreemapMetric.ExtendedQuantity));
        Assert.Equal(3, assembly.Descendants); // B, C and the restricted block
        Assert.Same(root, BomTreemapModel.Find(root, ""));
        Assert.Equal("P-C", BomTreemapModel.Find(root, "10.20")!.Label);
        Assert.Null(BomTreemapModel.Find(root, "99"));
    }

    [Fact]
    public void The_treemap_layout_respects_the_tile_budget_and_counts_only_readable_parts_in_others()
    {
        var lines = new List<BomLine>();
        for (var i = 1; i <= 1000; i++)
            lines.Add(i % 50 == 0 ? Line($"{i}", null, 1, i, redacted: true) : Line($"{i}", $"P-{i:D4}", 1, i));
        var root = BomTreemapModel.Build("TOP", "Top", lines);
        var result = BomTreemapModel.Layout(root, 960, 540, TreemapMetric.ExtendedQuantity, 400);
        Assert.True(result.TileCount <= 400);
        Assert.True(result.Folded);
        var others = Assert.Single(result.Tiles, t => t.IsOthers);
        // Everything below the kept tiles is folded; only readable parts are counted, the hidden block (the 50-multiples merge into ONE block) is not.
        var keptReadable = result.Tiles.Count(t => t.Node is { Redacted: false });
        Assert.Equal(1000 - 20 - keptReadable, others.OthersCount);
        Assert.False(others.OthersHasRestricted); // the restricted block is large (sum of 50..1000) so it is kept
        Assert.Equal(960 * 540, result.Tiles.Where(t => t.Depth == 1).Sum(t => t.Width * t.Height), 4);
        Assert.All(result.Tiles, t => Assert.True(t.Width > 0 && t.Height > 0));
        Assert.Equal(result.Tiles.Select(t => t.Id), BomTreemapModel.Layout(root, 960, 540, TreemapMetric.ExtendedQuantity, 400).Tiles.Select(t => t.Id)); // deterministic
    }

    [Fact]
    public void Tiles_nest_one_level_inside_big_assemblies_and_each_state_and_marking_class_has_a_colour_and_a_pattern()
    {
        var lines = new List<BomLine> { Line("10", "P-A", 1, 1, hasChildren: true), Line("10.10", "P-B", 1, 10), Line("10.20", "P-C", 1, 10), Line("20", "P-D", 1, 20) };
        var root = BomTreemapModel.Build("TOP", "Top", lines);
        var result = BomTreemapModel.Layout(root, 960, 540, TreemapMetric.ExtendedQuantity);
        Assert.Equal(2, result.Tiles.Count(t => t.Depth == 2));
        Assert.All(result.Tiles.Where(t => t.Depth == 2), t => Assert.Equal("10", t.ParentId));
        var inner = result.Tiles.Where(t => t.Depth == 2).ToList();
        var outer = result.Tiles.Single(t => t.Id == "10");
        Assert.All(inner, t => Assert.True(t.X >= outer.X && t.Y >= outer.Y && t.X + t.Width <= outer.X + outer.Width + 1e-6 && t.Y + t.Height <= outer.Y + outer.Height + 1e-6));

        // Colour and pattern: both always present for hidden, released, in work, under review, obsolete, unresolved; by marking class too.
        var seenState = new HashSet<(string, string?)>();
        foreach (var state in new[] { "Released", "InWork", "UnderReview", "Obsolete", "Weird" })
            seenState.Add(Keel.Web.Components.Visuals.TreemapStyle.For(BomTreemapModel.Build("T", "t", [Line("10", "P", 1, 1, state: state)]).Children[0], TreemapColour.State));
        Assert.Equal(5, seenState.Select(s => s.Item1).Distinct().Count()); // five different fills...
        Assert.True(seenState.Count(s => s.Item2 is null) <= 1 && seenState.Select(s => s.Item2).Distinct().Count() >= 4); // ...and distinct patterns (only one plain)
        var byMarking = new[] { Open, Itar, Markings.Create(ExportJurisdiction.Ear, DistributionStatement.D, "ENG", eccn: "3A001", cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology").Value, Markings.Create(ExportJurisdiction.None, DistributionStatement.B, "ENG", cuiCategories: ["PROPIN"], controllingOffice: "x", distributionReason: "y").Value }
            .Select(m => Keel.Web.Components.Visuals.TreemapStyle.For(BomTreemapModel.Build("T", "t", [Line("10", "P", 1, 1, m: m)]).Children[0], TreemapColour.Markings)).ToList();
        Assert.Equal(4, byMarking.Select(s => s.Class).Distinct().Count());
        Assert.Equal(("restricted", "hatch"), Keel.Web.Components.Visuals.TreemapStyle.For(BomTreemapModel.Build("T", "t", [Line("10", null, 1, 1, redacted: true)]).Children[0], TreemapColour.State));
    }

    // ---------------------------------------------------------------- structure diff

    private static DeepDiffLine Deep(BomChangeKind kind, string path, string? number, string? from = null, string? to = null, decimal? fq = null, decimal? tq = null, bool redacted = false, params string[] changes) =>
        new(kind, redacted, path.Split('.').Length, path, number, number is null ? null : $"Name {number}", from, to, null, null, fq, tq, "EA", changes, null, null);

    [Fact]
    public void The_deep_diff_has_context_rows_for_unchanged_assemblies_text_for_every_change_and_counts()
    {
        var lines = new[]
        {
            Deep(BomChangeKind.Changed, "10", "P-A", "A.1", "B.1", 2, 3, changes: ["Revision A.1 to B.1", "Quantity 2 to 3"]),
            Deep(BomChangeKind.Added, "10.20", "P-N", null, "A.1", null, 1),
            Deep(BomChangeKind.Removed, "30.10", "P-R", "A.1", null, 4, null),
            Deep(BomChangeKind.Changed, "30.20", null, redacted: true, changes: ["Restricted item changed"]),
        };
        var model = StructureDiffModel.FromDeep(new DeepComparison("Compare", "BL-1", "BL-2", lines, 17, 1, 1, 2, "UNCLASSIFIED"));
        Assert.Equal(["10", "10.20", "30", "30.10", "30.20"], model.Rows.Select(r => r.Path));
        Assert.Equal([0, 1, 0, 1, 1], model.Rows.Select(r => r.Depth));
        var ctx = model.Rows.Single(r => r.Path == "30");
        Assert.Null(ctx.Kind);
        Assert.Equal("Unchanged assembly", ctx.Label);
        Assert.True(ctx.HasChildren);
        var changed = model.Rows[0];
        Assert.Equal("Qty 2 → 3; Rev A.1 → B.1", changed.Summary);
        Assert.Equal("Added", model.Rows[1].Summary);
        Assert.Equal("Removed", model.Rows[3].Summary);
        Assert.Equal("", model.Rows[1].Left);
        Assert.Equal("", model.Rows[3].Right);
        var restricted = model.Rows[4];
        Assert.True(restricted.Redacted);
        Assert.Equal("Restricted item", restricted.Label);
        Assert.Null(restricted.Href);
        Assert.Null(restricted.Sublabel);
        Assert.Equal((1, 1, 2, 17), (model.Added, model.Removed, model.Changed, model.Unchanged));
        Assert.Equal(4, model.ChangeCount);
    }

    [Fact]
    public void The_shallow_and_manufacturing_diffs_use_the_same_words()
    {
        var shallow = StructureDiffModel.FromShallow(new BomComparison("P-1", "A.1", "A.2",
        [
            new BomDiffLine(BomChangeKind.Changed, false, "P-2", "Screw", 10, 10, 2, 3, "EA", "EA", ["Quantity 2 to 3"], null, null),
            new BomDiffLine(BomChangeKind.Added, false, "P-3", "Nut", null, 20, null, 1, null, "EA", [], null, null),
            new BomDiffLine(BomChangeKind.Removed, true, null, null, 30, null, 1, null, "EA", null, [], null, null),
        ], 5, "UNCLASSIFIED"));
        Assert.Equal(["Qty 2 → 3", "Added", "Removed"], shallow.Rows.Select(r => r.Summary));
        Assert.Equal("Restricted item", shallow.Rows[2].Label);
        Assert.Null(shallow.Rows[2].Href);
        Assert.Equal(5, shallow.Unchanged);

        var mbom = StructureDiffModel.FromMbom(new MbomComparison("MB-1", "A.1", "A.2", [new MbomDiffEntry(BomChangeKind.Changed, false, "P-2", "Screw", 10, 10, ["Quantity 4 to 5", "Scrap 0 to 0.1"], null, null)], 0, "UNCLASSIFIED"));
        Assert.Equal("Qty 4 → 5; Scrap 0 → 0.1", mbom.Rows[0].Summary);

        var side = StructureDiffModel.FromSideBySide(new SideBySide("MB-1", "P-1 latest", "A.1",
        [
            new SideBySideRow([new SideLine(Guid.NewGuid(), 10, false, "P-2", "Screw", 4, "EA", null, null)], [new SideLine(Guid.NewGuid(), 10, false, "P-2", "Screw", 4, "EA", null, null)], "linked", "matched", null),
            new SideBySideRow([new SideLine(Guid.NewGuid(), 20, false, "P-3", "Nut", 1, "EA", null, null)], [], "ebom_only", null, null),
            new SideBySideRow([], [new SideLine(Guid.NewGuid(), 30, true, null, null, 1, "EA", null, null)], "mbom_only", null, null),
        ], "UNCLASSIFIED"));
        Assert.Equal([null, BomChangeKind.Removed, BomChangeKind.Added], side.Rows.Select(r => r.Kind));
        Assert.True(side.Rows[2].Redacted);
        Assert.Equal((1, 1, 0, 1), (side.Added, side.Removed, side.Changed, side.Unchanged));
    }

    // ---------------------------------------------------------------- workflow progress map

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private const string Review = """
        { "id": "t", "name": "T", "objectType": "part",
          "slots": [ { "name": "reviewer", "label": "R", "role": "plm-viewer", "min": 1, "max": 1 }, { "name": "approvers", "label": "A", "role": "plm-approver", "min": 2, "max": 10 } ],
          "start": "flow",
          "nodes": [
            { "id": "flow", "type": "Serial", "name": "Release review", "steps": ["tech", "vote", "end"] },
            { "id": "tech", "type": "Task", "name": "Technical review", "kind": "review", "assignment": { "mode": "Slot", "slot": "reviewer" } },
            { "id": "vote", "type": "Parallel", "name": "Approval", "rule": { "type": "NofM", "n": 2 }, "fanOut": { "slot": "approvers", "kind": "approval" } },
            { "id": "end", "type": "End", "name": "Release", "outcome": "Release" } ] }
        """;

    private static Participant P(string name) => new("sub-" + name, name, name.ToUpperInvariant() + " Example");

    [Fact]
    public void The_progress_map_shows_steps_in_order_with_the_vote_rule_and_a_status_per_task()
    {
        var template = TemplateParser.Parse(Review).Value;
        var people = new Dictionary<string, IReadOnlyList<Participant>> { ["reviewer"] = [P("rita")], ["approvers"] = [P("bob"), P("carol"), P("dave")] };
        var (state, _) = WorkflowEngine.Start(template, people, Now);

        var start = WorkflowMapBuilder.Build(template, state, new Dictionary<Guid, SignatureRow>(), people, "Active", Now, redacted: false);
        Assert.Equal(["tech", "vote", "end"], start.Select(s => s.NodeId));
        Assert.Equal(["Active", "Waiting", "Waiting"], start.Select(s => s.State));
        Assert.Equal("Pending", start[0].Tasks.Single().Status);
        Assert.Equal("2 of 3", start[1].Rule);
        Assert.Equal((2, 3), (start[1].Required, start[1].Total));
        Assert.Equal(["Waiting", "Waiting", "Waiting"], start[1].Tasks.Select(t => t.Status)); // the approvers are known before their step starts
        Assert.Equal(["BOB Example", "CAROL Example", "DAVE Example"], start[1].Tasks.Select(t => t.Person!.Name));
        Assert.Equal("RE", start[0].Tasks[0].Person!.Initials);

        var review = state.Tasks.Single(t => t.NodeId == "tech");
        WorkflowEngine.Complete(template, state, people, review.Id, true, null, Now.AddHours(1));
        WorkflowEngine.Complete(template, state, people, state.Tasks.First(t => t.AssigneeUsername == "bob").Id, true, null, Now.AddHours(2));
        var sig = new Dictionary<Guid, SignatureRow> { [state.Tasks.First(t => t.AssigneeUsername == "bob").Id] = Sig("Approved as to design") };
        var mid = WorkflowMapBuilder.Build(template, state, sig, people, "Active", Now.AddHours(3), redacted: false);
        Assert.Equal(["Approved", "Active", "Waiting"], mid.Select(s => s.State));
        Assert.Equal(new[] { "Pending", "Signed", "Pending" }.Order(), mid[1].Tasks.Select(t => t.Status).Order());
        Assert.Equal("Approved as to design", mid[1].Tasks.Single(t => t.Status == "Signed").Meaning);

        // Two approvals decide the vote: the third is skipped and the map ends at Release.
        WorkflowEngine.Complete(template, state, people, state.Tasks.First(t => t.AssigneeUsername == "carol").Id, true, null, Now.AddHours(4));
        var done = WorkflowMapBuilder.Build(template, state, sig, people, "Approved", Now.AddHours(5), redacted: false);
        Assert.Equal(["Approved", "Approved", "Approved"], done.Select(s => s.State));
        Assert.Equal("Skipped", done[1].Tasks.Single(t => t.Person!.Name.StartsWith("DAVE", StringComparison.Ordinal)).Status);
    }

    [Fact]
    public void A_reader_without_access_sees_participant_instead_of_names_and_no_meanings_or_times()
    {
        var template = TemplateParser.Parse(Review).Value;
        var people = new Dictionary<string, IReadOnlyList<Participant>> { ["reviewer"] = [P("rita")], ["approvers"] = [P("bob"), P("carol")] };
        var (state, _) = WorkflowEngine.Start(template, people, Now);
        WorkflowEngine.Complete(template, state, people, state.Tasks.Single(t => t.NodeId == "tech").Id, true, null, Now);
        var sig = new Dictionary<Guid, SignatureRow> { [state.Tasks.Single(t => t.NodeId == "tech").Id] = Sig("Technical review complete") };
        var stages = WorkflowMapBuilder.Build(template, state, sig, people, "Active", Now, redacted: true);
        var text = JsonSerializer.Serialize(stages);
        Assert.DoesNotContain("RITA", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BOB", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Technical review complete", text, StringComparison.Ordinal);
        Assert.All(stages.SelectMany(s => s.Tasks), t =>
        {
            Assert.Equal("Participant", t.Person!.Name);
            Assert.True(t.Person.Redacted);
            Assert.Null(t.At);
            Assert.Null(t.Meaning);
        });
        Assert.Equal("Signed", stages[0].Tasks.Single().Status); // the shape and the status survive
        Assert.Equal("Technical review", stages[0].Name);
    }

    private static SignatureRow Sig(string meaning) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "sub", "user", "User", null, "code", meaning, "approve", null, Now, null, "part", "P-1", "A", Guid.NewGuid(), "A.1", "", "{}", new byte[32], new byte[32]);
}
