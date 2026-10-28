using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;
using Keel.Api.Infrastructure;
using Keel.Audit;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Vault;
using Keel.Siem;
using Keel.TestSupport;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// SIEM forwarding against real PostgreSQL and a real TLS syslog collector with a test CA (ADR-0026): framing and content, ordering, resume after
/// restart, reconnect and backoff, chain-break detection when the test tampers with the audit table as a superuser, mutual TLS, certificate validation,
/// no marked data in messages, backpressure, health and metrics.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed partial class SiemForwardingTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _cleanup = [];
    private readonly List<string> _dirs = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _cleanup) await c.DisposeAsync();
        foreach (var d in _dirs) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    private T Track<T>(T disposable) where T : IAsyncDisposable { _cleanup.Add(disposable); return disposable; }

    private string TempDir()
    {
        var dir = Directory.CreateTempSubdirectory("keel-siem-it-").FullName;
        _dirs.Add(dir);
        return dir;
    }

    /// <summary>A forwarder wired to a test database; <see cref="Start"/> / <see cref="StopAsync"/> can be repeated like a process restart.</summary>
    private sealed class Harness(TestDatabase db, SiemOptions options, string? secretsDir = null) : IDisposable
    {
        public void Dispose() => _service?.Dispose();

        public TestDatabase Db { get; } = db;
        public NpgsqlDataSource Ds { get; } = db.AppDataSource();
        public PostgresAuditLog Audit => new(Ds, TimeProvider.System);
        public SiemStatusTracker Tracker { get; private set; } = new();
        private SiemForwardingService? _service;

        public async Task Start()
        {
            Tracker = new SiemStatusTracker();
            Tracker.MarkEnabled();
            _service = new SiemForwardingService(options, new CursorStore(Ds, TimeProvider.System), Tracker, Audit, TimeProvider.System, NullLoggerFactory.Instance, secretsDir);
            await _service.StartAsync(CancellationToken.None);
        }

        public async Task StopAsync()
        {
            if (_service is null) return;
            await _service.StopAsync(CancellationToken.None);
            _service.Dispose();
            _service = null;
        }

        public SiemDestinationStatus Status => Tracker.Current().Destinations[0];

        public static async Task<bool> WaitUntilAsync(Func<bool> condition, int seconds = 20)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline) { if (condition()) return true; await Task.Delay(50); }
            return condition();
        }

        public async Task AppendAsync(int count, string action = "test.event", int start = 0, int corrLength = 0)
        {
            var audit = Audit;
            for (var i = start; i < start + count; i++)
                await audit.AppendAsync(new AuditEntry
                {
                    Actor = ActorSnapshot.System, Action = action + (i % 3), ObjectType = "widget", ObjectId = $"W-{i}", CorrelationId = corrLength > 0 ? new string('c', corrLength) : $"c-{i}",
                    Outcome = i % 7 == 6 ? AuditOutcome.Denied : i % 11 == 10 ? AuditOutcome.Failure : AuditOutcome.Success,
                    Details = AuditDetails.Of(("secretName", "Never forwarded name"), ("n", i.ToString(CultureInfo.InvariantCulture))),
                });
        }
    }

    private static SiemOptions Options(Action<SiemDestinationOptions>? change = null, Action<SiemOptions>? top = null)
    {
        var d = new SiemDestinationOptions { Name = "primary", Host = "localhost", ConnectTimeoutSeconds = 3, SendTimeoutSeconds = 2, BackoffMinSeconds = 1, BackoffMaxSeconds = 2 };
        change?.Invoke(d);
        var o = new SiemOptions { Enabled = true, Hostname = "keel-test", BatchSize = 50, QueueBatches = 2, PollIntervalMilliseconds = 50, Destinations = [d] };
        top?.Invoke(o);
        return o;
    }

    private async Task<(TlsSyslogListener Listener, SyslogTestPki Pki, string Dir)> CollectorAsync(bool mtls = false)
    {
        var pki = new SyslogTestPki();
        var dir = TempDir();
        await File.WriteAllTextAsync(Path.Combine(dir, "ca.pem"), pki.CaPem);
        var listener = Track(new TlsSyslogListener(pki.Issue("localhost", server: true, "localhost"), mtls ? pki.Ca : null));
        return (listener, pki, dir);
    }

    // ---------------------------------------------------------------- parsing helpers

    [GeneratedRegex(@"^<(?<pri>\d+)>1 (?<ts>\S+) (?<host>\S+) (?<app>\S+) (?<proc>\S+) (?<msgid>\S+) \[keel@\d+(?<params>(?: [A-Za-z]+=""(?:[^""\\\]]|\\[""\\\]])*"")*)\] (?<msg>.*)$", RegexOptions.Singleline)]
    private static partial Regex SyslogLine();

    [GeneratedRegex(@" (?<name>[A-Za-z]+)=""(?<value>(?:[^""\\\]]|\\[""\\\]])*)""")]
    private static partial Regex SdParam();

    private sealed record Parsed(int Priority, string MsgId, Dictionary<string, string> Sd, string Msg, string Raw);

    private static Parsed Parse(string raw)
    {
        var m = SyslogLine().Match(raw);
        Assert.True(m.Success, "not RFC 5424: " + raw);
        var sd = SdParam().Matches(m.Groups["params"].Value).ToDictionary(p => p.Groups["name"].Value, p => Regex.Replace(p.Groups["value"].Value, @"\\([""\\\]])", "$1"));
        return new Parsed(int.Parse(m.Groups["pri"].Value, CultureInfo.InvariantCulture), m.Groups["msgid"].Value, sd, m.Groups["msg"].Value, raw);
    }

    private static List<Parsed> Events(TlsSyslogListener l) => [.. l.Messages.Select(Parse).Where(p => p.Sd.ContainsKey("seq") && !p.Sd.ContainsKey("checkpoint") && !p.Sd.ContainsKey("kind"))];

    private static List<long> DistinctSeqs(IEnumerable<Parsed> events) => [.. events.Select(e => long.Parse(e.Sd["seq"], CultureInfo.InvariantCulture)).Distinct().Order()];

    private static async Task<List<AuditRecord>> DbEvents(NpgsqlDataSource ds) => [.. await new AuditQueries(ds).ListAsync(new AuditQuery(Limit: 1000))];

    private static async Task SuperuserAsync(TestDatabase db, string sql) => await db.ExecuteAsSuperuserAsync(sql);

    private static Dictionary<string, long> CaptureCounters(MeterListener listener)
    {
        var counts = new Dictionary<string, long>();
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == "Keel.Siem") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((i, v, _, _) => { lock (counts) counts[i.Name] = counts.GetValueOrDefault(i.Name) + v; });
        listener.Start();
        return counts;
    }

    // ---------------------------------------------------------------- delivery

    [Fact]
    public async Task Events_arrive_over_tls_in_order_with_the_chain_data_and_the_cursor_advances()
    {
        var (listener, _, dir) = await CollectorAsync();
        var db = await pg.CreateDatabaseAsync();
        var h = new Harness(db, Options(d => { d.Port = listener.Port; d.CaBundlePath = "ca.pem"; }), dir);
        await h.AppendAsync(120);
        using var meter = new MeterListener();
        var counts = CaptureCounters(meter);

        await h.Start();
        Assert.True(await listener.WaitForCountAsync(120, TimeSpan.FromSeconds(20)), $"received {listener.Messages.Count}");
        Assert.True(await Harness.WaitUntilAsync(() => h.Status.LastForwardedSeq == 120 && h.Status.LagEvents == 0));
        await h.StopAsync();

        var events = Events(listener);
        Assert.Equal(Enumerable.Range(1, 120).Select(i => (long)i), DistinctSeqs(events));
        Assert.Equal(events.Select(e => e.Sd["seq"]).Distinct().Count(), events.Count); // no duplicates on a clean run
        Assert.Empty(listener.FramingErrors);
        Assert.All(listener.NegotiatedProtocols, p => Assert.True(p is SslProtocols.Tls12 or SslProtocols.Tls13));

        // What the SIEM needs to verify continuity downstream: each prevHash is the previous event's hash, and the hashes are the database's.
        var db1 = await DbEvents(h.Ds);
        for (var i = 0; i < events.Count; i++)
        {
            Assert.Equal(Convert.ToHexStringLower(db1[i].Hash), events[i].Sd["hash"]);
            Assert.Equal(Convert.ToHexStringLower(db1[i].PrevHash), events[i].Sd["prevHash"]);
            if (i > 0) Assert.Equal(events[i - 1].Sd["hash"], events[i].Sd["prevHash"]);
        }
        Assert.Equal(new string('0', 64), events[0].Sd["prevHash"]); // genesis
        // Severity mapping and fields.
        var denied = events.First(e => e.Sd["outcome"] == "Denied");
        Assert.Equal(13 * 8 + 4, denied.Priority);
        Assert.Equal(13 * 8 + 3, events.First(e => e.Sd["outcome"] == "Failure").Priority);
        Assert.Equal(13 * 8 + 6, events.First(e => e.Sd["outcome"] == "Success").Priority);
        Assert.Equal(("system:keel", "widget:W-0", "c-0"), (events[0].Sd["actor"], events[0].Sd["object"], events[0].Sd["corr"]));

        await using var cmd = h.Ds.CreateCommand("SELECT last_seq, forwarded_total, last_hash FROM siem.cursor WHERE destination = 'primary' AND stream = 'audit'");
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal((120L, 120L), (r.GetInt64(0), r.GetInt64(1)));
        Assert.Equal(db1[^1].Hash, (byte[])r[2]);
        lock (counts) Assert.Equal(120, counts["keel_siem_events_forwarded"]);
    }

    [Fact]
    public async Task A_restart_resumes_from_the_cursor_without_gaps()
    {
        var (listener, _, dir) = await CollectorAsync();
        var db = await pg.CreateDatabaseAsync();
        var options = Options(d => { d.Port = listener.Port; d.CaBundlePath = "ca.pem"; });
        var first = new Harness(db, options, dir);
        await first.AppendAsync(60);
        await first.Start();
        Assert.True(await Harness.WaitUntilAsync(() => first.Status.LastForwardedSeq == 60));
        await first.StopAsync();

        await first.AppendAsync(90, start: 60); // written while the forwarder is down
        var second = new Harness(db, options, dir);
        await second.Start();
        Assert.True(await Harness.WaitUntilAsync(() => second.Status.LastForwardedSeq == 150));
        await second.StopAsync();

        var events = Events(listener);
        Assert.Equal(Enumerable.Range(1, 150).Select(i => (long)i), DistinctSeqs(events));
        // At-least-once: a restart may repeat the batch that was in flight, never more, and never reorders.
        Assert.InRange(events.Count - 150, 0, options.BatchSize);
        var sequence = events.Select(e => long.Parse(e.Sd["seq"], CultureInfo.InvariantCulture)).ToList();
        for (var i = 1; i < sequence.Count; i++) Assert.True(sequence[i] >= sequence[i - 1] - options.BatchSize);
    }

    [Fact]
    public async Task The_forwarder_reconnects_with_backoff_and_audit_writes_never_wait_for_it()
    {
        var (listener, _, dir) = await CollectorAsync();
        var db = await pg.CreateDatabaseAsync();
        var h = new Harness(db, Options(d => { d.Port = listener.Port; d.CaBundlePath = "ca.pem"; }), dir);
        await h.AppendAsync(20);
        await h.Start();
        Assert.True(await Harness.WaitUntilAsync(() => h.Status.LastForwardedSeq == 20));
        var port = listener.Port;

        listener.Stop(); // the collector goes away
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await h.AppendAsync(100, start: 20);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), "audit appends do not wait for the SIEM");
        Assert.True(await Harness.WaitUntilAsync(() => h.Status.ConsecutiveFailures >= 2, 15), "failures are counted with backoff between attempts");
        Assert.False(h.Status.Connected);
        Assert.NotNull(h.Status.LastError);
        Assert.True(h.Status.LagEvents > 0);
        var degraded = await new SiemHealthCheck(h.Tracker).CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, degraded.Status);

        listener.Start(port); // and comes back
        Assert.True(await Harness.WaitUntilAsync(() => h.Status.LastForwardedSeq == 120, 30), $"forwarded {h.Status.LastForwardedSeq}");
        Assert.True(h.Status.Connected);
        Assert.Equal(0, h.Status.ConsecutiveFailures);
        Assert.Equal(HealthStatus.Healthy, (await new SiemHealthCheck(h.Tracker).CheckHealthAsync(new HealthCheckContext())).Status);
        await h.StopAsync();
        Assert.Equal(Enumerable.Range(1, 120).Select(i => (long)i), DistinctSeqs(Events(listener)));
    }

    [Fact]
    public async Task A_stuck_collector_applies_backpressure_through_a_bounded_queue()
    {
        var (listener, _, dir) = await CollectorAsync();
        var db = await pg.CreateDatabaseAsync();
        var options = Options(d => { d.Port = listener.Port; d.CaBundlePath = "ca.pem"; d.SendTimeoutSeconds = 1; }, o => { o.BatchSize = 20; o.QueueBatches = 2; });
        var h = new Harness(db, options, dir);
        listener.PauseReading = true;
        await h.AppendAsync(1500, corrLength: 4000); // ~6 MB: more than the socket buffers hold
        await h.Start();

        Assert.True(await Harness.WaitUntilAsync(() => h.Status.ConsecutiveFailures >= 1, 30), "the blocked write times out and is retried");
        // The reader stops when the queue is full: it holds at most the queue plus the batch in its hand, never the backlog.
        await Task.Delay(1500);
        var bound = (options.QueueBatches + 2) * options.BatchSize;
        Assert.InRange(h.Status.QueuedEvents, 1, bound);
        Assert.True(h.Status.LagEvents > h.Status.QueuedEvents, "the backlog stays in the database");
        Assert.True(h.Status.LagSeconds >= 0);

        listener.PauseReading = false;
        Assert.True(await Harness.WaitUntilAsync(() => h.Status.LastForwardedSeq == 1500 && h.Status.LagEvents == 0, 90), $"forwarded {h.Status.LastForwardedSeq}");
        Assert.Equal(0, h.Status.QueuedEvents);
        await h.StopAsync();
        Assert.Equal(Enumerable.Range(1, 1500).Select(i => (long)i), DistinctSeqs(Events(listener)));
    }

    // ---------------------------------------------------------------- tamper detection

    [Fact]
    public async Task An_event_deleted_from_the_middle_is_reported_in_band_counted_persisted_and_degrades_health()
    {
        var (listener, _, dir) = await CollectorAsync();
        var db = await pg.CreateDatabaseAsync();
        var options = Options(d => { d.Port = listener.Port; d.CaBundlePath = "ca.pem"; });
        var h = new Harness(db, options, dir);
        await h.AppendAsync(10);
        // A database superuser disables the immutability trigger and deletes event 8 before the forwarder has read it.
        await SuperuserAsync(db, "ALTER TABLE audit.event DISABLE TRIGGER event_immutable; DELETE FROM audit.event WHERE seq = 8; ALTER TABLE audit.event ENABLE TRIGGER event_immutable;");
        using var meter = new MeterListener();
        var counts = CaptureCounters(meter);

        await h.Start();
        Assert.True(await Harness.WaitUntilAsync(() => h.Status.ChainBroken));
        // 10 events (one deleted) plus the siem.chain_break event the forwarder itself writes, which flows through the log like any other.
        Assert.True(await Harness.WaitUntilAsync(() => h.Status.LastForwardedSeq >= 11 && listener.Messages.Count >= 11));
        await h.StopAsync();

        var all = listener.Messages.Select(Parse).ToList();
        var alert = Assert.Single(all, p => p.Sd.GetValueOrDefault("kind") == "gap");
        Assert.Equal("9", alert.Sd["seq"]);
        Assert.Equal(13 * 8 + 2, alert.Priority); // critical
        var flagged = Assert.Single(all, p => p.Sd.GetValueOrDefault("chain") == "break");
        Assert.Equal("9", flagged.Sd["seq"]); // the event after the hole carries the marker
        Assert.Equal([1L, 2, 3, 4, 5, 6, 7, 9, 10, 11], DistinctSeqs(Events(listener))); // the SIEM sees the missing 8 itself
        lock (counts) Assert.True(counts["keel_siem_chain_breaks"] >= 1);
        Assert.Equal(HealthStatus.Degraded, (await new SiemHealthCheck(h.Tracker).CheckHealthAsync(new HealthCheckContext())).Status);

        await using var cmd = h.Ds.CreateCommand("SELECT chain_break_seq, chain_break_note FROM siem.cursor WHERE destination = 'primary' AND stream = 'audit'");
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await r.ReadAsync());
            Assert.Equal(9L, r.GetInt64(0));
            Assert.Contains("gap", r.GetString(1), StringComparison.Ordinal);
        }
        Assert.Contains(await DbEvents(h.Ds), e => e is { Action: "siem.chain_break", Outcome: AuditOutcome.Failure });

        // The break survives a restart: still degraded, reported once in the audit log.
        var again = new Harness(db, options, dir);
        await again.Start();
        Assert.True(await Harness.WaitUntilAsync(() => again.Status.ChainBroken));
        await again.StopAsync();
        Assert.Single(await DbEvents(h.Ds), e => e.Action == "siem.chain_break");
    }

    [Fact]
    public async Task A_row_that_no_longer_matches_its_hash_is_reported()
    {
        var (listener, _, dir) = await CollectorAsync();
        var db = await pg.CreateDatabaseAsync();
        var h = new Harness(db, Options(d => { d.Port = listener.Port; d.CaBundlePath = "ca.pem"; }), dir);
        await h.AppendAsync(6);
        await SuperuserAsync(db, "ALTER TABLE audit.event DISABLE TRIGGER event_immutable; UPDATE audit.event SET payload = replace(payload, 'widget', 'gadget') WHERE seq = 4; ALTER TABLE audit.event ENABLE TRIGGER event_immutable;");

        await h.Start();
        Assert.True(await Harness.WaitUntilAsync(() => h.Status.ChainBroken));
        Assert.True(await Harness.WaitUntilAsync(() => listener.Messages.Any(m => m.Contains("siem.chain_break", StringComparison.Ordinal))));
        await h.StopAsync();

        Assert.Single(listener.Messages.Select(Parse), p => p.Sd.GetValueOrDefault("kind") == "hash_mismatch" && p.Sd["seq"] == "4");
    }

    [Fact]
    public async Task A_tail_deleted_after_it_was_forwarded_is_detected()
    {
        // The case no checkpoint can catch: events newer than the last signed checkpoint removed from the database. The SIEM already holds them.
        var (listener, _, dir) = await CollectorAsync();
        var db = await pg.CreateDatabaseAsync();
        var h = new Harness(db, Options(d => { d.Port = listener.Port; d.CaBundlePath = "ca.pem"; }), dir);
        await h.AppendAsync(10);
        await h.Start();
        Assert.True(await Harness.WaitUntilAsync(() => h.Status.LastForwardedSeq == 10));
        Assert.False(h.Status.ChainBroken);

        await SuperuserAsync(db, "ALTER TABLE audit.event DISABLE TRIGGER event_immutable; DELETE FROM audit.event WHERE seq >= 9; ALTER TABLE audit.event ENABLE TRIGGER event_immutable;");
        Assert.True(await Harness.WaitUntilAsync(() => h.Status.ChainBroken, 30), "the monitor notices that event 10 is gone");
        await h.StopAsync();

        var alert = Assert.Single(listener.Messages.Select(Parse), p => p.Sd.ContainsKey("kind"));
        Assert.Equal(("tail_changed", "10"), (alert.Sd["kind"], alert.Sd["seq"]));
        Assert.Equal(10, DistinctSeqs(Events(listener)).Count); // and the SIEM still has all ten
    }

    // ---------------------------------------------------------------- tls

    [Fact]
    public async Task Mutual_tls_delivers_with_a_client_certificate_and_is_refused_without_one()
    {
        var (listener, pki, dir) = await CollectorAsync(mtls: true);
        using var client = pki.Issue("keel-forwarder", server: false);
        var (certPath, keyPath) = SyslogTestPki.WritePem(client, dir, "client");
        var db = await pg.CreateDatabaseAsync();

        var without = new Harness(db, Options(d => { d.Port = listener.Port; d.CaBundlePath = "ca.pem"; d.Name = "nocert"; }), dir);
        await without.AppendAsync(5);
        await without.Start();
        Assert.True(await Harness.WaitUntilAsync(() => without.Status.ConsecutiveFailures >= 1, 15));
        await without.StopAsync();
        Assert.Empty(listener.Messages);

        var with = new Harness(db, Options(d => { d.Port = listener.Port; d.CaBundlePath = "ca.pem"; d.ClientCertificatePath = Path.GetFileName(certPath); d.ClientKeyPath = Path.GetFileName(keyPath); }), dir);
        await with.Start();
        Assert.True(await listener.WaitForCountAsync(5, TimeSpan.FromSeconds(20)), $"status error: {with.Status.LastError}; rejected handshakes {listener.RejectedHandshakes}");
        await with.StopAsync();
        Assert.Contains(listener.ClientCertificateSubjects, s => s.Contains("keel-forwarder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_untrusted_or_misnamed_server_certificate_is_never_sent_audit_data()
    {
        var (listener, _, dir) = await CollectorAsync();
        var db = await pg.CreateDatabaseAsync();
        await File.WriteAllTextAsync(Path.Combine(dir, "other-ca.pem"), new SyslogTestPki("Some other CA").CaPem);

        var wrongCa = new Harness(db, Options(d => { d.Port = listener.Port; d.CaBundlePath = "other-ca.pem"; d.Name = "wrongca"; }), dir);
        await wrongCa.AppendAsync(3);
        await wrongCa.Start();
        Assert.True(await Harness.WaitUntilAsync(() => wrongCa.Status.ConsecutiveFailures >= 1, 15));
        await wrongCa.StopAsync();

        var wrongName = new Harness(db, Options(d => { d.Port = listener.Port; d.CaBundlePath = "ca.pem"; d.ServerName = "siem.wrong.example"; d.Name = "wrongname"; }), dir);
        await wrongName.Start();
        Assert.True(await Harness.WaitUntilAsync(() => wrongName.Status.ConsecutiveFailures >= 1, 15));
        await wrongName.StopAsync();

        Assert.Empty(listener.Messages);
    }

    // ---------------------------------------------------------------- content

    [Fact]
    public async Task Forwarded_messages_contain_no_names_descriptions_or_markings()
    {
        var (listener, _, dir) = await CollectorAsync();
        var db = await pg.CreateDatabaseAsync();
        var fileSink = Path.Combine(dir, "audit.ndjson");
        var options = Options(d => { d.Port = listener.Port; d.CaBundlePath = "ca.pem"; });
        options.Destinations.Add(new SiemDestinationOptions { Name = "files", Type = SiemDestinationType.File, FilePath = fileSink });
        var ds = db.AppDataSource();
        var audit = new PostgresAuditLog(ds, TimeProvider.System);
        var vault = new ObjectVault(await VaultContainer.OptionsAsync());
        var docs = new DocumentService(ds, audit, TimeProvider.System, vault);
        var parts = new Keel.Parts.PartService(ds, audit, TimeProvider.System);

        var alice = DocumentServiceTests.Alice;
        var markings = DocumentServiceTests.Itar();
        var part = await parts.CreateAsync(alice, new Keel.Parts.CreatePartCommand("Aurora secret bracket", "Component", "EA", "Make", markings, "Classified-looking description", new System.Text.Json.Nodes.JsonObject { ["material"] = "6061-T6" }), "c-1");
        Assert.True(part.IsOk, string.Join(";", part.Errors));
        var doc = await docs.CreateAsync(alice, new CreateDocumentCommand("Aurora secret specification", "Specification", markings, "Hidden description"),
            new FileUpload("aurora-secret-file.bin", null, new MemoryStream("synthetic bytes"u8.ToArray())), "c-2");
        Assert.True(doc.IsOk);
        await docs.GetAsync(DocumentServiceTests.Lena, doc.Value!.Number, null, "c-3"); // a denial
        await docs.SearchAsync(alice, "Aurora", null, null, 10, 0, "c-4");

        var total = (await DbEvents(ds)).Count;
        var h = new Harness(db, options, dir);
        await h.Start();
        Assert.True(await listener.WaitForCountAsync(total, TimeSpan.FromSeconds(20)));
        Assert.True(await Harness.WaitUntilAsync(() => File.Exists(fileSink) && File.ReadAllLines(fileSink).Length >= total));
        await h.StopAsync();

        var everything = string.Join("\n", listener.Messages) + "\n" + await File.ReadAllTextAsync(fileSink);
        foreach (var forbidden in new[] { "Aurora", "secret", "Hidden description", "description", "ITAR", "USML", "EXPT", "CUI", "DISTRIBUTION", "aurora-secret-file", "Classified", "synthetic bytes", "6061" })
            Assert.DoesNotContain(forbidden, everything, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("document.create", everything, StringComparison.Ordinal); // but the events themselves are there
        Assert.Contains("Denied", everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checkpoints_and_verification_results_are_forwarded()
    {
        var (listener, _, dir) = await CollectorAsync();
        var db = await pg.CreateDatabaseAsync();
        var h = new Harness(db, Options(d => { d.Port = listener.Port; d.CaBundlePath = "ca.pem"; }), dir);
        await h.AppendAsync(5);
        await h.Audit.AppendAsync(new AuditEntry
        {
            Actor = ActorSnapshot.System, Action = "audit.verify", Outcome = AuditOutcome.Failure, ObjectType = "audit_log", Details = AuditDetails.Of(("failures", "2")),
        });
        using var key = CheckpointKeys.Generate();
        var checkpoint = await new CheckpointSigner(key, h.Ds, TimeProvider.System).CreateAsync();
        Assert.NotNull(checkpoint);

        await h.Start();
        Assert.True(await Harness.WaitUntilAsync(() => listener.Messages.Count >= 7));
        await h.StopAsync();

        var verify = Assert.Single(listener.Messages.Select(Parse), p => p.MsgId == "audit.verify");
        Assert.Equal(("Failure", 13 * 8 + 3), (verify.Sd["outcome"], verify.Priority));
        var cp = Assert.Single(listener.Messages.Select(Parse), p => p.MsgId == "audit.checkpoint");
        Assert.Equal((checkpoint!.Seq.ToString(CultureInfo.InvariantCulture), Convert.ToHexStringLower(checkpoint.Hash), CheckpointKeys.KeyId(key)), (cp.Sd["seq"], cp.Sd["hash"], cp.Sd["keyId"]));
        await using var cmd = h.Ds.CreateCommand("SELECT last_seq FROM siem.cursor WHERE destination = 'primary' AND stream = 'checkpoint'");
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
    }

    // ---------------------------------------------------------------- file sink

    [Fact]
    public async Task The_file_sink_writes_ndjson_and_survives_external_rotation()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "forward", "audit.ndjson");
        var db = await pg.CreateDatabaseAsync();
        var options = Options(top: o => o.Destinations = [new SiemDestinationOptions { Name = "files", Type = SiemDestinationType.File, FilePath = path }]);
        var h = new Harness(db, options);
        await h.AppendAsync(30);
        await h.Start();
        Assert.True(await Harness.WaitUntilAsync(() => h.Status.LastForwardedSeq == 30));

        File.Move(path, path + ".1"); // logrotate
        await h.AppendAsync(25, start: 30);
        Assert.True(await Harness.WaitUntilAsync(() => h.Status.LastForwardedSeq == 55));
        await h.StopAsync();

        var lines = File.ReadAllLines(path + ".1").Concat(File.ReadAllLines(path)).ToList();
        var seqs = lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("seq").GetInt64()).Distinct().Order().ToList();
        Assert.Equal(Enumerable.Range(1, 55).Select(i => (long)i), seqs);
        Assert.NotEmpty(File.ReadAllLines(path));
        var first = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal(("audit", "keel-test"), (first.GetProperty("stream").GetString(), first.GetProperty("host").GetString()));
        Assert.Equal(64, first.GetProperty("hash").GetString()!.Length);
    }

    // ---------------------------------------------------------------- host integration

    [Fact]
    public async Task The_host_reports_forwarding_in_readiness_and_metrics()
    {
        var dir = TempDir();
        await using var api = await ApiFactory.CreateAsync(pg);
        api.ExtraSettings["Keel:Siem:Enabled"] = "true";
        api.ExtraSettings["Keel:Siem:PollIntervalMilliseconds"] = "50";
        api.ExtraSettings["Keel:Siem:Destinations:0:Name"] = "file1";
        api.ExtraSettings["Keel:Siem:Destinations:0:Type"] = "File";
        api.ExtraSettings["Keel:Siem:Destinations:0:FilePath"] = Path.Combine(dir, "audit.ndjson");
        var admin = api.ClientFor(ApiFactory.Token(sub: "u-ada", roles: ["plm-viewer", "plm-admin", "plm-auditor"]));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/audit/events")).StatusCode); // writes an audit.read event

        var ready = await api.CreateClient().GetAsync("/health/ready");
        using var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.True(body.RootElement.GetProperty("checks").TryGetProperty("siem", out var siem));
        Assert.NotEqual("Unhealthy", siem.GetProperty("status").GetString());

        var path = Path.Combine(dir, "audit.ndjson");
        for (var i = 0; i < 100 && !(File.Exists(path) && File.ReadAllText(path).Contains("audit.read", StringComparison.Ordinal)); i++) await Task.Delay(100);
        Assert.Contains("audit.read", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        // The counter is incremented just after the write the loop above waited for, so poll briefly instead of reading once.
        var metrics = "";
        for (var i = 0; i < 50; i++)
        {
            metrics = await api.CreateClient().GetStringAsync("/metrics");
            if (metrics.Contains("keel_siem_events_forwarded_total", StringComparison.Ordinal)) break;
            await Task.Delay(100);
        }
        Assert.Contains("keel_siem_lag_events{destination=\"file1\"}", metrics, StringComparison.Ordinal);
        Assert.Contains("keel_siem_events_forwarded_total", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Udp_and_plain_tcp_configurations_stop_the_host_from_starting()
    {
        await using var udp = await ApiFactory.CreateAsync(pg);
        udp.ExtraSettings["Keel:Siem:Enabled"] = "true";
        udp.ExtraSettings["Keel:Siem:Destinations:0:Name"] = "udp1";
        udp.ExtraSettings["Keel:Siem:Destinations:0:Host"] = "siem";
        udp.ExtraSettings["Keel:Siem:Destinations:0:Transport"] = "Udp";
        var e = Assert.ThrowsAny<Exception>(() => udp.CreateClient());
        Assert.Contains("UDP syslog is not supported", e.ToString(), StringComparison.Ordinal);

        await using var tcp = await ApiFactory.CreateAsync(pg);
        tcp.ExtraSettings["Keel:Siem:Enabled"] = "true";
        tcp.ExtraSettings["Keel:Siem:Destinations:0:Name"] = "tcp1";
        tcp.ExtraSettings["Keel:Siem:Destinations:0:Host"] = "siem";
        tcp.ExtraSettings["Keel:Siem:Destinations:0:Transport"] = "Tcp";
        var e2 = Assert.ThrowsAny<Exception>(() => tcp.CreateClient());
        Assert.Contains("plain TCP syslog", e2.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_forwarding_reports_itself_as_not_configured()
    {
        var tracker = new SiemStatusTracker();
        Assert.False(tracker.Current().Enabled);
        Assert.Equal(HealthStatus.Healthy, (await new SiemHealthCheck(tracker).CheckHealthAsync(new HealthCheckContext())).Status);
    }
}
