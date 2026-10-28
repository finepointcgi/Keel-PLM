using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Documents;

/// <summary>
/// Part ↔ document links (ADR-0016). Links belong to part iterations and change only through the part's check-out
/// working copy. Documents must not reference the Parts module, so part rows are read here with key lookups on
/// <c>plm.part_master</c> plus the marking column contract; list queries over parts apply the read filter.
/// </summary>
public sealed partial class DocumentService
{
    private const string PartObjectType = "part";

    private sealed record PartHead(Guid MasterId, string Number, Markings Markings);

    private sealed record PartLinkTarget(Guid RevisionId, string Revision, int LatestIteration, bool Locked, string? CheckoutUser);

    public async Task<Outcome<PartDocumentLinks>> AddLinkToWorkingCopyAsync(Subject subject, string partNumber, string documentNumber, string linkType,
        string? documentRevision, string? correlationId, CancellationToken ct = default)
    {
        const string action = "part.link_document";
        if (!LinkTypes.All.Contains(linkType)) return Outcome<PartDocumentLinks>.Invalid(["Link type must be 'describes' or 'references'."]);
        if (linkType == LinkTypes.References && documentRevision is not null)
            return Outcome<PartDocumentLinks>.Invalid(["A 'references' link points at the document and resolves at read time; do not pass a revision."]);

        return await MutateLinksAsync(subject, partNumber, action, correlationId, async (part, target, conn, tx) =>
        {
            var doc = await LoadHeadAsync(conn, tx, documentNumber, forUpdate: false, ct);
            var docRead = doc is null ? null : AccessPolicy.Evaluate(subject, doc.Markings, PlmAction.Read);
            if (doc is null || !docRead!.Permitted)
            {
                // Same answer whether the document is missing or hidden, so its existence is not disclosed (ADR-0004).
                if (doc is not null)
                    await audit.AppendAsync(Entry(subject, ObjectType, "document.read", AuditOutcome.Denied, documentNumber, correlationId,
                        ("reasons", string.Join(",", docRead!.Reasons)), ("via", action), ("part", partNumber)), ct);
                return LinkStep.Invalid($"Document {documentNumber} was not found.");
            }

            Guid? revisionId = null;
            string? revisionLabel = null;
            if (linkType == LinkTypes.Describes)
            {
                await using var rev = new NpgsqlCommand(
                    "SELECT id, revision FROM plm.document_revision WHERE master_id = @m AND (CASE WHEN @rev::text IS NULL THEN is_latest ELSE revision = @rev END)", conn, tx);
                rev.Parameters.AddWithValue("m", doc.MasterId);
                rev.Parameters.AddWithValue("rev", (object?)documentRevision ?? DBNull.Value);
                await using var r = await rev.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return LinkStep.Invalid($"Document {documentNumber} has no revision {documentRevision}.");
                (revisionId, revisionLabel) = (r.GetGuid(0), r.GetString(1));
            }

            await using (var dup = new NpgsqlCommand(
                "SELECT 1 FROM plm.part_checkout_document_link WHERE revision_id = @rid AND link_type = @t AND document_master_id = @d", conn, tx))
            {
                dup.Parameters.AddWithValue("rid", target.RevisionId);
                dup.Parameters.AddWithValue("t", linkType);
                dup.Parameters.AddWithValue("d", doc.MasterId);
                if (await dup.ExecuteScalarAsync(ct) is not null) return LinkStep.Conflict($"{partNumber} already {linkType} {documentNumber} in the working copy.");
            }
            await using (var ins = new NpgsqlCommand(
                """
                INSERT INTO plm.part_checkout_document_link (id, revision_id, link_type, document_master_id, document_revision_id, created_at, created_by)
                VALUES (@id, @rid, @t, @d, @r, @at, @by)
                """, conn, tx))
            {
                ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
                ins.Parameters.AddWithValue("rid", target.RevisionId);
                ins.Parameters.AddWithValue("t", linkType);
                ins.Parameters.AddWithValue("d", doc.MasterId);
                ins.Parameters.AddWithValue("r", (object?)revisionId ?? DBNull.Value);
                ins.Parameters.AddWithValue("at", clock.GetUtcNow());
                ins.Parameters.AddWithValue("by", DisplayId(subject));
                await ins.ExecuteNonQueryAsync(ct);
            }
            return LinkStep.Done(("document", documentNumber), ("linkType", linkType), ("documentRevision", revisionLabel),
                ("documentMarkings", doc.Markings.BannerText()), ("partRevision", target.Revision));
        }, ct);
    }

    public Task<Outcome<PartDocumentLinks>> RemoveLinkFromWorkingCopyAsync(Subject subject, string partNumber, Guid linkId, string? correlationId, CancellationToken ct = default) =>
        MutateLinksAsync(subject, partNumber, "part.unlink_document", correlationId, async (part, target, conn, tx) =>
        {
            Guid docId;
            string linkType;
            await using (var sel = new NpgsqlCommand(
                "SELECT document_master_id, link_type FROM plm.part_checkout_document_link WHERE id = @id AND revision_id = @rid", conn, tx))
            {
                sel.Parameters.AddWithValue("id", linkId);
                sel.Parameters.AddWithValue("rid", target.RevisionId);
                await using var r = await sel.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return LinkStep.Conflict("The working copy has no such document link.");
                (docId, linkType) = (r.GetGuid(0), r.GetString(1));
            }
            string docNumber;
            Markings docMarkings;
            await using (var doc = new NpgsqlCommand($"SELECT d.number, {MarkingColumns.Select("d")} FROM plm.document_master d WHERE d.id = @id", conn, tx))
            {
                doc.Parameters.AddWithValue("id", docId);
                await using var r = await doc.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                (docNumber, docMarkings) = (r.GetString(0), MarkingColumns.Read(r, 1));
            }
            // Changing a relationship to a document requires being able to read that document. The reason given is
            // generic so the hidden document's markings are not disclosed through the denial.
            if (!AccessPolicy.Evaluate(subject, docMarkings, PlmAction.Read).Permitted) return LinkStep.Forbid("document.read_denied");
            await using (var del = new NpgsqlCommand("DELETE FROM plm.part_checkout_document_link WHERE id = @id", conn, tx))
            {
                del.Parameters.AddWithValue("id", linkId);
                await del.ExecuteNonQueryAsync(ct);
            }
            return LinkStep.Done(("document", docNumber), ("linkType", linkType), ("linkId", linkId.ToString()), ("partRevision", target.Revision));
        }, ct);

    /// <summary>
    /// Links of one part iteration (default: latest iteration of the latest revision), plus the caller's working-copy
    /// links when the caller holds the part's check-out. Documents the caller may not read are redacted placeholders (A4).
    /// </summary>
    public async Task<Outcome<PartDocumentLinks>> GetPartLinksAsync(Subject subject, string partNumber, string? partRevision, int? partIteration,
        string? correlationId, CancellationToken ct = default)
    {
        const string action = "part.read_document_links";
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var part = await LoadPartHeadAsync(conn, tx, partNumber, forUpdate: false, ct);
        if (part is null) return Outcome<PartDocumentLinks>.NotFound();
        var read = AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, PartObjectType, action, AuditOutcome.Denied, partNumber, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<PartDocumentLinks>.NotFound();
        }

        Guid iterationId;
        string label;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT i.id, r.revision, i.iteration FROM plm.part_revision r JOIN plm.part_iteration i ON i.revision_id = r.id
            WHERE r.master_id = @m
              AND (CASE WHEN @rev::text IS NULL THEN r.is_latest ELSE r.revision = @rev END)
              AND (CASE WHEN @it::int IS NULL THEN i.iteration = r.latest_iteration ELSE i.iteration = @it END)
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("m", part.MasterId);
            cmd.Parameters.AddWithValue("rev", (object?)partRevision ?? DBNull.Value);
            cmd.Parameters.AddWithValue("it", (object?)partIteration ?? DBNull.Value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Outcome<PartDocumentLinks>.NotFound();
            (iterationId, label) = (r.GetGuid(0), $"{r.GetString(1)}.{r.GetInt32(2)}");
        }
        var result = await LoadLinksAsync(conn, tx, subject, part, iterationId, label, ct);
        await audit.AppendAsync(tx, Entry(subject, PartObjectType, action, AuditOutcome.Success, partNumber, correlationId,
            ("iteration", label), ("documents", string.Join(",", result.Links.Where(l => !l.Redacted).Select(l => l.Document!.Number))),
            ("redacted", result.Links.Count(l => l.Redacted).ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return Outcome<PartDocumentLinks>.Ok(result);
    }

    /// <summary>Parts whose latest iteration links to the document. Parts the caller may not read are redacted placeholders.</summary>
    public async Task<Outcome<DocumentPartLinks>> GetDocumentPartsAsync(Subject subject, string documentNumber, string? correlationId, CancellationToken ct = default)
    {
        const string action = "document.read_part_links";
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var doc = await LoadHeadAsync(conn, tx, documentNumber, forUpdate: false, ct);
        if (doc is null) return Outcome<DocumentPartLinks>.NotFound();
        var read = AccessPolicy.Evaluate(subject, doc.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, documentNumber, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<DocumentPartLinks>.NotFound();
        }

        var filter = AccessPolicy.BuildReadFilter(subject, "p");
        var parts = new List<DocumentPartLinkView>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT l.id, l.link_type, v.number, v.name, r.revision, i.iteration, {MarkingColumns.Select("v")}
            FROM plm.part_document_link l
            JOIN plm.part_iteration i ON i.id = l.part_iteration_id
            JOIN plm.part_revision r ON r.id = i.revision_id AND r.is_latest AND i.iteration = r.latest_iteration
            LEFT JOIN LATERAL (SELECT p.* FROM plm.part_master p WHERE p.id = r.master_id AND {filter.Sql}) v ON true
            WHERE l.document_master_id = @doc
            ORDER BY v.number NULLS LAST, l.id
            """, conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("doc", doc.MasterId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (r.IsDBNull(2))
                {
                    parts.Add(new DocumentPartLinkView(r.GetGuid(0), r.GetString(1), true, null, null, null, null, null));
                    continue;
                }
                var m = MarkingColumns.Read(r, 6);
                parts.Add(new DocumentPartLinkView(r.GetGuid(0), r.GetString(1), false, r.GetString(2), r.GetString(3),
                    $"{r.GetString(4)}.{r.GetInt32(5)}", m.BannerText(), m));
            }
        }
        await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Success, documentNumber, correlationId,
            ("parts", string.Join(",", parts.Where(p => !p.Redacted).Select(p => p.PartNumber))),
            ("redacted", parts.Count(p => p.Redacted).ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        var visible = parts.Where(p => !p.Redacted).Select(p => p.Markings!).Append(doc.Markings);
        return Outcome<DocumentPartLinks>.Ok(new DocumentPartLinks(documentNumber, parts, Markings.AggregateBanner(visible)));
    }

    // ---------------------------------------------------------------- link machinery

    private sealed record LinkStep(OutcomeKind Kind, IReadOnlyList<string> Errors, (string, string?)[] Details)
    {
        public static LinkStep Done(params (string, string?)[] details) => new(OutcomeKind.Ok, [], details);
        public static LinkStep Conflict(string message) => new(OutcomeKind.Conflict, [message], []);
        public static LinkStep Invalid(string message) => new(OutcomeKind.Invalid, [message], []);
        public static LinkStep Forbid(params string[] reasons) => new(OutcomeKind.Forbidden, reasons, []);
    }

    /// <summary>
    /// Serializes on the part master row (same lock as part check-out/check-in), checks read and modify on the part, that
    /// its latest revision is unlocked and that the caller holds its check-out, then runs <paramref name="body"/>.
    /// </summary>
    private async Task<Outcome<PartDocumentLinks>> MutateLinksAsync(Subject subject, string partNumber, string action, string? correlationId,
        Func<PartHead, PartLinkTarget, NpgsqlConnection, NpgsqlTransaction, Task<LinkStep>> body, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var part = await LoadPartHeadAsync(conn, tx, partNumber, forUpdate: true, ct);
        if (part is null) return Outcome<PartDocumentLinks>.NotFound();
        var read = AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Read);
        var decision = read.Permitted ? AccessPolicy.Evaluate(subject, part.Markings, PlmAction.Modify) : read;
        if (!decision.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, PartObjectType, action, AuditOutcome.Denied, partNumber, correlationId, ("reasons", string.Join(",", decision.Reasons))), ct);
            await tx.CommitAsync(ct);
            return read.Permitted ? Outcome<PartDocumentLinks>.Forbidden(decision.Reasons) : Outcome<PartDocumentLinks>.NotFound();
        }

        PartLinkTarget target;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT r.id, r.revision, r.latest_iteration, plm.is_locked_state(r.master_id, r.lifecycle_state), c.user_id
            FROM plm.part_revision r LEFT JOIN plm.part_checkout c ON c.revision_id = r.id
            WHERE r.master_id = @m AND r.is_latest
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("m", part.MasterId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            target = new PartLinkTarget(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetBoolean(3), r.IsDBNull(4) ? null : r.GetString(4));
        }
        if (target.Locked) return Outcome<PartDocumentLinks>.Conflict($"Revision {target.Revision} of {partNumber} is locked; its document links change through change control.");
        if (target.CheckoutUser != subject.Id) return Outcome<PartDocumentLinks>.Conflict($"Check {partNumber} out before changing its document links.");

        var step = await body(part, target, conn, tx);
        if (step.Kind != OutcomeKind.Ok)
        {
            await tx.RollbackAsync(ct);
            if (step.Kind == OutcomeKind.Forbidden)
            {
                await audit.AppendAsync(Entry(subject, PartObjectType, action, AuditOutcome.Denied, partNumber, correlationId, ("reasons", string.Join(",", step.Errors))), ct);
                return Outcome<PartDocumentLinks>.Forbidden(step.Errors);
            }
            return step.Kind == OutcomeKind.Invalid ? Outcome<PartDocumentLinks>.Invalid(step.Errors) : Outcome<PartDocumentLinks>.Conflict(step.Errors[0]);
        }
        await audit.AppendAsync(tx, Entry(subject, PartObjectType, action, AuditOutcome.Success, partNumber, correlationId, step.Details), ct);
        Guid iterationId;
        await using (var cmd = new NpgsqlCommand("SELECT id FROM plm.part_iteration WHERE revision_id = @rid AND iteration = @it", conn, tx))
        {
            cmd.Parameters.AddWithValue("rid", target.RevisionId);
            cmd.Parameters.AddWithValue("it", target.LatestIteration);
            iterationId = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        }
        var result = await LoadLinksAsync(conn, tx, subject, part, iterationId, $"{target.Revision}.{target.LatestIteration}", ct);
        await tx.CommitAsync(ct);
        return Outcome<PartDocumentLinks>.Ok(result);
    }

    private static async Task<PartHead?> LoadPartHeadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string number, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT m.id, m.number, {MarkingColumns.Select("m")} FROM plm.part_master m WHERE m.number = @n{(forUpdate ? " FOR UPDATE" : "")}", conn, tx);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new PartHead(r.GetGuid(0), r.GetString(1), MarkingColumns.Read(r, 2));
    }

    /// <summary>Links of an iteration plus, for the check-out owner, the working-copy links. Caller has read access to the part.</summary>
    private static async Task<PartDocumentLinks> LoadLinksAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, PartHead part,
        Guid iterationId, string label, CancellationToken ct)
    {
        var links = await QueryLinksAsync(conn, tx, subject, "plm.part_document_link", "part_iteration_id", iterationId, isWorking: false, ct);
        List<PartDocumentLinkView>? working = null;
        await using (var cmd = new NpgsqlCommand(
            "SELECT c.revision_id FROM plm.part_revision r JOIN plm.part_checkout c ON c.revision_id = r.id WHERE r.master_id = @m AND r.is_latest AND c.user_id = @u", conn, tx))
        {
            cmd.Parameters.AddWithValue("m", part.MasterId);
            cmd.Parameters.AddWithValue("u", subject.Id);
            if (await cmd.ExecuteScalarAsync(ct) is Guid checkoutRevision)
                working = await QueryLinksAsync(conn, tx, subject, "plm.part_checkout_document_link", "revision_id", checkoutRevision, isWorking: true, ct);
        }
        var visible = links.Concat(working ?? []).Where(l => !l.Redacted).Select(l => l.Document!.Markings).Append(part.Markings);
        return new PartDocumentLinks(part.Number, label, links, working, Markings.AggregateBanner(visible));
    }

    private static async Task<List<PartDocumentLinkView>> QueryLinksAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string table, string ownerColumn,
        Guid owner, bool isWorking, CancellationToken ct)
    {
        if (table is not ("plm.part_document_link" or "plm.part_checkout_document_link") || ownerColumn is not ("part_iteration_id" or "revision_id"))
            throw new ArgumentException("Unknown link table.", nameof(table));
        var filter = AccessPolicy.BuildReadFilter(subject, "d");
        var links = new List<PartDocumentLinkView>();
        // The lateral subquery returns the document only when the read filter admits it; otherwise the row is a placeholder
        // and no document column ever leaves the database.
        // ASSUMPTION(A23): 'references' resolves to the latest Released revision, else the latest revision.
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT l.id, l.link_type, l.document_revision_id, l.created_at, l.created_by,
                   v.number, v.name, v.document_type, {MarkingColumns.Select("v")},
                   pr.revision, lr.id, rr.revision, rr.latest_iteration, rr.lifecycle_state
            FROM {table} l
            LEFT JOIN LATERAL (SELECT d.* FROM plm.document_master d WHERE d.id = l.document_master_id AND {filter.Sql}) v ON true
            LEFT JOIN plm.document_revision lr ON lr.master_id = v.id AND lr.is_latest
            LEFT JOIN plm.document_revision pr ON pr.id = l.document_revision_id AND v.id IS NOT NULL
            LEFT JOIN LATERAL (
                SELECT x.revision, x.latest_iteration, x.lifecycle_state FROM plm.document_revision x
                WHERE v.id IS NOT NULL AND x.id = COALESCE(l.document_revision_id,
                    (SELECT y.id FROM plm.document_revision y WHERE y.master_id = v.id AND y.lifecycle_state = '{ReleasedState}'
                     ORDER BY length(y.revision) DESC, y.revision DESC LIMIT 1),
                    lr.id)
            ) rr ON true
            WHERE l.{ownerColumn} = @owner
            ORDER BY l.link_type, v.number NULLS LAST, l.id
            """, conn, tx);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("owner", owner);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetGuid(0);
            var type = r.GetString(1);
            if (r.IsDBNull(5))
            {
                links.Add(new PartDocumentLinkView(id, type, isWorking, true, null, null, false, null, null));
                continue;
            }
            const int mk = 8;
            var markings = MarkingColumns.Read(r, mk);
            var after = mk + MarkingColumns.Count;
            var pinned = r.IsDBNull(after) ? null : r.GetString(after);
            var newer = !r.IsDBNull(2) && r.GetGuid(after + 1) != r.GetGuid(2);
            var doc = new LinkedDocumentView(r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(after + 2), r.GetInt32(after + 3),
                r.GetString(after + 4), markings.BannerText(), markings);
            links.Add(new PartDocumentLinkView(id, type, isWorking, false, doc, pinned, newer,
                new DateTimeOffset(r.GetDateTime(3), TimeSpan.Zero), r.GetString(4)));
        }
        return links;
    }
}
