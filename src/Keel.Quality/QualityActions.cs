using System.Data.Common;
using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Quality;

public sealed record LinkResult(Guid Id, string Kind, string Key);

/// <summary>What quality records tell the notification sink (ADR-0030), inside the transaction of the step that caused it. Pointers only: the sink checks every recipient against the markings.</summary>
internal static class QualityAwareness
{
    public static Task ActionAssignedAsync(INotificationSink? sink, DbConnection conn, DbTransaction tx, QHead head, string ownerSub, string actorSub, Guid actionId, CancellationToken ct) =>
        sink is null ? Task.CompletedTask
            : sink.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.QualityActionAssigned, head.Kind, head.Number, head.Markings, actorSub, ownerSub, actionId), ct);

    public static Task WatchAsync(INotificationSink? sink, DbConnection conn, DbTransaction tx, string userSub, string kind, string number, CancellationToken ct) =>
        sink is null ? Task.CompletedTask : sink.AutoWatchAsync(conn, tx, userSub, kind, number, ct);

    public static Task SystemAsync(INotificationSink? sink, DbConnection conn, DbTransaction tx, string code, QHead head, string? recipientSub, Guid? refId, CancellationToken ct) =>
        sink is null ? Task.CompletedTask : sink.AppendAsync(conn, tx, new NotificationRequest(code, head.Kind, head.Number, head.Markings, null, recipientSub, refId), ct);
}

/// <summary>
/// Action items (containment of an NCR, corrective and preventive actions of a CAPA) and links between records. Owners and due dates follow the change-task pattern:
/// the owner hears about the assignment through the notification sink, starts and completes the item, and an item past its due date is reported once.
/// </summary>
public sealed class QualityActions
{
    private readonly QualityRunner _run;
    private readonly QualityLookups _lookups;
    private readonly INotificationSink? _notifications;

    internal QualityActions(QualityRunner run, QualityLookups lookups, INotificationSink? notifications)
    {
        _run = run;
        _lookups = lookups;
        _notifications = notifications;
    }

    // ---------------------------------------------------------------- action items

    private static bool AcceptsActions(QCtx c) => c.Head.Kind == QualityKinds.Ncr ? !c.Final : c.Editable;

    public Task<Outcome<ActionView>> AddAsync(Subject subject, string kind, string number, CreateActionCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        if (kind is not (QualityKinds.Ncr or QualityKinds.Capa)) return Task.FromResult(Outcome<ActionView>.Invalid(["Actions belong to nonconformance reports and corrective actions."]));
        Guid actionId = Guid.CreateVersion7();
        return _run.MutateAsync<ActionView>(subject, kind, number, kind + ".action_create", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (!AcceptsActions(c)) return QStep.Fail($"{c.Head.Number} is {c.StateLabel}; no more actions can be added.");
            var allowed = kind == QualityKinds.Ncr ? ActionTypes.Ncr : ActionTypes.Capa;
            var errors = new List<string>();
            if (!allowed.Contains(cmd.ActionType)) errors.Add($"Action type must be one of: {string.Join(", ", allowed)}.");
            var text = cmd.Description?.Trim() ?? "";
            if (text.Length is 0 or > 2000) errors.Add("Describe the action (at most 2000 characters).");
            if (cmd.DueDate is { } due && due < QualityShared.Today(_run.Clock)) errors.Add("The due date cannot be in the past.");
            var owner = string.IsNullOrWhiteSpace(cmd.Owner) ? null : await QualityStore.FindUserAsync(conn, tx, cmd.Owner.Trim(), ct);
            if (owner is null) errors.Add("Choose an owner who has signed in before.");
            else if (!AccessPolicy.Evaluate(owner.Subject, c.Head.Markings, PlmAction.Read).Permitted) errors.Add($"{owner.Username} cannot read this record and cannot own an action on it.");
            if (errors.Count > 0) return QStep.Invalid([.. errors]);

            await using var seq = QualityStore.Cmd(conn, tx, "SELECT nextval('qual.action_seq')");
            var number2 = "ACT-" + ((long)(await seq.ExecuteScalarAsync(ct))!).ToString("D6", CultureInfo.InvariantCulture);
            await using (var ins = QualityStore.Cmd(conn, tx,
                """
                INSERT INTO qual.action (id, number, parent_kind, parent_id, action_type, description, owner_sub, owner_username, owner_name, due_date, status, created_at, created_by)
                VALUES (@id, @num, @pk, @pid, @type, @desc, @osub, @ouser, @oname, @due, 'Open', @at, @by)
                """))
            {
                ins.Parameters.AddWithValue("id", actionId);
                ins.Parameters.AddWithValue("num", number2);
                ins.Parameters.AddWithValue("pk", kind);
                ins.Parameters.AddWithValue("pid", c.Head.Id);
                ins.Parameters.AddWithValue("type", cmd.ActionType);
                ins.Parameters.AddWithValue("desc", text);
                ins.Parameters.AddWithValue("osub", owner!.Sub);
                ins.Parameters.AddWithValue("ouser", owner.Username);
                ins.Parameters.AddWithValue("oname", owner.DisplayName);
                ins.Parameters.AddWithValue("due", cmd.DueDate is { } d ? d.ToDateTime(TimeOnly.MinValue) : DBNull.Value);
                ins.Parameters.AddWithValue("at", c.Now);
                ins.Parameters.AddWithValue("by", QualityStore.Who(subject));
                await ins.ExecuteNonQueryAsync(ct);
            }
            await QualityStore.AppendHistoryAsync(conn, tx, kind, c.Head.Id, subject, "Action added", $"{number2} ({cmd.ActionType}) for {owner.Username}.", c.Now, ct);
            if (owner.Sub != subject.Id) await QualityAwareness.ActionAssignedAsync(_notifications, conn, tx, c.Head, owner.Sub, subject.Id, actionId, ct);
            await QualityAwareness.WatchAsync(_notifications, conn, tx, owner.Sub, kind, c.Head.Number, ct);
            return QStep.Done(("action", number2), ("type", cmd.ActionType), ("owner", owner.Username), ("due", QualityStore.DateText(cmd.DueDate)));
        }, (conn, tx, id, _) => LoadActionAsync(conn, tx, subject, actionId, ct), ct);
    }

    private async Task<ActionView?> LoadActionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, Guid actionId, CancellationToken ct)
    {
        await using var cmd = QualityStore.Cmd(conn, tx, "SELECT a.parent_kind, a.parent_id FROM qual.action a WHERE a.id = @id");
        cmd.Parameters.AddWithValue("id", actionId);
        Guid pid;
        string pk;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            (pk, pid) = (r.GetString(0), r.GetGuid(1));
        }
        var head = await QualityStore.LoadByIdAsync(conn, tx, pk, pid, false, ct);
        var admin = head is not null && AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Administer).Permitted;
        var lifecycle = head is null ? null : await Lifecycle.LoadAsync(conn, tx, head.LifecycleId, ct);
        var final = lifecycle is null || QualityShared.IsFinal(lifecycle, head!.State);
        return (await QualityShared.LoadActionsAsync(conn, tx, subject, pk, pid, final, admin, QualityShared.Today(_run.Clock), ct)).FirstOrDefault(a => a.Id == actionId);
    }

    private sealed record ActionRow(Guid Id, string Number, string OwnerSub, string OwnerUsername, string Status);

    private static async Task<ActionRow?> FindActionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid parentId, string actionNumber, CancellationToken ct)
    {
        await using var cmd = QualityStore.Cmd(conn, tx, "SELECT a.id, a.number, a.owner_sub, a.owner_username, a.status FROM qual.action a WHERE a.parent_id = @p AND a.number = @n FOR UPDATE");
        cmd.Parameters.AddWithValue("p", parentId);
        cmd.Parameters.AddWithValue("n", actionNumber);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ActionRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)) : null;
    }

    public Task<Outcome<ActionView>> StartAsync(Subject subject, string kind, string number, string actionNumber, string? correlationId, CancellationToken ct = default)
    {
        Guid actionId = default;
        return _run.MutateAsync<ActionView>(subject, kind, number, kind + ".action_start", correlationId, PlmAction.Read, async (c, conn, tx) =>
        {
            var a = await FindActionAsync(conn, tx, c.Head.Id, actionNumber, ct);
            if (a is null) return QStep.Invalid($"{actionNumber} is not an action of {number}.");
            actionId = a.Id;
            if (c.Final) return QStep.Fail($"{c.Head.Number} is {c.StateLabel}.");
            if (a.OwnerSub != subject.Id) return QStep.Forbid("action.not_owner");
            if (a.Status != "Open") return QStep.Fail($"{a.Number} is {a.Status}; only an open action can be started.");
            await using var up = QualityStore.Cmd(conn, tx, "UPDATE qual.action SET status = 'InProgress', started_at = @at WHERE id = @id");
            up.Parameters.AddWithValue("at", c.Now);
            up.Parameters.AddWithValue("id", a.Id);
            await up.ExecuteNonQueryAsync(ct);
            await QualityStore.AppendHistoryAsync(conn, tx, kind, c.Head.Id, subject, "Action started", a.Number, c.Now, ct);
            return QStep.Done(("action", a.Number));
        }, (conn, tx, id, _) => LoadActionAsync(conn, tx, subject, actionId, ct), ct);
    }

    public Task<Outcome<ActionView>> CompleteAsync(Subject subject, string kind, string number, string actionNumber, string note, string? correlationId, CancellationToken ct = default)
    {
        Guid actionId = default;
        return _run.MutateAsync<ActionView>(subject, kind, number, kind + ".action_complete", correlationId, PlmAction.Read, async (c, conn, tx) =>
        {
            var a = await FindActionAsync(conn, tx, c.Head.Id, actionNumber, ct);
            if (a is null) return QStep.Invalid($"{actionNumber} is not an action of {number}.");
            actionId = a.Id;
            if (c.Final) return QStep.Fail($"{c.Head.Number} is {c.StateLabel}.");
            var admin = AccessPolicy.Evaluate(subject, c.Head.Markings, PlmAction.Administer).Permitted;
            if (a.OwnerSub != subject.Id && !admin) return QStep.Forbid("action.not_owner");
            if (a.Status is not ("Open" or "InProgress")) return QStep.Fail($"{a.Number} is already {a.Status}.");
            var text = note?.Trim() ?? "";
            if (text.Length is 0 or > 2000) return QStep.Invalid("Say what was done (required, at most 2000 characters).");
            await using var up = QualityStore.Cmd(conn, tx, "UPDATE qual.action SET status = 'Done', completion_note = @note, completed_at = @at, completed_by = @by WHERE id = @id");
            up.Parameters.AddWithValue("note", text);
            up.Parameters.AddWithValue("at", c.Now);
            up.Parameters.AddWithValue("by", QualityStore.Who(subject));
            up.Parameters.AddWithValue("id", a.Id);
            await up.ExecuteNonQueryAsync(ct);
            await QualityStore.AppendHistoryAsync(conn, tx, kind, c.Head.Id, subject, "Action completed", a.Number, c.Now, ct);
            return QStep.Done(("action", a.Number));
        }, (conn, tx, id, _) => LoadActionAsync(conn, tx, subject, actionId, ct), ct);
    }

    public Task<Outcome<ActionView>> CancelAsync(Subject subject, string kind, string number, string actionNumber, string reason, string? correlationId, CancellationToken ct = default)
    {
        Guid actionId = default;
        return _run.MutateAsync<ActionView>(subject, kind, number, kind + ".action_cancel", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            var a = await FindActionAsync(conn, tx, c.Head.Id, actionNumber, ct);
            if (a is null) return QStep.Invalid($"{actionNumber} is not an action of {number}.");
            actionId = a.Id;
            if (c.Final) return QStep.Fail($"{c.Head.Number} is {c.StateLabel}.");
            if (a.Status is not ("Open" or "InProgress")) return QStep.Fail($"{a.Number} is already {a.Status}.");
            var text = reason?.Trim() ?? "";
            if (text.Length is 0 or > 1000) return QStep.Invalid("Give a reason for cancelling (required, at most 1000 characters).");
            await using var up = QualityStore.Cmd(conn, tx, "UPDATE qual.action SET status = 'Cancelled', cancel_reason = @r, completed_at = @at, completed_by = @by WHERE id = @id");
            up.Parameters.AddWithValue("r", text);
            up.Parameters.AddWithValue("at", c.Now);
            up.Parameters.AddWithValue("by", QualityStore.Who(subject));
            up.Parameters.AddWithValue("id", a.Id);
            await up.ExecuteNonQueryAsync(ct);
            await QualityStore.AppendHistoryAsync(conn, tx, kind, c.Head.Id, subject, "Action cancelled", a.Number, c.Now, ct);
            return QStep.Done(("action", a.Number));
        }, (conn, tx, id, _) => LoadActionAsync(conn, tx, subject, actionId, ct), ct);
    }

    // ---------------------------------------------------------------- links

    public Task<Outcome<LinkResult>> AddLinkAsync(Subject subject, string kind, string number, string toKind, string toKey, string? correlationId, CancellationToken ct = default)
    {
        var linkId = Guid.CreateVersion7();
        var key = toKey?.Trim() ?? "";
        return _run.MutateAsync<LinkResult>(subject, kind, number, kind + ".link_add", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (c.Final) return QStep.Fail($"{c.Head.Number} is {c.StateLabel} and its links are frozen.");
            if (!LinkKinds.Targets.Contains(toKind)) return QStep.Invalid($"Link to one of: {string.Join(", ", LinkKinds.Targets)}.");
            if (key.Length is 0 or > 64) return QStep.Invalid("Enter the number of the record to link.");
            if (toKind == kind && string.Equals(key, c.Head.Number, StringComparison.OrdinalIgnoreCase)) return QStep.Invalid("A record cannot link to itself.");
            var target = await _lookups.ResolveAsync(conn, tx, toKind, key, ct);
            if (target is null || !AccessPolicy.Evaluate(subject, target.Markings, PlmAction.Read).Permitted) return QStep.Invalid($"{key} was not found.");
            // The record must cover what it links to (a CAPA that names a controlled part or report is at least as restricted), except changes, which carry their own markings.
            if (toKind != "change")
            {
                var gaps = MarkingDominance.Gaps(c.Head.Markings, target.Markings);
                if (gaps.Count > 0) return QStep.Invalid($"The markings of {c.Head.Number} must cover those of {target.Key}. {MarkingDominance.Describe(gaps)}");
            }
            await using (var exists = QualityStore.Cmd(conn, tx, "SELECT 1 FROM qual.link WHERE from_kind = @k AND from_id = @id AND to_kind = @tk AND to_key = @key AND removed_at IS NULL"))
            {
                exists.Parameters.AddWithValue("k", kind);
                exists.Parameters.AddWithValue("id", c.Head.Id);
                exists.Parameters.AddWithValue("tk", toKind);
                exists.Parameters.AddWithValue("key", target.Key);
                if (await exists.ExecuteScalarAsync(ct) is not null) return QStep.Fail($"{target.Key} is already linked.");
            }
            await using (var ins = QualityStore.Cmd(conn, tx, "INSERT INTO qual.link (id, from_kind, from_id, to_kind, to_key, added_at, added_by) VALUES (@id, @k, @fid, @tk, @key, @at, @by)"))
            {
                ins.Parameters.AddWithValue("id", linkId);
                ins.Parameters.AddWithValue("k", kind);
                ins.Parameters.AddWithValue("fid", c.Head.Id);
                ins.Parameters.AddWithValue("tk", toKind);
                ins.Parameters.AddWithValue("key", target.Key);
                ins.Parameters.AddWithValue("at", c.Now);
                ins.Parameters.AddWithValue("by", QualityStore.Who(subject));
                await ins.ExecuteNonQueryAsync(ct);
            }
            await QualityStore.AppendHistoryAsync(conn, tx, kind, c.Head.Id, subject, "Link added", $"{LinkKinds.Label(toKind)} {target.Key}.", c.Now, ct);
            return QStep.Done(("target", target.Key), ("targetKind", toKind));
        }, (conn, tx, id, _) => Task.FromResult<LinkResult?>(new LinkResult(linkId, toKind, key)), ct);
    }

    public Task<Outcome<LinkResult>> RemoveLinkAsync(Subject subject, string kind, string number, Guid linkId, string? correlationId, CancellationToken ct = default) =>
        _run.MutateAsync<LinkResult>(subject, kind, number, kind + ".link_remove", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (c.Final) return QStep.Fail($"{c.Head.Number} is {c.StateLabel} and its links are frozen.");
            await using var up = QualityStore.Cmd(conn, tx, "UPDATE qual.link SET removed_at = @at, removed_by = @by WHERE id = @id AND from_kind = @k AND from_id = @fid AND removed_at IS NULL RETURNING to_kind, to_key");
            up.Parameters.AddWithValue("at", c.Now);
            up.Parameters.AddWithValue("by", QualityStore.Who(subject));
            up.Parameters.AddWithValue("id", linkId);
            up.Parameters.AddWithValue("k", kind);
            up.Parameters.AddWithValue("fid", c.Head.Id);
            await using var r = await up.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return QStep.Invalid("That link does not exist.");
            var (tk, key) = (r.GetString(0), r.GetString(1));
            await r.CloseAsync();
            await QualityStore.AppendHistoryAsync(conn, tx, kind, c.Head.Id, subject, "Link removed", $"{LinkKinds.Label(tk)} {key}.", c.Now, ct);
            return QStep.Done(("target", key), ("targetKind", tk));
        }, (conn, tx, id, _) => Task.FromResult<LinkResult?>(new LinkResult(linkId, "", "")), ct);

    // ---------------------------------------------------------------- the caller's own work

    /// <summary>Open action items assigned to the caller, on records the caller can read now (a record that became unreadable is not listed).</summary>
    public async Task<IReadOnlyList<MyAction>> MyActionsAsync(Subject subject, string? correlationId, CancellationToken ct = default)
    {
        var list = new List<MyAction>();
        var today = QualityShared.Today(_run.Clock);
        await using var conn = await _run.DataSource.OpenConnectionAsync(ct);
        foreach (var kind in new[] { QualityKinds.Ncr, QualityKinds.Capa })
        {
            var filter = AccessPolicy.BuildReadFilter(subject, "c");
            await using var cmd = new NpgsqlCommand(
                $"""
                SELECT a.number, a.action_type, a.description, a.due_date, a.status, c.number, c.title, {MarkingColumns.Select("c")}
                FROM qual.action a JOIN {QualityKinds.Table(kind)} c ON c.id = a.parent_id
                WHERE a.parent_kind = @k AND a.owner_sub = @me AND a.status IN ('Open', 'InProgress') AND {filter.Sql}
                ORDER BY a.due_date NULLS LAST, a.number
                """, conn);
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("k", kind);
            cmd.Parameters.AddWithValue("me", subject.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var due = QualityShared.DateAt(r, 3);
                var m = MarkingColumns.Read(r, 7);
                list.Add(new MyAction(r.GetString(0), r.GetString(1), r.GetString(2), due, due is { } d && d < today, r.GetString(4), kind, r.GetString(5), r.GetString(6), m.BannerText(), m));
            }
        }
        await _run.Audit.AppendAsync(QualityStore.Entry(subject, "quality.my_actions", AuditOutcome.Success, "quality", null, correlationId, ("returned", QualityRunner.Int(list.Count))), ct);
        return list;
    }

    /// <summary>People of the user directory matching the text, for choosing an action owner (names only; the owner must still read the record).</summary>
    public async Task<IReadOnlyList<UserChoice>> SearchUsersAsync(string text, int limit = 15, CancellationToken ct = default)
    {
        var like = "%" + (text ?? "").Trim().Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%";
        await using var cmd = _run.DataSource.CreateCommand("SELECT username, display_name FROM identity.user_directory WHERE username ILIKE @l OR display_name ILIKE @l ORDER BY username LIMIT @n");
        cmd.Parameters.AddWithValue("l", like);
        cmd.Parameters.AddWithValue("n", Math.Clamp(limit, 1, 50));
        var list = new List<UserChoice>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new UserChoice(r.GetString(0), r.GetString(1)));
        return list;
    }
}

public sealed record MyAction(string Number, string ActionType, string Description, DateOnly? Due, bool Overdue, string Status, string Kind, string RecordNumber, string RecordTitle, string Banner, Markings Markings);
