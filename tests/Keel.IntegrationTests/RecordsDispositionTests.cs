using Amazon.S3.Model;
using Keel.Audit;
using Keel.Core;
using Keel.Documents.Vault;
using Keel.Records;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Disposition end to end against PostgreSQL and the real vault: approval, execution, tombstones, certificates, garbage collection, holds and archives (ADR-0041).</summary>
[Collection(PostgresCollection.Name)]
public sealed class RecordsDispositionTests(PostgresFixture pg)
{
    private static async Task<RecordsEnv> WithScheduleAsync(PostgresFixture pg, params ClassInput[] classes)
    {
        var env = await RecordsEnv.CreateAsync(pg);
        await env.PublishScheduleAsync(classes.Length > 0 ? classes : new[] { RecordsEnv.DocClass("SPEC", months: 0) });
        return env;
    }

    [Fact]
    public async Task A_disposal_tombstones_the_object_issues_a_signed_certificate_and_the_sweeper_removes_the_content_safely()
    {
        var env = await WithScheduleAsync(pg);
        var content = RecordsEnv.Unique("destroy me");
        var doc = await env.ReleasedDocAsync("Doomed spec", content: content);
        var sha = RecordsEnv.Sha(content);
        await env.ClassifyAsync();
        var request = await env.ApprovedRequestAsync(("document", doc));

        // The content is still in the vault until the sweep: a disposal is a decision, the sweeper (grace period, integrity check) removes bytes.
        Assert.True(await env.Store.ExistsAsync(sha, content.Length));
        var executed = await env.Disposition.ExecuteAsync(RecordsEnv.Rex, request, "corr-exec");
        Assert.True(executed.IsOk, string.Join("; ", executed.Errors));
        Assert.Equal((1, 0, 0, 0), (executed.Value!.Destroyed, executed.Value.Transferred, executed.Value.Retained, executed.Value.Skipped));
        Assert.NotNull(executed.Value.CertificateNumber);

        // The record stays: master, revision, iteration and file rows are all still there (referential integrity untouched), plus one tombstone.
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM plm.document_master WHERE number = @n", ("n", doc)));
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM plm.document_file f JOIN plm.document_iteration i ON i.id = f.iteration_id JOIN plm.document_revision r ON r.id = i.revision_id JOIN plm.document_master m ON m.id = r.master_id WHERE m.number = @n", ("n", doc)));
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM rec.tombstone WHERE object_number = @n", ("n", doc)));

        // The certificate is signed with the audit checkpoint key and names the content that was released.
        var cert = (await env.Disposition.GetCertificateAsync(RecordsEnv.Rex, executed.Value.CertificateNumber!, null)).Value!;
        Assert.True(cert.Verification.Valid);
        Assert.Contains(sha, cert.Body, StringComparison.Ordinal);
        Assert.Contains(doc, cert.Body, StringComparison.Ordinal);
        Assert.Contains("\"signer\":\"bob\"", cert.Body, StringComparison.Ordinal); // the approval's signatures are on the certificate
        Assert.Equal(["pending"], cert.Content.Select(c => c.State));
        Assert.NotNull(await env.AppSqlAsync("UPDATE rec.certificate SET issued_by = 'x'")); // a certificate cannot be changed by the application role

        // The object is frozen: no new work, no state change, no download.
        Assert.Contains("frozen", await env.AppSqlAsync("UPDATE plm.document_revision SET lifecycle_state = 'Obsolete' WHERE master_id = (SELECT id FROM plm.document_master WHERE number = @n)", ("n", doc)) ?? "ran", StringComparison.Ordinal);
        var download = await env.Docs.DownloadAsync(RecordsEnv.Alice, doc, null, sha, null, null);
        Assert.Equal(OutcomeKind.Conflict, download.Kind);
        Assert.Contains("retention schedule", string.Join(" ", download.Errors), StringComparison.Ordinal);
        var status = (await env.Holds.StatusOfAsync(RecordsEnv.Rex, "document", doc))!;
        Assert.True(status.Tombstoned);
        Assert.Equal(executed.Value.CertificateNumber, status.CertificateNumber);

        // The sweeper now treats the file row as garbage; it removes the bytes after its grace period, and the confirmation records that, signed.
        var early = await env.SweepAsync(null, GcMode.Delete, daysAhead: 0); // still inside the grace period
        Assert.Equal(0, early.Deleted);
        Assert.True(await env.Store.ExistsAsync(sha, content.Length));
        var pendingConfirm = (await env.Disposition.ConfirmDestructionAsync(RecordsEnv.Rex, executed.Value.CertificateNumber!, null)).Value!;
        Assert.Equal((0, 0, 1), (pendingConfirm.Confirmed, pendingConfirm.RetainedShared, pendingConfirm.Pending));
        var swept = await env.SweepAsync("", GcMode.Delete);
        Assert.True(swept.Deleted >= 1, "the released content should be collected");
        Assert.False(await env.Store.ExistsAsync(sha, content.Length));
        var confirmed = (await env.Disposition.ConfirmDestructionAsync(RecordsEnv.Rex, executed.Value.CertificateNumber!, null)).Value!;
        Assert.Equal((1, 0, 0), (confirmed.Confirmed, confirmed.RetainedShared, confirmed.Pending));
        Assert.Equal(["destroyed"], (await env.Disposition.GetCertificateAsync(RecordsEnv.Rex, executed.Value.CertificateNumber!, null)).Value!.Content.Select(c => c.State));
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM rec.destruction_confirmation WHERE vault_absent"));

        // Nothing broke the hash-chained audit log, and the disposal is in it: no free text, no marking, identifiers and codes only.
        var chain = await env.VerifyAuditChainAsync();
        Assert.True(chain.Valid, string.Join("; ", chain.Failures));
        var audit = await env.AuditAsync();
        var dispose = Assert.Single(audit, e => e.Action == "records.dispose" && e.ObjectId == doc);
        Assert.Contains(executed.Value.CertificateNumber!, dispose.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Doomed spec", string.Join("\n", audit.Where(e => e.Action.StartsWith("records.", StringComparison.Ordinal)).Select(e => e.Payload)), StringComparison.Ordinal);
        Assert.Contains(audit, e => e.Action == "records.destruction_confirm");
        Assert.Contains(audit, e => e.Action == "records.disposition_execute" && e.Outcome == AuditOutcome.Success);

        // Executing twice is refused, and the tombstone cannot be undone.
        Assert.Equal(OutcomeKind.Conflict, (await env.Disposition.ExecuteAsync(RecordsEnv.Rex, request, null)).Kind);
        Assert.NotNull(await env.AppSqlAsync("DELETE FROM rec.tombstone"));
    }

    [Fact]
    public async Task Content_another_live_document_still_uses_is_never_collected_and_is_recorded_as_retained()
    {
        var env = await WithScheduleAsync(pg);
        var shared = RecordsEnv.Unique("shared bytes");
        var doomed = await env.ReleasedDocAsync("Doomed", content: shared);
        var keeper = await env.NewDocAsync("Keeper (in work)", content: shared, type: "Other");
        await env.ClassifyAsync();
        var request = await env.ApprovedRequestAsync(("document", doomed));
        var executed = (await env.Disposition.ExecuteAsync(RecordsEnv.Rex, request, null)).Value!;

        var swept = await env.SweepAsync();
        Assert.True(await env.Store.ExistsAsync(RecordsEnv.Sha(shared), shared.Length), "another document still references the bytes");
        _ = swept;
        var confirm = (await env.Disposition.ConfirmDestructionAsync(RecordsEnv.Rex, executed.CertificateNumber!, null)).Value!;
        Assert.Equal((0, 1, 0), (confirm.Confirmed, confirm.RetainedShared, confirm.Pending));
        Assert.Equal("retained_shared", (await env.Disposition.GetCertificateAsync(RecordsEnv.Rex, executed.CertificateNumber!, null)).Value!.Content.Single().State);
        Assert.NotNull(await env.Docs.GetAsync(RecordsEnv.Alice, keeper, null, null));
    }

    [Fact]
    public async Task A_hold_placed_after_approval_keeps_the_object_and_a_hold_placed_during_the_grace_period_keeps_its_content_until_released()
    {
        var env = await WithScheduleAsync(pg);
        var late = await env.ReleasedDocAsync("Held late");
        var other = RecordsEnv.Unique("grace");
        var tombstoned = await env.ReleasedDocAsync("Disposed", content: other);
        await env.ClassifyAsync();
        var request = await env.ApprovedRequestAsync(("document", late), ("document", tombstoned));

        // The hold arrives between approval and execution: the held object is skipped, the other one is disposed of.
        var hold = await env.PlaceObjectHoldAsync("document", late);
        var executed = (await env.Disposition.ExecuteAsync(RecordsEnv.Rex, request, null)).Value!;
        Assert.Equal((1, 1), (executed.Destroyed, executed.Skipped));
        var view = (await env.Disposition.GetAsync(RecordsEnv.Rex, request, null)).Value!;
        Assert.Equal(SkipCodes.Held, view.Items.Single(i => i.Number == late).ResultCode);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM rec.tombstone WHERE object_number = @n", ("n", late)));
        Assert.True((await env.Docs.DownloadAsync(RecordsEnv.Alice, late, null, await env.FileHashAsync(late), null, null)).IsOk); // still fully usable

        // A hold on the disposed document, placed inside the grace period: the sweeper keeps its content while the hold lasts.
        await env.PlaceObjectHoldAsync("document", tombstoned);
        var kept = await env.SweepAsync();
        Assert.True(await env.Store.ExistsAsync(RecordsEnv.Sha(other), other.Length), "a held object's content is never collected");
        _ = kept;
        // Released with signatures, the next sweep takes it.
        var holds = (await env.Holds.ListAsync(RecordsEnv.Rex, false, null)).Value!;
        foreach (var h in holds) await env.ReviewAsync("legal_hold", h.Number);
        Assert.Empty((await env.Holds.ListAsync(RecordsEnv.Rex, false, null)).Value!);
        await env.SweepAsync();
        Assert.False(await env.Store.ExistsAsync(RecordsEnv.Sha(other), other.Length));
        _ = hold;
    }

    [Fact]
    public async Task The_database_itself_refuses_a_tombstone_for_a_held_object_or_without_an_approved_request()
    {
        var env = await WithScheduleAsync(pg);
        var doc = await env.ReleasedDocAsync("Protected");
        await env.ClassifyAsync();
        var request = await env.ApprovedRequestAsync(("document", doc));
        await env.PlaceObjectHoldAsync("document", doc);
        const string Insert = """
            INSERT INTO rec.tombstone (id, object_kind, object_id, object_number, disposition_id, certificate_id, action, tombstoned_at, tombstoned_by, export_jurisdiction, cui_categories, distribution_statement, owning_org, export_authorizations)
            VALUES (gen_random_uuid(), 'document', (SELECT id FROM plm.document_master WHERE number = @n), @n, (SELECT id FROM rec.disposition WHERE number = @r), gen_random_uuid(), 'destroy', now(), 'x', 'None', ARRAY[]::text[], 'A', 'ENG', ARRAY[]::text[])
            """;
        var held = await env.AppSqlAsync(Insert, ("n", doc), ("r", request));
        Assert.Contains("legal hold", held ?? "ran", StringComparison.Ordinal);
        var unlisted = await env.AppSqlAsync(Insert.Replace("'document', (SELECT id FROM plm.document_master WHERE number = @n), @n", "'document', gen_random_uuid(), @n", StringComparison.Ordinal), ("n", doc), ("r", request));
        Assert.Contains("approved disposition request", unlisted ?? "ran", StringComparison.Ordinal);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM rec.tombstone"));
    }

    [Fact]
    public async Task Marked_content_is_destroyed_only_by_a_records_manager_who_did_not_ask_for_it()
    {
        var env = await WithScheduleAsync(pg);
        var itar = await env.ReleasedDocAsync("Export controlled", RecordsEnv.Itar());
        await env.ClassifyAsync();
        var request = await env.ApprovedRequestAsync(("document", itar));

        // The requester cannot also execute marked content (two-person rule); an administrator alone cannot destroy records; a user outside the program cannot even see the request.
        var self = await env.Disposition.ExecuteAsync(RecordsEnv.Rex, request, null);
        Assert.Equal(OutcomeKind.Forbidden, self.Kind);
        Assert.Contains("sod.executor_is_requester", self.Errors);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Disposition.ExecuteAsync(RecordsEnv.Ada, request, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Disposition.ExecuteAsync(RecordsEnv.Eve, request, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Disposition.GetAsync(RecordsEnv.Eve, request, null)).Kind);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM rec.tombstone"));
        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.Action == "records.disposition_execute" && e.Outcome == AuditOutcome.Denied);

        var done = await env.Disposition.ExecuteAsync(RecordsEnv.Rhea, request, null);
        Assert.True(done.IsOk, string.Join("; ", done.Errors));
        // The certificate carries the markings of what it lists: a user outside the program cannot read it.
        Assert.Equal(OutcomeKind.NotFound, (await env.Disposition.GetCertificateAsync(RecordsEnv.Eve, done.Value!.CertificateNumber!, null)).Kind);
        Assert.Empty((await env.Disposition.ListCertificatesAsync(RecordsEnv.Eve, null)).Value!);
        Assert.Single((await env.Disposition.ListCertificatesAsync(RecordsEnv.Rhea, null)).Value!);
    }

    [Fact]
    public async Task An_object_the_requester_cannot_see_is_reported_as_missing_and_a_rejected_request_disposes_of_nothing()
    {
        var env = await WithScheduleAsync(pg);
        var orca = await env.ReleasedDocAsync("Orca", RecordsEnv.Itar("ORCA"));
        var kraken = await env.NewDocAsync("Kraken", RecordsEnv.Itar("KRAKEN"), by: RecordsEnv.Kay);
        await env.ClassifyAsync();
        var both = await env.Disposition.CreateAsync(RecordsEnv.Rex, new CreateDispositionCommand([new ObjectRef("document", orca), new ObjectRef("document", kraken)], null), null);
        // Rex is not a member of KRAKEN, so he cannot see that document: the answer is "not found", the same as for a document that does not exist.
        Assert.Equal(OutcomeKind.Invalid, both.Kind);
        Assert.Contains($"Document {kraken} was not found.", both.Errors);

        var created = (await env.Disposition.CreateAsync(RecordsEnv.Rex, new CreateDispositionCommand([new ObjectRef("document", orca)], null), null)).Value!;
        var started = (await env.Flow.StartAsync(RecordsEnv.Rex, new Keel.Workflow.StartReviewCommand("disposition", created.Number, new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob"] }), null)).Value!;
        var rejected = await env.Flow.SignAsync(Keel.IntegrationTests.WorkflowEnv.Fresh(RecordsEnv.Rita), started.Tasks.Single(t => t.Status == "Open").Id, "Rejected", "Not yet", "corr");
        Assert.True(rejected.IsOk, string.Join("; ", rejected.Errors));
        var after = (await env.Disposition.GetAsync(RecordsEnv.Rex, created.Number, null)).Value!;
        Assert.Equal(DispositionStatuses.Rejected, after.Status); // a rejection is final: the objects return to the queue
        // Not approved: it cannot be executed.
        Assert.Equal(OutcomeKind.Conflict, (await env.Disposition.ExecuteAsync(RecordsEnv.Rhea, created.Number, null)).Kind);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM rec.tombstone"));
    }

    [Fact]
    public async Task A_review_disposition_keeps_the_object_for_another_period_and_never_destroys()
    {
        var env = await WithScheduleAsync(pg, RecordsEnv.DocClass("REV", months: 0, action: DispositionActions.Review));
        var doc = await env.ReleasedDocAsync("Reviewed");
        await env.ClassifyAsync();
        var request = await env.ApprovedRequestAsync(("document", doc));
        var done = (await env.Disposition.ExecuteAsync(RecordsEnv.Rex, request, null)).Value!;
        Assert.Equal((0, 1, null), (done.Destroyed, done.Retained, done.CertificateNumber));
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM rec.tombstone"));
        await env.ClassifyAsync();
        Assert.Empty((await env.Disposition.EligibleAsync(RecordsEnv.Rex, new EligibleQuery(), null)).Value!.Items); // not due again until the new date
        Assert.True((await env.Holds.StatusOfAsync(RecordsEnv.Rex, "document", doc))!.EligibleOn > DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6)));
    }

    [Fact]
    public async Task A_document_still_linked_from_a_live_part_is_left_alone_and_reported()
    {
        var env = await WithScheduleAsync(pg);
        var doc = await env.ReleasedDocAsync("Linked");
        var part = await env.NewPartAsync();
        await env.Db.ExecuteAsSuperuserAsync(
            $"""
            INSERT INTO plm.part_document_link (id, part_iteration_id, link_type, document_master_id, created_at, created_by)
            SELECT gen_random_uuid(), i.id, 'references', (SELECT id FROM plm.document_master WHERE number = '{doc}'), now(), 'test'
            FROM plm.part_iteration i JOIN plm.part_revision r ON r.id = i.revision_id JOIN plm.part_master m ON m.id = r.master_id WHERE m.number = '{part}'
            """);
        await env.ClassifyAsync();
        var request = await env.ApprovedRequestAsync(("document", doc));
        var done = (await env.Disposition.ExecuteAsync(RecordsEnv.Rex, request, null)).Value!;
        Assert.Equal((0, 1), (done.Destroyed, done.Skipped));
        Assert.Equal(SkipCodes.Referenced, (await env.Disposition.GetAsync(RecordsEnv.Rex, request, null)).Value!.Items.Single().ResultCode);
        Assert.Null(done.CertificateNumber); // nothing was disposed of: no certificate
    }

    [Fact]
    public async Task A_transfer_to_the_archive_seals_a_package_first_and_the_package_outlives_the_content()
    {
        var env = await WithScheduleAsync(pg, RecordsEnv.DocClass("ARC", months: 0, action: DispositionActions.TransferArchive));
        var content = RecordsEnv.Unique("archive me");
        var doc = await env.ReleasedDocAsync("To the archive", content: content);
        await env.ClassifyAsync();
        var request = await env.ApprovedRequestAsync(("document", doc));
        var done = await env.Disposition.ExecuteAsync(RecordsEnv.Rex, request, null);
        Assert.True(done.IsOk, string.Join("; ", done.Errors));
        Assert.Equal((0, 1), (done.Value!.Destroyed, done.Value.Transferred));
        Assert.NotNull(done.Value.ArchiveNumber);

        await env.SweepAsync();
        Assert.False(await env.Store.ExistsAsync(RecordsEnv.Sha(content), content.Length)); // the vault copy is gone ...
        var opened = (await env.Archives.OpenAsync(RecordsEnv.Rex, done.Value.ArchiveNumber!, null)).Value!;
        Assert.True(opened.Verification.Valid, string.Join("; ", opened.Verification.Problems)); // ... the sealed package still verifies and holds the file
        Assert.Equal(doc, Assert.Single(opened.Objects).Number);
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM rec.archive WHERE disposition_id IS NOT NULL"));
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM rec.tombstone WHERE action = 'transfer_archive'"));
    }

    [Fact]
    public async Task Parts_are_tombstoned_too_and_nothing_else_about_them_changes()
    {
        var env = await WithScheduleAsync(pg, RecordsEnv.DocClass("PARTS", months: 0, kinds: ["part"]));
        var part = await env.NewPartAsync("Old bracket");
        await env.ReleasePartAsync(part);
        await env.ClassifyAsync();
        var request = await env.ApprovedRequestAsync(("part", part));
        var done = (await env.Disposition.ExecuteAsync(RecordsEnv.Rex, request, null)).Value!;
        Assert.Equal(1, done.Destroyed);
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM plm.part_master WHERE number = @n", ("n", part)));
        Assert.Contains("frozen", await env.AppSqlAsync("UPDATE plm.part_revision SET lifecycle_state = 'Obsolete' WHERE master_id = (SELECT id FROM plm.part_master WHERE number = @n)", ("n", part)) ?? "ran", StringComparison.Ordinal);
        Assert.True((await env.VerifyAuditChainAsync()).Valid);
    }

    [Fact]
    public async Task Nothing_destructive_runs_without_a_signing_key()
    {
        var env = await WithScheduleAsync(pg);
        var doc = await env.ReleasedDocAsync("Unsignable");
        await env.ClassifyAsync();
        var request = await env.ApprovedRequestAsync(("document", doc));
        var noKey = new DispositionService(env.Ds, env.Audit, env.Clock, new RecordsSigner(null, env.Trusted), env.Archives, new RecordsOptions(), env.Store);
        var refused = await noKey.ExecuteAsync(RecordsEnv.Rex, request, null);
        Assert.Equal(OutcomeKind.Unavailable, refused.Kind);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM rec.tombstone"));
        Assert.Equal(DispositionStatuses.Approved, (await env.Disposition.GetAsync(RecordsEnv.Rex, request, null)).Value!.Status);
    }

    [Fact]
    public async Task A_tampered_certificate_fails_verification()
    {
        var env = await WithScheduleAsync(pg);
        var doc = await env.ReleasedDocAsync("Certified");
        await env.ClassifyAsync();
        var done = (await env.Disposition.ExecuteAsync(RecordsEnv.Rex, await env.ApprovedRequestAsync(("document", doc)), null)).Value!;
        // Only a database owner could do this (the triggers refuse everyone else); the verification still catches it.
        await env.Db.ExecuteAsSuperuserAsync("ALTER TABLE rec.certificate DISABLE TRIGGER certificate_immutable");
        await env.Db.ExecuteAsSuperuserAsync("UPDATE rec.certificate SET body = replace(body, 'destroy', 'keep')");
        var cert = (await env.Disposition.GetCertificateAsync(RecordsEnv.Rex, done.CertificateNumber!, null)).Value!;
        Assert.False(cert.Verification.Valid);
        Assert.False(cert.Verification.HashMatches);
    }
}
