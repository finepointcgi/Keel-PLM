using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Keel.Audit;
using Keel.Core;
using Keel.Migration.Canonical;
using Keel.Migration.Mapping;
using Keel.Migration.Sources;
using Npgsql;

namespace Keel.Migration.Load;

/// <summary>Raised by <see cref="LoadOptions.Fault"/> to simulate a crash mid-load; never caught by the importer.</summary>
public sealed class FaultInjectionException(string message) : Exception(message);

/// <summary>The load stopped because an object failed and quarantine mode is off. Committed chunks stay; a rerun completes the rest.</summary>
public sealed class LoadAbortedException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record FaultPoint(string Stage, int Index, string SourceId);

public sealed class LoadOptions
{
    public string BatchName { get; set; } = "migration";
    public string Operator { get; set; } = Environment.UserName;
    public bool Quarantine { get; set; }
    public bool DryRun { get; set; }
    /// <summary>Objects (part chains, documents, change trees) per transaction.</summary>
    public int ChunkSize { get; set; } = 100;
    public string? DryRunHash { get; set; }
    /// <summary>Called before each object is written; throw <see cref="FaultInjectionException"/> to simulate a crash.</summary>
    public Func<FaultPoint, Task>? Fault { get; set; }
    public Action<string>? Progress { get; set; }
}

public sealed class ImportResult
{
    public Guid BatchId { get; set; }
    public string Status { get; set; } = "";
    public SortedDictionary<string, long> Created { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> Skipped { get; set; } = new(StringComparer.Ordinal);
    public List<Problem> Problems { get; set; } = [];
    public List<(string EntityType, string SourceId, string Code, string Message)> Quarantined { get; set; } = [];
    public long AuditEventsWritten { get; set; }
    public long FilesUploaded { get; set; }
    public long FilesAlreadyInVault { get; set; }
    public long BytesUploaded { get; set; }
    public TimeSpan Elapsed { get; set; }
    public int Conflicts => Problems.Count(p => p.Code == "LOAD-CONFLICT");
}

/// <summary>
/// The load phase (ADR-0023). Runs the same code for a dry-run (one transaction, always rolled back, vault untouched) and a real load
/// (chunked transactions, resumable). Per object, a savepoint isolates failures: with quarantine the object is listed and skipped, without it
/// the load stops (earlier chunks stay committed; a rerun completes the rest, because everything written is recorded in <c>mig.id_map</c>).
/// </summary>
internal sealed class Importer(NpgsqlDataSource ds, IAuditLog audit, IContentStore? store, ReferenceData rd, TimeProvider clock)
{
    private sealed record Mapped(Guid Id, string Hash);

    private NpgsqlConnection _conn = null!;
    private ImportWriter _w = null!;
    private NpgsqlTransaction? _tx;
    private PreparedMigration _prep = null!;
    private LoadOptions _opt = null!;
    private ImportResult _res = null!;
    private Guid _batch;
    private string _system = "";
    private ActorSnapshot _actor = null!;
    private readonly Dictionary<(string Type, string Id), Mapped> _ids = [];
    private readonly Dictionary<(string Type, string Id), Mapped> _pending = [];
    private readonly HashSet<string> _uploaded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _conflictParts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _conflictDocs = new(StringComparer.Ordinal);
    private int _dryChangeCounter = 900000;

    public ImportResult Result => _res;

    private void Count(string key, long by = 1) => _res.Created[key] = _res.Created.GetValueOrDefault(key) + by;
    private void Skip(string key, long by = 1) => _res.Skipped[key] = _res.Skipped.GetValueOrDefault(key) + by;
    private DateTimeOffset Now => clock.GetUtcNow();

    public async Task<ImportResult> RunAsync(PreparedMigration prep, LoadOptions opt, CancellationToken ct)
    {
        _prep = prep; _opt = opt; _res = new ImportResult(); _system = prep.Plan.SourceSystem;
        _batch = Guid.CreateVersion7(); _res.BatchId = _batch;
        _actor = ActorSnapshot.System with { Id = "migration:" + opt.Operator, Name = opt.Operator + " (keel-import)" };
        var sw = Stopwatch.StartNew();
        await using var conn = await ds.OpenConnectionAsync(ct);
        _conn = conn;
        _w = new ImportWriter(conn);
        await LoadIdMapAsync(ct);
        NpgsqlTransaction? outer = null;
        try
        {
            if (opt.DryRun) { outer = await conn.BeginTransactionAsync(ct); _tx = outer; _w.Tx = outer; }
            await RunPhaseAsync<int>("start", [0], (_, _) => Task.CompletedTask, startBatch: true, ct);
            await DocumentsAsync(ct);
            await PartMastersAsync(ct);
            await PartChainsAsync(ct);
            await ChangesAsync(ct);
            await RecordQuarantineAsync(ct);
            await FinishAsync(ct);
            _res.Status = opt.DryRun ? "DryRun" : _res.Status;
        }
        catch (Exception e) when (!opt.DryRun && e is not OperationCanceledException)
        {
            await MarkFailedAsync(e);
            throw;
        }
        finally
        {
            if (outer is not null) await outer.RollbackAsync(CancellationToken.None);
            sw.Stop();
            _res.Elapsed = sw.Elapsed;
        }
        return _res;
    }

    private async Task LoadIdMapAsync(CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("SELECT entity_type, source_id, keel_id, source_hash FROM mig.id_map WHERE source_system = @s", _conn);
        c.Parameters.AddWithValue("s", _system);
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) _ids[(r.GetString(0), r.GetString(1))] = new Mapped(r.GetGuid(2), r.GetString(3));
    }

    // ------------------------------------------------------------------------------------------------ units of work
    /// <summary>Runs <paramref name="items"/> in chunks. Real load: one transaction per chunk. Dry-run: a savepoint per chunk inside the one outer transaction.</summary>
    private async Task RunPhaseAsync<T>(string stage, IReadOnlyList<T> items, Func<T, int, Task> perItem, bool startBatch, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var size = Math.Max(1, _opt.ChunkSize);
        for (var offset = 0; offset < items.Count; offset += size)
        {
            ct.ThrowIfCancellationRequested();
            if (_opt.DryRun) await _w.SavepointAsync("chunk", ct);
            else { _tx = await _conn.BeginTransactionAsync(ct); _w.Tx = _tx; }
            _pending.Clear();
            try
            {
                if (startBatch) await StartBatchAsync(ct);
                for (var i = offset; i < Math.Min(items.Count, offset + size); i++) await perItem(items[i], i);
                if (_opt.DryRun) await _w.ReleaseAsync("chunk", ct);
                else await _tx!.CommitAsync(ct);
            }
            catch
            {
                if (_opt.DryRun) { try { await _w.RollbackToAsync("chunk", CancellationToken.None); } catch (NpgsqlException) { } }
                else if (_tx is not null) await _tx.RollbackAsync(CancellationToken.None);
                _pending.Clear();
                throw;
            }
            finally
            {
                if (!_opt.DryRun) { if (_tx is not null) await _tx.DisposeAsync(); _tx = null; _w.Tx = null; }
            }
            foreach (var (k, v) in _pending) _ids[k] = v;
            _pending.Clear();
            _opt.Progress?.Invoke($"{stage}: {Math.Min(items.Count, offset + size)}/{items.Count}");
        }
    }

    /// <summary>One object inside a chunk, isolated by a savepoint.</summary>
    private async Task<bool> ObjectAsync(string stage, string entityType, string sourceId, int index, Func<Task> work, CancellationToken ct)
    {
        if (_opt.Fault is not null) await _opt.Fault(new FaultPoint(stage, index, sourceId));
        await _w.SavepointAsync("obj", ct);
        var before = _pending.ToDictionary(p => p.Key, p => p.Value);
        var snapshot = _res.Created.ToDictionary(p => p.Key, p => p.Value);
        var auditBefore = _res.AuditEventsWritten;
        try
        {
            await work();
            await _w.ReleaseAsync("obj", ct);
            return true;
        }
        catch (Exception e) when (e is PostgresException or ImportRejectedException)
        {
            await _w.RollbackToAsync("obj", ct);
            _pending.Clear();
            foreach (var (k, v) in before) _pending[k] = v;
            _res.Created = new SortedDictionary<string, long>(snapshot, StringComparer.Ordinal);
            _res.AuditEventsWritten = auditBefore;
            var code = e is PostgresException pg ? "LOAD-DB-REJECTED" : "LOAD-REJECTED";
            var message = e is PostgresException p2 ? $"The database rejected the object ({p2.SqlState}): {p2.MessageText}" : e.Message;
            _res.Problems.Add(new Problem(Severity.Error, code, entityType, sourceId, message, "Fix the source data or the mapping and rerun; objects already imported are skipped."));
            _res.Quarantined.Add((entityType, sourceId, code, message));
            if (!_opt.Quarantine) throw new LoadAbortedException($"{entityType} '{sourceId}' could not be loaded: {message} Use --quarantine to skip failing objects.", e);
            return false;
        }
    }

    // ------------------------------------------------------------------------------------------------ batch start/end
    private async Task StartBatchAsync(CancellationToken ct)
    {
        await _w.InsertBatchAsync(_batch, _opt.BatchName, _system, _prep.Manifest.Sha256, _prep.MappingSha256, _opt.DryRunHash, _prep.Config.AuditGranularity, _opt.Quarantine, Now, _opt.Operator, ct);
        await AuditAsync("migration.batch.start", "batch", _batch.ToString(), new()
        {
            ["packageSha256"] = _prep.Manifest.Sha256, ["mappingSha256"] = _prep.MappingSha256, ["sourceSystem"] = _system, ["granularity"] = _prep.Config.AuditGranularity,
            ["quarantine"] = _opt.Quarantine.ToString(), ["dryRunHash"] = _opt.DryRunHash, ["operator"] = _opt.Operator, ["name"] = _opt.BatchName,
            ["plannedParts"] = _prep.Plan.Parts.Count.ToString(), ["plannedDocuments"] = _prep.Plan.Documents.Count.ToString(), ["plannedChanges"] = _prep.Plan.Changes.Count.ToString(),
        }, ct);
    }

    private async Task AuditAsync(string action, string type, string id, Dictionary<string, string?> details, CancellationToken ct)
    {
        await audit.AppendAsync(_tx!, new AuditEntry
        {
            Actor = _actor, Action = action, Outcome = AuditOutcome.Success, ObjectType = type, ObjectId = id, CorrelationId = _batch.ToString(),
            Details = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, details),
        }, ct);
        _res.AuditEventsWritten++;
    }

    private async Task RecordQuarantineAsync(CancellationToken ct)
    {
        // Objects the mapping rejected (never written) are listed with the first error that stopped them.
        var plan = _prep.Plan;
        var rows = new List<(string Type, string Id, string Code, string Message)>();
        foreach (var key in plan.Failed.Order(StringComparer.Ordinal))
        {
            var parts = key.Split('\u001f');
            if (parts[0] is "package" or "mapping" or "user") continue;
            var first = plan.Problems.FirstOrDefault(p => p.Severity == Severity.Error && p.EntityType == parts[0] && p.SourceId == parts[1]);
            rows.Add((parts[0], parts[1], first?.Code ?? "VALIDATION", first?.Message ?? "Validation error."));
        }
        rows.AddRange(_res.Quarantined);
        await RunPhaseAsync("quarantine", rows, async (r, _) => await _w.InsertQuarantineAsync(_batch, r.Type, r.Id, r.Code, r.Message, Now, ct), false, ct);
        _res.Quarantined = rows.Select(r => (r.Type, r.Id, r.Code, r.Message)).ToList();
    }

    private async Task FinishAsync(CancellationToken ct)
    {
        var conflicts = _res.Conflicts;
        var issues = _res.Quarantined.Count > 0 || conflicts > 0;
        var status = issues ? "CompletedWithIssues" : "Completed";
        _res.Status = status;
        var summary = JsonSerializer.Serialize(new { created = _res.Created, skipped = _res.Skipped, quarantined = _res.Quarantined.Count, conflicts, filesUploaded = _res.FilesUploaded, bytesUploaded = _res.BytesUploaded, packageSha256 = _prep.Manifest.Sha256 });
        await RunPhaseAsync<int>("finish", [0], async (_, _) =>
        {
            foreach (var (entity, counts) in new[] { ("part", new[] { "parts", "partRevisions", "partIterations", "bomUsages", "partDocumentLinks" }), ("document", new[] { "documents", "documentRevisions", "documentIterations", "files", "fileBytes" }), ("change", new[] { "changes", "affectedObjects" }) })
            {
                var details = counts.ToDictionary(k => k, k => (string?)_res.Created.GetValueOrDefault(k).ToString(), StringComparer.Ordinal);
                details["packageSha256"] = _prep.Manifest.Sha256;
                await AuditAsync("migration.batch.entity-summary", entity, _batch.ToString(), details, ct);
            }
            await AuditAsync("migration.batch.end", "batch", _batch.ToString(), new()
            {
                ["status"] = status, ["quarantined"] = _res.Quarantined.Count.ToString(), ["conflicts"] = conflicts.ToString(),
                ["packageSha256"] = _prep.Manifest.Sha256, ["mappingSha256"] = _prep.MappingSha256,
            }, ct);
            await _w.FinishBatchAsync(_batch, status, summary, Now, ct);
        }, false, ct);
    }

    private async Task MarkFailedAsync(Exception e)
    {
        try
        {
            await using var conn = await ds.OpenConnectionAsync(CancellationToken.None);
            await using var tx = await conn.BeginTransactionAsync(CancellationToken.None);
            var w = new ImportWriter(conn) { Tx = tx };
            await using var exists = new NpgsqlCommand("SELECT count(*) FROM mig.batch WHERE id = @id", conn, tx);
            exists.Parameters.AddWithValue("id", _batch);
            if ((long)(await exists.ExecuteScalarAsync())! == 0) { await tx.RollbackAsync(); return; }
            await w.FinishBatchAsync(_batch, "Failed", JsonSerializer.Serialize(new { error = e.Message, created = _res.Created }), Now, CancellationToken.None);
            await tx.CommitAsync();
        }
        catch (NpgsqlException)
        {
            // the batch row stays Running; it is visible as an unfinished batch
        }
    }

    // ------------------------------------------------------------------------------------------------ documents
    private static string MasterHash(PDocument d) => PlanHash.Of(new { d.Number, d.Name, d.DocumentType, d.Markings, d.CreatedAt, d.CreatedBy, d.LegacyJson });
    private static string MasterHash(PPart p) => PlanHash.Of(new { p.Number, p.Name, p.PartType, p.Unit, p.Source, p.Markings, p.CreatedAt, p.CreatedBy, p.LegacyJson });

    private void Conflict(string type, string id, string what) =>
        _res.Problems.Add(new Problem(Severity.Error, "LOAD-CONFLICT", type, id, $"{what}: it was imported before and the source or the mapping has changed since. Imported (released) data is immutable, so nothing was overwritten.",
            "Review the difference; changes to released data go through a Keel change notice."));

    private async Task DocumentsAsync(CancellationToken ct)
    {
        var docs = _prep.Plan.Documents;
        await RunPhaseAsync("documents", docs, async (d, i) =>
        {
            var hasExisting = _ids.TryGetValue(("document", d.SourceId), out var existing);
            if (hasExisting && existing!.Hash != MasterHash(d)) { Conflict("document", d.SourceId, $"Document {d.Number}"); Skip("documents"); _conflictDocs.Add(d.SourceId); return; }
            var newRevisions = d.Revisions.Where(r => !_ids.ContainsKey(("document_revision", r.SourceId))).ToList();
            foreach (var r in d.Revisions.Where(r => _ids.TryGetValue(("document_revision", r.SourceId), out var m) && m.Hash != PlanHash.Of(r)))
            { Conflict("document_revision", r.SourceId, $"Document {d.Number} revision {r.LegacyLabel}"); _conflictDocs.Add(d.SourceId); }
            if (_conflictDocs.Contains(d.SourceId)) { Skip("documents"); return; }
            if (hasExisting && newRevisions.Count == 0) { Skip("documents"); return; }
            if (newRevisions.Count > 0 && d.Revisions.Select((r, idx) => (r, idx)).Any(t => !newRevisions.Contains(t.r) && t.idx > d.Revisions.IndexOf(newRevisions[0])))
            { Conflict("document", d.SourceId, $"Document {d.Number}: new revisions would be inserted before imported ones"); _conflictDocs.Add(d.SourceId); return; }
            await ObjectAsync("documents", "document", d.SourceId, i, async () => await WriteDocumentAsync(d, hasExisting ? existing!.Id : null, newRevisions, ct), ct);
        }, false, ct);
    }

    private async Task WriteDocumentAsync(PDocument d, Guid? existingMaster, List<PDocRevision> newRevisions, CancellationToken ct)
    {
        // Bytes first: content-addressed and immutable, so an orphan after a rollback is harmless and the rows below never name a missing object.
        foreach (var f in newRevisions.SelectMany(r => r.Iterations).SelectMany(i => i.Files)) await EnsureFileAsync(f, ct);
        var masterId = existingMaster ?? Guid.CreateVersion7();
        if (existingMaster is null)
        {
            await _w.InsertDocumentMasterAsync(masterId, d, _batch, ct);
            _pending[("document", d.SourceId)] = new Mapped(masterId, MasterHash(d));
            await _w.InsertIdMapAsync(_system, "document", d.SourceId, masterId, _batch, MasterHash(d), Now, ct);
            Count("documents");
            if (_prep.Config.AuditGranularity == "object")
                await AuditAsync("migration.object.import", "document", masterId.ToString(), new() { ["sourceId"] = d.SourceId, ["number"] = d.Number, ["revisions"] = newRevisions.Count.ToString() }, ct);
        }
        var initial = await InitialStateAsync("document-default", ct);
        var lastLabel = d.Revisions[^1].SourceId;
        foreach (var r in newRevisions)
        {
            var isLatest = r.SourceId == lastLabel;
            if (isLatest && existingMaster is not null) await _w.DemoteLatestDocumentRevisionAsync(masterId, ct);
            var revId = Guid.CreateVersion7();
            await _w.InsertDocumentRevisionAsync(revId, masterId, r, isLatest, r.Iterations.Max(i => i.Sequence), initial, _batch, ct);
            foreach (var it in r.Iterations)
            {
                var itId = Guid.CreateVersion7();
                await _w.InsertDocumentIterationAsync(itId, revId, it, _batch, ct);
                Count("documentIterations");
                foreach (var f in it.Files)
                {
                    await _w.InsertDocumentFileAsync(itId, f, it.CreatedAt, it.CreatedBy, ct);
                    Count("files"); Count("fileBytes", f.SizeBytes);
                }
            }
            await _w.SetDocumentRevisionStateAsync(revId, r, ct);
            _pending[("document_revision", r.SourceId)] = new Mapped(revId, PlanHash.Of(r));
            await _w.InsertIdMapAsync(_system, "document_revision", r.SourceId, revId, _batch, PlanHash.Of(r), Now, ct);
            Count("documentRevisions");
        }
    }

    private readonly Dictionary<string, string> _initialStates = [];
    private async Task<string> InitialStateAsync(string lifecycle, CancellationToken ct)
    {
        if (!_initialStates.TryGetValue(lifecycle, out var s)) _initialStates[lifecycle] = s = await _w.InitialStateAsync(lifecycle, ct);
        return s;
    }

    private async Task EnsureFileAsync(PFile f, CancellationToken ct)
    {
        if (!_uploaded.Add(f.Sha256)) return;
        if (store is null) return;
        if (await store.ExistsAsync(f.Sha256, f.SizeBytes, ct)) { _res.FilesAlreadyInVault++; return; }
        if (_opt.DryRun) { _res.FilesUploaded++; _res.BytesUploaded += f.SizeBytes; return; } // would upload: counted, nothing written
        await using var s = _prep.Package.OpenRead(f.PackagePath);
        var stored = await store.PutAsync(s, ct);
        if (stored.Sha256 != f.Sha256 || stored.SizeBytes != f.SizeBytes)
            throw new ImportRejectedException($"File '{f.PackagePath}' changed while it was imported (expected {f.Sha256[..12]}..., stored {stored.Sha256[..12]}...). The package must not be modified during a load.");
        _res.FilesUploaded++; _res.BytesUploaded += stored.SizeBytes;
    }

    // ------------------------------------------------------------------------------------------------ parts
    private async Task PartMastersAsync(CancellationToken ct)
    {
        await RunPhaseAsync("part masters", _prep.Plan.Parts, async (p, i) =>
        {
            if (_ids.TryGetValue(("part", p.SourceId), out var m))
            {
                if (m.Hash != MasterHash(p)) { Conflict("part", p.SourceId, $"Part {p.Number}"); _conflictParts.Add(p.SourceId); }
                Skip("parts");
                return;
            }
            await ObjectAsync("part masters", "part", p.SourceId, i, async () =>
            {
                var id = Guid.CreateVersion7();
                await _w.InsertPartMasterAsync(id, p, _batch, ct);
                _pending[("part", p.SourceId)] = new Mapped(id, MasterHash(p));
                await _w.InsertIdMapAsync(_system, "part", p.SourceId, id, _batch, MasterHash(p), Now, ct);
                Count("parts");
                if (_prep.Config.AuditGranularity == "object")
                    await AuditAsync("migration.object.import", "part", id.ToString(), new() { ["sourceId"] = p.SourceId, ["number"] = p.Number, ["revisions"] = p.Revisions.Count.ToString() }, ct);
            }, ct);
        }, false, ct);
    }

    private async Task PartChainsAsync(CancellationToken ct)
    {
        var plan = _prep.Plan;
        var docsBySource = plan.Documents.ToDictionary(d => d.SourceId, StringComparer.Ordinal);
        await RunPhaseAsync("part revisions", plan.Parts, async (p, i) =>
        {
            if (_conflictParts.Contains(p.SourceId) || !_ids.TryGetValue(("part", p.SourceId), out var master)) return;
            var newRevisions = p.Revisions.Where(r => !_ids.ContainsKey(("part_revision", r.SourceId))).ToList();
            var changed = p.Revisions.Where(r => _ids.TryGetValue(("part_revision", r.SourceId), out var m) && m.Hash != PlanHash.Of(r)).ToList();
            foreach (var r in changed) Conflict("part_revision", r.SourceId, $"Part {p.Number} revision {r.LegacyLabel}");
            if (changed.Count > 0) { Skip("partRevisions", p.Revisions.Count - newRevisions.Count); return; }
            Skip("partRevisions", p.Revisions.Count - newRevisions.Count);
            if (newRevisions.Count == 0) return;
            var firstNew = p.Revisions.IndexOf(newRevisions[0]);
            if (p.Revisions.Where((_, idx) => idx > firstNew).Any(r => !newRevisions.Contains(r)))
            { Conflict("part", p.SourceId, $"Part {p.Number}: new revisions would be inserted before imported ones"); return; }
            await ObjectAsync("part revisions", "part", p.SourceId, i, async () => await WritePartChainAsync(p, master.Id, newRevisions, docsBySource, ct), ct);
        }, false, ct);
    }

    private async Task WritePartChainAsync(PPart p, Guid masterId, List<PRevision> newRevisions, Dictionary<string, PDocument> docs, CancellationToken ct)
    {
        var initial = await InitialStateAsync("part-default", ct);
        var last = p.Revisions[^1].SourceId;
        foreach (var r in newRevisions)
        {
            var isLatest = r.SourceId == last;
            if (isLatest && newRevisions.Count < p.Revisions.Count) await _w.DemoteLatestPartRevisionAsync(masterId, ct);
            var revId = Guid.CreateVersion7();
            await _w.InsertPartRevisionAsync(revId, masterId, r, isLatest, r.Iterations.Max(x => x.Sequence), initial, _batch, ct);
            foreach (var it in r.Iterations)
            {
                var itId = Guid.CreateVersion7();
                await _w.InsertPartIterationAsync(itId, revId, it, _batch, ct);
                Count("partIterations");
                var usages = new List<(PUsage, Guid)>();
                foreach (var u in it.Usages)
                {
                    if (!_ids.TryGetValue(("part", u.ChildSourceId), out var child))
                        throw new ImportRejectedException($"Part {p.Number} uses '{u.ChildSourceId}', which was not imported.");
                    usages.Add((u, child.Id));
                }
                await _w.InsertUsagesAsync(itId, usages, it.CreatedAt, it.CreatedBy, ct);
                Count("bomUsages", usages.Count);
                foreach (var l in it.Links)
                {
                    if (!_ids.TryGetValue(("document", l.DocumentSourceId), out var dm)) throw new ImportRejectedException($"Part {p.Number} links to document '{l.DocumentSourceId}', which was not imported.");
                    Guid? dr = null;
                    if (l.Type == "describes")
                    {
                        var rev = docs[l.DocumentSourceId].Revisions.First(x => x.Label == l.DocumentRevisionLabel);
                        dr = _ids.TryGetValue(("document_revision", rev.SourceId), out var drm) ? drm.Id : throw new ImportRejectedException($"Document revision '{rev.SourceId}' was not imported.");
                    }
                    await _w.InsertPartLinkAsync(itId, l.Type, dm.Id, dr, it.CreatedAt, it.CreatedBy, ct);
                    Count("partDocumentLinks");
                }
            }
            await _w.SetPartRevisionStateAsync(revId, r, ct);
            _pending[("part_revision", r.SourceId)] = new Mapped(revId, PlanHash.Of(r));
            await _w.InsertIdMapAsync(_system, "part_revision", r.SourceId, revId, _batch, PlanHash.Of(r), Now, ct);
            Count("partRevisions");
        }
    }

    // ------------------------------------------------------------------------------------------------ changes
    private sealed record Tree(PChange Root, List<PChange> Children);

    private async Task ChangesAsync(CancellationToken ct)
    {
        var plan = _prep.Plan;
        var byId = plan.Changes.ToDictionary(c => c.SourceId, StringComparer.Ordinal);
        var trees = new List<Tree>();
        foreach (var c in plan.Changes.Where(c => c.ChangeType == "PR")) trees.Add(new Tree(c, []));
        foreach (var c in plan.Changes.Where(c => c.ChangeType == "ECR")) trees.Add(new Tree(c, plan.Changes.Where(n => n.ChangeType == "ECN" && n.ParentSourceId == c.SourceId).ToList()));
        foreach (var c in plan.Changes.Where(c => c.ChangeType == "ECN" && (c.ParentSourceId is null || !byId.ContainsKey(c.ParentSourceId)))) trees.Add(new Tree(c, []));
        var transitions = new Dictionary<string, List<(string From, string To)>>();
        foreach (var lc in new[] { "pr-default", "ecr-default", "ecn-default" }) transitions[lc] = await _w.LoadTransitionsAsync(lc, ct);
        await RunPhaseAsync("changes", trees, async (t, i) =>
        {
            var all = new[] { t.Root }.Concat(t.Children).ToList();
            var existing = all.Where(c => _ids.ContainsKey(("change", c.SourceId))).ToList();
            foreach (var c in existing)
            {
                if (_ids[("change", c.SourceId)].Hash != PlanHash.Of(c)) Conflict("change", c.SourceId, $"Change {c.LegacyNumber}");
                Skip("changes");
            }
            if (existing.Count == all.Count) return;
            if (existing.Count > 0) { Conflict("change", t.Root.SourceId, $"Change {t.Root.LegacyNumber}: only part of its tree was imported before"); return; }
            await ObjectAsync("changes", "change", t.Root.SourceId, i, async () => await WriteChangeTreeAsync(t, transitions, ct), ct);
        }, false, ct);
    }

    private async Task WriteChangeTreeAsync(Tree t, Dictionary<string, List<(string From, string To)>> transitions, CancellationToken ct)
    {
        var rootId = Guid.CreateVersion7();
        Guid? parentId = null;
        if (t.Root.ParentSourceId is not null && (_pending.TryGetValue(("change", t.Root.ParentSourceId), out var pm) || _ids.TryGetValue(("change", t.Root.ParentSourceId), out pm))) parentId = pm.Id;
        // A Class I change notice is only accepted under an APPROVED change request, so the request walks to Approved first.
        var holdAtApproved = t.Root.ChangeType == "ECR" && t.Children.Any(c => c.ChangeClass == "I");
        var rootState = await InsertChangeAsync(t.Root, rootId, parentId, holdAtApproved ? "Approved" : t.Root.State, transitions, ct);
        foreach (var child in t.Children)
            await WriteChangeAsync(child, Guid.CreateVersion7(), rootId, child.State, transitions, ct);
        if (holdAtApproved && rootState != t.Root.State) await WalkAsync(rootId, "ecr-default", rootState, t.Root.State, t.Root, transitions, ct);
    }

    private async Task WriteChangeAsync(PChange c, Guid id, Guid? parent, string stopAt, Dictionary<string, List<(string From, string To)>> transitions, CancellationToken ct) =>
        await InsertChangeAsync(c, id, parent, stopAt, transitions, ct);

    private async Task<string> InsertChangeAsync(PChange c, Guid id, Guid? parent, string walkTo, Dictionary<string, List<(string From, string To)>> transitions, CancellationToken ct)
    {
        var lifecycle = c.ChangeType.ToLowerInvariant() + "-default";
        var initial = await InitialStateAsync(lifecycle, ct);
        var number = c.Number ?? (_opt.DryRun ? $"{c.ChangeType}-{_dryChangeCounter++}" : await _w.NextChangeNumberAsync(c.ChangeType, ct));
        var user = rd.Directory.TryGetValue(c.CreatedBy, out var u) ? (u.Sub, u.Username, u.Username) : ("imported:" + c.CreatedBy, c.CreatedBy, c.CreatedBy);
        await _w.InsertChangeAsync(id, c, number, parent, initial, user, _batch, ct);
        foreach (var a in c.Affected)
        {
            var part = _prep.Plan.Parts.First(p => p.SourceId == a.ItemSourceId);
            var rev = part.Revisions.First(r => r.SourceId == a.RevisionSourceId);
            var res = a.ResultingRevisionSourceId is null ? null : part.Revisions.First(r => r.SourceId == a.ResultingRevisionSourceId);
            if (!_ids.TryGetValue(("part", part.SourceId), out var pm) || !_ids.TryGetValue(("part_revision", rev.SourceId), out var rm))
                throw new ImportRejectedException($"Change {c.LegacyNumber} lists {part.Number} {rev.LegacyLabel}, which was not imported.");
            Guid? resId = null;
            if (res is not null) resId = _ids.TryGetValue(("part_revision", res.SourceId), out var xm) ? xm.Id : throw new ImportRejectedException($"Resulting revision of {part.Number} was not imported.");
            await _w.InsertAffectedAsync(id, pm.Id, part.Number, part.Name, rm.Id, rev.Label, a.Action, resId, res?.Label, c.CreatedAt, c.CreatedBy, ct);
            Count("affectedObjects");
        }
        await _w.InsertChangeHistoryAsync(id, Now, "migration:" + _opt.Operator, _actor.Name ?? "keel-import",
            $"Migrated record (batch {_batch}): imported from {_system} number {c.LegacyNumber}; its workflow and signatures were not reproduced.", ct);
        var reached = await WalkAsync(id, lifecycle, initial, walkTo, c, transitions, ct);
        _pending[("change", c.SourceId)] = new Mapped(id, PlanHash.Of(c));
        await _w.InsertIdMapAsync(_system, "change", c.SourceId, id, _batch, PlanHash.Of(c), Now, ct);
        Count("changes");
        if (_prep.Config.AuditGranularity == "object")
            await AuditAsync("migration.object.import", "change", id.ToString(), new() { ["sourceId"] = c.SourceId, ["number"] = number, ["legacyNumber"] = c.LegacyNumber, ["type"] = c.ChangeType }, ct);
        return reached;
    }

    /// <summary>Takes the shortest path through the lifecycle's transitions; the guards check every step, none is skipped.</summary>
    private async Task<string> WalkAsync(Guid id, string lifecycle, string from, string to, PChange c, Dictionary<string, List<(string From, string To)>> transitions, CancellationToken ct)
    {
        if (from == to) return from;
        var edges = transitions[lifecycle];
        var prev = new Dictionary<string, string> { [from] = "" };
        var queue = new Queue<string>([from]);
        while (queue.Count > 0)
        {
            var s = queue.Dequeue();
            foreach (var e in edges.Where(e => e.From == s && !prev.ContainsKey(e.To))) { prev[e.To] = s; queue.Enqueue(e.To); }
        }
        if (!prev.ContainsKey(to)) throw new ImportRejectedException($"Lifecycle {lifecycle} has no path from {from} to {to} for {c.LegacyNumber}.");
        var path = new List<string>();
        for (var s = to; s != from; s = prev[s]) path.Add(s);
        path.Reverse();
        foreach (var step in path) await _w.SetChangeStateAsync(id, step, c.StateChangedAt, c.StateChangedBy, ct);
        return to;
    }
}

/// <summary>A planned object cannot be written (a precondition of the load, not a database error).</summary>
public sealed class ImportRejectedException(string message) : Exception(message);
