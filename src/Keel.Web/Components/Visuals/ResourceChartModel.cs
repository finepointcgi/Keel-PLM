using System.Globalization;
using Keel.Programs;

namespace Keel.Web.Components.Visuals;

public sealed record CostSeriesPoint(DateOnly Month, decimal Budget, decimal Actual, decimal Eac, bool Past);

public sealed record CostMarker(double X, double Y, string Series, string Tip);

public sealed record CostAxisTick(double Pos, string Label);

/// <summary>
/// The geometry of the cost curves of a program (ADR-0049, visuals kit): cumulative budget, cumulative actual (up to the status month) and cumulative estimate at completion, by month,
/// computed on the server from a <see cref="CostView"/> (which holds only what the viewer may read and is available only with the program cost permission). Each series has its own line
/// style AND marker shape, and the last value of each is written on the chart, so no meaning rests on colour alone. A table with the same numbers sits next to the picture.
/// </summary>
public sealed record CostCurveLayout(double Width, double Height, IReadOnlyList<CostSeriesPoint> Points, string BudgetPath, string ActualPath, string EacPath,
    IReadOnlyList<CostMarker> Markers, IReadOnlyList<CostAxisTick> XTicks, IReadOnlyList<CostAxisTick> YTicks, double? StatusX, string Summary)
{
    public static readonly CostCurveLayout Empty = new(720, 120, [], "", "", "", [], [], [], null, "No cost data yet.");
    public const double Left = 64, Right = 28, Top = 20, Bottom = 40, PlotWidth = 628, PlotHeight = 240;

    public static string Compact(decimal v)
    {
        var a = Math.Abs(v);
        var (div, suffix) = a >= 1_000_000m ? (1_000_000m, "M") : a >= 1_000m ? (1_000m, "k") : (1m, "");
        return (v / div).ToString(div == 1m ? "0" : "0.##", CultureInfo.InvariantCulture) + suffix;
    }

    public static IReadOnlyList<CostSeriesPoint> Series(CostView v)
    {
        if (v.Months.Count == 0) return [];
        var first = v.Months.Min(m => m.Period);
        var last = v.Months.Max(m => m.Period);
        var statusMonth = new DateOnly(v.AsOf.Year, v.AsOf.Month, 1);
        var points = new List<CostSeriesPoint>();
        decimal budget = 0, actual = 0, etc = 0;
        for (var m = first; m <= last; m = m.AddMonths(1))
        {
            foreach (var row in v.Months.Where(r => r.Period == m))
            {
                budget += row.Budget;
                actual += row.Actual;
                etc += row.Etc;
            }
            points.Add(new CostSeriesPoint(m, budget, actual, actual + etc, m <= statusMonth));
        }
        return points;
    }

    public static CostCurveLayout Build(CostView v)
    {
        List<CostSeriesPoint> pts = [.. Series(v)];
        if (pts.Count == 0) return Empty;
        var max = pts.Max(p => Math.Max(p.Budget, Math.Max(p.Actual, p.Eac)));
        var step = NiceStep(max <= 0 ? 1 : max / 4);
        var top = Math.Max(step * 4, Math.Ceiling(max / step) * step);
        var width = Left + PlotWidth + Right;
        var height = Top + PlotHeight + Bottom;
        double X(int i) => Left + (pts.Count == 1 ? PlotWidth / 2 : i * PlotWidth / (pts.Count - 1));
        double Y(decimal val) => Top + PlotHeight - (double)(val / top) * PlotHeight;
        string Line(IEnumerable<(int I, decimal V)> xs) => string.Join(" ", xs.Select((p, n) => FormattableString.Invariant($"{(n == 0 ? "M" : "L")} {X(p.I):0.##} {Y(p.V):0.##}")));
        var budgetPath = Line(pts.Select((p, i) => (i, p.Budget)));
        var actualPath = Line(pts.Select((p, i) => (i, p: p, ok: p.Past)).Where(t => t.ok).Select(t => (t.i, t.p.Actual)));
        var eacPath = Line(pts.Select((p, i) => (i, p.Eac)));
        var markers = new List<CostMarker>();
        for (var i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var label = p.Month.ToString("MMM yyyy", CultureInfo.InvariantCulture);
            markers.Add(new CostMarker(X(i), Y(p.Budget), "budget", $"{label}: budget {ResourceMath.Money(p.Budget)} cumulative"));
            if (p.Past) markers.Add(new CostMarker(X(i), Y(p.Actual), "actual", $"{label}: actual {ResourceMath.Money(p.Actual)} cumulative"));
            markers.Add(new CostMarker(X(i), Y(p.Eac), "eac", $"{label}: estimate at completion {ResourceMath.Money(p.Eac)} cumulative"));
        }
        var xTicks = new List<CostAxisTick>();
        var every = Math.Max(1, (int)Math.Ceiling(pts.Count / 10.0));
        for (var i = 0; i < pts.Count; i += every) xTicks.Add(new CostAxisTick(X(i), pts[i].Month.ToString("MMM yy", CultureInfo.InvariantCulture)));
        var yTicks = new List<CostAxisTick>();
        for (var t = 0m; t <= top; t += step) yTicks.Add(new CostAxisTick(Y(t), Compact(t)));
        var statusMonth = new DateOnly(v.AsOf.Year, v.AsOf.Month, 1);
        var statusIndex = pts.FindIndex(p => p.Month == statusMonth);
        var end = pts[^1];
        var summary = $"Cumulative cost by month from {pts[0].Month:MMM yyyy} to {end.Month:MMM yyyy} in {v.Currency}. Budget ends at {ResourceMath.Money(end.Budget)}, actual to date is {ResourceMath.Money(v.Total.Actual)}, estimate at completion is {ResourceMath.Money(end.Eac)}"
            + (end.Eac > end.Budget && end.Budget > 0 ? ", which is over the budget." : ".");
        return new CostCurveLayout(width, height, pts, budgetPath, actualPath, eacPath, markers, xTicks, yTicks, statusIndex >= 0 ? X(statusIndex) : null, summary);
    }

    private static decimal NiceStep(decimal raw)
    {
        var pow = (decimal)Math.Pow(10, Math.Floor(Math.Log10((double)raw)));
        var n = raw / pow;
        return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * pow;
    }
}

/// <summary>One cell of the load picture: a week of one resource. <see cref="Band"/> is none, low, high or over; over-allocated cells are hatched, so colour is never the only signal.</summary>
public sealed record LoadCell(double X, double Width, string Band, string Tip);

public sealed record LoadRowShape(string Id, int Row, string Key, string Name, double Y, string Aria, IReadOnlyList<LoadCell> Cells, string? Up, string? Down);

public sealed record LoadTick(double X, string Label);

/// <summary>
/// The geometry of the resource load picture (ADR-0049): one row per resource the viewer may read, one cell per week, painted by allocation against capacity (none, up to 50 percent,
/// up to 100 percent, over capacity). Rows are the keyboard stops (arrow keys up and down) with a spoken summary; a table with the same facts is offered next to the picture.
/// </summary>
public sealed record LoadHeatLayout(double Width, double Height, IReadOnlyList<LoadRowShape> Rows, IReadOnlyList<LoadTick> Ticks)
{
    public static readonly LoadHeatLayout Empty = new(600, 80, [], []);
    public const double Label = 200, Cell = 14, Row = 26, Header = 34;

    public static string BandOf(LoadCellRow c) => c.Allocated <= 0 ? "none" : c.Allocated > c.Capacity ? "over" : c.Allocated / c.Capacity > 0.5m ? "high" : "low";

    public static LoadHeatLayout Build(LoadView v)
    {
        if (v.Rows.Count == 0 || v.Weeks.Count == 0) return Empty;
        string IdOf(int n) => "r" + n.ToString(CultureInfo.InvariantCulture);
        var rows = new List<LoadRowShape>();
        for (var n = 0; n < v.Rows.Count; n++)
        {
            var r = v.Rows[n];
            var y = Header + n * Row;
            var cells = r.Weeks.Select((c, i) => new LoadCell(Label + i * Cell, Cell - 2, BandOf(c),
                $"{r.ResourceKey} week of {c.Week:yyyy-MM-dd}: {ResourceMath.Hours(c.Allocated)} of {ResourceMath.Hours(c.Capacity)} hours" + (c.Over ? " (over capacity)" : ""))).ToList();
            var overWeeks = r.Weeks.Count(c => BandOf(c) == "over");
            var aria = $"{r.ResourceKey} {r.ResourceName}, {r.RoleName}, {ResourceMath.Hours(r.Allocated)} hours allocated of {ResourceMath.Hours(r.Capacity)} available" + (overWeeks > 0 ? $", over capacity in {overWeeks} week{(overWeeks == 1 ? "" : "s")}" : ", within capacity");
            rows.Add(new LoadRowShape(IdOf(n), n, r.ResourceKey, r.ResourceName, y, aria, cells, n > 0 ? IdOf(n - 1) : null, n + 1 < v.Rows.Count ? IdOf(n + 1) : null));
        }
        var ticks = new List<LoadTick>();
        var every = v.Weeks.Count > 52 ? 13 : 4;
        for (var i = 0; i < v.Weeks.Count; i += every) ticks.Add(new LoadTick(Label + i * Cell, v.Weeks[i].ToString("d MMM yy", CultureInfo.InvariantCulture)));
        return new LoadHeatLayout(Label + v.Weeks.Count * Cell + 24, Header + rows.Count * Row + 16, rows, ticks);
    }
}
