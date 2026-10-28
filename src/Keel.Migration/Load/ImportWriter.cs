using Keel.Core;
using Keel.Migration.Mapping;
using Npgsql;
using NpgsqlTypes;

namespace Keel.Migration.Load;

/// <summary>
/// The narrow, internal import writer (ADR-0023). Bulk, history-preserving import cannot go through the interactive services (they stamp
/// "now" and the current user), so this class writes the plm/chg rows with plain SQL. It never disables a trigger or bypasses a guard: rows are
/// written in the order the guards of migrations 0002/0003/0005/0007 accept (revision in its initial state, iterations, structure, links;
/// the final lifecycle state last), and every markings column goes through <see cref="Markings"/> which was validated by <see cref="Markings.Create"/>.
/// </summary>
internal sealed class ImportWriter(NpgsqlConnection conn)
{
    public NpgsqlTransaction? Tx { get; set; }

    private NpgsqlCommand Cmd(string sql, params (string Name, object? Value)[] ps)
    {
        var c = new NpgsqlCommand(sql, conn, Tx);
        foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return c;
    }

    private static NpgsqlParameter Json(string name, string json) => new(name, NpgsqlDbType.Jsonb) { Value = json };
    private static NpgsqlParameter Text(string name, string? v) => new(name, NpgsqlDbType.Text) { Value = (object?)v ?? DBNull.Value };
    private static NpgsqlParameter TextArray(string name, IEnumerable<string> v) => new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = v.ToArray() };
    private static DateTime Utc(DateTimeOffset t) => t.UtcDateTime;

    private static void AddMarkings(NpgsqlCommand c, Markings m)
    {
        c.Parameters.Add(Text("ej", m.ExportJurisdiction.ToString()));
        c.Parameters.Add(Text("eccn", m.Eccn));
        c.Parameters.Add(Text("usml", m.UsmlCategory));
        c.Parameters.Add(TextArray("cui", m.CuiCategories));
        c.Parameters.Add(Text("ds", m.DistributionStatement.ToString()));
        c.Parameters.Add(Text("office", m.ControllingOffice));
        c.Parameters.Add(Text("reason", m.DistributionReason));
        c.Parameters.Add(Text("program", m.Program));
        c.Parameters.Add(Text("org", m.OwningOrg));
        c.Parameters.Add(TextArray("auths", m.ExportAuthorizations));
    }

    private const string MarkingColumns = "export_jurisdiction, eccn, usml_category, cui_categories, distribution_statement, controlling_office, distribution_reason, program, owning_org, export_authorizations";
    private const string MarkingValues = "@ej, @eccn, @usml, @cui, @ds, @office, @reason, @program, @org, @auths";

    // ------------------------------------------------------------------------------------------------ batch bookkeeping
    public async Task InsertBatchAsync(Guid id, string name, string system, string packageSha, string mappingSha, string? dryRunHash, string granularity, bool quarantine, DateTimeOffset at, string by, CancellationToken ct)
    {
        await using var c = Cmd("""
            INSERT INTO mig.batch (id, name, source_system, package_sha256, mapping_sha256, dry_run_hash, audit_granularity, quarantine_mode, status, started_at, started_by)
            VALUES (@id, @name, @system, @pkg, @map, @dry, @gran, @q, 'Running', @at, @by)
            """, ("id", id), ("name", name), ("system", system), ("pkg", packageSha), ("map", mappingSha), ("dry", dryRunHash), ("gran", granularity), ("q", quarantine), ("at", Utc(at)), ("by", by));
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task FinishBatchAsync(Guid id, string status, string summaryJson, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = Cmd("UPDATE mig.batch SET status = @s, completed_at = @at, summary = @sum WHERE id = @id", ("s", status), ("at", Utc(at)), ("id", id));
        c.Parameters.Add(Json("sum", summaryJson));
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertIdMapAsync(string system, string entityType, string sourceId, Guid keelId, Guid batch, string hash, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = Cmd("INSERT INTO mig.id_map (source_system, entity_type, source_id, keel_id, batch_id, source_hash, imported_at) VALUES (@s, @t, @sid, @k, @b, @h, @at)",
            ("s", system), ("t", entityType), ("sid", sourceId), ("k", keelId), ("b", batch), ("h", hash), ("at", Utc(at)));
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertQuarantineAsync(Guid batch, string entityType, string sourceId, string code, string message, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = Cmd("INSERT INTO mig.quarantine (batch_id, entity_type, source_id, code, message, recorded_at) VALUES (@b, @t, @s, @c, @m, @at)",
            ("b", batch), ("t", entityType), ("s", sourceId), ("c", code), ("m", message.Length > 2000 ? message[..2000] : message), ("at", Utc(at)));
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertReportAsync(Guid batch, string kind, string contentJson, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = Cmd("INSERT INTO mig.report (batch_id, kind, content, created_at) VALUES (@b, @k, @c, @at)", ("b", batch), ("k", kind), ("at", Utc(at)));
        c.Parameters.Add(Json("c", contentJson));
        await c.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------------------------------------ parts
    public async Task InsertPartMasterAsync(Guid id, PPart p, Guid batch, CancellationToken ct)
    {
        await using var c = Cmd($"""
            INSERT INTO plm.part_master (id, number, name, part_type, default_unit, source, lifecycle_id, {MarkingColumns}, created_at, created_by, imported_batch_id, source_id, legacy_properties)
            VALUES (@id, @number, @name, @type, @unit, @source, 'part-default', {MarkingValues}, @at, @by, @batch, @sid, @legacy)
            """, ("id", id), ("number", p.Number), ("name", p.Name), ("type", p.PartType), ("unit", p.Unit), ("source", p.Source), ("at", Utc(p.CreatedAt)), ("by", p.CreatedBy), ("batch", batch), ("sid", p.SourceId));
        AddMarkings(c, p.Markings);
        c.Parameters.Add(Json("legacy", p.LegacyJson));
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task DemoteLatestPartRevisionAsync(Guid masterId, CancellationToken ct)
    {
        await using var c = Cmd("UPDATE plm.part_revision SET is_latest = false WHERE master_id = @m AND is_latest", ("m", masterId));
        await c.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Inserts the revision in the lifecycle's initial state so the iteration, structure and link guards accept the rows that follow.</summary>
    public async Task InsertPartRevisionAsync(Guid id, Guid masterId, PRevision r, bool isLatest, int latestIteration, string initialState, Guid batch, CancellationToken ct)
    {
        await using var c = Cmd("""
            INSERT INTO plm.part_revision (id, master_id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by, imported_batch_id, source_id, legacy_revision, legacy_properties)
            VALUES (@id, @m, @rev, @state, @latest, @iter, @at, @by, @at, @by, @batch, @sid, @legacy, @props)
            """, ("id", id), ("m", masterId), ("rev", r.Label), ("state", initialState), ("latest", isLatest), ("iter", latestIteration), ("at", Utc(r.CreatedAt)), ("by", r.CreatedBy), ("batch", batch), ("sid", r.SourceId), ("legacy", r.LegacyLabel));
        c.Parameters.Add(Json("props", r.LegacyJson));
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertPartIterationAsync(Guid id, Guid revisionId, PIteration i, Guid batch, CancellationToken ct)
    {
        await using var c = Cmd("""
            INSERT INTO plm.part_iteration (id, revision_id, iteration, description, attributes, comment, created_at, created_by, imported_batch_id, legacy_properties)
            VALUES (@id, @rev, @n, @desc, @attrs, @comment, @at, @by, @batch, @legacy)
            """, ("id", id), ("rev", revisionId), ("n", i.Sequence), ("desc", i.Description), ("at", Utc(i.CreatedAt)), ("by", i.CreatedBy), ("batch", batch));
        c.Parameters.Add(Text("comment", i.Comment));
        c.Parameters.Add(Json("attrs", i.AttributesJson));
        c.Parameters.Add(Json("legacy", i.LegacyJson));
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertUsagesAsync(Guid iterationId, IReadOnlyList<(PUsage Usage, Guid ChildMaster)> usages, DateTimeOffset at, string by, CancellationToken ct)
    {
        if (usages.Count == 0) return;
        await using var batch = new NpgsqlBatch(conn, Tx);
        foreach (var (u, child) in usages)
        {
            var cmd = new NpgsqlBatchCommand("""
                INSERT INTO plm.bom_usage (id, parent_iteration_id, child_master_id, quantity, unit, find_number, reference_designators, notes, created_at, created_by, eff_kind, eff_context, eff_from, eff_to)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
                """);
            cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = iterationId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = child });
            cmd.Parameters.Add(new NpgsqlParameter { Value = u.Quantity, NpgsqlDbType = NpgsqlDbType.Numeric });
            cmd.Parameters.Add(new NpgsqlParameter { Value = u.Unit });
            cmd.Parameters.Add(new NpgsqlParameter { Value = u.FindNumber });
            cmd.Parameters.Add(new NpgsqlParameter { Value = u.Designators.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)u.Notes ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = at.UtcDateTime });
            cmd.Parameters.Add(new NpgsqlParameter { Value = by });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)u.Effectivity?.Kind ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)u.Effectivity?.Context ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)u.Effectivity?.From ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)u.Effectivity?.To ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            batch.BatchCommands.Add(cmd);
        }
        await batch.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertPartLinkAsync(Guid iterationId, string type, Guid documentMaster, Guid? documentRevision, DateTimeOffset at, string by, CancellationToken ct)
    {
        await using var c = Cmd("""
            INSERT INTO plm.part_document_link (id, part_iteration_id, link_type, document_master_id, document_revision_id, created_at, created_by)
            VALUES (@id, @it, @type, @dm, @dr, @at, @by)
            """, ("id", Guid.CreateVersion7()), ("it", iterationId), ("type", type), ("dm", documentMaster), ("at", Utc(at)), ("by", by));
        c.Parameters.Add(new NpgsqlParameter("dr", NpgsqlDbType.Uuid) { Value = (object?)documentRevision ?? DBNull.Value });
        await c.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The final lifecycle state is set last, after every row that a locked state would reject (ADR-0023).</summary>
    public async Task SetPartRevisionStateAsync(Guid revisionId, PRevision r, CancellationToken ct)
    {
        await using var c = Cmd("UPDATE plm.part_revision SET lifecycle_state = @s, state_changed_at = @at, state_changed_by = @by WHERE id = @id",
            ("s", r.State), ("at", Utc(r.StateChangedAt)), ("by", r.StateChangedBy), ("id", revisionId));
        await c.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------------------------------------ documents
    public async Task InsertDocumentMasterAsync(Guid id, PDocument d, Guid batch, CancellationToken ct)
    {
        await using var c = Cmd($"""
            INSERT INTO plm.document_master (id, number, name, document_type, lifecycle_id, {MarkingColumns}, created_at, created_by, imported_batch_id, source_id, legacy_properties)
            VALUES (@id, @number, @name, @type, 'document-default', {MarkingValues}, @at, @by, @batch, @sid, @legacy)
            """, ("id", id), ("number", d.Number), ("name", d.Name), ("type", d.DocumentType), ("at", Utc(d.CreatedAt)), ("by", d.CreatedBy), ("batch", batch), ("sid", d.SourceId));
        AddMarkings(c, d.Markings);
        c.Parameters.Add(Json("legacy", d.LegacyJson));
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task DemoteLatestDocumentRevisionAsync(Guid masterId, CancellationToken ct)
    {
        await using var c = Cmd("UPDATE plm.document_revision SET is_latest = false WHERE master_id = @m AND is_latest", ("m", masterId));
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertDocumentRevisionAsync(Guid id, Guid masterId, PDocRevision r, bool isLatest, int latestIteration, string initialState, Guid batch, CancellationToken ct)
    {
        await using var c = Cmd("""
            INSERT INTO plm.document_revision (id, master_id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by, imported_batch_id, source_id, legacy_revision, legacy_properties)
            VALUES (@id, @m, @rev, @state, @latest, @iter, @at, @by, @at, @by, @batch, @sid, @legacy, @props)
            """, ("id", id), ("m", masterId), ("rev", r.Label), ("state", initialState), ("latest", isLatest), ("iter", latestIteration), ("at", Utc(r.CreatedAt)), ("by", r.CreatedBy), ("batch", batch), ("sid", r.SourceId), ("legacy", r.LegacyLabel));
        c.Parameters.Add(Json("props", r.LegacyJson));
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertDocumentIterationAsync(Guid id, Guid revisionId, PDocIteration i, Guid batch, CancellationToken ct)
    {
        await using var c = Cmd("""
            INSERT INTO plm.document_iteration (id, revision_id, iteration, description, comment, created_at, created_by, imported_batch_id, legacy_properties)
            VALUES (@id, @rev, @n, @desc, @comment, @at, @by, @batch, @legacy)
            """, ("id", id), ("rev", revisionId), ("n", i.Sequence), ("desc", i.Description), ("at", Utc(i.CreatedAt)), ("by", i.CreatedBy), ("batch", batch));
        c.Parameters.Add(Text("comment", i.Comment));
        c.Parameters.Add(Json("legacy", i.LegacyJson));
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertDocumentFileAsync(Guid iterationId, PFile f, DateTimeOffset at, string by, CancellationToken ct)
    {
        await using var c = Cmd("""
            INSERT INTO plm.document_file (id, iteration_id, role, file_name, media_type, size_bytes, sha256, added_at, added_by)
            VALUES (@id, @it, @role, @name, @media, @size, @sha, @at, @by)
            """, ("id", Guid.CreateVersion7()), ("it", iterationId), ("role", f.Primary ? "primary" : "attachment"), ("name", f.Name), ("media", f.MediaType), ("size", f.SizeBytes), ("sha", f.Sha256), ("at", Utc(at)), ("by", by));
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task SetDocumentRevisionStateAsync(Guid revisionId, PDocRevision r, CancellationToken ct)
    {
        await using var c = Cmd("UPDATE plm.document_revision SET lifecycle_state = @s, state_changed_at = @at, state_changed_by = @by WHERE id = @id",
            ("s", r.State), ("at", Utc(r.StateChangedAt)), ("by", r.StateChangedBy), ("id", revisionId));
        await c.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------------------------------------ changes
    public async Task<string> NextChangeNumberAsync(string type, CancellationToken ct)
    {
        var seq = type switch { "PR" => "chg.pr_seq", "ECR" => "chg.ecr_seq", _ => "chg.ecn_seq" };
#pragma warning disable CA2100 // sequence name is chosen from a fixed switch
        await using var c = Cmd($"SELECT nextval('{seq}')");
#pragma warning restore CA2100
        var n = (long)(await c.ExecuteScalarAsync(ct))!;
        return $"{type}-{n:D6}";
    }

    public async Task InsertChangeAsync(Guid id, PChange c0, string number, Guid? parent, string initialLifecycleState, (string Sub, string Username, string Name) requester, Guid batch, CancellationToken ct)
    {
        var lifecycle = c0.ChangeType.ToLowerInvariant() + "-default";
        await using var c = Cmd($"""
            INSERT INTO chg.change (id, change_type, number, title, description, priority, change_class, reason, requester_sub, requester_username, requester_name, source_id,
                lifecycle_id, lifecycle_state, {MarkingColumns}, created_at, created_by, state_changed_at, state_changed_by, imported_batch_id, legacy_number, legacy_properties)
            VALUES (@id, @type, @number, @title, @desc, @prio, @class, @chreason, @rsub, @runame, @rname, @parent,
                @lc, @state, {MarkingValues}, @at, @by, @at, @by, @batch, @legacy, @props)
            """, ("id", id), ("type", c0.ChangeType), ("number", number), ("title", c0.Title), ("desc", c0.Description), ("prio", c0.Priority), ("chreason", c0.Reason),
            ("rsub", requester.Sub), ("runame", requester.Username), ("rname", requester.Name), ("lc", lifecycle), ("state", initialLifecycleState),
            ("at", Utc(c0.CreatedAt)), ("by", c0.CreatedBy), ("batch", batch), ("legacy", c0.LegacyNumber));
        c.Parameters.Add(Text("class", c0.ChangeClass));
        c.Parameters.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = (object?)parent ?? DBNull.Value });
        c.Parameters.Add(Json("props", c0.LegacyJson));
        AddMarkings(c, c0.Markings);
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertAffectedAsync(Guid changeId, Guid masterId, string number, string name, Guid revisionId, string revision, string action, Guid? resultingId, string? resultingRevision, DateTimeOffset at, string by, CancellationToken ct)
    {
        await using var c = Cmd("""
            INSERT INTO chg.affected_object (id, change_id, object_type, master_id, object_key, object_name, revision_id, revision, change_action, resulting_revision_id, resulting_revision, binding, added_at, added_by)
            VALUES (@id, @ch, 'part', @m, @key, @name, @rid, @rev, @action, @res, @resrev, false, @at, @by)
            """, ("id", Guid.CreateVersion7()), ("ch", changeId), ("m", masterId), ("key", number), ("name", name), ("rid", revisionId), ("rev", revision), ("action", action), ("at", Utc(at)), ("by", by));
        c.Parameters.Add(new NpgsqlParameter("res", NpgsqlDbType.Uuid) { Value = (object?)resultingId ?? DBNull.Value });
        c.Parameters.Add(Text("resrev", resultingRevision));
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertChangeHistoryAsync(Guid changeId, DateTimeOffset at, string actorSub, string actorName, string detail, CancellationToken ct)
    {
        await using var c = Cmd("INSERT INTO chg.history (change_id, at, actor_sub, actor_name, action, detail) VALUES (@ch, @at, @sub, @name, 'imported', @detail)",
            ("ch", changeId), ("at", Utc(at)), ("sub", actorSub), ("name", actorName), ("detail", detail));
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task SetChangeStateAsync(Guid changeId, string state, DateTimeOffset at, string by, CancellationToken ct)
    {
        await using var c = Cmd("UPDATE chg.change SET lifecycle_state = @s, state_changed_at = @at, state_changed_by = @by WHERE id = @id", ("s", state), ("at", Utc(at)), ("by", by), ("id", changeId));
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<(string From, string To)>> LoadTransitionsAsync(string lifecycle, CancellationToken ct)
    {
        var list = new List<(string, string)>();
        await using var c = Cmd("SELECT from_state, to_state FROM plm.lifecycle_transition WHERE lifecycle_id = @l", ("l", lifecycle));
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    public async Task<string> InitialStateAsync(string lifecycle, CancellationToken ct)
    {
        await using var c = Cmd("SELECT state FROM plm.lifecycle_state WHERE lifecycle_id = @l AND is_initial", ("l", lifecycle));
        return (string)(await c.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException($"Lifecycle {lifecycle} has no initial state."));
    }

    // ------------------------------------------------------------------------------------------------ savepoints (per-object isolation inside a chunk)
    public async Task SavepointAsync(string name, CancellationToken ct) { await using var c = Cmd($"SAVEPOINT {name}"); await c.ExecuteNonQueryAsync(ct); }
    public async Task ReleaseAsync(string name, CancellationToken ct) { await using var c = Cmd($"RELEASE SAVEPOINT {name}"); await c.ExecuteNonQueryAsync(ct); }
    public async Task RollbackToAsync(string name, CancellationToken ct) { await using var c = Cmd($"ROLLBACK TO SAVEPOINT {name}"); await c.ExecuteNonQueryAsync(ct); }
}
