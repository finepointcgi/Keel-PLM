using Keel.Programs;

namespace Keel.UnitTests;

/// <summary>The pure schedule rules of the program module (8.8, ADR-0043): critical path, roll-up, cycles, slip, readiness.</summary>
public sealed class ProgramRulesTests
{
    private static readonly DateOnly D0 = new(2027, 1, 4);
    private static DateOnly Day(int n) => D0.AddDays(n);
    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static ScheduleItem Task(int n, int start, int end, string status = ItemStatuses.NotStarted, int percent = 0, DateOnly? aStart = null, DateOnly? aEnd = null, Guid? parent = null) =>
        new(Id(n), $"TK-{n:D3}", ProgramKinds.Task, parent, Day(start), Day(end), aStart, aEnd, status, percent);

    private static ScheduleItem Milestone(int n, int day, string status = ItemStatuses.NotStarted) => new(Id(n), $"MS-{n:D3}", ProgramKinds.Milestone, null, Day(day), Day(day), null, null, status, 0);

    private static ScheduleLink Link(int a, int b, int lag = 0) => new(Id(a), Id(b), lag);

    // A (0-10) -> B (10-20) -> D milestone(20); A -> C (10-15) -> D. B is longer than C: B is critical, C has 5 days of float.
    private static (ScheduleItem[] Items, ScheduleLink[] Links) Diamond() =>
        ([Task(1, 0, 10), Task(2, 10, 20), Task(3, 10, 15), Milestone(4, 20)], [Link(1, 2), Link(1, 3), Link(2, 4), Link(3, 4)]);

    [Fact]
    public void The_critical_path_is_the_chain_with_no_float_and_the_shorter_branch_has_float()
    {
        var (items, links) = Diamond();
        var r = ScheduleMath.Compute(items, links, Day(-30));
        Assert.False(r.Cyclic);
        Assert.Equal(Day(20), r.Finish);
        Assert.Equal([Id(1), Id(2), Id(4)], r.CriticalPath);
        Assert.Equal(5, r.Forecast[Id(3)].FloatDays);
        Assert.False(r.Forecast[Id(3)].Critical);
        Assert.Equal(0, r.Forecast[Id(2)].FloatDays);
        Assert.Equal(Day(15), r.Forecast[Id(3)].LateFinish.AddDays(-5)); // late finish 20, early finish 15
    }

    [Fact]
    public void The_result_does_not_depend_on_the_order_the_items_are_given_in()
    {
        var (items, links) = Diamond();
        var a = ScheduleMath.Compute(items, links, Day(0));
        var b = ScheduleMath.Compute([.. items.Reverse()], [.. links.Reverse()], Day(0));
        Assert.Equal(a.CriticalPath, b.CriticalPath);
        Assert.Equal(a.Forecast.OrderBy(x => x.Key).Select(x => x.Value), b.Forecast.OrderBy(x => x.Key).Select(x => x.Value));
    }

    [Fact]
    public void A_task_that_finishes_late_pushes_its_successors_and_shows_slip()
    {
        var (items, links) = Diamond();
        // Task 1 should have finished on day 10 but is still running on day 14: successors start no earlier than the status date.
        items[0] = Task(1, 0, 10, ItemStatuses.InProgress, 60, aStart: Day(0));
        var r = ScheduleMath.Compute(items, links, Day(14));
        Assert.Equal(Day(14), r.Forecast[Id(1)].EarlyFinish);
        Assert.Equal(4, r.Forecast[Id(1)].SlipDays);
        Assert.Equal(Day(24), r.Forecast[Id(4)].EarlyFinish);
        Assert.Equal(4, r.Forecast[Id(4)].SlipDays);
        Assert.Equal(Day(24), r.Finish);
    }

    [Fact]
    public void A_completed_item_is_fixed_at_its_actual_dates_and_is_never_critical()
    {
        var items = new[] { Task(1, 0, 10, ItemStatuses.Complete, 100, Day(0), Day(12)), Task(2, 10, 20) };
        var r = ScheduleMath.Compute(items, [Link(1, 2)], Day(-5));
        Assert.Equal(Day(12), r.Forecast[Id(1)].EarlyFinish);
        Assert.False(r.Forecast[Id(1)].Critical);
        Assert.Equal(Day(12), r.Forecast[Id(2)].EarlyStart); // follows the actual finish
        Assert.Equal(Day(22), r.Forecast[Id(2)].EarlyFinish);
        Assert.Equal(2, r.Forecast[Id(1)].SlipDays);
    }

    [Fact]
    public void Lag_delays_the_successor_and_a_negative_lag_overlaps_it()
    {
        var items = new[] { Task(1, 0, 10), Task(2, 0, 5) };
        Assert.Equal(Day(13), ScheduleMath.Compute(items, [Link(1, 2, 3)], Day(-1)).Forecast[Id(2)].EarlyStart);
        Assert.Equal(Day(8), ScheduleMath.Compute(items, [Link(1, 2, -2)], Day(-1)).Forecast[Id(2)].EarlyStart);
    }

    [Fact]
    public void Phases_and_cancelled_items_take_no_part_and_links_to_them_are_ignored()
    {
        var phase = new ScheduleItem(Id(9), "PH-001", ProgramKinds.Phase, null, Day(0), Day(50), null, null, ItemStatuses.NotStarted, 0);
        var cancelled = Task(2, 10, 20, ItemStatuses.Cancelled);
        var r = ScheduleMath.Compute([Task(1, 0, 10), cancelled, phase], [Link(1, 2), new ScheduleLink(Id(9), Id(1), 0)], Day(-1));
        Assert.Equal([Id(1)], r.Forecast.Keys);
        Assert.Equal(Day(10), r.Finish);
    }

    [Fact]
    public void A_circular_dependency_is_reported_and_no_dates_are_invented()
    {
        var r = ScheduleMath.Compute([Task(1, 0, 5), Task(2, 5, 9)], [Link(1, 2), Link(2, 1)], Day(0));
        Assert.True(r.Cyclic);
        Assert.Empty(r.CriticalPath);
        Assert.Null(r.Finish);
        Assert.True(ScheduleMath.WouldCreateCycle([Link(1, 2), Link(2, 3)], Id(3), Id(1)));
        Assert.True(ScheduleMath.WouldCreateCycle([], Id(1), Id(1)));
        Assert.False(ScheduleMath.WouldCreateCycle([Link(1, 2), Link(2, 3)], Id(1), Id(3)));
    }

    [Fact]
    public void An_empty_schedule_has_no_finish_and_is_not_cyclic()
    {
        var r = ScheduleMath.Compute([], [], Day(0));
        Assert.Null(r.Finish);
        Assert.False(r.Cyclic);
    }

    [Fact]
    public void Percent_complete_rolls_up_by_duration_through_phases_and_ignores_cancelled_items()
    {
        var phase = new ScheduleItem(Id(1), "PH-001", ProgramKinds.Phase, null, Day(0), Day(40), null, null, ItemStatuses.InProgress, 0);
        var big = Task(2, 0, 30, ItemStatuses.InProgress, 50, parent: Id(1));   // weight 30, 50 percent
        var small = Task(3, 30, 40, ItemStatuses.Complete, 100, Day(30), Day(40), parent: Id(1)); // weight 10, 100 percent
        var dropped = Task(4, 0, 100, ItemStatuses.Cancelled, 0, parent: Id(1));
        var (by, overall) = ScheduleMath.Rollup([phase, big, small, dropped]);
        Assert.Equal(63, by[Id(1)]); // (30*50 + 10*100) / 40 = 62.5, rounded half up
        Assert.Equal(63, overall);
        Assert.Equal(50, by[Id(2)]);
        Assert.Null(ScheduleMath.Rollup([new ScheduleItem(Id(5), "PH-002", ProgramKinds.Phase, null, Day(0), Day(1), null, null, ItemStatuses.NotStarted, 0)]).Overall);
    }

    [Fact]
    public void Slip_is_the_distance_to_the_baseline_end_and_overdue_means_open_and_past()
    {
        Assert.Equal(7, ScheduleMath.BaselineSlip(Day(10), Day(17)));
        Assert.Equal(-3, ScheduleMath.BaselineSlip(Day(10), Day(7)));
        Assert.True(ScheduleMath.IsOverdue(Task(1, 0, 5), Day(6)));
        Assert.False(ScheduleMath.IsOverdue(Task(1, 0, 5), Day(5)));
        Assert.False(ScheduleMath.IsOverdue(Task(1, 0, 5, ItemStatuses.Complete, 100, Day(0), Day(4)), Day(60)));
        Assert.False(ScheduleMath.IsOverdue(Task(1, 0, 5, ItemStatuses.Cancelled), Day(60)));
    }

    [Theory]
    [InlineData("Released", Readiness.Ready)]
    [InlineData("Established", Readiness.Ready)]
    [InlineData("  approved ", Readiness.Ready)]
    [InlineData("Under review", Readiness.InReview)]
    [InlineData("Under Review", Readiness.InReview)]
    [InlineData("In Work", Readiness.InWork)]
    [InlineData("Draft", Readiness.InWork)]
    [InlineData("Obsolete", Readiness.Ended)]
    [InlineData("Expired", Readiness.Ended)]
    [InlineData("something custom", Readiness.InWork)]
    public void A_deliverable_is_ready_when_the_lifecycle_state_of_its_object_says_so(string label, string expected) => Assert.Equal(expected, Readiness.Of(label));

    [Fact]
    public void The_gate_types_and_statuses_have_readable_labels()
    {
        Assert.All(GateTypes.All, t => Assert.False(string.IsNullOrWhiteSpace(GateTypes.Label(t))));
        Assert.Equal("Preliminary design review", GateTypes.Label("PDR"));
        Assert.Equal("On hold", ProgramStatuses.Label("OnHold"));
        Assert.Equal("In progress", ItemStatuses.Label(ItemStatuses.InProgress));
        Assert.Equal("Under review", GateStates.Label(GateStates.UnderReview));
    }

    [Fact]
    public void Options_are_validated()
    {
        Assert.Empty(new ProgramOptions().Validate());
        Assert.NotEmpty(new ProgramOptions { DueWarningDays = 0 }.Validate());
        Assert.NotEmpty(new ProgramOptions { MaintenanceInterval = TimeSpan.FromSeconds(1) }.Validate());
    }
}
