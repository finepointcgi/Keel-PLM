using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Documents.Compare;
using Keel.Documents.Marking;
using Keel.Documents.Vault;
using Keel.Security;
using Npgsql;

namespace Keel.Documents;

/// <summary>
/// Cross-document compare and redline review (increment 9.5, ADR-0053). Any two versions of one document, or any version of any two documents, are compared word by word
/// when both files are text Keel can read (plain text, Markdown, DOCX, XLSX, PDF text layer), or by size, hash and facts when they are not. The comparison is computed from
/// the vault bytes on every view (verified against their SHA-256) and is never stored. Rules, all enforced here and nowhere else:
/// <list type="bullet">
/// <item>The caller must be allowed to READ both documents: if either is not readable the answer is the same 404 as for a missing document and the denial is audited on the
/// document that refused. Reading the CONTENT needs permission to download both (viewing is downloading, VWR-1); without it only the facts table is shown.</item>
/// <item>Whatever is shown carries the aggregate marking of both documents; the exports carry the banners on every page and every notice either document needs.</item>
/// <item>Review comments are marked data derived from both documents: they have no markings of their own, they are read and written only by someone who may read and
/// download both, and the list query joins both masters through <see cref="AccessPolicy.BuildReadFilter"/>.</item>
/// </list>
/// </summary>
public sealed partial class DocumentService
{
    public const string CompareAction = "document.compare";
    public const string RedlineCommentAction = "document.redline_comment";
    public const string RedlineResolveAction = "document.redline_resolve";
    public const string RedlineExportAction = "document.redline_export";
    public const int MaxRedlineCommentLength = 4000;
    public const int MaxRedlineCommentsPerReview = 500;
    public const int MaxRedlineCommentsPerMinute = 20;

    private static readonly SemaphoreSlim CompareSlots = new(2, 2);

    private sealed record PreparedSide(Head Head, string Name, VersionRecord Version);

    private sealed record Prepared(PreparedSide Left, PreparedSide Right, bool CanDownloadBoth, string Banner, IReadOnlyList<string> Notices);

    private static Outcome<T> Remap<T, U>(Outcome<U> o) => o.Kind switch
    {
        OutcomeKind.NotFound => Outcome<T>.NotFound(),
        OutcomeKind.Forbidden => Outcome<T>.Forbidden(o.Errors),
        OutcomeKind.Conflict => Outcome<T>.Conflict(o.Errors[0]),
        OutcomeKind.Unavailable => Outcome<T>.Unavailable(o.Errors[0]),
        _ => Outcome<T>.Invalid(o.Errors),
    };

    // ---------------------------------------------------------------- authorization and version resolution

    /// <summary>
    /// Authorizes both sides and resolves the versions. A document the caller may not read is a 404 for the whole request (existence is not disclosed, and which side refused is
    /// not either) with an audited denial on that document; a disposed document or an unavailable version is said plainly.
    /// </summary>
    private async Task<Outcome<Prepared>> PrepareAsync(Subject subject, CompareRef left, CompareRef right, string action, string? correlationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(left.Number) || string.IsNullOrWhiteSpace(right.Number)) return Outcome<Prepared>.Invalid(["Choose a document for both sides."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var lh = await LoadHeadAsync(conn, tx, left.Number.Trim(), forUpdate: false, ct);
        var rh = left.Number.Trim() == right.Number.Trim() ? lh : await LoadHeadAsync(conn, tx, right.Number.Trim(), forUpdate: false, ct);
        if (lh is null || rh is null) return Outcome<Prepared>.NotFound();
        var denied = false;
        var both = lh == rh ? new[] { lh } : new[] { lh, rh };
        foreach (var h in both)
        {
            var read = AccessPolicy.Evaluate(subject, h.Markings, PlmAction.Read);
            if (read.Permitted) continue;
            denied = true;
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, h.Number, correlationId, ("reasons", string.Join(",", read.Reasons)), ("with", h == lh ? rh.Number : lh.Number)), ct);
        }
        if (denied)
        {
            await tx.CommitAsync(ct);
            return Outcome<Prepared>.NotFound();
        }
        foreach (var h in both)
            if (await IsDisposedAsync(conn, tx, h.MasterId, ct))
            {
                await tx.CommitAsync(ct);
                return Outcome<Prepared>.Conflict($"The content of {h.Number} was disposed of under a retention schedule.");
            }
        var lRecords = await LoadVersionRecordsAsync(conn, tx, lh.MasterId, ct);
        var rRecords = lh == rh ? lRecords : await LoadVersionRecordsAsync(conn, tx, rh.MasterId, ct);
        await tx.CommitAsync(ct);

        var l = Pick(lRecords, left.IterationId);
        var r = Pick(rRecords, right.IterationId);
        if (l is null || r is null) return Outcome<Prepared>.Invalid(["One of the versions does not exist or is not available."]);
        if (l.FileId is null || r.FileId is null || l.Sha256 is null || r.Sha256 is null || l.FileName is null || r.FileName is null)
            return Outcome<Prepared>.Invalid(["Both versions need a primary file to be compared."]);
        if (l.IterationId == r.IterationId) return Outcome<Prepared>.Invalid(["Choose two different versions to compare."]);

        var lName = await NameOfAsync(lh.MasterId, ct);
        var rName = lh == rh ? lName : await NameOfAsync(rh.MasterId, ct);
        var download = AccessPolicy.Evaluate(subject, lh.Markings, PlmAction.Download).Permitted && AccessPolicy.Evaluate(subject, rh.Markings, PlmAction.Download).Permitted;
        var all = both.Select(h => h.Markings).ToList();
        return Outcome<Prepared>.Ok(new Prepared(new PreparedSide(lh, lName, l), new PreparedSide(rh, rName, r), download, AggregateBanner(all), AggregateNotices(all)));
    }

    /// <summary>The banner of an output that shows several documents (the union of what each requires, programs listed), and every distinct notice, strictest first.</summary>
    internal static string AggregateBanner(IReadOnlyCollection<Markings> all)
    {
        var distinct = all.Distinct().ToList();
        var programs = distinct.Select(m => m.Program).Where(p => p is not null).Select(p => p!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var banner = Markings.AggregateBanner(distinct);
        return programs.Count > 0 ? banner + " · PROGRAM " + string.Join(", ", programs) : banner;
    }

    internal static IReadOnlyList<string> AggregateNotices(IReadOnlyCollection<Markings> all)
    {
        var lines = new List<string>();
        foreach (var m in all.Distinct().OrderByDescending(m => m.DistributionStatement).ThenByDescending(m => m.IsExportControlled).ThenBy(m => m.BannerText(), StringComparer.Ordinal))
            foreach (var n in MarkingStatements.Notices(m))
                if (!lines.Contains(n)) lines.Add(n);
        return lines;
    }

    private static VersionRecord? Pick(List<VersionRecord> records, Guid? iterationId)
    {
        if (iterationId is { } id) return records.FirstOrDefault(v => v.IterationId == id);
        var latestRevision = records.Where(v => v.RevisionIsLatest).ToList();
        return latestRevision.Count == 0 ? null : latestRevision.MaxBy(v => v.Iteration);
    }

    private static CompareSide ToSide(PreparedSide s, string banner, string format)
    {
        var v = s.Version;
        return new CompareSide(s.Head.Number, s.Name, v.IterationId, v.Label, v.State, v.CreatedAt, v.CreatedBy, v.Comment, v.FileName!, v.MediaType ?? "application/octet-stream", v.SizeBytes ?? 0, v.Sha256!, banner, format);
    }

    // ---------------------------------------------------------------- compare

    /// <summary>
    /// Compares two document versions. See the type remarks for who may do it and what is shown. Audited as <c>document.compare</c> on each document, with version labels,
    /// checksums and counts, never any text.
    /// </summary>
    public async Task<Outcome<DocumentComparison>> CompareDocumentsAsync(Subject subject, CompareRef left, CompareRef right, string? correlationId, CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(subject, left, right, CompareAction, correlationId, ct);
        if (!prepared.IsOk) return Remap<DocumentComparison, Prepared>(prepared);
        var p = prepared.Value!;
        Outcome<DocumentComparison> result;
        try
        {
            result = await BuildAsync(subject, p, includeThreads: true, ct);
        }
        catch (Exception e) when (e is VaultIntegrityException or VaultUnavailableException)
        {
            await AuditBothAsync(subject, p, CompareAction, AuditOutcome.Failure, correlationId, null, ("error", e is VaultIntegrityException ? "integrity" : "vault_unavailable"));
            return Outcome<DocumentComparison>.Unavailable(e is VaultIntegrityException
                ? "A stored file failed its integrity check, so nothing was compared. This has been recorded."
                : "The vault could not be reached just now, so nothing was compared. Try again in a moment.");
        }
        if (!result.IsOk) return result;
        var c = result.Value!;
        await AuditBothAsync(subject, p, CompareAction, AuditOutcome.Success, correlationId, null,
            ("mode", c.Mode), ("insertedWords", c.Diff?.Counts.InsertedWords.ToString(CultureInfo.InvariantCulture)), ("deletedWords", c.Diff?.Counts.DeletedWords.ToString(CultureInfo.InvariantCulture)),
            ("changes", c.Diff?.Counts.Changes.ToString(CultureInfo.InvariantCulture)), ("markings", p.Banner));
        return result;
    }

    private async Task AuditBothAsync(Subject subject, Prepared p, string action, AuditOutcome outcome, string? correlationId, NpgsqlTransaction? tx, params (string, string?)[] extra)
    {
        var details = new List<(string, string?)>
        {
            ("left", $"{p.Left.Head.Number} {p.Left.Version.Label}"), ("right", $"{p.Right.Head.Number} {p.Right.Version.Label}"),
            ("leftSha256", p.Left.Version.Sha256), ("rightSha256", p.Right.Version.Sha256),
        };
        details.AddRange(extra);
        foreach (var number in p.Left.Head.MasterId == p.Right.Head.MasterId ? new[] { p.Left.Head.Number } : new[] { p.Left.Head.Number, p.Right.Head.Number })
        {
            var entry = Entry(subject, action, outcome, number, correlationId, [.. details]);
            if (tx is null) await audit.AppendAsync(entry, CancellationToken.None);
            else await audit.AppendAsync(tx, entry, CancellationToken.None);
        }
    }

    private async Task<Outcome<DocumentComparison>> BuildAsync(Subject subject, Prepared p, bool includeThreads, CancellationToken ct)
    {
        var l = p.Left.Version;
        var r = p.Right.Version;
        var sameBytes = l.Sha256 == r.Sha256;
        var sameDoc = p.Left.Head.MasterId == p.Right.Head.MasterId;
        var lf = FormatOf(l.FileName!, l.MediaType);
        var rf = FormatOf(r.FileName!, r.MediaType);
        var metadata = MetadataRows(p);

        DocumentComparison Facts(string mode, string? reason) => new(ToSide(p.Left, p.Banner, lf), ToSide(p.Right, p.Banner, rf), mode, sameDoc, sameBytes, reason, p.Banner, p.Notices, null, metadata, null, [], CanComment(subject));

        if (sameBytes) return Outcome<DocumentComparison>.Ok(Facts(CompareModes.Identical, null));
        if (!p.CanDownloadBoth) return Outcome<DocumentComparison>.Ok(Facts(CompareModes.Metadata, "Content not comparable for you: comparing the text needs permission to download both documents. The facts below are compared."));
        if (lf == "other" || rf == "other")
        {
            var which = lf == "other" ? (rf == "other" ? "Neither file is" : p.Left.Head.Number + " is not") : p.Right.Head.Number + " is not";
            return Outcome<DocumentComparison>.Ok(Facts(CompareModes.Metadata, $"Content not comparable: {which} a format Keel reads as text (text, Markdown, DOCX, XLSX, PDF text). The size, hash and facts below are compared."));
        }
        if ((l.SizeBytes ?? 0) > TextExtractor.MaxFileBytes || (r.SizeBytes ?? 0) > TextExtractor.MaxFileBytes)
            return Outcome<DocumentComparison>.Ok(Facts(CompareModes.Metadata, $"Content not comparable: a file is larger than {TextExtractor.MaxFileBytes / 1024 / 1024} MB, too large to compare word by word."));

        ExtractedText lt, rt;
        await CompareSlots.WaitAsync(ct);
        try
        {
            var lb = await vault.ReadAllAsync(l.Sha256!, l.SizeBytes ?? 0, ct);
            var rb = await vault.ReadAllAsync(r.Sha256!, r.SizeBytes ?? 0, ct);
            lt = await Task.Run(() => TextExtractor.Extract(l.FileName!, l.MediaType, lb), ct);
            rt = await Task.Run(() => TextExtractor.Extract(r.FileName!, r.MediaType, rb), ct);
        }
        finally
        {
            CompareSlots.Release();
        }
        if (!lt.IsText || !rt.IsText)
        {
            var reason = string.Join(" ", new[] { lt.IsText ? null : $"{p.Left.Head.Number} {l.Label}: {lt.Reason}", rt.IsText ? null : $"{p.Right.Head.Number} {r.Label}: {rt.Reason}" }.Where(x => x is not null));
            return Outcome<DocumentComparison>.Ok(Facts(CompareModes.Metadata, "Content not comparable: " + reason + " The size, hash and facts below are compared."));
        }
        var diff = await Task.Run(() => WordDiff.Compare(lt.Text!, rt.Text!), ct);
        if (diff is null) return Outcome<DocumentComparison>.Ok(Facts(CompareModes.Metadata, "Content not comparable: the text is too long to compare word by word. The size, hash and facts below are compared."));

        Guid? reviewId = null;
        IReadOnlyList<RedlineThread> threads = [];
        if (includeThreads) (reviewId, threads) = await LoadThreadsAsync(subject, p, diff, ct);
        return Outcome<DocumentComparison>.Ok(new DocumentComparison(ToSide(p.Left, p.Banner, lt.Format), ToSide(p.Right, p.Banner, rt.Format), CompareModes.Text, sameDoc, false, null, p.Banner, p.Notices,
            diff, metadata, reviewId, threads, CanComment(subject)));
    }

    private static string FormatOf(string fileName, string? mediaType) => TextExtractor.FormatOf(fileName, mediaType);

    private static List<MetadataRow> MetadataRows(Prepared p)
    {
        var l = p.Left.Version;
        var r = p.Right.Version;
        MetadataRow Row(string field, string a, string b) => new(field, a, b, a == b);
        return
        [
            Row("Document", $"{p.Left.Head.Number} {p.Left.Name}", $"{p.Right.Head.Number} {p.Right.Name}"),
            Row("Version", l.Label, r.Label),
            Row("State", l.State, r.State),
            Row("File name", l.FileName!, r.FileName!),
            Row("Media type", l.MediaType ?? "", r.MediaType ?? ""),
            Row("Size", (l.SizeBytes ?? 0).ToString("N0", CultureInfo.InvariantCulture) + " bytes", (r.SizeBytes ?? 0).ToString("N0", CultureInfo.InvariantCulture) + " bytes"),
            Row("SHA-256", l.Sha256!, r.Sha256!),
            Row("Saved by", l.CreatedBy, r.CreatedBy),
            Row("Saved (UTC)", l.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), r.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
            Row("Change note", l.Comment ?? "", r.Comment ?? ""),
        ];
    }

    // ---------------------------------------------------------------- review threads

    /// <summary>Roles that may write review comments: a viewer or higher. Auditors and service accounts read only.</summary>
    public static bool CanComment(Subject s) => s.Roles.Overlaps([Roles.Viewer, Roles.Author, Roles.Approver, Roles.Admin]);

    private sealed record AnnotationRow(Guid Id, Guid? ParentId, int ChangeIndex, string ChangeKind, string Digest, string Body, string AuthorSub, string AuthorName, DateTimeOffset At,
        DateTimeOffset? ResolvedAt, string? ResolvedByName);

    private async Task<(Guid? ReviewId, IReadOnlyList<RedlineThread> Threads)> LoadThreadsAsync(Subject subject, Prepared p, TextDiff diff, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        Guid? reviewId = null;
        await using (var find = new NpgsqlCommand("SELECT id FROM rdl.review WHERE left_iteration_id = @l AND right_iteration_id = @r", conn))
        {
            find.Parameters.AddWithValue("l", p.Left.Version.IterationId);
            find.Parameters.AddWithValue("r", p.Right.Version.IterationId);
            if (await find.ExecuteScalarAsync(ct) is Guid g) reviewId = g;
        }
        if (reviewId is null) return (null, []);
        var rows = new List<AnnotationRow>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT id, parent_id, change_index, change_kind, change_digest, body, author_sub, author_name, created_at, resolved_at, resolved_by_name
            FROM rdl.annotation WHERE review_id = @r ORDER BY change_index, created_at, id
            """, conn))
        {
            cmd.Parameters.AddWithValue("r", reviewId.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                rows.Add(new AnnotationRow(rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetGuid(1), rd.GetInt32(2), rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetString(7),
                    rd.GetFieldValue<DateTimeOffset>(8), rd.IsDBNull(9) ? null : rd.GetFieldValue<DateTimeOffset>(9), rd.IsDBNull(10) ? null : rd.GetString(10)));
        }
        var mayResolveAny = AccessPolicy.Evaluate(subject, p.Right.Head.Markings, PlmAction.Modify).Permitted || AccessPolicy.Evaluate(subject, p.Right.Head.Markings, PlmAction.Sign).Permitted;
        var threads = new List<RedlineThread>();
        foreach (var root in rows.Where(x => x.ParentId is null))
        {
            var detached = root.ChangeIndex >= diff.Changes.Count || diff.Changes[root.ChangeIndex].Digest != root.Digest;
            var replies = rows.Where(x => x.ParentId == root.Id).Select(x => new RedlineReply(x.Id, x.AuthorName, x.At, x.Body)).ToList();
            threads.Add(new RedlineThread(root.Id, root.ChangeIndex, root.ChangeKind, detached, root.At, root.AuthorName, root.Body, root.ResolvedAt is not null, root.ResolvedAt, root.ResolvedByName, replies,
                CanComment(subject) && (mayResolveAny || root.AuthorSub == subject.Id)));
        }
        return (reviewId, threads);
    }

    private static string? ValidateRedlineBody(string? body, out string text)
    {
        text = (body ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (text.Length == 0) return "Write a comment.";
        if (text.Length > MaxRedlineCommentLength) return $"A comment may be at most {MaxRedlineCommentLength} characters.";
        if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\t'))) return "A comment may not contain control characters.";
        return null;
    }

    /// <summary>
    /// Adds a comment to one changed range of the redline of two versions, or a reply to a thread on it. The caller must be able to read and download both documents
    /// and be a viewer or higher; the first comment creates the shared review of that pair. The text stays in <c>rdl.annotation</c>; the audit events name the review and
    /// the comment, never the text. Watchers of the right-hand document are notified (a pointer to the review).
    /// </summary>
    public async Task<Outcome<PostedRedlineComment>> AddRedlineCommentAsync(Subject subject, CompareRef left, CompareRef right, int changeIndex, string body, Guid? replyTo, string? correlationId, CancellationToken ct = default)
    {
        if (ValidateRedlineBody(body, out var text) is { } problem) return Outcome<PostedRedlineComment>.Invalid([problem]);
        var prepared = await PrepareAsync(subject, left, right, RedlineCommentAction, correlationId, ct);
        if (!prepared.IsOk) return Remap<PostedRedlineComment, Prepared>(prepared);
        var p = prepared.Value!;
        if (!CanComment(subject) || !p.CanDownloadBoth)
        {
            var reason = !CanComment(subject) ? AccessPolicy.RoleMissing : "download.not_permitted";
            await AuditBothAsync(subject, p, RedlineCommentAction, AuditOutcome.Denied, correlationId, null, ("reasons", reason));
            return Outcome<PostedRedlineComment>.Forbidden([reason]);
        }
        Outcome<DocumentComparison> built;
        try { built = await BuildAsync(subject, p, includeThreads: false, ct); }
        catch (Exception e) when (e is VaultIntegrityException or VaultUnavailableException)
        {
            return Outcome<PostedRedlineComment>.Unavailable("The stored files could not be read and verified just now, so the comment was not saved.");
        }
        var diff = built.Value!.Diff;
        if (diff is null) return Outcome<PostedRedlineComment>.Invalid(["These versions are not compared word by word, so there is no changed range to comment on."]);
        if (changeIndex < 0 || changeIndex >= diff.Changes.Count) return Outcome<PostedRedlineComment>.Invalid(["That change does not exist in this comparison."]);
        var change = diff.Changes[changeIndex];

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var who = string.IsNullOrWhiteSpace(subject.DisplayName) ? subject.Username ?? subject.Id : subject.DisplayName;
        Guid reviewId;
        await using (var ins = new NpgsqlCommand(
            """
            INSERT INTO rdl.review (id, left_master_id, left_iteration_id, right_master_id, right_iteration_id, left_sha256, right_sha256, diff_version, extractor_version, created_at, created_by, created_by_name)
            VALUES (@id, @lm, @li, @rm, @ri, @ls, @rs, @dv, @ev, @now, @by, @name)
            ON CONFLICT (left_iteration_id, right_iteration_id) DO NOTHING
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("id", Guid.CreateVersion7());
            ins.Parameters.AddWithValue("lm", p.Left.Head.MasterId);
            ins.Parameters.AddWithValue("li", p.Left.Version.IterationId);
            ins.Parameters.AddWithValue("rm", p.Right.Head.MasterId);
            ins.Parameters.AddWithValue("ri", p.Right.Version.IterationId);
            ins.Parameters.AddWithValue("ls", p.Left.Version.Sha256!);
            ins.Parameters.AddWithValue("rs", p.Right.Version.Sha256!);
            ins.Parameters.AddWithValue("dv", WordDiff.DiffVersion);
            ins.Parameters.AddWithValue("ev", TextExtractor.ExtractorVersion);
            ins.Parameters.AddWithValue("now", now);
            ins.Parameters.AddWithValue("by", subject.Id);
            ins.Parameters.AddWithValue("name", who);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await using (var find = new NpgsqlCommand("SELECT id FROM rdl.review WHERE left_iteration_id = @l AND right_iteration_id = @r", conn, tx))
        {
            find.Parameters.AddWithValue("l", p.Left.Version.IterationId);
            find.Parameters.AddWithValue("r", p.Right.Version.IterationId);
            reviewId = (Guid)(await find.ExecuteScalarAsync(ct))!;
        }
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("k", "rdl-annotation:" + reviewId.ToString("D"));
            await lockCmd.ExecuteNonQueryAsync(ct);
        }
        await using (var limit = new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM rdl.annotation WHERE author_sub = @u AND created_at > @since), (SELECT count(*) FROM rdl.annotation WHERE review_id = @r)", conn, tx))
        {
            limit.Parameters.AddWithValue("u", subject.Id);
            limit.Parameters.AddWithValue("since", now - TimeSpan.FromMinutes(1));
            limit.Parameters.AddWithValue("r", reviewId);
            await using var rd = await limit.ExecuteReaderAsync(ct);
            await rd.ReadAsync(ct);
            var (recent, onReview) = (rd.GetInt64(0), rd.GetInt64(1));
            if (recent >= MaxRedlineCommentsPerMinute) return Outcome<PostedRedlineComment>.Conflict("You are commenting too quickly. Wait a moment and try again.");
            if (onReview >= MaxRedlineCommentsPerReview) return Outcome<PostedRedlineComment>.Conflict($"This review has reached its limit of {MaxRedlineCommentsPerReview} comments.");
        }
        string? parentAuthor = null;
        if (replyTo is { } parentId)
        {
            await using var cmd = new NpgsqlCommand("SELECT author_sub, parent_id IS NOT NULL, change_index, resolved_at IS NOT NULL FROM rdl.annotation WHERE id = @id AND review_id = @r", conn, tx);
            cmd.Parameters.AddWithValue("id", parentId);
            cmd.Parameters.AddWithValue("r", reviewId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Outcome<PostedRedlineComment>.Invalid(["The comment you are replying to does not exist in this review."]);
            if (rd.GetBoolean(1)) return Outcome<PostedRedlineComment>.Invalid(["Replies go one level deep: reply to the comment at the top of the thread."]);
            if (rd.GetInt32(2) != changeIndex) return Outcome<PostedRedlineComment>.Invalid(["That thread belongs to another change."]);
            if (rd.GetBoolean(3)) return Outcome<PostedRedlineComment>.Conflict("This thread is resolved. Reopen it to reply.");
            parentAuthor = rd.GetString(0);
        }
        var id = Guid.CreateVersion7();
        await using (var ins = new NpgsqlCommand(
            """
            INSERT INTO rdl.annotation (id, review_id, parent_id, change_index, change_kind, change_digest, body, author_sub, author_name, created_at)
            VALUES (@id, @r, @p, @ci, @ck, @cd, @b, @a, @an, @now)
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("r", reviewId);
            ins.Parameters.Add(new NpgsqlParameter("p", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)replyTo ?? DBNull.Value });
            ins.Parameters.AddWithValue("ci", changeIndex);
            ins.Parameters.AddWithValue("ck", change.Kind);
            ins.Parameters.AddWithValue("cd", change.Digest);
            ins.Parameters.AddWithValue("b", text);
            ins.Parameters.AddWithValue("a", subject.Id);
            ins.Parameters.AddWithValue("an", who);
            ins.Parameters.AddWithValue("now", now);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await AuditBothAsync(subject, p, RedlineCommentAction, AuditOutcome.Success, correlationId, tx,
            ("review", reviewId.ToString("D")), ("comment", id.ToString("D")), ("parent", replyTo?.ToString("D")), ("change", (changeIndex + 1).ToString(CultureInfo.InvariantCulture)),
            ("length", text.Length.ToString(CultureInfo.InvariantCulture)), ("markings", p.Banner));
        if (notifications is not null)
        {
            if (parentAuthor is not null && parentAuthor != subject.Id)
                await notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.RedlineComment, AwarenessKinds.Document, p.Right.Head.Number, p.Right.Head.Markings, subject.Id, parentAuthor, reviewId), ct);
            await notifications.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.RedlineComment, AwarenessKinds.Document, p.Right.Head.Number, p.Right.Head.Markings, subject.Id, null, reviewId), ct);
            await notifications.AutoWatchAsync(conn, tx, subject.Id, AwarenessKinds.Document, p.Right.Head.Number, ct);
        }
        await tx.CommitAsync(ct);
        return Outcome<PostedRedlineComment>.Ok(new PostedRedlineComment(id, reviewId, replyTo is not null));
    }

    /// <summary>
    /// Resolves or reopens a comment thread. The caller must be able to read and download both documents of the review, and be the author of the thread or able to modify
    /// or sign the right-hand document (the people who act on review findings). Audited on both documents.
    /// </summary>
    public async Task<Outcome<bool>> SetRedlineThreadResolvedAsync(Subject subject, Guid threadId, bool resolved, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid reviewId, leftId, rightId;
        string authorSub;
        bool isRoot, isResolved;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT a.review_id, rv.left_master_id, rv.right_master_id, a.author_sub, a.parent_id IS NULL, a.resolved_at IS NOT NULL
            FROM rdl.annotation a JOIN rdl.review rv ON rv.id = a.review_id WHERE a.id = @id FOR UPDATE OF a
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", threadId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Outcome<bool>.NotFound();
            (reviewId, leftId, rightId, authorSub, isRoot, isResolved) = (rd.GetGuid(0), rd.GetGuid(1), rd.GetGuid(2), rd.GetString(3), rd.GetBoolean(4), rd.GetBoolean(5));
        }
        var lh = await LoadHeadByIdAsync(conn, tx, leftId, ct);
        var rh = leftId == rightId ? lh : await LoadHeadByIdAsync(conn, tx, rightId, ct);
        if (lh is null || rh is null) return Outcome<bool>.NotFound();
        var heads = leftId == rightId ? new[] { lh } : new[] { lh, rh };
        var action = RedlineResolveAction;
        var allowed = heads.All(h => AccessPolicy.Evaluate(subject, h.Markings, PlmAction.Read).Permitted && AccessPolicy.Evaluate(subject, h.Markings, PlmAction.Download).Permitted);
        if (!allowed)
        {
            foreach (var h in heads) await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, h.Number, correlationId, ("reasons", "object.not_readable"), ("review", reviewId.ToString("D"))), ct);
            await tx.CommitAsync(ct);
            return Outcome<bool>.NotFound();
        }
        if (!isRoot) return Outcome<bool>.Invalid(["Only a whole thread is resolved or reopened, not a reply."]);
        var mayAct = CanComment(subject) && (authorSub == subject.Id || AccessPolicy.Evaluate(subject, rh.Markings, PlmAction.Modify).Permitted || AccessPolicy.Evaluate(subject, rh.Markings, PlmAction.Sign).Permitted);
        if (!mayAct)
        {
            foreach (var h in heads) await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, h.Number, correlationId, ("reasons", "redline.not_author_or_reviewer"), ("review", reviewId.ToString("D"))), ct);
            await tx.CommitAsync(ct);
            return Outcome<bool>.Forbidden(["redline.not_author_or_reviewer"]);
        }
        if (isResolved == resolved) return Outcome<bool>.Conflict(resolved ? "This thread is already resolved." : "This thread is not resolved.");
        var who = string.IsNullOrWhiteSpace(subject.DisplayName) ? subject.Username ?? subject.Id : subject.DisplayName;
        await using (var up = new NpgsqlCommand(
            "UPDATE rdl.annotation SET resolved_at = @at, resolved_by = @by, resolved_by_name = @name WHERE id = @id", conn, tx))
        {
            up.Parameters.AddWithValue("id", threadId);
            up.Parameters.Add(new NpgsqlParameter("at", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = resolved ? clock.GetUtcNow() : DBNull.Value });
            up.Parameters.Add(new NpgsqlParameter("by", NpgsqlTypes.NpgsqlDbType.Text) { Value = resolved ? subject.Id : DBNull.Value });
            up.Parameters.Add(new NpgsqlParameter("name", NpgsqlTypes.NpgsqlDbType.Text) { Value = resolved ? who : DBNull.Value });
            await up.ExecuteNonQueryAsync(ct);
        }
        foreach (var h in heads)
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Success, h.Number, correlationId,
                ("review", reviewId.ToString("D")), ("thread", threadId.ToString("D")), ("state", resolved ? "resolved" : "reopened")), ct);
        await tx.CommitAsync(ct);
        return Outcome<bool>.Ok(true);
    }

    private static async Task<Head?> LoadHeadByIdAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid masterId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT m.number FROM plm.document_master m WHERE m.id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", masterId);
        return await cmd.ExecuteScalarAsync(ct) is string number ? await LoadHeadAsync(conn, tx, number, forUpdate: false, ct) : null;
    }

    // ---------------------------------------------------------------- reviews: list and pointer

    /// <summary>
    /// The redline reviews that involve a document, newest first, with how many comment threads are open. Only reviews whose BOTH documents the caller may read and download
    /// are returned (the read filter is applied to each master), so a review of a document the caller cannot see does not exist for them, not even as a count.
    /// </summary>
    public async Task<IReadOnlyList<RedlineReviewSummary>> ListRedlineReviewsAsync(Subject subject, string number, CancellationToken ct = default)
    {
        if (!AccessPolicy.HasRoleFor(subject, PlmAction.Download)) return [];
        var leftFilter = AccessPolicy.BuildReadFilter(subject, "lm");
        var rightFilter = AccessPolicy.BuildReadFilter(subject, "rm");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT rv.id, lm.number, lm.name, lr.revision, li.iteration, rm.number, rm.name, rr.revision, ri.iteration, rv.created_at, rv.created_by_name,
                   (SELECT count(*) FROM rdl.annotation a WHERE a.review_id = rv.id AND a.parent_id IS NULL AND a.resolved_at IS NULL),
                   (SELECT count(*) FROM rdl.annotation a WHERE a.review_id = rv.id AND a.parent_id IS NULL),
                   {MarkingColumns.Select("lm")}, {MarkingColumns.Select("rm")}
            FROM rdl.review rv
            JOIN plm.document_master lm ON lm.id = rv.left_master_id
            JOIN plm.document_master rm ON rm.id = rv.right_master_id
            JOIN plm.document_iteration li ON li.id = rv.left_iteration_id
            JOIN plm.document_revision lr ON lr.id = li.revision_id
            JOIN plm.document_iteration ri ON ri.id = rv.right_iteration_id
            JOIN plm.document_revision rr ON rr.id = ri.revision_id
            WHERE (lm.number = @n OR rm.number = @n) AND {leftFilter.Sql} AND {rightFilter.Sql}
            ORDER BY rv.created_at DESC, rv.id DESC LIMIT 100
            """, conn);
        cmd.Parameters.AddWithValue("n", number);
        foreach (var prm in leftFilter.Parameters.Concat(rightFilter.Parameters)) cmd.Parameters.AddWithValue(prm.Key, prm.Value);
        var list = new List<RedlineReviewSummary>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new RedlineReviewSummary(rd.GetGuid(0), rd.GetString(1), rd.GetString(2), $"{rd.GetString(3)}.{rd.GetInt32(4).ToString(CultureInfo.InvariantCulture)}", rd.GetString(5), rd.GetString(6),
                $"{rd.GetString(7)}.{rd.GetInt32(8).ToString(CultureInfo.InvariantCulture)}", (int)rd.GetInt64(11), (int)rd.GetInt64(12), rd.GetFieldValue<DateTimeOffset>(9), rd.GetString(10),
                AggregateBanner([MarkingColumns.Read(rd, 13), MarkingColumns.Read(rd, 13 + MarkingColumns.Count)])));
        return list;
    }

    /// <summary>The versions of one document that can be compared, for choosing a side. Needs Read (404 otherwise, denial audited); a view, audited as <c>document.compare_versions</c>.</summary>
    public async Task<Outcome<CompareVersionList>> ListCompareVersionsAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var head = await LoadHeadAsync(conn, tx, number.Trim(), forUpdate: false, ct);
        if (head is null) return Outcome<CompareVersionList>.NotFound();
        var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, "document.compare_versions", AuditOutcome.Denied, head.Number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<CompareVersionList>.NotFound();
        }
        if (await IsDisposedAsync(conn, tx, head.MasterId, ct))
        {
            await tx.CommitAsync(ct);
            return Outcome<CompareVersionList>.Conflict("The content of this document was disposed of under a retention schedule.");
        }
        var records = await LoadVersionRecordsAsync(conn, tx, head.MasterId, ct);
        var latest = Pick(records, null);
        await audit.AppendAsync(tx, Entry(subject, "document.compare_versions", AuditOutcome.Success, head.Number, correlationId, ("versions", records.Count.ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        var options = records.Where(v => v.FileId is not null).Select(v => new CompareVersionOption(v.IterationId, v.Label, v.State, latest is not null && v.IterationId == latest.IterationId, v.CreatedAt, v.CreatedBy, v.FileName!, v.SizeBytes ?? 0)).ToList();
        return Outcome<CompareVersionList>.Ok(new CompareVersionList(head.Number, await NameOfAsync(head.MasterId, ct), head.Markings.BannerText(), options));
    }

    /// <summary>Resolves a review pointer (from a notification or a link) to the two versions it compares, if the caller may read and download both documents. Otherwise 404, audited.</summary>
    public async Task<Outcome<RedlineReviewTarget>> ResolveRedlineReviewAsync(Subject subject, Guid reviewId, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid lm, li, rm, ri;
        await using (var cmd = new NpgsqlCommand("SELECT left_master_id, left_iteration_id, right_master_id, right_iteration_id FROM rdl.review WHERE id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", reviewId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Outcome<RedlineReviewTarget>.NotFound();
            (lm, li, rm, ri) = (rd.GetGuid(0), rd.GetGuid(1), rd.GetGuid(2), rd.GetGuid(3));
        }
        var lh = await LoadHeadByIdAsync(conn, tx, lm, ct);
        var rh = lm == rm ? lh : await LoadHeadByIdAsync(conn, tx, rm, ct);
        if (lh is null || rh is null) return Outcome<RedlineReviewTarget>.NotFound();
        var heads = lm == rm ? new[] { lh } : new[] { lh, rh };
        var allowed = heads.All(h => AccessPolicy.Evaluate(subject, h.Markings, PlmAction.Read).Permitted && AccessPolicy.Evaluate(subject, h.Markings, PlmAction.Download).Permitted);
        if (!allowed)
        {
            foreach (var h in heads) await audit.AppendAsync(tx, Entry(subject, CompareAction, AuditOutcome.Denied, h.Number, correlationId, ("reasons", "object.not_readable"), ("review", reviewId.ToString("D"))), ct);
            await tx.CommitAsync(ct);
            return Outcome<RedlineReviewTarget>.NotFound();
        }
        await tx.CommitAsync(ct);
        return Outcome<RedlineReviewTarget>.Ok(new RedlineReviewTarget(reviewId, new CompareRef(lh.Number, li), new CompareRef(rh.Number, ri)));
    }

    /// <summary>How many redline comment threads are still open on the reviews of a document that the caller may read (a count over what they can see only).</summary>
    public async Task<int> CountOpenRedlineThreadsAsync(Subject subject, string number, CancellationToken ct = default) =>
        (await ListRedlineReviewsAsync(subject, number, ct)).Sum(r => r.OpenThreads);

    // ---------------------------------------------------------------- export

    /// <summary>
    /// Exports the comparison as a report (<paramref name="format"/> "html" or "pdf") under the aggregate banner of both documents, with every notice either needs, the summary
    /// counts, the redline, the change list and the review threads. Same authorization as the comparison; audited as <c>document.redline_export</c> on both documents.
    /// </summary>
    public async Task<Outcome<RedlineExport>> ExportRedlineAsync(Subject subject, CompareRef left, CompareRef right, string format, string? correlationId, CancellationToken ct = default)
    {
        format = (format ?? "").Trim().ToLowerInvariant();
        if (format is not ("html" or "pdf")) return Outcome<RedlineExport>.Invalid(["The format must be html or pdf."]);
        var prepared = await PrepareAsync(subject, left, right, RedlineExportAction, correlationId, ct);
        if (!prepared.IsOk) return Remap<RedlineExport, Prepared>(prepared);
        var p = prepared.Value!;
        Outcome<DocumentComparison> built;
        try { built = await BuildAsync(subject, p, includeThreads: true, ct); }
        catch (Exception e) when (e is VaultIntegrityException or VaultUnavailableException)
        {
            await AuditBothAsync(subject, p, RedlineExportAction, AuditOutcome.Failure, correlationId, null, ("format", format), ("error", e is VaultIntegrityException ? "integrity" : "vault_unavailable"));
            return Outcome<RedlineExport>.Unavailable("The stored files could not be read and verified just now, so no report was made.");
        }
        if (!built.IsOk) return Remap<RedlineExport, DocumentComparison>(built);
        var c = built.Value!;
        var now = clock.GetUtcNow();
        var identity = new RedlineIdentity(DisplayId(subject), now);
        byte[] bytes;
        string contentType;
        if (format == "html") { bytes = RedlineReport.Html(c, identity); contentType = "text/html; charset=utf-8"; }
        else { EmbeddedFontResolver.EnsureInstalled(); bytes = RedlineReport.Pdf(c, identity); contentType = "application/pdf"; }
        var fileName = $"redline-{RedlineReport.SafeName(c.Left.Number)}-{RedlineReport.SafeName(c.Left.Label)}-vs-{RedlineReport.SafeName(c.Right.Number)}-{RedlineReport.SafeName(c.Right.Label)}.{format}";
        await AuditBothAsync(subject, p, RedlineExportAction, AuditOutcome.Success, correlationId, null,
            ("format", format), ("mode", c.Mode), ("bytes", bytes.Length.ToString(CultureInfo.InvariantCulture)), ("changes", c.Diff?.Counts.Changes.ToString(CultureInfo.InvariantCulture)), ("markings", p.Banner));
        return Outcome<RedlineExport>.Ok(new RedlineExport(fileName, contentType, p.Banner, bytes));
    }
}
