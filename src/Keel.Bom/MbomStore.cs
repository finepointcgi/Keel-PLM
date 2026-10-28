using System.Data.Common;
using System.Globalization;
using Keel.Core;
using Keel.Security;
using Npgsql;
using NpgsqlTypes;

namespace Keel.Bom;

/// <summary>The stored header of an MBOM with its latest revision and its check-out, if any.</summary>
internal sealed record MbomHead(
    Guid Id, string Number, Guid PartMasterId, string PartNumber, string PartName, string PartType, string PartSource, Markings PartMarkings, string Plant, string LifecycleId,
    Markings Markings, DateTimeOffset CreatedAt, string CreatedBy, Guid RevisionId, string Revision, string State, int LatestIteration, Guid IterationId,
    string? CheckoutUser, string? CheckoutUserName, DateTimeOffset? CheckoutAt)
{
    public string Label => $"{Revision}.{LatestIteration.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>A line to write into an iteration, with its links. Used by check-in, generate, regenerate and revise: one write path.</summary>
internal sealed record NewMbomLine(
    Guid Id, Guid Child, decimal Quantity, string Unit, int Find, string[] Designators, decimal? Scrap, bool Phantom, string SupplyType, string? OperationRef, bool MbomOnly,
    string? Notes, EffectivityRange? Eff, DateTimeOffset CreatedAt, string CreatedBy, IReadOnlyList<LinkInput> Links);

internal sealed record PartRef(Guid Id, string Number, string Name, string PartType, string Source, string DefaultUnit, Markings Markings);

internal sealed record ResolvedEbom(Guid MasterId, string PartNumber, Guid RevisionId, Guid IterationId, string Label, string State, EbomSpec Spec)
{
    public string Description => $"{Spec.Describe()} ({PartNumber} {Label})";
}

/// <summary>Shared SQL of the MBOM module: head loading, line writing, hashing, EBOM resolution.</summary>
internal static class MbomStore
{
    private const string HeadColumns =
        "mm.id, mm.number, mm.part_master_id, mm.plant, mm.lifecycle_id, mm.created_at, mm.created_by, r.id, r.revision, r.lifecycle_state, r.latest_iteration, i.id, c.user_id, c.user_name, c.checked_out_at";

    private static async Task<MbomHead?> ReadHeadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        Guid id, part, rid, iid;
        string number, plant, lifecycle, createdBy, revision, state;
        DateTimeOffset createdAt;
        int latest;
        Markings markings;
        string? coUser, coName;
        DateTimeOffset? coAt;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            (id, number, part, plant, lifecycle) = (r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4));
            (createdAt, createdBy, rid, revision, state, latest, iid) = (r.GetFieldValue<DateTimeOffset>(5), r.GetString(6), r.GetGuid(7), r.GetString(8), r.GetString(9), r.GetInt32(10), r.GetGuid(11));
            (coUser, coName, coAt) = (r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14));
            markings = MarkingColumns.Read(r, 15);
        }
        var p = await LoadPartAsync(cmd.Connection!, cmd.Transaction, part, ct) ?? throw new InvalidOperationException("The part of an MBOM is missing.");
        return new MbomHead(id, number, part, p.Number, p.Name, p.PartType, p.Source, p.Markings, plant, lifecycle, markings, createdAt, createdBy, rid, revision, state, latest, iid, coUser, coName, coAt);
    }

    /// <summary>The MBOM with this number at its latest revision, or at the named revision.</summary>
    public static async Task<MbomHead?> LoadByNumberAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string number, string? revision, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT {HeadColumns}, {MarkingColumns.Select("mm")}
            FROM mbom.mbom mm
            JOIN mbom.revision r ON r.mbom_id = mm.id AND ((@rev::text IS NULL AND r.is_latest) OR r.revision = @rev)
            JOIN mbom.iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            LEFT JOIN mbom.checkout c ON c.revision_id = r.id
            WHERE mm.number = @n{(forUpdate ? " FOR UPDATE OF mm" : "")}
            """, conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        cmd.Parameters.Add(new NpgsqlParameter("rev", NpgsqlDbType.Text) { Value = (object?)revision ?? DBNull.Value });
        return await ReadHeadAsync(cmd, ct);
    }

    /// <summary>The MBOM that owns a revision (the revision the workflow is bound to).</summary>
    public static async Task<MbomHead?> LoadByRevisionAsync(DbConnection conn, DbTransaction? tx, Guid revisionId, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT {HeadColumns}, {MarkingColumns.Select("mm")}
            FROM mbom.revision r
            JOIN mbom.mbom mm ON mm.id = r.mbom_id
            JOIN mbom.iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            LEFT JOIN mbom.checkout c ON c.revision_id = r.id
            WHERE r.id = @rid{(forUpdate ? " FOR UPDATE OF mm" : "")}
            """, (NpgsqlConnection)conn, (NpgsqlTransaction?)tx);
        cmd.Parameters.AddWithValue("rid", revisionId);
        return await ReadHeadAsync(cmd, ct);
    }

    /// <summary>The MBOM of a part in a plant (latest revision), or null.</summary>
    public static async Task<MbomHead?> LoadByPartAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid partMasterId, string plant, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT {HeadColumns}, {MarkingColumns.Select("mm")}
            FROM mbom.mbom mm
            JOIN mbom.revision r ON r.mbom_id = mm.id AND r.is_latest
            JOIN mbom.iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            LEFT JOIN mbom.checkout c ON c.revision_id = r.id
            WHERE mm.part_master_id = @pm AND mm.plant = @plant{(forUpdate ? " FOR UPDATE OF mm" : "")}
            """, conn, tx);
        cmd.Parameters.AddWithValue("pm", partMasterId);
        cmd.Parameters.AddWithValue("plant", plant);
        return await ReadHeadAsync(cmd, ct);
    }

    public static async Task<PartRef?> LoadPartAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid masterId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT m.number, m.name, m.part_type, m.source, m.default_unit, {MarkingColumns.Select("m")} FROM plm.part_master m WHERE m.id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", masterId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new PartRef(masterId, r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), MarkingColumns.Read(r, 5)) : null;
    }

    public static async Task<PartRef?> LoadPartByNumberAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string number, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT m.id, m.name, m.part_type, m.source, m.default_unit, {MarkingColumns.Select("m")} FROM plm.part_master m WHERE m.number = @n", conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new PartRef(r.GetGuid(0), number, r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), MarkingColumns.Read(r, 5)) : null;
    }

    public static async Task<PlantInfo?> LoadPlantAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string code, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT code, name, is_active, release_allows_warnings, sort_order FROM mbom.plant WHERE code = @c", conn, tx);
        cmd.Parameters.AddWithValue("c", code);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new PlantInfo(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.GetInt32(4)) : null;
    }

    public static async Task<IReadOnlyList<string>> ActiveSupplyTypesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var list = new List<string>();
        await using var cmd = new NpgsqlCommand("SELECT code FROM mbom.supply_type WHERE is_active ORDER BY sort_order, code", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    // ---------------------------------------------------------------- EBOM resolution

    /// <summary>
    /// The iteration of a part's EBOM a spec selects. The error is user-facing. A baseline spec needs the baseline to be readable and to name the part; it reads
    /// the baseline's items through the part read filter like every item read.
    /// </summary>
    public static async Task<(ResolvedEbom? Value, string? Error)> ResolveEbomAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject? subject, PartRef part, EbomSpec spec, CancellationToken ct)
    {
        var errors = spec.Validate();
        if (errors.Count > 0) return (null, errors[0]);
        string? sql;
        switch (spec.Kind)
        {
            case EbomSpecKind.Latest:
                sql = "SELECT r.id, i.id, r.revision, i.iteration, r.lifecycle_state FROM plm.part_revision r JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration WHERE r.master_id = @m AND r.is_latest";
                break;
            case EbomSpecKind.LatestReleased:
                sql = "SELECT r.id, i.id, r.revision, i.iteration, r.lifecycle_state FROM plm.part_revision r JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration WHERE r.master_id = @m AND r.lifecycle_state = 'Released' ORDER BY length(r.revision) DESC, r.revision DESC LIMIT 1";
                break;
            case EbomSpecKind.Effectivity:
                sql = $"SELECT r.id, i.id, r.revision, i.iteration, r.lifecycle_state FROM plm.part_revision r JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration LEFT JOIN cfg.revision_effectivity e ON e.revision_id = r.id WHERE r.master_id = @m AND r.lifecycle_state = 'Released' ORDER BY (e.revision_id IS NOT NULL AND {BomSql.Covers("e")}) DESC, length(r.revision) DESC, r.revision DESC LIMIT 1";
                break;
            case EbomSpecKind.Iteration:
                sql = "SELECT r.id, i.id, r.revision, i.iteration, r.lifecycle_state FROM plm.part_revision r JOIN plm.part_iteration i ON i.revision_id = r.id WHERE r.master_id = @m AND r.revision = @rev AND i.iteration = @it";
                break;
            default:
                return await ResolveFromBaselineAsync(conn, tx, subject, part, spec, ct);
        }
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("m", part.Id);
        if (spec.Kind == EbomSpecKind.Effectivity) BomService.BindEffectivity(cmd, spec.Unit);
        if (spec.Kind == EbomSpecKind.Iteration)
        {
            var match = MbomRules.IterationLabel.Match(spec.IterationLabel!);
            cmd.Parameters.AddWithValue("rev", match.Groups[1].Value);
            cmd.Parameters.AddWithValue("it", int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        }
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return (null, spec.Kind == EbomSpecKind.Iteration ? $"{part.Number} has no version {spec.IterationLabel}."
                : $"{part.Number} has no {(spec.Kind == EbomSpecKind.Latest ? "revision" : "released revision")}{(spec.Kind == EbomSpecKind.Effectivity ? " for this unit" : "")} to take the EBOM from.");
        return (new ResolvedEbom(part.Id, part.Number, r.GetGuid(0), r.GetGuid(1), $"{r.GetString(2)}.{r.GetInt32(3).ToString(CultureInfo.InvariantCulture)}", r.GetString(4), spec), null);
    }

    private static async Task<(ResolvedEbom?, string?)> ResolveFromBaselineAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject? subject, PartRef part, EbomSpec spec, CancellationToken ct)
    {
        if (subject is null) throw new InvalidOperationException("Resolving a baseline needs the subject whose read access applies.");
        var baseline = await BaselineStore.LoadByNumberAsync(conn, tx, spec.Baseline!.Trim(), false, ct);
        if (baseline is null || !AccessPolicy.Evaluate(subject, baseline.Markings, PlmAction.Read).Permitted)
            return (null, $"Baseline '{spec.Baseline}' was not found, or you are not authorized to see it.");
        var filter = AccessPolicy.BuildReadFilter(subject, "cm");
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT bi.revision_id, bi.iteration_id, r.revision, i.iteration, r.lifecycle_state
            FROM cfg.baseline_item bi
            JOIN plm.part_master cm ON cm.id = bi.master_id AND {filter.Sql}
            JOIN plm.part_revision r ON r.id = bi.revision_id
            JOIN plm.part_iteration i ON i.id = bi.iteration_id
            WHERE bi.baseline_id = @b AND bi.master_id = @m
            ORDER BY bi.seq LIMIT 1
            """, conn, tx);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("b", baseline.Id);
        cmd.Parameters.AddWithValue("m", part.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (null, $"Baseline {baseline.Number} does not contain {part.Number}.");
        return (new ResolvedEbom(part.Id, part.Number, r.GetGuid(0), r.GetGuid(1), $"{r.GetString(2)}.{r.GetInt32(3).ToString(CultureInfo.InvariantCulture)}", r.GetString(4), spec), null);
    }

    public static async Task<string?> IterationLabelAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid partIterationId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT r.revision || '.' || i.iteration FROM plm.part_iteration i JOIN plm.part_revision r ON r.id = i.revision_id WHERE i.id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", partIterationId);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    /// <summary>The part master that owns a part iteration, with its current latest iteration (for staleness).</summary>
    public static async Task<(Guid Master, Guid LatestIteration, string LatestLabel)?> OwnerOfIterationAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid partIterationId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT r.master_id, li.id, lr.revision || '.' || li.iteration
            FROM plm.part_iteration i
            JOIN plm.part_revision r ON r.id = i.revision_id
            JOIN plm.part_revision lr ON lr.master_id = r.master_id AND lr.is_latest
            JOIN plm.part_iteration li ON li.revision_id = lr.id AND li.iteration = lr.latest_iteration
            WHERE i.id = @id
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", partIterationId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetGuid(0), r.GetGuid(1), r.GetString(2)) : null;
    }

    public static async Task<List<EbomLineData>> LoadEbomLinesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid partIteration, EffectivityRequest? unit, CancellationToken ct)
    {
        var list = new List<EbomLineData>();
        await using var cmd = new NpgsqlCommand(
            $"SELECT u.id, u.find_number, u.alt, u.child_master_id, u.quantity, u.unit, u.eff_kind, u.eff_context, u.eff_from, u.eff_to FROM plm.bom_usage u WHERE u.parent_iteration_id = @it{(unit is null ? "" : " AND " + BomSql.UsageApplies("u"))}", conn, tx);
        cmd.Parameters.AddWithValue("it", partIteration);
        if (unit is not null) BomService.BindEffectivity(cmd, unit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new EbomLineData(r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), r.GetGuid(3), r.GetDecimal(4), r.GetString(5), EffectivityStore.ReadRange(r, 6)));
        return list;
    }

    // ---------------------------------------------------------------- reading stored content (system view, no access filter: callers hash, copy or reconcile)

    /// <summary>All lines and links of an iteration (alt from storage) or of a working copy (alt as the next write will assign it).</summary>
    public static async Task<List<NewMbomLine>> ReadContentAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, bool working, Guid id, CancellationToken ct)
    {
        var lines = new List<(NewMbomLine Line, int Alt)>();
        var byId = new Dictionary<Guid, int>();
        var linkRows = new List<(Guid Line, LinkInput Link)>();
        var altSql = working ? "(row_number() OVER (PARTITION BY find_number ORDER BY eff_from NULLS FIRST, created_at, id) - 1)::int" : "alt";
        var table = working ? "mbom.working_usage" : "mbom.usage";
        var key = working ? "checkout_revision_id" : "iteration_id";
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT id, child_master_id, quantity, unit, find_number, reference_designators, scrap_factor, phantom, supply_type, operation_ref, mbom_only, notes,
                   eff_kind, eff_context, eff_from, eff_to, created_at, created_by, {altSql}
            FROM {table} WHERE {key} = @id ORDER BY find_number, {(working ? "eff_from NULLS FIRST" : "alt")}, created_at, id
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                lines.Add((new NewMbomLine(r.GetGuid(0), r.GetGuid(1), r.GetDecimal(2), r.GetString(3), r.GetInt32(4), r.GetFieldValue<string[]>(5),
                    r.IsDBNull(6) ? null : r.GetDecimal(6), r.GetBoolean(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetBoolean(10),
                    r.IsDBNull(11) ? null : r.GetString(11), EffectivityStore.ReadRange(r, 12), r.GetFieldValue<DateTimeOffset>(16), r.GetString(17), []), r.GetInt32(18)));
        }
        for (var i = 0; i < lines.Count; i++) byId[lines[i].Line.Id] = i;
        var linkTable = working ? "mbom.working_link" : "mbom.usage_link";
        var linkKey = working ? "working_usage_id" : "mbom_usage_id";
        await using (var cmd = new NpgsqlCommand(
            $"SELECT l.{linkKey}, l.ebom_usage_id, l.status, l.reason FROM {linkTable} l JOIN {table} u ON u.id = l.{linkKey} WHERE u.{key} = @id ORDER BY l.ebom_usage_id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) linkRows.Add((r.GetGuid(0), new LinkInput(r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3))));
        }
        var result = lines.Select(l => l.Line with { Links = [.. linkRows.Where(k => k.Line == l.Line.Id).Select(k => k.Link)] }).ToList();
        return result;
    }

    /// <summary>The hash lines and links of stored rows, with alternates as stored (iteration) or as the next write assigns them (working copy).</summary>
    public static async Task<(List<MbomRules.HashLine> Lines, List<MbomRules.HashLink> Links)> ReadHashRowsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, bool working, Guid id, CancellationToken ct)
    {
        var content = await ReadContentAsync(conn, tx, working, id, ct);
        return HashRows(content);
    }

    public static (List<MbomRules.HashLine> Lines, List<MbomRules.HashLink> Links) HashRows(IReadOnlyList<NewMbomLine> content)
    {
        var lines = new List<MbomRules.HashLine>(content.Count);
        var links = new List<MbomRules.HashLink>();
        foreach (var (l, alt) in WithAlternates(content))
        {
            lines.Add(new MbomRules.HashLine(l.Find, alt, l.Child, l.Quantity, l.Unit, string.Join(',', l.Designators), l.Scrap, l.Phantom, l.SupplyType, l.OperationRef, l.MbomOnly, l.Notes, MbomRules.EffectivityText(l.Eff)));
            links.AddRange(l.Links.Select(k => new MbomRules.HashLink(l.Find, alt, k.EbomUsageId, k.Status, k.Reason)));
        }
        return (lines, links);
    }

    /// <summary>Assigns the alternate ordinal the insert trigger will assign: lines of one find number in (effectivity start, created, id) order.</summary>
    public static IEnumerable<(NewMbomLine Line, int Alt)> WithAlternates(IReadOnlyList<NewMbomLine> content) =>
        content.GroupBy(l => l.Find).OrderBy(g => g.Key).SelectMany(g => g
            .OrderBy(l => l.Eff?.From is null ? 0 : 1).ThenBy(l => l.Eff?.From ?? 0).ThenBy(l => l.CreatedAt).ThenBy(l => l.Id)
            .Select((l, i) => (l, i)));

    /// <summary>Digest of the stored iteration (or working copy) content.</summary>
    public static async Task<string> HashAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, bool working, Guid id, Guid? derivedFrom, CancellationToken ct)
    {
        var (lines, links) = await ReadHashRowsAsync(conn, tx, working, id, ct);
        return MbomRules.ContentHash(derivedFrom, lines, links);
    }

    // ---------------------------------------------------------------- writing an iteration

    /// <summary>
    /// Writes one iteration with its lines and links and makes it the latest of its revision. The digest is computed from the content before the write
    /// and verified against the stored rows after it, so a trigger or a bug can never leave an iteration whose hash does not verify.
    /// </summary>
    public static async Task<(Guid IterationId, string Sha256)> WriteIterationAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid revisionId, int iteration, string description, string? comment, Guid? derivedFrom, string? derivedSpec,
        IReadOnlyList<NewMbomLine> content, DateTimeOffset now, string by, CancellationToken ct)
    {
        var (hashLines, hashLinks) = HashRows(content);
        var sha = MbomRules.ContentHash(derivedFrom, hashLines, hashLinks);
        var iterationId = Guid.CreateVersion7();
        await using (var ins = new NpgsqlCommand(
            """
            INSERT INTO mbom.iteration (id, revision_id, iteration, description, comment, derived_part_iteration_id, derived_spec, content_sha256, line_count, link_count, created_at, created_by)
            VALUES (@id, @rid, @it, @desc, @comment, @derived, @spec, @sha, @lines, @links, @at, @by)
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("id", iterationId);
            ins.Parameters.AddWithValue("rid", revisionId);
            ins.Parameters.AddWithValue("it", iteration);
            ins.Parameters.AddWithValue("desc", description);
            ins.Parameters.Add(new NpgsqlParameter("comment", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(comment) ? DBNull.Value : comment.Trim() });
            ins.Parameters.Add(new NpgsqlParameter("derived", NpgsqlDbType.Uuid) { Value = (object?)derivedFrom ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("spec", NpgsqlDbType.Text) { Value = (object?)derivedSpec ?? DBNull.Value });
            ins.Parameters.AddWithValue("sha", sha);
            ins.Parameters.AddWithValue("lines", content.Count);
            ins.Parameters.AddWithValue("links", hashLinks.Count);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", by);
            await ins.ExecuteNonQueryAsync(ct);
        }

        var ordered = WithAlternates(content).ToList();
        await using (var copy = await conn.BeginBinaryImportAsync(
            "COPY mbom.usage (id, iteration_id, child_master_id, quantity, unit, find_number, alt, reference_designators, scrap_factor, phantom, supply_type, operation_ref, mbom_only, notes, eff_kind, eff_context, eff_from, eff_to, created_at, created_by) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var (l, alt) in ordered)
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(l.Id, NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(iterationId, NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(l.Child, NpgsqlDbType.Uuid, ct);
                await copy.WriteAsync(l.Quantity, NpgsqlDbType.Numeric, ct);
                await copy.WriteAsync(l.Unit, NpgsqlDbType.Text, ct);
                await copy.WriteAsync(l.Find, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(alt, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(l.Designators, NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
                if (l.Scrap is { } s) await copy.WriteAsync(s, NpgsqlDbType.Numeric, ct); else await copy.WriteNullAsync(ct);
                await copy.WriteAsync(l.Phantom, NpgsqlDbType.Boolean, ct);
                await copy.WriteAsync(l.SupplyType, NpgsqlDbType.Text, ct);
                if (l.OperationRef is { } op) await copy.WriteAsync(op, NpgsqlDbType.Text, ct); else await copy.WriteNullAsync(ct);
                await copy.WriteAsync(l.MbomOnly, NpgsqlDbType.Boolean, ct);
                if (l.Notes is { } n) await copy.WriteAsync(n, NpgsqlDbType.Text, ct); else await copy.WriteNullAsync(ct);
                if (l.Eff is null) { await copy.WriteNullAsync(ct); await copy.WriteNullAsync(ct); await copy.WriteNullAsync(ct); await copy.WriteNullAsync(ct); }
                else
                {
                    await copy.WriteAsync(l.Eff.Kind, NpgsqlDbType.Text, ct);
                    if (l.Eff.Context is { } c) await copy.WriteAsync(c, NpgsqlDbType.Text, ct); else await copy.WriteNullAsync(ct);
                    if (l.Eff.From is { } f) await copy.WriteAsync(f, NpgsqlDbType.Bigint, ct); else await copy.WriteNullAsync(ct);
                    if (l.Eff.To is { } t) await copy.WriteAsync(t, NpgsqlDbType.Bigint, ct); else await copy.WriteNullAsync(ct);
                }
                await copy.WriteAsync(l.CreatedAt, NpgsqlDbType.TimestampTz, ct);
                await copy.WriteAsync(l.CreatedBy, NpgsqlDbType.Text, ct);
            }
            await copy.CompleteAsync(ct);
        }
        if (hashLinks.Count > 0)
        {
            await using var copy = await conn.BeginBinaryImportAsync("COPY mbom.usage_link (mbom_usage_id, ebom_usage_id, status, reason) FROM STDIN (FORMAT BINARY)", ct);
            foreach (var l in content)
                foreach (var k in l.Links)
                {
                    await copy.StartRowAsync(ct);
                    await copy.WriteAsync(l.Id, NpgsqlDbType.Uuid, ct);
                    await copy.WriteAsync(k.EbomUsageId, NpgsqlDbType.Uuid, ct);
                    await copy.WriteAsync(k.Status, NpgsqlDbType.Text, ct);
                    if (k.Reason is { } reason) await copy.WriteAsync(reason.Trim(), NpgsqlDbType.Text, ct); else await copy.WriteNullAsync(ct);
                }
            await copy.CompleteAsync(ct);
        }
        var stored = await HashAsync(conn, tx, working: false, iterationId, derivedFrom, ct);
        if (stored != sha) throw new InvalidOperationException("The stored MBOM content does not match the digest computed before writing it.");
        await using (var bump = new NpgsqlCommand("UPDATE mbom.revision SET latest_iteration = @it WHERE id = @rid", conn, tx))
        {
            bump.Parameters.AddWithValue("it", iteration);
            bump.Parameters.AddWithValue("rid", revisionId);
            await bump.ExecuteNonQueryAsync(ct);
        }
        return (iterationId, sha);
    }

    // ---------------------------------------------------------------- link validation

    /// <summary>
    /// Checks links against the EBOM iteration the MBOM is derived from: every named EBOM line must belong to it, a matched link joins the same part.
    /// Messages name find numbers only, never parts, so they are safe for a caller who cannot read every part.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ValidateLinksAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid? derivedFrom, IReadOnlyList<NewMbomLine> content, CancellationToken ct)
    {
        var errors = new List<string>();
        var ids = content.SelectMany(l => l.Links).Select(k => k.EbomUsageId).Distinct().ToArray();
        if (ids.Length == 0) return errors;
        if (derivedFrom is null) return ["Lines are linked to EBOM lines but the MBOM names no EBOM source."];
        var ebom = new Dictionary<Guid, (Guid Parent, Guid Child, int Find)>();
        await using (var cmd = new NpgsqlCommand("SELECT id, parent_iteration_id, child_master_id, find_number FROM plm.bom_usage WHERE id = ANY(@ids)", conn, tx))
        {
            cmd.Parameters.AddWithValue("ids", ids);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ebom[r.GetGuid(0)] = (r.GetGuid(1), r.GetGuid(2), r.GetInt32(3));
        }
        foreach (var l in content)
            foreach (var k in l.Links)
            {
                if (!ebom.TryGetValue(k.EbomUsageId, out var e) || e.Parent != derivedFrom)
                    errors.Add($"Line {l.Find.ToString(CultureInfo.InvariantCulture)} links to an EBOM line that is not part of the EBOM it is derived from.");
                else if (k.Status == LinkStatuses.Matched && e.Child != l.Child)
                    errors.Add($"Line {l.Find.ToString(CultureInfo.InvariantCulture)} is linked as matched to EBOM line {e.Find.ToString(CultureInfo.InvariantCulture)} but is a different part; declare a substitution with a reason.");
                errors.AddRange(MbomRules.ValidateLink(k.Status, k.Reason, l.MbomOnly).Select(m => $"Line {l.Find.ToString(CultureInfo.InvariantCulture)}: {m}"));
            }
        return [.. errors.Distinct()];
    }

    public static string Fmt(int n) => n.ToString(CultureInfo.InvariantCulture);
}
