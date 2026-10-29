using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Compliance;

/// <summary>
/// Compliance declarations of parts (ADR-0040): what a part (or a manufacturer part of a supplier, kept as an opaque reference) is made of, which exemptions it claims, how
/// long the declaration holds and whether it was verified. A declaration has no markings of its own: it is readable only through a readable part, and every call evaluates
/// the access policy on the part's markings (ADR-0004). Business change and audit event commit in one transaction (ADR-0005), denials included. Audit details and
/// notifications carry numbers, codes and counts only (pointers, ADR-0030): never a name, a note, a reason or a reference text.
/// </summary>
public sealed class DeclarationService(
    NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, INotificationSink? notifications = null, IEnumerable<IObjectAccess>? objectAccess = null)
{
    private readonly IObjectAccess? _documents = objectAccess?.FirstOrDefault(o => o.Kind == AwarenessKinds.Document);

    public static readonly SortMap SortKeys = new(
        ("number", ["d.number"]), ("part", ["m.number"]), ("status", ["d.status"]), ("expires", ["d.expires_on"]), ("declared", ["d.declared_on"]), ("source", ["d.source"]));

    private static AuditEntry PartEntry(Subject s, string action, AuditOutcome outcome, string? partNumber, string? corr, params (string, string?)[] details) =>
        ComplianceStore.Entry(s, action, outcome, ComplianceStore.PartObjectType, partNumber, corr, details);

    private static string Utc(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Loads the part and decides: not found, or readable (a denial is audited and looks like "not found"), and then whether the action is allowed (a denial is audited and says why).</summary>
    private async Task<(PartRef? Part, Outcome<T>? Failure)> AuthorizeAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string partNumber, PlmAction action, string auditAction, string? corr, CancellationToken ct)
    {
        var part = await ComplianceStore.LoadPartAsync(conn, tx, partNumber, false, ct);
        if (part is null) return (null, Outcome<T>.NotFound());
        var read = AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, PartEntry(subject, auditAction, AuditOutcome.Denied, partNumber, corr, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return (null, Outcome<T>.NotFound());
        }
        if (action != PlmAction.Read)
        {
            var decision = AccessPolicy.Evaluate(subject, part.Markings, action);
            if (!decision.Permitted)
            {
                await audit.AppendAsync(tx, PartEntry(subject, auditAction, AuditOutcome.Denied, partNumber, corr, ("reasons", string.Join(",", decision.Reasons))), ct);
                await tx.CommitAsync(ct);
                return (null, Outcome<T>.Forbidden(decision.Reasons));
            }
        }
        return (part, null);
    }

    // ---------------------------------------------------------------- create and update

    public async Task<Outcome<DeclarationDetail>> CreateAsync(Subject subject, CreateDeclarationCommand cmd, string? correlationId, CancellationToken ct = default)
    {
        var input = Clean(cmd.Input);
        var errors = ComplianceRules.ValidateDeclaration(input, ComplianceStore.Today(clock));
        if (errors.Count > 0) return Outcome<DeclarationDetail>.Invalid(errors);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (part, failure) = await AuthorizeAsync<DeclarationDetail>(conn, tx, subject, cmd.PartNumber, PlmAction.Modify, "compliance.declaration_create", correlationId, ct);
        if (failure is not null) return failure;
        var resolved = await ResolveLinesAsync<DeclarationDetail>(conn, tx, input, ct);
        if (resolved.Failure is not null) return resolved.Failure;

        var number = ComplianceStore.NextNumber("CD-", await ComplianceStore.NextValAsync(conn, tx, "cmp.declaration_seq", ct));
        var id = Guid.CreateVersion7();
        var now = clock.GetUtcNow();
        await using (var ins = new NpgsqlCommand(
            """
            INSERT INTO cmp.declaration (id, number, part_master_id, revision_scope, source, supplier_ref, mpn_ref, item_mass_mg, declared_on, expires_on, status, note, created_at, created_by)
            VALUES (@id, @n, @p, @scope, @src, @sup, @mpn, @mass, @declared, @expires, 'declared', @note, @now, @by)
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("n", number);
            ins.Parameters.AddWithValue("p", part!.Id);
            ins.Parameters.AddWithValue("scope", (object?)input.RevisionScope ?? DBNull.Value);
            ins.Parameters.AddWithValue("src", input.Source);
            ins.Parameters.AddWithValue("sup", (object?)input.SupplierRef ?? DBNull.Value);
            ins.Parameters.AddWithValue("mpn", (object?)input.MpnRef ?? DBNull.Value);
            ins.Parameters.AddWithValue("mass", (object?)input.ItemMassMg ?? DBNull.Value);
            ins.Parameters.AddWithValue("declared", input.DeclaredOn);
            ins.Parameters.AddWithValue("expires", input.ExpiresOn);
            ins.Parameters.AddWithValue("note", (object?)input.Note ?? DBNull.Value);
            ins.Parameters.AddWithValue("now", now);
            ins.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await InsertLinesAsync(conn, tx, id, input.Lines, resolved, ct);
        var row = (await ComplianceStore.LoadDeclarationByIdAsync(conn, tx, id, false, ct))!;
        var ctx = await ComplianceEvaluator.ContextAsync(conn, tx, clock, null, null, ct);
        var (status, _, _) = await ComplianceEvaluator.RefreshAsync(conn, tx, row, ctx, ct);
        await audit.AppendAsync(tx, PartEntry(subject, "compliance.declaration_create", AuditOutcome.Success, part.Number, correlationId,
            ("declaration", number), ("source", input.Source), ("lines", input.Lines.Count.ToString(CultureInfo.InvariantCulture)), ("status", status)), ct);
        if (notifications is not null)
        {
            await notifications.AutoWatchAsync(conn, tx, subject.Id, AwarenessKinds.Part, part.Number, ct);
            if (input.Source == DeclarationSources.Supplier)
                await notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.ComplianceSupplierDeclaration, AwarenessKinds.Part, part.Number, part.Markings, subject.Id), ct);
            if (status == DeclarationStatuses.NonCompliant)
                await notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.ComplianceNonCompliant, AwarenessKinds.Part, part.Number, part.Markings, subject.Id), ct);
        }
        var detail = await BuildDetailAsync(conn, tx, subject, row with { Status = status }, ctx, ct);
        await tx.CommitAsync(ct);
        return Outcome<DeclarationDetail>.Ok(detail with { Documents = [] });
    }

    public async Task<Outcome<DeclarationDetail>> UpdateAsync(Subject subject, string number, DeclarationInput input0, int revision, string? correlationId, CancellationToken ct = default)
    {
        var input = Clean(input0);
        var errors = ComplianceRules.ValidateDeclaration(input, ComplianceStore.Today(clock));
        if (errors.Count > 0) return Outcome<DeclarationDetail>.Invalid(errors);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await ComplianceStore.LoadDeclarationAsync(conn, tx, number, true, ct);
        if (row is null) return Outcome<DeclarationDetail>.NotFound();
        var (part, failure) = await AuthorizeAsync<DeclarationDetail>(conn, tx, subject, row.PartNumber, PlmAction.Modify, "compliance.declaration_update", correlationId, ct);
        if (failure is not null) return failure;
        if (row.Revision != revision) return Outcome<DeclarationDetail>.Conflict("The declaration was changed by someone else; reload and try again.");
        if (row.Status == DeclarationStatuses.Withdrawn) return Outcome<DeclarationDetail>.Conflict("A withdrawn declaration cannot be changed; make a new one.");
        var resolved = await ResolveLinesAsync<DeclarationDetail>(conn, tx, input, ct);
        if (resolved.Failure is not null) return resolved.Failure;
        await using (var del = new NpgsqlCommand("DELETE FROM cmp.declaration_line WHERE declaration_id = @d", conn, tx))
        {
            del.Parameters.AddWithValue("d", row.Id);
            await del.ExecuteNonQueryAsync(ct);
        }
        await InsertLinesAsync(conn, tx, row.Id, input.Lines, resolved, ct);
        // A changed declaration has to be verified again.
        await using (var upd = new NpgsqlCommand(
            """
            UPDATE cmp.declaration SET revision_scope = @scope, source = @src, supplier_ref = @sup, mpn_ref = @mpn, item_mass_mg = @mass, declared_on = @declared, expires_on = @expires, note = @note,
                   verified_at = NULL, verified_by = NULL, revision = revision + 1, updated_at = @now, updated_by = @by WHERE id = @id
            """, conn, tx))
        {
            upd.Parameters.AddWithValue("scope", (object?)input.RevisionScope ?? DBNull.Value);
            upd.Parameters.AddWithValue("src", input.Source);
            upd.Parameters.AddWithValue("sup", (object?)input.SupplierRef ?? DBNull.Value);
            upd.Parameters.AddWithValue("mpn", (object?)input.MpnRef ?? DBNull.Value);
            upd.Parameters.AddWithValue("mass", (object?)input.ItemMassMg ?? DBNull.Value);
            upd.Parameters.AddWithValue("declared", input.DeclaredOn);
            upd.Parameters.AddWithValue("expires", input.ExpiresOn);
            upd.Parameters.AddWithValue("note", (object?)input.Note ?? DBNull.Value);
            upd.Parameters.AddWithValue("now", clock.GetUtcNow());
            upd.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            upd.Parameters.AddWithValue("id", row.Id);
            await upd.ExecuteNonQueryAsync(ct);
        }
        var reloaded = (await ComplianceStore.LoadDeclarationByIdAsync(conn, tx, row.Id, false, ct))!;
        var ctx = await ComplianceEvaluator.ContextAsync(conn, tx, clock, null, null, ct);
        var (status, changed, previous) = await ComplianceEvaluator.RefreshAsync(conn, tx, reloaded, ctx, ct);
        await audit.AppendAsync(tx, PartEntry(subject, "compliance.declaration_update", AuditOutcome.Success, part!.Number, correlationId,
            ("declaration", number), ("lines", input.Lines.Count.ToString(CultureInfo.InvariantCulture)), ("status", status)), ct);
        if (changed && status == DeclarationStatuses.NonCompliant && notifications is not null)
            await notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.ComplianceNonCompliant, AwarenessKinds.Part, part.Number, part.Markings, subject.Id), ct);
        _ = previous;
        var detail = await BuildDetailAsync(conn, tx, subject, reloaded with { Status = status }, ctx, ct);
        await tx.CommitAsync(ct);
        return Outcome<DeclarationDetail>.Ok(await WithDocumentsAsync(subject, detail, ct));
    }

    private static DeclarationInput Clean(DeclarationInput i) => i with
    {
        RevisionScope = ComplianceRules.Clean(i.RevisionScope)?.ToUpperInvariant(),
        SupplierRef = ComplianceRules.Clean(i.SupplierRef),
        MpnRef = ComplianceRules.Clean(i.MpnRef),
        Note = ComplianceRules.Clean(i.Note),
        Lines = [.. i.Lines.Select(l => l with { SubstanceCas = ComplianceRules.Clean(l.SubstanceCas), MaterialCode = ComplianceRules.Clean(l.MaterialCode)?.ToUpperInvariant(), ExemptionCode = ComplianceRules.Clean(l.ExemptionCode) })],
    };

    private sealed record ResolvedLines<T>(Dictionary<string, Guid> Substances, Dictionary<string, Guid> Materials, Outcome<T>? Failure);

    private static async Task<ResolvedLines<T>> ResolveLinesAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, DeclarationInput input, CancellationToken ct)
    {
        var cas = input.Lines.Where(l => l.SubstanceCas is not null).Select(l => l.SubstanceCas!).Distinct(StringComparer.Ordinal).ToArray();
        var codes = input.Lines.Where(l => l.MaterialCode is not null).Select(l => l.MaterialCode!).Distinct(StringComparer.Ordinal).ToArray();
        var substances = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var materials = new Dictionary<string, Guid>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("SELECT cas_number, id FROM cmp.substance WHERE cas_number = ANY(@c)", conn, tx))
        {
            cmd.Parameters.AddWithValue("c", cas);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) substances[r.GetString(0)] = r.GetGuid(1);
        }
        await using (var cmd = new NpgsqlCommand("SELECT code, id FROM cmp.material WHERE code = ANY(@c) AND active", conn, tx))
        {
            cmd.Parameters.AddWithValue("c", codes);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) materials[r.GetString(0)] = r.GetGuid(1);
        }
        var errors = cas.Where(c => !substances.ContainsKey(c)).Select(c => $"There is no substance {c} in the substance master.")
            .Concat(codes.Where(c => !materials.ContainsKey(c)).Select(c => $"There is no active material {c}.")).ToList();
        return new ResolvedLines<T>(substances, materials, errors.Count > 0 ? Outcome<T>.Invalid(errors) : null);
    }

    private static async Task InsertLinesAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, Guid declarationId, IReadOnlyList<DeclarationLineInput> lines, ResolvedLines<T> resolved, CancellationToken ct)
    {
        var ordinal = 0;
        foreach (var l in lines)
        {
            ordinal++;
            await using var ins = new NpgsqlCommand(
                "INSERT INTO cmp.declaration_line (id, declaration_id, ordinal, substance_id, material_id, percent, mass_mg, exemption_code, conflict_free) VALUES (@id, @d, @o, @s, @m, @p, @mass, @x, @cf)", conn, tx);
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("d", declarationId);
            ins.Parameters.AddWithValue("o", ordinal);
            ins.Parameters.AddWithValue("s", l.SubstanceCas is null ? DBNull.Value : resolved.Substances[l.SubstanceCas]);
            ins.Parameters.AddWithValue("m", l.MaterialCode is null ? DBNull.Value : resolved.Materials[l.MaterialCode]);
            ins.Parameters.AddWithValue("p", (object?)l.Percent ?? DBNull.Value);
            ins.Parameters.AddWithValue("mass", (object?)l.MassMg ?? DBNull.Value);
            ins.Parameters.AddWithValue("x", (object?)l.ExemptionCode ?? DBNull.Value);
            ins.Parameters.AddWithValue("cf", (object?)l.ConflictFree ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    // ---------------------------------------------------------------- verify, withdraw

    /// <summary>Verification is a signature-level act (approver role): the declaration's content was checked against the supporting document. Changing the declaration clears it.</summary>
    public async Task<Outcome<DeclarationDetail>> VerifyAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await ComplianceStore.LoadDeclarationAsync(conn, tx, number, true, ct);
        if (row is null) return Outcome<DeclarationDetail>.NotFound();
        var (part, failure) = await AuthorizeAsync<DeclarationDetail>(conn, tx, subject, row.PartNumber, PlmAction.Sign, "compliance.declaration_verify", correlationId, ct);
        if (failure is not null) return failure;
        if (row.Status == DeclarationStatuses.Withdrawn) return Outcome<DeclarationDetail>.Conflict("A withdrawn declaration cannot be verified.");
        if (row.ExpiresOn < ComplianceStore.Today(clock)) return Outcome<DeclarationDetail>.Conflict("The declaration has expired; a new declaration is needed.");
        if (row.VerifiedAt is not null) return Outcome<DeclarationDetail>.Conflict("The declaration is verified already.");
        await using (var upd = new NpgsqlCommand("UPDATE cmp.declaration SET verified_at = @now, verified_by = @by, revision = revision + 1, updated_at = @now, updated_by = @by WHERE id = @id", conn, tx))
        {
            upd.Parameters.AddWithValue("now", clock.GetUtcNow());
            upd.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            upd.Parameters.AddWithValue("id", row.Id);
            await upd.ExecuteNonQueryAsync(ct);
        }
        var reloaded = (await ComplianceStore.LoadDeclarationByIdAsync(conn, tx, row.Id, false, ct))!;
        var ctx = await ComplianceEvaluator.ContextAsync(conn, tx, clock, null, null, ct);
        var (status, _, _) = await ComplianceEvaluator.RefreshAsync(conn, tx, reloaded, ctx, ct);
        await audit.AppendAsync(tx, PartEntry(subject, "compliance.declaration_verify", AuditOutcome.Success, part!.Number, correlationId, ("declaration", number), ("status", status)), ct);
        var detail = await BuildDetailAsync(conn, tx, subject, reloaded with { Status = status }, ctx, ct);
        await tx.CommitAsync(ct);
        return Outcome<DeclarationDetail>.Ok(await WithDocumentsAsync(subject, detail, ct));
    }

    public async Task<Outcome<DeclarationDetail>> WithdrawAsync(Subject subject, string number, string reason, string? correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500) return Outcome<DeclarationDetail>.Invalid(["A reason of up to 500 characters is required."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await ComplianceStore.LoadDeclarationAsync(conn, tx, number, true, ct);
        if (row is null) return Outcome<DeclarationDetail>.NotFound();
        var (part, failure) = await AuthorizeAsync<DeclarationDetail>(conn, tx, subject, row.PartNumber, PlmAction.Modify, "compliance.declaration_withdraw", correlationId, ct);
        if (failure is not null) return failure;
        if (row.Status == DeclarationStatuses.Withdrawn) return Outcome<DeclarationDetail>.Conflict("The declaration is withdrawn already.");
        await using (var upd = new NpgsqlCommand("UPDATE cmp.declaration SET status = 'withdrawn', withdrawn_reason = @r, revision = revision + 1, updated_at = @now, updated_by = @by WHERE id = @id", conn, tx))
        {
            upd.Parameters.AddWithValue("r", reason.Trim());
            upd.Parameters.AddWithValue("now", clock.GetUtcNow());
            upd.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            upd.Parameters.AddWithValue("id", row.Id);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, PartEntry(subject, "compliance.declaration_withdraw", AuditOutcome.Success, part!.Number, correlationId, ("declaration", number), ("from", row.Status)), ct);
        var reloaded = (await ComplianceStore.LoadDeclarationByIdAsync(conn, tx, row.Id, false, ct))!;
        var ctx = await ComplianceEvaluator.ContextAsync(conn, tx, clock, null, null, ct);
        var detail = await BuildDetailAsync(conn, tx, subject, reloaded, ctx, ct);
        await tx.CommitAsync(ct);
        return Outcome<DeclarationDetail>.Ok(await WithDocumentsAsync(subject, detail, ct));
    }

    // ---------------------------------------------------------------- documents

    public async Task<Outcome<DeclarationDetail>> LinkDocumentAsync(Subject subject, string number, string documentNumber, string? correlationId, CancellationToken ct = default)
    {
        var doc = ComplianceRules.Clean(documentNumber);
        if (doc is null || doc.Length is < 2 or > 40) return Outcome<DeclarationDetail>.Invalid(["Give the number of the document."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await ComplianceStore.LoadDeclarationAsync(conn, tx, number, true, ct);
        if (row is null) return Outcome<DeclarationDetail>.NotFound();
        var (part, failure) = await AuthorizeAsync<DeclarationDetail>(conn, tx, subject, row.PartNumber, PlmAction.Modify, "compliance.document_link", correlationId, ct);
        if (failure is not null) return failure;
        if (row.Status == DeclarationStatuses.Withdrawn) return Outcome<DeclarationDetail>.Conflict("A withdrawn declaration cannot be changed.");
        // A document the caller cannot read looks exactly like one that does not exist.
        var info = _documents is null ? null : await _documents.FindAsync(doc, ct);
        if (info is null || !AccessPolicy.Evaluate(subject, info.Markings, PlmAction.Read).Permitted)
        {
            await audit.AppendAsync(tx, PartEntry(subject, "compliance.document_link", AuditOutcome.Denied, part!.Number, correlationId, ("declaration", number), ("reasons", "document.not_readable")), ct);
            await tx.CommitAsync(ct);
            return Outcome<DeclarationDetail>.Invalid([$"There is no document {doc} that you may read."]);
        }
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO cmp.declaration_document (id, declaration_id, document_number, linked_at, linked_by) VALUES (@id, @d, @n, @now, @by) ON CONFLICT DO NOTHING", conn, tx))
        {
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("d", row.Id);
            ins.Parameters.AddWithValue("n", info.Number);
            ins.Parameters.AddWithValue("now", clock.GetUtcNow());
            ins.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, PartEntry(subject, "compliance.document_link", AuditOutcome.Success, part!.Number, correlationId, ("declaration", number), ("document", info.Number)), ct);
        var ctx = await ComplianceEvaluator.ContextAsync(conn, tx, clock, null, null, ct);
        var detail = await BuildDetailAsync(conn, tx, subject, row, ctx, ct);
        await tx.CommitAsync(ct);
        return Outcome<DeclarationDetail>.Ok(await WithDocumentsAsync(subject, detail, ct));
    }

    public async Task<Outcome<DeclarationDetail>> UnlinkDocumentAsync(Subject subject, string number, string documentNumber, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await ComplianceStore.LoadDeclarationAsync(conn, tx, number, true, ct);
        if (row is null) return Outcome<DeclarationDetail>.NotFound();
        var (part, failure) = await AuthorizeAsync<DeclarationDetail>(conn, tx, subject, row.PartNumber, PlmAction.Modify, "compliance.document_unlink", correlationId, ct);
        if (failure is not null) return failure;
        await using (var del = new NpgsqlCommand("DELETE FROM cmp.declaration_document WHERE declaration_id = @d AND document_number = @n", conn, tx))
        {
            del.Parameters.AddWithValue("d", row.Id);
            del.Parameters.AddWithValue("n", documentNumber);
            if (await del.ExecuteNonQueryAsync(ct) == 0) return Outcome<DeclarationDetail>.NotFound();
        }
        await audit.AppendAsync(tx, PartEntry(subject, "compliance.document_unlink", AuditOutcome.Success, part!.Number, correlationId, ("declaration", number), ("document", documentNumber)), ct);
        var ctx = await ComplianceEvaluator.ContextAsync(conn, tx, clock, null, null, ct);
        var detail = await BuildDetailAsync(conn, tx, subject, row, ctx, ct);
        await tx.CommitAsync(ct);
        return Outcome<DeclarationDetail>.Ok(await WithDocumentsAsync(subject, detail, ct));
    }

    // ---------------------------------------------------------------- reads

    public async Task<Outcome<DeclarationDetail>> GetAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await ComplianceStore.LoadDeclarationAsync(conn, tx, number, false, ct);
        if (row is null) return Outcome<DeclarationDetail>.NotFound();
        var read = AccessPolicy.Evaluate(subject, row.PartMarkings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, PartEntry(subject, "compliance.declaration_read", AuditOutcome.Denied, row.PartNumber, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<DeclarationDetail>.NotFound();
        }
        var ctx = await ComplianceEvaluator.ContextAsync(conn, tx, clock, null, null, ct);
        var detail = await BuildDetailAsync(conn, tx, subject, row, ctx, ct);
        await audit.AppendAsync(tx, PartEntry(subject, "compliance.declaration_read", AuditOutcome.Success, row.PartNumber, correlationId, ("declaration", number)), ct);
        await tx.CommitAsync(ct);
        return Outcome<DeclarationDetail>.Ok(await WithDocumentsAsync(subject, detail, ct));
    }

    private async Task<DeclarationDetail> WithDocumentsAsync(Subject subject, DeclarationDetail detail, CancellationToken ct)
    {
        var links = new List<DocumentLinkView>();
        foreach (var d in detail.Documents)
        {
            var info = _documents is null || d.DocumentNumber is null ? null : await _documents.FindAsync(d.DocumentNumber, ct);
            var readable = info is not null && AccessPolicy.Evaluate(subject, info.Markings, PlmAction.Read).Permitted;
            links.Add(readable ? new DocumentLinkView(d.DocumentNumber, true) : new DocumentLinkView(null, false));
        }
        return detail with { Documents = links };
    }

    private static async Task<DeclarationDetail> BuildDetailAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, DeclarationRow row, EvalContext ctx, CancellationToken ct)
    {
        _ = subject;
        var lines = (await ComplianceStore.LoadLinesAsync(conn, tx, [row.Id], ct))[row.Id];
        var expanded = await ComplianceEvaluator.ExpandAsync(conn, tx, lines, row.ItemMassMg, ct);
        var evaluations = ComplianceEvaluator.EvaluateAll(ctx, expanded);
        var views = ctx.Versions.Select(v => new EvaluationView(v.Code, v.Name, v.Version, evaluations[v.Code].Result, evaluations[v.Code].Findings.Count)).ToList();
        var docs = new List<DocumentLinkView>();
        await using (var cmd = new NpgsqlCommand("SELECT document_number FROM cmp.declaration_document WHERE declaration_id = @d ORDER BY linked_at, document_number", conn, tx))
        {
            cmd.Parameters.AddWithValue("d", row.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) docs.Add(new DocumentLinkView(r.GetString(0), true));
        }
        return new DeclarationDetail(
            Summarize(row, views, ctx.Today), row.ItemMassMg, row.Note, row.VerifiedAt, row.VerifiedBy, row.WithdrawnReason, row.Revision,
            [.. lines.Select(l => new DeclarationLineView(l.Ordinal, l.SubstanceNumber, l.Cas, l.SubstanceName, l.MaterialCode, l.MaterialName, l.Percent, l.MassMg, l.ExemptionCode, l.ConflictFree))],
            docs, ComplianceEvaluator.ToViews(ctx, row.Number, evaluations), row.PartMarkings.BannerText());
    }

    private static DeclarationSummary Summarize(DeclarationRow row, IReadOnlyList<EvaluationView> evaluations, DateOnly today) =>
        new(row.Id, row.Number, row.PartNumber, row.RevisionScope, row.Source, row.SupplierRef, row.MpnRef, row.DeclaredOn, row.ExpiresOn, row.Status, row.ExpiresOn.DayNumber - today.DayNumber, evaluations);

    /// <summary>A part's compliance: its declarations and what it stands at for each regulation in force (the worst of its current declarations).</summary>
    public async Task<Outcome<PartComplianceView>> GetForPartAsync(Subject subject, string partNumber, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (part, failure) = await AuthorizeAsync<PartComplianceView>(conn, tx, subject, partNumber, PlmAction.Read, "compliance.part_read", correlationId, ct);
        if (failure is not null) return failure;
        var ctx = await ComplianceEvaluator.ContextAsync(conn, tx, clock, null, null, ct);
        var rows = new List<DeclarationRow>();
        await using (var cmd = new NpgsqlCommand($"SELECT {ComplianceStore.DeclarationSelect()} FROM cmp.declaration d JOIN plm.part_master m ON m.id = d.part_master_id WHERE d.part_master_id = @p ORDER BY d.declared_on DESC, d.number DESC", conn, tx))
        {
            cmd.Parameters.AddWithValue("p", part!.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(ComplianceStore.ReadDeclaration(r));
        }
        var lines = await ComplianceStore.LoadLinesAsync(conn, tx, [.. rows.Select(r => r.Id)], ct);
        var compositions = await ComplianceStore.LoadMaterialCompositionsAsync(conn, tx, [.. lines.Values.SelectMany(l => l).Where(l => l.MaterialId is not null).Select(l => l.MaterialId!.Value).Distinct()], ct);
        var evaluated = new Dictionary<Guid, IReadOnlyDictionary<string, ListEvaluation>>();
        foreach (var r in rows) evaluated[r.Id] = ComplianceEvaluator.EvaluateAll(ctx, ComplianceEvaluator.Expand(lines[r.Id], r.ItemMassMg, compositions));

        var summaries = rows.Select(r => Summarize(r, [.. ctx.Versions.Select(v => new EvaluationView(v.Code, v.Name, v.Version, evaluated[r.Id][v.Code].Result, evaluated[r.Id][v.Code].Findings.Count))], ctx.Today)).ToList();
        var current = rows.Where(r => r.Status != DeclarationStatuses.Withdrawn)
            .Select(r => new CurrentDeclaration(r.Number, r.RevisionScope, r.ExpiresOn, r.Status, evaluated[r.Id].ToDictionary(kv => kv.Key, kv => kv.Value.Result, StringComparer.Ordinal))).ToList();
        var regs = new List<PartRegulationStanding>();
        foreach (var v in ctx.Versions)
        {
            var standing = ComplianceRules.PartStanding(current, v.Code, part.LatestRevision, ctx.Today);
            var covering = rows.Where(r => r.Status != DeclarationStatuses.Withdrawn && r.ExpiresOn >= ctx.Today && ComplianceRules.Covers(r.RevisionScope, part.LatestRevision)).ToList();
            var findings = covering.SelectMany(r => ComplianceEvaluator.ToViews(ctx, r.Number, evaluated[r.Id]).Where(f => f.ListCode == v.Code)).ToList();
            regs.Add(new PartRegulationStanding(v.Code, v.Name, v.Kind, v.Version, standing, [.. covering.Select(r => r.Number)], findings));
        }
        await audit.AppendAsync(tx, PartEntry(subject, "compliance.part_read", AuditOutcome.Success, part.Number, correlationId, ("declarations", rows.Count.ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return Outcome<PartComplianceView>.Ok(new PartComplianceView(part.Number, part.LatestRevision, part.Markings.BannerText(), summaries, regs,
            AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Modify).Permitted, AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Sign).Permitted));
    }

    public async Task<Outcome<DeclarationResults>> SearchAsync(Subject subject, DeclarationSearch q, string? correlationId, CancellationToken ct = default)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "m");
        var where = new List<string> { filter.Sql };
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand { Connection = conn };
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        if (q.PartNumber is not null) { where.Add("m.number = @part"); cmd.Parameters.AddWithValue("part", q.PartNumber.Trim()); }
        if (q.Status is not null) { where.Add("d.status = @status"); cmd.Parameters.AddWithValue("status", q.Status); }
        if (q.Source is not null) { where.Add("d.source = @source"); cmd.Parameters.AddWithValue("source", q.Source); }
        if (q.SupplierRef is not null) { where.Add("d.supplier_ref = @sup"); cmd.Parameters.AddWithValue("sup", q.SupplierRef.Trim()); }
        var order = SortKeys.OrderBy(q.Sort, "d.expires_on, d.number", "d.number");
        cmd.CommandText = $"SELECT {ComplianceStore.DeclarationSelect()}, count(*) OVER () FROM cmp.declaration d JOIN plm.part_master m ON m.id = d.part_master_id WHERE {string.Join(" AND ", where)} ORDER BY {order} LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("limit", Math.Clamp(q.Limit, 1, 200));
        cmd.Parameters.AddWithValue("offset", Math.Max(0, q.Offset));
        var rows = new List<DeclarationRow>();
        var total = 0;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            var countIndex = 4 + MarkingColumns.Count + 13;
            while (await r.ReadAsync(ct)) { rows.Add(ComplianceStore.ReadDeclaration(r)); total = (int)r.GetInt64(countIndex); }
        }
        var today = ComplianceStore.Today(clock);
        var items = rows.Select(r => Summarize(r, [], today)).ToList();
        var banner = Markings.AggregateBanner(rows.Select(r => r.PartMarkings).ToList());
        await using var tx = await conn.BeginTransactionAsync(ct);
        await audit.AppendAsync(tx, ComplianceStore.Entry(subject, "compliance.declaration_search", AuditOutcome.Success, "declaration", null, correlationId, ("results", items.Count.ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return Outcome<DeclarationResults>.Ok(new DeclarationResults(items, total, banner));
    }

    /// <summary>Counts for the dashboard: declarations by status and those expiring within 60 days, over the parts the caller may read. Nothing is counted for a part the caller cannot read.</summary>
    public async Task<ComplianceCounts> CountsAsync(Subject subject, CancellationToken ct = default)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "m");
        var today = ComplianceStore.Today(clock);
        await using var cmd = dataSource.CreateCommand(
            $"""
            SELECT count(*) FILTER (WHERE d.status = 'declared'), count(*) FILTER (WHERE d.status = 'verified'), count(*) FILTER (WHERE d.status = 'expired'),
                   count(*) FILTER (WHERE d.status = 'non_compliant'),
                   count(*) FILTER (WHERE d.status IN ('declared', 'verified', 'non_compliant') AND d.expires_on BETWEEN @today AND @soon), count(DISTINCT d.part_master_id)
            FROM cmp.declaration d JOIN plm.part_master m ON m.id = d.part_master_id WHERE {filter.Sql} AND d.status <> 'withdrawn'
            """);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("today", today);
        cmd.Parameters.AddWithValue("soon", today.AddDays(ComplianceRules.ExpiryWarnDays));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new ComplianceCounts((int)r.GetInt64(0), (int)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3), (int)r.GetInt64(4), (int)r.GetInt64(5));
    }
}
