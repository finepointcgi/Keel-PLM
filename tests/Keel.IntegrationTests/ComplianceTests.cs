using Keel.Audit;
using Keel.Compliance;
using Keel.Core;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Substances, materials, regulatory lists and declarations against a real PostgreSQL (ADR-0040): rules, roles, immutability, review and re-evaluation.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ComplianceTests(PostgresFixture pg)
{
    [Fact]
    public async Task The_migration_seeds_three_published_starter_lists_with_valid_substances()
    {
        var env = await ComplianceEnv.CreateAsync(pg);
        var lists = (await env.Catalog.ListsAsync(ComplianceEnv.Alice, null)).Value!;
        Assert.Equal(["conflict_minerals", "reach_svhc", "rohs"], lists.Select(l => l.Code).Order(StringComparer.Ordinal));
        Assert.All(lists, l => Assert.Equal(1, l.ActiveVersion));
        var subs = (await env.Catalog.SearchSubstancesAsync(ComplianceEnv.Alice, new SubstanceSearch(Limit: 200), null)).Value!;
        Assert.Equal(16, subs.Total);
        Assert.All(subs.Items, s => Assert.True(ComplianceRules.IsValidCas(s.CasNumber), s.CasNumber));
        Assert.Equal(4, subs.Items.Count(s => s.Mineral is not null));
        var rohs = (await env.Catalog.GetVersionAsync(ComplianceEnv.Alice, "rohs", 1, null)).Value!;
        Assert.Equal(VersionStates.Published, rohs.Version.Status);
        Assert.Contains(rohs.Exemptions, x => x.Code == "7(a)");
    }

    [Fact]
    public async Task Substances_are_changed_only_by_administrators_and_validated()
    {
        var env = await ComplianceEnv.CreateAsync(pg);
        var input = new SubstanceInput("7782-49-2", "Selenium");
        Assert.Equal(OutcomeKind.Forbidden, (await env.Catalog.CreateSubstanceAsync(ComplianceEnv.Alice, input, null)).Kind);
        var created = await env.Catalog.CreateSubstanceAsync(ComplianceEnv.Ada, input, null);
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        Assert.Equal(OutcomeKind.Conflict, (await env.Catalog.CreateSubstanceAsync(ComplianceEnv.Ada, input, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Catalog.CreateSubstanceAsync(ComplianceEnv.Ada, new SubstanceInput("7782-49-3", "Bad check digit"), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Catalog.CreateSubstanceAsync(ComplianceEnv.Ada, new SubstanceInput("7782-49-2", "x", null, "copper"), null)).Kind);
        var updated = await env.Catalog.UpdateSubstanceAsync(ComplianceEnv.Ada, created.Value!.Number, new SubstanceInput("7782-49-2", "Selenium (element)"), created.Value.Revision, null);
        Assert.True(updated.IsOk);
        Assert.Equal(OutcomeKind.Conflict, (await env.Catalog.UpdateSubstanceAsync(ComplianceEnv.Ada, created.Value.Number, new SubstanceInput("7782-49-2", "Again"), created.Value.Revision, null)).Kind);
        // Everyone with an internal role reads and searches; the supplier role and no role at all do not.
        var found = (await env.Catalog.SearchSubstancesAsync(ComplianceEnv.Mallory, new SubstanceSearch("seleni"), null)).Value!;
        Assert.Equal(1, found.Total);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Catalog.SearchSubstancesAsync(SourcingEnv.Sup1, new SubstanceSearch(), null)).Kind);
        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.Action == "compliance.substance_create" && e.Outcome == AuditOutcome.Denied);
        Assert.Contains(audit, e => e.Action == "compliance.substance_create" && e.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task Materials_expand_into_substances_and_cannot_exceed_one_hundred_percent()
    {
        var env = await ComplianceEnv.CreateAsync(pg);
        var bad = await env.Catalog.CreateMaterialAsync(ComplianceEnv.Ada, new MaterialInput("BRASS-CW614N", "Brass", null, [new(ComplianceEnv.Lead, 60), new(ComplianceEnv.Tin, 50)]), null);
        Assert.Equal(OutcomeKind.Invalid, bad.Kind);
        var unknown = await env.Catalog.CreateMaterialAsync(ComplianceEnv.Ada, new MaterialInput("BRASS-X", "Brass", null, [new("50-00-0", 5)]), null);
        Assert.Equal(OutcomeKind.Invalid, unknown.Kind);
        var created = await env.Catalog.CreateMaterialAsync(ComplianceEnv.Ada, new MaterialInput("BRASS-CW614N", "Free-cutting brass", null, [new(ComplianceEnv.Lead, 3.5m)]), null);
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        Assert.Equal(OutcomeKind.Forbidden, (await env.Catalog.CreateMaterialAsync(ComplianceEnv.Alice, new MaterialInput("X1", "x", null, []), null)).Kind);

        // A part made of 100 % of that material carries 3.5 % lead: above the RoHS threshold of 0.1 %.
        var part = await env.NewPartAsync();
        var d = await env.DeclareAsync(part, [new DeclarationLineInput(null, "BRASS-CW614N", 100, null)]);
        Assert.Equal(DeclarationStatuses.NonCompliant, d.Summary.Status);
        var finding = Assert.Single(d.Findings, f => f.ListCode == "rohs");
        Assert.Equal(3.5m, finding.Percent);
        Assert.Equal(FindingReasons.AboveThreshold, finding.Reason);
        // The same material under an exemption is allowed.
        var exempt = await env.DeclareAsync(part, [new DeclarationLineInput(null, "BRASS-CW614N", 100, null, "6(c)")]);
        Assert.Equal(EvaluationResults.Exempt, exempt.Summary.Evaluations.Single(e => e.ListCode == "rohs").Result);
    }

    [Fact]
    public async Task A_published_list_version_is_immutable_in_the_database_and_a_draft_is_edited_then_published_through_the_review()
    {
        var env = await ComplianceEnv.CreateAsync(pg);
        // The database refuses a direct change of a published version's entries and of the version itself.
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = env.Ds.CreateCommand("UPDATE cmp.reg_list_entry SET threshold_pct = 5 WHERE version_id IN (SELECT id FROM cmp.reg_list_version WHERE status = 'published')");
            await cmd.ExecuteNonQueryAsync();
        });
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = env.Ds.CreateCommand("DELETE FROM cmp.reg_list_version WHERE status = 'published'");
            await cmd.ExecuteNonQueryAsync();
        });

        Assert.Equal(OutcomeKind.Forbidden, (await env.Catalog.CreateDraftAsync(ComplianceEnv.Alice, "rohs", env.Today, null, null)).Kind);
        var draft = await env.Catalog.CreateDraftAsync(ComplianceEnv.Ada, "rohs", env.Today, "Amendment", null);
        Assert.True(draft.IsOk, string.Join("; ", draft.Errors));
        Assert.Equal(2, draft.Value!.Version);
        Assert.Equal(10, draft.Value.Entries); // copied from version 1
        Assert.Equal(OutcomeKind.Conflict, (await env.Catalog.CreateDraftAsync(ComplianceEnv.Ada, "rohs", env.Today, null, null)).Kind); // one in preparation at a time

        var content = (await env.Catalog.GetVersionAsync(ComplianceEnv.Ada, "rohs", 2, null)).Value!;
        var entries = content.Entries.Select(e => new RegEntryInput(e.CasNumber, e.CasNumber == ComplianceEnv.Lead ? 0.05m : e.ThresholdPct)).ToList();
        var exemptions = content.Exemptions.Select(x => new RegExemptionInput(x.Code, x.Description, x.CasNumber, x.ExpiresOn)).ToList();
        Assert.Equal(OutcomeKind.Invalid, (await env.Catalog.SaveDraftAsync(ComplianceEnv.Ada, "rohs", 2, env.Today, null, [.. entries, new RegEntryInput("1-1-1", 1)], exemptions, draft.Value.Revision, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Catalog.SaveDraftAsync(ComplianceEnv.Ada, "rohs", 2, env.Today, null, [.. entries, new RegEntryInput("50-00-0", 1)], exemptions, draft.Value.Revision, null)).Kind); // not in the master
        var saved = await env.Catalog.SaveDraftAsync(ComplianceEnv.Ada, "rohs", 2, env.Today, null, entries, exemptions, draft.Value.Revision, null);
        Assert.True(saved.IsOk, string.Join("; ", saved.Errors));
        Assert.Equal(OutcomeKind.Conflict, (await env.Catalog.SaveDraftAsync(ComplianceEnv.Ada, "rohs", 2, env.Today, null, entries, exemptions, draft.Value.Revision, null)).Kind); // stale revision

        var diff = (await env.Catalog.GetVersionAsync(ComplianceEnv.Ada, "rohs", 2, null)).Value!.DiffToPrevious;
        Assert.Single(diff!.Changed); // a draft is compared with the version it will replace
        Assert.Empty(diff.Added);
    }

    [Fact]
    public async Task Publishing_supersedes_the_previous_version_freezes_the_review_and_shows_the_difference()
    {
        var env = await ComplianceEnv.CreateAsync(pg);
        var version = await env.NewVersionAsync("rohs", (entries, _) =>
        {
            entries[entries.FindIndex(e => e.CasNumber == ComplianceEnv.Lead)] = new RegEntryInput(ComplianceEnv.Lead, 0.05m);
            entries.RemoveAll(e => e.CasNumber == "84-69-5");
            return env.Today;
        });
        Assert.Equal(2, version);
        var detail = (await env.Catalog.GetListAsync(ComplianceEnv.Alice, "rohs", null)).Value!;
        Assert.Equal(VersionStates.Published, detail.Versions.Single(v => v.Version == 2).Status);
        Assert.Equal(VersionStates.Superseded, detail.Versions.Single(v => v.Version == 1).Status);
        Assert.Equal(2, detail.List.ActiveVersion);
        var v2 = (await env.Catalog.GetVersionAsync(ComplianceEnv.Alice, "rohs", 2, null)).Value!;
        Assert.Single(v2.DiffToPrevious!.Removed);
        Assert.Single(v2.DiffToPrevious.Changed);
        Assert.Contains("0.1 % to 0.05 %", v2.DiffToPrevious.Changed[0], StringComparison.Ordinal);
        // The signatures were made through the engine and the publication is audited.
        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.Action == "compliance.list_publish" && e.ObjectId == "rohs-2");
        Assert.Contains(audit, e => e.Action == "compliance.list_submit" && e.ObjectId == "rohs-2");
        // A published version cannot be sent to review again, nor edited.
        Assert.Equal(OutcomeKind.Conflict, (await env.Catalog.SaveDraftAsync(ComplianceEnv.Ada, "rohs", 2, env.Today, null, [new(ComplianceEnv.Lead, 1)], [], v2.Version.Revision, null)).Kind);
    }

    [Fact]
    public async Task A_version_in_review_is_frozen_and_returns_to_draft_when_the_review_is_rejected()
    {
        var env = await ComplianceEnv.CreateAsync(pg);
        var draft = await env.Catalog.CreateDraftAsync(ComplianceEnv.Ada, "rohs", env.Today, null, null);
        var started = await env.Flow.StartAsync(ComplianceEnv.Alice, new Keel.Workflow.StartReviewCommand("reg_list_version", "rohs-2", new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob", "carol"] }), "c");
        Assert.True(started.IsOk, string.Join("; ", started.Errors));
        var content = (await env.Catalog.GetVersionAsync(ComplianceEnv.Ada, "rohs", 2, null)).Value!;
        var frozen = await env.Catalog.SaveDraftAsync(ComplianceEnv.Ada, "rohs", 2, env.Today, null, [new(ComplianceEnv.Lead, 1)], [], content.Version.Revision, null);
        Assert.Equal(OutcomeKind.Conflict, frozen.Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Catalog.DiscardDraftAsync(ComplianceEnv.Ada, "rohs", 2, null)).Kind);
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = env.Ds.CreateCommand("UPDATE cmp.reg_list_entry SET threshold_pct = 7 WHERE version_id IN (SELECT id FROM cmp.reg_list_version WHERE status = 'in_review')");
            await cmd.ExecuteNonQueryAsync();
        });
        // Rejection by the reviewer returns the draft.
        var rejected = await env.Flow.SignAsync(WorkflowEnv.Fresh(ComplianceEnv.Rita), started.Value!.Tasks.Single(t => t.Status == "Open").Id, "Rejected", "Not ready", "c");
        Assert.True(rejected.IsOk, string.Join("; ", rejected.Errors));
        Assert.Equal(VersionStates.Draft, (await env.Catalog.GetVersionAsync(ComplianceEnv.Ada, "rohs", 2, null)).Value!.Version.Status);
        Assert.True((await env.Catalog.DiscardDraftAsync(ComplianceEnv.Ada, "rohs", 2, null)).IsOk);
        _ = draft;
    }

    [Fact]
    public async Task Declarations_are_evaluated_against_the_lists_in_force_with_exemptions_and_conflict_status()
    {
        var env = await ComplianceEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();

        var over = await env.DeclareAsync(part, [ComplianceEnv.Sub(ComplianceEnv.Lead, 0.5m)]);
        Assert.Equal(DeclarationStatuses.NonCompliant, over.Summary.Status);
        Assert.Equal(EvaluationResults.NonCompliant, over.Summary.Evaluations.Single(e => e.ListCode == "rohs").Result);
        Assert.Equal(EvaluationResults.NonCompliant, over.Summary.Evaluations.Single(e => e.ListCode == "reach_svhc").Result); // lead is on the SVHC sample too

        var under = await env.DeclareAsync(part, [ComplianceEnv.Sub(ComplianceEnv.Lead, 0.05m), ComplianceEnv.Sub(ComplianceEnv.Cadmium, 0.005m)]);
        Assert.Equal(DeclarationStatuses.Declared, under.Summary.Status);

        var cadmium = await env.DeclareAsync(part, [ComplianceEnv.Sub(ComplianceEnv.Cadmium, 0.02m)]); // the cadmium threshold is 0.01 %
        Assert.Equal(DeclarationStatuses.NonCompliant, cadmium.Summary.Status);

        // An exemption of another substance does not apply; an unknown code is a finding; the right one is allowed.
        var wrongSubstance = await env.DeclareAsync(part, [ComplianceEnv.Sub(ComplianceEnv.Mercury, 0.5m, "7(a)")]);
        Assert.Contains(wrongSubstance.Findings, f => f.Reason == FindingReasons.ExemptionNotApplicable);
        var unknownCode = await env.DeclareAsync(part, [ComplianceEnv.Sub(ComplianceEnv.Lead, 0.5m, "99(z)")]);
        Assert.Contains(unknownCode.Findings, f => f.Reason == FindingReasons.ExemptionUnknown);
        var allowed = await env.DeclareAsync(part, [ComplianceEnv.Sub(ComplianceEnv.Lead, 90m, "7(a)")]);
        Assert.Equal(EvaluationResults.Exempt, allowed.Summary.Evaluations.Single(e => e.ListCode == "rohs").Result);
        // An exemption of RoHS means nothing for REACH: lead above 0.1 % is still reportable there.
        Assert.Equal(EvaluationResults.NonCompliant, allowed.Summary.Evaluations.Single(e => e.ListCode == "reach_svhc").Result);
        Assert.Contains(allowed.Findings, f => f.Reason == FindingReasons.Exempt && f.ExemptionCode == "7(a)");

        // Conflict minerals: unknown leaves the result unverified, not conflict-free is a violation.
        var unknownGold = await env.DeclareAsync(part, [ComplianceEnv.Sub(ComplianceEnv.Gold, 0.3m)]);
        Assert.Equal(EvaluationResults.Unverified, unknownGold.Summary.Evaluations.Single(e => e.ListCode == "conflict_minerals").Result);
        Assert.Equal(DeclarationStatuses.Declared, unknownGold.Summary.Status);
        var freeGold = await env.DeclareAsync(part, [ComplianceEnv.Sub(ComplianceEnv.Gold, 0.3m, null, true)]);
        Assert.Equal(EvaluationResults.Compliant, freeGold.Summary.Evaluations.Single(e => e.ListCode == "conflict_minerals").Result);
        var conflictGold = await env.DeclareAsync(part, [ComplianceEnv.Sub(ComplianceEnv.Gold, 0.3m, null, false)]);
        Assert.Equal(DeclarationStatuses.NonCompliant, conflictGold.Summary.Status);

        // A mass becomes a percent through the mass of the item.
        var byMass = await env.Declarations.CreateAsync(ComplianceEnv.Alice, new CreateDeclarationCommand(part,
            new DeclarationInput(null, DeclarationSources.Internal, null, null, 1000m, env.Today, env.Today.AddDays(30), [new DeclarationLineInput(ComplianceEnv.Lead, null, null, 5m)], null)), null);
        Assert.True(byMass.IsOk, string.Join("; ", byMass.Errors));
        Assert.Equal(0.5m, byMass.Value!.Findings.Single(f => f.ListCode == "rohs").Percent);
        var noItemMass = await env.Declarations.CreateAsync(ComplianceEnv.Alice, new CreateDeclarationCommand(part, env.Input([new DeclarationLineInput(ComplianceEnv.Lead, null, null, 5m)])), null);
        Assert.Equal(OutcomeKind.Invalid, noItemMass.Kind);
    }

    [Fact]
    public async Task Declarations_need_the_right_roles_validate_their_input_and_name_no_text_in_the_audit_log()
    {
        var env = await ComplianceEnv.CreateAsync(pg);
        var part = await env.NewPartAsync("Secret bracket name");
        var lines = new[] { ComplianceEnv.Sub(ComplianceEnv.Tin, 1) };
        Assert.Equal(OutcomeKind.Forbidden, (await env.Declarations.CreateAsync(ComplianceEnv.Mallory, new CreateDeclarationCommand(part, env.Input(lines)), null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Declarations.CreateAsync(ComplianceEnv.Alice, new CreateDeclarationCommand("P-999999", env.Input(lines)), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Declarations.CreateAsync(ComplianceEnv.Alice, new CreateDeclarationCommand(part, env.Input([])), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Declarations.CreateAsync(ComplianceEnv.Alice, new CreateDeclarationCommand(part, env.Input([ComplianceEnv.Sub("50-00-0", 1)])), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Declarations.CreateAsync(ComplianceEnv.Alice, new CreateDeclarationCommand(part, env.Input([ComplianceEnv.Sub(ComplianceEnv.Tin, 101)])), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Declarations.CreateAsync(ComplianceEnv.Alice, new CreateDeclarationCommand(part, env.Input(lines, DeclarationSources.Supplier)), null)).Kind); // a supplier declaration names its supplier

        var created = await env.DeclareAsync(part, lines);
        // Verification is for approvers; the author cannot verify, a viewer cannot either.
        Assert.Equal(OutcomeKind.Forbidden, (await env.Declarations.VerifyAsync(ComplianceEnv.Alice, created.Summary.Number, null)).Kind);
        var verified = await env.Declarations.VerifyAsync(ComplianceEnv.Bob, created.Summary.Number, null);
        Assert.True(verified.IsOk, string.Join("; ", verified.Errors));
        Assert.Equal(DeclarationStatuses.Verified, verified.Value!.Summary.Status);
        Assert.Equal(OutcomeKind.Conflict, (await env.Declarations.VerifyAsync(ComplianceEnv.Bob, created.Summary.Number, null)).Kind);
        // Changing a declaration clears the verification.
        var changed = await env.Declarations.UpdateAsync(ComplianceEnv.Alice, created.Summary.Number, env.Input([ComplianceEnv.Sub(ComplianceEnv.Tin, 2)]), verified.Value.Revision, null);
        Assert.True(changed.IsOk, string.Join("; ", changed.Errors));
        Assert.Equal(DeclarationStatuses.Declared, changed.Value!.Summary.Status);
        Assert.Null(changed.Value.VerifiedAt);
        Assert.Equal(OutcomeKind.Conflict, (await env.Declarations.UpdateAsync(ComplianceEnv.Alice, created.Summary.Number, env.Input(lines), verified.Value.Revision, null)).Kind); // stale
        // Withdrawing ends it.
        Assert.Equal(OutcomeKind.Invalid, (await env.Declarations.WithdrawAsync(ComplianceEnv.Alice, created.Summary.Number, " ", null)).Kind);
        var withdrawn = await env.Declarations.WithdrawAsync(ComplianceEnv.Alice, created.Summary.Number, "Superseded by a new declaration", null);
        Assert.Equal(DeclarationStatuses.Withdrawn, withdrawn.Value!.Summary.Status);
        Assert.Equal(OutcomeKind.Conflict, (await env.Declarations.UpdateAsync(ComplianceEnv.Alice, created.Summary.Number, env.Input(lines), changed.Value.Revision + 1, null)).Kind);

        // The audit events name numbers, codes and counts, never the part's name or the free text.
        var audit = (await env.AuditAsync()).Where(e => e.Action.StartsWith("compliance.", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(audit);
        foreach (var e in audit)
        {
            var text = e.Payload;
            Assert.DoesNotContain("Secret bracket", text, StringComparison.Ordinal);
            Assert.DoesNotContain("note text", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Superseded by", text, StringComparison.Ordinal);
        }
        Assert.Contains(audit, e => e.Action == "compliance.declaration_create" && e.Outcome == AuditOutcome.Denied);
        Assert.Contains(audit, e => e.Action == "compliance.declaration_verify" && e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task Supplier_declarations_name_opaque_references_and_notify_the_watchers_with_pointers_only()
    {
        var env = await ComplianceEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        var created = await env.Declarations.CreateAsync(ComplianceEnv.Alice, new CreateDeclarationCommand(part,
            new DeclarationInput(null, DeclarationSources.Supplier, "SUP-000007", "MPN-100", null, env.Today, env.Today.AddDays(200), [ComplianceEnv.Sub(ComplianceEnv.Tin, 2)], "Received by mail")), null);
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        Assert.Equal("SUP-000007", created.Value!.Summary.SupplierRef);
        Assert.Equal("MPN-100", created.Value.Summary.MpnRef);
        var request = Assert.Single(env.Sink.Requests, r => r.Kind == NotificationKindCodes.ComplianceSupplierDeclaration);
        Assert.Equal(("part", part), (request.ObjectType, request.ObjectNumber));
        Assert.Contains(env.Sink.Watches, w => w.Number == part && w.User == ComplianceEnv.Alice.Id);
        var page = (await env.Declarations.SearchAsync(ComplianceEnv.Alice, new DeclarationSearch(SupplierRef: "SUP-000007"), null)).Value!;
        Assert.Equal(1, page.Total);
        Assert.Equal(0, (await env.Declarations.SearchAsync(ComplianceEnv.Alice, new DeclarationSearch(SupplierRef: "SUP-000008"), null)).Value!.Total);
    }

    [Fact]
    public async Task Documents_are_linked_by_number_and_an_unreadable_document_looks_like_a_missing_one()
    {
        var env = await ComplianceEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        env.Docs.Documents["DOC-000001"] = ComplianceEnv.Open;
        env.Docs.Documents["DOC-000002"] = WorkflowEnv.Itar("KRAKEN"); // alice is not on KRAKEN
        var d = await env.DeclareAsync(part, [ComplianceEnv.Sub(ComplianceEnv.Tin, 1)]);
        var ok = await env.Declarations.LinkDocumentAsync(ComplianceEnv.Alice, d.Summary.Number, "DOC-000001", null);
        Assert.True(ok.IsOk, string.Join("; ", ok.Errors));
        Assert.Equal("DOC-000001", Assert.Single(ok.Value!.Documents).DocumentNumber);
        Assert.Equal(OutcomeKind.Invalid, (await env.Declarations.LinkDocumentAsync(ComplianceEnv.Alice, d.Summary.Number, "DOC-000002", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Declarations.LinkDocumentAsync(ComplianceEnv.Alice, d.Summary.Number, "DOC-999999", null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Declarations.LinkDocumentAsync(ComplianceEnv.Mallory, d.Summary.Number, "DOC-000001", null)).Kind);
        // A reader who may not read the document sees a placeholder without its number.
        var seenByMallory = (await env.Declarations.GetAsync(ComplianceEnv.Mallory, d.Summary.Number, null)).Value!;
        Assert.Equal("DOC-000001", Assert.Single(seenByMallory.Documents).DocumentNumber);
        env.Docs.Documents["DOC-000001"] = WorkflowEnv.Itar("KRAKEN");
        var hidden = Assert.Single((await env.Declarations.GetAsync(ComplianceEnv.Alice, d.Summary.Number, null)).Value!.Documents);
        Assert.Null(hidden.DocumentNumber);
        Assert.False(hidden.Readable);
        Assert.True((await env.Declarations.UnlinkDocumentAsync(ComplianceEnv.Alice, d.Summary.Number, "DOC-000001", null)).IsOk);
    }

    [Fact]
    public async Task Publishing_a_stricter_list_reevaluates_declarations_and_tells_the_watchers_of_the_part()
    {
        var env = await ComplianceEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        var d = await env.DeclareAsync(part, [ComplianceEnv.Sub(ComplianceEnv.Lead, 0.08m)]);
        Assert.Equal(DeclarationStatuses.Declared, d.Summary.Status);
        env.Sink.Requests.Clear();

        await env.NewVersionAsync("rohs", (entries, _) =>
        {
            entries[entries.FindIndex(e => e.CasNumber == ComplianceEnv.Lead)] = new RegEntryInput(ComplianceEnv.Lead, 0.05m);
            return env.Today;
        });
        var after = (await env.Declarations.GetAsync(ComplianceEnv.Alice, d.Summary.Number, null)).Value!;
        Assert.Equal(DeclarationStatuses.NonCompliant, after.Summary.Status);
        Assert.Equal(2, after.Summary.Evaluations.Single(e => e.ListCode == "rohs").ListVersion);
        var note = Assert.Single(env.Sink.Requests, r => r.Kind == NotificationKindCodes.ComplianceNonCompliant);
        Assert.Equal(part, note.ObjectNumber);
        var audit = await env.AuditAsync();
        var change = Assert.Single(audit, e => e.Action == "compliance.status_change" && e.ObjectId == part);
        Assert.Contains("list_published", change.Payload, StringComparison.Ordinal);

        // Publishing a version that is effective later changes nothing yet; the daily job re-evaluates once the date has come.
        var futureVersion = await env.NewVersionAsync("rohs", (entries, _) =>
        {
            entries[entries.FindIndex(e => e.CasNumber == ComplianceEnv.Lead)] = new RegEntryInput(ComplianceEnv.Lead, 0.5m);
            return env.Today.AddDays(30);
        });
        Assert.Equal(3, futureVersion);
        Assert.Equal(DeclarationStatuses.NonCompliant, (await env.Declarations.GetAsync(ComplianceEnv.Alice, d.Summary.Number, null)).Value!.Summary.Status);
        env.Clock.Advance(TimeSpan.FromDays(31));
        await env.Expiry.RunOnceAsync();
        Assert.Equal(DeclarationStatuses.Declared, (await env.Declarations.GetAsync(ComplianceEnv.Alice, d.Summary.Number, null)).Value!.Summary.Status);
    }

    [Fact]
    public async Task Declarations_expire_with_notices_at_60_30_and_7_days_and_on_expiry_once_each()
    {
        var env = await ComplianceEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        var d = await env.DeclareAsync(part, [ComplianceEnv.Sub(ComplianceEnv.Tin, 1, null, true)], validDays: 70);
        env.Sink.Requests.Clear();
        Assert.Equal(0, await env.Expiry.RunOnceAsync()); // 70 days: nothing yet
        env.Clock.Advance(TimeSpan.FromDays(15)); // 55 days left
        Assert.Equal(1, await env.Expiry.RunOnceAsync());
        Assert.Equal(0, await env.Expiry.RunOnceAsync()); // idempotent
        env.Clock.Advance(TimeSpan.FromDays(30)); // 25 days left
        Assert.Equal(1, await env.Expiry.RunOnceAsync());
        env.Clock.Advance(TimeSpan.FromDays(20)); // 5 days left
        Assert.Equal(1, await env.Expiry.RunOnceAsync());
        Assert.Equal(DeclarationStatuses.Declared, (await env.Declarations.GetAsync(ComplianceEnv.Alice, d.Summary.Number, null)).Value!.Summary.Status);
        env.Clock.Advance(TimeSpan.FromDays(10)); // expired
        Assert.Equal(1, await env.Expiry.RunOnceAsync());
        Assert.Equal(0, await env.Expiry.RunOnceAsync());
        Assert.Equal(DeclarationStatuses.Expired, (await env.Declarations.GetAsync(ComplianceEnv.Alice, d.Summary.Number, null)).Value!.Summary.Status);
        Assert.Equal(3, env.Sink.Requests.Count(r => r.Kind == NotificationKindCodes.ComplianceDeclarationExpiring));
        var expired = Assert.Single(env.Sink.Requests, r => r.Kind == NotificationKindCodes.ComplianceDeclarationExpired);
        Assert.Equal(("part", part), (expired.ObjectType, expired.ObjectNumber));
        Assert.Equal(OutcomeKind.Conflict, (await env.Declarations.VerifyAsync(ComplianceEnv.Bob, d.Summary.Number, null)).Kind);
        // An expired declaration says nothing: the part is a gap again.
        var view = (await env.Declarations.GetForPartAsync(ComplianceEnv.Alice, part, null)).Value!;
        Assert.Equal(Standings.Expired, view.Regulations.Single(r => r.ListCode == "rohs").Standing);
    }

    [Fact]
    public async Task The_part_view_shows_the_worst_current_declaration_per_regulation()
    {
        var env = await ComplianceEnv.CreateAsync(pg);
        var part = await env.NewPartAsync();
        var none = (await env.Declarations.GetForPartAsync(ComplianceEnv.Alice, part, null)).Value!;
        Assert.All(none.Regulations, r => Assert.Equal(Standings.Gap, r.Standing));
        Assert.True(none.CanModify);
        Assert.False(none.CanVerify);
        await env.DeclareAsync(part, [ComplianceEnv.Sub(ComplianceEnv.Tin, 1, null, true)]);
        var ok = (await env.Declarations.GetForPartAsync(ComplianceEnv.Alice, part, null)).Value!;
        Assert.Equal(Standings.Compliant, ok.Regulations.Single(r => r.ListCode == "rohs").Standing);
        await env.DeclareAsync(part, [ComplianceEnv.Sub(ComplianceEnv.Mercury, 1)], source: DeclarationSources.Supplier, supplierRef: "SUP-000001");
        var worst = (await env.Declarations.GetForPartAsync(ComplianceEnv.Alice, part, null)).Value!;
        Assert.Equal(Standings.NonCompliant, worst.Regulations.Single(r => r.ListCode == "rohs").Standing);
        Assert.Equal(2, worst.Regulations.Single(r => r.ListCode == "rohs").DeclarationNumbers.Count);
        // A declaration for revision B does not cover the part at revision A.
        var other = await env.NewPartAsync("Other");
        await env.DeclareAsync(other, [ComplianceEnv.Sub(ComplianceEnv.Tin, 1)], scope: "B");
        Assert.Equal(Standings.Gap, (await env.Declarations.GetForPartAsync(ComplianceEnv.Alice, other, null)).Value!.Regulations.Single(r => r.ListCode == "rohs").Standing);
    }

    [Fact]
    public async Task The_rollup_explains_non_compliance_counts_gaps_and_can_be_pinned_to_a_list_version()
    {
        var env = await ComplianceEnv.CreateAsync(pg);
        var good = await env.NewPartAsync("Good");
        var bad = await env.NewPartAsync("Bad");
        var missing = await env.NewPartAsync("Missing");
        var exempt = await env.NewPartAsync("Exempt");
        var asm = await env.NewPartAsync("Assembly");
        await env.DeclareAsync(good, [ComplianceEnv.Sub(ComplianceEnv.Tin, 5, null, true)]);
        await env.DeclareAsync(bad, [ComplianceEnv.Sub(ComplianceEnv.Cadmium, 0.5m)]);
        await env.DeclareAsync(exempt, [ComplianceEnv.Sub(ComplianceEnv.Lead, 90m, "7(a)")]);
        var lines = new[]
        {
            ComplianceEnv.Line("1", asm, true, level: 1), // an assembly line (has children) is not required itself
            ComplianceEnv.Line("1.1", good, false, level: 2), ComplianceEnv.Line("1.2", bad, false, level: 2),
            ComplianceEnv.Line("1.3", missing, false, level: 2), ComplianceEnv.Line("1.4", exempt, false, level: 2),
        };
        var report = (await env.Reports.BuildAsync(ComplianceEnv.Alice, ComplianceEnv.Request(asm, lines), null)).Value!;
        Assert.Equal(["conflict_minerals", "reach_svhc", "rohs"], report.Regulations.Select(r => r.Code).Order(StringComparer.Ordinal));
        var rohs = report.Summaries.Single(s => s.Code == "rohs");
        Assert.Equal(Standings.AssemblyNonCompliant, rohs.Standing);
        Assert.Equal((1, 1, 1, 1), (rohs.Compliant, rohs.Exempt, rohs.NonCompliant, rohs.Gaps));
        Assert.Equal(1, rohs.NotRequired);
        Assert.Equal(4, rohs.Required);
        // Causes: the non-compliant line first, with the substance, then the gap.
        Assert.Equal(["1.2", "1.3"], rohs.Causes.Select(c => c.Path));
        var cause = rohs.Causes[0];
        Assert.Equal(bad, cause.Number);
        var finding = Assert.Single(cause.Findings);
        Assert.Equal(("7440-43-9", FindingReasons.AboveThreshold), (finding.CasNumber, finding.Reason));
        // Conflict minerals: tin with a conflict-free statement is fine; parts with no declaration are gaps.
        var conflict = report.Summaries.Single(s => s.Code == "conflict_minerals");
        Assert.Equal(Standings.AssemblyIncomplete, conflict.Standing);
        Assert.Equal(1, conflict.Gaps);

        // Pinned to a later version of the list, the same structure reads differently, and the older version stays available.
        var v2 = await env.NewVersionAsync("rohs", (entries, _) =>
        {
            entries.RemoveAll(e => e.CasNumber == ComplianceEnv.Cadmium);
            return env.Today;
        });
        var current = (await env.Reports.BuildAsync(ComplianceEnv.Alice, ComplianceEnv.Request(asm, lines), null)).Value!.Summaries.Single(s => s.Code == "rohs");
        Assert.Equal(0, current.NonCompliant);
        Assert.Equal(v2, current.Version);
        var pinned = (await env.Reports.BuildAsync(ComplianceEnv.Alice, ComplianceEnv.Request(asm, lines, new Dictionary<string, int> { ["rohs"] = 1 }), null)).Value!.Summaries.Single(s => s.Code == "rohs");
        Assert.Equal((1, 1), (pinned.Version, pinned.NonCompliant));
        Assert.Equal(OutcomeKind.Invalid, (await env.Reports.BuildAsync(ComplianceEnv.Alice, ComplianceEnv.Request(asm, lines, new Dictionary<string, int> { ["rohs"] = 9 }), null)).Kind);
        // And as of a date before the second version.
        var back = (await env.Reports.BuildAsync(ComplianceEnv.Alice, ComplianceEnv.Request(asm, lines, null, env.Today.AddDays(-1)), null)).Value!.Summaries.Single(s => s.Code == "rohs");
        Assert.Equal(1, back.Version);
        Assert.Contains((await env.AuditAsync()), e => e.Action == "compliance.rollup");
    }

    [Fact]
    public async Task Exports_are_built_from_the_report_audited_and_neutralize_formulas()
    {
        var env = await ComplianceEnv.CreateAsync(pg);
        var part = await env.NewPartAsync("=HYPERLINK(\"http://x\")");
        await env.DeclareAsync(part, [ComplianceEnv.Sub(ComplianceEnv.Gold, 2, null, true), ComplianceEnv.Sub(ComplianceEnv.Lead, 90, "7(a)")], source: DeclarationSources.Supplier, supplierRef: "SUP-000002");
        var lines = new[] { new StructureLine(1, 10, "1", part, "=HYPERLINK(\"http://x\")", "A", 2m, "EA", false) };
        var csv = await env.Reports.ExportAsync(ComplianceEnv.Alice, ComplianceEnv.Request(part, lines), "cmrt", null);
        Assert.True(csv.IsOk, string.Join("; ", csv.Errors));
        Assert.EndsWith("-cmrt.csv", csv.Value!.FileName, StringComparison.Ordinal);
        var rows = csv.Value.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, rows.Length);
        Assert.Contains("'=HYPERLINK", rows[1], StringComparison.Ordinal);
        Assert.Contains("Yes, conflict-free", rows[1], StringComparison.Ordinal);
        Assert.Contains("SUP-000002", rows[1], StringComparison.Ordinal);
        var xml = await env.Reports.ExportAsync(ComplianceEnv.Alice, ComplianceEnv.Request(part, lines), "ipc1752", null);
        Assert.True(xml.IsOk, string.Join("; ", xml.Errors));
        var doc = System.Xml.Linq.XDocument.Parse(xml.Value!.Content);
        Assert.Equal("MaterialDeclaration", doc.Root!.Name.LocalName);
        Assert.Contains(doc.Descendants("Substance"), s => (string?)s.Attribute("cas") == ComplianceEnv.Gold && (string?)s.Attribute("conflictFree") == "true");
        Assert.Equal(OutcomeKind.Invalid, (await env.Reports.ExportAsync(ComplianceEnv.Alice, ComplianceEnv.Request(part, lines), "pdf", null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Reports.ExportAsync(SourcingEnv.Sup1, ComplianceEnv.Request(part, lines), "cmrt", null)).Kind);
        var audit = await env.AuditAsync();
        Assert.Equal(2, audit.Count(e => e.Action == "compliance.export" && e.Outcome == AuditOutcome.Success));
    }
}
