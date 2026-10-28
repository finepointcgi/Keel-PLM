using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.Security;

namespace Keel.IntegrationTests;

/// <summary>MBOM explosion, summary, CSV, compare, where-used and the EBOM|MBOM side-by-side view: access filter, redaction, scrap and effectivity (ADR-0024).</summary>
[Collection(PostgresCollection.Name)]
public sealed class MbomQueryTests(PostgresFixture pg)
{
    private static async Task<MbomExplosion> Explode(MbomEnv env, string number, Subject? by = null, MbomQuery? query = null)
    {
        var r = await env.Mbom.ExplodeAsync(by ?? MbomEnv.Alice, query ?? new MbomQuery(number), "c");
        Assert.True(r.IsOk, $"{r.Kind} {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    private static async Task<(MbomEnv Env, MbomEnv.Product P, string Top, string Sub)> ScrapProductAsync(PostgresFixture pg)
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (p, top) = await env.ReconciledProductAsync();
        await env.CheckOutAsync(top);
        await env.UpdateLineAsync(top, 10, c => c with { ScrapFactor = 0.1m });
        await env.UpdateLineAsync(top, 30, c => c with { ScrapFactor = 0.5m });
        await env.CheckInAsync(top);
        var sub = (await env.Mbom.SearchAsync(MbomEnv.Alice, new MbomSearch(PartNumber: p.Sub), "c")).Value!.Items.Single().Number;
        return (env, p, top, sub);
    }

    [Fact]
    public async Task The_explosion_resolves_make_subassemblies_to_their_released_mbom_and_rolls_up_net_and_gross_quantities()
    {
        var (env, p, top, sub) = await ScrapProductAsync(pg);
        var e = await Explode(env, top);
        Assert.Equal(["10", "20", "30", "30.10"], e.Lines.Select(l => l.Path).ToArray());
        Assert.Equal([1, 1, 1, 2], e.Lines.Select(l => l.Level).ToArray());
        Assert.Equal([4m, 1m, 2m, 2m], e.Lines.Select(l => l.ExtendedQuantity).ToArray());
        Assert.Equal([4.4m, 1m, 3m, 3m], e.Lines.Select(l => l.GrossQuantity).ToArray()); // scrap compounds: 2 * 1.5 sub-assemblies are made, each with one bracket
        var subLine = e.Lines[2];
        Assert.Equal((p.Sub, true, "A", "Released"), (subLine.Number, subLine.HasChildren, subLine.ChildMbomRevision, subLine.ChildMbomState));
        Assert.False(e.Lines[0].HasChildren);
        Assert.Equal(("Make", 0.5m), (subLine.SupplyType, subLine.ScrapFactor));
        Assert.Equal(p.Bracket, e.Lines[3].Number);
        Assert.Equal(0, e.RedactedCount);
        var grid = subLine.ToGridLine();
        Assert.Equal(("A", 1), (grid.Revision, grid.Iteration)); // the shared tree grid shows the child's MBOM revision
        Assert.Equal(top, e.Number);
        Assert.NotEmpty(sub);
    }

    [Fact]
    public async Task Lazy_expansion_addresses_a_line_by_its_find_number_path()
    {
        var (env, p, top, _) = await ScrapProductAsync(pg);
        var level = await Explode(env, top, query: new MbomQuery(top, Depth: 1));
        Assert.Equal(["10", "20", "30"], level.Lines.Select(l => l.Path).ToArray());
        Assert.True(level.Lines[2].HasChildren);
        var below = await Explode(env, top, query: new MbomQuery(top, Depth: 1, ParentLine: "30"));
        var line = Assert.Single(below.Lines);
        Assert.Equal(("30.10", 2, 2m, 3m, p.Bracket), (line.Path, line.Level, line.ExtendedQuantity, line.GrossQuantity, line.Number)); // roll-up continues from the parent line
        Assert.Equal(OutcomeKind.NotFound, (await env.Mbom.ExplodeAsync(MbomEnv.Alice, new MbomQuery(top, ParentLine: "99"), "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.ExplodeAsync(MbomEnv.Alice, new MbomQuery(top, ParentLine: "x.y"), "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.ExplodeAsync(MbomEnv.Alice, new MbomQuery(top, Depth: 11), "c")).Kind);
    }

    [Fact]
    public async Task Children_resolve_per_plant_and_by_latest_or_latest_released()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (p, top) = await env.ReconciledProductAsync();
        // The same product in the second plant has no MBOM for the sub-assembly: the line is a leaf there.
        var other = await env.GenerateAsync(p.Top, "PLANT-B");
        var inB = await Explode(env, other.Summary.Number);
        Assert.Equal(["10", "20", "30"], inB.Lines.Select(l => l.Path).ToArray());
        Assert.False(inB.Lines[2].HasChildren);
        // A new revision of the sub-assembly's MBOM in work is only seen with latest children.
        var sub = (await env.Mbom.SearchAsync(MbomEnv.Alice, new MbomSearch(PartNumber: p.Sub, Plant: "PLANT-A"), "c")).Value!.Items.Single().Number;
        Assert.True((await env.Mbom.ReviseAsync(MbomEnv.Alice, sub, "Add a step", "c")).IsOk);
        var released = await Explode(env, top);
        Assert.Equal(("A", "Released"), (released.Lines[2].ChildMbomRevision!, released.Lines[2].ChildMbomState!));
        var latest = await Explode(env, top, query: new MbomQuery(top, LatestChildren: true));
        Assert.Equal(("B", "InWork"), (latest.Lines[2].ChildMbomRevision!, latest.Lines[2].ChildMbomState!));
    }

    [Fact]
    public async Task Effectivity_as_of_a_serial_picks_the_lines_that_apply_to_the_unit()
    {
        var env = await MbomEnv.CreateAsync(pg);
        await env.C.NewContextAsync("ORCA-POD");
        var screw = await env.BuyPartAsync("Screw");
        var plate = await env.BuyPartAsync("Plate");
        var top = await env.C.NewPartAsync("Pod assembly", "Assembly");
        var number = (await env.Mbom.CreateAsync(MbomEnv.Alice, new CreateMbomCommand(top, MbomEnv.Plant), "c")).Value!.Summary.Number;
        await env.CheckOutAsync(number);
        await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(screw, 4, FindNumber: 10, Effectivity: ConfigEnv.Serial("ORCA-POD", 1, 50)), "c");
        await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(plate, 1, FindNumber: 10, Effectivity: ConfigEnv.Serial("ORCA-POD", 51, null)), "c");
        await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(screw, 2, FindNumber: 20), "c");
        await env.CheckInAsync(number);
        var all = await Explode(env, number);
        Assert.Equal(["10", "10~1", "20"], all.Lines.Select(l => l.Path).ToArray());
        var s10 = await Explode(env, number, query: new MbomQuery(number, Unit: EffectivityRequest.ForSerial("ORCA-POD", 10)));
        Assert.Equal([(screw, "10"), (screw, "20")], s10.Lines.Select(l => (l.Number!, l.Path)).ToArray());
        Assert.Equal("serial 10 (ORCA-POD)", s10.Configuration);
        var s60 = await Explode(env, number, query: new MbomQuery(number, Unit: EffectivityRequest.ForSerial("ORCA-POD", 60)));
        Assert.Equal([(plate, "10~1"), (screw, "20")], s60.Lines.Select(l => (l.Number!, l.Path)).ToArray());
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.ExplodeAsync(MbomEnv.Alice, new MbomQuery(number, Unit: new EffectivityRequest(null, Serial: 5)), "c")).Kind);
    }

    [Fact]
    public async Task The_purchasing_summary_sums_net_and_gross_quantities_per_part_unit_and_supply_type_and_never_merges_restricted_lines()
    {
        var (env, p, top, _) = await ScrapProductAsync(pg);
        var s = (await env.Mbom.SummaryAsync(MbomEnv.Alice, new MbomQuery(top), true, "c")).Value!;
        var byPart = s.Lines.ToDictionary(l => l.Number!);
        Assert.Equal(["Buy", "Make", "Make"], new[] { byPart[p.Screw].SupplyType!, byPart[p.Plate].SupplyType!, byPart[p.Bracket].SupplyType! });
        Assert.Equal((4m, 4.4m), (byPart[p.Screw].NetQuantity, byPart[p.Screw].GrossQuantity));
        Assert.Equal((2m, 3m), (byPart[p.Bracket].NetQuantity, byPart[p.Bracket].GrossQuantity));
        Assert.DoesNotContain(s.Lines, l => l.Number == p.Sub); // leaves only: the sub-assembly is expanded
        var all = (await env.Mbom.SummaryAsync(MbomEnv.Alice, new MbomQuery(top), false, "c")).Value!;
        Assert.Contains(all.Lines, l => l.Number == p.Sub);

        // Two restricted lines of the same part stay two placeholders.
        var secret = await env.C.NewPartAsync("Stealth rotor", "Component", ConfigEnv.Itar());
        var mixed = await env.C.NewPartAsync("Mixed assembly", "Assembly");
        var number = (await env.Mbom.CreateAsync(MbomEnv.Alice, new CreateMbomCommand(mixed, MbomEnv.Plant), "c")).Value!.Summary.Number;
        await env.CheckOutAsync(number);
        await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(secret, 1, FindNumber: 10), "c");
        await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(secret, 2, FindNumber: 20), "c");
        await env.CheckInAsync(number);
        var eve = (await env.Mbom.SummaryAsync(MbomEnv.Eve, new MbomQuery(number), true, "c")).Value!;
        Assert.Equal(2, eve.Lines.Count(l => l.Redacted));
        Assert.All(eve.Lines, l => Assert.Null(l.Number));
        Assert.Single((await env.Mbom.SummaryAsync(MbomEnv.Alice, new MbomQuery(number), true, "c")).Value!.Lines);
    }

    [Fact]
    public async Task The_csv_export_carries_the_banner_first_and_last_neutralizes_formulas_and_is_audited_as_a_download()
    {
        var (env, p, top, _) = await ScrapProductAsync(pg);
        var export = (await env.Mbom.ExportCsvAsync(MbomEnv.Alice, new MbomQuery(top), "c")).Value!;
        var lines = export.Csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(lines[0], lines[^1]);
        Assert.StartsWith("UNCLASSIFIED", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("Level,Path,Find number,Part number", lines[1], StringComparison.Ordinal);
        Assert.Equal(7, lines.Length); // banner, header, four lines, banner
        Assert.Contains(lines, l => l.Contains(p.Bracket, StringComparison.Ordinal) && l.Contains(",3,", StringComparison.Ordinal));
        Assert.Equal($"{top}_PLANT-A_A.2_mbom.csv", export.FileName);
        Assert.Equal("'=cmd", MbomCsvCell("=cmd"));
        Assert.Contains(await env.AuditAsync(), e => e.Action == "mbom.export" && e.Outcome == AuditOutcome.Success);
        // A reader without the Download role is turned away.
        var noDownload = BomTestKit.User("nodl", roles: []);
        Assert.Equal(OutcomeKind.NotFound, (await env.Mbom.ExportCsvAsync(noDownload, new MbomQuery(top), "c")).Kind);
    }

    private static string MbomCsvCell(string v) => Keel.Bom.BomCsv.Cell(v);

    [Fact]
    public async Task A_restricted_subassembly_is_a_placeholder_and_its_subtree_is_never_read()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var plain = await env.BuyPartAsync("Washer");
        var bracket = await env.C.NewPartAsync("Rotor bracket", "Component", ConfigEnv.Itar());
        var secretSub = await env.C.NewPartAsync("Rotor assembly", "Assembly", ConfigEnv.Itar());
        await env.C.CheckOutAsync(secretSub);
        await env.C.AddLineAsync(secretSub, bracket, 1);
        await env.C.CheckInAsync(secretSub);
        await env.C.ReleaseAsync(secretSub);
        var top = await env.C.NewPartAsync("Open assembly", "Assembly");
        await env.C.CheckOutAsync(top);
        await env.C.AddLineAsync(top, plain, 2, 10);
        await env.C.AddLineAsync(top, secretSub, 1, 20);
        await env.C.CheckInAsync(top);
        await env.C.ReleaseAsync(top);
        var made = await env.Mbom.GenerateAsync(MbomEnv.Alice, new GenerateMbomCommand(top, MbomEnv.Plant, null, MultiLevel: true), "c");
        Assert.Equal(2, made.Value!.Items.Count);
        var number = made.Value.Items[0].MbomNumber;
        // The sub-assembly's MBOM is ITAR (it covers its part); release it so it resolves as a released child.
        var subNumber = made.Value.Items[1].MbomNumber;
        await env.ReconcileAsync(subNumber);
        await env.ReleaseAsync(subNumber);

        var alice = await Explode(env, number);
        Assert.Equal(["10", "20", "20.10"], alice.Lines.Select(l => l.Path).ToArray());
        var eve = await Explode(env, number, MbomEnv.Eve);
        Assert.Equal(["10", "20"], eve.Lines.Select(l => l.Path).ToArray());
        Assert.Equal(1, eve.RedactedCount);
        var placeholder = eve.Lines[1];
        Assert.True(placeholder.Redacted);
        Assert.All([placeholder.Number, placeholder.Name, placeholder.SupplyType, placeholder.Banner, placeholder.ChildMbomRevision], Assert.Null);
        Assert.Equal((1m, 1m, false), (placeholder.Quantity, placeholder.ExtendedQuantity, placeholder.HasChildren));
        Assert.DoesNotContain("ITAR", eve.Banner, StringComparison.Ordinal);
        Assert.Contains("ITAR", alice.Banner, StringComparison.Ordinal);
        // Expanding the restricted line directly is refused like a missing line.
        Assert.Equal(OutcomeKind.NotFound, (await env.Mbom.ExplodeAsync(MbomEnv.Eve, new MbomQuery(number, ParentLine: "20"), "c")).Kind);
        // The subtree of a restricted MBOM is invisible even when the part of its child is open.
        Assert.Equal(OutcomeKind.NotFound, (await env.Mbom.GetLinesAsync(MbomEnv.Eve, subNumber, null, null, "c")).Kind);
    }

    [Fact]
    public async Task The_sql_explosion_agrees_with_the_access_policy_for_every_subject()
    {
        var env = await MbomEnv.CreateAsync(pg);
        // TOP (open) = open component, S1 (EAR) = [open, ITAR/ORCA component], S2 (ITAR/ORCA) = [open component]; every assembly has a released MBOM.
        var open1 = await env.BuyPartAsync("Open one");
        var open2 = await env.BuyPartAsync("Open two");
        var itarPart = await env.C.NewPartAsync("ITAR component", "Component", ConfigEnv.Itar());
        var earPart = await env.C.NewPartAsync("EAR component", "Component", BomTestKit.Ear());
        var s1 = await env.C.NewPartAsync("EAR assembly", "Assembly", BomTestKit.Ear());
        await env.C.CheckOutAsync(s1);
        await env.C.AddLineAsync(s1, open1, 2);
        await env.C.AddLineAsync(s1, itarPart, 1);
        await env.C.AddLineAsync(s1, earPart, 1);
        await env.C.CheckInAsync(s1);
        await env.C.ReleaseAsync(s1);
        var s2 = await env.C.NewPartAsync("ITAR assembly", "Assembly", ConfigEnv.Itar());
        await env.C.CheckOutAsync(s2);
        await env.C.AddLineAsync(s2, open2, 3);
        await env.C.CheckInAsync(s2);
        await env.C.ReleaseAsync(s2);
        var top = await env.C.NewPartAsync("Top", "Assembly");
        await env.C.CheckOutAsync(top);
        await env.C.AddLineAsync(top, open1, 1, 10);
        await env.C.AddLineAsync(top, s1, 2, 20);
        await env.C.AddLineAsync(top, s2, 1, 30);
        await env.C.CheckInAsync(top);
        await env.C.ReleaseAsync(top);
        var made = await env.Mbom.GenerateAsync(MbomEnv.Alice, new GenerateMbomCommand(top, MbomEnv.Plant, null, MultiLevel: true), "c");
        Assert.True(made.IsOk, string.Join("; ", made.Errors));
        foreach (var item in made.Value!.Items.Skip(1))
        {
            await env.ReconcileAsync(item.MbomNumber);
            await env.ReleaseAsync(item.MbomNumber);
        }
        var number = made.Value.Items[0].MbomNumber;

        var full = await Explode(env, number);
        Assert.Equal(7, full.Lines.Count);
        var subjects = new[] { BomTestKit.Alice, BomTestKit.Bob, BomTestKit.Lena, BomTestKit.Carmen, BomTestKit.Viewer, BomTestKit.User("auth", usPerson: false, auths: ["TAA-1"]) };
        foreach (var subject in subjects)
        {
            // Oracle: walk the full explosion; a line is shown when its parent chain is readable, and is a placeholder when it is not readable itself.
            var shown = new List<(string Path, bool Redacted)>();
            var readableAt = new Dictionary<string, bool> { [""] = true };
            foreach (var l in full.Lines)
            {
                var cut = l.Path.LastIndexOf('.');
                var parent = cut < 0 ? "" : l.Path[..cut];
                if (!readableAt.GetValueOrDefault(parent)) continue;
                var readable = AccessPolicy.Evaluate(subject, l.Markings!, PlmAction.Read).Permitted;
                readableAt[l.Path] = readable;
                shown.Add((l.Path, !readable));
            }
            var r = await env.Mbom.ExplodeAsync(subject, new MbomQuery(number), "c");
            if (!AccessPolicy.Evaluate(subject, (await env.GetAsync(number)).Summary.Markings, PlmAction.Read).Permitted)
            {
                Assert.Equal(OutcomeKind.NotFound, r.Kind);
                continue;
            }
            Assert.True(r.IsOk, subject.Username);
            Assert.Equal(shown, r.Value!.Lines.Select(l => (l.Path, l.Redacted)).ToList());
            Assert.Equal(shown.Count(s => s.Redacted), r.Value.RedactedCount);
        }
    }

    [Fact]
    public async Task Two_iterations_are_compared_line_by_line()
    {
        var (env, p, top, _) = await ScrapProductAsync(pg);
        // Iterations: .1 generated, .2 reconciled-product check-in (none), .3 scrap edits. Compare .1 with .2 (nothing) and .2 with .3 (scrap on two lines).
        var d = await env.GetAsync(top);
        Assert.Equal(("A", 2), (d.Summary.Revision, d.Summary.Iteration));
        var same = (await env.Mbom.CompareAsync(MbomEnv.Alice, top, "A.1", "A.1", "c")).Value!;
        Assert.Empty(same.Lines);
        Assert.Equal(3, same.Unchanged);
        var diff = (await env.Mbom.CompareAsync(MbomEnv.Alice, top, "A.1", "A.2", "c")).Value!;
        Assert.Equal(2, diff.Lines.Count);
        Assert.All(diff.Lines, l => Assert.Equal(BomChangeKind.Changed, l.Kind));
        Assert.Equal("Scrap factor none to 0.1", diff.Lines.Single(l => l.Number == p.Screw).Changes.Single());
        Assert.Equal("Scrap factor none to 0.5", diff.Lines.Single(l => l.Number == p.Sub).Changes.Single());
        Assert.Equal(1, diff.Unchanged);

        await env.CheckOutAsync(top);
        var screwLine = await env.WorkingLineAsync(top, 10);
        await env.Mbom.RemoveLineAsync(MbomEnv.Alice, top, screwLine.Id, "c");
        await env.Mbom.AddLineAsync(MbomEnv.Alice, top, new AddMbomLineCommand(p.Bracket, 2, FindNumber: 40, Notes: "kit"), "c");
        await env.UpdateLineAsync(top, 20, c => c with { Quantity = 2, Phantom = true });
        await env.CheckInAsync(top);
        var again = (await env.Mbom.CompareAsync(MbomEnv.Alice, top, "A.2", "A.3", "c")).Value!;
        Assert.Equal([BomChangeKind.Removed, BomChangeKind.Changed, BomChangeKind.Added], again.Lines.Select(l => l.Kind).ToArray());
        Assert.Equal(["Quantity 1 to 2", "Made phantom"], again.Lines.Single(l => l.Kind == BomChangeKind.Changed).Changes.ToArray());
        Assert.Equal(OutcomeKind.NotFound, (await env.Mbom.CompareAsync(MbomEnv.Alice, top, "A.1", "A.9", "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.CompareAsync(MbomEnv.Alice, top, "1", "2", "c")).Kind);
    }

    [Fact]
    public async Task Where_used_by_plant_lists_the_mboms_using_a_part_and_hides_restricted_ones()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var p = await env.ProductAsync();
        var a = await env.GenerateAsync(p.Top);
        var b = await env.GenerateAsync(p.Top, "PLANT-B");
        var secretTop = await env.C.NewPartAsync("Restricted assembly", "Assembly", ConfigEnv.Itar());
        await env.C.CheckOutAsync(secretTop);
        await env.C.AddLineAsync(secretTop, p.Screw, 7, 10);
        await env.C.CheckInAsync(secretTop);
        await env.C.ReleaseAsync(secretTop);
        var hidden = await env.GenerateAsync(secretTop);

        var all = (await env.Mbom.WhereUsedAsync(MbomEnv.Alice, p.Screw, null, "c")).Value!;
        Assert.Equal([(a.Summary.Number, "PLANT-A"), (hidden.Summary.Number, "PLANT-A"), (b.Summary.Number, "PLANT-B")], all.Rows.Select(r => (r.MbomNumber!, r.Plant!)).OrderBy(x => x.Item2).ThenBy(x => x.Item1).ToArray());
        var onlyB = (await env.Mbom.WhereUsedAsync(MbomEnv.Alice, p.Screw, "plant-b", "c")).Value!;
        Assert.Equal([b.Summary.Number], onlyB.Rows.Select(r => r.MbomNumber!).ToArray());
        Assert.Equal((10, 4m, "EA", "Buy"), (onlyB.Rows[0].FindNumber, onlyB.Rows[0].Quantity, onlyB.Rows[0].Unit, onlyB.Rows[0].SupplyType));

        var eve = (await env.Mbom.WhereUsedAsync(MbomEnv.Eve, p.Screw, null, "c")).Value!;
        Assert.Equal(3, eve.Rows.Count);
        var placeholder = Assert.Single(eve.Rows, r => r.Redacted);
        Assert.Equal((10, 7m, "EA"), (placeholder.FindNumber, placeholder.Quantity, placeholder.Unit));
        Assert.All([placeholder.MbomNumber, placeholder.PartNumber, placeholder.PartName, placeholder.Banner], Assert.Null);
        Assert.Equal(OutcomeKind.NotFound, (await env.Mbom.WhereUsedAsync(MbomEnv.Eve, secretTop, null, "c")).Kind);
    }

    [Fact]
    public async Task Side_by_side_aligns_ebom_lines_with_the_mbom_lines_that_implement_them()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (p, number) = await env.ReconciledProductAsync();
        var glue = (await env.C.Parts.CreateAsync(MbomEnv.Alice, new Keel.Parts.CreatePartCommand("Adhesive", "Consumable", "mL", "Buy", ConfigEnv.Open), null)).Value!.Number;
        await env.CheckOutAsync(number);
        var screw = await env.WorkingLineAsync(number, 10);
        await env.UpdateLineAsync(number, 10, c => c with { Quantity = 2, ScrapFactor = 0.05m });
        await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(p.Screw, 2, FindNumber: 15), "c");
        await env.Mbom.SetLinksAsync(MbomEnv.Alice, number, (await env.WorkingLineAsync(number, 15)).Id, [new LinkInput(screw.Links[0].EbomUsageId)], "c");
        await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(glue, 5, "mL", 900, MbomOnly: true, SupplyType: "Consume"), "c");
        await env.Mbom.AddLineAsync(MbomEnv.Alice, number, new AddMbomLineCommand(p.Bracket, 1, FindNumber: 910), "c");
        await env.Mbom.RemoveLineAsync(MbomEnv.Alice, number, (await env.WorkingLineAsync(number, 20)).Id, "c");
        await env.CheckInAsync(number);

        var view = (await env.Mbom.SideBySideAsync(MbomEnv.Alice, number, null, "c")).Value!;
        Assert.Equal((number, "Latest released (" + p.Top + " A.2)"), (view.Number, view.EbomSource));
        Assert.Equal(["linked", "linked", "ebom_only", "mbom_only", "unexplained"], view.Rows.Select(r => r.State).ToArray());
        var split = view.Rows[0];
        Assert.Equal([10], split.Ebom.Select(e => e.FindNumber).ToArray());
        Assert.Equal([10, 15], split.Mbom.Select(m => m.FindNumber).ToArray());
        Assert.Equal(LinkStatuses.Matched, split.LinkStatus);
        Assert.Contains("scrap 0.05", split.Mbom[0].Detail, StringComparison.Ordinal);
        Assert.Equal(20, view.Rows[2].Ebom.Single().FindNumber);
        Assert.Equal(glue, view.Rows[3].Mbom.Single().Number);
        Assert.Contains("MBOM only", view.Rows[3].Mbom.Single().Detail, StringComparison.Ordinal);
        Assert.Equal(910, view.Rows[4].Mbom.Single().FindNumber);
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.SideBySideAsync(MbomEnv.Alice, number, new EbomSpec(EbomSpecKind.Iteration, IterationLabel: "Z.9"), "c")).Kind);
    }
}
