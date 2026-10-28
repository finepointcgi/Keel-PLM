using System.Data.Common;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

public sealed partial class BaselineService
{
    private sealed record Locked(BaselineHead Head, DateTimeOffset Now);

    /// <summary>
    /// Loads the baseline FOR UPDATE, evaluates Read (404 and a denial event when refused) and Modify (403), runs the body and audits its outcome.
    /// A failure rolls the change back and is audited as a Failure (or a Denial for access refusals) outside the rolled-back transaction.
    /// </summary>
    private async Task<Outcome<T>> MutateAsync<T>(
        Subject subject, string number, string action, string? correlationId,
        Func<NpgsqlConnection, NpgsqlTransaction, Locked, Task<Step<T>>> body, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = string.IsNullOrWhiteSpace(number) ? null : await BaselineStore.LoadByNumberAsync(conn, tx, number.Trim(), forUpdate: true, ct);
        if (head is null) return Outcome<T>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, head.Number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.NotFound();
        }
        var modify = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Modify);
        if (!modify.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, head.Number, correlationId, ("reasons", string.Join(",", modify.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.Forbidden(modify.Reasons);
        }
        var step = await body(conn, tx, new Locked(head, SignatureTime(clock.GetUtcNow())));
        if (step.Kind != OutcomeKind.Ok)
        {
            await tx.RollbackAsync(ct);
            await audit.AppendAsync(step.Denied
                ? Entry(subject, action, AuditOutcome.Denied, head.Number, correlationId, step.Details)
                : Entry(subject, action, AuditOutcome.Failure, head.Number, correlationId, ("error", string.Join(" ", step.Errors)), ("kind", step.Kind.ToString())), ct);
            return step.Kind == OutcomeKind.Conflict ? Outcome<T>.Conflict(step.Errors[0]) : Outcome<T>.Invalid(step.Errors);
        }
        await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Success, head.Number, correlationId, step.Details), ct);
        await tx.CommitAsync(ct);
        return Outcome<T>.Ok(step.Value!);
    }

    private static async Task<BaselineDetail> ReloadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, Guid id, CancellationToken ct) =>
        (await LoadDetailAsync(conn, tx, subject, (await BaselineStore.LoadByIdAsync(conn, tx, id, false, ct))!, [], ct))!;

    // ---------------------------------------------------------------- draft text

    /// <summary>Name, purpose and milestone can be corrected while the baseline is a draft; the content never changes.</summary>
    public Task<Outcome<BaselineDetail>> UpdateAsync(Subject subject, string number, string name, string purpose, string? milestone, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<BaselineDetail>(subject, number, "baseline.update", correlationId, async (conn, tx, l) =>
        {
            if (l.Head.State != BaselineStates.Draft) return Step<BaselineDetail>.Conflict($"{l.Head.Number} is {l.Head.State}; only a draft can be edited.");
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) return Step<BaselineDetail>.Invalid("The name is required and at most 200 characters.");
            if ((purpose?.Length ?? 0) > BaselineLimits.MaxPurposeLength) return Step<BaselineDetail>.Invalid($"The purpose must be at most {BaselineLimits.MaxPurposeLength} characters.");
            if (milestone is not null && milestone.Trim().Length is < 1 or > 40) return Step<BaselineDetail>.Invalid("The milestone must be 1 to 40 characters.");
            await using (var cmd = new NpgsqlCommand("UPDATE cfg.baseline SET name = @n, purpose = @p, milestone = @m WHERE id = @id", conn, tx))
            {
                cmd.Parameters.AddWithValue("n", name.Trim());
                cmd.Parameters.AddWithValue("p", purpose?.Trim() ?? "");
                cmd.Parameters.Add(new NpgsqlParameter("m", NpgsqlTypes.NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(milestone) ? DBNull.Value : milestone.Trim() });
                cmd.Parameters.AddWithValue("id", l.Head.Id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            return Step<BaselineDetail>.Done(await ReloadAsync(conn, tx, subject, l.Head.Id, ct), ("name", name.Trim()), ("milestone", milestone));
        }, ct);

    // ---------------------------------------------------------------- establish, supersede, archive

    /// <summary>
    /// Establishes a draft baseline directly (CFG-3): allowed for authors and administrators when the baseline's type does not require sign-off,
    /// and always with a reason. Types that require sign-off (Release, Product) and baselines created with sign-off go through the review workflow.
    /// </summary>
    public Task<Outcome<BaselineDetail>> EstablishAsync(Subject subject, string number, string reason, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<BaselineDetail>(subject, number, "baseline.establish", correlationId, async (conn, tx, l) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) return Step<BaselineDetail>.Invalid("Give the reason for establishing the baseline.");
            if (reason.Trim().Length > BaselineLimits.MaxReasonLength) return Step<BaselineDetail>.Invalid($"The reason must be at most {BaselineLimits.MaxReasonLength} characters.");
            if (l.Head.State != BaselineStates.Draft) return Step<BaselineDetail>.Conflict($"{l.Head.Number} is {l.Head.State}; only a draft can be established.");
            if (l.Head.Signoff)
                return Step<BaselineDetail>.Conflict($"{l.Head.Number} requires sign-off: submit it for review and it is established when every approver has signed.");
            var moved = await BaselineStore.TransitionAsync(conn, tx, l.Head, BaselineStates.Established, "manual", BomService.DisplayId(subject), l.Now, establishReason: reason.Trim(), ct: ct);
            if (!moved.IsOk) return Step<BaselineDetail>.Conflict(moved.Error!);
            await BaselineEvents.EmitEstablishedAsync(events, conn, tx, l.Head.Id, subject, correlationId, ct); // 4.0: ERP event, same transaction
            return Step<BaselineDetail>.Done(await ReloadAsync(conn, tx, subject, l.Head.Id, ct), ("reason", reason.Trim()), ("path", "direct"));
        }, ct);

    /// <summary>Marks an established baseline as replaced by a newer established baseline of the same root part.</summary>
    public async Task<Outcome<BaselineDetail>> SupersedeAsync(Subject subject, string number, string supersededByNumber, string reason, string? correlationId, CancellationToken ct = default)
    {
        // Lock both rows in a fixed order (by number) so two supersessions cannot deadlock.
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var numbers = new[] { number?.Trim() ?? "", supersededByNumber?.Trim() ?? "" };
        var heads = new Dictionary<string, BaselineHead>(StringComparer.Ordinal);
        foreach (var n in numbers.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
            if (n.Length > 0 && await BaselineStore.LoadByNumberAsync(conn, tx, n, forUpdate: true, ct) is { } h) heads[n] = h;
        if (!heads.TryGetValue(numbers[0], out var old) || !heads.TryGetValue(numbers[1], out var replacement)) return Outcome<BaselineDetail>.NotFound();

        foreach (var h in new[] { old, replacement })
        {
            var read = AccessPolicy.Evaluate(subject, h.Markings, PlmAction.Read);
            var modify = AccessPolicy.Evaluate(subject, h.Markings, PlmAction.Modify);
            if (read.Permitted && modify.Permitted) continue;
            await audit.AppendAsync(tx, Entry(subject, "baseline.supersede", AuditOutcome.Denied, h.Number, correlationId, ("reasons", string.Join(",", (read.Permitted ? modify : read).Reasons))), ct);
            await tx.CommitAsync(ct);
            return read.Permitted ? Outcome<BaselineDetail>.Forbidden(modify.Reasons) : Outcome<BaselineDetail>.NotFound();
        }

        string? problem = null;
        if (string.IsNullOrWhiteSpace(reason)) problem = "Give the reason for superseding the baseline.";
        else if (old.Id == replacement.Id) problem = "A baseline cannot supersede itself.";
        else if (old.State != BaselineStates.Established) problem = $"{old.Number} is {old.State}; only an established baseline is superseded.";
        else if (replacement.State != BaselineStates.Established) problem = $"{replacement.Number} is {replacement.State}; it must be established before it can supersede another baseline.";
        else if (old.RootMasterId != replacement.RootMasterId) problem = "A baseline can only be superseded by a baseline of the same part.";
        if (problem is not null)
        {
            await tx.RollbackAsync(ct);
            await audit.AppendAsync(Entry(subject, "baseline.supersede", AuditOutcome.Failure, old.Number, correlationId, ("error", problem), ("by", replacement.Number)), ct);
            return Outcome<BaselineDetail>.Invalid([problem]);
        }
        var now = SignatureTime(clock.GetUtcNow());
        var moved = await BaselineStore.TransitionAsync(conn, tx, old, BaselineStates.Superseded, "manual", BomService.DisplayId(subject), now, stateReason: reason.Trim(), supersededBy: replacement.Id, ct: ct);
        if (!moved.IsOk)
        {
            await tx.RollbackAsync(ct);
            return Outcome<BaselineDetail>.Conflict(moved.Error!);
        }
        await audit.AppendAsync(tx, Entry(subject, "baseline.supersede", AuditOutcome.Success, old.Number, correlationId, ("by", replacement.Number), ("reason", reason.Trim())), ct);
        var detail = await ReloadAsync(conn, tx, subject, old.Id, ct);
        await tx.CommitAsync(ct);
        return Outcome<BaselineDetail>.Ok(detail);
    }

    /// <summary>Archives a baseline that is no longer needed. Nothing is deleted; the snapshot stays readable.</summary>
    public Task<Outcome<BaselineDetail>> ArchiveAsync(Subject subject, string number, string reason, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<BaselineDetail>(subject, number, "baseline.archive", correlationId, async (conn, tx, l) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) return Step<BaselineDetail>.Invalid("Give the reason for archiving the baseline.");
            if (l.Head.State is not (BaselineStates.Draft or BaselineStates.Established or BaselineStates.Superseded))
                return Step<BaselineDetail>.Conflict($"{l.Head.Number} is {l.Head.State} and cannot be archived now.");
            var moved = await BaselineStore.TransitionAsync(conn, tx, l.Head, BaselineStates.Archived, "manual", BomService.DisplayId(subject), l.Now, stateReason: reason.Trim(), ct: ct);
            if (!moved.IsOk) return Step<BaselineDetail>.Conflict(moved.Error!);
            return Step<BaselineDetail>.Done(await ReloadAsync(conn, tx, subject, l.Head.Id, ct), ("from", l.Head.State), ("reason", reason.Trim()));
        }, ct);
}
