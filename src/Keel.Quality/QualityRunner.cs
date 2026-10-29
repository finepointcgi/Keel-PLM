using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Quality;

/// <summary>What a mutation body gets: the locked record, its lifecycle and the time of the call.</summary>
internal sealed record QCtx(QHead Head, Lifecycle Lifecycle, DateTimeOffset Now)
{
    public string State => Head.State;
    public string StateLabel => Lifecycle.State(Head.State)?.Label ?? Head.State;
    public bool Editable => !Lifecycle.IsLocked(Head.State);
    public bool Final => QualityShared.IsFinal(Lifecycle, Head.State);
}

internal sealed record QStep(OutcomeKind Kind, IReadOnlyList<string> Errors, (string, string?)[] Details)
{
    public List<string> Notices { get; } = [];
    public (string, string?)[]? DenialDetails { get; init; }

    public static QStep Done(params (string, string?)[] details) => new(OutcomeKind.Ok, [], details);
    public static QStep Fail(string message) => new(OutcomeKind.Conflict, [message], []);
    public static QStep Invalid(params string[] errors) => new(OutcomeKind.Invalid, errors, []);
    public static QStep Forbid(params string[] reasons) => new(OutcomeKind.Forbidden, reasons, []);
}

/// <summary>
/// The mutation scaffold shared by the three services (like the change service): load and lock the record, 404 when it cannot be read (denial audited),
/// 403 when the action is not permitted (audited), run the body and write its audit event in the same transaction as its change.
/// </summary>
internal sealed class QualityRunner(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock)
{
    public NpgsqlDataSource DataSource => dataSource;
    public IAuditLog Audit => audit;
    public TimeProvider Clock => clock;

    public async Task<Outcome<T>> MutateAsync<T>(
        Subject subject, string kind, string number, string action, string? correlationId, PlmAction permission,
        Func<QCtx, NpgsqlConnection, NpgsqlTransaction, Task<QStep>> body,
        Func<NpgsqlConnection, NpgsqlTransaction, Guid, IReadOnlyList<string>, Task<T?>> loadDetail, CancellationToken ct) where T : class
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await QualityStore.LoadByNumberAsync(conn, tx, kind, number, forUpdate: true, ct);
        if (head is null) return Outcome<T>.NotFound();

        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, QualityStore.Entry(subject, action, AuditOutcome.Denied, kind, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.NotFound();
        }
        var decision = permission == PlmAction.Read ? read : AccessPolicy.Evaluate(subject, head.Markings, permission);
        if (!decision.Permitted)
        {
            await audit.AppendAsync(tx, QualityStore.Entry(subject, action, AuditOutcome.Denied, kind, number, correlationId, ("reasons", string.Join(",", decision.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.Forbidden(decision.Reasons);
        }

        var lifecycle = await Lifecycle.LoadAsync(conn, tx, head.LifecycleId, ct) ?? throw new InvalidOperationException($"Lifecycle {head.LifecycleId} missing.");
        QStep step;
        try { step = await body(new QCtx(head, lifecycle, clock.GetUtcNow()), conn, tx); }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.CheckViolation or PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.InsufficientPrivilege)
        {
            // A database guard refused what the service did not foresee; the message of the guard is written for people.
            await tx.RollbackAsync(ct);
            return Outcome<T>.Conflict(e.MessageText);
        }
        if (step.Kind != OutcomeKind.Ok)
        {
            await tx.RollbackAsync(ct);
            if (step.Kind == OutcomeKind.Forbidden || step.DenialDetails is not null)
                await audit.AppendAsync(QualityStore.Entry(subject, action, AuditOutcome.Denied, kind, number, correlationId, step.DenialDetails ?? [("reasons", string.Join(",", step.Errors))]), ct);
            if (step.Kind == OutcomeKind.Forbidden) return Outcome<T>.Forbidden(step.Errors);
            return step.Kind == OutcomeKind.Invalid ? Outcome<T>.Invalid(step.Errors) : Outcome<T>.Conflict(step.Errors[0]);
        }
        await audit.AppendAsync(tx, QualityStore.Entry(subject, action, AuditOutcome.Success, kind, number, correlationId, step.Details), ct);
        var detail = await loadDetail(conn, tx, head.Id, step.Notices);
        await tx.CommitAsync(ct);
        return Outcome<T>.Ok(detail!);
    }

    /// <summary>Single-record read with the same 404 rule; the loader runs only for a reader.</summary>
    public async Task<Outcome<T>> ReadAsync<T>(
        Subject subject, string kind, string number, string action, string? correlationId,
        Func<NpgsqlConnection, NpgsqlTransaction, Guid, IReadOnlyList<string>, Task<T?>> loadDetail, Func<T, (string, string?)[]> details, CancellationToken ct) where T : class
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await QualityStore.LoadByNumberAsync(conn, tx, kind, number, forUpdate: false, ct);
        if (head is null) return Outcome<T>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, QualityStore.Entry(subject, action, AuditOutcome.Denied, kind, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.NotFound();
        }
        var detail = await loadDetail(conn, tx, head.Id, []);
        await audit.AppendAsync(tx, QualityStore.Entry(subject, action, AuditOutcome.Success, kind, number, correlationId, details(detail!)), ct);
        await tx.CommitAsync(ct);
        return Outcome<T>.Ok(detail!);
    }

    public static string Int(int n) => n.ToString(CultureInfo.InvariantCulture);
}
