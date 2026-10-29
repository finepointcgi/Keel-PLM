using System.Diagnostics;
using Keel.Migration;
using Keel.Migration.Fixtures;
using Keel.Migration.Load;
using Keel.Migration.Reconcile;
using Keel.Migration.Sources;

namespace Keel.IntegrationTests;

/// <summary>Scale, speed and memory of the importer. Numbers are written to the test output and to docs/migration/performance.md by hand after a run.</summary>
[Collection(PostgresCollection.Name)]
public sealed class MigrationPerformanceTests(PostgresFixture pg)
{
    private static void Note(string line)
    {
        TestContext.Current.SendDiagnosticMessage(line);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "keel-migration-perf.txt"), line + Environment.NewLine);
    }

    private sealed class MemorySampler : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _task;
        public long PeakHeap { get; private set; }
        public long PeakWorkingSet { get; private set; }

        public MemorySampler()
        {
            _task = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    PeakHeap = Math.Max(PeakHeap, GC.GetTotalMemory(false));
                    PeakWorkingSet = Math.Max(PeakWorkingSet, System.Diagnostics.Process.GetCurrentProcess().WorkingSet64);
                    try { await Task.Delay(20, _cts.Token); } catch (OperationCanceledException) { }
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await _task;
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task Ten_thousand_parts_forty_thousand_bom_lines_and_five_hundred_files_load_reconcile_and_stay_consistent()
    {
        using var k = await MigrationKit.Create(pg);
        var product = Samples.BuildScale(parts: 10_000, assemblies: 2_000, linesPerAssembly: 20, documents: 500);
        Assert.Equal(40_000, product.Package.Items.Sum(i => i.Revisions.Sum(r => r.Iterations.Sum(it => it.Usages.Count))));
        var dir = k.Package(product);
        var cfg = Samples.Mapping();

        var sw = Stopwatch.StartNew();
        using var prepared = await k.Pipeline.PrepareAsync(dir, null, cfg, CancellationToken.None);
        var prepareSeconds = sw.Elapsed.TotalSeconds;
        Assert.DoesNotContain(prepared.Plan.Problems, p => p.Severity == Keel.Migration.Canonical.Severity.Error);

        sw.Restart();
        var dry = await k.Pipeline.DryRunAsync(prepared, new LoadOptions { Operator = "perf" }, CancellationToken.None);
        var drySeconds = sw.Elapsed.TotalSeconds;
        Assert.True(dry.Succeeded);
        Assert.Equal(0, await k.Count("SELECT count(*) FROM plm.part_master"));

        await using var memory = new MemorySampler();
        sw.Restart();
        var (report, result) = await k.Pipeline.LoadAsync(prepared, dry, new LoadOptions { Operator = "perf" }, CancellationToken.None);
        var loadSeconds = sw.Elapsed.TotalSeconds;
        Assert.Equal("Completed", result.Status);
        Assert.Equal(10_000, result.Created["parts"]);
        Assert.Equal(40_000, result.Created["bomUsages"]);
        Assert.Equal(500, result.Created["files"]);

        sw.Restart();
        var rec = await k.Pipeline.ReconcileAsync(prepared, new ReconcileOptions(), CancellationToken.None);
        var reconcileSeconds = sw.Elapsed.TotalSeconds;
        Assert.True(rec.Ok, string.Join("\n", rec.Checks.Where(c => !c.Ok).Select(c => c.Name + ": " + c.Expected + " vs " + c.Actual)));

        Note($"SCALE parts=10000 revisions={dry.Planned["partRevisions"]} iterations={dry.Planned["partIterations"]} bomLines=40000 documents=500 files=500 (+{dry.Planned["fileBytes"]} bytes)");
        Note($"SCALE prepare(parse+map+validate)={prepareSeconds:F1}s dryRun={drySeconds:F1}s load={loadSeconds:F1}s reconcile={reconcileSeconds:F1}s rows={dry.EstimatedRows} loadRate={dry.EstimatedRows / loadSeconds:F0} rows/s");
        Note($"SCALE load peak managed heap={memory.PeakHeap / 1024 / 1024} MB, peak working set={memory.PeakWorkingSet / 1024 / 1024} MB, auditEvents={result.AuditEventsWritten}");
        Assert.True(loadSeconds < 900, $"load took {loadSeconds:F0} s");
        Assert.True((await k.VerifyAudit()).Valid);
        Assert.NotNull(report);
    }

    [Fact]
    public async Task Object_level_audit_costs_one_event_per_object_and_batch_level_costs_five_events_in_total()
    {
        const int parts = 2_000;
        var timings = new Dictionary<string, (double Seconds, long Events)>();
        foreach (var mode in new[] { "batch", "object" })
        {
            using var k = await MigrationKit.Create(pg);
            var product = Samples.BuildScale(parts, 400, 20, 50);
            var dir = k.Package(product);
            var cfg = Samples.Mapping();
            cfg.AuditGranularity = mode;
            using var prepared = await k.Pipeline.PrepareAsync(dir, null, cfg, CancellationToken.None);
            var dry = await k.Pipeline.DryRunAsync(prepared, new LoadOptions(), CancellationToken.None);
            var sw = Stopwatch.StartNew();
            var (_, result) = await k.Pipeline.LoadAsync(prepared, dry, new LoadOptions(), CancellationToken.None);
            timings[mode] = (sw.Elapsed.TotalSeconds, await k.Count("SELECT count(*) FROM audit.event"));
            Assert.True((await k.VerifyAudit()).Valid);
            Assert.Equal("Completed", result.Status);
        }
        Assert.Equal(5, timings["batch"].Events);
        Assert.Equal(5 + parts + 50, timings["object"].Events);
        var extra = timings["object"].Seconds - timings["batch"].Seconds;
        Note($"AUDIT {parts} parts + 50 documents: batch granularity {timings["batch"].Seconds:F1}s ({timings["batch"].Events} events), object granularity {timings["object"].Seconds:F1}s ({timings["object"].Events} events); extra {extra:F1}s = {extra / (parts + 50) * 1000:F2} ms per object");
    }

    [Fact]
    public async Task A_two_hundred_megabyte_plm_xml_is_streamed_with_flat_memory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "keel-mig-big-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "big.plmxml");
            var count = Samples.WriteLargePlmXml(path, 200L * 1024 * 1024);
            var size = new FileInfo(path).Length;
            Assert.True(size >= 200L * 1024 * 1024);

            GC.Collect();
            var baseline = GC.GetTotalMemory(true);
            await using var memory = new MemorySampler();
            var sw = Stopwatch.StartNew();
            var inspect = await MigrationPipeline.InspectAsync(dir, null, CancellationToken.None);
            var inspectSeconds = sw.Elapsed.TotalSeconds;
            Assert.Equal(count, inspect.Inventory.Counts["items"]);
            Assert.Equal(count, inspect.Inventory.Counts["itemRevisions"]);
            Assert.Equal(count, inspect.Inventory.IgnoredElements["Representation"]);
            var heapGrowthMb = (memory.PeakHeap - baseline) / 1024 / 1024;
            Note($"STREAM {size / 1024 / 1024} MB PLM XML, {count} parts: inspect (read + manifest hash) {inspectSeconds:F1}s, peak managed heap growth {heapGrowthMb} MB (file is {size / 1024 / 1024} MB)");
            Assert.True(heapGrowthMb < 120, $"managed heap grew by {heapGrowthMb} MB while reading a {size / 1024 / 1024} MB file");

            // the full pipeline keeps the extracted data (not the XML): memory is proportional to the parts, not to the bytes
            sw.Restart();
            using var prepared = await MigrationPipeline.PrepareAsync(dir, null, Samples.Mapping(), Keel.Migration.Mapping.ReferenceData.Defaults(), CancellationToken.None);
            Assert.Equal(count, prepared.Plan.Parts.Count);
            Note($"STREAM full read+map of the same file: {sw.Elapsed.TotalSeconds:F1}s, {prepared.Plan.Parts.Count} parts planned, peak managed heap {memory.PeakHeap / 1024 / 1024} MB, peak working set {memory.PeakWorkingSet / 1024 / 1024} MB");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
