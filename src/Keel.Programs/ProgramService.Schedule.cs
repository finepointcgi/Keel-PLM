using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Programs;

public sealed partial class ProgramService
{
    // ---------------------------------------------------------------- item rules

    /// <summary>Read access to an item is the policy on the item's own markings; a hidden item is "not found". Modify needs the policy too.</summary>
    private static PStep? RequireItem(Subject s, IHead? item, PlmAction permission)
    {
        if (item is null || !AccessPolicy.Evaluate(s, item.Markings, PlmAction.Read).Permitted) return PStep.Missing();
        if (permission == PlmAction.Read) return null;
        var d = AccessPolicy.Evaluate(s, item.Markings, permission);
        return d.Permitted ? null : PStep.Forbid([.. d.Reasons]);
    }

    private static async Task<(DirectoryUser? Owner, string? Error)> ResolveOwnerAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string? login, Markings markings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(login)) return (null, null);
        var u = await ProgramStore.FindUserAsync(conn, tx, login.Trim(), ct);
        if (u is null) return (null, $"The user '{login}' was not found.");
        if (!AccessPolicy.Evaluate(u.Subject, markings, PlmAction.Read).Permitted)
            return (null, $"{u.DisplayName} could not read this item. Membership of the program comes from the identity provider; it is not granted here.");
        return (u, null);
    }

    private static string? ValidateText(string? title, string? description)
    {
        if ((title?.Trim().Length ?? 0) is 0 or > 200) return "Title is required (at most 200 characters).";
        if ((description?.Length ?? 0) > 4000) return "The description must be at most 4000 characters.";
        return null;
    }

    // ---------------------------------------------------------------- add, update, progress

    public Task<Outcome<ItemRow>> AddItemAsync(Subject subject, string code, AddItemCommand cmd, string? corr, CancellationToken ct = default)
    {
        string? newKey = null;
        return MutateAsync<ItemRow>(subject, code, "program.item_add", corr, PlmAction.Modify, false, async (head, conn, tx, now) =>
        {
            var errors = new List<string>();
            if (!ProgramKinds.Items.Contains(cmd.Kind)) errors.Add($"Kind must be one of: {string.Join(", ", ProgramKinds.Items)}.");
            if (ValidateText(cmd.Title, cmd.Description) is { } te) errors.Add(te);
            var end = cmd.Kind == ProgramKinds.Milestone ? cmd.Start : cmd.End;
            if (cmd.Start > end) errors.Add("The start date must not be after the end date.");
            if (cmd.GateType is not null && (cmd.Kind != ProgramKinds.Milestone || !GateTypes.All.Contains(cmd.GateType))) errors.Add("Only a milestone can be a gate review, and its type must be one of: " + string.Join(", ", GateTypes.All) + ".");
            if (errors.Count > 0) return PStep.Invalid([.. errors]);
            var markings = cmd.Markings ?? head.Markings;
            var gaps = MarkingDominance.Gaps(markings, head.Markings);
            if (gaps.Count > 0) return PStep.Invalid($"The markings of an item must cover those of the program. {MarkingDominance.Describe(gaps)}");
            var modify = AccessPolicy.Evaluate(subject, markings, PlmAction.Modify);
            if (!modify.Permitted) return PStep.Forbid([.. modify.Reasons]);
            Guid? parentId = null;
            if (!string.IsNullOrWhiteSpace(cmd.ParentKey))
            {
                var parent = await ProgramStore.LoadItemAsync(conn, tx, head.Id, cmd.ParentKey.Trim(), false, ct);
                if (parent is null || !AccessPolicy.Evaluate(subject, parent.Markings, PlmAction.Read).Permitted || parent.Kind != ProgramKinds.Phase)
                    return PStep.Invalid($"There is no phase '{cmd.ParentKey}' you can use.");
                parentId = parent.Id;
            }
            var (owner, ownerError) = await ResolveOwnerAsync(conn, tx, cmd.OwnerLogin, markings, ct);
            if (ownerError is not null) return PStep.Invalid(ownerError);
            long no;
            await using (var n = ProgramStore.Cmd(conn, tx, "UPDATE prg.program SET next_item_no = next_item_no + 1 WHERE id = @id RETURNING next_item_no - 1"))
            {
                n.Parameters.AddWithValue("id", head.Id);
                no = Convert.ToInt64(await n.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }
            var key = $"{ProgramKinds.Prefix(cmd.Kind)}-{no.ToString("D3", CultureInfo.InvariantCulture)}";
            string? gateNumber = null;
            if (cmd.GateType is not null)
            {
                await using var g = ProgramStore.Cmd(conn, tx, "SELECT nextval('prg.gate_seq')");
                gateNumber = $"GR-{Convert.ToInt64(await g.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture).ToString("D6", CultureInfo.InvariantCulture)}";
            }
            long sort;
            await using (var so = ProgramStore.Cmd(conn, tx, "SELECT COALESCE(max(sort_order), 0) + 10 FROM prg.item WHERE program_id = @p"))
            {
                so.Parameters.AddWithValue("p", head.Id);
                sort = Convert.ToInt64(await so.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }
            await using var ins = ProgramStore.Cmd(conn, tx,
                $"""
                INSERT INTO prg.item (id, program_id, key, kind, parent_id, sort_order, title, description, owner_sub, owner_username, owner_name, planned_start, planned_end,
                                      gate_type, gate_number, gate_state, {MarkingColumns.InsertColumns}, created_at, created_by, updated_at, updated_by)
                VALUES (@id, @p, @key, @kind, @parent, @sort, @title, @desc, @osub, @ouser, @oname, @start, @end, @gtype, @gnum, @gstate, {MarkingColumns.InsertValues}, @at, @by, @at, @by)
                """);
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("p", head.Id);
            ins.Parameters.AddWithValue("key", key);
            ins.Parameters.AddWithValue("kind", cmd.Kind);
            ins.Parameters.AddWithValue("parent", (object?)parentId ?? DBNull.Value);
            ins.Parameters.AddWithValue("sort", (int)sort);
            ins.Parameters.AddWithValue("title", cmd.Title.Trim());
            ins.Parameters.AddWithValue("desc", cmd.Description?.Trim() ?? "");
            ins.Parameters.AddWithValue("osub", (object?)owner?.Sub ?? DBNull.Value);
            ins.Parameters.AddWithValue("ouser", (object?)owner?.Username ?? DBNull.Value);
            ins.Parameters.AddWithValue("oname", (object?)owner?.DisplayName ?? DBNull.Value);
            ins.Parameters.AddWithValue("start", cmd.Start.ToDateTime(TimeOnly.MinValue));
            ins.Parameters.AddWithValue("end", end.ToDateTime(TimeOnly.MinValue));
            ins.Parameters.AddWithValue("gtype", (object?)cmd.GateType ?? DBNull.Value);
            ins.Parameters.AddWithValue("gnum", (object?)gateNumber ?? DBNull.Value);
            ins.Parameters.AddWithValue("gstate", gateNumber is null ? DBNull.Value : GateStates.Draft);
            foreach (var (k, v) in MarkingColumns.Parameters(markings)) ins.Parameters.AddWithValue(k, v);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", ProgramStore.Who(subject));
            await ins.ExecuteNonQueryAsync(ct);
            newKey = key;
            return PStep.Done(("key", key), ("kind", cmd.Kind), ("gate", gateNumber), ("markings", markings.BannerText()));
        }, (conn, tx, head) => LoadItemRowAsync(conn, tx, subject, head, newKey!, ct), ct);
    }

    public Task<Outcome<ItemRow>> UpdateItemAsync(Subject subject, string code, string key, UpdateItemCommand cmd, string? corr, CancellationToken ct = default) =>
        MutateAsync<ItemRow>(subject, code, "program.item_update", corr, PlmAction.Read, false, async (head, conn, tx, now) =>
        {
            var item = await ProgramStore.LoadItemAsync(conn, tx, head.Id, key, true, ct);
            if (RequireItem(subject, item, PlmAction.Modify) is { } denied) return denied;
            if (ValidateText(cmd.Title, cmd.Description) is { } te) return PStep.Invalid(te);
            if (item!.Status == ItemStatuses.Complete) return PStep.Fail($"{item.Key} is complete. Reopen it before changing it.");
            if (item.GateState is GateStates.UnderReview or GateStates.Approved) return PStep.Fail($"Gate review {item.GateNumber} is {GateStates.Label(item.GateState).ToLowerInvariant()} and is frozen.");
            var end = item.Kind == ProgramKinds.Milestone ? cmd.Start : cmd.End;
            if (cmd.Start > end) return PStep.Invalid("The start date must not be after the end date.");
            var slip = end.DayNumber - item.PlannedEnd.DayNumber;
            var moved = cmd.Start != item.PlannedStart || end != item.PlannedEnd;
            var reason = cmd.Reason?.Trim() ?? "";
            if (slip > 0 && reason.Length == 0) return PStep.Invalid($"Moving the end date {slip} day(s) later is a slip: say why.");
            if (reason.Length > 1000) return PStep.Invalid("The reason must be at most 1000 characters.");
            var (owner, ownerError) = await ResolveOwnerAsync(conn, tx, cmd.OwnerLogin, item.Markings, ct);
            if (ownerError is not null) return PStep.Invalid(ownerError);
            await using (var up = ProgramStore.Cmd(conn, tx,
                "UPDATE prg.item SET title = @title, description = @desc, owner_sub = @osub, owner_username = @ouser, owner_name = @oname, planned_start = @start, planned_end = @end, updated_at = @at, updated_by = @by WHERE id = @id"))
            {
                up.Parameters.AddWithValue("title", cmd.Title.Trim());
                up.Parameters.AddWithValue("desc", cmd.Description?.Trim() ?? "");
                up.Parameters.AddWithValue("osub", (object?)owner?.Sub ?? DBNull.Value);
                up.Parameters.AddWithValue("ouser", (object?)owner?.Username ?? DBNull.Value);
                up.Parameters.AddWithValue("oname", (object?)owner?.DisplayName ?? DBNull.Value);
                up.Parameters.AddWithValue("start", cmd.Start.ToDateTime(TimeOnly.MinValue));
                up.Parameters.AddWithValue("end", end.ToDateTime(TimeOnly.MinValue));
                up.Parameters.AddWithValue("at", now);
                up.Parameters.AddWithValue("by", ProgramStore.Who(subject));
                up.Parameters.AddWithValue("id", item.Id);
                await up.ExecuteNonQueryAsync(ct);
            }
            if (moved)
            {
                await using var log = ProgramStore.Cmd(conn, tx,
                    "INSERT INTO prg.schedule_change (program_id, item_id, at, actor_sub, actor_name, old_start, old_end, new_start, new_end, slip_days, reason) VALUES (@p, @i, @at, @sub, @name, @os, @oe, @ns, @ne, @slip, @reason)");
                log.Parameters.AddWithValue("p", head.Id);
                log.Parameters.AddWithValue("i", item.Id);
                log.Parameters.AddWithValue("at", now);
                log.Parameters.AddWithValue("sub", subject.Id);
                log.Parameters.AddWithValue("name", subject.DisplayName);
                log.Parameters.AddWithValue("os", item.PlannedStart.ToDateTime(TimeOnly.MinValue));
                log.Parameters.AddWithValue("oe", item.PlannedEnd.ToDateTime(TimeOnly.MinValue));
                log.Parameters.AddWithValue("ns", cmd.Start.ToDateTime(TimeOnly.MinValue));
                log.Parameters.AddWithValue("ne", end.ToDateTime(TimeOnly.MinValue));
                log.Parameters.AddWithValue("slip", slip);
                log.Parameters.AddWithValue("reason", reason);
                await log.ExecuteNonQueryAsync(ct);
            }
            return PStep.Done(("key", item.Key), ("slipDays", ProgramStore.Int(slip)), ("moved", moved ? "yes" : "no"));
        }, (conn, tx, head) => LoadItemRowAsync(conn, tx, subject, head, key, ct), ct);

    public Task<Outcome<ItemRow>> SetProgressAsync(Subject subject, string code, string key, SetProgressCommand cmd, string? corr, CancellationToken ct = default)
    {
        return MutateAsync<ItemRow>(subject, code, "program.item_progress", corr, PlmAction.Read, false, async (head, conn, tx, now) =>
        {
            var item = await ProgramStore.LoadItemAsync(conn, tx, head.Id, key, true, ct);
            if (RequireItem(subject, item, PlmAction.Modify) is { } denied) return denied;
            if (!ItemStatuses.All.Contains(cmd.Status)) return PStep.Invalid($"Status must be one of: {string.Join(", ", ItemStatuses.All)}.");
            if (cmd.Percent is < 0 or > 100) return PStep.Invalid("Percent complete must be between 0 and 100.");
            if (item!.GateNumber is not null && cmd.Status == ItemStatuses.Complete) return PStep.Fail($"Gate review {item.GateNumber} is completed by its signed approval, not by hand.");
            if (item.GateState is GateStates.UnderReview) return PStep.Fail($"Gate review {item.GateNumber} is under review and is frozen.");
            var today = Today;
            var percent = cmd.Status switch
            {
                ItemStatuses.Complete => 100,
                ItemStatuses.NotStarted => 0,
                _ => item.Kind == ProgramKinds.Milestone || item.Kind == ProgramKinds.Phase ? 0 : Math.Min(cmd.Percent, 99),
            };
            DateOnly? aStart = cmd.Status == ItemStatuses.NotStarted ? null : cmd.ActualStart ?? (await StoredActualAsync(conn, tx, item.Id, ct)).Start ?? (cmd.Status is ItemStatuses.InProgress or ItemStatuses.Complete ? today : null);
            DateOnly? aEnd = cmd.Status == ItemStatuses.Complete ? cmd.ActualEnd ?? today : null;
            if (aStart is { } a && aEnd is { } b && a > b) return PStep.Invalid("The actual start must not be after the actual end.");
            await using (var up = ProgramStore.Cmd(conn, tx, "UPDATE prg.item SET status = @st, percent_complete = @pc, actual_start = @as, actual_end = @ae, updated_at = @at, updated_by = @by WHERE id = @id"))
            {
                up.Parameters.AddWithValue("st", cmd.Status);
                up.Parameters.AddWithValue("pc", percent);
                up.Parameters.AddWithValue("as", ProgramStore.OptDate(aStart));
                up.Parameters.AddWithValue("ae", ProgramStore.OptDate(aEnd));
                up.Parameters.AddWithValue("at", now);
                up.Parameters.AddWithValue("by", ProgramStore.Who(subject));
                up.Parameters.AddWithValue("id", item.Id);
                await up.ExecuteNonQueryAsync(ct);
            }
            if (cmd.Status == ItemStatuses.Complete && item.Kind == ProgramKinds.Milestone && item.Status != ItemStatuses.Complete)
                await EmitMilestoneReachedAsync(conn, tx, head, item, aEnd!.Value, ProgramStore.Who(subject), corr, now, ct);
            return PStep.Done(("key", item.Key), ("from", item.Status), ("to", cmd.Status), ("percent", ProgramStore.Int(percent)));
        }, (conn, tx, head) => LoadItemRowAsync(conn, tx, subject, head, key, ct), ct);
    }

    private static async Task<(DateOnly? Start, DateOnly? End)> StoredActualAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var c = ProgramStore.Cmd(conn, tx, "SELECT actual_start, actual_end FROM prg.item WHERE id = @id");
        c.Parameters.AddWithValue("id", id);
        await using var r = await c.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (ProgramStore.DateAt(r, 0), ProgramStore.DateAt(r, 1));
    }

    /// <summary>Writes the additive <c>program.milestone_reached</c> outbox event (markings of the milestone) in the caller's transaction.</summary>
    internal async Task EmitMilestoneReachedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, PHead program, IHead item, DateOnly actual, string actor, string? corr, DateTimeOffset now, CancellationToken ct)
    {
        if (_events is null) return;
        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["program"] = program.Code,
            ["milestone"] = new System.Text.Json.Nodes.JsonObject
            {
                ["key"] = item.Key, ["name"] = item.Title, ["gateType"] = item.GateType, ["gateNumber"] = item.GateNumber,
                ["plannedDate"] = ScheduleMath.Fmt(item.PlannedEnd), ["actualDate"] = ScheduleMath.Fmt(actual),
            },
            ["reachedAt"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };
        await _events.AppendAsync(conn, tx, new DomainEvent(EventTypes.ProgramMilestoneReached, ProgramStore.ObjectType, program.Id, program.Code, null, item.Markings, payload, now, actor, corr), ct);
    }

    // ---------------------------------------------------------------- dependencies

    public Task<Outcome<ScheduleView>> AddDependencyAsync(Subject subject, string code, string predecessorKey, string successorKey, int lagDays, string? corr, CancellationToken ct = default) =>
        MutateAsync<ScheduleView>(subject, code, "program.dependency_add", corr, PlmAction.Read, false, async (head, conn, tx, now) =>
        {
            if (lagDays is < -365 or > 365) return PStep.Invalid("The lag must be between -365 and 365 days.");
            var p = await ProgramStore.LoadItemAsync(conn, tx, head.Id, predecessorKey?.Trim() ?? "", false, ct);
            var s = await ProgramStore.LoadItemAsync(conn, tx, head.Id, successorKey?.Trim() ?? "", false, ct);
            if (RequireItem(subject, p, PlmAction.Read) is not null || RequireItem(subject, s, PlmAction.Read) is not null) return PStep.Invalid("Both items must exist and be readable by you.");
            if (RequireItem(subject, s, PlmAction.Modify) is { } denied) return denied;
            if (p!.Kind == ProgramKinds.Phase || s!.Kind == ProgramKinds.Phase) return PStep.Invalid("Dependencies join tasks and milestones, not phases.");
            var all = await SystemLinksAsync(conn, tx, head.Id, ct);
            if (ScheduleMath.WouldCreateCycle(all, p.Id, s.Id)) return PStep.Invalid("That dependency would make the schedule circular.");
            await using var ins = ProgramStore.Cmd(conn, tx, "INSERT INTO prg.dependency (id, program_id, predecessor_id, successor_id, lag_days, created_at, created_by) VALUES (@id, @p, @a, @b, @lag, @at, @by)");
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("p", head.Id);
            ins.Parameters.AddWithValue("a", p.Id);
            ins.Parameters.AddWithValue("b", s.Id);
            ins.Parameters.AddWithValue("lag", lagDays);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", ProgramStore.Who(subject));
            await ins.ExecuteNonQueryAsync(ct);
            return PStep.Done(("from", p.Key), ("to", s.Key), ("lagDays", ProgramStore.Int(lagDays)));
        }, async (conn, tx, head) => await LoadScheduleAsync(conn, tx, subject, head, null, null, ct), ct);

    public Task<Outcome<ScheduleView>> RemoveDependencyAsync(Subject subject, string code, string predecessorKey, string successorKey, string? corr, CancellationToken ct = default) =>
        MutateAsync<ScheduleView>(subject, code, "program.dependency_remove", corr, PlmAction.Read, false, async (head, conn, tx, now) =>
        {
            var p = await ProgramStore.LoadItemAsync(conn, tx, head.Id, predecessorKey?.Trim() ?? "", false, ct);
            var s = await ProgramStore.LoadItemAsync(conn, tx, head.Id, successorKey?.Trim() ?? "", false, ct);
            if (RequireItem(subject, p, PlmAction.Read) is not null || RequireItem(subject, s, PlmAction.Read) is not null) return PStep.Missing();
            if (RequireItem(subject, s, PlmAction.Modify) is { } denied) return denied;
            await using var del = ProgramStore.Cmd(conn, tx, "DELETE FROM prg.dependency WHERE predecessor_id = @a AND successor_id = @b");
            del.Parameters.AddWithValue("a", p!.Id);
            del.Parameters.AddWithValue("b", s!.Id);
            if (await del.ExecuteNonQueryAsync(ct) == 0) return PStep.Missing();
            return PStep.Done(("from", p.Key), ("to", s.Key));
        }, async (conn, tx, head) => await LoadScheduleAsync(conn, tx, subject, head, null, null, ct), ct);

    private static async Task<List<ScheduleLink>> SystemLinksAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid programId, CancellationToken ct)
    {
        var list = new List<ScheduleLink>();
        await using var c = ProgramStore.Cmd(conn, tx, "SELECT predecessor_id, successor_id, lag_days FROM prg.dependency WHERE program_id = @p");
        c.Parameters.AddWithValue("p", programId);
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ScheduleLink(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2)));
        return list;
    }

    // ---------------------------------------------------------------- baselines

    public Task<Outcome<BaselineInfo>> CreateBaselineAsync(Subject subject, string code, string name, string? corr, CancellationToken ct = default) =>
        MutateAsync<BaselineInfo>(subject, code, "program.baseline_create", corr, PlmAction.Modify, false, async (head, conn, tx, now) =>
        {
            var n = name?.Trim() ?? "";
            if (n.Length is 0 or > 200) return PStep.Invalid("Name is required (at most 200 characters).");
            long seq;
            await using (var c = ProgramStore.Cmd(conn, tx, "UPDATE prg.program SET next_baseline_no = next_baseline_no + 1 WHERE id = @id RETURNING next_baseline_no - 1"))
            {
                c.Parameters.AddWithValue("id", head.Id);
                seq = Convert.ToInt64(await c.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }
            var id = Guid.CreateVersion7();
            // The snapshot is the system's view of every item's planned dates (no text); a reader sees a baseline date only through an item they can read.
            await using (var items = ProgramStore.Cmd(conn, tx,
                "INSERT INTO prg.baseline_item (baseline_id, item_id, planned_start, planned_end) SELECT @b, i.id, i.planned_start, i.planned_end FROM prg.item i WHERE i.program_id = @p AND i.status <> 'Cancelled'"))
            {
                items.Parameters.AddWithValue("b", id);
                items.Parameters.AddWithValue("p", head.Id);
                // the baseline row must exist first (foreign key): insert it with a provisional count, which the immutable row cannot be updated for, so count first
                await using var count = ProgramStore.Cmd(conn, tx, "SELECT count(*) FROM prg.item WHERE program_id = @p AND status <> 'Cancelled'");
                count.Parameters.AddWithValue("p", head.Id);
                var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
                await using var ins = ProgramStore.Cmd(conn, tx, "INSERT INTO prg.baseline (id, program_id, seq, name, created_at, created_by, item_count) VALUES (@id, @p, @seq, @name, @at, @by, @n)");
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("p", head.Id);
                ins.Parameters.AddWithValue("seq", (int)seq);
                ins.Parameters.AddWithValue("name", n);
                ins.Parameters.AddWithValue("at", now);
                ins.Parameters.AddWithValue("by", ProgramStore.Who(subject));
                ins.Parameters.AddWithValue("n", total);
                await ins.ExecuteNonQueryAsync(ct);
                await items.ExecuteNonQueryAsync(ct);
            }
            return PStep.Done(("baseline", ProgramStore.Int(seq)));
        }, async (conn, tx, head) => (await BaselinesAsync(conn, tx, head.Id, ct)).Last(), ct);

    private static async Task<List<BaselineInfo>> BaselinesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid programId, CancellationToken ct)
    {
        var list = new List<BaselineInfo>();
        await using var c = ProgramStore.Cmd(conn, tx, "SELECT seq, name, created_at, created_by, item_count FROM prg.baseline WHERE program_id = @p ORDER BY seq");
        c.Parameters.AddWithValue("p", programId);
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new BaselineInfo(r.GetInt32(0), r.GetString(1), ProgramStore.Utc(r, 2), r.GetString(3), r.GetInt32(4)));
        return list;
    }

    // ---------------------------------------------------------------- schedule view

    public Task<Outcome<ScheduleView>> GetScheduleAsync(Subject subject, string code, int? baselineSeq, DateOnly? asOf, string? corr, CancellationToken ct = default) =>
        ReadAsync(subject, code, "program.schedule", corr, (conn, tx, head) => LoadScheduleAsync(conn, tx, subject, head, baselineSeq, asOf, ct),
            v => [("items", ProgramStore.Int(v.Items.Count))], ct);

    private async Task<ItemRow?> LoadItemRowAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, PHead head, string key, CancellationToken ct)
    {
        var view = await LoadScheduleAsync(conn, tx, subject, head, null, null, ct);
        return view.Items.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    internal sealed record RawItem(ScheduleItem Core, string Title, string Description, string? OwnerLogin, string? OwnerName, int SortOrder, string? GateType, string? GateNumber, string? GateState, Markings Markings);

    internal async Task<ScheduleView> LoadScheduleAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, PHead head, int? baselineSeq, DateOnly? asOfDate, CancellationToken ct)
    {
        var asOf = asOfDate ?? Today;
        var filter = AccessPolicy.BuildReadFilter(subject, "i");
        var raw = new List<RawItem>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT i.id, i.key, i.kind, i.parent_id, i.planned_start, i.planned_end, i.actual_start, i.actual_end, i.status, i.percent_complete,
                   i.title, i.description, i.owner_username, i.owner_name, i.sort_order, i.gate_type, i.gate_number, i.gate_state, {MarkingColumns.Select("i")}
            FROM prg.item i WHERE i.program_id = @p AND {filter.Sql}
            ORDER BY i.sort_order, i.planned_start, i.key
            """, conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("p", head.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                raw.Add(new RawItem(new ScheduleItem(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetGuid(3), ProgramStore.Date(r, 4), ProgramStore.Date(r, 5),
                    ProgramStore.DateAt(r, 6), ProgramStore.DateAt(r, 7), r.GetString(8), r.GetInt32(9)), r.GetString(10), r.GetString(11), ProgramStore.Str(r, 12), ProgramStore.Str(r, 13), r.GetInt32(14),
                    ProgramStore.Str(r, 15), ProgramStore.Str(r, 16), ProgramStore.Str(r, 17), MarkingColumns.Read(r, 18)));
        }
        var restricted = new List<RestrictedItemRow>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT i.sort_order, i.planned_start, i.planned_end FROM prg.item i WHERE i.program_id = @p AND NOT COALESCE(({filter.Sql}), false) ORDER BY i.sort_order, i.planned_start", conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("p", head.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) restricted.Add(new RestrictedItemRow(r.GetInt32(0), ProgramStore.Date(r, 1), ProgramStore.Date(r, 2)));
        }
        var readable = raw.ToDictionary(x => x.Core.Id);
        // Dependencies are child rows: shown only when both ends are readable by the caller; computed on what the caller can read.
        var links = (await SystemLinksAsync(conn, tx, head.Id, ct)).Where(l => readable.ContainsKey(l.Predecessor) && readable.ContainsKey(l.Successor)).ToList();
        var core = raw.Select(x => x.Core).ToList();
        var result = ScheduleMath.Compute(core, links, asOf);
        var (rollup, overall) = ScheduleMath.Rollup(core);

        var baselines = await BaselinesAsync(conn, tx, head.Id, ct);
        var compared = baselineSeq is { } s ? baselines.FirstOrDefault(b => b.Seq == s) : baselines.LastOrDefault();
        var baselineEnd = new Dictionary<Guid, DateOnly>();
        if (compared is not null)
        {
            await using var c = ProgramStore.Cmd(conn, tx, "SELECT bi.item_id, bi.planned_end FROM prg.baseline_item bi JOIN prg.baseline b ON b.id = bi.baseline_id WHERE b.program_id = @p AND b.seq = @s");
            c.Parameters.AddWithValue("p", head.Id);
            c.Parameters.AddWithValue("s", compared.Seq);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) baselineEnd[r.GetGuid(0)] = ProgramStore.Date(r, 1);
        }
        var hasPred = links.Select(l => l.Successor).ToHashSet();
        var rows = new List<ItemRow>();
        foreach (var x in raw)
        {
            var i = x.Core;
            result.Forecast.TryGetValue(i.Id, out var f);
            var parentKey = i.ParentId is { } pid && readable.TryGetValue(pid, out var par) ? par.Core.Key : null;
            DateOnly? bEnd = baselineEnd.TryGetValue(i.Id, out var be) ? be : null;
            var percent = i.Kind == ProgramKinds.Phase && rollup.TryGetValue(i.Id, out var rp) && rp is { } rv ? rv : i.Percent;
            rows.Add(new ItemRow(i.Id, i.Key, i.Kind, parentKey, x.SortOrder, x.Title, x.Description, x.OwnerLogin, x.OwnerName, i.PlannedStart, i.PlannedEnd, i.ActualStart, i.ActualEnd,
                i.Status, percent, x.GateType, x.GateNumber, x.GateState, f?.EarlyStart, f?.EarlyFinish, f?.FloatDays, f?.Critical ?? false, f?.SlipDays ?? 0, bEnd,
                bEnd is { } b2 ? ScheduleMath.BaselineSlip(b2, i.PlannedEnd) : null, ScheduleMath.IsOverdue(i, asOf), AccessPolicy.Evaluate(subject, x.Markings, PlmAction.Modify).Permitted,
                x.Markings, hasPred.Contains(i.Id)));
        }
        var keys = result.CriticalPath.Select(id => readable[id].Core.Key).ToList();
        var deps = links.Select(l => new DependencyRow(readable[l.Predecessor].Core.Key, readable[l.Successor].Core.Key, l.LagDays)).OrderBy(d => d.PredecessorKey, StringComparer.Ordinal).ThenBy(d => d.SuccessorKey, StringComparer.Ordinal).ToList();
        return new ScheduleView(head.Code, asOf, rows, restricted, deps, keys, result.Finish, overall, baselines, compared, result.Cyclic, Markings.AggregateBanner(rows.Select(r => r.Markings).Append(head.Markings)));
    }
}
