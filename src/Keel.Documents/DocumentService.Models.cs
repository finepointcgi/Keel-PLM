using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Core.Geometry;
using Keel.Documents.Marking;
using Keel.Documents.Scanning;
using Keel.Documents.Vault;
using Keel.Security;
using Npgsql;

namespace Keel.Documents;

/// <summary>
/// Model versioning for CAD and mesh files (increment 9.1, ADR-0046). Every version of a model IS a document iteration: this partial adds no store of its own.
/// It gives the iterations of one document a lightweight face: a one-step "upload new version", a history with server-measured geometry, a comparison of any
/// two versions, "restore" (a new iteration equal to an old one) and a download of selected versions. All of it runs through the same access policy, check-out
/// rules, release locks, malware scan, vault hashing and audit as every other document action.
/// </summary>
public sealed partial class DocumentService
{
    public const string ModelHistoryAction = "document.model_history";
    public const string ModelCompareAction = "document.model_compare";
    public const string VersionUploadAction = "document.version_upload";
    public const string VersionRestoreAction = "document.version_restore";
    public const string VersionsDownloadAction = "document.versions_download";
    public const string ReviseAction = "document.revise";
    public const int MaxChangeNoteLength = 1000;
    public const int MaxVersionsInZip = 25;

    /// <summary>How many versions one history view measures (the rest show "measuring" and are done by the next view): a long history of large files must not hold a request.</summary>
    private const int MaxMeasurementsPerView = 8;
    private static readonly SemaphoreSlim MeasureSlots = new(2, 2);

    // ---------------------------------------------------------------- history

    private sealed record VersionRecord(
        Guid IterationId, string Revision, int Iteration, bool RevisionIsLatest, string State, DateTimeOffset CreatedAt, string CreatedBy, string? Comment,
        Guid? FileId, string? FileName, string? MediaType, long? SizeBytes, string? Sha256, ModelMetrics? Metrics)
    {
        public string Label => $"{Revision}.{Iteration.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The versions of one document, oldest first, WITHOUT any whose primary file is quarantined: a quarantined version is not listed, not counted, not offered for
    /// comparison, restore or download (ADR-0026). The caller has already been authorized on the document.
    /// </summary>
    private static async Task<List<VersionRecord>> LoadVersionRecordsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid masterId, CancellationToken ct)
    {
        var list = new List<VersionRecord>();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT i.id, r.revision, i.iteration, r.is_latest, r.lifecycle_state, i.created_at, i.created_by, i.comment,
                   f.id, f.file_name, f.media_type, f.size_bytes, f.sha256,
                   m.status, m.format, m.reason, m.unit, m.up_axis, m.triangles, m.degenerate, m.vertices,
                   m.min_x, m.min_y, m.min_z, m.max_x, m.max_y, m.max_z, m.surface_area, m.signed_volume, m.watertight, m.geometry_fp, m.step_products
            FROM plm.document_iteration i
            JOIN plm.document_revision r ON r.id = i.revision_id
            LEFT JOIN plm.document_file f ON f.iteration_id = i.id AND f.role = 'primary'
            LEFT JOIN mdl.iteration_metrics m ON m.iteration_id = i.id
            WHERE r.master_id = @m
              AND NOT EXISTS (SELECT 1 FROM plm.file_quarantine q WHERE q.master_id = r.master_id AND q.sha256 = f.sha256 AND q.released_at IS NULL)
            """, conn, tx);
        cmd.Parameters.AddWithValue("m", masterId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new VersionRecord(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetBoolean(3), r.GetString(4),
                new DateTimeOffset(r.GetDateTime(5), TimeSpan.Zero), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetGuid(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetInt64(11), r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : ReadMetrics(r, 13)));
        }
        list.Sort((a, b) => RevisionScheme.ToIndex(a.Revision) != RevisionScheme.ToIndex(b.Revision)
            ? RevisionScheme.ToIndex(a.Revision).CompareTo(RevisionScheme.ToIndex(b.Revision))
            : a.Iteration.CompareTo(b.Iteration));
        return list;
    }

    private static double? Dbl(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    private static long? Lng(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    private static ModelMetrics ReadMetrics(NpgsqlDataReader r, int s) => new()
    {
        Status = r.GetString(s), Format = r.GetString(s + 1), Reason = r.IsDBNull(s + 2) ? null : r.GetString(s + 2), Unit = r.GetString(s + 3), Up = r.GetString(s + 4),
        Triangles = Lng(r, s + 5), DegenerateTriangles = Lng(r, s + 6), Vertices = Lng(r, s + 7),
        MinX = Dbl(r, s + 8), MinY = Dbl(r, s + 9), MinZ = Dbl(r, s + 10), MaxX = Dbl(r, s + 11), MaxY = Dbl(r, s + 12), MaxZ = Dbl(r, s + 13),
        SurfaceArea = Dbl(r, s + 14), SignedVolume = Dbl(r, s + 15), Watertight = r.IsDBNull(s + 16) ? null : r.GetBoolean(s + 16),
        GeometryFingerprint = r.IsDBNull(s + 17) ? null : r.GetString(s + 17), StepProducts = r.IsDBNull(s + 18) ? null : r.GetInt32(s + 18),
    };

    /// <summary>
    /// The version history of a model document: every iteration with who, when, size, SHA-256, change note, state and the geometry the server measured in its
    /// primary file. Needs Read (404 otherwise, denial audited). Measurements are made when first needed and then stored, never taken from a client.
    /// </summary>
    public async Task<Outcome<ModelVersionHistory>> GetModelVersionsAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        List<VersionRecord> records;
        Head head;
        Lifecycle lifecycle;
        (string Revision, string State, int Iteration, string? CheckedOutBy, bool CheckedOut) latest;
        bool modify, download, admin;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            var loaded = await LoadHeadAsync(conn, tx, number, forUpdate: false, ct);
            if (loaded is null) return Outcome<ModelVersionHistory>.NotFound();
            head = loaded;
            var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
            if (!read.Permitted)
            {
                await audit.AppendAsync(tx, Entry(subject, ModelHistoryAction, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
                await tx.CommitAsync(ct);
                return Outcome<ModelVersionHistory>.NotFound();
            }
            if (await IsDisposedAsync(conn, tx, head.MasterId, ct))
            {
                await tx.CommitAsync(ct);
                return Outcome<ModelVersionHistory>.Conflict("The content of this document was disposed of under a retention schedule.");
            }
            lifecycle = (await Lifecycle.LoadAsync(conn, tx, head.LifecycleId, ct))!;
            records = await LoadVersionRecordsAsync(conn, tx, head.MasterId, ct);
            await using (var cmd = new NpgsqlCommand(
                """
                SELECT r.revision, r.lifecycle_state, r.latest_iteration, c.user_name, c.user_id
                FROM plm.document_revision r LEFT JOIN plm.document_checkout c ON c.revision_id = r.id
                WHERE r.master_id = @m AND r.is_latest
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("m", head.MasterId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                latest = (r.GetString(0), r.GetString(1), r.GetInt32(2), r.IsDBNull(3) ? (r.IsDBNull(4) ? null : r.GetString(4)) : r.GetString(3), !r.IsDBNull(4));
            }
            modify = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Modify).Permitted;
            download = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Download).Permitted;
            admin = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Administer).Permitted;
            await audit.AppendAsync(tx, Entry(subject, ModelHistoryAction, AuditOutcome.Success, number, correlationId, ("versions", records.Count.ToString(CultureInfo.InvariantCulture))), ct);
            await tx.CommitAsync(ct);
        }

        var pending = await EnsureMeasuredAsync(records, MaxMeasurementsPerView, ct);
        var rows = new List<ModelVersionRow>();
        string? previousFingerprint = null;
        foreach (var v in records)
        {
            var same = previousFingerprint is not null && v.Metrics?.GeometryFingerprint == previousFingerprint;
            previousFingerprint = v.Metrics?.GeometryFingerprint ?? previousFingerprint;
            var current = v.RevisionIsLatest && v.Iteration == latest.Iteration;
            rows.Add(new ModelVersionRow(v.IterationId, v.Revision, v.Iteration, v.Label, current, v.State, lifecycle.State(v.State)?.Label ?? v.State, lifecycle.IsLocked(v.State),
                v.CreatedAt, v.CreatedBy, v.Comment, v.FileId, v.FileName, v.SizeBytes, v.Sha256, v.Metrics, same));
        }
        return Outcome<ModelVersionHistory>.Ok(new ModelVersionHistory(number, await NameOfAsync(head.MasterId, ct), head.Markings.BannerText(), head.Markings, rows,
            $"{latest.Revision}.{latest.Iteration}", latest.Revision, latest.State, lifecycle.IsLocked(latest.State), latest.CheckedOut, latest.CheckedOutBy, latest.Iteration,
            modify, download, admin, pending));
    }

    private async Task<string> NameOfAsync(Guid masterId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT name FROM plm.document_master WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", masterId);
        return (string?)await cmd.ExecuteScalarAsync(ct) ?? "";
    }

    private static async Task<bool> IsDisposedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid masterId, CancellationToken ct)
    {
        await using var gone = new NpgsqlCommand("SELECT rec.is_tombstoned('document', @id)", conn, tx);
        gone.Parameters.AddWithValue("id", masterId);
        return (bool)(await gone.ExecuteScalarAsync(ct))!;
    }

    // ---------------------------------------------------------------- measurement (lazy, then cached for good)

    /// <summary>
    /// Fills in the missing metrics of model files, at most <paramref name="budget"/> of them. Returns how many model versions are still unmeasured.
    /// A measurement is derived from the stored bytes, verified against their SHA-256 on the way; an integrity failure or a vault outage is not stored (it is
    /// not a property of the file), everything else is, including "this file cannot be read" with its plain reason.
    /// </summary>
    private async Task<int> EnsureMeasuredAsync(List<VersionRecord> records, int budget, CancellationToken ct)
    {
        var limits = meshLimits ?? MeshLimits.Default;
        for (var i = 0; i < records.Count; i++)
        {
            var v = records[i];
            if (v.Metrics is not null || v.FileId is null || v.Sha256 is null || !ModelFormats.IsModel(v.FileName)) continue;
            if (budget <= 0) continue;
            ModelMetrics? metrics = await ReuseMetricsAsync(v.Sha256, v.FileName!, ct);
            var store = true;
            if (metrics is null)
            {
                budget--;
                try
                {
                    metrics = await MeasureAsync(v, limits, ct);
                }
                catch (Exception e) when (e is VaultIntegrityException or VaultUnavailableException)
                {
                    metrics = new ModelMetrics { Status = "failed", Format = ModelFormats.FromFileName(v.FileName) ?? "other", Reason = "The stored file could not be read and verified just now, so it was not measured." };
                    store = false;
                }
            }
            if (store) await StoreMetricsAsync(v, metrics, ct);
            records[i] = v with { Metrics = metrics };
        }
        return records.Count(v => v.Metrics is null && v.FileId is not null && ModelFormats.IsModel(v.FileName));
    }

    private async Task<ModelMetrics> MeasureAsync(VersionRecord v, MeshLimits limits, CancellationToken ct)
    {
        var format = ModelFormats.FromFileName(v.FileName) ?? "other";
        var size = v.SizeBytes ?? 0;
        var needsBytes = (ModelFormats.IsMesh(format) && size <= limits.MaxBytes) || (format == ModelFormats.Step && stepCounter is not null && size <= limits.MaxBytes);
        if (!needsBytes) return ModelAnalysis.Analyze(v.FileName!, ReadOnlyMemory<byte>.Empty, size, limits, stepCounter);
        await MeasureSlots.WaitAsync(ct);
        try
        {
            var bytes = await vault.ReadAllAsync(v.Sha256!, size, ct);
            return ModelAnalysis.Analyze(v.FileName!, bytes, size, limits, stepCounter);
        }
        finally
        {
            MeasureSlots.Release();
        }
    }

    /// <summary>The metrics of other bytes-identical files measured by this analyser version (a restore, or the same file in two documents): the same bytes give the same numbers.</summary>
    private async Task<ModelMetrics?> ReuseMetricsAsync(string sha256, string fileName, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT m.status, m.format, m.reason, m.unit, m.up_axis, m.triangles, m.degenerate, m.vertices,
                   m.min_x, m.min_y, m.min_z, m.max_x, m.max_y, m.max_z, m.surface_area, m.signed_volume, m.watertight, m.geometry_fp, m.step_products
            FROM mdl.iteration_metrics m
            WHERE m.file_sha256 = @sha AND m.analyser = @v AND m.format = @fmt
            ORDER BY m.analysed_at LIMIT 1
            """, conn);
        cmd.Parameters.AddWithValue("sha", sha256);
        cmd.Parameters.AddWithValue("v", ModelMetrics.AnalyserVersion);
        cmd.Parameters.AddWithValue("fmt", ModelFormats.FromFileName(fileName) ?? "other");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadMetrics(r, 0) : null;
    }

    private async Task StoreMetricsAsync(VersionRecord v, ModelMetrics m, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO mdl.iteration_metrics (iteration_id, file_id, file_sha256, format, status, reason, analyser, unit, up_axis, triangles, degenerate, vertices,
                min_x, min_y, min_z, max_x, max_y, max_z, surface_area, signed_volume, watertight, geometry_fp, step_products, analysed_at)
            VALUES (@iid, @fid, @sha, @fmt, @status, @reason, @an, @unit, @up, @tri, @deg, @vert, @minx, @miny, @minz, @maxx, @maxy, @maxz, @area, @vol, @wt, @fp, @prod, @at)
            ON CONFLICT (iteration_id) DO NOTHING
            """, conn);
        cmd.Parameters.AddWithValue("iid", v.IterationId);
        cmd.Parameters.AddWithValue("fid", v.FileId!.Value);
        cmd.Parameters.AddWithValue("sha", v.Sha256!);
        cmd.Parameters.AddWithValue("fmt", m.Format);
        cmd.Parameters.AddWithValue("status", m.Status);
        cmd.Parameters.AddWithValue("reason", (object?)(m.Reason is { Length: > 500 } ? m.Reason[..500] : m.Reason) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("an", ModelMetrics.AnalyserVersion);
        cmd.Parameters.AddWithValue("unit", m.Unit);
        cmd.Parameters.AddWithValue("up", m.Up);
        cmd.Parameters.AddWithValue("tri", (object?)m.Triangles ?? DBNull.Value);
        cmd.Parameters.AddWithValue("deg", (object?)m.DegenerateTriangles ?? DBNull.Value);
        cmd.Parameters.AddWithValue("vert", (object?)m.Vertices ?? DBNull.Value);
        cmd.Parameters.AddWithValue("minx", (object?)m.MinX ?? DBNull.Value);
        cmd.Parameters.AddWithValue("miny", (object?)m.MinY ?? DBNull.Value);
        cmd.Parameters.AddWithValue("minz", (object?)m.MinZ ?? DBNull.Value);
        cmd.Parameters.AddWithValue("maxx", (object?)m.MaxX ?? DBNull.Value);
        cmd.Parameters.AddWithValue("maxy", (object?)m.MaxY ?? DBNull.Value);
        cmd.Parameters.AddWithValue("maxz", (object?)m.MaxZ ?? DBNull.Value);
        cmd.Parameters.AddWithValue("area", (object?)m.SurfaceArea ?? DBNull.Value);
        cmd.Parameters.AddWithValue("vol", (object?)m.SignedVolume ?? DBNull.Value);
        cmd.Parameters.AddWithValue("wt", (object?)m.Watertight ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fp", (object?)m.GeometryFingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("prod", (object?)m.StepProducts ?? DBNull.Value);
        cmd.Parameters.AddWithValue("at", clock.GetUtcNow());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Best effort after a version was created: measure it now so the first view of the history is instant. A failure here is never an error of the upload.</summary>
    private async Task MeasureNewIterationAsync(Guid masterId, Guid iterationId)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(CancellationToken.None);
            await using var tx = await conn.BeginTransactionAsync(CancellationToken.None);
            var records = (await LoadVersionRecordsAsync(conn, tx, masterId, CancellationToken.None)).Where(v => v.IterationId == iterationId).ToList();
            await tx.CommitAsync(CancellationToken.None);
            await EnsureMeasuredAsync(records, 1, CancellationToken.None);
        }
        catch (Exception e) when (e is NpgsqlException or VaultUnavailableException or VaultIntegrityException or IOException or TimeoutException)
        {
            // The history measures lazily; nothing is lost.
        }
    }

    // ---------------------------------------------------------------- compare

    /// <summary>
    /// Compares two versions of a model document: the numbers each was measured with, their difference and a plain-language summary. Needs Read; the visual
    /// comparison in the viewer additionally needs Download. Audited as <c>document.model_compare</c> (version labels and checksums only, never geometry values).
    /// </summary>
    public async Task<Outcome<ModelComparison>> CompareModelVersionsAsync(Subject subject, string number, Guid iterationA, Guid iterationB, string? correlationId, CancellationToken ct = default)
    {
        List<VersionRecord> records;
        Head head;
        bool download;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            var loaded = await LoadHeadAsync(conn, tx, number, forUpdate: false, ct);
            if (loaded is null) return Outcome<ModelComparison>.NotFound();
            head = loaded;
            var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
            if (!read.Permitted)
            {
                await audit.AppendAsync(tx, Entry(subject, ModelCompareAction, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
                await tx.CommitAsync(ct);
                return Outcome<ModelComparison>.NotFound();
            }
            if (await IsDisposedAsync(conn, tx, head.MasterId, ct))
            {
                await tx.CommitAsync(ct);
                return Outcome<ModelComparison>.Conflict("The content of this document was disposed of under a retention schedule.");
            }
            records = await LoadVersionRecordsAsync(conn, tx, head.MasterId, ct);
            download = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Download).Permitted;
            await tx.CommitAsync(ct);
        }
        if (iterationA == iterationB) return Outcome<ModelComparison>.Invalid(["Choose two different versions to compare."]);
        var a = records.FirstOrDefault(v => v.IterationId == iterationA);
        var b = records.FirstOrDefault(v => v.IterationId == iterationB);
        if (a is null || b is null) return Outcome<ModelComparison>.Invalid(["One of the versions does not exist or is not available."]);
        if (a.FileId is null || b.FileId is null) return Outcome<ModelComparison>.Invalid(["Both versions need a primary file to be compared."]);

        var pair = new List<VersionRecord> { a, b };
        await EnsureMeasuredAsync(pair, 2, ct);
        a = pair[0]; b = pair[1];
        var ma = a.Metrics ?? new ModelMetrics { Status = "facts_only", Format = "other", Reason = ModelAnalysis.NotAnalysedReason };
        var mb = b.Metrics ?? new ModelMetrics { Status = "facts_only", Format = "other", Reason = ModelAnalysis.NotAnalysedReason };
        var sameBytes = a.Sha256 == b.Sha256;
        var delta = ModelDeltas.Compare(ma, mb, sameBytes);

        var bothMesh = ModelFormats.IsMesh(ma.Format) && ModelFormats.IsMesh(mb.Format) && ma.IsAnalysed && mb.IsAnalysed;
        string? visualReason = null;
        if (!download) visualReason = "Viewing needs permission to download the files.";
        else if (!bothMesh) visualReason = "The visual comparison supports STL, OBJ and 3MF models that Keel could read.";
        else if (sameBytes) visualReason = "Both versions are the same file.";

        await audit.AppendAsync(Entry(subject, ModelCompareAction, AuditOutcome.Success, number, correlationId,
            ("a", a.Label), ("b", b.Label), ("shaA", a.Sha256), ("shaB", b.Sha256), ("sameGeometry", delta.SameGeometry.ToString()), ("comparable", delta.Comparable.ToString()),
            ("markings", head.Markings.BannerText())), ct);
        return Outcome<ModelComparison>.Ok(new ModelComparison(number, await NameOfAsync(head.MasterId, ct), head.Markings.BannerText(), head.Markings, Side(a, ma), Side(b, mb), delta, sameBytes,
            visualReason is null, visualReason));
    }

    private static ModelCompareSide Side(VersionRecord v, ModelMetrics m) =>
        new(v.IterationId, v.Label, v.State, v.CreatedAt, v.CreatedBy, v.Comment, v.FileId, v.FileName, v.SizeBytes, v.Sha256, m);

    // ---------------------------------------------------------------- revise (a locked, released revision becomes editable again as the next revision)

    /// <summary>
    /// Creates the next revision (B after A) of a RELEASED document from its latest iteration: iteration 1, initial state, the same files and description. The released
    /// revision stays exactly as it was. Needs Modify and a reason; refused while the document is checked out. Documents are not part of change control yet
    /// (ChangeObject says so), so this is a direct, audited action; ADR-0046 records the assumption (MDL-3).
    /// </summary>
    public Task<Outcome<DocumentDetail>> ReviseAsync(Subject subject, string number, string? reason, string? correlationId, CancellationToken ct = default) =>
        MutateAsync(subject, number, ReviseAction, correlationId, PlmAction.Modify, async (ctx, conn, tx) =>
        {
            var problem = ReviseProblem(ctx, reason);
            if (problem is not null) return problem;
            var next = await ReviseCoreAsync(ctx, subject, reason!.Trim(), conn, tx, ct);
            return Done(("from", ctx.Revision), ("revision", $"{next.Revision}.{next.LatestIteration.ToString(CultureInfo.InvariantCulture)}"), ("reason", reason.Trim()));
        }, ct);

    private static Step? ReviseProblem(MutationContext ctx, string? reason)
    {
        if (ctx.State != ReleasedState) return Fail($"Only a released revision is revised; revision {ctx.Revision} is {ctx.StateLabel}.");
        if (ctx.CheckoutUser is not null) return Fail($"Checked out by {ctx.CheckoutUserName ?? ctx.CheckoutUser}; check in or undo the check-out first.");
        if (string.IsNullOrWhiteSpace(reason)) return Invalid(["A reason is required to revise a released document."]);
        if (reason.Trim().Length > 500) return Invalid(["The reason must be at most 500 characters."]);
        return null;
    }

    private async Task<MutationContext> ReviseCoreAsync(MutationContext ctx, Subject subject, string reason, NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        var nextRevision = RevisionScheme.Next(ctx.Revision);
        var now = clock.GetUtcNow();
        var who = DisplayId(subject);
        var revisionId = Guid.CreateVersion7();
        var iterationId = Guid.CreateVersion7();
        var initial = ctx.Lifecycle.Initial.State;
        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE plm.document_revision SET is_latest = false WHERE id = @old;
            INSERT INTO plm.document_revision (id, master_id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by)
            VALUES (@rid, @m, @rev, @state, true, 1, @at, @by, @at, @by);
            INSERT INTO plm.document_iteration (id, revision_id, iteration, description, comment, created_at, created_by)
            SELECT @iid, @rid, 1, i.description, @comment, @at, @by FROM plm.document_iteration i WHERE i.id = @latest;
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("old", ctx.RevisionId);
            cmd.Parameters.AddWithValue("rid", revisionId);
            cmd.Parameters.AddWithValue("m", ctx.MasterId);
            cmd.Parameters.AddWithValue("rev", nextRevision);
            cmd.Parameters.AddWithValue("state", initial);
            cmd.Parameters.AddWithValue("at", now);
            cmd.Parameters.AddWithValue("by", who);
            cmd.Parameters.AddWithValue("iid", iterationId);
            cmd.Parameters.AddWithValue("comment", $"Revised from {ctx.Revision}.{ctx.LatestIteration.ToString(CultureInfo.InvariantCulture)}: {reason}");
            cmd.Parameters.AddWithValue("latest", ctx.LatestIterationId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var files = await ReadFilesAsync(conn, tx,
            "SELECT f.id, f.role, f.file_name, f.media_type, f.size_bytes, f.sha256, f.added_at, f.added_by FROM plm.document_file f WHERE f.iteration_id = @owner", ctx.LatestIterationId, ct);
        foreach (var f in files)
            await InsertFileAsync(conn, tx, "plm.document_file", "iteration_id", iterationId, f.Role, f.FileName, f.MediaType, new StoredContent(f.Sha256, f.SizeBytes), f.AddedAt, f.AddedBy, ct);
        var stateLabel = ctx.Lifecycle.State(initial)?.Label ?? initial;
        return new MutationContext(ctx.MasterId, revisionId, nextRevision, initial, stateLabel, 1, iterationId, files.Any(f => f.Role == FileRoles.Primary), ctx.Markings, ctx.Lifecycle, null, null);
    }

    // ---------------------------------------------------------------- upload new version (check out, upload, check in in one step)

    /// <summary>
    /// Adds a new version of a model document in one step: check-out, replace the primary file, check-in with a required change note, all in ONE transaction, so a
    /// failure leaves no check-out behind. The bytes go through the normal upload path first (size limit, malware scan, SHA-256, vault) and nothing is accepted from
    /// a caller who may not modify the document. A file with the same SHA-256 as the current version is refused. A released revision is refused unless
    /// <paramref name="reviseIfReleased"/> asks to revise it first, in the same transaction. <paramref name="expectedIteration"/> (the latest iteration the person
    /// saw) makes concurrent uploads safe: if someone else got in first, nothing is changed and the message says so.
    /// </summary>
    public async Task<Outcome<DocumentDetail>> UploadNewVersionAsync(Subject subject, string number, FileUpload upload, string? changeNote, bool reviseIfReleased, int? expectedIteration,
        string? correlationId, CancellationToken ct = default)
    {
        var note = changeNote?.Trim() ?? "";
        var errors = new List<string>();
        if (note.Length == 0) errors.Add("A change note is required.");
        if (note.Length > MaxChangeNoteLength) errors.Add($"The change note must be at most {MaxChangeNoteLength} characters.");
        errors.AddRange(ValidateUpload(upload, out var mediaType));
        if (errors.Count > 0) return Outcome<DocumentDetail>.Invalid(errors);

        // Nothing is read from the caller until the document is known to accept a new version (access, lock, check-out, expected version).
        var pre = await PrecheckNewVersionAsync(subject, number, reviseIfReleased, expectedIteration, correlationId, ct);
        if (pre is not null) return pre;

        StoredContent stored;
        try { stored = await vault.PutAsync(upload.Content, ct); }
        catch (UploadTooLargeException e) { return Outcome<DocumentDetail>.Invalid([e.Message]); }
        catch (UploadInfectedException e) { return await RefuseInfectedAsync<DocumentDetail>(subject, VersionUploadAction, number, upload.FileName, e, correlationId, ct); }
        catch (UploadNotScannedException e) { return await RefuseNotScannedAsync<DocumentDetail>(subject, VersionUploadAction, number, upload.FileName, e, correlationId, ct); }

        var result = await MutateAsync(subject, number, VersionUploadAction, correlationId, PlmAction.Modify, async (ctx, conn, tx) =>
        {
            var problem = NewVersionProblem(ctx, reviseIfReleased, expectedIteration);
            if (problem is not null) return problem;
            var previous = $"{ctx.Revision}.{ctx.LatestIteration.ToString(CultureInfo.InvariantCulture)}";
            var current = await ReadFilesAsync(conn, tx,
                "SELECT f.id, f.role, f.file_name, f.media_type, f.size_bytes, f.sha256, f.added_at, f.added_by FROM plm.document_file f WHERE f.iteration_id = @owner", ctx.LatestIterationId, ct);
            if (current.FirstOrDefault(f => f.Role == FileRoles.Primary) is { } primary && primary.Sha256 == stored.Sha256)
                return Fail($"This file is identical to the current version {previous} (same SHA-256), so no new version was created.");

            var revised = false;
            if (ctx.Lifecycle.IsLocked(ctx.State))
            {
                ctx = await ReviseCoreAsync(ctx, subject, $"New version uploaded: {Shorten(note)}", conn, tx, ct);
                revised = true;
            }
            var checkedOut = await CheckOutBodyAsync(subject, ctx, conn, tx, ct);
            if (checkedOut.Kind != OutcomeKind.Ok) return checkedOut;
            ctx = ctx with { CheckoutUser = subject.Id, CheckoutUserName = DisplayId(subject) };

            await using (var del = new NpgsqlCommand("DELETE FROM plm.document_checkout_file WHERE revision_id = @rid AND role = 'primary'", conn, tx))
            {
                del.Parameters.AddWithValue("rid", ctx.RevisionId);
                await del.ExecuteNonQueryAsync(ct);
            }
            await using (var dup = new NpgsqlCommand("SELECT 1 FROM plm.document_checkout_file WHERE revision_id = @rid AND lower(file_name) = lower(@n)", conn, tx))
            {
                dup.Parameters.AddWithValue("rid", ctx.RevisionId);
                dup.Parameters.AddWithValue("n", upload.FileName);
                if (await dup.ExecuteScalarAsync(ct) is not null) return Fail($"The document already has an attachment named {upload.FileName}; rename the file.");
            }
            await InsertFileAsync(conn, tx, "plm.document_checkout_file", "revision_id", ctx.RevisionId, FileRoles.Primary, upload.FileName, mediaType!, stored, clock.GetUtcNow(), DisplayId(subject), ct);

            var checkedIn = await CheckInBodyAsync(subject, ctx, note, conn, tx, ct);
            if (checkedIn.Kind != OutcomeKind.Ok) return checkedIn;
            return Done([.. checkedIn.Details, ("previous", previous), ("revised", revised.ToString()), ("file", upload.FileName), ("sha256", stored.Sha256),
                ("size", stored.SizeBytes.ToString(CultureInfo.InvariantCulture))]);
        }, ct);

        if (result.IsOk) await MeasureNewIterationAsync(result.Value!.Id, result.Value.Iteration.Id);
        return result;
    }

    private static string Shorten(string s) => s.Length <= 120 ? s : s[..120];

    /// <summary>The conditions shared by the cheap pre-check and the check under the row lock.</summary>
    private static Step? NewVersionProblem(MutationContext ctx, bool reviseIfReleased, int? expectedIteration)
    {
        if (ctx.CheckoutUser is not null)
            return Fail($"The document is checked out by {ctx.CheckoutUserName ?? ctx.CheckoutUser}. Check in or undo that check-out first; nothing was uploaded.");
        if (expectedIteration is { } expected && expected != ctx.LatestIteration)
            return Fail($"The document changed while you were choosing the file: the current version is now {ctx.Revision}.{ctx.LatestIteration.ToString(CultureInfo.InvariantCulture)}. Reload and try again; nothing was uploaded.");
        if (ctx.Lifecycle.IsLocked(ctx.State))
        {
            if (ctx.State != ReleasedState)
                return Fail($"Revision {ctx.Revision} is {ctx.StateLabel} and locked; it cannot take a new version.");
            if (!reviseIfReleased)
                return Fail($"Revision {ctx.Revision} is {ctx.StateLabel} and locked. Revise it first (this creates revision {RevisionScheme.Next(ctx.Revision)}), then upload the new version.");
        }
        return null;
    }

    private async Task<Outcome<DocumentDetail>?> PrecheckNewVersionAsync(Subject subject, string number, bool reviseIfReleased, int? expectedIteration, string? correlationId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (head, denied) = await AuthorizeAsync(conn, tx, subject, number, VersionUploadAction, correlationId, PlmAction.Modify, forUpdate: false, allowAdminWithoutModify: false, ct);
        if (denied is not null) return denied;
        var lifecycle = await Lifecycle.LoadAsync(conn, tx, head!.LifecycleId, ct) ?? throw new InvalidOperationException($"Lifecycle {head.LifecycleId} missing.");
        var ctx = await LoadMutationContextAsync(conn, tx, head, lifecycle, ct);
        await tx.RollbackAsync(ct);
        var problem = NewVersionProblem(ctx, reviseIfReleased, expectedIteration);
        if (problem is null) return null;
        return problem.Kind == OutcomeKind.Invalid ? Outcome<DocumentDetail>.Invalid(problem.Errors) : Outcome<DocumentDetail>.Conflict(problem.Errors[0]);
    }

    // ---------------------------------------------------------------- restore

    /// <summary>
    /// Restores a version: creates a NEW iteration whose files and description equal those of <paramref name="iterationId"/>, with the change note
    /// "Restored from A.2". History is never rewritten. It runs the same check-out and check-in steps as every other save, in one transaction.
    /// A locked (released) revision refuses it: revise first. Restoring content identical to the current version is refused.
    /// </summary>
    public async Task<Outcome<DocumentDetail>> RestoreModelVersionAsync(Subject subject, string number, Guid iterationId, int? expectedIteration, string? correlationId, CancellationToken ct = default)
    {
        var result = await MutateAsync(subject, number, VersionRestoreAction, correlationId, PlmAction.Modify, async (ctx, conn, tx) =>
        {
            var problem = NewVersionProblem(ctx, reviseIfReleased: false, expectedIteration);
            if (problem is not null) return problem;
            var versions = await LoadVersionRecordsAsync(conn, tx, ctx.MasterId, ct);
            var target = versions.FirstOrDefault(v => v.IterationId == iterationId);
            if (target is null) return Invalid(["That version does not exist or is not available."]);
            var files = await ReadFilesAsync(conn, tx,
                "SELECT f.id, f.role, f.file_name, f.media_type, f.size_bytes, f.sha256, f.added_at, f.added_by FROM plm.document_file f WHERE f.iteration_id = @owner", target.IterationId, ct);
            if (files.Count == 0) return Fail($"Version {target.Label} has no files to restore.");
            var current = await ReadFilesAsync(conn, tx,
                "SELECT f.id, f.role, f.file_name, f.media_type, f.size_bytes, f.sha256, f.added_at, f.added_by FROM plm.document_file f WHERE f.iteration_id = @owner", ctx.LatestIterationId, ct);
            if (SameFiles(files, current))
                return Fail($"Version {target.Label} has the same files as the current version {ctx.Revision}.{ctx.LatestIteration.ToString(CultureInfo.InvariantCulture)}, so there is nothing to restore.");
            string? description;
            await using (var d = new NpgsqlCommand("SELECT description FROM plm.document_iteration WHERE id = @id", conn, tx))
            {
                d.Parameters.AddWithValue("id", target.IterationId);
                description = (string?)await d.ExecuteScalarAsync(ct);
            }

            var checkedOut = await CheckOutBodyAsync(subject, ctx, conn, tx, ct);
            if (checkedOut.Kind != OutcomeKind.Ok) return checkedOut;
            var previous = $"{ctx.Revision}.{ctx.LatestIteration.ToString(CultureInfo.InvariantCulture)}";
            ctx = ctx with { CheckoutUser = subject.Id, CheckoutUserName = DisplayId(subject) };
            await using (var clear = new NpgsqlCommand("DELETE FROM plm.document_checkout_file WHERE revision_id = @rid; UPDATE plm.document_checkout SET description = @d WHERE revision_id = @rid", conn, tx))
            {
                clear.Parameters.AddWithValue("rid", ctx.RevisionId);
                clear.Parameters.AddWithValue("d", description ?? "");
                await clear.ExecuteNonQueryAsync(ct);
            }
            foreach (var f in files)
                await InsertFileAsync(conn, tx, "plm.document_checkout_file", "revision_id", ctx.RevisionId, f.Role, f.FileName, f.MediaType, new StoredContent(f.Sha256, f.SizeBytes), f.AddedAt, f.AddedBy, ct);
            var checkedIn = await CheckInBodyAsync(subject, ctx, $"Restored from {target.Label}", conn, tx, ct);
            if (checkedIn.Kind != OutcomeKind.Ok) return checkedIn;
            return Done([.. checkedIn.Details, ("restoredFrom", target.Label), ("previous", previous), ("sha256", files.FirstOrDefault(f => f.Role == FileRoles.Primary)?.Sha256)]);
        }, ct);
        if (result.IsOk) await MeasureNewIterationAsync(result.Value!.Id, result.Value.Iteration.Id);
        return result;
    }

    private static bool SameFiles(List<DocumentFileView> a, List<DocumentFileView> b) =>
        a.Count == b.Count && a.OrderBy(f => f.Role).ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .Zip(b.OrderBy(f => f.Role).ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase))
            .All(p => p.First.Role == p.Second.Role && string.Equals(p.First.FileName, p.Second.FileName, StringComparison.OrdinalIgnoreCase) && p.First.Sha256 == p.Second.Sha256);

    // ---------------------------------------------------------------- download selected versions

    /// <summary>
    /// A ZIP of the files of the selected versions (at most <see cref="MaxVersionsInZip"/>), each under its version label, with the markings cover sheet, MARKINGS.txt
    /// and VERSIONS.txt. Same rules as a download: Read and Download, 404 when unreadable, quarantined versions are not available, every object is verified while it
    /// streams, and the delivery is audited.
    /// </summary>
    public async Task<Outcome<MarkedDownload>> DownloadVersionsAsync(Subject subject, string number, IReadOnlyList<Guid> iterationIds, string? correlationId, CancellationToken ct = default)
    {
        var ids = iterationIds.Distinct().ToList();
        if (ids.Count == 0) return Outcome<MarkedDownload>.Invalid(["Choose at least one version."]);
        if (ids.Count > MaxVersionsInZip) return Outcome<MarkedDownload>.Invalid([$"Choose at most {MaxVersionsInZip} versions."]);

        Head head;
        List<(VersionRecord Version, List<DocumentFileView> Files)> picked = [];
        string name;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            var loaded = await LoadHeadAsync(conn, tx, number, forUpdate: false, ct);
            if (loaded is null) return Outcome<MarkedDownload>.NotFound();
            head = loaded;
            var read = AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Read);
            var decision = read.Permitted ? AccessPolicy.Evaluate(subject, head.Markings, PlmAction.Download) : read;
            if (!decision.Permitted)
            {
                await audit.AppendAsync(tx, Entry(subject, VersionsDownloadAction, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", decision.Reasons))), ct);
                await tx.CommitAsync(ct);
                return read.Permitted ? Outcome<MarkedDownload>.Forbidden(decision.Reasons) : Outcome<MarkedDownload>.NotFound();
            }
            if (await IsDisposedAsync(conn, tx, head.MasterId, ct))
            {
                await tx.CommitAsync(ct);
                return Outcome<MarkedDownload>.Conflict("The content of this document was disposed of under a retention schedule.");
            }
            var versions = await LoadVersionRecordsAsync(conn, tx, head.MasterId, ct);
            foreach (var id in ids)
            {
                var v = versions.FirstOrDefault(x => x.IterationId == id);
                if (v is null) { await tx.CommitAsync(ct); return Outcome<MarkedDownload>.Invalid(["One of the versions does not exist or is not available."]); }
                var files = await ReadFilesAsync(conn, tx,
                    "SELECT f.id, f.role, f.file_name, f.media_type, f.size_bytes, f.sha256, f.added_at, f.added_by FROM plm.document_file f WHERE f.iteration_id = @owner", id, ct);
                if (files.Count == 0) { await tx.CommitAsync(ct); return Outcome<MarkedDownload>.Invalid([$"Version {v.Label} has no files."]); }
                if (await QuarantinedAmongAsync(conn, tx, head.MasterId, [.. files.Select(f => f.Sha256)], ct) is not null)
                { await tx.CommitAsync(ct); return Outcome<MarkedDownload>.Invalid(["One of the versions does not exist or is not available."]); }
                picked.Add((v, files));
            }
            await tx.CommitAsync(ct);
        }
        name = await NameOfAsync(head.MasterId, ct);
        picked.Sort((x, y) => RevisionScheme.ToIndex(x.Version.Revision) != RevisionScheme.ToIndex(y.Version.Revision)
            ? RevisionScheme.ToIndex(x.Version.Revision).CompareTo(RevisionScheme.ToIndex(y.Version.Revision)) : x.Version.Iteration.CompareTo(y.Version.Iteration));

        var last = picked[^1];
        var coverFile = last.Files.FirstOrDefault(f => f.Role == FileRoles.Primary) ?? last.Files[0];
        var ctx = new MarkingContext(head.Markings, number, name, last.Version.Label, coverFile.FileName, coverFile.MediaType, coverFile.SizeBytes, coverFile.Sha256, DisplayId(subject), clock.GetUtcNow());
        var entries = picked.SelectMany(p => p.Files.Select(f => new VersionZipEntry(p.Version.Label, f.FileName, f.Sha256, f.SizeBytes, p.Version.CreatedBy, p.Version.CreatedAt, p.Version.Comment))).ToList();
        var manifest = VersionsManifest(number, name, entries, ctx);
        try
        {
            foreach (var e in entries) await vault.VerifyAsync(e.Sha256, e.SizeBytes, ct);
        }
        catch (VaultIntegrityException ex)
        {
            await audit.AppendAsync(Entry(subject, VersionsDownloadAction, AuditOutcome.Failure, number, correlationId, ("markings", ctx.Banner), ("error", ex.Message)), CancellationToken.None);
            throw;
        }
        var download = new MarkedDownload($"{number}-versions.zip", "application/zip", ctx.Banner, "zip", async (s, c) =>
            await MarkedZip.WriteVersionsAsync(s, ctx, manifest, entries.Select(e => new MarkedZip.Member($"{e.Label}/{e.FileName}", e.SizeBytes, e.Sha256)).ToList(),
                (m, token) => vault.OpenReadAsync(m.Sha256, m.SizeBytes, token), c));
        await audit.AppendAsync(Entry(subject, VersionsDownloadAction, AuditOutcome.Success, number, correlationId,
            ("versions", string.Join(",", picked.Select(p => p.Version.Label))), ("files", entries.Count.ToString(CultureInfo.InvariantCulture)),
            ("sha256", string.Join(",", entries.Select(e => e.Sha256))), ("markings", ctx.Banner), ("format", "zip")), ct);
        return Outcome<MarkedDownload>.Ok(download);
    }

    private sealed record VersionZipEntry(string Label, string FileName, string Sha256, long SizeBytes, string CreatedBy, DateTimeOffset CreatedAt, string? Comment);

    private static string VersionsManifest(string number, string name, List<VersionZipEntry> entries, MarkingContext ctx)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{ctx.Banner}\r\nKeel PLM: versions of {number} {name}\r\nRetrieved {ctx.RetrievedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC by {ctx.RetrievedBy}\r\n\r\n");
        foreach (var e in entries)
            sb.Append(CultureInfo.InvariantCulture, $"{e.Label}/{e.FileName}\r\n  SHA-256 {e.Sha256}\r\n  {e.SizeBytes} bytes, checked in {e.CreatedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC by {e.CreatedBy}\r\n  Change note: {e.Comment ?? "none"}\r\n");
        sb.Append("\r\n").Append(ctx.Banner).Append("\r\n");
        return sb.ToString();
    }
}
