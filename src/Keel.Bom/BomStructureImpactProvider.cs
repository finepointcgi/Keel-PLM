using System.Globalization;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

/// <summary>
/// Gives change management (Keel.Change) where-used and structure comparison through <see cref="IStructureImpactProvider"/>, over the
/// BOM module's own audited queries. Parents the caller cannot read stay redacted placeholders (A4); only readable parents get a master
/// id, looked up with the read filter, so a hidden part can never be tied to another change.
/// </summary>
internal sealed class BomStructureImpactProvider(BomService bom, NpgsqlDataSource dataSource) : IStructureImpactProvider
{
    public string ObjectType => BomService.ObjectType;

    public async Task<Outcome<IReadOnlyList<WhereUsedEntry>>> WhereUsedAsync(Subject subject, string key, int levels, string? correlationId, CancellationToken ct)
    {
        var result = await bom.WhereUsedAsync(subject, key, levels, correlationId, ct);
        if (!result.IsOk) return new Outcome<IReadOnlyList<WhereUsedEntry>>(result.Kind, default, result.Errors);

        var numbers = result.Value!.Rows.Where(r => !r.Redacted && r.ParentNumber is not null).Select(r => r.ParentNumber!).Distinct().ToArray();
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (numbers.Length > 0)
        {
            var filter = AccessPolicy.BuildReadFilter(subject, "m");
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand($"SELECT m.number, m.id FROM plm.part_master m WHERE {filter.Sql} AND m.number = ANY(@nums)", conn);
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("nums", numbers);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids[r.GetString(0)] = r.GetGuid(1);
        }
        IReadOnlyList<WhereUsedEntry> rows =
        [
            .. result.Value.Rows.Select(w => new WhereUsedEntry(w.Level, w.Redacted, w.FindNumber, w.Quantity, w.Unit,
                w.ParentNumber is not null && ids.TryGetValue(w.ParentNumber, out var id) ? id : null,
                w.ParentNumber, w.ParentName, w.ParentRevision, w.ParentStateLabel ?? w.ParentState, w.Banner, w.Markings)),
        ];
        return Outcome<IReadOnlyList<WhereUsedEntry>>.Ok(rows);
    }

    public async Task<Outcome<StructureComparison>> CompareAsync(Subject subject, string key, string fromLabel, string toLabel, string? correlationId, CancellationToken ct)
    {
        var result = await bom.CompareAsync(subject, key, fromLabel, toLabel, correlationId, ct);
        if (!result.IsOk) return new Outcome<StructureComparison>(result.Kind, default, result.Errors);
        var c = result.Value!;
        IReadOnlyList<StructureDelta> deltas =
        [
            .. c.Lines.Select(l => new StructureDelta(l.Kind.ToString(), l.Redacted, l.Number, l.Name, l.Kind switch
            {
                BomChangeKind.Added => $"find {l.ToFindNumber}, {Qty(l.ToQuantity)} {l.ToUnit}",
                BomChangeKind.Removed => $"find {l.FromFindNumber}, {Qty(l.FromQuantity)} {l.FromUnit}",
                _ => string.Join("; ", l.Changes),
            })),
        ];
        return Outcome<StructureComparison>.Ok(new StructureComparison(c.From, c.To, deltas, c.Unchanged, c.Banner));
    }

    private static string Qty(decimal? q) => q?.ToString("0.######", CultureInfo.InvariantCulture) ?? "?";
}
