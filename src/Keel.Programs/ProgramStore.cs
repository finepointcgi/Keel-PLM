using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Programs;

/// <summary>What every program operation needs of a program: identity, state, manager and markings. Callers evaluate the access policy themselves.</summary>
internal sealed record PHead(Guid Id, string Code, string Name, string Status, string ManagerSub, string ManagerLogin, string ManagerName, Markings Markings)
{
    public bool Closed => Status is "Complete" or "Cancelled";
}

/// <summary>A schedule item as a key lookup. Callers evaluate the access policy on <see cref="Markings"/>.</summary>
internal sealed record IHead(Guid Id, Guid ProgramId, string Key, string Kind, Guid? ParentId, string Title, string Status, DateOnly PlannedStart, DateOnly PlannedEnd,
    string? GateNumber, string? GateState, string? GateType, int Submission, string? OwnerSub, Markings Markings);

internal sealed record DirectoryUser(string Sub, string Username, string DisplayName, Subject Subject);

/// <summary>Plain SQL shared by the program services and the review gateway.</summary>
internal static class ProgramStore
{
    public const string ObjectType = "program";

    public static NpgsqlCommand Cmd(DbConnection conn, DbTransaction? tx, string sql) => new(sql, (NpgsqlConnection)conn, (NpgsqlTransaction?)tx);
    public static DateTimeOffset Utc(DbDataReader r, int i) => new(r.GetDateTime(i), TimeSpan.Zero);
    public static DateOnly Date(DbDataReader r, int i) => DateOnly.FromDateTime(r.GetDateTime(i));
    public static DateOnly? DateAt(DbDataReader r, int i) => r.IsDBNull(i) ? null : DateOnly.FromDateTime(r.GetDateTime(i));
    public static string? Str(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static string Who(Subject s) => s.Username ?? s.Id;
    public static string Int(long n) => n.ToString(CultureInfo.InvariantCulture);
    public static object Opt(string? s) => string.IsNullOrWhiteSpace(s) ? DBNull.Value : s.Trim();
    public static object OptDate(DateOnly? d) => d is null ? DBNull.Value : d.Value.ToDateTime(TimeOnly.MinValue);
    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private const string ProgramColumns = "p.id, p.code, p.name, p.status, p.manager_sub, p.manager_username, p.manager_name, ";

    public static async Task<PHead?> LoadProgramAsync(DbConnection conn, DbTransaction? tx, string code, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, $"SELECT {ProgramColumns}{MarkingColumns.Select("p")} FROM prg.program p WHERE lower(p.code) = lower(@c)" + (forUpdate ? " FOR UPDATE OF p" : ""));
        cmd.Parameters.AddWithValue("c", code);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new PHead(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), MarkingColumns.Read(r, 7)) : null;
    }

    private const string ItemColumns = "i.id, i.program_id, i.key, i.kind, i.parent_id, i.title, i.status, i.planned_start, i.planned_end, i.gate_number, i.gate_state, i.gate_type, i.submission_no, i.owner_sub, ";

    private static IHead ReadItem(DbDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.GetString(5), r.GetString(6),
        Date(r, 7), Date(r, 8), Str(r, 9), Str(r, 10), Str(r, 11), r.GetInt32(12), Str(r, 13), MarkingColumns.Read(r, 14));

    public static async Task<IHead?> LoadItemAsync(DbConnection conn, DbTransaction? tx, Guid programId, string key, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, $"SELECT {ItemColumns}{MarkingColumns.Select("i")} FROM prg.item i WHERE i.program_id = @p AND upper(i.key) = upper(@k)" + (forUpdate ? " FOR UPDATE OF i" : ""));
        cmd.Parameters.AddWithValue("p", programId);
        cmd.Parameters.AddWithValue("k", key);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadItem(r) : null;
    }

    public static async Task<IHead?> LoadItemByIdAsync(DbConnection conn, DbTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, $"SELECT {ItemColumns}{MarkingColumns.Select("i")} FROM prg.item i WHERE i.id = @id" + (forUpdate ? " FOR UPDATE OF i" : ""));
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadItem(r) : null;
    }

    public static async Task<IHead?> LoadItemByGateAsync(DbConnection conn, DbTransaction? tx, string gateNumber, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, $"SELECT {ItemColumns}{MarkingColumns.Select("i")} FROM prg.item i WHERE i.gate_number = @g" + (forUpdate ? " FOR UPDATE OF i" : ""));
        cmd.Parameters.AddWithValue("g", gateNumber);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadItem(r) : null;
    }

    public static async Task<PHead?> LoadProgramByIdAsync(DbConnection conn, DbTransaction? tx, Guid id, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, $"SELECT {ProgramColumns}{MarkingColumns.Select("p")} FROM prg.program p WHERE p.id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new PHead(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), MarkingColumns.Read(r, 7)) : null;
    }

    public static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string objectType, string? objectId, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s), Action = action, Outcome = outcome, ObjectType = objectType, ObjectId = objectId, CorrelationId = correlationId, Details = AuditDetails.Of(details),
    };

    public static AuditEntry SystemEntry(string action, string objectType, string objectId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.System, Action = action, Outcome = AuditOutcome.Success, ObjectType = objectType, ObjectId = objectId, Details = AuditDetails.Of(details),
    };

    /// <summary>Finds a user of the directory (written by the workflow module at sign-in) by login name or subject.</summary>
    public static async Task<DirectoryUser?> FindUserAsync(DbConnection conn, DbTransaction? tx, string usernameOrSub, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            """
            SELECT sub, username, display_name, org, roles, us_person, citizenship, programs, export_authorizations, cui_categories
            FROM identity.user_directory WHERE sub = @k OR lower(username) = lower(@k) ORDER BY (sub = @k) DESC LIMIT 1
            """);
        cmd.Parameters.AddWithValue("k", usernameOrSub);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        ImmutableSortedSet<string> Set(int i) => r.GetFieldValue<string[]>(i).ToImmutableSortedSet(StringComparer.Ordinal);
        var subject = new Subject
        {
            Id = r.GetString(0), Username = r.GetString(1), DisplayName = r.GetString(2), Org = r.IsDBNull(3) ? null : r.GetString(3), Roles = Set(4), UsPerson = r.GetBoolean(5),
            Citizenship = r.IsDBNull(6) ? null : r.GetString(6), Programs = Set(7), ExportAuthorizations = Set(8), CuiCategories = Set(9),
        };
        return new DirectoryUser(subject.Id, subject.Username!, subject.DisplayName, subject);
    }

    public static Guid SubmissionId(Guid itemId, int submission) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"keel-program-gate-submission:{itemId:D}:{submission}")).AsSpan(0, 16));

    public static byte[] Sha256(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    public static string Hex(string text) => Convert.ToHexStringLower(Sha256(text));
}
