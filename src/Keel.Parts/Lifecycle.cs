using Npgsql;

namespace Keel.Parts;

public sealed record LifecycleState(string State, string Label, int SortOrder, bool IsInitial, bool IsLocked);
public sealed record LifecycleTransition(string From, string To, string Via);

public sealed record Lifecycle(string Id, string Name, IReadOnlyList<LifecycleState> States, IReadOnlyList<LifecycleTransition> Transitions)
{
    public LifecycleState Initial => States.Single(s => s.IsInitial);
    public LifecycleState? State(string state) => States.FirstOrDefault(s => s.State == state);
    public bool IsLocked(string state) => State(state)?.IsLocked ?? true;

    public static async Task<Lifecycle?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string id, CancellationToken ct)
    {
        string? name = null;
        await using (var c = new NpgsqlCommand("SELECT name FROM plm.lifecycle WHERE id = @id", conn, tx))
        {
            c.Parameters.AddWithValue("id", id);
            name = (string?)await c.ExecuteScalarAsync(ct);
        }
        if (name is null) return null;

        var states = new List<LifecycleState>();
        await using (var c = new NpgsqlCommand("SELECT state, label, sort_order, is_initial, is_locked FROM plm.lifecycle_state WHERE lifecycle_id = @id ORDER BY sort_order", conn, tx))
        {
            c.Parameters.AddWithValue("id", id);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) states.Add(new(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetBoolean(3), r.GetBoolean(4)));
        }
        var transitions = new List<LifecycleTransition>();
        await using (var c = new NpgsqlCommand("SELECT from_state, to_state, via FROM plm.lifecycle_transition WHERE lifecycle_id = @id", conn, tx))
        {
            c.Parameters.AddWithValue("id", id);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) transitions.Add(new(r.GetString(0), r.GetString(1), r.GetString(2)));
        }
        return new Lifecycle(id, name, states, transitions);
    }
}
