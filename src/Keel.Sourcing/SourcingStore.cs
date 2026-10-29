using System.Globalization;
using System.Text.Json;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Sourcing;

/// <summary>A supplier row with its markings. Internal: callers outside the module get <see cref="SupplierSummary"/> or <see cref="SupplierDetail"/> after an access check.</summary>
internal sealed record SupplierRow(
    Guid Id, string Number, string Name, string? CageCode, string? DunsId, string Country, string[] Roles, string Status, string? ProposedStatus, bool InReview, string? StatusReason,
    bool IsForeign, string? ClearanceProfile, int Revision, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset? UpdatedAt, string? UpdatedBy, Markings Markings)
{
    public SupplierSummary ToSummary() => new(Id, Number, Name, CageCode, Country, Roles, Status, InReview, IsForeign, Markings.BannerText(), Markings);
}

/// <summary>A part master as sourcing needs it: identity, state, source and the markings every decision is made from.</summary>
internal sealed record PartRef(Guid Id, string Number, string Name, string State, string Source, string? LatestRevision, Markings Markings);

/// <summary>SQL and small helpers shared by the services of the module. Every statement here either is a key lookup or takes an access filter from its caller.</summary>
internal static class SourcingStore
{
    public const string SupplierObjectType = "supplier";
    public const string EntryObjectType = "source_entry";

    public static string SupplierSelect(string a = "s") =>
        $"{a}.id, {a}.number, {a}.name, {a}.cage_code, {a}.duns_id, {a}.country, {a}.roles, {a}.status, {a}.proposed_status, {a}.in_review, {a}.status_reason, {a}.is_foreign, " +
        $"{a}.clearance_profile, {a}.revision, {a}.created_at, {a}.created_by, {a}.updated_at, {a}.updated_by, {MarkingColumns.Select(a)}";

    public static SupplierRow ReadSupplier(NpgsqlDataReader r, int o = 0) => new(
        r.GetGuid(o), r.GetString(o + 1), r.GetString(o + 2), Str(r, o + 3), Str(r, o + 4), r.GetString(o + 5), (string[])r.GetValue(o + 6), r.GetString(o + 7), Str(r, o + 8), r.GetBoolean(o + 9),
        Str(r, o + 10), r.GetBoolean(o + 11), Str(r, o + 12), r.GetInt32(o + 13), r.GetFieldValue<DateTimeOffset>(o + 14), r.GetString(o + 15),
        r.IsDBNull(o + 16) ? null : r.GetFieldValue<DateTimeOffset>(o + 16), Str(r, o + 17), MarkingColumns.Read(r, o + 18));

    public static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static async Task<SupplierRow?> LoadSupplierAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string number, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {SupplierSelect()} FROM src.supplier s WHERE s.number = @n{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadSupplier(r) : null;
    }

    public static async Task<SupplierRow?> LoadSupplierByIdAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {SupplierSelect()} FROM src.supplier s WHERE s.id = @id{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadSupplier(r) : null;
    }

    public static async Task<PartRef?> LoadPartAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string number, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT m.id, m.number, m.name, m.source,
                   (SELECT r.revision FROM plm.part_revision r WHERE r.master_id = m.id AND r.is_latest),
                   (SELECT r.lifecycle_state FROM plm.part_revision r WHERE r.master_id = m.id AND r.is_latest),
                   {MarkingColumns.Select("m")}
            FROM plm.part_master m WHERE m.number = @n{(forUpdate ? " FOR UPDATE OF m" : "")}
            """, conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new PartRef(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(5) ? "" : r.GetString(5), r.GetString(3), Str(r, 4), MarkingColumns.Read(r, 6)) : null;
    }

    public static async Task<PartRef?> LoadPartByIdAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT m.number FROM plm.part_master m WHERE m.id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        var number = (string?)await cmd.ExecuteScalarAsync(ct);
        return number is null ? null : await LoadPartAsync(conn, tx, number, false, ct);
    }

    /// <summary>The clearance profile an administrator assigned (read from the integration module's table), or null.</summary>
    public static async Task<ClearanceProfile?> LoadProfileAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string? name, CancellationToken ct)
    {
        if (name is null) return null;
        await using var cmd = new NpgsqlCommand(
            "SELECT name, recipient_category, us_persons_only, allowed_jurisdictions, allowed_eccns, allowed_usml, allowed_cui, allowed_programs, allowed_statements, export_authorizations FROM integration.clearance_profile WHERE name = @n AND active", conn, tx);
        cmd.Parameters.AddWithValue("n", name);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? SourcingRules.Profile(r.GetString(0), r.GetString(1), r.GetBoolean(2), (string[])r.GetValue(3), (string[])r.GetValue(4), (string[])r.GetValue(5), (string[])r.GetValue(6),
                (string[])r.GetValue(7), (string[])r.GetValue(8), (string[])r.GetValue(9))
            : null;
    }

    public static async Task<IReadOnlyList<string>> ProfileNamesAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT name FROM integration.clearance_profile WHERE active ORDER BY name");
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    public static string NextNumber(string prefix, long seq) => prefix + seq.ToString("D6", CultureInfo.InvariantCulture);

    public static async Task<long> NextValAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sequence, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT nextval('{sequence}')", conn, tx);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
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

    public static string Like(string text) => "%" + text.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

    public static string PriceBreaksJson(IReadOnlyList<PriceBreak>? breaks) =>
        JsonSerializer.Serialize((breaks ?? []).Select(b => new { quantity = b.Quantity, unitPrice = b.UnitPrice }));

    public static IReadOnlyList<PriceBreak> ParsePriceBreaks(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(e => new PriceBreak(e.GetProperty("quantity").GetDecimal(), e.GetProperty("unitPrice").GetDecimal()))];
    }

    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
}
