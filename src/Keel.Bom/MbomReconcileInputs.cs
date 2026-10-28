using Npgsql;

namespace Keel.Bom;

/// <summary>
/// The two facts the reconciliation needs about children that no reader's access should change: whether a child is a Make assembly and whether an MBOM of the
/// plant exists for it (and is released). They are inputs of the system's own check, read without the access filter; nothing here is ever shown. What a reader sees of
/// the result is filtered when a run is read. This is the only file of the module that reads these tables unfiltered, and the architecture tests name it.
/// </summary>
internal static class MbomReconcileInputs
{
    public static async Task<HashSet<Guid>> MakeAssembliesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyCollection<Guid> children, CancellationToken ct)
    {
        var set = new HashSet<Guid>();
        if (children.Count == 0) return set;
        // System-level: whether a child is a Make assembly is a fact about the structure, not something shown (the file is allow-listed in the architecture test).
        await using var cmd = new NpgsqlCommand("SELECT m.id FROM plm.part_master m WHERE m.part_type = 'Assembly' AND m.source = 'Make' AND m.id = ANY(@ids)", conn, tx);
        cmd.Parameters.AddWithValue("ids", children.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetGuid(0));
        return set;
    }

    public static async Task<Dictionary<Guid, ChildMbomInfo>> ChildMbomsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string plant, IReadOnlyCollection<Guid> children, CancellationToken ct)
    {
        var map = new Dictionary<Guid, ChildMbomInfo>();
        if (children.Count == 0) return map;
        await using var cmd = new NpgsqlCommand(
            """
            SELECT mm.part_master_id, EXISTS (SELECT 1 FROM mbom.revision r WHERE r.mbom_id = mm.id AND r.lifecycle_state = 'Released')
            FROM mbom.mbom mm WHERE mm.plant = @plant AND mm.part_master_id = ANY(@ids)
            """, conn, tx);
        cmd.Parameters.AddWithValue("plant", plant);
        cmd.Parameters.AddWithValue("ids", children.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetGuid(0)] = new ChildMbomInfo(true, r.GetBoolean(1));
        return map;
    }
}
