using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Sourcing;

/// <summary>A released document version as a request pins it: the identity, the released revision and its newest iteration, and the document's own markings.</summary>
internal sealed record DocumentPin(Guid MasterId, string Number, string Name, string Type, Markings Markings, Guid? RevisionId, string? Revision, Guid? IterationId, int Iteration);

/// <summary>One supplier's column of the side-by-side comparison.</summary>
public sealed record ComparisonSupplier(
    string Number, string Name, string State, int? ResponseSeq, bool Declined, DateOnly? ValidUntil, bool QuoteExpired, string? Message, bool Releasable);

/// <summary>One request line of the comparison with one cell per supplier (same order as the suppliers).</summary>
public sealed record ComparisonLine(
    string PartNumber, string PartName, decimal Quantity, string Unit, DateOnly? NeedBy, IReadOnlyList<SourcingRules.CompareCell> Cells, string? AwardedSupplier);

/// <summary>What one supplier's priced lines add up to (per currency; nothing is converted). <see cref="Complete"/> is true when every line is priced.</summary>
public sealed record ComparisonTotal(string SupplierNumber, string Currency, decimal Total, int LinesPriced, int LinesTotal)
{
    public bool Complete => LinesPriced == LinesTotal;
}

public sealed record RfqComparison(string Number, IReadOnlyList<ComparisonSupplier> Suppliers, IReadOnlyList<ComparisonLine> Lines, IReadOnlyList<ComparisonTotal> Totals, bool CanAward);

/// <summary>Awards the given lines (all lines when <see cref="PartNumbers"/> is empty) to one supplier on the strength of its latest quote.</summary>
public sealed record AwardCommand(string SupplierNumber, IReadOnlyList<string>? PartNumbers = null, string? Note = null);

public sealed partial class RequestService
{
    // ASSUMPTION(A76): documents are pinned released versions sent whole or refused; an award is one winner per line resting on an unexpired quote, linked to a draft vendor list entry.
    // ---------------------------------------------------------------- due date

    /// <summary>Moves the due date of a draft or issued request (a supplier can answer until the end of that day). An issued request needs a date that has not passed.</summary>
    public Task<Outcome<RequestDetail>> ChangeDueDateAsync(Subject subject, string number, DateOnly? dueOn, string? correlationId, CancellationToken ct = default) =>
        ChangeStateAsync(subject, number, "rfq.due_change", correlationId, async (conn, tx, r, _) =>
        {
            if (r.State is not (RequestStates.Draft or RequestStates.Issued)) return "Only a draft or issued request can change its due date.";
            if (r.State == RequestStates.Issued && (dueOn is null || dueOn < SourcingStore.Today(clock))) return "An issued request needs a due date that has not passed.";
            await using var cmd = new NpgsqlCommand("UPDATE src.request SET due_on = @d WHERE id = @id", conn, tx);
            cmd.Parameters.AddWithValue("d", (object?)dueOn ?? DBNull.Value);
            cmd.Parameters.AddWithValue("id", r.Id);
            await cmd.ExecuteNonQueryAsync(ct);
            return null;
        }, ct, extra: _ => [("due", dueOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]);

    // ---------------------------------------------------------------- attached documents

    /// <summary>The document with its released revision (the newest released one) and that revision's newest iteration, by number: a key lookup, the caller decides access.</summary>
    internal static async Task<DocumentPin?> LoadReleasedDocumentAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string number, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT m.id, m.number, m.name, m.document_type, {MarkingColumns.Select("m")}, r.id, r.revision, i.id, i.iteration
            FROM plm.document_master m
            LEFT JOIN LATERAL (SELECT x.id, x.revision, x.latest_iteration FROM plm.document_revision x WHERE x.master_id = m.id AND x.lifecycle_state = 'Released' ORDER BY x.created_at DESC LIMIT 1) r ON true
            LEFT JOIN plm.document_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            WHERE m.number = @n
            """, conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        var o = 4 + MarkingColumns.Count;
        return new DocumentPin(rd.GetGuid(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), MarkingColumns.Read(rd, 4),
            rd.IsDBNull(o) ? null : rd.GetGuid(o), rd.IsDBNull(o + 1) ? null : rd.GetString(o + 1), rd.IsDBNull(o + 2) ? null : rd.GetGuid(o + 2), rd.IsDBNull(o + 3) ? 0 : rd.GetInt32(o + 3));
    }

    /// <summary>A document the request pins, with its files, as stored (internal: callers decide who may see it).</summary>
    internal sealed record PinnedDocument(DocumentPin Pin, IReadOnlyList<(Guid Id, string FileName, long Size, string Sha256)> Files);

    internal static async Task<IReadOnlyList<PinnedDocument>> PinnedDocumentsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid requestId, CancellationToken ct)
    {
        var pins = new List<(DocumentPin Pin, Guid IterationId)>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT m.id, m.number, m.name, m.document_type, {MarkingColumns.Select("m")}, rd.document_revision_id, rv.revision, rd.iteration_id, it.iteration
            FROM src.request_document rd
            JOIN plm.document_master m ON m.id = rd.document_master_id
            JOIN plm.document_revision rv ON rv.id = rd.document_revision_id
            JOIN plm.document_iteration it ON it.id = rd.iteration_id
            WHERE rd.request_id = @r ORDER BY m.number
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("r", requestId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            var o = 4 + MarkingColumns.Count;
            while (await rd.ReadAsync(ct))
                pins.Add((new DocumentPin(rd.GetGuid(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), MarkingColumns.Read(rd, 4), rd.GetGuid(o), rd.GetString(o + 1), rd.GetGuid(o + 2), rd.GetInt32(o + 3)), rd.GetGuid(o + 2)));
        }
        var list = new List<PinnedDocument>();
        foreach (var (pin, iteration) in pins)
        {
            var files = new List<(Guid, string, long, string)>();
            await using var cmd = new NpgsqlCommand("SELECT id, file_name, size_bytes, sha256 FROM plm.document_file WHERE iteration_id = @i ORDER BY role DESC, file_name", conn, tx);
            cmd.Parameters.AddWithValue("i", iteration);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) files.Add((rd.GetGuid(0), rd.GetString(1), rd.GetInt64(2), rd.GetString(3)));
            list.Add(new PinnedDocument(pin, files));
        }
        return list;
    }

    /// <summary>
    /// The documents of the request as an internal reader sees them: only those the reader may read (the request's markings cover them, but a changed marking is re-checked), each with the
    /// recipients that do not receive it (the release check against the recipient's clearance profile; a refused document is not sent in part).
    /// </summary>
    private static async Task<IReadOnlyList<RequestDocumentView>> DocumentsOfAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, Guid requestId, IReadOnlyList<SupplierRow> recipients, CancellationToken ct)
    {
        var profiles = new Dictionary<Guid, ClearanceProfile?>();
        foreach (var s in recipients) profiles[s.Id] = await SourcingStore.LoadProfileAsync(conn, tx, s.ClearanceProfile, ct);
        return [.. (await PinnedDocumentsAsync(conn, tx, requestId, ct)).Where(d => AccessPolicy.Evaluate(subject, d.Pin.Markings, PlmAction.Read).Permitted)
            .Select(d => new RequestDocumentView(d.Pin.Number, d.Pin.Name, d.Pin.Type, d.Pin.Revision!, d.Pin.Iteration, [.. d.Files.Select(f => new RequestDocumentFile(f.Id, f.FileName, f.Size))], d.Pin.Markings.BannerText(),
                [.. recipients.Where(s => !SourcingRules.ReleaseToSupplier(s.IsForeign, profiles[s.Id], d.Pin.Markings).Permitted).Select(s => s.Number)]))];
    }

    // ---------------------------------------------------------------- awards

    private static async Task<IReadOnlyList<AwardView>> AwardsOfAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, Guid requestId, CancellationToken ct)
    {
        var list = new List<AwardView>();
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT a.id, pm.number, {MarkingColumns.Select("pm")}, s.id, a.response_id, a.quantity, a.unit_price, a.currency, a.lead_time_days, e.number, a.entry_created, a.note, a.awarded_at, a.awarded_by,
                   (SELECT seq FROM src.response x WHERE x.id = a.response_id)
            FROM src.award a
            JOIN plm.part_master pm ON pm.id = a.part_master_id
            JOIN src.source_entry e ON e.id = a.entry_id
            JOIN src.supplier s ON s.id = a.supplier_id
            WHERE a.request_id = @r AND a.withdrawn_at IS NULL ORDER BY pm.number
            """, conn, tx);
        cmd.Parameters.AddWithValue("r", requestId);
        var raw = new List<(string Part, Markings PartMarkings, Guid Supplier, decimal Qty, decimal Price, string Currency, int? Lead, string Entry, bool Created, string? Note, DateTimeOffset At, string By, Guid Id, int Seq)>();
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            var o = 1 + MarkingColumns.Count;
            while (await rd.ReadAsync(ct))
                raw.Add((rd.GetString(1), MarkingColumns.Read(rd, 2), rd.GetGuid(o + 1), rd.GetDecimal(o + 3), rd.GetDecimal(o + 4), rd.GetString(o + 5), rd.IsDBNull(o + 6) ? null : rd.GetInt32(o + 6), rd.GetString(o + 7),
                    rd.GetBoolean(o + 8), SourcingStore.Str(rd, o + 9), rd.GetFieldValue<DateTimeOffset>(o + 10), rd.GetString(o + 11), rd.GetGuid(0), rd.GetInt32(o + 12)));
        }
        foreach (var a in raw)
        {
            var s = await SourcingStore.LoadSupplierByIdAsync(conn, tx, a.Supplier, false, ct);
            if (s is null || !AccessPolicy.Evaluate(subject, s.Markings, PlmAction.Read).Permitted || !AccessPolicy.Evaluate(subject, a.PartMarkings, PlmAction.Read).Permitted) continue;
            list.Add(new AwardView(a.Id, a.Part, s.Number, s.Name, a.Seq, a.Qty, a.Price, a.Currency, a.Lead, a.Entry, a.Created, a.Note, a.At, a.By));
        }
        return list;
    }

    // ---------------------------------------------------------------- comparison

    /// <summary>
    /// The quotes of a request side by side: for every line what each supplier quoted at the line's quantity (tiers applied), the lowest price (within one currency), the shortest lead
    /// time, whether an order placed today meets the need-by date, exceptions and notes, and what each supplier's priced lines add up to. Built from what the caller may read: a supplier
    /// or part the caller cannot read is not in it. Only each supplier's latest response counts.
    /// </summary>
    public async Task<Outcome<RfqComparison>> CompareAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var r = await RequestRow.LoadAsync(conn, tx, number, false, ct);
        if (r is null) return Outcome<RfqComparison>.NotFound();
        var read = AccessPolicy.Evaluate(subject, r.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "rfq.compare", AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<RfqComparison>.NotFound();
        }
        var detail = await BuildAsync(conn, tx, subject, r, ct);
        var comparison = BuildComparison(detail, SourcingStore.Today(clock));
        await audit.AppendAsync(tx, Entry(subject, "rfq.compare", AuditOutcome.Success, number, correlationId,
            ("suppliers", comparison.Suppliers.Count.ToString(CultureInfo.InvariantCulture)), ("lines", comparison.Lines.Count.ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return Outcome<RfqComparison>.Ok(comparison);
    }

    /// <summary>The pure comparison of a request detail (testable without a database). Declined suppliers and suppliers that have not answered appear as columns without prices.</summary>
    public static RfqComparison BuildComparison(RequestDetail detail, DateOnly today)
    {
        var suppliers = new List<ComparisonSupplier>();
        var latest = new Dictionary<string, ResponseView>(StringComparer.Ordinal);
        foreach (var rec in detail.Recipients)
        {
            var response = detail.Responses.Where(x => x.SupplierNumber == rec.SupplierNumber).OrderByDescending(x => x.Seq).FirstOrDefault();
            if (response is not null) latest[rec.SupplierNumber] = response;
            suppliers.Add(new ComparisonSupplier(rec.SupplierNumber, rec.SupplierName, rec.State, response?.Seq, response?.Declined ?? false, response?.ValidUntil,
                response?.ValidUntil is { } v && v < today, string.IsNullOrWhiteSpace(response?.Message) ? null : response!.Message, rec.Releasable));
        }
        var lines = new List<ComparisonLine>();
        var totals = new Dictionary<(string Supplier, string Currency), (decimal Total, int Priced)>();
        foreach (var p in detail.Parts)
        {
            var quotes = suppliers.Select(s => (s.Number, Line: latest.TryGetValue(s.Number, out var resp) && !resp.Declined ? resp.Quote.FirstOrDefault(q => q.Part == p.PartNumber) : null)).ToList();
            var awarded = detail.AwardList.FirstOrDefault(a => a.PartNumber == p.PartNumber)?.SupplierNumber;
            var cells = SourcingRules.CompareLine(p.Quantity, p.NeedBy, today, quotes.Select(q => (q.Number, q.Line)).ToList(), awarded);
            lines.Add(new ComparisonLine(p.PartNumber, p.PartName, p.Quantity, p.Unit, p.NeedBy, cells, awarded));
            foreach (var c in cells.Where(c => c.ExtendedPrice is not null && c.Currency is not null))
            {
                var key = (c.SupplierNumber, c.Currency!);
                totals.TryGetValue(key, out var t);
                totals[key] = (t.Total + c.ExtendedPrice!.Value, t.Priced + 1);
            }
        }
        return new RfqComparison(detail.Summary.Number, suppliers, lines,
            [.. totals.OrderBy(t => t.Key.Supplier, StringComparer.Ordinal).ThenBy(t => t.Key.Currency, StringComparer.Ordinal).Select(t => new ComparisonTotal(t.Key.Supplier, t.Key.Currency, t.Value.Total, t.Value.Priced, detail.Parts.Count))],
            detail.CanModify && detail.Summary.State is RequestStates.Issued or RequestStates.Closed);
    }

    // ---------------------------------------------------------------- award

    /// <summary>
    /// Awards request lines to one supplier (every line when no part is named: a full award needs a priced quote for each line; otherwise just the named lines: a per-line award).
    /// The award rests on the supplier's latest quote, which must not have expired, at the line's quantity. For each line it records the sourcing link: the approved vendor list entry
    /// (AVL) of the part and the supplier, created as a DRAFT with the quoted lead time, minimum order and price breaks (a draft still goes through the usual approval) or, when an open
    /// entry exists already, that entry (unchanged). Needs permission to modify the request; the export-control rule for foreign suppliers is checked again. When every line has an award
    /// the request closes.
    /// </summary>
    public async Task<Outcome<RequestDetail>> AwardAsync(Subject subject, string number, AwardCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.SupplierNumber)) return Outcome<RequestDetail>.Invalid(["Choose the supplier to award."]);
        if (cmd.Note is { Length: > 500 }) return Outcome<RequestDetail>.Invalid(["The note is at most 500 characters."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var r = await RequestRow.LoadAsync(conn, tx, number, true, ct);
        if (r is null) return Outcome<RequestDetail>.NotFound();
        var read = AccessPolicy.Evaluate(subject, r.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "rfq.award", AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<RequestDetail>.NotFound();
        }
        var modify = AccessPolicy.Evaluate(subject, r.Markings, PlmAction.Modify);
        if (!modify.Permitted) return await DenyAsync(conn, tx, subject, "rfq.award", number, correlationId, modify.Reasons, ct);
        if (r.State is not (RequestStates.Issued or RequestStates.Closed)) return Outcome<RequestDetail>.Conflict("Only an issued or closed request can be awarded.");

        var supplier = await SourcingStore.LoadSupplierAsync(conn, tx, cmd.SupplierNumber.Trim(), false, ct);
        if (supplier is null || !AccessPolicy.Evaluate(subject, supplier.Markings, PlmAction.Read).Permitted) return Outcome<RequestDetail>.Invalid([$"There is no supplier '{cmd.SupplierNumber.Trim()}'."]);
        if (supplier.Status is SupplierStatuses.Obsolete or SupplierStatuses.Disapproved) return Outcome<RequestDetail>.Conflict($"{supplier.Number} is {SupplierStatuses.Label(supplier.Status).ToLowerInvariant()} and cannot be awarded.");
        Guid? responseId = null; string? quoteJson = null; DateOnly? validUntil = null; int seq = 0;
        await using (var q = new NpgsqlCommand(
            "SELECT p.id, p.quote::text, p.valid_until, p.seq FROM src.request_recipient rr JOIN src.response p ON p.request_id = rr.request_id AND p.supplier_id = rr.supplier_id WHERE rr.request_id = @r AND rr.supplier_id = @s AND NOT p.declined ORDER BY p.seq DESC LIMIT 1", conn, tx))
        {
            q.Parameters.AddWithValue("r", r.Id);
            q.Parameters.AddWithValue("s", supplier.Id);
            await using var rd = await q.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct)) { responseId = rd.GetGuid(0); quoteJson = rd.GetString(1); validUntil = rd.IsDBNull(2) ? null : rd.GetFieldValue<DateOnly>(2); seq = rd.GetInt32(3); }
        }
        // The supplier's latest response must be a quote (a later decline withdraws the earlier quote).
        await using (var latest = new NpgsqlCommand("SELECT declined FROM src.response WHERE request_id = @r AND supplier_id = @s ORDER BY seq DESC LIMIT 1", conn, tx))
        {
            latest.Parameters.AddWithValue("r", r.Id);
            latest.Parameters.AddWithValue("s", supplier.Id);
            if (await latest.ExecuteScalarAsync(ct) is not false) responseId = null;
        }
        if (responseId is null || quoteJson is null) return Outcome<RequestDetail>.Conflict($"{supplier.Number} has not sent a quote.");
        var today = SourcingStore.Today(clock);
        if (validUntil is { } until && until < today) return Outcome<RequestDetail>.Conflict($"The quote of {supplier.Number} expired on {until:yyyy-MM-dd}. Ask for a new one.");
        var quote = ParseQuote(quoteJson);

        var partRows = await PartsOfAsync(conn, tx, r.Id, ct);
        var wanted = (cmd.PartNumbers ?? []).Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var targets = wanted.Count == 0 ? partRows.ToList() : [];
        foreach (var w in wanted)
        {
            var row = partRows.FirstOrDefault(p => p.Number == w);
            if (row is null) return Outcome<RequestDetail>.Invalid([$"{w} is not a line of this request."]);
            targets.Add(row);
        }
        var profile = await SourcingStore.LoadProfileAsync(conn, tx, supplier.ClearanceProfile, ct);
        var picks = new List<(RequestPartRow Part, QuoteLine Line, decimal Price)>();
        foreach (var part in targets)
        {
            if (!AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Read).Permitted) return Outcome<RequestDetail>.Invalid([$"There is no part '{part.Number}'."]);
            if (SourcingRules.CheckSupplierForPart(supplier.IsForeign, part.Markings) is { } code)
            {
                await audit.AppendAsync(tx, Entry(subject, "rfq.award", AuditOutcome.Denied, number, correlationId, ("reasons", code), ("supplier", supplier.Number), ("part", part.Number)), ct);
                await tx.CommitAsync(ct);
                return Outcome<RequestDetail>.Forbidden([code]);
            }
            var line = quote.FirstOrDefault(x => x.Part == part.Number);
            var price = line is null ? null : SourcingRules.PriceAt(line, part.Quantity);
            if (line is null || price is null || string.IsNullOrWhiteSpace(line.Currency) || !SourcingRules.ReleaseToSupplier(supplier.IsForeign, profile, part.Markings).Permitted)
                return Outcome<RequestDetail>.Conflict(wanted.Count == 0
                    ? $"{supplier.Number} did not quote every line (missing a price for {part.Number} at {part.Quantity:0.####}). Award the lines it quoted one by one."
                    : $"{supplier.Number} has no price for {part.Number} at {part.Quantity:0.####}.");
            picks.Add((part, line, price.Value));
        }
        if (picks.Count == 0) return Outcome<RequestDetail>.Invalid(["There is nothing to award."]);
        await using (var taken = new NpgsqlCommand("SELECT pm.number FROM src.award a JOIN plm.part_master pm ON pm.id = a.part_master_id WHERE a.request_id = @r AND a.withdrawn_at IS NULL AND pm.number = ANY(@n)", conn, tx))
        {
            taken.Parameters.AddWithValue("r", r.Id);
            taken.Parameters.AddWithValue("n", picks.Select(p => p.Part.Number).ToArray());
            await using var rd = await taken.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct)) return Outcome<RequestDetail>.Conflict($"{rd.GetString(0)} is awarded already. Withdraw that award first.");
        }

        var now = clock.GetUtcNow();
        var by = subject.Username ?? subject.Id;
        var created = 0;
        foreach (var (part, line, price) in picks)
        {
            var (entryId, _, entryCreated) = await LinkEntryAsync(conn, tx, subject, number, part, supplier, line, price, now, correlationId, ct);
            await using var ins = new NpgsqlCommand(
                """
                INSERT INTO src.award (id, request_id, part_master_id, supplier_id, response_id, quantity, unit_price, currency, lead_time_days, entry_id, entry_created, note, awarded_at, awarded_by)
                VALUES (@id, @r, @p, @s, @resp, @q, @price, @cur, @lead, @entry, @created, @note, @now, @by)
                """, conn, tx);
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("r", r.Id);
            ins.Parameters.AddWithValue("p", part.PartId);
            ins.Parameters.AddWithValue("s", supplier.Id);
            ins.Parameters.AddWithValue("resp", responseId.Value);
            ins.Parameters.AddWithValue("q", part.Quantity);
            ins.Parameters.AddWithValue("price", price);
            ins.Parameters.AddWithValue("cur", line.Currency!.ToUpperInvariant());
            ins.Parameters.AddWithValue("lead", (object?)line.LeadTimeDays ?? DBNull.Value);
            ins.Parameters.AddWithValue("entry", entryId);
            ins.Parameters.AddWithValue("created", entryCreated);
            ins.Parameters.AddWithValue("note", (object?)SourcingRules.Clean(cmd.Note) ?? DBNull.Value);
            ins.Parameters.AddWithValue("now", now);
            ins.Parameters.AddWithValue("by", by);
            await ins.ExecuteNonQueryAsync(ct);
            if (entryCreated) created++;
        }
        var closed = false;
        await using (var open = new NpgsqlCommand(
            "SELECT count(*) FROM src.request_part rp WHERE rp.request_id = @r AND NOT EXISTS (SELECT 1 FROM src.award a WHERE a.request_id = rp.request_id AND a.part_master_id = rp.part_master_id AND a.withdrawn_at IS NULL)", conn, tx))
        {
            open.Parameters.AddWithValue("r", r.Id);
            if ((long)(await open.ExecuteScalarAsync(ct))! == 0 && r.State == RequestStates.Issued)
            {
                await SetStateAsync(conn, tx, r.Id, RequestStates.Closed, "closed_at", ct);
                closed = true;
            }
        }
        await audit.AppendAsync(tx, Entry(subject, "rfq.award", AuditOutcome.Success, number, correlationId,
            ("supplier", supplier.Number), ("response", seq.ToString(CultureInfo.InvariantCulture)), ("lines", picks.Count.ToString(CultureInfo.InvariantCulture)),
            ("entriesCreated", created.ToString(CultureInfo.InvariantCulture)), ("closed", closed ? "true" : "false")), ct);
        var detail = await BuildAsync(conn, tx, subject, (await RequestRow.LoadAsync(conn, tx, number, false, ct))!, ct);
        await tx.CommitAsync(ct);
        return Outcome<RequestDetail>.Ok(detail);
    }

    /// <summary>The sourcing link of one awarded line: an open AVL entry of the part and the supplier, or a new draft one carrying the quote's lead time, minimum order and price breaks.</summary>
    private async Task<(Guid Id, string Number, bool Created)> LinkEntryAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string requestNumber, RequestPartRow part, SupplierRow supplier, QuoteLine line, decimal price, DateTimeOffset now, string? corr, CancellationToken ct)
    {
        await using (var find = new NpgsqlCommand(
            "SELECT id, number FROM src.source_entry WHERE part_master_id = @p AND kind = 'avl' AND supplier_id = @s AND mpn_id IS NULL AND revision_scope IS NULL AND approval_state IN ('Draft', 'UnderReview', 'Active') LIMIT 1", conn, tx))
        {
            find.Parameters.AddWithValue("p", part.PartId);
            find.Parameters.AddWithValue("s", supplier.Id);
            await using var rd = await find.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct)) return (rd.GetGuid(0), rd.GetString(1), false);
        }
        var breaks = line.Tiers is { Count: > 0 } ? line.Tiers : [new PriceBreak(part.Quantity, price)];
        var currency = line.Currency!.ToUpperInvariant();
        var number = SourcingStore.NextNumber("SRC-", await SourcingStore.NextValAsync(conn, tx, "src.entry_seq", ct));
        var id = Guid.CreateVersion7();
        await using (var ins = new NpgsqlCommand(
            """
            INSERT INTO src.source_entry (id, number, kind, part_master_id, supplier_id, status, approval_state, lead_time_days, moq, currency, price_breaks, note, created_at, created_by)
            VALUES (@id, @number, 'avl', @p, @s, 'approved', 'Draft', @lead, @moq, @cur, @breaks::jsonb, @note, @now, @by)
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("number", number);
            ins.Parameters.AddWithValue("p", part.PartId);
            ins.Parameters.AddWithValue("s", supplier.Id);
            ins.Parameters.AddWithValue("lead", (object?)line.LeadTimeDays ?? DBNull.Value);
            ins.Parameters.AddWithValue("moq", (object?)line.Moq ?? DBNull.Value);
            ins.Parameters.AddWithValue("cur", currency);
            ins.Parameters.AddWithValue("breaks", SourcingStore.PriceBreaksJson(breaks));
            ins.Parameters.AddWithValue("note", "Awarded on " + requestNumber);
            ins.Parameters.AddWithValue("now", now);
            ins.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, SourcingStore.Entry(subject, "sourcing.entry_create", AuditOutcome.Success, "part", part.Number, corr,
            ("entry", number), ("list", SourceKinds.Avl), ("supplier", supplier.Number), ("status", EntryStatuses.Approved), ("request", requestNumber)), ct);
        return (id, number, true);
    }

    /// <summary>
    /// Withdraws the active award of one line (a reason is required). A list entry the award created is withdrawn with it while it is still a draft; an entry that has gone into review or
    /// is active stays and is changed on the part's Sourcing tab. The line can then be awarded again.
    /// </summary>
    public async Task<Outcome<RequestDetail>> WithdrawAwardAsync(Subject subject, string number, string partNumber, string reason, string? correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000) return Outcome<RequestDetail>.Invalid(["A reason is required (at most 1000 characters)."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var r = await RequestRow.LoadAsync(conn, tx, number, true, ct);
        if (r is null) return Outcome<RequestDetail>.NotFound();
        var read = AccessPolicy.Evaluate(subject, r.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "rfq.award_withdraw", AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<RequestDetail>.NotFound();
        }
        var modify = AccessPolicy.Evaluate(subject, r.Markings, PlmAction.Modify);
        if (!modify.Permitted) return await DenyAsync(conn, tx, subject, "rfq.award_withdraw", number, correlationId, modify.Reasons, ct);
        Guid? awardId = null, entryId = null; bool entryCreated = false;
        await using (var find = new NpgsqlCommand(
            "SELECT a.id, a.entry_id, a.entry_created FROM src.award a JOIN plm.part_master pm ON pm.id = a.part_master_id WHERE a.request_id = @r AND a.withdrawn_at IS NULL AND pm.number = @n FOR UPDATE OF a", conn, tx))
        {
            find.Parameters.AddWithValue("r", r.Id);
            find.Parameters.AddWithValue("n", partNumber.Trim());
            await using var rd = await find.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct)) { awardId = rd.GetGuid(0); entryId = rd.GetGuid(1); entryCreated = rd.GetBoolean(2); }
        }
        if (awardId is null) return Outcome<RequestDetail>.Conflict($"{partNumber.Trim()} has no active award.");
        var now = clock.GetUtcNow();
        await using (var upd = new NpgsqlCommand("UPDATE src.award SET withdrawn_at = @now, withdrawn_by = @by, withdraw_reason = @reason WHERE id = @id", conn, tx))
        {
            upd.Parameters.AddWithValue("now", now);
            upd.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            upd.Parameters.AddWithValue("reason", reason.Trim());
            upd.Parameters.AddWithValue("id", awardId.Value);
            await upd.ExecuteNonQueryAsync(ct);
        }
        var entryWithdrawn = false;
        if (entryCreated && entryId is { } eid)
        {
            var entry = await EntryStore.LoadByIdAsync(conn, tx, eid, true, ct);
            if (entry is { ApprovalState: EntryApprovalStates.Draft })
            {
                await EntryStore.SetStateAsync(conn, tx, eid, EntryApprovalStates.Withdrawn, subject.Username ?? subject.Id, now, "The award on " + number + " was withdrawn", ct);
                entryWithdrawn = true;
            }
        }
        await audit.AppendAsync(tx, Entry(subject, "rfq.award_withdraw", AuditOutcome.Success, number, correlationId,
            ("part", partNumber.Trim()), ("entryWithdrawn", entryWithdrawn ? "true" : "false"), ("reason", reason.Trim())), ct);
        var detail = await BuildAsync(conn, tx, subject, (await RequestRow.LoadAsync(conn, tx, number, false, ct))!, ct);
        await tx.CommitAsync(ct);
        return Outcome<RequestDetail>.Ok(detail);
    }
}
