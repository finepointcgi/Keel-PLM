using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Process;

public sealed partial class ProcessPlanService
{
    // ---------------------------------------------------------------- check-out, working copy, check-in

    /// <summary>Opens the next iteration as the caller's private working copy (a copy of the last checked-in one). Only the latest In Work revision can be checked out.</summary>
    public Task<Outcome<PlanDetail>> CheckOutAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<PlanDetail>(subject, number, "process.plan_checkout", correlationId, async (head, mbom, conn, tx) =>
        {
            if (!head.IsLatest || head.State != PlanStates.InWork) return Step<PlanDetail>.Conflict($"Revision {head.Revision} is {PlanStates.Label(head.State).ToLowerInvariant()}; only the latest In work revision can be checked out.");
            if (head.IsOpen) return Step<PlanDetail>.Conflict($"Already checked out by {head.OpenByName ?? head.OpenBy}.");
            var next = head.LatestIteration + 1;
            await using (var ins = new NpgsqlCommand(
                """
                INSERT INTO proc.plan_iteration (id, revision_id, iteration, is_open, opened_by, opened_by_name, content, created_at, created_by) VALUES (@id, @r, @n, true, @sub, @by, @content::jsonb, @at, @by);
                UPDATE proc.plan_revision SET latest_iteration = @n WHERE id = @r
                """, conn, tx))
            {
                ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
                ins.Parameters.AddWithValue("r", head.RevisionId);
                ins.Parameters.AddWithValue("n", next);
                ins.Parameters.AddWithValue("sub", subject.Id);
                ins.Parameters.AddWithValue("by", ProcessStore.Who(subject));
                ins.Parameters.AddWithValue("content", head.ContentJson);
                ins.Parameters.AddWithValue("at", clock.GetUtcNow());
                await ins.ExecuteNonQueryAsync(ct);
            }
            await ProcessToolUse.RebuildAsync(conn, tx, head.RevisionId, next, ProcessRules.Deserialize(head.ContentJson), ct);
            var fresh = (await ProcessStore.LoadByNumberAsync(conn, tx, number, null, false, ct))!;
            return Step<PlanDetail>.Done(await BuildDetailAsync(conn, tx, subject, fresh, mbom, correlationId, ct), ("revision", $"{head.Revision}.{Fmt(next)}"));
        }, ct);

    /// <summary>The caller's working copy for editing. A step attachment the caller may not view is returned with an empty document number (the file id is kept) and is restored on save.</summary>
    public async Task<Outcome<PlanContent>> GetWorkingCopyAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        await ReadAsync<PlanContent>(subject, number, null, "process.working_read", correlationId, async (conn, tx, head, mbom) =>
        {
            if (!head.IsOpen || head.OpenBy != subject.Id) return (Outcome<PlanContent>.Conflict("The plan is not checked out by you."), []);
            var content = ProcessRules.Normalize(ProcessRules.Deserialize(head.ContentJson));
            var routings = new List<RoutingDef>();
            foreach (var r in content.Routings)
            {
                var ops = new List<OperationDef>();
                foreach (var o in r.Operations)
                {
                    var steps = new List<StepDef>();
                    foreach (var s in o.Steps)
                    {
                        var atts = new List<AttachmentDef>();
                        foreach (var a in s.Attachments)
                        {
                            var described = files is null ? null : await files.DescribeAsync(subject, a.Document, a.FileId, null, ct);
                            atts.Add(described is { IsOk: true } ? a : a with { Document = "" });
                        }
                        steps.Add(s with { Attachments = atts });
                    }
                    ops.Add(o with { Steps = steps });
                }
                routings.Add(r with { Operations = ops });
            }
            return (Outcome<PlanContent>.Ok(new PlanContent(routings)), [("revision", head.Label)]);
        }, ct);

    /// <summary>
    /// Replaces the content of the caller's working copy. Checked: structure and limits, active work centers of the plant, tools (exist, readable, not retired unless already used), attachments
    /// added now (the caller may view the document; the file exists and is not quarantined) and assignments added now (the line exists in the bound MBOM revision and the caller may read it).
    /// </summary>
    public Task<Outcome<PlanDetail>> SaveWorkingCopyAsync(Subject subject, string number, PlanContent content, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<PlanDetail>(subject, number, "process.plan_edit", correlationId, async (head, mbom, conn, tx) =>
        {
            if (!head.IsOpen || head.OpenBy != subject.Id) return Step<PlanDetail>.Conflict("Check the plan out before editing it.");
            var previous = ProcessRules.Deserialize(head.ContentJson);
            var prevAtts = previous.Routings.SelectMany(r => r.Operations).SelectMany(o => o.Steps).SelectMany(s => s.Attachments).ToDictionary(a => a.FileId, a => a);
            // Restore attachments the caller could not see (blank document number).
            var restored = new PlanContent([.. content.Routings.Select(r => r with
            {
                Operations = [.. r.Operations.Select(o => o with
                {
                    Steps = [.. o.Steps.Select(s => s with
                    {
                        Attachments = [.. s.Attachments.Select(a => string.IsNullOrEmpty(a.Document) && prevAtts.TryGetValue(a.FileId, out var old) ? a with { Document = old.Document } : a)],
                    })],
                })],
            })]);
            var normalized = ProcessRules.Normalize(restored);
            var errors = ProcessRules.Validate(normalized, await ProcessStore.ActiveWorkCentersAsync(conn, tx, head.Plant, ct)).ToList();
            var prevTools = previous.Routings.SelectMany(r => r.Operations).SelectMany(o => o.Tools.Concat(o.Steps.SelectMany(s => s.Tools))).ToHashSet();
            var toolFilter = AccessPolicy.BuildReadFilter(subject, "t");
            foreach (var id in normalized.Routings.SelectMany(r => r.Operations).SelectMany(o => o.Tools.Concat(o.Steps.SelectMany(s => s.Tools))).Distinct())
            {
                await using var cmd = new NpgsqlCommand($"SELECT t.status FROM proc.tool t WHERE t.id = @id AND {toolFilter.Sql}", conn, tx);
                foreach (var (k, v) in toolFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
                cmd.Parameters.AddWithValue("id", id);
                var status = (string?)await cmd.ExecuteScalarAsync(ct);
                if (status is null) { if (!prevTools.Contains(id)) errors.Add("A referenced tool does not exist or you may not see it."); }
                else if (status == "retired" && !prevTools.Contains(id)) errors.Add("A retired tool cannot be added to a plan.");
            }
            foreach (var a in normalized.Routings.SelectMany(r => r.Operations).SelectMany(o => o.Steps).SelectMany(s => s.Attachments).DistinctBy(a => a.FileId))
            {
                if (prevAtts.TryGetValue(a.FileId, out var prior) && prior.Document == a.Document) continue;
                if (files is null) { errors.Add("Attachments are not available."); break; }
                var described = await files.DescribeAsync(subject, a.Document, a.FileId, null, ct);
                if (!described.IsOk) { errors.Add($"Attachment file {a.FileId:D} was not found, or you may not view it."); continue; }
                if (await IsQuarantinedAsync(conn, tx, described.Value!.DocumentNumber, described.Value.Sha256, ct)) errors.Add($"The file {described.Value.FileName} is quarantined by the malware scanner.");
            }
            var prevLines = previous.Routings.SelectMany(r => r.Operations).SelectMany(o => o.Components).Select(c => (c.Find, c.Alt)).ToHashSet();
            if (normalized.Routings.SelectMany(r => r.Operations).SelectMany(o => o.Components).Any(c => !prevLines.Contains((c.Find, c.Alt))))
            {
                var lines = await mboms.LinesAsync(subject, head.MbomNumber, head.BoundRevision, correlationId, ct);
                var byKey = lines.IsOk ? lines.Value!.ToDictionary(l => (l.Find, l.Alt)) : [];
                foreach (var c in normalized.Routings.SelectMany(r => r.Operations).SelectMany(o => o.Components).Where(c => !prevLines.Contains((c.Find, c.Alt))))
                    if (!byKey.TryGetValue((c.Find, c.Alt), out var line) || line.Redacted) errors.Add($"Line {c.Find} is not a line of the bound MBOM revision that you may read.");
                    else if (line.ChildMasterId != c.PartMasterId) errors.Add($"Line {c.Find} holds a different component than the assignment names.");
            }
            if (errors.Count > 0) return Step<PlanDetail>.Invalid([.. errors.Distinct()]);
            await using (var upd = new NpgsqlCommand("UPDATE proc.plan_iteration SET content = @c::jsonb WHERE id = @i AND is_open", conn, tx))
            {
                upd.Parameters.AddWithValue("c", ProcessRules.Serialize(normalized));
                upd.Parameters.AddWithValue("i", head.IterationId);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await ProcessToolUse.RebuildAsync(conn, tx, head.RevisionId, head.LatestIteration, normalized, ct);
            var fresh = (await ProcessStore.LoadByNumberAsync(conn, tx, number, null, false, ct))!;
            var ops = normalized.Routings.SelectMany(r => r.Operations).ToList();
            return Step<PlanDetail>.Done(await BuildDetailAsync(conn, tx, subject, fresh, mbom, correlationId, ct),
                ("revision", head.Label), ("routings", Fmt(normalized.Routings.Count)), ("operations", Fmt(ops.Count)), ("steps", Fmt(ops.Sum(o => o.Steps.Count))));
        }, ct);

    /// <summary>Checks the working copy in: it becomes an immutable iteration with its digest.</summary>
    public Task<Outcome<PlanDetail>> CheckInAsync(Subject subject, string number, string? comment, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<PlanDetail>(subject, number, "process.plan_checkin", correlationId, async (head, mbom, conn, tx) =>
        {
            if (!head.IsOpen || head.OpenBy != subject.Id) return Step<PlanDetail>.Conflict("The plan is not checked out by you.");
            if (comment is { Length: > 1000 }) return Step<PlanDetail>.Invalid("The comment is at most 1000 characters.");
            var content = ProcessRules.Normalize(ProcessRules.Deserialize(head.ContentJson));
            var errors = ProcessRules.Validate(content, await ProcessStore.ActiveWorkCentersAsync(conn, tx, head.Plant, ct));
            if (errors.Count > 0) return Step<PlanDetail>.Invalid([.. errors]);
            var sha = ProcessRules.Hash(content);
            await using (var upd = new NpgsqlCommand(
                "UPDATE proc.plan_iteration SET is_open = false, opened_by = NULL, opened_by_name = NULL, content = @c::jsonb, content_sha256 = @sha, comment = @comment WHERE id = @i AND is_open", conn, tx))
            {
                upd.Parameters.AddWithValue("c", ProcessRules.Serialize(content));
                upd.Parameters.AddWithValue("sha", sha);
                upd.Parameters.Add(new NpgsqlParameter("comment", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)ProcessRules.Clean(comment) ?? DBNull.Value });
                upd.Parameters.AddWithValue("i", head.IterationId);
                await upd.ExecuteNonQueryAsync(ct);
            }
            var fresh = (await ProcessStore.LoadByNumberAsync(conn, tx, number, null, false, ct))!;
            if (notifications is not null)
                await notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.WatchedChanged, AwarenessKinds.ProcessPlan, number, head.Markings, subject.Id), ct);
            return Step<PlanDetail>.Done(await BuildDetailAsync(conn, tx, subject, fresh, mbom, correlationId, ct), ("revision", head.Label), ("contentSha256", sha));
        }, ct);

    /// <summary>Discards the working copy (the open iteration after the first). The owner or an administrator can do it.</summary>
    public Task<Outcome<PlanDetail>> UndoCheckOutAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<PlanDetail>(subject, number, "process.plan_undo_checkout", correlationId, async (head, mbom, conn, tx) =>
        {
            if (!head.IsOpen) return Step<PlanDetail>.Conflict("The plan is not checked out.");
            if (head.OpenBy != subject.Id && !AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Administer).Permitted) return Step<PlanDetail>.Hidden("Only the person who checked it out, or an administrator, can undo the check-out.", "not_owner");
            if (head.LatestIteration == 1) return Step<PlanDetail>.Conflict("This is the first iteration of the revision; there is nothing earlier to return to. Check it in instead.");
            await using (var del = new NpgsqlCommand("DELETE FROM proc.plan_iteration WHERE id = @i AND is_open; UPDATE proc.plan_revision SET latest_iteration = @n WHERE id = @r", conn, tx))
            {
                del.Parameters.AddWithValue("i", head.IterationId);
                del.Parameters.AddWithValue("n", head.LatestIteration - 1);
                del.Parameters.AddWithValue("r", head.RevisionId);
                await del.ExecuteNonQueryAsync(ct);
            }
            var fresh = (await ProcessStore.LoadByNumberAsync(conn, tx, number, null, false, ct))!;
            return Step<PlanDetail>.Done(await BuildDetailAsync(conn, tx, subject, fresh, mbom, correlationId, ct), ("discarded", head.Label), ("checkedOutBy", head.OpenBy));
        }, ct);

    // ---------------------------------------------------------------- revise and rebind

    /// <summary>Starts the next revision of a Released plan, written against the MBOM revision that is released now. The released revision stays in force until the new one is released.</summary>
    public Task<Outcome<PlanDetail>> ReviseAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<PlanDetail>(subject, number, "process.plan_revise", correlationId, async (head, mbom, conn, tx) =>
        {
            if (!head.IsLatest || head.State != PlanStates.Released) return Step<PlanDetail>.Conflict("Only the latest Released revision can be revised.");
            if (mbom.LatestReleased is null) return Step<PlanDetail>.Conflict("The MBOM has no released revision to write the new plan revision against.");
            var next = RevisionScheme.Next(head.Revision);
            var now = clock.GetUtcNow();
            var rid = Guid.CreateVersion7();
            await using (var ins = new NpgsqlCommand(
                """
                UPDATE proc.plan_revision SET is_latest = false WHERE id = @old;
                INSERT INTO proc.plan_revision (id, plan_id, revision, lifecycle_state, state_changed_at, state_changed_by, is_latest, latest_iteration, bound_mbom_revision, bound_mbom_sha, bound_at, bound_by, created_at, created_by)
                VALUES (@rid, @p, @rev, 'InWork', @at, @by, true, 1, @brev, @bsha, @at, @by, @at, @by);
                INSERT INTO proc.plan_iteration (id, revision_id, iteration, is_open, opened_by, opened_by_name, content, created_at, created_by)
                VALUES (@iid, @rid, 1, true, @sub, @by, @content::jsonb, @at, @by)
                """, conn, tx))
            {
                ins.Parameters.AddWithValue("old", head.RevisionId);
                ins.Parameters.AddWithValue("rid", rid);
                ins.Parameters.AddWithValue("p", head.Id);
                ins.Parameters.AddWithValue("rev", next);
                ins.Parameters.AddWithValue("at", now);
                ins.Parameters.AddWithValue("by", ProcessStore.Who(subject));
                ins.Parameters.AddWithValue("brev", mbom.LatestReleased.Revision);
                ins.Parameters.AddWithValue("bsha", mbom.LatestReleased.ContentSha256);
                ins.Parameters.AddWithValue("iid", Guid.CreateVersion7());
                ins.Parameters.AddWithValue("sub", subject.Id);
                ins.Parameters.AddWithValue("content", head.ContentJson);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await ProcessToolUse.RebuildAsync(conn, tx, rid, 1, ProcessRules.Deserialize(head.ContentJson), ct);
            var fresh = (await ProcessStore.LoadByNumberAsync(conn, tx, number, null, false, ct))!;
            return Step<PlanDetail>.Done(await BuildDetailAsync(conn, tx, subject, fresh, mbom, correlationId, ct), ("from", head.Revision), ("to", next), ("mbomRevision", mbom.LatestReleased.Revision));
        }, ct);

    /// <summary>Points the working revision at the MBOM revision released now. Nothing in the content changes; findings then show which assignments need attention.</summary>
    public Task<Outcome<PlanDetail>> RebindAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<PlanDetail>(subject, number, "process.plan_rebind", correlationId, async (head, mbom, conn, tx) =>
        {
            if (!head.IsOpen || head.OpenBy != subject.Id) return Step<PlanDetail>.Conflict("Check the plan out before binding it to another MBOM revision.");
            if (mbom.LatestReleased is null) return Step<PlanDetail>.Conflict("The MBOM has no released revision.");
            await using (var upd = new NpgsqlCommand(
                "UPDATE proc.plan_revision SET bound_mbom_revision = @rev, bound_mbom_sha = @sha, bound_at = @at, bound_by = @by WHERE id = @r", conn, tx))
            {
                upd.Parameters.AddWithValue("rev", mbom.LatestReleased.Revision);
                upd.Parameters.AddWithValue("sha", mbom.LatestReleased.ContentSha256);
                upd.Parameters.AddWithValue("at", clock.GetUtcNow());
                upd.Parameters.AddWithValue("by", ProcessStore.Who(subject));
                upd.Parameters.AddWithValue("r", head.RevisionId);
                await upd.ExecuteNonQueryAsync(ct);
            }
            var fresh = (await ProcessStore.LoadByNumberAsync(conn, tx, number, null, false, ct))!;
            return Step<PlanDetail>.Done(await BuildDetailAsync(conn, tx, subject, fresh, mbom, correlationId, ct), ("from", head.BoundRevision), ("to", mbom.LatestReleased.Revision));
        }, ct);

    // ---------------------------------------------------------------- shop floor view

    /// <summary>
    /// The read-only view for the shop floor: the latest Released revision (or a named Released or Obsolete one), one routing (the primary by default), resolved under the reader's own access.
    /// What the reader may not see is a neutral placeholder; the banner covers the plan and everything shown.
    /// </summary>
    public async Task<Outcome<ShopFloorView>> GetShopFloorAsync(Subject subject, string number, string? revision, string? routingCode, string? correlationId, CancellationToken ct = default)
    {
        string? resolvedRevision = revision;
        if (resolvedRevision is null)
        {
            await using var c = await dataSource.OpenConnectionAsync(ct);
            await using var q = new NpgsqlCommand(
                "SELECT r.revision FROM proc.plan_revision r JOIN proc.plan p ON p.id = r.plan_id WHERE p.number = @n AND r.lifecycle_state = 'Released' ORDER BY length(r.revision) DESC, r.revision DESC LIMIT 1", c);
            q.Parameters.AddWithValue("n", number);
            resolvedRevision = (string?)await q.ExecuteScalarAsync(ct);
            if (resolvedRevision is null)
            {
                // No released revision: answer like an unreadable plan unless the reader may read it (then say so).
                var detail = await GetAsync(subject, number, null, correlationId, ct);
                return detail.IsOk ? Outcome<ShopFloorView>.Conflict("This plan has no released revision, so there is no shop floor view yet.") : Outcome<ShopFloorView>.NotFound();
            }
        }
        return await ReadAsync<ShopFloorView>(subject, number, resolvedRevision, "process.shopfloor_read", correlationId, async (conn, tx, head, mbom) =>
        {
            if (head.State is not (PlanStates.Released or PlanStates.Obsolete)) return (Outcome<ShopFloorView>.Conflict("Only a released revision is shown on the shop floor."), []);
            var content = ProcessRules.Normalize(ProcessRules.Deserialize(head.ContentJson));
            var chosen = routingCode is null ? content.Routings.FirstOrDefault(r => r.IsPrimary) : content.Routings.FirstOrDefault(r => r.Code == routingCode);
            if (chosen is null) return (Outcome<ShopFloorView>.NotFound(), []);
            var lines = await mboms.LinesAsync(subject, head.MbomNumber, head.BoundRevision, correlationId, ct);
            var seen = new List<Markings> { head.Markings, mbom.Markings };
            var views = await BuildViewAsync(conn, tx, subject, head, new PlanContent([chosen]), lines.IsOk ? lines.Value! : [], ct, seen);
            var stale = ProcessRules.StalenessOf(head.BoundRevision, head.BoundSha, mbom.LatestReleased);
            var banner = Markings.AggregateBanner(seen.Distinct());
            var view = new ShopFloorView(head.Number, head.PartNumber, head.PartName, head.Plant, head.Revision, head.BoundRevision, stale, mbom.LatestReleased?.Revision, banner, views[0],
                [.. content.Routings.Where(r => r.Code != chosen.Code).Select(r => r.Code)], clock.GetUtcNow());
            return (Outcome<ShopFloorView>.Ok(view), [("revision", head.Revision), ("routing", chosen.Code), ("banner", banner)]);
        }, ct);
    }
}
