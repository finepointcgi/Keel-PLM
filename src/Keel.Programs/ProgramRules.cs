using System.Globalization;

namespace Keel.Programs;

/// <summary>Kinds, statuses and codes of the program module (ADR-0043).</summary>
public static class ProgramKinds
{
    public const string Phase = "Phase";
    public const string Milestone = "Milestone";
    public const string Task = "Task";
    public static readonly string[] Items = [Phase, Milestone, Task];

    public static string Prefix(string kind) => kind switch { Phase => "PH", Milestone => "MS", Task => "TK", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
}

public static class ProgramStatuses
{
    public static readonly string[] All = ["Planning", "Active", "OnHold", "Complete", "Cancelled"];
    public static string Label(string s) => s == "OnHold" ? "On hold" : s;
}

public static class ItemStatuses
{
    public const string NotStarted = "NotStarted";
    public const string InProgress = "InProgress";
    public const string Blocked = "Blocked";
    public const string Complete = "Complete";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = [NotStarted, InProgress, Blocked, Complete, Cancelled];
    public static bool Open(string s) => s is NotStarted or InProgress or Blocked;
    public static string Label(string s) => s switch { NotStarted => "Not started", InProgress => "In progress", _ => s };
}

public static class GateTypes
{
    public static readonly string[] All = ["SRR", "PDR", "CDR", "TRR", "FRR", "PRR", "Other"];
    public static string Label(string t) => t switch
    {
        "SRR" => "System requirements review", "PDR" => "Preliminary design review", "CDR" => "Critical design review", "TRR" => "Test readiness review",
        "FRR" => "Flight or fielding readiness review", "PRR" => "Production readiness review", _ => "Other gate review",
    };
}

public static class GateStates
{
    public const string Draft = "Draft";
    public const string UnderReview = "UnderReview";
    public const string Approved = "Approved";
    public static string Label(string s) => s == "UnderReview" ? "Under review" : s;
}

/// <summary>How far a deliverable is, derived from the lifecycle state of the object it points at.</summary>
public static class Readiness
{
    public const string Ready = "Ready";
    public const string InReview = "InReview";
    public const string InWork = "InWork";
    public const string Ended = "Ended";
    /// <summary>The viewer may not read the object (or it does not exist): the state is not shown, and nothing else about it.</summary>
    public const string NotVisible = "NotVisible";

    public static string Label(string r) => r switch { InReview => "In review", InWork => "In work", NotVisible => "Not visible to you", Ended => "Ended", _ => r };

    private static readonly HashSet<string> ReadyLabels = ["released", "approved", "established", "closed", "implemented", "resolved", "dispositioned"];
    private static readonly HashSet<string> ReviewLabels = ["under review", "mrb review", "verification", "awaiting customer", "evaluating"];
    private static readonly HashSet<string> EndedLabels = ["obsolete", "superseded", "archived", "cancelled", "rejected", "withdrawn", "revoked", "expired"];

    /// <summary>Maps the lifecycle state label of a linked object to a readiness. The labels are those of the default lifecycles; an unknown label is "in work".</summary>
    public static string Of(string stateLabel)
    {
        var s = string.Join(' ', (stateLabel ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (ReadyLabels.Contains(s)) return Ready;
        if (ReviewLabels.Contains(s)) return InReview;
        if (EndedLabels.Contains(s)) return Ended;
        return InWork;
    }
}

public sealed record ScheduleItem(Guid Id, string Key, string Kind, Guid? ParentId, DateOnly PlannedStart, DateOnly PlannedEnd, DateOnly? ActualStart, DateOnly? ActualEnd, string Status, int Percent);
public sealed record ScheduleLink(Guid Predecessor, Guid Successor, int LagDays);

public sealed record ItemForecast(Guid Id, DateOnly EarlyStart, DateOnly EarlyFinish, DateOnly LateStart, DateOnly LateFinish, int FloatDays, bool Critical, int SlipDays);

/// <summary>The computed schedule: forecast dates per task and milestone, the project finish, and the critical items in schedule order.</summary>
public sealed record ScheduleResult(IReadOnlyDictionary<Guid, ItemForecast> Forecast, DateOnly? Finish, IReadOnlyList<Guid> CriticalPath, bool Cyclic);

/// <summary>
/// The pure rules of the schedule (ADR-0043): the critical path method over finish-to-start links, percent complete roll-up, slip against a baseline. No clock and no
/// database: the status date is a parameter, ties break on (planned start, key), so the same input always gives the same answer.
/// </summary>
public static class ScheduleMath
{
    private static int Days(DateOnly a, DateOnly b) => b.DayNumber - a.DayNumber;

    public static int Duration(ScheduleItem i) => Math.Max(0, Days(i.PlannedStart, i.PlannedEnd));

    private static bool InNetwork(ScheduleItem i) => i.Kind != ProgramKinds.Phase && i.Status != ItemStatuses.Cancelled;

    /// <summary>
    /// Forward and backward pass. Only tasks and milestones that are not cancelled take part; links to anything else are ignored. A completed item is fixed at its actual dates;
    /// a started item keeps its actual start and cannot finish before the status date; an item not started cannot start before the status date. The planned start is a "no earlier
    /// than" constraint. Float is late start minus early start; critical items are the open ones with no float.
    /// </summary>
    public static ScheduleResult Compute(IReadOnlyCollection<ScheduleItem> items, IReadOnlyCollection<ScheduleLink> links, DateOnly asOf)
    {
        var nodes = items.Where(InNetwork).OrderBy(i => i.PlannedStart).ThenBy(i => i.Key, StringComparer.Ordinal).ToList();
        var byId = nodes.ToDictionary(n => n.Id);
        var edges = links.Where(l => byId.ContainsKey(l.Predecessor) && byId.ContainsKey(l.Successor) && l.Predecessor != l.Successor).Distinct().ToList();
        var preds = edges.GroupBy(e => e.Successor).ToDictionary(g => g.Key, g => g.ToList());
        var succs = edges.GroupBy(e => e.Predecessor).ToDictionary(g => g.Key, g => g.ToList());

        // Topological order (Kahn), picking the smallest (planned start, key) first for a stable result.
        var indegree = nodes.ToDictionary(n => n.Id, n => preds.TryGetValue(n.Id, out var p) ? p.Count : 0);
        var order = new List<ScheduleItem>();
        var ready = new SortedSet<(DateOnly, string, Guid)>(nodes.Where(n => indegree[n.Id] == 0).Select(n => (n.PlannedStart, n.Key, n.Id)));
        while (ready.Count > 0)
        {
            var first = ready.Min;
            ready.Remove(first);
            var n = byId[first.Item3];
            order.Add(n);
            if (!succs.TryGetValue(n.Id, out var outs)) continue;
            foreach (var e in outs)
                if (--indegree[e.Successor] == 0) { var s = byId[e.Successor]; ready.Add((s.PlannedStart, s.Key, s.Id)); }
        }
        if (order.Count != nodes.Count) return new ScheduleResult(new Dictionary<Guid, ItemForecast>(), null, [], true);
        if (nodes.Count == 0) return new ScheduleResult(new Dictionary<Guid, ItemForecast>(), null, [], false);

        var es = new Dictionary<Guid, DateOnly>();
        var ef = new Dictionary<Guid, DateOnly>();
        foreach (var n in order)
        {
            var d = Duration(n);
            DateOnly start, finish;
            if (n.Status == ItemStatuses.Complete)
            {
                start = n.ActualStart ?? n.PlannedStart;
                finish = n.ActualEnd ?? n.PlannedEnd;
                if (finish < start) finish = start;
            }
            else if (n.ActualStart is { } started)
            {
                start = started;
                finish = start.AddDays(d);
                if (finish < asOf) finish = asOf;
            }
            else
            {
                start = n.PlannedStart > asOf ? n.PlannedStart : asOf;
                if (preds.TryGetValue(n.Id, out var ps))
                    foreach (var e in ps) { var c = ef[e.Predecessor].AddDays(e.LagDays); if (c > start) start = c; }
                finish = start.AddDays(d);
            }
            es[n.Id] = start;
            ef[n.Id] = finish;
        }
        var projectFinish = ef.Values.Max();

        var lf = new Dictionary<Guid, DateOnly>();
        var ls = new Dictionary<Guid, DateOnly>();
        for (var k = order.Count - 1; k >= 0; k--)
        {
            var n = order[k];
            var late = projectFinish;
            if (succs.TryGetValue(n.Id, out var outs))
                foreach (var e in outs) { var c = ls[e.Successor].AddDays(-e.LagDays); if (c < late) late = c; }
            if (late < ef[n.Id]) late = ef[n.Id];
            lf[n.Id] = late;
            ls[n.Id] = late.AddDays(-Duration(n));
        }

        var forecast = new Dictionary<Guid, ItemForecast>();
        foreach (var n in order)
        {
            var open = n.Status != ItemStatuses.Complete;
            var slackDays = open ? Math.Max(0, Days(es[n.Id], ls[n.Id])) : 0;
            var slip = Days(n.PlannedEnd, n.Status == ItemStatuses.Complete ? (n.ActualEnd ?? n.PlannedEnd) : ef[n.Id]);
            forecast[n.Id] = new ItemForecast(n.Id, es[n.Id], ef[n.Id], ls[n.Id], lf[n.Id], slackDays, open && slackDays == 0, slip);
        }
        var critical = order.Where(n => forecast[n.Id].Critical).OrderBy(n => es[n.Id]).ThenBy(n => ef[n.Id]).ThenBy(n => n.Key, StringComparer.Ordinal).Select(n => n.Id).ToList();
        return new ScheduleResult(forecast, projectFinish, critical, false);
    }

    /// <summary>Whether adding a link from <paramref name="predecessor"/> to <paramref name="successor"/> would close a loop.</summary>
    public static bool WouldCreateCycle(IReadOnlyCollection<ScheduleLink> links, Guid predecessor, Guid successor)
    {
        if (predecessor == successor) return true;
        var seen = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(successor);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == predecessor) return true;
            if (!seen.Add(cur)) continue;
            foreach (var l in links) if (l.Predecessor == cur) stack.Push(l.Successor);
        }
        return false;
    }

    /// <summary>
    /// Percent complete of every phase and of the whole program: the weighted mean of the open and completed tasks and milestones below it (cancelled ones do not count),
    /// weight = duration in days, at least one. A leaf has its own percent. Null when nothing below counts.
    /// </summary>
    public static (IReadOnlyDictionary<Guid, int?> ByItem, int? Overall) Rollup(IReadOnlyCollection<ScheduleItem> items)
    {
        var children = items.Where(i => i.ParentId is not null).GroupBy(i => i.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var result = new Dictionary<Guid, int?>();
        (long Sum, long Weight) Walk(ScheduleItem i)
        {
            if (i.Status == ItemStatuses.Cancelled) return (0, 0);
            if (i.Kind != ProgramKinds.Phase)
            {
                var w = Math.Max(1, Duration(i));
                var p = i.Status == ItemStatuses.Complete ? 100 : i.Percent;
                result[i.Id] = p;
                return ((long)p * w, w);
            }
            long sum = 0, weight = 0;
            if (children.TryGetValue(i.Id, out var kids))
                foreach (var k in kids) { var (s, w) = Walk(k); sum += s; weight += w; }
            result[i.Id] = weight == 0 ? null : (int)((sum + weight / 2) / weight);
            return (sum, weight);
        }
        long total = 0, totalWeight = 0;
        foreach (var root in items.Where(i => i.ParentId is null || items.All(p => p.Id != i.ParentId)))
        { var (s, w) = Walk(root); total += s; totalWeight += w; }
        return (result, totalWeight == 0 ? null : (int)((total + totalWeight / 2) / totalWeight));
    }

    /// <summary>Days an item's planned end moved since the baseline (positive = later).</summary>
    public static int BaselineSlip(DateOnly baselineEnd, DateOnly plannedEnd) => Days(baselineEnd, plannedEnd);

    public static bool IsOverdue(ScheduleItem i, DateOnly today) => ItemStatuses.Open(i.Status) && i.PlannedEnd < today;

    public static string Fmt(DateOnly? d) => d is null ? "none" : d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
