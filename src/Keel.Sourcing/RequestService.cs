using System.Globalization;
using System.Text.Json;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Sourcing;

/// <summary>A request header with its markings. Marked: read only through <c>AccessPolicy</c>.</summary>
internal sealed record RequestRow(
    Guid Id, string Number, string Kind, string Title, string Description, string State, DateOnly? DueOn, DateTimeOffset CreatedAt, string CreatedBy, Markings Markings)
{
    public const string Columns = "r.id, r.number, r.kind, r.title, r.description, r.state, r.due_on, r.created_at, r.created_by";

    public static string Select { get; } = Columns + ", " + MarkingColumns.Select("r");

    public static RequestRow Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateOnly>(6),
        r.GetFieldValue<DateTimeOffset>(7), r.GetString(8), MarkingColumns.Read(r, 9));

    public static async Task<RequestRow?> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string number, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {Select} FROM src.request r WHERE r.number = @n{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public static async Task<RequestRow?> LoadByIdAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {Select} FROM src.request r WHERE r.id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }
}

internal sealed record RequestPartRow(Guid PartId, string Number, string Name, decimal Quantity, string Unit, string? Note, Markings Markings);

/// <summary>
/// Requests for information or quotation to suppliers (RFQ-lite, ADR-0037), the internal side. A request is marked like any object; its markings must cover
/// every part it lists (a reader of the request must be able to read the parts) and, per recipient, the supplier's clearance profile decides what the portal
/// shows. Parts and recipients are fixed when the request is created (cancel and create a new one to change them).
/// </summary>
public sealed class RequestService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, IContentReader? reader = null)
{
    public const string ObjectType = "request";

    public static readonly SortMap SortKeys = new(("number", ["r.number"]), ("title", ["lower(r.title)"]), ("kind", ["r.kind"]), ("state", ["r.state"]), ("due", ["r.due_on"]), ("created", ["r.created_at"]));

    private static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string? number, string? corr, params (string, string?)[] details) =>
        SourcingStore.Entry(s, action, outcome, ObjectType, number, corr, details);

    // ---------------------------------------------------------------- create

    public async Task<Outcome<RequestDetail>> CreateAsync(Subject subject, CreateRequestCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        var errors = SourcingRules.ValidateRequest(cmd);
        if (errors.Count > 0) return Outcome<RequestDetail>.Invalid(errors);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var modify = AccessPolicy.Evaluate(subject, cmd.Markings, PlmAction.Modify);
        if (!modify.Permitted) return await DenyAsync(conn, tx, subject, "rfq.create", null, correlationId, modify.Reasons, ct);

        var parts = new List<(PartRef Part, RequestPartInput Input)>();
        foreach (var input in cmd.Parts)
        {
            var part = await SourcingStore.LoadPartAsync(conn, tx, input.PartNumber.Trim(), false, ct);
            if (part is null || !AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Read).Permitted) return Outcome<RequestDetail>.Invalid([$"There is no part '{input.PartNumber.Trim()}'."]);
            var gaps = MarkingDominance.Gaps(cmd.Markings, part.Markings);
            if (gaps.Count > 0) return Outcome<RequestDetail>.Invalid([$"The request's markings do not cover part {part.Number} ({string.Join(", ", gaps)}). Raise the request's markings."]);
            parts.Add((part, input));
        }
        var suppliers = new List<SupplierRow>();
        foreach (var n in cmd.SupplierNumbers)
        {
            var s = await SourcingStore.LoadSupplierAsync(conn, tx, n.Trim(), false, ct);
            if (s is null || !AccessPolicy.Evaluate(subject, s.Markings, PlmAction.Read).Permitted) return Outcome<RequestDetail>.Invalid([$"There is no supplier '{n.Trim()}'."]);
            if (s.Status is SupplierStatuses.Obsolete or SupplierStatuses.Disapproved) return Outcome<RequestDetail>.Invalid([$"{s.Number} is {SupplierStatuses.Label(s.Status).ToLowerInvariant()} and cannot receive requests."]);
            foreach (var (part, _) in parts)
                if (SourcingRules.CheckSupplierForPart(s.IsForeign, part.Markings) is { } code)
                {
                    await audit.AppendAsync(tx, Entry(subject, "rfq.create", AuditOutcome.Denied, null, correlationId, ("reasons", code), ("supplier", s.Number), ("part", part.Number)), ct);
                    await tx.CommitAsync(ct);
                    return Outcome<RequestDetail>.Forbidden([code]);
                }
            suppliers.Add(s);
        }

        var number = SourcingStore.NextNumber("RFQ-", await SourcingStore.NextValAsync(conn, tx, "src.request_seq", ct));
        var id = Guid.CreateVersion7();
        var now = clock.GetUtcNow();
        await using (var ins = new NpgsqlCommand(
            $"""
            INSERT INTO src.request (id, number, kind, title, description, state, due_on, {MarkingColumns.InsertColumns}, created_at, created_by)
            VALUES (@id, @number, @kind, @title, @desc, 'Draft', @due, {MarkingColumns.InsertValues}, @now, @by)
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("number", number);
            ins.Parameters.AddWithValue("kind", cmd.Kind);
            ins.Parameters.AddWithValue("title", cmd.Title.Trim());
            ins.Parameters.AddWithValue("desc", cmd.Description?.Trim() ?? "");
            ins.Parameters.AddWithValue("due", (object?)cmd.DueOn ?? DBNull.Value);
            foreach (var (k, v) in MarkingColumns.Parameters(cmd.Markings)) ins.Parameters.AddWithValue(k, v);
            ins.Parameters.AddWithValue("now", now);
            ins.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await ins.ExecuteNonQueryAsync(ct);
        }
        // Recipients first: the trigger that refuses an ITAR part for a foreign recipient is on both tables, so either order is checked.
        foreach (var s in suppliers)
        {
            await using var ins = new NpgsqlCommand("INSERT INTO src.request_recipient (request_id, supplier_id, state, added_at) VALUES (@r, @s, 'Sent', @now)", conn, tx);
            ins.Parameters.AddWithValue("r", id);
            ins.Parameters.AddWithValue("s", s.Id);
            ins.Parameters.AddWithValue("now", now);
            await ins.ExecuteNonQueryAsync(ct);
        }
        foreach (var (part, input) in parts)
        {
            await using var ins = new NpgsqlCommand("INSERT INTO src.request_part (request_id, part_master_id, quantity, unit, note) VALUES (@r, @p, @q, @u, @n)", conn, tx);
            ins.Parameters.AddWithValue("r", id);
            ins.Parameters.AddWithValue("p", part.Id);
            ins.Parameters.AddWithValue("q", input.Quantity);
            ins.Parameters.AddWithValue("u", SourcingRules.Clean(input.Unit) ?? "EA");
            ins.Parameters.AddWithValue("n", (object?)SourcingRules.Clean(input.Note) ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "rfq.create", AuditOutcome.Success, number, correlationId,
            ("kind", cmd.Kind), ("parts", parts.Count.ToString(CultureInfo.InvariantCulture)), ("recipients", suppliers.Count.ToString(CultureInfo.InvariantCulture)), ("markings", cmd.Markings.BannerText())), ct);
        var detail = await BuildAsync(conn, tx, subject, (await RequestRow.LoadAsync(conn, tx, number, false, ct))!, ct);
        await tx.CommitAsync(ct);
        return Outcome<RequestDetail>.Ok(detail);
    }

    // ---------------------------------------------------------------- state changes

    public Task<Outcome<RequestDetail>> IssueAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        ChangeStateAsync(subject, number, "rfq.issue", correlationId, async (conn, tx, r, detail) =>
        {
            if (r.State != RequestStates.Draft) return "Only a draft request can be issued.";
            if (!detail.Recipients.Any(x => x.Releasable)) return "No recipient can receive this request: each needs a clearance profile that allows its markings and parts.";
            await SetStateAsync(conn, tx, r.Id, RequestStates.Issued, "issued_at", ct);
            return null;
        }, ct, extra: d => [("recipients", d.Recipients.Count.ToString(CultureInfo.InvariantCulture)), ("released", d.Recipients.Count(x => x.Releasable).ToString(CultureInfo.InvariantCulture))]);

    public Task<Outcome<RequestDetail>> CloseAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        ChangeStateAsync(subject, number, "rfq.close", correlationId, async (conn, tx, r, _) =>
        {
            if (r.State != RequestStates.Issued) return "Only an issued request can be closed.";
            await SetStateAsync(conn, tx, r.Id, RequestStates.Closed, "closed_at", ct);
            return null;
        }, ct);

    public Task<Outcome<RequestDetail>> CancelAsync(Subject subject, string number, string reason, string? correlationId, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(reason) || reason.Length > 1000
            ? Task.FromResult(Outcome<RequestDetail>.Invalid(["A reason is required (at most 1000 characters)."]))
            : ChangeStateAsync(subject, number, "rfq.cancel", correlationId, async (conn, tx, r, _) =>
            {
                if (r.State is RequestStates.Closed or RequestStates.Cancelled) return "The request is closed already.";
                await SetStateAsync(conn, tx, r.Id, RequestStates.Cancelled, "closed_at", ct);
                return null;
            }, ct, extra: _ => [("reason", reason.Trim())]);

    private static async Task SetStateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, string state, string stampColumn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"UPDATE src.request SET state = @s, {stampColumn} = now() WHERE id = @id", conn, tx);
        cmd.Parameters.AddWithValue("s", state);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<Outcome<RequestDetail>> ChangeStateAsync(
        Subject subject, string number, string action, string? corr, Func<NpgsqlConnection, NpgsqlTransaction, RequestRow, RequestDetail, Task<string?>> apply, CancellationToken ct,
        Func<RequestDetail, (string, string?)[]>? extra = null)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var r = await RequestRow.LoadAsync(conn, tx, number, true, ct);
        if (r is null) return Outcome<RequestDetail>.NotFound();
        var read = AccessPolicy.Evaluate(subject, r.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, corr, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<RequestDetail>.NotFound();
        }
        var modify = AccessPolicy.Evaluate(subject, r.Markings, PlmAction.Modify);
        if (!modify.Permitted) return await DenyAsync(conn, tx, subject, action, number, corr, modify.Reasons, ct);
        var before = await BuildAsync(conn, tx, subject, r, ct);
        if (await apply(conn, tx, r, before) is { } problem) return Outcome<RequestDetail>.Conflict(problem);
        await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Success, number, corr, extra?.Invoke(before) ?? []), ct);
        var detail = await BuildAsync(conn, tx, subject, (await RequestRow.LoadAsync(conn, tx, number, false, ct))!, ct);
        await tx.CommitAsync(ct);
        return Outcome<RequestDetail>.Ok(detail);
    }

    // ---------------------------------------------------------------- read

    public async Task<Outcome<RequestDetail>> GetAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var r = await RequestRow.LoadAsync(conn, tx, number, false, ct);
        if (r is null) return Outcome<RequestDetail>.NotFound();
        var read = AccessPolicy.Evaluate(subject, r.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "rfq.read", AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<RequestDetail>.NotFound();
        }
        var detail = await BuildAsync(conn, tx, subject, r, ct);
        await audit.AppendAsync(tx, Entry(subject, "rfq.read", AuditOutcome.Success, number, correlationId), ct);
        await tx.CommitAsync(ct);
        return Outcome<RequestDetail>.Ok(detail);
    }

    internal static async Task<IReadOnlyList<RequestPartRow>> PartsOfAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid requestId, CancellationToken ct)
    {
        var list = new List<RequestPartRow>();
        await using var cmd = new NpgsqlCommand(
            $"SELECT pm.id, pm.number, pm.name, rp.quantity, rp.unit, rp.note, {MarkingColumns.Select("pm")} FROM src.request_part rp JOIN plm.part_master pm ON pm.id = rp.part_master_id WHERE rp.request_id = @r ORDER BY pm.number", conn, tx);
        cmd.Parameters.AddWithValue("r", requestId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(new RequestPartRow(rd.GetGuid(0), rd.GetString(1), rd.GetString(2), rd.GetDecimal(3), rd.GetString(4), SourcingStore.Str(rd, 5), MarkingColumns.Read(rd, 6)));
        return list;
    }

    private static async Task<RequestDetail> BuildAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, RequestRow r, CancellationToken ct)
    {
        var partRows = await PartsOfAsync(conn, tx, r.Id, ct);
        // Every part is readable by anyone who can read the request (the request's markings cover them), but a changed part marking is re-checked at read time.
        var parts = partRows.Where(p => AccessPolicy.Evaluate(subject, p.Markings, PlmAction.Read).Permitted)
            .Select(p => new RequestPartView(p.Number, p.Name, p.Quantity, p.Unit, p.Note, p.Markings.BannerText())).ToList();
        var recipients = new List<RequestRecipientView>();
        var responses = new List<ResponseView>();
        var suppliers = new List<(SupplierRow Supplier, string State, DateTimeOffset? Viewed)>();
        await using (var cmd = new NpgsqlCommand("SELECT rr.supplier_id, rr.state, rr.first_viewed_at FROM src.request_recipient rr JOIN src.supplier s ON s.id = rr.supplier_id WHERE rr.request_id = @r ORDER BY s.number", conn, tx))
        {
            cmd.Parameters.AddWithValue("r", r.Id);
            var ids = new List<(Guid, string, DateTimeOffset?)>();
            await using (var rd = await cmd.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct)) ids.Add((rd.GetGuid(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetFieldValue<DateTimeOffset>(2)));
            foreach (var (sid, state, viewed) in ids)
                suppliers.Add((await SourcingStore.LoadSupplierByIdAsync(conn, tx, sid, false, ct) ?? throw new InvalidOperationException("Recipient supplier is missing."), state, viewed));
        }
        foreach (var (s, state, viewed) in suppliers)
        {
            // A supplier the caller may not read is shown by number only to nobody: it is omitted (the request's own readers may be unable to read a restricted supplier).
            if (!AccessPolicy.Evaluate(subject, s.Markings, PlmAction.Read).Permitted) continue;
            var (releasable, reason) = await ReleasabilityAsync(conn, tx, s, r.Markings, partRows, ct);
            recipients.Add(new RequestRecipientView(s.Number, s.Name, state, viewed, releasable, reason));
        }
        await using (var cmd = new NpgsqlCommand("SELECT id, supplier_id, seq, declined, message, quote::text, submitted_at, submitted_by_name FROM src.response WHERE request_id = @r ORDER BY supplier_id, seq", conn, tx))
        {
            cmd.Parameters.AddWithValue("r", r.Id);
            var raw = new List<(Guid Id, Guid Supplier, int Seq, bool Declined, string Message, string Quote, DateTimeOffset At, string By)>();
            await using (var rd = await cmd.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct)) raw.Add((rd.GetGuid(0), rd.GetGuid(1), rd.GetInt32(2), rd.GetBoolean(3), rd.GetString(4), rd.GetString(5), rd.GetFieldValue<DateTimeOffset>(6), rd.GetString(7)));
            foreach (var x in raw)
            {
                var s = suppliers.FirstOrDefault(q => q.Supplier.Id == x.Supplier).Supplier;
                if (s is null || !AccessPolicy.Evaluate(subject, s.Markings, PlmAction.Read).Permitted) continue;
                responses.Add(new ResponseView(x.Id, s.Number, s.Name, x.Seq, x.Declined, x.Message, ParseQuote(x.Quote), x.At, x.By, await FilesAsync(conn, tx, x.Id, ct)));
            }
        }
        var summary = await SummaryAsync(conn, tx, r, ct);
        return new RequestDetail(summary, r.Description, parts, recipients, responses, AccessPolicy.Evaluate(subject, r.Markings, PlmAction.Modify).Permitted);
    }

    internal static async Task<IReadOnlyList<ResponseFileView>> FilesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid responseId, CancellationToken ct)
    {
        var files = new List<ResponseFileView>();
        await using var cmd = new NpgsqlCommand("SELECT id, kind, file_name, sha256, size_bytes FROM src.response_file WHERE response_id = @r ORDER BY uploaded_at, file_name", conn, tx);
        cmd.Parameters.AddWithValue("r", responseId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) files.Add(new ResponseFileView(rd.GetGuid(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetInt64(4)));
        return files;
    }

    /// <summary>Reads the quote lines of a response from their JSON form (the REST layer takes the quote of a portal response that way).</summary>
    public static IReadOnlyList<QuoteLine> ParseQuote(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(e => new QuoteLine(
            e.GetProperty("part").GetString()!,
            e.TryGetProperty("unitPrice", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : null,
            e.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
            e.TryGetProperty("leadTimeDays", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null,
            e.TryGetProperty("moq", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetDecimal() : null))];
    }

    internal static string QuoteJson(IReadOnlyList<QuoteLine> quote) =>
        JsonSerializer.Serialize(quote.Select(q => new { part = q.Part, unitPrice = q.UnitPrice, currency = q.Currency, leadTimeDays = q.LeadTimeDays, moq = q.Moq }));

    /// <summary>Can this supplier receive the request now? The supplier's standing, its clearance profile against the request's markings, and at least one part it may see.</summary>
    internal static async Task<(bool Releasable, string? Reason)> ReleasabilityAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SupplierRow s, Markings requestMarkings, IReadOnlyList<RequestPartRow> parts, CancellationToken ct)
    {
        if (s.Status is SupplierStatuses.Obsolete or SupplierStatuses.Disapproved) return (false, SourcingRules.SupplierNotActive);
        var profile = await SourcingStore.LoadProfileAsync(conn, tx, s.ClearanceProfile, ct);
        var request = SourcingRules.ReleaseToSupplier(s.IsForeign, profile, requestMarkings);
        if (!request.Permitted) return (false, request.Reasons[0]);
        return parts.Any(p => SourcingRules.ReleaseToSupplier(s.IsForeign, profile, p.Markings).Permitted) ? (true, null) : (false, "portal.no_releasable_part");
    }

    private static async Task<RequestSummary> SummaryAsync(NpgsqlConnection conn, NpgsqlTransaction tx, RequestRow r, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM src.request_part WHERE request_id = @r), (SELECT count(*) FROM src.request_recipient WHERE request_id = @r), (SELECT count(*) FROM src.request_recipient WHERE request_id = @r AND state IN ('Responded', 'Declined'))", conn, tx);
        cmd.Parameters.AddWithValue("r", r.Id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        await rd.ReadAsync(ct);
        return new RequestSummary(r.Id, r.Number, r.Kind, r.Title, r.State, r.DueOn, (int)rd.GetInt64(0), (int)rd.GetInt64(1), (int)rd.GetInt64(2), r.Markings.BannerText(), r.Markings, r.CreatedAt, r.CreatedBy);
    }

    // ---------------------------------------------------------------- search

    public async Task<Outcome<RequestPage>> SearchAsync(Subject subject, RequestSearch query, string? correlationId, CancellationToken ct = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 200);
        var offset = Math.Max(0, query.Offset);
        var filter = AccessPolicy.BuildReadFilter(subject, "r");
        var where = new List<string>();
        var parameters = new List<KeyValuePair<string, object>>(filter.Parameters);
        if (!string.IsNullOrWhiteSpace(query.Text)) { where.Add("(r.number ILIKE @q OR r.title ILIKE @q)"); parameters.Add(new("q", SourcingStore.Like(query.Text))); }
        if (!string.IsNullOrWhiteSpace(query.State)) { where.Add("r.state = @state"); parameters.Add(new("state", query.State.Trim())); }
        var extra = string.Concat(where.Select(w => " AND " + w));
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int total;
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM src.request r WHERE {filter.Sql}{extra}", conn, tx))
        {
            foreach (var (k, v) in parameters) count.Parameters.AddWithValue(k, v);
            total = Convert.ToInt32(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        var rows = new List<RequestRow>();
        await using (var cmd = new NpgsqlCommand($"SELECT {RequestRow.Select} FROM src.request r WHERE {filter.Sql}{extra} ORDER BY {SortKeys.OrderBy(query.Sort, "r.created_at DESC, r.number DESC", "r.number")} LIMIT @limit OFFSET @offset", conn, tx))
        {
            foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add(RequestRow.Read(rd));
        }
        var items = new List<RequestSummary>();
        foreach (var r in rows) items.Add(await SummaryAsync(conn, tx, r, ct));
        var banner = Markings.AggregateBanner(items.Select(i => i.Markings).Distinct());
        await audit.AppendAsync(tx, Entry(subject, "rfq.search", AuditOutcome.Success, null, correlationId, ("state", query.State), ("results", items.Count.ToString(CultureInfo.InvariantCulture)), ("banner", banner)), ct);
        await tx.CommitAsync(ct);
        return Outcome<RequestPage>.Ok(new RequestPage(items, total, banner));
    }

    // ---------------------------------------------------------------- response files

    /// <summary>Opens a file a supplier uploaded, for an internal reader of the request. The file is delivered as an attachment as it is: it is the supplier's, not a marked rendition.</summary>
    public async Task<Outcome<(Stream Content, string FileName, long Size)>> OpenResponseFileAsync(Subject subject, string requestNumber, Guid fileId, string? correlationId, CancellationToken ct = default)
    {
        if (reader is null) return Outcome<(Stream, string, long)>.Unavailable("File storage is not available.");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var r = await RequestRow.LoadAsync(conn, tx, requestNumber, false, ct);
        if (r is null) return Outcome<(Stream, string, long)>.NotFound();
        var download = AccessPolicy.Evaluate(subject, r.Markings, PlmAction.Download);
        if (!download.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "rfq.file_download", AuditOutcome.Denied, requestNumber, correlationId, ("reasons", string.Join(",", download.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<(Stream, string, long)>.NotFound();
        }
        string? sha = null, name = null; long size = 0;
        await using (var cmd = new NpgsqlCommand("SELECT f.sha256, f.file_name, f.size_bytes FROM src.response_file f JOIN src.response p ON p.id = f.response_id WHERE f.id = @f AND p.request_id = @r", conn, tx))
        {
            cmd.Parameters.AddWithValue("f", fileId);
            cmd.Parameters.AddWithValue("r", r.Id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct)) { sha = rd.GetString(0); name = rd.GetString(1); size = rd.GetInt64(2); }
        }
        if (sha is null) return Outcome<(Stream, string, long)>.NotFound();
        await audit.AppendAsync(tx, Entry(subject, "rfq.file_download", AuditOutcome.Success, requestNumber, correlationId, ("file", fileId.ToString("D"))), ct);
        await tx.CommitAsync(ct);
        return Outcome<(Stream, string, long)>.Ok((await reader.OpenReadAsync(sha, size, ct), name!, size));
    }

    // ---------------------------------------------------------------- helpers

    private async Task<Outcome<RequestDetail>> DenyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string action, string? number, string? corr, IReadOnlyCollection<string> reasons, CancellationToken ct)
    {
        await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, corr, ("reasons", string.Join(",", reasons))), ct);
        await tx.CommitAsync(ct);
        return Outcome<RequestDetail>.Forbidden([.. reasons]);
    }
}
