using System.Data.Common;
using System.Text;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

/// <summary>Structure-wide checks shared by the editing service and the check-in participant.</summary>
internal static class BomStructure
{
    /// <summary>Advisory lock key serializing check-ins that publish BOM structure, so cycle checks and commits cannot interleave. ASCII "KEELBOM".</summary>
    public const long StructureLockKey = 0x4B45454C424F4D;

    /// <summary>
    /// Finds a child that would close a loop under <paramref name="parent"/>: a child that is the parent itself, or a master that
    /// already contains the parent through its latest-iteration structure. Returns the chain child, ..., parent (each contains the next), or null.
    /// </summary>
    public static async Task<(Guid Child, IReadOnlyList<Guid> Chain)?> FindCycleAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid parent, IReadOnlyCollection<Guid> children, CancellationToken ct)
    {
        if (children.Count == 0) return null;
        if (children.Contains(parent)) return (parent, [parent]);

        var via = new Dictionary<Guid, Guid?>();
        await using (var cmd = new NpgsqlCommand(BomSql.Ancestors, conn, tx))
        {
            cmd.Parameters.AddWithValue("parent", parent);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var m = r.GetGuid(0);
                // Keep the first route found to each ancestor.
                via.TryAdd(m, r.IsDBNull(1) ? null : r.GetGuid(1));
            }
        }
        foreach (var child in children)
        {
            if (!via.ContainsKey(child)) continue;
            var chain = new List<Guid>();
            var seen = new HashSet<Guid>();
            for (Guid? at = child; at is { } g && seen.Add(g); at = via.GetValueOrDefault(g)) chain.Add(g);
            return (child, chain);
        }
        return null;
    }

    /// <summary>Part numbers for a chain; parts the subject cannot read are named "(restricted item)" so the message leaks nothing.</summary>
    public static async Task<IReadOnlyList<string>> NamesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Subject subject, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "m");
        var names = new Dictionary<Guid, string>();
        await using (var cmd = new NpgsqlCommand($"SELECT m.id, m.number FROM plm.part_master m WHERE m.id = ANY(@ids) AND {filter.Sql}", conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("ids", ids.ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) names[r.GetGuid(0)] = r.GetString(1);
        }
        return [.. ids.Select(id => names.GetValueOrDefault(id, "(restricted item)"))];
    }
}

/// <summary>Reads marking columns once per distinct value so a 50,000-line explosion builds a handful of <see cref="Markings"/> objects.</summary>
internal sealed class MarkingCache
{
    private readonly Dictionary<Guid, (Markings Markings, string Banner)> _byMaster = [];
    private readonly Dictionary<string, (Markings Markings, string Banner)> _byKey = new(StringComparer.Ordinal);

    public IEnumerable<Markings> Distinct => _byKey.Values.Select(v => v.Markings);

    /// <summary>Reads the <see cref="MarkingColumns.Count"/> columns at <paramref name="start"/>; the part's master id selects the cache entry.</summary>
    public (Markings Markings, string Banner) Get(DbDataReader r, Guid masterId, int start)
    {
        if (_byMaster.TryGetValue(masterId, out var hit)) return hit;
        var sb = new StringBuilder(96);
        for (var i = 0; i < MarkingColumns.Count; i++)
        {
            if (i == 3 || i == 9) sb.Append(string.Join(',', r.GetFieldValue<string[]>(start + i)));
            else if (!r.IsDBNull(start + i)) sb.Append(r.GetString(start + i));
            sb.Append('|');
        }
        var key = sb.ToString();
        if (!_byKey.TryGetValue(key, out var entry))
        {
            var m = MarkingColumns.Read(r, start);
            entry = (m, m.BannerText());
            _byKey[key] = entry;
        }
        _byMaster[masterId] = entry;
        return entry;
    }
}
