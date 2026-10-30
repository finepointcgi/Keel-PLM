using System.Globalization;
using System.Text.Json;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Sourcing;

/// <summary>
/// Supplier quality scorecards (ADR-0054): computed on demand from data Keel already holds (linked nonconformances and corrective actions, the supplier's answers to requests, its
/// certifications) by the transparent formula of <see cref="ScorecardRules"/>. A scorecard is a view of the buyer's data; nothing about it reaches the supplier until a buyer
/// publishes it, and what is published is a frozen copy that counts only records releasable to that supplier (the outbound release check against its clearance profile).
/// </summary>
// ASSUMPTION(A78): a scorecard counts only what its viewer may read; the supplier's copy only what may be released to that supplier, and only on purpose.
public sealed class ScorecardService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, ISupplierQualityFacts? quality = null)
{
    public const string ObjectType = "supplier";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string? number, string? corr, params (string, string?)[] details) =>
        SourcingStore.Entry(s, action, outcome, ObjectType, number, corr, details);

    private sealed record Inputs(IReadOnlyList<SupplierQualityFact> Facts, int Linked, int Unreadable, IReadOnlyList<RequestOutcome> Requests, IReadOnlyList<CertificationFact> Certifications, IReadOnlyList<Markings> RequestMarkings);

    /// <summary>
    /// The facts of one supplier for one viewer. Quality facts are the ones the viewer may read; requests are the ones the viewer may read; with <paramref name="releaseTo"/> only what
    /// that supplier may receive is kept (for publication).
    /// </summary>
    private async Task<Inputs> LoadInputsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, SupplierRow supplier, bool forSupplier, CancellationToken ct)
    {
        var refs = new List<(string Kind, string Id)>();
        await using (var cmd = new NpgsqlCommand("SELECT ref_kind, ref_id FROM src.supplier_quality_ref WHERE supplier_id = @s AND ref_kind IN ('ncr', 'capa')", conn, tx))
        {
            cmd.Parameters.AddWithValue("s", supplier.Id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) refs.Add((rd.GetString(0), rd.GetString(1)));
        }
        var facts = refs.Count == 0 || quality is null ? [] : await quality.ReadAsync(subject, refs, ct);
        var unreadable = refs.Count - facts.Count;
        var profile = forSupplier ? await SourcingStore.LoadProfileAsync(conn, tx, supplier.ClearanceProfile, ct) : null;
        if (forSupplier) facts = [.. facts.Where(f => SourcingRules.ReleaseToSupplier(supplier.IsForeign, profile, f.Markings).Permitted)];

        var filter = AccessPolicy.BuildReadFilter(subject, "r");
        var requests = new List<RequestOutcome>();
        var requestMarkings = new List<Markings>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT r.issued_at, r.due_on, (SELECT min(p.submitted_at) FROM src.response p WHERE p.request_id = r.id AND p.supplier_id = rr.supplier_id), {MarkingColumns.Select("r")}
            FROM src.request_recipient rr JOIN src.request r ON r.id = rr.request_id
            WHERE rr.supplier_id = @s AND r.issued_at IS NOT NULL AND {filter.Sql}
            """, conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("s", supplier.Id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var marked = MarkingColumns.Read(rd, 3);
                if (forSupplier && !SourcingRules.ReleaseToSupplier(supplier.IsForeign, profile, marked).Permitted) continue;
                requestMarkings.Add(marked);
                requests.Add(new RequestOutcome(Day(rd.GetFieldValue<DateTimeOffset>(0)), rd.IsDBNull(1) ? null : rd.GetFieldValue<DateOnly>(1), rd.IsDBNull(2) ? null : Day(rd.GetFieldValue<DateTimeOffset>(2))));
            }
        }
        var certs = new List<CertificationFact>();
        await using (var cmd = new NpgsqlCommand("SELECT issued_on, expires_on, revoked FROM src.supplier_certification WHERE supplier_id = @s", conn, tx))
        {
            cmd.Parameters.AddWithValue("s", supplier.Id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) certs.Add(new CertificationFact(rd.GetFieldValue<DateOnly>(0), rd.GetFieldValue<DateOnly>(1), rd.GetBoolean(2)));
        }
        return new Inputs(facts, refs.Count, unreadable, requests, certs, requestMarkings);
    }

    private static DateOnly Day(DateTimeOffset t) => DateOnly.FromDateTime(t.UtcDateTime);

    private async Task<(Outcome<T>? Refusal, SupplierRow? Supplier)> OpenAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string number, string action, PlmAction need, string? corr, CancellationToken ct)
    {
        var s = await SourcingStore.LoadSupplierAsync(conn, tx, number, false, ct);
        if (s is null) return (Outcome<T>.NotFound(), null);
        var read = AccessPolicy.Evaluate(subject, s.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, corr, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return (Outcome<T>.NotFound(), null);
        }
        if (need != PlmAction.Read)
        {
            var d = AccessPolicy.Evaluate(subject, s.Markings, need);
            if (!d.Permitted)
            {
                await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, corr, ("reasons", string.Join(",", d.Reasons))), ct);
                await tx.CommitAsync(ct);
                return (Outcome<T>.Forbidden([.. d.Reasons]), null);
            }
        }
        return (null, s);
    }

    // ---------------------------------------------------------------- the buyer's view

    public async Task<Outcome<SupplierScorecard>> GetAsync(Subject subject, string supplierNumber, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (refusal, supplier) = await OpenAsync<SupplierScorecard>(conn, tx, subject, supplierNumber, "scorecard.read", PlmAction.Read, correlationId, ct);
        if (supplier is null) return refusal!;
        var today = SourcingStore.Today(clock);
        var inputs = await LoadInputsAsync(conn, tx, subject, supplier, false, ct);
        var card = ScorecardRules.Compute(today, inputs.Facts, inputs.Linked, inputs.Requests, inputs.Certifications, today);
        var published = await LatestPublicationAsync(conn, tx, supplier.Id, ct);
        await audit.AppendAsync(tx, Entry(subject, "scorecard.read", AuditOutcome.Success, supplier.Number, correlationId), ct);
        await tx.CommitAsync(ct);
        var banner = Markings.AggregateBanner(new[] { supplier.Markings }.Concat(inputs.Facts.Where(f => !f.Cancelled).Select(f => f.Markings)).Concat(inputs.RequestMarkings));
        return Outcome<SupplierScorecard>.Ok(new SupplierScorecard(supplier.Number, supplier.Name, banner, card, inputs.Unreadable + inputs.Facts.Count(f => f.Cancelled), published?.View,
            AccessPolicy.Evaluate(subject, supplier.Markings, PlmAction.Modify).Permitted));
    }

    private static async Task<(ScorecardPublicationView View, string PayloadJson)?> LatestPublicationAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid supplierId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT id, published_at, published_by, as_of, payload::text FROM src.scorecard_publication WHERE supplier_id = @s AND withdrawn_at IS NULL ORDER BY published_at DESC LIMIT 1", conn, tx);
        cmd.Parameters.AddWithValue("s", supplierId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        var payload = rd.GetString(4);
        var card = JsonSerializer.Deserialize<ScorecardView>(payload, Json)!;
        return (new ScorecardPublicationView(rd.GetGuid(0), rd.GetFieldValue<DateTimeOffset>(1), rd.GetString(2), rd.GetFieldValue<DateOnly>(3), card.Score), payload);
    }

    /// <summary>The scorecard a supplier may see: the latest published, not withdrawn one. Used by the portal, which passes the caller's own supplier id.</summary>
    internal static async Task<PortalScorecard?> PublishedForAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid supplierId, CancellationToken ct)
    {
        var latest = await LatestPublicationAsync(conn, tx, supplierId, ct);
        return latest is null ? null : new PortalScorecard(JsonSerializer.Deserialize<ScorecardView>(latest.Value.PayloadJson, Json)!, latest.Value.View.PublishedAt);
    }

    // ---------------------------------------------------------------- publish and withdraw

    /// <summary>
    /// Publishes the current scorecard to the supplier's portal users (a frozen copy; a later publication replaces it for the supplier, earlier ones stay in the history). Needs
    /// permission to modify the supplier. Refused when a linked quality record is hidden from the publisher: the card would understate it, so someone who may read all of them publishes.
    /// The published card counts only records that may be released to this supplier.
    /// </summary>
    public async Task<Outcome<SupplierScorecard>> PublishAsync(Subject subject, string supplierNumber, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (refusal, supplier) = await OpenAsync<SupplierScorecard>(conn, tx, subject, supplierNumber, "scorecard.publish", PlmAction.Modify, correlationId, ct);
        if (supplier is null) return refusal!;
        if (supplier.Status is SupplierStatuses.Obsolete or SupplierStatuses.Disapproved) return Outcome<SupplierScorecard>.Conflict($"{supplier.Number} is {SupplierStatuses.Label(supplier.Status).ToLowerInvariant()}; its scorecard is not published.");
        var today = SourcingStore.Today(clock);
        var viewer = await LoadInputsAsync(conn, tx, subject, supplier, false, ct);
        if (viewer.Unreadable > 0) return Outcome<SupplierScorecard>.Conflict("Some quality records linked to this supplier are hidden from you, so the scorecard would understate them. Ask someone who may read them to publish.");
        var inputs = await LoadInputsAsync(conn, tx, subject, supplier, true, ct);
        var card = ScorecardRules.Compute(today, inputs.Facts, inputs.Linked, inputs.Requests, inputs.Certifications, today) with
        {
            Banner = Markings.AggregateBanner(inputs.Facts.Where(f => !f.Cancelled).Select(f => f.Markings).Concat(inputs.RequestMarkings)),
        };
        var id = Guid.CreateVersion7();
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO src.scorecard_publication (id, supplier_id, as_of, formula_version, payload, published_at, published_by) VALUES (@id, @s, @asof, @v, @payload::jsonb, @now, @by)", conn, tx))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("s", supplier.Id);
            ins.Parameters.AddWithValue("asof", card.AsOf);
            ins.Parameters.AddWithValue("v", card.FormulaVersion);
            ins.Parameters.AddWithValue("payload", JsonSerializer.Serialize(card, Json));
            ins.Parameters.AddWithValue("now", clock.GetUtcNow());
            ins.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "scorecard.publish", AuditOutcome.Success, supplier.Number, correlationId,
            ("score", card.Score?.ToString("0.0", CultureInfo.InvariantCulture)), ("asOf", card.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("formula", card.FormulaVersion.ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return await GetAsync(subject, supplierNumber, correlationId, ct);
    }

    /// <summary>Withdraws the published scorecard: the supplier no longer sees any scorecard until a new one is published.</summary>
    public async Task<Outcome<SupplierScorecard>> WithdrawAsync(Subject subject, string supplierNumber, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (refusal, supplier) = await OpenAsync<SupplierScorecard>(conn, tx, subject, supplierNumber, "scorecard.withdraw", PlmAction.Modify, correlationId, ct);
        if (supplier is null) return refusal!;
        int rows;
        await using (var upd = new NpgsqlCommand("UPDATE src.scorecard_publication SET withdrawn_at = @now, withdrawn_by = @by WHERE supplier_id = @s AND withdrawn_at IS NULL", conn, tx))
        {
            upd.Parameters.AddWithValue("now", clock.GetUtcNow());
            upd.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            upd.Parameters.AddWithValue("s", supplier.Id);
            rows = await upd.ExecuteNonQueryAsync(ct);
        }
        if (rows == 0) return Outcome<SupplierScorecard>.Conflict("No scorecard is published to this supplier.");
        await audit.AppendAsync(tx, Entry(subject, "scorecard.withdraw", AuditOutcome.Success, supplier.Number, correlationId), ct);
        await tx.CommitAsync(ct);
        return await GetAsync(subject, supplierNumber, correlationId, ct);
    }
}
