using System.Text.Json;
using System.Text.Json.Serialization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Admin;

/// <summary>What a configuration change decided inside its transaction: the outcome plus the audit details (before and after).</summary>
internal sealed class Step<T>
{
    public required Outcome<T> Result { get; init; }
    public (string, string?)[] Details { get; init; } = [];

    public static Step<T> Ok(T value, params (string, string?)[] details) => new() { Result = Outcome<T>.Ok(value), Details = details };
    public static Step<T> Invalid(params string[] errors) => new() { Result = Outcome<T>.Invalid(errors) };
    public static Step<T> Invalid(IEnumerable<string> errors) => new() { Result = Outcome<T>.Invalid(errors) };
    public static Step<T> Conflict(string message) => new() { Result = Outcome<T>.Conflict(message) };
    public static Step<T> NotFound() => new() { Result = Outcome<T>.NotFound() };
}

/// <summary>
/// The shared skeleton of every configuration command (ADR-0022): administrators only (a denial is audited), one transaction for the
/// change and its audit event, and a single canonical JSON form for the before/after values the history page diffs.
/// </summary>
public sealed class AdminRunner(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock)
{
    public const string ObjectType = "configuration";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    internal NpgsqlDataSource DataSource => dataSource;
    internal TimeProvider Clock => clock;

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static string Who(Subject s) => s.Username ?? s.Id;

    internal static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string? objectId, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s),
        Action = action,
        Outcome = outcome,
        ObjectType = ObjectType,
        ObjectId = objectId,
        CorrelationId = correlationId,
        Details = AuditDetails.Of(details),
    };

    /// <summary>Audits and returns false when the subject is not an administrator.</summary>
    internal async Task<bool> AdminOnlyAsync(Subject subject, string action, string? objectId, string? correlationId, CancellationToken ct)
    {
        if (AccessPolicy.HasRoleFor(subject, PlmAction.Administer)) return true;
        await audit.AppendAsync(Entry(subject, action, AuditOutcome.Denied, objectId, correlationId, ("reasons", AccessPolicy.RoleMissing)), ct);
        return false;
    }

    /// <summary>A read-only call: administrators only, no audit event on success (configuration is not marked data).</summary>
    internal async Task<Outcome<T>> ReadAsync<T>(Subject subject, string action, string? objectId, Func<NpgsqlConnection, Task<T>> body, CancellationToken ct)
    {
        if (!await AdminOnlyAsync(subject, action, objectId, null, ct)) return Outcome<T>.Forbidden([AccessPolicy.RoleMissing]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return Outcome<T>.Ok(await body(conn));
    }

    /// <summary>A change: runs <paramref name="body"/> in a transaction, writes the audit event in the same transaction, commits.</summary>
    internal async Task<Outcome<T>> ChangeAsync<T>(Subject subject, string action, string? objectId, string? correlationId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<Step<T>>> body, CancellationToken ct)
    {
        if (!await AdminOnlyAsync(subject, action, objectId, correlationId, ct)) return Outcome<T>.Forbidden([AccessPolicy.RoleMissing]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Step<T> step;
        try
        {
            step = await body(conn, tx);
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation
                                              or PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.RaiseException)
        {
            // The database constraint is the safety net (ADR-0022): surface its message instead of a server error.
            await tx.RollbackAsync(ct);
            return Outcome<T>.Conflict(e.MessageText);
        }
        if (!step.Result.IsOk)
        {
            await tx.RollbackAsync(ct);
            return step.Result;
        }
        await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Success, objectId, correlationId, step.Details), ct);
        await tx.CommitAsync(ct);
        return step.Result;
    }

    internal static NpgsqlCommand Cmd(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, params (string, object?)[] parameters)
    {
#pragma warning disable CA2100 // Callers pass constant SQL text; values travel as parameters.
        var cmd = new NpgsqlCommand(sql, conn, tx);
#pragma warning restore CA2100
        foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }
}
