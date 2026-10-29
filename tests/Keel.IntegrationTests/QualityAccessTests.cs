using Keel.Audit;
using Keel.Change;
using Keel.Core;
using Keel.Quality;
using Keel.Security;

namespace Keel.IntegrationTests;

/// <summary>
/// Access control of the quality records (ADR-0004): every list, count, search and part view is limited by the SQL read filter and agrees with the in-memory policy for
/// every probe user; what a user may not read never shows in a number, a total, a link or a timeline (8.1, ADR-0036).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QualityAccessTests(PostgresFixture pg)
{
    private static Subject Kim => QualityEnv.Kim;

    private static Markings Cti() => Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT", "CTI"], controllingOffice: "ACME",
        distributionReason: "Critical technology", program: "ORCA").Value;

    private static Markings Ear99() => Markings.Create(ExportJurisdiction.Ear, DistributionStatement.B, "ENG", eccn: Markings.Ear99, controllingOffice: "ACME", distributionReason: "Administrative", program: "ORCA").Value;

    private static Markings Authorized() => Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"], controllingOffice: "ACME",
        distributionReason: "Critical technology", program: "ORCA", exportAuthorizations: ["TAA-0042"]).Value;

    private static readonly (string Name, Subject Who)[] Probes =
    [
        ("alice (author, ORCA)", QualityEnv.Alice), ("eve (author, no program)", QualityEnv.Eve), ("kim (ORCA and KRAKEN)", Kim), ("mallory (viewer)", QualityEnv.Mallory),
        ("lena (foreign, no authorization)", WorkflowEnv.Lena), ("farid (foreign, TAA-0042)", WorkflowEnv.Farid), ("audrey (auditor, no program)", QualityEnv.Audrey),
        ("ada (administrator)", QualityEnv.Ada), ("a user without roles", WorkflowEnv.User("nobody", [])),
    ];

    private sealed record Seeded(string Kind, string Number, Markings Markings, string Part);

    private static async Task<List<Seeded>> SeedAsync(QualityEnv env)
    {
        var variants = new[] { QualityEnv.Open, QualityEnv.Itar("ORCA"), QualityEnv.Itar("KRAKEN"), Cti(), Ear99(), Authorized() };
        var list = new List<Seeded>();
        foreach (var m in variants)
        {
            var part = await env.Base.NewPartAsync("Part", m, Kim);
            list.Add(new Seeded(QualityKinds.Ncr, (await env.NewNcrAsync(Kim, part, m)).Number, m, part));
            list.Add(new Seeded(QualityKinds.Capa, (await env.NewCapaAsync(Kim, m)).Number, m, part));
            var dev = await env.NewDeviationAsync(Kim, m);
            await env.AddScopeAsync(Kim, dev.Number, part);
            list.Add(new Seeded(QualityKinds.Deviation, dev.Number, m, part));
        }
        return list;
    }

    [Fact]
    public async Task Lists_totals_and_dashboard_counts_agree_with_the_policy_for_every_probe_user_and_marking_variant()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var seeded = await SeedAsync(env);
        Assert.Equal(18, seeded.Count);
        var agreements = 0;
        foreach (var (name, who) in Probes)
        {
            foreach (var kind in QualityKinds.All)
            {
                var expected = seeded.Where(s => s.Kind == kind && AccessPolicy.Evaluate(who, s.Markings, PlmAction.Read).Permitted).Select(s => s.Number).Order().ToList();
                var found = await env.Queries.SearchAsync(who, kind, new QualitySearchQuery(Limit: 200), null);
                Assert.True(expected.SequenceEqual(found.Items.Select(i => i.Number).Order()), $"{name}: {kind} list differs from the policy");
                Assert.Equal(expected.Count, found.Total); // the total counts only what the user may read
                // a text search for a number the user may not read finds nothing
                foreach (var hidden in seeded.Where(s => s.Kind == kind && !expected.Contains(s.Number)))
                    Assert.Empty((await env.Queries.SearchAsync(who, kind, new QualitySearchQuery(Text: hidden.Number), null)).Items);
                // the single read agrees too, and a refusal is audited and answers "not found"
                foreach (var s in seeded.Where(x => x.Kind == kind))
                {
                    var read = kind switch
                    {
                        QualityKinds.Ncr => (await env.Ncrs.GetAsync(who, s.Number, null)).Kind,
                        QualityKinds.Capa => (await env.Capas.GetAsync(who, s.Number, null)).Kind,
                        _ => (await env.Deviations.GetAsync(who, s.Number, null)).Kind,
                    };
                    Assert.Equal(expected.Contains(s.Number) ? OutcomeKind.Ok : OutcomeKind.NotFound, read);
                    agreements++;
                }
            }

            var dash = await env.Queries.DashboardAsync(who, null);
            Assert.Equal(seeded.Count(s => s.Kind == QualityKinds.Ncr && AccessPolicy.Evaluate(who, s.Markings, PlmAction.Read).Permitted), dash.OpenNcrs);
            Assert.Equal(seeded.Count(s => s.Kind == QualityKinds.Capa && AccessPolicy.Evaluate(who, s.Markings, PlmAction.Read).Permitted), dash.OpenCapas);
            Assert.Equal(0, dash.ActiveDeviations); // none is approved yet

            // the part view: not found when the part itself is unreadable, otherwise only readable records
            foreach (var part in seeded.Select(s => s.Part).Distinct())
            {
                var partMarkings = seeded.First(s => s.Part == part).Markings;
                var view = await env.Queries.ForPartAsync(who, part, true, null);
                if (!AccessPolicy.Evaluate(who, partMarkings, PlmAction.Read).Permitted) { Assert.Equal(OutcomeKind.NotFound, view.Kind); continue; }
                Assert.True(view.IsOk);
                Assert.Equal(seeded.Count(s => s.Part == part && s.Kind == QualityKinds.Deviation && AccessPolicy.Evaluate(who, s.Markings, PlmAction.Read).Permitted), view.Value!.Deviations.Count);
                Assert.Equal(seeded.Count(s => s.Part == part && s.Kind == QualityKinds.Ncr && AccessPolicy.Evaluate(who, s.Markings, PlmAction.Read).Permitted), view.Value.OpenNcrs.Count);
            }
        }
        Assert.True(agreements >= 9 * 18);
    }

    [Fact]
    public async Task A_restricted_record_changes_no_number_a_restricted_user_can_see()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var openPart = await env.Base.NewPartAsync("Open part", QualityEnv.Open, QualityEnv.Alice);
        await env.NewNcrAsync(QualityEnv.Alice, openPart, QualityEnv.Open);
        var before = (await env.Queries.DashboardAsync(QualityEnv.Eve, null), (await env.Queries.SearchAsync(QualityEnv.Eve, QualityKinds.Ncr, new QualitySearchQuery(), null)).Total);
        var secretPart = await env.ReleasedPartAsync();
        var secret = await env.NewNcrAsync(QualityEnv.Alice, secretPart);
        await env.NewCapaAsync(QualityEnv.Alice);
        var dev = await env.NewDeviationAsync(QualityEnv.Alice);
        await env.AddScopeAsync(QualityEnv.Alice, dev.Number, secretPart);
        await env.AddActionAsync(QualityEnv.Alice, "ncr", secret.Number, "Containment", "bob");
        var after = (await env.Queries.DashboardAsync(QualityEnv.Eve, null), (await env.Queries.SearchAsync(QualityEnv.Eve, QualityKinds.Ncr, new QualitySearchQuery(), null)).Total);
        Assert.Equal(before, after); // not a count, not a total, not an overdue number moved
        Assert.Equal(1, after.Item1.OpenNcrs);
        Assert.Equal(0, after.Item1.OpenCapas);
        Assert.Equal(OutcomeKind.NotFound, (await env.Ncrs.GetAsync(QualityEnv.Eve, secret.Number, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Ncrs.GetAsync(QualityEnv.Eve, "NCR-999999", null)).Kind); // the same answer as for a missing number
        // she cannot act on it either: not found, and the attempt is audited as denied
        var act = await env.Actions.AddAsync(QualityEnv.Eve, "ncr", secret.Number, new CreateActionCommand("Containment", "x", "eve", null), null);
        Assert.Equal(OutcomeKind.NotFound, act.Kind);
        var denied = (await env.AuditAsync()).Where(e => e.Outcome == AuditOutcome.Denied && e.ObjectId == secret.Number).ToList();
        Assert.Contains(denied, e => e.Action == "ncr.read");
        Assert.Contains(denied, e => e.Action == "ncr.action_create");
        // the part page of a part she can read shows nothing of a record she cannot
        var summary = (await env.Queries.ForPartAsync(QualityEnv.Eve, openPart, true, null)).Value!;
        Assert.Single(summary.OpenNcrs);
        Assert.Equal(OutcomeKind.NotFound, (await env.Queries.ForPartAsync(QualityEnv.Eve, secretPart, true, null)).Kind);
        // and her timeline of the open part names only what she may read
        var timeline = await new QualityTimelineSourceProbe(env.Ds).GetAsync(QualityEnv.Eve, openPart);
        Assert.Single(timeline);
    }

    [Fact]
    public async Task A_link_to_something_the_viewer_may_not_read_is_a_neutral_row()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var change = await env.Base.NewChangeAsync(Kim, ChangeTypes.Ecn, "Kraken change", QualityEnv.Itar("KRAKEN"), ChangeClasses.II);
        var capa = await env.NewCapaAsync(Kim, QualityEnv.Itar("ORCA"));
        Assert.True((await env.Actions.AddLinkAsync(Kim, "capa", capa.Number, "change", change.Number, null)).IsOk);
        // alice cannot even find the change to link it
        var refused = await env.Actions.AddLinkAsync(QualityEnv.Alice, "capa", capa.Number, "change", change.Number, null);
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        Assert.Contains("was not found", refused.Errors[0], StringComparison.Ordinal);

        var kimView = (await env.Capas.GetAsync(Kim, capa.Number, null)).Value!;
        var named = Assert.Single(kimView.Links);
        Assert.False(named.Redacted);
        Assert.Equal(change.Number, named.Key);

        var aliceView = (await env.Capas.GetAsync(QualityEnv.Alice, capa.Number, null)).Value!;
        var placeholder = Assert.Single(aliceView.Links);
        Assert.True(placeholder.Redacted);
        Assert.Equal((null, null, null, null, null), (placeholder.Key, placeholder.Title, placeholder.StateLabel, placeholder.Href, placeholder.Banner));
        Assert.DoesNotContain("KRAKEN", aliceView.Banner, StringComparison.Ordinal);
        Assert.DoesNotContain(change.Number, System.Text.Json.JsonSerializer.Serialize(aliceView), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_supplier_pages_reference_resolver_names_only_what_the_caller_may_read()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();
        var ncr = await env.NewNcrAsync(QualityEnv.Alice, part);
        var resolver = new QualityReferenceResolver(env.Ds);
        Assert.True(resolver.Handles("ncr"));
        Assert.False(resolver.Handles("supplier"));
        var hit = await resolver.ResolveAsync(QualityEnv.Alice, "ncr", ncr.Number);
        Assert.NotNull(hit);
        Assert.Equal($"quality/ncrs/{ncr.Number}", hit.Href);
        Assert.Null(await resolver.ResolveAsync(QualityEnv.Eve, "ncr", ncr.Number)); // exists, but not for her: indistinguishable from missing
        Assert.Null(await resolver.ResolveAsync(QualityEnv.Alice, "ncr", "NCR-999999"));
        Assert.Null(await resolver.ResolveAsync(QualityEnv.Alice, "capa", ncr.Number)); // a number of another kind
    }

    [Fact]
    public async Task Server_side_sorting_orders_by_the_whitelisted_columns_of_readable_rows()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var part = await env.ReleasedPartAsync();
        var minor = await env.NewNcrAsync(QualityEnv.Alice, part, title: "B second", severity: "Minor");
        var critical = await env.NewNcrAsync(QualityEnv.Alice, part, title: "A first", severity: "Critical");
        var asc = await env.Queries.SearchAsync(QualityEnv.Alice, QualityKinds.Ncr, new QualitySearchQuery(Sort: new SortSpec("title", false)), null);
        Assert.Equal([critical.Number, minor.Number], asc.Items.Select(i => i.Number));
        var desc = await env.Queries.SearchAsync(QualityEnv.Alice, QualityKinds.Ncr, new QualitySearchQuery(Sort: new SortSpec("title", true)), null);
        Assert.Equal([minor.Number, critical.Number], desc.Items.Select(i => i.Number));
        var bySeverity = await env.Queries.SearchAsync(QualityEnv.Alice, QualityKinds.Ncr, new QualitySearchQuery(Sort: new SortSpec("severity", false)), null);
        Assert.Equal([critical.Number, minor.Number], bySeverity.Items.Select(i => i.Number)); // "Critical" sorts before "Minor"
        var page = await env.Queries.SearchAsync(QualityEnv.Alice, QualityKinds.Ncr, new QualitySearchQuery(Limit: 1, Offset: 1, Sort: new SortSpec("title", false)), null);
        Assert.Equal((2, minor.Number), (page.Total, Assert.Single(page.Items).Number));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Task.FromResult(QualityQueries.SortKeysFor(QualityKinds.Ncr).Resolve("part_number; --", null)));
    }

    [Fact]
    public async Task Part_applicability_never_names_a_record_the_caller_cannot_read_even_by_serial_and_lot()
    {
        var env = await QualityEnv.CreateAsync(pg);
        var part = await env.Base.NewPartAsync("Shared part", QualityEnv.Open, Kim);
        var open = await env.NewDeviationAsync(Kim, QualityEnv.Open, "Open one");
        var secret = await env.NewDeviationAsync(Kim, QualityEnv.Itar("KRAKEN"), "Kraken one");
        // the KRAKEN record must cover the open part: it does (it is more restrictive)
        await env.AddScopeAsync(Kim, open.Number, part);
        await env.AddScopeAsync(Kim, secret.Number, part);
        await env.ApproveAllAsync(await env.SubmitAsync("deviation", open.Number, Kim));
        // the KRAKEN record needs an approver who may read it
        var quinn = WorkflowEnv.User("quinn", [Roles.Viewer, Roles.Approver], programs: ["ORCA", "KRAKEN"]);
        await env.Base.Directory.UpsertAsync(quinn);
        var v = await env.SubmitAsync("deviation", secret.Number, Kim, "rita", "quinn");
        Assert.Equal("Approved", (await env.SignAsync(quinn, WorkflowEnv.TaskOf(v, "quinn"), "Approved")).Status);
        var forKim = (await env.Deviations.ForPartAsync(Kim, part, false, "5", "L-1", null, null)).Value!;
        Assert.Equal(2, forKim.Count);
        var forAlice = (await env.Deviations.ForPartAsync(QualityEnv.Alice, part, false, "5", "L-1", null, null)).Value!;
        var only = Assert.Single(forAlice);
        Assert.Equal(open.Number, only.Number);
        Assert.Equal(1, (await env.Queries.DashboardAsync(QualityEnv.Alice, null)).ActiveDeviations);
        Assert.Equal(2, (await env.Queries.DashboardAsync(Kim, null)).ActiveDeviations);
    }
}
