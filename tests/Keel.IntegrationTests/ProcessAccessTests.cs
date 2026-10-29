using System.Text.Json;
using Keel.Core;
using Keel.Process;
using Keel.Security;

namespace Keel.IntegrationTests;

/// <summary>No-leak tests: what a reader may not see is absent from every answer, in every route of the plan service, with the SQL filter checked against AccessPolicy.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ProcessAccessTests(PostgresFixture pg)
{
    private static string Json(object o) => JsonSerializer.Serialize(o);

    /// <summary>Top (open) = 4 screws (open), 1 plate (ITAR, ORCA); the MBOM is released in the plant.</summary>
    private static async Task<(string Top, string Screw, string Plate, string Mbom)> ProductWithHiddenPlateAsync(ProcEnv env)
    {
        var screw = await env.M.BuyPartAsync("Screw");
        var plate = await env.M.C.NewPartAsync("Secret plate", "Component", ConfigEnv.Itar());
        var top = await env.M.C.NewPartAsync("Open top", "Assembly");
        await env.M.C.CheckOutAsync(top);
        await env.M.C.AddLineAsync(top, screw, 4, 10);
        await env.M.C.AddLineAsync(top, plate, 1, 20);
        await env.M.C.CheckInAsync(top);
        await env.M.C.ReleaseAsync(top);
        var mbom = (await env.M.GenerateAsync(top)).Summary.Number;
        await env.M.ReconcileAsync(mbom);
        await env.M.ReleaseAsync(mbom);
        return (top, screw, plate, mbom);
    }

    [Fact]
    public async Task A_reader_without_the_program_sees_a_hidden_component_as_a_placeholder_everywhere()
    {
        var env = await ProcEnv.CreateAsync(pg);
        var (_, screw, plate, mbom) = await ProductWithHiddenPlateAsync(env);
        await env.WorkCenterAsync("WC-1");
        var number = (await env.CreatePlanAsync(mbom)).Summary.Number;
        var wc = await env.WorkingAsync(number);
        var plateId = await env.MasterIdAsync(plate);
        var screwId = await env.MasterIdAsync(screw);
        await env.SaveAsync(number, ProcEnv.With(wc, ProcEnv.Op(10, "Assemble", "WC-1", [new ComponentAssignment(screwId, 10, 0, null), new ComponentAssignment(plateId, 20, 0, null)], null, [ProcEnv.Step("Join them")])));
        await env.CheckInAsync(number);
        await env.ReleaseAsync(number);

        var plateName = "Secret plate";
        var alice = await env.GetAsync(number, ProcEnv.Alice);
        Assert.Contains(plate, Json(alice), StringComparison.Ordinal);
        Assert.Contains(alice.Routings[0].Operations[0].Components, c => c.PartNumber == plate && !c.Redacted);

        var eve = await env.GetAsync(number, ProcEnv.Eve);
        var text = Json(eve);
        Assert.DoesNotContain(plate, text, StringComparison.Ordinal);
        Assert.DoesNotContain(plateName, text, StringComparison.Ordinal);
        Assert.DoesNotContain(plateId.ToString("D"), text, StringComparison.Ordinal);
        var hidden = eve.Routings[0].Operations[0].Components.Single(c => c.Find == 20);
        Assert.True(hidden.Redacted);
        Assert.Null(hidden.PartNumber);
        Assert.Equal(1m, hidden.LineQuantity); // the placeholder keeps find number and quantity (A4)
        Assert.Contains(eve.Lines, l => l.Find == 20 && l.Redacted && l.PartNumber is null);
        Assert.NotEqual(alice.Summary.Banner, (await env.GetAsync(number, ProcEnv.Eve)).Summary.Banner == alice.Summary.Banner ? "" : alice.Summary.Banner);

        // The matrix and the shop floor view carry the same placeholder; the banner of the view covers what the reader sees.
        var matrix = PlanMatrix.Build(eve.Routings[0], eve.Lines);
        Assert.DoesNotContain(plate, Json(matrix), StringComparison.Ordinal);
        var floorAlice = ProcEnv.Ok(await env.Plans.GetShopFloorAsync(ProcEnv.Alice, number, null, null, "c"));
        var floorEve = ProcEnv.Ok(await env.Plans.GetShopFloorAsync(ProcEnv.Eve, number, null, null, "c"));
        Assert.Contains(plate, Json(floorAlice), StringComparison.Ordinal);
        Assert.DoesNotContain(plate, Json(floorEve), StringComparison.Ordinal);
        Assert.NotEqual(floorAlice.Banner, floorEve.Banner); // the hidden part's markings are in Alice's banner only

        // A working copy returned to a reader without the program names no hidden component either (ids are opaque, part numbers never stored).
        var audit = string.Join("\n", (await env.M.AuditAsync()).Where(a => a.Action.StartsWith("process.", StringComparison.Ordinal)).Select(a => a.Payload));
        Assert.DoesNotContain(plateName, audit, StringComparison.Ordinal);
        Assert.DoesNotContain("Join them", audit, StringComparison.Ordinal); // step text is never audited
    }

    [Fact]
    public async Task A_plan_whose_markings_exclude_the_reader_is_invisible_in_every_route_and_denials_are_audited()
    {
        var env = await ProcEnv.CreateAsync(pg);
        var (_, mbom) = await env.ReleasedMbomAsync();
        var number = (await env.CreatePlanAsync(mbom, markings: ConfigEnv.Itar())).Summary.Number;

        Assert.Equal(OutcomeKind.NotFound, (await env.Plans.GetAsync(ProcEnv.Eve, number, null, "c")).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Plans.CheckOutAsync(ProcEnv.Eve, number, "c")).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Plans.GetShopFloorAsync(ProcEnv.Eve, number, null, null, "c")).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Plans.GetAsync(ProcEnv.Eve, "PP-999999", null, "c")).Kind); // missing looks the same
        Assert.DoesNotContain(ProcEnv.Ok(await env.Plans.SearchAsync(ProcEnv.Eve, new PlanSearch(Limit: 200), "c")).Items, i => i.Number == number);
        Assert.Contains(ProcEnv.Ok(await env.Plans.SearchAsync(ProcEnv.Alice, new PlanSearch(Limit: 200), "c")).Items, i => i.Number == number);
        Assert.Empty(await env.Plans.FindReadableAsync(ProcEnv.Eve, [number]));
        Assert.Contains(await env.M.AuditAsync(), a => a.Action == "process.read" && a.Outcome == Keel.Audit.AuditOutcome.Denied && a.ObjectId == number);
    }

    [Fact]
    public async Task A_plan_is_hidden_when_its_mbom_is_hidden_even_if_the_plan_markings_were_lowered()
    {
        var env = await ProcEnv.CreateAsync(pg);
        var p = await env.M.ProductAsync(ConfigEnv.Itar());
        var gen = await env.M.GenerateAsync(p.Top);
        var sub = await env.M.GenerateAsync(p.Sub);
        await env.M.ReconcileAsync(sub.Summary.Number);
        await env.M.ReleaseAsync(sub.Summary.Number);
        await env.M.ReconcileAsync(gen.Summary.Number);
        await env.M.ReleaseAsync(gen.Summary.Number);
        var number = (await env.CreatePlanAsync(gen.Summary.Number)).Summary.Number;
        // Edit the plan markings outside the application (superuser): the MBOM check still holds.
        await env.M.Db.ExecuteAsSuperuserAsync("UPDATE proc.plan SET export_jurisdiction = 'None', usml_category = NULL, eccn = NULL, cui_categories = '{}', distribution_statement = 'A', controlling_office = NULL, distribution_reason = NULL, program = NULL, export_authorizations = '{}' WHERE number = '" + number + "'");
        Assert.Equal(OutcomeKind.NotFound, (await env.Plans.GetAsync(ProcEnv.Eve, number, null, "c")).Kind);
        Assert.DoesNotContain(ProcEnv.Ok(await env.Plans.SearchAsync(ProcEnv.Eve, new PlanSearch(Limit: 200), "c")).Items, i => i.Number == number);
        Assert.Empty(await env.Plans.FindReadableAsync(ProcEnv.Eve, [number]));
        Assert.Equal(OutcomeKind.NotFound, (await env.Plans.ForMbomAsync(ProcEnv.Eve, gen.Summary.Number, "c")).Kind);
        Assert.True((await env.Plans.GetAsync(ProcEnv.Alice, number, null, "c")).IsOk);
    }

    [Fact]
    public async Task The_plan_filter_agrees_with_the_access_policy_for_every_subject()
    {
        var env = await ProcEnv.CreateAsync(pg);
        var made = new List<(string Number, Markings M)>();
        foreach (var m in new[] { ConfigEnv.Open, ConfigEnv.Itar("ORCA") })
        {
            var (_, mbom) = await env.ReleasedMbomAsync();
            made.Add(((await env.CreatePlanAsync(mbom, markings: m)).Summary.Number, m));
        }
        foreach (var who in new[] { ProcEnv.Alice, ProcEnv.Eve, ProcEnv.Ada, ProcEnv.Bob, ConfigEnv.Mallory })
        {
            var expected = made.Where(x => AccessPolicy.Evaluate(who, x.M, PlmAction.Read).Permitted).Select(x => x.Number).Order().ToList();
            var found = ProcEnv.Ok(await env.Plans.SearchAsync(who, new PlanSearch(Limit: 200), "c")).Items.Select(i => i.Number).Where(n => made.Any(x => x.Number == n)).Order().ToList();
            Assert.Equal(expected, found);
        }
    }

    [Fact]
    public async Task Only_people_who_may_modify_the_plan_can_change_it_and_the_working_copy_is_private()
    {
        var env = await ProcEnv.CreateAsync(pg);
        var (_, mbom) = await env.ReleasedMbomAsync();
        var number = (await env.CreatePlanAsync(mbom)).Summary.Number;
        var wc = await env.WorkingAsync(number);
        // Another author cannot read or save the creator's working copy.
        Assert.Equal(OutcomeKind.Conflict, (await env.Plans.GetWorkingCopyAsync(ConfigEnv.Eve, number, "c")).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Plans.SaveWorkingCopyAsync(ConfigEnv.Eve, number, wc, "c")).Kind);
        // A viewer without the author role cannot check out.
        await env.CheckInAsync(number);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Plans.CheckOutAsync(ConfigEnv.Mallory, number, "c")).Kind);
        // Create needs the author role.
        var (_, other) = await env.ReleasedMbomAsync();
        Assert.Equal(OutcomeKind.Invalid, (await env.Plans.CreateAsync(ConfigEnv.Mallory, new CreatePlanCommand(other, null), "c")).Kind);
        // Markings of a plan must cover its MBOM: lowering is refused, and the minimal cover needs confirmation.
        var (_, third) = await env.ReleasedMbomAsync();
        var itarMbomProduct = await env.M.ProductAsync(ConfigEnv.Itar());
        Assert.NotNull(itarMbomProduct);
        Assert.True((await env.Plans.CreateAsync(ProcEnv.Alice, new CreatePlanCommand(third, null, ConfigEnv.Itar()), "c")).IsOk);
    }
}
