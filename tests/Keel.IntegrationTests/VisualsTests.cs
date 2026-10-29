using System.Diagnostics;
using Keel.Audit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Documents;
using Keel.Parts;
using Keel.Security;
using Keel.TestSupport;
using Keel.Web.Services;
using Keel.Web.Services.Visuals;
using Keel.Workflow;

namespace Keel.IntegrationTests;

/// <summary>
/// The relationship pictures against real Postgres (UX-3, ADR-0029): for six kinds of user the impact graph, treemap, diff and timeline contain exactly what
/// <see cref="AccessPolicy.Evaluate"/> allows and show everything else only as neutral placeholders; the merge order is stable; the workflow map equals the workflow
/// views; the SVG export carries the banner and no hidden identifier; and the layouts meet their budgets on the 50,000-line synthetic BOM.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VisualsTests(PostgresFixture pg)
{
    private static readonly Subject Alice = WorkflowEnv.Alice;
    private static readonly Subject Eve = WorkflowEnv.User("eve", [Roles.Viewer, Roles.Author], programs: []);
    private static readonly Subject Kim = WorkflowEnv.User("kim", [Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]);
    private static readonly Subject Lena = WorkflowEnv.Lena;
    private static readonly Subject Mallory = WorkflowEnv.Mallory;
    private static readonly Subject Audrey = WorkflowEnv.Audrey;
    private static readonly Subject[] Users = [Alice, Kim, Eve, Lena, Mallory, Audrey];

    private static readonly Markings Open = ConfigEnv.Open;
    private static readonly Markings Orca = WorkflowEnv.Itar("ORCA");
    private static readonly Markings Kraken = WorkflowEnv.Itar("KRAKEN");

    private static bool Can(Subject s, Markings m) => AccessPolicy.Evaluate(s, m, PlmAction.Read).Permitted;

    /// <summary>
    /// The world of the tests. Bracket (open, released, ECN-listed) is used by SUB (open), ORCA-HOUSING (ORCA) and KRAKEN-GEARBOX (KRAKEN); TOP (open) uses them all.
    /// Two documents are linked to the bracket (an open drawing and a KRAKEN specification); three other changes list it (one open, one KRAKEN, plus the ECN under test);
    /// a second ECN revises TOP (screw quantity 4 to 5, and a KRAKEN shim is added).
    /// </summary>
    private sealed class Scene
    {
        public required MbomEnv M { get; init; }
        public ConfigEnv C => M.C;
        public required ImpactAnalysisService Impact { get; init; }
        public required TimelineService Timeline { get; init; }
        public string Screw = "", Bracket = "", Plate = "", Sub = "", OrcaAsm = "", KrakenAsm = "", Kpart = "", Top = "";
        public string Ecn = "", OpenEcr = "", KrakenEcn = "", TopEcn = "", Drawing = "", Spec = "", Baseline = "", Mbom = "";
        public Dictionary<string, Markings> Marks { get; } = [];
        public Dictionary<string, string> Names { get; } = [];
        public Dictionary<string, string> Titles { get; } = [];

        public IEnumerable<string> HiddenIdentifiers(Subject s) =>
            Marks.Where(m => !Can(s, m.Value)).SelectMany(m => new[] { m.Key, Names.GetValueOrDefault(m.Key) ?? "" }.Where(x => x.Length > 0))
                .Concat(Marks.Where(m => !Can(s, m.Value) && Titles.ContainsKey(m.Key)).Select(m => Titles[m.Key]));
    }

    private async Task<Scene> BuildAsync()
    {
        var c = await ConfigEnv.CreateAsync(pg, documents: true);
        var m = await MbomEnv.CreateAsync(pg, c);
        var audit = c.Audit;
        var scene = new Scene
        {
            M = m,
            Impact = new ImpactAnalysisService(c.Ds, audit, [new PartChangeGateway([new BomParticipant()])], [new BomStructureImpactProvider(c.Bom, c.Ds)], [new LinkedDocumentProvider(c.Docs!)], [new MbomImpactProvider(c.Ds, audit)]),
            Timeline = new TimelineService([new PartTimelineSource(c.Ds), new DocumentTimelineSource(c.Ds), new WorkflowTimelineSource(m.Flow), new ChangeTimelineSource(c.Ds, new RevisionEffectivityService()), new BomTimelineSource(c.Ds)], audit),
        };
        async Task<string> Part(string name, string type, Markings mk, Subject by)
        {
            var n = await c.NewPartAsync(name, type, mk, by);
            scene.Marks[n] = mk;
            scene.Names[n] = name;
            return n;
        }
        async Task Use(string parent, string child, decimal qty, int find, Subject by)
        {
            await c.CheckOutAsync(parent, by);
            await c.AddLineAsync(parent, child, qty, find, null, by);
            await c.CheckInAsync(parent, by, "structure");
        }

        scene.Screw = await m.BuyPartAsync("Screw");
        scene.Marks[scene.Screw] = Open;
        scene.Names[scene.Screw] = "Screw";
        scene.Bracket = await Part("Sensor bracket", "Component", Open, Alice);
        scene.Plate = await Part("Orca plate", "Component", Orca, Alice);
        scene.Kpart = await Part("Kraken shim", "Component", Kraken, Kim);

        // Two documents linked to the bracket by Kim (the only author who can read both), then the bracket is released.
        scene.Drawing = (await c.Docs!.CreateAsync(Alice, new CreateDocumentCommand("Bracket drawing", "Drawing", Open), ChangeEnv.File("bracket.txt"), null)).Value!.Number;
        scene.Spec = (await c.Docs.CreateAsync(Kim, new CreateDocumentCommand("Kraken specification", "Specification", Kraken), ChangeEnv.File("spec.txt"), null)).Value!.Number;
        (scene.Marks[scene.Drawing], scene.Names[scene.Drawing]) = (Open, "Bracket drawing");
        (scene.Marks[scene.Spec], scene.Names[scene.Spec]) = (Kraken, "Kraken specification");
        await c.CheckOutAsync(scene.Bracket, Kim);
        Assert.True((await c.Docs.AddLinkToWorkingCopyAsync(Kim, scene.Bracket, scene.Drawing, "describes", null, null)).IsOk);
        Assert.True((await c.Docs.AddLinkToWorkingCopyAsync(Kim, scene.Bracket, scene.Spec, "references", null, null)).IsOk);
        await c.CheckInAsync(scene.Bracket, Kim, "Linked the drawing and the specification");
        await c.ReleaseAsync(scene.Bracket);

        scene.Sub = await Part("Sensor module", "Assembly", Open, Alice);
        await Use(scene.Sub, scene.Bracket, 1, 10, Alice);
        await c.ReleaseAsync(scene.Sub);
        scene.OrcaAsm = await Part("Orca housing", "Assembly", Orca, Alice);
        await Use(scene.OrcaAsm, scene.Bracket, 2, 10, Alice);
        scene.KrakenAsm = await Part("Kraken gearbox", "Assembly", Kraken, Kim);
        await Use(scene.KrakenAsm, scene.Bracket, 3, 10, Kim);

        scene.Top = await Part("Top assembly", "Assembly", Open, Alice);
        await c.CheckOutAsync(scene.Top);
        await c.AddLineAsync(scene.Top, scene.Screw, 4, 10);
        await c.AddLineAsync(scene.Top, scene.Plate, 1, 20);
        await c.AddLineAsync(scene.Top, scene.Sub, 2, 30);
        await c.AddLineAsync(scene.Top, scene.OrcaAsm, 1, 40);
        await c.CheckInAsync(scene.Top);
        await Use(scene.Top, scene.KrakenAsm, 1, 50, Kim);
        await c.ReleaseAsync(scene.Top);

        // Changes that list the bracket: the one under test, an open request, and a KRAKEN notice.
        async Task<string> Change(Subject by, string type, string title, Markings mk, string? cls, string key, string action)
        {
            var created = await c.Change.CreateAsync(by, new CreateChangeCommand(type, title, "Synthetic", "Medium", "Synthetic reason", mk, cls), "corr-create");
            Assert.True(created.IsOk, string.Join("; ", created.Errors));
            var number = created.Value!.Number;
            var added = await c.Change.AddAffectedAsync(by, number, new AddAffectedCommand("part", key, action), "corr-add");
            Assert.True(added.IsOk, $"{added.Kind} {string.Join("; ", added.Errors)}");
            scene.Marks[number] = mk;
            scene.Titles[number] = title;
            scene.Names[number] = title;
            return number;
        }
        scene.Ecn = await Change(Alice, ChangeTypes.Ecn, "Stiffen the bracket", Open, ChangeClasses.II, scene.Bracket, ChangeActions.Revise);
        scene.OpenEcr = await Change(Alice, ChangeTypes.Ecr, "Also looks at the bracket", Open, null, scene.Bracket, ChangeActions.None);
        scene.KrakenEcn = await Change(Kim, ChangeTypes.Ecn, "Kraken retrofit of the bracket", Kraken, ChangeClasses.II, scene.Bracket, ChangeActions.None);

        // A second notice revises TOP: the screw quantity goes from 4 to 5 and a KRAKEN shim is added (by Kim).
        var topEcn = await c.ReviseWithEffectivityAsync(scene.Top, null);
        scene.TopEcn = topEcn.Number;
        await c.CheckOutAsync(scene.Top, Kim);
        var screwLine = (await c.Bom.GetWorkingAsync(Kim, scene.Top, null)).Value!.Lines.Single(l => l.FindNumber == 10);
        Assert.True((await c.Bom.UpdateUsageAsync(Kim, scene.Top, screwLine.Id, new UpdateUsageCommand(5, screwLine.Unit, 10, screwLine.ReferenceDesignators, screwLine.Notes), null)).IsOk);
        await c.AddLineAsync(scene.Top, scene.Kpart, 1, 60, null, Kim);
        await c.CheckInAsync(scene.Top, Kim, "Screw 5, add the shim");

        scene.Baseline = (await c.BaselineAsync(scene.Sub)).Summary.Number;
        scene.Mbom = (await m.GenerateAsync(scene.Sub)).Summary.Number;
        return scene;
    }

    private static IEnumerable<string> Strings(GraphNode n) => new[] { n.Id, n.Label, n.Sublabel, n.Href, n.Description, n.Banner }.Where(s => s is not null).Select(s => s!).Concat(n.Facts?.SelectMany(f => new[] { f.Key, f.Value }) ?? []);

    private static string Everything(ImpactGraph g) => string.Join('\n', g.Nodes.SelectMany(Strings).Concat(g.Edges.SelectMany(e => new[] { e.From, e.To, e.Label })).Append(g.Banner));

    private static void AssertNoneOf(IEnumerable<string> hidden, string text, string who) =>
        Assert.All(hidden.Distinct(), h => Assert.False(text.Contains(h, StringComparison.Ordinal), $"{who} was shown '{h}'"));

    // ---------------------------------------------------------------- (a) no-leak oracle

    [Fact]
    public async Task The_impact_graph_contains_exactly_what_the_policy_allows_and_hidden_items_are_neutral_placeholders()
    {
        var s = await BuildAsync();
        foreach (var user in Users)
        {
            var detail = (await s.C.Change.GetAsync(user, s.Ecn, null)).Value!;
            var report = (await s.Impact.AnalyzeAsync(user, s.Ecn, 10, "corr-map")).Value!;
            var graph = ImpactGraphBuilder.Build(detail, report, new ImpactGraphOptions(ShowLevels: 10));

            var parents = new[] { s.Sub, s.Top }.Concat(new[] { s.OrcaAsm, s.KrakenAsm }.Where(p => Can(user, s.Marks[p]))).Order().ToList();
            Assert.Equal(parents, graph.Nodes.Where(n => n.Kind == GraphNodeKind.Parent).Select(n => n.Label).Order().ToList());
            var hiddenParents = new[] { s.OrcaAsm, s.KrakenAsm }.Count(p => !Can(user, s.Marks[p]));
            Assert.Equal(hiddenParents, graph.Nodes.Count(n => n.IsRestricted && n.Id.StartsWith("pr:", StringComparison.Ordinal)));
            // Documents: the drawing for everyone, the KRAKEN specification only for those who may read it; otherwise one placeholder.
            var docs = graph.Nodes.Where(n => n.Kind == GraphNodeKind.Document).Select(n => n.Label).Order().ToList();
            Assert.Equal(new[] { s.Drawing }.Concat(Can(user, Kraken) ? [s.Spec] : []).Order().ToList(), docs);
            Assert.Equal(Can(user, Kraken) ? 0 : 1, graph.Nodes.Count(n => n.IsRestricted && n.Id.StartsWith("dr:", StringComparison.Ordinal)));
            // Other open changes: the service names the readable ones and omits the others entirely.
            Assert.Equal(new[] { s.OpenEcr, s.TopEcn }.Concat(Can(user, Kraken) ? [s.KrakenEcn] : []).Order().ToList(), graph.Nodes.Where(n => n.Kind == GraphNodeKind.Conflict).Select(n => n.Label).Order().ToList());
            // Restricted nodes carry nothing but the fixed label.
            Assert.All(graph.Nodes.Where(n => n.IsRestricted), n => { Assert.Equal(ImpactGraphBuilder.RestrictedLabel, n.Label); Assert.Null(n.Href); Assert.Null(n.Sublabel); Assert.Null(n.Banner); });
            // No hidden identifier, name, title or marking text anywhere.
            AssertNoneOf(s.HiddenIdentifiers(user), Everything(graph), user.Username!);
            if (!Can(user, Kraken)) Assert.DoesNotContain("KRAKEN", Everything(graph), StringComparison.Ordinal);
            // The banner is the union of what is drawn.
            var drawn = graph.Nodes.Select(n => n.Banner).Where(b => b is not null).ToArray();
            Assert.Equal(BannerMerge.Union(drawn), graph.Banner);
            Assert.Equal(Can(user, Orca) || Can(user, Kraken), graph.Banner.Contains("ITAR", StringComparison.Ordinal)); // only a visible ITAR item raises the banner
            // The layout of what is drawn is valid.
            var layout = LayeredLayout.Compute([.. graph.Nodes.Select(n => { var (w, h) = GraphGeometry.Size(n.Kind); return new LayoutNodeInput(n.Id, n.Layer, w, h); })], [.. graph.Edges.Select(e => new LayoutEdgeInput(e.From, e.To))]);
            Assert.Equal(graph.Nodes.Count, layout.Nodes.Count);
        }
        // The oracle is not vacuous: the users really see different pictures.
        var kim = ImpactGraphBuilder.Build((await s.C.Change.GetAsync(Kim, s.Ecn, null)).Value!, (await s.Impact.AnalyzeAsync(Kim, s.Ecn, 10, null)).Value!);
        var eve = ImpactGraphBuilder.Build((await s.C.Change.GetAsync(Eve, s.Ecn, null)).Value!, (await s.Impact.AnalyzeAsync(Eve, s.Ecn, 10, null)).Value!);
        Assert.True(kim.Nodes.Count(n => !n.IsRestricted) > eve.Nodes.Count(n => !n.IsRestricted));
        Assert.Contains(s.KrakenAsm, Everything(kim), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_treemap_tree_holds_exactly_the_readable_parts_and_one_block_of_the_restricted_quantities()
    {
        var s = await BuildAsync();
        var structure = new Dictionary<string, (string Child, int Find, decimal Qty)[]>
        {
            [s.Top] = [(s.Screw, 10, 5), (s.Plate, 20, 1), (s.Sub, 30, 2), (s.OrcaAsm, 40, 1), (s.KrakenAsm, 50, 1), (s.Kpart, 60, 1)],
            [s.Sub] = [(s.Bracket, 10, 1)],
            [s.OrcaAsm] = [(s.Bracket, 10, 2)],
            [s.KrakenAsm] = [(s.Bracket, 10, 3)],
        };
        foreach (var user in Users)
        {
            var explosion = (await s.C.Bom.ExplodeAsync(user, new BomQuery(s.Top), null)).Value!;
            var root = BomTreemapModel.Build(explosion.RootNumber, explosion.RootName, explosion.Lines);

            // Oracle: a part is shown when it and every ancestor are readable; a hidden child of a shown part is part of that part's restricted block.
            var expectedParts = new List<string>();
            var expectedBlocks = new Dictionary<string, decimal>();
            void Walk(string part, string path, decimal multiplier)
            {
                var hidden = 0m;
                foreach (var (child, find, qty) in structure.GetValueOrDefault(part, []))
                {
                    if (!Can(user, s.Marks[child])) { hidden += qty * multiplier; continue; }
                    expectedParts.Add(child);
                    Walk(child, path + "." + find, qty * multiplier);
                }
                if (hidden > 0) expectedBlocks[part] = hidden;
            }
            Walk(s.Top, "", 1);
            var nodes = new List<TmNode>();
            void Collect(TmNode n) { nodes.Add(n); n.Children.ForEach(Collect); }
            Collect(root);
            Assert.Equal(expectedParts.Order().ToList(), nodes.Where(n => n is { Redacted: false } && n.Path.Length > 0).Select(n => n.Number!).Order().ToList());
            var blocks = nodes.Where(n => n.Redacted).ToList();
            Assert.Equal(expectedBlocks.Count, blocks.Count); // one block per assembly that has hidden children, never one per line
            Assert.Equal(expectedBlocks.Values.Order().ToList(), blocks.Select(b => b.ExtendedQuantity).Order().ToList()); // sized by the quantities the service exposes
            Assert.All(blocks, b => { Assert.Null(b.Number); Assert.Null(b.Banner); Assert.Equal(BomTreemapModel.RestrictedLabel, b.Label); });
            // No hidden identifier in any field of any node, nor in the tiles.
            var layout = BomTreemapModel.Layout(root, 960, 540, TreemapMetric.ExtendedQuantity);
            var text = string.Join('\n', nodes.SelectMany(n => new[] { n.Path, n.Label, n.Sublabel, n.Number, n.Banner, n.StateLabel }).Where(x => x is not null));
            AssertNoneOf(s.HiddenIdentifiers(user), text, user.Username!);
            Assert.Equal(960.0 * 540.0, layout.Tiles.Where(t => t.Depth == 1).Sum(t => t.Width * t.Height), 3);
            Assert.True(layout.TileCount <= 400);
        }
    }

    [Fact]
    public async Task The_structure_diff_names_only_readable_lines_and_the_hidden_addition_is_one_restricted_row()
    {
        var s = await BuildAsync();
        foreach (var user in Users)
        {
            var cmpOutcome = await s.C.Bom.CompareAsync(user, s.Top, "A.3", "B.3", null);
            Assert.True(cmpOutcome.IsOk, $"{cmpOutcome.Kind} {string.Join("; ", cmpOutcome.Errors)}");
            var cmp = cmpOutcome.Value!;
            var summary = string.Join(", ", cmp.Lines.Select(l => l.Kind + ":" + l.Number + ":" + string.Join("/", l.Changes)));
            TestContext.Current.SendDiagnosticMessage(user.Username + ": " + cmp.Lines.Count + " lines, " + cmp.Unchanged + " unchanged: " + summary);
            var history = string.Join(",", (await s.C.Parts.GetAsync(user, s.Top, null, null)).Value!.History.Select(h => h.Label));
            Assert.True(cmp.Lines.Count > 0, "versions " + history + "; " + summary + "; unchanged " + cmp.Unchanged);
            var model = StructureDiffModel.FromShallow(cmp);
            Assert.Equal((1, 0, 1), (model.Added, model.Removed, model.Changed));
            var added = model.Rows.Single(r => r.Kind == BomChangeKind.Added);
            Assert.Equal(!Can(user, Kraken), added.Redacted);
            Assert.Equal(Can(user, Kraken) ? s.Kpart : "Restricted item", added.Label);
            Assert.Equal(Can(user, Kraken) ? $"parts/{s.Kpart}" : null, added.Href);
            var changed = model.Rows.Single(r => r.Kind == BomChangeKind.Changed);
            Assert.Equal(s.Screw, changed.Label);
            Assert.Equal("Qty 4 → 5", changed.Summary);
            var text = string.Join('\n', model.Rows.SelectMany(r => new[] { r.Label, r.Sublabel, r.Href, r.Left, r.Right, r.Summary, r.Banner }).Where(x => x is not null));
            AssertNoneOf(s.HiddenIdentifiers(user), text, user.Username!);
        }
    }

    private static async Task<TimelineResult> TimelineOf(Scene s, Subject user, string type, string key) => (await s.Timeline.GetAsync(user, type, key, "corr-timeline")).Value!;

    [Fact]
    public async Task The_timeline_has_exactly_the_events_the_policy_allows_and_neutral_entries_for_hidden_objects()
    {
        var s = await BuildAsync();
        var counts = new Dictionary<string, int>();
        foreach (var user in Users)
        {
            var result = await TimelineOf(s, user, "part", s.Bracket);
            var events = result.Events;
            counts[user.Username!] = events.Count;
            var kraken = Can(user, Kraken);
            // Changes: the ECN under test and the open request are named for everyone; the KRAKEN notice only for those who may read it.
            Assert.Contains(events, e => e.Title == $"Listed in {s.Ecn}");
            Assert.Contains(events, e => e.Title == $"Listed in {s.OpenEcr}");
            Assert.Equal(kraken, events.Any(e => e.Title == $"Listed in {s.KrakenEcn}"));
            Assert.Equal(kraken, events.Any(e => e.Title.Contains(s.Spec, StringComparison.Ordinal)));
            Assert.Contains(events, e => e.Title.Contains(s.Drawing, StringComparison.Ordinal));
            Assert.Contains(events, e => e.Title.StartsWith($"Included in baseline {s.Baseline}", StringComparison.Ordinal));
            // The two hidden things (the KRAKEN notice, the KRAKEN specification link) are placeholders, one each.
            var placeholders = events.Where(e => e.Redacted).ToList();
            Assert.Equal(kraken ? 0 : 2, placeholders.Count);
            Assert.All(placeholders, e => { Assert.Equal(TimelineKinds.Restricted, e.Kind); Assert.Equal("A restricted item was involved", e.Title); Assert.Null(e.Detail); Assert.Null(e.Actor); Assert.Null(e.Href); Assert.Null(e.Revision); Assert.Null(e.Markings); });
            // No hidden identifier, title, name or marking text in any event.
            var text = string.Join('\n', events.SelectMany(e => new[] { e.Title, e.Detail, e.Actor, e.Href, e.Revision, e.Markings?.BannerText() }).Where(x => x is not null));
            AssertNoneOf(s.HiddenIdentifiers(user), text, user.Username!);
            if (!kraken) Assert.DoesNotContain("KRAKEN", text, StringComparison.Ordinal);
            // Kinds that exist for everyone: revisions, check-ins, signatures, lifecycle (review submitted and approved).
            foreach (var kind in new[] { TimelineKinds.Revision, TimelineKinds.Iteration, TimelineKinds.Signature, TimelineKinds.Lifecycle, TimelineKinds.Change, TimelineKinds.Link, TimelineKinds.Baseline })
                Assert.Contains(events, e => e.Kind == kind);
            // The shown markings are exactly those of the named other objects.
            Assert.All(result.Shown, m => Assert.True(Can(user, m)));
        }
        // A hidden object becomes a placeholder, so the number of entries is the same for every user: nothing is dropped or added.
        Assert.Single(counts.Values.Distinct());

        // A part the user cannot read at all is not found and leaves a denial in the audit log.
        var secret = await s.C.NewPartAsync("Secret ORCA part", "Component", Orca, Alice);
        Assert.True((await s.Timeline.GetAsync(Alice, "part", secret, null)).IsOk);
        var denied = await s.Timeline.GetAsync(Eve, "part", secret, "corr-denied");
        Assert.Equal(OutcomeKind.NotFound, denied.Kind);
        Assert.Contains(await s.C.AuditAsync(), a => a is { Action: "timeline.read", Outcome: AuditOutcome.Denied } && a.ObjectId == secret);
        Assert.Equal(OutcomeKind.NotFound, (await s.Timeline.GetAsync(Eve, "part", "P-999999", null)).Kind); // identical to a part that does not exist
    }

    [Fact]
    public async Task Timeline_merging_is_stable_ordered_by_time_and_the_documents_and_manufacturing_boms_have_their_own()
    {
        var s = await BuildAsync();
        var first = (await TimelineOf(s, Alice, "part", s.Bracket)).Events;
        var second = (await TimelineOf(s, Alice, "part", s.Bracket)).Events;
        Assert.Equal(first.Select(e => (e.At, e.Kind, e.Title, e.Order)), second.Select(e => (e.At, e.Kind, e.Title, e.Order)));
        Assert.Equal(first.OrderBy(e => e.At).ThenBy(e => e.SourceRank).ThenBy(e => e.Order).Select(e => e.Title), first.Select(e => e.Title));
        Assert.Equal(first.Select(e => e.At), first.Select(e => e.At).Order());
        // Entries of the first revision come before entries of the second.
        var revisionB = first.First(e => e.Title == "Revision B created");
        Assert.True(first.Where(e => e.Title.StartsWith("Version A.", StringComparison.Ordinal)).All(e => e.At <= revisionB.At));
        // The signatures carry who signed and the meaning, and link to the workflow.
        var signature = first.First(e => e.Kind == TimelineKinds.Signature);
        Assert.Contains("signed:", signature.Title, StringComparison.Ordinal);
        Assert.StartsWith("workflows/", signature.Href, StringComparison.Ordinal);
        // Deep links name the exact revision.
        Assert.Contains(first, e => e.Href == $"parts/{s.Bracket}?revision=B#history");

        // The sub-assembly's own life shows the baseline and the manufacturing BOM derived from it.
        var sub = (await TimelineOf(s, Alice, "part", s.Sub)).Events;
        Assert.Contains(sub, e => e.Kind == TimelineKinds.Mbom && e.Title.Contains(s.Mbom, StringComparison.Ordinal));
        Assert.Contains(sub, e => e.Kind == TimelineKinds.Baseline && e.Title.Contains(s.Baseline, StringComparison.Ordinal));
        // The manufacturing BOM has a timeline of its own, and so does a document (check-ins and, for the drawing, nothing hidden).
        var mbom = (await TimelineOf(s, Alice, "mbom", s.Mbom)).Events;
        Assert.Contains(mbom, e => e.Title == "Revision A created");
        Assert.Contains(mbom, e => e.Detail is not null && e.Detail.Contains("Derived from EBOM version", StringComparison.Ordinal));
        var doc = (await TimelineOf(s, Alice, "document", s.Drawing)).Events;
        Assert.Contains(doc, e => e.Kind == TimelineKinds.Iteration);
        Assert.Equal(OutcomeKind.NotFound, (await s.Timeline.GetAsync(Eve, "document", s.Spec, null)).Kind);
    }

    // ---------------------------------------------------------------- (c) workflow map equals the workflow views

    [Fact]
    public async Task The_workflow_map_equals_the_workflow_views_at_every_step_and_hides_participants_from_a_reader_who_lost_access()
    {
        var c = await ConfigEnv.CreateAsync(pg);
        var number = await c.NewPartAsync("Mapped bracket");
        var started = await c.Flow.StartAsync(Alice, WorkflowEnv.Start(number, "rita", "bob", "carol"), "corr-start");
        Assert.True(started.IsOk, string.Join("; ", started.Errors));
        var instanceId = started.Value!.Id;

        async Task Check(string status, string[] stageStates)
        {
            var view = (await c.Flow.GetInstanceAsync(Alice, instanceId, null)).Value!;
            var map = (await c.Flow.GetMapAsync(Alice, instanceId, "corr-map")).Value!;
            Assert.False(map.Redacted);
            Assert.Equal(view.Status, map.Status);
            Assert.Equal(status, map.Status);
            Assert.Equal(view.TemplateName, map.TemplateName);
            Assert.Equal(stageStates, map.Stages.Select(x => x.State).ToArray());
            var mapTasks = map.Stages.SelectMany(x => x.Tasks).Where(t => t.TaskId is not null).ToList();
            Assert.Equal(view.Tasks.Count, mapTasks.Count);
            foreach (var t in view.Tasks)
            {
                var mt = mapTasks.Single(x => x.TaskId == t.Id);
                Assert.Equal(t.Name, mt.Name);
                Assert.Equal(t.AssigneeName, mt.Person!.Name);
                Assert.Equal(t.Status switch { "Open" => "Pending", "Completed" => t.Decision == "reject" ? "Rejected" : "Signed", "Skipped" => "Skipped", _ => "Cancelled" }, mt.Status);
                var sig = view.Signatures.SingleOrDefault(x => x.TaskId == t.Id);
                Assert.Equal(sig?.Meaning, mt.Meaning);
                Assert.Equal(t.CompletedAt, mt.At);
            }
            var approval = map.Stages.Single(x => x.Type == "Parallel");
            Assert.Equal("All", approval.Rule);
            Assert.Equal(2, approval.Total);
            Assert.Equal(map.Stages.Count, map.Stages.Select(x => x.NodeId).Distinct().Count());
            Assert.Equal("End", map.Stages[^1].Type);
        }

        await Check("Active", ["Active", "Waiting", "Waiting"]);
        var v = (await c.Flow.GetInstanceAsync(Alice, instanceId, null)).Value!;
        v = (await c.Flow.SignAsync(WorkflowEnv.Fresh(WorkflowEnv.Rita), v.Tasks.Single(t => t.Status == "Open").Id, "Reviewed", "ok", null)).Value!;
        await Check("Active", ["Approved", "Active", "Waiting"]);
        v = (await c.Flow.SignAsync(WorkflowEnv.Fresh(WorkflowEnv.Bob), v.Tasks.Single(t => t.Status == "Open" && t.AssigneeUsername == "bob").Id, "Approved", null, null)).Value!;
        await Check("Active", ["Approved", "Active", "Waiting"]);

        // Someone who holds a task but may no longer read the part sees the steps and statuses with every participant shown as "Participant".
        var ritaWithoutAccess = WorkflowEnv.Rita with { Programs = [] }; // holds the open review task of the second instance, but is no longer on the program
        var secretPart = await c.NewPartAsync("Secret mapped part", "Component", Orca, Alice);
        var secretStart = await c.Flow.StartAsync(Alice, WorkflowEnv.Start(secretPart, "rita", "bob", "carol"), "corr-secret");
        Assert.True(secretStart.IsOk, string.Join("; ", secretStart.Errors));
        var hidden = (await c.Flow.GetMapAsync(ritaWithoutAccess, secretStart.Value!.Id, "corr-hidden")).Value!;
        Assert.True(hidden.Redacted);
        Assert.Equal("UNCLASSIFIED", hidden.Banner);
        Assert.All(hidden.Stages.SelectMany(x => x.Tasks), t => { Assert.Equal("Participant", t.Person!.Name); Assert.Null(t.At); Assert.Null(t.Meaning); });
        var text = System.Text.Json.JsonSerializer.Serialize(hidden);
        Assert.DoesNotContain("Rita", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret mapped part", text, StringComparison.Ordinal);
        Assert.DoesNotContain(secretPart, text, StringComparison.Ordinal);
        // The same instance is NotFound for a reader who holds no task, and the denial is audited.
        Assert.Equal(OutcomeKind.NotFound, (await c.Flow.GetMapAsync(Eve, secretStart.Value.Id, "corr-eve")).Kind);
        Assert.Contains(await c.AuditAsync(), a => a is { Action: "workflow.map", Outcome: AuditOutcome.Denied });
        // A reader of the object sees the full map of the same instance.
        Assert.False((await c.Flow.GetMapAsync(Mallory, secretStart.Value.Id, null)).Value!.Redacted);
    }

    // ---------------------------------------------------------------- (e) export

    [Fact]
    public async Task The_svg_export_carries_the_banner_and_the_distribution_statement_and_no_hidden_identifier_for_any_user()
    {
        var s = await BuildAsync();
        foreach (var user in Users)
        {
            var detail = (await s.C.Change.GetAsync(user, s.Ecn, null)).Value!;
            var report = (await s.Impact.AnalyzeAsync(user, s.Ecn, 10, null)).Value!;
            var graph = ImpactGraphBuilder.Build(detail, report);
            var layout = LayeredLayout.Compute([.. graph.Nodes.Select(n => { var (w, h) = GraphGeometry.Size(n.Kind); return new LayoutNodeInput(n.Id, n.Layer, w, h); })], [.. graph.Edges.Select(e => new LayoutEdgeInput(e.From, e.To))]);
            var svg = GraphSvgWriter.Write(graph, layout, $"Impact map {s.Ecn}", user.Username!, DateTimeOffset.UtcNow);
            System.Xml.Linq.XNamespace ns = "http://www.w3.org/2000/svg";
            Assert.Equal(2, System.Xml.Linq.XDocument.Parse(svg).Descendants(ns + "text").Count(t => t.Value == graph.Banner)); // the top bar and the bottom bar
            Assert.Contains("DISTRIBUTION STATEMENT", svg, StringComparison.Ordinal);
            AssertNoneOf(s.HiddenIdentifiers(user), svg, user.Username!);
            if (!Can(user, Kraken)) Assert.DoesNotContain("KRAKEN", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
            System.Xml.Linq.XDocument.Parse(svg);
            if (Can(user, Kraken)) Assert.Contains(s.KrakenAsm, svg, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- (d) performance

    private static void Log(string message)
    {
        TestContext.Current.SendDiagnosticMessage(message);
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        if (Environment.GetEnvironmentVariable("KEEL_PERF_LOG") is { Length: > 0 } path) File.AppendAllText(path, message + Environment.NewLine);
    }

    [Fact]
    public async Task The_treemap_of_the_fifty_thousand_line_bom_builds_and_lays_out_within_budget()
    {
        var db = await pg.CreateDatabaseAsync();
        try
        {
            var ds = db.AppDataSource();
            var bom = await SyntheticBomGenerator.InsertAsync(ds, new SyntheticBomOptions { Seed = 7, Levels = 10, TargetLines = 50_000 });
            await db.ExecuteAsSuperuserAsync("ANALYZE");
            var svc = new BomService(ds, new PostgresAuditLog(ds, TimeProvider.System), TimeProvider.System);
            foreach (var (label, user) in new[] { ("full access", BomTestKit.Alice), ("non-U.S. person (partly restricted)", BomTestKit.Lena) })
            {
                var query = new BomQuery(bom.RootNumber);
                await svc.ExplodeAsync(user, query, null); // warm-up
                var sw = Stopwatch.StartNew();
                var explosion = (await svc.ExplodeAsync(user, query, null)).Value!;
                var explodeMs = sw.ElapsedMilliseconds;
                sw.Restart();
                var root = BomTreemapModel.Build(explosion.RootNumber, explosion.RootName, explosion.Lines);
                var buildMs = sw.ElapsedMilliseconds;
                sw.Restart();
                var layout = BomTreemapModel.Layout(root, 960, 540, TreemapMetric.ExtendedQuantity, 400);
                var layoutMs = sw.ElapsedMilliseconds;
                sw.Restart();
                var byCount = BomTreemapModel.Layout(root, 960, 540, TreemapMetric.DescendantCount, 400);
                var countMs = sw.ElapsedMilliseconds;
                Log($"Treemap, {label}: explosion {explosion.Lines.Count} lines in {explodeMs} ms (service), tree build {buildMs} ms, layout {layout.TileCount} tiles in {layoutMs} ms (count metric {byCount.TileCount} tiles, {countMs} ms), folded: {layout.Folded}");
                Assert.True(buildMs + layoutMs < 500, $"treemap build {buildMs} ms + layout {layoutMs} ms");
                Assert.True(layout.TileCount <= 400);
                Assert.True(byCount.TileCount <= 400);
                // Drilling into the largest assembly stays within the budget too.
                var biggest = root.Children.Where(c => !c.Redacted && c.Children.Count > 0).OrderByDescending(c => c.Value(TreemapMetric.ExtendedQuantity)).FirstOrDefault();
                if (biggest is not null)
                {
                    sw.Restart();
                    var drilled = BomTreemapModel.Layout(biggest, 960, 540, TreemapMetric.ExtendedQuantity, 400);
                    Assert.True(sw.ElapsedMilliseconds < 500 && drilled.TileCount <= 400);
                }
                if (user == BomTestKit.Lena) Assert.Contains(root.Children, c => c.Redacted);
            }
        }
        finally
        {
            await db.DropAsync();
        }
    }

    [Fact]
    public void An_impact_of_five_hundred_nodes_builds_and_lays_out_within_budget()
    {
        var change = new ChangeDetail(Guid.NewGuid(), "ECN", "ECN-000001", "Big change", "", "Medium", "II", "", "A", "a", "InWork", "In Work", false, false, Open, Open.BannerText(), null, [],
            [new AffectedObjectView(Guid.Empty, false, "part", "P-1", "Bracket", "A", "Released", "revise", "B", "InWork", Open.BannerText(), DateTimeOffset.UnixEpoch, "a", null)], [], [], [],
            new ChangePermissions(true, false, true, []), null, null, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [], []);
        var rnd = new Random(11);
        var where = new List<WhereUsedEntry>();
        var level1 = Enumerable.Range(0, 40).Select(i => $"P-1{i:D3}").ToList();
        foreach (var k in level1) where.Add(new WhereUsedEntry(1, false, 10, 1, "EA", Guid.NewGuid(), k, $"Assembly {k}", "A", "Released", Open.BannerText(), Open, "P-1"));
        var previous = level1;
        for (var level = 2; level <= 6; level++)
        {
            var current = Enumerable.Range(0, 90).Select(i => $"P-{level}{i:D3}").ToList();
            foreach (var k in current) where.Add(new WhereUsedEntry(level, false, 10, 1, "EA", Guid.NewGuid(), k, $"Assembly {k}", "A", "Released", Open.BannerText(), Open, previous[rnd.Next(previous.Count)]));
            previous = current;
        }
        var report = new ImpactReport("ECN-000001", [new ImpactObject(Guid.Empty, false, "revise", "P-1", "Bracket", "A", Open.BannerText(), where, 0, 6, [], null, null)], [], new ImpactSummary(1, 0, where.Count, 0, 0, 0, 0, 0), Open.BannerText(), []);
        ImpactGraphBuilder.Build(change, report, new ImpactGraphOptions(ShowLevels: 10)); // warm-up
        var sw = Stopwatch.StartNew();
        var graph = ImpactGraphBuilder.Build(change, report, new ImpactGraphOptions(ShowLevels: 10));
        var buildMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var layout = LayeredLayout.Compute([.. graph.Nodes.Select(n => { var (w, h) = GraphGeometry.Size(n.Kind); return new LayoutNodeInput(n.Id, n.Layer, w, h); })], [.. graph.Edges.Select(e => new LayoutEdgeInput(e.From, e.To))]);
        var layoutMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var svg = GraphSvgWriter.Write(graph, layout, "Impact map", "alice", DateTimeOffset.UtcNow);
        var svgMs = sw.ElapsedMilliseconds;
        Log($"Impact map of {graph.Nodes.Count} nodes and {graph.Edges.Count} edges: build {buildMs} ms, layout {layoutMs} ms ({layout.Crossings} crossings left), SVG {svg.Length / 1024} KiB in {svgMs} ms");
        Assert.InRange(graph.Nodes.Count, 480, 500);
        Assert.True(layoutMs < 300, $"layout took {layoutMs} ms");
        Assert.True(buildMs < 300, $"graph build took {buildMs} ms");
    }
}
