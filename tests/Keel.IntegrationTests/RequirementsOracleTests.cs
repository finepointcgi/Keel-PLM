using System.Text.Json;
using Keel.Change;
using Keel.Core;
using Keel.Requirements;
using Keel.Security;

namespace Keel.IntegrationTests;

/// <summary>
/// The SQL read filters against the in-memory policy for the requirements module (ADR-0004, ADR-0038): for six subjects, what every query returns (outline, links, trace, coverage, matrix,
/// the view from a part, a document and a change, search counts) equals what the policy says the subject may see, and nothing of a restricted requirement or part appears anywhere.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RequirementsOracleTests(PostgresFixture pg)
{
    private static readonly Subject Kim = WorkflowEnv.User("kim", [Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]);
    private static readonly Subject Lena = WorkflowEnv.Lena; // not a U.S. person
    private static readonly Subject Audrey = WorkflowEnv.Audrey;

    private sealed record World(ReqEnv Env, string Spec, string[] Keys, Dictionary<string, Markings> ReqMarkings, string Open, string Itar, string Doc, string DocItar, string Change,
        Dictionary<string, string> Titles, Dictionary<string, Markings> PartMarkings, List<(string Req, string Part)> Allocations);

    private async Task<World> BuildAsync()
    {
        var env = await ReqEnv.CreateAsync(pg, documents: true);
        var spec = await env.SpecAsync("Mixed set", ReqEnv.Open);
        var itar = ReqEnv.Itar;
        var keys = new string[5];
        var titles = new Dictionary<string, string>();
        var markings = new Dictionary<string, Markings>();
        async Task<string> Add(int i, string title, Markings m, string? parent = null)
        {
            var k = await env.AddAsync(spec, ReqEnv.Input(title + $" oracle-{i}", $"Text {i} oracle-secret-{i}", parent: parent, markings: m));
            keys[i] = k; titles[k] = title + $" oracle-{i}"; markings[k] = m;
            return k;
        }
        var r0 = await Add(0, "Open parent", ReqEnv.Open);
        var r1 = await Add(1, "Restricted parent", itar);
        var r2 = await Add(2, "Open child of restricted", ReqEnv.Open, r1);
        var r3 = await Add(3, "Restricted child of open", itar, r0);
        var r4 = await Add(4, "Open other", ReqEnv.Open);
        var open = await env.C.NewPartAsync("Open part", "Component", ReqEnv.Open);
        var secret = await env.C.NewPartAsync("Secret part", "Component", itar);
        var partMarkings = new Dictionary<string, Markings> { [open] = ReqEnv.Open, [secret] = itar };
        var allocations = new List<(string, string)> { (r0, open), (r0, secret), (r1, open), (r2, open), (r3, open), (r3, secret), (r4, secret) };
        foreach (var (r, p) in allocations) await env.LinkAsync(spec, r, "allocated-to", p);
        var doc = await env.DocumentAsync("Open report", ReqEnv.Open);
        var docItar = await env.DocumentAsync("Secret report", itar);
        await env.LinkAsync(spec, r0, "verified-by", doc);
        await env.LinkAsync(spec, r0, "verified-by", docItar);
        await env.LinkAsync(spec, r1, "verified-by", doc);
        foreach (var k in new[] { r0, r1, r2, r3 })
            Assert.True((await env.Req.RecordVerificationAsync(ReqEnv.Alice, spec, k, new RecordVerificationCommand("pass", null, null, null, [doc], false, null), null)).IsOk);
        var ecn = await env.C.Change.CreateAsync(ReqEnv.Alice, new CreateChangeCommand(ChangeTypes.Ecn, "Oracle change", "d", "Medium", "reason", ReqEnv.Open, ChangeClasses.II), null);
        Assert.True(ecn.IsOk, string.Join("; ", ecn.Errors));
        await env.LinkAsync(spec, r0, "changed-by", ecn.Value!.Number);
        await env.LinkAsync(spec, r1, "changed-by", ecn.Value.Number);
        return new World(env, spec, keys, markings, open, secret, doc, docItar, ecn.Value.Number, titles, partMarkings, allocations);
    }

    private static bool Reads(Subject s, Markings m) => AccessPolicy.Evaluate(s, m, PlmAction.Read).Permitted;

    [Fact]
    public async Task For_every_subject_every_query_equals_the_policy_and_leaks_nothing()
    {
        var w = await BuildAsync();
        var env = w.Env;
        foreach (var subject in new[] { ReqEnv.Alice, Kim, ConfigEnv.Eve, Lena, ReqEnv.Mallory, Audrey })
        {
            var visible = w.Keys.Where(k => Reads(subject, w.ReqMarkings[k])).ToHashSet();
            var hidden = w.Keys.Where(k => !visible.Contains(k)).ToList();
            var readable = (await env.Req.GetAsync(subject, w.Spec, null, null)).IsOk;
            Assert.True(readable, $"{subject.Username} reads the open specification");
            var json = new List<string>();

            // Outline: readable requirements in place, one placeholder per restricted one, nothing else.
            var set = (await env.Req.GetRequirementsAsync(subject, w.Spec, null, null)).Value!;
            json.Add(JsonSerializer.Serialize(set));
            Assert.Equal(visible.Order(StringComparer.Ordinal), set.Rows.Where(r => !r.Redacted).Select(r => r.Key!).Order(StringComparer.Ordinal));
            Assert.Equal(hidden.Count, set.Rows.Count(r => r.Redacted));
            Assert.All(set.Rows.Where(r => r.Redacted), r => Assert.True(r.Key is null && r.Title is null && r.Text is null && r.Banner is null && r.Markings is null));
            var detail = (await env.Req.GetAsync(subject, w.Spec, null, null)).Value!;
            Assert.Equal(visible.Count, detail.Summary.RequirementCount); // the count is what the subject can read
            var search = (await env.Req.SearchAsync(subject, new SpecSearch(Query: "Mixed"), null)).Value!;
            Assert.Equal(visible.Count, search.Items.Single(i => i.Number == w.Spec).RequirementCount);

            // Links of each readable requirement: ends the subject can read, placeholders otherwise.
            foreach (var k in visible)
            {
                var links = (await env.Req.GetLinksAsync(subject, w.Spec, k, null)).Value!;
                json.Add(JsonSerializer.Serialize(links));
                var expectedAlloc = w.Allocations.Where(a => a.Req == k).ToList();
                var shownParts = links.Links.Where(l => !l.Redacted && l.LinkType == "allocated-to").Select(l => l.OtherNumber!).Order(StringComparer.Ordinal).ToList();
                Assert.Equal(expectedAlloc.Where(a => Reads(subject, w.PartMarkings[a.Part])).Select(a => a.Part).Order(StringComparer.Ordinal), shownParts);
            }

            // Spec trace and coverage: counts over what is readable.
            var trace = (await env.Req.GetTraceAsync(subject, w.Spec, null, null)).Value!;
            json.Add(JsonSerializer.Serialize(trace));
            var expectedLinks = w.Allocations.Count(a => visible.Contains(a.Req) && Reads(subject, w.PartMarkings[a.Part]));
            Assert.Equal(expectedLinks, trace.Links.Count(l => !l.Redacted && l.LinkType == "allocated-to"));
            var cov = (await env.Req.GetCoverageAsync(subject, w.Spec, null, null, null)).Value!;
            json.Add(JsonSerializer.Serialize(cov));
            Assert.Equal(visible.Count, cov.Summary.Total);
            Assert.Equal(visible.Count(k => w.Allocations.Any(a => a.Req == k && Reads(subject, w.PartMarkings[a.Part]))), cov.Summary.Allocated);
            Assert.Equal(visible.Count(k => k is var kk && new[] { w.Keys[0], w.Keys[1], w.Keys[2], w.Keys[3] }.Contains(kk)), cov.Summary.Verified);
            var matrix = (await env.Req.GetMatrixAsync(subject, w.Spec, "allocated-to", null, null)).Value!;
            json.Add(JsonSerializer.Serialize(matrix));
            Assert.Equal(expectedLinks, matrix.Cells.Count);

            // From the other side: a part, a document and a change.
            foreach (var part in new[] { w.Open, w.Itar })
            {
                var t = await env.Req.GetObjectTraceAsync(subject, "part", part, null);
                if (!Reads(subject, w.PartMarkings[part])) { Assert.Equal(OutcomeKind.NotFound, t.Kind); continue; }
                json.Add(JsonSerializer.Serialize(t.Value));
                var sources = w.Allocations.Where(a => a.Part == part).Select(a => a.Req).ToList();
                Assert.Equal(sources.Count(visible.Contains), t.Value!.Requirements.Count(r => !r.Redacted));
                Assert.Equal(sources.Count(k => !visible.Contains(k)), t.Value.Requirements.Count(r => r.Redacted));
                Assert.All(t.Value.Requirements.Where(r => r.Redacted), r => Assert.True(r.Key is null && r.Title is null && r.SpecNumber is null && r.LinkType is null && r.Banner is null));
            }
            var docTrace = await env.Req.GetObjectTraceAsync(subject, "document", w.DocItar, null);
            if (Reads(subject, ReqEnv.Itar)) Assert.True(docTrace.IsOk); else Assert.Equal(OutcomeKind.NotFound, docTrace.Kind);
            var openDoc = (await env.Req.GetObjectTraceAsync(subject, "document", w.Doc, null)).Value!;
            json.Add(JsonSerializer.Serialize(openDoc));
            Assert.Equal(visible.Count(k => k is var kk && new[] { w.Keys[0], w.Keys[1], w.Keys[2], w.Keys[3] }.Contains(kk)), openDoc.Requirements.Count(r => !r.Redacted));
            var changeTrace = (await env.Req.GetObjectTraceAsync(subject, "change", w.Change, null)).Value!;
            Assert.Equal(new[] { w.Keys[0], w.Keys[1] }.Count(visible.Contains), changeTrace.Requirements.Count(r => !r.Redacted));
            Assert.Equal(new[] { w.Keys[0], w.Keys[1] }.Count(k => !visible.Contains(k)), changeTrace.Requirements.Count(r => r.Redacted));
            json.Add(JsonSerializer.Serialize(changeTrace));

            // The impact provider (change impact) answers exactly like the part's trace.
            var impact = new RequirementImpactProvider(env.Req);
            var viaProvider = await impact.AffectedRequirementsAsync(subject, "part", w.Open, null);
            Assert.Equal((await env.Req.GetObjectTraceAsync(subject, "part", w.Open, null)).Value!.Requirements.Count, viaProvider.Value!.Requirements.Count);

            // Nothing about a restricted requirement or part appears in anything this subject was given.
            var all = string.Join('\n', json);
            foreach (var k in hidden)
            {
                Assert.DoesNotContain(w.Titles[k], all, StringComparison.Ordinal);
                Assert.DoesNotContain($"oracle-secret-{Array.IndexOf(w.Keys, k)}", all, StringComparison.Ordinal);
                Assert.DoesNotContain($"\"{k}\"", all, StringComparison.Ordinal);
            }
            if (!Reads(subject, ReqEnv.Itar))
            {
                Assert.DoesNotContain(w.Itar, all, StringComparison.Ordinal);
                Assert.DoesNotContain(w.DocItar, all, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Verification_and_evidence_follow_the_same_rules()
    {
        var w = await BuildAsync();
        var env = w.Env;
        // The first requirement's evidence list shows the open report and a placeholder for nothing (only the open one was used).
        var records = (await env.Req.GetVerificationsAsync(ReqEnv.Alice, w.Spec, w.Keys[0], null)).Value!;
        Assert.Equal(w.Doc, records.Records.Single().Evidence.Single().Number);
        // A restricted requirement's verification is not available to a subject who cannot read the requirement.
        Assert.Equal(OutcomeKind.NotFound, (await env.Req.GetVerificationsAsync(ConfigEnv.Eve, w.Spec, w.Keys[1], null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Req.GetLinksAsync(ConfigEnv.Eve, w.Spec, w.Keys[1], null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Req.RemoveLinkAsync(ConfigEnv.Eve, w.Spec, Guid.NewGuid(), null)).Kind);
    }
}
