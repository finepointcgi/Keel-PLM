using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Change;

public sealed partial class ChangeService
{
    // ---------------------------------------------------------------- affected and resulting objects

    /// <summary>
    /// Adds an object (the latest revision, or the revision named) to a change that can still be edited. The caller needs Read and Modify on
    /// the object, so nobody lists, or touches, what they cannot see. On an ECN, <c>revise</c> creates the next revision in work, bound to the
    /// notice (ADR-0019); <c>obsolete</c> reserves the revision for retirement at release. The change's markings must cover the object's.
    /// </summary>
    public Task<Outcome<ChangeDetail>> AddAffectedAsync(Subject subject, string number, AddAffectedCommand cmd, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, "change.add_affected", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (!c.Editable) return Frozen(c);
            var notices = new List<string>();
            var step = await AddAffectedCoreAsync(conn, tx, subject, c.Head, cmd, c.Now, notices, ct);
            if (step.Kind == OutcomeKind.Ok) step.Notices.AddRange(notices);
            return step;
        }, ct);

    /// <summary>
    /// Takes an object off a change that can still be edited. Rows are never deleted (history); the resulting revision of a <c>revise</c>
    /// row is abandoned (Obsolete, the revised revision is the latest again) unless somebody already worked on it.
    /// </summary>
    public Task<Outcome<ChangeDetail>> RemoveAffectedAsync(Subject subject, string number, Guid affectedId, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, "change.remove_affected", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            if (!c.Editable) return Frozen(c);
            var row = (await ChangeStore.LoadAffectedAsync(conn, tx, c.Head.Id, ct)).FirstOrDefault(r => r.Id == affectedId);
            if (row is null) return Fail($"{c.Head.Number} does not list that object.");
            string? discarded = null;
            if (row.ResultingRevisionId is { } resultingId)
            {
                var gateway = GatewayFor(row.ObjectType) ?? throw new InvalidOperationException($"No gateway for {row.ObjectType}.");
                var resulting = await gateway.FindByRevisionAsync(conn, tx, resultingId, forUpdate: true, ct);
                if (resulting is not null)
                {
                    if (resulting.LatestIteration > 1 || resulting.CheckedOutBy is not null)
                        return Fail($"Revision {resulting.Revision} of {resulting.Key} was already worked on ({resulting.LatestIterationLabel}{(resulting.CheckedOutBy is null ? "" : ", checked out")}); it cannot be removed from the change. Cancel the change notice to abandon it.");
                    var result = await gateway.DiscardResultingAsync(conn, tx, resulting, row.RevisionId, subject, c.Now, ct);
                    if (!result.IsOk) return Fail(result.Error!);
                    discarded = $"{resulting.Key} revision {resulting.Revision}";
                }
            }
            await using var up = new NpgsqlCommand("UPDATE chg.affected_object SET removed_at = @at, removed_by = @by, binding = false WHERE id = @id", conn, tx);
            up.Parameters.AddWithValue("at", c.Now);
            up.Parameters.AddWithValue("by", ChangeStore.Who(subject));
            up.Parameters.AddWithValue("id", row.Id);
            await up.ExecuteNonQueryAsync(ct);
            await ChangeStore.AppendHistoryAsync(conn, tx, c.Head.Id, null, subject, "Object removed",
                $"{row.Key} revision {row.Revision} ({row.Action}){(discarded is null ? "" : $"; revision {row.ResultingRevision} abandoned (Obsolete)")}.", c.Now, ct);
            return Done(("object", row.Key), ("revision", row.Revision), ("action", row.Action), ("discarded", discarded));
        }, ct);

    private async Task<Step> AddAffectedCoreAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, ChangeRow change, AddAffectedCommand cmd,
        DateTimeOffset now, List<string> notices, CancellationToken ct)
    {
        if (!ChangeActions.All.Contains(cmd.Action)) return Invalid($"Action must be one of: {string.Join(", ", ChangeActions.All)}.");
        if (change.Type == ChangeTypes.Pr && cmd.Action != ChangeActions.None) return Invalid("A problem report only references objects; use the action 'none'.");
        var gateway = GatewayFor(cmd.ObjectType);
        if (gateway is null) return Invalid($"Objects of type '{cmd.ObjectType}' cannot be changed yet. Parts only in Phase 1; documents arrive in Phase 2.");
        if (string.IsNullOrWhiteSpace(cmd.Key)) return Invalid("Name the object to add.");

        // Locks the master row: object edits, review and other changes touching it queue behind this transaction.
        var obj = await gateway.FindByKeyAsync(conn, tx, cmd.Key.Trim(), cmd.Revision, forUpdate: true, ct);
        var read = obj is null ? null : AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Read);
        if (obj is null || !read!.Permitted)
        {
            // The same answer whether the object is missing or hidden, so its existence is not disclosed (ADR-0004); a hidden one is audited.
            var refusal = Invalid($"{cmd.Key} was not found.");
            return obj is null ? refusal : refusal with { DenialDetails = [("reasons", string.Join(",", read!.Reasons)), ("target", cmd.Key)] };
        }
        var modify = AccessPolicy.Evaluate(subject, obj.Markings, PlmAction.Modify);
        if (!modify.Permitted) return Forbid([.. modify.Reasons]) with { DenialDetails = [("reasons", string.Join(",", modify.Reasons)), ("target", obj.Key)] };

        var bindsRevision = change.Type == ChangeTypes.Ecn && cmd.Action is ChangeActions.Revise or ChangeActions.Obsolete;
        if (cmd.Action is ChangeActions.Revise or ChangeActions.Obsolete)
        {
            // Another open change notice may already hold the revision, or have created it as its working revision.
            var holder = await FindHolderAsync(conn, tx, obj.ObjectType, obj.RevisionId, change.Id, ct);
            if (holder is not null)
            {
                var visible = AccessPolicy.Evaluate(subject, holder.Row.Markings, PlmAction.Read).Permitted;
                var who = visible ? holder.Row.Number : "another open change notice";
                return Fail(holder.IsResulting
                    ? $"Revision {obj.Revision} of {obj.Key} is the working revision of {who}; only one open change notice can change {obj.Key} at a time."
                    : $"Revision {obj.Revision} of {obj.Key} is already being changed by {who}. Only one open change notice can revise or obsolete a revision at a time.");
            }
        }

        if (!obj.IsLatest) return Fail($"Revision {obj.Revision} of {obj.Key} is not the latest revision; only the latest revision is changed.");
        if (cmd.Action is ChangeActions.Revise or ChangeActions.Obsolete && obj.State != "Released")
            return Fail($"Revision {obj.Revision} of {obj.Key} is {obj.StateLabel}. Only a Released revision is {(cmd.Action == ChangeActions.Revise ? "revised" : "obsoleted")} in a change; an unreleased revision is edited directly.");

        var existing = await ChangeStore.LoadAffectedAsync(conn, tx, change.Id, ct);
        if (existing.Any(r => r.ObjectType == obj.ObjectType && r.MasterId == obj.MasterId))
            return Fail($"{obj.Key} is already listed in {change.Number}.");

        // ---- marking dominance (ADR-0019): whoever reads this change learns about the object.
        var gaps = MarkingDominance.Gaps(change.Markings, obj.Markings);
        if (gaps.Count > 0)
        {
            var cover = MarkingDominance.MinimalCover(change.Markings, [obj.Markings]);
            if (!cover.IsSuccess) return Invalid([.. cover.Errors]);
            if (!cmd.RaiseMarkings)
                return Invalid($"The markings of {change.Number} do not cover {obj.Key}. {MarkingDominance.Describe(gaps)} Raise the change's markings to {cover.Value.BannerText()} to list it.");
            if (!AccessPolicy.Evaluate(subject, cover.Value, PlmAction.Modify).Permitted)
                return Invalid($"Raising the markings of {change.Number} to {cover.Value.BannerText()} would lock you out of it.");
            var problem = await CheckCoverageAsync(conn, tx, change, cover.Value, subject, ct);
            if (problem is not null) return Invalid(problem);
            await SetMarkingsAsync(conn, tx, change.Id, cover.Value, ct);
            var message = $"The markings of {change.Number} were raised from {change.Markings.BannerText()} to {cover.Value.BannerText()} because {obj.Key} is more restrictive.";
            notices.Add(message);
            await ChangeStore.AppendHistoryAsync(conn, tx, change.Id, null, subject, "Markings raised", message, now, ct);
            await audit.AppendAsync(tx, ChangeStore.Entry(subject, "change.markings_raise", AuditOutcome.Success, ChangeTypes.ObjectTypeOf(change.Type), change.Number, null,
                ("before", change.Markings.BannerText()), ("after", cover.Value.BannerText()), ("because", obj.Key)), ct);
        }

        Guid? resultingId = null;
        string? resultingLabel = null;
        if (change.Type == ChangeTypes.Ecn && cmd.Action == ChangeActions.Revise)
        {
            var revised = await gateway.ReviseAsync(conn, tx, obj, change.Number, subject, now, ct);
            if (!revised.IsOk) return Fail(revised.Error!);
            (resultingId, resultingLabel) = (revised.Resulting!.RevisionId, revised.Resulting.Revision);
        }

        var id = Guid.CreateVersion7();
        try
        {
            await using var ins = new NpgsqlCommand(
                """
                INSERT INTO chg.affected_object (id, change_id, object_type, master_id, object_key, object_name, revision_id, revision, change_action,
                                                 resulting_revision_id, resulting_revision, binding, added_at, added_by)
                VALUES (@id, @cid, @type, @master, @key, @name, @rev, @revl, @action, @res, @resl, @binding, @at, @by)
                """, conn, tx);
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("cid", change.Id);
            ins.Parameters.AddWithValue("type", obj.ObjectType);
            ins.Parameters.AddWithValue("master", obj.MasterId);
            ins.Parameters.AddWithValue("key", obj.Key);
            ins.Parameters.AddWithValue("name", obj.Name);
            ins.Parameters.AddWithValue("rev", obj.RevisionId);
            ins.Parameters.AddWithValue("revl", obj.Revision);
            ins.Parameters.AddWithValue("action", cmd.Action);
            ins.Parameters.AddWithValue("res", (object?)resultingId ?? DBNull.Value);
            ins.Parameters.AddWithValue("resl", (object?)resultingLabel ?? DBNull.Value);
            ins.Parameters.AddWithValue("binding", bindsRevision);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", ChangeStore.Who(subject));
            await ins.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Fail($"Revision {obj.Revision} of {obj.Key} is already held by another open change notice.");
        }
        await ChangeStore.AppendHistoryAsync(conn, tx, change.Id, null, subject, "Object added",
            $"{obj.Key} revision {obj.Revision}: {cmd.Action}{(resultingLabel is null ? "" : $"; revision {resultingLabel} created in work")}.", now, ct);
        return Done(("object", obj.Key), ("revision", obj.Revision), ("action", cmd.Action), ("resulting", resultingLabel), ("objectMarkings", obj.Markings.BannerText()),
            ("raised", (gaps.Count > 0).ToString(CultureInfo.InvariantCulture)));
    }

    private sealed record Holder(ChangeRow Row, bool IsResulting);

    private static async Task<Holder?> FindHolderAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string objectType, Guid revisionId, Guid exceptChange, CancellationToken ct)
    {
        Guid? holder = null;
        var resulting = false;
        await using (var cmd = new NpgsqlCommand(
            "SELECT ao.change_id, (ao.resulting_revision_id = @r) FROM chg.affected_object ao WHERE ao.object_type = @t AND (ao.revision_id = @r OR ao.resulting_revision_id = @r) AND ao.binding AND ao.change_id <> @self", conn, tx))
        {
            cmd.Parameters.AddWithValue("t", objectType);
            cmd.Parameters.AddWithValue("r", revisionId);
            cmd.Parameters.AddWithValue("self", exceptChange);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) (holder, resulting) = (r.GetGuid(0), r.GetBoolean(1));
        }
        if (holder is null) return null;
        var row = await ChangeStore.LoadByIdAsync(conn, tx, holder.Value, forUpdate: false, ct);
        return row is null ? null : new Holder(row, resulting);
    }
}
