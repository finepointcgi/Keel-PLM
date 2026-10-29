using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Npgsql;

namespace Keel.Sourcing;

/// <summary>
/// Lets the workflow engine (Keel.Workflow, which must not reference this module) review a supplier like any other object (ADR-0018, ADR-0037): approval of a
/// supplier is granted by signatures. The engine's "revision" and "iteration" are the supplier record itself; the signed content is a readable summary
/// of the record and every certification, plus a digest recomputed from the live rows, so a certification added or changed after signing makes verification fail.
/// All methods run in the engine's transaction.
/// </summary>
internal sealed class SupplierWorkflowGateway(IAuditLog audit, TimeProvider clock, INotificationSink? notifications = null) : IWorkflowObjectGateway
{
    public const string ContentFormat = "supplier-content/1";
    public string ObjectType => SourcingStore.SupplierObjectType;

    private static WorkflowObject Map(SupplierRow s) =>
        new(SourcingStore.SupplierObjectType, s.Id, s.Number, s.Name, "1", s.Id, s.Id, s.Number, s.Status, SupplierStatuses.Label(s.Status), s.InReview, true, null, s.Markings);

    public async Task<WorkflowObject?> FindByKeyAsync(DbConnection conn, DbTransaction tx, string key, bool forUpdate, CancellationToken ct)
    {
        var row = await SourcingStore.LoadSupplierAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, key, forUpdate, ct);
        return row is null ? null : Map(row);
    }

    public async Task<WorkflowObject?> FindByRevisionAsync(DbConnection conn, DbTransaction tx, Guid revisionId, bool forUpdate, CancellationToken ct)
    {
        var row = await SourcingStore.LoadSupplierByIdAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, revisionId, forUpdate, ct);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyDictionary<Guid, WorkflowObject>> FindManyByRevisionAsync(DbConnection conn, DbTransaction tx, IReadOnlyCollection<Guid> revisionIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, WorkflowObject>();
        foreach (var id in revisionIds)
            if (await FindByRevisionAsync(conn, tx, id, false, ct) is { } o) result[id] = o;
        return result;
    }

    private async Task<int> ValidCertificationsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM src.supplier_certification WHERE supplier_id = @s AND NOT revoked AND expires_on >= @today", conn, tx);
        cmd.Parameters.AddWithValue("s", id);
        cmd.Parameters.AddWithValue("today", SourcingStore.Today(clock));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async Task<GatewayResult> BeginReviewAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var row = await SourcingStore.LoadSupplierByIdAsync(c, t, obj.Id, true, ct);
        if (row is null) return GatewayResult.Fail("The supplier no longer exists.");
        if (row.InReview) return GatewayResult.Fail($"{row.Number} is already in a review.");
        if (row.ProposedStatus is null) return GatewayResult.Fail("Choose the status the review should grant (approved or conditional) before submitting.");
        if (SourcingRules.CheckProposal(row.Status, row.ProposedStatus, await ValidCertificationsAsync(c, t, row.Id, ct)) is { } problem) return GatewayResult.Fail(problem);
        await SetAsync(c, t, row.Id, "in_review = true", actor, now, ct);
        await audit.AppendAsync(t, SourcingStore.Entry(actor, "supplier.submit", AuditOutcome.Success, SourcingStore.SupplierObjectType, row.Number, null, ("proposed", row.ProposedStatus)), ct);
        return GatewayResult.Ok;
    }

    public async Task<GatewayResult> CompleteReleaseAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Guid boundIterationId, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var row = await SourcingStore.LoadSupplierByIdAsync(c, t, obj.Id, true, ct);
        if (row is null) return GatewayResult.Fail("The supplier no longer exists.");
        if (!row.InReview || row.ProposedStatus is null) return GatewayResult.Fail($"{row.Number} is not in a review.");
        if (boundIterationId != row.Id) return GatewayResult.Fail($"The review is not bound to {row.Number}.");
        // The record signed is the record approved: a certification changed since the review began changes the digest the signatures cover.
        if (SourcingRules.CheckProposal(row.Status, row.ProposedStatus, await ValidCertificationsAsync(c, t, row.Id, ct)) is { } problem) return GatewayResult.Fail(problem);
        await using (var upd = new NpgsqlCommand(
            "UPDATE src.supplier SET status = @p, proposed_status = NULL, in_review = false, status_reason = NULL, revision = revision + 1, updated_at = @now, updated_by = @by WHERE id = @id", c, t))
        {
            upd.Parameters.AddWithValue("p", row.ProposedStatus);
            upd.Parameters.AddWithValue("now", now);
            upd.Parameters.AddWithValue("by", actor.Username ?? actor.Id);
            upd.Parameters.AddWithValue("id", row.Id);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(t, SourcingStore.Entry(actor, "supplier.approve", AuditOutcome.Success, SourcingStore.SupplierObjectType, row.Number, null,
            ("from", row.Status), ("to", row.ProposedStatus), ("path", "workflow")), ct);
        if (notifications is not null)
            await notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.SupplierStatusChanged, AwarenessKinds.Supplier, row.Number, row.Markings, actor.Id), ct);
        return GatewayResult.Ok;
    }

    public async Task<GatewayResult> ReturnToInWorkAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var row = await SourcingStore.LoadSupplierByIdAsync(c, t, obj.Id, true, ct);
        if (row is null) return GatewayResult.Fail("The supplier no longer exists.");
        if (!row.InReview) return GatewayResult.Fail($"{row.Number} is not in a review.");
        await SetAsync(c, t, row.Id, "in_review = false, proposed_status = NULL", actor, now, ct);
        await audit.AppendAsync(t, SourcingStore.Entry(actor, "supplier.review_cancel", AuditOutcome.Success, SourcingStore.SupplierObjectType, row.Number, null), ct);
        return GatewayResult.Ok;
    }

    private static async Task SetAsync(NpgsqlConnection c, NpgsqlTransaction t, Guid id, string set, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        await using var upd = new NpgsqlCommand($"UPDATE src.supplier SET {set}, revision = revision + 1, updated_at = @now, updated_by = @by WHERE id = @id", c, t);
        upd.Parameters.AddWithValue("now", now);
        upd.Parameters.AddWithValue("by", actor.Username ?? actor.Id);
        upd.Parameters.AddWithValue("id", id);
        await upd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IterationContent?> LoadIterationContentAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var row = await SourcingStore.LoadSupplierByIdAsync(c, t, iterationId, false, ct);
        if (row is null) return null;
        var certs = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT cert_type, certificate_number, issuer, expires_on, revoked FROM src.supplier_certification WHERE supplier_id = @s ORDER BY cert_type, certificate_number", c, t))
        {
            cmd.Parameters.AddWithValue("s", row.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                certs.Add($"{r.GetString(0)} {r.GetString(1)} ({r.GetString(2)}) expires {r.GetFieldValue<DateOnly>(3):yyyy-MM-dd}{(r.GetBoolean(4) ? " revoked" : "")}");
        }
        // The live digest: editing a row after signing changes what verification computes.
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            row.Number, row.Name, row.CageCode, row.Country, string.Join(',', row.Roles), row.IsForeign, row.ClearanceProfile, row.Markings.BannerText(), row.ProposedStatus, string.Join('\n', certs)))));
        var description = new StringBuilder()
            .AppendLine($"{row.Number}: {row.Name}")
            .AppendLine($"Country {row.Country}{(row.IsForeign ? " (foreign)" : "")}; roles {string.Join(", ", row.Roles)}")
            .AppendLine($"CAGE {row.CageCode ?? "none"}")
            .AppendLine($"Status {SupplierStatuses.Label(row.Status)} to {SupplierStatuses.Label(row.ProposedStatus ?? row.Status)}")
            .AppendLine($"Markings: {row.Markings.BannerText()}")
            .Append(certs.Count == 0 ? "No certifications" : "Certifications:\n" + string.Join('\n', certs))
            .ToString();
        var attributes = new JsonObject
        {
            ["format"] = ContentFormat, ["status"] = row.Status, ["proposed"] = row.ProposedStatus, ["certifications"] = certs.Count, ["contentSha256"] = digest,
        };
        return new IterationContent(iterationId, SourcingStore.SupplierObjectType, row.Number, "1", row.Number, description, attributes.ToJsonString());
    }
}

/// <summary>
/// Review of an approved list entry (AML or AVL) through the workflow engine. The readable object of the review is the entry; the markings the engine
/// checks readers and signers against are the PART's, so only people who may read the part can review or sign. Releasing makes the entry Active.
/// </summary>
internal sealed class SourceEntryWorkflowGateway(IAuditLog audit, TimeProvider clock) : IWorkflowObjectGateway
{
    public const string ContentFormat = "source-entry-content/1";
    public string ObjectType => SourcingStore.EntryObjectType;

    private static WorkflowObject Map(EntryRow e) =>
        new(SourcingStore.EntryObjectType, e.Id, e.Number, $"{e.PartNumber} ({SourceKinds.Label(e.Kind).ToLowerInvariant()})", "1", e.Id, e.Id, e.Number, e.ApprovalState,
            EntryApprovalStates.Label(e.ApprovalState), e.ApprovalState == EntryApprovalStates.UnderReview, true, null, e.PartMarkings);

    public async Task<WorkflowObject?> FindByKeyAsync(DbConnection conn, DbTransaction tx, string key, bool forUpdate, CancellationToken ct)
    {
        var row = await EntryStore.LoadAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, key, forUpdate, ct);
        return row is null ? null : Map(row);
    }

    public async Task<WorkflowObject?> FindByRevisionAsync(DbConnection conn, DbTransaction tx, Guid revisionId, bool forUpdate, CancellationToken ct)
    {
        var row = await EntryStore.LoadByIdAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, revisionId, forUpdate, ct);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyDictionary<Guid, WorkflowObject>> FindManyByRevisionAsync(DbConnection conn, DbTransaction tx, IReadOnlyCollection<Guid> revisionIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, WorkflowObject>();
        foreach (var id in revisionIds)
            if (await FindByRevisionAsync(conn, tx, id, false, ct) is { } o) result[id] = o;
        return result;
    }

    public async Task<GatewayResult> BeginReviewAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var e = await EntryStore.LoadByIdAsync(c, t, obj.Id, true, ct);
        if (e is null) return GatewayResult.Fail("The entry no longer exists.");
        if (e.ApprovalState != EntryApprovalStates.Draft) return GatewayResult.Fail($"{e.Number} is {EntryApprovalStates.Label(e.ApprovalState).ToLowerInvariant()}; only a draft entry can be submitted for review.");
        if (EntryStore.CheckApprovable(e, SourcingStore.Today(clock)) is { } problem) return GatewayResult.Fail(problem);
        await EntryStore.SetStateAsync(c, t, e.Id, EntryApprovalStates.UnderReview, actor.Username ?? actor.Id, now, null, ct);
        await audit.AppendAsync(t, SourcingStore.Entry(actor, "sourcing.entry_submit", AuditOutcome.Success, "part", e.PartNumber, null, ("entry", e.Number), ("list", e.Kind)), ct);
        return GatewayResult.Ok;
    }

    public async Task<GatewayResult> CompleteReleaseAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Guid boundIterationId, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var e = await EntryStore.LoadByIdAsync(c, t, obj.Id, true, ct);
        if (e is null) return GatewayResult.Fail("The entry no longer exists.");
        if (e.ApprovalState != EntryApprovalStates.UnderReview) return GatewayResult.Fail($"{e.Number} is not under review.");
        if (boundIterationId != e.Id) return GatewayResult.Fail($"The review is not bound to {e.Number}.");
        if (EntryStore.CheckApprovable(e, SourcingStore.Today(clock)) is { } problem) return GatewayResult.Fail(problem);
        await EntryStore.SetStateAsync(c, t, e.Id, EntryApprovalStates.Active, actor.Username ?? actor.Id, now, null, ct);
        await audit.AppendAsync(t, SourcingStore.Entry(actor, "sourcing.entry_approve", AuditOutcome.Success, "part", e.PartNumber, null, ("entry", e.Number), ("list", e.Kind), ("status", e.Status), ("path", "workflow")), ct);
        return GatewayResult.Ok;
    }

    public async Task<GatewayResult> ReturnToInWorkAsync(DbConnection conn, DbTransaction tx, WorkflowObject obj, Subject actor, DateTimeOffset now, CancellationToken ct)
    {
        var c = (NpgsqlConnection)conn; var t = (NpgsqlTransaction)tx;
        var e = await EntryStore.LoadByIdAsync(c, t, obj.Id, true, ct);
        if (e is null) return GatewayResult.Fail("The entry no longer exists.");
        if (e.ApprovalState != EntryApprovalStates.UnderReview) return GatewayResult.Fail($"{e.Number} is not under review.");
        await EntryStore.SetStateAsync(c, t, e.Id, EntryApprovalStates.Draft, actor.Username ?? actor.Id, now, null, ct);
        await audit.AppendAsync(t, SourcingStore.Entry(actor, "sourcing.entry_review_cancel", AuditOutcome.Success, "part", e.PartNumber, null, ("entry", e.Number)), ct);
        return GatewayResult.Ok;
    }

    public async Task<IterationContent?> LoadIterationContentAsync(DbConnection conn, DbTransaction tx, Guid iterationId, CancellationToken ct)
    {
        var e = await EntryStore.LoadByIdAsync((NpgsqlConnection)conn, (NpgsqlTransaction)tx, iterationId, false, ct);
        if (e is null) return null;
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            e.Number, e.Kind, e.PartNumber, e.RevisionScope, e.SupplierNumber, e.Mpn, e.Status, e.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            e.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e.VendorPartNumber, e.LeadTimeDays, e.Moq, e.Currency, e.PriceBreaksJson))));
        var description = new StringBuilder()
            .AppendLine($"{e.Number}: {SourceKinds.Label(e.Kind)} entry for part {e.PartNumber}{(e.RevisionScope is null ? "" : $" revision {e.RevisionScope}")}")
            .AppendLine($"{(e.Kind == SourceKinds.Aml ? "Manufacturer" : "Vendor")} {e.SupplierNumber} ({e.SupplierName}){(e.Mpn is null ? "" : $", manufacturer part {e.Mpn}")}")
            .AppendLine($"Status {EntryStatuses.Label(e.Status)}; effective {e.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "from approval"} to {e.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "no end"}")
            .Append(e.VendorPartNumber is null && e.LeadTimeDays is null && e.Moq is null ? "No vendor data" :
                $"Vendor part {e.VendorPartNumber ?? "-"}; lead time {e.LeadTimeDays?.ToString(CultureInfo.InvariantCulture) ?? "-"} days; minimum order {e.Moq?.ToString("0.####", CultureInfo.InvariantCulture) ?? "-"}")
            .ToString();
        var attributes = new JsonObject { ["format"] = ContentFormat, ["kind"] = e.Kind, ["status"] = e.Status, ["contentSha256"] = digest };
        return new IterationContent(iterationId, SourcingStore.EntryObjectType, e.Number, "1", e.Number, description, attributes.ToJsonString());
    }
}
