using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Quality;

/// <summary>
/// Deviations and waivers (ADR-0036): a permission to depart from a requirement for named parts (optionally revisions, serials, lots and a quantity) inside a time window.
/// Approval is a signed review through the workflow engine; a customer approval can be required in addition. An approved record whose window ends expires by itself
/// (see <see cref="QualityMaintenance"/>). A part's page shows the records that apply to it. Audit events carry numbers, states and counts, never the justification.
/// </summary>
public sealed class DeviationService
{
    private const string Kind = QualityKinds.Deviation;
    public const int MaxScopeRows = 50;
    private readonly QualityRunner _run;
    private readonly QualityLookups _lookups;
    private readonly INotificationSink? _notifications;

    internal DeviationService(QualityRunner run, QualityLookups lookups, INotificationSink? notifications)
    {
        _run = run;
        _lookups = lookups;
        _notifications = notifications;
    }

    private List<string> Validate(string kind, string? title, string? description, string? requirement, DateOnly from, DateOnly to, bool checkFuture)
    {
        var errors = new List<string>();
        if (!DeviationKinds.All.Contains(kind)) errors.Add($"Kind must be one of: {string.Join(", ", DeviationKinds.All)}.");
        if ((title?.Trim().Length ?? 0) is 0 or > 200) errors.Add("Title is required (at most 200 characters).");
        if ((description?.Length ?? 0) > 8000) errors.Add("The justification must be at most 8000 characters.");
        if ((requirement?.Length ?? 0) > 500) errors.Add("The requirement reference must be at most 500 characters.");
        if (from > to) errors.Add("The window must end on or after its start.");
        if (checkFuture && to < QualityShared.Today(_run.Clock)) errors.Add("The window must not end in the past.");
        return errors;
    }

    // ---------------------------------------------------------------- create

    public async Task<Outcome<DeviationDetail>> CreateAsync(Subject subject, CreateDeviationCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        var errors = Validate(cmd.Kind, cmd.Title, cmd.Description, cmd.RequirementRef, cmd.ValidFrom, cmd.ValidTo, checkFuture: true);
        if (errors.Count > 0) return Outcome<DeviationDetail>.Invalid(errors);
        await using var conn = await _run.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var decision = AccessPolicy.Evaluate(subject, cmd.Markings, PlmAction.Modify);
        if (!decision.Permitted)
        {
            await _run.Audit.AppendAsync(tx, QualityStore.Entry(subject, "deviation.create", AuditOutcome.Denied, Kind, null, correlationId, ("reasons", string.Join(",", decision.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<DeviationDetail>.Forbidden(decision.Reasons);
        }
        var lifecycleId = await Lifecycle.CurrentIdAsync(conn, tx, QualityKinds.Family(Kind), ct);
        var lifecycle = (await Lifecycle.LoadAsync(conn, tx, lifecycleId, ct))!;
        var number = await QualityStore.NextNumberAsync(conn, tx, Kind, ct);
        var id = Guid.CreateVersion7();
        var now = _run.Clock.GetUtcNow();
        await using (var ins = QualityStore.Cmd(conn, tx,
            $"""
            INSERT INTO qual.deviation (id, number, dev_kind, title, description, requirement_ref, customer_approval_required, valid_from, valid_to, lifecycle_id, lifecycle_state,
                                        requester_sub, requester_username, requester_name, {MarkingColumns.InsertColumns}, created_at, created_by, state_changed_at, state_changed_by)
            VALUES (@id, @num, @kind, @title, @desc, @req, @cust, @from, @to, @lc, @state, @sub, @user, @name, {MarkingColumns.InsertValues}, @at, @by, @at, @by)
            """))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("num", number);
            ins.Parameters.AddWithValue("kind", cmd.Kind);
            ins.Parameters.AddWithValue("title", cmd.Title.Trim());
            ins.Parameters.AddWithValue("desc", cmd.Description?.Trim() ?? "");
            ins.Parameters.AddWithValue("req", cmd.RequirementRef?.Trim() ?? "");
            ins.Parameters.AddWithValue("cust", cmd.CustomerApprovalRequired);
            ins.Parameters.AddWithValue("from", cmd.ValidFrom.ToDateTime(TimeOnly.MinValue));
            ins.Parameters.AddWithValue("to", cmd.ValidTo.ToDateTime(TimeOnly.MinValue));
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
        await QualityStore.AppendHistoryAsync(conn, tx, Kind, id, subject, "Created", $"{cmd.Kind} for {QualityStore.DateText(cmd.ValidFrom)} to {QualityStore.DateText(cmd.ValidTo)}.", now, ct);
        await QualityAwareness.WatchAsync(_notifications, conn, tx, subject.Id, Kind, number, ct);
        await _run.Audit.AppendAsync(tx, QualityStore.Entry(subject, "deviation.create", AuditOutcome.Success, Kind, number, correlationId,
            ("kind", cmd.Kind), ("from", QualityStore.DateText(cmd.ValidFrom)), ("to", QualityStore.DateText(cmd.ValidTo)), ("customerApproval", cmd.CustomerApprovalRequired ? "required" : "not required"), ("markings", cmd.Markings.BannerText())), ct);
        var detail = await LoadDetailAsync(conn, tx, subject, id, [], ct);
        await tx.CommitAsync(ct);
        return Outcome<DeviationDetail>.Ok(detail!);
    }

    public Task<Outcome<DeviationDetail>> GetAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        _run.ReadAsync(subject, Kind, number, "deviation.read", correlationId, (conn, tx, id, _) => LoadDetailAsync(conn, tx, subject, id, [], ct),
            d => [("scope", QualityRunner.Int(d.Scope.Count)), ("links", QualityRunner.Int(d.Links.Count))], ct);

    // ---------------------------------------------------------------- edit

    public Task<Outcome<DeviationDetail>> UpdateAsync(Subject subject, string number, UpdateDeviationCommand cmd, string? correlationId, CancellationToken ct = default) =>
        Run(subject, number, "deviation.update", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (!c.Editable) return Frozen(c);
            var errors = Validate(cmd.Kind, cmd.Title, cmd.Description, cmd.RequirementRef, cmd.ValidFrom, cmd.ValidTo, checkFuture: true);
            if (errors.Count > 0) return QStep.Invalid([.. errors]);
            await using var up = QualityStore.Cmd(conn, tx,
                "UPDATE qual.deviation SET dev_kind = @k, title = @t, description = @d, requirement_ref = @r, customer_approval_required = @c, valid_from = @f, valid_to = @to WHERE id = @id");
            up.Parameters.AddWithValue("k", cmd.Kind);
            up.Parameters.AddWithValue("t", cmd.Title.Trim());
            up.Parameters.AddWithValue("d", cmd.Description?.Trim() ?? "");
            up.Parameters.AddWithValue("r", cmd.RequirementRef?.Trim() ?? "");
            up.Parameters.AddWithValue("c", cmd.CustomerApprovalRequired);
            up.Parameters.AddWithValue("f", cmd.ValidFrom.ToDateTime(TimeOnly.MinValue));
            up.Parameters.AddWithValue("to", cmd.ValidTo.ToDateTime(TimeOnly.MinValue));
            up.Parameters.AddWithValue("id", c.Head.Id);
            await up.ExecuteNonQueryAsync(ct);
            await QualityStore.AppendHistoryAsync(conn, tx, Kind, c.Head.Id, subject, "Edited", null, c.Now, ct);
            return QStep.Done(("from", QualityStore.DateText(cmd.ValidFrom)), ("to", QualityStore.DateText(cmd.ValidTo)));
        }, ct);

    public Task<Outcome<DeviationDetail>> ChangeMarkingsAsync(Subject subject, string number, Markings markings, string? reason, string? correlationId, CancellationToken ct = default) =>
        Run(subject, number, "deviation.markings_change", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) return QStep.Invalid("A reason is required to change markings.");
            if (!c.Editable) return Frozen(c);
            if (markings == c.Head.Markings) return QStep.Fail("The markings are unchanged.");
            var after = AccessPolicy.Evaluate(subject, markings, PlmAction.Modify);
            if (!after.Permitted) return QStep.Forbid([.. after.Reasons.Select(r => "new_markings:" + r)]);
            foreach (var partMarkings in await ScopedPartMarkingsAsync(conn, tx, c.Head.Id, ct))
                if (MarkingDominance.Gaps(markings, partMarkings) is { Count: > 0 } gaps)
                    return QStep.Invalid($"The new markings do not cover a part in the scope. {MarkingDominance.Describe(gaps)}");
            await QualityStore.SetMarkingsAsync(conn, tx, Kind, c.Head.Id, markings);
            await QualityStore.AppendHistoryAsync(conn, tx, Kind, c.Head.Id, subject, "Markings changed", $"{c.Head.Markings.BannerText()} to {markings.BannerText()}. Reason: {reason.Trim()}", c.Now, ct);
            return QStep.Done(("before", c.Head.Markings.BannerText()), ("after", markings.BannerText()), ("reason", reason.Trim()));
        }, ct);

    private async Task<List<Markings>> ScopedPartMarkingsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid deviationId, CancellationToken ct)
    {
        var parts = new List<(string Number, string? Revision)>();
        await using (var q = QualityStore.Cmd(conn, tx, "SELECT part_number, part_revision FROM qual.deviation_scope WHERE deviation_id = @id"))
        {
            q.Parameters.AddWithValue("id", deviationId);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) parts.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
        }
        var list = new List<Markings>();
        foreach (var (number, revision) in parts)
            if (await _lookups.FindPartAsync(conn, tx, number, revision, false, ct) is { } p) list.Add(p.Markings);
        return list;
    }

    // ---------------------------------------------------------------- scope

    public Task<Outcome<DeviationDetail>> AddScopeAsync(Subject subject, string number, ScopeInput input, string? correlationId, CancellationToken ct = default) =>
        Run(subject, number, "deviation.scope_add", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (!c.Editable) return Frozen(c);
            var serials = (input.Serials ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            var lots = (input.Lots ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            var errors = new List<string>();
            if (serials.Length > 500) errors.Add("At most 500 serial numbers per part.");
            if (lots.Length > 200) errors.Add("At most 200 lots per part.");
            if (serials.Any(s => s.Length > 100) || lots.Any(s => s.Length > 100)) errors.Add("Serial numbers and lots must be at most 100 characters.");
            if (input.SerialFrom is < 0 || input.SerialTo is < 0) errors.Add("Serial numbers cannot be negative.");
            if (input.SerialFrom is { } sf && input.SerialTo is { } st && sf > st) errors.Add("The serial range must end on or after its start.");
            if (input.MaxQuantity is <= 0) errors.Add("The quantity limit must be more than zero.");
            if (errors.Count > 0) return QStep.Invalid([.. errors]);
            var part = await _lookups.FindPartAsync(conn, tx, input.PartNumber?.Trim() ?? "", string.IsNullOrWhiteSpace(input.Revision) ? null : input.Revision.Trim(), false, ct);
            if (part is null || !AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Read).Permitted) return QStep.Invalid($"Part {input.PartNumber} was not found.");
            var gaps = MarkingDominance.Gaps(c.Head.Markings, part.Markings);
            if (gaps.Count > 0) return QStep.Invalid($"The markings of {c.Head.Number} must cover those of {part.Key}. {MarkingDominance.Describe(gaps)}");
            await using (var cnt = QualityStore.Cmd(conn, tx, "SELECT count(*) FROM qual.deviation_scope WHERE deviation_id = @id"))
            {
                cnt.Parameters.AddWithValue("id", c.Head.Id);
                if (Convert.ToInt32(await cnt.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) >= MaxScopeRows) return QStep.Fail($"A record can name at most {MaxScopeRows} parts.");
            }
            var wholePart = string.IsNullOrWhiteSpace(input.Revision);
            await using (var dup = QualityStore.Cmd(conn, tx, "SELECT 1 FROM qual.deviation_scope WHERE deviation_id = @id AND part_master_id = @pm AND part_revision_id IS NOT DISTINCT FROM @rev"))
            {
                dup.Parameters.AddWithValue("id", c.Head.Id);
                dup.Parameters.AddWithValue("pm", part.MasterId);
                dup.Parameters.AddWithValue("rev", wholePart ? DBNull.Value : part.RevisionId);
                if (await dup.ExecuteScalarAsync(ct) is not null) return QStep.Fail($"{part.Key}{(wholePart ? "" : " revision " + part.Revision)} is already in the scope.");
            }
            await using var ins = QualityStore.Cmd(conn, tx,
                """
                INSERT INTO qual.deviation_scope (id, deviation_id, part_master_id, part_number, part_revision_id, part_revision, serial_from, serial_to, serials, lots, max_quantity)
                VALUES (@id, @d, @pm, @pn, @rev, @revl, @sf, @st, @se, @lo, @q)
                """);
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("d", c.Head.Id);
            ins.Parameters.AddWithValue("pm", part.MasterId);
            ins.Parameters.AddWithValue("pn", part.Key);
            ins.Parameters.AddWithValue("rev", wholePart ? DBNull.Value : part.RevisionId);
            ins.Parameters.AddWithValue("revl", wholePart ? DBNull.Value : part.Revision);
            ins.Parameters.AddWithValue("sf", (object?)input.SerialFrom ?? DBNull.Value);
            ins.Parameters.AddWithValue("st", (object?)input.SerialTo ?? DBNull.Value);
            ins.Parameters.AddWithValue("se", serials);
            ins.Parameters.AddWithValue("lo", lots);
            ins.Parameters.AddWithValue("q", (object?)input.MaxQuantity ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync(ct);
            await QualityStore.AppendHistoryAsync(conn, tx, Kind, c.Head.Id, subject, "Scope added", part.Key + (wholePart ? "" : " revision " + part.Revision), c.Now, ct);
            return QStep.Done(("part", part.Key), ("serials", QualityRunner.Int(serials.Length)), ("lots", QualityRunner.Int(lots.Length)));
        }, ct);

    public Task<Outcome<DeviationDetail>> RemoveScopeAsync(Subject subject, string number, Guid scopeId, string? correlationId, CancellationToken ct = default) =>
        Run(subject, number, "deviation.scope_remove", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (!c.Editable) return Frozen(c);
            await using var del = QualityStore.Cmd(conn, tx, "DELETE FROM qual.deviation_scope WHERE id = @id AND deviation_id = @d RETURNING part_number");
            del.Parameters.AddWithValue("id", scopeId);
            del.Parameters.AddWithValue("d", c.Head.Id);
            var part = (string?)await del.ExecuteScalarAsync(ct);
            if (part is null) return QStep.Invalid("That scope entry does not exist.");
            await QualityStore.AppendHistoryAsync(conn, tx, Kind, c.Head.Id, subject, "Scope removed", part, c.Now, ct);
            return QStep.Done(("part", part));
        }, ct);

    // ---------------------------------------------------------------- steps that are not the review

    public Task<Outcome<DeviationDetail>> WithdrawAsync(Subject subject, string number, string reason, string? correlationId, CancellationToken ct = default) =>
        Close(subject, number, "deviation.withdraw", "Withdrawn", "manual", PlmAction.Modify, reason, correlationId, ct);

    /// <summary>Ends an approved record early (the condition that justified it changed). Needs the approver role.</summary>
    public Task<Outcome<DeviationDetail>> RevokeAsync(Subject subject, string number, string reason, string? correlationId, CancellationToken ct = default) =>
        Close(subject, number, "deviation.revoke", "Revoked", "manual", PlmAction.Sign, reason, correlationId, ct);

    /// <summary>The customer declined (or did not respond in time): the record ends as Rejected.</summary>
    public Task<Outcome<DeviationDetail>> RejectByCustomerAsync(Subject subject, string number, string reason, string? correlationId, CancellationToken ct = default) =>
        Close(subject, number, "deviation.customer_reject", "Rejected", "manual", PlmAction.Sign, reason, correlationId, ct);

    private Task<Outcome<DeviationDetail>> Close(Subject subject, string number, string action, string to, string via, PlmAction permission, string reason, string? corr, CancellationToken ct) =>
        Run(subject, number, action, corr, permission, async (c, conn, tx) =>
        {
            var text = reason?.Trim() ?? "";
            if (text.Length is 0 or > 1000) return QStep.Invalid("Give a reason (required, at most 1000 characters).");
            var moved = await QualityStore.TransitionAsync(conn, tx, c.Head, to, via, QualityStore.Who(subject), c.Now, "close_reason = @r", ("r", text));
            if (!moved.IsOk) return QStep.Fail(moved.Error!);
            await QualityStore.AppendHistoryAsync(conn, tx, Kind, c.Head.Id, subject, to, text, c.Now, ct);
            return QStep.Done(("from", c.State), ("to", to));
        }, ct);

    /// <summary>
    /// Records the customer's approval (their reference and the date). Allowed once the internal review approved and the customer approval is still outstanding; the record
    /// then becomes Approved.
    /// </summary>
    public Task<Outcome<DeviationDetail>> RecordCustomerApprovalAsync(Subject subject, string number, CustomerApprovalCommand cmd, string? correlationId, CancellationToken ct = default) =>
        Run(subject, number, "deviation.customer_approval", correlationId, PlmAction.Sign, async (c, conn, tx) =>
        {
            var reference = cmd.Reference?.Trim() ?? "";
            if (reference.Length is 0 or > 300) return QStep.Invalid("Enter the customer's approval reference (required, at most 300 characters).");
            if (cmd.ApprovedOn > QualityShared.Today(_run.Clock)) return QStep.Invalid("The customer's approval date cannot be in the future.");
            var moved = await QualityStore.TransitionAsync(conn, tx, c.Head, "Approved", "manual", QualityStore.Who(subject), c.Now,
                "customer_approval_ref = @ref, customer_approval_date = @on, customer_approval_by = @by2", ("ref", reference), ("on", cmd.ApprovedOn.ToDateTime(TimeOnly.MinValue)), ("by2", QualityStore.Who(subject)));
            if (!moved.IsOk) return QStep.Fail(moved.Error!);
            await QualityStore.AppendHistoryAsync(conn, tx, Kind, c.Head.Id, subject, "Customer approval recorded", $"Approved on {QualityStore.DateText(cmd.ApprovedOn)}.", c.Now, ct);
            return QStep.Done(("approvedOn", QualityStore.DateText(cmd.ApprovedOn)));
        }, ct);

    private static QStep Frozen(QCtx c) => QStep.Fail($"{c.Head.Number} is {c.StateLabel} and can no longer be edited.");

    private Task<Outcome<DeviationDetail>> Run(Subject subject, string number, string action, string? corr, PlmAction permission, Func<QCtx, NpgsqlConnection, NpgsqlTransaction, Task<QStep>> body, CancellationToken ct) =>
        _run.MutateAsync(subject, Kind, number, action, corr, permission, body, (conn, tx, id, notices) => LoadDetailAsync(conn, tx, subject, id, notices, ct), ct);

    // ---------------------------------------------------------------- applicability

    public static string DescribeScope(string? revision, long? serialFrom, long? serialTo, IReadOnlyList<string> serials, IReadOnlyList<string> lots, decimal? maxQuantity)
    {
        var parts = new List<string>();
        if (revision is not null) parts.Add($"revision {revision}");
        if (serialFrom is not null || serialTo is not null)
            parts.Add((serialFrom, serialTo) switch
            {
                ({ } f, { } t) when f == t => $"serial {f}", ({ } f, { } t) => $"serials {f} to {t}", ({ } f, null) => $"serials {f} and up", (null, { } t) => $"serials up to {t}", _ => "",
            });
        if (serials.Count > 0) parts.Add(serials.Count <= 5 ? "serials " + string.Join(", ", serials) : $"{serials.Count} listed serials");
        if (lots.Count > 0) parts.Add((lots.Count == 1 ? "lot " : "lots ") + string.Join(", ", lots.Take(5)) + (lots.Count > 5 ? $" and {lots.Count - 5} more" : ""));
        if (maxQuantity is { } q) parts.Add($"up to {QualityStore.Num(q)} units");
        return parts.Count == 0 ? "Every unit" : string.Join("; ", parts);
    }

    /// <summary>True when a unit (serial and lot may be unknown) falls inside a scope row. A restriction the unit cannot be tested against (no serial given) does not match.</summary>
    public static bool Covers(long? serialFrom, long? serialTo, IReadOnlyList<string> serials, IReadOnlyList<string> lots, string? serial, string? lot)
    {
        var serialRestricted = serialFrom is not null || serialTo is not null || serials.Count > 0;
        if (serialRestricted)
        {
            if (string.IsNullOrWhiteSpace(serial)) return false;
            var listed = serials.Contains(serial.Trim(), StringComparer.Ordinal);
            var inRange = (serialFrom is not null || serialTo is not null) && long.TryParse(serial.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                && (serialFrom is null || n >= serialFrom) && (serialTo is null || n <= serialTo);
            if (!listed && !inRange) return false;
        }
        if (lots.Count > 0 && (string.IsNullOrWhiteSpace(lot) || !lots.Contains(lot.Trim(), StringComparer.Ordinal))) return false;
        return true;
    }

    /// <summary>
    /// The deviations and waivers that name a part. NotFound when the caller cannot read the part. Only records the caller may read are returned (SQL read filter), so a part
    /// that has restricted records shows no sign of them. With a serial or lot, only the records that cover that unit and are active on <paramref name="asOf"/> are returned.
    /// </summary>
    public async Task<Outcome<IReadOnlyList<ApplicableDeviation>>> ForPartAsync(Subject subject, string partNumber, bool includeInactive, string? serial, string? lot, DateOnly? asOf, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await _run.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var part = await _lookups.FindPartAsync(conn, tx, partNumber, null, false, ct);
        var read = part is null ? null : AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Read);
        if (part is null || !read!.Permitted)
        {
            if (read is not null) await _run.Audit.AppendAsync(tx, QualityStore.Entry(subject, "deviation.for_part", AuditOutcome.Denied, "part", partNumber, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<IReadOnlyList<ApplicableDeviation>>.NotFound();
        }
        var day = asOf ?? QualityShared.Today(_run.Clock);
        var filter = AccessPolicy.BuildReadFilter(subject, "d");
        var list = new List<ApplicableDeviation>();
        await using (var cmd = QualityStore.Cmd(conn, tx,
            $"""
            SELECT d.number, d.dev_kind, d.title, d.lifecycle_state, COALESCE(ls.label, d.lifecycle_state), d.valid_from, d.valid_to, d.customer_approval_required, d.customer_approval_ref,
                   s.part_revision, s.serial_from, s.serial_to, s.serials, s.lots, s.max_quantity, {MarkingColumns.Select("d")}
            FROM qual.deviation_scope s
            JOIN qual.deviation d ON d.id = s.deviation_id
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = d.lifecycle_id AND ls.state = d.lifecycle_state
            WHERE {filter.Sql} AND s.part_master_id = @pm
              AND (@all OR d.lifecycle_state = 'Approved')
            ORDER BY d.valid_to, d.number
            """))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("pm", part.MasterId);
            cmd.Parameters.AddWithValue("all", includeInactive);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var from = DateOnly.FromDateTime(r.GetDateTime(5));
                var to = DateOnly.FromDateTime(r.GetDateTime(6));
                var state = r.GetString(3);
                var serials = r.GetFieldValue<string[]>(12);
                var lots = r.GetFieldValue<string[]>(13);
                var revision = r.IsDBNull(9) ? null : r.GetString(9);
                var active = state == "Approved" && from <= day && day <= to;
                if (serial is not null || lot is not null)
                {
                    if (!active || !Covers(r.IsDBNull(10) ? null : r.GetInt64(10), r.IsDBNull(11) ? null : r.GetInt64(11), serials, lots, serial, lot)) continue;
                    if (revision is not null && revision != part.Revision) continue;
                }
                var m = MarkingColumns.Read(r, 15);
                list.Add(new ApplicableDeviation(r.GetString(0), r.GetString(1), r.GetString(2), state, r.GetString(4), from, to, active,
                    DescribeScope(revision, r.IsDBNull(10) ? null : r.GetInt64(10), r.IsDBNull(11) ? null : r.GetInt64(11), serials, lots, r.IsDBNull(14) ? null : r.GetDecimal(14)),
                    !r.GetBoolean(7) || !r.IsDBNull(8), m.BannerText(), m));
            }
        }
        await _run.Audit.AppendAsync(tx, QualityStore.Entry(subject, "deviation.for_part", AuditOutcome.Success, "part", partNumber, correlationId,
            ("returned", QualityRunner.Int(list.Count)), ("asOf", QualityStore.DateText(day))), ct);
        await tx.CommitAsync(ct);
        return Outcome<IReadOnlyList<ApplicableDeviation>>.Ok(list);
    }

    // ---------------------------------------------------------------- detail

    internal async Task<DeviationDetail?> LoadDetailAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, Guid id, IReadOnlyList<string> notices, CancellationToken ct)
    {
        await using var cmd = QualityStore.Cmd(conn, tx,
            $"""
            SELECT n.number, n.dev_kind, n.title, n.description, n.requirement_ref, n.customer_approval_required, n.customer_approval_ref, n.customer_approval_date, n.customer_approval_by,
                   n.valid_from, n.valid_to, n.close_reason, n.lifecycle_id, n.lifecycle_state, n.submission_no, n.requester_name, n.created_at, n.state_changed_at, {MarkingColumns.Select("n")}
            FROM qual.deviation n WHERE n.id = @id
            """);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var number = r.GetString(0);
        var markings = MarkingColumns.Read(r, 18);
        var state = r.GetString(13);
        var f = (Kind: r.GetString(1), Title: r.GetString(2), Desc: r.GetString(3), Req: r.GetString(4), CustReq: r.GetBoolean(5), CustRef: r.IsDBNull(6) ? null : r.GetString(6),
            CustDate: QualityShared.DateAt(r, 7), CustBy: r.IsDBNull(8) ? null : r.GetString(8), From: DateOnly.FromDateTime(r.GetDateTime(9)), To: DateOnly.FromDateTime(r.GetDateTime(10)),
            Close: r.IsDBNull(11) ? null : r.GetString(11), Lifecycle: r.GetString(12), Submission: r.GetInt32(14), Requester: r.GetString(15), Created: QualityStore.Utc(r, 16), Changed: QualityStore.Utc(r, 17));
        await r.CloseAsync();

        var lifecycle = (await Lifecycle.LoadAsync(conn, tx, f.Lifecycle, ct))!;
        var modify = AccessPolicy.Evaluate(subject, markings, PlmAction.Modify);
        var sign = AccessPolicy.Evaluate(subject, markings, PlmAction.Sign);
        var final = QualityShared.IsFinal(lifecycle, state);
        var today = QualityShared.Today(_run.Clock);
        var shown = new List<Markings> { markings };
        var scope = new List<ScopeView>();
        await using (var q = QualityStore.Cmd(conn, tx, "SELECT id, part_number, part_revision, serial_from, serial_to, serials, lots, max_quantity, part_master_id FROM qual.deviation_scope WHERE deviation_id = @id ORDER BY part_number, part_revision NULLS FIRST"))
        {
            q.Parameters.AddWithValue("id", id);
            await using var rr = await q.ExecuteReaderAsync(ct);
            var raw = new List<(Guid, string, string?, long?, long?, string[], string[], decimal?)>();
            while (await rr.ReadAsync(ct))
                raw.Add((rr.GetGuid(0), rr.GetString(1), rr.IsDBNull(2) ? null : rr.GetString(2), rr.IsDBNull(3) ? null : rr.GetInt64(3), rr.IsDBNull(4) ? null : rr.GetInt64(4), rr.GetFieldValue<string[]>(5), rr.GetFieldValue<string[]>(6), rr.IsDBNull(7) ? null : rr.GetDecimal(7)));
            await rr.CloseAsync();
            foreach (var (sid, pn, rev, sf, st, se, lo, mq) in raw)
            {
                var part = await _lookups.FindPartAsync(conn, tx, pn, rev, false, ct);
                var readable = part is not null && AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Read).Permitted;
                if (readable) shown.Add(part!.Markings);
                scope.Add(new ScopeView(sid, readable ? pn : "A restricted part", rev, sf, st, se, lo, mq, readable, readable ? part!.Markings.BannerText() : null));
            }
        }
        var links = await QualityShared.LoadLinksAsync(conn, tx, subject, _lookups, Kind, id, number, modify.Permitted && !final, shown, ct);
        var history = await QualityShared.LoadHistoryAsync(conn, tx, Kind, id, ct);
        var active = state == "Approved" && f.From <= today && today <= f.To;
        var transitions = QualityShared.Transitions(lifecycle, state, modify.Permitted,
            to => null, to => to is "Withdrawn" or "Revoked" or "Rejected");
        // The steps only an approver takes (customer decision, revoking) follow the Sign permission rather than Modify.
        transitions = [.. transitions.Select(t => t.Via == "manual" && state is "AwaitingCustomer" or "Approved" ? t with { Allowed = sign.Permitted, Reason = sign.Permitted ? null : "You need the approver role." } : t)];
        return new DeviationDetail(id, number, f.Kind, f.Title, f.Desc, f.Req, f.CustReq, f.CustRef, f.CustDate, f.CustBy, f.From, f.To, active, state == "Approved" ? f.To.DayNumber - today.DayNumber : null,
            f.Close, state, lifecycle.State(state)?.Label ?? state, lifecycle.IsLocked(state), final, f.Submission, markings, Markings.AggregateBanner(shown), f.Requester, f.Created, f.Changed, scope, links, history,
            transitions, new QualityPermissions(modify.Permitted, !lifecycle.IsLocked(state), modify.Reasons), notices, shown);
    }
}
