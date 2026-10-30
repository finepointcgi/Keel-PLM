using System.Globalization;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Programs;

internal sealed record PoolR(Guid Id, string Key, string Name, string Description, Markings Markings);
internal sealed record RoleR(Guid Id, string Key, string Name, Guid PoolId, Markings Markings);
internal sealed record ResR(Guid Id, string Key, string Name, Guid RoleId, decimal Capacity, DateOnly? From, DateOnly? To, bool Active, Markings Markings);
internal sealed record AsgR(Guid Id, string Key, Guid ItemId, Guid ResourceId, DateOnly Start, DateOnly End, decimal HoursPerDay, Markings Markings);
internal sealed record ItemLite(Guid Id, string Key, string Title, string Kind, string Status, int Percent, Markings Markings);

/// <summary>Everything of the resource model the caller may read for one program. Rows the caller may not read are absent: no placeholder, no count.</summary>
internal sealed record ResData(IReadOnlyList<PoolR> Pools, IReadOnlyList<RoleR> Roles, IReadOnlyDictionary<Guid, List<RateStep>> Rates, IReadOnlyList<ResR> Resources, IReadOnlyList<AsgR> Assignments,
    IReadOnlyDictionary<Guid, ItemLite> Items)
{
    public IEnumerable<Markings> AllMarkings => Pools.Select(p => p.Markings).Concat(Roles.Select(r => r.Markings)).Concat(Resources.Select(r => r.Markings)).Concat(Assignments.Select(a => a.Markings));
}

/// <summary>Plain SQL of the resource and cost model (ADR-0049). Lists apply the read filter; lookups by key are decided by the caller on the returned markings.</summary>
internal static class ProgramResourceStore
{
    public static readonly DateOnly RateEpoch = new(2000, 1, 1);

    /// <summary>The next key of a kind in a program ("RS-001"). The program row is locked by the surrounding change, so two changes never take the same number.</summary>
    public static async Task<string> NextKeyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid programId, string kind, string prefix, int width, CancellationToken ct)
    {
        await using var cmd = ProgramStore.Cmd(conn, tx,
            "INSERT INTO prg.res_counter (program_id, kind, next_no) VALUES (@p, @k, 2) ON CONFLICT (program_id, kind) DO UPDATE SET next_no = prg.res_counter.next_no + 1 RETURNING next_no - 1");
        cmd.Parameters.AddWithValue("p", programId);
        cmd.Parameters.AddWithValue("k", kind);
        var n = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        return $"{prefix}-{n.ToString("D" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)}";
    }

    /// <summary>The markings that cover all of <paramref name="parts"/>, or null when none of them does (two incomparable markings).</summary>
    public static Markings? Cover(params Markings[] parts)
    {
        foreach (var candidate in parts)
            if (parts.All(o => MarkingDominance.Gaps(candidate, o).Count == 0)) return candidate;
        return null;
    }

    public static async Task<PoolR?> FindPoolAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid programId, string key, CancellationToken ct)
    {
        await using var cmd = ProgramStore.Cmd(conn, tx, $"SELECT p.id, p.key, p.name, p.description, {MarkingColumns.Select("p")} FROM prg.res_pool p WHERE p.program_id = @p AND upper(p.key) = upper(@k)");
        cmd.Parameters.AddWithValue("p", programId);
        cmd.Parameters.AddWithValue("k", key ?? "");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new PoolR(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), MarkingColumns.Read(r, 4)) : null;
    }

    public static async Task<RoleR?> FindRoleAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid programId, string key, CancellationToken ct)
    {
        await using var cmd = ProgramStore.Cmd(conn, tx, $"SELECT r.id, r.key, r.name, r.pool_id, {MarkingColumns.Select("r")} FROM prg.res_role r WHERE r.program_id = @p AND upper(r.key) = upper(@k)");
        cmd.Parameters.AddWithValue("p", programId);
        cmd.Parameters.AddWithValue("k", key ?? "");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new RoleR(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetGuid(3), MarkingColumns.Read(r, 4)) : null;
    }

    private const string ResourceColumns = "r.id, r.key, r.name, r.role_id, r.capacity_hours_per_day, r.available_from, r.available_to, r.active, ";

    private static ResR ReadResource(System.Data.Common.DbDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetDecimal(4), ProgramStore.DateAt(r, 5), ProgramStore.DateAt(r, 6), r.GetBoolean(7), MarkingColumns.Read(r, 8));

    public static async Task<ResR?> FindResourceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid programId, string key, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = ProgramStore.Cmd(conn, tx, $"SELECT {ResourceColumns}{MarkingColumns.Select("r")} FROM prg.resource r WHERE r.program_id = @p AND upper(r.key) = upper(@k)" + (forUpdate ? " FOR UPDATE OF r" : ""));
        cmd.Parameters.AddWithValue("p", programId);
        cmd.Parameters.AddWithValue("k", key ?? "");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadResource(r) : null;
    }

    private const string AssignmentColumns = "a.id, a.key, a.item_id, a.resource_id, a.start_date, a.end_date, a.hours_per_day, ";

    private static AsgR ReadAssignment(System.Data.Common.DbDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetGuid(3), ProgramStore.Date(r, 4), ProgramStore.Date(r, 5), r.GetDecimal(6), MarkingColumns.Read(r, 7));

    public static async Task<AsgR?> FindAssignmentAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid programId, string key, CancellationToken ct)
    {
        await using var cmd = ProgramStore.Cmd(conn, tx, $"SELECT {AssignmentColumns}{MarkingColumns.Select("a")} FROM prg.assignment a WHERE a.program_id = @p AND upper(a.key) = upper(@k) AND a.removed_at IS NULL FOR UPDATE OF a");
        cmd.Parameters.AddWithValue("p", programId);
        cmd.Parameters.AddWithValue("k", key ?? "");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadAssignment(r) : null;
    }

    public static async Task<ItemLite?> FindItemAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid programId, string key, CancellationToken ct)
    {
        await using var cmd = ProgramStore.Cmd(conn, tx, $"SELECT i.id, i.key, i.title, i.kind, i.status, i.percent_complete, {MarkingColumns.Select("i")} FROM prg.item i WHERE i.program_id = @p AND upper(i.key) = upper(@k)");
        cmd.Parameters.AddWithValue("p", programId);
        cmd.Parameters.AddWithValue("k", key ?? "");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ItemLite(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), MarkingColumns.Read(r, 6)) : null;
    }

    /// <summary>The current rate of a role (in force on <paramref name="day"/>), read for validation only; never shown without the cost permission.</summary>
    public static async Task<decimal?> RateOnAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid roleId, DateOnly day, CancellationToken ct)
    {
        await using var cmd = ProgramStore.Cmd(conn, tx, "SELECT hourly_rate FROM prg.role_rate WHERE role_id = @r AND effective_from <= @d ORDER BY effective_from DESC LIMIT 1");
        cmd.Parameters.AddWithValue("r", roleId);
        cmd.Parameters.AddWithValue("d", day.ToDateTime(TimeOnly.MinValue));
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
    }

    /// <summary>Loads the pools, roles, resources, assignments and the schedule items of a program that the caller may read.</summary>
    public static async Task<ResData> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, PHead head, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "p");
        var pools = new List<PoolR>();
        await using (var cmd = new NpgsqlCommand($"SELECT p.id, p.key, p.name, p.description, {MarkingColumns.Select("p")} FROM prg.res_pool p WHERE p.program_id = @pid AND {filter.Sql} ORDER BY p.key", conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("pid", head.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) pools.Add(new PoolR(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), MarkingColumns.Read(r, 4)));
        }
        var rFilter = AccessPolicy.BuildReadFilter(subject, "r");
        var roles = new List<RoleR>();
        await using (var cmd = new NpgsqlCommand($"SELECT r.id, r.key, r.name, r.pool_id, {MarkingColumns.Select("r")} FROM prg.res_role r WHERE r.program_id = @pid AND {rFilter.Sql} ORDER BY r.key", conn, tx))
        {
            foreach (var (k, v) in rFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("pid", head.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) roles.Add(new RoleR(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetGuid(3), MarkingColumns.Read(r, 4)));
        }
        // Rates are child rows: read only for the roles the caller may read.
        var rates = new Dictionary<Guid, List<RateStep>>();
        if (roles.Count > 0)
        {
            await using var cmd = new NpgsqlCommand("SELECT x.role_id, x.effective_from, x.hourly_rate FROM prg.role_rate x WHERE x.role_id = ANY(@ids) ORDER BY x.role_id, x.effective_from", conn, tx);
            cmd.Parameters.AddWithValue("ids", roles.Select(r => r.Id).ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!rates.TryGetValue(r.GetGuid(0), out var list)) rates[r.GetGuid(0)] = list = [];
                list.Add(new RateStep(ProgramStore.Date(r, 1), r.GetDecimal(2)));
            }
        }
        var resources = new List<ResR>();
        await using (var cmd = new NpgsqlCommand($"SELECT {ResourceColumns}{MarkingColumns.Select("r")} FROM prg.resource r WHERE r.program_id = @pid AND {rFilter.Sql} ORDER BY r.key", conn, tx))
        {
            foreach (var (k, v) in rFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("pid", head.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) resources.Add(ReadResource(r));
        }
        var aFilter = AccessPolicy.BuildReadFilter(subject, "a");
        var assignments = new List<AsgR>();
        await using (var cmd = new NpgsqlCommand($"SELECT {AssignmentColumns}{MarkingColumns.Select("a")} FROM prg.assignment a WHERE a.program_id = @pid AND a.removed_at IS NULL AND {aFilter.Sql} ORDER BY a.key", conn, tx))
        {
            foreach (var (k, v) in aFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("pid", head.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) assignments.Add(ReadAssignment(r));
        }
        var iFilter = AccessPolicy.BuildReadFilter(subject, "i");
        var items = new Dictionary<Guid, ItemLite>();
        await using (var cmd = new NpgsqlCommand($"SELECT i.id, i.key, i.title, i.kind, i.status, i.percent_complete, {MarkingColumns.Select("i")} FROM prg.item i WHERE i.program_id = @pid AND {iFilter.Sql}", conn, tx))
        {
            foreach (var (k, v) in iFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("pid", head.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items[r.GetGuid(0)] = new ItemLite(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), MarkingColumns.Read(r, 6));
        }
        return new ResData(pools, roles, rates, resources, assignments, items);
    }

    public static IReadOnlyList<MathResource> MathResources(ResData d) => [.. d.Resources.Select(r => new MathResource(r.Id, r.Key, r.Name, r.RoleId, r.Capacity, r.From, r.To))];

    public static IReadOnlyList<MathAssignment> MathAssignments(ResData d) =>
        [.. d.Assignments.Where(a => d.Resources.Select(r => r.Id).Contains(a.ResourceId)).Select(a => new MathAssignment(a.Id, a.Key, a.ResourceId, a.ItemId, a.Start, a.End, a.HoursPerDay))];
}
