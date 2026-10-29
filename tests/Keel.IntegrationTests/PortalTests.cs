using System.Text;
using Keel.Audit;
using Keel.Core;
using Keel.Sourcing;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Requests to suppliers, the supplier portal and its isolation: a supplier user can never see another supplier or an internal object (ADR-0037).</summary>
[Collection(PostgresCollection.Name)]
public sealed class PortalTests(PostgresFixture pg)
{
    private static Subject Alice => SourcingEnv.Alice;

    private sealed record World(SourcingEnv Env, string S1, string S2, string Part, string Part2);

    /// <summary>Two approved US suppliers with the DoD contractor profile (cleared for program ORCA) and one user each.</summary>
    private async Task<World> WorldAsync(Markings? partMarkings = null)
    {
        var env = await SourcingEnv.CreateAsync(pg);
        await env.SetProfileProgramsAsync("dod-contractor", "ORCA");
        var s1 = await env.NewSupplierAsync("Alpha Works", profile: "dod-contractor");
        var s2 = await env.NewSupplierAsync("Bravo Works", profile: "dod-contractor");
        await env.LinkPortalUserAsync(s1, SourcingEnv.Sup1);
        await env.LinkPortalUserAsync(s2, SourcingEnv.Sup2);
        var part = await env.NewPartAsync("Housing", partMarkings ?? SourcingEnv.Itar());
        var part2 = await env.NewPartAsync("Cover", partMarkings ?? SourcingEnv.Itar());
        return new World(env, s1, s2, part, part2);
    }

    [Fact]
    public async Task A_supplier_user_sees_only_requests_addressed_to_its_own_supplier()
    {
        var w = await WorldAsync();
        var env = w.Env;
        var forS1 = await env.IssuedRequestAsync("Quote for S1", SourcingEnv.Itar(), [w.Part], [w.S1]);
        var forS2 = await env.IssuedRequestAsync("Quote for S2", SourcingEnv.Itar(), [w.Part], [w.S2]);
        var forBoth = await env.IssuedRequestAsync("Quote for both", SourcingEnv.Itar(), [w.Part, w.Part2], [w.S1, w.S2]);
        var draft = (await env.Requests.CreateAsync(Alice, SourcingEnv.Request("Draft", SourcingEnv.Itar(), [w.Part], [w.S1]), null)).Value!.Summary.Number;

        var list1 = (await env.Portal.ListRequestsAsync(SourcingEnv.Sup1, null)).Value!;
        Assert.Equal(new[] { forBoth, forS1 }.Order(), list1.Select(r => r.Number).Order());
        var list2 = (await env.Portal.ListRequestsAsync(SourcingEnv.Sup2, null)).Value!;
        Assert.Equal(new[] { forBoth, forS2 }.Order(), list2.Select(r => r.Number).Order());

        // Another supplier's request, a draft, and an unknown number are indistinguishable: not found.
        Assert.Equal(OutcomeKind.NotFound, (await env.Portal.GetRequestAsync(SourcingEnv.Sup1, forS2, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Portal.GetRequestAsync(SourcingEnv.Sup1, draft, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Portal.GetRequestAsync(SourcingEnv.Sup1, "RFQ-999999", null)).Kind);
        var own = (await env.Portal.GetRequestAsync(SourcingEnv.Sup1, forBoth, null)).Value!;
        Assert.Equal(2, own.Parts.Count);
        Assert.True(own.CanRespond);
        Assert.Contains("ITAR", own.Summary.Banner, StringComparison.Ordinal);
        // Their view is audited, and the first view is recorded for the buyer.
        Assert.Contains(await env.AuditAsync(), e => e.Action == "portal.request_read" && e.ObjectId == forBoth && e.Outcome == AuditOutcome.Success);
        Assert.NotNull((await env.Requests.GetAsync(Alice, forBoth, null)).Value!.Recipients.Single(r => r.SupplierNumber == w.S1).FirstViewedAt);
        Assert.Null((await env.Requests.GetAsync(Alice, forBoth, null)).Value!.Recipients.Single(r => r.SupplierNumber == w.S2).FirstViewedAt);
    }

    [Fact]
    public async Task A_supplier_user_reaches_no_internal_object_and_internal_users_and_unlinked_users_have_no_portal()
    {
        var w = await WorldAsync();
        var env = w.Env;
        var number = await env.IssuedRequestAsync("Quote", SourcingEnv.Itar(), [w.Part], [w.S1]);
        var sup = SourcingEnv.Sup1;

        // Internal services answer a supplier user as they answer anybody without a role: nothing, never an error that confirms existence.
        Assert.Equal(OutcomeKind.NotFound, (await env.Parts.GetAsync(sup, w.Part, null, null)).Kind);
        Assert.Equal(0, (await env.Parts.SearchAsync(sup, "", null, 50, 0, null)).Total);
        Assert.Equal(OutcomeKind.NotFound, (await env.Suppliers.GetAsync(sup, w.S1, null)).Kind);
        Assert.Equal(0, (await env.Suppliers.SearchAsync(sup, new SupplierSearch(), null)).Value!.Total);
        Assert.Equal(OutcomeKind.NotFound, (await env.Requests.GetAsync(sup, number, null)).Kind);
        Assert.Equal(0, (await env.Requests.SearchAsync(sup, new RequestSearch(), null)).Value!.Total);
        Assert.Equal(OutcomeKind.NotFound, (await env.Sourcing.GetPartSourcingAsync(sup, w.Part, null)).Kind);
        Assert.Empty((await env.Sourcing.CoverageAsync(sup, new Dictionary<string, string?> { [w.Part] = "A" }, null)).Value!);
        Assert.Equal(0, (await env.Sourcing.WhereSourcedAsync(sup, new WhereSourcedQuery(SupplierNumber: w.S1), null)).Value!.Total);
        Assert.Equal(OutcomeKind.NotFound, (await env.Requests.OpenResponseFileAsync(sup, number, Guid.NewGuid(), null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Sourcing.CreateEntryAsync(sup, new CreateEntryCommand(SourceKinds.Avl, w.Part, null, w.S1, null, null, EntryStatuses.Approved), null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Suppliers.ProposeStatusAsync(sup, w.S1, SupplierStatuses.Approved, null)).Kind);

        // The portal refuses people with internal roles, even with a link, and users nobody linked.
        foreach (var internalUser in new[] { Alice, SourcingEnv.Ada, SourcingEnv.Bob, SourcingEnv.Audrey, SourcingEnv.Mallory })
            Assert.Equal(OutcomeKind.Forbidden, (await env.Portal.ListRequestsAsync(internalUser, null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Portal.ListRequestsAsync(SourcingEnv.Sup3, null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Portal.GetRequestAsync(SourcingEnv.Sup3, number, null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Portal.RespondAsync(SourcingEnv.Sup3, number, new PortalResponseInput("hi", false, []), [], null)).Kind);
        var denials = (await env.AuditAsync()).Where(e => e.Outcome == AuditOutcome.Denied && e.Action.StartsWith("portal.", StringComparison.Ordinal)).ToList();
        Assert.True(denials.Count >= 8);
        // A link can be removed: access ends at once. A supplier that is no longer active has no portal.
        Assert.True((await env.Suppliers.UnlinkPortalUserAsync(SourcingEnv.Ada, w.S1, SourcingEnv.Sup1.Id, null)).IsOk);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Portal.ListRequestsAsync(SourcingEnv.Sup1, null)).Kind);
        Assert.True((await env.Suppliers.SetStatusAsync(Alice, w.S2, SupplierStatuses.Disapproved, "Failed audit", null)).IsOk);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Portal.ListRequestsAsync(SourcingEnv.Sup2, null)).Kind);
    }

    [Fact]
    public async Task What_a_supplier_sees_follows_its_clearance_profile_and_a_foreign_supplier_never_sees_ITAR_data()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        await env.SetProfileProgramsAsync("dod-contractor", "ORCA");
        var cleared = await env.NewSupplierAsync("Cleared Co", profile: "dod-contractor");
        var noProfile = await env.NewSupplierAsync("No Profile Co");
        var publicOnly = await env.NewSupplierAsync("Public Co", profile: "public-release");
        var foreign = await env.NewSupplierAsync("Foreign Co", "GBR", foreign: true, profile: "foreign-government-taa-0042");
        await env.LinkPortalUserAsync(cleared, SourcingEnv.Sup1);
        await env.LinkPortalUserAsync(noProfile, SourcingEnv.Sup2);
        await env.LinkPortalUserAsync(publicOnly, SourcingEnv.Sup3);
        var itar = await env.NewPartAsync("Housing", SourcingEnv.Itar());
        var open = await env.NewPartAsync("Washer");

        // The profile of a foreign supplier even lists the agreement for ITAR data, and the request still cannot be made for it.
        var refused = await env.Requests.CreateAsync(Alice, SourcingEnv.Request("ITAR to foreign", SourcingEnv.Itar(), [itar], [foreign]), null);
        Assert.Equal(OutcomeKind.Forbidden, refused.Kind);
        Assert.Equal([SourcingRules.ForeignSupplierItarPart], refused.Errors);
        // An open request is fine for everyone with a profile; the one without a profile cannot receive anything.
        var openRequest = await env.IssuedRequestAsync("Washers", SourcingEnv.Open, [open], [cleared, publicOnly, foreign]);
        Assert.Equal([openRequest], (await env.Portal.ListRequestsAsync(SourcingEnv.Sup1, null)).Value!.Select(r => r.Number));
        Assert.Equal([openRequest], (await env.Portal.ListRequestsAsync(SourcingEnv.Sup3, null)).Value!.Select(r => r.Number));
        var created = await env.Requests.CreateAsync(Alice, SourcingEnv.Request("To nobody", SourcingEnv.Open, [open], [noProfile]), null);
        var issued = await env.Requests.IssueAsync(Alice, created.Value!.Summary.Number, null);
        Assert.Equal(OutcomeKind.Conflict, issued.Kind); // no recipient can receive it
        Assert.Empty((await env.Portal.ListRequestsAsync(SourcingEnv.Sup2, null)).Value!);

        // ITAR data: cleared only for the supplier whose profile allows it (program ORCA).
        var itarRequest = await env.IssuedRequestAsync("Housings", SourcingEnv.Itar(), [itar], [cleared, publicOnly]);
        Assert.Contains(itarRequest, (await env.Portal.ListRequestsAsync(SourcingEnv.Sup1, null)).Value!.Select(r => r.Number));
        Assert.DoesNotContain(itarRequest, (await env.Portal.ListRequestsAsync(SourcingEnv.Sup3, null)).Value!.Select(r => r.Number));
        Assert.Equal(OutcomeKind.NotFound, (await env.Portal.GetRequestAsync(SourcingEnv.Sup3, itarRequest, null)).Kind);
        var detail = (await env.Requests.GetAsync(Alice, itarRequest, null)).Value!;
        Assert.True(detail.Recipients.Single(r => r.SupplierNumber == cleared).Releasable);
        Assert.False(detail.Recipients.Single(r => r.SupplierNumber == publicOnly).Releasable);

        // A profile that stops allowing the program takes effect at the next view (nothing is cached).
        await env.SetProfileProgramsAsync("dod-contractor");
        Assert.DoesNotContain(itarRequest, (await env.Portal.ListRequestsAsync(SourcingEnv.Sup1, null)).Value!.Select(r => r.Number));
        Assert.Equal(OutcomeKind.NotFound, (await env.Portal.GetRequestAsync(SourcingEnv.Sup1, itarRequest, null)).Kind);
    }

    [Fact]
    public async Task A_part_whose_markings_were_raised_after_the_request_drops_out_of_the_suppliers_view()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var supplier = await env.NewSupplierAsync("Public Co", profile: "public-release");
        await env.LinkPortalUserAsync(supplier, SourcingEnv.Sup1);
        var a = await env.NewPartAsync("Washer");
        var b = await env.NewPartAsync("Nut");
        var number = await env.IssuedRequestAsync("Fasteners", SourcingEnv.Open, [a, b], [supplier]);
        Assert.Equal(2, (await env.Portal.GetRequestAsync(SourcingEnv.Sup1, number, null)).Value!.Parts.Count);
        await env.Db.ExecuteAsSuperuserAsync($"UPDATE plm.part_master SET cui_categories = ARRAY['CTI'], distribution_statement = 'C', controlling_office = 'X', distribution_reason = 'r' WHERE number = '{b}'");
        var after = (await env.Portal.GetRequestAsync(SourcingEnv.Sup1, number, null)).Value!;
        Assert.Equal([a], after.Parts.Select(p => p.PartNumber));
        // And the supplier cannot quote a part it cannot see.
        var quote = await env.Portal.RespondAsync(SourcingEnv.Sup1, number, new PortalResponseInput("", false, [new QuoteLine(b, 1m, "USD", 5, null)]), [], null);
        Assert.Equal(OutcomeKind.Invalid, quote.Kind);
        await env.Db.ExecuteAsSuperuserAsync($"UPDATE plm.part_master SET cui_categories = ARRAY['CTI'], distribution_statement = 'C', controlling_office = 'X', distribution_reason = 'r' WHERE number = '{a}'");
        Assert.Equal(OutcomeKind.NotFound, (await env.Portal.GetRequestAsync(SourcingEnv.Sup1, number, null)).Kind); // nothing left to show
    }

    [Fact]
    public async Task A_response_with_files_is_stored_scanned_immutable_and_visible_only_to_its_supplier_and_the_buyer()
    {
        var w = await WorldAsync(SourcingEnv.Open);
        var env = w.Env;
        var number = await env.IssuedRequestAsync("Quote", SourcingEnv.Open, [w.Part], [w.S1, w.S2]);
        static UploadedFile File(string name, string text, string kind = "response") => new(name, kind, new MemoryStream(Encoding.UTF8.GetBytes(text)));

        var ok = await env.Portal.RespondAsync(SourcingEnv.Sup1, number, new PortalResponseInput("Secret pricing note XYZ", false, [new QuoteLine(w.Part, 2.5m, "USD", 21, 100)]),
            [File("../../quote.pdf", "%PDF quote"), File("AS9100.pdf", "certificate bytes", "certificate")], "corr-resp");
        Assert.True(ok.IsOk, string.Join("; ", ok.Errors));
        Assert.Equal(1, ok.Value!.Seq);
        Assert.Equal(["AS9100.pdf", "quote.pdf"], ok.Value.Files.Select(f => f.FileName).Order()); // no path survives
        Assert.Equal(2, env.Store.Objects.Count);

        // A file the scanner rejects stops the whole response: nothing is recorded and the denial is audited without the file name.
        var before = await env.ScalarAsync("SELECT count(*) FROM src.response");
        var infected = await env.Portal.RespondAsync(SourcingEnv.Sup1, number, new PortalResponseInput("Second try", false, []), [File("invoice.pdf", "X5O! EICAR test file")], null);
        Assert.Equal(OutcomeKind.Unprocessable, infected.Kind);
        Assert.Equal(before, await env.ScalarAsync("SELECT count(*) FROM src.response"));
        Assert.Contains(await env.AuditAsync(), e => e.Action == "portal.upload_refused" && e.Outcome == AuditOutcome.Denied && !e.Payload.Contains("invoice", StringComparison.Ordinal));
        // Too many files, too large a file and a bad kind are refused.
        Assert.Equal(OutcomeKind.Invalid, (await env.Portal.RespondAsync(SourcingEnv.Sup1, number, new PortalResponseInput("x", false, []), [.. Enumerable.Range(0, 6).Select(i => File($"f{i}.txt", "a"))], null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Portal.RespondAsync(SourcingEnv.Sup1, number, new PortalResponseInput("x", false, []), [File("a.txt", "a", "invoice")], null)).Kind);

        // A later response counts; the first stays in the history. A decline carries no quote or files.
        var second = await env.Portal.RespondAsync(SourcingEnv.Sup1, number, new PortalResponseInput("Revised", false, [new QuoteLine(w.Part, 2.2m, "USD", 14, null)]), [], null);
        Assert.Equal(2, second.Value!.Seq);
        Assert.Equal([2, 1], (await env.Portal.GetRequestAsync(SourcingEnv.Sup1, number, null)).Value!.Responses.Select(r => r.Seq));
        Assert.Equal(OutcomeKind.Invalid, (await env.Portal.RespondAsync(SourcingEnv.Sup2, number, new PortalResponseInput("", true, [new QuoteLine(w.Part, 1m, null, null, null)]), [], null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Portal.RespondAsync(SourcingEnv.Sup2, number, new PortalResponseInput("", false, []), [], null)).Kind); // nothing to say
        Assert.True((await env.Portal.RespondAsync(SourcingEnv.Sup2, number, new PortalResponseInput("We cannot make this", true, []), [], null)).IsOk);

        // Supplier two sees none of supplier one's responses or files, and cannot fetch them by id.
        Assert.DoesNotContain("Revised", string.Join(" ", (await env.Portal.GetRequestAsync(SourcingEnv.Sup2, number, null)).Value!.Responses.Select(r => r.Message)), StringComparison.Ordinal);
        var fileId = ok.Value.Files[0].Id;
        Assert.Equal(OutcomeKind.NotFound, (await env.Portal.OpenMyFileAsync(SourcingEnv.Sup2, number, fileId, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Portal.OpenMyFileAsync(SourcingEnv.Sup1, "RFQ-000999", fileId, null)).Kind);
        var mine = await env.Portal.OpenMyFileAsync(SourcingEnv.Sup1, number, fileId, null);
        Assert.True(mine.IsOk);
        await mine.Value.Content.DisposeAsync();
        // The buyer sees both suppliers' responses and states, and may download; audits carry counts only.
        var buyer = (await env.Requests.GetAsync(Alice, number, null)).Value!;
        Assert.Equal(3, buyer.Responses.Count);
        Assert.Equal([RecipientStates.Responded, RecipientStates.Declined], buyer.Recipients.OrderBy(r => r.SupplierNumber).Select(r => r.State));
        Assert.True((await env.Requests.OpenResponseFileAsync(Alice, number, fileId, null)).IsOk);
        var all = string.Join("\n", (await env.AuditAsync()).Select(e => e.Payload));
        foreach (var secret in new[] { "Secret pricing note", "quote.pdf", "AS9100.pdf", "Revised", "We cannot make this" }) Assert.DoesNotContain(secret, all, StringComparison.Ordinal);

        // Responses are immutable, also for the application role; a closed request takes no more.
        await using (var conn = new NpgsqlConnection(env.Db.AppConnectionString))
        {
            await conn.OpenAsync();
            await using var upd = new NpgsqlCommand("UPDATE src.response SET message = 'x'", conn);
            await Assert.ThrowsAsync<PostgresException>(() => upd.ExecuteNonQueryAsync());
            await using var del = new NpgsqlCommand("DELETE FROM src.response_file", conn);
            await Assert.ThrowsAsync<PostgresException>(() => del.ExecuteNonQueryAsync());
        }
        Assert.True((await env.Requests.CloseAsync(Alice, number, null)).IsOk);
        Assert.Equal(OutcomeKind.Conflict, (await env.Portal.RespondAsync(SourcingEnv.Sup1, number, new PortalResponseInput("late", false, []), [], null)).Kind);
        Assert.False((await env.Portal.GetRequestAsync(SourcingEnv.Sup1, number, null)).Value!.CanRespond);
        // The internal download route is not a way in for a supplier user either.
        Assert.Equal(OutcomeKind.NotFound, (await env.Requests.OpenResponseFileAsync(SourcingEnv.Sup1, number, fileId, null)).Kind);
    }

    [Fact]
    public async Task A_request_must_be_covered_by_its_markings_and_only_its_authors_with_the_right_may_change_it()
    {
        var w = await WorldAsync();
        var env = w.Env;
        // Open markings do not cover an ITAR part: the request would leak the part's existence to people who may not read it.
        var low = await env.Requests.CreateAsync(Alice, SourcingEnv.Request("Low", SourcingEnv.Open, [w.Part], [w.S1]), null);
        Assert.Equal(OutcomeKind.Invalid, low.Kind);
        Assert.Contains("do not cover", low.Errors[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await env.Requests.CreateAsync(Alice, SourcingEnv.Request("None", SourcingEnv.Itar(), [], [w.S1]), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Requests.CreateAsync(Alice, SourcingEnv.Request("Unknown", SourcingEnv.Itar(), ["P-999999"], [w.S1]), null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Requests.CreateAsync(SourcingEnv.Mallory, SourcingEnv.Request("Viewer", SourcingEnv.Itar(), [w.Part], [w.S1]), null)).Kind);

        var number = (await env.Requests.CreateAsync(Alice, SourcingEnv.Request("Quote", SourcingEnv.Itar(), [w.Part], [w.S1]), null)).Value!.Summary.Number;
        // Eve (no program) cannot see the request at all; Mallory can read but not change it.
        Assert.Equal(OutcomeKind.NotFound, (await env.Requests.GetAsync(SourcingEnv.Eve, number, null)).Kind);
        Assert.Equal(0, (await env.Requests.SearchAsync(SourcingEnv.Eve, new RequestSearch(), null)).Value!.Total);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Requests.IssueAsync(SourcingEnv.Mallory, number, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Requests.IssueAsync(SourcingEnv.Eve, number, null)).Kind);
        Assert.True((await env.Requests.IssueAsync(Alice, number, null)).IsOk);
        Assert.Equal(OutcomeKind.Conflict, (await env.Requests.IssueAsync(Alice, number, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Requests.CancelAsync(Alice, number, "", null)).Kind);
        Assert.True((await env.Requests.CancelAsync(Alice, number, "Part obsolete", null)).IsOk);
        Assert.Empty((await env.Portal.ListRequestsAsync(SourcingEnv.Sup1, null)).Value!); // cancelled: gone from the portal
        Assert.Contains(await env.AuditAsync(), e => e.Action == "rfq.issue" && e.ObjectId == number);
    }

    [Fact]
    public async Task The_database_refuses_a_foreign_recipient_for_an_ITAR_part_and_an_ITAR_part_for_a_foreign_recipient()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var foreign = await env.NewSupplierAsync("Foreign Co", "GBR", foreign: true);
        var domestic = await env.NewSupplierAsync("Domestic Co");
        var itar = await env.NewPartAsync("Housing", SourcingEnv.Itar());
        var request = (await env.Requests.CreateAsync(Alice, SourcingEnv.Request("Quote", SourcingEnv.Itar(), [itar], [domestic]), null)).Value!.Summary.Number;
        await using var conn = new NpgsqlConnection(env.Db.AppConnectionString);
        await conn.OpenAsync();
        await using var recipient = new NpgsqlCommand("INSERT INTO src.request_recipient (request_id, supplier_id, state, added_at) SELECT r.id, s.id, 'Sent', now() FROM src.request r, src.supplier s WHERE r.number = @r AND s.number = @s", conn);
        recipient.Parameters.AddWithValue("r", request);
        recipient.Parameters.AddWithValue("s", foreign);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => recipient.ExecuteNonQueryAsync())).SqlState);
    }
}
