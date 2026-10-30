using Keel.Core;

namespace Keel.Programs;

// ---------------------------------------------------------------- commands (ADR-0049)

public sealed record AddPoolCommand(string Name, string? Description, Markings? Markings);
public sealed record AddRoleCommand(string PoolKey, string Name, decimal? HourlyRate, DateOnly? RateFrom, Markings? Markings);
public sealed record SetRoleRateCommand(decimal HourlyRate, DateOnly EffectiveFrom);
public sealed record AddResourceCommand(string RoleKey, string Name, decimal CapacityHoursPerDay, DateOnly? AvailableFrom, DateOnly? AvailableTo, Markings? Markings);
public sealed record UpdateResourceCommand(string Name, string RoleKey, decimal CapacityHoursPerDay, DateOnly? AvailableFrom, DateOnly? AvailableTo, bool Active);
public sealed record AddAssignmentCommand(string ItemKey, string ResourceKey, DateOnly Start, DateOnly End, decimal HoursPerDay);
public sealed record UpdateAssignmentCommand(DateOnly Start, DateOnly End, decimal HoursPerDay);
public sealed record SetBudgetCommand(string CostType, DateOnly Period, decimal Amount);
/// <summary>A labor entry gives hours and a resource (the amount is hours times the rate in force on the day); other entries give the amount. A reversal names the entry it cancels and takes its amount with the opposite sign.</summary>
public sealed record AddActualCommand(string CostType, DateOnly IncurredOn, decimal? Amount, decimal? Hours, string? ResourceKey, string? ItemKey, string? Note, string? ReversesKey);
public sealed record AddMaterialLineCommand(string PartNumber, decimal Quantity, DateOnly NeededOn, string? Note);

// ---------------------------------------------------------------- views

public sealed record PoolRow(string Key, string Name, string Description, int RoleCount, int ResourceCount, Markings Markings);

/// <summary>A rate in force from a day. Money fields are null when the viewer lacks the program cost permission.</summary>
public sealed record RateRow(DateOnly From, decimal HourlyRate);

public sealed record RoleRow(string Key, string Name, string PoolKey, string PoolName, decimal? CurrentRate, IReadOnlyList<RateRow> Rates, bool RateHidden, int ResourceCount, Markings Markings);

public sealed record ResourceRow(string Key, string Name, string RoleKey, string RoleName, string PoolKey, decimal CapacityHoursPerDay, DateOnly? AvailableFrom, DateOnly? AvailableTo, bool Active, bool CanModify, Markings Markings);

public sealed record AssignmentRow(string Key, string? ItemKey, string? ItemTitle, string ResourceKey, string ResourceName, DateOnly Start, DateOnly End, decimal HoursPerDay, decimal TotalHours, bool CanModify, Markings Markings);

public sealed record ResourceCatalog(string Code, IReadOnlyList<PoolRow> Pools, IReadOnlyList<RoleRow> Roles, IReadOnlyList<ResourceRow> Resources, IReadOnlyList<AssignmentRow> Assignments,
    bool CanModify, bool CanViewCost, string Banner);

public sealed record OverAllocationRow(string ResourceKey, string ResourceName, DateOnly From, DateOnly To, decimal PeakHours, decimal CapacityHoursPerDay, decimal ExcessHours, IReadOnlyList<string> AssignmentKeys);

public sealed record LoadCellRow(DateOnly Week, decimal Allocated, decimal Capacity)
{
    public bool Over => Allocated > Capacity;
    public decimal Utilization => Capacity <= 0 ? (Allocated > 0 ? 9.99m : 0m) : Allocated / Capacity;
}

public sealed record LoadRow(string ResourceKey, string ResourceName, string RoleName, IReadOnlyList<LoadCellRow> Weeks, decimal Allocated, decimal Capacity);

public sealed record LoadView(string Code, DateOnly From, DateOnly To, IReadOnlyList<DateOnly> Weeks, IReadOnlyList<LoadRow> Rows, IReadOnlyList<OverAllocationRow> OverAllocations, string Banner);

public sealed record BudgetRow(string Key, string CostType, DateOnly Period, decimal Amount, Markings Markings);

public sealed record ActualRow(string Key, string CostType, DateOnly IncurredOn, decimal Amount, decimal? Hours, string? ResourceKey, string? ItemKey, string Note, string? ReversesKey, string CreatedBy, Markings Markings);

public sealed record MaterialLineRow(string Key, string PartNumber, string? PartName, decimal Quantity, DateOnly NeededOn, string Note, decimal? Estimate, int PricedLines, int UnpricedLines, int RestrictedLines,
    IReadOnlyList<MaterialPartCost> Parts, string? Problem, Markings Markings);

public sealed record RoleCostRow(string RoleKey, string RoleName, string PoolName, decimal Hours, decimal PlannedCost, decimal RemainingHours, decimal Etc);

/// <summary>
/// Budget against actual against estimate to complete for one program, over the rows the viewer may read (ADR-0049). Only a viewer with the program cost permission gets it.
/// <see cref="Months"/> holds one row per month and cost type; <see cref="Eac"/> is actual plus estimate to complete and <see cref="Variance"/> is budget minus that.
/// </summary>
public sealed record CostView(string Code, DateOnly AsOf, string Currency, IReadOnlyList<CostMonth> Months, IReadOnlyDictionary<string, CostTotals> ByType, CostTotals Total,
    IReadOnlyList<RoleCostRow> Roles, IReadOnlyList<BudgetRow> Budget, IReadOnlyList<ActualRow> Actuals, IReadOnlyList<MaterialLineRow> Material, decimal UnpricedHours,
    int ResourcesWithoutRate, int OverAllocatedResources, bool CanModify, string Banner)
{
    public decimal PercentSpent => Total.Budget <= 0 ? 0m : Total.Actual / Total.Budget;
    public bool OverBudget => Total.Budget > 0 && Total.Eac > Total.Budget;
}
