using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;
using static Keel.Requirements.RequirementStore;

namespace Keel.Requirements;

public sealed partial class RequirementService
{
    public const int MaxOpsPerEdit = 5000;

    /// <summary>
    /// Applies a batch of edits to the latest iteration of the latest revision (In Work) as ONE new iteration: either every edit applies or none. The caller names the iteration it
    /// read; a newer one is a conflict. A parent may be named as <c>#n</c>, the n-th (0-based) add of the same batch. Requirements are never removed: they are withdrawn.
    /// </summary>
    public Task<Outcome<EditResult>> EditAsync(Subject subject, string number, EditCommand cmd, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<EditResult>(subject, number, "requirement.edit", correlationId, (head, conn, tx) => ApplyEditAsync(subject, head, conn, tx, cmd, ct), ct);

    internal async Task<Step<EditResult>> ApplyEditAsync(Subject subject, SpecHead head, NpgsqlConnection conn, NpgsqlTransaction tx, EditCommand cmd, CancellationToken ct)
    {
            if (!head.IsLatest || head.State != SpecStates.InWork) return Step<EditResult>.Invalid($"Only the latest revision of a specification that is In Work can be edited; {head.Number} {head.Revision} is {SpecStates.Label(head.State)}.");
            if (cmd.Ops.Count == 0) return Step<EditResult>.Invalid("Nothing to change.");
            if (cmd.Ops.Count > MaxOpsPerEdit) return Step<EditResult>.Invalid($"At most {MaxOpsPerEdit} changes can be applied at once.");
            if ((cmd.Comment?.Length ?? 0) > 1000) return Step<EditResult>.Invalid("The comment must be at most 1000 characters.");
            if (cmd.BaseIteration != head.Iteration) return Step<EditResult>.Conflict($"The specification changed since you read it: it is now at iteration {Fmt(head.Iteration)}. Reload and try again.");

            var original = await LoadWorkAsync(conn, tx, head.IterationId, ct);
            var cur = original.ToDictionary(w => w.Id);
            var byKey = original.ToDictionary(w => w.Key, StringComparer.Ordinal);
            var createdKeys = new List<string>();
            var nextKey = head.NextKey;
            var errors = new List<string>();

            WorkReq? Find(string? key, bool forChange)
            {
                if (key is null || !byKey.TryGetValue(key, out var w)) return null;
                if (!AccessPolicy.Evaluate(subject, w.Markings, PlmAction.Read).Permitted) return null; // a requirement the caller cannot read looks like one that does not exist
                if (forChange && !AccessPolicy.Evaluate(subject, w.Markings, PlmAction.Modify).Permitted) { errors.Add($"You are not authorized to change {key}."); return null; }
                return cur.GetValueOrDefault(w.Id);
            }

            bool Place(WorkReq w, Guid? parentId, string? beforeKey)
            {
                Guid? beforeId = null;
                if (beforeKey is not null)
                {
                    var b = Find(beforeKey, false);
                    if (b is null || b.ParentId != parentId || b.Id == w.Id) { errors.Add($"{beforeKey} is not a requirement at the place you chose."); return false; }
                    beforeId = b.Id;
                }
                var sibs = cur.Values.Where(x => x.ParentId == parentId && x.Id != w.Id).OrderBy(x => x.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
                var idx = beforeId is null ? sibs.Count : sibs.FindIndex(x => x.Id == beforeId);
                sibs.Insert(idx, w with { ParentId = parentId });
                for (var i = 0; i < sibs.Count; i++) cur[sibs[i].Id] = sibs[i] with { Ordinal = i + 1 };
                return true;
            }

            Guid? ResolveParent(string? parentKey, out bool ok)
            {
                ok = true;
                if (string.IsNullOrEmpty(parentKey)) return null;
                if (parentKey[0] == '#')
                {
                    if (int.TryParse(parentKey.AsSpan(1), out var n) && n >= 0 && n < createdKeys.Count) return byKey[createdKeys[n]].Id;
                    errors.Add($"'{parentKey}' does not name an earlier added requirement."); ok = false; return null;
                }
                var p = Find(parentKey, false);
                if (p is null) { errors.Add($"The parent {parentKey} was not found."); ok = false; return null; }
                return p.Id;
            }

            Markings? ResolveMarkings(RequirementInput input, Markings fallback)
            {
                var m = input.Markings ?? fallback;
                var gaps = RequirementRules.DominanceGaps(m, head.Markings);
                if (gaps.Count > 0)
                {
                    var cover = MarkingDominance.MinimalCover(m, [head.Markings]);
                    if (!cover.IsSuccess) { errors.Add(string.Join(" ", cover.Errors)); return null; }
                    if (!input.ConfirmRaiseMarkings) { errors.Add($"The markings {m.BannerText()} do not cover the specification's. A requirement must be at least as restrictive as its specification; use {cover.Value.BannerText()}."); return null; }
                    m = cover.Value;
                }
                if (!AccessPolicy.Evaluate(subject, m, PlmAction.Modify).Permitted) { errors.Add($"Markings {m.BannerText()} would lock you out of the requirement."); return null; }
                return m;
            }

            var opIndex = 0;
            foreach (var op in cmd.Ops)
            {
                opIndex++;
                var input = op.Input;
                switch (op.Op)
                {
                    case "add":
                    {
                        if (input is null) { errors.Add($"Change {Fmt(opIndex)}: a new requirement needs its fields."); break; }
                        var problems = RequirementRules.Validate(input.Title, input.Text, input.Type, input.Priority, input.Method, input.Rationale, input.Status);
                        if (problems.Count > 0) { errors.AddRange(problems.Select(p => $"Change {Fmt(opIndex)}: {p}")); break; }
                        var parent = ResolveParent(input.ParentKey, out var ok);
                        if (!ok) break;
                        var markings = ResolveMarkings(input, head.Markings);
                        if (markings is null) break;
                        var key = RequirementRules.FormatKey(nextKey++);
                        var w = new WorkReq(Guid.CreateVersion7(), key, parent, 0, input.Title.Trim(), input.Text.Trim(), input.Type, input.Priority, input.Method, input.Rationale?.Trim() ?? "", input.Status, markings, true);
                        cur[w.Id] = w;
                        byKey[key] = w;
                        if (!Place(w, parent, input.BeforeKey)) { cur.Remove(w.Id); byKey.Remove(key); break; }
                        createdKeys.Add(key);
                        break;
                    }
                    case "update":
                    {
                        var w = Find(op.Key, true);
                        if (w is null) { if (errors.Count == 0 || !errors[^1].StartsWith("You are not", StringComparison.Ordinal)) errors.Add($"Requirement {op.Key} was not found."); break; }
                        if (input is null) { errors.Add($"Change {Fmt(opIndex)}: the new fields are missing."); break; }
                        var problems = RequirementRules.Validate(input.Title, input.Text, input.Type, input.Priority, input.Method, input.Rationale, input.Status);
                        if (problems.Count > 0) { errors.AddRange(problems.Select(p => $"{w.Key}: {p}")); break; }
                        var markings = input.Markings is null ? w.Markings : ResolveMarkings(input, w.Markings);
                        if (markings is null) break;
                        var updated = w with { Title = input.Title.Trim(), Text = input.Text.Trim(), Type = input.Type, Priority = input.Priority, Method = input.Method, Rationale = input.Rationale?.Trim() ?? "", Status = input.Status, Markings = markings };
                        cur[w.Id] = updated;
                        byKey[w.Key] = updated;
                        break;
                    }
                    case "move":
                    {
                        var w = Find(op.Key, true);
                        if (w is null) { if (errors.Count == 0 || !errors[^1].StartsWith("You are not", StringComparison.Ordinal)) errors.Add($"Requirement {op.Key} was not found."); break; }
                        var parent = ResolveParent(input?.ParentKey, out var ok);
                        if (!ok) break;
                        // A requirement cannot move under itself or one of its own descendants.
                        for (var up = parent; up is { } u; up = cur.GetValueOrDefault(u)?.ParentId)
                            if (u == w.Id) { errors.Add($"{w.Key} cannot be moved below itself."); ok = false; break; }
                        if (!ok) break;
                        if (!Place(w, parent, input?.BeforeKey)) break;
                        break;
                    }
                    default:
                        errors.Add($"Change {Fmt(opIndex)}: '{op.Op}' is not a known change (add, update or move).");
                        break;
                }
                if (errors.Count > 20) break;
            }
            if (errors.Count > 0) return Step<EditResult>.Invalid([.. errors.Take(20)]);

            if (cur.Count > RequirementRules.MaxRequirements) return Step<EditResult>.Invalid($"A specification has at most {RequirementRules.MaxRequirements} requirements.");
            foreach (var id in cur.Keys)
                if (RequirementRules.DepthOf(id, cur) >= RequirementRules.MaxDepth) return Step<EditResult>.Invalid($"The hierarchy is at most {RequirementRules.MaxDepth} levels deep.");

            var before = original.ToDictionary(w => w.Id);
            var changedContent = cur.Values.Where(w => !w.IsNew && before[w.Id].Sha != w.Sha).ToList();
            var moved = cur.Values.Count(w => !w.IsNew && (before[w.Id].ParentId != w.ParentId || before[w.Id].Ordinal != w.Ordinal));
            var markingsChanged = cur.Values.Count(w => !w.IsNew && before[w.Id].Markings != w.Markings);
            if (createdKeys.Count == 0 && changedContent.Count == 0 && moved == 0 && markingsChanged == 0) return Step<EditResult>.Invalid("Nothing changed.");

            var now = clock.GetUtcNow();
            var who = Who(subject);
            var next = head.Iteration + 1;
            var work = cur.Values.OrderBy(w => w.Key, StringComparer.Ordinal).ToList();
            var sha = await WriteIterationAsync(conn, tx, head.Id, head.RevisionId, next, cmd.Comment?.Trim(), work, now, who, true, ct);
            await using (var up = new NpgsqlCommand("UPDATE req.spec SET next_key = @n WHERE id = @id", conn, tx))
            {
                up.Parameters.AddWithValue("n", nextKey);
                up.Parameters.AddWithValue("id", head.Id);
                await up.ExecuteNonQueryAsync(ct);
            }
            var suspects = await MarkChangedRequirementsAsync(conn, tx, subject, head, changedContent, now, ct);
            if (notifications is not null)
            {
                await NotifyAsync(conn, tx, NotificationKindCodes.WatchedChanged, head, subject, ct);
                await notifications.AutoWatchAsync(conn, tx, subject.Id, AwarenessKinds.ReqSpec, head.Number, ct);
            }
            var changed = createdKeys.Count + changedContent.Count;
            return Step<EditResult>.Done(new EditResult(next, createdKeys, changed, suspects),
                ("revision", head.Revision), ("iteration", Fmt(next)), ("created", Fmt(createdKeys.Count)), ("updated", Fmt(changedContent.Count)), ("moved", Fmt(moved)),
                ("markingsChanged", Fmt(markingsChanged)), ("suspectLinks", Fmt(suspects)), ("contentSha256", sha));
    }

    public Task<Outcome<EditResult>> AddAsync(Subject subject, string number, int baseIteration, RequirementInput input, string? correlationId, CancellationToken ct = default) =>
        EditAsync(subject, number, new EditCommand(baseIteration, null, [new EditOp("add", null, input)]), correlationId, ct);

    // ---------------------------------------------------------------- markings of the specification

    /// <summary>
    /// Changes the markings of the specification (with a reason, audited with the banner before and after). Every requirement must stay at least as restrictive: when it would not,
    /// the change is refused unless <paramref name="confirmRaise"/> is set, which raises those requirements to the minimal cover in a new iteration (In Work only).
    /// </summary>
    public Task<Outcome<SpecDetail>> SetMarkingsAsync(Subject subject, string number, Markings markings, string reason, bool confirmRaise, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<SpecDetail>(subject, number, "requirement.markings_change", correlationId, async (head, conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000) return Step<SpecDetail>.Invalid("A reason is required (at most 1000 characters).");
            if (!AccessPolicy.Evaluate(subject, markings, PlmAction.Modify).Permitted) return Step<SpecDetail>.Invalid($"Markings {markings.BannerText()} would lock you out of the specification.");
            var work = await LoadWorkAsync(conn, tx, head.IterationId, ct);
            var short_ = work.Where(w => RequirementRules.DominanceGaps(w.Markings, markings).Count > 0).ToList();
            var now = clock.GetUtcNow();
            if (short_.Count > 0)
            {
                if (!head.IsLatest || head.State != SpecStates.InWork) return Step<SpecDetail>.Invalid("Some requirements would no longer be at least as restrictive as the specification, and this revision is locked. Revise the specification first.");
                if (!confirmRaise) return Step<SpecDetail>.Invalid("Some requirements are less restrictive than the new markings. Confirm to raise them to match.");
                var raised = new List<WorkReq>();
                foreach (var w in work)
                {
                    if (!short_.Contains(w)) { raised.Add(w); continue; }
                    var cover = MarkingDominance.MinimalCover(w.Markings, [markings]);
                    if (!cover.IsSuccess) return Step<SpecDetail>.Invalid([.. cover.Errors]);
                    raised.Add(w with { Markings = cover.Value });
                }
                await WriteIterationAsync(conn, tx, head.Id, head.RevisionId, head.Iteration + 1, "Requirement markings raised with the specification", raised, now, Who(subject), true, ct);
            }
            await using (var up = new NpgsqlCommand($"UPDATE req.spec SET {MarkingColumns.UpdateSet} WHERE id = @id", conn, tx))
            {
                foreach (var (k, v) in MarkingColumns.Parameters(markings)) up.Parameters.AddWithValue(k, v);
                up.Parameters.AddWithValue("id", head.Id);
                await up.ExecuteNonQueryAsync(ct);
            }
            var fresh = (await LoadSpecAsync(conn, tx, head.Number, null, false, ct))!;
            return Step<SpecDetail>.Done(await BuildDetailAsync(conn, tx, subject, fresh, ct),
                ("before", head.Markings.BannerText()), ("after", markings.BannerText()), ("reason", reason.Trim()), ("raisedRequirements", Fmt(short_.Count)));
        }, ct);

    // ---------------------------------------------------------------- revise and obsolete

    /// <summary>Starts the next revision of a Released specification: a copy of its latest iteration (hidden requirements included) as iteration 1, In Work. The Released revision stays in force until the new one is released.</summary>
    public Task<Outcome<SpecDetail>> ReviseAsync(Subject subject, string number, string reason, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<SpecDetail>(subject, number, "requirement.revise", correlationId, async (head, conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000) return Step<SpecDetail>.Invalid("A reason is required (at most 1000 characters).");
            if (!head.IsLatest || head.State != SpecStates.Released) return Step<SpecDetail>.Invalid($"Only a Released specification can be revised; {head.Number} {head.Revision} is {SpecStates.Label(head.State)}.");
            var work = (await LoadWorkAsync(conn, tx, head.IterationId, ct));
            var now = clock.GetUtcNow();
            var who = Who(subject);
            var lifecycle = await LifecycleAsync(conn, tx, head.LifecycleId, ct);
            var label = RevisionScheme.Next(head.Revision);
            var revisionId = Guid.CreateVersion7();
            await using (var flip = new NpgsqlCommand("UPDATE req.spec_revision SET is_latest = false WHERE id = @id", conn, tx))
            {
                flip.Parameters.AddWithValue("id", head.RevisionId);
                await flip.ExecuteNonQueryAsync(ct);
            }
            await using (var ins = new NpgsqlCommand(
                "INSERT INTO req.spec_revision (id, spec_id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by, state_reason) VALUES (@id, @s, @rev, @state, true, 1, @at, @by, @at, @by, @reason)", conn, tx))
            {
                ins.Parameters.AddWithValue("id", revisionId);
                ins.Parameters.AddWithValue("s", head.Id);
                ins.Parameters.AddWithValue("rev", label);
                ins.Parameters.AddWithValue("state", lifecycle.Initial.State);
                ins.Parameters.AddWithValue("at", now);
                ins.Parameters.AddWithValue("by", who);
                ins.Parameters.AddWithValue("reason", reason.Trim());
                await ins.ExecuteNonQueryAsync(ct);
            }
            await WriteIterationAsync(conn, tx, head.Id, revisionId, 1, $"Revised from {head.Revision}", work, now, who, false, ct);
            var fresh = (await LoadSpecAsync(conn, tx, head.Number, null, false, ct))!;
            if (notifications is not null) await NotifyAsync(conn, tx, NotificationKindCodes.WatchedChanged, fresh, subject, ct);
            return Step<SpecDetail>.Done(await BuildDetailAsync(conn, tx, subject, fresh, ct), ("from", head.Revision), ("to", label), ("reason", reason.Trim()));
        }, ct);

    /// <summary>Retires a specification revision directly (In Work or Released to Obsolete) with a reason.</summary>
    public Task<Outcome<SpecDetail>> ObsoleteAsync(Subject subject, string number, string reason, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<SpecDetail>(subject, number, "requirement.transition", correlationId, async (head, conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000) return Step<SpecDetail>.Invalid("A reason is required (at most 1000 characters).");
            var lifecycle = await LifecycleAsync(conn, tx, head.LifecycleId, ct);
            if (lifecycle.Transitions.All(t => !(t.From == head.State && t.To == SpecStates.Obsolete && t.Via == "manual")))
                return Step<SpecDetail>.Invalid($"A specification that is {SpecStates.Label(head.State)} cannot be made obsolete directly.");
            await using (var up = new NpgsqlCommand("UPDATE req.spec_revision SET lifecycle_state = 'Obsolete', state_changed_at = @at, state_changed_by = @by, state_reason = @reason WHERE id = @id AND lifecycle_state = @from", conn, tx))
            {
                up.Parameters.AddWithValue("at", clock.GetUtcNow());
                up.Parameters.AddWithValue("by", Who(subject));
                up.Parameters.AddWithValue("reason", reason.Trim());
                up.Parameters.AddWithValue("id", head.RevisionId);
                up.Parameters.AddWithValue("from", head.State);
                if (await up.ExecuteNonQueryAsync(ct) != 1) return Step<SpecDetail>.Conflict("The specification changed state concurrently.");
            }
            var fresh = (await LoadSpecAsync(conn, tx, head.Number, null, false, ct))!;
            if (notifications is not null) await NotifyAsync(conn, tx, NotificationKindCodes.WatchedObsoleted, fresh, subject, ct);
            return Step<SpecDetail>.Done(await BuildDetailAsync(conn, tx, subject, fresh, ct), ("from", head.State), ("to", SpecStates.Obsolete), ("reason", reason.Trim()));
        }, ct, PlmAction.Modify);
}
