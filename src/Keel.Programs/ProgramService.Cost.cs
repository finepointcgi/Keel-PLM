using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Programs;

internal sealed record BudR(Guid Id, string Key, string CostType, DateOnly Period, decimal Amount, Markings Markings);
internal sealed record ActR(Guid Id, string Key, string CostType, DateOnly On, decimal Amount, decimal? Hours, Guid? ResourceId, Guid? ItemId, Guid? ReversesId, string Note, string By, Markings Markings);
internal sealed record MatR(Guid Id, string Key, string Part, decimal Quantity, DateOnly Needed, string Note, Markings Markings);

public sealed partial class ProgramService
{
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- loading (every list applies the read filter)

    private static async Task<List<BudR>> LoadBudgetAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, PHead head, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "b");
        var rows = new List<BudR>();
        await using var cmd = new NpgsqlCommand($"SELECT b.id, b.key, b.cost_type, b.period, b.amount, {MarkingColumns.Select("b")} FROM prg.budget_line b WHERE b.program_id = @pid AND {filter.Sql} ORDER BY b.period, b.cost_type", conn, tx);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("pid", head.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(new BudR(r.GetGuid(0), r.GetString(1), r.GetString(2), ProgramStore.Date(r, 3), r.GetDecimal(4), MarkingColumns.Read(r, 5)));
        return rows;
    }

    private static async Task<List<ActR>> LoadActualsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, PHead head, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "x");
        var rows = new List<ActR>();
        await using var cmd = new NpgsqlCommand(
            $"SELECT x.id, x.key, x.cost_type, x.incurred_on, x.amount, x.hours, x.resource_id, x.item_id, x.reverses_id, x.note, x.created_by, {MarkingColumns.Select("x")} FROM prg.actual_cost x WHERE x.program_id = @pid AND {filter.Sql} ORDER BY x.incurred_on, x.key", conn, tx);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("pid", head.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new ActR(r.GetGuid(0), r.GetString(1), r.GetString(2), ProgramStore.Date(r, 3), r.GetDecimal(4), r.IsDBNull(5) ? null : r.GetDecimal(5), r.IsDBNull(6) ? null : r.GetGuid(6), r.IsDBNull(7) ? null : r.GetGuid(7),
                r.IsDBNull(8) ? null : r.GetGuid(8), r.GetString(9), r.GetString(10), MarkingColumns.Read(r, 11)));
        return rows;
    }

    private static async Task<List<MatR>> LoadMaterialAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, PHead head, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "m");
        var rows = new List<MatR>();
        await using var cmd = new NpgsqlCommand(
            $"SELECT m.id, m.key, m.part_number, m.quantity, m.needed_on, m.note, {MarkingColumns.Select("m")} FROM prg.material_line m WHERE m.program_id = @pid AND m.removed_at IS NULL AND {filter.Sql} ORDER BY m.needed_on, m.key", conn, tx);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("pid", head.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(new MatR(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), ProgramStore.Date(r, 4), r.GetString(5), MarkingColumns.Read(r, 6)));
        return rows;
    }

    // ---------------------------------------------------------------- the cost view

    private async Task<CostView> BuildCostAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, PHead head, DateOnly asOf, string? corr, CancellationToken ct)
    {
        var d = await ProgramResourceStore.LoadAsync(conn, tx, subject, head, ct);
        var budget = await LoadBudgetAsync(conn, tx, subject, head, ct);
        var actuals = await LoadActualsAsync(conn, tx, subject, head, ct);
        var lines = await LoadMaterialAsync(conn, tx, subject, head, ct);
        var banners = new List<Markings> { head.Markings };
        banners.AddRange(d.AllMarkings);
        banners.AddRange(budget.Select(b => b.Markings));
        banners.AddRange(actuals.Select(a => a.Markings));
        banners.AddRange(lines.Select(l => l.Markings));

        var materialRows = new List<MaterialLineRow>();
        var mathMaterial = new List<MathMaterial>();
        foreach (var l in lines)
        {
            MaterialEstimate? est = null;
            string? problem = null;
            if (_materials is null) problem = "No material cost source is configured.";
            else
            {
                var o = await _materials.EstimateAsync(subject, l.Part, l.Quantity, asOf, corr, ct);
                if (o.IsOk) est = o.Value;
                else problem = o.Kind == OutcomeKind.NotFound ? "The part was not found or you may not read it." : o.Errors.Count > 0 ? o.Errors[0] : "The estimate could not be made.";
            }
            if (est is not null) banners.AddRange(est.Markings);
            var total = est is not null && est.PricedLines > 0 ? est.Total : (decimal?)null;
            mathMaterial.Add(new MathMaterial(l.Needed, total));
            materialRows.Add(new MaterialLineRow(l.Key, l.Part, est?.PartName, l.Quantity, l.Needed, l.Note, total, est?.PricedLines ?? 0, est?.UnpricedLines ?? 0, est?.RestrictedLines ?? 0,
                est?.Lines ?? [], problem ?? (est is not null && est.PricedLines == 0 ? "No part has a usable price." : null), l.Markings));
        }

        var items = d.Items.ToDictionary(kv => kv.Key, kv => new MathItem(kv.Key, kv.Value.Status, kv.Value.Percent));
        var rates = d.Rates.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<RateStep>)kv.Value);
        var resourceRole = d.Resources.ToDictionary(r => r.Id, r => r.RoleId);
        var math = ResourceMath.Compute(asOf, ProgramResourceStore.MathResources(d), ProgramResourceStore.MathAssignments(d), items, rates, resourceRole,
            [.. budget.Select(b => new MathBudget(b.CostType, b.Period, b.Amount))], [.. actuals.Select(a => new MathActual(a.CostType, a.On, a.Amount))], mathMaterial);

        var roleById = d.Roles.ToDictionary(r => r.Id);
        var poolById = d.Pools.ToDictionary(p => p.Id);
        var roleRows = math.Roles.Where(r => roleById.ContainsKey(r.RoleId)).Select(r =>
        {
            var role = roleById[r.RoleId];
            return new RoleCostRow(role.Key, role.Name, poolById.TryGetValue(role.PoolId, out var p) ? p.Name : "", r.Hours, r.PlannedCost, r.RemainingHours, r.Etc);
        }).OrderBy(r => r.RoleKey, StringComparer.Ordinal).ToList();

        var resKey = d.Resources.ToDictionary(r => r.Id, r => r.Key);
        var actKey = actuals.ToDictionary(a => a.Id, a => a.Key);
        var actualRows = actuals.OrderByDescending(a => a.On).ThenByDescending(a => a.Key, StringComparer.Ordinal).Take(300).Select(a =>
            new ActualRow(a.Key, a.CostType, a.On, a.Amount, a.Hours, a.ResourceId is { } rid ? resKey.GetValueOrDefault(rid) : null, a.ItemId is { } iid && d.Items.TryGetValue(iid, out var it) ? it.Key : null,
                a.Note, a.ReversesId is { } rv ? actKey.GetValueOrDefault(rv) : null, a.By, a.Markings)).ToList();
        var withoutRate = d.Resources.Count(r => r.Active && (!d.Rates.TryGetValue(r.RoleId, out var s) || s.Count == 0));
        var over = ResourceMath.OverAllocations(ProgramResourceStore.MathResources(d), ProgramResourceStore.MathAssignments(d)).Select(o => o.ResourceId).Distinct().Count();
        return new CostView(head.Code, asOf, CostTypes.Currency, math.Months, math.ByType, math.Total, roleRows, [.. budget.Select(b => new BudgetRow(b.Key, b.CostType, b.Period, b.Amount, b.Markings))], actualRows, materialRows,
            math.UnpricedHours, withoutRate, over, AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Modify).Permitted, Markings.AggregateBanner(banners));
    }

    /// <summary>
    /// Budget against actual against estimate to complete for one program (ADR-0049). Only a holder of the program cost permission may ask: anybody else is refused with
    /// <see cref="CostRoleMissing"/> (audited) after the usual 404 for a program they cannot read. Every figure is computed over the rows the caller may read and the material
    /// estimate over the structure and price lists the caller may read; the audit event carries counts, never amounts.
    /// </summary>
    public async Task<Outcome<CostView>> GetCostAsync(Subject subject, string code, DateOnly? asOf, string? corr, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await ProgramStore.LoadProgramAsync(conn, tx, code, false, ct);
        if (head is null) return Outcome<CostView>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await _audit.AppendAsync(tx, ProgramStore.Entry(subject, "program.cost", AuditOutcome.Denied, ProgramStore.ObjectType, head.Code, corr, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<CostView>.NotFound();
        }
        if (!CanViewCost(subject))
        {
            await _audit.AppendAsync(tx, ProgramStore.Entry(subject, "program.cost", AuditOutcome.Denied, ProgramStore.ObjectType, head.Code, corr, ("reasons", CostRoleMissing)), ct);
            await tx.CommitAsync(ct);
            return Outcome<CostView>.Forbidden([CostRoleMissing]);
        }
        var view = await BuildCostAsync(conn, tx, subject, head, asOf ?? Today, corr, ct);
        await _audit.AppendAsync(tx, ProgramStore.Entry(subject, "program.cost", AuditOutcome.Success, ProgramStore.ObjectType, head.Code, corr,
            ("months", ProgramStore.Int(view.Months.Count)), ("material", ProgramStore.Int(view.Material.Count)), ("actuals", ProgramStore.Int(view.Actuals.Count))), ct);
        await tx.CommitAsync(ct);
        return Outcome<CostView>.Ok(view);
    }

    /// <summary>Writes the audit event of an export of the cost data (the file itself is built by the host from <see cref="GetCostAsync"/> and its siblings; this records that it was taken).</summary>
    public async Task RecordCostExportAsync(Subject subject, string code, string format, int rows, string? corr, CancellationToken ct = default)
    {
        await _audit.AppendAsync(ProgramStore.Entry(subject, "program.cost_export", AuditOutcome.Success, ProgramStore.ObjectType, code, corr, ("format", format), ("rows", ProgramStore.Int(rows))), ct);
    }

    // ---------------------------------------------------------------- the cost plan: budget, actuals, material lines

    private Task<Outcome<T>> MutateCostAsync<T>(Subject subject, string code, string action, string? corr, Func<PHead, NpgsqlConnection, NpgsqlTransaction, DateTimeOffset, Task<PStep>> body,
        Func<NpgsqlConnection, NpgsqlTransaction, Subject, PHead, Task<T?>> load, CancellationToken ct, bool allowClosed = false) where T : class =>
        MutateAsync<T>(subject, code, action, corr, PlmAction.Modify, allowClosed, async (head, conn, tx, now) => CanViewCost(subject) ? await body(head, conn, tx, now) : PStep.Forbid(CostRoleMissing),
            (conn, tx, head) => load(conn, tx, subject, head), ct);

    /// <summary>Sets the budget of one cost type for one month (any day of the month names it). Every change of an amount is written to the budget log.</summary>
    public Task<Outcome<BudgetRow>> SetBudgetAsync(Subject subject, string code, SetBudgetCommand cmd, string? corr, CancellationToken ct = default)
    {
        string? key = null;
        return MutateCostAsync(subject, code, "program.budget_set", corr, async (head, conn, tx, now) =>
        {
            if (!CostTypes.All.Contains(cmd.CostType)) return PStep.Invalid($"The cost type must be one of: {string.Join(", ", CostTypes.All)}.");
            if (cmd.Amount < 0 || cmd.Amount > 100_000_000_000m || decimal.Round(cmd.Amount, 2) != cmd.Amount) return PStep.Invalid("The amount must be from 0 to 100000000000 with at most two decimals.");
            var period = ResourceMath.MonthOf(cmd.Period);
            Guid? existing = null;
            decimal? old = null;
            await using (var q = ProgramStore.Cmd(conn, tx, "SELECT id, key, amount FROM prg.budget_line WHERE program_id = @p AND cost_type = @t AND period = @d FOR UPDATE"))
            {
                q.Parameters.AddWithValue("p", head.Id);
                q.Parameters.AddWithValue("t", cmd.CostType);
                q.Parameters.AddWithValue("d", period.ToDateTime(TimeOnly.MinValue));
                await using var r = await q.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) { existing = r.GetGuid(0); key = r.GetString(1); old = r.GetDecimal(2); }
            }
            if (existing is { } id)
            {
                if (old == cmd.Amount) return PStep.Fail("The budget already has that amount.");
                await using var up = ProgramStore.Cmd(conn, tx, "UPDATE prg.budget_line SET amount = @a, updated_at = @at, updated_by = @by WHERE id = @id");
                up.Parameters.AddWithValue("a", cmd.Amount);
                up.Parameters.AddWithValue("at", now);
                up.Parameters.AddWithValue("by", ProgramStore.Who(subject));
                up.Parameters.AddWithValue("id", id);
                await up.ExecuteNonQueryAsync(ct);
            }
            else
            {
                id = Guid.CreateVersion7();
                key = await ProgramResourceStore.NextKeyAsync(conn, tx, head.Id, "budget", "BL", 3, ct);
                await InsertRowAsync(conn, tx, $"INSERT INTO prg.budget_line (id, program_id, key, cost_type, period, amount, {MarkingColumns.InsertColumns}, created_at, created_by, updated_at, updated_by) VALUES (@id, @p, @key, @t, @d, @a, {MarkingColumns.InsertValues}, @at, @by, @at, @by)", head.Markings, c =>
                {
                    c.Parameters.AddWithValue("id", id);
                    c.Parameters.AddWithValue("p", head.Id);
                    c.Parameters.AddWithValue("key", key);
                    c.Parameters.AddWithValue("t", cmd.CostType);
                    c.Parameters.AddWithValue("d", period.ToDateTime(TimeOnly.MinValue));
                    c.Parameters.AddWithValue("a", cmd.Amount);
                    c.Parameters.AddWithValue("at", now);
                    c.Parameters.AddWithValue("by", ProgramStore.Who(subject));
                }, ct);
            }
            await using var log = ProgramStore.Cmd(conn, tx, "INSERT INTO prg.budget_log (line_id, old_amount, new_amount, at, by) VALUES (@l, @o, @n, @at, @by)");
            log.Parameters.AddWithValue("l", id);
            log.Parameters.AddWithValue("o", (object?)old ?? DBNull.Value);
            log.Parameters.AddWithValue("n", cmd.Amount);
            log.Parameters.AddWithValue("at", now);
            log.Parameters.AddWithValue("by", ProgramStore.Who(subject));
            await log.ExecuteNonQueryAsync(ct);
            return PStep.Done(("line", key), ("type", cmd.CostType), ("period", Iso(period)), ("change", old is null ? "new" : cmd.Amount > old ? "up" : "down"));
        }, async (conn, tx, s, head) => (await LoadBudgetAsync(conn, tx, s, head, ct)).Where(b => b.Key == key).Select(b => new BudgetRow(b.Key, b.CostType, b.Period, b.Amount, b.Markings)).FirstOrDefault(), ct);
    }

    /// <summary>
    /// Records an actual cost. Labor gives hours and a resource: the amount is the hours times the rate of the resource's role in force on the day (refused when there is none).
    /// Material and other costs give the amount. A recorded cost is never changed; a reversal names the entry it cancels and books the opposite amount.
    /// </summary>
    public Task<Outcome<ActualRow>> AddActualAsync(Subject subject, string code, AddActualCommand cmd, string? corr, CancellationToken ct = default)
    {
        string? key = null;
        return MutateCostAsync(subject, code, "program.actual_add", corr, async (head, conn, tx, now) =>
        {
            if (!CostTypes.All.Contains(cmd.CostType)) return PStep.Invalid($"The cost type must be one of: {string.Join(", ", CostTypes.All)}.");
            if ((cmd.Note?.Length ?? 0) > 200) return PStep.Invalid("The note must be at most 200 characters.");
            ResR? res = null;
            ItemLite? item = null;
            if (!string.IsNullOrWhiteSpace(cmd.ResourceKey))
            {
                res = await ProgramResourceStore.FindResourceAsync(conn, tx, head.Id, cmd.ResourceKey, false, ct);
                if (RequireRow(subject, res?.Markings, PlmAction.Read) is not null) return PStep.Invalid($"Resource '{cmd.ResourceKey}' was not found.");
            }
            if (!string.IsNullOrWhiteSpace(cmd.ItemKey))
            {
                item = await ProgramResourceStore.FindItemAsync(conn, tx, head.Id, cmd.ItemKey, ct);
                if (RequireRow(subject, item?.Markings, PlmAction.Read) is not null) return PStep.Invalid($"Item '{cmd.ItemKey}' was not found.");
            }
            decimal amount;
            decimal? hours = null;
            Guid? reverses = null;
            Guid? itemId = item?.Id;
            Markings? reversedMarkings = null;
            string costType = cmd.CostType;
            string? reversesKey = null;
            if (!string.IsNullOrWhiteSpace(cmd.ReversesKey))
            {
                var all = await LoadActualsAsync(conn, tx, subject, head, ct);
                var original = all.FirstOrDefault(a => string.Equals(a.Key, cmd.ReversesKey.Trim(), StringComparison.OrdinalIgnoreCase));
                if (original is null) return PStep.Invalid($"Entry '{cmd.ReversesKey}' was not found.");
                if (original.ReversesId is not null) return PStep.Invalid("A reversal cannot itself be reversed; book a new entry.");
                if (all.Any(a => a.ReversesId == original.Id)) return PStep.Fail($"{original.Key} was already reversed.");
                (amount, costType, reverses, reversesKey) = (-original.Amount, original.CostType, original.Id, original.Key);
                res = original.ResourceId is { } rid ? await FindResourceByIdAsync(conn, tx, rid, ct) : null;
                (itemId, reversedMarkings) = (original.ItemId, original.Markings);
            }
            else if (costType == CostTypes.Labor)
            {
                if (cmd.Hours is not { } h || h <= 0 || h > 100_000 || decimal.Round(h, 2) != h) return PStep.Invalid("Labor needs hours above 0 (at most two decimals).");
                if (res is null) return PStep.Invalid("Labor needs the resource that worked the hours.");
                var rate = await ProgramResourceStore.RateOnAsync(conn, tx, res.RoleId, cmd.IncurredOn, ct);
                if (rate is null) return PStep.Invalid($"The role of {res.Key} has no rate in force on {Iso(cmd.IncurredOn)}. Set the rate first.");
                hours = h;
                amount = decimal.Round(h * rate.Value, 2, MidpointRounding.AwayFromZero);
                if (amount == 0) return PStep.Invalid("The cost is zero at that rate.");
            }
            else
            {
                if (cmd.Amount is not { } a || a == 0 || Math.Abs(a) > 100_000_000_000m || decimal.Round(a, 2) != a) return PStep.Invalid("Give an amount other than zero (at most two decimals). A negative amount is a credit.");
                if (cmd.Hours is not null) return PStep.Invalid("Hours belong to labor entries only.");
                amount = a;
            }
            // A reversal carries the markings of the entry it cancels, so no reader sees one without the other.
            var markings = reversedMarkings ?? ProgramResourceStore.Cover([head.Markings, .. res is null ? [] : new[] { res.Markings }, .. item is null ? [] : new[] { item.Markings }]);
            if (markings is null) return PStep.Invalid("The markings of the program, resource and task cannot be combined: none covers the others.");
            if (RequireRow(subject, markings, PlmAction.Modify) is { } denied) return denied;
            key = await ProgramResourceStore.NextKeyAsync(conn, tx, head.Id, "actual", "AC", 4, ct);
            await InsertRowAsync(conn, tx,
                $"INSERT INTO prg.actual_cost (id, program_id, key, cost_type, incurred_on, amount, hours, resource_id, item_id, reverses_id, note, {MarkingColumns.InsertColumns}, created_at, created_by) VALUES (@id, @p, @key, @t, @d, @amt, @hours, @res, @item, @rev, @note, {MarkingColumns.InsertValues}, @at, @by)", markings, c =>
            {
                c.Parameters.AddWithValue("id", Guid.CreateVersion7());
                c.Parameters.AddWithValue("p", head.Id);
                c.Parameters.AddWithValue("key", key);
                c.Parameters.AddWithValue("t", costType);
                c.Parameters.AddWithValue("d", cmd.IncurredOn.ToDateTime(TimeOnly.MinValue));
                c.Parameters.AddWithValue("amt", amount);
                c.Parameters.AddWithValue("hours", (object?)hours ?? DBNull.Value);
                c.Parameters.AddWithValue("res", (object?)res?.Id ?? DBNull.Value);
                c.Parameters.AddWithValue("item", (object?)itemId ?? DBNull.Value);
                c.Parameters.AddWithValue("rev", (object?)reverses ?? DBNull.Value);
                c.Parameters.AddWithValue("note", cmd.Note?.Trim() ?? "");
                c.Parameters.AddWithValue("at", now);
                c.Parameters.AddWithValue("by", ProgramStore.Who(subject));
            }, ct);
            return PStep.Done(("entry", key), ("type", costType), ("date", Iso(cmd.IncurredOn)), ("reverses", reversesKey));
        }, async (conn, tx, s, head) =>
        {
            var d = await ProgramResourceStore.LoadAsync(conn, tx, s, head, ct);
            var all = await LoadActualsAsync(conn, tx, s, head, ct);
            var a = all.FirstOrDefault(x => x.Key == key);
            if (a is null) return null;
            return new ActualRow(a.Key, a.CostType, a.On, a.Amount, a.Hours, a.ResourceId is { } rid ? d.Resources.FirstOrDefault(r => r.Id == rid)?.Key : null, a.ItemId is { } iid && d.Items.TryGetValue(iid, out var it) ? it.Key : null,
                a.Note, a.ReversesId is { } rv ? all.FirstOrDefault(x => x.Id == rv)?.Key : null, a.By, a.Markings);
        }, ct, allowClosed: true); // costs are still booked, and corrected, after a program is complete or cancelled (FEAT-RES-19)
    }

    private static async Task<ResR?> FindResourceByIdAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var cmd = ProgramStore.Cmd(conn, tx, "SELECT key, program_id FROM prg.resource WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var (key, program) = (r.GetString(0), r.GetGuid(1));
        await r.CloseAsync();
        return await ProgramResourceStore.FindResourceAsync(conn, tx, program, key, false, ct);
    }

    /// <summary>Adds a quantity of an end item (a part number) needed on a day. Its cost is estimated live from the BOM and the sourcing price lists; the part must be readable to the caller now.</summary>
    public Task<Outcome<MaterialLineRow>> AddMaterialLineAsync(Subject subject, string code, AddMaterialLineCommand cmd, string? corr, CancellationToken ct = default)
    {
        string? key = null;
        return MutateCostAsync(subject, code, "program.material_add", corr, async (head, conn, tx, now) =>
        {
            var part = cmd.PartNumber?.Trim() ?? "";
            if (part.Length is 0 or > 64) return PStep.Invalid("The part number is required (at most 64 characters).");
            if (cmd.Quantity <= 0 || cmd.Quantity > 1_000_000m) return PStep.Invalid("The quantity must be above 0 and at most 1,000,000.");
            if ((cmd.Note?.Length ?? 0) > 200) return PStep.Invalid("The note must be at most 200 characters.");
            if (_materials is null) return PStep.Invalid("No material cost source is configured in this installation.");
            var probe = await _materials.EstimateAsync(subject, part, cmd.Quantity, cmd.NeededOn, corr, ct);
            if (probe.Kind == OutcomeKind.NotFound) return PStep.Invalid($"Part '{part}' was not found.");
            if (!probe.IsOk) return PStep.Invalid([.. probe.Errors]);
            key = await ProgramResourceStore.NextKeyAsync(conn, tx, head.Id, "material", "ML", 3, ct);
            await InsertRowAsync(conn, tx, $"INSERT INTO prg.material_line (id, program_id, key, part_number, quantity, needed_on, note, {MarkingColumns.InsertColumns}, created_at, created_by) VALUES (@id, @p, @key, @part, @q, @d, @note, {MarkingColumns.InsertValues}, @at, @by)", head.Markings, c =>
            {
                c.Parameters.AddWithValue("id", Guid.CreateVersion7());
                c.Parameters.AddWithValue("p", head.Id);
                c.Parameters.AddWithValue("key", key);
                c.Parameters.AddWithValue("part", probe.Value!.PartNumber);
                c.Parameters.AddWithValue("q", cmd.Quantity);
                c.Parameters.AddWithValue("d", cmd.NeededOn.ToDateTime(TimeOnly.MinValue));
                c.Parameters.AddWithValue("note", cmd.Note?.Trim() ?? "");
                c.Parameters.AddWithValue("at", now);
                c.Parameters.AddWithValue("by", ProgramStore.Who(subject));
            }, ct);
            return PStep.Done(("line", key), ("part", probe.Value!.PartNumber), ("needed", Iso(cmd.NeededOn)));
        }, async (conn, tx, s, head) =>
        {
            var l = (await LoadMaterialAsync(conn, tx, s, head, ct)).FirstOrDefault(x => x.Key == key);
            return l is null ? null : new MaterialLineRow(l.Key, l.Part, null, l.Quantity, l.Needed, l.Note, null, 0, 0, 0, [], null, l.Markings);
        }, ct);
    }

    public Task<Outcome<CostView>> RemoveMaterialLineAsync(Subject subject, string code, string lineKey, string? corr, CancellationToken ct = default) =>
        MutateCostAsync<CostView>(subject, code, "program.material_remove", corr, async (head, conn, tx, now) =>
        {
            var line = (await LoadMaterialAsync(conn, tx, subject, head, ct)).FirstOrDefault(l => string.Equals(l.Key, lineKey?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (line is null) return PStep.Missing();
            if (RequireRow(subject, line.Markings, PlmAction.Modify) is { } denied) return denied;
            await using var up = ProgramStore.Cmd(conn, tx, "UPDATE prg.material_line SET removed_at = @at, removed_by = @by WHERE id = @id");
            up.Parameters.AddWithValue("at", now);
            up.Parameters.AddWithValue("by", ProgramStore.Who(subject));
            up.Parameters.AddWithValue("id", line.Id);
            await up.ExecuteNonQueryAsync(ct);
            return PStep.Done(("line", line.Key));
        }, (conn, tx, s, head) => BuildCostAsync(conn, tx, s, head, Today, null, ct)!, ct);
}
