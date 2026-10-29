using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Compliance;

/// <summary>A part master as the module needs it: identity and the markings every decision is made from.</summary>
internal sealed record PartRef(Guid Id, string Number, string Name, string Source, string? LatestRevision, Markings Markings);

/// <summary>A declaration row with its part's number and markings. Internal: callers outside the module get views after an access check.</summary>
internal sealed record DeclarationRow(
    Guid Id, string Number, Guid PartId, string PartNumber, Markings PartMarkings, string? RevisionScope, string Source, string? SupplierRef, string? MpnRef, decimal? ItemMassMg,
    DateOnly DeclaredOn, DateOnly ExpiresOn, string Status, DateTimeOffset? VerifiedAt, string? VerifiedBy, string? Note, string? WithdrawnReason, int Revision);

internal sealed record LineRow(
    int Ordinal, Guid? SubstanceId, string? SubstanceNumber, string? Cas, string? SubstanceName, Guid? MaterialId, string? MaterialCode, string? MaterialName, decimal? Percent, decimal? MassMg,
    string? ExemptionCode, bool? ConflictFree);

/// <summary>SQL and small helpers shared by the services of the module. Every statement here either is a key lookup or takes an access filter from its caller.</summary>
internal static class ComplianceStore
{
    public const string ListVersionObjectType = "reg_list_version";
    public const string PartObjectType = "part";

    public static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static decimal? Dec(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string objectType, string? id, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s),
        Action = action,
        Outcome = outcome,
        ObjectType = objectType,
        ObjectId = id,
        CorrelationId = correlationId,
        Details = AuditDetails.Of(details),
    };

    public static AuditEntry SystemEntry(string action, string objectType, string? id, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.System,
        Action = action,
        Outcome = AuditOutcome.Success,
        ObjectType = objectType,
        ObjectId = id,
        Details = AuditDetails.Of(details),
    };

    public static string Like(string text) => "%" + text.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

    public static async Task<long> NextValAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sequence, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT nextval('{sequence}')", conn, tx);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public static string NextNumber(string prefix, long seq) => prefix + seq.ToString("D6", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- parts (key lookups: the caller evaluates the markings)

    public static async Task<PartRef?> LoadPartAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string number, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT m.id, m.number, m.name, m.source,
                   (SELECT r.revision FROM plm.part_revision r WHERE r.master_id = m.id AND r.is_latest),
                   {MarkingColumns.Select("m")}
            FROM plm.part_master m WHERE m.number = @n{(forUpdate ? " FOR UPDATE OF m" : "")}
            """, conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new PartRef(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), Str(r, 4), MarkingColumns.Read(r, 5)) : null;
    }

    public static async Task<PartRef?> LoadPartByIdAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT m.id, m.number, m.name, m.source,
                   (SELECT r.revision FROM plm.part_revision r WHERE r.master_id = m.id AND r.is_latest),
                   {MarkingColumns.Select("m")}
            FROM plm.part_master m WHERE m.id = @id
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new PartRef(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), Str(r, 4), MarkingColumns.Read(r, 5)) : null;
    }

    // ---------------------------------------------------------------- declarations

    public static string DeclarationSelect(string d = "d", string m = "m") =>
        $"{d}.id, {d}.number, {d}.part_master_id, {m}.number, {MarkingColumns.Select(m)}, {d}.revision_scope, {d}.source, {d}.supplier_ref, {d}.mpn_ref, {d}.item_mass_mg, " +
        $"{d}.declared_on, {d}.expires_on, {d}.status, {d}.verified_at, {d}.verified_by, {d}.note, {d}.withdrawn_reason, {d}.revision";

    public static DeclarationRow ReadDeclaration(NpgsqlDataReader r)
    {
        var o = 4 + MarkingColumns.Count;
        return new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), MarkingColumns.Read(r, 4), Str(r, o), r.GetString(o + 1), Str(r, o + 2), Str(r, o + 3), Dec(r, o + 4),
            r.GetFieldValue<DateOnly>(o + 5), r.GetFieldValue<DateOnly>(o + 6), r.GetString(o + 7), r.IsDBNull(o + 8) ? null : r.GetFieldValue<DateTimeOffset>(o + 8), Str(r, o + 9), Str(r, o + 10),
            Str(r, o + 11), r.GetInt32(o + 12));
    }

    public static async Task<DeclarationRow?> LoadDeclarationAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string number, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT {DeclarationSelect()} FROM cmp.declaration d JOIN plm.part_master m ON m.id = d.part_master_id WHERE d.number = @n{(forUpdate ? " FOR UPDATE OF d" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadDeclaration(r) : null;
    }

    public static async Task<DeclarationRow?> LoadDeclarationByIdAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT {DeclarationSelect()} FROM cmp.declaration d JOIN plm.part_master m ON m.id = d.part_master_id WHERE d.id = @id{(forUpdate ? " FOR UPDATE OF d" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadDeclaration(r) : null;
    }

    /// <summary>The lines of declarations (by declaration id), with the names of substances and materials.</summary>
    public static async Task<Dictionary<Guid, List<LineRow>>> LoadLinesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, IReadOnlyCollection<Guid> declarationIds, CancellationToken ct)
    {
        var result = declarationIds.ToDictionary(i => i, _ => new List<LineRow>());
        if (declarationIds.Count == 0) return result;
        await using var cmd = new NpgsqlCommand(
            """
            SELECT l.declaration_id, l.ordinal, l.substance_id, s.number, s.cas_number, s.name, l.material_id, mt.code, mt.name, l.percent, l.mass_mg, l.exemption_code, l.conflict_free
            FROM cmp.declaration_line l
            LEFT JOIN cmp.substance s ON s.id = l.substance_id
            LEFT JOIN cmp.material mt ON mt.id = l.material_id
            WHERE l.declaration_id = ANY(@ids)
            ORDER BY l.declaration_id, l.ordinal
            """, conn, tx);
        cmd.Parameters.AddWithValue("ids", declarationIds.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result[r.GetGuid(0)].Add(new LineRow(r.GetInt32(1), r.IsDBNull(2) ? null : r.GetGuid(2), Str(r, 3), Str(r, 4), Str(r, 5), r.IsDBNull(6) ? null : r.GetGuid(6), Str(r, 7), Str(r, 8),
                Dec(r, 9), Dec(r, 10), Str(r, 11), r.IsDBNull(12) ? null : r.GetBoolean(12)));
        return result;
    }

    // ---------------------------------------------------------------- lists and materials

    /// <summary>
    /// The regulations in force on a date: for every active list the latest published version whose effective date has come (a version published for a later date does not apply
    /// yet), or the version asked for in <paramref name="pinned"/> (a published or superseded version number per list code).
    /// </summary>
    public static async Task<IReadOnlyList<ListVersionData>> LoadVersionsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, DateOnly asOf, IReadOnlyDictionary<string, int>? pinned, CancellationToken ct)
    {
        var heads = new List<(string Code, string Name, string Kind, Guid VersionId, int Version, DateOnly From)>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT l.code, l.name, l.kind, v.id, v.version, v.effective_from
            FROM cmp.reg_list l
            JOIN LATERAL (
                SELECT x.id, x.version, x.effective_from FROM cmp.reg_list_version x
                WHERE x.list_id = l.id AND x.status IN ('published', 'superseded')
                  AND CASE WHEN l.code = ANY(@pinCodes) THEN x.version = (@pinVersions)[array_position(@pinCodes, l.code)] ELSE x.effective_from <= @asOf END
                ORDER BY x.version DESC LIMIT 1) v ON true
            WHERE l.active
            ORDER BY l.code
            """, conn, tx))
        {
            var codes = pinned?.Keys.ToArray() ?? [];
            cmd.Parameters.AddWithValue("pinCodes", codes);
            cmd.Parameters.AddWithValue("pinVersions", pinned is null ? Array.Empty<int>() : codes.Select(c => pinned[c]).ToArray());
            cmd.Parameters.AddWithValue("asOf", asOf);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) heads.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetInt32(4), r.GetFieldValue<DateOnly>(5)));
        }
        var result = new List<ListVersionData>();
        foreach (var h in heads)
        {
            var entries = new Dictionary<Guid, ListEntry>();
            await using (var cmd = new NpgsqlCommand(
                "SELECT s.id, s.number, s.cas_number, s.name, e.threshold_pct, s.mineral FROM cmp.reg_list_entry e JOIN cmp.substance s ON s.id = e.substance_id WHERE e.version_id = @v", conn, tx))
            {
                cmd.Parameters.AddWithValue("v", h.VersionId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) entries[r.GetGuid(0)] = new ListEntry(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDecimal(4), Str(r, 5));
            }
            var exemptions = new List<ListExemption>();
            await using (var cmd = new NpgsqlCommand("SELECT code, substance_id, expires_on FROM cmp.reg_list_exemption WHERE version_id = @v", conn, tx))
            {
                cmd.Parameters.AddWithValue("v", h.VersionId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) exemptions.Add(new ListExemption(r.GetString(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.IsDBNull(2) ? null : r.GetFieldValue<DateOnly>(2)));
            }
            result.Add(new ListVersionData(h.Code, h.Name, h.Kind, h.VersionId, h.Version, h.From, entries, exemptions));
        }
        return result;
    }

    public static async Task<Dictionary<Guid, List<(Guid SubstanceId, decimal Fraction)>>> LoadMaterialCompositionsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, IReadOnlyCollection<Guid> materialIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, List<(Guid, decimal)>>();
        if (materialIds.Count == 0) return result;
        await using var cmd = new NpgsqlCommand("SELECT material_id, substance_id, fraction_pct FROM cmp.material_substance WHERE material_id = ANY(@ids)", conn, tx);
        cmd.Parameters.AddWithValue("ids", materialIds.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (!result.TryGetValue(r.GetGuid(0), out var list)) result[r.GetGuid(0)] = list = [];
            list.Add((r.GetGuid(1), r.GetDecimal(2)));
        }
        return result;
    }
}

/// <summary>The inputs of an evaluation, loaded once for a batch of declarations.</summary>
internal sealed record EvalContext(IReadOnlyList<ListVersionData> Versions, DateOnly Today, DateTimeOffset Now);

/// <summary>Evaluates declarations and writes the stored result and status. Runs in the caller's transaction.</summary>
internal static class ComplianceEvaluator
{
    public static async Task<EvalContext> ContextAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, TimeProvider clock, DateOnly? asOf, IReadOnlyDictionary<string, int>? pinned, CancellationToken ct)
    {
        var today = asOf ?? ComplianceStore.Today(clock);
        return new EvalContext(await ComplianceStore.LoadVersionsAsync(conn, tx, today, pinned, ct), today, clock.GetUtcNow());
    }

    /// <summary>The lines of a declaration with materials expanded into their substances. A line whose share cannot be turned into a percent contributes nothing.</summary>
    public static async Task<IReadOnlyList<ExpandedLine>> ExpandAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, IReadOnlyList<LineRow> lines, decimal? itemMassMg, CancellationToken ct)
    {
        var compositions = await ComplianceStore.LoadMaterialCompositionsAsync(conn, tx, [.. lines.Where(l => l.MaterialId is not null).Select(l => l.MaterialId!.Value).Distinct()], ct);
        return Expand(lines, itemMassMg, compositions);
    }

    public static IReadOnlyList<ExpandedLine> Expand(IReadOnlyList<LineRow> lines, decimal? itemMassMg, IReadOnlyDictionary<Guid, List<(Guid SubstanceId, decimal Fraction)>> compositions)
    {
        var result = new List<ExpandedLine>();
        foreach (var l in lines)
        {
            var pct = ComplianceRules.LinePercent(l.Percent, l.MassMg, itemMassMg);
            if (pct is null) continue;
            if (l.SubstanceId is { } sid) result.Add(new ExpandedLine(l.Ordinal, sid, pct.Value, l.ExemptionCode, l.ConflictFree));
            else if (l.MaterialId is { } mid && compositions.TryGetValue(mid, out var comp))
                foreach (var (substance, fraction) in comp) result.Add(new ExpandedLine(l.Ordinal, substance, pct.Value * fraction / 100m, l.ExemptionCode, l.ConflictFree));
        }
        return result;
    }

    public static IReadOnlyDictionary<string, ListEvaluation> EvaluateAll(EvalContext ctx, IReadOnlyList<ExpandedLine> lines) =>
        ctx.Versions.ToDictionary(v => v.Code, v => ComplianceRules.Evaluate(v, lines, ctx.Today), StringComparer.Ordinal);

    public static IReadOnlyList<FindingView> ToViews(EvalContext ctx, string declarationNumber, IReadOnlyDictionary<string, ListEvaluation> evaluations)
    {
        var views = new List<FindingView>();
        foreach (var v in ctx.Versions)
        {
            if (!evaluations.TryGetValue(v.Code, out var ev)) continue;
            foreach (var f in ev.Findings)
            {
                var e = v.Entries[f.SubstanceId];
                views.Add(new FindingView(v.Code, v.Version, declarationNumber, f.Ordinal, e.SubstanceNumber, e.Cas, e.Name, ComplianceRules.RoundPercent(f.Percent), f.ThresholdPct, f.Reason, f.ExemptionCode));
            }
        }
        return views;
    }

    /// <summary>Evaluates one declaration against the context, stores the result per regulation and the status derived from it. Returns the status change, if any.</summary>
    public static async Task<(string Status, bool Changed, string Previous)> RefreshAsync(NpgsqlConnection conn, NpgsqlTransaction tx, DeclarationRow row, EvalContext ctx, CancellationToken ct)
    {
        var lines = (await ComplianceStore.LoadLinesAsync(conn, tx, [row.Id], ct))[row.Id];
        var expanded = await ExpandAsync(conn, tx, lines, row.ItemMassMg, ct);
        var evaluations = EvaluateAll(ctx, expanded);
        foreach (var v in ctx.Versions)
        {
            var ev = evaluations[v.Code];
            await using var up = new NpgsqlCommand(
                """
                INSERT INTO cmp.evaluation (declaration_id, list_code, list_version, result, findings, evaluated_at) VALUES (@d, @c, @v, @r, @f, @now)
                ON CONFLICT (declaration_id, list_code) DO UPDATE SET list_version = @v, result = @r, findings = @f, evaluated_at = @now
                """, conn, tx);
            up.Parameters.AddWithValue("d", row.Id);
            up.Parameters.AddWithValue("c", v.Code);
            up.Parameters.AddWithValue("v", v.Version);
            up.Parameters.AddWithValue("r", ev.Result);
            up.Parameters.AddWithValue("f", ev.Findings.Count);
            up.Parameters.AddWithValue("now", ctx.Now);
            await up.ExecuteNonQueryAsync(ct);
        }
        await using (var del = new NpgsqlCommand("DELETE FROM cmp.evaluation WHERE declaration_id = @d AND list_code <> ALL(@codes)", conn, tx))
        {
            del.Parameters.AddWithValue("d", row.Id);
            del.Parameters.AddWithValue("codes", ctx.Versions.Select(v => v.Code).ToArray());
            await del.ExecuteNonQueryAsync(ct);
        }
        var violates = evaluations.Values.Any(e => e.Result == EvaluationResults.NonCompliant);
        var status = ComplianceRules.DeclarationStatus(row.Status == DeclarationStatuses.Withdrawn, row.VerifiedAt is not null, row.ExpiresOn, ctx.Today, violates);
        if (status != row.Status)
        {
            await using var upd = new NpgsqlCommand("UPDATE cmp.declaration SET status = @s WHERE id = @id", conn, tx);
            upd.Parameters.AddWithValue("s", status);
            upd.Parameters.AddWithValue("id", row.Id);
            await upd.ExecuteNonQueryAsync(ct);
        }
        return (status, status != row.Status, row.Status);
    }

    /// <summary>
    /// Re-evaluates every declaration that is not withdrawn (after a list was published, or by the expiry job) and returns the status changes. A declaration whose status
    /// became non-compliant or expired is what the watchers of its part are told about.
    /// </summary>
    public static async Task<IReadOnlyList<(DeclarationRow Row, string From, string To)>> RefreshAllAsync(NpgsqlConnection conn, NpgsqlTransaction tx, EvalContext ctx, CancellationToken ct)
    {
        var ids = new List<Guid>();
        await using (var cmd = new NpgsqlCommand("SELECT id FROM cmp.declaration WHERE status <> 'withdrawn' ORDER BY number FOR UPDATE", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) ids.Add(r.GetGuid(0));
        var changes = new List<(DeclarationRow, string, string)>();
        foreach (var id in ids)
        {
            var row = (await ComplianceStore.LoadDeclarationByIdAsync(conn, tx, id, false, ct))!;
            var (status, changed, previous) = await RefreshAsync(conn, tx, row, ctx, ct);
            if (changed) changes.Add((row, previous, status));
        }
        return changes;
    }
}
