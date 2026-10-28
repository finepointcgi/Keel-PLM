using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Change;

public sealed partial class ChangeService
{
    // ---------------------------------------------------------------- read

    public async Task<Outcome<ChangeDetail>> GetAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await ChangeStore.LoadByNumberAsync(conn, tx, number, forUpdate: false, ct);
        if (head is null) return Outcome<ChangeDetail>.NotFound();
        var objectType = ChangeTypes.ObjectTypeOf(head.Type);
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, ChangeStore.Entry(subject, "change.read", AuditOutcome.Denied, objectType, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<ChangeDetail>.NotFound();
        }
        var detail = await LoadDetailAsync(conn, tx, subject, head.Id, [], ct);
        await audit.AppendAsync(tx, ChangeStore.Entry(subject, "change.read", AuditOutcome.Success, objectType, number, correlationId,
            ("affected", detail!.Affected.Count.ToString(CultureInfo.InvariantCulture)), ("redacted", detail.Affected.Count(a => a.Redacted).ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return Outcome<ChangeDetail>.Ok(detail);
    }

    public async Task<ChangeSearchResult> SearchAsync(Subject subject, ChangeSearchQuery query, string? correlationId, CancellationToken ct = default)
    {
        var limit = Math.Clamp(query.Limit, 1, MaxSearchLimit);
        var offset = Math.Max(0, query.Offset);
        var q = (query.Text ?? "").Trim();
        var like = "%" + q.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%";
        var filter = AccessPolicy.BuildReadFilter(subject, "c");

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var items = new List<ChangeSummary>();
        long total = 0;
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT c.id, c.change_type, c.number, c.title, c.lifecycle_state, COALESCE(ls.label, c.lifecycle_state), c.change_class, c.priority, c.requester_name,
                   c.created_at, {MarkingColumns.Select("c")}, count(*) OVER ()
            FROM chg.change c
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = c.lifecycle_id AND ls.state = c.lifecycle_state
            WHERE {filter.Sql}
              AND (@type::text IS NULL OR c.change_type = @type)
              AND (@state::text IS NULL OR c.lifecycle_state = @state)
              AND (@q = '' OR c.number ILIKE @like OR c.title ILIKE @like)
              AND (NOT @open OR NOT COALESCE(ls.is_final, false))
            ORDER BY c.created_at DESC, c.number DESC
            LIMIT @limit OFFSET @offset
            """, conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("type", (object?)query.Type ?? DBNull.Value);
            cmd.Parameters.AddWithValue("state", (object?)query.State ?? DBNull.Value);
            cmd.Parameters.AddWithValue("q", q);
            cmd.Parameters.AddWithValue("like", like);
            cmd.Parameters.AddWithValue("open", query.OpenOnly);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var m = MarkingColumns.Read(r, 10);
                items.Add(new ChangeSummary(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.GetString(8), ChangeStore.Utc(r, 9), m.BannerText(), m));
                total = r.GetInt64(10 + MarkingColumns.Count);
            }
        }
        await audit.AppendAsync(tx, ChangeStore.Entry(subject, "change.search", AuditOutcome.Success, "change", null, correlationId,
            ("type", query.Type), ("state", query.State), ("query", q), ("returned", items.Count.ToString(CultureInfo.InvariantCulture)),
            ("numbers", string.Join(",", items.Select(i => i.Number)))), ct);
        await tx.CommitAsync(ct);
        return new ChangeSearchResult(items, total, Markings.AggregateBanner(items.Select(i => i.Markings)));
    }

    /// <summary>
    /// What changes touch one part: the open requests and notices that list it (only those the caller may read) and, for a revision a
    /// change created, the open notice it is bound to. NotFound when the caller cannot read the part.
    /// </summary>
    public async Task<Outcome<ObjectChanges>> ChangesForObjectAsync(Subject subject, string objectType, string key, string? revision, string? correlationId, CancellationToken ct = default)
    {
        var gateway = GatewayFor(objectType);
        if (gateway is null) return Outcome<ObjectChanges>.Invalid([$"Objects of type '{objectType}' have no change history yet."]);
        var filter = AccessPolicy.BuildReadFilter(subject, "c");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var obj = await gateway.FindByKeyAsync(conn, tx, key, revision, forUpdate: false, ct);
        var read = obj is null ? null : AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Read);
        if (obj is null || !read!.Permitted)
        {
            if (read is not null)
                await audit.AppendAsync(tx, ChangeStore.Entry(subject, "change.for_object", AuditOutcome.Denied, objectType, key, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<ObjectChanges>.NotFound();
        }

        var open = new List<ChangeSummary>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT DISTINCT c.id, c.change_type, c.number, c.title, c.lifecycle_state, COALESCE(ls.label, c.lifecycle_state), c.change_class, c.priority, c.requester_name,
                   c.created_at, {MarkingColumns.Select("c")}
            FROM chg.affected_object ao
            JOIN chg.change c ON c.id = ao.change_id
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = c.lifecycle_id AND ls.state = c.lifecycle_state
            WHERE {filter.Sql} AND ao.object_type = @t AND ao.master_id = @m AND ao.removed_at IS NULL AND NOT COALESCE(ls.is_final, false)
            ORDER BY c.created_at DESC, c.number DESC
            """, conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("t", objectType);
            cmd.Parameters.AddWithValue("m", obj.MasterId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var m = MarkingColumns.Read(r, 10);
                open.Add(new ChangeSummary(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.GetString(8), ChangeStore.Utc(r, 9), m.BannerText(), m));
            }
        }

        ChangeSummary? bound = null;
        var boundHidden = false;
        Guid? holder = null;
        await using (var cmd = new NpgsqlCommand("SELECT ao.change_id FROM chg.affected_object ao WHERE ao.resulting_revision_id = @r AND ao.binding", conn, tx))
        {
            cmd.Parameters.AddWithValue("r", obj.RevisionId);
            holder = (Guid?)await cmd.ExecuteScalarAsync(ct);
        }
        if (holder is { } holderId)
        {
            bound = open.FirstOrDefault(o => o.Id == holderId);
            boundHidden = bound is null;
        }
        await audit.AppendAsync(tx, ChangeStore.Entry(subject, "change.for_object", AuditOutcome.Success, objectType, key, correlationId,
            ("open", open.Count.ToString(CultureInfo.InvariantCulture)), ("bound", bound?.Number ?? (boundHidden ? "hidden" : null))), ct);
        await tx.CommitAsync(ct);
        return Outcome<ObjectChanges>.Ok(new ObjectChanges(open, bound, boundHidden, Markings.AggregateBanner(open.Select(o => o.Markings).Append(obj.Markings))));
    }

    // ---------------------------------------------------------------- detail

    /// <summary>Loads the full view. Callers must already have established read access to the change.</summary>
    private async Task<ChangeDetail?> LoadDetailAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, Guid id, IReadOnlyList<string> notices, CancellationToken ct)
    {
        var row = await ChangeStore.LoadByIdAsync(conn, tx, id, forUpdate: false, ct);
        if (row is null) return null;
        var lifecycle = (await Lifecycle.LoadAsync(conn, tx, row.LifecycleId, ct))!;
        var shown = new List<Markings> { row.Markings };

        // ---- source and derived objects (only those the caller may read)
        ChangeRef? source = null;
        if (row.SourceId is { } sourceId && await ChangeStore.LoadByIdAsync(conn, tx, sourceId, forUpdate: false, ct) is { } s
            && AccessPolicy.Evaluate(subject, s.Markings, PlmAction.Read).Permitted)
        {
            var sl = await Lifecycle.LoadAsync(conn, tx, s.LifecycleId, ct);
            source = new ChangeRef(s.Type, s.Number, s.Title, sl?.State(s.State)?.Label ?? s.State, s.Markings.BannerText());
            shown.Add(s.Markings);
        }
        var derived = new List<ChangeRef>();
        var filter = AccessPolicy.BuildReadFilter(subject, "c");
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT c.change_type, c.number, c.title, COALESCE(ls.label, c.lifecycle_state), {MarkingColumns.Select("c")}
            FROM chg.change c
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = c.lifecycle_id AND ls.state = c.lifecycle_state
            WHERE {filter.Sql} AND c.source_id = @id
            ORDER BY c.number
            """, conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var m = MarkingColumns.Read(r, 4);
                shown.Add(m);
                derived.Add(new ChangeRef(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), m.BannerText()));
            }
        }

        // ---- affected objects: current markings decide what this reader may see (a placeholder otherwise, A4)
        var rows = await ChangeStore.LoadAffectedAsync(conn, tx, id, ct);
        var live = new Dictionary<Guid, ChangeObject>();
        foreach (var group in rows.GroupBy(r => r.ObjectType))
        {
            var gateway = GatewayFor(group.Key);
            if (gateway is null) continue;
            var ids = group.SelectMany(r => r.ResultingRevisionId is { } res ? new[] { r.RevisionId, res } : [r.RevisionId]).Distinct().ToArray();
            foreach (var (rev, obj) in await gateway.FindByRevisionsAsync(conn, tx, ids, ct)) live[rev] = obj;
        }
        var affected = new List<AffectedObjectView>();
        foreach (var r in rows)
        {
            if (!live.TryGetValue(r.RevisionId, out var o) || !AccessPolicy.Evaluate(subject, o.Markings, PlmAction.Read).Permitted)
            {
                affected.Add(new AffectedObjectView(r.Id, true, r.ObjectType, null, null, null, null, r.Action, null, null, null, r.AddedAt, r.AddedBy));
                continue;
            }
            shown.Add(o.Markings);
            var res = r.ResultingRevisionId is { } resId && live.TryGetValue(resId, out var ro) ? ro : null;
            affected.Add(new AffectedObjectView(r.Id, false, r.ObjectType, o.Key, o.Name, o.Revision, o.StateLabel, r.Action, r.ResultingRevision, res?.StateLabel, o.Markings.BannerText(), r.AddedAt, r.AddedBy));
        }

        var modify = AccessPolicy.Evaluate(subject, row.Markings, PlmAction.Modify);
        var admin = AccessPolicy.Evaluate(subject, row.Markings, PlmAction.Administer);
        bool final;
        await using (var fin = new NpgsqlCommand("SELECT chg.is_final_state(@l, @s)", conn, tx))
        {
            fin.Parameters.AddWithValue("l", row.LifecycleId);
            fin.Parameters.AddWithValue("s", row.State);
            final = (bool)(await fin.ExecuteScalarAsync(ct))!;
        }

        // ---- tasks
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var tasks = new List<TaskView>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT t.id, t.number, t.title, t.description, t.lifecycle_state, COALESCE(ls.label, t.lifecycle_state), t.assignee_sub, t.assignee_username, t.assignee_name,
                   t.due_date, t.completion_note, t.cancel_reason, t.created_at, t.completed_at
            FROM chg.change_task t
            LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = t.lifecycle_id AND ls.state = t.lifecycle_state
            WHERE t.ecn_id = @id ORDER BY t.number
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var state = r.GetString(4);
                var active = state is "Open" or "InProgress";
                var mine = r.GetString(6) == subject.Id;
                DateOnly? due = r.IsDBNull(9) ? null : DateOnly.FromDateTime(r.GetDateTime(9));
                tasks.Add(new TaskView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), state, r.GetString(5), r.GetString(7), r.GetString(8), due,
                    active && due is { } d && d < today, r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), ChangeStore.Utc(r, 12),
                    r.IsDBNull(13) ? null : ChangeStore.Utc(r, 13),
                    CanStart: state == "Open" && mine && !final,
                    CanComplete: active && (mine || admin.Permitted) && !final,
                    CanCancel: active && modify.Permitted && !final));
            }
        }

        // ---- history
        var history = new List<HistoryView>();
        await using (var cmd = new NpgsqlCommand("SELECT h.at, h.actor_name, h.action, h.detail FROM chg.history h WHERE h.change_id = @id ORDER BY h.id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) history.Add(new HistoryView(ChangeStore.Utc(r, 0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        }

        var openTasks = tasks.Count(t => t.State is "Open" or "InProgress");
        var transitions = lifecycle.Transitions.Where(t => t.From == row.State).Select(t =>
        {
            var label = lifecycle.State(t.To)!.Label;
            string? why = t.Via != "manual" ? ViaMessage(row.Type, t.Via, label)
                : !modify.Permitted ? "You may not modify this change."
                : row.Type == ChangeTypes.Ecn && t.To == "Implemented" && openTasks > 0 ? $"{openTasks} change task(s) are still open."
                : null;
            return new ChangeTransitionView(t.To, label, t.Via, why is null, why, ReasonScope(row.Type, t.To));
        }).ToList();

        return new ChangeDetail(row.Id, row.Type, row.Number, row.Title, row.Description, row.Priority, row.ChangeClass, row.Reason, row.RequesterName, row.RequesterUsername,
            row.State, lifecycle.State(row.State)?.Label ?? row.State, lifecycle.IsLocked(row.State), final, row.Markings, Markings.AggregateBanner(shown), source, derived, affected, tasks,
            history, transitions, new ChangePermissions(modify.Permitted, admin.Permitted, !lifecycle.IsLocked(row.State), modify.Reasons), row.CloseReasonCode, row.CloseReason,
            row.Submission, row.CreatedAt, row.StateChangedAt, notices, shown);
    }
}
