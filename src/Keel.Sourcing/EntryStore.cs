using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Sourcing;

/// <summary>A list entry with the part and supplier it joins. Callers must apply the part's and the supplier's read filters (or check them) before showing anything of it.</summary>
internal sealed record EntryRow(
    Guid Id, string Number, string Kind, Guid PartId, string PartNumber, string PartName, Markings PartMarkings, string? RevisionScope, Guid SupplierId, string SupplierNumber,
    string SupplierName, string SupplierStatus, bool SupplierForeign, Markings SupplierMarkings, Guid? MpnId, string? Mpn, string? ManufacturerNumber, string? ManufacturerName,
    string Status, string ApprovalState, DateOnly? From, DateOnly? To, string? VendorPartNumber, int? LeadTimeDays, decimal? Moq, string? Currency, string PriceBreaksJson,
    string? Note, string? Reason, int Revision, DateTimeOffset CreatedAt, string CreatedBy)
{
    public EntryView ToView(DateOnly today) => new(
        Id, Number, Kind, PartNumber, RevisionScope, SupplierNumber, SupplierName, SupplierStatus, SupplierForeign, Mpn, ManufacturerNumber, ManufacturerName, Status, ApprovalState,
        From, To, VendorPartNumber, LeadTimeDays, Moq, Currency, SourcingStore.ParsePriceBreaks(PriceBreaksJson), Note, Reason,
        SourcingRules.IsEffective(ApprovalState, From, To, today), CreatedBy, CreatedAt, Revision);
}

internal static class EntryStore
{
    public static string Select { get; } =
        $"""
        e.id, e.number, e.kind, e.part_master_id, pm.number, pm.name, {MarkingColumns.Select("pm")}, e.revision_scope, e.supplier_id, s.number, s.name, s.status, s.is_foreign, {MarkingColumns.Select("s")},
        e.mpn_id, mp.mpn, ms.number, ms.name, e.status, e.approval_state, e.effective_from, e.effective_to, e.vendor_part_number, e.lead_time_days, e.moq, e.currency, e.price_breaks::text,
        e.note, e.reason, e.revision, e.created_at, e.created_by
        """;

    public const string From =
        """
        src.source_entry e JOIN plm.part_master pm ON pm.id = e.part_master_id JOIN src.supplier s ON s.id = e.supplier_id
        LEFT JOIN src.mpn mp ON mp.id = e.mpn_id LEFT JOIN src.supplier ms ON ms.id = mp.manufacturer_id
        """;

    /// <summary>
    /// The same join for READS: the manufacturer of a vendor's manufacturer part is joined only when the caller may read it (its name is then null, never its
    /// existence withheld by dropping the row: the entry itself is readable through its part and vendor).
    /// </summary>
    public static string FromFiltered(string manufacturerFilterSql) =>
        $"""
        src.source_entry e JOIN plm.part_master pm ON pm.id = e.part_master_id JOIN src.supplier s ON s.id = e.supplier_id
        LEFT JOIN src.mpn mp ON mp.id = e.mpn_id LEFT JOIN src.supplier ms ON ms.id = mp.manufacturer_id AND {manufacturerFilterSql}
        """;

    public static EntryRow Read(NpgsqlDataReader r)
    {
        static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        static DateOnly? D(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateOnly>(i);
        return new EntryRow(
            r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetString(4), r.GetString(5), MarkingColumns.Read(r, 6), S(r, 16), r.GetGuid(17), r.GetString(18),
            r.GetString(19), r.GetString(20), r.GetBoolean(21), MarkingColumns.Read(r, 22), r.IsDBNull(32) ? null : r.GetGuid(32), S(r, 33), S(r, 34), S(r, 35),
            r.GetString(36), r.GetString(37), D(r, 38), D(r, 39), S(r, 40), r.IsDBNull(41) ? null : r.GetInt32(41), r.IsDBNull(42) ? null : r.GetDecimal(42), S(r, 43), r.GetString(44),
            S(r, 45), S(r, 46), r.GetInt32(47), r.GetFieldValue<DateTimeOffset>(48), r.GetString(49));
    }

    public static async Task<EntryRow?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string number, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {Select} FROM {From} WHERE e.number = @n{(forUpdate ? " FOR UPDATE OF e" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public static async Task<EntryRow?> LoadByIdAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {Select} FROM {From} WHERE e.id = @id{(forUpdate ? " FOR UPDATE OF e" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public static async Task SetStateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, string state, string by, DateTimeOffset now, string? reason, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE src.source_entry SET approval_state = @s, reason = COALESCE(@reason, reason), revision = revision + 1, updated_at = @now, updated_by = @by WHERE id = @id", conn, tx);
        cmd.Parameters.AddWithValue("s", state);
        cmd.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("by", by);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Why an entry cannot be approved now, or null. A positive entry needs a supplier that is approved or conditional; the export-control rule is checked against the part's
    /// markings AS THEY ARE NOW (they may have been raised since the entry was drafted).
    /// </summary>
    public static string? CheckApprovable(EntryRow e, DateOnly today)
    {
        if (SourcingRules.CheckSupplierForPart(e.SupplierForeign, e.PartMarkings) is not null)
            return "A foreign supplier cannot be selected for an ITAR-marked part.";
        if (e.Status != EntryStatuses.Disapproved && e.SupplierStatus is not (SupplierStatuses.Approved or SupplierStatuses.Conditional))
            return $"The supplier is {SupplierStatuses.Label(e.SupplierStatus).ToLowerInvariant()}; only an approved or conditional supplier can be listed as a source.";
        if (e.To is { } to && to < today) return "The entry has already ended.";
        return null;
    }
}
