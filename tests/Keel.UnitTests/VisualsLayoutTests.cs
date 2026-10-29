using System.Diagnostics;
using Keel.Core;
using Keel.Web.Services.Visuals;

namespace Keel.UnitTests;

/// <summary>The pure layout algorithms of the relationship pictures (UX-3, ADR-0029): determinism, no overlaps, exact tiling, packing and the 300 ms budget.</summary>
public sealed class VisualsLayoutTests
{
    private static (List<LayoutNodeInput> Nodes, List<LayoutEdgeInput> Edges) Synthetic(int count, int layers, int seed)
    {
        var rnd = new Random(seed);
        var nodes = new List<LayoutNodeInput> { new("n0", 0, 196, 64) };
        for (var i = 1; i < count; i++) nodes.Add(new LayoutNodeInput($"n{i}", 1 + (i % layers), 176, 56));
        var edges = new List<LayoutEdgeInput>();
        foreach (var n in nodes.Skip(1))
        {
            var candidates = nodes.Where(c => c.Layer == n.Layer - 1).ToList();
            edges.Add(new LayoutEdgeInput(candidates[rnd.Next(candidates.Count)].Id, n.Id));
            if (rnd.Next(4) == 0) edges.Add(new LayoutEdgeInput(candidates[rnd.Next(candidates.Count)].Id, n.Id));
        }
        return (nodes, edges);
    }

    private static bool Overlaps(PlacedNode a, PlacedNode b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    [Fact]
    public void Layered_layout_is_deterministic()
    {
        var (nodes, edges) = Synthetic(120, 5, 1);
        var first = LayeredLayout.Compute(nodes, edges);
        var second = LayeredLayout.Compute([.. nodes], [.. edges]);
        Assert.Equal(first.Nodes, second.Nodes);
        Assert.Equal(first.Edges.Select(e => e.Path), second.Edges.Select(e => e.Path));
        Assert.Equal(first.Crossings, second.Crossings);
    }

    [Fact]
    public void Layered_layout_never_overlaps_nodes_and_edges_connect_the_sides_of_their_nodes()
    {
        var (nodes, edges) = Synthetic(300, 6, 2);
        var layout = LayeredLayout.Compute(nodes, edges);
        for (var i = 0; i < layout.Nodes.Count; i++)
            for (var j = i + 1; j < layout.Nodes.Count; j++)
                Assert.False(Overlaps(layout.Nodes[i], layout.Nodes[j]), $"{layout.Nodes[i].Id} overlaps {layout.Nodes[j].Id}");
        var byId = layout.Nodes.ToDictionary(n => n.Id);
        Assert.Equal(edges.Distinct().Count(), layout.Edges.Count); // a repeated edge is drawn once
        foreach (var e in layout.Edges)
        {
            var (from, to) = (byId[e.From], byId[e.To]);
            Assert.Equal(from.Right, e.X1, 6);
            Assert.Equal(to.X, e.X2, 6);
            Assert.InRange(e.Y1, from.Y, from.Bottom);
            Assert.InRange(e.Y2, to.Y, to.Bottom);
        }
        Assert.All(layout.Nodes, n => { Assert.InRange(n.X, 0, layout.Width); Assert.InRange(n.Bottom, 0, layout.Height); });
        // Columns are in layer order from left to right.
        Assert.Equal(layout.Nodes.OrderBy(n => n.Layer).Select(n => n.Layer), layout.Nodes.OrderBy(n => n.X).ThenBy(n => n.Y).Select(n => n.Layer));
    }

    [Fact]
    public void Ordering_inside_a_column_removes_avoidable_crossings()
    {
        // Two parents and two children wired "crossed" in input order: the layout swaps a column to get zero crossings.
        var nodes = new List<LayoutNodeInput> { new("a", 0, 100, 40), new("b", 0, 100, 40), new("c", 1, 100, 40), new("d", 1, 100, 40) };
        var edges = new List<LayoutEdgeInput> { new("a", "d"), new("b", "c") };
        Assert.Equal(0, LayeredLayout.Compute(nodes, edges).Crossings);
        // A genuinely crossing structure (complete bipartite 3 x 3) keeps its unavoidable crossings, and the count is reported.
        var k33n = new List<LayoutNodeInput>();
        var k33e = new List<LayoutEdgeInput>();
        for (var i = 0; i < 3; i++) { k33n.Add(new($"l{i}", 0, 80, 30)); k33n.Add(new($"r{i}", 1, 80, 30)); }
        for (var i = 0; i < 3; i++) for (var j = 0; j < 3; j++) k33e.Add(new($"l{i}", $"r{j}"));
        Assert.Equal(9, LayeredLayout.Compute(k33n, k33e).Crossings);
    }

    [Fact]
    public void Layout_ignores_unknown_edges_rejects_duplicate_ids_and_handles_an_empty_graph()
    {
        Assert.Empty(LayeredLayout.Compute([], []).Nodes);
        var one = LayeredLayout.Compute([new("a", 0, 10, 10)], [new("a", "ghost"), new("a", "a")]);
        Assert.Single(one.Nodes);
        Assert.Empty(one.Edges);
        Assert.Throws<ArgumentException>(() => LayeredLayout.Compute([new("a", 0, 1, 1), new("a", 1, 1, 1)], []));
    }

    [Fact]
    public void Five_hundred_nodes_lay_out_within_the_300_ms_budget()
    {
        var (nodes, edges) = Synthetic(500, 7, 3);
        LayeredLayout.Compute(nodes, edges); // warm-up (JIT)
        var sw = Stopwatch.StartNew();
        var layout = LayeredLayout.Compute(nodes, edges);
        sw.Stop();
        TestContext.Current.SendDiagnosticMessage($"Layered layout of {layout.Nodes.Count} nodes and {layout.Edges.Count} edges: {sw.ElapsedMilliseconds} ms, {layout.Crossings} crossings left");
        Assert.True(sw.ElapsedMilliseconds < 300, $"layout took {sw.ElapsedMilliseconds} ms");
    }

    // ---------------------------------------------------------------- treemap

    [Fact]
    public void Treemap_tiles_have_areas_proportional_to_their_values_and_tile_the_rectangle_exactly()
    {
        var rnd = new Random(5);
        var items = Enumerable.Range(0, 60).Select(i => new TreemapItem($"i{i}", 1 + rnd.NextDouble() * 99)).ToList();
        var (x, y, w, h) = (10.0, 20.0, 960.0, 540.0);
        var tiles = SquarifiedTreemap.Layout(items, x, y, w, h);
        Assert.Equal(items.Count, tiles.Count);
        var total = items.Sum(i => i.Value);
        Assert.Equal(w * h, tiles.Sum(t => t.Area), 6);
        foreach (var t in tiles)
        {
            Assert.Equal(t.Value / total * w * h, t.Area, 6);
            Assert.True(t.X >= x - 1e-6 && t.Y >= y - 1e-6 && t.X + t.Width <= x + w + 1e-6 && t.Y + t.Height <= y + h + 1e-6);
        }
        for (var i = 0; i < tiles.Count; i++)
            for (var j = i + 1; j < tiles.Count; j++)
            {
                var (a, b) = (tiles[i], tiles[j]);
                var overlapW = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
                var overlapH = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
                Assert.False(overlapW > 1e-6 && overlapH > 1e-6, $"{a.Id} overlaps {b.Id}");
            }
        Assert.Equal(tiles.Select(t => (t.Id, t.X, t.Y, t.Width, t.Height)), SquarifiedTreemap.Layout([.. items], x, y, w, h).Select(t => (t.Id, t.X, t.Y, t.Width, t.Height)));
    }

    [Fact]
    public void Treemap_drops_empty_values_and_keeps_the_aspect_ratio_reasonable()
    {
        var tiles = SquarifiedTreemap.Layout([new("a", 6), new("b", 6), new("c", 4), new("d", 3), new("e", 2), new("f", 2), new("g", 1), new("zero", 0), new("neg", -2)], 0, 0, 600, 400);
        Assert.Equal(7, tiles.Count);
        Assert.DoesNotContain(tiles, t => t.Id is "zero" or "neg");
        Assert.All(tiles, t => Assert.True(Math.Max(t.Width / t.Height, t.Height / t.Width) < 4.5, $"{t.Id} is {t.Width:F0} x {t.Height:F0}"));
        Assert.Empty(SquarifiedTreemap.Layout([], 0, 0, 100, 100));
        Assert.Empty(SquarifiedTreemap.Layout([new("a", 1)], 0, 0, 0, 100));
    }

    [Fact]
    public void Limiting_keeps_the_largest_items_and_folds_the_rest_into_one_others_item_with_the_right_sum()
    {
        var items = Enumerable.Range(1, 1000).Select(i => new TreemapItem($"i{i:D4}", i)).ToList();
        var limited = SquarifiedTreemap.Limit(items, 400);
        Assert.Equal(399, limited.Items.Count);
        Assert.NotNull(limited.Others);
        Assert.Equal(601, limited.OthersCount);
        Assert.Equal(items.Sum(i => i.Value), limited.Items.Sum(i => i.Value) + limited.Others!.Value, 6);
        Assert.Equal(1000, limited.Items[0].Value);
        Assert.Equal(602, limited.Items.Min(i => i.Value));
        Assert.Equal(601.0 * 602 / 2, limited.Others.Value, 6); // the sum of 1..601
        var small = SquarifiedTreemap.Limit(items.Take(10).ToList(), 400);
        Assert.Null(small.Others);
        Assert.Equal(10, small.Items.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => SquarifiedTreemap.Limit(items, 1));
        // With the others item laid out, the tiles still cover the rectangle.
        var tiles = SquarifiedTreemap.Layout([.. limited.Items, limited.Others], 0, 0, 960, 540);
        Assert.Equal(400, tiles.Count);
        Assert.Equal(960 * 540, tiles.Sum(t => t.Area), 6);
    }

    // ---------------------------------------------------------------- lane packing and the time axis

    [Fact]
    public void Lane_packing_never_overlaps_inside_a_lane_uses_the_minimum_number_of_lanes_and_is_deterministic()
    {
        var rnd = new Random(9);
        var items = Enumerable.Range(0, 200).Select(i => { var s = rnd.NextDouble() * 2000; return new LaneItem($"e{i}", s, s + 40 + rnd.NextDouble() * 160); }).ToList();
        var packed = LanePacking.Assign(items, 8);
        foreach (var lane in packed.GroupBy(p => p.Lane))
        {
            var ordered = lane.OrderBy(p => p.Start).ToList();
            for (var i = 1; i < ordered.Count; i++) Assert.True(ordered[i].Start >= ordered[i - 1].End + 8, $"lane {lane.Key} overlaps");
        }
        // The number of lanes equals the deepest overlap (counting the gap), so it is minimal.
        var depth = items.Max(a => items.Count(b => b.Start <= a.Start && b.End + 8 > a.Start));
        Assert.Equal(depth, packed.Max(p => p.Lane) + 1);
        Assert.Equal(packed, LanePacking.Assign([.. items], 8));
        Assert.Empty(LanePacking.Assign([]));
    }

    private static TimelineEvent Event(int day, string kind = TimelineKinds.Iteration, string? revision = "A", int rank = 10, long order = 0, string title = "Checked in") =>
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(day), kind, title, null, "alice", revision, null, false, rank, order);

    [Fact]
    public void The_time_axis_packs_labels_without_overlap_in_both_scales()
    {
        var events = Enumerable.Range(0, 40).Select(i => Event(i * 3 + (i % 5), order: i, revision: i < 20 ? "A" : "B")).ToList();
        foreach (var byTime in new[] { true, false })
        {
            var axis = TimelineAxis.Build(events, byTime);
            Assert.Equal(events.Count, axis.Items.Count);
            foreach (var lane in axis.Items.GroupBy(i => i.Lane))
            {
                var ordered = lane.OrderBy(i => i.X).ToList();
                for (var i = 1; i < ordered.Count; i++) Assert.True(ordered[i].X - 8 >= ordered[i - 1].X - 8 + ordered[i - 1].Width, $"labels overlap in lane {lane.Key} (byTime {byTime})");
            }
            Assert.All(axis.Items, i => Assert.True(i.X + i.Width <= axis.Width));
            Assert.NotEmpty(axis.Ticks);
        }
        Assert.Same(TimelineAxis.Empty, TimelineAxis.Build([], true));
    }

    [Fact]
    public void Merged_timelines_are_ordered_by_time_then_source_rank_then_the_sources_own_order_and_stay_stable()
    {
        var same = Event(5, rank: 30, order: 1, title: "signed");
        var events = new[]
        {
            Event(9, order: 4, title: "late"), same, Event(5, rank: 10, order: 7, title: "part second"), Event(5, rank: 10, order: 2, title: "part first"),
            Event(1, rank: 50, order: 0, title: "early"), Event(5, rank: 20, order: 0, title: "document"),
        };
        var merged = Keel.Web.Services.Visuals.TimelineService.Merge(events);
        Assert.Equal(["early", "part first", "part second", "document", "signed", "late"], merged.Select(e => e.Title));
        // Any input order gives the same result.
        var shuffled = Keel.Web.Services.Visuals.TimelineService.Merge(events.Reverse());
        Assert.Equal(merged.Select(e => e.Title), shuffled.Select(e => e.Title));
        Assert.Equal(merged, Keel.Web.Services.Visuals.TimelineService.Merge(merged));
    }
}
