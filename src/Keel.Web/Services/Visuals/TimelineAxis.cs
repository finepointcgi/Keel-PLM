using System.Globalization;
using Keel.Core;

namespace Keel.Web.Services.Visuals;

public sealed record AxisItem(TimelineEvent Event, double X, int Lane, double LabelY, double Width);

public sealed record AxisTick(double X, string Label);

/// <summary>
/// Geometry of the horizontal time line (ADR-0029). Events are placed along an axis, either by time (one scale in months) or evenly in order with ticks where the revision
/// changes; the label boxes are packed into lanes below the axis with <see cref="LanePacking"/> so none overlaps another. Pure and deterministic.
/// </summary>
public sealed record TimelineAxis(IReadOnlyList<AxisItem> Items, IReadOnlyList<AxisTick> Ticks, double Width, double Height, double AxisY, int Lanes)
{
    public const double LabelWidth = 176;
    public const double LaneHeight = 48;
    public const double Gap = 10;
    private const double Start = 40;

    public static readonly TimelineAxis Empty = new([], [], 200, 120, 50, 0);

    public static TimelineAxis Build(IReadOnlyList<TimelineEvent> events, bool byTime)
    {
        if (events.Count == 0) return Empty;
        const double axisY = 50;
        var xs = new double[events.Count];
        var ticks = new List<AxisTick>();
        if (byTime && events.Count > 1 && events[^1].At > events[0].At)
        {
            var t0 = events[0].At.UtcDateTime;
            var span = (events[^1].At.UtcDateTime - t0).TotalDays;
            var width = Math.Max(900, Math.Min(6000, events.Count * 110));
            for (var i = 0; i < events.Count; i++) xs[i] = Start + (events[i].At.UtcDateTime - t0).TotalDays / span * width;
            var month = new DateTime(t0.Year, t0.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
            var lastTick = double.NegativeInfinity;
            for (; month <= events[^1].At.UtcDateTime; month = month.AddMonths(1))
            {
                var x = Start + (month - t0).TotalDays / span * width;
                if (x - lastTick < 56) continue; // a tick label needs room
                ticks.Add(new AxisTick(x, month.ToString("yyyy-MM", CultureInfo.InvariantCulture)));
                lastTick = x;
            }
        }
        else
        {
            var last = "";
            for (var i = 0; i < events.Count; i++)
            {
                xs[i] = Start + i * 120;
                var rev = events[i].Revision ?? "";
                if (rev.Length > 0 && rev != last) { ticks.Add(new AxisTick(xs[i], $"Rev {rev}")); last = rev; }
            }
        }

        var packed = LanePacking.Assign([.. xs.Select((x, i) => new LaneItem(i.ToString(CultureInfo.InvariantCulture), x - 8, x - 8 + LabelWidth))], Gap);
        var lanes = packed.Count == 0 ? 0 : packed.Max(p => p.Lane) + 1;
        var laneOf = packed.ToDictionary(p => p.Id, p => p.Lane, StringComparer.Ordinal);
        var items = new List<AxisItem>(events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            var lane = laneOf[i.ToString(CultureInfo.InvariantCulture)];
            items.Add(new AxisItem(events[i], xs[i], lane, axisY + 36 + lane * LaneHeight, LabelWidth));
        }
        return new TimelineAxis(items, ticks, xs.Max() + LabelWidth + 24, axisY + 36 + lanes * LaneHeight + 16, axisY, lanes);
    }
}
