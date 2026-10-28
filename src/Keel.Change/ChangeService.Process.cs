using System.Globalization;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Change;

public sealed partial class ChangeService
{
    // ---------------------------------------------------------------- manual lifecycle transitions with process rules

    /// <summary>
    /// Takes a manual lifecycle transition (reject/close a PR, close an ECR, cancel or implement an ECN) and applies the process rules
    /// of A8: leaving a lifecycle without completing it needs a reason code from <c>chg.reason_code</c>; an ECN cannot be implemented while
    /// change tasks are open; cancelling an ECN abandons its resulting revisions. States reached only by the review workflow or by a released
    /// change notice are refused with a message that says how to get there.
    /// </summary>
    public Task<Outcome<ChangeDetail>> TransitionAsync(Subject subject, string number, string toState, string? reasonCode, string? reason, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, "change.transition", correlationId, PlmAction.Modify, async (c, conn, tx) =>
        {
            var target = c.Lifecycle.State(toState);
            if (target is null) return Invalid($"'{toState}' is not a state of lifecycle {c.Lifecycle.Id}.");
            if (toState == c.State) return Fail($"{c.Head.Number} is already {target.Label}.");
            var transition = c.Lifecycle.Transitions.FirstOrDefault(t => t.From == c.State && t.To == toState);
            if (transition is null) return Fail($"A {c.Label.ToLowerInvariant()} cannot move from {c.StateLabel} to {target.Label}.");
            if (transition.Via != "manual") return Fail(ViaMessage(c.Head.Type, transition.Via, target.Label));
            if ((reason?.Length ?? 0) > 2000) return Invalid("The reason must be at most 2000 characters.");

            string? code = null;
            var scope = ReasonScope(c.Head.Type, toState);
            if (scope is not null)
            {
                var valid = await LoadReasonCodesAsync(conn, tx, scope, ct);
                var hit = valid.FirstOrDefault(r => string.Equals(r.Code, reasonCode, StringComparison.Ordinal));
                if (hit is null) return Invalid($"Choose a reason for {target.Label.ToLowerInvariant()}: {string.Join(", ", valid.Select(v => v.Label))}.");
                code = hit.Code;
            }

            var step = await ApplyRulesAsync(c, toState, subject, conn, tx, ct);
            if (step is not null) return step;

            var moved = await ChangeStore.TransitionAsync(conn, tx, c.Head, toState, "manual", subject, c.Now, code, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), ct);
            if (!moved.IsOk) return Fail(moved.Error!);
            await ChangeStore.AppendHistoryAsync(conn, tx, c.Head.Id, null, subject, $"Moved to {target.Label}",
                (code is null ? "" : $"Reason: {code}. ") + (string.IsNullOrWhiteSpace(reason) ? "" : reason.Trim()), c.Now, ct);
            return Done(("from", c.State), ("to", toState), ("reasonCode", code), ("reason", string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()));
        }, ct);

    /// <summary>Process rules per type; null when the transition may go ahead.</summary>
    private async Task<Step?> ApplyRulesAsync(Ctx c, string toState, Subject subject, NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        var derived = await DerivedStatesAsync(conn, tx, c.Head.Id, ct);
        switch (c.Head.Type, toState)
        {
            case (ChangeTypes.Pr, "Resolved"):
                if (derived.Count == 0) return Fail("Resolve a problem report only after a change request addressed it. If no change is needed, close it with a reason instead.");
                if (derived.Any(d => !d.IsFinal)) return Fail($"{derived.Count(d => !d.IsFinal)} change request(s) created from this problem report are still open.");
                if (!derived.Any(d => d.State == "Implemented")) return Fail("None of the change requests created from this problem report was implemented.");
                return null;
            case (ChangeTypes.Pr, "Rejected" or "Closed"):
                if (derived.Any(d => !d.IsFinal)) return Fail($"{derived.Count(d => !d.IsFinal)} change request(s) created from this problem report are still open. Close or finish them first.");
                return null;
            case (ChangeTypes.Ecr, "Closed"):
                if (derived.Any(d => d.State != "Cancelled")) return Fail($"{derived.Count(d => d.State != "Cancelled")} change notice(s) were opened from this change request. Cancel them before closing it.");
                return null;
            case (ChangeTypes.Ecn, "Implemented"):
            {
                var open = await CountOpenTasksAsync(conn, tx, c.Head.Id, ct);
                return open > 0 ? Fail($"{open} change task(s) are still open. Complete or cancel them before marking {c.Head.Number} as implemented.") : null;
            }
            case (ChangeTypes.Ecn, "Cancelled"):
                return await DiscardResultingRevisionsAsync(c, subject, conn, tx, ct);
            default:
                return null;
        }
    }

    /// <summary>
    /// Cancelling an ECN abandons every resulting revision that never released: its open check-out is discarded, it becomes Obsolete and the
    /// revision it was created from is the latest again. Nothing is deleted, so no orphan can remain (ADR-0019).
    /// </summary>
    private async Task<Step?> DiscardResultingRevisionsAsync(Ctx c, Subject subject, NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        var abandoned = new List<string>();
        foreach (var row in await ChangeStore.LoadAffectedAsync(conn, tx, c.Head.Id, ct))
        {
            if (row.ResultingRevisionId is not { } resultingId) continue;
            var gateway = GatewayFor(row.ObjectType) ?? throw new InvalidOperationException($"No gateway for {row.ObjectType}.");
            var resulting = await gateway.FindByRevisionAsync(conn, tx, resultingId, forUpdate: true, ct);
            if (resulting is null) continue;
            var result = await gateway.DiscardResultingAsync(conn, tx, resulting, row.RevisionId, subject, c.Now, ct);
            if (!result.IsOk) return Fail(result.Error!);
            abandoned.Add($"{resulting.Key} {resulting.Revision}");
        }
        await ChangeStore.ReleaseBindingsAsync(conn, tx, c.Head.Id, ct);
        if (abandoned.Count > 0)
            await ChangeStore.AppendHistoryAsync(conn, tx, c.Head.Id, null, subject, "Resulting revisions abandoned", $"{string.Join(", ", abandoned)} set to Obsolete; the revisions they were created from are the latest again.", c.Now, ct);
        return null;
    }

    private sealed record DerivedState(string Type, string State, bool IsFinal);

    /// <summary>States of the objects created from a change (counts only; nothing about them is disclosed to the caller).</summary>
    private static async Task<List<DerivedState>> DerivedStatesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        var list = new List<DerivedState>();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT c.change_type, c.lifecycle_state, chg.is_final_state(c.lifecycle_id, c.lifecycle_state)
            FROM chg.change c WHERE c.source_id = @id
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new DerivedState(r.GetString(0), r.GetString(1), r.GetBoolean(2)));
        return list;
    }

    private static async Task<int> CountOpenTasksAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid ecnId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM chg.change_task t WHERE t.ecn_id = @id AND t.lifecycle_state IN ('Open', 'InProgress')", conn, tx);
        cmd.Parameters.AddWithValue("id", ecnId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    internal static string? ReasonScope(string type, string toState) => toState switch
    {
        "Rejected" => $"{ChangeTypes.ObjectTypeOf(type)}.reject",
        "Closed" => $"{ChangeTypes.ObjectTypeOf(type)}.close",
        "Cancelled" => $"{ChangeTypes.ObjectTypeOf(type)}.cancel",
        _ => null,
    };

    internal static string ViaMessage(string type, string via, string targetLabel) => via switch
    {
        "workflow" when type == ChangeTypes.Ecr => $"{targetLabel} is reached only through the CCB review: submit the change request for review.",
        "workflow" => $"{targetLabel} is reached only through the release review: submit the change notice for review.",
        "change" => $"{targetLabel} is set automatically when the change notice that implements it is released.",
        _ => $"Moving to {targetLabel} is not available.",
    };

    private static async Task<List<ReasonCodeView>> LoadReasonCodesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string scope, CancellationToken ct)
    {
        var list = new List<ReasonCodeView>();
        await using var cmd = new NpgsqlCommand("SELECT code, label FROM chg.reason_code WHERE scope = @s AND is_active ORDER BY sort_order", conn, tx);
        cmd.Parameters.AddWithValue("s", scope);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ReasonCodeView(r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>Reason codes for leaving a lifecycle without completing it: <paramref name="verb"/> is "reject", "close" or "cancel".</summary>
    public async Task<IReadOnlyList<ReasonCodeView>> ReasonCodesAsync(string type, string verb, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await LoadReasonCodesAsync(conn, null, $"{ChangeTypes.ObjectTypeOf(type)}.{verb}", ct);
    }
}
