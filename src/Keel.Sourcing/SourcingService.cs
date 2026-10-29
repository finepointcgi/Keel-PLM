using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Sourcing;

/// <summary>
/// Manufacturer parts, the approved manufacturer and vendor lists of a part, alternate and replacement relations, where-sourced queries and the sourcing coverage of
/// parts (ADR-0037). Entries and relations carry no markings of their own: they are read through the part's markings (and the supplier's), joined at query time, so
/// a change of a part's markings applies at once. Every call evaluates the access policy and writes its audit event in the same transaction; denials are audited.
/// </summary>
public sealed class SourcingService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock)
{
    private const string PartType = "part";
    private const string UniqueViolation = "23505";

    public static readonly SortMap EntrySortKeys = new(
        ("part", ["pm.number"]), ("entry", ["e.number"]), ("list", ["e.kind"]), ("status", ["e.status"]), ("supplier", ["lower(s.name)"]), ("approval", ["e.approval_state"]));

    private static AuditEntry PartEntry(Subject s, string action, AuditOutcome outcome, string partNumber, string? corr, params (string, string?)[] details) =>
        SourcingStore.Entry(s, action, outcome, PartType, partNumber, corr, details);

    // ---------------------------------------------------------------- the Sourcing tab of a part

    public async Task<Outcome<PartSourcing>> GetPartSourcingAsync(Subject subject, string partNumber, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var part = await SourcingStore.LoadPartAsync(conn, tx, partNumber, false, ct);
        if (part is null) return Outcome<PartSourcing>.NotFound();
        var read = AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, PartEntry(subject, "sourcing.part_read", AuditOutcome.Denied, partNumber, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<PartSourcing>.NotFound();
        }
        var today = SourcingStore.Today(clock);
        var sFilter = AccessPolicy.BuildReadFilter(subject, "s");
        var msFilter = AccessPolicy.BuildReadFilter(subject, "ms");
        var entries = new List<EntryRow>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {EntryStore.Select} FROM {EntryStore.FromFiltered(msFilter.Sql)} WHERE e.part_master_id = @p AND e.approval_state <> 'Withdrawn' AND {sFilter.Sql} ORDER BY e.kind, e.number", conn, tx))
        {
            foreach (var (k, v) in sFilter.Parameters.Concat(msFilter.Parameters)) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("p", part.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) entries.Add(EntryStore.Read(r));
        }
        var expired = await ExpiredSuppliersAsync(conn, tx, entries.Select(e => e.SupplierId).Distinct().ToArray(), today, ct);
        var bucket = SourcingRules.Classify(
            entries.Where(e => e.ApprovalState == EntryApprovalStates.Active).Select(e => new SourcingRules.EntryFacts(e.Status, SourcingRules.IsEffective(e.ApprovalState, e.From, e.To, today), e.SupplierStatus, expired.Contains(e.SupplierId), e.SupplierForeign, e.RevisionScope)),
            part.Markings, part.LatestRevision);
        var relations = await RelationsAsync(conn, tx, subject, part, ct);
        var views = entries.Select(e => e.ToView(today)).ToList();
        var banner = Markings.AggregateBanner(new[] { part.Markings }.Concat(entries.Select(e => e.SupplierMarkings)).Concat(relations.Select(x => x.Markings)));
        await audit.AppendAsync(tx, PartEntry(subject, "sourcing.part_read", AuditOutcome.Success, partNumber, correlationId, ("entries", views.Count.ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return Outcome<PartSourcing>.Ok(new PartSourcing(part.Number, part.Name, part.State, part.Markings.BannerText(), banner, views, relations,
            AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Modify).Permitted, bucket));
    }

    private static async Task<HashSet<Guid>> ExpiredSuppliersAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid[] supplierIds, DateOnly today, CancellationToken ct)
    {
        var set = new HashSet<Guid>();
        if (supplierIds.Length == 0) return set;
        await using var cmd = new NpgsqlCommand("SELECT DISTINCT supplier_id FROM src.supplier_certification WHERE supplier_id = ANY(@s) AND NOT revoked AND expires_on < @today", conn, tx);
        cmd.Parameters.AddWithValue("s", supplierIds);
        cmd.Parameters.AddWithValue("today", today);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetGuid(0));
        return set;
    }

    private static async Task<IReadOnlyList<RelationView>> RelationsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, PartRef part, CancellationToken ct)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "op");
        var list = new List<RelationView>();
        // The other part is shown only when the caller may read it: a hidden part leaves no placeholder here (the relation is simply not listed).
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT x.id, x.relation, (x.part_master_id = @p), op.number, op.name, x.note, {MarkingColumns.Select("op")}
            FROM src.part_relation x JOIN plm.part_master op ON op.id = CASE WHEN x.part_master_id = @p THEN x.related_part_id ELSE x.part_master_id END
            WHERE (x.part_master_id = @p OR x.related_part_id = @p) AND {filter.Sql} ORDER BY x.relation, op.number
            """, conn, tx);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("p", part.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var relation = r.GetString(1);
            var outgoing = r.GetBoolean(2);
            var direction = relation == PartRelations.Alternate ? "Alternate" : outgoing ? "This part replaces" : "Replaced by";
            var markings = MarkingColumns.Read(r, 6);
            list.Add(new RelationView(r.GetGuid(0), relation, r.GetString(3), r.GetString(4), direction, SourcingStore.Str(r, 5), markings.BannerText(), markings));
        }
        return list;
    }

    // ---------------------------------------------------------------- create / change entries

    public async Task<Outcome<EntryView>> CreateEntryAsync(Subject subject, CreateEntryCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        var kind = cmd.Kind?.Trim().ToLowerInvariant() ?? "";
        var mpn = SourcingRules.Clean(cmd.Mpn);
        var currency = SourcingRules.Clean(cmd.Currency)?.ToUpperInvariant();
        var scope = SourcingRules.Clean(cmd.RevisionScope)?.ToUpperInvariant();
        var errors = SourcingRules.ValidateEntry(kind, cmd.Status, cmd.EffectiveFrom, cmd.EffectiveTo, SourcingRules.Clean(cmd.VendorPartNumber), cmd.LeadTimeDays, cmd.Moq, currency, cmd.PriceBreaks, SourcingRules.Clean(cmd.Note), scope).ToList();
        if (kind == SourceKinds.Aml && mpn is null) errors.Add("A manufacturer list entry needs the manufacturer part number.");
        if (mpn is { Length: > 60 }) errors.Add("The manufacturer part number is at most 60 characters.");
        if (kind == SourceKinds.Avl && (mpn is null) != string.IsNullOrWhiteSpace(cmd.ManufacturerNumber)) errors.Add("Give both the manufacturer and its part number, or neither.");
        if (errors.Count > 0) return Outcome<EntryView>.Invalid(errors);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var part = await SourcingStore.LoadPartAsync(conn, tx, cmd.PartNumber.Trim(), false, ct);
        if (part is null) return Outcome<EntryView>.NotFound();
        var read = AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Read);
        if (!read.Permitted) return await DenyHiddenAsync<EntryView>(conn, tx, subject, "sourcing.entry_create", part.Number, correlationId, read.Reasons, ct);
        var modify = AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Modify);
        if (!modify.Permitted) return await DenyAsync<EntryView>(conn, tx, subject, "sourcing.entry_create", part.Number, correlationId, modify.Reasons, ct);

        var supplier = await SourcingStore.LoadSupplierAsync(conn, tx, cmd.SupplierNumber.Trim(), false, ct);
        if (supplier is null || !AccessPolicy.Evaluate(subject, supplier.Markings, PlmAction.Read).Permitted) return Outcome<EntryView>.Invalid([$"There is no supplier '{cmd.SupplierNumber.Trim()}'."]);
        var requiredRole = kind == SourceKinds.Aml ? SupplierRoles.Manufacturer : SupplierRoles.Vendor;
        if (!supplier.Roles.Contains(requiredRole)) return Outcome<EntryView>.Invalid([$"{supplier.Number} is not a {requiredRole}."]);
        if (supplier.Status == SupplierStatuses.Obsolete) return Outcome<EntryView>.Invalid([$"{supplier.Number} is obsolete."]);

        SupplierRow manufacturer = supplier;
        if (kind == SourceKinds.Avl && mpn is not null)
        {
            var m = await SourcingStore.LoadSupplierAsync(conn, tx, cmd.ManufacturerNumber!.Trim(), false, ct);
            if (m is null || !AccessPolicy.Evaluate(subject, m.Markings, PlmAction.Read).Permitted || !m.Roles.Contains(SupplierRoles.Manufacturer))
                return Outcome<EntryView>.Invalid([$"There is no manufacturer '{cmd.ManufacturerNumber!.Trim()}'."]);
            manufacturer = m;
        }
        // The export-control rule (service level; the database has the same rule as a trigger).
        foreach (var s in new[] { supplier, manufacturer }.DistinctBy(x => x.Id))
            if (SourcingRules.CheckSupplierForPart(s.IsForeign, part.Markings) is { } code)
            {
                await audit.AppendAsync(tx, PartEntry(subject, "sourcing.entry_create", AuditOutcome.Denied, part.Number, correlationId, ("reasons", code), ("supplier", s.Number)), ct);
                await tx.CommitAsync(ct);
                return Outcome<EntryView>.Forbidden([code]);
            }
        if (scope is not null)
        {
            await using var rev = new NpgsqlCommand("SELECT 1 FROM plm.part_revision WHERE master_id = @m AND revision = @r", conn, tx);
            rev.Parameters.AddWithValue("m", part.Id);
            rev.Parameters.AddWithValue("r", scope);
            if (await rev.ExecuteScalarAsync(ct) is null) return Outcome<EntryView>.Invalid([$"Part {part.Number} has no revision {scope}."]);
        }

        Guid? mpnId = null;
        if (mpn is not null)
        {
            await using var find = new NpgsqlCommand("SELECT id FROM src.mpn WHERE manufacturer_id = @m AND upper(mpn) = upper(@mpn)", conn, tx);
            find.Parameters.AddWithValue("m", manufacturer.Id);
            find.Parameters.AddWithValue("mpn", mpn);
            mpnId = (Guid?)await find.ExecuteScalarAsync(ct);
            if (mpnId is null)
            {
                mpnId = Guid.CreateVersion7();
                await using var ins = new NpgsqlCommand("INSERT INTO src.mpn (id, manufacturer_id, mpn, created_at, created_by) VALUES (@id, @m, @mpn, @now, @by)", conn, tx);
                ins.Parameters.AddWithValue("id", mpnId.Value);
                ins.Parameters.AddWithValue("m", manufacturer.Id);
                ins.Parameters.AddWithValue("mpn", mpn);
                ins.Parameters.AddWithValue("now", clock.GetUtcNow());
                ins.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
                await ins.ExecuteNonQueryAsync(ct);
            }
        }

        var number = SourcingStore.NextNumber("SRC-", await SourcingStore.NextValAsync(conn, tx, "src.entry_seq", ct));
        var id = Guid.CreateVersion7();
        try
        {
            await using var ins = new NpgsqlCommand(
                """
                INSERT INTO src.source_entry (id, number, kind, part_master_id, revision_scope, supplier_id, mpn_id, status, approval_state, effective_from, effective_to, vendor_part_number, lead_time_days, moq, currency, price_breaks, note, created_at, created_by)
                VALUES (@id, @number, @kind, @part, @scope, @supplier, @mpn, @status, 'Draft', @from, @to, @vpn, @lead, @moq, @currency, @breaks::jsonb, @note, @now, @by)
                """, conn, tx);
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("number", number);
            ins.Parameters.AddWithValue("kind", kind);
            ins.Parameters.AddWithValue("part", part.Id);
            ins.Parameters.AddWithValue("scope", (object?)scope ?? DBNull.Value);
            ins.Parameters.AddWithValue("supplier", supplier.Id);
            ins.Parameters.AddWithValue("mpn", (object?)mpnId ?? DBNull.Value);
            ins.Parameters.AddWithValue("status", cmd.Status);
            ins.Parameters.AddWithValue("from", (object?)cmd.EffectiveFrom ?? DBNull.Value);
            ins.Parameters.AddWithValue("to", (object?)cmd.EffectiveTo ?? DBNull.Value);
            ins.Parameters.AddWithValue("vpn", (object?)SourcingRules.Clean(cmd.VendorPartNumber) ?? DBNull.Value);
            ins.Parameters.AddWithValue("lead", (object?)cmd.LeadTimeDays ?? DBNull.Value);
            ins.Parameters.AddWithValue("moq", (object?)cmd.Moq ?? DBNull.Value);
            ins.Parameters.AddWithValue("currency", (object?)currency ?? DBNull.Value);
            ins.Parameters.AddWithValue("breaks", SourcingStore.PriceBreaksJson(cmd.PriceBreaks));
            ins.Parameters.AddWithValue("note", (object?)SourcingRules.Clean(cmd.Note) ?? DBNull.Value);
            ins.Parameters.AddWithValue("now", clock.GetUtcNow());
            ins.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await ins.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException e) when (e.SqlState == UniqueViolation)
        {
            return Outcome<EntryView>.Conflict("The part already has an open entry for this supplier (and manufacturer part) on this list. Edit or withdraw it instead.");
        }
        await audit.AppendAsync(tx, PartEntry(subject, "sourcing.entry_create", AuditOutcome.Success, part.Number, correlationId,
            ("entry", number), ("list", kind), ("supplier", supplier.Number), ("status", cmd.Status)), ct);
        var row = (await EntryStore.LoadByIdAsync(conn, tx, id, false, ct))!;
        await tx.CommitAsync(ct);
        return Outcome<EntryView>.Ok(row.ToView(SourcingStore.Today(clock)));
    }

    public async Task<Outcome<EntryView>> UpdateEntryAsync(Subject subject, string entryNumber, UpdateEntryCommand cmd, int expectedRevision, string? correlationId, CancellationToken ct = default)
    {
        var currency = SourcingRules.Clean(cmd.Currency)?.ToUpperInvariant();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (e, refusal) = await OpenEntryAsync<EntryView>(conn, tx, subject, entryNumber, "sourcing.entry_update", correlationId, ct);
        if (e is null) return refusal!;
        var errors = SourcingRules.ValidateEntry(e.Kind, cmd.Status, cmd.EffectiveFrom, cmd.EffectiveTo, SourcingRules.Clean(cmd.VendorPartNumber), cmd.LeadTimeDays, cmd.Moq, currency, cmd.PriceBreaks, SourcingRules.Clean(cmd.Note), e.RevisionScope);
        if (errors.Count > 0) return Outcome<EntryView>.Invalid(errors);
        if (e.ApprovalState != EntryApprovalStates.Draft) return Outcome<EntryView>.Conflict("Only a draft entry can be edited. Withdraw this one and add a new entry to change it.");
        if (e.Revision != expectedRevision) return Outcome<EntryView>.Conflict("The entry changed since you opened it. Reload and try again.");
        await using (var upd = new NpgsqlCommand(
            """
            UPDATE src.source_entry SET status = @status, effective_from = @from, effective_to = @to, vendor_part_number = @vpn, lead_time_days = @lead, moq = @moq, currency = @currency,
                   price_breaks = @breaks::jsonb, note = @note, revision = revision + 1, updated_at = @now, updated_by = @by WHERE id = @id
            """, conn, tx))
        {
            upd.Parameters.AddWithValue("status", cmd.Status);
            upd.Parameters.AddWithValue("from", (object?)cmd.EffectiveFrom ?? DBNull.Value);
            upd.Parameters.AddWithValue("to", (object?)cmd.EffectiveTo ?? DBNull.Value);
            upd.Parameters.AddWithValue("vpn", (object?)SourcingRules.Clean(cmd.VendorPartNumber) ?? DBNull.Value);
            upd.Parameters.AddWithValue("lead", (object?)cmd.LeadTimeDays ?? DBNull.Value);
            upd.Parameters.AddWithValue("moq", (object?)cmd.Moq ?? DBNull.Value);
            upd.Parameters.AddWithValue("currency", (object?)currency ?? DBNull.Value);
            upd.Parameters.AddWithValue("breaks", SourcingStore.PriceBreaksJson(cmd.PriceBreaks));
            upd.Parameters.AddWithValue("note", (object?)SourcingRules.Clean(cmd.Note) ?? DBNull.Value);
            upd.Parameters.AddWithValue("now", clock.GetUtcNow());
            upd.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            upd.Parameters.AddWithValue("id", e.Id);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, PartEntry(subject, "sourcing.entry_update", AuditOutcome.Success, e.PartNumber, correlationId, ("entry", e.Number), ("status", cmd.Status)), ct);
        var row = (await EntryStore.LoadByIdAsync(conn, tx, e.Id, false, ct))!;
        await tx.CommitAsync(ct);
        return Outcome<EntryView>.Ok(row.ToView(SourcingStore.Today(clock)));
    }

    /// <summary>Takes a draft or an active entry off the list (it stays in the history). An entry under review must have its review cancelled first.</summary>
    public async Task<Outcome<EntryView>> WithdrawEntryAsync(Subject subject, string entryNumber, string reason, string? correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000) return Outcome<EntryView>.Invalid(["A reason is required (at most 1000 characters)."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (e, refusal) = await OpenEntryAsync<EntryView>(conn, tx, subject, entryNumber, "sourcing.entry_withdraw", correlationId, ct);
        if (e is null) return refusal!;
        if (e.ApprovalState == EntryApprovalStates.UnderReview) return Outcome<EntryView>.Conflict("The entry is under review; cancel the review first.");
        if (e.ApprovalState == EntryApprovalStates.Withdrawn) return Outcome<EntryView>.Conflict("The entry is withdrawn already.");
        await EntryStore.SetStateAsync(conn, tx, e.Id, EntryApprovalStates.Withdrawn, subject.Username ?? subject.Id, clock.GetUtcNow(), reason.Trim(), ct);
        await audit.AppendAsync(tx, PartEntry(subject, "sourcing.entry_withdraw", AuditOutcome.Success, e.PartNumber, correlationId, ("entry", e.Number), ("from", e.ApprovalState), ("reason", reason.Trim())), ct);
        var row = (await EntryStore.LoadByIdAsync(conn, tx, e.Id, false, ct))!;
        await tx.CommitAsync(ct);
        return Outcome<EntryView>.Ok(row.ToView(SourcingStore.Today(clock)));
    }

    /// <summary>Sets the end date of an active entry (the entry stops counting after that day).</summary>
    public async Task<Outcome<EntryView>> EndEntryAsync(Subject subject, string entryNumber, DateOnly endsOn, string reason, string? correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000) return Outcome<EntryView>.Invalid(["A reason is required (at most 1000 characters)."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (e, refusal) = await OpenEntryAsync<EntryView>(conn, tx, subject, entryNumber, "sourcing.entry_end", correlationId, ct);
        if (e is null) return refusal!;
        if (e.ApprovalState != EntryApprovalStates.Active) return Outcome<EntryView>.Conflict("Only an active entry can be ended.");
        if (e.From is { } from && endsOn < from) return Outcome<EntryView>.Invalid(["The end date cannot be before the start date."]);
        await using (var upd = new NpgsqlCommand("UPDATE src.source_entry SET effective_to = @to, reason = @reason, revision = revision + 1, updated_at = @now, updated_by = @by WHERE id = @id", conn, tx))
        {
            upd.Parameters.AddWithValue("to", endsOn);
            upd.Parameters.AddWithValue("reason", reason.Trim());
            upd.Parameters.AddWithValue("now", clock.GetUtcNow());
            upd.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            upd.Parameters.AddWithValue("id", e.Id);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, PartEntry(subject, "sourcing.entry_end", AuditOutcome.Success, e.PartNumber, correlationId, ("entry", e.Number), ("endsOn", endsOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("reason", reason.Trim())), ct);
        var row = (await EntryStore.LoadByIdAsync(conn, tx, e.Id, false, ct))!;
        await tx.CommitAsync(ct);
        return Outcome<EntryView>.Ok(row.ToView(SourcingStore.Today(clock)));
    }

    public async Task<Outcome<EntryView>> GetEntryAsync(Subject subject, string entryNumber, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var e = await EntryStore.LoadAsync(conn, tx, entryNumber, false, ct);
        if (e is null) return Outcome<EntryView>.NotFound();
        var readPart = AccessPolicy.Evaluate(subject, e.PartMarkings, PlmAction.Read);
        var readSupplier = AccessPolicy.Evaluate(subject, e.SupplierMarkings, PlmAction.Read);
        if (!readPart.Permitted || !readSupplier.Permitted)
        {
            await audit.AppendAsync(tx, PartEntry(subject, "sourcing.entry_read", AuditOutcome.Denied, e.PartNumber, correlationId, ("reasons", string.Join(",", readPart.Reasons.Concat(readSupplier.Reasons)))), ct);
            await tx.CommitAsync(ct);
            return Outcome<EntryView>.NotFound();
        }
        await audit.AppendAsync(tx, PartEntry(subject, "sourcing.entry_read", AuditOutcome.Success, e.PartNumber, correlationId, ("entry", e.Number)), ct);
        await tx.CommitAsync(ct);
        return Outcome<EntryView>.Ok(e.ToView(SourcingStore.Today(clock)));
    }

    private async Task<(EntryRow?, Outcome<T>?)> OpenEntryAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string entryNumber, string action, string? corr, CancellationToken ct)
    {
        var e = await EntryStore.LoadAsync(conn, tx, entryNumber, true, ct);
        if (e is null) return (null, Outcome<T>.NotFound());
        var read = AccessPolicy.Evaluate(subject, e.PartMarkings, PlmAction.Read);
        var readSupplier = AccessPolicy.Evaluate(subject, e.SupplierMarkings, PlmAction.Read);
        if (!read.Permitted || !readSupplier.Permitted) return (null, await DenyHiddenAsync<T>(conn, tx, subject, action, e.PartNumber, corr, [.. read.Reasons, .. readSupplier.Reasons], ct));
        var modify = AccessPolicy.Evaluate(subject, e.PartMarkings, PlmAction.Modify);
        if (!modify.Permitted) return (null, await DenyAsync<T>(conn, tx, subject, action, e.PartNumber, corr, modify.Reasons, ct));
        return (e, null);
    }

    // ---------------------------------------------------------------- relations

    public async Task<Outcome<PartSourcing>> AddRelationAsync(Subject subject, string partNumber, string relatedNumber, string relation, string? note, string? correlationId, CancellationToken ct = default)
    {
        if (relation is not (PartRelations.Alternate or PartRelations.Replaces)) return Outcome<PartSourcing>.Invalid(["The relation is alternate or replaces."]);
        if (note is { Length: > 500 }) return Outcome<PartSourcing>.Invalid(["The note is at most 500 characters."]);
        if (string.Equals(partNumber.Trim(), relatedNumber.Trim(), StringComparison.Ordinal)) return Outcome<PartSourcing>.Invalid(["A part cannot be related to itself."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (part, other, refusal) = await OpenPairAsync<PartSourcing>(conn, tx, subject, partNumber.Trim(), relatedNumber.Trim(), "sourcing.relation_add", correlationId, ct);
        if (part is null) return refusal!;
        // Reverse pair of a replacement would be a cycle of two.
        await using (var rev = new NpgsqlCommand("SELECT 1 FROM src.part_relation WHERE relation = 'replaces' AND part_master_id = @b AND related_part_id = @a", conn, tx))
        {
            rev.Parameters.AddWithValue("a", part.Id);
            rev.Parameters.AddWithValue("b", other!.Id);
            if (relation == PartRelations.Replaces && await rev.ExecuteScalarAsync(ct) is not null) return Outcome<PartSourcing>.Conflict($"{other.Number} already replaces {part.Number}.");
        }
        try
        {
            await using var ins = new NpgsqlCommand("INSERT INTO src.part_relation (id, part_master_id, related_part_id, relation, note, created_at, created_by) VALUES (@id, @a, @b, @r, @n, @now, @by)", conn, tx);
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("a", part.Id);
            ins.Parameters.AddWithValue("b", other!.Id);
            ins.Parameters.AddWithValue("r", relation);
            ins.Parameters.AddWithValue("n", (object?)SourcingRules.Clean(note) ?? DBNull.Value);
            ins.Parameters.AddWithValue("now", clock.GetUtcNow());
            ins.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await ins.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException e) when (e.SqlState == UniqueViolation)
        {
            return Outcome<PartSourcing>.Conflict("These parts are related this way already.");
        }
        await audit.AppendAsync(tx, PartEntry(subject, "sourcing.relation_add", AuditOutcome.Success, part.Number, correlationId, ("related", other!.Number), ("relation", relation)), ct);
        await tx.CommitAsync(ct);
        return await GetPartSourcingAsync(subject, part.Number, correlationId, ct);
    }

    public async Task<Outcome<PartSourcing>> RemoveRelationAsync(Subject subject, string partNumber, Guid relationId, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid? a = null, b = null;
        await using (var find = new NpgsqlCommand("SELECT part_master_id, related_part_id FROM src.part_relation WHERE id = @id", conn, tx))
        {
            find.Parameters.AddWithValue("id", relationId);
            await using var r = await find.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { a = r.GetGuid(0); b = r.GetGuid(1); }
        }
        if (a is null) return Outcome<PartSourcing>.NotFound();
        var partA = await SourcingStore.LoadPartByIdAsync(conn, tx, a.Value, ct);
        var partB = await SourcingStore.LoadPartByIdAsync(conn, tx, b!.Value, ct);
        if (partA is null || partB is null || (partA.Number != partNumber && partB.Number != partNumber)) return Outcome<PartSourcing>.NotFound();
        foreach (var p in new[] { partA, partB })
        {
            var read = AccessPolicy.Evaluate(subject, p.Markings, PlmAction.Read);
            if (!read.Permitted) return await DenyHiddenAsync<PartSourcing>(conn, tx, subject, "sourcing.relation_remove", partNumber, correlationId, read.Reasons, ct);
            var modify = AccessPolicy.Evaluate(subject, p.Markings, PlmAction.Modify);
            if (!modify.Permitted) return await DenyAsync<PartSourcing>(conn, tx, subject, "sourcing.relation_remove", partNumber, correlationId, modify.Reasons, ct);
        }
        await using (var del = new NpgsqlCommand("DELETE FROM src.part_relation WHERE id = @id", conn, tx))
        {
            del.Parameters.AddWithValue("id", relationId);
            await del.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, PartEntry(subject, "sourcing.relation_remove", AuditOutcome.Success, partNumber, correlationId), ct);
        await tx.CommitAsync(ct);
        return await GetPartSourcingAsync(subject, partNumber, correlationId, ct);
    }

    private async Task<(PartRef?, PartRef?, Outcome<T>?)> OpenPairAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string a, string b, string action, string? corr, CancellationToken ct)
    {
        var pa = await SourcingStore.LoadPartAsync(conn, tx, a, false, ct);
        var pb = await SourcingStore.LoadPartAsync(conn, tx, b, false, ct);
        if (pa is null || pb is null) return (null, null, Outcome<T>.NotFound());
        foreach (var p in new[] { pa, pb })
        {
            var read = AccessPolicy.Evaluate(subject, p.Markings, PlmAction.Read);
            if (!read.Permitted) return (null, null, await DenyHiddenAsync<T>(conn, tx, subject, action, a, corr, read.Reasons, ct));
            var modify = AccessPolicy.Evaluate(subject, p.Markings, PlmAction.Modify);
            if (!modify.Permitted) return (null, null, await DenyAsync<T>(conn, tx, subject, action, a, corr, modify.Reasons, ct));
        }
        return (pa, pb, null);
    }

    // ---------------------------------------------------------------- where sourced

    /// <summary>
    /// Which readable parts are sourced from a supplier or through a manufacturer part number. A hidden part never appears and is never counted; a supplier the
    /// caller may not read answers with nothing, like an unknown one.
    /// </summary>
    public async Task<Outcome<WhereSourcedResult>> WhereSourcedAsync(Subject subject, WhereSourcedQuery query, string? correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.SupplierNumber) && string.IsNullOrWhiteSpace(query.Mpn)) return Outcome<WhereSourcedResult>.Invalid(["Give a supplier or a manufacturer part number."]);
        var limit = Math.Clamp(query.Limit, 1, 200);
        var offset = Math.Max(0, query.Offset);
        var pmFilter = AccessPolicy.BuildReadFilter(subject, "pm");
        var sFilter = AccessPolicy.BuildReadFilter(subject, "s");
        var msFilter = AccessPolicy.BuildReadFilter(subject, "ms");
        var where = new List<string> { pmFilter.Sql, sFilter.Sql, "e.approval_state <> 'Withdrawn'" };
        var parameters = new List<KeyValuePair<string, object>>(pmFilter.Parameters.Concat(sFilter.Parameters).Concat(msFilter.Parameters));
        if (!string.IsNullOrWhiteSpace(query.SupplierNumber)) { where.Add("(s.number = @sup OR ms.number = @sup)"); parameters.Add(new("sup", query.SupplierNumber.Trim())); }
        if (!string.IsNullOrWhiteSpace(query.Mpn)) { where.Add("mp.mpn ILIKE @mpn"); parameters.Add(new("mpn", SourcingStore.Like(query.Mpn))); }
        var from = EntryStore.FromFiltered(msFilter.Sql);
        var whereSql = string.Join(" AND ", where);
        var today = SourcingStore.Today(clock);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int total;
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM {from} WHERE {whereSql}", conn, tx))
        {
            foreach (var (k, v) in parameters) count.Parameters.AddWithValue(k, v);
            total = Convert.ToInt32(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        var rows = new List<WhereSourcedRow>();
        await using (var cmd = new NpgsqlCommand($"SELECT {EntryStore.Select} FROM {from} WHERE {whereSql} ORDER BY pm.number, e.number LIMIT @limit OFFSET @offset", conn, tx))
        {
            foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var e = EntryStore.Read(r);
                rows.Add(new WhereSourcedRow(e.PartNumber, e.PartName, "", e.Number, e.Kind, e.Status, e.ApprovalState, e.Mpn, e.SupplierNumber, e.SupplierName,
                    SourcingRules.IsEffective(e.ApprovalState, e.From, e.To, today), e.PartMarkings.BannerText(), e.PartMarkings));
            }
        }
        var banner = Markings.AggregateBanner(rows.Select(r => r.Markings).Distinct());
        await audit.AppendAsync(tx, SourcingStore.Entry(subject, "sourcing.where_sourced", AuditOutcome.Success, "supplier", query.SupplierNumber?.Trim(), correlationId,
            ("mpn", string.IsNullOrWhiteSpace(query.Mpn) ? null : "given"), ("results", rows.Count.ToString(CultureInfo.InvariantCulture)), ("banner", banner)), ct);
        await tx.CommitAsync(ct);
        return Outcome<WhereSourcedResult>.Ok(new WhereSourcedResult(rows, total, banner));
    }

    // ---------------------------------------------------------------- coverage

    /// <summary>
    /// The sourcing standing of parts, as the caller can see it. Parts the caller may not read are absent from the answer (never reported as uncovered), and entries or
    /// suppliers the caller may not read do not count, so the answer is a function of what the caller can read only. <paramref name="revisions"/> narrows revision-scoped entries.
    /// </summary>
    public async Task<Outcome<IReadOnlyDictionary<string, PartCoverage>>> CoverageAsync(
        Subject subject, IReadOnlyDictionary<string, string?> revisions, string? correlationId, CancellationToken ct = default)
    {
        var numbers = revisions.Keys.ToArray();
        var pmFilter = AccessPolicy.BuildReadFilter(subject, "pm");
        var sFilter = AccessPolicy.BuildReadFilter(subject, "s");
        var today = SourcingStore.Today(clock);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var parts = new Dictionary<string, (string Source, Markings Markings)>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand($"SELECT pm.number, pm.source, {MarkingColumns.Select("pm")} FROM plm.part_master pm WHERE pm.number = ANY(@n) AND {pmFilter.Sql}", conn, tx))
        {
            foreach (var (k, v) in pmFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("n", numbers);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) parts[r.GetString(0)] = (r.GetString(1), MarkingColumns.Read(r, 2));
        }
        var facts = new Dictionary<string, List<SourcingRules.EntryFacts>>(StringComparer.Ordinal);
        var preferred = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT pm.number, e.status, e.effective_from, e.effective_to, e.revision_scope, s.status, s.is_foreign,
                   EXISTS (SELECT 1 FROM src.supplier_certification c WHERE c.supplier_id = s.id AND NOT c.revoked AND c.expires_on < @today)
            FROM src.source_entry e JOIN plm.part_master pm ON pm.id = e.part_master_id JOIN src.supplier s ON s.id = e.supplier_id
            WHERE pm.number = ANY(@n) AND {pmFilter.Sql} AND {sFilter.Sql} AND e.approval_state = 'Active'
            """, conn, tx))
        {
            foreach (var (k, v) in pmFilter.Parameters.Concat(sFilter.Parameters)) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("n", numbers);
            cmd.Parameters.AddWithValue("today", today);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var number = r.GetString(0);
                var from = r.IsDBNull(2) ? (DateOnly?)null : r.GetFieldValue<DateOnly>(2);
                var to = r.IsDBNull(3) ? (DateOnly?)null : r.GetFieldValue<DateOnly>(3);
                var f = new SourcingRules.EntryFacts(r.GetString(1), SourcingRules.IsEffective(EntryApprovalStates.Active, from, to, today), r.GetString(5), r.GetBoolean(7), r.GetBoolean(6), r.IsDBNull(4) ? null : r.GetString(4));
                if (!facts.TryGetValue(number, out var list)) facts[number] = list = [];
                list.Add(f);
                if (f.Status == EntryStatuses.Preferred && f.Effective) preferred.Add(number);
            }
        }
        var result = new Dictionary<string, PartCoverage>(StringComparer.Ordinal);
        foreach (var (number, (source, markings)) in parts)
        {
            var list = facts.GetValueOrDefault(number) ?? [];
            var bucket = SourcingRules.Classify(list, markings, revisions[number]);
            result[number] = new PartCoverage(number, source, bucket, list.Count(f => f.Effective), preferred.Contains(number));
        }
        await audit.AppendAsync(tx, SourcingStore.Entry(subject, "sourcing.coverage", AuditOutcome.Success, "part", null, correlationId, ("parts", result.Count.ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return Outcome<IReadOnlyDictionary<string, PartCoverage>>.Ok(result);
    }

    // ---------------------------------------------------------------- denial helpers

    private async Task<Outcome<T>> DenyHiddenAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string action, string partNumber, string? corr, IReadOnlyCollection<string> reasons, CancellationToken ct)
    {
        await audit.AppendAsync(tx, PartEntry(subject, action, AuditOutcome.Denied, partNumber, corr, ("reasons", string.Join(",", reasons))), ct);
        await tx.CommitAsync(ct);
        return Outcome<T>.NotFound();
    }

    private async Task<Outcome<T>> DenyAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string action, string partNumber, string? corr, IReadOnlyCollection<string> reasons, CancellationToken ct)
    {
        await audit.AppendAsync(tx, PartEntry(subject, action, AuditOutcome.Denied, partNumber, corr, ("reasons", string.Join(",", reasons))), ct);
        await tx.CommitAsync(ct);
        return Outcome<T>.Forbidden([.. reasons]);
    }
}
