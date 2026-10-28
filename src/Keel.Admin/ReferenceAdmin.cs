using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Keel.Core;
using Npgsql;

namespace Keel.Admin;

/// <summary>
/// Cached reads of the reference lists for pickers and metadata (<see cref="IReferenceData"/>). Entries live for <see cref="Lifetime"/> and are dropped
/// when an administrator changes a list through <see cref="ReferenceAdminService"/> in this process; other instances see a change within the lifetime.
/// Validation inside services does not use the cache (it reads in its own transaction).
/// </summary>
public sealed class ReferenceDataService(NpgsqlDataSource dataSource, TimeProvider clock) : IReferenceData
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<RefItem> Items)> _cache = new(StringComparer.Ordinal);

    public void Invalidate() => _cache.Clear();

    public async Task<IReadOnlyList<RefItem>> ListAsync(string list, bool includeRetired = false, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        if (!_cache.TryGetValue(list, out var entry) || now - entry.At > Lifetime)
        {
            entry = (now, await LoadAsync(dataSource, list, ct));
            _cache[list] = entry;
        }
        return includeRetired ? entry.Items : [.. entry.Items.Where(i => i.IsActive)];
    }

    internal static async Task<IReadOnlyList<RefItem>> LoadAsync(NpgsqlDataSource ds, string list, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        return await LoadAsync(conn, null, list, ct);
    }

    internal static async Task<IReadOnlyList<RefItem>> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string list, CancellationToken ct)
    {
        var items = new List<RefItem>();
        await using var cmd = AdminRunner.Cmd(conn, tx, "SELECT list, code, label, sort_order, is_active, lifecycle_family FROM cfg.ref_item WHERE list = @l ORDER BY sort_order, code", ("l", list));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) items.Add(new RefItem(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetBoolean(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return items;
    }
}

public sealed record RefItemInput(string List, string Code, string Label, int SortOrder = 0, string? LifecycleFamily = null);

/// <summary>Part types, sources, units, document types, change priorities and class labels, and the picker vocabularies (offices, programs).</summary>
public sealed partial class ReferenceAdminService(AdminRunner run, ReferenceDataService cache)
{
    /// <summary>Items code refers to by value; they can be relabelled but never retired.</summary>
    private static readonly HashSet<(string, string)> Protected = [(RefLists.Unit, UnitsOfMeasure.Each), (RefLists.ChangeClass, "I"), (RefLists.ChangeClass, "II")];

    public Task<Outcome<IReadOnlyList<RefItem>>> ListAsync(Subject subject, string list, CancellationToken ct = default) =>
        run.ReadAsync<IReadOnlyList<RefItem>>(subject, "admin.ref_list", list, conn => ReferenceDataService.LoadAsync(conn, null, list, ct), ct);

    /// <summary>Creates an item, or updates the label, order and lifecycle binding of an existing one (the code never changes).</summary>
    public Task<Outcome<RefItem>> SaveAsync(Subject subject, RefItemInput input, string? correlationId, CancellationToken ct = default) =>
        run.ChangeAsync<RefItem>(subject, "admin.ref_save", $"{input.List}/{input.Code}", correlationId, async (conn, tx) =>
        {
            var errors = new List<string>();
            if (!RefLists.All.Contains(input.List)) return Step<RefItem>.Invalid($"Unknown list '{input.List}'.");
            var code = input.Code?.Trim() ?? "";
            if (!CodePattern().IsMatch(code)) errors.Add("The code must start with a letter or digit and use letters, digits, space and . _ / + - (at most 60 characters).");
            if (string.IsNullOrWhiteSpace(input.Label) || input.Label.Trim().Length > 120) errors.Add("The label is required (at most 120 characters).");
            if (input.SortOrder is < -10000 or > 10000) errors.Add("The sort order must be between -10000 and 10000.");
            if (input.LifecycleFamily is not null)
            {
                if (input.List is not (RefLists.PartType or RefLists.DocumentType)) errors.Add("Only part and document types are bound to a lifecycle.");
                else
                {
                    var wanted = input.List == RefLists.PartType ? "part" : "document";
                    await using var fc = AdminRunner.Cmd(conn, tx, "SELECT object_type FROM plm.lifecycle_family WHERE family = @f", ("f", input.LifecycleFamily));
                    var type = (string?)await fc.ExecuteScalarAsync(ct);
                    if (type is null) errors.Add($"Lifecycle '{input.LifecycleFamily}' does not exist.");
                    else if (type != wanted) errors.Add($"Lifecycle '{input.LifecycleFamily}' is a {type} lifecycle.");
                }
            }
            if (errors.Count > 0) return Step<RefItem>.Invalid(errors);

            var before = (await ReferenceDataService.LoadAsync(conn, tx, input.List, ct)).FirstOrDefault(i => i.Code == code);
            if (before is null && input.List == RefLists.ChangeClass) return Step<RefItem>.Invalid("Change classes carry process rules in code (A8); only the labels of Class I and II are editable.");
            // Codes are compared exactly, except that two codes differing only in case would be easy to confuse in a picker.
            if (before is null && (await ReferenceDataService.LoadAsync(conn, tx, input.List, ct)).Any(i => string.Equals(i.Code, code, StringComparison.OrdinalIgnoreCase) && input.List != RefLists.Unit))
                return Step<RefItem>.Conflict($"An item whose code differs from '{code}' only in letter case already exists.");
            await using (var cmd = AdminRunner.Cmd(conn, tx,
                """
                INSERT INTO cfg.ref_item (list, code, label, sort_order, lifecycle_family, updated_at, updated_by) VALUES (@l, @c, @label, @o, @f, @at, @by)
                ON CONFLICT (list, code) DO UPDATE SET label = @label, sort_order = @o, lifecycle_family = @f, updated_at = @at, updated_by = @by
                """, ("l", input.List), ("c", code), ("label", input.Label.Trim()), ("o", input.SortOrder), ("f", input.LifecycleFamily), ("at", run.Clock.GetUtcNow()), ("by", AdminRunner.Who(subject))))
                await cmd.ExecuteNonQueryAsync(ct);
            var after = (await ReferenceDataService.LoadAsync(conn, tx, input.List, ct)).Single(i => i.Code == code);
            cache.Invalidate();
            return Step<RefItem>.Ok(after, ("before", before is null ? null : AdminRunner.ToJson(before)), ("after", AdminRunner.ToJson(after)));
        }, ct);

    /// <summary>Retires or restores an item. Existing objects keep a retired value; it is no longer offered for new ones.</summary>
    public Task<Outcome<RefItem>> SetActiveAsync(Subject subject, string list, string code, bool active, string? reason, string? correlationId, CancellationToken ct = default) =>
        run.ChangeAsync<RefItem>(subject, active ? "admin.ref_restore" : "admin.ref_retire", $"{list}/{code}", correlationId, async (conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) return Step<RefItem>.Invalid("A reason is required.");
            var items = await ReferenceDataService.LoadAsync(conn, tx, list, ct);
            var item = items.FirstOrDefault(i => i.Code == code);
            if (item is null) return Step<RefItem>.NotFound();
            if (item.IsActive == active) return Step<RefItem>.Conflict(active ? "The item is already active." : "The item is already retired.");
            if (!active && Protected.Contains((list, code))) return Step<RefItem>.Invalid($"'{code}' is referred to by name in code and cannot be retired.");
            if (!active && items.Count(i => i.IsActive) <= 1 && list is not (RefLists.Office or RefLists.Program))
                return Step<RefItem>.Invalid("The last active item of a list cannot be retired: nothing could be created any more.");
            await using (var cmd = AdminRunner.Cmd(conn, tx, "UPDATE cfg.ref_item SET is_active = @a, updated_at = @at, updated_by = @by WHERE list = @l AND code = @c",
                ("a", active), ("at", run.Clock.GetUtcNow()), ("by", AdminRunner.Who(subject)), ("l", list), ("c", code)))
                await cmd.ExecuteNonQueryAsync(ct);
            var after = item with { IsActive = active };
            cache.Invalidate();
            return Step<RefItem>.Ok(after, ("before", AdminRunner.ToJson(item)), ("after", AdminRunner.ToJson(after)), ("reason", reason.Trim()));
        }, ct);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._/+\-]{0,59}$")]
    private static partial Regex CodePattern();
}
