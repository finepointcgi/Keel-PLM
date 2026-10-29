using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Quality;

/// <summary>
/// Nonconformance reports (ADR-0036): a defect against a part revision, lot or serial, with containment actions and a disposition that the material review board
/// signs through the workflow engine. Like every module service, each call evaluates the access policy (ADR-0004) and writes its audit event in the same
/// transaction as its change, including denials. Audit events carry numbers, states and counts, never the defect text.
/// </summary>
public sealed class NcrService
{
    private const string Kind = QualityKinds.Ncr;
    private readonly QualityRunner _run;
    private readonly QualityLookups _lookups;
    private readonly INotificationSink? _notifications;

    internal NcrService(QualityRunner run, QualityLookups lookups, INotificationSink? notifications)
    {
        _run = run;
        _lookups = lookups;
        _notifications = notifications;
    }

    // ---------------------------------------------------------------- create

    public async Task<Outcome<NcrDetail>> CreateAsync(Subject subject, CreateNcrCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await _run.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var errors = new List<string>();
        var title = cmd.Title?.Trim() ?? "";
        if (title.Length is 0 or > 200) errors.Add("Title is required (at most 200 characters).");
        if ((cmd.Description?.Length ?? 0) > 8000) errors.Add("The defect description must be at most 8000 characters.");
        if (!NcrSeverities.All.Contains(cmd.Severity)) errors.Add($"Severity must be one of: {string.Join(", ", NcrSeverities.All)}.");
        if (!NcrSeverities.Stages.Contains(cmd.Stage)) errors.Add($"Stage must be one of: {string.Join(", ", NcrSeverities.Stages)}.");
        if (cmd.Quantity <= 0) errors.Add("The quantity must be more than zero.");
        errors.AddRange(TextLimits(cmd.LotNumber, cmd.SerialNumber, cmd.SupplierRef, cmd.Unit));
        if (errors.Count > 0) return Outcome<NcrDetail>.Invalid(errors);

        var part = await _lookups.FindPartAsync(conn, tx, cmd.PartNumber?.Trim() ?? "", string.IsNullOrWhiteSpace(cmd.Revision) ? null : cmd.Revision.Trim(), false, ct);
        if (part is null || !AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Read).Permitted)
        {
            if (part is not null)
                await _run.Audit.AppendAsync(tx, QualityStore.Entry(subject, "ncr.create", AuditOutcome.Denied, Kind, null, correlationId, ("reasons", string.Join(",", AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Read).Reasons)), ("via", "part")), ct);
            await tx.CommitAsync(ct);
            return Outcome<NcrDetail>.Invalid([$"Part {cmd.PartNumber} was not found."]);
        }
        // The report inherits the part's restrictions: its markings must cover the part's (the defect text may describe the part).
        var gaps = MarkingDominance.Gaps(cmd.Markings, part.Markings);
        if (gaps.Count > 0) return Outcome<NcrDetail>.Invalid([$"The markings of the report must cover those of the part. {MarkingDominance.Describe(gaps)}"]);
        var decision = AccessPolicy.Evaluate(subject, cmd.Markings, PlmAction.Modify);
        if (!decision.Permitted)
        {
            await _run.Audit.AppendAsync(tx, QualityStore.Entry(subject, "ncr.create", AuditOutcome.Denied, Kind, null, correlationId, ("reasons", string.Join(",", decision.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<NcrDetail>.Forbidden(decision.Reasons);
        }

        var lifecycleId = await Lifecycle.CurrentIdAsync(conn, tx, QualityKinds.Family(Kind), ct);
        var lifecycle = (await Lifecycle.LoadAsync(conn, tx, lifecycleId, ct))!;
        var number = await QualityStore.NextNumberAsync(conn, tx, Kind, ct);
        var id = Guid.CreateVersion7();
        var now = _run.Clock.GetUtcNow();
        await using (var ins = QualityStore.Cmd(conn, tx,
            $"""
            INSERT INTO qual.ncr (id, number, title, description, severity, detected_at_stage, part_master_id, part_number, part_revision_id, part_revision, lot_number, serial_number,
                                  supplier_ref, quantity, unit, lifecycle_id, lifecycle_state, reporter_sub, reporter_username, reporter_name, {MarkingColumns.InsertColumns},
                                  created_at, created_by, state_changed_at, state_changed_by)
            VALUES (@id, @num, @title, @desc, @sev, @stage, @pm, @pn, @pr, @prl, @lot, @ser, @sup, @qty, @unit, @lc, @state, @sub, @user, @name, {MarkingColumns.InsertValues}, @at, @by, @at, @by)
            """))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("num", number);
            ins.Parameters.AddWithValue("title", title);
            ins.Parameters.AddWithValue("desc", cmd.Description?.Trim() ?? "");
            ins.Parameters.AddWithValue("sev", cmd.Severity);
            ins.Parameters.AddWithValue("stage", cmd.Stage);
            ins.Parameters.AddWithValue("pm", part.MasterId);
            ins.Parameters.AddWithValue("pn", part.Key);
            ins.Parameters.AddWithValue("pr", part.RevisionId);
            ins.Parameters.AddWithValue("prl", part.Revision);
            ins.Parameters.AddWithValue("lot", Opt(cmd.LotNumber));
            ins.Parameters.AddWithValue("ser", Opt(cmd.SerialNumber));
            ins.Parameters.AddWithValue("sup", Opt(cmd.SupplierRef));
            ins.Parameters.AddWithValue("qty", cmd.Quantity);
            ins.Parameters.AddWithValue("unit", string.IsNullOrWhiteSpace(cmd.Unit) ? "EA" : cmd.Unit.Trim());
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
        await QualityStore.AppendHistoryAsync(conn, tx, Kind, id, subject, "Created", $"Against {part.Key} revision {part.Revision}.", now, ct);
        await QualityAwareness.WatchAsync(_notifications, conn, tx, subject.Id, Kind, number, ct);
        await _run.Audit.AppendAsync(tx, QualityStore.Entry(subject, "ncr.create", AuditOutcome.Success, Kind, number, correlationId,
            ("part", part.Key), ("revision", part.Revision), ("severity", cmd.Severity), ("quantity", QualityStore.Num(cmd.Quantity)), ("markings", cmd.Markings.BannerText())), ct);
        var detail = await LoadDetailAsync(conn, tx, subject, id, [], ct);
        await tx.CommitAsync(ct);
        return Outcome<NcrDetail>.Ok(detail!);
    }

    private static object Opt(string? s) => string.IsNullOrWhiteSpace(s) ? DBNull.Value : s.Trim();

    private static IEnumerable<string> TextLimits(string? lot, string? serial, string? supplier, string? unit)
    {
        if ((lot?.Trim().Length ?? 0) > 100) yield return "The lot number must be at most 100 characters.";
        if ((serial?.Trim().Length ?? 0) > 100) yield return "The serial number must be at most 100 characters.";
        if ((supplier?.Trim().Length ?? 0) > 200) yield return "The supplier reference must be at most 200 characters.";
        if ((unit?.Trim().Length ?? 0) > 10) yield return "The unit must be at most 10 characters.";
    }

    // ---------------------------------------------------------------- read

    public Task<Outcome<NcrDetail>> GetAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        _run.ReadAsync(subject, Kind, number, "ncr.read", correlationId, (conn, tx, id, _) => LoadDetailAsync(conn, tx, subject, id, [], ct),
            d => [("actions", QualityRunner.Int(d.Containment.Count)), ("links", QualityRunner.Int(d.Links.Count))], ct);

    // ---------------------------------------------------------------- edit

    public Task<Outcome<NcrDetail>> UpdateAsync(Subject subject, string number, UpdateNcrCommand cmd, string? correlationId, CancellationToken ct = default) =>
        Run(subject, number, "ncr.update", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (!c.Editable) return Frozen(c);
            var errors = new List<string>();
            var title = cmd.Title?.Trim() ?? "";
            if (title.Length is 0 or > 200) errors.Add("Title is required (at most 200 characters).");
            if ((cmd.Description?.Length ?? 0) > 8000) errors.Add("The defect description must be at most 8000 characters.");
            if (!NcrSeverities.All.Contains(cmd.Severity)) errors.Add($"Severity must be one of: {string.Join(", ", NcrSeverities.All)}.");
            if (!NcrSeverities.Stages.Contains(cmd.Stage)) errors.Add($"Stage must be one of: {string.Join(", ", NcrSeverities.Stages)}.");
            if (cmd.Quantity <= 0) errors.Add("The quantity must be more than zero.");
            errors.AddRange(TextLimits(cmd.LotNumber, cmd.SerialNumber, cmd.SupplierRef, cmd.Unit));
            if (errors.Count > 0) return QStep.Invalid([.. errors]);
            await using var up = QualityStore.Cmd(conn, tx,
                "UPDATE qual.ncr SET title = @t, description = @d, severity = @s, detected_at_stage = @st, lot_number = @lot, serial_number = @ser, supplier_ref = @sup, quantity = @q, unit = @u WHERE id = @id");
            up.Parameters.AddWithValue("t", title);
            up.Parameters.AddWithValue("d", cmd.Description?.Trim() ?? "");
            up.Parameters.AddWithValue("s", cmd.Severity);
            up.Parameters.AddWithValue("st", cmd.Stage);
            up.Parameters.AddWithValue("lot", Opt(cmd.LotNumber));
            up.Parameters.AddWithValue("ser", Opt(cmd.SerialNumber));
            up.Parameters.AddWithValue("sup", Opt(cmd.SupplierRef));
            up.Parameters.AddWithValue("q", cmd.Quantity);
            up.Parameters.AddWithValue("u", string.IsNullOrWhiteSpace(cmd.Unit) ? "EA" : cmd.Unit.Trim());
            up.Parameters.AddWithValue("id", c.Head.Id);
            await up.ExecuteNonQueryAsync(ct);
            await QualityStore.AppendHistoryAsync(conn, tx, Kind, c.Head.Id, subject, "Edited", null, c.Now, ct);
            return QStep.Done(("severity", cmd.Severity), ("quantity", QualityStore.Num(cmd.Quantity)));
        }, ct);

    public Task<Outcome<NcrDetail>> ProposeDispositionAsync(Subject subject, string number, ProposeDispositionCommand cmd, string? correlationId, CancellationToken ct = default) =>
        Run(subject, number, "ncr.propose_disposition", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (!c.Editable) return Frozen(c);
            if (!NcrDispositions.All.Contains(cmd.Disposition)) return QStep.Invalid($"Disposition must be one of: {string.Join(", ", NcrDispositions.All)}.");
            var why = cmd.Rationale?.Trim() ?? "";
            if (why.Length is 0 or > 4000) return QStep.Invalid("Give the rationale for the disposition (required, at most 4000 characters).");
            await using var up = QualityStore.Cmd(conn, tx, "UPDATE qual.ncr SET proposed_disposition = @d, disposition_rationale = @r WHERE id = @id");
            up.Parameters.AddWithValue("d", cmd.Disposition);
            up.Parameters.AddWithValue("r", why);
            up.Parameters.AddWithValue("id", c.Head.Id);
            await up.ExecuteNonQueryAsync(ct);
            await QualityStore.AppendHistoryAsync(conn, tx, Kind, c.Head.Id, subject, "Disposition proposed", NcrDispositions.Label(cmd.Disposition), c.Now, ct);
            return QStep.Done(("disposition", cmd.Disposition));
        }, ct);

    /// <summary>Changes the markings of a report that can still be edited. They must cover the part and every linked record, and the actor must still read it.</summary>
    public Task<Outcome<NcrDetail>> ChangeMarkingsAsync(Subject subject, string number, Markings markings, string? reason, string? correlationId, CancellationToken ct = default) =>
        Run(subject, number, "ncr.markings_change", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) return QStep.Invalid("A reason is required to change markings.");
            if (!c.Editable) return Frozen(c);
            if (markings == c.Head.Markings) return QStep.Fail("The markings are unchanged.");
            var after = AccessPolicy.Evaluate(subject, markings, PlmAction.Modify);
            if (!after.Permitted) return QStep.Forbid([.. after.Reasons.Select(r => "new_markings:" + r)]);
            await using (var q = QualityStore.Cmd(conn, tx, "SELECT part_number, part_revision FROM qual.ncr WHERE id = @id"))
            {
                q.Parameters.AddWithValue("id", c.Head.Id);
                await using var r = await q.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                var (pn, prl) = (r.GetString(0), r.GetString(1));
                await r.CloseAsync();
                var part = await _lookups.FindPartAsync(conn, tx, pn, prl, false, ct);
                if (part is not null && MarkingDominance.Gaps(markings, part.Markings) is { Count: > 0 } gaps)
                    return QStep.Invalid($"The new markings do not cover the part. {MarkingDominance.Describe(gaps)}");
            }
            await QualityStore.SetMarkingsAsync(conn, tx, Kind, c.Head.Id, markings);
            await QualityStore.AppendHistoryAsync(conn, tx, Kind, c.Head.Id, subject, "Markings changed", $"{c.Head.Markings.BannerText()} to {markings.BannerText()}. Reason: {reason.Trim()}", c.Now, ct);
            return QStep.Done(("before", c.Head.Markings.BannerText()), ("after", markings.BannerText()), ("reason", reason.Trim()));
        }, ct);

    // ---------------------------------------------------------------- manual steps

    /// <summary>Closes a dispositioned report once the approved disposition was carried out and every containment action is finished.</summary>
    public Task<Outcome<NcrDetail>> CloseAsync(Subject subject, string number, string executionNote, string? correlationId, CancellationToken ct = default) =>
        Run(subject, number, "ncr.close", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            var note = executionNote?.Trim() ?? "";
            if (note.Length is 0 or > 4000) return QStep.Invalid("Say how the disposition was carried out (required, at most 4000 characters).");
            if (c.State != "Dispositioned") return QStep.Fail($"{c.Head.Number} is {c.StateLabel}; only a dispositioned report can be closed.");
            var open = await OpenActionsAsync(conn, tx, c.Head.Id, ct);
            if (open > 0) return QStep.Fail($"{open} containment action(s) are still open.");
            var moved = await QualityStore.TransitionAsync(conn, tx, c.Head, "Closed", "manual", QualityStore.Who(subject), c.Now, "disposition_note = @note", ("note", note));
            if (!moved.IsOk) return QStep.Fail(moved.Error!);
            await QualityStore.AppendHistoryAsync(conn, tx, Kind, c.Head.Id, subject, "Closed", "The disposition was carried out.", c.Now, ct);
            return QStep.Done(("from", c.State), ("to", "Closed"));
        }, ct);

    /// <summary>Cancels a report that is still open (raised in error, duplicate). A report under or after review cannot be cancelled.</summary>
    public Task<Outcome<NcrDetail>> CancelAsync(Subject subject, string number, string reason, string? correlationId, CancellationToken ct = default) =>
        Run(subject, number, "ncr.cancel", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            var text = reason?.Trim() ?? "";
            if (text.Length is 0 or > 1000) return QStep.Invalid("Give a reason for cancelling (required, at most 1000 characters).");
            var moved = await QualityStore.TransitionAsync(conn, tx, c.Head, "Cancelled", "manual", QualityStore.Who(subject), c.Now, "cancel_reason = @r", ("r", text));
            if (!moved.IsOk) return QStep.Fail(moved.Error!);
            await QualityStore.AppendHistoryAsync(conn, tx, Kind, c.Head.Id, subject, "Cancelled", text, c.Now, ct);
            return QStep.Done(("from", c.State), ("to", "Cancelled"));
        }, ct);

    private static async Task<int> OpenActionsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var cmd = QualityStore.Cmd(conn, tx, "SELECT count(*) FROM qual.action WHERE parent_kind = 'ncr' AND parent_id = @id AND status IN ('Open', 'InProgress')");
        cmd.Parameters.AddWithValue("id", id);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static QStep Frozen(QCtx c) => QStep.Fail($"{c.Head.Number} is {c.StateLabel} and can no longer be edited.");

    private Task<Outcome<NcrDetail>> Run(Subject subject, string number, string action, string? corr, PlmAction permission, Func<QCtx, NpgsqlConnection, NpgsqlTransaction, Task<QStep>> body, CancellationToken ct) =>
        _run.MutateAsync(subject, Kind, number, action, corr, permission, body, (conn, tx, id, notices) => LoadDetailAsync(conn, tx, subject, id, notices, ct), ct);

    // ---------------------------------------------------------------- detail

    internal async Task<NcrDetail?> LoadDetailAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, Guid id, IReadOnlyList<string> notices, CancellationToken ct)
    {
        await using var cmd = QualityStore.Cmd(conn, tx,
            $"""
            SELECT n.number, n.title, n.description, n.severity, n.detected_at_stage, n.part_number, n.part_revision, n.part_revision_id, n.lot_number, n.serial_number, n.supplier_ref,
                   n.quantity, n.unit, n.proposed_disposition, n.disposition_rationale, n.disposition, n.disposition_note, n.cancel_reason, n.lifecycle_id, n.lifecycle_state,
                   n.submission_no, n.reporter_name, n.created_at, n.state_changed_at, {MarkingColumns.Select("n")}
            FROM qual.ncr n WHERE n.id = @id
            """);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var number = r.GetString(0);
        var markings = MarkingColumns.Read(r, 24);
        var lifecycleId = r.GetString(18);
        var state = r.GetString(19);
        var row = (Title: r.GetString(1), Description: r.GetString(2), Severity: r.GetString(3), Stage: r.GetString(4), PartNumber: r.GetString(5), PartRevision: r.GetString(6), PartRevisionId: r.GetGuid(7),
            Lot: r.IsDBNull(8) ? null : r.GetString(8), Serial: r.IsDBNull(9) ? null : r.GetString(9), Supplier: r.IsDBNull(10) ? null : r.GetString(10), Qty: r.GetDecimal(11), Unit: r.GetString(12),
            Proposed: r.IsDBNull(13) ? null : r.GetString(13), Rationale: r.GetString(14), Disposition: r.IsDBNull(15) ? null : r.GetString(15), Note: r.IsDBNull(16) ? null : r.GetString(16),
            Cancel: r.IsDBNull(17) ? null : r.GetString(17), Submission: r.GetInt32(20), Reporter: r.GetString(21), Created: QualityStore.Utc(r, 22), Changed: QualityStore.Utc(r, 23));
        await r.CloseAsync();

        var lifecycle = (await Lifecycle.LoadAsync(conn, tx, lifecycleId, ct))!;
        var modify = AccessPolicy.Evaluate(subject, markings, PlmAction.Modify);
        var admin = AccessPolicy.Evaluate(subject, markings, PlmAction.Administer);
        var final = QualityShared.IsFinal(lifecycle, state);
        var shown = new List<Markings> { markings };
        var containment = await QualityShared.LoadActionsAsync(conn, tx, subject, Kind, id, final, admin.Permitted, QualityShared.Today(_run.Clock), ct);
        var links = await QualityShared.LoadLinksAsync(conn, tx, subject, _lookups, Kind, id, number, modify.Permitted && !final, shown, ct);
        var history = await QualityShared.LoadHistoryAsync(conn, tx, Kind, id, ct);
        var partReadable = (await _lookups.FindPartRevisionsAsync(conn, tx, [row.PartRevisionId], ct)).TryGetValue(row.PartRevisionId, out var part) && AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Read).Permitted;
        var open = containment.Count(a => a.Status is "Open" or "InProgress");
        var transitions = QualityShared.Transitions(lifecycle, state, modify.Permitted,
            to => to == "Closed" && open > 0 ? $"{open} containment action(s) are still open." : null, to => to == "Cancelled");
        return new NcrDetail(id, number, row.Title, row.Description, row.Severity, row.Stage, row.PartNumber, row.PartRevision, row.Lot, row.Serial, row.Supplier, row.Qty, row.Unit, row.Proposed,
            row.Rationale, row.Disposition, row.Note, row.Cancel, state, lifecycle.State(state)?.Label ?? state, lifecycle.IsLocked(state), final, row.Submission, markings, Markings.AggregateBanner(shown),
            row.Reporter, row.Created, row.Changed, containment, links, history, transitions, new QualityPermissions(modify.Permitted, !lifecycle.IsLocked(state), modify.Reasons), partReadable, notices, shown);
    }
}
