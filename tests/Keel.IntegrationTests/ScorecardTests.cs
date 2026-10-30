using System.Text.Json;
using Keel.Core;
using Keel.Quality;
using Keel.Security;
using Keel.Sourcing;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Supplier quality scorecards (9.6, ADR-0054): computed from data Keel holds with a transparent formula, shared with the supplier only when a buyer publishes it, and counting only
/// what the reader may read and, for the supplier's copy, only what may be released to that supplier.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ScorecardTests(PostgresFixture pg)
{
    private static Subject Alice => SourcingEnv.Alice;

    private static Markings Make(ExportJurisdiction j, DistributionStatement d, string[]? cui = null, string? program = null, string? eccn = null) =>
        Markings.Create(j, d, "ENG", eccn, null, cui, d == DistributionStatement.A ? null : "ACME", d == DistributionStatement.A ? null : "Reason", program, null).Value;

    private static SupplierQualityFact Ncr(SourcingEnv env, string id, string severity, int daysAgo, Markings? markings = null, bool cancelled = false) =>
        new("ncr", id, env.Clock.GetUtcNow().AddDays(-daysAgo), null, severity, markings ?? SourcingEnv.Open, cancelled);

    private static SupplierQualityFact Capa(SourcingEnv env, string id, int daysAgo, int? closedDaysAgo = null, Markings? markings = null) =>
        new("capa", id, env.Clock.GetUtcNow().AddDays(-daysAgo), closedDaysAgo is null ? null : env.Clock.GetUtcNow().AddDays(-closedDaysAgo.Value), null, markings ?? SourcingEnv.Open);

    private static async Task LinkAsync(SourcingEnv env, string supplier, params SupplierQualityFact[] facts)
    {
        foreach (var f in facts)
        {
            env.Facts.Records.Add(f);
            Assert.True((await env.Suppliers.AddQualityRefAsync(Alice, supplier, f.Kind, f.Id, null)).IsOk);
        }
    }

    private static ScorecardComponentView Part(SupplierScorecard c, string key) => c.Card.Components.Single(x => x.Key == key);

    private static int Counted(string measure) => int.Parse(measure.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);

    private async Task<(SourcingEnv Env, string Supplier, string Part)> WorldAsync(string profile = "dod-contractor")
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var supplier = await env.NewSupplierAsync("Alpha Works", profile: profile);
        await env.LinkPortalUserAsync(supplier, SourcingEnv.Sup1);
        await env.AddCertificationAsync(supplier);
        return (env, supplier, await env.NewPartAsync("Housing"));
    }

    [Fact]
    public async Task The_scorecard_is_computed_from_linked_records_answers_to_requests_and_certifications_by_the_published_formula()
    {
        var (env, supplier, part) = await WorldAsync();
        await LinkAsync(env, supplier, Ncr(env, "NCR-000001", "Minor", 10), Ncr(env, "NCR-000002", "Major", 20), Ncr(env, "NCR-000003", "Critical", 500), Ncr(env, "NCR-000004", "Major", 5, cancelled: true), Capa(env, "CAPA-000001", 30));
        // One request answered in time, one that was due yesterday and never answered.
        var answered = await env.IssuedRequestAsync("Answered", SourcingEnv.Open, [part], [supplier]);
        Assert.True((await env.Portal.RespondAsync(SourcingEnv.Sup1, answered, new PortalResponseInput("We quote", false, []), [], null)).IsOk);
        var created = await env.Requests.CreateAsync(Alice, new CreateRequestCommand(RequestKinds.Rfq, "Missed", "d", DateOnly.FromDateTime(env.Clock.GetUtcNow().UtcDateTime).AddDays(-1), SourcingEnv.Open, [new RequestPartInput(part, 1)], [supplier]), null);
        Assert.True((await env.Requests.IssueAsync(Alice, created.Value!.Summary.Number, null)).IsOk);

        var card = (await env.Scorecards.GetAsync(Alice, supplier, null)).Value!;
        Assert.Equal(60m, Part(card, "ncr").Score);   // 1 minor + 1 major = 4 points; the critical one is older than 12 months, the cancelled one is not counted
        Assert.Equal(75m, Part(card, "capa").Score);  // one open corrective action
        Assert.Equal(50m, Part(card, "rfq").Score);   // one of two requests answered on time
        Assert.Equal(100m, Part(card, "certs").Score);
        Assert.False(Part(card, "delivery").Available);
        Assert.Equal(68.3m, card.Card.Score);         // (40 * 60 + 10 * 75 + 20 * 50 + 20 * 100) / 90
        Assert.Equal("Watch", card.Card.Band);
        Assert.Equal(12, card.Card.Trend.Count);
        Assert.Equal(card.Card.Score, card.Card.Trend[^1].Score);
        Assert.Equal(1, card.NotCounted); // the cancelled record
        Assert.True(card.CanPublish);
        Assert.Null(card.Published);
        Assert.Contains(await env.AuditAsync(), e => e.Action == "scorecard.read" && e.Outcome == Keel.Audit.AuditOutcome.Success && e.ObjectId == supplier);
        // A viewer reads it but cannot publish; a supplier user and strangers see nothing.
        var viewer = (await env.Scorecards.GetAsync(SourcingEnv.Mallory, supplier, null)).Value!;
        Assert.False(viewer.CanPublish);
        Assert.Equal(OutcomeKind.NotFound, (await env.Scorecards.GetAsync(SourcingEnv.Sup1, supplier, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Scorecards.GetAsync(Alice, "SUP-999999", null)).Kind);
    }

    [Fact]
    public async Task A_supplier_with_no_linked_quality_record_has_no_quality_score_rather_than_a_perfect_one()
    {
        var (env, supplier, _) = await WorldAsync();
        var card = (await env.Scorecards.GetAsync(Alice, supplier, null)).Value!;
        Assert.False(Part(card, "ncr").Available);
        Assert.False(Part(card, "capa").Available);
        Assert.StartsWith("Not measured", Part(card, "ncr").Measure, StringComparison.Ordinal);
        Assert.Equal(100m, card.Card.Score); // only the certification is measured
        var bare = await env.NewSupplierAsync("Bare Co");
        var empty = (await env.Scorecards.GetAsync(Alice, bare, null)).Value!;
        Assert.Null(empty.Card.Score);
        Assert.Equal("Not enough data", empty.Card.Band);
    }

    [Fact]
    public async Task Nothing_is_shared_until_a_buyer_publishes_and_the_suppliers_copy_is_a_frozen_snapshot_it_alone_can_read()
    {
        var (env, supplier, _) = await WorldAsync();
        var other = await env.NewSupplierAsync("Bravo Works", profile: "dod-contractor");
        await env.LinkPortalUserAsync(other, SourcingEnv.Sup2);
        await LinkAsync(env, supplier, Ncr(env, "NCR-000001", "Minor", 10));
        Assert.Equal(OutcomeKind.NotFound, (await env.Portal.GetScorecardAsync(SourcingEnv.Sup1, null)).Kind); // nothing published yet

        Assert.Equal(OutcomeKind.Forbidden, (await env.Scorecards.PublishAsync(SourcingEnv.Mallory, supplier, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Portal.GetScorecardAsync(SourcingEnv.Sup1, null)).Kind);
        var published = await env.Scorecards.PublishAsync(Alice, supplier, null);
        Assert.True(published.IsOk, string.Join("; ", published.Errors));
        Assert.NotNull(published.Value!.Published);
        var mine = (await env.Portal.GetScorecardAsync(SourcingEnv.Sup1, null)).Value!;
        Assert.Equal(published.Value.Card.Score, mine.Card.Score);
        Assert.Equal(12, mine.Card.Trend.Count);
        Assert.Contains(mine.Card.Components, c => c.Key == "delivery" && !c.Available);

        // A later change does not move the supplier's copy until the next publication.
        await LinkAsync(env, supplier, Ncr(env, "NCR-000002", "Critical", 3));
        Assert.True((await env.Scorecards.GetAsync(Alice, supplier, null)).Value!.Card.Score < mine.Card.Score);
        Assert.Equal(mine.Card.Score, (await env.Portal.GetScorecardAsync(SourcingEnv.Sup1, null)).Value!.Card.Score);
        Assert.True((await env.Scorecards.PublishAsync(Alice, supplier, null)).IsOk);
        Assert.True((await env.Portal.GetScorecardAsync(SourcingEnv.Sup1, null)).Value!.Card.Score < mine.Card.Score);

        // Another supplier's user sees none of it; an unlinked user and internal users have no portal.
        Assert.Equal(OutcomeKind.NotFound, (await env.Portal.GetScorecardAsync(SourcingEnv.Sup2, null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Portal.GetScorecardAsync(SourcingEnv.Sup3, null)).Kind);
        foreach (var internalUser in new[] { Alice, SourcingEnv.Ada, SourcingEnv.Audrey }) Assert.Equal(OutcomeKind.Forbidden, (await env.Portal.GetScorecardAsync(internalUser, null)).Kind);

        // Withdrawing ends the supplier's access at once.
        Assert.Equal(OutcomeKind.Forbidden, (await env.Scorecards.WithdrawAsync(SourcingEnv.Mallory, supplier, null)).Kind);
        Assert.True((await env.Scorecards.WithdrawAsync(Alice, supplier, null)).IsOk);
        Assert.Equal(OutcomeKind.NotFound, (await env.Portal.GetScorecardAsync(SourcingEnv.Sup1, null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Scorecards.WithdrawAsync(Alice, supplier, null)).Kind);
        var audit = await env.AuditAsync();
        Assert.Equal(2, audit.Count(e => e.Action == "scorecard.publish" && e.Outcome == Keel.Audit.AuditOutcome.Success));
        Assert.Contains(audit, e => e.Action == "scorecard.publish" && e.Outcome == Keel.Audit.AuditOutcome.Denied);
        Assert.Contains(audit, e => e.Action == "portal.scorecard_read" && e.Outcome == Keel.Audit.AuditOutcome.Success);
        Assert.Contains(audit, e => e.Action == "portal.scorecard_read" && e.Outcome == Keel.Audit.AuditOutcome.Denied);
    }

    [Fact]
    public async Task The_published_card_counts_only_records_that_may_be_released_to_the_supplier_and_names_no_record()
    {
        var (env, supplier, _) = await WorldAsync("public-release"); // statement A and no export jurisdiction only
        var restricted = Make(ExportJurisdiction.None, DistributionStatement.C, cui: ["CTI"]);
        await LinkAsync(env, supplier, Ncr(env, "NCR-000001", "Minor", 10), Ncr(env, "NCR-000002", "Major", 12, restricted), Capa(env, "CAPA-000001", 20, markings: restricted));
        var buyer = (await env.Scorecards.GetAsync(Alice, supplier, null)).Value!;
        Assert.Equal(2, Counted(Part(buyer, "ncr").Measure));             // the buyer sees both nonconformances
        Assert.Equal(75m, Part(buyer, "capa").Score);                    // and the restricted corrective action that is still open
        Assert.True((await env.Scorecards.PublishAsync(Alice, supplier, null)).IsOk);
        var theirs = (await env.Portal.GetScorecardAsync(SourcingEnv.Sup1, null)).Value!;
        var ncr = theirs.Card.Components.Single(c => c.Key == "ncr");
        Assert.Equal(1, Counted(ncr.Measure));                           // only the releasable one is counted for the supplier
        Assert.Equal(90m, ncr.Score);
        Assert.Equal(100m, theirs.Card.Components.Single(c => c.Key == "capa").Score); // the restricted corrective action is not part of their copy
        // The stored copy holds counts and rates only: no record number, no title.
        await using var conn = new NpgsqlConnection(env.Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT payload::text FROM src.scorecard_publication", conn);
        var payload = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.DoesNotContain("NCR-", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("CAPA-", payload, StringComparison.Ordinal);
        Assert.Contains("\"formulaVersion\": 1", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_foreign_supplier_never_gets_a_count_of_ITAR_records_and_the_buyer_who_cannot_read_a_linked_record_cannot_publish()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var foreign = await env.NewSupplierAsync("Foreign Co", "GBR", foreign: true, profile: "foreign-government-taa-0042");
        await env.LinkPortalUserAsync(foreign, SourcingEnv.Sup1);
        var itar = SourcingEnv.Itar();
        await LinkAsync(env, foreign, Ncr(env, "NCR-000001", "Critical", 5, itar), Ncr(env, "NCR-000002", "Minor", 6));
        Assert.True((await env.Scorecards.PublishAsync(Alice, foreign, null)).IsOk);
        Assert.Equal(1, Counted((await env.Portal.GetScorecardAsync(SourcingEnv.Sup1, null)).Value!.Card.Components.Single(c => c.Key == "ncr").Measure));
        // Eve is not in program ORCA: the ITAR record is hidden from her, so her card would understate it and she cannot publish.
        var seen = (await env.Scorecards.GetAsync(SourcingEnv.Eve, foreign, null)).Value!;
        Assert.Equal(1, Counted(Part(seen, "ncr").Measure));
        Assert.Equal(1, seen.NotCounted);
        var refused = await env.Scorecards.PublishAsync(SourcingEnv.Eve, foreign, null);
        Assert.Equal(OutcomeKind.Conflict, refused.Kind);
        Assert.Contains("hidden from you", refused.Errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_responsiveness_and_quality_counts_a_reader_sees_follow_the_policy_for_every_subject()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var supplier = await env.NewSupplierAsync("Alpha Works", profile: "dod-contractor");
        var part = await env.NewPartAsync("Washer");
        Markings[] variants = [SourcingEnv.Open, SourcingEnv.Cui(), SourcingEnv.Itar(), Make(ExportJurisdiction.None, DistributionStatement.B, program: "ORCA"), Make(ExportJurisdiction.Ear, DistributionStatement.C, cui: ["EXPT"], eccn: "9A610")];
        var facts = new List<SupplierQualityFact>();
        var requests = new Dictionary<string, Markings>();
        var i = 0;
        foreach (var m in variants)
        {
            facts.Add(Ncr(env, $"NCR-{++i:D6}", "Minor", 5, m));
            var created = await env.Requests.CreateAsync(Alice, new CreateRequestCommand(RequestKinds.Rfq, "Request " + i, "d", null, m, [new RequestPartInput(part, 1)], [supplier]), null);
            Assert.True(created.IsOk, string.Join("; ", created.Errors));
            requests[created.Value!.Summary.Number] = m;
        }
        await LinkAsync(env, supplier, [.. facts]);
        // Issued a while ago, due last week, never answered.
        await env.Db.ExecuteAsSuperuserAsync("UPDATE src.request SET issued_at = now() - interval '20 days', due_on = current_date - 7");

        Subject[] subjects = [Alice, SourcingEnv.Bob, SourcingEnv.Ada, SourcingEnv.Mallory, SourcingEnv.Audrey, SourcingEnv.Eve, SourcingEnv.Vic, WorkflowEnv.User("lena", [Roles.Viewer], us: false), WorkflowEnv.User("farid", [Roles.Viewer], us: false, auths: ["TAA-0042"]), WorkflowEnv.User("kim", [Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"])];
        foreach (var subject in subjects)
        {
            var card = await env.Scorecards.GetAsync(subject, supplier, null);
            Assert.True(card.IsOk, subject.Username);
            var readableNcr = facts.Count(f => AccessPolicy.Evaluate(subject, f.Markings, PlmAction.Read).Permitted);
            var readableRequests = requests.Count(kv => AccessPolicy.Evaluate(subject, kv.Value, PlmAction.Read).Permitted);
            var ncr = Part(card.Value!, "ncr");
            if (readableNcr == 0) Assert.StartsWith("No nonconformance", ncr.Measure, StringComparison.Ordinal);
            else Assert.Equal(readableNcr, Counted(ncr.Measure));
            Assert.Equal(variants.Length - readableNcr, card.Value!.NotCounted);
            var rfq = Part(card.Value, "rfq");
            if (readableRequests == 0) Assert.False(rfq.Available, subject.Username);
            else Assert.Contains($"0 of {readableRequests} request", rfq.Measure, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_published_scorecard_cannot_be_changed_or_deleted_except_to_withdraw_it()
    {
        var (env, supplier, _) = await WorldAsync();
        Assert.True((await env.Scorecards.PublishAsync(Alice, supplier, null)).IsOk);
        async Task<PostgresException> Fails(string sql) => await Assert.ThrowsAsync<PostgresException>(() => env.Db.ExecuteAsSuperuserAsync(sql));
        Assert.Contains("only be withdrawn", (await Fails("UPDATE src.scorecard_publication SET payload = '{}'::jsonb")).Message, StringComparison.Ordinal);
        Assert.Contains("only be withdrawn", (await Fails("UPDATE src.scorecard_publication SET as_of = '2000-01-01', withdrawn_at = now(), withdrawn_by = 'x'")).Message, StringComparison.Ordinal);
        Assert.Contains("immutable", (await Fails("DELETE FROM src.scorecard_publication")).Message, StringComparison.Ordinal);
        Assert.Contains("immutable", (await Fails("TRUNCATE src.scorecard_publication")).Message, StringComparison.Ordinal);
        Assert.True((await env.Scorecards.WithdrawAsync(Alice, supplier, null)).IsOk);
        Assert.Contains("only be withdrawn", (await Fails("UPDATE src.scorecard_publication SET withdrawn_by = 'y'")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_quality_modules_facts_for_the_scorecard_agree_with_the_policy_for_every_subject_and_never_return_a_title()
    {
        var q = await QualityEnv.CreateAsync(pg);
        var part = await q.ReleasedPartAsync(QualityEnv.Open);
        var orca = QualityEnv.Itar("ORCA");
        var kraken = QualityEnv.Itar("KRAKEN");
        var records = new Dictionary<string, Markings>();
        foreach (var m in new[] { QualityEnv.Open, orca, kraken })
        {
            records[(await q.NewNcrAsync(QualityEnv.Kim, part, m)).Number] = m;
            records[(await q.NewCapaAsync(QualityEnv.Kim, m)).Number] = m;
        }
        var facts = new SupplierQualityFacts(q.Ds);
        var refs = new List<(string, string)> { ("ncr", "NCR-999999"), ("deviation", "DEV-000001") };
        refs.AddRange(records.Keys.Select(n => (n.StartsWith("NCR", StringComparison.Ordinal) ? "ncr" : "capa", n)));
        Subject[] subjects = [QualityEnv.Alice, QualityEnv.Eve, QualityEnv.Kim, QualityEnv.Mallory, QualityEnv.Audrey, QualityEnv.Ada, WorkflowEnv.Lena, WorkflowEnv.Farid, WorkflowEnv.User("nobody", [])];
        foreach (var subject in subjects)
        {
            var expected = records.Where(kv => AccessPolicy.Evaluate(subject, kv.Value, PlmAction.Read).Permitted).Select(kv => kv.Key).Order().ToList();
            var actual = (await facts.ReadAsync(subject, refs)).ToList();
            Assert.True(expected.SequenceEqual(actual.Select(f => f.Id).Order()), $"facts for {subject.Username}");
            Assert.All(actual, f => Assert.Equal(records[f.Id], f.Markings));
            Assert.All(actual.Where(f => f.Kind == "ncr"), f => Assert.Equal("Major", f.Severity));
            Assert.All(actual.Where(f => f.Kind == "capa"), f => Assert.Null(f.Severity));
            Assert.All(actual, f => Assert.False(f.Cancelled));
        }
        Assert.Empty(await facts.ReadAsync(QualityEnv.Alice, []));
    }
}
