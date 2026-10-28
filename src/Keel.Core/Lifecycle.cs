using System.Data.Common;

namespace Keel.Core;

public sealed record LifecycleState(string State, string Label, int SortOrder, bool IsInitial, bool IsLocked);
public sealed record LifecycleTransition(string From, string To, string Via);

/// <summary>
/// A data-defined lifecycle (table <c>plm.lifecycle*</c>), shared by every versioned type family. Loaded through
/// plain ADO.NET so that Core stays free of a database driver dependency.
/// </summary>
public sealed record Lifecycle(string Id, string Name, IReadOnlyList<LifecycleState> States, IReadOnlyList<LifecycleTransition> Transitions,
    string Family = "", int Version = 1)
{
    /// <summary>The family a lifecycle id belongs to: version 1 has the family name as id, later versions are '&lt;family&gt;~v&lt;n&gt;' (0009).</summary>
    public static string FamilyOf(string lifecycleId) => lifecycleId.Split("~v")[0];

    /// <summary>The id of the family's current published version: the lifecycle new objects start in.</summary>
    public static async Task<string> CurrentIdAsync(DbConnection conn, DbTransaction? tx, string family, CancellationToken ct)
    {
        await using var c = Command(conn, tx,
            "SELECT l.id FROM plm.lifecycle_family f JOIN plm.lifecycle l ON l.family = f.family AND l.version = f.current_version WHERE f.family = @id", family);
        return (string?)await c.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException($"Lifecycle family {family} is missing.");
    }

    public LifecycleState Initial => States.Single(s => s.IsInitial);
    public LifecycleState? State(string state) => States.FirstOrDefault(s => s.State == state);
    public bool IsLocked(string state) => State(state)?.IsLocked ?? true;

    public static async Task<Lifecycle?> LoadAsync(DbConnection conn, DbTransaction? tx, string id, CancellationToken ct)
    {
        string name, family;
        int version;
        await using (var c = Command(conn, tx, "SELECT name, family, version FROM plm.lifecycle WHERE id = @id", id))
        {
            await using var r = await c.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            (name, family, version) = (r.GetString(0), r.GetString(1), r.GetInt32(2));
        }

        var states = new List<LifecycleState>();
        await using (var c = Command(conn, tx, "SELECT state, label, sort_order, is_initial, is_locked FROM plm.lifecycle_state WHERE lifecycle_id = @id ORDER BY sort_order", id))
        {
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) states.Add(new(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetBoolean(3), r.GetBoolean(4)));
        }
        var transitions = new List<LifecycleTransition>();
        await using (var c = Command(conn, tx, "SELECT from_state, to_state, via FROM plm.lifecycle_transition WHERE lifecycle_id = @id", id))
        {
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) transitions.Add(new(r.GetString(0), r.GetString(1), r.GetString(2)));
        }
        return new Lifecycle(id, name, states, transitions, family, version);
    }

    private static DbCommand Command(DbConnection conn, DbTransaction? tx, string sql, string id)
    {
        var c = conn.CreateCommand();
#pragma warning disable CA2100 // SQL text is a constant; the only value is passed as a parameter.
        c.CommandText = sql;
#pragma warning restore CA2100
        c.Transaction = tx;
        var p = c.CreateParameter();
        p.ParameterName = "id";
        p.Value = id;
        c.Parameters.Add(p);
        return c;
    }
}
