using Keel.Audit;
using Keel.Change;
using Keel.Core;
using Keel.Security;

namespace Keel.IntegrationTests;

/// <summary>Access control for change objects: marking dominance, redaction, filtered reads, audited denials (ADR-0004, ADR-0019).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ChangeAccessTests(PostgresFixture pg)
{
    private static Subject Alice => ChangeEnv.Alice;

    private static Markings WithCti() => Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT", "CTI"],
        controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;

    [Fact]
    public async Task A_change_must_cover_the_markings_of_every_object_it_lists_and_may_raise_them_only_when_asked()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync(markings: ChangeEnv.Itar());
        var ecn = (await env.NewChangeAsync(Alice, ChangeTypes.Ecn, "Low markings", ChangeEnv.Open, ChangeClasses.II)).Number;

        // Refused by default, with the markings that would be needed.
        var refused = await env.Change.AddAffectedAsync(Alice, ecn, new AddAffectedCommand("part", part, "revise"), null);
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        Assert.Contains("do not cover", refused.Errors[0], StringComparison.Ordinal);
        Assert.Contains("CUI//EXPT", refused.Errors[0], StringComparison.Ordinal);
        Assert.Equal("Draft", (await env.ChangeAsync(ecn)).State);
        Assert.Empty((await env.ChangeAsync(ecn)).Affected);
        Assert.Equal("A", (await env.PartAsync(part)).Revision.Revision); // nothing was revised on the way

        // Raising is explicit, minimal, visible (notice, history) and audited.
        var raised = await env.Change.AddAffectedAsync(Alice, ecn, new AddAffectedCommand("part", part, "revise", null, RaiseMarkings: true), "corr-raise");
        Assert.True(raised.IsOk, string.Join("; ", raised.Errors));
        var notice = Assert.Single(raised.Value!.Notices);
        Assert.Contains("were raised from UNCLASSIFIED", notice, StringComparison.Ordinal);
        var m = raised.Value.Markings;
        Assert.Equal((ExportJurisdiction.Itar, "XI(d)", DistributionStatement.D, "ORCA"), (m.ExportJurisdiction, m.UsmlCategory, m.DistributionStatement, m.Program));
        Assert.Equal(["EXPT"], m.CuiCategories);
        Assert.True(MarkingDominance.Covers(m, ChangeEnv.Itar()));
        Assert.Contains(raised.Value.History, h => h.Action == "Markings raised");
        var audit = await env.AuditAsync();
        var event_ = Assert.Single(audit, e => e.Action == "change.markings_raise");
        Assert.Contains(part, event_.Payload, StringComparison.Ordinal);

        // The markings of a change can be changed only while covering what it lists, and with a reason.
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.ChangeMarkingsAsync(Alice, ecn, ChangeEnv.Open, "lower", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.ChangeMarkingsAsync(Alice, ecn, WithCti(), "", null)).Kind);
        var tightened = await env.Change.ChangeMarkingsAsync(Alice, ecn, WithCti(), "Add CTI", null);
        Assert.True(tightened.IsOk, string.Join("; ", tightened.Errors));
        Assert.Equal(["CTI", "EXPT"], tightened.Value!.Markings.CuiCategories);
    }

    [Fact]
    public async Task One_change_cannot_list_objects_of_two_programs()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var orca = await env.NewPartAsync("Orca part", ChangeEnv.Itar("ORCA"), ChangeEnv.Kim);
        var kraken = await env.NewPartAsync("Kraken part", ChangeEnv.Itar("KRAKEN"), ChangeEnv.Kim);
        var ecn = (await env.NewChangeAsync(ChangeEnv.Kim, ChangeTypes.Ecn, "Cross-program", ChangeEnv.Itar("ORCA"), ChangeClasses.II)).Number;
        await env.AddAsync(ChangeEnv.Kim, ecn, orca, ChangeActions.None);
        var refused = await env.Change.AddAffectedAsync(ChangeEnv.Kim, ecn, new AddAffectedCommand("part", kraken, "none", null, RaiseMarkings: true), null);
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        Assert.Contains("program ORCA", refused.Errors[0], StringComparison.Ordinal);
        Assert.Contains("separate change", refused.Errors[0], StringComparison.Ordinal);
        Assert.Equal("A", (await env.PartAsync(kraken, who: ChangeEnv.Kim)).Revision.Revision);
    }

    [Fact]
    public async Task Nobody_can_add_what_they_cannot_see_or_modify_and_the_denials_are_audited_without_disclosure()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();
        var eve = ChangeEnv.Eve; // author, but not on program ORCA: the part is invisible to her
        var ecnOfEve = (await env.NewChangeAsync(eve, ChangeTypes.Ecn, "Eve's notice", ChangeEnv.Open, ChangeClasses.II)).Number;

        // A hidden part and a part that does not exist give the same answer.
        var hidden = await env.Change.AddAffectedAsync(eve, ecnOfEve, new AddAffectedCommand("part", part, "revise", null, RaiseMarkings: true), "corr-hidden");
        var missing = await env.Change.AddAffectedAsync(eve, ecnOfEve, new AddAffectedCommand("part", "P-999999", "revise", null, RaiseMarkings: true), null);
        Assert.Equal((OutcomeKind.Invalid, $"{part} was not found."), (hidden.Kind, hidden.Errors[0]));
        Assert.Equal((OutcomeKind.Invalid, "P-999999 was not found."), (missing.Kind, missing.Errors[0]));
        var denial = Assert.Single(await env.AuditAsync(), e => e.Action == "change.add_affected" && e.Outcome == AuditOutcome.Denied);
        Assert.Equal((ecnOfEve, "corr-hidden"), (denial.ObjectId, denial.CorrelationId));
        Assert.Contains("program.not_member", denial.Payload, StringComparison.Ordinal);
        Assert.Equal("A", (await env.PartAsync(part)).Revision.Revision);

        // A viewer can read the part but not modify it: refused, audited, nothing revised.
        var mallory = ChangeEnv.Mallory;
        var ecn = (await env.NewEcnAsync()).Number;
        var viewerAdd = await env.Change.AddAffectedAsync(mallory, ecn, new AddAffectedCommand("part", part, "none"), null);
        Assert.Equal(OutcomeKind.Forbidden, viewerAdd.Kind);
        Assert.Contains(AccessPolicy.RoleMissing, viewerAdd.Errors);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "change.add_affected" && e.Outcome == AuditOutcome.Denied && e.Actor == mallory.Id);

        // The same holds for creating: no Modify rights on the markings, no change.
        var create = await env.Change.CreateAsync(eve, new CreateChangeCommand(ChangeTypes.Pr, "Not allowed", null, "Low", null, ChangeEnv.Itar()), null);
        Assert.Equal(OutcomeKind.Forbidden, create.Kind);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "change.create" && e.Outcome == AuditOutcome.Denied && e.Actor == eve.Id);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Change.CreateAsync(mallory, new CreateChangeCommand(ChangeTypes.Pr, "Viewer", null, "Low", null, ChangeEnv.Open), null)).Kind);
    }

    [Fact]
    public async Task Reads_search_and_conflict_checks_go_through_the_read_filter_and_denials_are_audited()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var secret = await env.NewChangeAsync(Alice, ChangeTypes.Pr, "Secret problem", ChangeEnv.Itar());
        var open = await env.NewChangeAsync(Alice, ChangeTypes.Pr, "Public problem", ChangeEnv.Open);

        var eve = ChangeEnv.Eve;
        var found = await env.Change.SearchAsync(eve, new ChangeSearchQuery(), "corr-search");
        Assert.Equal([open.Number], found.Items.Select(i => i.Number));
        Assert.Equal("UNCLASSIFIED · DISTRIBUTION A", found.PageBanner);
        var all = await env.Change.SearchAsync(Alice, new ChangeSearchQuery(Type: ChangeTypes.Pr, Text: "problem"), null);
        Assert.Equal(2, all.Total);
        Assert.Equal("CUI//EXPT · ITAR · DISTRIBUTION D", all.PageBanner);
        Assert.Empty((await env.Change.SearchAsync(ChangeEnv.Lena, new ChangeSearchQuery(Text: "Secret"), null)).Items); // non-U.S. person: ITAR is invisible
        Assert.Equal(["PR-000001"], (await env.Change.SearchAsync(Alice, new ChangeSearchQuery(Text: "000001"), null)).Items.Select(i => i.Number));
        Assert.Empty((await env.Change.SearchAsync(Alice, new ChangeSearchQuery(Type: ChangeTypes.Ecn), null)).Items);

        // Single objects: 404 for the unauthorized, with an audited denial naming the reasons.
        var denied = await env.Change.GetAsync(eve, secret.Number, "corr-get");
        Assert.Equal(OutcomeKind.NotFound, denied.Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Change.GetAsync(eve, "PR-999999", null)).Kind);
        var audit = await env.AuditAsync();
        var event_ = Assert.Single(audit, e => e.Action == "change.read" && e.Outcome == AuditOutcome.Denied);
        Assert.Equal((secret.Number, "corr-get"), (event_.ObjectId, event_.CorrelationId));
        Assert.Contains("program.not_member", event_.Payload, StringComparison.Ordinal);
        Assert.Contains(audit, e => e.Action == "change.search" && e.CorrelationId == "corr-search" && e.Payload.Contains(open.Number, StringComparison.Ordinal) && !e.Payload.Contains(secret.Number, StringComparison.Ordinal));

        // Every mutation of a change the caller cannot read answers 404 and audits the attempt.
        Assert.Equal(OutcomeKind.NotFound, (await env.Change.UpdateAsync(eve, secret.Number, new UpdateChangeCommand("x", null, "Low", null), null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Change.TransitionAsync(eve, secret.Number, "Closed", "Withdrawn", null, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Change.CreateFromAsync(eve, secret.Number, ChangeTypes.Ecr, new CreateFromCommand(), null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Impact.AnalyzeAsync(eve, secret.Number, 3, null)).Kind);
        Assert.Equal(4, (await env.AuditAsync()).Count(e => e.Outcome == AuditOutcome.Denied && e.ObjectId == secret.Number && e.Action != "change.read"));
    }

    [Fact]
    public async Task A_reader_of_the_change_who_cannot_read_a_listed_object_sees_a_redacted_placeholder()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var ivan = WorkflowEnv.User("ivan", [Roles.Viewer, Roles.Author], cui: ["EXPT"]); // reads EXPT, not CTI
        var visible = await env.NewPartAsync("Visible part");
        var tightened = await env.NewPartAsync("Soon restricted");
        var ecn = (await env.NewEcnAsync()).Number;
        await env.AddAsync(Alice, ecn, visible, ChangeActions.None);
        await env.AddAsync(Alice, ecn, tightened, ChangeActions.None);
        Assert.All((await env.ChangeAsync(ecn, ivan)).Affected, a => Assert.False(a.Redacted));

        // Somebody with more access tightens the part afterwards (it is still in work): the change's reader can no longer see it.
        var tighten = await env.Parts.ChangeMarkingsAsync(Alice, tightened, WithCti(), "Contains CTI after review", null);
        Assert.True(tighten.IsOk, string.Join("; ", tighten.Errors));

        var asIvan = await env.ChangeAsync(ecn, ivan);
        var shown = asIvan.Affected.Single(a => !a.Redacted);
        var placeholder = asIvan.Affected.Single(a => a.Redacted);
        Assert.Equal(visible, shown.Key);
        Assert.Equal(("none", null, null, null, null, null), (placeholder.Action, placeholder.Key, placeholder.Name, placeholder.Revision, placeholder.State, placeholder.Banner));
        Assert.DoesNotContain("CTI", asIvan.Banner, StringComparison.Ordinal);
        Assert.Contains("CTI", (await env.ChangeAsync(ecn)).Banner, StringComparison.Ordinal);
        Assert.All((await env.ChangeAsync(ecn)).Affected, a => Assert.False(a.Redacted)); // Alice still sees both

        // Impact analysis redacts it too, counts it, and never queries structure or documents of the hidden part.
        var impact = (await env.Impact.AnalyzeAsync(ivan, ecn, 5, "corr-impact")).Value!;
        Assert.Equal((2, 1), (impact.Summary.AffectedObjects, impact.Summary.HiddenObjects));
        Assert.Contains(impact.Objects, o => o.Redacted && o.Key is null && o.WhereUsed.Count == 0);
        Assert.DoesNotContain("CTI", impact.Banner, StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await env.Change.AddAffectedAsync(ivan, (await env.NewEcnAsync(ivan, "Ivan's")).Number, new AddAffectedCommand("part", tightened, "none"), null)).Kind);

        // Submitting needs the change to cover the part again; raising the markings of the change does that, and the read is audited with the redaction count.
        var submit = await env.Flow.StartAsync(Alice, new Keel.Workflow.StartReviewCommand("ecn", ecn, new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob"] }), null);
        Assert.Equal(OutcomeKind.Conflict, submit.Kind);
        Assert.Contains("changes nothing yet", submit.Errors[0], StringComparison.Ordinal); // only 'none' items: nothing to release
        Assert.Contains(await env.AuditAsync(), e => e.Action == "change.read" && e.ObjectId == ecn && e.Payload.Contains("\"redacted\":\"1\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Submitting_is_refused_when_a_resulting_revision_was_tightened_beyond_the_change()
    {
        var env = await ChangeEnv.CreateAsync(pg);
        var (ecn, part) = await env.ReadyEcnAsync();
        // The engineer tightens the revision in work after it was added; the notice no longer covers it.
        Assert.True((await env.Parts.ChangeMarkingsAsync(Alice, part, WithCti(), "Adds CTI", null)).IsOk);
        var submit = await env.Flow.StartAsync(Alice, new Keel.Workflow.StartReviewCommand("ecn", ecn, new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob"] }), null);
        Assert.Equal(OutcomeKind.Conflict, submit.Kind);
        Assert.Contains("no longer covered", submit.Errors[0], StringComparison.Ordinal);
        Assert.Contains("CUI categories", submit.Errors[0], StringComparison.Ordinal);
        Assert.Equal("InWork", (await env.PartAsync(part)).Revision.State);
        Assert.Equal("Draft", (await env.ChangeAsync(ecn)).State);

        Assert.True((await env.Change.ChangeMarkingsAsync(Alice, ecn, WithCti(), "Cover the tightened part", null)).IsOk);
        Assert.Equal("Active", (await env.SubmitAsync(ecn)).Status);
    }
}
