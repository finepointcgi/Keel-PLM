using System.Globalization;
using System.Text;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Sourcing;

/// <summary>
/// The supplier portal (ADR-0037): what an external supplier's user can do. EVERY method starts by resolving the caller's own supplier from the portal-user link of
/// the caller's identity; no parameter, claim or header ever names a supplier, and every query below carries that supplier id, so one supplier cannot reach another
/// supplier's requests, responses or files, and no internal object is reachable at all (the portal reads only its own request rows and, through them, the parts
/// that the supplier's clearance profile allows). Data is released through the same outbound check as the integrations: profile, distribution statement, export
/// control; and a foreign supplier never receives an ITAR-marked part. Responses are immutable; their files go through the vault, which scans them.
/// </summary>
public sealed class PortalService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, IContentStore? store = null, IContentReader? reader = null)
{
    public const int MaxFiles = 5;
    public const long MaxFileBytes = 25L * 1024 * 1024;

    private static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string? number, string? corr, params (string, string?)[] details) =>
        SourcingStore.Entry(s, action, outcome, "request", number, corr, details);

    private sealed record Me(Guid SupplierId, SupplierRow Supplier, string Sub, string DisplayName);

    /// <summary>The caller's supplier, or null. Needs the supplier role, an active link and a supplier that is not disapproved or obsolete.</summary>
    private static async Task<(Me? Me, string? Reason)> ResolveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, CancellationToken ct)
    {
        if (!subject.Roles.Contains(Roles.Supplier)) return (null, AccessPolicy.RoleMissing);
        // A person with an internal role is never a portal user, even if both roles are present at the IdP.
        if (subject.Roles.Any(r => Roles.All.Contains(r))) return (null, AccessPolicy.RoleMissing);
        Guid? supplierId;
        await using (var cmd = new NpgsqlCommand("SELECT supplier_id FROM src.portal_user WHERE user_sub = @u AND active", conn, tx))
        {
            cmd.Parameters.AddWithValue("u", subject.Id);
            supplierId = (Guid?)await cmd.ExecuteScalarAsync(ct);
        }
        if (supplierId is null) return (null, "portal.not_linked");
        var supplier = await SourcingStore.LoadSupplierByIdAsync(conn, tx, supplierId.Value, false, ct);
        if (supplier is null || supplier.Status is SupplierStatuses.Obsolete or SupplierStatuses.Disapproved) return (null, SourcingRules.SupplierNotActive);
        return (new Me(supplier.Id, supplier, subject.Id, subject.DisplayName), null);
    }

    private async Task<Outcome<T>> RefuseAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, Subject subject, string action, string? corr, string reason, CancellationToken ct)
    {
        await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, null, corr, ("reasons", reason)), ct);
        await tx.CommitAsync(ct);
        return Outcome<T>.Forbidden([reason]);
    }

    public async Task<Outcome<PortalProfile>> ProfileAsync(Subject subject, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (me, reason) = await ResolveAsync(conn, tx, subject, ct);
        if (me is null) return await RefuseAsync<PortalProfile>(conn, tx, subject, "portal.profile", correlationId, reason!, ct);
        await tx.CommitAsync(ct);
        return Outcome<PortalProfile>.Ok(new PortalProfile(me.Supplier.Number, me.Supplier.Name, subject.Username ?? subject.DisplayName));
    }

    // ---------------------------------------------------------------- requests

    public async Task<Outcome<IReadOnlyList<PortalRequestSummary>>> ListRequestsAsync(Subject subject, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (me, reason) = await ResolveAsync(conn, tx, subject, ct);
        if (me is null) return await RefuseAsync<IReadOnlyList<PortalRequestSummary>>(conn, tx, subject, "portal.list", correlationId, reason!, ct);
        var profile = await SourcingStore.LoadProfileAsync(conn, tx, me.Supplier.ClearanceProfile, ct);
        var list = new List<PortalRequestSummary>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {RequestRow.Select}, rr.state FROM src.request r JOIN src.request_recipient rr ON rr.request_id = r.id WHERE rr.supplier_id = @s AND r.state IN ('Issued', 'Closed') ORDER BY r.created_at DESC, r.number DESC", conn, tx))
        {
            cmd.Parameters.AddWithValue("s", me.SupplierId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var r = RequestRow.Read(rd);
                // Not releasable: the request does not exist as far as this supplier can tell.
                if (!SourcingRules.ReleaseToSupplier(me.Supplier.IsForeign, profile, r.Markings).Permitted) continue;
                list.Add(new PortalRequestSummary(r.Number, r.Kind, r.Title, r.State, r.DueOn, rd.GetString(9 + MarkingColumns.Count), r.Markings.BannerText()));
            }
        }
        await audit.AppendAsync(tx, Entry(subject, "portal.list", AuditOutcome.Success, null, correlationId, ("supplier", me.Supplier.Number), ("results", list.Count.ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return Outcome<IReadOnlyList<PortalRequestSummary>>.Ok(list);
    }

    public async Task<Outcome<PortalRequestDetail>> GetRequestAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (me, reason) = await ResolveAsync(conn, tx, subject, ct);
        if (me is null) return await RefuseAsync<PortalRequestDetail>(conn, tx, subject, "portal.request_read", correlationId, reason!, ct);
        var loaded = await LoadVisibleAsync(conn, tx, me, number, ct);
        if (loaded is null)
        {
            // Not addressed to this supplier, not issued, or not releasable: all look the same.
            await audit.AppendAsync(tx, Entry(subject, "portal.request_read", AuditOutcome.Denied, number, correlationId, ("reasons", "portal.not_found"), ("supplier", me.Supplier.Number)), ct);
            await tx.CommitAsync(ct);
            return Outcome<PortalRequestDetail>.NotFound();
        }
        var (r, myState, parts) = loaded.Value;
        await using (var viewed = new NpgsqlCommand("UPDATE src.request_recipient SET first_viewed_at = COALESCE(first_viewed_at, @now) WHERE request_id = @r AND supplier_id = @s", conn, tx))
        {
            viewed.Parameters.AddWithValue("now", clock.GetUtcNow());
            viewed.Parameters.AddWithValue("r", r.Id);
            viewed.Parameters.AddWithValue("s", me.SupplierId);
            await viewed.ExecuteNonQueryAsync(ct);
        }
        var responses = await MyResponsesAsync(conn, tx, r.Id, me.SupplierId, ct);
        var banner = Markings.AggregateBanner(new[] { r.Markings }.Concat(parts.Select(p => p.Markings)));
        await audit.AppendAsync(tx, Entry(subject, "portal.request_read", AuditOutcome.Success, number, correlationId, ("supplier", me.Supplier.Number)), ct);
        await tx.CommitAsync(ct);
        var summary = new PortalRequestSummary(r.Number, r.Kind, r.Title, r.State, r.DueOn, myState, banner);
        return Outcome<PortalRequestDetail>.Ok(new PortalRequestDetail(summary, r.Description,
            [.. parts.Select(p => new PortalPart(p.Number, p.Name, p.Quantity, p.Unit, p.Note))], responses, r.State == RequestStates.Issued));
    }

    /// <summary>The request as this supplier may see it, or null: addressed to it, issued or closed, releasable under its profile; with only the parts it may see.</summary>
    private static async Task<(RequestRow Request, string MyState, IReadOnlyList<RequestPartRow> Parts)?> LoadVisibleAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Me me, string number, CancellationToken ct)
    {
        RequestRow? r;
        string myState;
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {RequestRow.Select}, rr.state FROM src.request r JOIN src.request_recipient rr ON rr.request_id = r.id WHERE r.number = @n AND rr.supplier_id = @s AND r.state IN ('Issued', 'Closed')", conn, tx))
        {
            cmd.Parameters.AddWithValue("n", number);
            cmd.Parameters.AddWithValue("s", me.SupplierId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;
            r = RequestRow.Read(rd);
            myState = rd.GetString(9 + MarkingColumns.Count);
        }
        var profile = await SourcingStore.LoadProfileAsync(conn, tx, me.Supplier.ClearanceProfile, ct);
        if (!SourcingRules.ReleaseToSupplier(me.Supplier.IsForeign, profile, r.Markings).Permitted) return null;
        var parts = (await RequestService.PartsOfAsync(conn, tx, r.Id, ct)).Where(p => SourcingRules.ReleaseToSupplier(me.Supplier.IsForeign, profile, p.Markings).Permitted).ToList();
        return parts.Count == 0 ? null : (r, myState, parts);
    }

    private static async Task<IReadOnlyList<PortalResponse>> MyResponsesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid requestId, Guid supplierId, CancellationToken ct)
    {
        var raw = new List<(Guid Id, int Seq, bool Declined, string Message, string Quote, DateTimeOffset At, string By)>();
        await using (var cmd = new NpgsqlCommand("SELECT id, seq, declined, message, quote::text, submitted_at, submitted_by_name FROM src.response WHERE request_id = @r AND supplier_id = @s ORDER BY seq DESC", conn, tx))
        {
            cmd.Parameters.AddWithValue("r", requestId);
            cmd.Parameters.AddWithValue("s", supplierId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) raw.Add((rd.GetGuid(0), rd.GetInt32(1), rd.GetBoolean(2), rd.GetString(3), rd.GetString(4), rd.GetFieldValue<DateTimeOffset>(5), rd.GetString(6)));
        }
        var list = new List<PortalResponse>();
        foreach (var x in raw) list.Add(new PortalResponse(x.Id, x.Seq, x.Declined, x.Message, RequestService.ParseQuote(x.Quote), x.At, x.By, await RequestService.FilesAsync(conn, tx, x.Id, ct)));
        return list;
    }

    // ---------------------------------------------------------------- respond

    /// <summary>
    /// Submits a response (or a decline) with optional files. Files are streamed to the vault, which scans them: a file the scanner rejects (or cannot scan) refuses
    /// the whole response and nothing is recorded. A later response replaces the earlier ones for the buyer; the earlier ones stay in the history.
    /// </summary>
    public async Task<Outcome<PortalResponse>> RespondAsync(Subject subject, string number, PortalResponseInput input, IReadOnlyList<UploadedFile> files, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        RequestRow request;
        Me me;
        IReadOnlyList<RequestPartRow> parts;
        await using (var tx0 = await conn.BeginTransactionAsync(ct))
        {
            var (resolved, reason) = await ResolveAsync(conn, tx0, subject, ct);
            if (resolved is null) return await RefuseAsync<PortalResponse>(conn, tx0, subject, "portal.respond", correlationId, reason!, ct);
            me = resolved;
            var loaded = await LoadVisibleAsync(conn, tx0, me, number, ct);
            if (loaded is null)
            {
                await audit.AppendAsync(tx0, Entry(subject, "portal.respond", AuditOutcome.Denied, number, correlationId, ("reasons", "portal.not_found"), ("supplier", me.Supplier.Number)), ct);
                await tx0.CommitAsync(ct);
                return Outcome<PortalResponse>.NotFound();
            }
            (request, _, parts) = loaded.Value;
            await tx0.CommitAsync(ct);
        }
        if (request.State != RequestStates.Issued) return Outcome<PortalResponse>.Conflict("This request is closed; it takes no more responses.");
        var clean = input with { Message = input.Message?.Trim() ?? "", Quote = [.. input.Quote.Select(q => q with { Currency = SourcingRules.Clean(q.Currency)?.ToUpperInvariant() })] };
        var errors = SourcingRules.ValidateResponse(clean, [.. parts.Select(p => p.Number)]).ToList();
        if (files.Count > MaxFiles) errors.Add($"At most {MaxFiles} files per response.");
        foreach (var f in files)
            if (f.Kind is not ("response" or "certificate")) errors.Add("A file is a response file or a certificate.");
        if (input.Declined && files.Count > 0) errors.Add("A declined response carries no files.");
        if (errors.Count > 0) return Outcome<PortalResponse>.Invalid(errors);

        // Files first (the vault scans while it hashes). A rejected file stops everything before a row exists.
        var stored = new List<(UploadedFile File, ContentInfo Info, string Name)>();
        if (files.Count > 0)
        {
            if (store is null) return Outcome<PortalResponse>.Unavailable("File upload is not available.");
            foreach (var f in files)
            {
                var name = SafeName(f.FileName);
                try
                {
                    stored.Add((f, await store.PutAsync(new LimitedStream(f.Content, MaxFileBytes), ct), name));
                }
                catch (ContentRejectedException e)
                {
                    await audit.AppendAsync(Entry(subject, "portal.upload_refused", AuditOutcome.Denied, number, correlationId, ("supplier", me.Supplier.Number), ("reason", "scan")), ct);
                    return Outcome<PortalResponse>.Unprocessable("A file was not accepted: " + e.Message);
                }
                catch (FileTooLargeException)
                {
                    return Outcome<PortalResponse>.Unprocessable($"A file is larger than {MaxFileBytes / (1024 * 1024)} MB.");
                }
            }
        }

        await using var tx = await conn.BeginTransactionAsync(ct);
        // Serialize concurrent submissions of one supplier for one request, and re-check that it is still open.
        await using (var lockCmd = new NpgsqlCommand("SELECT state FROM src.request WHERE id = @r FOR SHARE", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("r", request.Id);
            if ((string?)await lockCmd.ExecuteScalarAsync(ct) != RequestStates.Issued) return Outcome<PortalResponse>.Conflict("This request is closed; it takes no more responses.");
        }
        await using (var lockRecipient = new NpgsqlCommand("SELECT 1 FROM src.request_recipient WHERE request_id = @r AND supplier_id = @s FOR UPDATE", conn, tx))
        {
            lockRecipient.Parameters.AddWithValue("r", request.Id);
            lockRecipient.Parameters.AddWithValue("s", me.SupplierId);
            await lockRecipient.ExecuteScalarAsync(ct);
        }
        int seq;
        await using (var next = new NpgsqlCommand("SELECT COALESCE(max(seq), 0) + 1 FROM src.response WHERE request_id = @r AND supplier_id = @s", conn, tx))
        {
            next.Parameters.AddWithValue("r", request.Id);
            next.Parameters.AddWithValue("s", me.SupplierId);
            seq = Convert.ToInt32(await next.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        var id = Guid.CreateVersion7();
        var now = clock.GetUtcNow();
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO src.response (id, request_id, supplier_id, seq, declined, message, quote, submitted_at, submitted_by_sub, submitted_by_name) VALUES (@id, @r, @s, @seq, @d, @m, @q::jsonb, @now, @sub, @name)", conn, tx))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("r", request.Id);
            ins.Parameters.AddWithValue("s", me.SupplierId);
            ins.Parameters.AddWithValue("seq", seq);
            ins.Parameters.AddWithValue("d", clean.Declined);
            ins.Parameters.AddWithValue("m", clean.Message);
            ins.Parameters.AddWithValue("q", RequestService.QuoteJson(clean.Quote));
            ins.Parameters.AddWithValue("now", now);
            ins.Parameters.AddWithValue("sub", subject.Id);
            ins.Parameters.AddWithValue("name", subject.DisplayName);
            await ins.ExecuteNonQueryAsync(ct);
        }
        foreach (var (file, info, name) in stored)
        {
            await using var fin = new NpgsqlCommand("INSERT INTO src.response_file (id, response_id, kind, file_name, sha256, size_bytes, uploaded_at) VALUES (@id, @r, @k, @n, @h, @z, @now)", conn, tx);
            fin.Parameters.AddWithValue("id", Guid.CreateVersion7());
            fin.Parameters.AddWithValue("r", id);
            fin.Parameters.AddWithValue("k", file.Kind);
            fin.Parameters.AddWithValue("n", name);
            fin.Parameters.AddWithValue("h", info.Sha256);
            fin.Parameters.AddWithValue("z", info.SizeBytes);
            fin.Parameters.AddWithValue("now", now);
            await fin.ExecuteNonQueryAsync(ct);
        }
        await using (var state = new NpgsqlCommand("UPDATE src.request_recipient SET state = @s WHERE request_id = @r AND supplier_id = @sup", conn, tx))
        {
            state.Parameters.AddWithValue("s", clean.Declined ? RecipientStates.Declined : RecipientStates.Responded);
            state.Parameters.AddWithValue("r", request.Id);
            state.Parameters.AddWithValue("sup", me.SupplierId);
            await state.ExecuteNonQueryAsync(ct);
        }
        // Counts and numbers only: the message, quote and file names stay out of the audit log.
        await audit.AppendAsync(tx, Entry(subject, "portal.respond", AuditOutcome.Success, number, correlationId,
            ("supplier", me.Supplier.Number), ("seq", seq.ToString(CultureInfo.InvariantCulture)), ("declined", clean.Declined ? "true" : "false"),
            ("quoteLines", clean.Quote.Count.ToString(CultureInfo.InvariantCulture)), ("files", stored.Count.ToString(CultureInfo.InvariantCulture))), ct);
        var view = (await MyResponsesAsync(conn, tx, request.Id, me.SupplierId, ct)).First(x => x.Id == id);
        await tx.CommitAsync(ct);
        return Outcome<PortalResponse>.Ok(view);
    }

    /// <summary>A supplier's own uploaded file (never another supplier's).</summary>
    public async Task<Outcome<(Stream Content, string FileName, long Size)>> OpenMyFileAsync(Subject subject, string requestNumber, Guid fileId, string? correlationId, CancellationToken ct = default)
    {
        if (reader is null) return Outcome<(Stream, string, long)>.Unavailable("File storage is not available.");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (me, reason) = await ResolveAsync(conn, tx, subject, ct);
        if (me is null) return await RefuseAsync<(Stream, string, long)>(conn, tx, subject, "portal.file_download", correlationId, reason!, ct);
        string? sha = null, name = null; long size = 0;
        await using (var cmd = new NpgsqlCommand(
            "SELECT f.sha256, f.file_name, f.size_bytes FROM src.response_file f JOIN src.response p ON p.id = f.response_id JOIN src.request r ON r.id = p.request_id WHERE f.id = @f AND r.number = @n AND p.supplier_id = @s", conn, tx))
        {
            cmd.Parameters.AddWithValue("f", fileId);
            cmd.Parameters.AddWithValue("n", requestNumber);
            cmd.Parameters.AddWithValue("s", me.SupplierId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct)) { sha = rd.GetString(0); name = rd.GetString(1); size = rd.GetInt64(2); }
        }
        if (sha is null)
        {
            await audit.AppendAsync(tx, Entry(subject, "portal.file_download", AuditOutcome.Denied, requestNumber, correlationId, ("reasons", "portal.not_found"), ("supplier", me.Supplier.Number)), ct);
            await tx.CommitAsync(ct);
            return Outcome<(Stream, string, long)>.NotFound();
        }
        await audit.AppendAsync(tx, Entry(subject, "portal.file_download", AuditOutcome.Success, requestNumber, correlationId, ("supplier", me.Supplier.Number)), ct);
        await tx.CommitAsync(ct);
        return Outcome<(Stream, string, long)>.Ok((await reader.OpenReadAsync(sha, size, ct), name!, size));
    }

    /// <summary>The file name without a path or control characters, at most 200 characters; never empty.</summary>
    public static string SafeName(string name)
    {
        var leaf = name.Replace('\\', '/').Split('/').Last();
        var sb = new StringBuilder();
        foreach (var c in leaf) if (!char.IsControl(c) && c is not ('<' or '>' or '"' or ':' or '|' or '?' or '*')) sb.Append(c);
        var cleaned = sb.ToString().Trim().TrimStart('.');
        if (cleaned.Length > 200) cleaned = cleaned[^200..];
        return cleaned.Length == 0 ? "file" : cleaned;
    }

    private sealed class FileTooLargeException : Exception;

    /// <summary>Counts bytes while the vault reads them and stops the upload at the limit.</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Count(await inner.ReadAsync(buffer, cancellationToken));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        private int Count(int n)
        {
            _read += n;
            return _read > limit ? throw new FileTooLargeException() : n;
        }
    }
}
