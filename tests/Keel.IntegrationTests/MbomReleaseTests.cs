using Keel.Audit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Workflow;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>The release of an MBOM through the review workflow: the reconciliation gate, step-up signatures, the signed content hash and the read APIs other modules use.</summary>
[Collection(PostgresCollection.Name)]
public sealed class MbomReleaseTests(PostgresFixture pg)
{
    [Fact]
    public async Task An_mbom_cannot_be_submitted_without_a_reconciliation_or_with_errors()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var p = await env.ProductAsync();
        var number = (await env.GenerateAsync(p.Top)).Summary.Number;

        var none = await env.SubmitAsync(number);
        Assert.Equal(OutcomeKind.Conflict, none.Kind);
        Assert.Contains("Run a reconciliation", none.Errors[0], StringComparison.Ordinal);
        Assert.Equal("InWork", (await env.GetAsync(number)).Summary.State);

        // The sub-assembly has no MBOM: an error that blocks the release.
        await env.ReconcileAsync(number);
        var errors = await env.SubmitAsync(number);
        Assert.Equal(OutcomeKind.Conflict, errors.Kind);
        Assert.Contains("Not reconciled", errors.Errors[0], StringComparison.Ordinal);
        Assert.Equal("InWork", (await env.GetAsync(number)).Summary.State);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM wf.instance WHERE object_type = 'mbom'"));

        // An edit after the reconciliation makes it describe another iteration: the new iteration needs its own run.
        await env.CheckOutAsync(number);
        await env.CheckInAsync(number);
        Assert.Contains("Run a reconciliation", (await env.SubmitAsync(number)).Errors[0], StringComparison.Ordinal);
        Assert.Contains("Run a reconciliation", (await env.GetAsync(number)).ReleaseBlocker!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_plant_policy_decides_whether_warnings_block_the_release()
    {
        var env = await MbomEnv.CreateAsync(pg);
        Assert.True((await env.Mbom.SavePlantAsync(MbomEnv.Ada, new SavePlantCommand("PLANT-S", "Strict plant (synthetic)", true, false, 30), "c")).IsOk);
        var p = await env.ProductAsync();
        foreach (var plant in new[] { "PLANT-A", "PLANT-S" })
        {
            var r = await env.Mbom.GenerateAsync(MbomEnv.Alice, new GenerateMbomCommand(p.Top, plant, null, MultiLevel: true), "c");
            Assert.True(r.IsOk, string.Join("; ", r.Errors));
            var top = r.Value!.Items[0].MbomNumber;
            var report = await env.ReconcileAsync(top);
            Assert.Equal((ReconcileStatuses.WithExceptions, 0, 1), (report.Run.Status, report.Run.Errors, report.Run.Warnings)); // the sub-assembly MBOM is not released yet
            var submit = await env.SubmitAsync(top);
            if (plant == "PLANT-A") Assert.True(submit.IsOk, string.Join("; ", submit.Errors));
            else
            {
                Assert.Equal(OutcomeKind.Conflict, submit.Kind);
                Assert.Contains("does not release an MBOM whose reconciliation has warnings", submit.Errors[0], StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task A_clean_mbom_is_released_by_signatures_that_need_a_fresh_authentication()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (p, number) = await env.ReconciledProductAsync();
        var started = await env.SubmitAsync(number);
        Assert.True(started.IsOk, string.Join("; ", started.Errors));
        var view = started.Value!;
        Assert.Equal("UnderReview", (await env.GetAsync(number)).Summary.State);
        Assert.Equal(OutcomeKind.Conflict, (await env.Mbom.CheckOutAsync(MbomEnv.Alice, number, "c")).Kind); // locked while under review

        // Step-up: a stale authentication cannot sign.
        var task = view.Tasks.First(t => t.Status == "Open");
        var stale = await env.Flow.SignAsync(WorkflowEnv.Fresh(ConfigEnv.Rita, DateTimeOffset.UtcNow.AddMinutes(-30)), task.Id, "Reviewed", null, "c");
        Assert.Equal(OutcomeKind.StepUpRequired, stale.Kind);
        Assert.Equal(OutcomeKind.StepUpRequired, (await env.Flow.SignAsync(WorkflowEnv.WithoutAuthTime(ConfigEnv.Rita), task.Id, "Reviewed", null, "c")).Kind);

        var done = await env.SignAllAsync(view);
        Assert.Equal("Approved", done.Status);
        var released = await env.GetAsync(number);
        Assert.Equal(("A", "Released"), (released.Summary.Revision, released.Summary.State));
        Assert.Equal(3, done.Signatures.Count);
        var verified = (await env.Flow.VerifyInstanceAsync(MbomEnv.Alice, done.Id, "c")).Value!;
        Assert.All(verified, v => Assert.True(v.Valid, string.Join("; ", v.Problems)));

        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.Action == "mbom.submit" && e.ObjectId == number && e.Outcome == AuditOutcome.Success);
        Assert.Contains(audit, e => e.Action == "mbom.release" && e.ObjectId == number && e.Outcome == AuditOutcome.Success);
        Assert.Equal(1, await env.ScalarAsync($"SELECT count(*) FROM mbom.review_binding b JOIN mbom.revision r ON r.id = b.revision_id JOIN mbom.mbom m ON m.id = r.mbom_id WHERE m.number = '{number}'"));
        Assert.Equal("Reconciled", (await env.Mbom.GetReconciliationAsync(MbomEnv.Alice, number, null, "c")).Value!.Run.Status);
        Assert.NotEmpty(p.Top);
    }

    [Fact]
    public async Task The_signature_covers_lines_links_and_the_reconciliation_so_tampering_is_detected()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (_, number) = await env.ReconciledProductAsync();
        var done = await env.ReleaseAsync(number);
        Assert.All((await env.Flow.VerifyInstanceAsync(MbomEnv.Alice, done.Id, "c")).Value!, v => Assert.True(v.Valid));

        // Editing an MBOM line with a superuser connection, bypassing the immutability triggers, invalidates every signature.
        await env.Db.ExecuteAsSuperuserAsync("SET session_replication_role = replica; UPDATE mbom.usage SET quantity = 99 WHERE find_number = 10 AND iteration_id = (SELECT i.id FROM mbom.iteration i JOIN mbom.revision r ON r.id = i.revision_id JOIN mbom.mbom m ON m.id = r.mbom_id WHERE m.number = '" + number + "' AND i.iteration = r.latest_iteration)");
        var tampered = (await env.Flow.VerifyInstanceAsync(MbomEnv.Alice, done.Id, "c")).Value!;
        Assert.All(tampered, v =>
        {
            Assert.False(v.Valid);
            Assert.Contains(v.Problems, pr => pr.Contains("changed after signing", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task A_line_a_link_or_the_run_changed_during_the_review_refuses_the_release()
    {
        foreach (var attack in new[]
        {
            "UPDATE mbom.usage SET quantity = 99 WHERE id = (SELECT u.id FROM mbom.usage u JOIN mbom.iteration i ON i.id = u.iteration_id JOIN mbom.revision r ON r.id = i.revision_id JOIN mbom.mbom m ON m.id = r.mbom_id WHERE m.number = '{n}' AND u.find_number = 10 AND i.iteration = r.latest_iteration)",
            "UPDATE mbom.usage_link SET status = 'substituted', reason = 'forged' WHERE mbom_usage_id = (SELECT u.id FROM mbom.usage u JOIN mbom.iteration i ON i.id = u.iteration_id JOIN mbom.revision r ON r.id = i.revision_id JOIN mbom.mbom m ON m.id = r.mbom_id WHERE m.number = '{n}' AND u.find_number = 10 AND i.iteration = r.latest_iteration)",
            "UPDATE mbom.reconciliation_run SET warnings = 3 WHERE mbom_id = (SELECT id FROM mbom.mbom WHERE number = '{n}')",
        })
        {
            var env = await MbomEnv.CreateAsync(pg);
            var (_, number) = await env.ReconciledProductAsync();
            var started = await env.SubmitAsync(number);
            var v = started.Value!;
            // The reviewer signs, then the stored content is altered, then the approvers sign: the last signature cannot complete the release.
            var rita = v.Tasks.First(t => t.Status == "Open" && t.AssigneeUsername == "rita");
            v = (await env.Flow.SignAsync(WorkflowEnv.Fresh(ConfigEnv.Rita), rita.Id, "Reviewed", null, "c")).Value!;
            await env.Db.ExecuteAsSuperuserAsync("SET session_replication_role = replica; " + attack.Replace("{n}", number, StringComparison.Ordinal));
            InstanceView? last = null;
            foreach (var user in new[] { ConfigEnv.Bob, ConfigEnv.Carol })
            {
                var task = v.Tasks.FirstOrDefault(t => t.Status == "Open" && t.AssigneeUsername == user.Username);
                if (task is null) continue;
                var r = await env.Flow.SignAsync(WorkflowEnv.Fresh(user), task.Id, "Approved", null, "c");
                if (!r.IsOk) { Assert.Contains("does not match what was reviewed", string.Join(" ", r.Errors), StringComparison.Ordinal); break; }
                last = r.Value;
                v = r.Value!;
            }
            Assert.NotEqual("Approved", last?.Status);
            Assert.Equal("UnderReview", (await env.GetAsync(number)).Summary.State);
        }
    }

    [Fact]
    public async Task The_database_refuses_a_release_that_no_clean_reconciliation_was_bound_to()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var p = await env.ProductAsync();
        var number = (await env.GenerateAsync(p.Top)).Summary.Number;
        await env.ReconcileAsync(number); // not reconciled: the sub-assembly MBOM is missing
        async Task<string> Rejected(string sql)
        {
            try { await env.Db.ExecuteAsSuperuserAsync(sql); }
            catch (PostgresException e) { return e.MessageText; }
            Assert.Fail("Not rejected: " + sql);
            return "";
        }
        await env.Db.ExecuteAsSuperuserAsync("UPDATE mbom.revision SET lifecycle_state = 'UnderReview'");
        Assert.Contains("released only after a reconciliation without errors", await Rejected("UPDATE mbom.revision SET lifecycle_state = 'Released'"), StringComparison.Ordinal);
        Assert.Contains("no transition", await Rejected("UPDATE mbom.revision SET lifecycle_state = 'Obsolete'"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_review_returns_the_mbom_to_in_work()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (_, number) = await env.ReconciledProductAsync();
        var started = (await env.SubmitAsync(number)).Value!;
        var task = started.Tasks.First(t => t.Status == "Open");
        var rejected = await env.Flow.SignAsync(WorkflowEnv.Fresh(ConfigEnv.Rita), task.Id, "Rejected", "Routing does not match the plant layout", "c");
        Assert.True(rejected.IsOk, string.Join("; ", rejected.Errors));
        Assert.Equal("Rejected", rejected.Value!.Status);
        Assert.Equal("InWork", (await env.GetAsync(number)).Summary.State);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "mbom.review_cancel" || e.Action == "workflow.sign");
        // After a fix and a new reconciliation it can be submitted again.
        Assert.True((await env.SubmitAsync(number)).IsOk);
    }

    // ---------------------------------------------------------------- read API for integrations and change management

    [Fact]
    public async Task Integrations_read_the_released_mbom_with_resolved_children_and_redaction()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (p, number) = await env.ReconciledProductAsync();
        Assert.Equal(OutcomeKind.NotFound, (await env.Source.Source.GetReleasedAsync(MbomEnv.Alice, p.Top, MbomEnv.Plant, null, "c")).Kind); // not released yet
        await env.ReleaseAsync(number);
        var snapshot = (await env.Source.Source.GetReleasedAsync(MbomEnv.Alice, p.Top, MbomEnv.Plant.ToLowerInvariant(), null, "c")).Value!;
        Assert.Equal((number, p.Top, MbomEnv.Plant, "A", "Released", "A.2"), (snapshot.MbomNumber, snapshot.PartNumber, snapshot.Plant, snapshot.Revision, snapshot.State, snapshot.DerivedFromEbom));
        Assert.Equal(ReconcileStatuses.Reconciled, snapshot.ReconciliationStatus);
        Assert.NotNull(snapshot.ReconciliationRunId);
        Assert.Equal(64, snapshot.ContentSha256.Length);
        var sub = snapshot.Lines.Single(l => l.FindNumber == 30);
        Assert.Equal((p.Sub, "A"), (sub.ChildNumber, sub.ChildMbomRevision));
        Assert.StartsWith("MB-", sub.ChildMbomNumber, StringComparison.Ordinal);
        Assert.Equal([10], snapshot.Lines.Single(l => l.FindNumber == 10).EbomLinks.Select(k => k.EbomFindNumber).ToArray());
        Assert.Null(snapshot.Lines.Single(l => l.FindNumber == 10).ChildMbomNumber);
        Assert.Equal(OutcomeKind.NotFound, (await env.Source.Source.GetReleasedAsync(MbomEnv.Alice, p.Top, "PLANT-B", null, "c")).Kind);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "mbom.read_released" && e.Outcome == AuditOutcome.Success);

        // A reader outside the program sees nothing of an ITAR MBOM.
        var itarTop = await env.C.NewPartAsync("Hidden assembly", "Assembly", ConfigEnv.Itar());
        await env.C.CheckOutAsync(itarTop);
        await env.C.AddLineAsync(itarTop, p.Screw, 1);
        await env.C.CheckInAsync(itarTop);
        await env.C.ReleaseAsync(itarTop);
        var hidden = (await env.GenerateAsync(itarTop)).Summary.Number;
        await env.ReconcileAsync(hidden);
        await env.ReleaseAsync(hidden);
        Assert.Equal(OutcomeKind.NotFound, (await env.Source.Source.GetReleasedAsync(MbomEnv.Eve, itarTop, MbomEnv.Plant, null, "c")).Kind);
        Assert.True((await env.Source.Source.GetReleasedAsync(MbomEnv.Alice, itarTop, MbomEnv.Plant, null, "c")).IsOk);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "mbom.read_released" && e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task Change_impact_lists_the_mboms_derived_from_a_revised_ebom_per_plant()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var p = await env.ProductAsync();
        var a = await env.GenerateAsync(p.Top);
        var b = await env.GenerateAsync(p.Top, "PLANT-B");
        var manual = await env.Mbom.CreateAsync(MbomEnv.Alice, new CreateMbomCommand(p.Sub, MbomEnv.Plant), "c"); // not derived: not affected

        var ecn = await env.C.Change.CreateAsync(MbomEnv.Alice, new CreateChangeCommand(ChangeTypes.Ecn, "Revise top", "Synthetic", "Medium", "Synthetic reason", ConfigEnv.Open, ChangeClasses.II), "c");
        var added = await env.C.Change.AddAffectedAsync(MbomEnv.Alice, ecn.Value!.Number, new AddAffectedCommand("part", p.Top, ChangeActions.Revise, null, false, null), "c");
        Assert.True(added.IsOk, string.Join("; ", added.Errors));
        var report = (await env.Impact.AnalyzeAsync(MbomEnv.Alice, ecn.Value.Number, 5, "c")).Value!;
        var stale = report.Objects.Single().StaleMboms!;
        Assert.Equal([(a.Summary.Number, "PLANT-A"), (b.Summary.Number, "PLANT-B")], stale.Select(s => (s.MbomNumber, s.Plant)).ToArray());
        Assert.All(stale, s => Assert.Equal("A.2", s.DerivedFrom));
        Assert.Equal(2, report.Summary.StaleMboms);
        Assert.DoesNotContain(manual.Value!.Summary.Number, stale.Select(s => s.MbomNumber));
        // The revision exists now (B.1): the EBOM has already moved past what the MBOMs were derived from.
        Assert.All(stale, s => Assert.Equal((true, "B.1"), (s.StaleNow, s.CurrentEbom)));

        // Restricted MBOMs are counted, never named.
        var itarTop = await env.C.NewPartAsync("Restricted assembly", "Assembly", ConfigEnv.Itar());
        await env.C.CheckOutAsync(itarTop);
        await env.C.AddLineAsync(itarTop, p.Screw, 1);
        await env.C.CheckInAsync(itarTop);
        await env.C.ReleaseAsync(itarTop);
        await env.GenerateAsync(itarTop);
        var viaProvider = (await env.Source.Impact.StaleMbomsAsync(MbomEnv.Eve, itarTop, "c"));
        Assert.Equal(OutcomeKind.NotFound, viaProvider.Kind); // Eve cannot read the part at all
        var asAlice = (await env.Source.Impact.StaleMbomsAsync(MbomEnv.Alice, itarTop, "c", default)).Value!;
        Assert.Single(asAlice.Mboms);
        Assert.Equal(0, asAlice.Hidden);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "change.impact" && e.Outcome == AuditOutcome.Success);
    }
}
