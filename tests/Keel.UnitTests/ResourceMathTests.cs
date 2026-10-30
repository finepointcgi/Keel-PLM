using Keel.Programs;

namespace Keel.UnitTests;

/// <summary>Resource loading and cost arithmetic (9.5, ADR-0049): working days, rates over time, over-allocation, load by week, budget against actual against estimate to complete.</summary>
public sealed class ResourceMathTests
{
    // 2027-03-01 is a Monday.
    private static readonly DateOnly Mon = new(2027, 3, 1);
    private static DateOnly D(int days) => Mon.AddDays(days);

    private static readonly Guid Role = Guid.NewGuid();
    private static MathResource Res(string key, decimal capacity = 8, DateOnly? from = null, DateOnly? to = null) => new(Guid.NewGuid(), key, "Name " + key, Role, capacity, from, to);
    private static MathAssignment Asg(string key, MathResource r, int start, int end, decimal hours, Guid? item = null) => new(Guid.NewGuid(), key, r.Id, item ?? Guid.NewGuid(), D(start), D(end), hours);

    [Fact]
    public void Working_days_skip_weekends_and_a_week_starts_on_monday()
    {
        Assert.Equal(5, ResourceMath.WorkingDays(D(0), D(6)).Count());
        Assert.Equal(10, ResourceMath.WorkingDays(D(0), D(13)).Count());
        Assert.Empty(ResourceMath.WorkingDays(D(5), D(6)));
        Assert.Equal(Mon, ResourceMath.WeekOf(D(6)));
        Assert.Equal(Mon, ResourceMath.WeekOf(D(0)));
        Assert.Equal(new DateOnly(2027, 3, 1), ResourceMath.MonthOf(D(20)));
    }

    [Fact]
    public void The_rate_in_force_is_the_latest_step_on_or_before_the_day_and_none_before_the_first()
    {
        RateStep[] steps = [new(D(10), 120m), new(D(0), 100m), new(D(20), 140m)];
        Assert.Null(ResourceMath.RateAt(steps, D(-1)));
        Assert.Equal(100m, ResourceMath.RateAt(steps, D(0)));
        Assert.Equal(100m, ResourceMath.RateAt(steps, D(9)));
        Assert.Equal(120m, ResourceMath.RateAt(steps, D(10)));
        Assert.Equal(140m, ResourceMath.RateAt(steps, D(400)));
        Assert.Null(ResourceMath.RateAt([], D(0)));
    }

    [Fact]
    public void Two_assignments_on_one_resource_over_capacity_make_one_period_with_peak_and_excess()
    {
        var r = Res("RS-001");
        var over = ResourceMath.OverAllocations([r], [Asg("AS-001", r, 0, 4, 6), Asg("AS-002", r, 2, 8, 4)]);
        // Wed to Fri carry 10 hours (2 above), Mon and Tue 6, the weekend nothing; the run is Wed to Fri and ends before the weekend gap only if Monday is not over.
        var p = Assert.Single(over);
        Assert.Equal(D(2), p.From);
        Assert.Equal(D(4), p.To);
        Assert.Equal(10m, p.PeakHours);
        Assert.Equal(6m, p.ExcessHours);
        Assert.Equal(["AS-001", "AS-002"], p.AssignmentKeys);
    }

    [Fact]
    public void A_run_continues_over_a_weekend_and_a_quiet_working_day_splits_it()
    {
        var r = Res("RS-001");
        var continuous = ResourceMath.OverAllocations([r], [Asg("AS-001", r, 0, 11, 9)]);
        var one = Assert.Single(continuous);
        Assert.Equal((D(0), D(11)), (one.From, one.To));
        // 9 hours Monday and Wednesday only: Tuesday is within capacity, so two periods.
        var split = ResourceMath.OverAllocations([r], [Asg("AS-001", r, 0, 0, 9), Asg("AS-002", r, 2, 2, 9)]);
        Assert.Equal(2, split.Count);
    }

    [Fact]
    public void Work_outside_the_availability_window_is_over_allocation()
    {
        var r = Res("RS-001", 8, D(7), D(13));
        var over = ResourceMath.OverAllocations([r], [Asg("AS-001", r, 0, 11, 4)]);
        var p = Assert.Single(over);
        Assert.Equal(D(0), p.From);
        Assert.Equal(D(4), p.To);
        Assert.Equal(20m, p.ExcessHours);
    }

    [Fact]
    public void Exactly_full_capacity_is_not_over_allocation_and_each_resource_is_judged_alone()
    {
        var a = Res("RS-001");
        var b = Res("RS-002");
        Assert.Empty(ResourceMath.OverAllocations([a, b], [Asg("AS-001", a, 0, 4, 8), Asg("AS-002", b, 0, 4, 8)]));
    }

    [Fact]
    public void Weekly_cells_hold_allocated_and_available_hours_and_are_stable_for_any_input_order()
    {
        var r = Res("RS-001");
        var asg = new[] { Asg("AS-001", r, 2, 8, 4), Asg("AS-002", r, 0, 1, 2) };
        var cells = ResourceMath.Weekly([r], asg, D(0), D(13));
        Assert.Equal(new WeekCell(2 * 2 + 3 * 4 + 0, 40), cells[(r.Id, D(0))]);
        Assert.Equal(new WeekCell(2 * 4, 40), cells[(r.Id, D(7))]);
        var reversed = ResourceMath.Weekly([r], [.. asg.Reverse()], D(0), D(13));
        Assert.Equal(cells[(r.Id, D(0))], reversed[(r.Id, D(0))]);
    }

    private static CostMath Run(DateOnly asOf, MathResource r, IReadOnlyList<MathAssignment> asg, IReadOnlyList<RateStep> steps, Dictionary<Guid, MathItem>? items = null,
        IReadOnlyList<MathBudget>? budget = null, IReadOnlyList<MathActual>? actuals = null, IReadOnlyList<MathMaterial>? material = null) =>
        ResourceMath.Compute(asOf, [r], asg, items ?? [], new Dictionary<Guid, IReadOnlyList<RateStep>> { [Role] = steps }, new Dictionary<Guid, Guid> { [r.Id] = Role }, budget ?? [], actuals ?? [], material ?? []);

    [Fact]
    public void Labor_planned_cost_is_hours_times_the_rate_in_force_on_each_day_and_the_month_splits_it()
    {
        var r = Res("RS-001");
        // Mon 2027-03-29 to Fri 2027-04-02: three days in March at 100, then a raise to 200 on 2027-04-01 (two days in April).
        var a = new MathAssignment(Guid.NewGuid(), "AS-001", r.Id, Guid.NewGuid(), new DateOnly(2027, 3, 29), new DateOnly(2027, 4, 2), 8);
        var m = Run(new DateOnly(2027, 1, 1), r, [a], [new(new DateOnly(2027, 1, 1), 100m), new(new DateOnly(2027, 4, 1), 200m)]);
        Assert.Equal(3 * 8 * 100m, m.Months.Single(x => x.Period == new DateOnly(2027, 3, 1) && x.CostType == CostTypes.Labor).Planned);
        Assert.Equal(2 * 8 * 200m, m.Months.Single(x => x.Period == new DateOnly(2027, 4, 1) && x.CostType == CostTypes.Labor).Planned);
        Assert.Equal(5600m, m.ByType[CostTypes.Labor].Planned);
        Assert.Equal(0m, m.UnpricedHours);
    }

    [Fact]
    public void Hours_without_a_rate_are_counted_as_unpriced_and_cost_nothing()
    {
        var r = Res("RS-001");
        var m = Run(D(0), r, [Asg("AS-001", r, 0, 4, 8)], [new(D(2), 100m)]);
        Assert.Equal(16m, m.UnpricedHours);
        Assert.Equal(3 * 8 * 100m, m.ByType[CostTypes.Labor].Planned);
    }

    [Fact]
    public void Estimate_to_complete_is_the_share_not_done_spread_over_the_days_after_the_status_date()
    {
        var r = Res("RS-001");
        var item = Guid.NewGuid();
        var a = Asg("AS-001", r, 0, 11, 8, item); // 10 working days, 80 hours, 100 per hour
        var items = new Dictionary<Guid, MathItem> { [item] = new(item, "InProgress", 50) };
        var m = Run(D(4), r, [a], [new(D(-30), 100m)], items); // status Friday of week one: the five days of week two remain
        Assert.Equal(40m * 100m, m.ByType[CostTypes.Labor].Etc);
        Assert.Equal(40m, m.Roles.Single().RemainingHours);
        Assert.Equal(8000m, m.ByType[CostTypes.Labor].Planned);
    }

    [Fact]
    public void Late_work_has_its_estimate_on_the_next_working_day_and_finished_or_cancelled_tasks_have_none()
    {
        var r = Res("RS-001");
        var late = Guid.NewGuid();
        var done = Guid.NewGuid();
        var cancelled = Guid.NewGuid();
        var items = new Dictionary<Guid, MathItem> { [late] = new(late, "InProgress", 0), [done] = new(done, "Complete", 100), [cancelled] = new(cancelled, "Cancelled", 0) };
        var asg = new[] { Asg("AS-001", r, 0, 4, 8, late), Asg("AS-002", r, 0, 4, 8, done), Asg("AS-003", r, 0, 4, 8, cancelled) };
        var m = Run(D(20), r, asg, [new(D(-30), 100m)], items); // status Sunday 2027-03-21: the next working day is Monday 22 March
        Assert.Equal(40m * 100m, m.ByType[CostTypes.Labor].Etc);
        Assert.Equal(new DateOnly(2027, 3, 1), m.Months.Single(x => x.CostType == CostTypes.Labor && x.Etc > 0).Period);
        Assert.Equal(1, m.Months.Count(x => x.CostType == CostTypes.Labor && x.Etc > 0));
    }

    [Fact]
    public void Budget_actual_and_estimate_roll_up_to_the_estimate_at_completion_and_the_variance()
    {
        var r = Res("RS-001");
        var item = Guid.NewGuid();
        var a = Asg("AS-001", r, 0, 11, 8, item);
        var items = new Dictionary<Guid, MathItem> { [item] = new(item, "InProgress", 50) };
        var m = Run(D(4), r, [a], [new(D(-30), 100m)], items,
            [new(CostTypes.Labor, Mon, 7000m), new(CostTypes.Material, Mon, 500m), new(CostTypes.Other, Mon, 100m)],
            [new(CostTypes.Labor, D(2), 3000m), new(CostTypes.Other, D(3), 150m), new(CostTypes.Other, D(4), -50m)],
            [new(D(2), 200m), new(D(40), 300m), new(D(41), null)]);
        var labor = m.ByType[CostTypes.Labor];
        Assert.Equal((7000m, 8000m, 3000m, 4000m), (labor.Budget, labor.Planned, labor.Actual, labor.Etc));
        Assert.Equal(7000m, labor.Eac);
        Assert.Equal(0m, labor.Variance);
        var material = m.ByType[CostTypes.Material];
        Assert.Equal((500m, 500m, 0m, 300m), (material.Budget, material.Planned, material.Actual, material.Etc)); // the line needed before the status date is not an estimate to complete
        var other = m.ByType[CostTypes.Other];
        Assert.Equal((100m, 0m, 100m, 0m), (other.Budget, other.Planned, other.Actual, other.Etc));
        Assert.Equal(7600m, m.Total.Budget);
        Assert.Equal(m.Total.Actual + m.Total.Etc, m.Total.Eac);
        Assert.Equal(m.Total.Budget - m.Total.Eac, m.Total.Variance);
    }

    [Fact]
    public void The_result_is_the_same_for_any_input_order()
    {
        var r = Res("RS-001");
        var asg = new[] { Asg("AS-002", r, 3, 12, 5), Asg("AS-001", r, 0, 6, 7) };
        var steps = new RateStep[] { new(D(-10), 90m), new(D(5), 110m) };
        var one = Run(D(2), r, asg, steps);
        var two = Run(D(2), r, [.. asg.Reverse()], [.. steps.Reverse()]);
        Assert.Equal(one.Total, two.Total);
        Assert.Equal(one.Months, two.Months);
    }
}
