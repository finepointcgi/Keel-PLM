using System.Data.Common;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Documents;

/// <summary>
/// All-or-nothing operations over a set of documents (CAD packages, ADR-0035): one transaction, every row lock taken in number order so
/// concurrent sets cannot deadlock, the same per-document rules and audit events as the single-document commands, and no change at all when
/// any member refuses. The bodies are the ones <see cref="CheckOutAsync"/>, <see cref="CheckInAsync"/> and <see cref="UndoCheckOutAsync"/> run.
/// </summary>
public sealed partial class DocumentService
{
    public const int MaxSetSize = 500;

    private sealed record SetMember(Head Head, MutationContext Ctx);

    /// <summary>Authorizes and locks every member. On refusal the transaction is rolled back and the outcome to return is given.</summary>
    private async Task<(List<SetMember>? Members, Outcome<T>? Refusal)> LockSetAsync<T>(
        NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, IReadOnlyList<string> numbers, string action, PlmAction permission, bool allowAdminWithoutModify,
        string? correlationId, CancellationToken ct)
    {
        var members = new List<SetMember>();
        foreach (var number in numbers)
        {
            var head = await LoadHeadAsync(conn, tx, number, forUpdate: true, ct);
            if (head is null)
            {
                await tx.RollbackAsync(ct);
                return (null, Outcome<T>.NotFound());
            }
            var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
            var decision = read.Permitted ? AccessPolicy.Evaluate(subject, head.Markings, permission) : read;
            if (!decision.Permitted && read.Permitted && allowAdminWithoutModify && AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Administer).Permitted)
                decision = AccessDecision.Permit;
            if (!decision.Permitted)
            {
                await tx.RollbackAsync(ct);
                await audit.AppendAsync(Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", decision.Reasons)), ("set", "true")), ct);
                // Not readable: answer exactly as for a missing document so existence is not disclosed (ADR-0004).
                return (null, read.Permitted ? Outcome<T>.Forbidden(decision.Reasons) : Outcome<T>.NotFound());
            }
            var lifecycle = await Lifecycle.LoadAsync(conn, tx, head.LifecycleId, ct) ?? throw new InvalidOperationException($"Lifecycle {head.LifecycleId} missing.");
            members.Add(new SetMember(head, await LoadMutationContextAsync(conn, tx, head, lifecycle, ct)));
        }
        return (members, null);
    }

    private static (List<string>? Numbers, Outcome<T>? Refusal) NormalizeSet<T>(IReadOnlyList<string> numbers)
    {
        var distinct = numbers.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (distinct.Count == 0) return (null, Outcome<T>.Invalid(["At least one document is required."]));
        if (distinct.Count > MaxSetSize) return (null, Outcome<T>.Invalid([$"A set can hold at most {MaxSetSize} documents."]));
        return (distinct, null);
    }

    internal async Task<Outcome<IReadOnlyList<string>>> CheckOutSetAsync(Subject subject, IReadOnlyList<string> numbers, string? correlationId,
        Func<DbConnection, DbTransaction, CancellationToken, Task>? inTransaction, CancellationToken ct)
    {
        var (list, bad) = NormalizeSet<IReadOnlyList<string>>(numbers);
        if (bad is not null) return bad;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (members, refusal) = await LockSetAsync<IReadOnlyList<string>>(conn, tx, subject, list!, "document.checkout", PlmAction.Modify, false, correlationId, ct);
        if (refusal is not null) return refusal;
        foreach (var m in members!)
        {
            var step = await CheckOutBodyAsync(subject, m.Ctx, conn, tx, ct);
            if (step.Kind != OutcomeKind.Ok)
            {
                await tx.RollbackAsync(ct);
                return Outcome<IReadOnlyList<string>>.Conflict($"{m.Head.Number}: {step.Errors[0]}");
            }
            await audit.AppendAsync(tx, Entry(subject, "document.checkout", AuditOutcome.Success, m.Head.Number, correlationId, [.. step.Details, ("set", "true")]), ct);
            await DocumentAwareness.AfterAsync(notifications, conn, tx, "document.checkout", m.Head.Number, m.Head.Markings, subject, step.Details, ct);
        }
        if (inTransaction is not null) await inTransaction(conn, tx, ct);
        await tx.CommitAsync(ct);
        return Outcome<IReadOnlyList<string>>.Ok(list!);
    }

    internal async Task<Outcome<IReadOnlyList<CadCheckedIn>>> CheckInSetAsync(Subject subject, IReadOnlyList<CadCheckInItem> items, string? correlationId,
        Func<DbConnection, DbTransaction, IReadOnlyList<CadCheckedIn>, CancellationToken, Task>? inTransaction, CancellationToken ct)
    {
        var (list, bad) = NormalizeSet<IReadOnlyList<CadCheckedIn>>([.. items.Select(i => i.Number)]);
        if (bad is not null) return bad;
        var comments = items.GroupBy(i => i.Number.Trim(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Comment, StringComparer.Ordinal);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (members, refusal) = await LockSetAsync<IReadOnlyList<CadCheckedIn>>(conn, tx, subject, list!, "document.checkin", PlmAction.Modify, false, correlationId, ct);
        if (refusal is not null) return refusal;
        var results = new List<CadCheckedIn>();
        foreach (var m in members!)
        {
            var step = await CheckInBodyAsync(subject, m.Ctx, comments.GetValueOrDefault(m.Head.Number), conn, tx, ct);
            if (step.Kind != OutcomeKind.Ok)
            {
                await tx.RollbackAsync(ct);
                return step.Kind == OutcomeKind.Invalid
                    ? Outcome<IReadOnlyList<CadCheckedIn>>.Invalid(step.Errors.Select(e => $"{m.Head.Number}: {e}"))
                    : Outcome<IReadOnlyList<CadCheckedIn>>.Conflict($"{m.Head.Number}: {step.Errors[0]}");
            }
            var next = m.Ctx.LatestIteration + 1;
            Guid iterationId;
            await using (var cmd = new NpgsqlCommand("SELECT id FROM plm.document_iteration WHERE revision_id = @rid AND iteration = @n", conn, tx))
            {
                cmd.Parameters.AddWithValue("rid", m.Ctx.RevisionId);
                cmd.Parameters.AddWithValue("n", next);
                iterationId = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
            }
            results.Add(new CadCheckedIn(m.Head.MasterId, m.Head.Number, m.Ctx.Revision, m.Ctx.LatestIteration, next, iterationId));
            await audit.AppendAsync(tx, Entry(subject, "document.checkin", AuditOutcome.Success, m.Head.Number, correlationId, [.. step.Details, ("set", "true")]), ct);
            await DocumentAwareness.AfterAsync(notifications, conn, tx, "document.checkin", m.Head.Number, m.Head.Markings, subject, step.Details, ct);
        }
        if (inTransaction is not null) await inTransaction(conn, tx, results, ct);
        await tx.CommitAsync(ct);
        return Outcome<IReadOnlyList<CadCheckedIn>>.Ok(results);
    }

    internal async Task<Outcome<IReadOnlyList<string>>> UndoCheckOutSetAsync(Subject subject, IReadOnlyList<string> numbers, string? correlationId, CancellationToken ct)
    {
        var (list, bad) = NormalizeSet<IReadOnlyList<string>>(numbers);
        if (bad is not null) return bad;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (members, refusal) = await LockSetAsync<IReadOnlyList<string>>(conn, tx, subject, list!, "document.undo_checkout", PlmAction.Modify, true, correlationId, ct);
        if (refusal is not null) return refusal;
        var released = new List<string>();
        foreach (var m in members!)
        {
            if (m.Ctx.CheckoutUser is null) continue;
            var isAdmin = AccessPolicy.Evaluate(subject, m.Ctx.Markings, PlmAction.Administer).Permitted;
            if (m.Ctx.CheckoutUser != subject.Id && !isAdmin)
            {
                await tx.RollbackAsync(ct);
                await audit.AppendAsync(Entry(subject, "document.undo_checkout", AuditOutcome.Denied, m.Head.Number, correlationId, ("reasons", "checkout.not_owner"), ("set", "true")), ct);
                return Outcome<IReadOnlyList<string>>.Forbidden(["checkout.not_owner"]);
            }
            await using (var cmd = new NpgsqlCommand("DELETE FROM plm.document_checkout WHERE revision_id = @rid", conn, tx))
            {
                cmd.Parameters.AddWithValue("rid", m.Ctx.RevisionId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await audit.AppendAsync(tx, Entry(subject, "document.undo_checkout", AuditOutcome.Success, m.Head.Number, correlationId,
                ("checkedOutBy", m.Ctx.CheckoutUserName ?? m.Ctx.CheckoutUser), ("override", (m.Ctx.CheckoutUser != subject.Id).ToString()), ("set", "true")), ct);
            released.Add(m.Head.Number);
        }
        await tx.CommitAsync(ct);
        return Outcome<IReadOnlyList<string>>.Ok(released);
    }
}
