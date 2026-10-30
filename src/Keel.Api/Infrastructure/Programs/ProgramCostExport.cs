using System.Globalization;
using Keel.Core;
using Keel.Programs;
using Keel.Reports.Tabular;

namespace Keel.Api.Infrastructure.Programs;

/// <summary>
/// Excel and CSV export of a program's resources and cost (ADR-0049). The hours tables (resources, assignments, load by week, over-allocation) are built from what the caller may read;
/// the money tables (summary, by month, roles, budget, actuals, material) only when the caller holds the program cost permission, and the rate column of the resource table with them.
/// Every file starts with the aggregate marking banner of what it holds and the currency, text cells are neutralized against formula injection and numbers are numbers.
/// </summary>
public static class ProgramCostExport
{
    public sealed record Built(byte[] Content, string ContentType, string FileName, int Rows);

    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public static readonly string[] Tables = ["summary", "months", "roles", "budget", "actuals", "material", "resources", "assignments", "load", "overallocation"];

    private static Cell T(string? s) => Cell.T(s);
    private static Cell N(decimal n) => Cell.N(n);
    private static Cell D(DateOnly d) => Cell.T(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    private static Cell[] Row(params Cell[] cells) => cells;

    public static IReadOnlyList<SheetData> Sheets(ResourceCatalog catalog, LoadView load, CostView? cost, IReadOnlyList<string> preface)
    {
        var sheets = new List<SheetData>();
        if (cost is not null)
        {
            var total = cost.Total;
            sheets.Add(new SheetData("Summary", ["Measure", $"Amount ({cost.Currency})"],
            [
                Row(T("Budget"), N(total.Budget)), Row(T("Actual to date"), N(total.Actual)), Row(T("Estimate to complete"), N(total.Etc)),
                Row(T("Estimate at completion"), N(total.Eac)), Row(T("Variance at completion (budget minus estimate at completion)"), N(total.Variance)),
                .. CostTypes.All.Select(t => Row(T($"{t}: budget / actual / estimate to complete"), T($"{ResourceMath.Money(cost.ByType[t].Budget)} / {ResourceMath.Money(cost.ByType[t].Actual)} / {ResourceMath.Money(cost.ByType[t].Etc)}"))),
                Row(T("Status date"), D(cost.AsOf)),
                Row(T("Labor hours without a rate"), N(cost.UnpricedHours)),
                Row(T("Active resources whose role has no rate"), N(cost.ResourcesWithoutRate)),
                Row(T("Over-allocated resources"), N(cost.OverAllocatedResources)),
            ], preface));
            sheets.Add(new SheetData("By month", ["Month", "Cost type", "Budget", "Planned", "Actual", "Estimate to complete", "Estimate at completion"],
                [.. cost.Months.Select(m => Row(D(m.Period), T(m.CostType), N(m.Budget), N(m.Planned), N(m.Actual), N(m.Etc), N(m.Eac)))], preface));
            sheets.Add(new SheetData("Roles", ["Role", "Name", "Pool", "Planned hours", "Planned cost", "Remaining hours", "Estimate to complete"],
                [.. cost.Roles.Select(r => Row(T(r.RoleKey), T(r.RoleName), T(r.PoolName), N(r.Hours), N(r.PlannedCost), N(r.RemainingHours), N(r.Etc)))], preface));
            sheets.Add(new SheetData("Budget", ["Line", "Cost type", "Month", "Amount"], [.. cost.Budget.Select(b => Row(T(b.Key), T(b.CostType), D(b.Period), N(b.Amount)))], preface));
            sheets.Add(new SheetData("Actuals", ["Entry", "Cost type", "Date", "Amount", "Hours", "Resource", "Task", "Reverses", "Note", "Recorded by"],
                [.. cost.Actuals.Select(a => Row(T(a.Key), T(a.CostType), D(a.IncurredOn), N(a.Amount), a.Hours is { } h ? N(h) : T(""), T(a.ResourceKey), T(a.ItemKey), T(a.ReversesKey), T(a.Note), T(a.CreatedBy)))], preface));
            sheets.Add(new SheetData("Material", ["Line", "Part", "Quantity", "Needed", "Estimate", "Priced parts", "Unpriced parts", "Restricted parts", "Problem"],
                [.. cost.Material.Select(m => Row(T(m.Key), T(m.PartNumber), N(m.Quantity), D(m.NeededOn), m.Estimate is { } e ? N(e) : T(""), N(m.PricedLines), N(m.UnpricedLines), N(m.RestrictedLines), T(m.Problem)))], preface));
        }
        var rate = catalog.CanViewCost;
        var roleRate = catalog.Roles.ToDictionary(r => r.Key, r => r.CurrentRate);
        sheets.Add(new SheetData("Resources", rate ? ["Resource", "Name", "Role", "Capacity (hours per day)", "Available from", "Available to", "Active", "Current rate per hour"] : ["Resource", "Name", "Role", "Capacity (hours per day)", "Available from", "Available to", "Active"],
            [.. catalog.Resources.Select(r =>
            {
                var cells = new List<Cell> { T(r.Key), T(r.Name), T(r.RoleName), N(r.CapacityHoursPerDay), r.AvailableFrom is { } f ? D(f) : T(""), r.AvailableTo is { } t ? D(t) : T(""), T(r.Active ? "Yes" : "No") };
                if (rate) cells.Add(roleRate.GetValueOrDefault(r.RoleKey) is { } v ? N(v) : T(""));
                return (IReadOnlyList<Cell>)cells;
            })], preface));
        sheets.Add(new SheetData("Assignments", ["Assignment", "Task", "Resource", "Start", "End", "Hours per day", "Total hours"],
            [.. catalog.Assignments.Select(a => Row(T(a.Key), T(a.ItemKey), T(a.ResourceKey), D(a.Start), D(a.End), N(a.HoursPerDay), N(a.TotalHours)))], preface));
        sheets.Add(new SheetData("Load by week", ["Resource", "Name", "Week starting", "Allocated hours", "Capacity hours", "Over-allocated"],
            [.. load.Rows.SelectMany(r => r.Weeks.Select(w => Row(T(r.ResourceKey), T(r.ResourceName), D(w.Week), N(w.Allocated), N(w.Capacity), T(w.Over ? "Yes" : "No"))))], preface));
        sheets.Add(new SheetData("Over-allocation", ["Resource", "Name", "From", "To", "Peak hours per day", "Capacity per day", "Hours above capacity", "Assignments"],
            [.. load.OverAllocations.Select(o => Row(T(o.ResourceKey), T(o.ResourceName), D(o.From), D(o.To), N(o.PeakHours), N(o.CapacityHoursPerDay), N(o.ExcessHours), T(string.Join(" ", o.AssignmentKeys))))], preface));
        return sheets;
    }

    public static Built Build(string code, ResourceCatalog catalog, LoadView load, CostView? cost, string format, string? table, DateOnly asOf)
    {
        // The cost view reads every row the hours views read and more, so its banner covers the file; without it the catalog and the load view share one banner (same rows).
        var banner = cost?.Banner ?? catalog.Banner;
        var preface = new List<string> { banner, $"Program {code}, status date {asOf:yyyy-MM-dd}", cost is null ? "Hours only: no cost permission" : $"Amounts in {cost.Currency}" };
        var sheets = Sheets(catalog, load, cost, preface);
        var stamp = asOf.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var rows = sheets.Sum(s => s.Rows.Count);
        if (format == "xlsx") return new Built(XlsxWriter.Write(sheets), Xlsx, $"program-{code}-resources-cost-{stamp}.xlsx", rows);
        var name = table switch
        {
            "summary" => "Summary", "months" => "By month", "roles" => "Roles", "budget" => "Budget", "actuals" => "Actuals", "material" => "Material", "resources" => "Resources",
            "assignments" => "Assignments", "load" => "Load by week", "overallocation" => "Over-allocation", _ => cost is null ? "Load by week" : "By month",
        };
        var sheet = sheets.FirstOrDefault(s => s.Name == name) ?? sheets.First(s => s.Name == "Load by week");
        return new Built(Csv.Write(sheet.Header, sheet.Rows, preface), "text/csv; charset=utf-8", $"program-{code}-{sheet.Name.ToLowerInvariant().Replace(' ', '-')}-{stamp}.csv", sheet.Rows.Count);
    }
}
