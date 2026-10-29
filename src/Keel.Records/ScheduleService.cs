using Keel.Audit;
using Keel.Core;
using Npgsql;

namespace Keel.Records;

/// <summary>
/// Retention schedules as data (ADR-0041): a versioned list of record classes an administrator drafts and the workflow engine publishes with signatures
/// (<see cref="ScheduleWorkflowGateway"/>). A published version is immutable (database triggers); a new version is a copy of the last one, edited. Program
/// events (program close, contract end) are recorded here too, append only.
/// </summary>
public sealed class ScheduleService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock)
{
    private const string ObjectType = "retention_schedule";

    internal static string ClassSelect => "c.id, c.code, c.name, c.description, c.priority, c.match_kinds, c.match_types, c.match_programs, c.match_jurisdictions, c.match_cui, c.match_statements, c.retention_months, c.trigger_event, c.disposition, c.citation, c.notes";

    internal static ClassView ReadClass(NpgsqlDataReader r) => new(r.GetGuid(0), new ClassInput(
        r.GetString(1), r.GetString(2), RecordsStore.Str(r, 3), r.GetInt32(4), (string[])r.GetValue(5), (string[])r.GetValue(6), (string[])r.GetValue(7), (string[])r.GetValue(8),
        (string[])r.GetValue(9), (string[])r.GetValue(10), r.GetInt32(11), r.GetString(12), r.GetString(13), RecordsStore.Str(r, 14), RecordsStore.Str(r, 15)));

    private async Task<Outcome<T>> DenyAsync<T>(Subject s, string action, string? id, string? corr, CancellationToken ct)
    {
        await audit.AppendAsync(RecordsStore.Entry(s, action, AuditOutcome.Denied, ObjectType, id, corr, ("reasons", AccessPolicyReasons.Role)), ct);
        return Outcome<T>.Forbidden([AccessPolicyReasons.Role]);
    }

    public async Task<Outcome<IReadOnlyList<ScheduleSummary>>> ListAsync(Subject s, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanView(s)) return await DenyAsync<IReadOnlyList<ScheduleSummary>>(s, "records.schedule_list", null, corr, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var list = new List<ScheduleSummary>();
        await using var cmd = new NpgsqlCommand(
            "SELECT s.id, s.version, s.status, s.title, (SELECT count(*) FROM rec.record_class c WHERE c.schedule_id = s.id), s.created_at, s.created_by, s.published_at, s.published_by FROM rec.schedule s ORDER BY s.version DESC", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ScheduleSummary(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetString(3), (int)r.GetInt64(4), r.GetFieldValue<DateTimeOffset>(5), r.GetString(6),
                r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), RecordsStore.Str(r, 8)));
        return Outcome<IReadOnlyList<ScheduleSummary>>.Ok(list);
    }

    internal static async Task<ScheduleView?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string where, Action<NpgsqlCommand> bind, bool forUpdate, CancellationToken ct)
    {
        ScheduleView? head = null;
        await using (var cmd = new NpgsqlCommand($"SELECT s.id, s.version, s.status, s.title, s.notes, s.revision, s.created_at, s.created_by, s.published_at, s.published_by FROM rec.schedule s WHERE {where}{(forUpdate ? " FOR UPDATE" : "")}", conn, tx))
        {
            bind(cmd);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                head = new ScheduleView(r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetString(3), RecordsStore.Str(r, 4), r.GetInt32(5), r.GetFieldValue<DateTimeOffset>(6), r.GetString(7),
                    r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), RecordsStore.Str(r, 9), []);
        }
        if (head is null) return null;
        var classes = new List<ClassView>();
        await using (var cmd = new NpgsqlCommand($"SELECT {ClassSelect} FROM rec.record_class c WHERE c.schedule_id = @s ORDER BY c.priority, c.code", conn, tx))
        {
            cmd.Parameters.AddWithValue("s", head.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) classes.Add(ReadClass(r));
        }
        return head with { Classes = classes };
    }

    public async Task<Outcome<ScheduleView>> GetAsync(Subject s, int version, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanView(s)) return await DenyAsync<ScheduleView>(s, "records.schedule_read", version.ToString(System.Globalization.CultureInfo.InvariantCulture), corr, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var v = await LoadAsync(conn, null, "s.version = @v", c => c.Parameters.AddWithValue("v", version), false, ct);
        return v is null ? Outcome<ScheduleView>.NotFound() : Outcome<ScheduleView>.Ok(v);
    }

    public async Task<Outcome<ScheduleView>> GetPublishedAsync(Subject s, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanView(s)) return await DenyAsync<ScheduleView>(s, "records.schedule_read", null, corr, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var v = await LoadAsync(conn, null, "s.status = 'published'", _ => { }, false, ct);
        return v is null ? Outcome<ScheduleView>.NotFound() : Outcome<ScheduleView>.Ok(v);
    }

    /// <summary>Starts the next version as a copy of the published one (or of nothing, the first time). Only one draft can be open.</summary>
    public async Task<Outcome<ScheduleView>> CreateDraftAsync(Subject s, string title, string? notes, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanManage(s)) return await DenyAsync<ScheduleView>(s, "records.schedule_create", null, corr, ct);
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200) return Outcome<ScheduleView>.Invalid(["The schedule needs a title of at most 200 characters."]);
        if ((notes?.Length ?? 0) > 4000) return Outcome<ScheduleView>.Invalid(["The notes must be at most 4000 characters."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var open = new NpgsqlCommand("SELECT count(*) FROM rec.schedule WHERE status IN ('draft', 'in_review')", conn, tx))
            if ((long)(await open.ExecuteScalarAsync(ct))! > 0) return Outcome<ScheduleView>.Conflict("A draft schedule is already open: finish or discard it first.");
        var id = Guid.CreateVersion7();
        int version;
        await using (var v = new NpgsqlCommand("SELECT coalesce(max(version), 0) + 1 FROM rec.schedule", conn, tx)) version = Convert.ToInt32(await v.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        var now = clock.GetUtcNow();
        await using (var ins = new NpgsqlCommand("INSERT INTO rec.schedule (id, version, status, title, notes, owning_org, created_at, created_by) VALUES (@id, @v, 'draft', @t, @n, @o, @now, @by)", conn, tx))
        {
            ins.Parameters.AddWithValue("id", id); ins.Parameters.AddWithValue("v", version); ins.Parameters.AddWithValue("t", title.Trim());
            ins.Parameters.AddWithValue("n", (object?)notes?.Trim() ?? DBNull.Value); ins.Parameters.AddWithValue("o", RecordsStore.OrgOf(s));
            ins.Parameters.AddWithValue("now", now); ins.Parameters.AddWithValue("by", s.Username ?? s.Id);
            await ins.ExecuteNonQueryAsync(ct);
        }
        var copied = 0;
        await using (var copy = new NpgsqlCommand(
            """
            INSERT INTO rec.record_class (id, schedule_id, code, name, description, priority, match_kinds, match_types, match_programs, match_jurisdictions, match_cui, match_statements,
                                          retention_months, trigger_event, disposition, citation, notes)
            SELECT gen_random_uuid(), @new, code, name, description, priority, match_kinds, match_types, match_programs, match_jurisdictions, match_cui, match_statements,
                   retention_months, trigger_event, disposition, citation, notes
            FROM rec.record_class WHERE schedule_id = (SELECT id FROM rec.schedule WHERE status = 'published')
            """, conn, tx))
        {
            copy.Parameters.AddWithValue("new", id);
            copied = await copy.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, RecordsStore.Entry(s, "records.schedule_create", AuditOutcome.Success, ObjectType, $"v{version}", corr, ("version", version.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("classesCopied", copied.ToString(System.Globalization.CultureInfo.InvariantCulture))), ct);
        var view = await LoadAsync(conn, tx, "s.id = @id", c => c.Parameters.AddWithValue("id", id), false, ct);
        await tx.CommitAsync(ct);
        return Outcome<ScheduleView>.Ok(view!);
    }

    public Task<Outcome<ScheduleView>> UpdateDraftAsync(Subject s, int version, string title, string? notes, int revision, string? corr, CancellationToken ct = default) =>
        ChangeDraftAsync(s, version, "records.schedule_update", corr, [], async (conn, tx, cur) =>
        {
            if (cur.Revision != revision) return "The schedule changed since you opened it. Reload and try again.";
            if (string.IsNullOrWhiteSpace(title) || title.Length > 200) return "The schedule needs a title of at most 200 characters.";
            await using var upd = new NpgsqlCommand("UPDATE rec.schedule SET title = @t, notes = @n, revision = revision + 1, updated_at = @now WHERE id = @id", conn, tx);
            upd.Parameters.AddWithValue("t", title.Trim()); upd.Parameters.AddWithValue("n", (object?)notes?.Trim() ?? DBNull.Value);
            upd.Parameters.AddWithValue("now", clock.GetUtcNow()); upd.Parameters.AddWithValue("id", cur.Id);
            await upd.ExecuteNonQueryAsync(ct);
            return null;
        }, ct);

    /// <summary>Adds a class, or replaces the one with the same code.</summary>
    public Task<Outcome<ScheduleView>> UpsertClassAsync(Subject s, int version, ClassInput input, string? corr, CancellationToken ct = default) =>
        ChangeDraftAsync(s, version, "records.class_upsert", corr, [("class", input.Code)], async (conn, tx, cur) =>
        {
            var errors = RetentionRules.Validate(input);
            if (errors.Count > 0) return string.Join(" ", errors);
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO rec.record_class (id, schedule_id, code, name, description, priority, match_kinds, match_types, match_programs, match_jurisdictions, match_cui, match_statements,
                                              retention_months, trigger_event, disposition, citation, notes)
                VALUES (@id, @s, @code, @name, @desc, @prio, @kinds, @types, @programs, @juris, @cui, @stmts, @months, @trig, @disp, @cit, @notes)
                ON CONFLICT (schedule_id, code) DO UPDATE SET name = EXCLUDED.name, description = EXCLUDED.description, priority = EXCLUDED.priority, match_kinds = EXCLUDED.match_kinds,
                    match_types = EXCLUDED.match_types, match_programs = EXCLUDED.match_programs, match_jurisdictions = EXCLUDED.match_jurisdictions, match_cui = EXCLUDED.match_cui,
                    match_statements = EXCLUDED.match_statements, retention_months = EXCLUDED.retention_months, trigger_event = EXCLUDED.trigger_event, disposition = EXCLUDED.disposition,
                    citation = EXCLUDED.citation, notes = EXCLUDED.notes
                """, conn, tx);
            cmd.Parameters.AddWithValue("id", Guid.CreateVersion7()); cmd.Parameters.AddWithValue("s", cur.Id);
            cmd.Parameters.AddWithValue("code", input.Code); cmd.Parameters.AddWithValue("name", input.Name.Trim());
            cmd.Parameters.AddWithValue("desc", (object?)input.Description?.Trim() ?? DBNull.Value); cmd.Parameters.AddWithValue("prio", input.Priority);
            cmd.Parameters.AddWithValue("kinds", input.Kinds); cmd.Parameters.AddWithValue("types", input.Types); cmd.Parameters.AddWithValue("programs", input.Programs);
            cmd.Parameters.AddWithValue("juris", input.Jurisdictions); cmd.Parameters.AddWithValue("cui", input.Cui); cmd.Parameters.AddWithValue("stmts", input.Statements);
            cmd.Parameters.AddWithValue("months", input.RetentionMonths); cmd.Parameters.AddWithValue("trig", input.TriggerEvent); cmd.Parameters.AddWithValue("disp", input.Disposition);
            cmd.Parameters.AddWithValue("cit", (object?)input.Citation?.Trim() ?? DBNull.Value); cmd.Parameters.AddWithValue("notes", (object?)input.Notes?.Trim() ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
            await Touch(conn, tx, cur.Id, ct);
            return null;
        }, ct);

    public Task<Outcome<ScheduleView>> RemoveClassAsync(Subject s, int version, string code, string? corr, CancellationToken ct = default) =>
        ChangeDraftAsync(s, version, "records.class_remove", corr, [("class", code)], async (conn, tx, cur) =>
        {
            await using var cmd = new NpgsqlCommand("DELETE FROM rec.record_class WHERE schedule_id = @s AND code = @c", conn, tx);
            cmd.Parameters.AddWithValue("s", cur.Id); cmd.Parameters.AddWithValue("c", code);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return "There is no class with that code.";
            await Touch(conn, tx, cur.Id, ct);
            return null;
        }, ct);

    public Task<Outcome<ScheduleView>> DiscardDraftAsync(Subject s, int version, string? corr, CancellationToken ct = default) =>
        ChangeDraftAsync(s, version, "records.schedule_discard", corr, [], async (conn, tx, cur) =>
        {
            await using var cmd = new NpgsqlCommand("UPDATE rec.schedule SET status = 'discarded', updated_at = @now WHERE id = @id", conn, tx);
            cmd.Parameters.AddWithValue("now", clock.GetUtcNow()); cmd.Parameters.AddWithValue("id", cur.Id);
            await cmd.ExecuteNonQueryAsync(ct);
            return null;
        }, ct);

    private static async Task Touch(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("UPDATE rec.schedule SET revision = revision + 1 WHERE id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<Outcome<ScheduleView>> ChangeDraftAsync(Subject s, int version, string action, string? corr, (string, string?)[] details,
        Func<NpgsqlConnection, NpgsqlTransaction, ScheduleView, Task<string?>> change, CancellationToken ct)
    {
        if (!RecordsAccess.CanManage(s)) return await DenyAsync<ScheduleView>(s, action, $"v{version}", corr, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var cur = await LoadAsync(conn, tx, "s.version = @v", c => c.Parameters.AddWithValue("v", version), true, ct);
        if (cur is null) return Outcome<ScheduleView>.NotFound();
        if (cur.Status != ScheduleStatuses.Draft) return Outcome<ScheduleView>.Conflict($"Version {version} is {ScheduleStatuses.Label(cur.Status).ToLowerInvariant()}; only a draft can be changed.");
        string? problem;
        try { problem = await change(conn, tx, cur); }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { problem = "That change conflicts with an existing class."; }
        if (problem is not null) { await tx.RollbackAsync(ct); return Outcome<ScheduleView>.Invalid([problem]); }
        await audit.AppendAsync(tx, RecordsStore.Entry(s, action, AuditOutcome.Success, ObjectType, $"v{version}", corr, [("version", version.ToString(System.Globalization.CultureInfo.InvariantCulture)), .. details]), ct);
        var view = await LoadAsync(conn, tx, "s.version = @v", c => c.Parameters.AddWithValue("v", version), false, ct);
        await tx.CommitAsync(ct);
        return Outcome<ScheduleView>.Ok(view!);
    }

    // ---------------------------------------------------------------- program events

    public async Task<Outcome<ProgramEventView>> RecordProgramEventAsync(Subject s, string program, string @event, DateOnly occurredOn, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanManage(s)) return await DenyAsync<ProgramEventView>(s, "records.program_event", null, corr, ct);
        if (string.IsNullOrWhiteSpace(program) || program.Length > 64) return Outcome<ProgramEventView>.Invalid(["Enter the program code."]);
        if (@event is not (TriggerEvents.ProgramClose or TriggerEvents.ContractEnd)) return Outcome<ProgramEventView>.Invalid(["The event must be program close or contract end."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        await using (var ins = new NpgsqlCommand("INSERT INTO rec.program_event (program, event, occurred_on, recorded_at, recorded_by) VALUES (@p, @e, @d, @now, @by)", conn, tx))
        {
            ins.Parameters.AddWithValue("p", program.Trim()); ins.Parameters.AddWithValue("e", @event); ins.Parameters.AddWithValue("d", occurredOn);
            ins.Parameters.AddWithValue("now", now); ins.Parameters.AddWithValue("by", s.Username ?? s.Id);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, RecordsStore.Entry(s, "records.program_event", AuditOutcome.Success, "program", program.Trim(), corr, ("event", @event), ("occurredOn", occurredOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return Outcome<ProgramEventView>.Ok(new ProgramEventView(program.Trim(), @event, occurredOn, now, s.Username ?? s.Id));
    }

    public async Task<Outcome<IReadOnlyList<ProgramEventView>>> ListProgramEventsAsync(Subject s, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanView(s)) return await DenyAsync<IReadOnlyList<ProgramEventView>>(s, "records.program_event_list", null, corr, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var list = new List<ProgramEventView>();
        await using var cmd = new NpgsqlCommand("SELECT program, event, occurred_on, recorded_at, recorded_by FROM rec.program_event_current ORDER BY program, event", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ProgramEventView(r.GetString(0), r.GetString(1), r.GetFieldValue<DateOnly>(2), r.GetFieldValue<DateTimeOffset>(3), r.GetString(4)));
        return Outcome<IReadOnlyList<ProgramEventView>>.Ok(list);
    }
}

/// <summary>Stable reason codes shared with <c>AccessPolicy</c> (kept here so the module does not repeat string literals).</summary>
internal static class AccessPolicyReasons
{
    public const string Role = Keel.Security.AccessPolicy.RoleMissing;
}
