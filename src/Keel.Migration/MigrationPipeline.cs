using Keel.Audit;
using Keel.Core;
using Keel.Migration.Canonical;
using Keel.Migration.Load;
using Keel.Migration.Mapping;
using Keel.Migration.Reconcile;
using Keel.Migration.Reports;
using Keel.Migration.Sources;
using Npgsql;

namespace Keel.Migration;

/// <summary>A package read, mapped and validated: everything the later steps (report, dry-run, load, reconcile) work from.</summary>
public sealed class PreparedMigration : IDisposable
{
    public required PackageSource Package { get; init; }
    public required PackageManifest Manifest { get; init; }
    public required string Format { get; init; }
    public required StagingSink Sink { get; init; }
    public required MappingConfig Config { get; init; }
    public required string MappingSha256 { get; init; }
    public required ReferenceData Reference { get; init; }
    public required Plan Plan { get; init; }
    public void Dispose() => Package.Dispose();
}

public sealed record InspectResult(string Format, PackageManifest Manifest, SourceInventory Inventory, IReadOnlyList<Problem> Problems);

/// <summary>The load was refused before anything was written: the dry-run report does not authorize it.</summary>
public sealed class LoadRefusedException(string message) : Exception(message);

/// <summary>
/// Parse -> canonical staging model -> map -> validate -> dry-run -> load -> reconcile -> report (ADR-0023). The database is needed for
/// everything except <see cref="InspectAsync"/> and the offline validation of <see cref="PrepareAsync(string, string?, MappingConfig, ReferenceData?, CancellationToken)"/>.
/// </summary>
public sealed class MigrationPipeline(NpgsqlDataSource? ds, IAuditLog? audit, IContentStore? store, TimeProvider clock, IClassificationLoader? classification = null)
{
    /// <summary>Reads the package into the staging model only to count it (streaming: nothing is retained).</summary>
    public static async Task<InspectResult> InspectAsync(string path, string? format, CancellationToken ct)
    {
        using var package = PackageSource.Open(path);
        var fmt = format ?? PackageReaders.Detect(package);
        var sink = new StagingSink(retain: false);
        await PackageReaders.Create(fmt).ReadAsync(package, sink, ct);
        await ClassificationHierarchyReader.ReadAsync(package, sink, ct);
        return new InspectResult(fmt, package.ComputeManifest(), sink.Inventory, sink.Problems);
    }

    /// <summary>Reads, maps and validates a package. Reference data comes from the database, or from built-in defaults when <paramref name="offline"/> is given.</summary>
    public async Task<PreparedMigration> PrepareAsync(string path, string? format, MappingConfig cfg, CancellationToken ct) =>
        await PrepareCoreAsync(path, format, cfg, null, ct);

    public static async Task<PreparedMigration> PrepareAsync(string path, string? format, MappingConfig cfg, ReferenceData? offline, CancellationToken ct) =>
        await new MigrationPipeline(null, null, null, TimeProvider.System).PrepareCoreAsync(path, format, cfg, offline ?? ReferenceData.Defaults(), ct);

    private async Task<PreparedMigration> PrepareCoreAsync(string path, string? format, MappingConfig cfg, ReferenceData? offline, CancellationToken ct)
    {
        if (cfg.Effectivity.RevisionLevel)
            throw new NotSupportedException("Revision-level effectivity (the effectivity a change notice gives a released revision) is not imported: none of the supported source formats specifies it (FUP-3). " +
                "Line-level effectivity on BOM lines is imported with effectivity.load; set effectivity.revisionLevel to false.");
        if (cfg.Classification.Load && offline is null && classification is null)
            throw new NotSupportedException("classification.load needs an IClassificationLoader (Keel.Admin's MigrationClassificationLoader); this host did not provide one.");
        var package = PackageSource.Open(path);
        try
        {
            var fmt = format ?? PackageReaders.Detect(package);
            var sink = new StagingSink(retain: true);
            var reader = PackageReaders.Create(fmt);
            await reader.ReadAsync(package, sink, ct);
            await ClassificationHierarchyReader.ReadAsync(package, sink, ct);
            if (string.IsNullOrEmpty(sink.Package.SourceSystem)) sink.Package.SourceSystem = reader.DefaultSourceSystem;
            var manifest = package.ComputeManifest();
            var rd = offline ?? await ReferenceData.LoadAsync(ds ?? throw new InvalidOperationException("A database connection is required."), ct, cfg.Classification.Load ? classification : null);
            var existing = offline is null ? await ExistingKeysAsync(sink.Package, ct) : new ExistingKeys();
            var plan = new Mapper(cfg, rd, manifest) { Existing = existing }.Map(sink.Package);
            plan.Problems.InsertRange(0, sink.Problems);
            plan.SourceErrors = sink.Problems.Count(p => p.Severity == Severity.Error);
            return new PreparedMigration { Package = package, Manifest = manifest, Format = fmt, Sink = sink, Config = cfg, MappingSha256 = cfg.Hash(), Reference = rd, Plan = plan };
        }
        catch
        {
            package.Dispose();
            throw;
        }
    }

    /// <summary>Numbers that exist in Keel and are not the same source objects (those mapped in the id map are fine: they are the objects of an earlier run).</summary>
    private async Task<ExistingKeys> ExistingKeysAsync(CanonicalPackage pkg, CancellationToken ct)
    {
        var keys = new ExistingKeys();
        var system = pkg.SourceSystem;
        if (string.IsNullOrWhiteSpace(system)) return keys;
        var mapped = new Dictionary<(string, Guid), string>();
        await using (var cmd = ds!.CreateCommand("SELECT entity_type, keel_id, source_id FROM mig.id_map WHERE source_system = @s"))
        {
            cmd.Parameters.AddWithValue("s", system);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) mapped[(r.GetString(0), r.GetGuid(1))] = r.GetString(2);
        }
        async Task Check(string table, string type, IEnumerable<(string Number, string SourceId)> numbers, HashSet<string> into)
        {
            foreach (var chunk in numbers.Where(n => !string.IsNullOrWhiteSpace(n.Number)).Chunk(2000))
            {
                var sourceByNumber = chunk.GroupBy(c => c.Number).ToDictionary(g => g.Key, g => g.Select(x => x.SourceId).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
#pragma warning disable CA2100 // table name is chosen from a fixed list below
                await using var cmd = ds.CreateCommand($"SELECT number, id FROM {table} WHERE number = ANY(@n)");
#pragma warning restore CA2100
                cmd.Parameters.AddWithValue("n", sourceByNumber.Keys.ToArray());
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var number = r.GetString(0);
                    if (!(mapped.TryGetValue((type, r.GetGuid(1)), out var src) && sourceByNumber[number].Contains(src))) into.Add(number);
                }
            }
        }
        await Check("plm.part_master", "part", pkg.Items.Select(i => (i.Number, i.SourceId)), keys.PartNumbers);
        await Check("plm.document_master", "document", pkg.Documents.Select(i => (i.Number, i.SourceId)), keys.DocumentNumbers);
        await Check("chg.change", "change", pkg.Changes.Select(i => (i.Number, i.SourceId)), keys.ChangeNumbers);
        return keys;
    }

    /// <summary>Parse, map and validate only; no database writes.</summary>
    public MigrationReport Validate(PreparedMigration p, LoadOptions opt) => ReportBuilder.Build(p, "validate", null, opt, clock.GetUtcNow());

    /// <summary>
    /// Runs the full load simulation inside one transaction that is ALWAYS rolled back: same code, same errors and counts as a real load, nothing kept.
    /// Files are checked (hash, size, vault presence) but not uploaded.
    /// </summary>
    public async Task<MigrationReport> DryRunAsync(PreparedMigration p, LoadOptions opt, CancellationToken ct)
    {
        opt.DryRun = true;
        var pre = ReportBuilder.Build(p, "dry-run", null, opt, clock.GetUtcNow());
        if (!pre.Succeeded) return pre;
        var importer = new Importer(ds ?? throw new InvalidOperationException("A database connection is required."), audit!, store, p.Reference, clock, classification);
        ImportResult result;
        try { result = await importer.RunAsync(p, opt, ct); }
        catch (LoadAbortedException) { result = importer.Result; }
        var report = ReportBuilder.Build(p, "dry-run", result, opt, clock.GetUtcNow());
        report.EstimatedLoadSeconds = result.Elapsed.TotalSeconds * 1.25 + result.BytesUploaded / (30.0 * 1024 * 1024);
        report.EstimatedUploadBytes = result.BytesUploaded;
        return report;
    }

    /// <summary>Loads the package. Refused unless the supplied dry-run report succeeded for exactly this package and mapping (ADR-0023).</summary>
    public async Task<(MigrationReport Report, ImportResult Result)> LoadAsync(PreparedMigration p, MigrationReport dryRun, LoadOptions opt, CancellationToken ct)
    {
        opt.DryRun = false;
        if (dryRun.Kind != "dry-run") throw new LoadRefusedException($"The report is a '{dryRun.Kind}' report; a successful dry-run report is required.");
        if (!dryRun.HashIsValid()) throw new LoadRefusedException("The dry-run report has been modified (its hash does not match its content).");
        if (dryRun.Schema != MigrationReport.SchemaVersion) throw new LoadRefusedException("The dry-run report was written by an incompatible version of the tool.");
        if (!dryRun.Succeeded) throw new LoadRefusedException("The dry-run did not succeed: " + string.Join(" ", dryRun.Blocking));
        if (dryRun.PackageSha256 != p.Manifest.Sha256) throw new LoadRefusedException("The package changed since the dry-run (package SHA-256 differs). Run the dry-run again.");
        if (dryRun.MappingSha256 != p.MappingSha256) throw new LoadRefusedException("The mapping changed since the dry-run (mapping SHA-256 differs). Run the dry-run again.");
        if (dryRun.Quarantine != opt.Quarantine) throw new LoadRefusedException($"The dry-run was made with quarantine {(dryRun.Quarantine ? "on" : "off")}; the load must use the same setting.");
        var pre = ReportBuilder.Build(p, "dry-run", null, opt, clock.GetUtcNow());
        if (!pre.Succeeded) throw new LoadRefusedException("The package no longer validates against the target: " + string.Join(" ", pre.Blocking));
        if (pre.Hash != dryRun.Hash) throw new LoadRefusedException("The validation result differs from the dry-run report (the target database changed since). Run the dry-run again.");
        opt.DryRunHash = dryRun.Hash;
        var importer = new Importer(ds!, audit!, store ?? throw new InvalidOperationException("A vault is required to load files."), p.Reference, clock, classification);
        ImportResult result;
        try { result = await importer.RunAsync(p, opt, ct); }
        catch (LoadAbortedException) { result = importer.Result; var failed = ReportBuilder.Build(p, "load", result, opt, clock.GetUtcNow()); failed.Blocking.Add("The load stopped; rerun to complete it."); failed.Succeeded = false; throw new LoadFailedException(failed); }
        var report = ReportBuilder.Build(p, "load", result, opt, clock.GetUtcNow());
        await using (var conn = await ds!.OpenConnectionAsync(ct))
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await new ImportWriter(conn) { Tx = tx }.InsertReportAsync(result.BatchId, "load", report.ToJson(), clock.GetUtcNow(), ct);
            await tx.CommitAsync(ct);
        }
        return (report, result);
    }

    public async Task<ReconcileReport> ReconcileAsync(PreparedMigration p, ReconcileOptions opt, CancellationToken ct)
    {
        var report = await new Reconciler(ds ?? throw new InvalidOperationException("A database connection is required."), store, clock, classification).RunAsync(p, opt, ct);
        if (report.BatchId is { } batch)
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await new ImportWriter(conn) { Tx = tx }.InsertReportAsync(batch, "reconcile", report.ToJson(), clock.GetUtcNow(), ct);
            await tx.CommitAsync(ct);
        }
        return report;
    }
}

/// <summary>A load stopped after committing part of its work; <see cref="Report"/> says what happened. A rerun completes it.</summary>
public sealed class LoadFailedException(MigrationReport report) : Exception("The load stopped before it finished. Committed work is kept; rerun the same command to complete it.")
{
    public MigrationReport Report { get; } = report;
}
