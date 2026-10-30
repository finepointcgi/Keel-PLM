using Keel.Core;
using Keel.Programs;
using Keel.Web.Components.Visuals;

namespace Keel.UnitTests;

/// <summary>The geometry of the cost curves and the load picture (9.5, ADR-0049): deterministic, every series distinguishable without colour, over-allocated cells flagged.</summary>
public sealed class ResourceChartLayoutTests
{
    private static readonly Markings Open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG", program: "ORCA").Value;
    private static DateOnly M(int month) => new(2027, month, 1);

    private static CostView View(DateOnly asOf, params CostMonth[] months)
    {
        var byType = CostTypes.All.ToDictionary(t => t, t => new CostTotals(months.Where(m => m.CostType == t).Sum(m => m.Budget), 0, months.Where(m => m.CostType == t).Sum(m => m.Actual), months.Where(m => m.CostType == t).Sum(m => m.Etc)));
        var total = new CostTotals(byType.Values.Sum(t => t.Budget), 0, byType.Values.Sum(t => t.Actual), byType.Values.Sum(t => t.Etc));
        return new CostView("ORCA", asOf, "USD", months, byType, total, [], [], [], [], 0, 0, 0, true, Open.BannerText());
    }

    [Fact]
    public void Cumulative_series_add_up_month_by_month_and_fill_months_without_rows()
    {
        var v = View(new DateOnly(2027, 2, 10),
            new CostMonth(M(1), CostTypes.Labor, 1000, 0, 800, 0), new CostMonth(M(2), CostTypes.Labor, 1000, 0, 500, 300), new CostMonth(M(4), CostTypes.Labor, 1000, 0, 0, 900));
        var s = CostCurveLayout.Series(v);
        Assert.Equal([M(1), M(2), M(3), M(4)], s.Select(p => p.Month));
        Assert.Equal([1000m, 2000m, 2000m, 3000m], s.Select(p => p.Budget));
        Assert.Equal([800m, 1300m, 1300m, 1300m], s.Select(p => p.Actual));
        Assert.Equal([800m, 1600m, 1600m, 2500m], s.Select(p => p.Eac));
        Assert.Equal([true, true, false, false], s.Select(p => p.Past));
    }

    [Fact]
    public void The_curve_layout_draws_three_distinguishable_series_and_describes_an_overrun_in_words()
    {
        var v = View(new DateOnly(2027, 2, 10), new CostMonth(M(1), CostTypes.Labor, 1000, 0, 800, 0), new CostMonth(M(2), CostTypes.Labor, 500, 0, 500, 900));
        var a = CostCurveLayout.Build(v);
        var b = CostCurveLayout.Build(v);
        Assert.Equal((a.BudgetPath, a.ActualPath, a.EacPath, a.Summary, a.Width, a.Height), (b.BudgetPath, b.ActualPath, b.EacPath, b.Summary, b.Width, b.Height));
        Assert.Equal(a.Markers.Select(m => (m.X, m.Y, m.Series)), b.Markers.Select(m => (m.X, m.Y, m.Series)));
        Assert.NotEmpty(a.BudgetPath);
        Assert.NotEmpty(a.ActualPath);
        Assert.NotEmpty(a.EacPath);
        Assert.Equal(["actual", "budget", "eac"], a.Markers.Select(m => m.Series).Distinct().Order(StringComparer.Ordinal));
        Assert.Contains("over the budget", a.Summary, StringComparison.Ordinal);
        Assert.NotNull(a.StatusX);
        Assert.All(a.Markers, m => Assert.Contains("cumulative", m.Tip, StringComparison.Ordinal));
        Assert.Equal("1.5k", CostCurveLayout.Compact(1500m));
        Assert.Equal("2M", CostCurveLayout.Compact(2_000_000m));
    }

    [Fact]
    public void An_empty_cost_view_gives_the_empty_layout()
    {
        Assert.Same(CostCurveLayout.Empty, CostCurveLayout.Build(View(M(1))));
    }

    private static LoadView Load(params LoadRow[] rows) =>
        new("ORCA", new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 14), [new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 8)], rows, [], Open.BannerText());

    private static LoadRow Row(string key, params (decimal Allocated, decimal Capacity)[] weeks) =>
        new(key, "Name " + key, "Role", [.. weeks.Select((w, i) => new LoadCellRow(new DateOnly(2027, 3, 1).AddDays(7 * i), w.Allocated, w.Capacity))], weeks.Sum(w => w.Allocated), weeks.Sum(w => w.Capacity));

    [Fact]
    public void Load_cells_are_banded_by_allocation_and_over_capacity_is_named_in_the_spoken_summary()
    {
        var layout = LoadHeatLayout.Build(Load(Row("RS-001", (0, 40), (50, 40)), Row("RS-002", (10, 40), (40, 40))));
        Assert.Equal(["none", "over"], layout.Rows[0].Cells.Select(c => c.Band));
        Assert.Equal(["low", "high"], layout.Rows[1].Cells.Select(c => c.Band));
        Assert.Contains("over capacity in 1 week", layout.Rows[0].Aria, StringComparison.Ordinal);
        Assert.Contains("within capacity", layout.Rows[1].Aria, StringComparison.Ordinal);
        Assert.Contains("(over capacity)", layout.Rows[0].Cells[1].Tip, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_rows_chain_the_keyboard_neighbours_and_an_empty_view_is_empty()
    {
        var layout = LoadHeatLayout.Build(Load(Row("RS-001", (1, 40), (1, 40)), Row("RS-002", (1, 40), (1, 40))));
        Assert.Null(layout.Rows[0].Up);
        Assert.Equal(layout.Rows[1].Id, layout.Rows[0].Down);
        Assert.Null(layout.Rows[1].Down);
        Assert.Same(LoadHeatLayout.Empty, LoadHeatLayout.Build(Load()));
    }
}
