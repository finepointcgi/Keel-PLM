using System.Globalization;
using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

public sealed record EffectivityBlock(string Name, int Number);

/// <summary>An end-item context ("ORCA-POD"): what a serial number or lot is a number of. Named blocks map to lot numbers.</summary>
public sealed record EffectivityContextView(string Id, string Name, string Description, string? Program, IReadOnlyList<EffectivityBlock> Blocks, DateTimeOffset CreatedAt, string CreatedBy);

public sealed record CreateContextCommand(string Id, string Name, string Description = "", string? Program = null, IReadOnlyList<EffectivityBlock>? Blocks = null);

/// <summary>A revision of a part with the units it serves (null: not restricted by effectivity).</summary>
public sealed record RevisionEffectivityRow(string Revision, string State, string StateLabel, bool IsLatest, EffectivityRange? Range, string? SourceChange);

public sealed record PartEffectivity(string Number, string Name, IReadOnlyList<RevisionEffectivityRow> Revisions, string Banner);

/// <summary>
/// Effectivity contexts and the effectivity of a part's revisions (ADR-0021). Contexts are created by users who may modify data; a context tied to a
/// program is visible only to holders of that program (the program name is a marking). Every call is audited, denials included.
/// </summary>
public sealed partial class EffectivityService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock)
{
    public const string ObjectType = "effectivity_context";

    [GeneratedRegex("^[A-Z][A-Z0-9-]{1,39}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._-]{0,39}$")]
    private static partial Regex BlockPattern();

    public async Task<IReadOnlyList<EffectivityContextView>> ListContextsAsync(Subject subject, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var list = new List<EffectivityContextView>();
        var ids = new List<string>();
        await using (var cmd = new NpgsqlCommand("SELECT id FROM cfg.effectivity_context WHERE program IS NULL OR program = ANY(@programs) ORDER BY id", conn, tx))
        {
            cmd.Parameters.AddWithValue("programs", subject.Programs.ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add(r.GetString(0));
        }
        foreach (var id in ids)
            if (await LoadViewAsync(conn, tx, subject, id, ct) is { } v) list.Add(v);
        await audit.AppendAsync(tx, Entry(subject, "effectivity.context_list", AuditOutcome.Success, null, correlationId, ("count", list.Count.ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return list;
    }

    private static async Task<EffectivityContextView?> LoadViewAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string id, CancellationToken ct)
    {
        var row = await EffectivityStore.LoadContextAsync(conn, tx, id, subject, ct);
        if (row is null) return null;
        DateTimeOffset at;
        string by;
        await using (var cmd = new NpgsqlCommand("SELECT created_at, created_by FROM cfg.effectivity_context WHERE id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            (at, by) = (r.GetFieldValue<DateTimeOffset>(0), r.GetString(1));
        }
        return new EffectivityContextView(row.Id, row.Name, row.Description, row.Program, [.. row.Blocks.Select(b => new EffectivityBlock(b.Key, b.Value)).OrderBy(b => b.Number)], at, by);
    }

    public async Task<Outcome<EffectivityContextView>> GetContextAsync(Subject subject, string id, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var view = await LoadViewAsync(conn, tx, subject, (id ?? "").Trim().ToUpperInvariant(), ct);
        await audit.AppendAsync(tx, Entry(subject, "effectivity.context_read", view is null ? AuditOutcome.Denied : AuditOutcome.Success, id, correlationId), ct);
        await tx.CommitAsync(ct);
        return view is null ? Outcome<EffectivityContextView>.NotFound() : Outcome<EffectivityContextView>.Ok(view);
    }

    public async Task<Outcome<EffectivityContextView>> CreateContextAsync(Subject subject, CreateContextCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        var id = (cmd.Id ?? "").Trim().ToUpperInvariant();
        var errors = new List<string>();
        if (!IdPattern().IsMatch(id)) errors.Add("The context id is 2 to 40 characters: capital letters, digits and '-', starting with a letter (for example ORCA-POD).");
        if (string.IsNullOrWhiteSpace(cmd.Name) || cmd.Name.Trim().Length > 200) errors.Add("The name is required and at most 200 characters.");
        if ((cmd.Description?.Length ?? 0) > 2000) errors.Add("The description must be at most 2000 characters.");
        var blocks = cmd.Blocks ?? [];
        foreach (var b in blocks)
            if (!BlockPattern().IsMatch(b.Name ?? "") || b.Number < 1) errors.Add($"Block '{b.Name}': give a name (letters, digits, space, '.', '_', '-') and a number of 1 or more.");
        if (blocks.GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1) || blocks.GroupBy(b => b.Number).Any(g => g.Count() > 1))
            errors.Add("Block names and numbers must be unique within a context.");
        if (errors.Count > 0) return Outcome<EffectivityContextView>.Invalid(errors);

        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Modify))
        {
            await audit.AppendAsync(Entry(subject, "effectivity.context_create", AuditOutcome.Denied, id, correlationId, ("reasons", "role.modify")), ct);
            return Outcome<EffectivityContextView>.Forbidden(["role.modify"]);
        }
        var program = string.IsNullOrWhiteSpace(cmd.Program) ? null : cmd.Program.Trim();
        if (program is not null && !subject.Programs.Contains(program))
        {
            await audit.AppendAsync(Entry(subject, "effectivity.context_create", AuditOutcome.Denied, id, correlationId, ("reasons", "program.not_held")), ct);
            return Outcome<EffectivityContextView>.Forbidden(["program.not_held"]);
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var ins = new NpgsqlCommand(
                "INSERT INTO cfg.effectivity_context (id, name, description, program, created_at, created_by) VALUES (@id, @name, @desc, @program, @at, @by)", conn, tx))
            {
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("name", cmd.Name.Trim());
                ins.Parameters.AddWithValue("desc", cmd.Description?.Trim() ?? "");
                ins.Parameters.Add(new NpgsqlParameter("program", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)program ?? DBNull.Value });
                ins.Parameters.AddWithValue("at", clock.GetUtcNow());
                ins.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
                await ins.ExecuteNonQueryAsync(ct);
            }
            foreach (var b in blocks)
            {
                await using var bi = new NpgsqlCommand("INSERT INTO cfg.effectivity_block (context_id, name, number) VALUES (@c, @n, @num)", conn, tx);
                bi.Parameters.AddWithValue("c", id);
                bi.Parameters.AddWithValue("n", b.Name.Trim());
                bi.Parameters.AddWithValue("num", b.Number);
                await bi.ExecuteNonQueryAsync(ct);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync(ct);
            await audit.AppendAsync(Entry(subject, "effectivity.context_create", AuditOutcome.Failure, id, correlationId, ("error", "exists")), ct);
            return Outcome<EffectivityContextView>.Conflict($"The context '{id}' already exists.");
        }
        await audit.AppendAsync(tx, Entry(subject, "effectivity.context_create", AuditOutcome.Success, id, correlationId, ("name", cmd.Name.Trim()), ("program", program), ("blocks", blocks.Count.ToString(CultureInfo.InvariantCulture))), ct);
        var view = (await LoadViewAsync(conn, tx, subject, id, ct))!;
        await tx.CommitAsync(ct);
        return Outcome<EffectivityContextView>.Ok(view);
    }

    // ---------------------------------------------------------------- revisions of a part

    /// <summary>Which units each revision of a part serves. Needs Read on the part.</summary>
    public async Task<Outcome<PartEffectivity>> GetPartAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid master;
        string name, lifecycle;
        Markings markings;
        await using (var cmd = new NpgsqlCommand($"SELECT m.id, m.name, m.lifecycle_id, {MarkingColumns.Select("m")} FROM plm.part_master m WHERE m.number = @n", conn, tx))
        {
            cmd.Parameters.AddWithValue("n", number);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Outcome<PartEffectivity>.NotFound();
            (master, name, lifecycle, markings) = (r.GetGuid(0), r.GetString(1), r.GetString(2), MarkingColumns.Read(r, 3));
        }
        var read = AccessPolicy.Evaluate(subject, markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, new AuditEntry
            {
                Actor = ActorSnapshot.From(subject), Action = "effectivity.part_read", Outcome = AuditOutcome.Denied, ObjectType = BomService.ObjectType, ObjectId = number,
                CorrelationId = correlationId, Details = AuditDetails.Of(("reasons", string.Join(",", read.Reasons))),
            }, ct);
            await tx.CommitAsync(ct);
            return Outcome<PartEffectivity>.NotFound();
        }
        var rows = new List<RevisionEffectivityRow>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT r.revision, r.lifecycle_state, COALESCE(s.label, r.lifecycle_state), r.is_latest, e.eff_kind, e.eff_context, e.eff_from, e.eff_to, e.source_change
            FROM plm.part_revision r
            LEFT JOIN plm.lifecycle_state s ON s.lifecycle_id = @lc AND s.state = r.lifecycle_state
            LEFT JOIN cfg.revision_effectivity e ON e.revision_id = r.id
            WHERE r.master_id = @m ORDER BY length(r.revision), r.revision
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("m", master);
            cmd.Parameters.AddWithValue("lc", lifecycle);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new RevisionEffectivityRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), EffectivityStore.ReadRange(r, 4), r.IsDBNull(8) ? null : r.GetString(8)));
        }
        await audit.AppendAsync(tx, new AuditEntry
        {
            Actor = ActorSnapshot.From(subject), Action = "effectivity.part_read", Outcome = AuditOutcome.Success, ObjectType = BomService.ObjectType, ObjectId = number,
            CorrelationId = correlationId, Details = AuditDetails.Of(("revisions", rows.Count.ToString(CultureInfo.InvariantCulture))),
        }, ct);
        await tx.CommitAsync(ct);
        return Outcome<PartEffectivity>.Ok(new PartEffectivity(number, name, rows, markings.BannerText()));
    }

    private static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string? id, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s),
        Action = action,
        Outcome = outcome,
        ObjectType = ObjectType,
        ObjectId = id,
        CorrelationId = correlationId,
        Details = AuditDetails.Of(details),
    };
}
