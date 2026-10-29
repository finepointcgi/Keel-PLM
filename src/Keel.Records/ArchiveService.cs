using System.Globalization;
using System.Text.Json;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Records;

/// <summary>What was stored for an archive package, before its rows are registered.</summary>
internal sealed record PackagedArchive(
    Guid Id, string Number, string Title, string PackageSha256, long PackageSize, string ManifestSha256, string KeyId, byte[] Signature, int ObjectCount, int FileCount,
    string WormMode, DateTimeOffset? RetainUntil, IReadOnlyList<(string Kind, Guid Id, string Number, int Revisions)> Objects, Markings Cover);

/// <summary>
/// WORM-style archive packages (ADR-0041): the released revisions of an object set with their files, the workflow signatures and an extract of the audit log,
/// written as one ZIP with a hash manifest signed by the audit checkpoint key, stored in the vault under its own SHA-256. The application keeps a package
/// immutable by construction (no code path writes or deletes it, database triggers refuse changes to its record, the vault garbage collector never counts it as
/// garbage) and, when the vault bucket supports S3 Object Lock, asks the store to enforce a retention period too (feature-detected, never assumed). What this does and
/// does not guarantee is written down in docs/ops/records-management.md.
/// </summary>
public sealed class ArchiveService(
    NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, RecordsSigner signer, TrustedCheckpointKeys trusted, RecordsOptions options,
    IContentStore? store = null, IContentReader? reader = null, IWormStorage? worm = null)
{
    private const string ObjectType = "archive";

    private async Task<Outcome<T>> DenyAsync<T>(Subject s, string action, string? id, string? corr, CancellationToken ct)
    {
        await audit.AppendAsync(RecordsStore.Entry(s, action, AuditOutcome.Denied, ObjectType, id, corr, ("reasons", AccessPolicyReasons.Role)), ct);
        return Outcome<T>.Forbidden([AccessPolicyReasons.Role]);
    }

    // ---------------------------------------------------------------- create

    public async Task<Outcome<ArchiveView>> CreateAsync(Subject s, CreateArchiveCommand cmd, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanManage(s)) return await DenyAsync<ArchiveView>(s, "records.archive_create", null, corr, ct);
        if (string.IsNullOrWhiteSpace(cmd.Title) || cmd.Title.Length > 200) return Outcome<ArchiveView>.Invalid(["The archive needs a title of at most 200 characters."]);
        if (cmd.Items.Count == 0) return Outcome<ArchiveView>.Invalid(["Choose at least one object."]);
        if (cmd.Items.Count > options.MaxItemsPerRequest) return Outcome<ArchiveView>.Invalid([$"An archive holds at most {options.MaxItemsPerRequest} objects."]);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var resolved = await ResolveAsync(conn, null, s, cmd.Items, ct);
        if (resolved.Errors.Count > 0) return Outcome<ArchiveView>.Invalid(resolved.Errors);
        var packaged = await PackageAsync(s.Username ?? s.Id, cmd.Title.Trim(), resolved.Objects, resolved.Cover!, corr, ct);
        if (!packaged.IsOk) return new Outcome<ArchiveView>(packaged.Kind, null, packaged.Errors);

        await using var tx = await conn.BeginTransactionAsync(ct);
        await RegisterAsync(conn, tx, packaged.Value!, s.Username ?? s.Id, null, ct);
        await audit.AppendAsync(tx, RecordsStore.Entry(s, "records.archive_create", AuditOutcome.Success, ObjectType, packaged.Value!.Number, corr,
            ("objects", packaged.Value.ObjectCount.ToString(CultureInfo.InvariantCulture)), ("files", packaged.Value.FileCount.ToString(CultureInfo.InvariantCulture)), ("worm", packaged.Value.WormMode)), ct);
        var view = await LoadViewAsync(conn, tx, packaged.Value.Number, ct);
        await tx.CommitAsync(ct);
        return Outcome<ArchiveView>.Ok(view!);
    }

    internal sealed record Resolved(IReadOnlyList<ObjectFacts> Objects, Markings? Cover, IReadOnlyList<string> Errors);

    /// <summary>Resolves named objects the caller can read (an unreadable object is reported exactly like a missing one) and computes the markings that cover them all.</summary>
    internal static async Task<Resolved> ResolveAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Subject s, IReadOnlyList<ObjectRef> items, CancellationToken ct)
    {
        var errors = new List<string>();
        var facts = new List<ObjectFacts>();
        foreach (var o in items.DistinctBy(i => (i.Kind, i.Number)))
        {
            if (!RecordKinds.IsKnown(o.Kind)) { errors.Add($"'{o.Kind}' is not an object kind."); continue; }
            var f = await RecordsStore.LoadFactsAsync(conn, tx, o.Kind, o.Number, ct);
            if (f is null || !AccessPolicy.Evaluate(s, f.Markings, PlmAction.Read).Permitted) errors.Add($"{RecordKinds.Label(o.Kind)} {o.Number} was not found.");
            else facts.Add(f);
        }
        if (errors.Count > 0) return new Resolved([], null, errors);
        var cover = MarkingDominance.MinimalCover(facts[0].Markings, facts.Skip(1).Select(f => f.Markings));
        if (!cover.IsSuccess) return new Resolved([], null, [.. cover.Errors.Select(e => e.Replace("change", "request", StringComparison.Ordinal))]);
        if (!AccessPolicy.Evaluate(s, cover.Value, PlmAction.Read).Permitted) return new Resolved([], null, ["The selection is not readable as a whole; choose objects you can read together."]);
        return new Resolved(facts, cover.Value, []);
    }

    // ---------------------------------------------------------------- packaging

    internal async Task<Outcome<PackagedArchive>> PackageAsync(string createdBy, string title, IReadOnlyList<ObjectFacts> objects, Markings cover, string? corr, CancellationToken ct)
    {
        if (!signer.Available) return Outcome<PackagedArchive>.Unavailable("No checkpoint signing key is configured, so an archive cannot be signed.");
        if (store is null || reader is null) return Outcome<PackagedArchive>.Unavailable("The vault is not available to this host.");
        var optionErrors = options.Validate();
        if (optionErrors.Count > 0) return Outcome<PackagedArchive>.Unavailable(string.Join(" ", optionErrors));

        var now = AuditChain.Truncate(clock.GetUtcNow());
        Guid id = Guid.CreateVersion7();
        string number;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            number = RecordsStore.NextNumber("AR-", await RecordsStore.NextValAsync(conn, tx, "rec.archive_seq", ct));
            await tx.CommitAsync(ct);
        }

        using var writer = new ArchiveWriter(now);
        var described = new List<(string Kind, Guid Id, string Number, int Revisions)>();
        var files = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct))
        {
            foreach (var f in objects.OrderBy(o => o.Kind, StringComparer.Ordinal).ThenBy(o => o.Number, StringComparer.Ordinal))
            {
                var obj = await DescribeAsync(conn, tx, f, ct);
                if (obj.Revisions.Count == 0) return Outcome<PackagedArchive>.Invalid([$"{RecordKinds.Label(f.Kind)} {f.Number} has no released revision to archive."]);
                foreach (var file in obj.Revisions.SelectMany(r => r.Iterations).SelectMany(i => i.Files)) files[file.Sha256] = file.Size;
                writer.AddBytes(ArchiveFormat.ObjectPath(f.Kind, f.Number), JsonSerializer.SerializeToUtf8Bytes(obj, ArchiveFormat.Json));
                described.Add((f.Kind, f.Id, f.Number, obj.Revisions.Count));
            }
            if (files.Values.Sum() > options.MaxArchiveBytes) return Outcome<PackagedArchive>.Invalid([$"The files total more than the {options.MaxArchiveBytes.ToString(CultureInfo.InvariantCulture)} byte limit of one archive; split the selection."]);
            writer.AddBytes(ArchiveFormat.SignaturesPath, JsonSerializer.SerializeToUtf8Bytes(await LoadSignaturesAsync(conn, tx, described, ct), ArchiveFormat.Json));
            writer.AddBytes(ArchiveFormat.AuditPath, JsonSerializer.SerializeToUtf8Bytes(await LoadAuditExtractAsync(conn, tx, described, ct), ArchiveFormat.Json));
            await tx.CommitAsync(ct);
        }
        foreach (var (sha, fileSize) in files.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            try
            {
                await using var content = await reader.OpenReadAsync(sha, fileSize, ct);
                var info = await writer.AddStreamAsync(ArchiveFormat.FilePath(sha), content, ct);
                if (info.Sha256 != sha) return Outcome<PackagedArchive>.Conflict("A stored file does not match its content hash; nothing was archived.");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Outcome<PackagedArchive>.Unavailable($"A file could not be read from the vault ({e.GetType().Name}); nothing was archived.");
            }
        }

        var manifest = new ArchiveManifest(ArchiveFormat.Format, number, title, AuditChain.FormatTimestamp(now), createdBy, signer.KeyId, cover.BannerText(),
            [.. described.Select(d => new ManifestObject(d.Kind, d.Number, d.Revisions))], [.. writer.Entries.OrderBy(e => e.Path, StringComparer.Ordinal)]);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ArchiveFormat.Json);
        var manifestSha = RecordsSigner.Sha256Hex(manifestBytes);
        var signature = signer.Sign(RecordsSigner.ArchiveDomain, manifestSha);
        writer.AddBytes(ArchiveFormat.ManifestPath, manifestBytes, record: false);
        writer.AddBytes(ArchiveFormat.SignaturePath, JsonSerializer.SerializeToUtf8Bytes(
            new ArchiveSignatureFile("keel-archive-signature/1", manifestSha, signer.KeyId, "ECDSA-P256-SHA256", Convert.ToBase64String(signature)), ArchiveFormat.Json), record: false);

        await using var package = writer.Finish();
        if (package.Length > options.MaxArchiveBytes + 64 * 1024 * 1024) return Outcome<PackagedArchive>.Invalid(["The package is larger than the archive limit; split the selection."]);
        var size = package.Length;
        ContentInfo stored;
        try { stored = await store.PutAsync(package, ct); }
        catch (ContentRejectedException e) { return Outcome<PackagedArchive>.Unprocessable(e.Message); }
        if (stored.SizeBytes != size) return Outcome<PackagedArchive>.Conflict("The vault stored a different number of bytes than were sent; the archive was not registered.");

        var wormMode = "application";
        DateTimeOffset? until = null;
        if (options.ObjectLock != "Off" && worm is not null)
        {
            WormCapability capability;
            try { capability = await worm.DetectAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { capability = new WormCapability(false, $"The store did not answer the object lock query ({e.GetType().Name})."); }
            if (capability.Supported)
            {
                try
                {
                    var applied = await worm.ApplyRetentionAsync(stored.Sha256, options.ObjectLockMode, now.AddDays(options.ObjectLockDays), ct);
                    wormMode = applied.Mode == "compliance" ? "s3_compliance" : "s3_governance";
                    until = applied.RetainUntil;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    if (options.ObjectLock == "Required") return Outcome<PackagedArchive>.Unavailable($"S3 Object Lock is required but the store refused the retention ({e.GetType().Name}); the archive was not registered.");
                }
            }
            else if (options.ObjectLock == "Required") return Outcome<PackagedArchive>.Unavailable($"S3 Object Lock is required but not available: {capability.Detail}");
        }
        else if (options.ObjectLock == "Required") return Outcome<PackagedArchive>.Unavailable("S3 Object Lock is required but this host has no object store that can provide it.");

        return Outcome<PackagedArchive>.Ok(new PackagedArchive(id, number, title, stored.Sha256, size, manifestSha, signer.KeyId, signature, described.Count, files.Count, wormMode, until, described, cover));
    }

    internal async Task RegisterAsync(NpgsqlConnection conn, NpgsqlTransaction tx, PackagedArchive p, string createdBy, Guid? dispositionId, CancellationToken ct)
    {
        await using (var ins = new NpgsqlCommand(
            $"""
            INSERT INTO rec.archive (id, number, title, package_sha256, package_size, manifest_sha256, key_id, signature, object_count, file_count, worm_mode, retain_until, created_at, created_by, disposition_id, {MarkingColumns.InsertColumns})
            VALUES (@id, @n, @t, @sha, @size, @msha, @kid, @sig, @oc, @fc, @worm, @until, @now, @by, @disp, {MarkingColumns.InsertValues})
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("id", p.Id); ins.Parameters.AddWithValue("n", p.Number); ins.Parameters.AddWithValue("t", p.Title);
            ins.Parameters.AddWithValue("sha", p.PackageSha256); ins.Parameters.AddWithValue("size", p.PackageSize); ins.Parameters.AddWithValue("msha", p.ManifestSha256);
            ins.Parameters.AddWithValue("kid", p.KeyId); ins.Parameters.AddWithValue("sig", p.Signature); ins.Parameters.AddWithValue("oc", p.ObjectCount); ins.Parameters.AddWithValue("fc", p.FileCount);
            ins.Parameters.AddWithValue("worm", p.WormMode); ins.Parameters.AddWithValue("until", (object?)p.RetainUntil ?? DBNull.Value);
            ins.Parameters.AddWithValue("now", AuditChain.Truncate(clock.GetUtcNow())); ins.Parameters.AddWithValue("by", createdBy);
            ins.Parameters.AddWithValue("disp", (object?)dispositionId ?? DBNull.Value);
            RecordsStore.AddMarkings(ins, p.Cover);
            await ins.ExecuteNonQueryAsync(ct);
        }
        foreach (var (kind, objId, number, revisions) in p.Objects)
        {
            await using var o = new NpgsqlCommand("INSERT INTO rec.archive_object (archive_id, object_kind, object_id, object_number, revisions) VALUES (@a, @k, @id, @n, @r)", conn, tx);
            o.Parameters.AddWithValue("a", p.Id); o.Parameters.AddWithValue("k", kind); o.Parameters.AddWithValue("id", objId); o.Parameters.AddWithValue("n", number); o.Parameters.AddWithValue("r", revisions);
            await o.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<ArchivedObject> DescribeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ObjectFacts f, CancellationToken ct)
    {
        var doc = f.Kind == RecordKinds.Document;
        var revisions = new List<(Guid Id, string Revision, string State, DateTimeOffset At)>();
        await using (var cmd = new NpgsqlCommand($"SELECT id, revision, lifecycle_state, state_changed_at FROM {(doc ? "plm.document_revision" : "plm.part_revision")} WHERE master_id = @m AND lifecycle_state IN ('Released', 'Obsolete') ORDER BY created_at, revision", conn, tx))
        {
            cmd.Parameters.AddWithValue("m", f.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) revisions.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)));
        }
        var outRevisions = new List<ArchivedRevision>();
        foreach (var rev in revisions)
        {
            var iterations = new List<(Guid Id, int N, string Description, DateTimeOffset At, string By)>();
            await using (var cmd = new NpgsqlCommand($"SELECT id, iteration, description, created_at, created_by FROM {(doc ? "plm.document_iteration" : "plm.part_iteration")} WHERE revision_id = @r ORDER BY iteration", conn, tx))
            {
                cmd.Parameters.AddWithValue("r", rev.Id);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) iterations.Add((r.GetGuid(0), r.GetInt32(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.GetString(4)));
            }
            var outIterations = new List<ArchivedIteration>();
            foreach (var it in iterations)
            {
                var files = new List<ArchivedFile>();
                if (doc)
                {
                    await using var cmd = new NpgsqlCommand("SELECT file_name, media_type, size_bytes, sha256 FROM plm.document_file WHERE iteration_id = @i ORDER BY role, lower(file_name)", conn, tx);
                    cmd.Parameters.AddWithValue("i", it.Id);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) files.Add(new ArchivedFile(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3)));
                }
                outIterations.Add(new ArchivedIteration(it.N, it.Description, it.At, it.By, files));
            }
            outRevisions.Add(new ArchivedRevision(rev.Revision, rev.State, rev.At, outIterations));
        }
        return new ArchivedObject(f.Kind, f.Number, f.Name, f.Type, f.Markings.BannerText(), outRevisions);
    }

    private static async Task<List<ArchivedSignature>> LoadSignaturesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<(string Kind, Guid Id, string Number, int Revisions)> objects, CancellationToken ct)
    {
        var list = new List<ArchivedSignature>();
        foreach (var (kind, _, number, _) in objects)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT object_key, object_revision, signer_username, meaning_label, decision, signed_at, content_hash, signature_hash FROM wf.signature WHERE object_type = @t AND object_key = @k ORDER BY signed_at, id", conn, tx);
            cmd.Parameters.AddWithValue("t", kind); cmd.Parameters.AddWithValue("k", number);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new ArchivedSignature(kind, r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5),
                    Convert.ToHexStringLower((byte[])r[6]), Convert.ToHexStringLower((byte[])r[7])));
        }
        return list;
    }

    private static async Task<AuditExtract> LoadAuditExtractAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<(string Kind, Guid Id, string Number, int Revisions)> objects, CancellationToken ct)
    {
        var events = new List<AuditExtractEvent>();
        foreach (var (kind, _, number, _) in objects)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT seq, id, occurred_at, actor, action, object_type, object_id, outcome, correlation_id, payload, prev_hash, hash FROM audit.event WHERE object_type = @t AND object_id = @k ORDER BY seq", conn, tx);
            cmd.Parameters.AddWithValue("t", kind); cmd.Parameters.AddWithValue("k", number);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                events.Add(new AuditExtractEvent(r.GetInt64(0), r.GetGuid(1).ToString("D"), AuditChain.FormatTimestamp(r.GetFieldValue<DateTimeOffset>(2)), r.GetString(3), r.GetString(4),
                    RecordsStore.Str(r, 5), RecordsStore.Str(r, 6), r.GetString(7), RecordsStore.Str(r, 8), r.GetString(9), Convert.ToHexStringLower((byte[])r[10]), Convert.ToHexStringLower((byte[])r[11])));
        }
        events = [.. events.OrderBy(e => e.Seq)];
        AuditExtractCheckpoint? checkpoint = null;
        if (events.Count > 0)
        {
            await using var cmd = new NpgsqlCommand("SELECT seq, hash, created_at, key_id, signature FROM audit.checkpoint WHERE seq >= @s ORDER BY seq LIMIT 1", conn, tx);
            cmd.Parameters.AddWithValue("s", events[^1].Seq);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                checkpoint = new AuditExtractCheckpoint(r.GetInt64(0), Convert.ToHexStringLower((byte[])r[1]), AuditChain.FormatTimestamp(r.GetFieldValue<DateTimeOffset>(2)), r.GetString(3), Convert.ToBase64String((byte[])r[4]));
        }
        return new AuditExtract("keel-audit-extract/1", events, checkpoint);
    }

    // ---------------------------------------------------------------- read

    private static readonly string Select = $"a.id, a.number, a.title, a.created_at, a.created_by, a.package_sha256, a.package_size, a.manifest_sha256, a.key_id, a.worm_mode, a.retain_until, a.object_count, a.file_count, {MarkingColumns.Select("a")}";

    private static async Task<ArchiveView?> LoadViewAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string number, CancellationToken ct)
    {
        ArchiveView? view = null; Guid id;
        await using (var cmd = new NpgsqlCommand($"SELECT {Select} FROM rec.archive a WHERE a.number = @n", conn, tx))
        {
            cmd.Parameters.AddWithValue("n", number);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            id = r.GetGuid(0);
            var m = MarkingColumns.Read(r, 13);
            view = new ArchiveView(r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.GetString(4), r.GetString(5), r.GetInt64(6), r.GetString(7), r.GetString(8), r.GetString(9),
                r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), m.BannerText(), m, []);
        }
        var objs = new List<(string, string, int)>();
        await using (var cmd = new NpgsqlCommand("SELECT object_kind, object_number, revisions FROM rec.archive_object WHERE archive_id = @a ORDER BY object_kind, object_number", conn, tx))
        {
            cmd.Parameters.AddWithValue("a", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) objs.Add((r.GetString(0), r.GetString(1), r.GetInt32(2)));
        }
        return view with { Objects = objs };
    }

    public async Task<Outcome<ArchiveView>> GetAsync(Subject s, string number, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanView(s)) return await DenyAsync<ArchiveView>(s, "records.archive_read", number, corr, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var v = await LoadViewAsync(conn, null, number, ct);
        if (v is null || !AccessPolicy.Evaluate(s, v.Markings, PlmAction.Read).Permitted)
        {
            if (v is not null) await audit.AppendAsync(RecordsStore.Entry(s, "records.archive_read", AuditOutcome.Denied, ObjectType, number, corr, ("reasons", "markings")), ct);
            return Outcome<ArchiveView>.NotFound();
        }
        return Outcome<ArchiveView>.Ok(v);
    }

    public async Task<Outcome<IReadOnlyList<ArchiveSummary>>> ListAsync(Subject s, string? corr, CancellationToken ct = default)
    {
        if (!RecordsAccess.CanView(s)) return await DenyAsync<IReadOnlyList<ArchiveSummary>>(s, "records.archive_list", null, corr, ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var f = AccessPolicy.BuildReadFilter(s, "a");
        var list = new List<ArchiveSummary>();
        await using var cmd = new NpgsqlCommand($"SELECT a.number, a.title, a.object_count, a.file_count, a.package_size, a.worm_mode, a.created_at, a.created_by, {MarkingColumns.Select("a")} FROM rec.archive a WHERE {f.Sql} ORDER BY a.created_at DESC LIMIT 500", conn);
        foreach (var (k, v) in f.Parameters) cmd.Parameters.AddWithValue(k, v);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ArchiveSummary(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt64(4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6), r.GetString(7), MarkingColumns.Read(r, 8).BannerText()));
        return Outcome<IReadOnlyList<ArchiveSummary>>.Ok(list);
    }

    /// <summary>Reads the package from the vault, verifies it completely and returns what it holds: the restore-as-read-only view. Nothing in the result can write back.</summary>
    public async Task<Outcome<ArchiveContents>> OpenAsync(Subject s, string number, string? corr, CancellationToken ct = default)
    {
        var archive = await GetAsync(s, number, corr, ct);
        if (!archive.IsOk) return new Outcome<ArchiveContents>(archive.Kind, null, archive.Errors);
        var read = await ReadPackageAsync(archive.Value!, ct);
        await audit.AppendAsync(RecordsStore.Entry(s, "records.archive_open", read.Verification.Valid ? AuditOutcome.Success : AuditOutcome.Failure, ObjectType, number, corr, ("valid", read.Verification.Valid ? "true" : "false")), ct);
        return Outcome<ArchiveContents>.Ok(new ArchiveContents(archive.Value!, read.Verification, read.Objects, read.Signatures, read.Events));
    }

    public async Task<Outcome<ArchiveVerification>> VerifyAsync(Subject s, string number, string? corr, CancellationToken ct = default)
    {
        var archive = await GetAsync(s, number, corr, ct);
        if (!archive.IsOk) return new Outcome<ArchiveVerification>(archive.Kind, null, archive.Errors);
        var read = await ReadPackageAsync(archive.Value!, ct);
        await audit.AppendAsync(RecordsStore.Entry(s, "records.archive_verify", read.Verification.Valid ? AuditOutcome.Success : AuditOutcome.Failure, ObjectType, number, corr, ("valid", read.Verification.Valid ? "true" : "false")), ct);
        return Outcome<ArchiveVerification>.Ok(read.Verification);
    }

    private async Task<ArchiveRead> ReadPackageAsync(ArchiveView archive, CancellationToken ct)
    {
        if (reader is null) return new ArchiveRead(new ArchiveVerification(false, ["The vault is not available to this host."], 0, 0, false, false, null), null, null, null, [], [], []);
        var path = Path.GetTempFileName();
        try
        {
            await using (var temp = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.None))
            {
                try
                {
                    await using var package = await reader.OpenReadAsync(archive.PackageSha256, archive.PackageSize, ct);
                    await package.CopyToAsync(temp, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return new ArchiveRead(new ArchiveVerification(false, [$"The package could not be read from the vault intact ({e.GetType().Name})."], 0, 0, false, false, null), null, null, null, [], [], []);
                }
            }
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var read = ArchiveReader.Read(input, trusted.Keys);
            var problems = read.Verification.Problems.ToList();
            if (read.ManifestSha256 is not null && read.ManifestSha256 != archive.ManifestSha256) problems.Add("The manifest differs from the one recorded when the archive was made.");
            if (read.Number is not null && read.Number != archive.Number) problems.Add("The package belongs to a different archive.");
            return read with { Verification = read.Verification with { Valid = problems.Count == 0, Problems = problems } };
        }
        finally
        {
            File.Delete(path);
        }
    }
}
