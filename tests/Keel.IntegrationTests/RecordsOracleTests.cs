using Keel.Core;
using Keel.Records;
using Keel.Security;

namespace Keel.IntegrationTests;

/// <summary>
/// SQL-oracle no-leak tests: for many subjects with different clearances, everything records management lists, counts or answers must equal what the in-memory access policy
/// allows that subject to read (ADR-0004). Holds, queues, requests, certificates and archives are checked against the same oracle, so a hold never reveals a hidden object.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RecordsOracleTests(PostgresFixture pg)
{
    private static Subject Records(string name, bool us = true, string[]? programs = null, string[]? cui = null, string[]? auths = null, string[]? roles = null) =>
        WorkflowEnv.User(name, roles ?? [Roles.Viewer, Roles.Author, Roles.RecordsManager], us, "ENG", programs ?? ["ORCA", "KRAKEN"], auths, cui ?? ["EXPT", "CTI"]);

    private static readonly Subject Maker = WorkflowEnv.User("maker", [Roles.Author, Roles.Admin, Roles.Viewer, Roles.RecordsManager], programs: ["ORCA", "KRAKEN"]);

    private static Markings Ear() => Markings.Create(ExportJurisdiction.Ear, DistributionStatement.C, "ENG", eccn: "3A001", cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Proprietary").Value;

    private static IEnumerable<Subject> Subjects() =>
    [
        Records("all"), Records("orcaonly", programs: ["ORCA"]), Records("krakenonly", programs: ["KRAKEN"]), Records("noprograms", programs: []), Records("nocui", cui: []),
        Records("nonus", us: false), Records("nonuswithauth", us: false, auths: ["TAA-1"]), Records("auditor", roles: [Roles.Auditor]), Records("adminrecords", roles: [Roles.Admin, Roles.Viewer]),
        Records("onlycti", cui: ["CTI"]),
    ];

    [Fact]
    public async Task Every_list_count_and_answer_equals_what_the_access_policy_allows_each_subject_to_read()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        await env.Directory.UpsertAsync(Maker);
        var docs = new Dictionary<string, Markings>();
        async Task<string> Make(string name, Markings m)
        {
            var d = await env.Docs.CreateAsync(Maker, new Keel.Documents.CreateDocumentCommand(name, "Specification", m, "Synthetic"), new Keel.Documents.FileUpload("f.txt", "text/plain", new MemoryStream(RecordsEnv.Unique(name))), null);
            Assert.True(d.IsOk, string.Join("; ", d.Errors));
            Assert.True((await env.Docs.SetStateAsync(Maker, d.Value!.Number, "Released", "release", true, null)).IsOk);
            docs[d.Value.Number] = m;
            return d.Value.Number;
        }
        var open = await Make("open", RecordsEnv.Open);
        var cui = await Make("cui", RecordsEnv.Cui());
        var orca = await Make("orca", RecordsEnv.Itar("ORCA"));
        var orca2 = await Make("orca two", RecordsEnv.Itar("ORCA"));
        var kraken = await Make("kraken", RecordsEnv.Itar("KRAKEN"));
        var ear = await Make("ear", Ear());
        await env.PublishScheduleAsync(RecordsEnv.DocClass("ALL", months: 0));
        await env.ClassifyAsync();

        // A disposed document (and its certificate), before any hold exists: the ORCA hold placed later still lists it as history.
        var disposeRequest = (await env.Disposition.CreateAsync(RecordsEnv.Rex, new CreateDispositionCommand([new ObjectRef("document", orca2)], null), null)).Value!;
        await env.ReviewAsync("disposition", disposeRequest.Number);
        var done = await env.Disposition.ExecuteAsync(RecordsEnv.Rhea, disposeRequest.Number, null);
        Assert.True(done.IsOk, string.Join("; ", done.Errors));
        var certificate = done.Value!.CertificateNumber!;

        // Holds: one by program (covers both ORCA documents) and one naming the CUI document.
        var orcaHold = (await env.Holds.PlaceAsync(Maker, new PlaceHoldCommand("Orca", "Why", new HoldScope(HoldScopeKinds.Program, Program: "ORCA")), null)).Value!;
        var cuiHold = (await env.Holds.PlaceAsync(Maker, new PlaceHoldCommand("Cui", "Why", new HoldScope(HoldScopeKinds.Objects, [new HoldObjectRef("document", cui)])), null)).Value!;
        var held = new HashSet<string> { orca, cui };            // held and still disposable-looking: counted for readers who can see them
        var heldForStatus = new HashSet<string> { orca, orca2, cui };
        var orcaMembers = new HashSet<string> { orca, orca2 };

        // A draft request for the open document and an archive of the KRAKEN document.
        var openRequest = (await env.Disposition.CreateAsync(Maker, new CreateDispositionCommand([new ObjectRef("document", open)], null), null)).Value!;
        var archive = (await env.Archives.CreateAsync(Maker, new CreateArchiveCommand("Kraken archive", [new ObjectRef("document", kraken)]), null)).Value!;
        var requestMarkings = new Dictionary<string, Markings> { [openRequest.Number] = openRequest.Markings, [disposeRequest.Number] = disposeRequest.Markings };

        foreach (var s in Subjects())
        {
            var readable = docs.Where(d => AccessPolicy.Evaluate(s, d.Value, PlmAction.Read).Permitted).Select(d => d.Key).ToHashSet();
            var canView = RecordsAccess.CanView(s);
            if (!canView) continue;

            // The queue: readable, due, unheld, not already in a request, not disposed of.
            var inRequest = new HashSet<string> { open }; // openRequest is a draft; orca2 is tombstoned
            var queue = (await env.Disposition.EligibleAsync(s, new EligibleQuery(), null)).Value!;
            var expectedQueue = readable.Where(n => !held.Contains(n) && !inRequest.Contains(n) && n != orca2).Order(StringComparer.Ordinal).ToList();
            Assert.True(expectedQueue.SequenceEqual(queue.Items.Select(i => i.Number).Order(StringComparer.Ordinal)), $"{s.Username}: queue {string.Join(",", queue.Items.Select(i => i.Number))} expected {string.Join(",", expectedQueue)}");
            Assert.Equal(expectedQueue.Count, queue.Total);
            Assert.Equal(readable.Count(held.Contains), queue.HeldReadable); // only what the subject can read is ever counted as held

            // A hold: the number of members and the list are the readable ones only.
            var hold = await env.Holds.GetAsync(s, orcaHold.Number, null);
            Assert.Equal(readable.Count(orcaMembers.Contains), hold.Value!.ReadableMembers);
            Assert.Equal(readable.Where(orcaMembers.Contains).Order(StringComparer.Ordinal), (await env.Holds.MembersAsync(s, orcaHold.Number, 100, 0, null)).Value!.Select(m => m.Number).Order(StringComparer.Ordinal));
            Assert.Equal(readable.Count(n => n == cui), (await env.Holds.GetAsync(s, cuiHold.Number, null)).Value!.ReadableMembers);

            // The object's own page: a hold or a tombstone is visible only for objects the subject can read.
            foreach (var (number, m) in docs)
            {
                var status = await env.Holds.StatusOfAsync(s, "document", number);
                Assert.Equal(readable.Contains(number), status is not null);
                if (status is not null) { Assert.Equal(heldForStatus.Contains(number), status.Held); Assert.Equal(number == orca2, status.Tombstoned); }
                _ = m;
            }

            // Naming objects in a request or a hold: everything unreadable is "not found", with no hint about holds or state.
            var attempt = await env.Disposition.CreateAsync(s, new CreateDispositionCommand([.. docs.Keys.Select(n => new ObjectRef("document", n))], null), null);
            if (!RecordsAccess.CanManage(s)) Assert.Equal(OutcomeKind.Forbidden, attempt.Kind); // auditors read, they do not request
            else
            {
                Assert.Equal(OutcomeKind.Invalid, attempt.Kind);
                var notFound = docs.Keys.Where(n => !readable.Contains(n)).Order(StringComparer.Ordinal).ToList();
                Assert.Equal(notFound.Select(n => $"Document {n} was not found."), attempt.Errors.Where(e => e.EndsWith("was not found.", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
                if (notFound.Count > 0) Assert.Equal(notFound.Count, attempt.Errors.Count); // nothing else is said while any object is unreadable
                if (notFound.Count > 0) // a subject who can read everything would really place the hold, so only refused attempts are made
                {
                    var holdAttempt = await env.Holds.PlaceAsync(s, new PlaceHoldCommand("x", "y", new HoldScope(HoldScopeKinds.Objects, [.. docs.Keys.Select(n => new HoldObjectRef("document", n))])), null);
                    Assert.Equal(notFound.Select(n => $"Document {n} was not found."), holdAttempt.Errors.Order(StringComparer.Ordinal));
                }
            }

            // Requests, certificates, archives: listed only when the subject can read what they cover.
            var expectedRequests = requestMarkings.Where(r => AccessPolicy.Evaluate(s, r.Value, PlmAction.Read).Permitted).Select(r => r.Key).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(expectedRequests, (await env.Disposition.ListAsync(s, null, null)).Value!.Select(r => r.Number).Order(StringComparer.Ordinal));
            var certReadable = AccessPolicy.Evaluate(s, disposeRequest.Markings, PlmAction.Read).Permitted;
            Assert.Equal(certReadable, (await env.Disposition.ListCertificatesAsync(s, null)).Value!.Any(c => c.Number == certificate));
            Assert.Equal(certReadable, (await env.Disposition.GetCertificateAsync(s, certificate, null)).IsOk);
            Assert.Equal(certReadable, (await env.Disposition.GetAsync(s, disposeRequest.Number, null)).IsOk);
            var archiveReadable = AccessPolicy.Evaluate(s, archive.Markings, PlmAction.Read).Permitted;
            Assert.Equal(archiveReadable, (await env.Archives.ListAsync(s, null)).Value!.Any(a => a.Number == archive.Number));
            Assert.Equal(archiveReadable, (await env.Archives.GetAsync(s, archive.Number, null)).IsOk);
        }
        Assert.True((await env.VerifyAuditChainAsync()).Valid);
    }

    [Fact]
    public async Task The_answer_for_an_unreadable_object_is_identical_to_the_answer_for_a_missing_one_everywhere()
    {
        var env = await RecordsEnv.CreateAsync(pg);
        var secret = await env.ReleasedDocAsync("Secret", RecordsEnv.Itar("ORCA"));
        await env.PlaceObjectHoldAsync("document", secret);
        var eve = RecordsEnv.Eve;
        foreach (var kind in new[] { "document" })
        {
            Assert.Null(await env.Holds.StatusOfAsync(eve, kind, secret));
            Assert.Null(await env.Holds.StatusOfAsync(eve, kind, "D-999999"));
        }
        var a = await env.Disposition.CreateAsync(eve, new CreateDispositionCommand([new ObjectRef("document", secret)], null), null);
        var b = await env.Disposition.CreateAsync(eve, new CreateDispositionCommand([new ObjectRef("document", "D-999999")], null), null);
        Assert.Equal(b.Errors.Select(e => e.Replace("D-999999", "X", StringComparison.Ordinal)), a.Errors.Select(e => e.Replace(secret, "X", StringComparison.Ordinal)));
        var c = await env.Archives.CreateAsync(eve, new CreateArchiveCommand("t", [new ObjectRef("document", secret)]), null);
        var d = await env.Archives.CreateAsync(eve, new CreateArchiveCommand("t", [new ObjectRef("document", "D-999999")]), null);
        Assert.Equal(d.Errors.Select(e => e.Replace("D-999999", "X", StringComparison.Ordinal)), c.Errors.Select(e => e.Replace(secret, "X", StringComparison.Ordinal)));
        // Her queue and her counts are the same whether or not the secret exists and is held.
        await env.PublishScheduleAsync(RecordsEnv.DocClass("ALL", months: 0));
        await env.ClassifyAsync();
        var queue = (await env.Disposition.EligibleAsync(eve, new EligibleQuery(), null)).Value!;
        Assert.Equal((0, 0, 0), (queue.Items.Count, queue.Total, queue.HeldReadable));
    }
}
