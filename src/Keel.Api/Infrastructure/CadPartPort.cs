using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Bom;
using Keel.Core;
using Keel.Parts;
using Keel.Security;
using Npgsql;

namespace Keel.Api.Infrastructure;

/// <summary>
/// The part and BOM side of CAD integration (ADR-0035), composed here because neither module may reference the other. Everything that changes data is
/// the owning module's own command: <see cref="PartService"/> (check-out, check-in, create) and <see cref="BomService"/> (working-copy edits).
/// </summary>
public sealed class CadPartPort(NpgsqlDataSource dataSource, PartService partService, BomService bom) : ICadPartPort
{
    private const string Select =
        "m.id, m.number, m.name, m.part_type, m.default_unit, m.source, m.lifecycle_id, r.revision, r.latest_iteration, r.lifecycle_state";

    public Task<IReadOnlyList<CadPartInfo>> FindManyAsync(Subject subject, IReadOnlyList<string> numbers, CancellationToken ct = default) =>
        QueryAsync(subject, "m.number = ANY(@arg)", numbers.ToArray(), null, ct);

    public Task<IReadOnlyList<CadPartInfo>> FindByIdsAsync(Subject subject, IReadOnlyList<Guid> ids, CancellationToken ct = default) =>
        QueryAsync(subject, "m.id = ANY(@arg)", ids.ToArray(), null, ct);

    public async Task<IReadOnlyList<CadPartInfo>> FindByAttributeAsync(Subject subject, string attribute, string value, int limit, CancellationToken ct = default)
    {
        if (attribute.Length is 0 or > 60) return [];
        return await QueryAsync(subject, "i.attributes ->> @attr = @arg", value, (attribute, Math.Clamp(limit, 1, 20)), ct);
    }

    private async Task<IReadOnlyList<CadPartInfo>> QueryAsync(Subject subject, string predicate, object arg, (string Attribute, int Limit)? attribute, CancellationToken ct)
    {
        if (arg is Array { Length: 0 }) return [];
        var filter = AccessPolicy.BuildReadFilter(subject, "m");
        var list = new List<CadPartInfo>();
        var lifecycles = new Dictionary<string, Lifecycle?>(StringComparer.Ordinal);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT {Select}, {MarkingColumns.Select("m")}
            FROM plm.part_master m
            JOIN plm.part_revision r ON r.master_id = m.id AND r.is_latest
            JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            WHERE {filter.Sql} AND {predicate}
            ORDER BY m.number
            LIMIT {(attribute?.Limit ?? 5000).ToString(CultureInfo.InvariantCulture)}
            """, conn);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("arg", arg);
        if (attribute is { } a) cmd.Parameters.AddWithValue("attr", a.Attribute);
        var rows = new List<(CadPartInfo Info, string LifecycleId)>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var m = MarkingColumns.Read(r, 10);
                rows.Add((new CadPartInfo(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(7), r.GetInt32(8), r.GetString(9),
                    false, m.BannerText(), m), r.GetString(6)));
            }
        foreach (var (info, lifecycleId) in rows)
        {
            if (!lifecycles.TryGetValue(lifecycleId, out var lc)) lifecycles[lifecycleId] = lc = await Lifecycle.LoadAsync(conn, null, lifecycleId, ct);
            list.Add(info with { IsLocked = lc?.IsLocked(info.State) ?? true });
        }
        return list;
    }

    public async Task<Outcome<CadPartInfo>> CreateAsync(Subject subject, CadPartCreate part, string? correlationId, CancellationToken ct = default)
    {
        JsonObject? attributes = null;
        if (part.Attributes is { Count: > 0 })
        {
            // CAD attributes arrive as text; the administrator's definitions say which are numbers and flags.
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            var defs = (await AttributeValidator.LoadAsync(conn, null, PartService.ObjectType, ct)).ToDictionary(d => d.Name, StringComparer.Ordinal);
            attributes = [];
            foreach (var (k, v) in part.Attributes)
            {
                if (!defs.TryGetValue(k, out var def)) return Outcome<CadPartInfo>.Invalid([$"'{k}' is not a part attribute defined by your administrator."]);
                attributes[k] = def.DataType switch
                {
                    "number" when decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) => JsonValue.Create(n),
                    "boolean" when bool.TryParse(v, out var b) => JsonValue.Create(b),
                    _ => JsonValue.Create(v),
                };
            }
        }
        var created = await partService.CreateAsync(subject, new CreatePartCommand(part.Name, part.PartType, part.Unit, part.Source, part.Markings, part.Description, attributes), correlationId, ct);
        if (!created.IsOk) return new Outcome<CadPartInfo>(created.Kind, null, created.Errors);
        var d = created.Value!;
        return Outcome<CadPartInfo>.Ok(new CadPartInfo(d.Id, d.Number, d.Name, d.PartType, d.DefaultUnit, d.Source, d.Revision.Revision, d.Iteration.Iteration, d.Revision.State, d.Revision.IsLocked, d.Banner, d.Markings));
    }

    public async Task<Outcome<CadBomSnapshot>> GetBomAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        var detail = await partService.GetAsync(subject, number, null, correlationId, ct);
        if (!detail.IsOk) return new Outcome<CadBomSnapshot>(detail.Kind, null, detail.Errors);
        var d = detail.Value!;
        var explosion = await bom.ExplodeAsync(subject, new BomQuery(number, Depth: 1), correlationId, ct);
        if (!explosion.IsOk) return new Outcome<CadBomSnapshot>(explosion.Kind, null, explosion.Errors);
        var lines = explosion.Value!.Lines.Where(l => !l.Redacted && !l.Unresolved && l.Number is not null)
            .Select(l => new CadBomLine(l.Number!, l.Name, l.Quantity, l.Unit, l.FindNumber, l.ReferenceDesignators, l.Notes)).ToList();
        return Outcome<CadBomSnapshot>.Ok(new CadBomSnapshot(number, d.Revision.Revision, d.Iteration.Iteration, d.Revision.IsLocked,
            d.Checkout?.UserName ?? d.Checkout?.UserId, lines, explosion.Value.Lines.Count(l => l.Redacted)));
    }

    public async Task<Outcome<string>> ApplyBomAsync(Subject subject, string parentNumber, IReadOnlyList<CadBomEdit> edits, string comment, string? correlationId, CancellationToken ct = default)
    {
        var checkedOut = await partService.CheckOutAsync(subject, parentNumber, correlationId, ct);
        if (!checkedOut.IsOk) return new Outcome<string>(checkedOut.Kind, null, checkedOut.Errors);
        async Task<Outcome<string>> Fail(OutcomeKind kind, IReadOnlyList<string> errors)
        {
            await partService.UndoCheckOutAsync(subject, parentNumber, correlationId, CancellationToken.None);
            return new Outcome<string>(kind == OutcomeKind.Ok ? OutcomeKind.Conflict : kind, null, errors.Count == 0 ? ["The change was refused."] : errors);
        }
        var working = await bom.GetWorkingAsync(subject, parentNumber, correlationId, ct);
        if (!working.IsOk) return await Fail(working.Kind, working.Errors);
        var current = working.Value!.Lines.ToList();
        foreach (var e in edits)
        {
            switch (e.Kind)
            {
                case CadBomEditKind.Add:
                {
                    var r = await bom.AddUsageAsync(subject, parentNumber, new AddUsageCommand(e.ChildNumber, e.Quantity, string.IsNullOrEmpty(e.Unit) ? null : e.Unit, e.FindNumber, e.ReferenceDesignators, e.Notes), correlationId, ct);
                    if (!r.IsOk) return await Fail(r.Kind, r.Errors);
                    break;
                }
                case CadBomEditKind.Update or CadBomEditKind.Remove:
                {
                    var line = current.FirstOrDefault(l => l.ChildNumber == e.ChildNumber && (e.CurrentFindNumber is null || l.FindNumber == e.CurrentFindNumber));
                    if (line is null) return await Fail(OutcomeKind.Conflict, [$"The working copy has no line for {e.ChildNumber}."]);
                    if (e.Kind == CadBomEditKind.Remove)
                    {
                        var r = await bom.RemoveUsageAsync(subject, parentNumber, line.Id, correlationId, ct);
                        if (!r.IsOk) return await Fail(r.Kind, r.Errors);
                    }
                    else
                    {
                        var r = await bom.UpdateUsageAsync(subject, parentNumber, line.Id,
                            new UpdateUsageCommand(e.Quantity, string.IsNullOrEmpty(e.Unit) ? line.Unit : e.Unit, e.FindNumber ?? line.FindNumber, e.ReferenceDesignators, e.Notes), correlationId, ct);
                        if (!r.IsOk) return await Fail(r.Kind, r.Errors);
                    }
                    break;
                }
            }
        }
        var checkedIn = await partService.CheckInAsync(subject, parentNumber, comment, correlationId, ct);
        if (!checkedIn.IsOk) return await Fail(checkedIn.Kind, checkedIn.Errors);
        return Outcome<string>.Ok(checkedIn.Value!.Iteration.Label);
    }
}
