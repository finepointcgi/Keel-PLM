using System.Globalization;
using Keel.Programs;

namespace Keel.Web.Components.Visuals;

public sealed record GanttBar(string Id, int Row, string? Key, string Kind, string Label, double X, double Width, double Y, string Aria, bool Critical, bool Redacted, bool Gate, int Percent,
    double? BaselineX, string? Href, string? Tip, string? Left, string? Right, string? Up, string? Down);
public sealed record GanttArrow(double X1, double Y1, double X2, double Y2);
public sealed record GanttTick(double X, string Label);

/// <summary>
/// The geometry of the Gantt-style timeline (ADR-0043), computed on the server from a <see cref="ScheduleView"/> (which holds only what the viewer may read): one row per item in
/// outline order, a bar or diamond placed by date, dependency arrows, a baseline tick and the status date. An item the viewer cannot read keeps its row and its time span and is drawn
/// as a hatched bar named "Restricted item"; it has no key, no title, no link and no arrows. A table with the same facts is offered next to the picture.
/// </summary>
public sealed record GanttLayout(double Width, double Height, double LabelWidth, double RowHeight, IReadOnlyList<GanttBar> Bars, IReadOnlyList<GanttArrow> Arrows, IReadOnlyList<GanttTick> Ticks, double? TodayX)
{
    public static readonly GanttLayout Empty = new(600, 80, 200, 28, [], [], [], null);
    public const double Row = 28;
    public const double Label = 230;
    public const double Header = 34;

    public static string Fmt(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static GanttLayout Build(ScheduleView v)
    {
        var rows = new List<(int Sort, DateOnly Start, ItemRow? Item, RestrictedItemRow? Hidden)>();
        rows.AddRange(v.Items.Select(i => (i.SortOrder, i.PlannedStart, (ItemRow?)i, (RestrictedItemRow?)null)));
        rows.AddRange(v.Restricted.Select(r => (r.SortOrder, r.PlannedStart, (ItemRow?)null, (RestrictedItemRow?)r)));
        rows = [.. rows.OrderBy(r => r.Sort).ThenBy(r => r.Start).ThenBy(r => r.Item?.Key ?? "~", StringComparer.Ordinal)];
        if (rows.Count == 0) return Empty;

        var dates = new List<DateOnly> { v.AsOf };
        foreach (var r in rows)
        {
            if (r.Item is { } i) { dates.Add(i.PlannedStart); dates.Add(i.PlannedEnd); if (i.ForecastEnd is { } fe) dates.Add(fe); if (i.BaselineEnd is { } be) dates.Add(be); }
            else { dates.Add(r.Hidden!.PlannedStart); dates.Add(r.Hidden.PlannedEnd); }
        }
        var min = dates.Min().AddDays(-3);
        var max = dates.Max().AddDays(3);
        var span = Math.Max(14, max.DayNumber - min.DayNumber);
        var perDay = Math.Clamp(900.0 / span, 3.0, 14.0);
        double X(DateOnly d) => Label + (d.DayNumber - min.DayNumber) * perDay;
        var width = Label + span * perDay + 40;
        var height = Header + rows.Count * Row + 16;

        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var n = 0; n < rows.Count; n++) if (rows[n].Item is { } it) index[it.Key] = n;
        string IdOf(int n) => "r" + n.ToString(CultureInfo.InvariantCulture);

        var bars = new List<GanttBar>();
        for (var n = 0; n < rows.Count; n++)
        {
            var y = Header + n * Row;
            string? up = n > 0 ? IdOf(n - 1) : null, down = n + 1 < rows.Count ? IdOf(n + 1) : null;
            if (rows[n].Item is { } i)
            {
                var milestone = i.Kind == ProgramKinds.Milestone;
                var x = X(i.PlannedStart);
                var w = milestone ? 14 : Math.Max(6, (i.PlannedEnd.DayNumber - i.PlannedStart.DayNumber + 1) * perDay);
                if (milestone) x -= 7;
                var left = v.Dependencies.Where(d => d.SuccessorKey == i.Key && index.ContainsKey(d.PredecessorKey)).Select(d => IdOf(index[d.PredecessorKey])).FirstOrDefault();
                var right = v.Dependencies.Where(d => d.PredecessorKey == i.Key && index.ContainsKey(d.SuccessorKey)).Select(d => IdOf(index[d.SuccessorKey])).FirstOrDefault();
                var aria = $"{i.Key} {i.Title}, {i.Kind.ToLowerInvariant()}{(i.GateType is null ? "" : ", " + i.GateType + " gate review")}, {Fmt(i.PlannedStart)}{(milestone ? "" : " to " + Fmt(i.PlannedEnd))}, {i.StatusLabel.ToLowerInvariant()}, {i.Percent} percent complete"
                    + (i.Critical ? ", on the critical path" : "") + (i.Overdue ? ", overdue" : "") + (i.BaselineSlipDays is > 0 and var s ? $", {s} days later than the baseline" : "");
                bars.Add(new GanttBar(IdOf(n), n, i.Key, i.Kind, i.Title, x, w, y, aria, i.Critical, false, i.GateNumber is not null, i.Percent, i.BaselineEnd is { } b ? X(b) : null,
                    i.GateNumber is not null ? $"programs/gates/{Uri.EscapeDataString(i.GateNumber)}" : $"programs/{Uri.EscapeDataString(v.Code)}?tab=schedule&item={Uri.EscapeDataString(i.Key)}",
                    $"{i.Key} {i.Title} · {Fmt(i.PlannedStart)}{(milestone ? "" : " to " + Fmt(i.PlannedEnd))} · {i.Percent}%", left, right, up, down));
            }
            else
            {
                var h = rows[n].Hidden!;
                var w = Math.Max(6, (h.PlannedEnd.DayNumber - h.PlannedStart.DayNumber + 1) * perDay);
                bars.Add(new GanttBar(IdOf(n), n, null, "Restricted", "Restricted item", X(h.PlannedStart), w, y, $"Restricted item, {Fmt(h.PlannedStart)} to {Fmt(h.PlannedEnd)}", false, true, false, 0, null, null,
                    "Restricted item", null, null, up, down));
            }
        }
        var arrows = new List<GanttArrow>();
        foreach (var d in v.Dependencies)
        {
            if (!index.TryGetValue(d.PredecessorKey, out var pi) || !index.TryGetValue(d.SuccessorKey, out var si)) continue;
            var p = bars[pi];
            var s = bars[si];
            arrows.Add(new GanttArrow(p.X + p.Width, p.Y + Row / 2, s.X, s.Y + Row / 2));
        }
        var ticks = new List<GanttTick>();
        var m = new DateOnly(min.Year, min.Month, 1).AddMonths(1);
        var step = span > 500 ? 3 : 1;
        for (; m <= max; m = m.AddMonths(step)) ticks.Add(new GanttTick(X(m), m.ToString("MMM yyyy", CultureInfo.InvariantCulture)));
        return new GanttLayout(width, height, Label, Row, bars, arrows, ticks, X(v.AsOf));
    }
}
