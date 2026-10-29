using Keel.Audit;
using Keel.Core;
using Keel.Sourcing;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>The supplier master: records, certifications, contacts, approval through the review engine, audit hygiene, access (ADR-0037).</summary>
[Collection(PostgresCollection.Name)]
public sealed class SupplierTests(PostgresFixture pg)
{
    private static Subject Alice => SourcingEnv.Alice;

    [Fact]
    public async Task A_supplier_is_created_prospective_numbered_audited_and_found_by_search()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var number = await env.NewSupplierAsync("Alpine Machining", cage: "1A2B3");
        Assert.Equal("SUP-000001", number);
        var detail = (await env.Suppliers.GetAsync(Alice, number, "corr-get")).Value!;
        Assert.Equal(SupplierStatuses.Prospective, detail.Summary.Status);
        Assert.True(detail.Permissions.CanModify);
        Assert.Contains(env.Sink.Watches, w => w.Type == "supplier" && w.Number == number); // the creator follows the supplier

        var found = (await env.Suppliers.SearchAsync(Alice, new SupplierSearch(Text: "alpine"), null)).Value!;
        Assert.Equal(1, found.Total);
        Assert.Equal(0, (await env.Suppliers.SearchAsync(Alice, new SupplierSearch(Text: "alpine", Status: "approved"), null)).Value!.Total);
        Assert.Equal(1, (await env.Suppliers.SearchAsync(Alice, new SupplierSearch(Text: "1A2B3"), null)).Value!.Total);
        var sorted = (await env.Suppliers.SearchAsync(Alice, new SupplierSearch(Sort: new Core.SortSpec("name", true)), null)).Value!;
        Assert.Single(sorted.Items);

        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.Action == "supplier.create" && e.ObjectId == number && e.Outcome == AuditOutcome.Success);
        // A duplicate CAGE code is refused; so are invalid data and a foreign country that is not flagged foreign.
        var dup = await env.Suppliers.CreateAsync(Alice, new CreateSupplierCommand("Other", "USA", ["vendor"], false, SourcingEnv.Open, "1A2B3"), null);
        Assert.Equal(OutcomeKind.Conflict, dup.Kind);
        var bad = await env.Suppliers.CreateAsync(Alice, new CreateSupplierCommand("Berlin", "DEU", ["vendor"], false, SourcingEnv.Open), null);
        Assert.Equal(OutcomeKind.Invalid, bad.Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Suppliers.CreateAsync(Alice, new CreateSupplierCommand("X", "USA", [], false, SourcingEnv.Open), null)).Kind);
    }

    [Fact]
    public async Task Only_a_supplier_with_a_valid_certification_can_be_proposed_for_approval_and_a_review_with_signatures_grants_it()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var number = await env.NewSupplierAsync();
        var none = await env.Suppliers.ProposeStatusAsync(Alice, number, SupplierStatuses.Approved, null);
        Assert.Equal(OutcomeKind.Invalid, none.Kind);
        Assert.Contains("certification", none.Errors[0], StringComparison.Ordinal);
        // Without a proposal the review does not start.
        var unproposed = await env.Flow.StartAsync(Alice, new Keel.Workflow.StartReviewCommand("supplier", number, new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob"] }), null);
        Assert.Equal(OutcomeKind.Conflict, unproposed.Kind);

        await env.ApproveSupplierAsync(number);
        var approved = (await env.Suppliers.GetAsync(Alice, number, null)).Value!;
        Assert.Equal(SupplierStatuses.Approved, approved.Summary.Status);
        Assert.False(approved.Summary.InReview);
        Assert.Null(approved.ProposedStatus);
        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.Action == "supplier.approve" && e.ObjectId == number);
        Assert.Equal(3, await env.ScalarAsync("SELECT count(*) FROM wf.signature WHERE object_type = 'supplier'")); // reviewer and both approvers signed
        Assert.Contains(env.Sink.Requests, n => n.Kind == NotificationKindCodes.SupplierStatusChanged && n.ObjectType == "supplier" && n.ObjectNumber == number);
    }

    [Fact]
    public async Task A_rejected_review_leaves_the_status_and_clears_the_proposal()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var number = await env.NewSupplierAsync();
        await env.AddCertificationAsync(number);
        Assert.True((await env.Suppliers.ProposeStatusAsync(Alice, number, SupplierStatuses.Approved, null)).IsOk);
        var started = await env.Flow.StartAsync(Alice, new Keel.Workflow.StartReviewCommand("supplier", number, new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob"] }), null);
        Assert.True(started.IsOk, string.Join("; ", started.Errors));
        Assert.True((await env.Suppliers.GetAsync(Alice, number, null)).Value!.Summary.InReview);
        // In review the record is frozen.
        Assert.Equal(OutcomeKind.Conflict, (await env.Suppliers.SetStatusAsync(Alice, number, SupplierStatuses.Disapproved, "no", null)).Kind);
        var v = await env.SignAsync(SourcingEnv.Rita, started.Value!.Tasks.Single(t => t.Status == "Open").Id, "Reviewed");
        var rejected = await env.Flow.SignAsync(Keel.IntegrationTests.WorkflowEnv.Fresh(SourcingEnv.Bob), Keel.IntegrationTests.WorkflowEnv.TaskOf(v, "bob"), "Rejected", "Audit finding open", null);
        Assert.True(rejected.IsOk, string.Join("; ", rejected.Errors));
        var after = (await env.Suppliers.GetAsync(Alice, number, null)).Value!;
        Assert.Equal(SupplierStatuses.Prospective, after.Summary.Status);
        Assert.False(after.Summary.InReview);
        Assert.Null(after.ProposedStatus);
    }

    [Fact]
    public async Task Disapproval_and_obsolescence_need_a_reason_and_obsolete_is_final()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var number = await env.NewSupplierAsync();
        await env.ApproveSupplierAsync(number);
        Assert.Equal(OutcomeKind.Invalid, (await env.Suppliers.SetStatusAsync(Alice, number, SupplierStatuses.Disapproved, "", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Suppliers.SetStatusAsync(Alice, number, SupplierStatuses.Approved, "back", null)).Kind);
        var dis = await env.Suppliers.SetStatusAsync(Alice, number, SupplierStatuses.Disapproved, "Failed audit", null);
        Assert.Equal(SupplierStatuses.Disapproved, dis.Value!.Summary.Status);
        var obs = await env.Suppliers.SetStatusAsync(Alice, number, SupplierStatuses.Obsolete, "Out of business", null);
        Assert.Equal(SupplierStatuses.Obsolete, obs.Value!.Summary.Status);
        Assert.Equal(OutcomeKind.Invalid, (await env.Suppliers.SetStatusAsync(Alice, number, SupplierStatuses.Disapproved, "again", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Suppliers.ProposeStatusAsync(Alice, number, SupplierStatuses.Conditional, null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Suppliers.AddCertificationAsync(Alice, number, new AddCertificationCommand("ISO9001", "X", "Y", null, new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1)), null)).Kind);
    }

    [Fact]
    public async Task Certification_expiry_notices_are_raised_once_per_stage_audited_and_pointer_only()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var number = await env.NewSupplierAsync();
        await env.AddCertificationAsync(number, validForDays: 50, type: "NADCAP");
        await env.AddCertificationAsync(number, validForDays: 900, type: "ISO9001");

        Assert.Equal(1, await env.Expiry.RunOnceAsync()); // inside 60 days
        Assert.Equal(0, await env.Expiry.RunOnceAsync()); // idempotent
        env.Clock.Advance(TimeSpan.FromDays(25)); // 25 days left
        Assert.Equal(1, await env.Expiry.RunOnceAsync()); // d30
        env.Clock.Advance(TimeSpan.FromDays(20)); // 5 days left
        Assert.Equal(1, await env.Expiry.RunOnceAsync()); // d7
        env.Clock.Advance(TimeSpan.FromDays(10)); // expired
        Assert.Equal(1, await env.Expiry.RunOnceAsync());
        Assert.Equal(0, await env.Expiry.RunOnceAsync());

        var notices = (await env.AuditAsync()).Where(e => e.Action == "supplier.cert_expiry_notice").ToList();
        Assert.Equal(4, notices.Count);
        Assert.All(notices, n => { Assert.Equal(number, n.ObjectId); Assert.Contains("NADCAP", n.Payload, StringComparison.Ordinal); Assert.DoesNotContain("Alpine", n.Payload, StringComparison.Ordinal); });
        var requests = env.Sink.Requests.Where(r => r.Kind is NotificationKindCodes.SupplierCertExpiring or NotificationKindCodes.SupplierCertExpired).ToList();
        Assert.Equal(3, requests.Count(r => r.Kind == NotificationKindCodes.SupplierCertExpiring));
        Assert.Equal(1, requests.Count(r => r.Kind == NotificationKindCodes.SupplierCertExpired));
        Assert.All(requests, r => Assert.Equal(("supplier", number), (r.ObjectType, r.ObjectNumber)));

        var detail = (await env.Suppliers.GetAsync(Alice, number, null)).Value!;
        Assert.Equal(1, detail.ExpiredCertifications);
        // A revoked certification is not noticed.
        var cert = detail.Certifications.Single(c => c.Type == "ISO9001");
        Assert.True((await env.Suppliers.RevokeCertificationAsync(Alice, number, cert.Id, "Registrar withdrew it", null)).IsOk);
        env.Clock.Advance(TimeSpan.FromDays(2000));
        await env.Expiry.RunOnceAsync();
        Assert.Equal(4, (await env.AuditAsync()).Count(e => e.Action == "supplier.cert_expiry_notice"));
    }

    [Fact]
    public async Task Contact_data_and_names_never_reach_the_audit_log_and_a_primary_contact_is_unique()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var number = await env.NewSupplierAsync("Zephyr Secret Foundry Name");
        var added = await env.Suppliers.AddContactAsync(Alice, number, new ContactInput("Quentin Contact", "quentin@zephyr.example", "+1 555 0100", "Quality manager", true), null);
        Assert.True(added.IsOk, string.Join("; ", added.Errors));
        Assert.True((await env.Suppliers.AddContactAsync(Alice, number, new ContactInput("Second Person", null, null, null, true), null)).IsOk);
        var detail = (await env.Suppliers.GetAsync(Alice, number, null)).Value!;
        Assert.Equal(2, detail.Contacts.Count);
        Assert.Single(detail.Contacts, c => c.IsPrimary);
        Assert.Equal(OutcomeKind.Invalid, (await env.Suppliers.AddContactAsync(Alice, number, new ContactInput("Bad", "no-at-sign", null, null, false), null)).Kind);
        Assert.True((await env.Suppliers.AddQualityRefAsync(Alice, number, "ncr", "NCR-000123", null)).IsOk);
        await env.Suppliers.SearchAsync(Alice, new SupplierSearch(Text: "Zephyr"), null);

        var all = string.Join("\n", (await env.AuditAsync()).Select(e => e.Payload));
        foreach (var secret in new[] { "Zephyr", "Quentin", "zephyr.example", "555 0100", "Second Person" })
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        Assert.Contains("NCR-000123", all, StringComparison.Ordinal); // the opaque reference id is an identifier
        Assert.Equal("NCR-000123", (await env.Suppliers.GetAsync(Alice, number, null)).Value!.QualityRefs.Single().RefId);
    }

    [Fact]
    public async Task Marked_suppliers_are_invisible_to_those_who_may_not_read_them_and_denials_are_audited()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var restricted = await env.NewSupplierAsync("Restricted Works", markings: SourcingEnv.Itar());
        var open = await env.NewSupplierAsync("Open Works");

        Assert.Equal(OutcomeKind.NotFound, (await env.Suppliers.GetAsync(SourcingEnv.Eve, restricted, "corr-eve")).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Suppliers.GetAsync(SourcingEnv.Eve, "SUP-999999", null)).Kind);
        var page = (await env.Suppliers.SearchAsync(SourcingEnv.Eve, new SupplierSearch(), null)).Value!;
        Assert.Equal([open], page.Items.Select(i => i.Number));
        Assert.Equal(1, page.Total); // a count never includes what the caller cannot read
        Assert.Equal(OutcomeKind.NotFound, (await env.Suppliers.UpdateAsync(SourcingEnv.Eve, restricted, new UpdateSupplierCommand("X", "USA", ["vendor"], false, null, null, null), 1, null)).Kind);
        var denied = (await env.AuditAsync()).Where(e => e.Outcome == AuditOutcome.Denied && e.ObjectId == restricted).Select(e => e.Action).ToList();
        Assert.Contains("supplier.read", denied);
        Assert.Contains("supplier.update", denied);
        // A viewer may read but not modify; a supplier user and an integration account see nothing at all.
        Assert.Equal(OutcomeKind.Forbidden, (await env.Suppliers.AddContactAsync(SourcingEnv.Mallory, open, new ContactInput("N", null, null, null, false), null)).Kind);
        Assert.Equal(0, (await env.Suppliers.SearchAsync(SourcingEnv.Sup1, new SupplierSearch(), null)).Value!.Total);
        Assert.Equal(OutcomeKind.NotFound, (await env.Suppliers.GetAsync(SourcingEnv.Sup1, open, null)).Kind);
    }

    [Fact]
    public async Task The_clearance_profile_and_portal_links_are_administrator_decisions()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var number = await env.NewSupplierAsync();
        var detail = (await env.Suppliers.GetAsync(Alice, number, null)).Value!;
        var asAuthor = await env.Suppliers.UpdateAsync(Alice, number, new UpdateSupplierCommand("Alpine Machining", "USA", ["vendor"], false, null, null, "dod-contractor"), detail.Revision, null);
        Assert.Equal(OutcomeKind.Forbidden, asAuthor.Kind);
        var asAdmin = await env.Suppliers.UpdateAsync(SourcingEnv.Ada, number, new UpdateSupplierCommand("Alpine Machining", "USA", ["vendor"], false, null, null, "dod-contractor"), detail.Revision, null);
        Assert.True(asAdmin.IsOk, string.Join("; ", asAdmin.Errors));
        Assert.Equal(OutcomeKind.Invalid, (await env.Suppliers.UpdateAsync(SourcingEnv.Ada, number, new UpdateSupplierCommand("Alpine Machining", "USA", ["vendor"], false, null, null, "no-such-profile"), asAdmin.Value!.Revision, null)).Kind);
        // Stale edits are refused.
        Assert.Equal(OutcomeKind.Conflict, (await env.Suppliers.UpdateAsync(SourcingEnv.Ada, number, new UpdateSupplierCommand("Renamed", "USA", ["vendor"], false, null, null, "dod-contractor"), detail.Revision, null)).Kind);

        Assert.Equal(OutcomeKind.Forbidden, (await env.Suppliers.LinkPortalUserAsync(Alice, number, "sup1", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Suppliers.LinkPortalUserAsync(SourcingEnv.Ada, number, "alice", null)).Kind); // internal role
        Assert.Equal(OutcomeKind.Invalid, (await env.Suppliers.LinkPortalUserAsync(SourcingEnv.Ada, number, "nobody-has-signed-in", null)).Kind);
        await env.LinkPortalUserAsync(number, SourcingEnv.Sup1);
        var other = await env.NewSupplierAsync("Other Supplier");
        Assert.Equal(OutcomeKind.Conflict, (await env.Suppliers.LinkPortalUserAsync(SourcingEnv.Ada, other, "sup1", null)).Kind); // one supplier per user
        Assert.Single((await env.Suppliers.GetAsync(SourcingEnv.Ada, number, null)).Value!.PortalUsers);
        Assert.Empty((await env.Suppliers.GetAsync(Alice, number, null)).Value!.PortalUsers); // only administrators see the links
    }

    [Fact]
    public async Task The_signed_content_of_a_supplier_review_covers_its_certifications()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var number = await env.NewSupplierAsync();
        await env.AddCertificationAsync(number);
        Assert.True((await env.Suppliers.ProposeStatusAsync(Alice, number, SupplierStatuses.Approved, null)).IsOk);
        var started = await env.Flow.StartAsync(Alice, new Keel.Workflow.StartReviewCommand("supplier", number, new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob"] }), null);
        var v = await env.SignAsync(SourcingEnv.Rita, started.Value!.Tasks.Single(t => t.Status == "Open").Id, "Reviewed");
        // A certification revoked after the review began makes the approval impossible: the record signed is the record approved.
        var detail = (await env.Suppliers.GetAsync(Alice, number, null)).Value!;
        await using (var conn = new NpgsqlConnection(env.Db.SuperuserConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("UPDATE src.supplier_certification SET revoked = true", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var signed = await env.Flow.SignAsync(Keel.IntegrationTests.WorkflowEnv.Fresh(SourcingEnv.Bob), Keel.IntegrationTests.WorkflowEnv.TaskOf(v, "bob"), "Approved", null, null);
        Assert.False(signed.IsOk);
        Assert.Equal(SupplierStatuses.Prospective, (await env.Suppliers.GetAsync(Alice, number, null)).Value!.Summary.Status);
        Assert.Single(detail.Certifications);
    }
}
