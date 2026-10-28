using System.Security.Cryptography;
using System.Text;
using Amazon.S3.Model;
using Keel.Audit;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Vault;
using Keel.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Vault garbage collection against the real SeaweedFS and PostgreSQL (ADR-0026). The bucket is shared with other tests, so every sweep is limited to
/// a random four-hex-digit slice of the key space and the test's own objects are mined to fall inside it; staging and foreign keys use unique prefixes.
/// Time is manual: objects get their real last-modified time, the collector's clock is moved days ahead.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VaultGcTests(PostgresFixture pg)
{
    private static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    private sealed class Env(DocumentService docs, ObjectVault vault, VaultGarbageCollector gc, NpgsqlDataSource ds, ManualTimeProvider clock, string prefix, VaultOptions options)
    {
        public DocumentService Docs => docs;
        public ObjectVault Vault => vault;
        public VaultGarbageCollector Gc => gc;
        public NpgsqlDataSource Ds => ds;
        public ManualTimeProvider Clock => clock;
        public string Prefix => prefix;
        public VaultOptions Options => options;
        public PostgresVaultPendingGuard Guard { get; init; } = null!;

        public Task<GcReport> RunAsync(GcMode mode, int max = 1000, string? token = null, TimeSpan? grace = null) =>
            gc.RunAsync(new GcRequest(mode, grace ?? Grace, max, 100, token, ActorSnapshot.System, "gc-test", prefix));

        /// <summary>Bytes whose SHA-256 starts with this env's slice, so a sweep of the slice sees exactly the test's objects.</summary>
        public byte[] Mine()
        {
            while (true)
            {
                var bytes = Encoding.UTF8.GetBytes("gc test object " + Guid.NewGuid());
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)).StartsWith(prefix, StringComparison.Ordinal)) return bytes;
            }
        }

        public async Task<string> OrphanAsync() => (await vault.PutAsync(new MemoryStream(Mine()))).Sha256;
    }

    private async Task<Env> SetupAsync(TestDatabase? db = null, DateTimeOffset? start = null)
    {
        db ??= await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var clock = new ManualTimeProvider(start ?? DateTimeOffset.UtcNow);
        var audit = new PostgresAuditLog(ds, clock);
        var options = await VaultContainer.OptionsAsync();
        var guard = new PostgresVaultPendingGuard(ds, clock);
        var vault = new ObjectVault(options, null, guard);
        var prefix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2));
        return new Env(new DocumentService(ds, audit, clock, vault), vault, new VaultGarbageCollector(vault, ds, audit, clock), ds, clock, prefix, options) { Guard = guard };
    }

    private static CreateDocumentCommand Cmd(string name) => new(name, "Specification", DocumentServiceTests.Itar(), "Synthetic");

    private static async Task<List<AuditRecord>> Audit(NpgsqlDataSource ds) => [.. await new AuditQueries(ds).ListAsync(new AuditQuery(Limit: 1000))];

    private static async Task<string> ReferencedAsync(Env env)
    {
        var doc = (await env.Docs.CreateAsync(DocumentServiceTests.Alice, Cmd("Referenced " + Guid.NewGuid()), new FileUpload("ref.bin", null, new MemoryStream(env.Mine())), null)).Value!;
        return doc.Iteration.Files[0].Sha256;
    }

    // ---------------------------------------------------------------- detection, grace, dry run

    [Fact]
    public async Task Dry_run_reports_orphans_and_referenced_objects_and_deletes_nothing()
    {
        var env = await SetupAsync();
        var orphans = new[] { await env.OrphanAsync(), await env.OrphanAsync(), await env.OrphanAsync() };
        var kept = await ReferencedAsync(env);
        env.Clock.Advance(TimeSpan.FromDays(2));

        var report = await env.RunAsync(GcMode.DryRun);

        Assert.Equal((4, 1, 3, 0), (report.ObjectsListed, report.ObjectsReferenced, report.Candidates, report.Deleted));
        Assert.True(report.Complete);
        Assert.Null(report.Continuation);
        Assert.Equal(orphans.Order(StringComparer.Ordinal), report.SampleKeys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(kept, report.SampleKeys);
        foreach (var sha in orphans.Append(kept)) Assert.NotNull(await env.Vault.HeadAsync(sha));

        // The same state gives the same report hash; one more orphan changes it.
        Assert.Equal(report.ReportHash, (await env.RunAsync(GcMode.DryRun)).ReportHash);
        await env.OrphanAsync();
        Assert.NotEqual(report.ReportHash, (await env.RunAsync(GcMode.DryRun)).ReportHash);
    }

    [Fact]
    public async Task Delete_mode_removes_only_unreferenced_old_objects_and_audits_the_run()
    {
        var env = await SetupAsync();
        var orphans = new[] { await env.OrphanAsync(), await env.OrphanAsync() };
        var kept = await ReferencedAsync(env);
        env.Clock.Advance(TimeSpan.FromDays(2));
        var preview = await env.RunAsync(GcMode.DryRun);

        var report = await env.RunAsync(GcMode.Delete);

        Assert.Equal((2, 2), (report.Candidates, report.Deleted));
        Assert.Equal(preview.ReportHash, report.ReportHash); // the report that was reviewed is the set that was deleted
        foreach (var sha in orphans) Assert.Null(await env.Vault.HeadAsync(sha));
        Assert.NotNull(await env.Vault.HeadAsync(kept));
        await env.Vault.VerifyAsync(kept, (await env.Vault.HeadAsync(kept))!.SizeBytes); // still intact and downloadable

        // One audit event per run with counts and the hash, never per object and never keys.
        var events = (await Audit(env.Ds)).Where(e => e.Action == "vault.gc").ToList();
        Assert.Equal(2, events.Count);
        var last = events[^1];
        Assert.Equal(AuditOutcome.Success, last.Outcome);
        Assert.Contains(report.ReportHash, last.Payload, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"delete\"", last.Payload, StringComparison.Ordinal);
        Assert.Contains("\"deleted\":\"2\"", last.Payload, StringComparison.Ordinal);
        foreach (var sha in orphans) Assert.DoesNotContain(sha, last.Payload, StringComparison.Ordinal);

        await using var cmd = env.Ds.CreateCommand("SELECT mode, deleted, report_hash FROM vault.gc_run ORDER BY started_at DESC, id DESC LIMIT 1");
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(("delete", 2L, report.ReportHash), (r.GetString(0), r.GetInt64(1), r.GetString(2)));
    }

    [Fact]
    public async Task Objects_younger_than_the_grace_period_are_kept_and_the_minimum_is_enforced()
    {
        var env = await SetupAsync();
        await env.OrphanAsync();
        env.Clock.Advance(TimeSpan.FromHours(12));

        var young = await env.RunAsync(GcMode.Delete);
        Assert.Equal((1, 0, 0), (young.ObjectsYoung, young.Candidates, young.Deleted));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => env.RunAsync(GcMode.Delete, grace: TimeSpan.FromHours(23)));
        Assert.Contains(new GcOptions { GraceDays = 0 }.Validate(), e => e.Contains("GraceDays", StringComparison.Ordinal));
        Assert.Empty(new GcOptions().Validate());
        Assert.Equal(7, new GcOptions().GraceDays);
        Assert.False(new GcOptions().Enabled);
        Assert.Equal(GcMode.DryRun, new GcOptions().Mode);

        env.Clock.Advance(TimeSpan.FromHours(13)); // now older than a day
        Assert.Equal(1, (await env.RunAsync(GcMode.Delete)).Deleted);
    }

    [Fact]
    public async Task Max_deletions_caps_a_run_and_the_continuation_token_resumes_it()
    {
        var env = await SetupAsync();
        for (var i = 0; i < 5; i++) await env.OrphanAsync();
        env.Clock.Advance(TimeSpan.FromDays(2));

        var first = await env.RunAsync(GcMode.Delete, max: 2);
        Assert.Equal((2, false), (first.Deleted, first.Complete));
        Assert.NotNull(first.Continuation);
        var second = await env.RunAsync(GcMode.Delete, max: 2, token: first.Continuation);
        Assert.Equal((2, false), (second.Deleted, second.Complete));
        var third = await env.RunAsync(GcMode.Delete, max: 2, token: second.Continuation);
        Assert.Equal((1, true, (string?)null), (third.Deleted, third.Complete, third.Continuation));
        Assert.Equal(0, (await env.RunAsync(GcMode.DryRun)).Candidates);
    }

    [Fact]
    public async Task Working_copy_files_keep_their_objects_alive()
    {
        var env = await SetupAsync();
        var doc = (await env.Docs.CreateAsync(DocumentServiceTests.Alice, Cmd("Working copy"), null, null)).Value!;
        await env.Docs.CheckOutAsync(DocumentServiceTests.Alice, doc.Number, null);
        var upload = await env.Docs.UploadFileAsync(DocumentServiceTests.Alice, doc.Number, "attachment", new FileUpload("a.bin", null, new MemoryStream(env.Mine())), null);
        var sha = upload.Value!.Checkout!.Files![0].Sha256;
        env.Clock.Advance(TimeSpan.FromDays(2));

        var report = await env.RunAsync(GcMode.Delete);

        Assert.Equal((1, 0), (report.ObjectsReferenced, report.Deleted));
        Assert.NotNull(await env.Vault.HeadAsync(sha));
    }

    [Fact]
    public async Task Staging_leftovers_are_swept_and_foreign_keys_are_never_touched()
    {
        var env = await SetupAsync();
        using var s3 = ObjectVault.CreateClient(env.Options);
        var unique = Guid.NewGuid().ToString("N");
        var staging = $"staging/gc-test-{unique}";
        var foreign = $"foreign/gc-test-{unique}";
        foreach (var key in new[] { staging, foreign })
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = env.Options.Bucket, Key = key, ContentBody = "leftover" });
        env.Clock.Advance(TimeSpan.FromDays(2));

        var staged = await env.Gc.RunAsync(new GcRequest(GcMode.Delete, Grace, 10, 50, null, ActorSnapshot.System, null, staging));
        Assert.Equal((1, 1), (staged.Candidates, staged.Deleted));
        Assert.Null(await env.Vault.HeadAsync(staging));

        var other = await env.Gc.RunAsync(new GcRequest(GcMode.Delete, Grace, 10, 50, null, ActorSnapshot.System, null, foreign));
        Assert.Equal((1, 0, 0), (other.ObjectsUnrecognized, other.Candidates, other.Deleted));
        Assert.NotNull(await env.Vault.HeadAsync(foreign));
    }

    // ---------------------------------------------------------------- the race with uploads

    [Fact]
    public async Task A_pending_upload_marker_keeps_an_old_orphan_that_an_upload_is_about_to_reference()
    {
        var env = await SetupAsync();
        var sha = await env.OrphanAsync();
        env.Clock.Advance(TimeSpan.FromHours(25)); // the orphan is old enough now ...
        await env.Guard.MarkPendingAsync(sha);       // ... but an identical upload has just declared it needs the object

        var report = await env.RunAsync(GcMode.Delete);
        Assert.Equal((1, 0, 1), (report.Candidates, report.Deleted, report.SkippedByRecheck));
        Assert.NotNull(await env.Vault.HeadAsync(sha));

        env.Clock.Advance(PostgresVaultPendingGuard.Lease + TimeSpan.FromMinutes(1)); // an abandoned marker expires on its own
        Assert.Equal(1, (await env.RunAsync(GcMode.Delete)).Deleted);
        Assert.Null(await env.Vault.HeadAsync(sha));
    }

    [Fact]
    public async Task An_identical_upload_during_the_sweep_waits_for_the_key_lock_and_never_loses_its_object()
    {
        var env = await SetupAsync();
        var bytes = env.Mine();
        var sha = (await env.Vault.PutAsync(new MemoryStream(bytes))).Sha256; // an old orphan
        env.Clock.Advance(TimeSpan.FromDays(2));

        Task<StoredContent>? upload = null;
        var uploadBlocked = false;
        env.Gc.InsideLockHook = async key =>
        {
            // The sweeper is inside its critical section: reference check done, delete about to happen. The same bytes are uploaded right now.
            upload = env.Vault.PutAsync(new MemoryStream(bytes));
            await Task.Delay(750);
            uploadBlocked = !upload.IsCompleted; // it must be waiting for the key lock, not racing the delete
        };

        var report = await env.RunAsync(GcMode.Delete);
        Assert.Equal(1, report.Deleted);
        Assert.True(uploadBlocked, "the upload has to wait for the sweeper's lock");

        var stored = await upload!;
        Assert.Equal(sha, stored.Sha256);
        // The upload looked for the object after the sweeper was done: it found it gone and wrote it again.
        var head = await env.Vault.HeadAsync(sha);
        Assert.NotNull(head);
        await env.Vault.VerifyAsync(sha, bytes.Length);
    }

    [Fact]
    public async Task Racing_uploads_and_sweeps_never_leave_a_document_without_its_bytes()
    {
        var env = await SetupAsync();
        env.Clock.Advance(TimeSpan.FromDays(2));
        for (var round = 0; round < 12; round++)
        {
            var bytes = env.Mine();
            await env.Vault.PutAsync(new MemoryStream(bytes)); // an old orphan, then the same bytes arrive while the sweeper runs
            var sweep = Task.Run(() => env.RunAsync(GcMode.Delete));
            var create = Task.Run(() => env.Docs.CreateAsync(DocumentServiceTests.Alice, Cmd("Race " + round), new FileUpload("r.bin", null, new MemoryStream(bytes)), null));
            await Task.WhenAll(sweep, create);

            var doc = (await create).Value!;
            var file = doc.Iteration.Files[0];
            var download = await env.Docs.DownloadAsync(DocumentServiceTests.Alice, doc.Number, file.Id, null, null, null);
            Assert.True(download.IsOk);
            await env.Vault.VerifyAsync(file.Sha256, file.SizeBytes); // the bytes of every created document are in the vault
        }
    }

    [Fact]
    public async Task Only_one_sweep_runs_at_a_time()
    {
        var env = await SetupAsync();
        await env.OrphanAsync();
        env.Clock.Advance(TimeSpan.FromDays(2));
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        env.Gc.InsideLockHook = async _ => { entered.TrySetResult(); await release.Task; };

        var first = Task.Run(() => env.RunAsync(GcMode.Delete));
        await entered.Task;
        await Assert.ThrowsAsync<GcAlreadyRunningException>(() => env.RunAsync(GcMode.DryRun));
        release.SetResult();
        Assert.Equal(1, (await first).Deleted);
    }

    // ---------------------------------------------------------------- reference registry and admin surface

    [Fact]
    public async Task Every_hash_column_in_the_schema_is_classified_for_the_sweeper()
    {
        var db = await pg.CreateDatabaseAsync();
        var known = VaultReferences.Sources.Select(s => s.Qualified).Concat(VaultReferences.NotVaultContent.Select(n => n.Qualified)).ToHashSet(StringComparer.Ordinal);
        var found = new List<string>();
        await using (var conn = new NpgsqlConnection(db.SuperuserConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                """
                SELECT c.table_schema || '.' || c.table_name || '.' || c.column_name
                FROM information_schema.columns c
                JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name AND t.table_type = 'BASE TABLE'
                WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema', 'public')
                  AND (c.column_name ~* '(sha|hash|digest|checksum|etag)' OR c.column_name ~* '^(content|vault|blob|file)_(key|ref|address)$')
                ORDER BY 1
                """, conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) found.Add(r.GetString(0));
        }
        // A new migration that adds a hash or content-key column must say whether it keeps vault objects alive (VaultReferences.Sources,
        // which the sweeper consults) or not (NotVaultContent, with a reason). Until then this fails, so a table the sweeper does not know
        // about can never lose its objects to a sweep.
        var unclassified = found.Where(f => !known.Contains(f)).ToList();
        Assert.True(unclassified.Count == 0, "Hash columns the garbage collector does not know: " + string.Join(", ", unclassified));
        // The registry names real columns, and the sources are what the sweeper's SQL is built from.
        foreach (var k in known) Assert.Contains(k, found);
        foreach (var source in VaultReferences.Sources)
            Assert.Contains($"{source.Schema}.{source.Table} r WHERE r.{source.Column} = k", VaultReferences.ReferencedKeysSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admins_can_read_reports_and_run_a_dry_run_but_nobody_deletes_from_the_ui_surface()
    {
        var env = await SetupAsync();
        await env.OrphanAsync();
        env.Clock.Advance(TimeSpan.FromDays(2));
        var options = new VaultOptions();
        options.Gc.GraceDays = 1;
        var admin = new VaultGcAdmin(env.Gc, env.Ds, new PostgresAuditLog(env.Ds, env.Clock), options);

        Assert.Equal(OutcomeKind.Forbidden, (await admin.DryRunAsync(DocumentServiceTests.Alice, null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await admin.RecentRunsAsync(DocumentServiceTests.Alice, 5, null)).Kind);
        Assert.Contains((await Audit(env.Ds)), e => e is { Action: "vault.gc", Outcome: AuditOutcome.Denied });

        var run = await admin.DryRunAsync(DocumentServiceTests.Ada, "ui-1");
        Assert.True(run.IsOk);
        Assert.Equal(GcMode.DryRun, run.Value!.Mode);
        Assert.Equal(0, run.Value.Deleted);
        var runs = (await admin.RecentRunsAsync(DocumentServiceTests.Ada, 5, null)).Value!;
        Assert.Equal("dry-run", runs[0].Mode);
        Assert.Equal(run.Value.ReportHash, runs[0].ReportHash);
    }

    [Fact]
    public async Task The_scheduled_sweeper_is_off_by_default()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        using var client = api.CreateClient(); // starts the host
        Assert.DoesNotContain(api.Services.GetServices<IHostedService>(), s => s.GetType().Name == "VaultGcService");
        Assert.NotNull(api.Services.GetRequiredService<VaultGarbageCollector>());
    }
}
