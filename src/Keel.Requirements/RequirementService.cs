using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;
using static Keel.Requirements.RequirementStore;

namespace Keel.Requirements;

public sealed class RequirementOptions
{
    /// <summary>How recent the sign-in must be for a formal verification (e-signature step-up, like review signatures).</summary>
    public int StepUpMaxAgeSeconds { get; set; } = 300;
    /// <summary>The <c>acr</c> a formal verification's sign-in must carry (<c>Keel:Auth:StepUpAcr</c>, ADR-0058); empty = any fresh sign-in.</summary>
    public string? StepUpRequiredAcr { get; set; }
    /// <summary>Seconds between background checks of links whose part or document got a new revision. 0 turns the background check off (an administrator can still run it).</summary>
    public int SweepSeconds { get; set; } = 300;
}

/// <summary>
/// Requirement specifications and their traceability (ADR-0038). A specification is a versioned object (revision, iteration, release by review with e-signatures); its
/// requirements are permanent identities with a version row per iteration and their own markings. Every call evaluates the access policy (ADR-0004), writes its audit
/// event in the transaction of its change (denials included, ADR-0005) and serializes per specification on <c>SELECT ... FOR UPDATE</c> of the header row.
/// Requirement text is marked data: it never reaches an audit detail, a notification or a log.
/// </summary>
public sealed partial class RequirementService(
    NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, INotificationSink? notifications = null, RequirementOptions? options = null, IBaselineMembership? baselines = null)
{
    public const string ObjectType = RequirementStore.ObjectType;
    private readonly RequirementOptions opts = options ?? new RequirementOptions();

    public static readonly SortMap SortKeys = new(
        ("number", ["s.number"]), ("name", ["lower(s.name)"]), ("product", ["lower(s.product)"]), ("state", ["r.lifecycle_state"]), ("revision", ["r.revision"]), ("created", ["s.created_at"]));

    internal sealed record Step<T>(OutcomeKind Kind, IReadOnlyList<string> Errors, T? Value, (string, string?)[] Details, bool Denied = false)
    {
        public static Step<T> Done(T value, params (string, string?)[] details) => new(OutcomeKind.Ok, [], value, details);
        public static Step<T> Invalid(params string[] errors) => new(OutcomeKind.Invalid, errors, default, []);
        public static Step<T> Conflict(string message) => new(OutcomeKind.Conflict, [message], default, []);
        public static Step<T> Missing() => new(OutcomeKind.NotFound, [], default, []);
        /// <summary>A refusal on access grounds: audited as Denied, reported as invalid input so existence is not disclosed.</summary>
        public static Step<T> Hidden(string message, string reason) => new(OutcomeKind.Invalid, [message], default, [("reasons", reason)], true);
    }

    private static string Fmt(int n) => n.ToString(CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- scaffolding

    /// <summary>Loads the specification, enforces read access with auditing, runs a read body and audits its outcome.</summary>
    private async Task<Outcome<T>> ReadAsync<T>(
        Subject subject, string number, string? revision, string action, string? correlationId,
        Func<NpgsqlConnection, NpgsqlTransaction, SpecHead, Task<(Outcome<T> Result, (string, string?)[] Details)>> body, CancellationToken ct, PlmAction permission = PlmAction.Read)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await LoadSpecAsync(conn, tx, number, revision, false, ct);
        if (head is null) return Outcome<T>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        var decision = permission == PlmAction.Read ? read : AccessPolicy.Evaluate(subject, head.Markings, permission);
        if (!read.Permitted || !decision.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", (read.Permitted ? decision : read).Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.NotFound();
        }
        var (result, details) = await body(conn, tx, head);
        await audit.AppendAsync(tx, Entry(subject, action, result.IsOk ? AuditOutcome.Success : AuditOutcome.Failure, number, correlationId,
            result.IsOk ? details : [("error", string.Join(" ", result.Errors)), ("kind", result.Kind.ToString())]), ct);
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>Locks the specification row, enforces read and modify access (denials audited) and runs a mutation; its audit event commits with it.</summary>
    internal async Task<Outcome<T>> MutateAsync<T>(
        Subject subject, string number, string action, string? correlationId,
        Func<SpecHead, NpgsqlConnection, NpgsqlTransaction, Task<Step<T>>> body, CancellationToken ct, PlmAction permission = PlmAction.Modify)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await LoadSpecAsync(conn, tx, number, null, true, ct);
        if (head is null) return Outcome<T>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.NotFound();
        }
        var modify = AccessPolicy.Evaluate(subject, head.Markings, permission);
        if (!modify.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", modify.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.Forbidden(modify.Reasons);
        }
        var step = await body(head, conn, tx);
        if (step.Kind != OutcomeKind.Ok)
        {
            await tx.RollbackAsync(ct);
            await audit.AppendAsync(step.Denied
                ? Entry(subject, action, AuditOutcome.Denied, number, correlationId, step.Details)
                : Entry(subject, action, AuditOutcome.Failure, number, correlationId, ("error", string.Join(" ", step.Errors)), ("kind", step.Kind.ToString())), ct);
            return step.Kind switch
            {
                OutcomeKind.Invalid => Outcome<T>.Invalid(step.Errors),
                OutcomeKind.NotFound => Outcome<T>.NotFound(),
                _ => Outcome<T>.Conflict(step.Errors[0]),
            };
        }
        await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Success, number, correlationId, step.Details), ct);
        await tx.CommitAsync(ct);
        return Outcome<T>.Ok(step.Value!);
    }

    private async Task NotifyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string kind, SpecHead head, Subject actor, CancellationToken ct)
    {
        if (notifications is null) return;
        await notifications.AppendAsync(conn, tx, new NotificationRequest(kind, AwarenessKinds.ReqSpec, head.Number, head.Markings, actor.Id), ct);
    }

    private static SpecSummary Summary(SpecHead h, int readableCount) =>
        new(h.Id, h.Number, h.Name, h.Product, h.Revision, h.Iteration, h.State, SpecStates.Label(h.State), readableCount, h.Markings.BannerText(), h.Markings);

    // ---------------------------------------------------------------- create and search

    public async Task<Outcome<SpecDetail>> CreateAsync(Subject subject, CreateSpecCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(cmd.Name) || cmd.Name.Trim().Length > 200) errors.Add("The name is required (at most 200 characters).");
        if ((cmd.Product?.Trim().Length ?? 0) > 120) errors.Add("The product must be at most 120 characters.");
        if ((cmd.Description?.Length ?? 0) > 4000) errors.Add("The description must be at most 4000 characters.");
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Modify))
        {
            await audit.AppendAsync(tx, Entry(subject, "requirement.create", AuditOutcome.Denied, null, correlationId, ("reasons", AccessPolicy.RoleMissing)), ct);
            await tx.CommitAsync(ct);
            return Outcome<SpecDetail>.Forbidden([AccessPolicy.RoleMissing]);
        }
        if (!AccessPolicy.Evaluate(subject, cmd.Markings, PlmAction.Modify).Permitted)
            errors.Add($"Markings {cmd.Markings.BannerText()} would lock you out of the specification you are creating.");
        if (errors.Count > 0)
        {
            await tx.RollbackAsync(ct);
            return Outcome<SpecDetail>.Invalid(errors);
        }
        var lifecycle = await LifecycleAsync(conn, tx, await Lifecycle.CurrentIdAsync(conn, tx, "reqspec-default", ct), ct);
        var number = await Numbering.NextAsync(conn, tx, ObjectType, ct);
        var now = clock.GetUtcNow();
        var who = Who(subject);
        var id = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        await using (var ins = new NpgsqlCommand(
            $"""
            INSERT INTO req.spec (id, number, name, product, description, lifecycle_id, {MarkingColumns.InsertColumns}, created_at, created_by)
            VALUES (@id, @number, @name, @product, @desc, @lc, {MarkingColumns.InsertValues}, @at, @by);
            INSERT INTO req.spec_revision (id, spec_id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by)
            VALUES (@rid, @id, @rev, @state, true, 1, @at, @by, @at, @by)
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("number", number);
            ins.Parameters.AddWithValue("name", cmd.Name.Trim());
            ins.Parameters.Add(new NpgsqlParameter("product", NpgsqlTypes.NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(cmd.Product) ? DBNull.Value : cmd.Product.Trim() });
            ins.Parameters.AddWithValue("desc", cmd.Description?.Trim() ?? "");
            ins.Parameters.AddWithValue("lc", lifecycle.Id);
            foreach (var (k, v) in MarkingColumns.Parameters(cmd.Markings)) ins.Parameters.AddWithValue(k, v);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", who);
            ins.Parameters.AddWithValue("rid", revisionId);
            ins.Parameters.AddWithValue("rev", RevisionScheme.First);
            ins.Parameters.AddWithValue("state", lifecycle.Initial.State);
            await ins.ExecuteNonQueryAsync(ct);
        }
        // The revision row already says latest_iteration = 1; its first iteration (no requirements yet) is written now.
        await WriteIterationAsync(conn, tx, id, revisionId, 1, "Created", [], now, who, false, ct);
        var head = (await LoadSpecAsync(conn, tx, number, null, false, ct))!;
        await audit.AppendAsync(tx, Entry(subject, "requirement.create", AuditOutcome.Success, number, correlationId, ("banner", cmd.Markings.BannerText())), ct);
        if (notifications is not null) await notifications.AutoWatchAsync(conn, tx, subject.Id, AwarenessKinds.ReqSpec, number, ct);
        var detail = await BuildDetailAsync(conn, tx, subject, head, ct);
        await tx.CommitAsync(ct);
        return Outcome<SpecDetail>.Ok(detail);
    }

    public async Task<Outcome<SpecPage>> SearchAsync(Subject subject, SpecSearch query, string? correlationId, CancellationToken ct = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 200);
        var offset = Math.Max(0, query.Offset);
        var sFilter = AccessPolicy.BuildReadFilter(subject, "s");
        var vFilter = AccessPolicy.BuildReadFilter(subject, "v");
        var where = new List<string> { sFilter.Sql };
        var parms = new List<KeyValuePair<string, object>>(sFilter.Parameters);
        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            where.Add("(s.number ILIKE @q OR s.name ILIKE @q OR s.product ILIKE @q)");
            parms.Add(new("q", "%" + query.Query.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%"));
        }
        if (!string.IsNullOrWhiteSpace(query.State)) { where.Add("r.lifecycle_state = @state"); parms.Add(new("state", query.State)); }
        if (!string.IsNullOrWhiteSpace(query.Program)) { where.Add("s.program = @prog"); parms.Add(new("prog", query.Program)); }
        var whereSql = string.Join(" AND ", where);
        var order = SortKeys.OrderBy(query.Sort, "s.number", "s.number");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int total;
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM req.spec s JOIN req.spec_revision r ON r.spec_id = s.id AND r.is_latest WHERE {whereSql}", conn, tx))
        {
            foreach (var p in parms) count.Parameters.AddWithValue(p.Key, p.Value);
            total = Convert.ToInt32(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        var items = new List<SpecSummary>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT s.id, s.number, s.name, s.product, r.revision, r.latest_iteration, r.lifecycle_state,
                   (SELECT count(*) FROM req.requirement_version v WHERE v.iteration_id = i.id AND {vFilter.Sql}), {MarkingColumns.Select("s")}
            FROM req.spec s
            JOIN req.spec_revision r ON r.spec_id = s.id AND r.is_latest
            JOIN req.spec_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            WHERE {whereSql}
            ORDER BY {order}
            LIMIT @limit OFFSET @offset
            """, conn, tx))
        {
            foreach (var p in parms) cmd.Parameters.AddWithValue(p.Key, p.Value);
            foreach (var p in vFilter.Parameters) cmd.Parameters.AddWithValue(p.Key, p.Value);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var m = MarkingColumns.Read(r, 8);
                items.Add(new SpecSummary(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetString(6), SpecStates.Label(r.GetString(6)),
                    Convert.ToInt32(r.GetValue(7), CultureInfo.InvariantCulture), m.BannerText(), m));
            }
        }
        await audit.AppendAsync(tx, Entry(subject, "requirement.search", AuditOutcome.Success, null, correlationId, ("results", Fmt(items.Count)), ("total", Fmt(total))), ct);
        await tx.CommitAsync(ct);
        return Outcome<SpecPage>.Ok(new SpecPage(items, total, Markings.AggregateBanner(items.Select(i => i.Markings))));
    }

    // ---------------------------------------------------------------- read

    public Task<Outcome<SpecDetail>> GetAsync(Subject subject, string number, string? revision, string? correlationId, CancellationToken ct = default) =>
        ReadAsync<SpecDetail>(subject, number, revision, "requirement.read", correlationId, async (conn, tx, head) =>
        {
            var detail = await BuildDetailAsync(conn, tx, subject, head, ct);
            return (Outcome<SpecDetail>.Ok(detail), [("revision", head.Label), ("banner", detail.Summary.Banner)]);
        }, ct);

    private static async Task<SpecDetail> BuildDetailAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, SpecHead head, CancellationToken ct)
    {
        var revisions = new List<SpecRevisionInfo>();
        await using (var cmd = new NpgsqlCommand("SELECT id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by FROM req.spec_revision WHERE spec_id = @s ORDER BY created_at DESC, revision DESC", conn, tx))
        {
            cmd.Parameters.AddWithValue("s", head.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                revisions.Add(new SpecRevisionInfo(r.GetGuid(0), r.GetString(1), r.GetString(2), SpecStates.Label(r.GetString(2)), r.GetBoolean(3), r.GetInt32(4), r.GetFieldValue<DateTimeOffset>(5), r.GetString(6)));
        }
        var vFilter = AccessPolicy.BuildReadFilter(subject, "v");
        int readable;
        await using (var cmd = new NpgsqlCommand($"SELECT count(*) FROM req.requirement_version v WHERE v.iteration_id = @i AND {vFilter.Sql}", conn, tx))
        {
            cmd.Parameters.AddWithValue("i", head.IterationId);
            foreach (var p in vFilter.Parameters) cmd.Parameters.AddWithValue(p.Key, p.Value);
            readable = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        var modify = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Modify);
        var displayed = revisions.First(r => r.Id == head.RevisionId);
        return new SpecDetail(Summary(head, readable), head.Description, revisions, displayed, head.IterationComment ?? "", head.IterationAt, head.IterationBy,
            modify.Permitted, AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Administer).Permitted, modify.Reasons, head.IsLatest && head.State == SpecStates.InWork);
    }

    /// <summary>
    /// The requirements of a revision's latest iteration in outline order. A requirement the caller may not read is a placeholder at its place in the outline (number and depth only).
    /// </summary>
    public Task<Outcome<RequirementSet>> GetRequirementsAsync(Subject subject, string number, string? revision, string? correlationId, CancellationToken ct = default) =>
        ReadAsync<RequirementSet>(subject, number, revision, "requirement.list", correlationId, async (conn, tx, head) =>
        {
            var rows = await ReadRowsAsync(conn, tx, subject, head.IterationId, ct);
            var outline = BuildRows(rows);
            var set = new RequirementSet(head.Number, head.Revision, head.Iteration, head.State, head.IsLatest, outline, Markings.AggregateBanner(outline.Where(r => r.Markings is not null).Select(r => r.Markings!).Append(head.Markings)));
            return (Outcome<RequirementSet>.Ok(set), [("revision", head.Label), ("rows", Fmt(outline.Count)), ("restricted", Fmt(outline.Count(r => r.Redacted)))]);
        }, ct);

    public async Task<Outcome<RequirementRow>> GetRequirementAsync(Subject subject, string number, string key, string? correlationId, CancellationToken ct = default)
    {
        var set = await GetRequirementsAsync(subject, number, null, correlationId, ct);
        if (!set.IsOk) return Outcome<RequirementRow>.NotFound();
        var row = set.Value!.Rows.FirstOrDefault(r => !r.Redacted && r.Key == key);
        return row is null ? Outcome<RequirementRow>.NotFound() : Outcome<RequirementRow>.Ok(row);
    }

    /// <summary>One requirement version as the caller may read it (null content for a requirement the filter hides). The identity and place are not marked data.</summary>
    internal sealed record RawRow(Guid Id, Guid? ParentId, int Ordinal, string? TieKey, WorkReq? Visible);

    internal static async Task<List<RawRow>> ReadRowsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, Guid iterationId, CancellationToken ct)
    {
        var vFilter = AccessPolicy.BuildReadFilter(subject, "v");
        var list = new List<RawRow>();
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT p.requirement_id, p.parent_requirement_id, p.ordinal, v.id, q.key, v.title, v.body, v.req_type, v.priority, v.verification_method, v.rationale, v.status, {MarkingColumns.Select("v")}
            FROM req.requirement_version p
            LEFT JOIN req.requirement_version v ON v.id = p.id AND {vFilter.Sql}
            LEFT JOIN req.requirement q ON q.id = v.requirement_id
            WHERE p.iteration_id = @i
            """, conn, tx);
        cmd.Parameters.AddWithValue("i", iterationId);
        foreach (var p in vFilter.Parameters) cmd.Parameters.AddWithValue(p.Key, p.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetGuid(0);
            var parent = r.IsDBNull(1) ? (Guid?)null : r.GetGuid(1);
            var ordinal = r.GetInt32(2);
            if (r.IsDBNull(3)) { list.Add(new RawRow(id, parent, ordinal, null, null)); continue; }
            var w = new WorkReq(id, r.GetString(4), parent, ordinal, r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10), r.GetString(11), MarkingColumns.Read(r, 12));
            list.Add(new RawRow(id, parent, ordinal, w.Key, w));
        }
        return list;
    }

    internal static List<RequirementRow> BuildRows(IReadOnlyList<RawRow> raw)
    {
        var order = RequirementRules.Outline([.. raw.Select(r => (r.Id, r.ParentId, r.Ordinal, r.TieKey ?? r.Id.ToString("N")))]);
        var byId = raw.ToDictionary(r => r.Id);
        var rows = new List<RequirementRow>(order.Count);
        foreach (var (id, outline, depth) in order)
        {
            var r = byId[id];
            if (r.Visible is not { } w) { rows.Add(new RequirementRow(true, outline, depth, null, null, null, null, null, null, null, null, null, null, null, null)); continue; }
            var parentKey = r.ParentId is { } p && byId.TryGetValue(p, out var pr) ? pr.Visible?.Key : null;
            rows.Add(new RequirementRow(false, outline, depth, w.Key, parentKey, w.Title, w.Text, w.Type, w.Priority, w.Method, w.Rationale, w.Status, w.Sha, w.Markings.BannerText(), w.Markings));
        }
        return rows;
    }
}
