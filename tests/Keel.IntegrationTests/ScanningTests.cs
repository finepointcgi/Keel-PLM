using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Keel.Audit;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Scanning;
using Keel.Documents.Vault;
using Keel.TestSupport;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Malware scanning of uploads end to end: the real document service, vault (SeaweedFS), PostgreSQL and REST API, with a protocol-faithful
/// clamd (real TCP) as the scanner (ADR-0026). Nothing is mocked.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ScanningTests(PostgresFixture pg)
{
    private static readonly Subject Alice = DocumentServiceTests.Alice;
    private static readonly Subject Ada = DocumentServiceTests.Ada;
    private static readonly byte[] Eicar = FakeClamd.Eicar;

    private static ScanningOptions ScanOptions(FakeClamd? server, ScanMode mode) => new()
    {
        Mode = mode, Host = "127.0.0.1", Port = server?.Port ?? 1, ConnectTimeoutSeconds = 1, IoTimeoutSeconds = 5, ResultTimeoutSeconds = 5, ChunkBytes = 64 * 1024,
        MaxScanBytes = 64L * 1024 * 1024,
    };

    private sealed record Env(DocumentService Docs, ObjectVault Vault, NpgsqlDataSource Ds, TestDatabase Db, PostgresAuditLog Audit);

    private async Task<Env> SetupAsync(FakeClamd? server, ScanMode mode, TestDatabase? db = null)
    {
        db ??= await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var audit = new PostgresAuditLog(ds, TimeProvider.System);
        var options = ScanOptions(server, mode);
        var scanning = new UploadScanning(mode == ScanMode.Disabled ? new NoScanner() : new ClamdScanner(options), options, TimeProvider.System);
        var vault = new ObjectVault(await VaultContainer.OptionsAsync(), scanning, new PostgresVaultPendingGuard(ds, TimeProvider.System));
        return new Env(new DocumentService(ds, audit, TimeProvider.System, vault), vault, ds, db, audit);
    }

    private static CreateDocumentCommand Cmd(string name) => new(name, "Specification", DocumentServiceTests.Itar(), "Synthetic");
    private static FileUpload Upload(string name, byte[] bytes) => new(name, "application/octet-stream", new MemoryStream(bytes));
    private static byte[] Unique(string text) => Encoding.UTF8.GetBytes($"{text}\n{Guid.NewGuid()}\n");
    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
    private static async Task<List<AuditRecord>> Audit(NpgsqlDataSource ds) => [.. await new AuditQueries(ds).ListAsync(new AuditQuery(Limit: 1000))];

    private static async Task<long> Scalar(NpgsqlDataSource ds, string sql)
    {
        await using var cmd = ds.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> StagingObjectsAsync(ObjectVault vault)
    {
        var count = 0;
        string? after = null;
        while (true)
        {
            var page = await vault.ListAsync(after, 1000);
            count += page.Objects.Count(o => ObjectVault.IsStagingKey(o.Key));
            if (page.NextToken is null) return count;
            after = page.NextToken;
        }
    }

    // ---------------------------------------------------------------- DocumentService

    [Fact]
    public async Task Clean_upload_is_stored_and_its_scan_is_recorded()
    {
        await using var server = new FakeClamd();
        var env = await SetupAsync(server, ScanMode.Required);
        var bytes = Unique("clean specification");
        var doc = (await env.Docs.CreateAsync(Alice, Cmd("Clean spec"), Upload("clean.bin", bytes), "c1")).Value!;

        var file = Assert.Single(doc.Iteration.Files);
        Assert.Equal("clean", file.ScanStatus);
        Assert.False(file.Quarantined);
        Assert.True(await env.Vault.ExistsAsync(Sha(bytes), bytes.Length));
        Assert.Equal(bytes.Length, server.BytesReceived);
        await using var cmd = env.Ds.CreateCommand("SELECT status, scanner_name, scanner_version, signature_db_date, source FROM plm.file_scan");
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(("clean", "clamd", "upload"), (r.GetString(0), r.GetString(1), r.GetString(4)));
        Assert.Contains("1.4.1", r.GetString(2), StringComparison.Ordinal);
        Assert.False(r.IsDBNull(3));
    }

    [Fact]
    public async Task Eicar_upload_is_rejected_and_nothing_is_stored_or_referenced()
    {
        await using var server = new FakeClamd();
        var env = await SetupAsync(server, ScanMode.Required);
        var meter = new Dictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == "Keel.Documents" && i.Name == "keel_upload_scans") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var t in tags) if (t.Key == "result") lock (meter) meter[(string)t.Value!] = meter.GetValueOrDefault((string)t.Value!) + value;
        });
        listener.Start();

        // The vault bucket is shared by the whole suite and other tests store the bare EICAR bytes (scanning disabled),
        // so this test uses its own unique payload that still contains the EICAR string.
        var payload = Unique("infected spec").Concat(Eicar).ToArray();
        var outcome = await env.Docs.CreateAsync(Alice, Cmd("Infected spec"), Upload("invoice.pdf.exe", payload), "c2");

        Assert.Equal(OutcomeKind.Unprocessable, outcome.Kind);
        Assert.Equal(FakeClamd.EicarSignature, outcome.Errors[1]);
        Assert.False(await env.Vault.ExistsAsync(Sha(payload), payload.Length), "nothing under the content address");
        Assert.Equal(0, await Scalar(env.Ds, "SELECT count(*) FROM plm.document_master"));
        Assert.Equal(0, await Scalar(env.Ds, "SELECT count(*) FROM plm.file_scan"));
        var evt = Assert.Single(await Audit(env.Ds), e => e.Action == "document.upload_infected");
        Assert.Equal(AuditOutcome.Failure, evt.Outcome);
        Assert.Contains("invoice.pdf.exe", evt.Payload, StringComparison.Ordinal);
        Assert.Contains(Sha(payload), evt.Payload, StringComparison.Ordinal);
        Assert.Contains(FakeClamd.EicarSignature, evt.Payload, StringComparison.Ordinal);
        Assert.Contains($"\"size\":\"{payload.Length}\"", evt.Payload, StringComparison.Ordinal);
        lock (meter) Assert.Equal(1, meter.GetValueOrDefault("infected"));
    }

    [Fact]
    public async Task Eicar_inside_a_large_multipart_upload_is_rejected_and_staging_is_cleaned()
    {
        await using var server = new FakeClamd();
        var env = await SetupAsync(server, ScanMode.Required);
        var data = RandomNumberGenerator.GetBytes(12 * 1024 * 1024 + 99); // three parts of 5 MiB
        Eicar.CopyTo(data, 6 * 1024 * 1024); // in the second part
        var stagingBefore = await StagingObjectsAsync(env.Vault);

        var outcome = await env.Docs.CreateAsync(Alice, Cmd("Large infected"), Upload("big.bin", data), null);

        Assert.Equal(OutcomeKind.Unprocessable, outcome.Kind);
        Assert.False(await env.Vault.ExistsAsync(Sha(data), data.Length));
        Assert.Equal(stagingBefore, await StagingObjectsAsync(env.Vault));
        Assert.Equal(data.Length, server.BytesReceived);
        Assert.True(server.LargestChunk <= 64 * 1024);
        Assert.Contains((await Audit(env.Ds)), e => e.Action == "document.upload_infected");
    }

    [Fact]
    public async Task Large_clean_upload_is_scanned_during_the_multipart_stream_without_a_second_pass()
    {
        await using var server = new FakeClamd();
        var env = await SetupAsync(server, ScanMode.Required);
        var data = RandomNumberGenerator.GetBytes(12 * 1024 * 1024 + 7);
        var doc = (await env.Docs.CreateAsync(Alice, Cmd("Large clean"), Upload("big.bin", data), null)).Value!;

        Assert.Equal("clean", doc.Iteration.Files[0].ScanStatus);
        Assert.Equal(data.Length, server.BytesReceived); // exactly one pass: every byte once
        Assert.Equal(1, server.InstreamCount);
        await env.Vault.VerifyAsync(Sha(data), data.Length);
    }

    [Fact]
    public async Task Required_mode_refuses_with_unavailable_when_the_scanner_is_down()
    {
        var server = new FakeClamd();
        var env = await SetupAsync(server, ScanMode.Required);
        await server.DisposeAsync();
        var bytes = Unique("while the scanner is down");

        var outcome = await env.Docs.CreateAsync(Alice, Cmd("No scanner"), Upload("a.bin", bytes), null);

        Assert.Equal(OutcomeKind.Unavailable, outcome.Kind);
        Assert.False(await env.Vault.ExistsAsync(Sha(bytes), bytes.Length));
        Assert.Equal(0, await Scalar(env.Ds, "SELECT count(*) FROM plm.document_master"));
        var evt = Assert.Single(await Audit(env.Ds), e => e.Action == "document.upload_refused");
        Assert.Contains("scan.unavailable", evt.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Best_effort_accepts_the_upload_flagged_unscanned_when_the_scanner_is_down()
    {
        var server = new FakeClamd();
        var env = await SetupAsync(server, ScanMode.BestEffort);
        await server.DisposeAsync();
        var bytes = Unique("best effort");

        var doc = (await env.Docs.CreateAsync(Alice, Cmd("Unscanned spec"), Upload("a.bin", bytes), null)).Value!;

        Assert.Equal("unscanned", doc.Iteration.Files[0].ScanStatus);
        Assert.True(await env.Vault.ExistsAsync(Sha(bytes), bytes.Length));
        Assert.Equal(1, await Scalar(env.Ds, "SELECT count(*) FROM plm.file_scan WHERE status = 'unscanned' AND reason IS NOT NULL"));
        Assert.Equal(1, await Scalar(env.Ds, "SELECT count(*) FROM plm.document_file_scan_status WHERE scan_status = 'unscanned'"));
    }

    [Fact]
    public async Task Disabled_mode_records_skipped()
    {
        var env = await SetupAsync(null, ScanMode.Disabled);
        var doc = (await env.Docs.CreateAsync(Alice, Cmd("Unchecked spec"), Upload("a.bin", Unique("disabled")), null)).Value!;
        Assert.Equal("skipped", doc.Iteration.Files[0].ScanStatus);
    }

    [Fact]
    public async Task Working_copy_uploads_are_scanned_too()
    {
        await using var server = new FakeClamd();
        var env = await SetupAsync(server, ScanMode.Required);
        var doc = (await env.Docs.CreateAsync(Alice, Cmd("Working spec"), null, null)).Value!;
        await env.Docs.CheckOutAsync(Alice, doc.Number, null);

        var infected = await env.Docs.UploadFileAsync(Alice, doc.Number, "attachment", Upload("macro.docm", Eicar), "c9");
        Assert.Equal(OutcomeKind.Unprocessable, infected.Kind);
        var clean = await env.Docs.UploadFileAsync(Alice, doc.Number, "attachment", Upload("notes.txt", Unique("notes")), null);
        Assert.True(clean.IsOk);
        Assert.Single(clean.Value!.Checkout!.Files!);
        Assert.Equal(1, await Scalar(env.Ds, "SELECT count(*) FROM plm.file_scan WHERE status = 'clean'"));
    }

    // ---------------------------------------------------------------- rescan and quarantine

    [Fact]
    public async Task Rescan_quarantines_a_stored_infection_per_document_and_downloads_are_refused()
    {
        await using var server = new FakeClamd();
        var db = await pg.CreateDatabaseAsync();
        var unscanned = await SetupAsync(null, ScanMode.Disabled, db); // how an infected file got in: scanning was off
        var scanning = await SetupAsync(server, ScanMode.Required, db);
        var shared = Eicar;
        var a = (await unscanned.Docs.CreateAsync(Alice, Cmd("Doc A"), Upload("a.bin", shared), null)).Value!;
        var b = (await unscanned.Docs.CreateAsync(Alice, Cmd("Doc B"), Upload("b.bin", shared), null)).Value!; // same object, other document
        var c = (await unscanned.Docs.CreateAsync(Alice, Cmd("Doc C"), Upload("c.bin", Unique("harmless")), null)).Value!;

        // Only administrators rescan; the denial is audited.
        var denied = await scanning.Docs.RescanAsync(Alice, new RescanRequest(), "rc0");
        Assert.Equal(OutcomeKind.Forbidden, denied.Kind);
        Assert.Contains((await Audit(scanning.Ds)), e => e is { Action: "document.rescan", Outcome: AuditOutcome.Denied });

        var report = (await scanning.Docs.RescanAsync(Ada, new RescanRequest(), "rc1")).Value!;
        Assert.Equal((2, 1, 1, 2), (report.Examined, report.Clean, report.Infected, report.DocumentsQuarantined));
        Assert.Null(report.Continuation);

        // The infected document refuses delivery with 409; the harmless one is unaffected; bytes and trail stay.
        var downloadA = await scanning.Docs.DownloadAsync(Alice, a.Number, a.Iteration.Files[0].Id, null, null, "d1");
        Assert.Equal(OutcomeKind.Conflict, downloadA.Kind);
        Assert.Contains("quarantined", downloadA.Errors[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Conflict, (await scanning.Docs.DownloadAsync(Alice, b.Number, b.Iteration.Files[0].Id, null, null, null)).Kind);
        Assert.True((await scanning.Docs.DownloadAsync(Alice, c.Number, c.Iteration.Files[0].Id, null, null, null)).IsOk);
        Assert.True(await scanning.Vault.ExistsAsync(Sha(shared), shared.Length));
        var events = await Audit(scanning.Ds);
        Assert.Equal(2, events.Count(e => e.Action == "document.quarantine"));
        Assert.Contains(events, e => e is { Action: "document.download", Outcome: AuditOutcome.Failure } && e.Payload.Contains("file.quarantined", StringComparison.Ordinal));

        // The detail view shows the state.
        var detail = (await scanning.Docs.GetAsync(Alice, a.Number, null, null)).Value!;
        Assert.True(detail.Iteration.Files[0].Quarantined);
        Assert.Equal("infected", detail.Iteration.Files[0].ScanStatus);

        // Releasing A (reason required, admin only) does not release B: the flag belongs to the document, not the shared object.
        Assert.Equal(OutcomeKind.Forbidden, (await scanning.Docs.ReleaseQuarantineAsync(Alice, a.Number, Sha(shared), "false positive", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await scanning.Docs.ReleaseQuarantineAsync(Ada, a.Number, Sha(shared), " ", null)).Kind);
        Assert.True((await scanning.Docs.ReleaseQuarantineAsync(Ada, a.Number, Sha(shared), "reviewed by security office", null)).IsOk);
        Assert.True((await scanning.Docs.DownloadAsync(Alice, a.Number, a.Iteration.Files[0].Id, null, null, null)).IsOk);
        Assert.Equal(OutcomeKind.Conflict, (await scanning.Docs.DownloadAsync(Alice, b.Number, b.Iteration.Files[0].Id, null, null, null)).Kind);

        // Coverage for auditors.
        var coverage = (await scanning.Docs.GetScanCoverageAsync(Ada, null)).Value!;
        Assert.Equal(3, coverage.Files);
        Assert.Equal((1, 2, 0, 1), (coverage.Clean, coverage.Infected, coverage.Unscanned + coverage.Skipped, coverage.Quarantined));
        Assert.Equal("Required", coverage.Mode);
        Assert.Equal("clamd", coverage.Scanner);
        Assert.NotNull(coverage.SignatureDatabaseDate);
    }

    [Fact]
    public async Task Quarantined_content_cannot_be_checked_in_again()
    {
        await using var server = new FakeClamd();
        var db = await pg.CreateDatabaseAsync();
        var off = await SetupAsync(null, ScanMode.Disabled, db);
        var on = await SetupAsync(server, ScanMode.Required, db);
        var doc = (await off.Docs.CreateAsync(Alice, Cmd("Carry-over"), Upload("a.bin", Eicar), null)).Value!;
        await on.Docs.RescanAsync(Ada, new RescanRequest(), null);

        Assert.True((await on.Docs.CheckOutAsync(Alice, doc.Number, null)).IsOk);
        var checkin = await on.Docs.CheckInAsync(Alice, doc.Number, "again", null);
        Assert.Equal(OutcomeKind.Conflict, checkin.Kind);
        Assert.Contains("quarantined", checkin.Errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rescan_pages_through_a_continuation_and_skips_clean_files_unless_asked()
    {
        await using var server = new FakeClamd();
        var db = await pg.CreateDatabaseAsync();
        var off = await SetupAsync(null, ScanMode.Disabled, db);
        var on = await SetupAsync(server, ScanMode.Required, db);
        for (var i = 0; i < 3; i++) await off.Docs.CreateAsync(Alice, Cmd($"Doc {i}"), Upload($"f{i}.bin", Unique("paged " + i)), null);

        var first = (await on.Docs.RescanAsync(Ada, new RescanRequest(Limit: 2), null)).Value!;
        Assert.Equal((2, 2), (first.Examined, first.Clean));
        Assert.NotNull(first.Continuation);
        var second = (await on.Docs.RescanAsync(Ada, new RescanRequest(Limit: 2, AfterSha256: first.Continuation), null)).Value!;
        Assert.Equal((1, 1, (string?)null), (second.Examined, second.Clean, second.Continuation));

        Assert.Equal(0, (await on.Docs.RescanAsync(Ada, new RescanRequest(), null)).Value!.Examined); // everything is clean now
        Assert.Equal(3, (await on.Docs.RescanAsync(Ada, new RescanRequest(All: true), null)).Value!.Examined);
    }

    [Fact]
    public async Task Rescan_needs_a_scanner()
    {
        var env = await SetupAsync(null, ScanMode.Disabled);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.RescanAsync(Ada, new RescanRequest(), null)).Kind);

        var server = new FakeClamd();
        var down = await SetupAsync(server, ScanMode.Required);
        await server.DisposeAsync();
        Assert.Equal(OutcomeKind.Unavailable, (await down.Docs.RescanAsync(Ada, new RescanRequest(), null)).Kind);
    }

    [Fact]
    public async Task Migration_imports_go_through_the_same_scanner()
    {
        await using var server = new FakeClamd();
        var options = ScanOptions(server, ScanMode.Required);
        using var vault = new ObjectVault(await VaultContainer.OptionsAsync(), new UploadScanning(new ClamdScanner(options), options, TimeProvider.System));
        var store = new VaultContentStore(vault);

        var e = await Assert.ThrowsAsync<ContentRejectedException>(() => store.PutAsync(new MemoryStream(Eicar)));
        Assert.Contains(FakeClamd.EicarSignature, e.Message, StringComparison.Ordinal);
        var ok = Unique("imported file");
        Assert.Equal(Sha(ok), (await store.PutAsync(new MemoryStream(ok))).Sha256);
    }

    // ---------------------------------------------------------------- REST

    private static MultipartFormDataContent Form(string metadata, string fileName, byte[] bytes)
    {
        var form = new MultipartFormDataContent { { new StringContent(metadata, Encoding.UTF8, "application/json"), "metadata" } };
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", fileName);
        return form;
    }

    private const string OpenMarkings = """{"exportJurisdiction":"None","distributionStatement":"A","owningOrg":"ENG"}""";
    private static string Metadata(string name) => $$"""{"name":"{{name}}","documentType":"Other","markings":{{OpenMarkings}}}""";

    [Fact]
    public async Task Rest_upload_maps_infected_to_422_and_unavailable_to_503_and_reports_metrics()
    {
        await using var server = new FakeClamd();
        await using var api = await ApiFactory.CreateAsync(pg);
        api.ExtraSettings["Keel:Vault:Scanning:Mode"] = "Required";
        api.ExtraSettings["Keel:Vault:Scanning:Port"] = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        api.ExtraSettings["Keel:Vault:Scanning:MaxScanBytes"] = "67108864";
        var author = api.ClientFor(ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"]));
        var admin = api.ClientFor(ApiFactory.Token(sub: "u-ada", roles: ["plm-viewer", "plm-admin"]));

        var clean = await author.PostAsync("/api/v1/documents", Form(Metadata("Clean doc"), "ok.txt", Unique("rest clean")));
        Assert.Equal(HttpStatusCode.Created, clean.StatusCode);

        var infected = await author.PostAsync("/api/v1/documents", Form(Metadata("Bad doc"), "bad.exe", Eicar));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, infected.StatusCode);
        using (var body = JsonDocument.Parse(await infected.Content.ReadAsStringAsync()))
            Assert.Equal(FakeClamd.EicarSignature, body.RootElement.GetProperty("signature").GetString());
        Assert.False((await author.GetFromJsonAsync<JsonElement>("/api/v1/documents?q=Bad")).GetProperty("total").GetInt64() > 0);

        // Coverage and rescan are administrator functions.
        Assert.Equal(HttpStatusCode.Forbidden, (await author.PostAsJsonAsync("/api/v1/admin/vault/rescan", new { })).StatusCode);
        var coverage = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/vault/scan-coverage");
        Assert.Equal(1, coverage.GetProperty("clean").GetInt64());
        Assert.Equal("Required", coverage.GetProperty("mode").GetString());

        var metrics = await api.CreateClient().GetStringAsync("/metrics");
        Assert.Contains("keel_upload_scans_total", metrics, StringComparison.Ordinal);
        Assert.Contains("result=\"infected\"", metrics, StringComparison.Ordinal);

        // Scanner goes away: Required mode refuses with 503 and Retry-After; readiness reports the scanner Unhealthy.
        server.DropConnections = true;
        await server.DisposeAsync();
        var refused = await author.PostAsync("/api/v1/documents", Form(Metadata("Late doc"), "late.txt", Unique("rest late")));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.True(refused.Headers.RetryAfter is not null);
        var ready = await api.CreateClient().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Contains("scanner", await ready.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rest_download_of_a_quarantined_file_is_409()
    {
        await using var server = new FakeClamd();
        await using var api = await ApiFactory.CreateAsync(pg);
        // Upload with scanning disabled (the default), then switch the same data to a host that scans and rescan there.
        var author = api.ClientFor(ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"]));
        var created = await author.PostAsync("/api/v1/documents", Form(Metadata("Old upload"), "old.bin", Eicar));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var fileId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("iteration").GetProperty("files")[0].GetProperty("id").GetString();

        await using var scanningApi = new ApiFactory(api.Database, await WriteKeyAsync(), api.Vault);
        scanningApi.ExtraSettings["Keel:Vault:Scanning:Mode"] = "Required";
        scanningApi.ExtraSettings["Keel:Vault:Scanning:Port"] = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var admin = scanningApi.ClientFor(ApiFactory.Token(sub: "u-ada", roles: ["plm-viewer", "plm-admin"]));
        var rescan = await admin.PostAsJsonAsync("/api/v1/admin/vault/rescan", new { });
        Assert.Equal(HttpStatusCode.OK, rescan.StatusCode);
        Assert.Equal(1, JsonDocument.Parse(await rescan.Content.ReadAsStringAsync()).RootElement.GetProperty("infected").GetInt64());

        var download = await scanningApi.ClientFor(ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"]))
            .GetAsync($"/api/v1/documents/D-000001/files/{fileId}/download");
        Assert.Equal(HttpStatusCode.Conflict, download.StatusCode);
    }

    private static async Task<string> WriteKeyAsync()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("keel-test-").FullName, "checkpoint.pem");
        using var key = CheckpointKeys.Generate();
        await File.WriteAllTextAsync(path, key.ExportPkcs8PrivateKeyPem());
        return path;
    }
}
