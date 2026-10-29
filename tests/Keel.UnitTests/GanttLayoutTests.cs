using Keel.Core;
using Keel.Programs;
using Keel.Web.Components.Visuals;

namespace Keel.UnitTests;

/// <summary>The geometry of the program timeline (8.8): restricted items reveal nothing but a time span, the picture is deterministic.</summary>
public sealed class GanttLayoutTests
{
    private static readonly Markings Open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG", program: "ORCA").Value;
    private static readonly DateOnly D0 = new(2027, 2, 1);

    private static ItemRow Row(string key, string kind, int start, int end, int sort, bool critical = false, string? gate = null) =>
        new(Guid.NewGuid(), key, kind, null, sort, "Title of " + key, "", null, null, D0.AddDays(start), D0.AddDays(end), null, null, "NotStarted", 0, gate is null ? null : "PDR", gate, gate is null ? null : "Draft",
            D0.AddDays(start), D0.AddDays(end), 0, critical, 0, null, null, false, true, Open, false);

    private static ScheduleView View(IReadOnlyList<ItemRow> items, IReadOnlyList<RestrictedItemRow>? hidden = null, IReadOnlyList<DependencyRow>? deps = null) =>
        new("ORCA", D0, items, hidden ?? [], deps ?? [], [], D0.AddDays(60), 0, [], null, false, Open.BannerText());

    [Fact]
    public void A_restricted_item_keeps_its_row_and_dates_and_has_no_name_key_link_or_arrows()
    {
        var layout = GanttLayout.Build(View([Row("TK-001", ProgramKinds.Task, 0, 10, 10), Row("TK-003", ProgramKinds.Task, 20, 30, 30)], [new RestrictedItemRow(20, D0.AddDays(10), D0.AddDays(20))]));
        Assert.Equal(3, layout.Bars.Count);
        var hidden = layout.Bars[1];
        Assert.True(hidden.Redacted);
        Assert.Null(hidden.Key);
        Assert.Null(hidden.Href);
        Assert.Equal("Restricted item", hidden.Label);
        Assert.DoesNotContain("Title", hidden.Aria, StringComparison.Ordinal);
        Assert.StartsWith("Restricted item, ", hidden.Aria, StringComparison.Ordinal);
        Assert.Null(hidden.Left);
        Assert.Null(hidden.Right);
    }

    [Fact]
    public void Rows_follow_the_outline_order_and_keyboard_neighbours_chain_up_and_down()
    {
        var layout = GanttLayout.Build(View([Row("TK-002", ProgramKinds.Task, 5, 9, 20), Row("TK-001", ProgramKinds.Task, 0, 4, 10)]));
        Assert.Equal(["TK-001", "TK-002"], layout.Bars.Select(b => b.Key));
        Assert.Null(layout.Bars[0].Up);
        Assert.Equal(layout.Bars[1].Id, layout.Bars[0].Down);
        Assert.Null(layout.Bars[1].Down);
    }

    [Fact]
    public void A_dependency_draws_an_arrow_from_the_end_of_the_predecessor_to_the_start_of_the_successor_and_links_the_keyboard_path()
    {
        var layout = GanttLayout.Build(View([Row("TK-001", ProgramKinds.Task, 0, 9, 10), Row("TK-002", ProgramKinds.Task, 10, 19, 20)], deps: [new DependencyRow("TK-001", "TK-002", 0)]));
        var arrow = Assert.Single(layout.Arrows);
        Assert.Equal(layout.Bars[0].X + layout.Bars[0].Width, arrow.X1, 3);
        Assert.Equal(layout.Bars[1].X, arrow.X2, 3);
        Assert.Equal(layout.Bars[1].Id, layout.Bars[0].Right);
        Assert.Equal(layout.Bars[0].Id, layout.Bars[1].Left);
    }

    [Fact]
    public void A_gate_milestone_links_to_its_gate_page_and_names_the_critical_path_in_words()
    {
        var layout = GanttLayout.Build(View([Row("MS-001", ProgramKinds.Milestone, 5, 5, 10, critical: true, gate: "GR-000007")]));
        var bar = layout.Bars[0];
        Assert.True(bar.Gate);
        Assert.Equal("programs/gates/GR-000007", bar.Href);
        Assert.Contains("on the critical path", bar.Aria, StringComparison.Ordinal);
        Assert.Contains("PDR gate review", bar.Aria, StringComparison.Ordinal);
    }

    [Fact]
    public void The_layout_is_deterministic_and_an_empty_schedule_is_empty()
    {
        var v = View([Row("TK-001", ProgramKinds.Task, 0, 9, 10), Row("MS-002", ProgramKinds.Milestone, 12, 12, 20)]);
        Assert.Equal(GanttLayout.Build(v).Bars, GanttLayout.Build(v).Bars);
        Assert.Same(GanttLayout.Empty, GanttLayout.Build(View([])));
    }
}
