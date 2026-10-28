using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Bom;

/// <summary>The stored header of a baseline (<c>cfg.baseline</c>).</summary>
internal sealed record BaselineHead(
    Guid Id, string Number, string Name, string Type, string Purpose, string? Milestone, bool Signoff,
    Guid RootMasterId, Guid RootRevisionId, Guid RootIterationId, string Spec, bool ReleasedOnly, bool IncludeDocuments,
    string? SpecContext, long? SpecSerial, long? SpecLot, long? SpecDay, int ItemCount, int DocumentCount, string Sha256,
    string State, string? EstablishReason, DateTimeOffset? EstablishedAt, string? EstablishedBy, Guid? SupersededBy, string? StateReason,
    Markings Markings, DateTimeOffset CreatedAt, string CreatedBy)
{
    public string SpecText
    {
        get
        {
            var unit = new List<string>();
            if (SpecSerial is { } s) unit.Add($"serial {s.ToString(CultureInfo.InvariantCulture)}");
            if (SpecLot is { } l) unit.Add($"lot {l.ToString(CultureInfo.InvariantCulture)}");
            if (SpecDay is { } d) unit.Add($"date {EffectivityRange.DayOf(d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
            var spec = Spec switch
            {
                "LatestReleased" => "Latest released",
                "Effectivity" => $"As of {string.Join(", ", unit)}{(SpecContext is null ? "" : $" ({SpecContext})")}",
                _ => "Latest",
            };
            return spec + (ReleasedOnly ? ", released only" : "");
        }
    }
}

/// <summary>One stored item as the canonical hash and the workflow signature see it.</summary>
internal sealed record StoredItem(
    int Seq, int? ParentSeq, int Level, string Path, int? Find, decimal Quantity, string Unit, decimal Extended,
    Guid MasterId, Guid RevisionId, Guid IterationId, string Designators, string? Notes, string? Effectivity);

internal sealed record StoredDocument(int Seq, int ItemSeq, string LinkType, Guid MasterId, Guid RevisionId);

internal static class BaselineStore
{
    public const string Columns =
        """
        b.id, b.number, b.name, b.baseline_type, b.purpose, b.milestone, b.signoff, b.root_master_id, b.root_revision_id, b.root_iteration_id,
        b.spec, b.released_only, b.include_documents, b.spec_context, b.spec_serial, b.spec_lot, b.spec_day, b.item_count, b.document_count,
        b.content_sha256, b.lifecycle_state, b.establish_reason, b.established_at, b.established_by, b.superseded_by, b.state_reason,
        b.created_at, b.created_by,
        """;

    public static string Select => Columns + " " + MarkingColumns.Select("b");

    public static BaselineHead Read(DbDataReader r)
    {
        long? L(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new BaselineHead(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), S(5), r.GetBoolean(6),
            r.GetGuid(7), r.GetGuid(8), r.GetGuid(9), r.GetString(10), r.GetBoolean(11), r.GetBoolean(12), S(13), L(14), L(15), L(16),
            r.GetInt32(17), r.GetInt32(18), r.GetString(19), r.GetString(20), S(21), r.IsDBNull(22) ? null : r.GetFieldValue<DateTimeOffset>(22), S(23),
            r.IsDBNull(24) ? null : r.GetGuid(24), S(25), MarkingColumns.Read(r, 28), r.GetFieldValue<DateTimeOffset>(26), r.GetString(27));
    }

    public static async Task<BaselineHead?> LoadByNumberAsync(DbConnection conn, DbTransaction? tx, string number, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {Select} FROM cfg.baseline b WHERE b.number = @n{(forUpdate ? " FOR UPDATE OF b" : "")}", (NpgsqlConnection)conn, (NpgsqlTransaction?)tx);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public static async Task<BaselineHead?> LoadByIdAsync(DbConnection conn, DbTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {Select} FROM cfg.baseline b WHERE b.id = @id{(forUpdate ? " FOR UPDATE OF b" : "")}", (NpgsqlConnection)conn, (NpgsqlTransaction?)tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public static async Task<string> StateLabelAsync(DbConnection conn, DbTransaction? tx, string state, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT label FROM plm.lifecycle_state WHERE lifecycle_id = @l AND state = @s", (NpgsqlConnection)conn, (NpgsqlTransaction?)tx);
        cmd.Parameters.AddWithValue("l", BaselineStates.Lifecycle);
        cmd.Parameters.AddWithValue("s", state);
        return await cmd.ExecuteScalarAsync(ct) as string ?? state;
    }

    /// <summary>
    /// One lifecycle transition of a baseline. The transition must exist in the lifecycle data and carry the tag the caller's path requires
    /// (manual or workflow), so establishing with sign-off cannot be done through the manual path.
    /// </summary>
    public static async Task<GatewayResult> TransitionAsync(
        DbConnection conn, DbTransaction tx, BaselineHead head, string to, string via, string who, DateTimeOffset now,
        string? establishReason = null, string? stateReason = null, Guid? supersededBy = null, CancellationToken ct = default)
    {
        string? actualVia;
        await using (var t = new NpgsqlCommand("SELECT via FROM plm.lifecycle_transition WHERE lifecycle_id = @l AND from_state = @f AND to_state = @t", (NpgsqlConnection)conn, (NpgsqlTransaction)tx))
        {
            t.Parameters.AddWithValue("l", BaselineStates.Lifecycle);
            t.Parameters.AddWithValue("f", head.State);
            t.Parameters.AddWithValue("t", to);
            actualVia = (string?)await t.ExecuteScalarAsync(ct);
        }
        if (actualVia is null) return GatewayResult.Fail($"The baseline lifecycle has no transition from {head.State} to {to}.");
        if (actualVia != via) return GatewayResult.Fail($"{head.State} to {to} is a {actualVia} transition; it cannot be done through the {via} path.");
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE cfg.baseline SET lifecycle_state = @to, state_changed_at = @at, state_changed_by = @by,
                establish_reason = CASE WHEN @to = 'Established' THEN @er ELSE establish_reason END,
                established_at = CASE WHEN @to = 'Established' THEN @at ELSE established_at END,
                established_by = CASE WHEN @to = 'Established' THEN @by ELSE established_by END,
                state_reason = COALESCE(@sr, state_reason), superseded_by = COALESCE(@sup, superseded_by)
            WHERE id = @id AND lifecycle_state = @from
            """, (NpgsqlConnection)conn, (NpgsqlTransaction)tx);
        cmd.Parameters.AddWithValue("to", to);
        cmd.Parameters.AddWithValue("from", head.State);
        cmd.Parameters.AddWithValue("at", now);
        cmd.Parameters.AddWithValue("by", who);
        cmd.Parameters.Add(new NpgsqlParameter("er", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)establishReason ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("sr", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)stateReason ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("sup", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)supersededBy ?? DBNull.Value });
        cmd.Parameters.AddWithValue("id", head.Id);
        return await cmd.ExecuteNonQueryAsync(ct) == 1 ? GatewayResult.Ok : GatewayResult.Fail("The baseline changed state concurrently.");
    }

    // ---------------------------------------------------------------- canonical content hash

    /// <summary>
    /// SHA-256 over the canonical text of the baseline's items and pinned documents: the identity of the snapshot. Computed at creation,
    /// stored, recomputed from the live rows by the review (signatures cover the recomputed value) and by verification.
    /// </summary>
    public static string ContentHash(Guid rootMaster, Guid rootRevision, Guid rootIteration, string spec, IEnumerable<StoredItem> items, IEnumerable<StoredDocument> documents)
    {
        var sb = new StringBuilder(1 << 16);
        sb.Append("keel-baseline/1|").Append(rootMaster.ToString("N")).Append('|').Append(rootRevision.ToString("N")).Append('|').Append(rootIteration.ToString("N")).Append('|').Append(spec).Append('\n');
        foreach (var i in items.OrderBy(x => x.Seq))
        {
            sb.Append("i|").Append(i.Seq.ToString(CultureInfo.InvariantCulture)).Append('|').Append(i.ParentSeq?.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(i.Level.ToString(CultureInfo.InvariantCulture)).Append('|').Append(i.Path).Append('|').Append(i.Find?.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(Q(i.Quantity)).Append('|').Append(i.Unit).Append('|').Append(Q(i.Extended)).Append('|')
              .Append(i.MasterId.ToString("N")).Append('|').Append(i.RevisionId.ToString("N")).Append('|').Append(i.IterationId.ToString("N")).Append('|')
              .Append(i.Designators).Append('|').Append(i.Notes).Append('|').Append(i.Effectivity).Append('\n');
        }
        foreach (var d in documents.OrderBy(x => x.Seq))
            sb.Append("d|").Append(d.Seq.ToString(CultureInfo.InvariantCulture)).Append('|').Append(d.ItemSeq.ToString(CultureInfo.InvariantCulture)).Append('|').Append(d.LinkType).Append('|')
              .Append(d.MasterId.ToString("N")).Append('|').Append(d.RevisionId.ToString("N")).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string Q(decimal d) => d.ToString("0.######", CultureInfo.InvariantCulture);

    public static string EffectivityText(EffectivityRange? r) => r is null ? "" : $"{r.Kind}:{r.Context}:{r.From?.ToString(CultureInfo.InvariantCulture)}:{r.To?.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Reads the stored rows (no access filter: used by the owner of the rows to hash and verify, never shown).</summary>
    public static async Task<(List<StoredItem> Items, List<StoredDocument> Documents)> LoadContentAsync(DbConnection conn, DbTransaction? tx, Guid baselineId, CancellationToken ct)
    {
        var items = new List<StoredItem>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT seq, parent_seq, level, path, find_number, quantity, unit, ext_quantity, master_id, revision_id, iteration_id,
                   array_to_string(reference_designators, ','), notes, eff_kind, eff_context, eff_from, eff_to
            FROM cfg.baseline_item WHERE baseline_id = @id ORDER BY seq
            """, (NpgsqlConnection)conn, (NpgsqlTransaction?)tx))
        {
            cmd.Parameters.AddWithValue("id", baselineId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new StoredItem(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetInt32(1), r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt32(4),
                    r.GetDecimal(5), r.GetString(6), r.GetDecimal(7), r.GetGuid(8), r.GetGuid(9), r.GetGuid(10), r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12),
                    EffectivityText(EffectivityStore.ReadRange(r, 13)) is { Length: > 0 } e ? e : null));
        }
        var docs = new List<StoredDocument>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT seq, item_seq, link_type, document_master_id, document_revision_id FROM cfg.baseline_document WHERE baseline_id = @id ORDER BY seq", (NpgsqlConnection)conn, (NpgsqlTransaction?)tx))
        {
            cmd.Parameters.AddWithValue("id", baselineId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) docs.Add(new StoredDocument(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetGuid(3), r.GetGuid(4)));
        }
        return (items, docs);
    }

    public static async Task<string> RecomputeHashAsync(DbConnection conn, DbTransaction? tx, BaselineHead head, CancellationToken ct)
    {
        var (items, docs) = await LoadContentAsync(conn, tx, head.Id, ct);
        return ContentHash(head.RootMasterId, head.RootRevisionId, head.RootIterationId, head.Spec, items, docs);
    }
}
