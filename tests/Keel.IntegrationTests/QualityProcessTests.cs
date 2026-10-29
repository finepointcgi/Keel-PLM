using Keel.Audit;
using Keel.Core;
using Keel.Notifications;
using Keel.Quality;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Nonconformance reports, corrective actions and deviations through their whole process with the real workflow engine and e-signatures (8.1, ADR-0036).</summary>
[Collection(PostgresCollection.Name)]
public sealed class QualityProcessTests(PostgresFixture pg)
{
    private static Subject Alice => QualityEnv.Alice;

    // ================================================================ NCR

    [Fact]
    public async Task An_NCR_goes_from_report_to_signed_disposition_to_closed()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();
        var ncr = await env.NewNcrAsync(Alice, part, supplier: "SUP-OPAQUE-1");
        Assert.Equal(("NCR-000001", "Open", "A", 5m), (ncr.Number, ncr.State, ncr.PartRevision, ncr.Quantity));
        Assert.Equal("SUP-OPAQUE-1", ncr.SupplierRef); // an opaque reference, kept as typed

        // containment first: bob owns it and hears about it
        var action = await env.AddActionAsync(Alice, "ncr", ncr.Number, "Containment", "bob");
        Assert.Equal("ACT-000001", action.Number);
        var inbox = await env.Center.ListAsync(QualityEnv.Bob, NotificationFilter.Tasks, 0, 20);
        Assert.Contains(inbox.Items, i => i.Number == ncr.Number && !i.Restricted);

        // the disposition is proposed, then signed by the board
        Assert.Equal(OutcomeKind.Conflict, (await env.TrySubmitAsync("ncr", ncr.Number)).Kind); // nothing proposed yet
        var proposed = await env.Ncrs.ProposeDispositionAsync(Alice, ncr.Number, new ProposeDispositionCommand("Rework", "Deburr within drawing limits"), "c1");
        Assert.True(proposed.IsOk, string.Join("; ", proposed.Errors));
        var started = await env.SubmitAsync("ncr", ncr.Number);
        Assert.Equal("MRB review", (await env.Ncrs.GetAsync(Alice, ncr.Number, null)).Value!.StateLabel);

        // while the board reviews nothing about the report can change
        var edit = await env.Ncrs.UpdateAsync(Alice, ncr.Number, new UpdateNcrCommand("Changed", null, "Minor", "Final", null, null, null, 1, "EA"), null);
        Assert.Equal(OutcomeKind.Conflict, edit.Kind);
        var direct = await Assert.ThrowsAsync<PostgresException>(() => env.AppSqlAsync("UPDATE qual.ncr SET title = 'Tampered' WHERE number = @n", ("n", ncr.Number)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, direct.SqlState);

        var final = await env.ApproveAllAsync(started);
        Assert.Equal("Approved", final.Status);
        var done = (await env.Ncrs.GetAsync(Alice, ncr.Number, null)).Value!;
        Assert.Equal(("Dispositioned", "Rework", "Rework"), (done.State, done.Disposition, done.ProposedDisposition));

        // closing needs the containment finished and a note on how the disposition was carried out
        Assert.Equal(OutcomeKind.Conflict, (await env.Ncrs.CloseAsync(Alice, ncr.Number, "Reworked and re-inspected", null)).Kind);
        await env.FinishActionAsync(QualityEnv.Bob, "ncr", ncr.Number, action.Number);
        Assert.Equal(OutcomeKind.Invalid, (await env.Ncrs.CloseAsync(Alice, ncr.Number, " ", null)).Kind);
        var closed = await env.Ncrs.CloseAsync(Alice, ncr.Number, "Reworked and re-inspected", null);
        Assert.True(closed.IsOk, string.Join("; ", closed.Errors));
        Assert.Equal("Closed", closed.Value!.State);
        Assert.Contains(closed.Value.History, h => h.Action == "Dispositioned");

        // audited: the whole chain verifies and no event carries the defect text or the supplier reference
        var verdict = await env.VerifyAuditChainAsync();
        Assert.True(verdict.Valid, string.Join("; ", verdict.Failures));
        var events = (await env.AuditAsync()).Where(e => e.ObjectType == "ncr").ToList();
        Assert.Contains(events, e => e.Action == "ncr.create");
        Assert.Contains(events, e => e.Action == "ncr.disposition_approved");
        Assert.Contains(events, e => e.Action == "ncr.close");
        Assert.DoesNotContain(events, e => e.Payload.Contains("Synthetic defect description", StringComparison.Ordinal) || e.Payload.Contains("SUP-OPAQUE-1", StringComparison.Ordinal)
            || e.Payload.Contains("Burr on mounting face", StringComparison.Ordinal) || e.Payload.Contains("Deburr within", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_disposition_cannot_be_reached_without_the_signatures_not_even_by_SQL()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();
        var ncr = await env.NewNcrAsync(Alice, part);
        Assert.True((await env.Ncrs.ProposeDispositionAsync(Alice, ncr.Number, new ProposeDispositionCommand("Scrap", "Beyond repair"), null)).IsOk);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => env.AppSqlAsync("UPDATE qual.ncr SET lifecycle_state = 'Dispositioned', disposition = 'Scrap' WHERE number = @n", ("n", ncr.Number)));
        Assert.Contains("no transition", ex.MessageText, StringComparison.Ordinal);
        var started = await env.SubmitAsync("ncr", ncr.Number);
        // Under review the only way on is the review itself: a direct step without a signature is refused by the database guard.
        var direct = await Assert.ThrowsAsync<PostgresException>(() => env.AppSqlAsync("UPDATE qual.ncr SET lifecycle_state = 'Dispositioned', disposition = 'Scrap' WHERE number = @n", ("n", ncr.Number)));
        Assert.Contains("approving signature", direct.MessageText, StringComparison.Ordinal);
        Assert.Equal("UnderReview", await env.TextAsync($"SELECT lifecycle_state FROM qual.ncr WHERE number = '{ncr.Number}'"));
        Assert.Equal("Active", started.Status);
    }

    [Fact]
    public async Task A_rejected_review_returns_the_report_for_rework_and_a_new_submission_is_signed_afresh()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();
        var ncr = await env.NewNcrAsync(Alice, part);
        Assert.True((await env.Ncrs.ProposeDispositionAsync(Alice, ncr.Number, new ProposeDispositionCommand("UseAsIs", "Within tolerance"), null)).IsOk);
        var v = await env.SubmitAsync("ncr", ncr.Number);
        v = await env.SignAsync(QualityEnv.Rita, v.Tasks.Single(t => t.Status == "Open").Id, "Reviewed");
        v = await env.SignAsync(QualityEnv.Bob, WorkflowEnv.TaskOf(v, "bob"), "Rejected", "Not within tolerance");
        Assert.Equal("Rejected", v.Status);
        var back = (await env.Ncrs.GetAsync(Alice, ncr.Number, null)).Value!;
        Assert.Equal(("Open", null), (back.State, back.Disposition));
        Assert.True(back.Permissions.Editable);
        Assert.True((await env.Ncrs.ProposeDispositionAsync(Alice, ncr.Number, new ProposeDispositionCommand("Repair", "Repair per procedure"), null)).IsOk);
        var again = await env.ApproveAllAsync(await env.SubmitAsync("ncr", ncr.Number));
        Assert.Equal("Approved", again.Status);
        Assert.Equal("Repair", (await env.Ncrs.GetAsync(Alice, ncr.Number, null)).Value!.Disposition);
        Assert.Equal(2, int.Parse(await env.TextAsync($"SELECT count(*) FROM qual.submission WHERE object_id = (SELECT id FROM qual.ncr WHERE number = '{ncr.Number}')"), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task The_rules_of_a_report_are_enforced()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();

        // the part must exist and the report must cover its markings
        var missing = await env.Ncrs.CreateAsync(Alice, new CreateNcrCommand("T", null, "Minor", "Final", "P-999999", null, null, null, null, 1, "EA", QualityEnv.Itar()), null);
        Assert.Equal(OutcomeKind.Invalid, missing.Kind);
        var low = await env.Ncrs.CreateAsync(Alice, new CreateNcrCommand("T", null, "Minor", "Final", part, null, null, null, null, 1, "EA", QualityEnv.Open), null);
        Assert.Equal(OutcomeKind.Invalid, low.Kind);
        Assert.Contains("must cover", low.Errors[0], StringComparison.Ordinal);
        var qty = await env.Ncrs.CreateAsync(Alice, new CreateNcrCommand("T", null, "Minor", "Final", part, null, null, null, null, 0, "EA", QualityEnv.Itar()), null);
        Assert.Equal(OutcomeKind.Invalid, qty.Kind);
        var sev = await env.Ncrs.CreateAsync(Alice, new CreateNcrCommand("T", null, "Dire", "Final", part, null, null, null, null, 1, "EA", QualityEnv.Itar()), null);
        Assert.Equal(OutcomeKind.Invalid, sev.Kind);
        // a viewer cannot raise one
        var viewer = await env.Ncrs.CreateAsync(QualityEnv.Mallory, new CreateNcrCommand("T", null, "Minor", "Final", part, null, null, null, null, 1, "EA", QualityEnv.Itar()), null);
        Assert.Equal(OutcomeKind.Forbidden, viewer.Kind);

        var ncr = await env.NewNcrAsync(Alice, part);
        // an action owner must be able to read the record
        var eveOwner = await env.Actions.AddAsync(Alice, "ncr", ncr.Number, new CreateActionCommand("Containment", "Quarantine", "eve", null), null);
        Assert.Equal(OutcomeKind.Invalid, eveOwner.Kind);
        Assert.Contains("cannot read", eveOwner.Errors[0], StringComparison.Ordinal);
        var past = await env.Actions.AddAsync(Alice, "ncr", ncr.Number, new CreateActionCommand("Containment", "Quarantine", "bob", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2))), null);
        Assert.Equal(OutcomeKind.Invalid, past.Kind);
        var wrongType = await env.Actions.AddAsync(Alice, "ncr", ncr.Number, new CreateActionCommand("Corrective", "Quarantine", "bob", null), null);
        Assert.Equal(OutcomeKind.Invalid, wrongType.Kind);

        // only the owner (or an administrator) completes an action; completion needs a note
        var a = await env.AddActionAsync(Alice, "ncr", ncr.Number, "Containment", "bob");
        Assert.Equal(OutcomeKind.Forbidden, (await env.Actions.CompleteAsync(QualityEnv.Carol, "ncr", ncr.Number, a.Number, "Done", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Actions.CompleteAsync(QualityEnv.Bob, "ncr", ncr.Number, a.Number, " ", null)).Kind);
        Assert.True((await env.Actions.CompleteAsync(QualityEnv.Ada, "ncr", ncr.Number, a.Number, "Done by the administrator", null)).IsOk);

        // only an open report is cancelled, with a reason
        Assert.Equal(OutcomeKind.Invalid, (await env.Ncrs.CancelAsync(Alice, ncr.Number, "", null)).Kind);
        var cancelled = await env.Ncrs.CancelAsync(Alice, ncr.Number, "Raised against the wrong part", null);
        Assert.Equal("Cancelled", cancelled.Value!.State);
        Assert.True(cancelled.Value.IsFinal);
        Assert.Equal(OutcomeKind.Conflict, (await env.Actions.AddAsync(Alice, "ncr", ncr.Number, new CreateActionCommand("Containment", "More", "bob", null), null)).Kind);
    }

    [Fact]
    public async Task Markings_of_a_report_inherit_enforce_and_change_only_with_a_reason()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();
        var ncr = await env.NewNcrAsync(Alice, part);
        var open = await env.Ncrs.ChangeMarkingsAsync(Alice, ncr.Number, QualityEnv.Open, "Lower them", null);
        Assert.Equal(OutcomeKind.Invalid, open.Kind); // would not cover the part
        Assert.Equal(OutcomeKind.Invalid, (await env.Ncrs.ChangeMarkingsAsync(Alice, ncr.Number, QualityEnv.Itar("KRAKEN"), " ", null)).Kind);
        // a change that still covers the part and keeps the actor able to read the record works, is audited with its reason and shows in the history
        var cui = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT", "CTI"], controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;
        var raised = await env.Ncrs.ChangeMarkingsAsync(Alice, ncr.Number, cui, "Defect text mentions a technique", "c-m");
        Assert.True(raised.IsOk, string.Join("; ", raised.Errors));
        Assert.Contains(raised.Value!.History, h => h.Action == "Markings changed" && h.Detail!.Contains("Defect text mentions", StringComparison.Ordinal));
        Assert.Contains((await env.AuditAsync()), e => e.Action == "ncr.markings_change" && e.Payload.Contains("Defect text mentions", StringComparison.Ordinal)); // the reason is process text
    }

    // ================================================================ CAPA

    [Fact]
    public async Task A_CAPA_closes_only_through_a_signed_verification_of_an_effective_result()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();
        var ncr = await env.NewNcrAsync(Alice, part);
        var capa = await env.NewCapaAsync(Alice, owner: "rita");
        Assert.Equal(("CAPA-000001", "rita"), (capa.Number, capa.OwnerUsername));

        // links to a report, a part and a change
        var change = await env.Base.NewEcnAsync();
        Assert.True((await env.Actions.AddLinkAsync(Alice, "capa", capa.Number, "ncr", ncr.Number, null)).IsOk);
        Assert.True((await env.Actions.AddLinkAsync(Alice, "capa", capa.Number, "part", part, null)).IsOk);
        Assert.True((await env.Actions.AddLinkAsync(Alice, "capa", capa.Number, "change", change.Number, null)).IsOk);
        Assert.Equal(OutcomeKind.Conflict, (await env.Actions.AddLinkAsync(Alice, "capa", capa.Number, "ncr", ncr.Number, null)).Kind);
        var linked = (await env.Capas.GetAsync(Alice, capa.Number, null)).Value!;
        Assert.Equal(3, linked.Links.Count(l => !l.Redacted && l.Direction == "out"));
        var fromNcr = (await env.Ncrs.GetAsync(Alice, ncr.Number, null)).Value!;
        Assert.Contains(fromNcr.Links, l => l.Direction == "in" && l.Key == capa.Number); // the report sees who links to it

        // starting needs an action; the analysis is structured
        Assert.Equal(OutcomeKind.Conflict, (await env.Capas.StartAsync(Alice, capa.Number, null)).Kind);
        var act = await env.AddActionAsync(Alice, "capa", capa.Number, "Corrective", "rita", "Change the deburr step");
        Assert.True((await env.Capas.StartAsync(Alice, capa.Number, null)).IsOk);
        Assert.Equal(OutcomeKind.Invalid, (await env.Capas.SaveAnalysisAsync(Alice, capa.Number, new SaveAnalysisCommand("x", Enumerable.Range(1, 9).Select(i => new WhyInput("why " + i, null)).ToList(), []), null)).Kind);
        var analysis = await env.Capas.SaveAnalysisAsync(Alice, capa.Number,
            new SaveAnalysisCommand("The deburr tool wore past its limit",
                [new WhyInput("The burr was left on", "Photos"), new WhyInput("The tool was worn", null), new WhyInput("Tool life was not tracked", null)],
                [new CauseInput("Machine", "Worn tool", true), new CauseInput("Method", "No tool-life check", false)]), "c");
        Assert.True(analysis.IsOk, string.Join("; ", analysis.Errors));
        Assert.Equal((3, 2), (analysis.Value!.Whys.Count, analysis.Value.Causes.Count));

        // not ready: the action is open, no plan, no result
        Assert.Equal(OutcomeKind.Conflict, (await env.TrySubmitAsync("capa", capa.Number)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Capas.RecordVerificationAsync(Alice, capa.Number, new RecordVerificationCommand("Looks fine", true), null)).Kind);
        await env.FinishActionAsync(QualityEnv.Rita, "capa", capa.Number, act.Number);
        var plan = await env.Capas.SaveVerificationPlanAsync(Alice, capa.Number, new SaveVerificationPlanCommand("Inspect the next 3 lots", "No burr in 3 lots", null), null);
        Assert.True(plan.IsOk, string.Join("; ", plan.Errors));

        // an ineffective result keeps the record open
        Assert.True((await env.Capas.RecordVerificationAsync(Alice, capa.Number, new RecordVerificationCommand("Burr found in lot 2", false), null)).IsOk);
        var refused = await env.TrySubmitAsync("capa", capa.Number);
        Assert.Equal(OutcomeKind.Conflict, refused.Kind);
        Assert.Contains("effective", refused.Errors[0], StringComparison.Ordinal);

        // an effective result goes to the signed review; nothing else can close the record
        Assert.True((await env.Capas.RecordVerificationAsync(Alice, capa.Number, new RecordVerificationCommand("No burr in 3 lots", true), null)).IsOk);
        var direct = await Assert.ThrowsAsync<PostgresException>(() => env.AppSqlAsync("UPDATE qual.capa SET lifecycle_state = 'Closed' WHERE number = @n", ("n", capa.Number)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, direct.SqlState);
        var started = await env.SubmitAsync("capa", capa.Number);
        Assert.Equal("Verification", (await env.Capas.GetAsync(Alice, capa.Number, null)).Value!.StateLabel);
        var late = await Assert.ThrowsAsync<PostgresException>(() => env.AppSqlAsync("UPDATE qual.capa SET lifecycle_state = 'Closed' WHERE number = @n", ("n", capa.Number)));
        Assert.Contains("approving signature", late.MessageText, StringComparison.Ordinal);
        var final = await env.ApproveAllAsync(started);
        Assert.Equal("Approved", final.Status);
        var closed = (await env.Capas.GetAsync(Alice, capa.Number, null)).Value!;
        Assert.Equal(("Closed", true), (closed.State, closed.IsFinal));
        Assert.Contains(closed.History, h => h.Action == "Closed");
        Assert.Equal(OutcomeKind.Conflict, (await env.Capas.UpdateAsync(Alice, capa.Number, new UpdateCapaCommand("T", null, "Corrective", "Ncr", "Low", "rita", null), null)).Kind);

        var events = (await env.AuditAsync()).Where(e => e.ObjectType == "capa").ToList();
        Assert.Contains(events, e => e.Action == "capa.close");
        Assert.DoesNotContain(events, e => e.Payload.Contains("deburr tool wore", StringComparison.Ordinal) || e.Payload.Contains("No burr in 3 lots", StringComparison.Ordinal) || e.Payload.Contains("Worn tool", StringComparison.Ordinal));
        Assert.True((await env.VerifyAuditChainAsync()).Valid);
    }

    [Fact]
    public async Task A_CAPA_with_a_signed_closure_shows_who_signed_what_was_verified()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var capa = await env.NewCapaAsync(Alice, owner: "rita");
        var act = await env.AddActionAsync(Alice, "capa", capa.Number, "Preventive", "rita");
        Assert.True((await env.Capas.StartAsync(Alice, capa.Number, null)).IsOk);
        Assert.True((await env.Capas.SaveAnalysisAsync(Alice, capa.Number, new SaveAnalysisCommand("Root", [new WhyInput("Because", null)], []), null)).IsOk);
        await env.FinishActionAsync(QualityEnv.Rita, "capa", capa.Number, act.Number);
        Assert.True((await env.Capas.SaveVerificationPlanAsync(Alice, capa.Number, new SaveVerificationPlanCommand("Audit", "Pass", null), null)).IsOk);
        Assert.True((await env.Capas.RecordVerificationAsync(Alice, capa.Number, new RecordVerificationCommand("Passed", true), null)).IsOk);
        var started = await env.SubmitAsync("capa", capa.Number);
        var content = await env.Flow.GetTaskAsync(QualityEnv.Bob, started.Tasks.First(t => t.AssigneeUsername == "bob").Id, null);
        Assert.True(content.IsOk);
        var final = await env.ApproveAllAsync(started);
        var verify = await env.Flow.VerifyInstanceAsync(Alice, final.Id, null);
        Assert.True(verify.IsOk);
        Assert.All(verify.Value!, v => Assert.True(v.Valid, string.Join("; ", v.Problems)));
        // tampering with the stored snapshot (as the owner of the database) is detected
        await using (var conn = new NpgsqlConnection(env.Db.SuperuserConnectionString))
        {
            await conn.OpenAsync();
            await using var off = new NpgsqlCommand("ALTER TABLE qual.submission DISABLE TRIGGER submission_immutable", conn);
            await off.ExecuteNonQueryAsync();
            await using var tamper = new NpgsqlCommand("UPDATE qual.submission SET snapshot = replace(snapshot, 'Passed', 'Faked')", conn);
            await tamper.ExecuteNonQueryAsync();
        }
        var after = await env.Flow.VerifyInstanceAsync(Alice, final.Id, null);
        Assert.All(after.Value!, v => Assert.False(v.Valid));
    }

    // ================================================================ deviations

    [Fact]
    public async Task A_deviation_is_approved_through_signatures_and_then_applies_to_the_part()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();
        var dev = await env.NewDeviationAsync(Alice);
        Assert.Equal(("DEV-000001", "Draft"), (dev.Number, dev.State));
        Assert.Equal(OutcomeKind.Conflict, (await env.TrySubmitAsync("deviation", dev.Number)).Kind); // no scope yet

        await env.AddScopeAsync(Alice, dev.Number, part, from: 100, to: 120, lots: ["L-1"], qty: 10);
        var dup = await env.Deviations.AddScopeAsync(Alice, dev.Number, new ScopeInput(part, null, null, null, null, null, null), null);
        Assert.Equal(OutcomeKind.Conflict, dup.Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Deviations.AddScopeAsync(Alice, dev.Number, new ScopeInput("P-999999", null, null, null, null, null, null), null)).Kind);
        var lowMarkings = await env.NewDeviationAsync(Alice, QualityEnv.Open, "Open deviation");
        var cover = await env.Deviations.AddScopeAsync(Alice, lowMarkings.Number, new ScopeInput(part, null, null, null, null, null, null), null);
        Assert.Equal(OutcomeKind.Invalid, cover.Kind);
        Assert.Contains("must cover", cover.Errors[0], StringComparison.Ordinal);

        // not in force before approval
        var before = await env.Deviations.ForPartAsync(Alice, part, includeInactive: false, null, null, null, null);
        Assert.Empty(before.Value!);
        var started = await env.SubmitAsync("deviation", dev.Number);
        Assert.Equal(OutcomeKind.Conflict, (await env.Deviations.UpdateAsync(Alice, dev.Number, new UpdateDeviationCommand("Deviation", "T", null, null, false, dev.ValidFrom, dev.ValidTo), null)).Kind); // locked in review
        var final = await env.ApproveAllAsync(started);
        Assert.Equal("Approved", final.Status);
        var approved = (await env.Deviations.GetAsync(Alice, dev.Number, null)).Value!;
        Assert.Equal(("Approved", true), (approved.State, approved.IsActive));

        var onPage = (await env.Deviations.ForPartAsync(Alice, part, false, null, null, null, null)).Value!;
        var row = Assert.Single(onPage);
        Assert.Equal((dev.Number, true), (row.Number, row.Active));
        Assert.Contains("serials 100 to 120", row.Applicability, StringComparison.Ordinal);
        // applicability of single units
        Assert.Single((await env.Deviations.ForPartAsync(Alice, part, false, "110", "L-1", null, null)).Value!);
        Assert.Empty((await env.Deviations.ForPartAsync(Alice, part, false, "130", "L-1", null, null)).Value!);
        Assert.Empty((await env.Deviations.ForPartAsync(Alice, part, false, "110", "L-9", null, null)).Value!);
        Assert.Empty((await env.Deviations.ForPartAsync(Alice, part, false, "110", "L-1", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(90)), null)).Value!); // after the window

        // the part's timeline and the quality summary name it
        var summary = (await env.Queries.ForPartAsync(Alice, part, false, null)).Value!;
        Assert.Single(summary.Deviations);
        var timeline = await new QualityTimelineSourceProbe(env.Ds).GetAsync(Alice, part);
        Assert.Contains(timeline, t => t.Title.Contains(dev.Number, StringComparison.Ordinal));
        Assert.True((await env.VerifyAuditChainAsync()).Valid);
    }

    [Fact]
    public async Task A_deviation_that_needs_the_customer_waits_for_the_recorded_approval()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();
        var dev = await env.NewDeviationAsync(Alice, customer: true, kind: "Waiver");
        await env.AddScopeAsync(Alice, dev.Number, part);
        var final = await env.ApproveAllAsync(await env.SubmitAsync("deviation", dev.Number));
        Assert.Equal("Approved", final.Status);
        var waiting = (await env.Deviations.GetAsync(Alice, dev.Number, null)).Value!;
        Assert.Equal(("AwaitingCustomer", false), (waiting.State, waiting.IsActive)); // not in force until the customer approved
        Assert.Empty((await env.Deviations.ForPartAsync(Alice, part, false, null, null, null, null)).Value!);

        Assert.Equal(OutcomeKind.Forbidden, (await env.Deviations.RecordCustomerApprovalAsync(Alice, dev.Number, new CustomerApprovalCommand("LTR-9", DateOnly.FromDateTime(DateTime.UtcNow)), null)).Kind); // approver role
        Assert.Equal(OutcomeKind.Invalid, (await env.Deviations.RecordCustomerApprovalAsync(QualityEnv.Bob, dev.Number, new CustomerApprovalCommand(" ", DateOnly.FromDateTime(DateTime.UtcNow)), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Deviations.RecordCustomerApprovalAsync(QualityEnv.Bob, dev.Number, new CustomerApprovalCommand("LTR-9", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3))), null)).Kind);
        var ok = await env.Deviations.RecordCustomerApprovalAsync(QualityEnv.Bob, dev.Number, new CustomerApprovalCommand("LTR-9", DateOnly.FromDateTime(DateTime.UtcNow)), null);
        Assert.True(ok.IsOk, string.Join("; ", ok.Errors));
        Assert.Equal(("Approved", true, "LTR-9"), (ok.Value!.State, ok.Value.IsActive, ok.Value.CustomerApprovalRef));
        Assert.Single((await env.Deviations.ForPartAsync(Alice, part, false, null, null, null, null)).Value!);

        // revoking needs an approver and a reason
        Assert.Equal(OutcomeKind.Forbidden, (await env.Deviations.RevokeAsync(Alice, dev.Number, "No longer justified", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Deviations.RevokeAsync(QualityEnv.Bob, dev.Number, "", null)).Kind);
        var revoked = await env.Deviations.RevokeAsync(QualityEnv.Bob, dev.Number, "No longer justified", null);
        Assert.Equal(("Revoked", false), (revoked.Value!.State, revoked.Value.IsActive));
        Assert.Empty((await env.Deviations.ForPartAsync(Alice, part, false, null, null, null, null)).Value!);
        Assert.Single((await env.Deviations.ForPartAsync(Alice, part, true, null, null, null, null)).Value!); // history on request
    }

    [Fact]
    public async Task A_window_in_the_future_is_scheduled_not_active_and_the_part_page_says_so()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();
        var dev = await env.NewDeviationAsync(Alice, fromDays: 10, toDays: 40);
        await env.AddScopeAsync(Alice, dev.Number, part);
        await env.ApproveAllAsync(await env.SubmitAsync("deviation", dev.Number));
        var detail = (await env.Deviations.GetAsync(Alice, dev.Number, null)).Value!;
        Assert.Equal(("Approved", false), (detail.State, detail.IsActive));
        var row = Assert.Single((await env.Deviations.ForPartAsync(Alice, part, false, null, null, null, null)).Value!);
        Assert.False(row.Active);
        env.Clock.Advance(TimeSpan.FromDays(12));
        Assert.True(Assert.Single((await env.Deviations.ForPartAsync(Alice, part, false, null, null, null, null)).Value!).Active);
    }

    [Fact]
    public async Task An_approved_window_that_ends_expires_by_itself_with_audit_and_notifications_exactly_once()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();
        var dev = await env.NewDeviationAsync(Alice, toDays: 20);
        await env.AddScopeAsync(Alice, dev.Number, part);
        await env.ApproveAllAsync(await env.SubmitAsync("deviation", dev.Number));

        var quiet = await env.Q.Maintenance.RunOnceAsync();
        Assert.Equal(new MaintenanceReport(0, 0, 0), quiet); // 20 days left, warning starts at 14

        env.Clock.Advance(TimeSpan.FromDays(8)); // 12 days left
        var warn = await env.Q.Maintenance.RunOnceAsync();
        Assert.Equal(new MaintenanceReport(0, 1, 0), warn);
        Assert.Equal(new MaintenanceReport(0, 0, 0), await env.Q.Maintenance.RunOnceAsync()); // announced once
        Assert.Contains((await env.Center.ListAsync(Alice, NotificationFilter.Watching, 0, 20)).Items, i => i.Number == dev.Number);

        env.Clock.Advance(TimeSpan.FromDays(14)); // ended
        var expired = await env.Q.Maintenance.RunOnceAsync();
        Assert.Equal(new MaintenanceReport(1, 0, 0), expired);
        Assert.Equal(new MaintenanceReport(0, 0, 0), await env.Q.Maintenance.RunOnceAsync());
        var detail = (await env.Deviations.GetAsync(Alice, dev.Number, null)).Value!;
        Assert.Equal(("Expired", false, true), (detail.State, detail.IsActive, detail.IsFinal));
        Assert.Empty((await env.Deviations.ForPartAsync(Alice, part, false, null, null, null, null)).Value!);
        Assert.Contains(detail.History, h => h.Action == "Expired" && h.Actor == "Keel system");

        var events = (await env.AuditAsync()).Where(e => e.ObjectId == dev.Number).ToList();
        var expire = Assert.Single(events, e => e.Action == "deviation.expire");
        Assert.Equal("system:keel", expire.Actor);
        Assert.Single(events, e => e.Action == "deviation.expiry_warning");
        Assert.DoesNotContain(events, e => e.Payload.Contains("Alternate finish", StringComparison.Ordinal));
        var notices = (await env.Center.ListAsync(Alice, NotificationFilter.All, 0, 50)).Items.Where(i => i.Number == dev.Number).ToList();
        Assert.True(notices.Count >= 2); // ends soon, expired (each once)
        Assert.Equal(1, await CountNotificationsAsync(env, "deviation_expired", dev.Number, "sub-alice"));
        Assert.True((await env.VerifyAuditChainAsync()).Valid);
    }

    [Fact]
    public async Task An_action_past_its_due_date_is_reported_once_to_its_owner()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();
        var ncr = await env.NewNcrAsync(Alice, part);
        var due = DateOnly.FromDateTime(env.Clock.GetUtcNow().UtcDateTime).AddDays(3);
        var a = await env.AddActionAsync(Alice, "ncr", ncr.Number, "Containment", "bob", due: due);
        Assert.Equal(new MaintenanceReport(0, 0, 0), await env.Q.Maintenance.RunOnceAsync());
        env.Clock.Advance(TimeSpan.FromDays(5));
        var detail = (await env.Ncrs.GetAsync(Alice, ncr.Number, null)).Value!;
        Assert.True(detail.Containment.Single().Overdue);
        Assert.Equal(new MaintenanceReport(0, 0, 1), await env.Q.Maintenance.RunOnceAsync());
        Assert.Equal(new MaintenanceReport(0, 0, 0), await env.Q.Maintenance.RunOnceAsync());
        Assert.Equal(1, await CountNotificationsAsync(env, "quality_action_overdue", ncr.Number, "sub-bob"));
        var dash = await env.Queries.DashboardAsync(Alice, null);
        Assert.Equal(1, dash.OverdueActions);
        await env.FinishActionAsync(QualityEnv.Bob, "ncr", ncr.Number, a.Number);
        Assert.Equal(0, (await env.Queries.DashboardAsync(Alice, null)).OverdueActions);
    }

    [Fact]
    public async Task Quality_records_join_the_activity_feed_comments_and_watch_lists_without_leaking_text()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();
        var ncr = await env.NewNcrAsync(Alice, part);
        await env.AddActionAsync(Alice, "ncr", ncr.Number, "Containment", "bob");
        var feed = new ActivityService(env.Ds, env.Objects, env.Audit);
        var page = await feed.GetAsync(QualityEnv.Rita, "ncr", ncr.Number, null, 50, false, null);
        Assert.True(page.IsOk);
        Assert.Contains(page.Value!.Entries, e => e.Sentence.Contains("created the nonconformance report", StringComparison.Ordinal));
        Assert.Contains(page.Value.Entries, e => e.Sentence.Contains("added an action item", StringComparison.Ordinal));
        Assert.DoesNotContain(page.Value.Entries, e => e.Sentence.Contains("ncr.", StringComparison.Ordinal) || e.Details.Any(d => d.Value.Contains("Synthetic defect", StringComparison.Ordinal)));
        Assert.Equal(OutcomeKind.NotFound, (await feed.GetAsync(QualityEnv.Eve, "ncr", ncr.Number, null, 50, false, null)).Kind);

        var comments = new CommentService(env.Ds, env.Objects, env.Audit, env.NotificationOptions, env.Writer, env.Clock);
        var posted = await comments.PostAsync(QualityEnv.Rita, "ncr", ncr.Number, $"Please also check {part} @alice", null, null);
        Assert.True(posted.IsOk, string.Join("; ", posted.Errors));
        Assert.Equal(1, await comments.CountAsync(QualityEnv.Rita, "ncr", ncr.Number));
        Assert.Null(await comments.CountAsync(QualityEnv.Eve, "ncr", ncr.Number));
        Assert.Contains((await env.Center.ListAsync(Alice, NotificationFilter.Mentions, 0, 10)).Items, i => i.Number == ncr.Number);

        var watches = new WatchService(env.Ds, env.Objects, env.Audit, env.NotificationOptions, env.Clock);
        Assert.Contains((await watches.ListAsync(Alice)), w => w.Number == ncr.Number); // the reporter watches what she creates
        Assert.DoesNotContain((await watches.ListAsync(QualityEnv.Eve)), w => w.Number == ncr.Number);
    }

    [Fact]
    public async Task The_awareness_constraints_are_widened_by_pattern_so_the_order_with_other_migrations_does_not_matter()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "db", "migrations", "0019_quality.sql"));
        var start = sql.LastIndexOf("DO $$", StringComparison.Ordinal);
        var block = sql[start..(sql.IndexOf("$$;", start + 6, StringComparison.Ordinal) + 3)];
        await using var conn = new NpgsqlConnection(env.Db.SuperuserConnectionString);
        await conn.OpenAsync();
        async Task Exec(string text) { await using var c = new NpgsqlCommand(text, conn); await c.ExecuteNonQueryAsync(); }
        async Task<string> Def() { await using var c = new NpgsqlCommand("SELECT string_agg(pg_get_constraintdef(oid), ' | ') FROM pg_constraint WHERE conrelid = 'ntf.watch'::regclass AND contype = 'c' AND pg_get_constraintdef(oid) LIKE '%object_type%'", conn); return (string)(await c.ExecuteScalarAsync())!; }

        var applied = await Def();
        Assert.Contains("^[a-z]", applied, StringComparison.Ordinal); // already a pattern after the migrations ran
        await Exec(block); // running it again changes nothing (idempotent)
        Assert.Equal(applied, await Def());

        // an older database whose constraint still lists the first five kinds is widened by the same block
        await Exec("ALTER TABLE ntf.watch DROP CONSTRAINT watch_object_type_known; ALTER TABLE ntf.watch ADD CONSTRAINT legacy_kinds CHECK (object_type IN ('part', 'document', 'change', 'baseline', 'mbom'))");
        await Exec(block);
        Assert.Contains("^[a-z]", await Def(), StringComparison.Ordinal);
        await Exec("INSERT INTO ntf.watch (id, user_sub, object_type, object_number, level, created_at) VALUES (gen_random_uuid(), 'x', 'ncr', 'NCR-000009', 'all', now())");
    }

    private static async Task<long> CountNotificationsAsync(QualityEnv env, string kind, string number, string recipient)
    {
        await using var cmd = env.Ds.CreateCommand("SELECT count(*) FROM ntf.notification WHERE kind = @k AND object_number = @n AND recipient_sub = @r");
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("n", number);
        cmd.Parameters.AddWithValue("r", recipient);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}

/// <summary>Calls the part timeline source of the module (internal) with the signed-in test user.</summary>
internal sealed class QualityTimelineSourceProbe(NpgsqlDataSource ds)
{
    public async Task<IReadOnlyList<TimelineEvent>> GetAsync(Subject subject, string part) =>
        (await new QualityTimelineSource(ds).GetAsync(subject, "part", part, null, CancellationToken.None)).Events;
}
