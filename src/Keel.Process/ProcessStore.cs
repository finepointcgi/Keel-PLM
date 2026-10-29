using System.Data.Common;
using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;
using NpgsqlTypes;

namespace Keel.Process;

/// <summary>A plan with its requested revision, that revision's latest iteration and the open (working copy) state.</summary>
internal sealed record PlanHead(
    Guid Id, string Number, Guid MbomId, string MbomNumber, string PartNumber, string PartName, string Plant, string Description, Markings Markings, DateTimeOffset CreatedAt, string CreatedBy,
    Guid RevisionId, string Revision, string State, bool IsLatest, int LatestIteration, string BoundRevision, string BoundSha, Guid IterationId, bool IsOpen, string? OpenBy, string? OpenByName,
    string ContentJson, string? ContentSha)
{
    public string Label => $"{Revision}.{LatestIteration.ToString(CultureInfo.InvariantCulture)}";
}

internal sealed record ToolRow(
    Guid Id, string Number, string Name, string Kind, string Plant, string? Description, bool RequiresCalibration, int? IntervalDays, DateOnly? LastCalibratedOn, DateOnly? DueOn, string Status,
    int Revision, DateTimeOffset CreatedAt, string CreatedBy, Markings Markings);

internal static class ProcessStore
{
    public const string PlanObjectType = "process_plan";
    public const string ToolObjectType = "tool";

    private const string HeadColumns =
        "p.id, p.number, p.mbom_id, p.mbom_number, p.part_number, p.part_name, p.plant, p.description, p.created_at, p.created_by, r.id, r.revision, r.lifecycle_state, r.is_latest, r.latest_iteration, " +
        "r.bound_mbom_revision, r.bound_mbom_sha, i.id, i.is_open, i.opened_by, i.opened_by_name, i.content::text, i.content_sha256";

    private static PlanHead ReadHead(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), MarkingColumns.Read(r, 23), r.GetFieldValue<DateTimeOffset>(8), r.GetString(9),
        r.GetGuid(10), r.GetString(11), r.GetString(12), r.GetBoolean(13), r.GetInt32(14), r.GetString(15), r.GetString(16), r.GetGuid(17), r.GetBoolean(18),
        r.IsDBNull(19) ? null : r.GetString(19), r.IsDBNull(20) ? null : r.GetString(20), r.GetString(21), r.IsDBNull(22) ? null : r.GetString(22));

    public static async Task<PlanHead?> LoadByNumberAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string number, string? revision, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT {HeadColumns}, {MarkingColumns.Select("p")}
            FROM proc.plan p
            JOIN proc.plan_revision r ON r.plan_id = p.id AND ((@rev::text IS NULL AND r.is_latest) OR r.revision = @rev)
            JOIN proc.plan_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            WHERE p.number = @n{(forUpdate ? " FOR UPDATE OF p" : "")}
            """, conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        cmd.Parameters.Add(new NpgsqlParameter("rev", NpgsqlDbType.Text) { Value = (object?)revision ?? DBNull.Value });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadHead(r) : null;
    }

    public static async Task<PlanHead?> LoadByRevisionAsync(DbConnection conn, DbTransaction? tx, Guid revisionId, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT {HeadColumns}, {MarkingColumns.Select("p")}
            FROM proc.plan_revision r
            JOIN proc.plan p ON p.id = r.plan_id
            JOIN proc.plan_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            WHERE r.id = @rid{(forUpdate ? " FOR UPDATE OF p" : "")}
            """, (NpgsqlConnection)conn, (NpgsqlTransaction?)tx);
        cmd.Parameters.AddWithValue("rid", revisionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadHead(r) : null;
    }

    public static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string objectType, string? id, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s),
        Action = action,
        Outcome = outcome,
        ObjectType = objectType,
        ObjectId = id,
        CorrelationId = correlationId,
        Details = AuditDetails.Of(details),
    };

    public static string Who(Subject s) => s.Username ?? s.Id;
    public static string Fmt(int n) => n.ToString(CultureInfo.InvariantCulture);
    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
    public static string NextNumber(string prefix, long seq) => prefix + seq.ToString("D6", CultureInfo.InvariantCulture);

    public static async Task<long> NextValAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sequence, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT nextval('{sequence}')", conn, tx);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public static string Like(string text) => "%" + text.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

    // ---------------------------------------------------------------- tools

    public static string ToolSelect(string a = "t") =>
        $"{a}.id, {a}.number, {a}.name, {a}.kind, {a}.plant, {a}.description, {a}.requires_calibration, {a}.interval_days, {a}.last_calibrated_on, {a}.due_on, {a}.status, {a}.revision, " +
        $"{a}.created_at, {a}.created_by, {MarkingColumns.Select(a)}";

    public static ToolRow ReadTool(NpgsqlDataReader r, int o = 0) => new(
        r.GetGuid(o), r.GetString(o + 1), r.GetString(o + 2), r.GetString(o + 3), r.GetString(o + 4), r.IsDBNull(o + 5) ? null : r.GetString(o + 5), r.GetBoolean(o + 6),
        r.IsDBNull(o + 7) ? null : r.GetInt32(o + 7), r.IsDBNull(o + 8) ? null : r.GetFieldValue<DateOnly>(o + 8), r.IsDBNull(o + 9) ? null : r.GetFieldValue<DateOnly>(o + 9), r.GetString(o + 10),
        r.GetInt32(o + 11), r.GetFieldValue<DateTimeOffset>(o + 12), r.GetString(o + 13), MarkingColumns.Read(r, o + 14));

    public static async Task<ToolRow?> LoadToolAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string number, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {ToolSelect()} FROM proc.tool t WHERE t.number = @n{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadTool(r) : null;
    }

    public static async Task<ToolRow?> LoadToolByIdAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {ToolSelect()} FROM proc.tool t WHERE t.id = @id{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadTool(r) : null;
    }

    public static async Task<IReadOnlySet<string>> ActiveWorkCentersAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string plant, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand("SELECT code FROM proc.work_center WHERE plant = @p AND is_active", conn, tx);
        cmd.Parameters.AddWithValue("p", plant);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetString(0));
        return set;
    }

    public static async Task<Dictionary<string, string>> WorkCenterNamesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string plant, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand("SELECT code, name FROM proc.work_center WHERE plant = @p", conn, tx);
        cmd.Parameters.AddWithValue("p", plant);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetString(0)] = r.GetString(1);
        return map;
    }
}
