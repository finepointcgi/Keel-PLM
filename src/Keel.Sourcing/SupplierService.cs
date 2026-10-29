using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Sourcing;

/// <summary>
/// The supplier master (ADR-0037): vendor and manufacturer records with certifications, contacts, opaque quality references and the link of portal users.
/// Every call evaluates the access policy (ADR-0004) and writes its audit event in the same transaction as its change (ADR-0005), denials included.
/// Audit details carry numbers, codes and counts only: never names, contact data or free text. Mutations serialize on <c>SELECT ... FOR UPDATE</c> of the supplier row.
/// </summary>
public sealed class SupplierService(
    NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, INotificationSink? notifications = null, IEnumerable<IQualityReferenceResolver>? resolvers = null)
{
    public const string ObjectType = SourcingStore.SupplierObjectType;
    private readonly IQualityReferenceResolver[] _resolvers = resolvers?.ToArray() ?? [];

    public static readonly SortMap SortKeys = new(
        ("number", ["s.number"]), ("name", ["lower(s.name)"]), ("country", ["s.country"]), ("status", ["s.status"]), ("cage", ["s.cage_code"]), ("foreign", ["s.is_foreign"]));

    private static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string? number, string? corr, params (string, string?)[] details) =>
        SourcingStore.Entry(s, action, outcome, ObjectType, number, corr, details);

    // ---------------------------------------------------------------- create

    public async Task<Outcome<SupplierDetail>> CreateAsync(Subject subject, CreateSupplierCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        var name = cmd.Name?.Trim() ?? "";
        var country = cmd.Country?.Trim().ToUpperInvariant() ?? "";
        var cage = SourcingRules.Clean(cmd.CageCode)?.ToUpperInvariant();
        var duns = SourcingRules.Clean(cmd.DunsId);
        var errors = SourcingRules.ValidateSupplier(name, country, cmd.Roles, cmd.IsForeign, cage, duns);
        if (errors.Count > 0) return Outcome<SupplierDetail>.Invalid(errors);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var modify = AccessPolicy.Evaluate(subject, cmd.Markings, PlmAction.Modify);
        if (!modify.Permitted) return await DeniedAsync<SupplierDetail>(conn, tx, subject, "supplier.create", null, correlationId, modify.Reasons, ct);
        var profile = SourcingRules.Clean(cmd.ClearanceProfile);
        if (profile is not null)
        {
            if (!AccessPolicy.HasRoleFor(subject, PlmAction.Administer)) return await DeniedAsync<SupplierDetail>(conn, tx, subject, "supplier.create", null, correlationId, [AccessPolicy.RoleMissing], ct);
            if (await SourcingStore.LoadProfileAsync(conn, tx, profile, ct) is null) return Outcome<SupplierDetail>.Invalid([$"There is no active clearance profile '{profile}'."]);
        }
        if (cage is not null && await ExistsAsync(conn, tx, "SELECT 1 FROM src.supplier WHERE cage_code = @v", cage, ct)) return Outcome<SupplierDetail>.Conflict("A supplier with this CAGE code exists already.");

        var number = SourcingStore.NextNumber("SUP-", await SourcingStore.NextValAsync(conn, tx, "src.supplier_seq", ct));
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7();
        await using (var cmdIns = new NpgsqlCommand(
            $"""
            INSERT INTO src.supplier (id, number, name, cage_code, duns_id, country, roles, status, is_foreign, clearance_profile, {MarkingColumns.InsertColumns}, created_at, created_by)
            VALUES (@id, @number, @name, @cage, @duns, @country, @roles, 'prospective', @foreign, @profile, {MarkingColumns.InsertValues}, @now, @by)
            """, conn, tx))
        {
            cmdIns.Parameters.AddWithValue("id", id);
            cmdIns.Parameters.AddWithValue("number", number);
            cmdIns.Parameters.AddWithValue("name", name);
            cmdIns.Parameters.AddWithValue("cage", (object?)cage ?? DBNull.Value);
            cmdIns.Parameters.AddWithValue("duns", (object?)duns ?? DBNull.Value);
            cmdIns.Parameters.AddWithValue("country", country);
            cmdIns.Parameters.AddWithValue("roles", cmd.Roles.Distinct().Order(StringComparer.Ordinal).ToArray());
            cmdIns.Parameters.AddWithValue("foreign", cmd.IsForeign);
            cmdIns.Parameters.AddWithValue("profile", (object?)profile ?? DBNull.Value);
            foreach (var (k, v) in MarkingColumns.Parameters(cmd.Markings)) cmdIns.Parameters.AddWithValue(k, v);
            cmdIns.Parameters.AddWithValue("now", now);
            cmdIns.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await cmdIns.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "supplier.create", AuditOutcome.Success, number, correlationId,
            ("roles", string.Join(",", cmd.Roles.Order(StringComparer.Ordinal))), ("foreign", cmd.IsForeign ? "true" : "false"), ("markings", cmd.Markings.BannerText())), ct);
        if (notifications is not null) await notifications.AutoWatchAsync(conn, tx, subject.Id, AwarenessKinds.Supplier, number, ct);
        var row = (await SourcingStore.LoadSupplierAsync(conn, tx, number, false, ct))!;
        var detail = await BuildDetailAsync(conn, tx, subject, row, ct);
        await tx.CommitAsync(ct);
        return Outcome<SupplierDetail>.Ok(detail);
    }

    // ---------------------------------------------------------------- read

    public async Task<Outcome<SupplierDetail>> GetAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await SourcingStore.LoadSupplierAsync(conn, tx, number, false, ct);
        if (row is null) return Outcome<SupplierDetail>.NotFound();
        var read = AccessPolicy.Evaluate(subject, row.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "supplier.read", AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<SupplierDetail>.NotFound();
        }
        var detail = await BuildDetailAsync(conn, tx, subject, row, ct);
        await audit.AppendAsync(tx, Entry(subject, "supplier.read", AuditOutcome.Success, number, correlationId), ct);
        await tx.CommitAsync(ct);
        // Resolving quality references calls another module under the caller's own access: outside this transaction.
        return Outcome<SupplierDetail>.Ok(detail with { QualityRefs = await ResolveRefsAsync(subject, detail.QualityRefs, ct) });
    }

    private async Task<IReadOnlyList<QualityRefView>> ResolveRefsAsync(Subject subject, IReadOnlyList<QualityRefView> refs, CancellationToken ct)
    {
        var list = new List<QualityRefView>();
        foreach (var r in refs)
        {
            var resolver = _resolvers.FirstOrDefault(x => x.Handles(r.Kind));
            var resolved = resolver is null ? null : await resolver.ResolveAsync(subject, r.Kind, r.RefId, ct);
            list.Add(resolved is null ? r : r with { Label = resolved.Label, Href = resolved.Href, StateLabel = resolved.StateLabel });
        }
        return list;
    }

    private async Task<SupplierDetail> BuildDetailAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, SupplierRow row, CancellationToken ct)
    {
        var today = SourcingStore.Today(clock);
        var certs = new List<CertificationView>();
        await using (var cmd = new NpgsqlCommand("SELECT id, cert_type, certificate_number, issuer, scope, issued_on, expires_on, revoked FROM src.supplier_certification WHERE supplier_id = @s ORDER BY expires_on, cert_type", conn, tx))
        {
            cmd.Parameters.AddWithValue("s", row.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var expires = r.GetFieldValue<DateOnly>(6);
                var revoked = r.GetBoolean(7);
                certs.Add(new CertificationView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), SourcingStore.Str(r, 4), r.GetFieldValue<DateOnly>(5), expires, revoked,
                    SourcingRules.CertStanding(expires, revoked, today), expires.DayNumber - today.DayNumber));
            }
        }
        var contacts = new List<ContactView>();
        await using (var cmd = new NpgsqlCommand("SELECT id, name, email, phone, title, is_primary FROM src.supplier_contact WHERE supplier_id = @s ORDER BY is_primary DESC, lower(name)", conn, tx))
        {
            cmd.Parameters.AddWithValue("s", row.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) contacts.Add(new ContactView(r.GetGuid(0), r.GetString(1), SourcingStore.Str(r, 2), SourcingStore.Str(r, 3), SourcingStore.Str(r, 4), r.GetBoolean(5)));
        }
        var refs = new List<QualityRefView>();
        await using (var cmd = new NpgsqlCommand("SELECT id, ref_kind, ref_id FROM src.supplier_quality_ref WHERE supplier_id = @s ORDER BY ref_kind, ref_id", conn, tx))
        {
            cmd.Parameters.AddWithValue("s", row.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) refs.Add(new QualityRefView(r.GetGuid(0), r.GetString(1), r.GetString(2), null, null, null));
        }
        var canAdmin = AccessPolicy.Evaluate(subject, row.Markings, PlmAction.Administer).Permitted;
        var users = new List<PortalUserView>();
        if (canAdmin)
        {
            await using var cmd = new NpgsqlCommand("SELECT user_sub, username, display_name, active FROM src.portal_user WHERE supplier_id = @s ORDER BY lower(username)", conn, tx);
            cmd.Parameters.AddWithValue("s", row.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) users.Add(new PortalUserView(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)));
        }
        var canModify = AccessPolicy.Evaluate(subject, row.Markings, PlmAction.Modify).Permitted;
        return new SupplierDetail(row.ToSummary(), row.DunsId, row.ProposedStatus, row.StatusReason, row.ClearanceProfile, row.Revision, row.CreatedAt, row.CreatedBy, row.UpdatedAt,
            certs, contacts, refs, users, new SupplierPermissions(canModify, canAdmin));
    }

    public async Task<IReadOnlyList<string>> ClearanceProfilesAsync(CancellationToken ct = default) => await SourcingStore.ProfileNamesAsync(dataSource, ct);

    // ---------------------------------------------------------------- search

    public async Task<Outcome<SupplierPage>> SearchAsync(Subject subject, SupplierSearch query, string? correlationId, CancellationToken ct = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 200);
        var offset = Math.Max(0, query.Offset);
        var filter = AccessPolicy.BuildReadFilter(subject, "s");
        var where = new List<string>();
        var parameters = new List<KeyValuePair<string, object>>(filter.Parameters);
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            where.Add("(s.number ILIKE @q OR s.name ILIKE @q OR s.cage_code ILIKE @q)");
            parameters.Add(new("q", SourcingStore.Like(query.Text)));
        }
        if (!string.IsNullOrWhiteSpace(query.Status)) { where.Add("s.status = @status"); parameters.Add(new("status", query.Status.Trim())); }
        if (!string.IsNullOrWhiteSpace(query.Role)) { where.Add("@role = ANY(s.roles)"); parameters.Add(new("role", query.Role.Trim())); }
        if (!string.IsNullOrWhiteSpace(query.Country)) { where.Add("s.country = @country"); parameters.Add(new("country", query.Country.Trim().ToUpperInvariant())); }
        if (query.ForeignOnly) where.Add("s.is_foreign");
        var extra = string.Concat(where.Select(w => " AND " + w));

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int total;
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM src.supplier s WHERE {filter.Sql}{extra}", conn, tx))
        {
            foreach (var (k, v) in parameters) count.Parameters.AddWithValue(k, v);
            total = Convert.ToInt32(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        var items = new List<SupplierSummary>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {SourcingStore.SupplierSelect()} FROM src.supplier s WHERE {filter.Sql}{extra} ORDER BY {SortKeys.OrderBy(query.Sort, "s.number", "s.number")} LIMIT @limit OFFSET @offset", conn, tx))
        {
            foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) items.Add(SourcingStore.ReadSupplier(r).ToSummary());
        }
        var banner = Markings.AggregateBanner(items.Select(i => i.Markings).Distinct());
        await audit.AppendAsync(tx, Entry(subject, "supplier.search", AuditOutcome.Success, null, correlationId,
            ("status", query.Status), ("role", query.Role), ("country", query.Country), ("foreignOnly", query.ForeignOnly ? "true" : null),
            ("sort", query.Sort is null ? null : $"{query.Sort.Key} {query.Sort.DirectionText}"), ("results", items.Count.ToString(CultureInfo.InvariantCulture)), ("banner", banner)), ct);
        await tx.CommitAsync(ct);
        return Outcome<SupplierPage>.Ok(new SupplierPage(items, total, banner));
    }

    /// <summary>Key lookup for the dashboard's recent items: the suppliers among <paramref name="numbers"/> the caller may read.</summary>
    public async Task<IReadOnlyList<SupplierSummary>> FindReadableAsync(Subject subject, IReadOnlyCollection<string> numbers, CancellationToken ct = default)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "s");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {SourcingStore.SupplierSelect()} FROM src.supplier s WHERE s.number = ANY(@n) AND {filter.Sql}", conn);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("n", numbers.ToArray());
        var list = new List<SupplierSummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(SourcingStore.ReadSupplier(r).ToSummary());
        return list;
    }

    // ---------------------------------------------------------------- update

    public async Task<Outcome<SupplierDetail>> UpdateAsync(Subject subject, string number, UpdateSupplierCommand cmd, int expectedRevision, string? correlationId, CancellationToken ct = default)
    {
        var name = cmd.Name?.Trim() ?? "";
        var country = cmd.Country?.Trim().ToUpperInvariant() ?? "";
        var cage = SourcingRules.Clean(cmd.CageCode)?.ToUpperInvariant();
        var duns = SourcingRules.Clean(cmd.DunsId);
        var profile = SourcingRules.Clean(cmd.ClearanceProfile);
        var errors = SourcingRules.ValidateSupplier(name, country, cmd.Roles, cmd.IsForeign, cage, duns);
        if (errors.Count > 0) return Outcome<SupplierDetail>.Invalid(errors);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (row, refusal) = await OpenForChangeAsync<SupplierDetail>(conn, tx, subject, number, PlmAction.Modify, "supplier.update", correlationId, ct);
        if (row is null) return refusal!;
        if (row.Status == SupplierStatuses.Obsolete) return Outcome<SupplierDetail>.Conflict("An obsolete supplier cannot be edited.");
        if (row.InReview) return Outcome<SupplierDetail>.Conflict("The supplier is in an approval review; cancel the review to edit it.");
        if (row.Revision != expectedRevision) return Outcome<SupplierDetail>.Conflict("The supplier changed since you opened it. Reload and try again.");
        if (profile != row.ClearanceProfile)
        {
            // Releasing data to a supplier is an administrator decision.
            if (!AccessPolicy.HasRoleFor(subject, PlmAction.Administer)) return await DeniedAsync<SupplierDetail>(conn, tx, subject, "supplier.update", number, correlationId, [AccessPolicy.RoleMissing], ct);
            if (profile is not null && await SourcingStore.LoadProfileAsync(conn, tx, profile, ct) is null) return Outcome<SupplierDetail>.Invalid([$"There is no active clearance profile '{profile}'."]);
        }
        if (cage is not null && cage != row.CageCode && await ExistsAsync(conn, tx, "SELECT 1 FROM src.supplier WHERE cage_code = @v", cage, ct)) return Outcome<SupplierDetail>.Conflict("A supplier with this CAGE code exists already.");
        if (cmd.IsForeign && !row.IsForeign)
        {
            // The pairing rule is checked when entries are made; flagging a supplier foreign does not delete history. Entries for ITAR parts stop counting at once
            // (coverage re-checks the rule) and the number of open ones is reported so someone withdraws them.
            await using var open = new NpgsqlCommand(
                "SELECT count(*) FROM src.source_entry e JOIN plm.part_master m ON m.id = e.part_master_id WHERE e.supplier_id = @s AND m.export_jurisdiction = 'Itar' AND e.approval_state IN ('Draft', 'UnderReview', 'Active')", conn, tx);
            open.Parameters.AddWithValue("s", row.Id);
            var itarEntries = Convert.ToInt32(await open.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            if (itarEntries > 0)
                return Outcome<SupplierDetail>.Conflict($"This supplier is listed for {itarEntries} ITAR-marked part(s). Withdraw those entries before flagging the supplier foreign.");
        }
        var now = clock.GetUtcNow();
        await using (var upd = new NpgsqlCommand(
            """
            UPDATE src.supplier SET name = @name, cage_code = @cage, duns_id = @duns, country = @country, roles = @roles, is_foreign = @foreign, clearance_profile = @profile,
                   revision = revision + 1, updated_at = @now, updated_by = @by WHERE id = @id
            """, conn, tx))
        {
            upd.Parameters.AddWithValue("name", name);
            upd.Parameters.AddWithValue("cage", (object?)cage ?? DBNull.Value);
            upd.Parameters.AddWithValue("duns", (object?)duns ?? DBNull.Value);
            upd.Parameters.AddWithValue("country", country);
            upd.Parameters.AddWithValue("roles", cmd.Roles.Distinct().Order(StringComparer.Ordinal).ToArray());
            upd.Parameters.AddWithValue("foreign", cmd.IsForeign);
            upd.Parameters.AddWithValue("profile", (object?)profile ?? DBNull.Value);
            upd.Parameters.AddWithValue("now", now);
            upd.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            upd.Parameters.AddWithValue("id", row.Id);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "supplier.update", AuditOutcome.Success, number, correlationId,
            ("foreign", cmd.IsForeign ? "true" : "false"), ("clearanceProfile", profile), ("profileChanged", profile != row.ClearanceProfile ? "true" : null)), ct);
        var detail = await BuildDetailAsync(conn, tx, subject, (await SourcingStore.LoadSupplierAsync(conn, tx, number, false, ct))!, ct);
        await tx.CommitAsync(ct);
        return Outcome<SupplierDetail>.Ok(detail);
    }

    public async Task<Outcome<SupplierDetail>> ChangeMarkingsAsync(Subject subject, string number, Markings markings, string reason, string? correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000) return Outcome<SupplierDetail>.Invalid(["A reason is required (at most 1000 characters)."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (row, refusal) = await OpenForChangeAsync<SupplierDetail>(conn, tx, subject, number, PlmAction.Modify, "supplier.markings_change", correlationId, ct);
        if (row is null) return refusal!;
        // The change must leave the actor able to modify the supplier (no self-lockout), like every markings change.
        var after = AccessPolicy.Evaluate(subject, markings, PlmAction.Modify);
        if (!after.Permitted) return Outcome<SupplierDetail>.Invalid(["The new markings would lock you out of this supplier."]);
        var now = clock.GetUtcNow();
        await using (var upd = new NpgsqlCommand($"UPDATE src.supplier SET {MarkingColumns.UpdateSet}, revision = revision + 1, updated_at = @now, updated_by = @by WHERE id = @id", conn, tx))
        {
            foreach (var (k, v) in MarkingColumns.Parameters(markings)) upd.Parameters.AddWithValue(k, v);
            upd.Parameters.AddWithValue("now", now);
            upd.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            upd.Parameters.AddWithValue("id", row.Id);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "supplier.markings_change", AuditOutcome.Success, number, correlationId, ("before", row.Markings.BannerText()), ("after", markings.BannerText()), ("reason", reason.Trim())), ct);
        var detail = await BuildDetailAsync(conn, tx, subject, (await SourcingStore.LoadSupplierAsync(conn, tx, number, false, ct))!, ct);
        await tx.CommitAsync(ct);
        return Outcome<SupplierDetail>.Ok(detail);
    }

    // ---------------------------------------------------------------- status

    /// <summary>Chooses the status a review will grant (approved or conditional). The review itself (and the e-signatures) is the workflow engine's.</summary>
    public async Task<Outcome<SupplierDetail>> ProposeStatusAsync(Subject subject, string number, string proposed, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (row, refusal) = await OpenForChangeAsync<SupplierDetail>(conn, tx, subject, number, PlmAction.Modify, "supplier.propose", correlationId, ct);
        if (row is null) return refusal!;
        if (row.InReview) return Outcome<SupplierDetail>.Conflict("The supplier is already in a review.");
        var valid = await ValidCertificationsAsync(conn, tx, row.Id, ct);
        if (SourcingRules.CheckProposal(row.Status, proposed, valid) is { } problem) return Outcome<SupplierDetail>.Invalid([problem]);
        await using (var upd = new NpgsqlCommand("UPDATE src.supplier SET proposed_status = @p, revision = revision + 1, updated_at = @now, updated_by = @by WHERE id = @id", conn, tx))
        {
            upd.Parameters.AddWithValue("p", proposed);
            upd.Parameters.AddWithValue("now", clock.GetUtcNow());
            upd.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            upd.Parameters.AddWithValue("id", row.Id);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "supplier.propose", AuditOutcome.Success, number, correlationId, ("from", row.Status), ("proposed", proposed)), ct);
        var detail = await BuildDetailAsync(conn, tx, subject, (await SourcingStore.LoadSupplierAsync(conn, tx, number, false, ct))!, ct);
        await tx.CommitAsync(ct);
        return Outcome<SupplierDetail>.Ok(detail);
    }

    /// <summary>Disapproves or makes obsolete: the direction that withdraws standing, so it needs a reason but no signature.</summary>
    public async Task<Outcome<SupplierDetail>> SetStatusAsync(Subject subject, string number, string target, string reason, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (row, refusal) = await OpenForChangeAsync<SupplierDetail>(conn, tx, subject, number, PlmAction.Modify, "supplier.status", correlationId, ct);
        if (row is null) return refusal!;
        if (row.InReview) return Outcome<SupplierDetail>.Conflict("The supplier is in an approval review; cancel the review first.");
        if (SourcingRules.CheckDirectStatus(row.Status, target, reason) is { } problem) return Outcome<SupplierDetail>.Invalid([problem]);
        var now = clock.GetUtcNow();
        await using (var upd = new NpgsqlCommand(
            "UPDATE src.supplier SET status = @s, proposed_status = NULL, status_reason = @reason, revision = revision + 1, updated_at = @now, updated_by = @by WHERE id = @id", conn, tx))
        {
            upd.Parameters.AddWithValue("s", target);
            upd.Parameters.AddWithValue("reason", reason.Trim());
            upd.Parameters.AddWithValue("now", now);
            upd.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            upd.Parameters.AddWithValue("id", row.Id);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "supplier.status", AuditOutcome.Success, number, correlationId, ("from", row.Status), ("to", target), ("reason", reason.Trim())), ct);
        await NotifyStatusAsync(conn, tx, subject, row, ct);
        var detail = await BuildDetailAsync(conn, tx, subject, (await SourcingStore.LoadSupplierAsync(conn, tx, number, false, ct))!, ct);
        await tx.CommitAsync(ct);
        return Outcome<SupplierDetail>.Ok(detail);
    }

    internal Task NotifyStatusAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject actor, SupplierRow row, CancellationToken ct) =>
        notifications is null
            ? Task.CompletedTask
            : notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.SupplierStatusChanged, AwarenessKinds.Supplier, row.Number, row.Markings, actor.Id), ct);

    private async Task<int> ValidCertificationsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid supplierId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM src.supplier_certification WHERE supplier_id = @s AND NOT revoked AND expires_on >= @today", conn, tx);
        cmd.Parameters.AddWithValue("s", supplierId);
        cmd.Parameters.AddWithValue("today", SourcingStore.Today(clock));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------- certifications

    public async Task<Outcome<SupplierDetail>> AddCertificationAsync(Subject subject, string number, AddCertificationCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        var clean = cmd with { Type = cmd.Type?.Trim().ToUpperInvariant() ?? "", Number = cmd.Number?.Trim() ?? "", Issuer = cmd.Issuer?.Trim() ?? "", Scope = SourcingRules.Clean(cmd.Scope) };
        if (SourcingRules.ValidateCertification(clean) is { Count: > 0 } errors) return Outcome<SupplierDetail>.Invalid(errors);
        return await ChangeAsync(subject, number, "supplier.cert_add", correlationId, async (conn, tx, row) =>
        {
            var id = Guid.CreateVersion7();
            await using (var ins = new NpgsqlCommand(
                "INSERT INTO src.supplier_certification (id, supplier_id, cert_type, certificate_number, issuer, scope, issued_on, expires_on, created_at, created_by) VALUES (@id, @s, @t, @n, @i, @sc, @from, @to, @now, @by)", conn, tx))
            {
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("s", row.Id);
                ins.Parameters.AddWithValue("t", clean.Type);
                ins.Parameters.AddWithValue("n", clean.Number);
                ins.Parameters.AddWithValue("i", clean.Issuer);
                ins.Parameters.AddWithValue("sc", (object?)clean.Scope ?? DBNull.Value);
                ins.Parameters.AddWithValue("from", clean.IssuedOn);
                ins.Parameters.AddWithValue("to", clean.ExpiresOn);
                ins.Parameters.AddWithValue("now", clock.GetUtcNow());
                ins.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
                await ins.ExecuteNonQueryAsync(ct);
            }
            return [("certType", clean.Type), ("expires", clean.ExpiresOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))];
        }, ct);
    }

    public Task<Outcome<SupplierDetail>> RevokeCertificationAsync(Subject subject, string number, Guid certificationId, string reason, string? correlationId, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(reason) || reason.Length > 1000
            ? Task.FromResult(Outcome<SupplierDetail>.Invalid(["A reason is required (at most 1000 characters)."]))
            : ChangeAsync(subject, number, "supplier.cert_revoke", correlationId, async (conn, tx, row) =>
            {
                await using var upd = new NpgsqlCommand("UPDATE src.supplier_certification SET revoked = true WHERE id = @id AND supplier_id = @s AND NOT revoked RETURNING cert_type", conn, tx);
                upd.Parameters.AddWithValue("id", certificationId);
                upd.Parameters.AddWithValue("s", row.Id);
                var type = (string?)await upd.ExecuteScalarAsync(ct);
                return type is null ? null : [("certType", type), ("reason", reason.Trim())];
            }, ct, notFoundWhenNull: true);

    // ---------------------------------------------------------------- contacts

    public Task<Outcome<SupplierDetail>> AddContactAsync(Subject subject, string number, ContactInput contact, string? correlationId, CancellationToken ct = default)
    {
        var c = contact with { Name = contact.Name?.Trim() ?? "", Email = SourcingRules.Clean(contact.Email), Phone = SourcingRules.Clean(contact.Phone), Title = SourcingRules.Clean(contact.Title) };
        if (SourcingRules.ValidateContact(c) is { Count: > 0 } errors) return Task.FromResult(Outcome<SupplierDetail>.Invalid(errors));
        return ChangeAsync(subject, number, "supplier.contact_add", correlationId, async (conn, tx, row) =>
        {
            if (c.IsPrimary)
            {
                await using var clear = new NpgsqlCommand("UPDATE src.supplier_contact SET is_primary = false WHERE supplier_id = @s AND is_primary", conn, tx);
                clear.Parameters.AddWithValue("s", row.Id);
                await clear.ExecuteNonQueryAsync(ct);
            }
            await using var ins = new NpgsqlCommand(
                "INSERT INTO src.supplier_contact (id, supplier_id, name, email, phone, title, is_primary, created_at, created_by) VALUES (@id, @s, @n, @e, @p, @t, @pr, @now, @by)", conn, tx);
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("s", row.Id);
            ins.Parameters.AddWithValue("n", c.Name);
            ins.Parameters.AddWithValue("e", (object?)c.Email ?? DBNull.Value);
            ins.Parameters.AddWithValue("p", (object?)c.Phone ?? DBNull.Value);
            ins.Parameters.AddWithValue("t", (object?)c.Title ?? DBNull.Value);
            ins.Parameters.AddWithValue("pr", c.IsPrimary);
            ins.Parameters.AddWithValue("now", clock.GetUtcNow());
            ins.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await ins.ExecuteNonQueryAsync(ct);
            return [("primary", c.IsPrimary ? "true" : "false")]; // the contact's data stays out of the audit log
        }, ct);
    }

    public Task<Outcome<SupplierDetail>> RemoveContactAsync(Subject subject, string number, Guid contactId, string? correlationId, CancellationToken ct = default) =>
        ChangeAsync(subject, number, "supplier.contact_remove", correlationId, async (conn, tx, row) =>
        {
            await using var del = new NpgsqlCommand("DELETE FROM src.supplier_contact WHERE id = @id AND supplier_id = @s", conn, tx);
            del.Parameters.AddWithValue("id", contactId);
            del.Parameters.AddWithValue("s", row.Id);
            return await del.ExecuteNonQueryAsync(ct) == 0 ? null : [];
        }, ct, notFoundWhenNull: true);

    // ---------------------------------------------------------------- quality references

    /// <summary>Records an opaque reference to a quality record (kind such as <c>ncr</c>, and its id). Only the reference is stored.</summary>
    public Task<Outcome<SupplierDetail>> AddQualityRefAsync(Subject subject, string number, string kind, string refId, string? correlationId, CancellationToken ct = default)
    {
        kind = kind?.Trim().ToLowerInvariant() ?? "";
        refId = refId?.Trim() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(kind, "^[a-z][a-z-]{1,19}$") || refId.Length is 0 or > 100)
            return Task.FromResult(Outcome<SupplierDetail>.Invalid(["The reference needs a kind (for example ncr) and an id of at most 100 characters."]));
        return ChangeAsync(subject, number, "supplier.quality_ref_add", correlationId, async (conn, tx, row) =>
        {
            await using var ins = new NpgsqlCommand(
                "INSERT INTO src.supplier_quality_ref (id, supplier_id, ref_kind, ref_id, added_at, added_by) VALUES (@id, @s, @k, @r, @now, @by) ON CONFLICT DO NOTHING", conn, tx);
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("s", row.Id);
            ins.Parameters.AddWithValue("k", kind);
            ins.Parameters.AddWithValue("r", refId);
            ins.Parameters.AddWithValue("now", clock.GetUtcNow());
            ins.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await ins.ExecuteNonQueryAsync(ct);
            return [("refKind", kind), ("refId", refId)];
        }, ct);
    }

    public Task<Outcome<SupplierDetail>> RemoveQualityRefAsync(Subject subject, string number, Guid refId, string? correlationId, CancellationToken ct = default) =>
        ChangeAsync(subject, number, "supplier.quality_ref_remove", correlationId, async (conn, tx, row) =>
        {
            await using var del = new NpgsqlCommand("DELETE FROM src.supplier_quality_ref WHERE id = @id AND supplier_id = @s", conn, tx);
            del.Parameters.AddWithValue("id", refId);
            del.Parameters.AddWithValue("s", row.Id);
            return await del.ExecuteNonQueryAsync(ct) == 0 ? null : [];
        }, ct, notFoundWhenNull: true);

    // ---------------------------------------------------------------- portal users (administrators)

    /// <summary>
    /// Links a signed-in IdP user with the plm-supplier role to this supplier. Administrators only. A user belongs to at most one supplier; linking to another
    /// supplier is refused until the first link is removed.
    /// </summary>
    public async Task<Outcome<SupplierDetail>> LinkPortalUserAsync(Subject subject, string number, string usernameOrSub, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (row, refusal) = await OpenForChangeAsync<SupplierDetail>(conn, tx, subject, number, PlmAction.Administer, "supplier.portal_link", correlationId, ct);
        if (row is null) return refusal!;
        (string Sub, string Username, string Display, string[] Roles)? user = null;
        await using (var find = new NpgsqlCommand("SELECT sub, username, display_name, roles FROM identity.user_directory WHERE sub = @k OR lower(username) = lower(@k) ORDER BY (sub = @k) DESC LIMIT 1", conn, tx))
        {
            find.Parameters.AddWithValue("k", usernameOrSub.Trim());
            await using var r = await find.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) user = (r.GetString(0), r.GetString(1), r.GetString(2), (string[])r.GetValue(3));
        }
        if (user is null) return Outcome<SupplierDetail>.Invalid(["That user has not signed in yet. Ask them to sign in once, then link them."]);
        if (!user.Value.Roles.Contains(Roles.Supplier)) return Outcome<SupplierDetail>.Invalid(["That user does not have the supplier role at the identity provider."]);
        if (user.Value.Roles.Any(r => Roles.All.Contains(r))) return Outcome<SupplierDetail>.Invalid(["A user with an internal role cannot also be a portal user."]);
        await using (var other = new NpgsqlCommand("SELECT supplier_id FROM src.portal_user WHERE user_sub = @u", conn, tx))
        {
            other.Parameters.AddWithValue("u", user.Value.Sub);
            if ((Guid?)await other.ExecuteScalarAsync(ct) is { } existing && existing != row.Id) return Outcome<SupplierDetail>.Conflict("That user is linked to another supplier. Remove that link first.");
        }
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO src.portal_user (user_sub, supplier_id, username, display_name, created_at, created_by) VALUES (@u, @s, @un, @dn, @now, @by) ON CONFLICT (user_sub) DO UPDATE SET active = true, username = EXCLUDED.username, display_name = EXCLUDED.display_name", conn, tx))
        {
            ins.Parameters.AddWithValue("u", user.Value.Sub);
            ins.Parameters.AddWithValue("s", row.Id);
            ins.Parameters.AddWithValue("un", user.Value.Username);
            ins.Parameters.AddWithValue("dn", user.Value.Display);
            ins.Parameters.AddWithValue("now", clock.GetUtcNow());
            ins.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, Entry(subject, "supplier.portal_link", AuditOutcome.Success, number, correlationId, ("user", user.Value.Username)), ct);
        var detail = await BuildDetailAsync(conn, tx, subject, row, ct);
        await tx.CommitAsync(ct);
        return Outcome<SupplierDetail>.Ok(detail);
    }

    public async Task<Outcome<SupplierDetail>> UnlinkPortalUserAsync(Subject subject, string number, string userSub, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (row, refusal) = await OpenForChangeAsync<SupplierDetail>(conn, tx, subject, number, PlmAction.Administer, "supplier.portal_unlink", correlationId, ct);
        if (row is null) return refusal!;
        await using (var del = new NpgsqlCommand("DELETE FROM src.portal_user WHERE user_sub = @u AND supplier_id = @s", conn, tx))
        {
            del.Parameters.AddWithValue("u", userSub);
            del.Parameters.AddWithValue("s", row.Id);
            if (await del.ExecuteNonQueryAsync(ct) == 0) return Outcome<SupplierDetail>.NotFound();
        }
        await audit.AppendAsync(tx, Entry(subject, "supplier.portal_unlink", AuditOutcome.Success, number, correlationId), ct);
        var detail = await BuildDetailAsync(conn, tx, subject, row, ct);
        await tx.CommitAsync(ct);
        return Outcome<SupplierDetail>.Ok(detail);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<Outcome<SupplierDetail>> ChangeAsync(
        Subject subject, string number, string action, string? correlationId, Func<NpgsqlConnection, NpgsqlTransaction, SupplierRow, Task<(string, string?)[]?>> apply, CancellationToken ct, bool notFoundWhenNull = false)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (row, refusal) = await OpenForChangeAsync<SupplierDetail>(conn, tx, subject, number, PlmAction.Modify, action, correlationId, ct);
        if (row is null) return refusal!;
        if (row.Status == SupplierStatuses.Obsolete) return Outcome<SupplierDetail>.Conflict("An obsolete supplier cannot be edited.");
        var details = await apply(conn, tx, row);
        if (details is null && notFoundWhenNull) return Outcome<SupplierDetail>.NotFound();
        await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Success, number, correlationId, details ?? []), ct);
        var detail = await BuildDetailAsync(conn, tx, subject, row, ct);
        await tx.CommitAsync(ct);
        return Outcome<SupplierDetail>.Ok(detail);
    }

    /// <summary>Loads and locks the supplier and checks Read (hidden = not found, denial audited) and the action (denial audited, forbidden).</summary>
    private async Task<(SupplierRow?, Outcome<T>?)> OpenForChangeAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string number, PlmAction action, string auditAction, string? corr, CancellationToken ct)
    {
        var row = await SourcingStore.LoadSupplierAsync(conn, tx, number, forUpdate: true, ct);
        if (row is null) return (null, Outcome<T>.NotFound());
        var read = AccessPolicy.Evaluate(subject, row.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, auditAction, AuditOutcome.Denied, number, corr, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return (null, Outcome<T>.NotFound());
        }
        var decision = AccessPolicy.Evaluate(subject, row.Markings, action);
        if (!decision.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, auditAction, AuditOutcome.Denied, number, corr, ("reasons", string.Join(",", decision.Reasons))), ct);
            await tx.CommitAsync(ct);
            return (null, Outcome<T>.Forbidden(decision.Reasons));
        }
        return (row, null);
    }

    private async Task<Outcome<T>> DeniedAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string action, string? number, string? corr, IReadOnlyCollection<string> reasons, CancellationToken ct)
    {
        await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, corr, ("reasons", string.Join(",", reasons))), ct);
        await tx.CommitAsync(ct);
        return Outcome<T>.Forbidden([.. reasons]);
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, string value, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("v", value);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
