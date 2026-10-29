using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Cad;

/// <summary>
/// CAD package check-out and check-in (ADR-0035). A package is a set of ordinary documents. The exclusive lock is the document check-out of
/// every member, taken all-or-nothing; check-in creates one iteration of every member in one transaction together with the dependency rows.
/// Bytes, SHA-256, malware scanning, markings and audit are the document module's, reached through <see cref="ICadDocumentPort"/>.
/// </summary>
public sealed class CadPackageService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, ICadDocumentPort documents)
{
    private sealed record SetRow(Guid Id, string UserSub, string UserName, string TopNumber, DateTimeOffset CreatedAt, bool Open);
    private sealed record MemberRow(Guid MasterId, string Number, string Role, bool IsNew, bool Done);

    // ---------------------------------------------------------------- check-out

    public async Task<Outcome<PackageCheckout>> CheckOutAsync(Subject subject, string topNumber, PackageOptions? options, string? correlationId, CancellationToken ct = default)
    {
        options ??= new PackageOptions();
        if (options.Roles is { } roles && roles.Any(r => !CadRoles.All.Contains(r))) return Outcome<PackageCheckout>.Invalid([$"Roles must be among: {string.Join(", ", CadRoles.All)}."]);
        var top = await documents.GetAsync(subject, topNumber, correlationId, ct);
        if (!top.IsOk) return new Outcome<PackageCheckout>(top.Kind, null, top.Errors);

        List<(Guid Id, string Number, string Role)> members; int redacted;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        {
            var topRole = await RoleOfAsync(conn, top.Value!.MasterId, top.Value.DocumentType, ct);
            (members, redacted) = await CadGraphService.ResolvePackageAsync(conn, subject, top.Value.MasterId, topNumber, topRole, top.Value.DocumentType, options, ct);
        }
        if (redacted > 0 && !options.AllowPartial)
            return Outcome<PackageCheckout>.Conflict($"{CadErrors.PackageIncomplete}: {redacted} referenced document(s) are not visible to you. Check out with allowPartial to take the rest.");
        if (members.Count > CadGraphService.MaxPackageMembers) return Outcome<PackageCheckout>.Invalid([$"A package can hold at most {CadGraphService.MaxPackageMembers} documents."]);

        var setId = Guid.CreateVersion7();
        var now = clock.GetUtcNow();
        var infos = await documents.CheckOutSetAsync(subject, [.. members.Select(m => m.Number)], correlationId, async (c, t, token) =>
        {
            var conn = (NpgsqlConnection)c; var tx = (NpgsqlTransaction)t;
            await using (var cmd = new NpgsqlCommand(
                "INSERT INTO cad.checkout_set (id, user_sub, user_name, top_master_id, top_number, created_at) VALUES (@id, @sub, @name, @top, @num, @at)", conn, tx))
            {
                cmd.Parameters.AddWithValue("id", setId);
                cmd.Parameters.AddWithValue("sub", subject.Id);
                cmd.Parameters.AddWithValue("name", subject.Username ?? subject.Id);
                cmd.Parameters.AddWithValue("top", top.Value!.MasterId);
                cmd.Parameters.AddWithValue("num", topNumber);
                cmd.Parameters.AddWithValue("at", now);
                await cmd.ExecuteNonQueryAsync(token);
            }
            foreach (var m in members)
            {
                await using var ins = new NpgsqlCommand(
                    "INSERT INTO cad.checkout_member (set_id, document_master_id, document_number, role, is_new, base_revision, base_iteration) VALUES (@s, @d, @n, @r, false, 'A', 1)", conn, tx);
                ins.Parameters.AddWithValue("s", setId);
                ins.Parameters.AddWithValue("d", m.Id);
                ins.Parameters.AddWithValue("n", m.Number);
                ins.Parameters.AddWithValue("r", m.Role);
                await ins.ExecuteNonQueryAsync(token);
            }
            // The base revision and iteration are what the member looked like when it was taken (the working copy starts from them).
            await using var upd = new NpgsqlCommand(
                """
                UPDATE cad.checkout_member cm SET base_revision = r.revision, base_iteration = r.latest_iteration
                FROM plm.document_revision r WHERE cm.set_id = @s AND r.master_id = cm.document_master_id AND r.is_latest
                """, conn, tx);
            upd.Parameters.AddWithValue("s", setId);
            await upd.ExecuteNonQueryAsync(token);
        }, ct);
        if (!infos.IsOk) return Mapped<PackageCheckout>(infos, CadErrors.Locked);

        var references = await ReferencesAmongAsync(subject, members.Select(m => m.Id).ToArray(), ct);
        var manifest = BuildManifest(setId, topNumber, now, true, infos.Value!, members, references, [], redacted);
        await audit.AppendAsync(CadGraphService.Entry(subject, "cad.package.checkout", AuditOutcome.Success, topNumber, correlationId,
            ("set", setId.ToString("D")), ("members", members.Count.ToString(CultureInfo.InvariantCulture)), ("redacted", redacted.ToString(CultureInfo.InvariantCulture)),
            ("documents", string.Join(",", members.Select(m => m.Number)))), ct);
        return Outcome<PackageCheckout>.Ok(manifest);
    }

    public async Task<Outcome<PackageCheckout>> GetAsync(Subject subject, Guid id, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var set = await LoadSetAsync(conn, null, subject, id, ct);
        if (set is null) return Outcome<PackageCheckout>.NotFound();
        var members = await LoadMembersAsync(conn, null, id, ct);
        var infos = new List<CadDocumentInfo>();
        foreach (var m in members.Where(m => !m.Done))
        {
            var info = await documents.GetAsync(subject, m.Number, correlationId, ct);
            if (!info.IsOk) return new Outcome<PackageCheckout>(info.Kind, null, info.Errors);
            infos.Add(info.Value!);
        }
        var references = await ReferencesAmongAsync(subject, members.Select(m => m.MasterId).ToArray(), ct);
        return Outcome<PackageCheckout>.Ok(BuildManifest(id, set.TopNumber, set.CreatedAt, set.Open, infos,
            [.. members.Where(m => !m.Done).Select(m => (m.MasterId, m.Number, m.Role))], references, [.. members.Where(m => m.IsNew).Select(m => m.Number)], 0));
    }

    /// <summary>The open sets of the caller (a connector that lost its folder can find its check-outs again).</summary>
    public async Task<IReadOnlyList<(Guid Id, string Top, DateTimeOffset CreatedAt)>> ListOpenAsync(Subject subject, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id, top_number, created_at FROM cad.checkout_set WHERE user_sub = @u AND closed_at IS NULL ORDER BY created_at DESC LIMIT 100", conn);
        cmd.Parameters.AddWithValue("u", subject.Id);
        var list = new List<(Guid, string, DateTimeOffset)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetGuid(0), r.GetString(1), new DateTimeOffset(r.GetDateTime(2), TimeSpan.Zero)));
        return list;
    }

    // ---------------------------------------------------------------- new members, files

    /// <summary>Adds a new document (for example a drawing the CAD tool just created) to an open set. It takes the markings of the top document and is checked out to the caller.</summary>
    public async Task<Outcome<PackageMember>> AddMemberAsync(Subject subject, Guid setId, AddMemberRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (!CadRoles.All.Contains(request.CadRole)) return Outcome<PackageMember>.Invalid([$"Role must be one of: {string.Join(", ", CadRoles.All)}."]);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var set = await LoadSetAsync(conn, null, subject, setId, ct);
        if (set is null) return Outcome<PackageMember>.NotFound();
        if (!set.Open || set.UserSub != subject.Id) return Outcome<PackageMember>.Conflict($"{CadErrors.SetClosed}: the set is closed or belongs to someone else.");
        var top = await documents.GetAsync(subject, set.TopNumber, correlationId, ct);
        if (!top.IsOk) return new Outcome<PackageMember>(top.Kind, null, top.Errors);
        var created = await documents.CreateAsync(subject, request.Name, request.DocumentType, top.Value!.Markings, request.Description, correlationId, ct);
        if (!created.IsOk) return new Outcome<PackageMember>(created.Kind, null, created.Errors);
        var number = created.Value!.Number;
        var checkedOut = await documents.CheckOutSetAsync(subject, [number], correlationId, async (c, t, token) =>
        {
            var nc = (NpgsqlConnection)c; var nt = (NpgsqlTransaction)t;
            await using var ins = new NpgsqlCommand(
                "INSERT INTO cad.checkout_member (set_id, document_master_id, document_number, role, is_new, base_revision, base_iteration) VALUES (@s, @d, @n, @r, true, 'A', 1)", nc, nt);
            ins.Parameters.AddWithValue("s", setId);
            ins.Parameters.AddWithValue("d", created.Value.MasterId);
            ins.Parameters.AddWithValue("n", number);
            ins.Parameters.AddWithValue("r", request.CadRole);
            await ins.ExecuteNonQueryAsync(token);
            await using var reg = new NpgsqlCommand(
                "INSERT INTO cad.cad_document (document_master_id, role, registered_at, registered_by) VALUES (@d, @r, @at, @by) ON CONFLICT (document_master_id) DO NOTHING", nc, nt);
            reg.Parameters.AddWithValue("d", created.Value.MasterId);
            reg.Parameters.AddWithValue("r", request.CadRole);
            reg.Parameters.AddWithValue("at", clock.GetUtcNow());
            reg.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
            await reg.ExecuteNonQueryAsync(token);
        }, ct);
        if (!checkedOut.IsOk) return Mapped<PackageMember>(checkedOut, CadErrors.Locked);
        await audit.AppendAsync(CadGraphService.Entry(subject, "cad.package.member_add", AuditOutcome.Success, number, correlationId,
            ("set", setId.ToString("D")), ("role", request.CadRole), ("top", set.TopNumber)), ct);
        var info = checkedOut.Value![0];
        return Outcome<PackageMember>.Ok(new PackageMember(info.Number, info.Name, info.DocumentType, request.CadRole, info.Revision, info.Iteration, info.State, info.Banner, info.Files,
            request.References ?? [], false, true));
    }

    /// <summary>
    /// Streams one file into the working copy of a member. <paramref name="expectedSha256"/> (required) is the hash the client computed: the
    /// stream is hashed while it is read and a difference ends the upload before anything is recorded.
    /// </summary>
    public async Task<Outcome<CadFileInfo>> UploadAsync(Subject subject, Guid setId, string number, string role, string fileName, string? mediaType, string expectedSha256, Stream content,
        string? correlationId, CancellationToken ct = default)
    {
        if (!IsSha256(expectedSha256)) return Outcome<CadFileInfo>.Invalid(["A SHA-256 (64 hexadecimal characters) is required to upload a file."]);
        var gate = await MemberGateAsync<CadFileInfo>(subject, setId, number, ct);
        if (gate is not null) return gate;
        await using var verifying = new VerifyingStream(content, expectedSha256);
        Outcome<CadDocumentInfo> uploaded;
        try { uploaded = await documents.UploadAsync(subject, number, role, fileName, mediaType, verifying, correlationId, ct); }
        catch (CadIntegrityException)
        {
            await audit.AppendAsync(CadGraphService.Entry(subject, "cad.package.file_upload", AuditOutcome.Failure, number, correlationId, ("set", setId.ToString("D")), ("file", fileName), ("reason", CadErrors.IntegrityMismatch)), ct);
            return Outcome<CadFileInfo>.Unprocessable($"{CadErrors.IntegrityMismatch}: the bytes received do not match the SHA-256 given for {fileName}.");
        }
        if (!uploaded.IsOk) return new Outcome<CadFileInfo>(uploaded.Kind, null, uploaded.Errors);
        var file = uploaded.Value!.WorkingFiles?.FirstOrDefault(f => string.Equals(f.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        if (file is null || !string.Equals(file.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            return Outcome<CadFileInfo>.Unprocessable($"{CadErrors.IntegrityMismatch}: the stored file does not match the SHA-256 given for {fileName}.");
        await audit.AppendAsync(CadGraphService.Entry(subject, "cad.package.file_upload", AuditOutcome.Success, number, correlationId,
            ("set", setId.ToString("D")), ("file", fileName), ("sha256", file.Sha256), ("size", file.SizeBytes.ToString(CultureInfo.InvariantCulture))), ct);
        return Outcome<CadFileInfo>.Ok(file);
    }

    /// <summary>The marked download of a member's file (the unchanged-file ZIP). Only the owner of an open set, and only files of its members.</summary>
    public async Task<Outcome<CadDownload>> DownloadAsync(Subject subject, Guid setId, string number, string sha256, string? correlationId, CancellationToken ct = default)
    {
        if (!IsSha256(sha256)) return Outcome<CadDownload>.Invalid(["A SHA-256 (64 hexadecimal characters) is required."]);
        var gate = await MemberGateAsync<CadDownload>(subject, setId, number, ct);
        if (gate is not null) return gate;
        return await documents.DownloadLatestAsync(subject, number, sha256.ToLowerInvariant(), correlationId, ct);
    }

    // ---------------------------------------------------------------- check-in

    public async Task<Outcome<CheckInResult>> CheckInAsync(Subject subject, Guid setId, PackageCheckInRequest request, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var set = await LoadSetAsync(conn, null, subject, setId, ct);
        if (set is null) return Outcome<CheckInResult>.NotFound();
        if (!set.Open) return Outcome<CheckInResult>.Conflict($"{CadErrors.SetClosed}: the set is already closed.");
        if (set.UserSub != subject.Id) return Outcome<CheckInResult>.Forbidden(["checkout.not_owner"]);
        var open = (await LoadMembersAsync(conn, null, setId, ct)).Where(m => !m.Done).ToList();
        var requested = request.Members is { Count: > 0 } rm ? rm : [.. open.Select(m => new CheckInMemberRequest(m.Number))];
        if (requested.Select(m => m.Number).Distinct(StringComparer.Ordinal).Count() != requested.Count) return Outcome<CheckInResult>.Invalid(["A document appears twice in the check-in."]);
        var errors = requested.Where(r => open.All(m => m.Number != r.Number)).Select(r => $"{CadErrors.MemberUnknown}: {r.Number} is not an open member of this set.").ToList();
        if (errors.Count > 0) return Outcome<CheckInResult>.Invalid(errors);

        // Read every member's working copy, verify the hashes the client expects, and validate the declared references.
        var infos = new Dictionary<string, CadDocumentInfo>(StringComparer.Ordinal);
        foreach (var r in requested)
        {
            var info = await documents.GetAsync(subject, r.Number, correlationId, ct);
            if (!info.IsOk) return new Outcome<CheckInResult>(info.Kind, null, info.Errors);
            if (info.Value!.WorkingFiles is null) return Outcome<CheckInResult>.Conflict($"{CadErrors.SetClosed}: {r.Number} is not checked out to you.");
            foreach (var expected in r.Files ?? [])
            {
                var actual = info.Value.WorkingFiles.FirstOrDefault(f => string.Equals(f.FileName, expected.FileName, StringComparison.OrdinalIgnoreCase));
                if (actual is null || !string.Equals(actual.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    return Outcome<CheckInResult>.Unprocessable($"{CadErrors.IntegrityMismatch}: {r.Number} {expected.FileName} is not in the working copy with the expected SHA-256.");
            }
            infos[r.Number] = info.Value;
        }
        var memberMasters = open.ToDictionary(m => m.Number, m => m.MasterId, StringComparer.Ordinal);
        var declared = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (var r in requested.Where(r => r.References is not null))
        {
            var ids = new List<Guid>();
            foreach (var reference in r.References!.Distinct(StringComparer.Ordinal))
            {
                if (reference == r.Number) return Outcome<CheckInResult>.Invalid([$"{CadErrors.ReferenceUnknown}: {r.Number} cannot reference itself."]);
                var id = memberMasters.TryGetValue(reference, out var inSet) ? inSet : await ReadableMasterAsync(conn, subject, reference, ct);
                if (id is null) return Outcome<CheckInResult>.Invalid([$"{CadErrors.ReferenceUnknown}: {reference} was not found, or you are not authorized to see it."]);
                ids.Add(id.Value);
            }
            declared[r.Number] = ids;
        }

        // Members whose working copy equals the latest iteration (and whose references are not changed) need no new iteration when the client asks to skip them.
        var skipped = new List<string>();
        if (request.SkipUnchanged)
        {
            foreach (var r in requested)
            {
                var m = open.Single(o => o.Number == r.Number);
                var info = infos[r.Number];
                var sameFiles = info.WorkingFiles!.Select(f => f.Role + ":" + f.FileName + ":" + f.Sha256).Order().SequenceEqual(info.Files.Select(f => f.Role + ":" + f.FileName + ":" + f.Sha256).Order());
                var sameRefs = !declared.TryGetValue(r.Number, out var d) || await ReferencesUnchangedAsync(conn, m.MasterId, d, ct);
                if (!m.IsNew && sameFiles && sameRefs) skipped.Add(r.Number);
            }
        }
        var toCheckIn = requested.Where(r => !skipped.Contains(r.Number)).ToList();

        IReadOnlyList<CadCheckedIn> created = [];
        if (toCheckIn.Count > 0)
        {
            var result = await documents.CheckInSetAsync(subject, [.. toCheckIn.Select(r => new CadCheckInItem(r.Number, r.Comment ?? request.Comment))], correlationId,
                async (c, t, results, token) =>
                {
                    var nc = (NpgsqlConnection)c; var nt = (NpgsqlTransaction)t;
                    var byMaster = results.ToDictionary(x => x.MasterId);
                    foreach (var res in results)
                    {
                        var m = open.Single(o => o.Number == res.Number);
                        // A member never registered as CAD gets the default role of its document type so stale detection can see it.
                        await using (var reg = new NpgsqlCommand(
                            "INSERT INTO cad.cad_document (document_master_id, role, registered_at, registered_by) VALUES (@d, @r, @at, @by) ON CONFLICT (document_master_id) DO NOTHING", nc, nt))
                        {
                            reg.Parameters.AddWithValue("d", res.MasterId);
                            reg.Parameters.AddWithValue("r", m.Role);
                            reg.Parameters.AddWithValue("at", clock.GetUtcNow());
                            reg.Parameters.AddWithValue("by", subject.Username ?? subject.Id);
                            await reg.ExecuteNonQueryAsync(token);
                        }
                        if (declared.TryGetValue(res.Number, out var refs))
                        {
                            foreach (var child in refs)
                            {
                                // What the new iteration was built against: the new iteration of a member checked in with it, else the child's latest now.
                                string rev; int it;
                                if (byMaster.TryGetValue(child, out var sibling)) (rev, it) = (sibling.Revision, sibling.Iteration);
                                else (rev, it) = await LatestOfAsync(nc, nt, child, token);
                                await InsertDependencyAsync(nc, nt, res.IterationId, child, rev, it, token);
                            }
                        }
                        else
                        {
                            // Nothing declared: the edges of the previous iteration carry forward with the values they had (no silent refresh, so a stale drawing stays stale).
                            await using var copy = new NpgsqlCommand(
                                """
                                INSERT INTO cad.dependency (id, parent_iteration_id, child_master_id, seen_revision, seen_iteration, created_at)
                                SELECT gen_random_uuid(), @new, d.child_master_id, d.seen_revision, d.seen_iteration, @at
                                FROM cad.dependency d
                                JOIN plm.document_iteration pi ON pi.id = d.parent_iteration_id
                                JOIN plm.document_revision pr ON pr.id = pi.revision_id
                                WHERE pr.master_id = @m AND pr.revision = @rev AND pi.iteration = @prev
                                """, nc, nt);
                            copy.Parameters.AddWithValue("new", res.IterationId);
                            copy.Parameters.AddWithValue("at", clock.GetUtcNow());
                            copy.Parameters.AddWithValue("m", res.MasterId);
                            copy.Parameters.AddWithValue("rev", res.Revision);
                            copy.Parameters.AddWithValue("prev", res.PreviousIteration);
                            await copy.ExecuteNonQueryAsync(token);
                        }
                    }
                    await MarkDoneAsync(nc, nt, setId, results.Select(x => x.MasterId).ToArray(), token);
                    await CloseIfFinishedAsync(nc, nt, setId, "checked-in", token);
                }, ct);
            if (!result.IsOk) return Mapped<CheckInResult>(new Outcome<IReadOnlyList<CadCheckedIn>>(result.Kind, null, result.Errors), CadErrors.Locked);
            created = result.Value!;
        }

        var released = new List<string>();
        if (skipped.Count > 0)
        {
            var undone = await documents.UndoCheckOutSetAsync(subject, skipped, correlationId, ct);
            if (!undone.IsOk) return Mapped<CheckInResult>(new Outcome<IReadOnlyList<CadCheckedIn>>(undone.Kind, null, undone.Errors), CadErrors.Locked);
            released.AddRange(undone.Value!);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await MarkDoneAsync(conn, tx, setId, [.. skipped.Select(n => memberMasters[n])], ct);
            await CloseIfFinishedAsync(conn, tx, setId, "checked-in", ct);
            await tx.CommitAsync(ct);
        }
        var closed = await IsClosedAsync(conn, setId, ct);
        await audit.AppendAsync(CadGraphService.Entry(subject, "cad.package.checkin", AuditOutcome.Success, set.TopNumber, correlationId,
            ("set", setId.ToString("D")), ("created", created.Count.ToString(CultureInfo.InvariantCulture)), ("released", released.Count.ToString(CultureInfo.InvariantCulture)),
            ("documents", string.Join(",", created.Select(c => $"{c.Number}:{c.Revision}.{c.Iteration}")))), ct);
        return Outcome<CheckInResult>.Ok(new CheckInResult(setId, created, released, closed));
    }

    public async Task<Outcome<IReadOnlyList<string>>> UndoAsync(Subject subject, Guid setId, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var set = await LoadSetAsync(conn, null, subject, setId, ct);
        if (set is null) return Outcome<IReadOnlyList<string>>.NotFound();
        if (!set.Open) return Outcome<IReadOnlyList<string>>.Conflict($"{CadErrors.SetClosed}: the set is already closed.");
        var open = (await LoadMembersAsync(conn, null, setId, ct)).Where(m => !m.Done).ToList();
        var undone = await documents.UndoCheckOutSetAsync(subject, [.. open.Select(m => m.Number)], correlationId, ct);
        if (!undone.IsOk) return undone;
        await using var tx = await conn.BeginTransactionAsync(ct);
        await MarkDoneAsync(conn, tx, setId, [.. open.Select(m => m.MasterId)], ct);
        await CloseIfFinishedAsync(conn, tx, setId, "cancelled", ct);
        await tx.CommitAsync(ct);
        await audit.AppendAsync(CadGraphService.Entry(subject, "cad.package.undo", AuditOutcome.Success, set.TopNumber, correlationId,
            ("set", setId.ToString("D")), ("released", undone.Value!.Count.ToString(CultureInfo.InvariantCulture))), ct);
        return undone;
    }

    // ---------------------------------------------------------------- helpers

    private static PackageCheckout BuildManifest(Guid id, string top, DateTimeOffset createdAt, bool open, IReadOnlyList<CadDocumentInfo> infos,
        IReadOnlyList<(Guid Id, string Number, string Role)> members, IReadOnlyDictionary<Guid, List<string>> references, IReadOnlyCollection<string> newNumbers, int redacted)
    {
        var roles = members.ToDictionary(m => m.Number, m => m.Role, StringComparer.Ordinal);
        var list = infos.Select(i => new PackageMember(i.Number, i.Name, i.DocumentType, roles.GetValueOrDefault(i.Number, CadGraphService.DefaultRole(i.DocumentType)), i.Revision, i.Iteration,
            i.State, i.Banner, i.WorkingFiles ?? i.Files, references.GetValueOrDefault(i.MasterId) ?? [], i.Number == top, newNumbers.Contains(i.Number))).ToList();
        return new PackageCheckout(id, top, Markings.AggregateBanner(infos.Select(i => i.Markings)), createdAt, open, list, redacted);
    }

    private static Outcome<T> Mapped<T>(Outcome<IReadOnlyList<CadDocumentInfo>> o, string lockedCode) => MappedCore<T>(o.Kind, o.Errors, lockedCode);
    private static Outcome<T> Mapped<T>(Outcome<IReadOnlyList<CadCheckedIn>> o, string lockedCode) => MappedCore<T>(o.Kind, o.Errors, lockedCode);

    /// <summary>A refusal of the document module keeps its kind; a conflict (locked, already checked out) gets the protocol's stable code in front of the message.</summary>
    private static Outcome<T> MappedCore<T>(OutcomeKind kind, IReadOnlyList<string> errors, string lockedCode) => kind switch
    {
        OutcomeKind.Conflict => Outcome<T>.Conflict($"{lockedCode}: {errors[0]}"),
        _ => new Outcome<T>(kind, default, errors),
    };

    private async Task<Outcome<T>?> MemberGateAsync<T>(Subject subject, Guid setId, string number, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var set = await LoadSetAsync(conn, null, subject, setId, ct);
        if (set is null) return Outcome<T>.NotFound();
        if (!set.Open || set.UserSub != subject.Id) return Outcome<T>.Conflict($"{CadErrors.SetClosed}: the set is closed or belongs to someone else.");
        var member = (await LoadMembersAsync(conn, null, setId, ct)).FirstOrDefault(m => m.Number == number && !m.Done);
        return member is null ? Outcome<T>.Invalid([$"{CadErrors.MemberUnknown}: {number} is not an open member of this set."]) : null;
    }

    private static bool IsSha256(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);

    private static async Task<SetRow?> LoadSetAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Subject subject, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT id, user_sub, user_name, top_number, created_at, closed_at FROM cad.checkout_set WHERE id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var row = new SetRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), new DateTimeOffset(r.GetDateTime(4), TimeSpan.Zero), r.IsDBNull(5));
        // A set is visible to its owner and to administrators; to everybody else it does not exist.
        return row.UserSub == subject.Id || subject.Roles.Contains(Roles.Admin) ? row : null;
    }

    private static async Task<List<MemberRow>> LoadMembersAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid setId, CancellationToken ct)
    {
        var list = new List<MemberRow>();
        await using var cmd = new NpgsqlCommand("SELECT document_master_id, document_number, role, is_new, done_at IS NOT NULL FROM cad.checkout_member WHERE set_id = @s ORDER BY document_number", conn, tx);
        cmd.Parameters.AddWithValue("s", setId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new MemberRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetBoolean(4)));
        return list;
    }

    private static async Task<string> RoleOfAsync(NpgsqlConnection conn, Guid master, string documentType, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT role FROM cad.cad_document WHERE document_master_id = @id", conn);
        cmd.Parameters.AddWithValue("id", master);
        return await cmd.ExecuteScalarAsync(ct) as string ?? CadGraphService.DefaultRole(documentType);
    }

    /// <summary>Document numbers readable by the caller become ids; anything else is null (not found and not readable look the same).</summary>
    private static async Task<Guid?> ReadableMasterAsync(NpgsqlConnection conn, Subject subject, string number, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT m.id, {MarkingColumns.Select("m")} FROM plm.document_master m WHERE m.number = @n", conn);
        cmd.Parameters.AddWithValue("n", number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return AccessPolicy.Evaluate(subject, MarkingColumns.Read(r, 1), PlmAction.Read).Permitted ? r.GetGuid(0) : null;
    }

    private static async Task<(string Revision, int Iteration)> LatestOfAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid master, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT revision, latest_iteration FROM plm.document_revision WHERE master_id = @m AND is_latest", conn, tx);
        cmd.Parameters.AddWithValue("m", master);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetString(0), r.GetInt32(1));
    }

    private static async Task InsertDependencyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid parentIteration, Guid child, string revision, int iteration, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO cad.dependency (id, parent_iteration_id, child_master_id, seen_revision, seen_iteration, created_at) VALUES (@id, @p, @c, @r, @i, now()) ON CONFLICT DO NOTHING", conn, tx);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("p", parentIteration);
        cmd.Parameters.AddWithValue("c", child);
        cmd.Parameters.AddWithValue("r", revision);
        cmd.Parameters.AddWithValue("i", iteration);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The declared references equal the current ones AND are fresh: re-declaring a reference that is stale is a change (it asserts the document was rebuilt).</summary>
    private static async Task<bool> ReferencesUnchangedAsync(NpgsqlConnection conn, Guid master, List<Guid> declared, CancellationToken ct)
    {
        var current = new List<Guid>();
        var allFresh = true;
        await using var cmd = new NpgsqlCommand(
            """
            SELECT dep.child_master_id, cr.revision = dep.seen_revision AND cr.latest_iteration = dep.seen_iteration
            FROM cad.dependency dep
            JOIN plm.document_iteration pi ON pi.id = dep.parent_iteration_id
            JOIN plm.document_revision pr ON pr.id = pi.revision_id AND pr.is_latest AND pi.iteration = pr.latest_iteration
            JOIN plm.document_revision cr ON cr.master_id = dep.child_master_id AND cr.is_latest
            WHERE pr.master_id = @m
            """, conn);
        cmd.Parameters.AddWithValue("m", master);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { current.Add(r.GetGuid(0)); allFresh &= r.GetBoolean(1); }
        return allFresh && current.Order().SequenceEqual(declared.Order());
    }

    /// <summary>For the manifest: which members of the set reference which other members (names are not needed: the connector knows the numbers).</summary>
    private async Task<IReadOnlyDictionary<Guid, List<string>>> ReferencesAmongAsync(Subject subject, Guid[] masters, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var edges = await CadGraphService.QueryEdgesAsync(conn, null, subject, masters, down: true, ct);
        var inSet = masters.ToHashSet();
        return edges.Where(e => e.Visible && inSet.Contains(e.OtherId!.Value)).GroupBy(e => e.ParentMaster).ToDictionary(g => g.Key, g => g.Select(e => e.Number!).ToList());
    }

    private static async Task MarkDoneAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid setId, Guid[] masters, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("UPDATE cad.checkout_member SET done_at = now() WHERE set_id = @s AND document_master_id = ANY(@m) AND done_at IS NULL", conn, tx);
        cmd.Parameters.AddWithValue("s", setId);
        cmd.Parameters.AddWithValue("m", masters);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task CloseIfFinishedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid setId, string reason, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE cad.checkout_set SET closed_at = now(), closed_reason = @reason
            WHERE id = @s AND closed_at IS NULL AND NOT EXISTS (SELECT 1 FROM cad.checkout_member WHERE set_id = @s AND done_at IS NULL)
            """, conn, tx);
        cmd.Parameters.AddWithValue("s", setId);
        cmd.Parameters.AddWithValue("reason", reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> IsClosedAsync(NpgsqlConnection conn, Guid setId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT closed_at IS NOT NULL FROM cad.checkout_set WHERE id = @s", conn);
        cmd.Parameters.AddWithValue("s", setId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}

/// <summary>The bytes of an upload did not hash to the SHA-256 the client declared.</summary>
public sealed class CadIntegrityException() : Exception("The uploaded bytes do not match the declared SHA-256.");

/// <summary>Hashes everything read from the inner stream and, at its end, compares with the expected digest, failing before the receiver records anything.</summary>
internal sealed class VerifyingStream(Stream inner, string expectedSha256) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _verified;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var n = inner.Read(buffer);
        Account(buffer[..n], n);
        return n;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await inner.ReadAsync(buffer, cancellationToken);
        Account(buffer.Span[..n], n);
        return n;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Account(ReadOnlySpan<byte> data, int n)
    {
        if (n > 0) { _hash.AppendData(data); return; }
        if (_verified) return;
        _verified = true;
        var actual = Convert.ToHexStringLower(_hash.GetHashAndReset());
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new CadIntegrityException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }
}
