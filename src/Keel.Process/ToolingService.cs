using System.Globalization;
using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Process;

/// <summary>
/// Tool, fixture and gauge master data with calibration history, and work centers per plant (ADR-0042). Tools are marked; work centers are plant reference data that administrators
/// maintain. Every call evaluates the access policy and writes its audit event in the same transaction (denials included). Audit details carry numbers, codes and counts only.
/// </summary>
public sealed partial class ToolingService(
    NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, IMbomProcessSource mboms, INotificationSink? notifications = null)
{
    public static readonly SortMap SortKeys = new(
        ("number", ["t.number"]), ("name", ["lower(t.name)"]), ("kind", ["t.kind"]), ("plant", ["t.plant"]), ("due", ["t.due_on"]), ("status", ["t.status"]));

    private static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string objectType, string? id, string? corr, params (string, string?)[] details) =>
        ProcessStore.Entry(s, action, outcome, objectType, id, corr, details);

    private ToolSummary Summarize(ToolRow t)
    {
        var state = ProcessRules.CalibrationState(t.RequiresCalibration, t.DueOn, ProcessStore.Today(clock));
        return new ToolSummary(t.Number, t.Name, t.Kind, t.Plant, t.Status, t.RequiresCalibration, t.IntervalDays, t.LastCalibratedOn, t.DueOn, state, t.Markings.BannerText(), t.Markings);
    }

    [GeneratedRegex("^[A-Z][A-Z0-9-]{1,19}$")]
    private static partial Regex PlantPattern();

    // ---------------------------------------------------------------- work centers

    public async Task<IReadOnlyList<WorkCenterInfo>> ListWorkCentersAsync(string? plant, bool includeRetired, CancellationToken ct = default)
    {
        var list = new List<WorkCenterInfo>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, plant, code, name, resource_kind, is_active FROM proc.work_center WHERE (@plant::text IS NULL OR plant = @plant) AND (is_active OR @all) ORDER BY plant, code", conn);
        cmd.Parameters.Add(new NpgsqlParameter("plant", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)plant?.Trim().ToUpperInvariant() ?? DBNull.Value });
        cmd.Parameters.AddWithValue("all", includeRetired);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new WorkCenterInfo(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetBoolean(5)));
        return list;
    }

    /// <summary>Creates or updates a work center or resource of a plant (administrators). One is retired, never deleted, so released plans keep naming a valid code.</summary>
    public async Task<Outcome<WorkCenterInfo>> SaveWorkCenterAsync(Subject subject, SaveWorkCenterCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        var plant = cmd.Plant?.Trim().ToUpperInvariant() ?? "";
        var code = cmd.Code?.Trim().ToUpperInvariant() ?? "";
        var errors = new List<string>();
        if (!PlantPattern().IsMatch(plant)) errors.Add("Choose a plant.");
        if (!ProcessRules.ValidRoutingCode(code)) errors.Add("A work center code is 1 to 20 capital letters, digits, '.', '-' or '_'.");
        if (string.IsNullOrWhiteSpace(cmd.Name) || cmd.Name.Trim().Length > 120) errors.Add("The name is required (at most 120 characters).");
        if (!ResourceKinds.All.Contains(cmd.ResourceKind)) errors.Add("Unknown resource kind.");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Administer))
        {
            await audit.AppendAsync(tx, Entry(subject, "process.work_center_save", AuditOutcome.Denied, "work_center", code, correlationId, ("plant", plant), ("reasons", AccessPolicy.RoleMissing)), ct);
            await tx.CommitAsync(ct);
            return Outcome<WorkCenterInfo>.Forbidden([AccessPolicy.RoleMissing]);
        }
        if (errors.Count == 0 && !(await mboms.ActivePlantsAsync(ct)).Contains(plant)) errors.Add($"'{plant}' is not an active plant.");
        if (errors.Count > 0) return Outcome<WorkCenterInfo>.Invalid(errors);
        await using (var up = new NpgsqlCommand(
            """
            INSERT INTO proc.work_center (id, plant, code, name, resource_kind, is_active, updated_at, updated_by) VALUES (@id, @p, @c, @n, @k, @a, @at, @by)
            ON CONFLICT (plant, code) DO UPDATE SET name = @n, resource_kind = @k, is_active = @a, updated_at = @at, updated_by = @by
            """, conn, tx))
        {
            up.Parameters.AddWithValue("id", Guid.CreateVersion7());
            up.Parameters.AddWithValue("p", plant);
            up.Parameters.AddWithValue("c", code);
            up.Parameters.AddWithValue("n", cmd.Name.Trim());
            up.Parameters.AddWithValue("k", cmd.ResourceKind);
            up.Parameters.AddWithValue("a", cmd.IsActive);
            up.Parameters.AddWithValue("at", clock.GetUtcNow());
            up.Parameters.AddWithValue("by", ProcessStore.Who(subject));
            await up.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "process.work_center_save", AuditOutcome.Success, "work_center", code, correlationId, ("plant", plant), ("kind", cmd.ResourceKind), ("active", cmd.IsActive ? "true" : "false")), ct);
        await tx.CommitAsync(ct);
        return Outcome<WorkCenterInfo>.Ok((await ListWorkCentersAsync(plant, true, ct)).Single(w => w.Code == code));
    }

    // ---------------------------------------------------------------- tools

    public async Task<Outcome<ToolDetail>> CreateAsync(Subject subject, CreateToolCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        var name = cmd.Name?.Trim() ?? "";
        var plant = cmd.Plant?.Trim().ToUpperInvariant() ?? "";
        var errors = ValidateTool(name, cmd.Kind, cmd.Description, cmd.RequiresCalibration, cmd.IntervalDays);
        if (cmd.LastCalibratedOn is { } lc && lc > ProcessStore.Today(clock)) errors.Add("The last calibration cannot be in the future.");
        if (!(await mboms.ActivePlantsAsync(ct)).Contains(plant)) errors.Add($"'{plant}' is not an active plant.");
        if (errors.Count > 0) return Outcome<ToolDetail>.Invalid(errors);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var modify = AccessPolicy.Evaluate(subject, cmd.Markings, PlmAction.Modify);
        if (!modify.Permitted || !AccessPolicy.HasRoleFor(subject, PlmAction.Modify))
        {
            await audit.AppendAsync(tx, Entry(subject, "tool.create", AuditOutcome.Denied, ProcessStore.ToolObjectType, null, correlationId, ("reasons", string.Join(",", modify.Permitted ? [AccessPolicy.RoleMissing] : modify.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<ToolDetail>.Forbidden(modify.Permitted ? [AccessPolicy.RoleMissing] : modify.Reasons);
        }
        var number = ProcessStore.NextNumber("TL-", await ProcessStore.NextValAsync(conn, tx, "proc.tool_seq", ct));
        DateOnly? due = cmd.RequiresCalibration && cmd.LastCalibratedOn is { } last ? last.AddDays(cmd.IntervalDays!.Value) : null;
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7();
        await using (var ins = new NpgsqlCommand(
            $"""
            INSERT INTO proc.tool (id, number, name, kind, plant, description, requires_calibration, interval_days, last_calibrated_on, due_on, {MarkingColumns.InsertColumns}, created_at, created_by)
            VALUES (@id, @number, @name, @kind, @plant, @desc, @req, @interval, @last, @due, {MarkingColumns.InsertValues}, @at, @by)
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("number", number);
            ins.Parameters.AddWithValue("name", name);
            ins.Parameters.AddWithValue("kind", cmd.Kind);
            ins.Parameters.AddWithValue("plant", plant);
            ins.Parameters.Add(new NpgsqlParameter("desc", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)ProcessRules.Clean(cmd.Description) ?? DBNull.Value });
            ins.Parameters.AddWithValue("req", cmd.RequiresCalibration);
            ins.Parameters.Add(new NpgsqlParameter("interval", NpgsqlTypes.NpgsqlDbType.Integer) { Value = cmd.RequiresCalibration ? cmd.IntervalDays : DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("last", NpgsqlTypes.NpgsqlDbType.Date) { Value = cmd.RequiresCalibration && cmd.LastCalibratedOn is { } l ? l : DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("due", NpgsqlTypes.NpgsqlDbType.Date) { Value = due is { } d ? d : DBNull.Value });
            foreach (var (k, v) in MarkingColumns.Parameters(cmd.Markings)) ins.Parameters.AddWithValue(k, v);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", ProcessStore.Who(subject));
            await ins.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "tool.create", AuditOutcome.Success, ProcessStore.ToolObjectType, number, correlationId,
            ("kind", cmd.Kind), ("plant", plant), ("calibration", cmd.RequiresCalibration ? "required" : "none"), ("markings", cmd.Markings.BannerText())), ct);
        if (notifications is not null) await notifications.AutoWatchAsync(conn, tx, subject.Id, AwarenessKinds.Tool, number, ct);
        var detail = await BuildAsync(conn, tx, subject, (await ProcessStore.LoadToolAsync(conn, tx, number, false, ct))!, ct);
        await tx.CommitAsync(ct);
        return Outcome<ToolDetail>.Ok(detail);
    }

    private static List<string> ValidateTool(string name, string kind, string? description, bool requires, int? interval)
    {
        var errors = new List<string>();
        if (name.Length is < 1 or > 200) errors.Add("The name is required (at most 200 characters).");
        if (!ToolKinds.All.Contains(kind)) errors.Add("Choose tool, fixture or gauge.");
        if (description is { Length: > 1000 }) errors.Add("The description is at most 1000 characters.");
        if (requires && interval is null or < 1 or > 3660) errors.Add("A tool that needs calibration needs an interval of 1 to 3660 days.");
        return errors;
    }

    private async Task<ToolDetail> BuildAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, ToolRow row, CancellationToken ct, bool withUse = false)
    {
        var history = new List<CalibrationRecord>();
        await using (var cmd = new NpgsqlCommand("SELECT calibrated_on, next_due_on, reference, note, recorded_at, recorded_by FROM proc.tool_calibration WHERE tool_id = @t ORDER BY calibrated_on DESC, recorded_at DESC LIMIT 50", conn, tx))
        {
            cmd.Parameters.AddWithValue("t", row.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                history.Add(new CalibrationRecord(r.GetFieldValue<DateOnly>(0), r.GetFieldValue<DateOnly>(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), r.GetString(5)));
        }
        var use = withUse ? await UsedByAsync(conn, tx, subject, row.Id, ct) : [];
        var canModify = AccessPolicy.Evaluate(subject, row.Markings, PlmAction.Modify).Permitted && AccessPolicy.HasRoleFor(subject, PlmAction.Modify);
        return new ToolDetail(Summarize(row), row.Description, row.Revision, history, use, canModify);
    }

    private async Task<IReadOnlyList<ToolPlanUse>> UsedByAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, Guid toolId, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "p");
        var rows = new List<(string Plan, string Part, string Plant, string Rev, string State, string MbomNumber, Markings M)>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT DISTINCT p.number, p.part_number, p.plant, r.revision, r.lifecycle_state, p.mbom_number, {MarkingColumns.Select("p")}
            FROM proc.tool_use u JOIN proc.plan_iteration i ON i.id = u.iteration_id JOIN proc.plan_revision r ON r.id = i.revision_id AND r.latest_iteration = i.iteration
            JOIN proc.plan p ON p.id = r.plan_id
            WHERE u.tool_id = @t AND {filter.Sql} AND r.lifecycle_state <> 'Obsolete' ORDER BY p.number, r.revision
            """, conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("t", toolId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), MarkingColumns.Read(r, 6)));
        }
        var readable = await mboms.ReadableAsync(subject, rows.Select(x => x.MbomNumber).ToArray(), ct);
        return [.. rows.Where(x => readable.ContainsKey(x.MbomNumber)).Select(x => new ToolPlanUse(x.Plan, x.Part, x.Plant, x.Rev, PlanStates.Label(x.State), x.M.BannerText()))];
    }

    public async Task<Outcome<ToolDetail>> GetAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await ProcessStore.LoadToolAsync(conn, tx, number, false, ct);
        if (row is null) return Outcome<ToolDetail>.NotFound();
        var read = AccessPolicy.Evaluate(subject, row.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "tool.read", AuditOutcome.Denied, ProcessStore.ToolObjectType, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<ToolDetail>.NotFound();
        }
        var detail = await BuildAsync(conn, tx, subject, row, ct, withUse: true);
        await audit.AppendAsync(tx, Entry(subject, "tool.read", AuditOutcome.Success, ProcessStore.ToolObjectType, number, correlationId, ("plans", ProcessStore.Fmt(detail.UsedBy.Count))), ct);
        await tx.CommitAsync(ct);
        return Outcome<ToolDetail>.Ok(detail);
    }

    public async Task<Outcome<ToolPage>> SearchAsync(Subject subject, ToolSearch query, string? correlationId, CancellationToken ct = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 200);
        var offset = Math.Max(0, query.Offset);
        var filter = AccessPolicy.BuildReadFilter(subject, "t");
        var where = new List<string>();
        var parameters = new List<KeyValuePair<string, object>>(filter.Parameters);
        if (!string.IsNullOrWhiteSpace(query.Text)) { where.Add("(t.number ILIKE @q OR t.name ILIKE @q)"); parameters.Add(new("q", ProcessStore.Like(query.Text))); }
        if (!string.IsNullOrWhiteSpace(query.Plant)) { where.Add("t.plant = @plant"); parameters.Add(new("plant", query.Plant.Trim().ToUpperInvariant())); }
        if (!string.IsNullOrWhiteSpace(query.Kind)) { where.Add("t.kind = @kind"); parameters.Add(new("kind", query.Kind.Trim())); }
        if (!query.IncludeRetired) where.Add("t.status = 'active'");
        if (query.DueOnly) { where.Add("t.requires_calibration AND (t.due_on IS NULL OR t.due_on <= @soon)"); parameters.Add(new("soon", ProcessStore.Today(clock).AddDays(30))); }
        var extra = string.Concat(where.Select(w => " AND " + w));
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int total;
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM proc.tool t WHERE {filter.Sql}{extra}", conn, tx))
        {
            foreach (var (k, v) in parameters) count.Parameters.AddWithValue(k, v);
            total = Convert.ToInt32(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        var items = new List<ToolSummary>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {ProcessStore.ToolSelect()} FROM proc.tool t WHERE {filter.Sql}{extra} ORDER BY {SortKeys.OrderBy(query.Sort, "t.number", "t.number")} LIMIT @limit OFFSET @offset", conn, tx))
        {
            foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(Summarize(ProcessStore.ReadTool(r)));
        }
        var banner = Markings.AggregateBanner(items.Select(i => i.Markings).Distinct());
        await audit.AppendAsync(tx, Entry(subject, "tool.search", AuditOutcome.Success, ProcessStore.ToolObjectType, null, correlationId,
            ("plant", query.Plant), ("kind", query.Kind), ("dueOnly", query.DueOnly ? "true" : null), ("results", ProcessStore.Fmt(items.Count)), ("banner", banner)), ct);
        await tx.CommitAsync(ct);
        return Outcome<ToolPage>.Ok(new ToolPage(items, total, banner));
    }

    /// <summary>Key lookup for the palette and recent items: tools among <paramref name="numbers"/> the caller may read.</summary>
    public async Task<IReadOnlyList<ToolSummary>> FindReadableAsync(Subject subject, IReadOnlyCollection<string> numbers, CancellationToken ct = default)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "t");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {ProcessStore.ToolSelect()} FROM proc.tool t WHERE t.number = ANY(@n) AND {filter.Sql}", conn);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("n", numbers.ToArray());
        var list = new List<ToolSummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Summarize(ProcessStore.ReadTool(r)));
        return list;
    }

    public async Task<Outcome<ToolDetail>> UpdateAsync(Subject subject, string number, UpdateToolCommand cmd, int expectedRevision, string? correlationId, CancellationToken ct = default)
    {
        var name = cmd.Name?.Trim() ?? "";
        return await ChangeAsync(subject, number, "tool.update", correlationId, ct, async (conn, tx, row) =>
        {
            var errors = ValidateTool(name, row.Kind, cmd.Description, cmd.RequiresCalibration, cmd.IntervalDays);
            if (errors.Count > 0) return (Outcome<ToolDetail>.Invalid(errors), []);
            if (row.Revision != expectedRevision) return (Outcome<ToolDetail>.Conflict("The tool changed since you opened it. Reload and try again."), []);
            DateOnly? due = cmd.RequiresCalibration && row.LastCalibratedOn is { } last ? last.AddDays(cmd.IntervalDays!.Value) : null;
            await using var upd = new NpgsqlCommand(
                "UPDATE proc.tool SET name = @n, description = @d, requires_calibration = @r, interval_days = @i, due_on = @due, revision = revision + 1, updated_at = @at, updated_by = @by WHERE id = @id", conn, tx);
            upd.Parameters.AddWithValue("n", name);
            upd.Parameters.Add(new NpgsqlParameter("d", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)ProcessRules.Clean(cmd.Description) ?? DBNull.Value });
            upd.Parameters.AddWithValue("r", cmd.RequiresCalibration);
            upd.Parameters.Add(new NpgsqlParameter("i", NpgsqlTypes.NpgsqlDbType.Integer) { Value = cmd.RequiresCalibration ? cmd.IntervalDays : DBNull.Value });
            upd.Parameters.Add(new NpgsqlParameter("due", NpgsqlTypes.NpgsqlDbType.Date) { Value = due is { } d ? d : DBNull.Value });
            upd.Parameters.AddWithValue("at", clock.GetUtcNow());
            upd.Parameters.AddWithValue("by", ProcessStore.Who(subject));
            upd.Parameters.AddWithValue("id", row.Id);
            await upd.ExecuteNonQueryAsync(ct);
            return (await ReloadAsync(conn, tx, subject, number, ct), [("calibration", cmd.RequiresCalibration ? "required" : "none")]);
        });
    }

    /// <summary>Records a calibration: the next due date is the calibration date plus the tool's interval. Calibrations are append-only history.</summary>
    public Task<Outcome<ToolDetail>> RecordCalibrationAsync(Subject subject, string number, DateOnly calibratedOn, string? reference, string? note, string? correlationId, CancellationToken ct = default) =>
        ChangeAsync(subject, number, "tool.calibrate", correlationId, ct, async (conn, tx, row) =>
        {
            if (!row.RequiresCalibration) return (Outcome<ToolDetail>.Conflict("This tool does not need calibration."), []);
            if (row.Status == "retired") return (Outcome<ToolDetail>.Conflict("A retired tool cannot be calibrated."), []);
            if (calibratedOn > ProcessStore.Today(clock)) return (Outcome<ToolDetail>.Invalid(["The calibration date cannot be in the future."]), []);
            if (reference is { Length: > 100 } || note is { Length: > 500 }) return (Outcome<ToolDetail>.Invalid(["The reference is at most 100 and the note at most 500 characters."]), []);
            var next = calibratedOn.AddDays(row.IntervalDays!.Value);
            await using (var ins = new NpgsqlCommand(
                "INSERT INTO proc.tool_calibration (id, tool_id, calibrated_on, next_due_on, reference, note, recorded_at, recorded_by) VALUES (@id, @t, @c, @n, @ref, @note, @at, @by)", conn, tx))
            {
                ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
                ins.Parameters.AddWithValue("t", row.Id);
                ins.Parameters.AddWithValue("c", calibratedOn);
                ins.Parameters.AddWithValue("n", next);
                ins.Parameters.Add(new NpgsqlParameter("ref", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)ProcessRules.Clean(reference) ?? DBNull.Value });
                ins.Parameters.Add(new NpgsqlParameter("note", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)ProcessRules.Clean(note) ?? DBNull.Value });
                ins.Parameters.AddWithValue("at", clock.GetUtcNow());
                ins.Parameters.AddWithValue("by", ProcessStore.Who(subject));
                await ins.ExecuteNonQueryAsync(ct);
            }
            await using (var upd = new NpgsqlCommand("UPDATE proc.tool SET last_calibrated_on = @c, due_on = @n, revision = revision + 1, updated_at = @at, updated_by = @by WHERE id = @id", conn, tx))
            {
                upd.Parameters.AddWithValue("c", calibratedOn);
                upd.Parameters.AddWithValue("n", next);
                upd.Parameters.AddWithValue("at", clock.GetUtcNow());
                upd.Parameters.AddWithValue("by", ProcessStore.Who(subject));
                upd.Parameters.AddWithValue("id", row.Id);
                await upd.ExecuteNonQueryAsync(ct);
            }
            return (await ReloadAsync(conn, tx, subject, number, ct), [("calibratedOn", calibratedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("nextDue", next.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]);
        });

    /// <summary>Retires a tool (it cannot be added to plans; plans that use it show an error until changed) or restores it. A reason is required.</summary>
    public Task<Outcome<ToolDetail>> SetStatusAsync(Subject subject, string number, bool retire, string? reason, string? correlationId, CancellationToken ct = default) =>
        ChangeAsync(subject, number, retire ? "tool.retire" : "tool.restore", correlationId, ct, async (conn, tx, row) =>
        {
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000) return (Outcome<ToolDetail>.Invalid(["A reason is required (at most 1000 characters)."]), []);
            var to = retire ? "retired" : "active";
            if (row.Status == to) return (Outcome<ToolDetail>.Conflict($"The tool is already {to}."), []);
            await using var upd = new NpgsqlCommand("UPDATE proc.tool SET status = @s, revision = revision + 1, updated_at = @at, updated_by = @by WHERE id = @id", conn, tx);
            upd.Parameters.AddWithValue("s", to);
            upd.Parameters.AddWithValue("at", clock.GetUtcNow());
            upd.Parameters.AddWithValue("by", ProcessStore.Who(subject));
            upd.Parameters.AddWithValue("id", row.Id);
            await upd.ExecuteNonQueryAsync(ct);
            return (await ReloadAsync(conn, tx, subject, number, ct), [("reason", reason.Trim())]);
        });

    private async Task<Outcome<ToolDetail>> ReloadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string number, CancellationToken ct) =>
        Outcome<ToolDetail>.Ok(await BuildAsync(conn, tx, subject, (await ProcessStore.LoadToolAsync(conn, tx, number, false, ct))!, ct));

#pragma warning disable CA1068 // the body is the last argument by design; ct precedes it like the other helpers of the module
    private async Task<Outcome<ToolDetail>> ChangeAsync(
        Subject subject, string number, string action, string? correlationId, CancellationToken ct,
        Func<NpgsqlConnection, NpgsqlTransaction, ToolRow, Task<(Outcome<ToolDetail> Result, (string, string?)[] Details)>> body)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await ProcessStore.LoadToolAsync(conn, tx, number, true, ct);
        if (row is null) return Outcome<ToolDetail>.NotFound();
        var read = AccessPolicy.Evaluate(subject, row.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, ProcessStore.ToolObjectType, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<ToolDetail>.NotFound();
        }
        var modify = AccessPolicy.Evaluate(subject, row.Markings, PlmAction.Modify);
        if (!modify.Permitted || !AccessPolicy.HasRoleFor(subject, PlmAction.Modify))
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, ProcessStore.ToolObjectType, number, correlationId, ("reasons", string.Join(",", modify.Permitted ? [AccessPolicy.RoleMissing] : modify.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<ToolDetail>.Forbidden(modify.Permitted ? [AccessPolicy.RoleMissing] : modify.Reasons);
        }
        var (result, details) = await body(conn, tx, row);
        if (!result.IsOk)
        {
            await tx.RollbackAsync(ct);
            await audit.AppendAsync(Entry(subject, action, AuditOutcome.Failure, ProcessStore.ToolObjectType, number, correlationId, ("error", string.Join(" ", result.Errors))), ct);
            return result;
        }
        await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Success, ProcessStore.ToolObjectType, number, correlationId, details), ct);
        await tx.CommitAsync(ct);
        return result;
    }
#pragma warning restore CA1068
}
