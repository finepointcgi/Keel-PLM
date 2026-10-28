using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Core;
using Keel.Documents.Vault;
using Keel.Security;
using Npgsql;

namespace Keel.Documents;

/// <summary>
/// Document commands and queries. Same rules as parts: every call evaluates the access policy (ADR-0004) and writes an
/// audit event in the same transaction as its change (ADR-0005), including denials; per-document mutations serialize on
/// the master row. File bytes go to the content-addressed vault (ADR-0015) before the metadata transaction, so no row
/// lock is held while a large file streams.
/// </summary>
public sealed partial class DocumentService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, ObjectVault vault)
{
    public const string ObjectType = "document";
    public const string DefaultLifecycle = "document-default";
    public const int MaxSearchLimit = 200;
    private const string ReleasedState = "Released";
    private const string UnderReviewState = "UnderReview";

    // ---------------------------------------------------------------- create

    public async Task<Outcome<DocumentDetail>> CreateAsync(Subject subject, CreateDocumentCommand cmd, FileUpload? primary, string? correlationId, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var name = cmd.Name?.Trim() ?? "";
        if (name.Length is 0 or > 200) errors.Add("Name is required (at most 200 characters).");
        // 2.2: document types are administrator data (cfg.ref_item).
        await using (var typeConn = await dataSource.OpenConnectionAsync(ct))
        {
            var types = await ReferenceChecks.ActiveCodesAsync(typeConn, null, RefLists.DocumentType, ct);
            if (!types.Contains(cmd.DocumentType)) errors.Add($"Document type must be one of: {string.Join(", ", types)}.");
        }
        if (cmd.Number is not null && !NumberPattern().IsMatch(cmd.Number)) errors.Add("Explicit document numbers may contain only A-Z, 0-9, '-', '.', '_' (max 40).");
        if ((cmd.Description?.Length ?? 0) > 4000) errors.Add("Description must be at most 4000 characters.");
        string? mediaType = null;
        if (primary is not null) errors.AddRange(ValidateUpload(primary, out mediaType));
        if (errors.Count > 0) return Outcome<DocumentDetail>.Invalid(errors);

        var decision = AccessPolicy.Evaluate(subject, cmd.Markings, PlmAction.Modify);
        if (cmd.Number is not null && !AccessPolicy.HasRoleFor(subject, PlmAction.Administer))
            decision = decision with { Permitted = false, Reasons = decision.Reasons.Add("number.explicit_requires_admin") };
        if (!decision.Permitted)
        {
            await audit.AppendAsync(Entry(subject, "document.create", AuditOutcome.Denied, null, correlationId,
                ("reasons", string.Join(",", decision.Reasons)), ("markings", cmd.Markings.BannerText())), ct);
            return Outcome<DocumentDetail>.Forbidden(decision.Reasons);
        }

        // Authorized: stream the file into the vault first (no transaction open), then record it.
        StoredContent? stored = null;
        if (primary is not null)
        {
            try { stored = await vault.PutAsync(primary.Content, ct); }
            catch (UploadTooLargeException e) { return Outcome<DocumentDetail>.Invalid([e.Message]); }
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var family = await ReferenceChecks.LifecycleFamilyAsync(conn, tx, RefLists.DocumentType, cmd.DocumentType, DefaultLifecycle, ct);
        var lifecycle = await Lifecycle.LoadAsync(conn, tx, await Lifecycle.CurrentIdAsync(conn, tx, family, ct), ct) ?? throw new InvalidOperationException("Default document lifecycle is missing.");
        var number = cmd.Number;
        if (number is null)
        {
            number = await Numbering.NextAsync(conn, tx, "document", ct); // 2.2: prefix and width are configuration
        }
        else
        {
            await using var exists = new NpgsqlCommand("SELECT 1 FROM plm.document_master WHERE number = @n", conn, tx);
            exists.Parameters.AddWithValue("n", number);
            if (await exists.ExecuteScalarAsync(ct) is not null) return Outcome<DocumentDetail>.Conflict($"Document number {number} already exists.");
        }

        var now = clock.GetUtcNow();
        var who = DisplayId(subject);
        var masterId = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        var iterationId = Guid.CreateVersion7();
        await using (var ins = new NpgsqlCommand(
            $"""
            INSERT INTO plm.document_master (id, number, name, document_type, lifecycle_id, {MarkingColumns.InsertColumns}, created_at, created_by)
            VALUES (@id, @number, @name, @type, @lc, {MarkingColumns.InsertValues}, @at, @by);
            INSERT INTO plm.document_revision (id, master_id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by)
            VALUES (@rid, @id, @rev, @state, true, 1, @at, @by, @at, @by);
            INSERT INTO plm.document_iteration (id, revision_id, iteration, description, comment, created_at, created_by)
            VALUES (@iid, @rid, 1, @desc, 'Created', @at, @by);
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("id", masterId);
            ins.Parameters.AddWithValue("number", number);
            ins.Parameters.AddWithValue("name", name);
            ins.Parameters.AddWithValue("type", cmd.DocumentType);
            ins.Parameters.AddWithValue("lc", lifecycle.Id);
            foreach (var (k, v) in MarkingColumns.Parameters(cmd.Markings)) ins.Parameters.AddWithValue(k, v);
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", who);
            ins.Parameters.AddWithValue("rid", revisionId);
            ins.Parameters.AddWithValue("rev", RevisionScheme.First);
            ins.Parameters.AddWithValue("state", lifecycle.Initial.State);
            ins.Parameters.AddWithValue("iid", iterationId);
            ins.Parameters.AddWithValue("desc", cmd.Description?.Trim() ?? "");
            await ins.ExecuteNonQueryAsync(ct);
        }
        if (stored is not null)
            await InsertFileAsync(conn, tx, "plm.document_file", "iteration_id", iterationId, FileRoles.Primary, primary!.FileName, mediaType!, stored, now, who, ct);
        await audit.AppendAsync(tx, Entry(subject, "document.create", AuditOutcome.Success, number, correlationId,
            ("revision", "A.1"), ("markings", cmd.Markings.BannerText()), ("name", name), ("type", cmd.DocumentType),
            ("file", primary?.FileName), ("sha256", stored?.Sha256), ("size", stored?.SizeBytes.ToString(CultureInfo.InvariantCulture))), ct);
        var detail = await LoadDetailAsync(conn, tx, subject, number, null, ct);
        await tx.CommitAsync(ct);
        return Outcome<DocumentDetail>.Ok(detail!);
    }

    // ---------------------------------------------------------------- read

    public async Task<Outcome<DocumentDetail>> GetAsync(Subject subject, string number, string? revision, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await LoadHeadAsync(conn, tx, number, forUpdate: false, ct);
        if (head is null) return Outcome<DocumentDetail>.NotFound();
        var decision = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!decision.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "document.read", AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", decision.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<DocumentDetail>.NotFound();
        }
        var detail = await LoadDetailAsync(conn, tx, subject, number, revision, ct);
        if (detail is null) return Outcome<DocumentDetail>.NotFound();
        await audit.AppendAsync(tx, Entry(subject, "document.read", AuditOutcome.Success, number, correlationId, ("revision", detail.Iteration.Label)), ct);
        await tx.CommitAsync(ct);
        return Outcome<DocumentDetail>.Ok(detail);
    }

    public async Task<DocumentSearchResult> SearchAsync(Subject subject, string? query, string? documentType, string? state, int limit, int offset, string? correlationId, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxSearchLimit);
        offset = Math.Max(0, offset);
        var q = (query ?? "").Trim();
        var like = "%" + EscapeLike(q) + "%";
        var filter = AccessPolicy.BuildReadFilter(subject, "m");

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var items = new List<DocumentSummary>();
        long total = 0;
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT m.id, m.number, m.name, m.document_type, r.revision, r.latest_iteration, r.lifecycle_state, {MarkingColumns.Select("m")},
                   count(*) OVER ()
            FROM plm.document_master m
            JOIN plm.document_revision r ON r.master_id = m.id AND r.is_latest
            WHERE {filter.Sql}
              AND (@q = '' OR m.search @@ websearch_to_tsquery('english', @q) OR m.number ILIKE @like OR m.name ILIKE @like)
              AND (@type::text IS NULL OR m.document_type = @type)
              AND (@state::text IS NULL OR r.lifecycle_state = @state)
            ORDER BY CASE WHEN m.number ILIKE @prefix THEN 0 ELSE 1 END, m.number
            LIMIT @limit OFFSET @offset
            """, conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("q", q);
            cmd.Parameters.AddWithValue("like", like);
            cmd.Parameters.AddWithValue("prefix", EscapeLike(q) + "%");
            cmd.Parameters.AddWithValue("type", (object?)documentType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("state", (object?)state ?? DBNull.Value);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var m = MarkingColumns.Read(r, 7);
                items.Add(new DocumentSummary(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5),
                    r.GetString(6), m.BannerText(), m));
                total = r.GetInt64(7 + MarkingColumns.Count);
            }
        }
        await audit.AppendAsync(tx, Entry(subject, "document.search", AuditOutcome.Success, null, correlationId,
            ("query", q), ("type", documentType), ("state", state), ("offset", offset.ToString(CultureInfo.InvariantCulture)),
            ("returned", items.Count.ToString(CultureInfo.InvariantCulture)), ("numbers", string.Join(",", items.Select(i => i.Number)))), ct);
        await tx.CommitAsync(ct);
        return new DocumentSearchResult(items, total, Markings.AggregateBanner(items.Select(i => i.Markings)));
    }

    // ---------------------------------------------------------------- check-out / working copy / check-in

    public Task<Outcome<DocumentDetail>> CheckOutAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, "document.checkout", correlationId, PlmAction.Modify, async (ctx, conn, tx) =>
        {
            if (ctx.Lifecycle.IsLocked(ctx.State)) return Fail($"Revision {ctx.Revision} is {ctx.StateLabel} and locked; it cannot be checked out.");
            if (ctx.CheckoutUser is not null) return Fail($"Already checked out by {ctx.CheckoutUserName ?? ctx.CheckoutUser}.");
            var now = clock.GetUtcNow();
            await using (var cmd = new NpgsqlCommand(
                """
                INSERT INTO plm.document_checkout (revision_id, user_id, user_name, checked_out_at, description)
                SELECT r.id, @user, @name, @at, i.description
                FROM plm.document_revision r JOIN plm.document_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
                WHERE r.id = @rid
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("user", subject.Id);
                cmd.Parameters.AddWithValue("name", DisplayId(subject));
                cmd.Parameters.AddWithValue("at", now);
                cmd.Parameters.AddWithValue("rid", ctx.RevisionId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            // The working copy starts with the latest iteration's files (metadata only; bytes are content-addressed).
            var files = await ReadFilesAsync(conn, tx,
                "SELECT f.id, f.role, f.file_name, f.media_type, f.size_bytes, f.sha256, f.added_at, f.added_by FROM plm.document_file f WHERE f.iteration_id = @owner",
                ctx.LatestIterationId, ct);
            foreach (var f in files)
                await InsertFileAsync(conn, tx, "plm.document_checkout_file", "revision_id", ctx.RevisionId, f.Role, f.FileName, f.MediaType,
                    new StoredContent(f.Sha256, f.SizeBytes), f.AddedAt, f.AddedBy, ct);
            return Done(("revision", $"{ctx.Revision}.{ctx.LatestIteration}"));
        }, ct);

    public Task<Outcome<DocumentDetail>> UpdateWorkingCopyAsync(Subject subject, string number, string? description, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, "document.edit", correlationId, PlmAction.Modify, async (ctx, conn, tx) =>
        {
            if (ctx.CheckoutUser != subject.Id) return Fail("Check the document out before editing it.");
            if ((description?.Length ?? 0) > 4000) return Invalid(["Description must be at most 4000 characters."]);
            await using var cmd = new NpgsqlCommand("UPDATE plm.document_checkout SET description = @d WHERE revision_id = @rid", conn, tx);
            cmd.Parameters.AddWithValue("d", description?.Trim() ?? "");
            cmd.Parameters.AddWithValue("rid", ctx.RevisionId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Done(("revision", ctx.Revision));
        }, ct);

    /// <summary>
    /// Adds a file to the caller's working copy. A primary file replaces the existing primary; attachments must have
    /// names unique within the working copy (case-insensitive).
    /// </summary>
    public async Task<Outcome<DocumentDetail>> UploadFileAsync(Subject subject, string number, string role, FileUpload upload, string? correlationId, CancellationToken ct = default)
    {
        const string action = "document.file_add";
        var errors = new List<string>();
        if (!FileRoles.All.Contains(role)) errors.Add("Role must be 'primary' or 'attachment'.");
        errors.AddRange(ValidateUpload(upload, out var mediaType));
        if (errors.Count > 0) return Outcome<DocumentDetail>.Invalid(errors);

        // Check access and the check-out before accepting any bytes; repeated under the row lock afterwards.
        var pre = await PrecheckAsync(subject, number, action, correlationId, ct);
        if (pre is not null) return pre;

        StoredContent stored;
        try { stored = await vault.PutAsync(upload.Content, ct); }
        catch (UploadTooLargeException e) { return Outcome<DocumentDetail>.Invalid([e.Message]); }

        return await MutateAsync(subject, number, action, correlationId, PlmAction.Modify, async (ctx, conn, tx) =>
        {
            if (ctx.CheckoutUser != subject.Id) return Fail("Check the document out before adding files.");
            string? replaced = null;
            if (role == FileRoles.Primary)
            {
                await using var del = new NpgsqlCommand("DELETE FROM plm.document_checkout_file WHERE revision_id = @rid AND role = 'primary' RETURNING file_name", conn, tx);
                del.Parameters.AddWithValue("rid", ctx.RevisionId);
                replaced = (string?)await del.ExecuteScalarAsync(ct);
            }
            await using (var dup = new NpgsqlCommand("SELECT 1 FROM plm.document_checkout_file WHERE revision_id = @rid AND lower(file_name) = lower(@n)", conn, tx))
            {
                dup.Parameters.AddWithValue("rid", ctx.RevisionId);
                dup.Parameters.AddWithValue("n", upload.FileName);
                if (await dup.ExecuteScalarAsync(ct) is not null) return Fail($"The working copy already has a file named {upload.FileName}.");
            }
            await InsertFileAsync(conn, tx, "plm.document_checkout_file", "revision_id", ctx.RevisionId, role, upload.FileName, mediaType!, stored,
                clock.GetUtcNow(), DisplayId(subject), ct);
            return Done(("role", role), ("file", upload.FileName), ("mediaType", mediaType), ("sha256", stored.Sha256),
                ("size", stored.SizeBytes.ToString(CultureInfo.InvariantCulture)), ("replaced", replaced));
        }, ct);
    }

    public Task<Outcome<DocumentDetail>> RemoveFileAsync(Subject subject, string number, Guid fileId, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, "document.file_remove", correlationId, PlmAction.Modify, async (ctx, conn, tx) =>
        {
            if (ctx.CheckoutUser != subject.Id) return Fail("Check the document out before removing files.");
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM plm.document_checkout_file WHERE revision_id = @rid AND id = @id RETURNING role, file_name, sha256", conn, tx);
            cmd.Parameters.AddWithValue("rid", ctx.RevisionId);
            cmd.Parameters.AddWithValue("id", fileId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Fail("The working copy has no such file.");
            return Done(("role", r.GetString(0)), ("file", r.GetString(1)), ("sha256", r.GetString(2)));
        }, ct);

    public Task<Outcome<DocumentDetail>> CheckInAsync(Subject subject, string number, string? comment, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, "document.checkin", correlationId, PlmAction.Modify, async (ctx, conn, tx) =>
        {
            if (ctx.CheckoutUser != subject.Id) return Fail("Only the user who checked the document out can check it in.");
            if ((comment?.Length ?? 0) > 1000) return Invalid(["Check-in comment must be at most 1000 characters."]);
            var files = await ReadFilesAsync(conn, tx,
                "SELECT f.id, f.role, f.file_name, f.media_type, f.size_bytes, f.sha256, f.added_at, f.added_by FROM plm.document_checkout_file f WHERE f.revision_id = @owner",
                ctx.RevisionId, ct);
            // ASSUMPTION(A24): once an iteration has a primary file, every later iteration keeps one (it can be replaced, not dropped).
            if (ctx.LatestHasPrimary && files.All(f => f.Role != FileRoles.Primary))
                return Fail("The previous iteration has a primary file; add a primary file before checking in.");

            var next = ctx.LatestIteration + 1;
            var iterationId = Guid.CreateVersion7();
            var now = clock.GetUtcNow();
            await using (var cmd = new NpgsqlCommand(
                """
                INSERT INTO plm.document_iteration (id, revision_id, iteration, description, comment, created_at, created_by)
                SELECT @iid, revision_id, @next, description, @comment, @at, @by FROM plm.document_checkout WHERE revision_id = @rid
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("iid", iterationId);
                cmd.Parameters.AddWithValue("next", next);
                cmd.Parameters.AddWithValue("comment", string.IsNullOrWhiteSpace(comment) ? DBNull.Value : comment.Trim());
                cmd.Parameters.AddWithValue("at", now);
                cmd.Parameters.AddWithValue("by", DisplayId(subject));
                cmd.Parameters.AddWithValue("rid", ctx.RevisionId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            foreach (var f in files)
                await InsertFileAsync(conn, tx, "plm.document_file", "iteration_id", iterationId, f.Role, f.FileName, f.MediaType,
                    new StoredContent(f.Sha256, f.SizeBytes), f.AddedAt, f.AddedBy, ct);
            // Deleting the check-out cascades to its working files.
            await using (var cmd = new NpgsqlCommand(
                """
                UPDATE plm.document_revision SET latest_iteration = @next WHERE id = @rid;
                DELETE FROM plm.document_checkout WHERE revision_id = @rid;
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("next", next);
                cmd.Parameters.AddWithValue("rid", ctx.RevisionId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            return Done(("revision", $"{ctx.Revision}.{next}"), ("comment", comment),
                ("files", string.Join(",", files.Select(f => $"{f.Role}:{f.FileName}:{f.Sha256}"))));
        }, ct);

    public Task<Outcome<DocumentDetail>> UndoCheckOutAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, "document.undo_checkout", correlationId, PlmAction.Modify, async (ctx, conn, tx) =>
        {
            if (ctx.CheckoutUser is null) return Fail("The document is not checked out.");
            var isAdmin = AccessPolicy.Evaluate(subject, ctx.Markings, PlmAction.Administer).Permitted;
            if (ctx.CheckoutUser != subject.Id && !isAdmin) return Forbid("checkout.not_owner");
            await using var cmd = new NpgsqlCommand("DELETE FROM plm.document_checkout WHERE revision_id = @rid", conn, tx);
            cmd.Parameters.AddWithValue("rid", ctx.RevisionId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Done(("checkedOutBy", ctx.CheckoutUserName ?? ctx.CheckoutUser), ("override", (ctx.CheckoutUser != subject.Id).ToString()));
        }, ct, allowAdminWithoutModify: true);

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Manual transition, or (administrators, with a reason) the "Set State" override. Same rules as parts.</summary>
    public Task<Outcome<DocumentDetail>> SetStateAsync(Subject subject, string number, string toState, string? reason, bool adminOverride, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, adminOverride ? "document.set_state" : "document.transition", correlationId,
            adminOverride ? PlmAction.Administer : PlmAction.Modify, async (ctx, conn, tx) =>
        {
            var target = ctx.Lifecycle.State(toState);
            if (target is null) return Invalid([$"'{toState}' is not a state of lifecycle {ctx.Lifecycle.Id}."]);
            if (toState == ctx.State) return Fail($"Revision {ctx.Revision} is already {target.Label}.");
            if (ctx.CheckoutUser is not null) return Fail($"Checked out by {ctx.CheckoutUserName ?? ctx.CheckoutUser}; check in or undo the check-out first.");
            if (adminOverride)
            {
                if (string.IsNullOrWhiteSpace(reason)) return Invalid(["A reason is required when overriding the lifecycle state."]);
            }
            else
            {
                var t = ctx.Lifecycle.Transitions.FirstOrDefault(t => t.From == ctx.State && t.To == toState);
                if (t is null) return Fail($"No transition from {ctx.StateLabel} to {target.Label}.");
                if (t.Via != "manual") return Fail(ViaMessage(t.Via, target.Label));
                // ASSUMPTION(A24): a document is reviewed for its content, so submitting needs a primary file.
                if (toState == UnderReviewState && !ctx.LatestHasPrimary) return Fail("Add a primary file before submitting the document for review.");
            }
            await using var cmd = new NpgsqlCommand(
                "UPDATE plm.document_revision SET lifecycle_state = @s, state_changed_at = @at, state_changed_by = @by WHERE id = @rid", conn, tx);
            cmd.Parameters.AddWithValue("s", toState);
            cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
            cmd.Parameters.AddWithValue("by", DisplayId(subject));
            cmd.Parameters.AddWithValue("rid", ctx.RevisionId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Done(("revision", ctx.Revision), ("from", ctx.State), ("to", toState), ("reason", reason));
        }, ct);

    // ---------------------------------------------------------------- markings

    public Task<Outcome<DocumentDetail>> ChangeMarkingsAsync(Subject subject, string number, Markings markings, string? reason, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, "document.markings_change", correlationId, PlmAction.Modify, async (ctx, conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) return Invalid(["A reason is required to change markings."]);
            if (ctx.Lifecycle.IsLocked(ctx.State)) return Fail($"Revision {ctx.Revision} is {ctx.StateLabel}; markings of locked data change through change control.");
            if (markings == ctx.Markings) return Fail("The markings are unchanged.");
            // The actor must also be permitted under the new markings, so nobody marks data away from themselves by accident.
            var after = AccessPolicy.Evaluate(subject, markings, PlmAction.Modify);
            if (!after.Permitted) return Forbid([.. after.Reasons.Select(r => "new_markings:" + r)]);
            await using var cmd = new NpgsqlCommand($"UPDATE plm.document_master SET {MarkingColumns.UpdateSet} WHERE id = @id", conn, tx);
            foreach (var (k, v) in MarkingColumns.Parameters(markings)) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("id", ctx.MasterId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Done(("before", Describe(ctx.Markings)), ("after", Describe(markings)), ("reason", reason));
        }, ct);

    // ---------------------------------------------------------------- shared machinery

    private sealed record Head(Guid MasterId, string Number, string LifecycleId, Markings Markings);

    private sealed record MutationContext(
        Guid MasterId, Guid RevisionId, string Revision, string State, string StateLabel, int LatestIteration, Guid LatestIterationId,
        bool LatestHasPrimary, Markings Markings, Lifecycle Lifecycle, string? CheckoutUser, string? CheckoutUserName);

    private sealed record Step(OutcomeKind Kind, IReadOnlyList<string> Errors, (string, string?)[] Details);

    private static Step Done(params (string, string?)[] details) => new(OutcomeKind.Ok, [], details);
    private static Step Fail(string message) => new(OutcomeKind.Conflict, [message], []);
    private static Step Invalid(IEnumerable<string> errors) => new(OutcomeKind.Invalid, errors.ToList(), []);
    private static Step Forbid(params string[] reasons) => new(OutcomeKind.Forbidden, reasons, []);

    /// <summary>
    /// Evaluates read and the required permission on the document, writing denial audits. Returns the head when
    /// permitted, otherwise the outcome to return (404 when the caller may not even read it).
    /// </summary>
    private async Task<(Head? Head, Outcome<DocumentDetail>? Denied)> AuthorizeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string number, string action, string? correlationId, PlmAction permission,
        bool forUpdate, bool allowAdminWithoutModify, CancellationToken ct)
    {
        var head = await LoadHeadAsync(conn, tx, number, forUpdate, ct);
        if (head is null) return (null, Outcome<DocumentDetail>.NotFound());
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return (null, Outcome<DocumentDetail>.NotFound());
        }
        var decision = AccessPolicy.Evaluate(subject, head.Markings, permission);
        if (!decision.Permitted && allowAdminWithoutModify && AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Administer).Permitted)
            decision = AccessDecision.Permit;
        if (!decision.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", decision.Reasons))), ct);
            await tx.CommitAsync(ct);
            return (null, Outcome<DocumentDetail>.Forbidden(decision.Reasons));
        }
        return (head, null);
    }

    /// <summary>Access and check-out check before a file is accepted, so unauthorized callers never get bytes into the vault.</summary>
    private async Task<Outcome<DocumentDetail>?> PrecheckAsync(Subject subject, string number, string action, string? correlationId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (head, denied) = await AuthorizeAsync(conn, tx, subject, number, action, correlationId, PlmAction.Modify, forUpdate: false, allowAdminWithoutModify: false, ct);
        if (denied is not null) return denied;
        await using var cmd = new NpgsqlCommand(
            "SELECT c.user_id FROM plm.document_revision r LEFT JOIN plm.document_checkout c ON c.revision_id = r.id WHERE r.master_id = @m AND r.is_latest", conn, tx);
        cmd.Parameters.AddWithValue("m", head!.MasterId);
        var owner = await cmd.ExecuteScalarAsync(ct) as string;
        return owner == subject.Id ? null : Outcome<DocumentDetail>.Conflict("Check the document out before adding files.");
    }

    private async Task<Outcome<DocumentDetail>> MutateAsync(
        Subject subject, string number, string action, string? correlationId, PlmAction permission,
        Func<MutationContext, NpgsqlConnection, NpgsqlTransaction, Task<Step>> body, CancellationToken ct, bool allowAdminWithoutModify = false)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (head, denied) = await AuthorizeAsync(conn, tx, subject, number, action, correlationId, permission, forUpdate: true, allowAdminWithoutModify, ct);
        if (denied is not null) return denied;

        var lifecycle = await Lifecycle.LoadAsync(conn, tx, head!.LifecycleId, ct) ?? throw new InvalidOperationException($"Lifecycle {head.LifecycleId} missing.");
        MutationContext ctx;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT r.id, r.revision, r.lifecycle_state, r.latest_iteration, c.user_id, c.user_name, i.id,
                   EXISTS (SELECT 1 FROM plm.document_file f WHERE f.iteration_id = i.id AND f.role = 'primary')
            FROM plm.document_revision r
            JOIN plm.document_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            LEFT JOIN plm.document_checkout c ON c.revision_id = r.id
            WHERE r.master_id = @m AND r.is_latest
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("m", head.MasterId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var state = r.GetString(2);
            ctx = new MutationContext(head.MasterId, r.GetGuid(0), r.GetString(1), state, lifecycle.State(state)?.Label ?? state, r.GetInt32(3),
                r.GetGuid(6), r.GetBoolean(7), head.Markings, lifecycle, r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5));
        }

        var step = await body(ctx, conn, tx);
        if (step.Kind != OutcomeKind.Ok)
        {
            await tx.RollbackAsync(ct);
            if (step.Kind == OutcomeKind.Forbidden)
            {
                await audit.AppendAsync(Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", step.Errors))), ct);
                return Outcome<DocumentDetail>.Forbidden(step.Errors);
            }
            return step.Kind == OutcomeKind.Invalid ? Outcome<DocumentDetail>.Invalid(step.Errors) : Outcome<DocumentDetail>.Conflict(step.Errors[0]);
        }
        await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Success, number, correlationId, step.Details), ct);
        var detail = await LoadDetailAsync(conn, tx, subject, number, null, ct);
        await tx.CommitAsync(ct);
        return Outcome<DocumentDetail>.Ok(detail!);
    }

    private static async Task<Head?> LoadHeadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string number, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT m.id, m.number, m.lifecycle_id, {MarkingColumns.Select("m")} FROM plm.document_master m WHERE m.number = @n{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Head(r.GetGuid(0), r.GetString(1), r.GetString(2), MarkingColumns.Read(r, 3));
    }

    private static List<string> ValidateUpload(FileUpload upload, out string? mediaType)
    {
        var errors = new List<string>();
        if (FileNames.Validate(upload.FileName) is { } nameError) errors.Add(nameError);
        mediaType = FileNames.NormalizeMediaType(upload.MediaType);
        if (mediaType is null) errors.Add($"'{upload.MediaType}' is not a valid media type.");
        return errors;
    }

    /// <summary>Inserts a file row into <paramref name="table"/> (iteration files or working-copy files).</summary>
    private static async Task InsertFileAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string table, string ownerColumn, Guid owner, string role,
        string fileName, string mediaType, StoredContent content, DateTimeOffset at, string by, CancellationToken ct)
    {
        if (table is not ("plm.document_file" or "plm.document_checkout_file") || ownerColumn is not ("iteration_id" or "revision_id"))
            throw new ArgumentException("Unknown file table.", nameof(table));
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {table} (id, {ownerColumn}, role, file_name, media_type, size_bytes, sha256, added_at, added_by) VALUES (@id, @owner, @role, @name, @type, @size, @sha, @at, @by)",
            conn, tx);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("role", role);
        cmd.Parameters.AddWithValue("name", fileName);
        cmd.Parameters.AddWithValue("type", mediaType);
        cmd.Parameters.AddWithValue("size", content.SizeBytes);
        cmd.Parameters.AddWithValue("sha", content.Sha256);
        cmd.Parameters.AddWithValue("at", at);
        cmd.Parameters.AddWithValue("by", by);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<DocumentFileView>> ReadFilesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, Guid owner, CancellationToken ct)
    {
        var files = new List<DocumentFileView>();
        await using var cmd = new NpgsqlCommand(sql + " ORDER BY f.role DESC, f.file_name", conn, tx);
        cmd.Parameters.AddWithValue("owner", owner);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) files.Add(ReadFile(r, 0));
        return files;
    }

    private static DocumentFileView ReadFile(NpgsqlDataReader r, int start) => new(
        r.GetGuid(start), r.GetString(start + 1), r.GetString(start + 2), r.GetString(start + 3), r.GetInt64(start + 4), r.GetString(start + 5),
        new DateTimeOffset(r.GetDateTime(start + 6), TimeSpan.Zero), r.GetString(start + 7));

    /// <summary>Loads the full view. Callers must already have established read access.</summary>
    private static async Task<DocumentDetail?> LoadDetailAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string number, string? revision, CancellationToken ct)
    {
        Guid id;
        string name, type, lifecycleId, createdBy;
        DateTimeOffset createdAt;
        Markings markings;
        await using (var cmd = new NpgsqlCommand(
            $"SELECT m.id, m.name, m.document_type, m.lifecycle_id, m.created_at, m.created_by, {MarkingColumns.Select("m")} FROM plm.document_master m WHERE m.number = @n",
            conn, tx))
        {
            cmd.Parameters.AddWithValue("n", number);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            (id, name, type, lifecycleId) = (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3));
            createdAt = new DateTimeOffset(r.GetDateTime(4), TimeSpan.Zero);
            createdBy = r.GetString(5);
            markings = MarkingColumns.Read(r, 6);
        }
        var lifecycle = (await Lifecycle.LoadAsync(conn, tx, lifecycleId, ct))!;

        var revisions = new List<DocumentRevisionView>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by FROM plm.document_revision WHERE master_id = @m",
            conn, tx))
        {
            cmd.Parameters.AddWithValue("m", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var st = r.GetString(2);
                revisions.Add(new DocumentRevisionView(r.GetGuid(0), r.GetString(1), st, lifecycle.State(st)?.Label ?? st, r.GetBoolean(3), lifecycle.IsLocked(st),
                    r.GetInt32(4), new DateTimeOffset(r.GetDateTime(5), TimeSpan.Zero), r.GetString(6), new DateTimeOffset(r.GetDateTime(7), TimeSpan.Zero), r.GetString(8)));
            }
        }
        revisions.Sort((a, b) => RevisionScheme.ToIndex(a.Revision).CompareTo(RevisionScheme.ToIndex(b.Revision)));
        var selected = revision is null ? revisions.Single(r => r.IsLatest) : revisions.FirstOrDefault(r => r.Revision == revision);
        if (selected is null) return null;

        var filesByIteration = new Dictionary<Guid, List<DocumentFileView>>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT f.iteration_id, f.id, f.role, f.file_name, f.media_type, f.size_bytes, f.sha256, f.added_at, f.added_by
            FROM plm.document_file f JOIN plm.document_iteration i ON i.id = f.iteration_id JOIN plm.document_revision r ON r.id = i.revision_id
            WHERE r.master_id = @m ORDER BY f.role DESC, f.file_name
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("m", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var key = r.GetGuid(0);
                if (!filesByIteration.TryGetValue(key, out var list)) filesByIteration[key] = list = [];
                list.Add(ReadFile(r, 1));
            }
        }

        var history = new List<DocumentIterationView>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT i.id, r.revision, i.iteration, i.description, i.comment, i.created_at, i.created_by
            FROM plm.document_iteration i JOIN plm.document_revision r ON r.id = i.revision_id
            WHERE r.master_id = @m
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("m", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var iid = r.GetGuid(0);
                history.Add(new DocumentIterationView(iid, r.GetString(1), r.GetInt32(2), $"{r.GetString(1)}.{r.GetInt32(2)}", r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), new DateTimeOffset(r.GetDateTime(5), TimeSpan.Zero), r.GetString(6),
                    filesByIteration.TryGetValue(iid, out var files) ? files : []));
            }
        }
        history = [.. history.OrderByDescending(h => RevisionScheme.ToIndex(h.Revision)).ThenByDescending(h => h.Iteration)];
        var iteration = history.First(h => h.Revision == selected.Revision && h.Iteration == selected.LatestIteration);

        DocumentCheckoutView? checkout = null;
        await using (var cmd = new NpgsqlCommand("SELECT user_id, user_name, checked_out_at, description FROM plm.document_checkout WHERE revision_id = @rid", conn, tx))
        {
            cmd.Parameters.AddWithValue("rid", selected.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                checkout = new DocumentCheckoutView(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), new DateTimeOffset(r.GetDateTime(2), TimeSpan.Zero),
                    r.GetString(0) == subject.Id, r.GetString(0) == subject.Id ? r.GetString(3) : null, null);
        }
        // The working copy is private to its owner until check-in.
        if (checkout is { IsMine: true })
            checkout = checkout with
            {
                Files = await ReadFilesAsync(conn, tx,
                    "SELECT f.id, f.role, f.file_name, f.media_type, f.size_bytes, f.sha256, f.added_at, f.added_by FROM plm.document_checkout_file f WHERE f.revision_id = @owner",
                    selected.Id, ct),
            };

        var modify = AccessPolicy.Evaluate(subject, markings, PlmAction.Modify);
        var admin = AccessPolicy.Evaluate(subject, markings, PlmAction.Administer);
        var download = AccessPolicy.Evaluate(subject, markings, PlmAction.Download);
        var hasPrimary = iteration.Files.Any(f => f.Role == FileRoles.Primary);
        var transitions = selected.IsLatest
            ? lifecycle.Transitions.Where(t => t.From == selected.State).Select(t =>
            {
                var label = lifecycle.State(t.To)!.Label;
                string? why = t.Via != "manual" ? ViaMessage(t.Via, label)
                    : !modify.Permitted ? "You may not modify this document."
                    : checkout is not null ? "Check in or undo the check-out first."
                    : t.To == UnderReviewState && !hasPrimary ? "Add a primary file first."
                    : null;
                return new DocumentTransitionView(t.To, label, t.Via, why is null, why);
            }).ToList()
            : [];

        return new DocumentDetail(id, number, name, type, lifecycleId, markings, markings.BannerText(), selected, iteration, revisions, history,
            checkout, transitions, new DocumentPermissions(modify.Permitted, admin.Permitted, download.Permitted, modify.Reasons), createdAt, createdBy);
    }

    private static string ViaMessage(string via, string targetLabel) => via switch
    {
        "workflow" => $"{targetLabel} is reached only through an approved review workflow (increment 1.5).",
        "change" => $"{targetLabel} is reached only through a released change notice (increment 1.6).",
        _ => $"Transition to {targetLabel} is not available.",
    };

    private static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string? number, string? correlationId, params (string, string?)[] details) =>
        Entry(s, ObjectType, action, outcome, number, correlationId, details);

    private static AuditEntry Entry(Subject s, string objectType, string action, AuditOutcome outcome, string? number, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s),
        Action = action,
        Outcome = outcome,
        ObjectType = objectType,
        ObjectId = number,
        CorrelationId = correlationId,
        Details = AuditDetails.Of(details),
    };

    private static string Describe(Markings m) => JsonSerializer.Serialize(new
    {
        jurisdiction = m.ExportJurisdiction.ToString(), m.Eccn, m.UsmlCategory, cui = m.CuiCategories, distribution = m.DistributionStatement.ToString(),
        m.ControllingOffice, m.DistributionReason, m.Program, m.OwningOrg, authorizations = m.ExportAuthorizations,
    });

    // ASSUMPTION: created_by/state_changed_by store the login name for display; the audit log keeps the stable IdP subject.
    private static string DisplayId(Subject s) => s.Username ?? s.Id;

    private static string EscapeLike(string s) => s.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9._\-]{0,39}$")]
    private static partial Regex NumberPattern();
}
