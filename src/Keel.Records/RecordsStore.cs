using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Records;

/// <summary>SQL and helpers shared by the services of the module. Every statement is either a key lookup or takes an access filter from its caller.</summary>
internal static class RecordsStore
{
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

    public static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public static string NextNumber(string prefix, long seq) => prefix + seq.ToString("D6", CultureInfo.InvariantCulture);

    public static async Task<long> NextValAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sequence, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT nextval('{sequence}')", conn, tx);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Serializes everything that decides whether an object is held with everything that disposes of one: placing a hold, re-evaluating holds and executing a
    /// disposition take this lock, so a hold placed before the disposition commits is always seen by it (the tombstone trigger is the backstop).
    /// </summary>
    public static async Task TakeGateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('keel-records-gate', 0))", conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The markings of records data that names no object (schedules, holds): unmarked, owned by the creator's organization.</summary>
    public static Markings Neutral(string owningOrg) => Markings.Create(ExportJurisdiction.None, DistributionStatement.A, string.IsNullOrWhiteSpace(owningOrg) ? "RECORDS" : owningOrg).Value;

    public static string OrgOf(Subject s) => string.IsNullOrWhiteSpace(s.Org) ? "RECORDS" : s.Org;

    // ---------------------------------------------------------------- the published schedule

    public sealed record PublishedSchedule(Guid Id, int Version, IReadOnlyList<ClassRule> Rules);

    public static async Task<PublishedSchedule?> LoadPublishedAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        Guid id; int version;
        await using (var cmd = new NpgsqlCommand("SELECT id, version FROM rec.schedule WHERE status = 'published'", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            id = r.GetGuid(0); version = r.GetInt32(1);
        }
        return new PublishedSchedule(id, version, await LoadRulesAsync(conn, tx, id, ct));
    }

    public static async Task<IReadOnlyList<ClassRule>> LoadRulesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid scheduleId, CancellationToken ct)
    {
        var rules = new List<ClassRule>();
        await using var cmd = new NpgsqlCommand(
            "SELECT code, name, priority, match_kinds, match_types, match_programs, match_jurisdictions, match_cui, match_statements, retention_months, trigger_event, disposition FROM rec.record_class WHERE schedule_id = @s", conn, tx);
        cmd.Parameters.AddWithValue("s", scheduleId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rules.Add(new ClassRule(r.GetString(0), r.GetString(1), r.GetInt32(2), (string[])r.GetValue(3), (string[])r.GetValue(4), (string[])r.GetValue(5), (string[])r.GetValue(6),
                (string[])r.GetValue(7), (string[])r.GetValue(8), r.GetInt32(9), r.GetString(10), r.GetString(11)));
        return rules;
    }

    public static async Task<Func<string, string, DateOnly?>> LoadProgramEventsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var events = new Dictionary<(string, string), DateOnly>();
        await using var cmd = new NpgsqlCommand("SELECT program, event, occurred_on FROM rec.program_event_current", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) events[(r.GetString(0), r.GetString(1))] = r.GetFieldValue<DateOnly>(2);
        return (program, trigger) => events.TryGetValue((program, trigger), out var d) ? d : null;
    }

    // ---------------------------------------------------------------- object facts

    private static (string Master, string Revision, string Type) Tables(string kind) => kind switch
    {
        RecordKinds.Document => ("plm.document_master", "plm.document_revision", "document_type"),
        RecordKinds.Part => ("plm.part_master", "plm.part_revision", "part_type"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown record kind."),
    };

    /// <summary>The facts query for one kind. Table names come from a closed switch, never from input.</summary>
    public static string FactsSelect(string kind)
    {
        var (m, rv, type) = Tables(kind);
        return
            $"""
            SELECT x.id, x.number, x.name, x.{type}, {MarkingColumns.Select("x")},
                   (SELECT max(r.state_changed_at) FROM {rv} r WHERE r.master_id = x.id AND r.lifecycle_state = 'Released'),
                   (SELECT r.state_changed_at FROM {rv} r WHERE r.master_id = x.id AND r.is_latest AND r.lifecycle_state = 'Obsolete'),
                   EXISTS (SELECT 1 FROM {rv} r WHERE r.master_id = x.id AND r.lifecycle_state NOT IN ('Released', 'Obsolete')),
                   (SELECT max(d.retain_until) FROM rec.review_deferral d WHERE d.object_kind = '{kind}' AND d.object_id = x.id)
            FROM {m} x
            """;
    }

    public static ObjectFacts ReadFacts(NpgsqlDataReader r, string kind) => new(
        kind, r.GetGuid(0), r.GetString(1), r.GetString(2), Str(r, 3), MarkingColumns.Read(r, 4),
        r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14), r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15), r.GetBoolean(16),
        r.IsDBNull(17) ? null : r.GetFieldValue<DateOnly>(17));

    public static async Task<ObjectFacts?> LoadFactsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string kind, string number, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(FactsSelect(kind) + " WHERE x.number = @n", conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadFacts(r, kind) : null;
    }

    public static async Task<ObjectFacts?> LoadFactsByIdAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string kind, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(FactsSelect(kind) + " WHERE x.id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadFacts(r, kind) : null;
    }

    public static async Task<bool> IsHeldAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string kind, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT rec.is_held(@k, @id)", conn, tx);
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("id", id);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public static async Task<bool> IsTombstonedAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string kind, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT rec.is_tombstoned(@k, @id)", conn, tx);
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("id", id);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>The marking columns bound to <c>@mk_*</c> parameters of a command.</summary>
    public static void AddMarkings(NpgsqlCommand cmd, Markings m)
    {
        foreach (var (k, v) in MarkingColumns.Parameters(m)) cmd.Parameters.AddWithValue(k, v);
    }
}
