using System.Globalization;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Programs;

public sealed partial class ProgramService
{
    /// <summary>Reason code of a refused cost operation: the viewer lacks the program cost permission (the role <see cref="Roles.ProgramCost"/>).</summary>
    public const string CostRoleMissing = "cost.role_required";

    /// <summary>Whether the viewer holds the program cost permission: the additional gate, on top of the markings, for every rate and every amount (ADR-0049).</summary>
    public static bool CanViewCost(Subject s) => s.Roles.Contains(Roles.ProgramCost);

    public const int MaxLoadWeeks = 130;

    // ---------------------------------------------------------------- rules shared by the resource operations

    private static string? NameError(string? name, string what) => (name?.Trim().Length ?? 0) is >= 1 and <= 120 ? null : $"{what} name is required (at most 120 characters).";

    private static PStep? RequireRow(Subject s, Markings? row, PlmAction permission)
    {
        if (row is null || !AccessPolicy.Evaluate(s, row, PlmAction.Read).Permitted) return PStep.Missing();
        if (permission == PlmAction.Read) return null;
        var d = AccessPolicy.Evaluate(s, row, permission);
        return d.Permitted ? null : PStep.Forbid([.. d.Reasons]);
    }

    /// <summary>Markings of a new row: the requested ones or the default, which must cover the program's and every parent's.</summary>
    private static (Markings? Markings, PStep? Error) RowMarkings(PHead head, Markings? requested, Markings fallback, params Markings[] parents)
    {
        var markings = requested ?? fallback;
        foreach (var p in parents.Append(head.Markings))
        {
            var gaps = MarkingDominance.Gaps(markings, p);
            if (gaps.Count > 0) return (null, PStep.Invalid($"The markings must cover those of the program and of what the row belongs to. {MarkingDominance.Describe(gaps)}"));
        }
        return (markings, null);
    }

    private static async Task InsertRowAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, Markings markings, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        await using var cmd = ProgramStore.Cmd(conn, tx, sql);
        bind(cmd);
        foreach (var (k, v) in MarkingColumns.Parameters(markings)) cmd.Parameters.AddWithValue(k, v);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- reads

    internal static ResourceCatalog BuildCatalog(Subject subject, PHead head, ResData d, DateOnly today)
    {
        var cost = CanViewCost(subject);
        var poolKey = d.Pools.ToDictionary(p => p.Id);
        var roleById = d.Roles.ToDictionary(r => r.Id);
        var pools = d.Pools.Select(p => new PoolRow(p.Key, p.Name, p.Description, d.Roles.Count(r => r.PoolId == p.Id), d.Resources.Count(x => roleById.TryGetValue(x.RoleId, out var rr) && rr.PoolId == p.Id), p.Markings)).ToList();
        var roles = d.Roles.Select(r =>
        {
            d.Rates.TryGetValue(r.Id, out var steps);
            var current = steps is { Count: > 0 } ? ResourceMath.RateAt(steps, today) ?? steps[0].Rate : (decimal?)null;
            return new RoleRow(r.Key, r.Name, poolKey.TryGetValue(r.PoolId, out var p) ? p.Key : "", p?.Name ?? "", cost ? current : null,
                cost && steps is not null ? [.. steps.Select(s => new RateRow(s.From, s.Rate))] : [], !cost, d.Resources.Count(x => x.RoleId == r.Id), r.Markings);
        }).ToList();
        var resources = d.Resources.Select(r =>
        {
            roleById.TryGetValue(r.RoleId, out var role);
            return new ResourceRow(r.Key, r.Name, role?.Key ?? "", role?.Name ?? "", role is not null && poolKey.TryGetValue(role.PoolId, out var p) ? p.Key : "", r.Capacity, r.From, r.To, r.Active,
                AccessPolicy.Evaluate(subject, r.Markings, PlmAction.Modify).Permitted, r.Markings);
        }).ToList();
        var byResource = d.Resources.ToDictionary(r => r.Id);
        var assignments = d.Assignments.Select(a =>
        {
            d.Items.TryGetValue(a.ItemId, out var item);
            byResource.TryGetValue(a.ResourceId, out var res);
            return new AssignmentRow(a.Key, item?.Key, item?.Title, res?.Key ?? "", res?.Name ?? "", a.Start, a.End, a.HoursPerDay, ResourceMath.WorkingDays(a.Start, a.End).Count() * a.HoursPerDay,
                AccessPolicy.Evaluate(subject, a.Markings, PlmAction.Modify).Permitted, a.Markings);
        }).OrderBy(a => a.ItemKey, StringComparer.Ordinal).ThenBy(a => a.Key, StringComparer.Ordinal).ToList();
        return new ResourceCatalog(head.Code, pools, roles, resources, assignments, AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Modify).Permitted, cost,
            Markings.AggregateBanner(d.AllMarkings.Append(head.Markings)));
    }

    /// <summary>Pools, roles, resources and assignments of a program that the caller may read. Rates are in the answer only for a holder of the program cost permission.</summary>
    public Task<Outcome<ResourceCatalog>> GetResourcesAsync(Subject subject, string code, string? corr, CancellationToken ct = default) =>
        ReadAsync(subject, code, "program.resources", corr, async (conn, tx, head) => BuildCatalog(subject, head, await ProgramResourceStore.LoadAsync(conn, tx, subject, head, ct), Today),
            c => [("resources", ProgramStore.Int(c.Resources.Count)), ("assignments", ProgramStore.Int(c.Assignments.Count))], ct);

    internal static LoadView BuildLoad(PHead head, ResData d, DateOnly? from, DateOnly? to, DateOnly today)
    {
        var resources = ProgramResourceStore.MathResources(d);
        var assignments = ProgramResourceStore.MathAssignments(d);
        var lo = from ?? (assignments.Count > 0 ? assignments.Min(a => a.Start) : today);
        var hi = to ?? (assignments.Count > 0 ? assignments.Max(a => a.End) : lo.AddDays(83));
        if (hi < lo) hi = lo;
        lo = ResourceMath.WeekOf(lo);
        if (hi > lo.AddDays(MaxLoadWeeks * 7 - 1)) hi = lo.AddDays(MaxLoadWeeks * 7 - 1);
        var weeks = new List<DateOnly>();
        for (var w = lo; w <= hi; w = w.AddDays(7)) weeks.Add(w);
        var cells = ResourceMath.Weekly(resources, assignments, lo, hi);
        var roleName = d.Roles.ToDictionary(r => r.Id, r => r.Name);
        var rows = d.Resources.Select(r =>
        {
            var wk = weeks.Select(w => cells.TryGetValue((r.Id, w), out var c) ? new LoadCellRow(w, c.Allocated, c.Capacity) : new LoadCellRow(w, 0, 0)).ToList();
            return new LoadRow(r.Key, r.Name, roleName.GetValueOrDefault(r.RoleId, ""), wk, wk.Sum(c => c.Allocated), wk.Sum(c => c.Capacity));
        }).ToList();
        var names = resources.ToDictionary(r => r.Id);
        var over = ResourceMath.OverAllocations(resources, assignments).Select(o => new OverAllocationRow(names[o.ResourceId].Key, names[o.ResourceId].Name, o.From, o.To, o.PeakHours, o.Capacity, o.ExcessHours, o.AssignmentKeys)).ToList();
        return new LoadView(head.Code, lo, hi, weeks, rows, over, Markings.AggregateBanner(d.AllMarkings.Append(head.Markings)));
    }

    /// <summary>
    /// Allocated hours against capacity by resource and week, and the periods in which a resource carries more than it can (over-allocation). Computed over the pools, resources and
    /// assignments the caller may read; hours only, never a rate or an amount. Weeks start on Monday; at most <see cref="MaxLoadWeeks"/> weeks are returned.
    /// </summary>
    public Task<Outcome<LoadView>> GetLoadAsync(Subject subject, string code, DateOnly? from, DateOnly? to, string? corr, CancellationToken ct = default) =>
        ReadAsync(subject, code, "program.load", corr, async (conn, tx, head) => BuildLoad(head, await ProgramResourceStore.LoadAsync(conn, tx, subject, head, ct), from, to, Today),
            v => [("resources", ProgramStore.Int(v.Rows.Count)), ("over", ProgramStore.Int(v.OverAllocations.Count))], ct);

    // ---------------------------------------------------------------- pools and roles

    private Task<Outcome<T>> MutateCatalogAsync<T>(Subject subject, string code, string action, string? corr, Func<PHead, NpgsqlConnection, NpgsqlTransaction, DateTimeOffset, Task<PStep>> body, Func<ResourceCatalog, T?> pick, CancellationToken ct) where T : class =>
        MutateAsync<T>(subject, code, action, corr, PlmAction.Modify, false, body, async (conn, tx, head) => pick(BuildCatalog(subject, head, await ProgramResourceStore.LoadAsync(conn, tx, subject, head, ct), Today)), ct);

    public Task<Outcome<PoolRow>> AddPoolAsync(Subject subject, string code, AddPoolCommand cmd, string? corr, CancellationToken ct = default)
    {
        string? key = null;
        return MutateCatalogAsync(subject, code, "program.pool_add", corr, async (head, conn, tx, now) =>
        {
            if (NameError(cmd.Name, "Pool") is { } e) return PStep.Invalid(e);
            if ((cmd.Description?.Length ?? 0) > 1000) return PStep.Invalid("The description must be at most 1000 characters.");
            var (markings, error) = RowMarkings(head, cmd.Markings, head.Markings);
            if (error is not null) return error;
            if (RequireRow(subject, markings, PlmAction.Modify) is { } denied) return denied;
            if (await ExistsAsync(conn, tx, "SELECT 1 FROM prg.res_pool WHERE program_id = @p AND lower(name) = lower(@n)", head.Id, cmd.Name.Trim(), ct)) return PStep.Fail("A pool with that name already exists.");
            key = await ProgramResourceStore.NextKeyAsync(conn, tx, head.Id, "pool", "RP", 3, ct);
            await InsertRowAsync(conn, tx, $"INSERT INTO prg.res_pool (id, program_id, key, name, description, {MarkingColumns.InsertColumns}, created_at, created_by) VALUES (@id, @p, @key, @name, @desc, {MarkingColumns.InsertValues}, @at, @by)", markings!, c =>
            {
                c.Parameters.AddWithValue("id", Guid.CreateVersion7());
                c.Parameters.AddWithValue("p", head.Id);
                c.Parameters.AddWithValue("key", key);
                c.Parameters.AddWithValue("name", cmd.Name.Trim());
                c.Parameters.AddWithValue("desc", cmd.Description?.Trim() ?? "");
                c.Parameters.AddWithValue("at", now);
                c.Parameters.AddWithValue("by", ProgramStore.Who(subject));
            }, ct);
            return PStep.Done(("pool", key), ("markings", markings!.BannerText()));
        }, c => c.Pools.FirstOrDefault(p => p.Key == key), ct);
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, Guid programId, string text, CancellationToken ct)
    {
        await using var cmd = ProgramStore.Cmd(conn, tx, sql);
        cmd.Parameters.AddWithValue("p", programId);
        cmd.Parameters.AddWithValue("n", text);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static string? RateError(decimal? rate) => rate is { } r && (r < 0 || r > 100_000 || decimal.Round(r, 2) != r) ? "The hourly rate must be from 0 to 100000 with at most two decimals." : null;

    public Task<Outcome<RoleRow>> AddRoleAsync(Subject subject, string code, AddRoleCommand cmd, string? corr, CancellationToken ct = default)
    {
        string? key = null;
        return MutateCatalogAsync(subject, code, "program.role_add", corr, async (head, conn, tx, now) =>
        {
            if (NameError(cmd.Name, "Role") is { } e) return PStep.Invalid(e);
            if (RateError(cmd.HourlyRate) is { } re) return PStep.Invalid(re);
            if (cmd.HourlyRate is not null && !CanViewCost(subject)) return PStep.Forbid(CostRoleMissing);
            var pool = await ProgramResourceStore.FindPoolAsync(conn, tx, head.Id, cmd.PoolKey, ct);
            if (RequireRow(subject, pool?.Markings, PlmAction.Read) is not null) return PStep.Invalid($"Pool '{cmd.PoolKey}' was not found.");
            var (markings, error) = RowMarkings(head, cmd.Markings, pool!.Markings, pool.Markings);
            if (error is not null) return error;
            if (RequireRow(subject, markings, PlmAction.Modify) is { } denied) return denied;
            if (await ExistsAsync(conn, tx, "SELECT 1 FROM prg.res_role WHERE program_id = @p AND lower(name) = lower(@n)", head.Id, cmd.Name.Trim(), ct)) return PStep.Fail("A role with that name already exists.");
            key = await ProgramResourceStore.NextKeyAsync(conn, tx, head.Id, "role", "RR", 3, ct);
            var id = Guid.CreateVersion7();
            await InsertRowAsync(conn, tx, $"INSERT INTO prg.res_role (id, program_id, pool_id, key, name, {MarkingColumns.InsertColumns}, created_at, created_by) VALUES (@id, @p, @pool, @key, @name, {MarkingColumns.InsertValues}, @at, @by)", markings!, c =>
            {
                c.Parameters.AddWithValue("id", id);
                c.Parameters.AddWithValue("p", head.Id);
                c.Parameters.AddWithValue("pool", pool.Id);
                c.Parameters.AddWithValue("key", key);
                c.Parameters.AddWithValue("name", cmd.Name.Trim());
                c.Parameters.AddWithValue("at", now);
                c.Parameters.AddWithValue("by", ProgramStore.Who(subject));
            }, ct);
            if (cmd.HourlyRate is { } rate) await InsertRateAsync(conn, tx, id, cmd.RateFrom ?? ProgramResourceStore.RateEpoch, rate, now, subject, ct);
            return PStep.Done(("role", key), ("pool", pool.Key), ("rated", cmd.HourlyRate is null ? "no" : "yes"));
        }, c => c.Roles.FirstOrDefault(r => r.Key == key), ct);
    }

    private static async Task InsertRateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid roleId, DateOnly from, decimal rate, DateTimeOffset now, Subject subject, CancellationToken ct)
    {
        await using var cmd = ProgramStore.Cmd(conn, tx, "INSERT INTO prg.role_rate (id, role_id, effective_from, hourly_rate, created_at, created_by) VALUES (@id, @r, @from, @rate, @at, @by)");
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("r", roleId);
        cmd.Parameters.AddWithValue("from", from.ToDateTime(TimeOnly.MinValue));
        cmd.Parameters.AddWithValue("rate", rate);
        cmd.Parameters.AddWithValue("at", now);
        cmd.Parameters.AddWithValue("by", ProgramStore.Who(subject));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Sets the hourly rate of a role from a day on (a new row of the rate history; earlier rows stay). Needs the program cost permission.</summary>
    public Task<Outcome<RoleRow>> SetRoleRateAsync(Subject subject, string code, string roleKey, SetRoleRateCommand cmd, string? corr, CancellationToken ct = default) =>
        MutateCatalogAsync<RoleRow>(subject, code, "program.role_rate_set", corr, async (head, conn, tx, now) =>
        {
            if (!CanViewCost(subject)) return PStep.Forbid(CostRoleMissing);
            if (RateError(cmd.HourlyRate) is { } re) return PStep.Invalid(re);
            var role = await ProgramResourceStore.FindRoleAsync(conn, tx, head.Id, roleKey, ct);
            if (RequireRow(subject, role?.Markings, PlmAction.Modify) is { } denied) return denied;
            if (await ExistsAsync(conn, tx, "SELECT 1 FROM prg.role_rate WHERE role_id = @p AND effective_from = @n::date", role!.Id, cmd.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct))
                return PStep.Fail("A rate already starts on that day. Rates are never changed; choose another day.");
            await InsertRateAsync(conn, tx, role.Id, cmd.EffectiveFrom, cmd.HourlyRate, now, subject, ct);
            return PStep.Done(("role", role.Key), ("from", cmd.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }, c => c.Roles.FirstOrDefault(r => string.Equals(r.Key, roleKey, StringComparison.OrdinalIgnoreCase)), ct);

    // ---------------------------------------------------------------- resources

    private static string? ResourceFieldError(decimal capacity, DateOnly? from, DateOnly? to) =>
        capacity <= 0 || capacity > 24 || decimal.Round(capacity, 2) != capacity ? "The capacity must be above 0 and at most 24 hours a day." :
        from is { } f && to is { } t && f > t ? "The availability must not end before it starts." : null;

    public Task<Outcome<ResourceRow>> AddResourceAsync(Subject subject, string code, AddResourceCommand cmd, string? corr, CancellationToken ct = default)
    {
        string? key = null;
        return MutateCatalogAsync(subject, code, "program.resource_add", corr, async (head, conn, tx, now) =>
        {
            if (NameError(cmd.Name, "Resource") is { } e) return PStep.Invalid(e);
            if (ResourceFieldError(cmd.CapacityHoursPerDay, cmd.AvailableFrom, cmd.AvailableTo) is { } fe) return PStep.Invalid(fe);
            var role = await ProgramResourceStore.FindRoleAsync(conn, tx, head.Id, cmd.RoleKey, ct);
            if (RequireRow(subject, role?.Markings, PlmAction.Read) is not null) return PStep.Invalid($"Role '{cmd.RoleKey}' was not found.");
            var (markings, error) = RowMarkings(head, cmd.Markings, role!.Markings, role.Markings);
            if (error is not null) return error;
            if (RequireRow(subject, markings, PlmAction.Modify) is { } denied) return denied;
            if (await ExistsAsync(conn, tx, "SELECT 1 FROM prg.resource WHERE program_id = @p AND lower(name) = lower(@n)", head.Id, cmd.Name.Trim(), ct)) return PStep.Fail("A resource with that name already exists.");
            key = await ProgramResourceStore.NextKeyAsync(conn, tx, head.Id, "resource", "RS", 3, ct);
            await InsertRowAsync(conn, tx,
                $"INSERT INTO prg.resource (id, program_id, role_id, key, name, capacity_hours_per_day, available_from, available_to, {MarkingColumns.InsertColumns}, created_at, created_by, updated_at, updated_by) VALUES (@id, @p, @role, @key, @name, @cap, @from, @to, {MarkingColumns.InsertValues}, @at, @by, @at, @by)", markings!, c =>
            {
                c.Parameters.AddWithValue("id", Guid.CreateVersion7());
                c.Parameters.AddWithValue("p", head.Id);
                c.Parameters.AddWithValue("role", role.Id);
                c.Parameters.AddWithValue("key", key);
                c.Parameters.AddWithValue("name", cmd.Name.Trim());
                c.Parameters.AddWithValue("cap", cmd.CapacityHoursPerDay);
                c.Parameters.AddWithValue("from", ProgramStore.OptDate(cmd.AvailableFrom));
                c.Parameters.AddWithValue("to", ProgramStore.OptDate(cmd.AvailableTo));
                c.Parameters.AddWithValue("at", now);
                c.Parameters.AddWithValue("by", ProgramStore.Who(subject));
            }, ct);
            return PStep.Done(("resource", key), ("role", role.Key), ("capacity", ResourceMath.Hours(cmd.CapacityHoursPerDay)));
        }, c => c.Resources.FirstOrDefault(r => r.Key == key), ct);
    }

    public Task<Outcome<ResourceRow>> UpdateResourceAsync(Subject subject, string code, string resourceKey, UpdateResourceCommand cmd, string? corr, CancellationToken ct = default) =>
        MutateCatalogAsync<ResourceRow>(subject, code, "program.resource_update", corr, async (head, conn, tx, now) =>
        {
            if (NameError(cmd.Name, "Resource") is { } e) return PStep.Invalid(e);
            if (ResourceFieldError(cmd.CapacityHoursPerDay, cmd.AvailableFrom, cmd.AvailableTo) is { } fe) return PStep.Invalid(fe);
            var res = await ProgramResourceStore.FindResourceAsync(conn, tx, head.Id, resourceKey, true, ct);
            if (RequireRow(subject, res?.Markings, PlmAction.Modify) is { } denied) return denied;
            var role = await ProgramResourceStore.FindRoleAsync(conn, tx, head.Id, cmd.RoleKey, ct);
            if (RequireRow(subject, role?.Markings, PlmAction.Read) is not null) return PStep.Invalid($"Role '{cmd.RoleKey}' was not found.");
            var gaps = MarkingDominance.Gaps(res!.Markings, role!.Markings);
            if (gaps.Count > 0) return PStep.Invalid($"The markings of the resource must cover those of its role. {MarkingDominance.Describe(gaps)}");
            if (await ExistsAsync(conn, tx, $"SELECT 1 FROM prg.resource WHERE program_id = @p AND lower(name) = lower(@n) AND id <> '{res.Id}'", head.Id, cmd.Name.Trim(), ct)) return PStep.Fail("A resource with that name already exists.");
            await using var up = ProgramStore.Cmd(conn, tx, "UPDATE prg.resource SET name = @name, role_id = @role, capacity_hours_per_day = @cap, available_from = @from, available_to = @to, active = @active, updated_at = @at, updated_by = @by WHERE id = @id");
            up.Parameters.AddWithValue("name", cmd.Name.Trim());
            up.Parameters.AddWithValue("role", role.Id);
            up.Parameters.AddWithValue("cap", cmd.CapacityHoursPerDay);
            up.Parameters.AddWithValue("from", ProgramStore.OptDate(cmd.AvailableFrom));
            up.Parameters.AddWithValue("to", ProgramStore.OptDate(cmd.AvailableTo));
            up.Parameters.AddWithValue("active", cmd.Active);
            up.Parameters.AddWithValue("at", now);
            up.Parameters.AddWithValue("by", ProgramStore.Who(subject));
            up.Parameters.AddWithValue("id", res.Id);
            await up.ExecuteNonQueryAsync(ct);
            return PStep.Done(("resource", res.Key), ("role", role.Key), ("capacity", ResourceMath.Hours(cmd.CapacityHoursPerDay)), ("active", cmd.Active ? "yes" : "no"));
        }, c => c.Resources.FirstOrDefault(r => string.Equals(r.Key, resourceKey, StringComparison.OrdinalIgnoreCase)), ct);

    // ---------------------------------------------------------------- assignments

    private static string? AssignmentError(DateOnly start, DateOnly end, decimal hoursPerDay) =>
        start > end ? "The assignment must not end before it starts." :
        end.DayNumber - start.DayNumber > 3660 ? "An assignment covers at most ten years." :
        hoursPerDay <= 0 || hoursPerDay > 24 || decimal.Round(hoursPerDay, 2) != hoursPerDay ? "Hours per day must be above 0 and at most 24." : null;

    public Task<Outcome<AssignmentRow>> AddAssignmentAsync(Subject subject, string code, AddAssignmentCommand cmd, string? corr, CancellationToken ct = default)
    {
        string? key = null;
        return MutateCatalogAsync(subject, code, "program.assignment_add", corr, async (head, conn, tx, now) =>
        {
            if (AssignmentError(cmd.Start, cmd.End, cmd.HoursPerDay) is { } ae) return PStep.Invalid(ae);
            var item = await ProgramResourceStore.FindItemAsync(conn, tx, head.Id, cmd.ItemKey, ct);
            if (RequireRow(subject, item?.Markings, PlmAction.Read) is not null) return PStep.Invalid($"Item '{cmd.ItemKey}' was not found.");
            if (item!.Kind != ProgramKinds.Task) return PStep.Invalid("Resources are assigned to tasks, not to phases or milestones.");
            if (!ItemStatuses.Open(item.Status)) return PStep.Invalid($"{item.Key} is {ItemStatuses.Label(item.Status).ToLowerInvariant()}; a resource cannot be assigned to it.");
            var res = await ProgramResourceStore.FindResourceAsync(conn, tx, head.Id, cmd.ResourceKey, false, ct);
            if (RequireRow(subject, res?.Markings, PlmAction.Read) is not null) return PStep.Invalid($"Resource '{cmd.ResourceKey}' was not found.");
            if (!res!.Active) return PStep.Invalid($"{res.Key} is not active.");
            var markings = ProgramResourceStore.Cover(item.Markings, res.Markings);
            if (markings is null) return PStep.Invalid("The markings of the task and of the resource cannot be combined: neither covers the other.");
            if (RequireRow(subject, markings, PlmAction.Modify) is { } denied) return denied;
            key = await ProgramResourceStore.NextKeyAsync(conn, tx, head.Id, "assignment", "AS", 3, ct);
            await InsertRowAsync(conn, tx,
                $"INSERT INTO prg.assignment (id, program_id, key, item_id, resource_id, start_date, end_date, hours_per_day, {MarkingColumns.InsertColumns}, created_at, created_by, updated_at, updated_by) VALUES (@id, @p, @key, @item, @res, @start, @end, @hpd, {MarkingColumns.InsertValues}, @at, @by, @at, @by)", markings, c =>
            {
                c.Parameters.AddWithValue("id", Guid.CreateVersion7());
                c.Parameters.AddWithValue("p", head.Id);
                c.Parameters.AddWithValue("key", key);
                c.Parameters.AddWithValue("item", item.Id);
                c.Parameters.AddWithValue("res", res.Id);
                c.Parameters.AddWithValue("start", cmd.Start.ToDateTime(TimeOnly.MinValue));
                c.Parameters.AddWithValue("end", cmd.End.ToDateTime(TimeOnly.MinValue));
                c.Parameters.AddWithValue("hpd", cmd.HoursPerDay);
                c.Parameters.AddWithValue("at", now);
                c.Parameters.AddWithValue("by", ProgramStore.Who(subject));
            }, ct);
            return PStep.Done(("assignment", key), ("item", item.Key), ("resource", res.Key), ("hours", ResourceMath.Hours(ResourceMath.WorkingDays(cmd.Start, cmd.End).Count() * cmd.HoursPerDay)));
        }, c => c.Assignments.FirstOrDefault(a => a.Key == key), ct);
    }

    public Task<Outcome<AssignmentRow>> UpdateAssignmentAsync(Subject subject, string code, string assignmentKey, UpdateAssignmentCommand cmd, string? corr, CancellationToken ct = default) =>
        MutateCatalogAsync<AssignmentRow>(subject, code, "program.assignment_update", corr, async (head, conn, tx, now) =>
        {
            if (AssignmentError(cmd.Start, cmd.End, cmd.HoursPerDay) is { } ae) return PStep.Invalid(ae);
            var a = await ProgramResourceStore.FindAssignmentAsync(conn, tx, head.Id, assignmentKey, ct);
            if (RequireRow(subject, a?.Markings, PlmAction.Modify) is { } denied) return denied;
            await using var up = ProgramStore.Cmd(conn, tx, "UPDATE prg.assignment SET start_date = @start, end_date = @end, hours_per_day = @hpd, updated_at = @at, updated_by = @by WHERE id = @id");
            up.Parameters.AddWithValue("start", cmd.Start.ToDateTime(TimeOnly.MinValue));
            up.Parameters.AddWithValue("end", cmd.End.ToDateTime(TimeOnly.MinValue));
            up.Parameters.AddWithValue("hpd", cmd.HoursPerDay);
            up.Parameters.AddWithValue("at", now);
            up.Parameters.AddWithValue("by", ProgramStore.Who(subject));
            up.Parameters.AddWithValue("id", a!.Id);
            await up.ExecuteNonQueryAsync(ct);
            return PStep.Done(("assignment", a.Key), ("hours", ResourceMath.Hours(ResourceMath.WorkingDays(cmd.Start, cmd.End).Count() * cmd.HoursPerDay)));
        }, c => c.Assignments.FirstOrDefault(x => string.Equals(x.Key, assignmentKey, StringComparison.OrdinalIgnoreCase)), ct);

    /// <summary>Removes an assignment from the plan (the row stays, marked removed, so the audit trail keeps its subject).</summary>
    public Task<Outcome<ResourceCatalog>> RemoveAssignmentAsync(Subject subject, string code, string assignmentKey, string? corr, CancellationToken ct = default) =>
        MutateCatalogAsync<ResourceCatalog>(subject, code, "program.assignment_remove", corr, async (head, conn, tx, now) =>
        {
            var a = await ProgramResourceStore.FindAssignmentAsync(conn, tx, head.Id, assignmentKey, ct);
            if (RequireRow(subject, a?.Markings, PlmAction.Modify) is { } denied) return denied;
            await using var up = ProgramStore.Cmd(conn, tx, "UPDATE prg.assignment SET removed_at = @at, removed_by = @by, updated_at = @at, updated_by = @by WHERE id = @id");
            up.Parameters.AddWithValue("at", now);
            up.Parameters.AddWithValue("by", ProgramStore.Who(subject));
            up.Parameters.AddWithValue("id", a!.Id);
            await up.ExecuteNonQueryAsync(ct);
            return PStep.Done(("assignment", a.Key));
        }, c => c, ct);
}
