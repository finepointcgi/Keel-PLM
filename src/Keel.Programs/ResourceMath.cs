using System.Globalization;

namespace Keel.Programs;

/// <summary>Cost types and the currency of program costs (ADR-0049, A60).</summary>
public static class CostTypes
{
    public const string Labor = "Labor";
    public const string Material = "Material";
    public const string Other = "Other";
    public static readonly string[] All = [Labor, Material, Other];
    /// <summary>One currency per installation: every amount is US dollars, and nothing is converted.</summary>
    public const string Currency = "USD";
}

public sealed record RateStep(DateOnly From, decimal Rate);

public sealed record MathResource(Guid Id, string Key, string Name, Guid RoleId, decimal Capacity, DateOnly? From, DateOnly? To);

public sealed record MathAssignment(Guid Id, string Key, Guid ResourceId, Guid ItemId, DateOnly Start, DateOnly End, decimal HoursPerDay);

public sealed record MathItem(Guid Id, string Status, int Percent);

public sealed record OverAllocationPeriod(Guid ResourceId, DateOnly From, DateOnly To, decimal PeakHours, decimal Capacity, decimal ExcessHours, IReadOnlyList<string> AssignmentKeys);

public sealed record WeekCell(decimal Allocated, decimal Capacity);

public sealed record MathActual(string CostType, DateOnly On, decimal Amount);

public sealed record MathBudget(string CostType, DateOnly Period, decimal Amount);

/// <summary>A material line already priced for the reader: <see cref="Estimate"/> is null when no estimate could be made.</summary>
public sealed record MathMaterial(DateOnly NeededOn, decimal? Estimate);

public sealed record CostMonth(DateOnly Period, string CostType, decimal Budget, decimal Planned, decimal Actual, decimal Etc)
{
    public decimal Eac => Actual + Etc;
}

public sealed record CostTotals(decimal Budget, decimal Planned, decimal Actual, decimal Etc)
{
    /// <summary>Estimate at completion: what is spent plus what is still expected to be spent.</summary>
    public decimal Eac => Actual + Etc;
    /// <summary>Budget minus estimate at completion: negative means the program is forecast to overrun.</summary>
    public decimal Variance => Budget - Eac;
}

public sealed record RoleLoad(Guid RoleId, decimal Hours, decimal PlannedCost, decimal RemainingHours, decimal Etc);

public sealed record CostMath(IReadOnlyList<CostMonth> Months, IReadOnlyDictionary<string, CostTotals> ByType, CostTotals Total, decimal UnpricedHours, IReadOnlyList<RoleLoad> Roles);

/// <summary>
/// Pure, deterministic arithmetic of resource loading and cost (ADR-0049): no clock, no database. A day of work is a weekday (Monday to Friday; there are no holiday calendars, A59);
/// an assignment puts its hours per day on every working day from its start to its end; a resource is over-allocated on a day when the hours of all its assignments exceed its
/// capacity (zero outside its availability window).
/// </summary>
public static class ResourceMath
{
    public static bool IsWorkingDay(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    public static IEnumerable<DateOnly> WorkingDays(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1))
            if (IsWorkingDay(d)) yield return d;
    }

    public static DateOnly MonthOf(DateOnly d) => new(d.Year, d.Month, 1);

    public static DateOnly WeekOf(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    public static decimal Capacity(MathResource r, DateOnly day) =>
        IsWorkingDay(day) && (r.From is null || day >= r.From) && (r.To is null || day <= r.To) ? r.Capacity : 0m;

    /// <summary>The rate in force on a day: the latest step on or before it, or null when there is none (the hours are then unpriced).</summary>
    public static decimal? RateAt(IReadOnlyList<RateStep> steps, DateOnly day)
    {
        decimal? rate = null;
        DateOnly? from = null;
        foreach (var s in steps)
            if (s.From <= day && (from is null || s.From > from)) { from = s.From; rate = s.Rate; }
        return rate;
    }

    /// <summary>Hours per resource and working day, over every assignment.</summary>
    public static Dictionary<(Guid Resource, DateOnly Day), (decimal Hours, List<string> Keys)> DailyLoad(IEnumerable<MathAssignment> assignments)
    {
        var load = new Dictionary<(Guid, DateOnly), (decimal, List<string>)>();
        foreach (var a in assignments.OrderBy(a => a.Key, StringComparer.Ordinal))
            foreach (var d in WorkingDays(a.Start, a.End))
            {
                var key = (a.ResourceId, d);
                if (!load.TryGetValue(key, out var cur)) cur = (0m, []);
                cur.Item2.Add(a.Key);
                load[key] = (cur.Item1 + a.HoursPerDay, cur.Item2);
            }
        return load;
    }

    /// <summary>Runs of consecutive working days on which a resource carries more than it can: one period per run, with the peak load and the hours above capacity.</summary>
    public static IReadOnlyList<OverAllocationPeriod> OverAllocations(IReadOnlyList<MathResource> resources, IReadOnlyList<MathAssignment> assignments)
    {
        var byId = resources.ToDictionary(r => r.Id);
        var load = DailyLoad(assignments.Where(a => byId.ContainsKey(a.ResourceId)));
        var result = new List<OverAllocationPeriod>();
        foreach (var r in resources.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var days = load.Where(kv => kv.Key.Resource == r.Id).Select(kv => (Day: kv.Key.Day, kv.Value.Hours, kv.Value.Keys)).OrderBy(x => x.Day).ToList();
            DateOnly? start = null, last = null;
            decimal peak = 0, excess = 0;
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            void Close()
            {
                if (start is { } s) result.Add(new OverAllocationPeriod(r.Id, s, last!.Value, peak, r.Capacity, excess, [.. keys]));
                start = null; peak = 0; excess = 0; keys.Clear();
            }
            foreach (var (day, hours, ks) in days)
            {
                var over = hours - Capacity(r, day);
                if (over <= 0) continue;
                // A run continues across a weekend (the next working day follows the last one) and ends at any working day that is not over-allocated.
                if (start is not null && !IsNextWorkingDay(last!.Value, day)) Close();
                start ??= day;
                last = day;
                peak = Math.Max(peak, hours);
                excess += over;
                foreach (var k in ks) keys.Add(k);
            }
            Close();
        }
        return result;
    }

    private static bool IsNextWorkingDay(DateOnly previous, DateOnly day)
    {
        var next = previous.AddDays(1);
        while (!IsWorkingDay(next)) next = next.AddDays(1);
        return next == day;
    }

    /// <summary>Allocated and available hours per resource and week (weeks start on Monday) between two dates.</summary>
    public static Dictionary<(Guid Resource, DateOnly Week), WeekCell> Weekly(IReadOnlyList<MathResource> resources, IReadOnlyList<MathAssignment> assignments, DateOnly from, DateOnly to)
    {
        var load = DailyLoad(assignments);
        var cells = new Dictionary<(Guid, DateOnly), WeekCell>();
        foreach (var r in resources)
            foreach (var d in WorkingDays(from, to))
            {
                var key = (r.Id, WeekOf(d));
                cells.TryGetValue(key, out var cell);
                load.TryGetValue((r.Id, d), out var l);
                cells[key] = new WeekCell((cell?.Allocated ?? 0m) + l.Hours, (cell?.Capacity ?? 0m) + Capacity(r, d));
            }
        return cells;
    }

    /// <summary>
    /// Budget, planned, actual and estimate to complete by month and cost type (ADR-0049, A60). Labor planned is the full loading of every assignment at the rate in force on each day.
    /// Labor ETC is the work still to do on open tasks: the assigned hours times the share not yet done, spread evenly over the working days after the status date, or on the next working
    /// day when the assignment already ended (late work). A task that is complete or cancelled has no ETC. Material planned is each line's estimate in its month; material ETC is the
    /// estimate of lines needed after the status date (earlier lines are assumed to be in the actuals). Other costs have a budget and actuals only.
    /// </summary>
    public static CostMath Compute(DateOnly asOf, IReadOnlyList<MathResource> resources, IReadOnlyList<MathAssignment> assignments, IReadOnlyDictionary<Guid, MathItem> items,
        IReadOnlyDictionary<Guid, IReadOnlyList<RateStep>> rates, IReadOnlyDictionary<Guid, Guid> resourceRole, IReadOnlyList<MathBudget> budget, IReadOnlyList<MathActual> actuals, IReadOnlyList<MathMaterial> material)
    {
        var planned = new Dictionary<(DateOnly, string), decimal>();
        var etc = new Dictionary<(DateOnly, string), decimal>();
        void Add(Dictionary<(DateOnly, string), decimal> d, DateOnly day, string type, decimal amount)
        {
            var k = (MonthOf(day), type);
            d[k] = d.GetValueOrDefault(k) + amount;
        }
        decimal unpriced = 0;
        var roleHours = new Dictionary<Guid, decimal>();
        var roleCost = new Dictionary<Guid, decimal>();
        var roleRemaining = new Dictionary<Guid, decimal>();
        var roleEtc = new Dictionary<Guid, decimal>();
        var resourceIds = resources.Select(r => r.Id).ToHashSet();
        foreach (var a in assignments.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            if (!resourceIds.Contains(a.ResourceId) || !resourceRole.TryGetValue(a.ResourceId, out var roleId)) continue;
            var steps = rates.TryGetValue(roleId, out var s) ? s : [];
            var days = WorkingDays(a.Start, a.End).ToList();
            foreach (var d in days)
            {
                roleHours[roleId] = roleHours.GetValueOrDefault(roleId) + a.HoursPerDay;
                if (RateAt(steps, d) is { } rate)
                {
                    var cost = a.HoursPerDay * rate;
                    Add(planned, d, CostTypes.Labor, cost);
                    roleCost[roleId] = roleCost.GetValueOrDefault(roleId) + cost;
                }
                else unpriced += a.HoursPerDay;
            }
            items.TryGetValue(a.ItemId, out var item);
            if (item is { Status: "Complete" or "Cancelled" }) continue;
            var assigned = days.Count * a.HoursPerDay;
            var remaining = assigned * (100 - Math.Clamp(item?.Percent ?? 0, 0, 100)) / 100m;
            if (remaining <= 0) continue;
            var future = days.Where(d => d > asOf).ToList();
            if (future.Count == 0)
            {
                var next = asOf.AddDays(1);
                while (!IsWorkingDay(next)) next = next.AddDays(1);
                future = [next];
            }
            var perDay = remaining / future.Count;
            roleRemaining[roleId] = roleRemaining.GetValueOrDefault(roleId) + remaining;
            foreach (var d in future)
                if (RateAt(steps, d) is { } rate)
                {
                    Add(etc, d, CostTypes.Labor, perDay * rate);
                    roleEtc[roleId] = roleEtc.GetValueOrDefault(roleId) + perDay * rate;
                }
        }
        foreach (var m in material)
        {
            if (m.Estimate is not { } e) continue;
            Add(planned, m.NeededOn, CostTypes.Material, e);
            if (m.NeededOn > asOf) Add(etc, m.NeededOn, CostTypes.Material, e);
        }
        var bud = new Dictionary<(DateOnly, string), decimal>();
        foreach (var b in budget) Add(bud, b.Period, b.CostType, b.Amount);
        var act = new Dictionary<(DateOnly, string), decimal>();
        foreach (var x in actuals) Add(act, x.On, x.CostType, x.Amount);

        var keys = bud.Keys.Concat(planned.Keys).Concat(etc.Keys).Concat(act.Keys).Distinct().OrderBy(k => k.Item1).ThenBy(k => Array.IndexOf(CostTypes.All, k.Item2)).ToList();
        decimal R(decimal v) => decimal.Round(v, 2, MidpointRounding.AwayFromZero);
        var months = keys.Select(k => new CostMonth(k.Item1, k.Item2, R(bud.GetValueOrDefault(k)), R(planned.GetValueOrDefault(k)), R(act.GetValueOrDefault(k)), R(etc.GetValueOrDefault(k)))).ToList();
        var byType = CostTypes.All.ToDictionary(t => t, t => new CostTotals(R(months.Where(m => m.CostType == t).Sum(m => m.Budget)), R(months.Where(m => m.CostType == t).Sum(m => m.Planned)),
            R(months.Where(m => m.CostType == t).Sum(m => m.Actual)), R(months.Where(m => m.CostType == t).Sum(m => m.Etc))));
        var total = new CostTotals(byType.Values.Sum(t => t.Budget), byType.Values.Sum(t => t.Planned), byType.Values.Sum(t => t.Actual), byType.Values.Sum(t => t.Etc));
        var roles = roleHours.Keys.OrderBy(k => k).Select(id => new RoleLoad(id, roleHours[id], R(roleCost.GetValueOrDefault(id)), R(roleRemaining.GetValueOrDefault(id)), R(roleEtc.GetValueOrDefault(id)))).ToList();
        return new CostMath(months, byType, total, unpriced, roles);
    }

    public static string Money(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);
    public static string Hours(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
