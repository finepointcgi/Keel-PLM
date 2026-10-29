using System.Globalization;
using System.Text.Json;
using Keel.Audit;
using Keel.Core;
using Keel.Core.Reports;
using Keel.Reports.Pdf;
using Npgsql;

namespace Keel.Reports;

public sealed record ArtifactView(string Number, string Kind, string SubjectKind, string SubjectKey, string Title, string Sha256, long SizeBytes, string Banner, DateTimeOffset CreatedAt, string CreatedBy);

public sealed record ArtifactDownload(string FileName, string Banner, string Sha256, long SizeBytes, Stream Content);

/// <summary>
/// Release packages (ADR-0044). Generation reads everything through <see cref="IReleasePackageSource"/> as the caller, refuses (never redacts) when an included object is
/// unreadable, prints the aggregate marking on every page, stores the PDF content-addressed in the vault and records an immutable artifact row. Download repeats the
/// check against every included object, so a package never reaches someone who could not have read all of it.
/// </summary>
public sealed class ReleasePackageService(
    NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, IReleasePackageSource source, IContentStore store, IContentReader reader,
    Func<Markings, IReadOnlyList<string>> notices, Action ensureFonts)
{
    private static readonly SemaphoreSlim Slots = new(ReportsLimits.MaxConcurrentPackages);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record ObjRef(string T, string K);

    public async Task<Outcome<ArtifactView>> GenerateAsync(Subject subject, string kind, string key, string? correlationId, CancellationToken ct = default)
    {
        kind = (kind ?? "").Trim().ToLowerInvariant();
        key = (key ?? "").Trim();
        if (kind is not ("part" or "ecn" or "baseline") || key.Length is 0 or > 64) return Outcome<ArtifactView>.Invalid(["Choose a part, an ECN or a baseline and give its number."]);
        if (!await Slots.WaitAsync(0, ct)) return Outcome<ArtifactView>.Unavailable("Other release packages are being generated. Try again in a minute.");
        try
        {
            var collected = await source.CollectAsync(subject, kind, key, correlationId, ct);
            if (!collected.IsOk)
            {
                if (collected.Kind is OutcomeKind.Forbidden)
                    await audit.AppendAsync(ReportsAudit.Entry(subject, "output.package.generate", AuditOutcome.Denied, kind, key, correlationId, ("reasons", string.Join(",", collected.Errors))), ct);
                return new Outcome<ArtifactView>(collected.Kind, null, collected.Errors);
            }
            var c = collected.Value!;
            if (c.Bom.Count > ReportsLimits.MaxPackageBomLines)
                return Outcome<ArtifactView>.Invalid([$"The structure has {c.Bom.Count} lines; a release package holds at most {ReportsLimits.MaxPackageBomLines}. Export the BOM to Excel instead."]);
            var marking = PackageMarking.For([.. c.Objects.Select(o => o.Markings), .. c.Bom.Select(l => l.Markings), .. c.Documents.Select(d => d.Markings)], notices);
            var now = clock.GetUtcNow();
            long seq;
            await using (var conn0 = await dataSource.OpenConnectionAsync(ct))
            await using (var cmd0 = new NpgsqlCommand("SELECT nextval('rpt.artifact_number_seq')", conn0)) seq = (long)(await cmd0.ExecuteScalarAsync(ct))!;
            var number = "RPK-" + seq.ToString("D6", CultureInfo.InvariantCulture);
            var pdf = ReleasePackagePdf.Render(c, marking, new PackageIdentity(number, now, subject.DisplayName), ensureFonts);
            ContentInfo stored;
            try { stored = await store.PutAsync(new MemoryStream(pdf, writable: false), ct); }
            catch (ContentRejectedException e) { return Outcome<ArtifactView>.Unprocessable(e.Message); }

            var objects = c.Objects.Select(o => new ObjRef(o.ObjectType, o.Key))
                .Concat(c.Documents.Select(d => new ObjRef("document", d.Number)))
                .Distinct().Take(20000).ToList();
            var id = Guid.CreateVersion7();
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var ins = new NpgsqlCommand(
                """
                INSERT INTO rpt.artifact (id, number, kind, subject_kind, subject_key, title, sha256, size_bytes, banner, objects, created_at, created_by, created_by_sub, correlation_id)
                VALUES (@id, @n, 'release_package', @sk, @key, @title, @sha, @size, @banner, @objs::jsonb, @at, @by, @sub, @corr)
                """, conn, tx))
            {
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("n", number);
                ins.Parameters.AddWithValue("sk", kind);
                ins.Parameters.AddWithValue("key", key);
                ins.Parameters.AddWithValue("title", Trim(c.Title, 300));
                ins.Parameters.AddWithValue("sha", stored.Sha256);
                ins.Parameters.AddWithValue("size", stored.SizeBytes);
                ins.Parameters.AddWithValue("banner", marking.Banner);
                ins.Parameters.AddWithValue("objs", JsonSerializer.Serialize(objects, Json));
                ins.Parameters.AddWithValue("at", now);
                ins.Parameters.AddWithValue("by", subject.Username ?? subject.DisplayName);
                ins.Parameters.AddWithValue("sub", subject.Id);
                ins.Parameters.AddWithValue("corr", (object?)correlationId ?? DBNull.Value);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await audit.AppendAsync(tx, ReportsAudit.Entry(subject, "output.package.generate", AuditOutcome.Success, "output", number, correlationId,
                ("kind", kind), ("subject", key), ("sha256", stored.Sha256), ("bytes", stored.SizeBytes.ToString(CultureInfo.InvariantCulture)),
                ("objects", objects.Count.ToString(CultureInfo.InvariantCulture)), ("banner", marking.Banner)), ct);
            await tx.CommitAsync(ct);
            return Outcome<ArtifactView>.Ok(new ArtifactView(number, "release_package", kind, key, Trim(c.Title, 300), stored.Sha256, stored.SizeBytes, marking.Banner, now, subject.Username ?? subject.DisplayName));
        }
        finally { Slots.Release(); }
    }

    /// <summary>The packages of one subject that the caller may read completely (a package with any unreadable object is not listed, not even as a placeholder).</summary>
    public async Task<IReadOnlyList<ArtifactView>> ListAsync(Subject subject, string kind, string key, CancellationToken ct = default)
    {
        var rows = new List<(ArtifactView View, List<ObjRef> Objects)>();
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand(
            "SELECT number, kind, subject_kind, subject_key, title, sha256, size_bytes, banner, created_at, created_by, objects::text FROM rpt.artifact WHERE subject_kind = @k AND subject_key = @key ORDER BY created_at DESC LIMIT 50", conn))
        {
            cmd.Parameters.AddWithValue("k", (kind ?? "").Trim().ToLowerInvariant());
            cmd.Parameters.AddWithValue("key", (key ?? "").Trim());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add((new ArtifactView(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64(6), r.GetString(7), r.GetFieldValue<DateTimeOffset>(8), r.GetString(9)),
                    JsonSerializer.Deserialize<List<ObjRef>>(r.GetString(10), Json) ?? []));
        }
        var list = new List<ArtifactView>();
        foreach (var (view, objects) in rows)
            if (await source.CanReadAllAsync(subject, [.. objects.Select(o => new PackageObjectRef(o.T, o.K))], ct)) list.Add(view);
        return list;
    }

    public async Task<Outcome<ArtifactDownload>> DownloadAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        string sha, banner; long size; List<ObjRef> objects;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand("SELECT sha256, size_bytes, banner, objects::text FROM rpt.artifact WHERE number = @n", conn))
        {
            cmd.Parameters.AddWithValue("n", number ?? "");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Outcome<ArtifactDownload>.NotFound();
            sha = r.GetString(0); size = r.GetInt64(1); banner = r.GetString(2);
            objects = JsonSerializer.Deserialize<List<ObjRef>>(r.GetString(3), Json) ?? [];
        }
        if (!await source.CanReadAllAsync(subject, [.. objects.Select(o => new PackageObjectRef(o.T, o.K))], ct))
        {
            await audit.AppendAsync(ReportsAudit.Entry(subject, "output.package.download", AuditOutcome.Denied, "output", number, correlationId, ("reasons", "package.object_not_readable")), ct);
            return Outcome<ArtifactDownload>.NotFound(); // the same answer as for an unknown package
        }
        var stream = await reader.OpenReadAsync(sha, size, ct);
        await audit.AppendAsync(ReportsAudit.Entry(subject, "output.package.download", AuditOutcome.Success, "output", number, correlationId, ("sha256", sha)), ct);
        return Outcome<ArtifactDownload>.Ok(new ArtifactDownload($"{number}.pdf", banner, sha, size, stream));
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}
