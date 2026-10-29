using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Keel.Audit;
using Keel.Cad.Structure;
using Keel.Core;
using Keel.Core.Geometry;
using Keel.Documents;
using Keel.Documents.Vault;
using Keel.Security;
using Keel.TestSupport;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Model versioning (increment 9.1, ADR-0046) against real PostgreSQL and a real vault: the one-step upload, the history with server-measured geometry, the
/// comparison, restore, revise and the versions ZIP, with the access policy, release locks, quarantine, audit and concurrency of every other document action.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ModelVersionTests(PostgresFixture pg)
{
    private static readonly Subject Alice = DocumentServiceTests.Alice;
    private static readonly Subject Ada = DocumentServiceTests.Ada;
    private static readonly Subject Lena = DocumentServiceTests.Lena;
    private static readonly Subject Eve = DocumentServiceTests.User("eve", programs: []);
    private static readonly Subject Viewer = DocumentServiceTests.User("vera", roles: [Roles.Viewer]);
    private static readonly Subject Mallory = DocumentServiceTests.User("mallory", roles: []);

    private sealed record Env(DocumentService Docs, TestDatabase Db, NpgsqlDataSource Ds)
    {
        public async Task<long> CountAsync(string sql)
        {
            await using var conn = new NpgsqlConnection(Db.SuperuserConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task ExecAsync(string sql)
        {
            await using var conn = new NpgsqlConnection(Db.SuperuserConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        public Task<long> CheckoutsAsync() => CountAsync("SELECT count(*) FROM plm.document_checkout");

        public async Task<List<AuditRecord>> AuditAsync() => [.. await new AuditQueries(Ds).ListAsync(new AuditQuery(Limit: 5000))];
    }

    private async Task<Env> SetupAsync()
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var audit = new PostgresAuditLog(ds, TimeProvider.System);
        var docs = new DocumentService(ds, audit, TimeProvider.System, new ObjectVault(await VaultContainer.OptionsAsync()), null, null, new StepProductCounter());
        return new Env(docs, db, ds);
    }

    private static byte[] Bracket(double height = 30, double wall = 4, string title = "bracket") =>
        SyntheticMesh.ToBinaryStl(SyntheticMesh.Bracket(40, height, wall, 20), title + " " + Guid.NewGuid().ToString("N")[..8]);

    private static FileUpload Stl(byte[] bytes, string name = "bracket.stl") => new(name, "model/stl", new MemoryStream(bytes));

    private static async Task<string> CreateAsync(Env env, byte[]? first = null, Markings? markings = null, string name = "Gimbal bracket model", Subject? by = null, string fileName = "bracket.stl")
    {
        var r = await env.Docs.CreateAsync(by ?? Alice, new CreateDocumentCommand(name, "CAD", markings ?? DocumentServiceTests.Itar(), "Synthetic model"),
            Stl(first ?? Bracket(), fileName), "corr-create");
        Assert.True(r.IsOk, string.Join("; ", r.Errors));
        return r.Value!.Number;
    }

    private static async Task<DocumentDetail> UploadOk(Env env, string number, byte[] bytes, string note = "Taller upright", Subject? by = null, bool revise = false, int? expected = null, string name = "bracket.stl")
    {
        var r = await env.Docs.UploadNewVersionAsync(by ?? Alice, number, Stl(bytes, name), note, revise, expected, "corr-upload");
        Assert.True(r.IsOk, $"{r.Kind}: {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    private static async Task<ModelVersionHistory> HistoryAsync(Env env, string number, Subject? by = null)
    {
        var r = await env.Docs.GetModelVersionsAsync(by ?? Alice, number, "corr-history");
        Assert.True(r.IsOk, $"{r.Kind}: {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    private static async Task ReleaseAsync(Env env, string number)
    {
        var r = await env.Docs.SetStateAsync(Ada, number, "Released", "test release", adminOverride: true, "corr-release");
        Assert.True(r.IsOk, $"{r.Kind}: {string.Join("; ", r.Errors)}");
    }

    private static async Task<byte[]> BytesAsync(MarkedDownload d)
    {
        using var ms = new MemoryStream();
        await d.WriteToAsync(ms, CancellationToken.None);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- upload new version

    [Fact]
    public async Task Upload_new_version_checks_out_uploads_and_checks_in_in_one_step_and_leaves_no_check_out()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env);
        var v2 = Bracket(height: 32, wall: 5);
        var detail = await UploadOk(env, number, v2, "Raised the upright by 2 mm", expected: 1);

        Assert.Equal("A.2", detail.Iteration.Label);
        Assert.Equal("Raised the upright by 2 mm", detail.History.Single(h => h.Label == "A.2").Comment);
        Assert.Null(detail.Checkout);
        Assert.Equal(0, await env.CheckoutsAsync());
        var primary = Assert.Single(detail.Iteration.Files);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(v2)), primary.Sha256);
        Assert.Equal("primary", primary.Role);
        // the new iteration was measured when it was created, before anyone looked at the history
        Assert.Equal(1, await env.CountAsync("SELECT count(*) FROM mdl.iteration_metrics"));

        var audit = (await env.AuditAsync()).Single(e => e.Action == DocumentService.VersionUploadAction);
        Assert.Equal(AuditOutcome.Success, audit.Outcome);
        Assert.Contains(primary.Sha256, audit.Payload);
        Assert.Contains("Raised the upright by 2 mm", audit.Payload);
        Assert.Contains("A.1", audit.Payload);
    }

    [Fact]
    public async Task A_change_note_is_required_and_an_invalid_request_creates_nothing()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env);
        foreach (var note in new[] { null, "", "   ", new string('x', DocumentService.MaxChangeNoteLength + 1) })
        {
            var r = await env.Docs.UploadNewVersionAsync(Alice, number, Stl(Bracket(32)), note, false, null, null);
            Assert.Equal(OutcomeKind.Invalid, r.Kind);
        }
        var bad = await env.Docs.UploadNewVersionAsync(Alice, number, Stl(Bracket(32), "../evil.stl"), "note", false, null, null);
        Assert.Equal(OutcomeKind.Invalid, bad.Kind);
        Assert.Single((await HistoryAsync(env, number)).Versions);
        Assert.Equal(0, await env.CheckoutsAsync());
    }

    [Fact]
    public async Task Uploading_the_same_content_as_the_current_version_is_refused_with_a_clear_message()
    {
        var env = await SetupAsync();
        var first = Bracket();
        var number = await CreateAsync(env, first);
        var r = await env.Docs.UploadNewVersionAsync(Alice, number, Stl(first), "Same again", false, null, null);
        Assert.Equal(OutcomeKind.Conflict, r.Kind);
        Assert.Contains("identical to the current version A.1", r.Errors[0]);
        Assert.Contains("no new version was created", r.Errors[0]);
        Assert.Single((await HistoryAsync(env, number)).Versions);
        Assert.Equal(0, await env.CheckoutsAsync());
        // an older content is a new version again (it differs from the CURRENT one)
        await UploadOk(env, number, Bracket(32));
        await UploadOk(env, number, first, "Back to the first shape");
        Assert.Equal(3, (await HistoryAsync(env, number)).Versions.Count);
    }

    [Fact]
    public async Task A_failure_inside_the_one_step_rolls_everything_back_so_no_check_out_is_left()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env);
        // give the document an attachment, then upload a model whose name collides with it
        Assert.True((await env.Docs.CheckOutAsync(Alice, number, null)).IsOk);
        Assert.True((await env.Docs.UploadFileAsync(Alice, number, "attachment", new FileUpload("notes.txt", "text/plain", new MemoryStream("n"u8.ToArray())), null)).IsOk);
        Assert.True((await env.Docs.CheckInAsync(Alice, number, "notes", null)).IsOk);
        var before = (await HistoryAsync(env, number)).Versions.Count;
        var r = await env.Docs.UploadNewVersionAsync(Alice, number, Stl(Bracket(33), "NOTES.txt"), "collides", false, null, null);
        Assert.Equal(OutcomeKind.Conflict, r.Kind);
        Assert.Equal(0, await env.CheckoutsAsync());
        Assert.Equal(before, (await HistoryAsync(env, number)).Versions.Count);
        Assert.Equal(0, await env.CountAsync("SELECT count(*) FROM plm.document_checkout_file"));
    }

    [Fact]
    public async Task A_document_checked_out_by_someone_else_or_by_me_refuses_the_quick_upload_and_keeps_its_check_out()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env);
        Assert.True((await env.Docs.CheckOutAsync(Alice, number, null)).IsOk);
        var bob = DocumentServiceTests.Bob;
        var other = await env.Docs.UploadNewVersionAsync(bob, number, Stl(Bracket(35)), "mine", false, null, null);
        Assert.Equal(OutcomeKind.Conflict, other.Kind);
        Assert.Contains("checked out", other.Errors[0]);
        var self = await env.Docs.UploadNewVersionAsync(Alice, number, Stl(Bracket(36)), "mine", false, null, null);
        Assert.Equal(OutcomeKind.Conflict, self.Kind);
        Assert.Equal(1, await env.CheckoutsAsync()); // Alice's own check-out is untouched
        var h = await HistoryAsync(env, number);
        Assert.True(h.CheckedOut && h.CheckedOutByMe);
        Assert.False((await HistoryAsync(env, number, bob)).CheckedOutByMe);
    }

    [Fact]
    public async Task A_released_revision_refuses_the_upload_until_it_is_revised_in_the_same_request()
    {
        var env = await SetupAsync();
        var first = Bracket();
        var number = await CreateAsync(env, first);
        await UploadOk(env, number, Bracket(31));
        await ReleaseAsync(env, number);

        var refused = await env.Docs.UploadNewVersionAsync(Alice, number, Stl(Bracket(40)), "after release", false, 2, null);
        Assert.Equal(OutcomeKind.Conflict, refused.Kind);
        Assert.Contains("Released and locked", refused.Errors[0]);
        Assert.Contains("Revise it first", refused.Errors[0]);
        Assert.Equal(0, await env.CheckoutsAsync());

        var detail = await UploadOk(env, number, Bracket(40), "after release", revise: true, expected: 2);
        Assert.Equal("B.2", detail.Iteration.Label); // B.1 is the revision's first iteration (the released files), B.2 the new version
        Assert.Equal("InWork", detail.Revision.State);
        var h = await HistoryAsync(env, number);
        Assert.Equal(["A.1", "A.2", "B.1", "B.2"], h.Versions.Select(v => v.Label));
        Assert.Equal("Released", h.Versions.Single(v => v.Label == "A.2").State);
        Assert.True(h.Versions.Single(v => v.Label == "B.2").IsCurrent);
        Assert.Equal(h.Versions.Single(v => v.Label == "A.2").Sha256, h.Versions.Single(v => v.Label == "B.1").Sha256);
        Assert.Contains("Revised from A.2", h.Versions.Single(v => v.Label == "B.1").ChangeNote!);
        Assert.Contains((await env.AuditAsync()), e => e.Action == DocumentService.VersionUploadAction && e.Payload.Contains("revised", StringComparison.Ordinal) && e.Payload.Contains("True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_revision_under_review_cannot_be_revised_or_take_a_version()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env);
        Assert.True((await env.Docs.SetStateAsync(Ada, number, "UnderReview", "x", adminOverride: true, null)).IsOk);
        var r = await env.Docs.UploadNewVersionAsync(Alice, number, Stl(Bracket(40)), "x", true, null, null);
        Assert.Equal(OutcomeKind.Conflict, r.Kind);
        Assert.Contains("Under Review", r.Errors[0]);
        var revise = await env.Docs.ReviseAsync(Alice, number, "because", null);
        Assert.Equal(OutcomeKind.Conflict, revise.Kind);
    }

    [Fact]
    public async Task Concurrent_uploads_that_saw_the_same_version_let_exactly_one_win()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            env.Docs.UploadNewVersionAsync(Alice, number, Stl(Bracket(30 + i + 1)), $"upload {i}", false, 1, $"corr-{i}")));
        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.All(results.Where(r => !r.IsOk), r => { Assert.Equal(OutcomeKind.Conflict, r.Kind); Assert.Contains("changed", r.Errors[0]); });
        Assert.Equal(["A.1", "A.2"], (await HistoryAsync(env, number)).Versions.Select(v => v.Label));
        Assert.Equal(0, await env.CheckoutsAsync());
    }

    [Fact]
    public async Task Concurrent_uploads_without_an_expected_version_stack_in_order_and_never_lose_one()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => env.Docs.UploadNewVersionAsync(Alice, number, Stl(Bracket(31 + i)), $"u{i}", false, null, $"c{i}")));
        Assert.All(results, r => Assert.True(r.IsOk, string.Join(";", r.Errors)));
        var h = await HistoryAsync(env, number);
        Assert.Equal(7, h.Versions.Count);
        Assert.Equal(7, h.Versions.Select(v => v.Sha256).Distinct().Count());
        Assert.Equal(0, await env.CheckoutsAsync());
    }

    [Fact]
    public async Task A_reader_who_may_not_modify_gets_a_denial_and_a_stranger_gets_nothing()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env, markings: DocumentServiceTests.Open);
        var viewer = await env.Docs.UploadNewVersionAsync(Viewer, number, Stl(Bracket(31)), "x", false, null, null);
        Assert.Equal(OutcomeKind.Forbidden, viewer.Kind);
        var itar = await CreateAsync(env, name: "Secret model");
        var stranger = await env.Docs.UploadNewVersionAsync(Lena, itar, Stl(Bracket(31)), "x", false, null, null);
        Assert.Equal(OutcomeKind.NotFound, stranger.Kind);
        Assert.Equal(0, await env.CheckoutsAsync());
        Assert.Equal(1, await env.CountAsync("SELECT count(*) FROM plm.document_iteration i JOIN plm.document_revision r ON r.id = i.revision_id JOIN plm.document_master m ON m.id = r.master_id WHERE m.number = '" + itar + "'"));
        Assert.Contains(await env.AuditAsync(), e => e.Action == DocumentService.VersionUploadAction && e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task An_oversized_upload_is_refused_before_a_new_version_exists()
    {
        var env = await SetupAsync();
        var small = await VaultContainer.OptionsAsync();
        small.MaxUploadBytes = 1000;
        var docs = new DocumentService(env.Ds, new PostgresAuditLog(env.Ds, TimeProvider.System), TimeProvider.System, new ObjectVault(small));
        var number = await CreateAsync(env);
        var r = await docs.UploadNewVersionAsync(Alice, number, Stl(Bracket(31)), "too big", false, null, null);
        Assert.Equal(OutcomeKind.Invalid, r.Kind);
        Assert.Equal(0, await env.CheckoutsAsync());
        Assert.Single((await HistoryAsync(env, number)).Versions);
    }

    // ---------------------------------------------------------------- history and measured geometry

    [Fact]
    public async Task The_history_lists_every_version_with_who_when_size_checksum_note_state_and_measured_geometry()
    {
        var env = await SetupAsync();
        var v1 = Bracket(30, 4);
        var number = await CreateAsync(env, v1);
        await UploadOk(env, number, Bracket(32, 5), "Taller and thicker");
        var h = await HistoryAsync(env, number);

        Assert.Equal(["A.1", "A.2"], h.Versions.Select(v => v.Label));
        Assert.Equal("A.2", h.CurrentLabel);
        Assert.True(h.CanModify && h.CanDownload);
        var a1 = h.Versions[0];
        var a2 = h.Versions[1];
        Assert.Equal("alice", a1.CreatedBy);
        Assert.Equal("Created", a1.ChangeNote);
        Assert.Equal((long)v1.Length, a1.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(v1)), a1.Sha256);
        Assert.Equal("In Work", a1.StateLabel);
        Assert.False(a1.IsCurrent); Assert.True(a2.IsCurrent);

        var m1 = a1.Metrics!;
        Assert.Equal("analysed", m1.Status);
        Assert.Equal(true, m1.Watertight);
        Assert.InRange(m1.Volume!.Value, (40 * 4 + 4 * 26) * 20.0 - 1e-3, (40 * 4 + 4 * 26) * 20.0 + 1e-3);
        Assert.InRange(m1.SizeZ!.Value, 29.999, 30.001);
        Assert.InRange(a2.Metrics!.SizeZ!.Value, 31.999, 32.001);
        Assert.NotEqual(m1.GeometryFingerprint, a2.Metrics.GeometryFingerprint);
        Assert.False(a2.SameGeometryAsPrevious);
        // measurements are stored once per iteration, immutably
        Assert.Equal(2, await env.CountAsync("SELECT count(*) FROM mdl.iteration_metrics"));
        await HistoryAsync(env, number);
        Assert.Equal(2, await env.CountAsync("SELECT count(*) FROM mdl.iteration_metrics"));
        await Assert.ThrowsAnyAsync<PostgresException>(async () => { await using var c = await env.Ds.OpenConnectionAsync(); await new NpgsqlCommand("UPDATE mdl.iteration_metrics SET triangles = 1", c).ExecuteNonQueryAsync(); });
        await Assert.ThrowsAnyAsync<PostgresException>(() => env.ExecAsync("UPDATE mdl.iteration_metrics SET triangles = 1"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => env.ExecAsync("DELETE FROM mdl.iteration_metrics"));
    }

    [Fact]
    public async Task Versions_that_were_checked_in_the_normal_way_are_measured_on_first_view_and_a_re_export_is_recognised_as_the_same_geometry()
    {
        var env = await SetupAsync();
        var shape = SyntheticMesh.Bracket(40, 30, 4, 20);
        var number = await CreateAsync(env, SyntheticMesh.ToBinaryStl(shape));
        // the normal path: check out, replace the primary, check in
        Assert.True((await env.Docs.CheckOutAsync(Alice, number, null)).IsOk);
        Assert.True((await env.Docs.UploadFileAsync(Alice, number, "primary", Stl(SyntheticMesh.ToAsciiStl(shape, "0.0000000")), null)).IsOk);
        Assert.True((await env.Docs.CheckInAsync(Alice, number, "Re-exported as ASCII", null)).IsOk);
        Assert.Equal(0, await env.CountAsync("SELECT count(*) FROM mdl.iteration_metrics"));

        var h = await HistoryAsync(env, number);
        Assert.Equal(2, await env.CountAsync("SELECT count(*) FROM mdl.iteration_metrics"));
        Assert.NotEqual(h.Versions[0].Sha256, h.Versions[1].Sha256);
        Assert.Equal(h.Versions[0].Metrics!.GeometryFingerprint, h.Versions[1].Metrics!.GeometryFingerprint);
        Assert.True(h.Versions[1].SameGeometryAsPrevious);

        var compare = (await env.Docs.CompareModelVersionsAsync(Alice, number, h.Versions[0].IterationId, h.Versions[1].IterationId, "corr-cmp")).Value!;
        Assert.True(compare.Delta.SameGeometry);
        Assert.StartsWith("Same geometry", compare.Delta.Summary);
        Assert.False(compare.SameBytes);
    }

    [Fact]
    public async Task Step_files_get_file_facts_and_a_product_count_but_no_invented_geometry()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env, ViewerSamples.StepBox(), fileName: "box.step");
        var h = await HistoryAsync(env, number);
        var m = h.Versions.Single().Metrics!;
        Assert.Equal("facts_only", m.Status);
        Assert.Null(m.Triangles); Assert.Null(m.Volume); Assert.Null(m.SurfaceArea); Assert.Null(m.SizeX); Assert.Null(m.GeometryFingerprint); Assert.Null(m.Watertight);
        Assert.NotNull(m.StepProducts);
        Assert.True(m.StepProducts >= 0);
        Assert.Equal(ViewerSamples.StepBox().Length, h.Versions.Single().SizeBytes);
        // stored with the same shape rule: facts only means no numbers in the row
        Assert.Equal(1, await env.CountAsync("SELECT count(*) FROM mdl.iteration_metrics WHERE status = 'facts_only' AND triangles IS NULL AND signed_volume IS NULL AND geometry_fp IS NULL"));
    }

    [Fact]
    public async Task A_model_file_that_cannot_be_read_says_why_and_a_non_model_file_is_not_measured()
    {
        var env = await SetupAsync();
        var broken = await CreateAsync(env, Encoding.ASCII.GetBytes("solid broken\nvertex 1 2\nendsolid broken\n"), name: "Broken model");
        var m = (await HistoryAsync(env, broken)).Versions.Single().Metrics!;
        Assert.Equal("failed", m.Status);
        Assert.False(string.IsNullOrWhiteSpace(m.Reason));
        Assert.Null(m.Triangles);

        var text = await env.Docs.CreateAsync(Alice, new CreateDocumentCommand("Plain note", "Report", DocumentServiceTests.Open),
            new FileUpload("note.txt", "text/plain", new MemoryStream("hello"u8.ToArray())), null);
        var row = (await HistoryAsync(env, text.Value!.Number)).Versions.Single();
        Assert.Null(row.Metrics);
        Assert.Equal(1, await env.CountAsync("SELECT count(*) FROM mdl.iteration_metrics"));
    }

    [Fact]
    public async Task A_long_history_measures_a_bounded_number_of_versions_per_view_and_finishes_on_the_next()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env);
        // 12 versions checked in the normal way, so none is measured yet
        for (var i = 0; i < 11; i++)
        {
            Assert.True((await env.Docs.CheckOutAsync(Alice, number, null)).IsOk);
            Assert.True((await env.Docs.UploadFileAsync(Alice, number, "primary", Stl(Bracket(31 + i)), null)).IsOk);
            Assert.True((await env.Docs.CheckInAsync(Alice, number, $"v{i}", null)).IsOk);
        }
        var first = await HistoryAsync(env, number);
        Assert.Equal(12, first.Versions.Count);
        Assert.Equal(4, first.PendingMeasurements);
        Assert.Equal(4, first.Versions.Count(v => v.Metrics is null));
        var second = await HistoryAsync(env, number);
        Assert.Equal(0, second.PendingMeasurements);
        Assert.All(second.Versions, v => Assert.NotNull(v.Metrics));
    }

    // ---------------------------------------------------------------- compare

    [Fact]
    public async Task Compare_gives_the_numbers_the_percentages_and_a_plain_summary_and_audits_without_geometry()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env, Bracket(30, 4));
        await UploadOk(env, number, Bracket(32, 4));
        var h = await HistoryAsync(env, number);
        var r = await env.Docs.CompareModelVersionsAsync(Alice, number, h.Versions[0].IterationId, h.Versions[1].IterationId, "corr-compare");
        Assert.True(r.IsOk);
        var c = r.Value!;
        Assert.StartsWith("Taller by 2.0 units, volume up 3.0 %", c.Delta.Summary);
        var z = c.Delta.Rows.Single(x => x.Key == "sizeZ");
        Assert.InRange(z.Change!.Value, 1.999, 2.001);
        Assert.InRange(z.Percent!.Value, 6.6, 6.7);
        Assert.True(c.VisualAvailable);
        Assert.Equal(h.Versions[0].FileId, c.A.FileId);
        Assert.False(c.Delta.SameGeometry);

        var audit = (await env.AuditAsync()).Single(e => e.Action == DocumentService.ModelCompareAction);
        Assert.Contains("A.1", audit.Payload); Assert.Contains("A.2", audit.Payload);
        Assert.DoesNotContain(c.A.Metrics.GeometryFingerprint!, audit.Payload);
        Assert.DoesNotContain("Taller", audit.Payload);
        Assert.DoesNotContain("volume", audit.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Compare_works_across_revisions_refuses_the_same_version_twice_and_unknown_versions()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env);
        await ReleaseAsync(env, number);
        await UploadOk(env, number, Bracket(34), "after release", revise: true);
        var h = await HistoryAsync(env, number);
        var a = h.Versions[0]; var b = h.Versions[^1];
        Assert.Equal(("A.1", "B.2"), (a.Label, b.Label));
        Assert.True((await env.Docs.CompareModelVersionsAsync(Alice, number, a.IterationId, b.IterationId, null)).IsOk);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.CompareModelVersionsAsync(Alice, number, a.IterationId, a.IterationId, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.CompareModelVersionsAsync(Alice, number, a.IterationId, Guid.NewGuid(), null)).Kind);
        var identical = await env.Docs.CompareModelVersionsAsync(Alice, number, h.Versions[0].IterationId, h.Versions[1].IterationId, null);
        Assert.Equal("The two versions are the same file.", identical.Value!.Delta.Summary);
        Assert.False(identical.Value.VisualAvailable);
    }

    [Fact]
    public async Task Compare_of_step_versions_says_no_numbers_can_be_compared_and_offers_no_visual_comparison()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env, ViewerSamples.StepBox(), fileName: "box.step");
        await UploadOk(env, number, ViewerSamples.StepBox(50, 20, 10), name: "box.step");
        var h = await HistoryAsync(env, number);
        var c = (await env.Docs.CompareModelVersionsAsync(Alice, number, h.Versions[0].IterationId, h.Versions[1].IterationId, null)).Value!;
        Assert.False(c.Delta.Comparable);
        Assert.Empty(c.Delta.Rows);
        Assert.Contains("no numbers can be compared", c.Delta.Summary);
        Assert.False(c.VisualAvailable);
    }

    // ---------------------------------------------------------------- restore

    [Fact]
    public async Task Restore_creates_a_new_iteration_equal_to_the_old_one_and_never_rewrites_history()
    {
        var env = await SetupAsync();
        var first = Bracket(30, 4);
        var number = await CreateAsync(env, first);
        await UploadOk(env, number, Bracket(32, 5), "v2");
        await UploadOk(env, number, Bracket(34, 6), "v3");
        var before = await HistoryAsync(env, number);
        var a1 = before.Versions[0];

        var r = await env.Docs.RestoreModelVersionAsync(Alice, number, a1.IterationId, 3, "corr-restore");
        Assert.True(r.IsOk, string.Join(";", r.Errors));
        Assert.Equal("A.4", r.Value!.Iteration.Label);
        Assert.Equal(a1.Sha256, r.Value.Iteration.Files.Single().Sha256);
        Assert.Equal(0, await env.CheckoutsAsync());

        var h = await HistoryAsync(env, number);
        Assert.Equal(["A.1", "A.2", "A.3", "A.4"], h.Versions.Select(v => v.Label));
        Assert.Equal("Restored from A.1", h.Versions[3].ChangeNote);
        // the old rows are exactly as they were
        for (var i = 0; i < 3; i++) Assert.Equal((before.Versions[i].Sha256, before.Versions[i].ChangeNote, before.Versions[i].CreatedAt), (h.Versions[i].Sha256, h.Versions[i].ChangeNote, h.Versions[i].CreatedAt));
        // same bytes, same measurement
        Assert.Equal(a1.Metrics!.GeometryFingerprint, h.Versions[3].Metrics!.GeometryFingerprint);
        Assert.True((await env.Docs.CompareModelVersionsAsync(Alice, number, a1.IterationId, h.Versions[3].IterationId, null)).Value!.SameBytes);

        var audit = (await env.AuditAsync()).Single(e => e.Action == DocumentService.VersionRestoreAction);
        Assert.Equal(AuditOutcome.Success, audit.Outcome);
        Assert.Contains("restoredFrom", audit.Payload, StringComparison.Ordinal);
        Assert.Contains("A.1", audit.Payload, StringComparison.Ordinal);
        Assert.Contains("corr-restore", audit.CorrelationId);
    }

    [Fact]
    public async Task Restore_refuses_the_current_content_a_stale_view_a_locked_revision_a_checked_out_document_and_a_stranger()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env, markings: DocumentServiceTests.Open);
        await UploadOk(env, number, Bracket(32));
        var h = await HistoryAsync(env, number);
        var current = await env.Docs.RestoreModelVersionAsync(Alice, number, h.Versions[1].IterationId, null, null);
        Assert.Equal(OutcomeKind.Conflict, current.Kind);
        Assert.Contains("nothing to restore", current.Errors[0]);
        var stale = await env.Docs.RestoreModelVersionAsync(Alice, number, h.Versions[0].IterationId, 1, null);
        Assert.Equal(OutcomeKind.Conflict, stale.Kind);
        Assert.Contains("changed", stale.Errors[0]);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Docs.RestoreModelVersionAsync(Viewer, number, h.Versions[0].IterationId, null, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.RestoreModelVersionAsync(Alice, number, Guid.NewGuid(), null, null)).Kind);

        Assert.True((await env.Docs.CheckOutAsync(Alice, number, null)).IsOk);
        Assert.Equal(OutcomeKind.Conflict, (await env.Docs.RestoreModelVersionAsync(Alice, number, h.Versions[0].IterationId, null, null)).Kind);
        Assert.True((await env.Docs.UndoCheckOutAsync(Alice, number, null)).IsOk);

        await ReleaseAsync(env, number);
        var locked = await env.Docs.RestoreModelVersionAsync(Alice, number, h.Versions[0].IterationId, null, null);
        Assert.Equal(OutcomeKind.Conflict, locked.Kind);
        Assert.Contains("locked", locked.Errors[0]);
        Assert.Equal(["A.1", "A.2"], (await HistoryAsync(env, number)).Versions.Select(v => v.Label));
        Assert.Equal(0, await env.CheckoutsAsync());
    }

    [Fact]
    public async Task Restore_is_safe_under_concurrency()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env);
        await UploadOk(env, number, Bracket(32));
        await UploadOk(env, number, Bracket(34));
        var h = await HistoryAsync(env, number);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => env.Docs.RestoreModelVersionAsync(Alice, number, h.Versions[i % 2].IterationId, 3, $"c{i}")));
        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.Equal(4, (await HistoryAsync(env, number)).Versions.Count);
        Assert.Equal(0, await env.CheckoutsAsync());
    }

    // ---------------------------------------------------------------- revise

    [Fact]
    public async Task Revise_makes_the_next_revision_from_a_released_one_and_leaves_the_released_one_untouched()
    {
        var env = await SetupAsync();
        var v1 = Bracket();
        var number = await CreateAsync(env, v1);
        Assert.Equal(OutcomeKind.Conflict, (await env.Docs.ReviseAsync(Alice, number, "not yet released", null)).Kind);
        await ReleaseAsync(env, number);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.ReviseAsync(Alice, number, "  ", null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Docs.ReviseAsync(Viewer, number, "reason", null)).Kind);

        var r = await env.Docs.ReviseAsync(Alice, number, "Customer asked for a taller bracket", "corr-revise");
        Assert.True(r.IsOk, string.Join(";", r.Errors));
        Assert.Equal(("B", "InWork", "B.1"), (r.Value!.Revision.Revision, r.Value.Revision.State, r.Value.Iteration.Label));
        Assert.Equal(["A", "B"], r.Value.Revisions.Select(x => x.Revision));
        Assert.Equal("Released", r.Value.Revisions.Single(x => x.Revision == "A").State);
        Assert.False(r.Value.Revisions.Single(x => x.Revision == "A").IsLatest);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(v1)), r.Value.Iteration.Files.Single().Sha256);
        Assert.Equal(OutcomeKind.Conflict, (await env.Docs.ReviseAsync(Alice, number, "again", null)).Kind); // B is In Work, not released
        var audit = (await env.AuditAsync()).Single(e => e.Action == DocumentService.ReviseAction && e.Outcome == AuditOutcome.Success);
        Assert.Contains("Customer asked for a taller bracket", audit.Payload);
    }

    // ---------------------------------------------------------------- download of selected versions

    [Fact]
    public async Task The_versions_ZIP_holds_each_selected_version_under_its_label_with_the_markings_and_a_manifest()
    {
        var env = await SetupAsync();
        var v1 = Bracket(30);
        var number = await CreateAsync(env, v1);
        var v2 = Bracket(32);
        await UploadOk(env, number, v2, "Taller");
        var h = await HistoryAsync(env, number);
        var r = await env.Docs.DownloadVersionsAsync(Alice, number, [h.Versions[1].IterationId, h.Versions[0].IterationId], "corr-zip");
        Assert.True(r.IsOk, string.Join(";", r.Errors));
        Assert.Equal($"{number}-versions.zip", r.Value!.FileName);
        Assert.Contains("ITAR", r.Value.Banner);
        using var zip = new ZipArchive(new MemoryStream(await BytesAsync(r.Value)), ZipArchiveMode.Read);
        Assert.Equal(["MARKINGS.pdf", "MARKINGS.txt", "VERSIONS.txt", "A.1/bracket.stl", "A.2/bracket.stl"], zip.Entries.Select(e => e.FullName));
        async Task<byte[]> Read(string name) { await using var s = zip.GetEntry(name)!.Open(); using var ms = new MemoryStream(); await s.CopyToAsync(ms); return ms.ToArray(); }
        Assert.Equal(v1, await Read("A.1/bracket.stl"));
        Assert.Equal(v2, await Read("A.2/bracket.stl"));
        var manifest = Encoding.UTF8.GetString(await Read("VERSIONS.txt"));
        Assert.Contains(h.Versions[0].Sha256!, manifest);
        Assert.Contains("Change note: Taller", manifest);
        Assert.StartsWith("CUI//EXPT", manifest);

        var audit = (await env.AuditAsync()).Single(e => e.Action == DocumentService.VersionsDownloadAction);
        Assert.Contains("A.1,A.2", audit.Payload);
    }

    [Fact]
    public async Task The_versions_ZIP_refuses_nothing_chosen_too_many_unknown_and_unreadable()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.DownloadVersionsAsync(Alice, number, [], null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.DownloadVersionsAsync(Alice, number, [.. Enumerable.Range(0, DocumentService.MaxVersionsInZip + 1).Select(_ => Guid.NewGuid())], null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.DownloadVersionsAsync(Alice, number, [Guid.NewGuid()], null)).Kind);
        var h = await HistoryAsync(env, number);
        Assert.Equal(OutcomeKind.NotFound, (await env.Docs.DownloadVersionsAsync(Lena, number, [h.Versions[0].IterationId], null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Docs.DownloadVersionsAsync(Eve, number, [h.Versions[0].IterationId], null)).Kind);
    }

    // ---------------------------------------------------------------- access, quarantine, no leaks

    [Fact]
    public async Task For_every_probe_user_history_compare_restore_and_zip_equal_what_the_policy_allows_and_nothing_leaks()
    {
        var env = await SetupAsync();
        var openDoc = await CreateAsync(env, name: "Open bracket model", markings: DocumentServiceTests.Open);
        var itarDoc = await CreateAsync(env, Bracket(41), name: "Secret bracket model oracle-secret", markings: DocumentServiceTests.Itar());
        var otherProgram = await CreateAsync(env, Bracket(51), name: "Kraken bracket model", markings: DocumentServiceTests.Itar("KRAKEN"), by: DocumentServiceTests.User("kay", programs: ["KRAKEN"]));
        var numbers = new[] { openDoc, itarDoc, otherProgram };
        var markings = new Dictionary<string, Markings>
        {
            [openDoc] = DocumentServiceTests.Open, [itarDoc] = DocumentServiceTests.Itar(), [otherProgram] = DocumentServiceTests.Itar("KRAKEN"),
        };
        foreach (var n in numbers) await UploadOk(env, n, Bracket(n == openDoc ? 32 : n == itarDoc ? 43 : 53), "v2", by: n == otherProgram ? DocumentServiceTests.User("kay", programs: ["KRAKEN"]) : Alice);
        // one version of the open document is quarantined: it must vanish from lists and counts
        var openHistory = await HistoryAsync(env, openDoc);
        var quarantinedSha = openHistory.Versions[0].Sha256!;
        await env.ExecAsync($"INSERT INTO plm.file_quarantine (master_id, sha256, signature, quarantined_at, quarantined_by) SELECT id, '{quarantinedSha}', 'Test.Signature', now(), 'scanner' FROM plm.document_master WHERE number = '{openDoc}'");
        var kay = DocumentServiceTests.User("kay", programs: ["KRAKEN"]);
        var secretsOf = new Dictionary<string, List<string>>();
        foreach (var n in new[] { itarDoc, otherProgram })
        {
            var h = await HistoryAsync(env, n, n == otherProgram ? kay : Alice);
            secretsOf[n] = [n, h.Name, .. h.Versions.Select(v => v.Sha256!), .. h.Versions.Select(v => v.Metrics!.GeometryFingerprint!)];
        }

        foreach (var subject in new[] { Alice, Ada, Lena, Eve, Viewer, Mallory, DocumentServiceTests.Bob })
        {
            var json = new StringBuilder();
            foreach (var n in numbers)
            {
                var canRead = AccessPolicy.Evaluate(subject, markings[n], PlmAction.Read).Permitted;
                var canDownload = AccessPolicy.Evaluate(subject, markings[n], PlmAction.Download).Permitted;
                var history = await env.Docs.GetModelVersionsAsync(subject, n, null);
                json.Append(JsonSerializer.Serialize(history));
                if (!canRead)
                {
                    Assert.Equal(OutcomeKind.NotFound, history.Kind);
                    foreach (var outcome in new[]
                    {
                        JsonSerializer.Serialize(await env.Docs.CompareModelVersionsAsync(subject, n, Guid.NewGuid(), Guid.NewGuid(), null)),
                        JsonSerializer.Serialize(await env.Docs.RestoreModelVersionAsync(subject, n, Guid.NewGuid(), null, null)),
                        JsonSerializer.Serialize(await env.Docs.DownloadVersionsAsync(subject, n, [Guid.NewGuid()], null)),
                        JsonSerializer.Serialize(await env.Docs.UploadNewVersionAsync(subject, n, Stl(Bracket(40)), "x", false, null, null)),
                        JsonSerializer.Serialize(await env.Docs.ReviseAsync(subject, n, "x", null)),
                    })
                    {
                        Assert.Contains("\"Kind\":2", outcome, StringComparison.Ordinal); // NotFound
                        json.Append(outcome);
                    }
                    continue;
                }
                Assert.True(history.IsOk, $"{subject.Username} reads {n}");
                var expectedVersions = n == openDoc ? 1 : 2; // the quarantined first version of the open document is not counted
                Assert.Equal(expectedVersions, history.Value!.Versions.Count);
                Assert.DoesNotContain(history.Value.Versions, v => v.Sha256 == quarantinedSha);
                Assert.Equal(canDownload, history.Value.CanDownload);
                var ids = history.Value.Versions.Select(v => v.IterationId).ToList();
                if (ids.Count == 2)
                {
                    var compare = await env.Docs.CompareModelVersionsAsync(subject, n, ids[0], ids[1], null);
                    Assert.True(compare.IsOk);
                    Assert.Equal(canDownload, compare.Value!.VisualAvailable);
                    json.Append(JsonSerializer.Serialize(compare.Value));
                }
                var zip = await env.Docs.DownloadVersionsAsync(subject, n, ids, null);
                Assert.Equal(canDownload, zip.IsOk);
                if (!zip.IsOk) json.Append(JsonSerializer.Serialize(zip));
            }
            var all = json.ToString();
            // nothing about a document the subject cannot read, and nothing about the quarantined content, appears in anything the subject was given
            foreach (var (n, secrets) in secretsOf)
                if (!AccessPolicy.Evaluate(subject, markings[n], PlmAction.Read).Permitted)
                    foreach (var secret in secrets) Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
            Assert.DoesNotContain(quarantinedSha, all, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_quarantined_version_is_not_listed_counted_compared_restored_or_downloaded()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env);
        await UploadOk(env, number, Bracket(32));
        await UploadOk(env, number, Bracket(34));
        var h = await HistoryAsync(env, number);
        var bad = h.Versions[1];
        await env.ExecAsync($"INSERT INTO plm.file_quarantine (master_id, sha256, signature, quarantined_at, quarantined_by) SELECT id, '{bad.Sha256}', 'Test.Signature', now(), 'scanner' FROM plm.document_master WHERE number = '{number}'");

        var after = await HistoryAsync(env, number);
        Assert.Equal(["A.1", "A.3"], after.Versions.Select(v => v.Label));
        Assert.DoesNotContain(after.Versions, v => v.IterationId == bad.IterationId);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.CompareModelVersionsAsync(Alice, number, after.Versions[0].IterationId, bad.IterationId, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.RestoreModelVersionAsync(Alice, number, bad.IterationId, null, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.DownloadVersionsAsync(Alice, number, [bad.IterationId], null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Docs.DownloadVersionsAsync(Alice, number, [after.Versions[0].IterationId, bad.IterationId], null)).Kind);
    }

    [Fact]
    public async Task Audit_events_of_model_versions_carry_no_geometry_and_no_file_content()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env);
        await UploadOk(env, number, Bracket(32), "Raised the upright");
        var h = await HistoryAsync(env, number);
        await env.Docs.CompareModelVersionsAsync(Alice, number, h.Versions[0].IterationId, h.Versions[1].IterationId, null);
        await env.Docs.RestoreModelVersionAsync(Alice, number, h.Versions[0].IterationId, 2, null);
        var payload = string.Join('\n', (await env.AuditAsync()).Where(e => e.Action.StartsWith("document.version_", StringComparison.Ordinal) || e.Action.StartsWith("document.model_", StringComparison.Ordinal)).Select(e => e.Payload));
        Assert.NotEmpty(payload);
        foreach (var m in h.Versions.Select(v => v.Metrics!))
        {
            Assert.DoesNotContain(m.GeometryFingerprint!, payload, StringComparison.Ordinal);
            Assert.DoesNotContain("signedVolume", payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("triangles", payload, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Where_used_is_the_existing_document_where_used_and_is_available_for_the_same_document()
    {
        var env = await SetupAsync();
        var number = await CreateAsync(env, markings: DocumentServiceTests.Open);
        var parts = await env.Docs.GetDocumentPartsAsync(Alice, number, null);
        Assert.True(parts.IsOk);
        Assert.Empty(parts.Value!.Parts);
    }
}
