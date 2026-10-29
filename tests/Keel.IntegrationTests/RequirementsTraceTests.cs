using Keel.Audit;
using Keel.Core;
using Keel.Requirements;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Traceability links, suspect detection, verification and e-signature, coverage and the digital thread seen from parts and documents.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RequirementsTraceTests(PostgresFixture pg)
{
    [Fact]
    public async Task Links_join_requirements_to_parts_and_other_requirements_and_refuse_bad_ends()
    {
        var env = await ReqEnv.CreateAsync(pg);
        var spec = await env.SpecAsync();
        var parent = await env.AddAsync(spec, ReqEnv.Input("System depth rating"));
        var child = await env.AddAsync(spec, ReqEnv.Input("Housing depth rating"));
        var part = await env.C.NewPartAsync("Housing");
        await env.LinkAsync(spec, child, "allocated-to", part);
        await env.LinkAsync(spec, child, "derives-from", targetKey: parent);

        var duplicate = await env.Req.AddLinkAsync(ReqEnv.Alice, spec, child, new AddLinkCommand("allocated-to", null, null, part), null);
        Assert.Equal(OutcomeKind.Conflict, duplicate.Kind);
        var cycle = await env.Req.AddLinkAsync(ReqEnv.Alice, spec, parent, new AddLinkCommand("derives-from", null, child, null), null);
        Assert.Equal(OutcomeKind.Invalid, cycle.Kind);
        var self = await env.Req.AddLinkAsync(ReqEnv.Alice, spec, child, new AddLinkCommand("derives-from", null, child, null), null);
        Assert.Equal(OutcomeKind.Invalid, self.Kind);
        var wrongKind = await env.Req.AddLinkAsync(ReqEnv.Alice, spec, child, new AddLinkCommand("verified-by", null, null, part), null);
        Assert.Equal(OutcomeKind.Invalid, wrongKind.Kind); // a verification link points at a document, and the part number is not one
        var itar = await env.C.NewPartAsync("Secret housing", "Component", ReqEnv.Itar);
        var hidden = await env.Req.AddLinkAsync(ReqEnv.Eve, spec, child, new AddLinkCommand("allocated-to", null, null, itar), null);
        var missing = await env.Req.AddLinkAsync(ReqEnv.Eve, spec, child, new AddLinkCommand("allocated-to", null, null, "P-999999"), null);
        Assert.Equal(hidden.Errors[0].Replace(itar, "X", StringComparison.Ordinal), missing.Errors[0].Replace("P-999999", "X", StringComparison.Ordinal)); // not readable looks like not existing

        var links = (await env.Req.GetLinksAsync(ReqEnv.Alice, spec, child, null)).Value!;
        Assert.Equal(2, links.Links.Count);
        Assert.Contains(links.Links, l => l.Direction == "outgoing" && l.LinkType == "allocated-to" && l.OtherNumber == part && l.OtherRevision == "A");
        var up = (await env.Req.GetLinksAsync(ReqEnv.Alice, spec, parent, null)).Value!;
        var inc = Assert.Single(up.Links);
        Assert.Equal("incoming", inc.Direction);
        Assert.Equal(child, inc.OtherKey);

        // Seen from the part: the requirement, with its verification state.
        var trace = (await env.Req.GetObjectTraceAsync(ReqEnv.Alice, "part", part, null)).Value!;
        var row = Assert.Single(trace.Requirements);
        Assert.Equal(child, row.Key);
        Assert.Equal(VerificationStates.None, row.VerificationStatus);
        Assert.False(row.Suspect);

        // Remove a link: it disappears from both sides.
        Assert.True((await env.Req.RemoveLinkAsync(ReqEnv.Alice, spec, links.Links.Single(l => l.LinkType == "allocated-to").Id, null)).IsOk);
        Assert.Empty((await env.Req.GetObjectTraceAsync(ReqEnv.Alice, "part", part, null)).Value!.Requirements);
        await Assert.ThrowsAnyAsync<PostgresException>(() => env.C.Db.ExecuteAsSuperuserAsync("DELETE FROM req.link"));
    }

    [Fact]
    public async Task A_link_becomes_suspect_when_either_end_changes_and_stays_so_until_reviewed()
    {
        var env = await ReqEnv.CreateAsync(pg);
        var spec = await env.SpecAsync();
        var parent = await env.AddAsync(spec, ReqEnv.Input("Parent text"));
        var child = await env.AddAsync(spec, ReqEnv.Input("Child text"));
        var part = await env.C.ReleasedComponentAsync("Housing");
        await env.LinkAsync(spec, child, "allocated-to", part);
        await env.LinkAsync(spec, child, "derives-from", targetKey: parent);

        // The parent changes: the derives-from link is suspect (target changed) and an audit event says so, without text.
        await env.UpdateAsync(spec, parent, ReqEnv.Input("Parent text", "A changed statement with marker-TEXT-1."));
        var links = (await env.Req.GetLinksAsync(ReqEnv.Alice, spec, child, null)).Value!.Links;
        var derived = links.Single(l => l.LinkType == "derives-from");
        Assert.True(derived.Suspect);
        Assert.Equal(SuspectReasons.TargetChanged, derived.SuspectReason);
        Assert.False(links.Single(l => l.LinkType == "allocated-to").Suspect);
        var suspectEvents = (await env.C.AuditAsync()).Where(e => e.Action == "requirement.link_suspect").ToList();
        Assert.Single(suspectEvents);
        Assert.DoesNotContain("marker-TEXT-1", suspectEvents[0].Payload, StringComparison.Ordinal);

        // The child changes too: the allocation is now suspect (source changed).
        await env.UpdateAsync(spec, child, ReqEnv.Input("Child text", "Reworded child."));
        Assert.All((await env.Req.GetLinksAsync(ReqEnv.Alice, spec, child, null)).Value!.Links, l => Assert.True(l.Suspect));

        // Review clears one link; only an author may review.
        var review = await env.Req.ReviewLinkAsync(ReqEnv.Alice, spec, derived.Id, null);
        Assert.True(review.IsOk, string.Join("; ", review.Errors));
        Assert.Equal(OutcomeKind.Forbidden, (await env.Req.ReviewLinkAsync(ReqEnv.Mallory, spec, derived.Id, null)).Kind);
        var after = (await env.Req.GetLinksAsync(ReqEnv.Alice, spec, child, null)).Value!.Links;
        Assert.False(after.Single(l => l.LinkType == "derives-from").Suspect);
        Assert.True(after.Single(l => l.LinkType == "allocated-to").Suspect);
        Assert.Equal(OutcomeKind.Conflict, (await env.Req.ReviewLinkAsync(ReqEnv.Alice, spec, derived.Id, null)).Kind);
        Assert.Contains(await env.C.AuditAsync(), e => e.Action == "requirement.link_review" && e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task A_new_part_revision_makes_allocations_suspect_and_the_sweep_records_it_once()
    {
        var env = await ReqEnv.CreateAsync(pg);
        var spec = await env.SpecAsync();
        var key = await env.AddAsync(spec, ReqEnv.Input("Housing depth rating"));
        var part = await env.C.ReleasedComponentAsync("Housing");
        await env.LinkAsync(spec, key, "allocated-to", part);
        Assert.False((await env.Req.GetLinksAsync(ReqEnv.Alice, spec, key, null)).Value!.Links.Single().Suspect);

        var ecn = await env.C.ReviseWithEffectivityAsync(part, null);
        await env.C.ReleaseEcnAsync(ecn.Number);
        var link = (await env.Req.GetLinksAsync(ReqEnv.Alice, spec, key, null)).Value!.Links.Single();
        Assert.True(link.Suspect, "the drift shows on read, before any job ran");
        Assert.Equal(SuspectReasons.TargetRevised, link.SuspectReason);
        Assert.Equal("B", link.OtherRevision);
        Assert.Equal(0, await env.C.ScalarAsync("SELECT count(*) FROM req.link WHERE status = 'suspect'"));

        Assert.Equal(OutcomeKind.Forbidden, (await env.Req.SweepAsync(ReqEnv.Alice, null)).Kind);
        var sweep = (await env.Req.SweepAsync(ReqEnv.Ada, "corr-sweep")).Value!;
        Assert.Equal(1, sweep.MarkedSuspect);
        Assert.Equal(1, await env.C.ScalarAsync("SELECT count(*) FROM req.link WHERE status = 'suspect' AND suspect_reason = 'target_revised'"));
        Assert.Equal(0, (await env.Req.SweepSuspectLinksAsync(null, null)).MarkedSuspect);
        Assert.Contains(await env.C.AuditAsync(), e => e.Action == "requirement.link_suspect" && e.Payload.Contains("target_revised", StringComparison.Ordinal));

        // Reviewing records revision B as the reviewed one: the link is current until B is superseded.
        Assert.True((await env.Req.ReviewLinkAsync(ReqEnv.Alice, spec, link.Id, null)).IsOk);
        Assert.False((await env.Req.GetLinksAsync(ReqEnv.Alice, spec, key, null)).Value!.Links.Single().Suspect);
        Assert.Equal(0, (await env.Req.SweepSuspectLinksAsync(null, null)).MarkedSuspect);
        var trace = (await env.Req.GetObjectTraceAsync(ReqEnv.Alice, "part", part, null)).Value!;
        Assert.False(trace.Requirements.Single().Suspect);
    }

    [Fact]
    public async Task Verification_records_are_immutable_judge_the_current_content_and_formal_ones_are_signed()
    {
        var env = await ReqEnv.CreateAsync(pg, documents: true);
        var spec = await env.SpecAsync();
        var key = await env.AddAsync(spec, ReqEnv.Input("Depth rating"));
        var report = await env.DocumentAsync("Pressure test report");
        var hiddenDoc = await env.DocumentAsync("Secret test report", ReqEnv.Itar);

        Assert.Equal(VerificationStates.None, (await env.Req.GetVerificationsAsync(ReqEnv.Alice, spec, key, null)).Value!.State);
        var waivedNoReason = await env.Req.RecordVerificationAsync(ReqEnv.Alice, spec, key, new RecordVerificationCommand("waived", null, null, null, null, false, null), null);
        Assert.Equal(OutcomeKind.Invalid, waivedNoReason.Kind);
        var hiddenEvidence = await env.Req.RecordVerificationAsync(ReqEnv.Eve, spec, key, new RecordVerificationCommand("pass", null, null, null, [hiddenDoc], false, null), null);
        Assert.Equal(OutcomeKind.Invalid, hiddenEvidence.Kind);

        var pass = await env.Req.RecordVerificationAsync(ReqEnv.Alice, spec, key, new RecordVerificationCommand("pass", null, "Held 6000 m for 4 hours.", null, [report], false, null), null);
        Assert.True(pass.IsOk, string.Join("; ", pass.Errors));
        var state = (await env.Req.GetVerificationsAsync(ReqEnv.Alice, spec, key, null)).Value!;
        Assert.Equal(VerificationStates.Verified, state.State);
        Assert.Equal(report, state.Records.Single().Evidence.Single().Number);

        // Seen from the document: the requirement it is evidence for.
        var docTrace = (await env.Req.GetObjectTraceAsync(ReqEnv.Alice, "document", report, null)).Value!;
        Assert.Equal(key, Assert.Single(docTrace.Requirements).Key);
        Assert.Equal(VerificationStates.Verified, docTrace.Requirements[0].VerificationStatus);

        // Changing the requirement makes the old result stale; a newer failure wins; a waiver needs a reason.
        await env.UpdateAsync(spec, key, ReqEnv.Input("Depth rating", "The sensor shall report depth to 7000 m."));
        Assert.Equal(VerificationStates.Stale, (await env.Req.GetVerificationsAsync(ReqEnv.Alice, spec, key, null)).Value!.State);
        Assert.True((await env.Req.RecordVerificationAsync(ReqEnv.Alice, spec, key, new RecordVerificationCommand("fail", null, null, null, null, false, null), null)).IsOk);
        Assert.Equal(VerificationStates.Failed, (await env.Req.GetVerificationsAsync(ReqEnv.Alice, spec, key, null)).Value!.State);
        Assert.True((await env.Req.RecordVerificationAsync(ReqEnv.Alice, spec, key, new RecordVerificationCommand("waived", null, null, "Customer accepted the risk.", null, false, null), null)).IsOk);
        Assert.Equal(VerificationStates.Waived, (await env.Req.GetVerificationsAsync(ReqEnv.Alice, spec, key, null)).Value!.State);
        await Assert.ThrowsAnyAsync<PostgresException>(() => env.C.Db.ExecuteAsSuperuserAsync("UPDATE req.verification SET result = 'pass'"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => env.C.Db.ExecuteAsSuperuserAsync("DELETE FROM req.verification_evidence"));

        // Formal verification: the approver role, a recent sign-in, evidence, a meaning; the signature is stored and immutable.
        var formal = new RecordVerificationCommand("pass", null, null, null, [report], true, "Verified by test");
        Assert.Equal(OutcomeKind.Forbidden, (await env.Req.RecordVerificationAsync(ReqEnv.Alice, spec, key, formal, null)).Kind);
        var old = ReqEnv.Bob with { AuthTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1) };
        var needsStepUp = await env.Req.RecordVerificationAsync(old, spec, key, formal, null);
        Assert.Equal(OutcomeKind.StepUpRequired, needsStepUp.Kind);
        Assert.Equal(0, await env.C.ScalarAsync("SELECT count(*) FROM req.verification_signature"));
        Assert.Contains(await env.C.AuditAsync(), e => e.Action == "requirement.verify" && e.Outcome == AuditOutcome.Denied);
        var noEvidence = await env.Req.RecordVerificationAsync(WorkflowEnv.Fresh(ReqEnv.Bob), spec, key, formal with { EvidenceDocuments = null }, null);
        Assert.Equal(OutcomeKind.Invalid, noEvidence.Kind);
        var signed = await env.Req.RecordVerificationAsync(WorkflowEnv.Fresh(ReqEnv.Bob), spec, key, formal, null);
        Assert.True(signed.IsOk, string.Join("; ", signed.Errors));
        Assert.True(signed.Value!.Formal);
        Assert.Equal(1, await env.C.ScalarAsync("SELECT count(*) FROM req.verification_signature WHERE octet_length(signature_hash) = 32"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => env.C.Db.ExecuteAsSuperuserAsync("UPDATE req.verification_signature SET meaning = 'x'"));
        // A formal record without a signature cannot exist.
        await Assert.ThrowsAnyAsync<PostgresException>(() => env.C.Db.ExecuteAsSuperuserAsync(
            "INSERT INTO req.verification (id, requirement_id, spec_revision, requirement_sha256, method, result, formal, performed_at, performed_by, performed_by_sub) " +
            "SELECT gen_random_uuid(), id, 'A', repeat('a', 64), 'test', 'pass', true, now(), 'x', 'x' FROM req.requirement LIMIT 1"));
    }

    [Fact]
    public async Task Coverage_counts_allocations_and_verification_and_names_the_gaps()
    {
        var env = await ReqEnv.CreateAsync(pg);
        var spec = await env.SpecAsync();
        var r1 = await env.AddAsync(spec, ReqEnv.Input("Allocated and verified"));
        var r2 = await env.AddAsync(spec, ReqEnv.Input("Allocated only"));
        var r3 = await env.AddAsync(spec, ReqEnv.Input("Verified only"));
        var r4 = await env.AddAsync(spec, ReqEnv.Input("Withdrawn", status: ReqStatuses.Withdrawn));
        var r5 = await env.AddAsync(spec, ReqEnv.Input("Neither"));
        var inBaseline = await env.C.ReleasedComponentAsync("In baseline");
        var outside = await env.C.ReleasedComponentAsync("Outside");
        var top = await env.C.ReleasedAssemblyAsync("Top", (inBaseline, 1m));
        await env.LinkAsync(spec, r1, "allocated-to", inBaseline);
        await env.LinkAsync(spec, r2, "allocated-to", outside);
        foreach (var k in new[] { r1, r3 })
            Assert.True((await env.Req.RecordVerificationAsync(ReqEnv.Alice, spec, k, new RecordVerificationCommand("pass", null, null, null, null, false, null), null)).IsOk);

        var cov = (await env.Req.GetCoverageAsync(ReqEnv.Alice, spec, null, null, null)).Value!;
        Assert.Equal(5, cov.Summary.Total);
        Assert.Equal(4, cov.Summary.InScope);
        Assert.Equal(2, cov.Summary.Allocated);
        Assert.Equal(2, cov.Summary.Unallocated);
        Assert.Equal(2, cov.Summary.Verified);
        Assert.Equal(2, cov.Summary.NotVerified);
        Assert.Equal(3, cov.Summary.Gaps); // r2 (unverified), r3 (unallocated), r5 (both)
        Assert.Equal([r2, r3, r5], cov.Rows.Where(r => r.Unallocated || r.Unverified).Where(r => r.InScope).Select(r => r.Key).Order(StringComparer.Ordinal));
        Assert.NotNull(r4);

        var baseline = await env.C.BaselineAsync(top);
        var inB = (await env.Req.GetCoverageAsync(ReqEnv.Alice, spec, null, baseline.Summary.Number, null)).Value!;
        Assert.Equal(1, inB.Summary.Allocated); // only r1's part is in the baseline
        Assert.Equal(baseline.Summary.Number, inB.Baseline);
        Assert.Equal(OutcomeKind.Invalid, (await env.Req.GetCoverageAsync(ReqEnv.Alice, spec, null, "BL-999999", null)).Kind);

        // Coverage of the released revision and of the next revision are separate.
        await env.UpdateAsync(spec, r1, ReqEnv.Input("Allocated and verified", "Changed so the earlier verification is stale."));
        Assert.Equal(VerificationStates.Stale, (await env.Req.GetCoverageAsync(ReqEnv.Alice, spec, null, null, null)).Value!.Rows.Single(r => r.Key == r1).VerificationState);
    }

    [Fact]
    public async Task The_trace_matrix_lists_readable_ends_across_and_requirements_down()
    {
        var env = await ReqEnv.CreateAsync(pg);
        var spec = await env.SpecAsync();
        var r1 = await env.AddAsync(spec, ReqEnv.Input("One"));
        var r2 = await env.AddAsync(spec, ReqEnv.Input("Two"));
        var p1 = await env.C.NewPartAsync("Part one");
        var p2 = await env.C.NewPartAsync("Part two");
        await env.LinkAsync(spec, r1, "allocated-to", p1);
        await env.LinkAsync(spec, r1, "allocated-to", p2);
        await env.LinkAsync(spec, r2, "allocated-to", p2);
        var m = (await env.Req.GetMatrixAsync(ReqEnv.Alice, spec, "allocated-to", null, null)).Value!;
        Assert.Equal(2, m.Rows.Count);
        Assert.Equal([$"part:{p1}", $"part:{p2}"], m.Columns.Select(c => c.Id).Order(StringComparer.Ordinal));
        Assert.Equal(3, m.Cells.Count);
        Assert.Equal(OutcomeKind.Invalid, (await env.Req.GetMatrixAsync(ReqEnv.Alice, spec, "nope", null, null)).Kind);
    }

    [Fact]
    public async Task Link_types_are_administrator_data_and_system_types_stay()
    {
        var env = await ReqEnv.CreateAsync(pg);
        var types = await env.Req.ListLinkTypesAsync();
        Assert.Equal(["allocated-to", "changed-by", "derives-from", "refines", "verified-by"], types.Select(t => t.Code).Order(StringComparer.Ordinal));
        var denied = await env.Req.SaveLinkTypeAsync(ReqEnv.Alice, new SaveLinkTypeCommand("tested-on", "Tested on", "Test bench for", "part", "none", true, 60, null), null);
        Assert.Equal(OutcomeKind.Forbidden, denied.Kind);
        var ok = await env.Req.SaveLinkTypeAsync(ReqEnv.Ada, new SaveLinkTypeCommand("tested-on", "Tested on", "Test bench for", "part", "none", true, 60, null), null);
        Assert.True(ok.IsOk, string.Join("; ", ok.Errors));
        Assert.Equal(OutcomeKind.Invalid, (await env.Req.SaveLinkTypeAsync(ReqEnv.Ada, new SaveLinkTypeCommand("allocated-to", "Allocated to", "x", "part", "allocation", false, 30, "no"), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Req.SaveLinkTypeAsync(ReqEnv.Ada, new SaveLinkTypeCommand("tested-on", "Tested on", "x", "part", "none", false, 60, null), null)).Kind); // reason needed
        Assert.True((await env.Req.SaveLinkTypeAsync(ReqEnv.Ada, new SaveLinkTypeCommand("tested-on", "Tested on", "x", "part", "none", false, 60, "No longer used"), null)).IsOk);
        var spec = await env.SpecAsync();
        var key = await env.AddAsync(spec, ReqEnv.Input("One"));
        var part = await env.C.NewPartAsync();
        Assert.Equal(OutcomeKind.Invalid, (await env.Req.AddLinkAsync(ReqEnv.Alice, spec, key, new AddLinkCommand("tested-on", null, null, part), null)).Kind); // retired
        await Assert.ThrowsAnyAsync<PostgresException>(() => env.C.Db.ExecuteAsSuperuserAsync("DELETE FROM req.link_type WHERE code = 'tested-on'"));
    }

    [Fact]
    public async Task Change_impact_lists_the_requirements_that_trace_to_an_affected_part()
    {
        var env = await ReqEnv.CreateAsync(pg);
        var spec = await env.SpecAsync();
        var open = await env.AddAsync(spec, ReqEnv.Input("Depth rating"));
        var secret = await env.AddAsync(spec, ReqEnv.Input("Secret rating", markings: ReqEnv.Itar));
        var part = await env.C.ReleasedComponentAsync("Housing");
        await env.LinkAsync(spec, open, "allocated-to", part);
        await env.LinkAsync(spec, secret, "allocated-to", part);
        Assert.True((await env.Req.RecordVerificationAsync(ReqEnv.Alice, spec, open, new RecordVerificationCommand("pass", null, null, null, null, false, null), null)).IsOk);
        var ecn = await env.C.ReviseWithEffectivityAsync(part, null);

        var report = (await env.Impact.AnalyzeAsync(ReqEnv.Alice, ecn.Number, 3, "corr-impact")).Value!;
        var traced = report.Objects.Single().Requirements!;
        Assert.Equal([open, secret], traced.Select(t => t.Key).Order(StringComparer.Ordinal));
        Assert.Equal(VerificationStates.Verified, traced.Single(t => t.Key == open).VerificationStatus);
        Assert.Equal(2, report.Summary.Requirements);
        Assert.Contains(await env.C.AuditAsync(), e => e.Action == "change.impact" && e.Payload.Contains("requirements", StringComparison.Ordinal));
    }
}
