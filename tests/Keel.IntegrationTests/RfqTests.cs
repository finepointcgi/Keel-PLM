using System.Text;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Keel.Sourcing;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// RFQs beyond the portal (9.6, ADR-0054): lines with need-by dates, released documents that a supplier receives whole or not at all, tiered quotes with validity, the side-by-side
/// comparison and awards that record the sourcing link. Real services over PostgreSQL; the only fake is the vault.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RfqTests(PostgresFixture pg)
{
    private static Subject Alice => SourcingEnv.Alice;

    private static Markings Stmt(DistributionStatement d) => Markings.Create(ExportJurisdiction.None, d, "ENG", controllingOffice: "ACME", distributionReason: "Reason").Value;
    private static readonly Markings StatementB = Stmt(DistributionStatement.B);
    private static readonly Markings StatementD = Stmt(DistributionStatement.D);

    private sealed record World(SourcingEnv Env, string S1, string S2, string P1, string P2);

    /// <summary>Two US suppliers with their own portal user (dod-contractor and dod-component profiles) and two purchased parts.</summary>
    private async Task<World> WorldAsync()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var s1 = await env.NewSupplierAsync("Alpha Works", profile: "dod-contractor");
        var s2 = await env.NewSupplierAsync("Bravo Works", profile: "dod-component");
        await env.LinkPortalUserAsync(s1, SourcingEnv.Sup1);
        await env.LinkPortalUserAsync(s2, SourcingEnv.Sup2);
        return new World(env, s1, s2, await env.NewPartAsync("Housing"), await env.NewPartAsync("Cover"));
    }

    private static DateOnly Today(SourcingEnv env) => DateOnly.FromDateTime(env.Clock.GetUtcNow().UtcDateTime);

    private static async Task<string> IssueAsync(SourcingEnv env, string title, Markings markings, IReadOnlyList<RequestPartInput> lines, IReadOnlyList<string> suppliers, IReadOnlyList<string>? documents = null, DateOnly? due = null)
    {
        var created = await env.Requests.CreateAsync(Alice, new CreateRequestCommand(RequestKinds.Rfq, title, "Please quote.", due, markings, lines, suppliers, documents), null);
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        var issued = await env.Requests.IssueAsync(Alice, created.Value!.Summary.Number, null);
        Assert.True(issued.IsOk, string.Join("; ", issued.Errors));
        return created.Value.Summary.Number;
    }

    private static async Task<PortalResponse> QuoteAsync(SourcingEnv env, Subject who, string number, DateOnly? validUntil, params QuoteLine[] lines)
    {
        var r = await env.Portal.RespondAsync(who, number, new PortalResponseInput("", false, lines, validUntil), [], null);
        Assert.True(r.IsOk, $"{who.Username}: {r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    private static RequestPartInput Line(string part, decimal qty, DateOnly? needBy = null) => new(part, qty, null, null, needBy);

    // ---------------------------------------------------------------- lines and documents

    [Fact]
    public async Task Lines_carry_a_need_by_date_and_only_released_readable_covered_documents_are_pinned_to_the_request()
    {
        var w = await WorldAsync();
        var env = w.Env;
        var needBy = Today(env).AddDays(60);
        var released = await env.NewDocumentAsync(SourcingEnv.Open);
        var inWork = await env.NewDocumentAsync(SourcingEnv.Open, "InWork");
        var itarDoc = await env.NewDocumentAsync(SourcingEnv.Itar());

        var created = await env.Requests.CreateAsync(Alice, new CreateRequestCommand(RequestKinds.Rfq, "Housings", "d", null, StatementD, [Line(w.P1, 25, needBy), Line(w.P2, 5)], [w.S1], [released.Number]), null);
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        var d = created.Value!;
        Assert.Equal(needBy, d.Parts.Single(p => p.PartNumber == w.P1).NeedBy);
        Assert.Null(d.Parts.Single(p => p.PartNumber == w.P2).NeedBy);
        var doc = Assert.Single(d.DocumentList);
        Assert.Equal((released.Number, "A", 1), (doc.DocumentNumber, doc.Revision, doc.Iteration));
        Assert.Equal(["housing.pdf"], doc.Files.Select(f => f.FileName));

        string[] Errors(Outcome<RequestDetail> o) => [.. o.Errors];
        var notReleased = await env.Requests.CreateAsync(Alice, new CreateRequestCommand(RequestKinds.Rfq, "t", "d", null, StatementD, [Line(w.P1, 1)], [w.S1], [inWork.Number]), null);
        Assert.Equal(OutcomeKind.Invalid, notReleased.Kind);
        Assert.Contains("no released version", Errors(notReleased)[0], StringComparison.Ordinal);
        var notCovered = await env.Requests.CreateAsync(Alice, new CreateRequestCommand(RequestKinds.Rfq, "t", "d", null, StatementD, [Line(w.P1, 1)], [w.S1], [itarDoc.Number]), null);
        Assert.Equal(OutcomeKind.Invalid, notCovered.Kind);
        Assert.Contains("do not cover document", Errors(notCovered)[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await env.Requests.CreateAsync(Alice, new CreateRequestCommand(RequestKinds.Rfq, "t", "d", null, StatementD, [Line(w.P1, 1)], [w.S1], ["D-999999"]), null)).Kind);
        // A document the author cannot read is "no such document", the same as one that does not exist.
        var orca = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG", program: "ORCA").Value;
        var hidden = await env.NewDocumentAsync(orca);
        var byEve = await env.Requests.CreateAsync(SourcingEnv.Eve, new CreateRequestCommand(RequestKinds.Rfq, "t", "d", null, SourcingEnv.Open, [Line(w.P1, 1)], [w.S1], [hidden.Number]), null);
        Assert.Equal(OutcomeKind.Invalid, byEve.Kind);
        Assert.Equal(Errors(byEve)[0], Errors(await env.Requests.CreateAsync(SourcingEnv.Eve, new CreateRequestCommand(RequestKinds.Rfq, "t", "d", null, SourcingEnv.Open, [Line(w.P1, 1)], [w.S1], ["D-999999"]), null))[0].Replace("D-999999", hidden.Number));

        var audit = (await env.AuditAsync()).Single(e => e.Action == "rfq.create" && e.ObjectId == d.Summary.Number);
        Assert.Equal("1", audit.Details()["documents"]);
        Assert.DoesNotContain(audit.Details().Values, v => v is not null && (v.Contains("Housing", StringComparison.Ordinal) || v.Contains("housing.pdf", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_supplier_receives_only_the_documents_its_profile_allows_and_a_refused_file_is_not_found_never_redacted()
    {
        var w = await WorldAsync();
        var env = w.Env;
        var open = await env.NewDocumentAsync(SourcingEnv.Open, content: "Open drawing", fileName: "open.pdf");
        var restricted = await env.NewDocumentAsync(StatementB, content: "Restricted drawing", fileName: "restricted.pdf"); // statement B: U.S. Government agencies only
        var number = await IssueAsync(env, "Housings", StatementD, [Line(w.P1, 10)], [w.S1, w.S2], [open.Number, restricted.Number]);

        var sup1 = (await env.Portal.GetRequestAsync(SourcingEnv.Sup1, number, null)).Value!;
        Assert.Equal([open.Number], sup1.DocumentList.Select(x => x.DocumentNumber)); // the DoD contractor profile does not allow statement B
        var sup2 = (await env.Portal.GetRequestAsync(SourcingEnv.Sup2, number, null)).Value!;
        Assert.Equal(new[] { open.Number, restricted.Number }.Order(), sup2.DocumentList.Select(x => x.DocumentNumber).Order());

        // Whole file or nothing: the bytes are the stored bytes, and a refused file answers like a file that does not exist.
        var ok = await env.Portal.OpenDocumentFileAsync(SourcingEnv.Sup1, number, open.FileId, null);
        Assert.True(ok.IsOk);
        using (var ms = new MemoryStream()) { await ok.Value.Content.CopyToAsync(ms); Assert.Equal(open.Bytes, ms.ToArray()); }
        var refused = await env.Portal.OpenDocumentFileAsync(SourcingEnv.Sup1, number, restricted.FileId, null);
        var unknown = await env.Portal.OpenDocumentFileAsync(SourcingEnv.Sup1, number, Guid.NewGuid(), null);
        Assert.Equal(OutcomeKind.NotFound, refused.Kind);
        Assert.Equal(unknown.Kind, refused.Kind);
        Assert.Equal(unknown.Errors, refused.Errors);
        Assert.True((await env.Portal.OpenDocumentFileAsync(SourcingEnv.Sup2, number, restricted.FileId, null)).IsOk);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Portal.OpenDocumentFileAsync(SourcingEnv.Sup3, number, open.FileId, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Portal.OpenDocumentFileAsync(SourcingEnv.Sup1, "RFQ-999999", open.FileId, null)).Kind);
        // Another request's documents are not reachable through this request, and an internal user has no portal.
        var other = await IssueAsync(env, "Other", StatementD, [Line(w.P2, 1)], [w.S2], [restricted.Number]);
        Assert.Equal(OutcomeKind.NotFound, (await env.Portal.OpenDocumentFileAsync(SourcingEnv.Sup2, number, Guid.Parse("00000000-0000-0000-0000-000000000001"), null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Portal.OpenDocumentFileAsync(SourcingEnv.Sup1, other, restricted.FileId, null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Portal.OpenDocumentFileAsync(Alice, number, open.FileId, null)).Kind);
        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.Action == "portal.document_download" && e.Outcome == AuditOutcome.Denied && e.ObjectId == number);
        Assert.Contains(audit, e => e.Action == "portal.document_download" && e.Outcome == AuditOutcome.Success && e.ObjectId == number);

        // The buyer sees who does not receive what, by supplier number.
        var buyer = (await env.Requests.GetAsync(Alice, number, null)).Value!;
        Assert.Equal([w.S1], buyer.DocumentList.Single(x => x.DocumentNumber == restricted.Number).Withheld);
        Assert.Empty(buyer.DocumentList.Single(x => x.DocumentNumber == open.Number).Withheld);

        // A document whose markings were raised after the request (statement F is never released automatically) leaves every supplier's view at once.
        await env.Db.ExecuteAsSuperuserAsync($"UPDATE plm.document_master SET distribution_statement = 'F', controlling_office = 'X', distribution_reason = 'r', program = 'ORCA' WHERE number = '{open.Number}'");
        Assert.DoesNotContain(open.Number, (await env.Portal.GetRequestAsync(SourcingEnv.Sup1, number, null)).Value!.DocumentList.Select(x => x.DocumentNumber));
        Assert.Equal(OutcomeKind.NotFound, (await env.Portal.OpenDocumentFileAsync(SourcingEnv.Sup1, number, open.FileId, null)).Kind);
        Assert.Contains(w.S1, (await env.Requests.GetAsync(Alice, number, null)).Value!.DocumentList.Single(x => x.DocumentNumber == open.Number).Withheld);
    }

    [Fact]
    public async Task A_supplier_without_a_clearance_profile_or_a_foreign_one_never_receives_documents_it_may_not_have()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var publicOnly = await env.NewSupplierAsync("Public Co", profile: "public-release");
        var foreign = await env.NewSupplierAsync("Foreign Co", "GBR", foreign: true, profile: "foreign-government-taa-0042");
        await env.LinkPortalUserAsync(publicOnly, SourcingEnv.Sup1);
        await env.LinkPortalUserAsync(foreign, SourcingEnv.Sup2);
        var part = await env.NewPartAsync("Washer");
        var open = await env.NewDocumentAsync(SourcingEnv.Open);
        var cui = await env.NewDocumentAsync(SourcingEnv.Cui());
        var number = await IssueAsync(env, "Washers", SourcingEnv.Open, [Line(part, 100)], [publicOnly, foreign], [open.Number]);
        Assert.Equal([open.Number], (await env.Portal.GetRequestAsync(SourcingEnv.Sup1, number, null)).Value!.DocumentList.Select(x => x.DocumentNumber));
        Assert.Equal([open.Number], (await env.Portal.GetRequestAsync(SourcingEnv.Sup2, number, null)).Value!.DocumentList.Select(x => x.DocumentNumber));
        // A CUI document needs a request that covers it, and then the public-release supplier cannot receive the request at all.
        var coverCui = await env.Requests.CreateAsync(Alice, new CreateRequestCommand(RequestKinds.Rfq, "CUI", "d", null, SourcingEnv.Cui(), [Line(part, 1)], [publicOnly], [cui.Number]), null);
        Assert.True(coverCui.IsOk);
        Assert.Equal(OutcomeKind.Conflict, (await env.Requests.IssueAsync(Alice, coverCui.Value!.Summary.Number, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Portal.OpenDocumentFileAsync(SourcingEnv.Sup1, coverCui.Value.Summary.Number, cui.FileId, null)).Kind);
    }

    // ---------------------------------------------------------------- quotes

    [Fact]
    public async Task A_quote_carries_tiers_validity_notes_and_exceptions_and_the_due_date_closes_the_response_window()
    {
        var w = await WorldAsync();
        var env = w.Env;
        var today = Today(env);
        var number = await IssueAsync(env, "Housings", SourcingEnv.Open, [Line(w.P1, 600, today.AddDays(45))], [w.S1], due: today.AddDays(5));
        var line = new QuoteLine(w.P1, null, "USD", 21, 50, [new PriceBreak(100, 2.5m), new PriceBreak(500, 2.1m)], "Lot pricing", "Cannot ship before 1 July");

        var sent = await QuoteAsync(env, SourcingEnv.Sup1, number, today.AddDays(30), line);
        Assert.Equal(today.AddDays(30), sent.ValidUntil);
        Assert.Equal(2, sent.Quote.Single().Tiers!.Count);
        var seen = (await env.Requests.GetAsync(Alice, number, null)).Value!.Responses.Single();
        Assert.Equal(today.AddDays(30), seen.ValidUntil);
        Assert.Equal("Cannot ship before 1 July", seen.Quote.Single().Exception);
        Assert.Equal("Lot pricing", seen.Quote.Single().Note);

        string Err(Outcome<PortalResponse> o) => string.Join(" ", o.Errors);
        var past = await env.Portal.RespondAsync(SourcingEnv.Sup1, number, new PortalResponseInput("", false, [line], today.AddDays(-1)), [], null);
        Assert.Equal(OutcomeKind.Invalid, past.Kind);
        Assert.Contains("valid until a date that has not passed", Err(past), StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await env.Portal.RespondAsync(SourcingEnv.Sup1, number, new PortalResponseInput("", false, [line with { Tiers = [new PriceBreak(500, 2m), new PriceBreak(100, 3m)] }]), [], null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Portal.RespondAsync(SourcingEnv.Sup1, number, new PortalResponseInput("", false, [new QuoteLine(w.P1, 2m, null, 5, null)]), [], null)).Kind); // a price needs a currency
        Assert.Equal(OutcomeKind.Invalid, (await env.Portal.RespondAsync(SourcingEnv.Sup1, number, new PortalResponseInput("We cannot", true, [], today.AddDays(3)), [], null)).Kind);
        // Notes and exceptions are not in the audit log; counts are.
        var audit = (await env.AuditAsync()).Single(e => e.Action == "portal.respond" && e.ObjectId == number && e.Outcome == AuditOutcome.Success);
        Assert.Equal(("1", "2", "1"), (audit.Details()["quoteLines"], audit.Details()["tiers"], audit.Details()["exceptions"]));
        Assert.DoesNotContain(audit.Details().Values, v => v is not null && (v.Contains("Lot pricing", StringComparison.Ordinal) || v.Contains("July", StringComparison.Ordinal)));

        // After the due date the portal takes no more responses; the buyer can move the date and responses resume.
        env.Clock.Advance(TimeSpan.FromDays(6));
        var late = await env.Portal.RespondAsync(SourcingEnv.Sup1, number, new PortalResponseInput("late", false, []), [], null);
        Assert.Equal(OutcomeKind.Conflict, late.Kind);
        Assert.False((await env.Portal.GetRequestAsync(SourcingEnv.Sup1, number, null)).Value!.CanRespond);
        Assert.Equal(OutcomeKind.Conflict, (await env.Requests.ChangeDueDateAsync(Alice, number, today.AddDays(1), null)).Kind); // a date that has passed is refused
        var moved = await env.Requests.ChangeDueDateAsync(Alice, number, Today(env).AddDays(3), null);
        Assert.True(moved.IsOk, string.Join("; ", moved.Errors));
        Assert.True((await env.Portal.GetRequestAsync(SourcingEnv.Sup1, number, null)).Value!.CanRespond);
        Assert.True((await env.Portal.RespondAsync(SourcingEnv.Sup1, number, new PortalResponseInput("on time now", false, []), [], null)).IsOk);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Requests.ChangeDueDateAsync(SourcingEnv.Mallory, number, Today(env).AddDays(9), null)).Kind);
    }

    // ---------------------------------------------------------------- comparison and awards

    private async Task<(World W, string Number)> QuotedAsync()
    {
        var w = await WorldAsync();
        var env = w.Env;
        var today = Today(env);
        var number = await IssueAsync(env, "Housings and covers", SourcingEnv.Open, [Line(w.P1, 600, today.AddDays(45)), Line(w.P2, 5)], [w.S1, w.S2]);
        await QuoteAsync(env, SourcingEnv.Sup1, number, today.AddDays(30),
            new QuoteLine(w.P1, null, "USD", 21, 50, [new PriceBreak(100, 2.5m), new PriceBreak(500, 2.1m)], "Lot pricing", "Cannot ship before July"),
            new QuoteLine(w.P2, 9m, "USD", 14, null));
        await QuoteAsync(env, SourcingEnv.Sup2, number, null, new QuoteLine(w.P1, 2.2m, "USD", 10, null)); // no quote for the cover
        return (w, number);
    }

    [Fact]
    public async Task The_comparison_shows_each_suppliers_price_at_the_line_quantity_and_what_is_lowest_fastest_and_excepted()
    {
        var (w, number) = await QuotedAsync();
        var env = w.Env;
        var c = (await env.Requests.CompareAsync(Alice, number, null)).Value!;
        Assert.Equal([w.S1, w.S2], c.Suppliers.Select(s => s.Number));
        var housing = c.Lines.Single(l => l.PartNumber == w.P1);
        Assert.Equal(2.1m, housing.Cells[0].UnitPrice);            // the 500+ tier at 600 pieces
        Assert.Equal(1260m, housing.Cells[0].ExtendedPrice);
        Assert.Equal(2.2m, housing.Cells[1].UnitPrice);
        Assert.True(housing.Cells[0].Lowest);
        Assert.False(housing.Cells[1].Lowest);
        Assert.True(housing.Cells[1].Fastest);                      // 10 days against 21
        Assert.Equal("Cannot ship before July", housing.Cells[0].Exception);
        Assert.False(housing.Cells[0].MeetsNeedBy is false);        // 21 days fits the 45 day need-by date
        var cover = c.Lines.Single(l => l.PartNumber == w.P2);
        Assert.True(cover.Cells[0].Quoted);
        Assert.False(cover.Cells[1].Quoted);
        Assert.True(c.CanAward);
        Assert.Contains(c.Totals, t => t.SupplierNumber == w.S1 && t.Complete && t.Total == 1260m + 45m);
        Assert.Contains(c.Totals, t => t.SupplierNumber == w.S2 && !t.Complete && t.LinesPriced == 1);
        // Anybody who may read the request can compare; only someone who may modify it can award. Prices stay out of the audit log.
        var viewer = (await env.Requests.CompareAsync(SourcingEnv.Mallory, number, null)).Value!;
        Assert.False(viewer.CanAward);
        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.Action == "rfq.compare" && e.Outcome == AuditOutcome.Success && e.ObjectId == number);
        Assert.DoesNotContain(audit.Where(e => e.Action == "rfq.compare"), e => e.Details().Values.Any(v => v is not null && v.Contains("2.1", StringComparison.Ordinal)));
        // A supplier user and somebody without the right to read the request see nothing.
        Assert.Equal(OutcomeKind.NotFound, (await env.Requests.CompareAsync(SourcingEnv.Sup1, number, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Requests.CompareAsync(Alice, "RFQ-999999", null)).Kind);
    }

    [Fact]
    public async Task Awards_per_line_and_in_full_record_the_sourcing_link_and_close_the_request_when_every_line_is_awarded()
    {
        var (w, number) = await QuotedAsync();
        var env = w.Env;
        // A full award needs a price for every line; the second supplier quoted one.
        var partial = await env.Requests.AwardAsync(Alice, number, new AwardCommand(w.S2), null);
        Assert.Equal(OutcomeKind.Conflict, partial.Kind);
        Assert.Empty((await env.Requests.GetAsync(Alice, number, null)).Value!.AwardList);

        var first = await env.Requests.AwardAsync(Alice, number, new AwardCommand(w.S2, [w.P1], "Fastest at a fair price: secret note XYZ"), null);
        Assert.True(first.IsOk, string.Join("; ", first.Errors));
        var award = Assert.Single(first.Value!.AwardList);
        Assert.Equal((w.P1, w.S2, 2.2m, "USD", 10, true), (award.PartNumber, award.SupplierNumber, award.UnitPrice, award.Currency, award.LeadTimeDays, award.EntryCreated));
        Assert.Equal(600m, award.Quantity);
        Assert.Equal(RequestStates.Issued, first.Value.Summary.State); // the cover is still open

        // The sourcing link: a DRAFT approved-vendor entry of the part and the supplier with the quoted data. It is not a source until it is approved.
        var entry = (await env.Sourcing.GetEntryAsync(Alice, award.EntryNumber, null)).Value!;
        Assert.Equal((SourceKinds.Avl, EntryApprovalStates.Draft, w.P1, 10, "USD"), (entry.Kind, entry.ApprovalState, entry.PartNumber, entry.LeadTimeDays, entry.Currency));
        Assert.Equal(w.S2, entry.SupplierNumber);
        Assert.Equal([2.2m], entry.PriceBreaks.Select(b => b.UnitPrice));
        Assert.Equal(CoverageBuckets.NotCovered, (await env.Sourcing.GetPartSourcingAsync(Alice, w.P1, null)).Value!.Coverage);

        // One winner per line; the lines of a full award that are taken are refused as a whole.
        Assert.Equal(OutcomeKind.Conflict, (await env.Requests.AwardAsync(Alice, number, new AwardCommand(w.S1, [w.P1]), null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Requests.AwardAsync(Alice, number, new AwardCommand(w.S1), null)).Kind);
        var second = await env.Requests.AwardAsync(Alice, number, new AwardCommand(w.S1, [w.P2]), null);
        Assert.True(second.IsOk, string.Join("; ", second.Errors));
        Assert.Equal(RequestStates.Closed, second.Value!.Summary.State); // every line has an award
        var cover = second.Value.AwardList.Single(a => a.PartNumber == w.P2);
        Assert.Equal((9m, 14), (cover.UnitPrice, cover.LeadTimeDays));
        var coverEntry = (await env.Sourcing.GetEntryAsync(Alice, cover.EntryNumber, null)).Value!;
        Assert.Equal([new PriceBreak(5, 9m)], coverEntry.PriceBreaks.Select(b => new PriceBreak(b.Quantity, b.UnitPrice)));

        // Each supplier sees only the lines awarded to it.
        Assert.Equal([(w.P1, 2.2m)], (await env.Portal.GetRequestAsync(SourcingEnv.Sup2, number, null)).Value!.AwardList.Select(a => (a.PartNumber, a.UnitPrice)));
        Assert.Equal([(w.P2, 9m)], (await env.Portal.GetRequestAsync(SourcingEnv.Sup1, number, null)).Value!.AwardList.Select(a => (a.PartNumber, a.UnitPrice)));

        // The audit log holds counts and numbers, never the note or the price.
        var audit = await env.AuditAsync();
        var awards = audit.Where(e => e.Action == "rfq.award" && e.Outcome == AuditOutcome.Success).OrderBy(e => e.Seq).ToList();
        Assert.Equal(2, awards.Count);
        Assert.Equal(("1", "1", "false"), (awards[0].Details()["lines"], awards[0].Details()["entriesCreated"], awards[0].Details()["closed"]));
        Assert.DoesNotContain(awards, e => e.Details().Values.Any(v => v is not null && v.Contains("secret note", StringComparison.Ordinal)));
        Assert.Contains(audit, e => e.Action == "sourcing.entry_create" && e.ObjectId == w.P1 && e.Details()["entry"] == award.EntryNumber);
    }

    [Fact]
    public async Task A_full_award_takes_every_line_and_an_open_list_entry_is_linked_not_duplicated()
    {
        var (w, number) = await QuotedAsync();
        var env = w.Env;
        var existing = await env.DraftEntryAsync(SourceKinds.Avl, w.P2, w.S1, mpn: null);
        var full = await env.Requests.AwardAsync(Alice, number, new AwardCommand(w.S1), null);
        Assert.True(full.IsOk, string.Join("; ", full.Errors));
        Assert.Equal(2, full.Value!.AwardList.Count);
        Assert.Equal(RequestStates.Closed, full.Value.Summary.State);
        var linked = full.Value.AwardList.Single(a => a.PartNumber == w.P2);
        Assert.Equal((existing.Number, false), (linked.EntryNumber, linked.EntryCreated));
        Assert.Single((await env.Sourcing.GetPartSourcingAsync(Alice, w.P2, null)).Value!.Entries, e => e.SupplierNumber == w.S1 && e.Kind == SourceKinds.Avl);
        Assert.True(full.Value.AwardList.Single(a => a.PartNumber == w.P1).EntryCreated);
        Assert.Equal(2.1m, full.Value.AwardList.Single(a => a.PartNumber == w.P1).UnitPrice);
    }

    [Fact]
    public async Task An_award_can_be_withdrawn_with_a_reason_its_draft_entry_goes_with_it_and_the_line_can_be_awarded_again()
    {
        var (w, number) = await QuotedAsync();
        var env = w.Env;
        var awarded = (await env.Requests.AwardAsync(Alice, number, new AwardCommand(w.S2, [w.P1]), null)).Value!;
        var entry = awarded.AwardList.Single().EntryNumber;
        Assert.Equal(OutcomeKind.Invalid, (await env.Requests.WithdrawAwardAsync(Alice, number, w.P1, " ", null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Requests.WithdrawAwardAsync(Alice, number, w.P2, "Nothing awarded", null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Requests.WithdrawAwardAsync(SourcingEnv.Mallory, number, w.P1, "No right", null)).Kind);
        var withdrawn = await env.Requests.WithdrawAwardAsync(Alice, number, w.P1, "Chose the cheaper supplier", null);
        Assert.True(withdrawn.IsOk, string.Join("; ", withdrawn.Errors));
        Assert.Empty(withdrawn.Value!.AwardList);
        Assert.Equal(EntryApprovalStates.Withdrawn, (await env.Sourcing.GetEntryAsync(Alice, entry, null)).Value!.ApprovalState);
        Assert.Empty((await env.Portal.GetRequestAsync(SourcingEnv.Sup2, number, null)).Value!.AwardList);
        var again = await env.Requests.AwardAsync(Alice, number, new AwardCommand(w.S1, [w.P1]), null);
        Assert.True(again.IsOk, string.Join("; ", again.Errors));
        Assert.Equal(w.S1, again.Value!.AwardList.Single().SupplierNumber);
        var audit = await env.AuditAsync();
        Assert.Contains(audit, e => e.Action == "rfq.award_withdraw" && e.Outcome == AuditOutcome.Success && e.Details()["entryWithdrawn"] == "true");
        Assert.Contains(audit, e => e.Action == "rfq.award_withdraw" && e.Outcome == AuditOutcome.Denied);
    }

    [Fact]
    public async Task An_award_is_refused_for_an_expired_quote_a_later_decline_an_unknown_line_or_a_disapproved_supplier_and_by_someone_who_may_not_modify()
    {
        var (w, number) = await QuotedAsync();
        var env = w.Env;
        Assert.Equal(OutcomeKind.Forbidden, (await env.Requests.AwardAsync(SourcingEnv.Mallory, number, new AwardCommand(w.S2, [w.P1]), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Requests.AwardAsync(Alice, number, new AwardCommand(w.S2, ["P-999999"]), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Requests.AwardAsync(Alice, number, new AwardCommand("SUP-999999", [w.P1]), null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Requests.AwardAsync(SourcingEnv.Sup1, number, new AwardCommand(w.S1, [w.P1]), null)).Kind);
        // S2 declines after quoting: the later response withdraws its quote.
        Assert.True((await env.Portal.RespondAsync(SourcingEnv.Sup2, number, new PortalResponseInput("We cannot make it", true, []), [], null)).IsOk);
        var declined = await env.Requests.AwardAsync(Alice, number, new AwardCommand(w.S2, [w.P1]), null);
        Assert.Equal(OutcomeKind.Conflict, declined.Kind);
        Assert.Contains("has not sent a quote", declined.Errors[0], StringComparison.Ordinal);
        // S1's quote is valid for 30 days: awarded after that it is refused.
        env.Clock.Advance(TimeSpan.FromDays(31));
        var expired = await env.Requests.AwardAsync(Alice, number, new AwardCommand(w.S1, [w.P2]), null);
        Assert.Equal(OutcomeKind.Conflict, expired.Kind);
        Assert.Contains("expired", expired.Errors[0], StringComparison.Ordinal);
        env.Clock.Advance(TimeSpan.FromDays(-31));
        Assert.True((await env.Suppliers.SetStatusAsync(Alice, w.S1, SupplierStatuses.Disapproved, "Failed the audit", null)).IsOk);
        Assert.Equal(OutcomeKind.Conflict, (await env.Requests.AwardAsync(Alice, number, new AwardCommand(w.S1, [w.P2]), null)).Kind);
        Assert.Empty((await env.Requests.GetAsync(Alice, number, null)).Value!.AwardList);
        // A draft request cannot be awarded.
        var draft = (await env.Requests.CreateAsync(Alice, SourcingEnv.Request("Draft", SourcingEnv.Open, [w.P1], [w.S2]), null)).Value!.Summary.Number;
        Assert.Equal(OutcomeKind.Conflict, (await env.Requests.AwardAsync(Alice, draft, new AwardCommand(w.S2), null)).Kind);
    }

    [Fact]
    public async Task A_foreign_supplier_is_never_awarded_an_ITAR_part_even_if_the_part_was_marked_after_the_quote_and_the_database_agrees()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var foreign = await env.NewSupplierAsync("Foreign Co", "GBR", foreign: true, profile: "foreign-government-taa-0042");
        await env.LinkPortalUserAsync(foreign, SourcingEnv.Sup1);
        var part = await env.NewPartAsync("Washer");
        var number = await IssueAsync(env, "Washers", SourcingEnv.Open, [Line(part, 10)], [foreign]);
        await QuoteAsync(env, SourcingEnv.Sup1, number, null, new QuoteLine(part, 1m, "USD", 5, null));
        await env.DraftEntryAsync(SourceKinds.Avl, part, foreign, mpn: null);
        await env.Db.ExecuteAsSuperuserAsync($"UPDATE plm.part_master SET export_jurisdiction = 'Itar', usml_category = 'XI(d)', cui_categories = ARRAY['EXPT'], distribution_statement = 'D', controlling_office = 'X', distribution_reason = 'r', program = 'ORCA' WHERE number = '{part}'");
        var refused = await env.Requests.AwardAsync(Alice, number, new AwardCommand(foreign), null);
        Assert.Equal(OutcomeKind.Forbidden, refused.Kind);
        Assert.Equal([SourcingRules.ForeignSupplierItarPart], refused.Errors);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "rfq.award" && e.Outcome == AuditOutcome.Denied && e.Details()["reasons"] == SourcingRules.ForeignSupplierItarPart);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM src.award"));
        // The database refuses it as well, even for the schema owner.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => env.Db.ExecuteAsSuperuserAsync(
            $"INSERT INTO src.award (id, request_id, part_master_id, supplier_id, response_id, quantity, unit_price, currency, entry_id, entry_created, awarded_at, awarded_by) " +
            $"SELECT gen_random_uuid(), r.id, pm.id, s.id, p.id, 10, 1, 'USD', e.id, false, now(), 'x' FROM src.request r, plm.part_master pm, src.supplier s, src.response p, src.source_entry e " +
            $"WHERE r.number = '{number}' AND pm.number = '{part}' AND s.number = '{foreign}' AND p.request_id = r.id LIMIT 1"));
        Assert.Equal("23514", ex.SqlState); // check violation: the rule itself
    }

    [Fact]
    public async Task Awards_are_immutable_except_for_their_withdrawal_and_must_rest_on_a_quote_of_the_same_supplier_and_request()
    {
        var (w, number) = await QuotedAsync();
        var env = w.Env;
        Assert.True((await env.Requests.AwardAsync(Alice, number, new AwardCommand(w.S2, [w.P1]), null)).IsOk);
        async Task<PostgresException> Fails(string sql) => await Assert.ThrowsAsync<PostgresException>(() => env.Db.ExecuteAsSuperuserAsync(sql));
        Assert.Contains("only be withdrawn", (await Fails("UPDATE src.award SET unit_price = 0.01")).Message, StringComparison.Ordinal);
        Assert.Contains("immutable", (await Fails("DELETE FROM src.award")).Message, StringComparison.Ordinal);
        Assert.Contains("immutable", (await Fails("TRUNCATE src.award")).Message, StringComparison.Ordinal);
        Assert.Contains("only be withdrawn", (await Fails("UPDATE src.award SET quantity = 1, withdrawn_at = now(), withdrawn_by = 'x', withdraw_reason = 'y'")).Message, StringComparison.Ordinal);
        // A forged award on another supplier's response is refused.
        Assert.Contains("same supplier", (await Fails(
            $"INSERT INTO src.award (id, request_id, part_master_id, supplier_id, response_id, quantity, unit_price, currency, entry_id, entry_created, awarded_at, awarded_by) " +
            $"SELECT gen_random_uuid(), a.request_id, (SELECT part_master_id FROM src.request_part WHERE request_id = a.request_id AND part_master_id <> a.part_master_id), (SELECT id FROM src.supplier WHERE number = '{w.S1}'), a.response_id, 1, 1, 'USD', a.entry_id, false, now(), 'x' FROM src.award a")).Message, StringComparison.Ordinal);
        // Released documents only, and only while the request is a draft.
        var inWork = await env.NewDocumentAsync(SourcingEnv.Open, "InWork");
        var released = await env.NewDocumentAsync(SourcingEnv.Open);
        var attach = (string doc) => $"INSERT INTO src.request_document (request_id, document_master_id, document_revision_id, iteration_id, added_at, added_by) SELECT r.id, m.id, rv.id, i.id, now(), 'x' FROM src.request r, plm.document_master m JOIN plm.document_revision rv ON rv.master_id = m.id JOIN plm.document_iteration i ON i.revision_id = rv.id WHERE r.number = '{number}' AND m.number = '{doc}'";
        Assert.Contains("documents are attached when a request is created", (await Fails(attach(released.Number))).Message, StringComparison.Ordinal);
        var draft = (await env.Requests.CreateAsync(Alice, SourcingEnv.Request("Draft", SourcingEnv.Open, [w.P1], [w.S1]), null)).Value!.Summary.Number;
        Assert.Contains("only a released document version", (await Fails(attach(inWork.Number).Replace(number, draft))).Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the SQL-oracle style check for the buyer's reads

    private static readonly Subject Lena = WorkflowEnv.User("lena", [Roles.Viewer], us: false);
    private static readonly Subject Farid = WorkflowEnv.User("farid", [Roles.Viewer], us: false, auths: ["TAA-0042"]);
    private static readonly Subject Kim = WorkflowEnv.User("kim", [Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]);

    private static Markings Make(ExportJurisdiction j, DistributionStatement d, string? usml = null, string? eccn = null, string[]? cui = null, string? program = null, string[]? auths = null) =>
        Markings.Create(j, d, "ENG", eccn, usml, cui, d == DistributionStatement.A ? null : "ACME", d == DistributionStatement.A ? null : "Reason", program, auths).Value;

    [Fact]
    public async Task The_documents_of_a_request_listed_to_each_subject_agree_with_the_policy_on_the_markings_as_they_are_now()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var supplier = await env.NewSupplierAsync("Alpha Works", profile: "dod-contractor");
        var part = await env.NewPartAsync("Washer");
        Markings[] variants =
        [
            SourcingEnv.Open, SourcingEnv.Cui(), SourcingEnv.Itar(),
            Make(ExportJurisdiction.Ear, DistributionStatement.C, eccn: "9A610", cui: ["EXPT"]),
            Make(ExportJurisdiction.None, DistributionStatement.B, program: "ORCA"),
            Make(ExportJurisdiction.Itar, DistributionStatement.D, usml: "XI(d)", cui: ["EXPT"], program: "ORCA", auths: ["TAA-0042"]),
        ];
        var docs = new Dictionary<string, Markings>();
        foreach (var m in variants) docs[(await env.NewDocumentAsync(m)).Number] = m;
        var cover = MarkingDominance.MinimalCover(SourcingEnv.Open, variants);
        Assert.True(cover.IsSuccess, string.Join("; ", cover.Errors));
        var created = await env.Requests.CreateAsync(Alice, new CreateRequestCommand(RequestKinds.Rfq, "All the drawings", "d", null, cover.Value, [Line(part, 1)], [supplier], [.. docs.Keys]), null);
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        var number = created.Value!.Summary.Number;

        // Raise two documents after the request exists: the reader's access is decided on the markings as they are now.
        var raised = Make(ExportJurisdiction.None, DistributionStatement.C, cui: ["CTI"]);
        var changed = docs.Keys.Take(2).ToList();
        foreach (var n in changed) await env.Db.ExecuteAsSuperuserAsync($"UPDATE plm.document_master SET cui_categories = ARRAY['CTI'], distribution_statement = 'C', controlling_office = 'ACME', distribution_reason = 'Reason', export_jurisdiction = 'None', eccn = NULL, usml_category = NULL, program = NULL, export_authorizations = '{{}}' WHERE number = '{n}'");
        foreach (var n in changed) docs[n] = raised;

        Subject[] subjects = [SourcingEnv.Alice, SourcingEnv.Bob, SourcingEnv.Ada, SourcingEnv.Mallory, SourcingEnv.Audrey, SourcingEnv.Eve, SourcingEnv.Vic, Lena, Farid, Kim, SourcingEnv.Sup1];
        foreach (var subject in subjects)
        {
            var detail = await env.Requests.GetAsync(subject, number, null);
            Assert.Equal(AccessPolicy.Evaluate(subject, cover.Value, PlmAction.Read).Permitted, detail.IsOk);
            if (!detail.IsOk) continue;
            var expected = docs.Where(kv => AccessPolicy.Evaluate(subject, kv.Value, PlmAction.Read).Permitted).Select(kv => kv.Key).Order().ToList();
            Assert.True(expected.SequenceEqual(detail.Value!.DocumentList.Select(x => x.DocumentNumber).Order()), $"documents shown to {subject.Username}");
        }
    }

    [Fact]
    public async Task Awards_and_quotes_of_a_supplier_the_reader_may_not_read_are_left_out_of_the_comparison()
    {
        var w = await WorldAsync();
        var env = w.Env;
        await env.SetProfileProgramsAsync("dod-contractor", "ORCA");
        var supplierMarkings = Make(ExportJurisdiction.None, DistributionStatement.B, cui: ["CTI"]);
        var restricted = await env.NewSupplierAsync("Restricted Works", markings: supplierMarkings, profile: "dod-contractor");
        var number = await IssueAsync(env, "Washers", Make(ExportJurisdiction.None, DistributionStatement.D, program: "ORCA"), [Line(w.P1, 10)], [w.S1, restricted]);
        await env.LinkPortalUserAsync(restricted, SourcingEnv.Sup3);
        await QuoteAsync(env, SourcingEnv.Sup3, number, null, new QuoteLine(w.P1, 1m, "USD", 5, null));
        await QuoteAsync(env, SourcingEnv.Sup1, number, null, new QuoteLine(w.P1, 2m, "USD", 5, null));
        Assert.True((await env.Requests.AwardAsync(Alice, number, new AwardCommand(restricted), null)).IsOk);

        // Somebody in program ORCA without the CUI category of the supplier reads the request, not the supplier: its quote and its award are not there.
        var orcaNoCui = WorkflowEnv.User("orca-no-cui", [Roles.Viewer, Roles.Author], programs: ["ORCA"], cui: []);
        Assert.False(AccessPolicy.Evaluate(orcaNoCui, supplierMarkings, PlmAction.Read).Permitted);
        var limited = (await env.Requests.CompareAsync(orcaNoCui, number, null)).Value!;
        Assert.Equal([w.S1], limited.Suppliers.Select(s => s.Number));
        Assert.DoesNotContain(limited.Lines.Single().Cells, c => c.Awarded);
        var detail = (await env.Requests.GetAsync(orcaNoCui, number, null)).Value!;
        Assert.Empty(detail.AwardList);
        Assert.Equal([w.S1], detail.Recipients.Select(r => r.SupplierNumber));
        Assert.Equal(["Alpha Works"], detail.Responses.Select(r => r.SupplierName));
        var full = (await env.Requests.CompareAsync(Alice, number, null)).Value!;
        Assert.Equal(2, full.Suppliers.Count);
        Assert.Contains(full.Lines.Single().Cells, c => c.Awarded);
        Assert.Single((await env.Requests.GetAsync(Alice, number, null)).Value!.AwardList);
    }
}
