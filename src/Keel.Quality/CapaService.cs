using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Quality;

/// <summary>
/// Corrective and preventive actions (ADR-0036): the problem, a root cause analysis (a chain of whys and cause categories), action items with owners and due dates,
/// an effectiveness verification, and a closure that exists only as the outcome of a signed review. Audit events carry numbers, states and counts, never analysis text.
/// </summary>
public sealed class CapaService
{
    private const string Kind = QualityKinds.Capa;
    private readonly QualityRunner _run;
    private readonly QualityLookups _lookups;
    private readonly INotificationSink? _notifications;

    internal CapaService(QualityRunner run, QualityLookups lookups, INotificationSink? notifications)
    {
        _run = run;
        _lookups = lookups;
        _notifications = notifications;
    }

    private static List<string> Validate(string? title, string? description, string capaType, string source, string priority)
    {
        var errors = new List<string>();
        var t = title?.Trim() ?? "";
        if (t.Length is 0 or > 200) errors.Add("Title is required (at most 200 characters).");
        if ((description?.Length ?? 0) > 8000) errors.Add("The problem statement must be at most 8000 characters.");
        if (!CapaOptions.Types.Contains(capaType)) errors.Add($"Type must be one of: {string.Join(", ", CapaOptions.Types)}.");
        if (!CapaOptions.Sources.Contains(source)) errors.Add($"Source must be one of: {string.Join(", ", CapaOptions.Sources)}.");
        if (!CapaOptions.Priorities.Contains(priority)) errors.Add($"Priority must be one of: {string.Join(", ", CapaOptions.Priorities)}.");
        return errors;
    }

    // ---------------------------------------------------------------- create

    public async Task<Outcome<CapaDetail>> CreateAsync(Subject subject, CreateCapaCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        var errors = Validate(cmd.Title, cmd.Description, cmd.CapaType, cmd.Source, cmd.Priority);
        if (errors.Count > 0) return Outcome<CapaDetail>.Invalid(errors);
        await using var conn = await _run.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var decision = AccessPolicy.Evaluate(subject, cmd.Markings, PlmAction.Modify);
        if (!decision.Permitted)
        {
            await _run.Audit.AppendAsync(tx, QualityStore.Entry(subject, "capa.create", AuditOutcome.Denied, Kind, null, correlationId, ("reasons", string.Join(",", decision.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<CapaDetail>.Forbidden(decision.Reasons);
        }
        var owner = string.IsNullOrWhiteSpace(cmd.Owner) ? null : await QualityStore.FindUserAsync(conn, tx, cmd.Owner.Trim(), ct);
        var (ownerSub, ownerUser, ownerName) = owner is null ? (subject.Id, QualityStore.Who(subject), subject.DisplayName) : (owner.Sub, owner.Username, owner.DisplayName);
        if (!string.IsNullOrWhiteSpace(cmd.Owner) && owner is null) return Outcome<CapaDetail>.Invalid(["Choose an owner who has signed in before."]);
        if (owner is not null && !AccessPolicy.Evaluate(owner.Subject, cmd.Markings, PlmAction.Read).Permitted)
            return Outcome<CapaDetail>.Invalid([$"{owner.Username} cannot read a record with these markings and cannot own it."]);
        if (cmd.TargetDate is { } td && td < QualityShared.Today(_run.Clock)) return Outcome<CapaDetail>.Invalid(["The target date cannot be in the past."]);

        var lifecycleId = await Lifecycle.CurrentIdAsync(conn, tx, QualityKinds.Family(Kind), ct);
        var lifecycle = (await Lifecycle.LoadAsync(conn, tx, lifecycleId, ct))!;
        var number = await QualityStore.NextNumberAsync(conn, tx, Kind, ct);
        var id = Guid.CreateVersion7();
        var now = _run.Clock.GetUtcNow();
        await using (var ins = QualityStore.Cmd(conn, tx,
            $"""
            INSERT INTO qual.capa (id, number, title, description, capa_type, source, priority, owner_sub, owner_username, owner_name, target_date, lifecycle_id, lifecycle_state,
                                   requester_sub, requester_username, requester_name, {MarkingColumns.InsertColumns}, created_at, created_by, state_changed_at, state_changed_by)
            VALUES (@id, @num, @title, @desc, @type, @src, @prio, @osub, @ouser, @oname, @target, @lc, @state, @sub, @user, @name, {MarkingColumns.InsertValues}, @at, @by, @at, @by)
            """))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("num", number);
            ins.Parameters.AddWithValue("title", cmd.Title.Trim());
            ins.Parameters.AddWithValue("desc", cmd.Description?.Trim() ?? "");
            ins.Parameters.AddWithValue("type", cmd.CapaType);
            ins.Parameters.AddWithValue("src", cmd.Source);
            ins.Parameters.AddWithValue("prio", cmd.Priority);
            ins.Parameters.AddWithValue("osub", ownerSub);
            ins.Parameters.AddWithValue("ouser", ownerUser);
            ins.Parameters.AddWithValue("oname", ownerName);
            ins.Parameters.AddWithValue("target", cmd.TargetDate is { } d ? d.ToDateTime(TimeOnly.MinValue) : DBNull.Value);
            ins.Parameters.AddWithValue("lc", lifecycleId);
            ins.Parameters.AddWithValue("state", lifecycle.Initial.State);
            ins.Parameters.AddWithValue("sub", subject.Id);
            ins.Parameters.AddWithValue("user", QualityStore.Who(subject));
            ins.Parameters.AddWithValue("name", subject.DisplayName);
            foreach (var (k, v) in MarkingColumns.Parameters(cmd.Markings)) ins.Parameters.AddWithValue(k, v);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", QualityStore.Who(subject));
            await ins.ExecuteNonQueryAsync(ct);
        }
        await QualityStore.AppendHistoryAsync(conn, tx, Kind, id, subject, "Created", $"{cmd.CapaType} action, owner {ownerUser}.", now, ct);
        await QualityAwareness.WatchAsync(_notifications, conn, tx, subject.Id, Kind, number, ct);
        if (ownerSub != subject.Id) await QualityAwareness.WatchAsync(_notifications, conn, tx, ownerSub, Kind, number, ct);
        await _run.Audit.AppendAsync(tx, QualityStore.Entry(subject, "capa.create", AuditOutcome.Success, Kind, number, correlationId,
            ("type", cmd.CapaType), ("source", cmd.Source), ("owner", ownerUser), ("markings", cmd.Markings.BannerText())), ct);
        var detail = await LoadDetailAsync(conn, tx, subject, id, [], ct);
        await tx.CommitAsync(ct);
        return Outcome<CapaDetail>.Ok(detail!);
    }

    public Task<Outcome<CapaDetail>> GetAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        _run.ReadAsync(subject, Kind, number, "capa.read", correlationId, (conn, tx, id, _) => LoadDetailAsync(conn, tx, subject, id, [], ct),
            d => [("actions", QualityRunner.Int(d.Actions.Count)), ("links", QualityRunner.Int(d.Links.Count))], ct);

    // ---------------------------------------------------------------- edit

    public Task<Outcome<CapaDetail>> UpdateAsync(Subject subject, string number, UpdateCapaCommand cmd, string? correlationId, CancellationToken ct = default) =>
        Run(subject, number, "capa.update", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (!c.Editable) return Frozen(c);
            var errors = Validate(cmd.Title, cmd.Description, cmd.CapaType, cmd.Source, cmd.Priority);
            var owner = await QualityStore.FindUserAsync(conn, tx, cmd.Owner?.Trim() ?? "", ct);
            if (owner is null) errors.Add("Choose an owner who has signed in before.");
            else if (!AccessPolicy.Evaluate(owner.Subject, c.Head.Markings, PlmAction.Read).Permitted) errors.Add($"{owner.Username} cannot read this record and cannot own it.");
            if (errors.Count > 0) return QStep.Invalid([.. errors]);
            await using var up = QualityStore.Cmd(conn, tx,
                "UPDATE qual.capa SET title = @t, description = @d, capa_type = @ty, source = @s, priority = @p, owner_sub = @os, owner_username = @ou, owner_name = @on, target_date = @td WHERE id = @id");
            up.Parameters.AddWithValue("t", cmd.Title.Trim());
            up.Parameters.AddWithValue("d", cmd.Description?.Trim() ?? "");
            up.Parameters.AddWithValue("ty", cmd.CapaType);
            up.Parameters.AddWithValue("s", cmd.Source);
            up.Parameters.AddWithValue("p", cmd.Priority);
            up.Parameters.AddWithValue("os", owner!.Sub);
            up.Parameters.AddWithValue("ou", owner.Username);
            up.Parameters.AddWithValue("on", owner.DisplayName);
            up.Parameters.AddWithValue("td", cmd.TargetDate is { } d ? d.ToDateTime(TimeOnly.MinValue) : DBNull.Value);
            up.Parameters.AddWithValue("id", c.Head.Id);
            await up.ExecuteNonQueryAsync(ct);
            await QualityStore.AppendHistoryAsync(conn, tx, Kind, c.Head.Id, subject, "Edited", null, c.Now, ct);
            await QualityAwareness.WatchAsync(_notifications, conn, tx, owner.Sub, Kind, c.Head.Number, ct);
            return QStep.Done(("priority", cmd.Priority), ("owner", owner.Username));
        }, ct);

    public Task<Outcome<CapaDetail>> ChangeMarkingsAsync(Subject subject, string number, Markings markings, string? reason, string? correlationId, CancellationToken ct = default) =>
        Run(subject, number, "capa.markings_change", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) return QStep.Invalid("A reason is required to change markings.");
            if (!c.Editable) return Frozen(c);
            if (markings == c.Head.Markings) return QStep.Fail("The markings are unchanged.");
            var after = AccessPolicy.Evaluate(subject, markings, PlmAction.Modify);
            if (!after.Permitted) return QStep.Forbid([.. after.Reasons.Select(r => "new_markings:" + r)]);
            // The owner and every open action's owner must still read the record.
            var people = new List<string> { c.Head.RaiserSub };
            await using (var q = QualityStore.Cmd(conn, tx, "SELECT owner_sub FROM qual.capa WHERE id = @id UNION SELECT owner_sub FROM qual.action WHERE parent_id = @id AND status IN ('Open', 'InProgress')"))
            {
                q.Parameters.AddWithValue("id", c.Head.Id);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) people.Add(r.GetString(0));
            }
            foreach (var sub in people.Distinct())
                if (await QualityStore.FindUserAsync(conn, tx, sub, ct) is { } u && !AccessPolicy.Evaluate(u.Subject, markings, PlmAction.Read).Permitted)
                    return QStep.Invalid($"{u.Username} owns this record or one of its open actions and could no longer read it under the new markings.");
            await QualityStore.SetMarkingsAsync(conn, tx, Kind, c.Head.Id, markings);
            await QualityStore.AppendHistoryAsync(conn, tx, Kind, c.Head.Id, subject, "Markings changed", $"{c.Head.Markings.BannerText()} to {markings.BannerText()}. Reason: {reason.Trim()}", c.Now, ct);
            return QStep.Done(("before", c.Head.Markings.BannerText()), ("after", markings.BannerText()), ("reason", reason.Trim()));
        }, ct);

    /// <summary>Replaces the root cause analysis: the root cause statement, the chain of whys (1 to 8) and the cause categories.</summary>
    public Task<Outcome<CapaDetail>> SaveAnalysisAsync(Subject subject, string number, SaveAnalysisCommand cmd, string? correlationId, CancellationToken ct = default) =>
        Run(subject, number, "capa.analysis_save", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (!c.Editable) return Frozen(c);
            var errors = new List<string>();
            var root = cmd.RootCause?.Trim() ?? "";
            if (root.Length > 4000) errors.Add("The root cause must be at most 4000 characters.");
            var whys = (cmd.Whys ?? []).Where(w => !string.IsNullOrWhiteSpace(w.Why)).ToList();
            if (whys.Count > CapaOptions.MaxWhys) errors.Add($"At most {CapaOptions.MaxWhys} whys.");
            if (whys.Any(w => w.Why.Trim().Length > 1000 || (w.Evidence?.Length ?? 0) > 1000)) errors.Add("Each why and its evidence must be at most 1000 characters.");
            var causes = (cmd.Causes ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Description)).ToList();
            if (causes.Count > 30) errors.Add("At most 30 causes.");
            if (causes.Any(x => !CapaOptions.Categories.Contains(x.Category))) errors.Add($"A cause category must be one of: {string.Join(", ", CapaOptions.Categories)}.");
            if (causes.Any(x => x.Description.Trim().Length > 1000)) errors.Add("Each cause must be at most 1000 characters.");
            if (errors.Count > 0) return QStep.Invalid([.. errors]);

            await using (var del = QualityStore.Cmd(conn, tx, "DELETE FROM qual.rca_why WHERE capa_id = @id; DELETE FROM qual.rca_cause WHERE capa_id = @id;"))
            {
                del.Parameters.AddWithValue("id", c.Head.Id);
                await del.ExecuteNonQueryAsync(ct);
            }
            var seq = 1;
            foreach (var w in whys)
            {
                await using var ins = QualityStore.Cmd(conn, tx, "INSERT INTO qual.rca_why (id, capa_id, seq, why_text, evidence) VALUES (@id, @c, @s, @w, @e)");
                ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
                ins.Parameters.AddWithValue("c", c.Head.Id);
                ins.Parameters.AddWithValue("s", seq++);
                ins.Parameters.AddWithValue("w", w.Why.Trim());
                ins.Parameters.AddWithValue("e", w.Evidence?.Trim() ?? "");
                await ins.ExecuteNonQueryAsync(ct);
            }
            foreach (var x in causes)
            {
                await using var ins = QualityStore.Cmd(conn, tx, "INSERT INTO qual.rca_cause (id, capa_id, category, description, is_root) VALUES (@id, @c, @k, @d, @r)");
                ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
                ins.Parameters.AddWithValue("c", c.Head.Id);
                ins.Parameters.AddWithValue("k", x.Category);
                ins.Parameters.AddWithValue("d", x.Description.Trim());
                ins.Parameters.AddWithValue("r", x.IsRoot);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await using (var up = QualityStore.Cmd(conn, tx, "UPDATE qual.capa SET root_cause = @r WHERE id = @id"))
            {
                up.Parameters.AddWithValue("r", root);
                up.Parameters.AddWithValue("id", c.Head.Id);
                await up.ExecuteNonQueryAsync(ct);
            }
            await QualityStore.AppendHistoryAsync(conn, tx, Kind, c.Head.Id, subject, "Analysis saved", $"{whys.Count} why(s), {causes.Count} cause(s).", c.Now, ct);
            return QStep.Done(("whys", QualityRunner.Int(whys.Count)), ("causes", QualityRunner.Int(causes.Count)));
        }, ct);

    /// <summary>States how effectiveness will be checked (method, criteria, due date). It must be set before the verification result is recorded.</summary>
    public Task<Outcome<CapaDetail>> SaveVerificationPlanAsync(Subject subject, string number, SaveVerificationPlanCommand cmd, string? correlationId, CancellationToken ct = default) =>
        Run(subject, number, "capa.verification_plan", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (!c.Editable) return Frozen(c);
            var method = cmd.Method?.Trim() ?? "";
            var criteria = cmd.Criteria?.Trim() ?? "";
            if (method.Length is 0 or > 2000 || criteria.Length is 0 or > 2000) return QStep.Invalid("State the verification method and the success criteria (each at most 2000 characters).");
            await using var up = QualityStore.Cmd(conn, tx, "UPDATE qual.capa SET verification_method = @m, verification_criteria = @c, verification_due = @d, verification_effective = NULL, verification_result = '' WHERE id = @id");
            up.Parameters.AddWithValue("m", method);
            up.Parameters.AddWithValue("c", criteria);
            up.Parameters.AddWithValue("d", cmd.DueDate is { } d ? d.ToDateTime(TimeOnly.MinValue) : DBNull.Value);
            up.Parameters.AddWithValue("id", c.Head.Id);
            await up.ExecuteNonQueryAsync(ct);
            await QualityStore.AppendHistoryAsync(conn, tx, Kind, c.Head.Id, subject, "Verification plan saved", null, c.Now, ct);
            return QStep.Done(("due", QualityStore.DateText(cmd.DueDate)));
        }, ct);

    /// <summary>Records the result of the effectiveness check once every action is finished. An ineffective result keeps the record open for more actions.</summary>
    public Task<Outcome<CapaDetail>> RecordVerificationAsync(Subject subject, string number, RecordVerificationCommand cmd, string? correlationId, CancellationToken ct = default) =>
        Run(subject, number, "capa.verification_record", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (c.State != "InProgress") return QStep.Fail($"{c.Head.Number} is {c.StateLabel}; the result is recorded while the actions are in progress.");
            var result = cmd.Result?.Trim() ?? "";
            if (result.Length is 0 or > 4000) return QStep.Invalid("Describe what was found (required, at most 4000 characters).");
            var problem = await ReadinessAsync(conn, tx, c.Head.Id, requireResult: false, ct);
            if (problem is not null) return QStep.Fail(problem);
            await using var up = QualityStore.Cmd(conn, tx, "UPDATE qual.capa SET verification_result = @r, verification_effective = @e, verification_by = @by, verification_at = @at WHERE id = @id");
            up.Parameters.AddWithValue("r", result);
            up.Parameters.AddWithValue("e", cmd.Effective);
            up.Parameters.AddWithValue("by", QualityStore.Who(subject));
            up.Parameters.AddWithValue("at", c.Now);
            up.Parameters.AddWithValue("id", c.Head.Id);
            await up.ExecuteNonQueryAsync(ct);
            await QualityStore.AppendHistoryAsync(conn, tx, Kind, c.Head.Id, subject, "Verification recorded", cmd.Effective ? "Effective." : "Not effective.", c.Now, ct);
            return QStep.Done(("effective", cmd.Effective ? "true" : "false"));
        }, ct);

    /// <summary>Null when the record can go to the signed verification review; otherwise what is missing.</summary>
    internal static async Task<string?> ReadinessAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, bool requireResult, CancellationToken ct)
    {
        await using var q = QualityStore.Cmd(conn, tx,
            """
            SELECT c.root_cause, c.verification_method, c.verification_criteria, c.verification_result, c.verification_effective,
                   (SELECT count(*) FROM qual.rca_why w WHERE w.capa_id = c.id) + (SELECT count(*) FROM qual.rca_cause x WHERE x.capa_id = c.id),
                   (SELECT count(*) FROM qual.action a WHERE a.parent_kind = 'capa' AND a.parent_id = c.id AND a.status IN ('Open', 'InProgress')),
                   (SELECT count(*) FROM qual.action a WHERE a.parent_kind = 'capa' AND a.parent_id = c.id AND a.status = 'Done')
            FROM qual.capa c WHERE c.id = @id
            """);
        q.Parameters.AddWithValue("id", id);
        await using var r = await q.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var (root, method, criteria, result, effective, analysis, open, done) = (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? (bool?)null : r.GetBoolean(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7));
        if (string.IsNullOrWhiteSpace(root) || analysis == 0) return "Record the root cause analysis first (the root cause and at least one why or cause).";
        if (done == 0) return "Complete at least one corrective or preventive action first.";
        if (open > 0) return $"{open} action(s) are still open.";
        if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(criteria)) return "State how effectiveness will be verified (method and criteria) first.";
        if (requireResult && (effective != true || string.IsNullOrWhiteSpace(result))) return "Record an effective verification result first.";
        return null;
    }

    // ---------------------------------------------------------------- manual steps

    /// <summary>Starts the work: the record needs at least one action item.</summary>
    public Task<Outcome<CapaDetail>> StartAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        Run(subject, number, "capa.start", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            await using (var q = QualityStore.Cmd(conn, tx, "SELECT count(*) FROM qual.action WHERE parent_kind = 'capa' AND parent_id = @id AND status <> 'Cancelled'"))
            {
                q.Parameters.AddWithValue("id", c.Head.Id);
                if (Convert.ToInt32(await q.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 0) return QStep.Fail("Add at least one action item before starting the work.");
            }
            var moved = await QualityStore.TransitionAsync(conn, tx, c.Head, "InProgress", "manual", QualityStore.Who(subject), c.Now);
            if (!moved.IsOk) return QStep.Fail(moved.Error!);
            await QualityStore.AppendHistoryAsync(conn, tx, Kind, c.Head.Id, subject, "Started", null, c.Now, ct);
            return QStep.Done(("from", c.State), ("to", "InProgress"));
        }, ct);

    public Task<Outcome<CapaDetail>> CancelAsync(Subject subject, string number, string reason, string? correlationId, CancellationToken ct = default) =>
        Run(subject, number, "capa.cancel", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            var text = reason?.Trim() ?? "";
            if (text.Length is 0 or > 1000) return QStep.Invalid("Give a reason for cancelling (required, at most 1000 characters).");
            var moved = await QualityStore.TransitionAsync(conn, tx, c.Head, "Cancelled", "manual", QualityStore.Who(subject), c.Now, "cancel_reason = @r", ("r", text));
            if (!moved.IsOk) return QStep.Fail(moved.Error!);
            // Open actions end with the record.
            await using (var up = QualityStore.Cmd(conn, tx, "UPDATE qual.action SET status = 'Cancelled', cancel_reason = 'The corrective action was cancelled.', completed_at = @at, completed_by = @by WHERE parent_kind = 'capa' AND parent_id = @id AND status IN ('Open', 'InProgress')"))
            {
                up.Parameters.AddWithValue("at", c.Now);
                up.Parameters.AddWithValue("by", QualityStore.Who(subject));
                up.Parameters.AddWithValue("id", c.Head.Id);
                await up.ExecuteNonQueryAsync(ct);
            }
            await QualityStore.AppendHistoryAsync(conn, tx, Kind, c.Head.Id, subject, "Cancelled", text, c.Now, ct);
            return QStep.Done(("from", c.State), ("to", "Cancelled"));
        }, ct);

    private static QStep Frozen(QCtx c) => QStep.Fail($"{c.Head.Number} is {c.StateLabel} and can no longer be edited.");

    private Task<Outcome<CapaDetail>> Run(Subject subject, string number, string action, string? corr, PlmAction permission, Func<QCtx, NpgsqlConnection, NpgsqlTransaction, Task<QStep>> body, CancellationToken ct) =>
        _run.MutateAsync(subject, Kind, number, action, corr, permission, body, (conn, tx, id, notices) => LoadDetailAsync(conn, tx, subject, id, notices, ct), ct);

    // ---------------------------------------------------------------- detail

    internal async Task<CapaDetail?> LoadDetailAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, Guid id, IReadOnlyList<string> notices, CancellationToken ct)
    {
        await using var cmd = QualityStore.Cmd(conn, tx,
            $"""
            SELECT n.number, n.title, n.description, n.capa_type, n.source, n.priority, n.owner_name, n.owner_username, n.target_date, n.root_cause, n.verification_method,
                   n.verification_criteria, n.verification_due, n.verification_result, n.verification_effective, n.verification_by, n.verification_at, n.cancel_reason,
                   n.lifecycle_id, n.lifecycle_state, n.submission_no, n.requester_name, n.created_at, n.state_changed_at, {MarkingColumns.Select("n")}
            FROM qual.capa n WHERE n.id = @id
            """);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var number = r.GetString(0);
        var markings = MarkingColumns.Read(r, 24);
        var state = r.GetString(19);
        var lifecycleId = r.GetString(18);
        var f = (Title: r.GetString(1), Desc: r.GetString(2), Type: r.GetString(3), Source: r.GetString(4), Priority: r.GetString(5), OwnerName: r.GetString(6), OwnerUser: r.GetString(7),
            Target: QualityShared.DateAt(r, 8), Root: r.GetString(9), Method: r.GetString(10), Criteria: r.GetString(11), VDue: QualityShared.DateAt(r, 12), VResult: r.GetString(13),
            VEffective: r.IsDBNull(14) ? (bool?)null : r.GetBoolean(14), VBy: r.IsDBNull(15) ? null : r.GetString(15), VAt: r.IsDBNull(16) ? (DateTimeOffset?)null : QualityStore.Utc(r, 16),
            Cancel: r.IsDBNull(17) ? null : r.GetString(17), Submission: r.GetInt32(20), Requester: r.GetString(21), Created: QualityStore.Utc(r, 22), Changed: QualityStore.Utc(r, 23));
        await r.CloseAsync();

        var lifecycle = (await Lifecycle.LoadAsync(conn, tx, lifecycleId, ct))!;
        var modify = AccessPolicy.Evaluate(subject, markings, PlmAction.Modify);
        var admin = AccessPolicy.Evaluate(subject, markings, PlmAction.Administer);
        var final = QualityShared.IsFinal(lifecycle, state);
        var today = QualityShared.Today(_run.Clock);
        var shown = new List<Markings> { markings };
        var actions = await QualityShared.LoadActionsAsync(conn, tx, subject, Kind, id, final, admin.Permitted, today, ct);
        var links = await QualityShared.LoadLinksAsync(conn, tx, subject, _lookups, Kind, id, number, modify.Permitted && !final, shown, ct);
        var history = await QualityShared.LoadHistoryAsync(conn, tx, Kind, id, ct);
        var whys = new List<WhyView>();
        await using (var q = QualityStore.Cmd(conn, tx, "SELECT seq, why_text, evidence FROM qual.rca_why WHERE capa_id = @id ORDER BY seq"))
        {
            q.Parameters.AddWithValue("id", id);
            await using var rr = await q.ExecuteReaderAsync(ct);
            while (await rr.ReadAsync(ct)) whys.Add(new WhyView(rr.GetInt32(0), rr.GetString(1), rr.GetString(2)));
        }
        var causes = new List<CauseView>();
        await using (var q = QualityStore.Cmd(conn, tx, "SELECT id, category, description, is_root FROM qual.rca_cause WHERE capa_id = @id ORDER BY category, id"))
        {
            q.Parameters.AddWithValue("id", id);
            await using var rr = await q.ExecuteReaderAsync(ct);
            while (await rr.ReadAsync(ct)) causes.Add(new CauseView(rr.GetGuid(0), rr.GetString(1), rr.GetString(2), rr.GetBoolean(3)));
        }
        var open = actions.Count(a => a.Status is "Open" or "InProgress");
        var transitions = QualityShared.Transitions(lifecycle, state, modify.Permitted,
            to => to == "InProgress" && actions.All(a => a.Status == "Cancelled") ? "Add at least one action item first." : null, to => to == "Cancelled");
        _ = open;
        return new CapaDetail(id, number, f.Title, f.Desc, f.Type, f.Source, f.Priority, f.OwnerName, f.OwnerUser, f.Target, !final && f.Target is { } tt && tt < today, f.Root, whys, causes,
            f.Method, f.Criteria, f.VDue, f.VResult, f.VEffective, f.VBy, f.VAt, f.Cancel, state, lifecycle.State(state)?.Label ?? state, lifecycle.IsLocked(state), final, f.Submission, markings,
            Markings.AggregateBanner(shown), f.Requester, f.Created, f.Changed, actions, links, history, transitions, new QualityPermissions(modify.Permitted, !lifecycle.IsLocked(state), modify.Reasons), notices, shown);
    }
}
