using Keel.Core;
using Keel.Security;
using Keel.Process;

namespace Keel.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ProcessToolingTests(PostgresFixture pg)
{
    [Fact]
    public async Task Work_centers_are_administrator_data_per_plant_and_retired_never_deleted()
    {
        var env = await ProcEnv.CreateAsync(pg);
        var denied = await env.Tools.SaveWorkCenterAsync(ProcEnv.Alice, new SaveWorkCenterCommand(ProcEnv.Plant, "WC-1", "Lathe", "machine", true), "c");
        Assert.Equal(OutcomeKind.Forbidden, denied.Kind);
        await env.WorkCenterAsync("WC-1", kind: "machine");
        var bad = await env.Tools.SaveWorkCenterAsync(ProcEnv.Ada, new SaveWorkCenterCommand("NOWHERE", "WC-2", "X", "machine", true), "c");
        Assert.Equal(OutcomeKind.Invalid, bad.Kind);
        ProcEnv.Ok(await env.Tools.SaveWorkCenterAsync(ProcEnv.Ada, new SaveWorkCenterCommand(ProcEnv.Plant, "WC-1", "Lathe", "machine", false), "c"));
        Assert.Empty(await env.Tools.ListWorkCentersAsync(ProcEnv.Plant, false));
        Assert.Single(await env.Tools.ListWorkCentersAsync(ProcEnv.Plant, true));
    }

    [Fact]
    public async Task Calibration_sets_the_due_date_and_expiry_notices_are_raised_once_per_stage()
    {
        var env = await ProcEnv.CreateAsync(pg);
        var today = DateOnly.FromDateTime(env.Clock.GetUtcNow().UtcDateTime);
        var tool = await env.NewToolAsync("Bore gauge", "gauge", interval: 90, last: today.AddDays(-100));
        Assert.Equal(today.AddDays(-10), tool.Summary.DueOn);
        Assert.Equal("overdue", tool.Summary.CalibrationState);

        Assert.Equal(1, await env.Expiry.RunOnceAsync());
        Assert.Equal(0, await env.Expiry.RunOnceAsync()); // idempotent
        Assert.Equal(1, await env.M.ScalarAsync("SELECT count(*) FROM audit.event WHERE action = 'tool.calibration_notice' AND object_id = '" + tool.Summary.Number + "'"));

        var calibrated = ProcEnv.Ok(await env.Tools.RecordCalibrationAsync(ProcEnv.Alice, tool.Summary.Number, today, "CERT-1", null, "c"));
        Assert.Equal(today.AddDays(90), calibrated.Summary.DueOn);
        Assert.Equal("valid", calibrated.Summary.CalibrationState);
        Assert.Single(calibrated.History);
        Assert.Equal(0, await env.Expiry.RunOnceAsync());

        // Time passes: the next cycle gets fresh notices (30 days, 14, 7, then overdue).
        env.Clock.Advance(TimeSpan.FromDays(65));
        Assert.Equal(1, await env.Expiry.RunOnceAsync());
        env.Clock.Advance(TimeSpan.FromDays(12));
        Assert.Equal(1, await env.Expiry.RunOnceAsync());
        env.Clock.Advance(TimeSpan.FromDays(10));
        Assert.Equal(1, await env.Expiry.RunOnceAsync());

        // History is append-only.
        await Assert.ThrowsAnyAsync<Exception>(() => env.AppSqlAsync("UPDATE proc.tool_calibration SET note = 'x'"));
        await Assert.ThrowsAnyAsync<Exception>(() => env.AppSqlAsync("DELETE FROM proc.tool_calibration"));
    }

    [Fact]
    public async Task A_tool_that_needs_calibration_needs_an_interval_and_a_retired_one_cannot_be_used()
    {
        var env = await ProcEnv.CreateAsync(pg);
        var refused = await env.Tools.CreateAsync(ProcEnv.Alice, new CreateToolCommand("Gauge", "gauge", ProcEnv.Plant, null, true, null, null, ConfigEnv.Open), "c");
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        var viewer = await env.Tools.CreateAsync(ConfigEnv.Mallory, new CreateToolCommand("Gauge", "gauge", ProcEnv.Plant, null, false, null, null, ConfigEnv.Open), "c");
        Assert.Equal(OutcomeKind.Forbidden, viewer.Kind);

        var (_, mbom) = await env.ReleasedMbomAsync();
        var number = (await env.CreatePlanAsync(mbom)).Summary.Number;
        var tool = await env.NewToolAsync("Fixture", "fixture", calibrated: false);
        var id = Guid.Parse(await env.M.TextAsync("SELECT id FROM proc.tool WHERE number = '" + tool.Summary.Number + "'"));
        ProcEnv.Ok(await env.Tools.SetStatusAsync(ProcEnv.Alice, tool.Summary.Number, true, "Scrapped", "c"));
        var wc = await env.WorkingAsync(number);
        var add = await env.Plans.SaveWorkingCopyAsync(ProcEnv.Alice, number, ProcEnv.With(wc, ProcEnv.Op(10, "Fit", tools: [id])), "c");
        Assert.Equal(OutcomeKind.Invalid, add.Kind);
        Assert.Contains("retired", string.Join(" ", add.Errors), StringComparison.Ordinal);
        var noReason = await env.Tools.SetStatusAsync(ProcEnv.Alice, tool.Summary.Number, false, " ", "c");
        Assert.Equal(OutcomeKind.Invalid, noReason.Kind);
    }

    [Fact]
    public async Task A_plan_that_uses_an_out_of_calibration_gauge_cannot_be_submitted_until_it_is_calibrated()
    {
        var env = await ProcEnv.CreateAsync(pg);
        var today = DateOnly.FromDateTime(env.Clock.GetUtcNow().UtcDateTime);
        var (_, mbom) = await env.ReleasedMbomAsync();
        var number = (await env.CreatePlanAsync(mbom)).Summary.Number;
        var gauge = await env.NewToolAsync("Micrometer", "gauge", interval: 30, last: today.AddDays(-40));
        var id = Guid.Parse(await env.M.TextAsync("SELECT id FROM proc.tool WHERE number = '" + gauge.Summary.Number + "'"));
        var wc = await env.WorkingAsync(number);
        var saved = await env.SaveAsync(number, ProcEnv.With(wc, ProcEnv.Op(10, "Measure", tools: [id])));
        Assert.Contains(saved.Findings, f => f.Code == "tool.calibration");
        Assert.True(saved.Routings[0].Operations[0].Tools[0].CalibrationOverdue);
        await env.CheckInAsync(number);
        var blocked = await env.SubmitAsync(number);
        Assert.False(blocked.IsOk);
        Assert.Contains("out of calibration", string.Join(" ", blocked.Errors), StringComparison.Ordinal);

        ProcEnv.Ok(await env.Tools.RecordCalibrationAsync(ProcEnv.Alice, gauge.Summary.Number, today, null, null, "c"));
        var detail = await env.GetAsync(number);
        Assert.DoesNotContain(detail.Findings, f => f.Code == "tool.calibration");
        // Where-used lists the plan for a reader of both.
        var use = ProcEnv.Ok(await env.Tools.GetAsync(ProcEnv.Alice, gauge.Summary.Number, "c")).UsedBy;
        Assert.Single(use);
        Assert.Equal(number, use[0].PlanNumber);
    }

    [Fact]
    public async Task Tool_lists_agree_with_the_access_policy_for_every_subject_and_marking()
    {
        var env = await ProcEnv.CreateAsync(pg);
        var variants = new[] { ConfigEnv.Open, ConfigEnv.Itar("ORCA"), ConfigEnv.Itar("OTHER") };
        var made = new List<(string Number, Markings M)>();
        foreach (var (m, i) in variants.Select((m, i) => (m, i)))
        {
            var t = await env.Tools.CreateAsync(ProcEnv.Ada, new CreateToolCommand("Tool " + i, "tool", ProcEnv.Plant, null, false, null, null, m), "c");
            if (t.IsOk) made.Add((t.Value!.Summary.Number, m));
        }
        Assert.True(made.Count >= 2);
        foreach (var who in new[] { ProcEnv.Alice, ProcEnv.Eve, ProcEnv.Ada, ProcEnv.Bob, ConfigEnv.Mallory })
        {
            var expected = made.Where(x => AccessPolicy.Evaluate(who, x.M, PlmAction.Read).Permitted).Select(x => x.Number).Order().ToList();
            var found = ProcEnv.Ok(await env.Tools.SearchAsync(who, new ToolSearch(Limit: 200), "c")).Items.Select(i => i.Number).Where(n => made.Any(x => x.Number == n)).Order().ToList();
            Assert.Equal(expected, found);
            foreach (var x in made)
                Assert.Equal(expected.Contains(x.Number), (await env.Tools.GetAsync(who, x.Number, "c")).IsOk);
        }
    }
}
