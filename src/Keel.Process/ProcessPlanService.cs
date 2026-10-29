using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Core.Viewing;
using Keel.Security;
using Npgsql;

namespace Keel.Process;

/// <summary>
/// Process plans per MBOM (ADR-0042): routings, operations, component assignments and work instructions, revisioned like the MBOM they are written against. Every call evaluates the access
/// policy on the plan and on its MBOM (and part, through the MBOM module), writes its audit event in the same transaction as its change (denials included) and serializes per plan on
/// <c>SELECT ... FOR UPDATE</c> of the plan row. Audit details carry numbers, codes and counts only.
/// </summary>
public sealed partial class ProcessPlanService(
    NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, IMbomProcessSource mboms, IViewableFileSource? files = null, INotificationSink? notifications = null)
{
    public const string ObjectType = ProcessStore.PlanObjectType;

    public static readonly SortMap SortKeys = new(
        ("number", ["p.number"]), ("part", ["p.part_number"]), ("plant", ["p.plant"]), ("name", ["lower(p.part_name)"]), ("mbom", ["p.mbom_number"]), ("state", ["r.lifecycle_state"]));

    internal sealed record Step<T>(OutcomeKind Kind, IReadOnlyList<string> Errors, T? Value, (string, string?)[] Details, bool Denied = false)
    {
        public static Step<T> Done(T value, params (string, string?)[] details) => new(OutcomeKind.Ok, [], value, details);
        public static Step<T> Invalid(params string[] errors) => new(OutcomeKind.Invalid, errors, default, []);
        public static Step<T> Conflict(string message) => new(OutcomeKind.Conflict, [message], default, []);
        public static Step<T> Hidden(string message, string reason) => new(OutcomeKind.Invalid, [message], default, [("reasons", reason)], true);
    }

    internal static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string? number, string? correlationId, params (string, string?)[] details) =>
        ProcessStore.Entry(s, action, outcome, ObjectType, number, correlationId, details);

    private static string Fmt(int n) => ProcessStore.Fmt(n);

    // ---------------------------------------------------------------- scaffolding

    private async Task<Outcome<T>> ReadAsync<T>(
        Subject subject, string number, string? revision, string action, string? correlationId,
        Func<NpgsqlConnection, NpgsqlTransaction, PlanHead, MbomForProcess, Task<(Outcome<T> Result, (string, string?)[] Details)>> body, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await ProcessStore.LoadByNumberAsync(conn, tx, number, revision, false, ct);
        if (head is null) return Outcome<T>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        MbomForProcess? mbom = read.Permitted ? await mboms.FindAsync(subject, head.MbomNumber, correlationId, ct) : null;
        if (!read.Permitted || mbom is null)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", read.Permitted ? "mbom_not_readable" : string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.NotFound();
        }
        var (result, details) = await body(conn, tx, head, mbom);
        await audit.AppendAsync(tx, Entry(subject, action, result.IsOk ? AuditOutcome.Success : AuditOutcome.Failure, number, correlationId,
            result.IsOk ? details : [("error", string.Join(" ", result.Errors)), ("kind", result.Kind.ToString())]), ct);
        await tx.CommitAsync(ct);
        return result;
    }

    internal async Task<Outcome<T>> MutateAsync<T>(
        Subject subject, string number, string action, string? correlationId,
        Func<PlanHead, MbomForProcess, NpgsqlConnection, NpgsqlTransaction, Task<Step<T>>> body, CancellationToken ct, bool allowAdminWithoutModify = false)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await ProcessStore.LoadByNumberAsync(conn, tx, number, null, true, ct);
        if (head is null) return Outcome<T>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        MbomForProcess? mbom = read.Permitted ? await mboms.FindAsync(subject, head.MbomNumber, correlationId, ct) : null;
        if (!read.Permitted || mbom is null)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", read.Permitted ? "mbom_not_readable" : string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.NotFound();
        }
        var modify = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Modify);
        if (!modify.Permitted && allowAdminWithoutModify && AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Administer).Permitted) modify = AccessDecision.Permit;
        if (!modify.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", modify.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.Forbidden(modify.Reasons);
        }
        var step = await body(head, mbom, conn, tx);
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

    // ---------------------------------------------------------------- summaries

    private static PlanSummary Summarize(PlanHead h, MbomForProcess mbom)
    {
        var stale = ProcessRules.StalenessOf(h.BoundRevision, h.BoundSha, mbom.LatestReleased);
        return new PlanSummary(h.Number, h.MbomNumber, h.PartNumber, h.PartName, h.Plant, h.Revision, h.State, PlanStates.Label(h.State), h.Label, h.IsOpen, h.OpenByName ?? h.OpenBy,
            h.BoundRevision, stale, mbom.LatestReleased?.Revision, h.Markings.BannerText(), h.Markings);
    }

    // ---------------------------------------------------------------- search

    public async Task<Outcome<PlanResults>> SearchAsync(Subject subject, PlanSearch query, string? correlationId, CancellationToken ct = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 200);
        var offset = Math.Max(0, query.Offset);
        var filter = AccessPolicy.BuildReadFilter(subject, "p");
        var where = new List<string>();
        var parameters = new List<KeyValuePair<string, object>>(filter.Parameters);
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            where.Add("(p.number ILIKE @q OR p.part_number ILIKE @q OR p.part_name ILIKE @q OR p.mbom_number ILIKE @q)");
            parameters.Add(new("q", ProcessStore.Like(query.Text)));
        }
        if (!string.IsNullOrWhiteSpace(query.Plant)) { where.Add("p.plant = @plant"); parameters.Add(new("plant", query.Plant.Trim().ToUpperInvariant())); }
        if (!string.IsNullOrWhiteSpace(query.State)) { where.Add("r.lifecycle_state = @state"); parameters.Add(new("state", query.State.Trim())); }
        var extra = string.Concat(where.Select(w => " AND " + w));
        var rows = new List<PlanHead>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // The plan filter is applied in SQL; the MBOM (and its part) is checked per row by its owner below, so a plan whose MBOM the reader cannot read is invisible.
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT p.number FROM proc.plan p JOIN proc.plan_revision r ON r.plan_id = p.id AND r.is_latest
            WHERE {filter.Sql}{extra} ORDER BY {SortKeys.OrderBy(query.Sort, "p.number", "p.number")} LIMIT 500
            """, conn, tx))
        {
            foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
            var numbers = new List<string>();
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) numbers.Add(r.GetString(0));
            foreach (var n in numbers)
                if (await ProcessStore.LoadByNumberAsync(conn, tx, n, null, false, ct) is { } h) rows.Add(h);
        }
        var readable = await mboms.ReadableAsync(subject, rows.Select(h => h.MbomNumber).ToArray(), ct);
        var items = rows.Where(h => readable.ContainsKey(h.MbomNumber)).Select(h => Summarize(h, readable[h.MbomNumber])).ToList();
        if (query.StaleOnly) items = [.. items.Where(i => i.Stale != Staleness.Current)];
        var total = items.Count;
        var page = items.Skip(offset).Take(limit).ToList();
        var banner = Markings.AggregateBanner(page.Select(i => i.Markings).Distinct());
        await audit.AppendAsync(tx, Entry(subject, "process.search", AuditOutcome.Success, null, correlationId,
            ("plant", query.Plant), ("state", query.State), ("staleOnly", query.StaleOnly ? "true" : null), ("sort", query.Sort is null ? null : $"{query.Sort.Key} {query.Sort.DirectionText}"),
            ("results", Fmt(page.Count)), ("banner", banner)), ct);
        await tx.CommitAsync(ct);
        return Outcome<PlanResults>.Ok(new PlanResults(page, total, banner));
    }

    /// <summary>Key lookup for recent items and the palette: the plans among <paramref name="numbers"/> the caller may read (plan and MBOM).</summary>
    public async Task<IReadOnlyList<PlanSummary>> FindReadableAsync(Subject subject, IReadOnlyCollection<string> numbers, CancellationToken ct = default)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "p");
        var heads = new List<PlanHead>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var found = new List<string>();
        await using (var cmd = new NpgsqlCommand($"SELECT p.number FROM proc.plan p WHERE p.number = ANY(@n) AND {filter.Sql}", conn))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("n", numbers.ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) found.Add(r.GetString(0));
        }
        foreach (var n in found)
            if (await ProcessStore.LoadByNumberAsync(conn, null, n, null, false, ct) is { } h) heads.Add(h);
        var readable = await mboms.ReadableAsync(subject, heads.Select(h => h.MbomNumber).ToArray(), ct);
        return [.. heads.Where(h => readable.ContainsKey(h.MbomNumber)).Select(h => Summarize(h, readable[h.MbomNumber]))];
    }

    /// <summary>The plans of one MBOM the caller may read (the MBOM page).</summary>
    public async Task<Outcome<IReadOnlyList<PlanSummary>>> ForMbomAsync(Subject subject, string mbomNumber, string? correlationId, CancellationToken ct = default)
    {
        var mbom = await mboms.FindAsync(subject, mbomNumber, correlationId, ct);
        if (mbom is null) return Outcome<IReadOnlyList<PlanSummary>>.NotFound();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var head = await ProcessStore.LoadByNumberAsync(conn, null, await PlanNumberOfMbomAsync(conn, mbomNumber, ct) ?? "", null, false, ct);
        IReadOnlyList<PlanSummary> list = head is not null && AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read).Permitted ? [Summarize(head, mbom)] : [];
        return Outcome<IReadOnlyList<PlanSummary>>.Ok(list);
    }

    private static async Task<string?> PlanNumberOfMbomAsync(NpgsqlConnection conn, string mbomNumber, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT p.number FROM proc.plan p WHERE p.mbom_number = @m", conn);
        cmd.Parameters.AddWithValue("m", mbomNumber);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    // ---------------------------------------------------------------- create

    public async Task<Outcome<PlanDetail>> CreateAsync(Subject subject, CreatePlanCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        var description = cmd.Description?.Trim() ?? "";
        if (description.Length > 4000) return Outcome<PlanDetail>.Invalid(["The description must be at most 4000 characters."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var mbom = string.IsNullOrWhiteSpace(cmd.MbomNumber) ? null : await mboms.FindAsync(subject, cmd.MbomNumber.Trim(), correlationId, ct);
        Outcome<PlanDetail>? refusal = null;
        string? denial = null;
        if (mbom is null) { refusal = Outcome<PlanDetail>.Invalid([$"MBOM '{cmd.MbomNumber}' was not found, or you are not authorized to see it."]); denial = "mbom_not_readable"; }
        else if (!AccessPolicy.HasRoleFor(subject, PlmAction.Modify)) { refusal = Outcome<PlanDetail>.Invalid(["Your roles do not permit creating a process plan."]); denial = AccessPolicy.RoleMissing; }
        if (refusal is not null)
        {
            await audit.AppendAsync(tx, Entry(subject, "process.plan_create", AuditOutcome.Denied, null, correlationId, ("mbom", cmd.MbomNumber), ("reasons", denial)), ct);
            await tx.CommitAsync(ct);
            return refusal;
        }
        if (mbom is null) throw new InvalidOperationException("unreachable");
        if (mbom.LatestReleased is null)
            return await FailAsync(tx, subject, "process.plan_create", null, correlationId, $"{mbom.MbomNumber} has no released revision. A process plan is written against a released MBOM revision.", ct);
        if (await PlanNumberOfMbomAsync(conn, mbom.MbomNumber, ct) is { } existing)
            return await FailAsync(tx, subject, "process.plan_create", null, correlationId, $"{mbom.MbomNumber} already has the process plan {existing}.", ct, conflict: true);
        var markings = cmd.Markings ?? mbom.Markings;
        IReadOnlyList<Markings> floor = [mbom.Markings, mbom.PartMarkings];
        if (!MarkingDominance.CoversAll(markings, floor))
        {
            var cover = MarkingDominance.MinimalCover(markings, floor);
            if (!cover.IsSuccess) return await FailAsync(tx, subject, "process.plan_create", null, correlationId, string.Join(" ", cover.Errors), ct);
            if (!cmd.ConfirmRaiseMarkings)
                return await FailAsync(tx, subject, "process.plan_create", null, correlationId,
                    $"The markings {markings.BannerText()} do not cover the MBOM {mbom.MbomNumber}. A plan must be at least as restrictive as its MBOM; use {cover.Value.BannerText()}.", ct);
            markings = cover.Value;
        }
        if (!AccessPolicy.Evaluate(subject, markings, PlmAction.Modify).Permitted)
            return await FailAsync(tx, subject, "process.plan_create", null, correlationId, $"Markings {markings.BannerText()} would lock you out of the plan you are creating.", ct);

        var number = ProcessStore.NextNumber("PP-", await ProcessStore.NextValAsync(conn, tx, "proc.plan_seq", ct));
        var now = clock.GetUtcNow();
        var who = ProcessStore.Who(subject);
        var id = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        {
            var mid = mbom.MbomId;
            await using var ins = new NpgsqlCommand(
                $"""
                INSERT INTO proc.plan (id, number, mbom_id, mbom_number, part_number, part_name, plant, description, {MarkingColumns.InsertColumns}, created_at, created_by)
                VALUES (@id, @number, @mid, @mbom, @part, @pname, @plant, @desc, {MarkingColumns.InsertValues}, @at, @by);
                INSERT INTO proc.plan_revision (id, plan_id, revision, lifecycle_state, state_changed_at, state_changed_by, is_latest, latest_iteration, bound_mbom_revision, bound_mbom_sha, bound_at, bound_by, created_at, created_by)
                VALUES (@rid, @id, @rev, 'InWork', @at, @by, true, 1, @brev, @bsha, @at, @by, @at, @by);
                INSERT INTO proc.plan_iteration (id, revision_id, iteration, is_open, opened_by, opened_by_name, content, created_at, created_by)
                VALUES (@iid, @rid, 1, true, @sub, @by, @content::jsonb, @at, @by)
                """, conn, tx);
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("number", number);
            ins.Parameters.AddWithValue("mid", mid);
            ins.Parameters.AddWithValue("mbom", mbom.MbomNumber);
            ins.Parameters.AddWithValue("part", mbom.PartNumber);
            ins.Parameters.AddWithValue("pname", mbom.PartName);
            ins.Parameters.AddWithValue("plant", mbom.Plant);
            ins.Parameters.AddWithValue("desc", description);
            foreach (var (k, v) in MarkingColumns.Parameters(markings)) ins.Parameters.AddWithValue(k, v);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", who);
            ins.Parameters.AddWithValue("rid", revisionId);
            ins.Parameters.AddWithValue("rev", RevisionScheme.First);
            ins.Parameters.AddWithValue("brev", mbom.LatestReleased.Revision);
            ins.Parameters.AddWithValue("bsha", mbom.LatestReleased.ContentSha256);
            ins.Parameters.AddWithValue("iid", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("sub", subject.Id);
            ins.Parameters.AddWithValue("content", ProcessRules.Serialize(PlanContent.Empty));
            await ins.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "process.plan_create", AuditOutcome.Success, number, correlationId,
            ("mbom", mbom.MbomNumber), ("plant", mbom.Plant), ("mbomRevision", mbom.LatestReleased.Revision), ("banner", markings.BannerText())), ct);
        if (notifications is not null) await notifications.AutoWatchAsync(conn, tx, subject.Id, AwarenessKinds.ProcessPlan, number, ct);
        var head = (await ProcessStore.LoadByNumberAsync(conn, tx, number, null, false, ct))!;
        var detail = await BuildDetailAsync(conn, tx, subject, head, mbom, correlationId, ct);
        await tx.CommitAsync(ct);
        return Outcome<PlanDetail>.Ok(detail);
    }

    private async Task<Outcome<PlanDetail>> FailAsync(NpgsqlTransaction tx, Subject subject, string action, string? number, string? corr, string message, CancellationToken ct, bool conflict = false)
    {
        await tx.RollbackAsync(ct);
        await audit.AppendAsync(Entry(subject, action, AuditOutcome.Failure, number, corr, ("error", message)), ct);
        return conflict ? Outcome<PlanDetail>.Conflict(message) : Outcome<PlanDetail>.Invalid([message]);
    }

    // ---------------------------------------------------------------- read

    public Task<Outcome<PlanDetail>> GetAsync(Subject subject, string number, string? revision, string? correlationId, CancellationToken ct = default) =>
        ReadAsync<PlanDetail>(subject, number, revision, "process.read", correlationId, async (conn, tx, head, mbom) =>
        {
            var detail = await BuildDetailAsync(conn, tx, subject, head, mbom, correlationId, ct);
            return (Outcome<PlanDetail>.Ok(detail), [("revision", detail.Summary.IterationLabel), ("banner", detail.Summary.Banner)]);
        }, ct);

    /// <summary>The iteration other readers see: the open working copy belongs to its owner; everyone else reads the last checked-in iteration (null when there is none yet).</summary>
    private static async Task<(string? Json, int Iteration, bool Working)> VisibleContentAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, PlanHead head, CancellationToken ct)
    {
        if (!head.IsOpen) return (head.ContentJson, head.LatestIteration, false);
        if (head.OpenBy == subject.Id) return (head.ContentJson, head.LatestIteration, true);
        await using var cmd = new NpgsqlCommand("SELECT i.content::text FROM proc.plan_iteration i WHERE i.revision_id = @r AND i.iteration = @n", conn, tx);
        cmd.Parameters.AddWithValue("r", head.RevisionId);
        cmd.Parameters.AddWithValue("n", head.LatestIteration - 1);
        var json = (string?)await cmd.ExecuteScalarAsync(ct);
        return (json, head.LatestIteration - 1, false);
    }

    private async Task<PlanDetail> BuildDetailAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, PlanHead head, MbomForProcess mbom, string? corr, CancellationToken ct)
    {
        var revisions = new List<RevisionRow>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT revision, lifecycle_state, latest_iteration, is_latest, bound_mbom_revision, state_changed_at, state_changed_by FROM proc.plan_revision WHERE plan_id = @p ORDER BY length(revision) DESC, revision DESC", conn, tx))
        {
            cmd.Parameters.AddWithValue("p", head.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) revisions.Add(new RevisionRow(r.GetString(0), r.GetString(1), PlanStates.Label(r.GetString(1)), r.GetInt32(2), r.GetBoolean(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5), r.GetString(6)));
        }
        var (json, iteration, working) = await VisibleContentAsync(conn, tx, subject, head, ct);
        var content = json is null ? PlanContent.Empty : ProcessRules.Normalize(ProcessRules.Deserialize(json));
        var linesOutcome = await mboms.LinesAsync(subject, head.MbomNumber, head.BoundRevision, corr, ct);
        IReadOnlyList<MbomLineRef> lines = linesOutcome.IsOk ? linesOutcome.Value! : [];
        var view = await BuildViewAsync(conn, tx, subject, head, content, lines, ct);
        var findings = await FindingsAsync(conn, tx, subject, head, mbom, content, lines, linesOutcome.IsOk, ct);
        var summary = Summarize(head, mbom) with { IterationLabel = $"{head.Revision}.{Fmt(iteration)}" };
        var modify = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Modify).Permitted && AccessPolicy.HasRoleFor(subject, PlmAction.Modify);
        var owner = head.IsOpen && head.OpenBy == subject.Id;
        var permissions = new PlanPermissions(modify, AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Administer).Permitted, owner,
            modify && head.IsLatest && head.State == PlanStates.InWork && !head.IsOpen, modify && head.IsLatest && head.State == PlanStates.Released);
        var boundLines = lines.Select(l => new BoundLineView(l.Find, l.Alt, l.Redacted, l.ChildNumber, l.ChildName, l.Quantity, l.Unit, l.Phantom, l.SupplyType, l.ChildMasterId ?? Guid.Empty)).ToList();
        return new PlanDetail(summary, head.Description, revisions, view, boundLines, findings, working, head.CreatedAt, head.CreatedBy, permissions);
    }

    // ---------------------------------------------------------------- resolving content for display (redaction happens here)

    internal async Task<IReadOnlyList<RoutingView>> BuildViewAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, PlanHead head, PlanContent content, IReadOnlyList<MbomLineRef> lines, CancellationToken ct, ICollection<Markings>? seen = null)
    {
        var today = ProcessStore.Today(clock);
        var workCenters = await ProcessStore.WorkCenterNamesAsync(conn, tx, head.Plant, ct);
        var toolIds = content.Routings.SelectMany(r => r.Operations).SelectMany(o => o.Tools.Concat(o.Steps.SelectMany(s => s.Tools))).Distinct().ToArray();
        var tools = new Dictionary<Guid, ToolRow>();
        if (toolIds.Length > 0)
        {
            var filter = AccessPolicy.BuildReadFilter(subject, "t");
            await using var cmd = new NpgsqlCommand($"SELECT {ProcessStore.ToolSelect()} FROM proc.tool t WHERE t.id = ANY(@ids) AND {filter.Sql}", conn, tx);
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("ids", toolIds);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { var t = ProcessStore.ReadTool(r); tools[t.Id] = t; }
        }
        ToolView ToolOf(Guid id)
        {
            if (!tools.TryGetValue(id, out var t)) return new ToolView(id, true, null, null, null, null, null, null, false);
            seen?.Add(t.Markings);
            return new ToolView(id, false, t.Number, t.Name, t.Kind, t.Status, t.DueOn, t.Markings.BannerText(),
                t.RequiresCalibration && ProcessRules.CalibrationState(true, t.DueOn, today) is "overdue" or "never");
        }
        var byKey = lines.ToDictionary(l => (l.Find, l.Alt));
        var attachmentCache = new Dictionary<(string, Guid), AttachmentView>();
        var routings = new List<RoutingView>();
        foreach (var r in content.Routings)
        {
            var ops = new List<OperationView>();
            foreach (var o in r.Operations)
            {
                var comps = new List<ComponentView>();
                foreach (var c in o.Components)
                {
                    if (!byKey.TryGetValue((c.Find, c.Alt), out var line)) continue; // an orphan is reported as a finding, never shown as a component
                    if (line.Redacted) { comps.Add(new ComponentView(c.Find, c.Alt, true, null, null, line.Quantity, line.Unit, c.Quantity ?? line.Quantity, c.Quantity is null, null, false)); continue; }
                    if (line.Markings is { } lm) seen?.Add(lm);
                    comps.Add(new ComponentView(c.Find, c.Alt, false, line.ChildNumber, line.ChildName, line.Quantity, line.Unit, c.Quantity ?? line.Quantity, c.Quantity is null, line.Markings?.BannerText(), line.ChildMasterId != c.PartMasterId));
                }
                var steps = new List<StepView>();
                foreach (var s in o.Steps)
                {
                    var atts = new List<AttachmentView>();
                    foreach (var a in s.Attachments)
                    {
                        if (!attachmentCache.TryGetValue((a.Document, a.FileId), out var av))
                            attachmentCache[(a.Document, a.FileId)] = av = await DescribeAttachmentAsync(conn, tx, subject, a, seen, ct);
                        atts.Add(av with { Caption = a.Caption });
                    }
                    steps.Add(new StepView(s.Id, s.Kind, StepKinds.Label(s.Kind), s.Text, [.. s.Tools.Select(ToolOf)], atts));
                }
                ops.Add(new OperationView(o.Id, o.Number, o.Name, o.WorkCenter, o.WorkCenter is not null && workCenters.TryGetValue(o.WorkCenter, out var wn) ? wn : null,
                    o.SetupMinutes, o.RunMinutes, o.Condition, o.Inspection, o.InspectionText, comps, [.. o.Tools.Select(ToolOf)], steps));
            }
            routings.Add(new RoutingView(r.Code, r.Name, r.IsPrimary, r.Condition, r.Note, ops));
        }
        return routings;
    }

    /// <summary>
    /// Describes one attachment under the reader's own access. A document the reader may not view (or that is gone) is "restricted"; a file the scanner quarantined is "quarantined".
    /// Nothing about a restricted document (number, name, file) is returned.
    /// </summary>
    private async Task<AttachmentView> DescribeAttachmentAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, AttachmentDef a, ICollection<Markings>? seen, CancellationToken ct)
    {
        const string restricted = "restricted";
        if (files is null) return new AttachmentView("", a.FileId, a.Caption, restricted, null, false, null, null, null);
        var described = await files.DescribeAsync(subject, a.Document, a.FileId, null, ct);
        if (!described.IsOk) return new AttachmentView("", a.FileId, a.Caption, restricted, null, false, null, null, null);
        var f = described.Value!;
        seen?.Add(f.Markings);
        if (await IsQuarantinedAsync(conn, tx, f.DocumentNumber, f.Sha256, ct))
            return new AttachmentView(f.DocumentNumber, f.FileId, a.Caption, "quarantined", f.FileName, false, f.Banner, null, null);
        var image = FileFormats.FromFileName(f.FileName).Viewer == ViewerTypes.Image;
        var n = Uri.EscapeDataString(f.DocumentNumber);
        return new AttachmentView(f.DocumentNumber, f.FileId, a.Caption, "available", f.FileName, image, f.Banner,
            image ? $"/viewer/content/documents/{n}/files/{f.FileId:D}" : null, $"/viewer/documents/{n}/files/{f.FileId:D}");
    }

    internal static async Task<bool> IsQuarantinedAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string documentNumber, string sha256, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT 1 FROM plm.file_quarantine q JOIN plm.document_master m ON m.id = q.master_id
            WHERE m.number = @n AND q.sha256 = @s AND q.released_at IS NULL
            """, conn, tx);
        cmd.Parameters.AddWithValue("n", documentNumber);
        cmd.Parameters.AddWithValue("s", sha256);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    // ---------------------------------------------------------------- findings

    internal async Task<IReadOnlyList<PlanFinding>> FindingsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Subject? subject, PlanHead head, MbomForProcess? mbom, PlanContent content, IReadOnlyList<MbomLineRef> lines, bool linesKnown, CancellationToken ct)
    {
        var findings = new List<PlanFinding>();
        var workCenters = await ProcessStore.ActiveWorkCentersAsync(conn, tx, head.Plant, ct);
        findings.AddRange(ProcessRules.Validate(content, workCenters).Select(e => new PlanFinding(Severities.Error, "structure", e)));
        if (linesKnown) findings.AddRange(ProcessRules.Coverage(content, lines));
        else findings.Add(new PlanFinding(Severities.Warning, "lines_unavailable", "The lines of the bound MBOM revision could not be read, so component assignments were not checked."));
        if (mbom is not null)
        {
            var stale = ProcessRules.StalenessOf(head.BoundRevision, head.BoundSha, mbom.LatestReleased);
            if (stale == Staleness.Stale)
                findings.Add(new PlanFinding(Severities.Warning, "stale_mbom", $"The plan is written against MBOM revision {head.BoundRevision}; revision {mbom.LatestReleased!.Revision} is now released. Review the plan and rebind it."));
            else if (stale == Staleness.NoReleasedMbom)
                findings.Add(new PlanFinding(Severities.Warning, "no_released_mbom", "The MBOM has no released revision."));
        }
        // Tools: counts only, so a tool the reader may not see is not named.
        var toolIds = content.Routings.SelectMany(r => r.Operations).SelectMany(o => o.Tools.Concat(o.Steps.SelectMany(s => s.Tools))).Distinct().ToArray();
        if (toolIds.Length > 0)
        {
            var today = ProcessStore.Today(clock);
            int retired = 0, overdue = 0, missing = 0;
            foreach (var id in toolIds)
            {
                var t = await ProcessStore.LoadToolByIdAsync(conn, tx, id, false, ct);
                if (t is null) { missing++; continue; }
                if (t.Status == "retired") retired++;
                else if (t.RequiresCalibration && ProcessRules.CalibrationState(true, t.DueOn, today) is "overdue" or "never") overdue++;
            }
            if (missing > 0) findings.Add(new PlanFinding(Severities.Error, "tool.missing", $"{missing} referenced tool(s) no longer exist."));
            if (retired > 0) findings.Add(new PlanFinding(Severities.Error, "tool.retired", $"{retired} referenced tool(s) are retired."));
            if (overdue > 0) findings.Add(new PlanFinding(Severities.Error, "tool.calibration", $"{overdue} referenced tool(s) are out of calibration."));
        }
        // Attachments: quarantine is a system fact (the scanner flagged the file after it was stored).
        var quarantined = 0;
        foreach (var a in content.Routings.SelectMany(r => r.Operations).SelectMany(o => o.Steps).SelectMany(s => s.Attachments).DistinctBy(a => (a.Document, a.FileId)))
        {
            var sha = await AttachmentShaAsync(conn, tx, a.Document, a.FileId, ct);
            if (sha is null) findings.Add(new PlanFinding(Severities.Error, "attachment.missing", "A referenced attachment no longer exists."));
            else if (await IsQuarantinedAsync(conn, tx, a.Document, sha, ct)) quarantined++;
        }
        if (quarantined > 0) findings.Add(new PlanFinding(Severities.Error, "attachment.quarantined", $"{quarantined} attachment(s) are quarantined by the malware scanner."));
        _ = subject;
        return findings;
    }

    internal static async Task<string?> AttachmentShaAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string documentNumber, Guid fileId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT f.sha256 FROM plm.document_file f JOIN plm.document_iteration i ON i.id = f.iteration_id JOIN plm.document_revision r ON r.id = i.revision_id JOIN plm.document_master m ON m.id = r.master_id
            WHERE m.number = @n AND f.id = @f
            """, conn, tx);
        cmd.Parameters.AddWithValue("n", documentNumber);
        cmd.Parameters.AddWithValue("f", fileId);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }
}
