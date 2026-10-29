using Amazon.S3.Model;
using Keel.Audit;
using Keel.Core;
using Keel.Documents.Vault;
using Keel.Records;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>WORM-style archive packages: what they hold, what verifies, what the application and the store each guarantee (ADR-0041).</summary>
[Collection(PostgresCollection.Name)]
public sealed class RecordsArchiveTests(PostgresFixture pg)
{
    private static CreateArchiveCommand Cmd(params (string Kind, string Number)[] items) => new("Archive " + Guid.NewGuid().ToString("N")[..6], [.. items.Select(i => new ObjectRef(i.Kind, i.Number))]);

    [Fact]
    public async Task An_archive_holds_released_versions_files_signatures_and_an_audit_extract_and_verifies()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        var content = RecordsEnv.Unique("archived file");
        var doc = await env.NewDocAsync("Archived spec", content: content);
        await env.ReviewAsync("document", doc, submitter: RecordsEnv.Alice); // released by signatures: three of them are archived with it
        var part = await env.NewPartAsync("Archived bracket");
        await env.ReleasePartAsync(part);

        var created = await env.Archives.CreateAsync(RecordsEnv.Rex, Cmd(("document", doc), ("part", part)), "corr-archive");
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        var a = created.Value!;
        Assert.Matches("^AR-\\d{6}$", a.Number);
        Assert.Equal("application", a.WormMode);
        Assert.Equal(RecordsEnv.Sha(content), (await env.Docs.GetAsync(RecordsEnv.Ada, doc, null, null)).Value!.Iteration.Files[0].Sha256);

        var contents = (await env.Archives.OpenAsync(RecordsEnv.Rex, a.Number, null)).Value!;
        Assert.True(contents.Verification.Valid, string.Join("; ", contents.Verification.Problems));
        Assert.True(contents.Verification.ManifestSigned);
        Assert.Equal(new[] { doc, part }.Order(StringComparer.Ordinal), contents.Objects.Select(o => o.Number).Order(StringComparer.Ordinal));
        var archivedDoc = contents.Objects.Single(o => o.Number == doc);
        Assert.Equal(["Released"], archivedDoc.Revisions.Select(r => r.State));
        Assert.Equal("spec.txt", archivedDoc.Revisions[0].Iterations[0].Files.Single().Name);
        Assert.Equal(RecordsEnv.Sha(content), archivedDoc.Revisions[0].Iterations[0].Files.Single().Sha256);
        Assert.True(contents.Signatures.Count(s => s.ObjectKey == doc) >= 3);
        Assert.Contains(contents.Events, e => e.Action == "document.create");
        Assert.True(contents.Verification.Events >= 3);

        // The package is addressed by its own hash in the vault.
        Assert.True(await env.Store.ExistsAsync(a.PackageSha256, a.PackageSize));
        Assert.Equal(a.PackageSha256, RecordsEnv.Sha(await env.Vault.ReadAllAsync(a.PackageSha256, a.PackageSize)));
        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.Action == "records.archive_create" && e.ObjectId == a.Number);
        Assert.Contains(audit, e => e.Action == "records.archive_open" && e.Outcome == AuditOutcome.Success);
        Assert.True((await env.Archives.VerifyAsync(RecordsEnv.Rex, a.Number, null)).Value!.Valid);
    }

    [Fact]
    public async Task An_archive_is_immutable_in_the_database_and_its_package_is_never_garbage()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        var doc = await env.ReleasedDocAsync("Immutable archive");
        var a = (await env.Archives.CreateAsync(RecordsEnv.Rex, Cmd(("document", doc)), null)).Value!;
        Assert.NotNull(await env.AppSqlAsync("UPDATE rec.archive SET title = 'x'"));
        Assert.NotNull(await env.AppSqlAsync("DELETE FROM rec.archive"));
        Assert.NotNull(await env.AppSqlAsync("DELETE FROM rec.archive_object"));
        // Even the database owner meets the triggers (they would have to disable them first, which is visible to anyone who looks).
        var owner = await Assert.ThrowsAsync<PostgresException>(() => env.Db.ExecuteAsSuperuserAsync("UPDATE rec.archive SET title = 'x'"));
        Assert.Contains("immutable", owner.MessageText, StringComparison.Ordinal);
        Assert.Contains("immutable", (await Assert.ThrowsAsync<PostgresException>(() => env.Db.ExecuteAsSuperuserAsync("DELETE FROM rec.archive_object"))).MessageText, StringComparison.Ordinal);

        // The vault sweeper counts the package as referenced however old it is.
        var sweep = await env.SweepAsync(daysAhead: 400);
        Assert.True(await env.Store.ExistsAsync(a.PackageSha256, a.PackageSize), $"deleted {sweep.Deleted} objects, but the package must stay");
    }

    [Fact]
    public async Task A_package_changed_in_the_store_is_detected()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        var doc = await env.ReleasedDocAsync("To be tampered");
        var a = (await env.Archives.CreateAsync(RecordsEnv.Rex, Cmd(("document", doc)), null)).Value!;
        using var s3 = ObjectVault.CreateClient(env.VaultOptions);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = env.VaultOptions.Bucket, Key = a.PackageSha256, ContentBody = "replaced by someone with access to the store" });
        var verification = (await env.Archives.VerifyAsync(RecordsEnv.Rex, a.Number, null)).Value!;
        Assert.False(verification.Valid);
        Assert.Contains("could not be read", string.Join(" ", verification.Problems), StringComparison.Ordinal);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "records.archive_verify" && e.Outcome == AuditOutcome.Failure);
    }

    [Fact]
    public async Task An_archive_carries_the_markings_of_what_it_holds()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        var itar = await env.ReleasedDocAsync("Export controlled", RecordsEnv.Itar());
        var a = (await env.Archives.CreateAsync(RecordsEnv.Rex, Cmd(("document", itar)), null)).Value!;
        Assert.Contains("ITAR", a.Banner, StringComparison.Ordinal);
        // Eve is a records manager outside the program: she can neither create such an archive nor see it exists.
        Assert.Equal(OutcomeKind.Invalid, (await env.Archives.CreateAsync(RecordsEnv.Eve, Cmd(("document", itar)), null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Archives.GetAsync(RecordsEnv.Eve, a.Number, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Archives.OpenAsync(RecordsEnv.Eve, a.Number, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Archives.VerifyAsync(RecordsEnv.Eve, a.Number, null)).Kind);
        Assert.Empty((await env.Archives.ListAsync(RecordsEnv.Eve, null)).Value!);
        Assert.Single((await env.Archives.ListAsync(RecordsEnv.Rex, null)).Value!);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "records.archive_read" && e.Outcome == AuditOutcome.Denied);
        // Different programs cannot share an archive (an archive is one program's record).
        var kraken = await env.NewDocAsync("Kraken", RecordsEnv.Itar("KRAKEN"), by: RecordsEnv.Kay);
        var both = WorkflowEnv.User("both", [Roles.Viewer, Roles.Author, Roles.RecordsManager], programs: ["ORCA", "KRAKEN"]);
        Assert.Equal(OutcomeKind.Invalid, (await env.Archives.CreateAsync(both, Cmd(("document", itar), ("document", kraken)), null)).Kind);
    }

    [Fact]
    public async Task Only_released_work_can_be_archived_and_the_request_is_validated()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        var inWork = await env.NewDocAsync("Not released");
        var refused = await env.Archives.CreateAsync(RecordsEnv.Rex, Cmd(("document", inWork)), null);
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        Assert.Contains("no released revision", string.Join(" ", refused.Errors), StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await env.Archives.CreateAsync(RecordsEnv.Rex, new CreateArchiveCommand("", [new ObjectRef("document", inWork)]), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Archives.CreateAsync(RecordsEnv.Rex, new CreateArchiveCommand("Empty", []), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Archives.CreateAsync(RecordsEnv.Rex, Cmd(("supplier", "SUP-000001")), null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Archives.CreateAsync(RecordsEnv.Alice, Cmd(("document", inWork)), null)).Kind);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM rec.archive"));
    }

    [Theory]
    [InlineData("Off", true, false, "application", 0)]
    [InlineData("IfAvailable", true, false, "s3_governance", 1)]
    [InlineData("IfAvailable", false, false, "application", 0)]
    [InlineData("IfAvailable", true, true, "application", 0)]
    public async Task S3_object_lock_is_used_only_when_the_store_says_it_has_it(string mode, bool supported, bool applyFails, string expectedMode, int applied)
    {
        var env = await RecordsEnv.CreateAsync(pg, wormSupported: supported, options: new RecordsOptions { ObjectLock = mode });
        env.Worm.FailApply = applyFails;
        var doc = await env.ReleasedDocAsync("Lock test");
        var a = await env.Archives.CreateAsync(RecordsEnv.Rex, Cmd(("document", doc)), null);
        Assert.True(a.IsOk, string.Join("; ", a.Errors));
        Assert.Equal(expectedMode, a.Value!.WormMode);
        Assert.Equal(applied, env.Worm.Applied.Count);
        if (applied == 1)
        {
            Assert.Equal(a.Value.PackageSha256, env.Worm.Applied[0].Sha);
            Assert.Equal("governance", env.Worm.Applied[0].Mode);
            Assert.NotNull(a.Value.RetainUntil);
        }
        else Assert.Null(a.Value.RetainUntil);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task When_object_lock_is_required_and_cannot_be_had_nothing_is_archived(bool supported, bool applyFails)
    {
        var env = await RecordsEnv.CreateAsync(pg, wormSupported: supported, options: new RecordsOptions { ObjectLock = "Required" });
        env.Worm.FailApply = applyFails;
        var doc = await env.ReleasedDocAsync("Required lock");
        var a = await env.Archives.CreateAsync(RecordsEnv.Rex, Cmd(("document", doc)), null);
        Assert.Equal(OutcomeKind.Unavailable, a.Kind);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM rec.archive"));
    }

    [Fact]
    public async Task Compliance_mode_is_recorded_when_the_store_applies_it()
    {
        var env = await RecordsEnv.CreateAsync(pg, wormSupported: true, options: new RecordsOptions { ObjectLock = "Required", ObjectLockMode = "compliance", ObjectLockDays = 30 });
        var doc = await env.ReleasedDocAsync("Compliance");
        var a = (await env.Archives.CreateAsync(RecordsEnv.Rex, Cmd(("document", doc)), null)).Value!;
        Assert.Equal("s3_compliance", a.WormMode);
        Assert.InRange((a.RetainUntil!.Value - DateTimeOffset.UtcNow).TotalDays, 29, 31);
    }

    [Fact]
    public async Task The_real_store_is_asked_and_never_assumed_to_support_object_lock()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        using var worm = new VaultObjectLock(env.Vault);
        var capability = await worm.DetectAsync();
        // The test store is a plain bucket created without object lock: the answer must be "not supported", with a reason a person can read.
        Assert.False(capability.Supported);
        Assert.False(string.IsNullOrWhiteSpace(capability.Detail));
        // The whole path through the real vault works on such a store: the archive falls back to application-level immutability.
        var doc = await env.ReleasedDocAsync("Plain bucket");
        var archives = new ArchiveService(env.Ds, env.Audit, env.Clock, env.Signer, env.Trusted, new RecordsOptions { ObjectLock = "IfAvailable" }, env.Store, env.Store, worm);
        var a = await archives.CreateAsync(RecordsEnv.Rex, Cmd(("document", doc)), null);
        Assert.True(a.IsOk, string.Join("; ", a.Errors));
        Assert.Equal("application", a.Value!.WormMode);
    }

    [Fact]
    public async Task Without_a_signing_key_or_a_vault_an_archive_is_refused_not_faked()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        var doc = await env.ReleasedDocAsync("Unsigned");
        var noKey = new ArchiveService(env.Ds, env.Audit, env.Clock, new RecordsSigner(null, env.Trusted), env.Trusted, new RecordsOptions(), env.Store, env.Store);
        Assert.Equal(OutcomeKind.Unavailable, (await noKey.CreateAsync(RecordsEnv.Rex, Cmd(("document", doc)), null)).Kind);
        var noVault = new ArchiveService(env.Ds, env.Audit, env.Clock, env.Signer, env.Trusted, new RecordsOptions());
        Assert.Equal(OutcomeKind.Unavailable, (await noVault.CreateAsync(RecordsEnv.Rex, Cmd(("document", doc)), null)).Kind);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM rec.archive"));
    }
}
