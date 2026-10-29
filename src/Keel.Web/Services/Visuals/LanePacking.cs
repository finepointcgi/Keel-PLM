namespace Keel.Web.Services.Visuals;

/// <summary>An interval on a line (a label or marker on a time axis): <paramref name="Start"/> and <paramref name="End"/> in the same unit, End >= Start.</summary>
public sealed record LaneItem(string Id, double Start, double End);

public sealed record LaneAssignment(string Id, int Lane, double Start, double End);

/// <summary>
/// Greedy interval partitioning for the horizontal timeline: items are taken by start (then id) and placed in the first lane whose last item ended at least
/// <c>gap</c> before. This is optimal for the number of lanes (it equals the maximum overlap) and deterministic. No two items in one lane overlap.
/// </summary>
public static class LanePacking
{
    public static IReadOnlyList<LaneAssignment> Assign(IReadOnlyList<LaneItem> items, double gap = 0)
    {
        var order = items.OrderBy(i => i.Start).ThenBy(i => i.End).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        var laneEnds = new List<double>();
        var result = new List<LaneAssignment>(order.Count);
        foreach (var item in order)
        {
            var lane = laneEnds.FindIndex(end => end + gap <= item.Start);
            if (lane < 0) { laneEnds.Add(item.End); lane = laneEnds.Count - 1; }
            else laneEnds[lane] = item.End;
            result.Add(new LaneAssignment(item.Id, lane, item.Start, item.End));
        }
        return result;
    }
}
