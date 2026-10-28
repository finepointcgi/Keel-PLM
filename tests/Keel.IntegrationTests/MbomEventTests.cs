using System.Net;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.Integration;
using Keel.Security;
using Keel.Workflow;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.IntegrationTests;

/// <summary>
/// The <c>mbom.released</c> ERP event and the MBOM pull API (ADR-0025, FUP-1): emitted in the release transaction only, validated against its schema,
/// subject to the outbound release check and to the placeholder rule for children the MBOM's markings do not cover.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MbomEventTests(PostgresFixture pg)
{
    private sealed record Rig(IntegrationEnv Integration, MbomEnv Mbom);

    private async Task<Rig> StartAsync()
    {
        var integration = await IntegrationEnv.CreateAsync(pg);
        return new Rig(integration, await MbomEnv.CreateAsync(pg, integration.Config));
    }

    /// <summary>A released sub-assembly MBOM and a reconciled (not yet released) MBOM of the top assembly of a product with the given markings.</summary>
    private static async Task<(MbomEnv.Product Product, string Top, string Sub)> ReconciledAsync(MbomEnv env, Markings markings)
    {
        var p = await env.ProductAsync(markings);
        var sub = await env.GenerateAsync(p.Sub);
        await env.ReconcileAsync(sub.Summary.Number);
        await env.ReleaseAsync(sub.Summary.Number);
        var top = await env.GenerateAsync(p.Top);
        var r = await env.ReconcileAsync(top.Summary.Number);
        Assert.Equal(ReconcileStatuses.Reconciled, r.Run.Status);
        return (p, top.Summary.Number, sub.Summary.Number);
    }

    [Fact]
    public async Task The_event_is_emitted_when_the_mbom_is_released_and_not_when_it_is_checked_in_or_submitted()
    {
        var rig = await StartAsync();
        var (env, integration) = (rig.Mbom, rig.Integration);
        var (p, top, sub) = await ReconciledAsync(env, ConfigEnv.Itar());

        // The sub-assembly's release already reported once; nothing for generation, reconciliation, check-in or submission of the top MBOM.
        Assert.Equal([sub], (await integration.EventsAsync(EventTypes.MbomReleased)).Select(e => e.Aggregate));
        await env.CheckOutAsync(top);
        await env.UpdateLineAsync(top, 10, c => c with { Notes = "pack in kit" });
        await env.CheckInAsync(top);
        await env.ReconcileAsync(top);
        var started = await env.SubmitAsync(top);
        Assert.True(started.IsOk, string.Join("; ", started.Errors));
        Assert.Single(await integration.EventsAsync(EventTypes.MbomReleased));

        var done = await env.SignAllAsync(started.Value!);
        Assert.Equal("Approved", done.Status);
        var events = await integration.EventsAsync(EventTypes.MbomReleased);
        Assert.Equal([sub, top], events.Select(e => e.Aggregate));

        var e = events[1];
        var payload = JsonNode.Parse(e.Payload)!;
        Assert.Equal(top, payload["mbom"]!["number"]!.GetValue<string>());
        Assert.Equal("PLANT-A", payload["mbom"]!["plant"]!.GetValue<string>());
        Assert.Equal("A", payload["mbom"]!["revision"]!.GetValue<string>());
        Assert.Equal("A.2", payload["mbom"]!["iteration"]!.GetValue<string>());
        Assert.Equal("Released", payload["mbom"]!["state"]!.GetValue<string>());
        Assert.Equal(p.Top, payload["part"]!["number"]!.GetValue<string>());
        Assert.Equal("A.2", payload["derivedFrom"]!["ebomIteration"]!.GetValue<string>()); // the EBOM was checked in once after the part was created
        Assert.Equal(ReconcileStatuses.Reconciled, payload["reconciliation"]!["status"]!.GetValue<string>());
        Assert.Equal(await env.TextAsync($"SELECT i.content_sha256 FROM mbom.iteration i JOIN mbom.revision r ON r.id = i.revision_id JOIN mbom.mbom m ON m.id = r.mbom_id WHERE m.number = '{top}' AND i.iteration = 2"),
            payload["contentSha256"]!.GetValue<string>());
        Assert.Equal(await env.TextAsync($"SELECT rr.id::text FROM mbom.reconciliation_run rr JOIN mbom.mbom m ON m.id = rr.mbom_id WHERE m.number = '{top}' ORDER BY rr.run_at DESC LIMIT 1"),
            payload["reconciliation"]!["runId"]!.GetValue<string>());

        var lines = payload["lines"]!.AsArray();
        Assert.Equal(3, lines.Count);
        Assert.Equal(0, payload["restrictedLineCount"]!.GetValue<int>());
        var screw = lines.Single(l => l!["findNumber"]!.GetValue<int>() == 10)!;
        Assert.Equal(4m, screw["quantity"]!.GetValue<decimal>());
        Assert.Equal("EA", screw["unit"]!.GetValue<string>());
        Assert.Equal(p.Screw, screw["child"]!["number"]!.GetValue<string>());
        Assert.Equal("pack in kit", screw["notes"]!.GetValue<string>());
        Assert.False(screw["phantom"]!.GetValue<bool>());
        Assert.Equal("matched", screw["ebomLinks"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(10, screw["ebomLinks"]![0]!["ebomFindNumber"]!.GetValue<int>());
        var makeSub = lines.Single(l => l!["findNumber"]!.GetValue<int>() == 30)!;
        Assert.Equal(sub, makeSub["childMbom"]!["number"]!.GetValue<string>()); // the plant's released MBOM of the Make sub-assembly
        Assert.Equal("Make", makeSub["supplyType"]!.GetValue<string>());
        IntegrationEnv.AssertPayloadValid(e.Type, 1, e.Payload);
        await integration.AssertEveryEmittedPayloadMatchesItsSchemaAsync();
        // The event carries the MBOM's markings.
        Assert.Equal("Itar", await env.TextAsync($"SELECT export_jurisdiction FROM integration.outbox WHERE aggregate_number = '{top}' AND event_type = 'mbom.released'"));
        Assert.Equal("mbom", await env.TextAsync($"SELECT aggregate_type FROM integration.outbox WHERE aggregate_number = '{top}'"));
    }

    [Fact]
    public async Task A_release_that_rolls_back_leaves_no_event_and_the_real_release_leaves_exactly_one()
    {
        var rig = await StartAsync();
        var (env, integration) = (rig.Mbom, rig.Integration);
        var (_, top, _) = await ReconciledAsync(env, ConfigEnv.Open);
        var before = (await integration.EventsAsync(EventTypes.MbomReleased)).Count;
        var started = await env.SubmitAsync(top);
        Assert.True(started.IsOk, string.Join("; ", started.Errors));

        // Complete the release by hand inside a transaction that is rolled back: the state change, the audit event and the outbox row all disappear together.
        var audit = new PostgresAuditLog(env.Ds, env.C.Clock);
        var gateway = new MbomWorkflowGateway(audit, env.C.Sink);
        await using (var conn = await env.Ds.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            var obj = (await gateway.FindByKeyAsync(conn, tx, top, false, CancellationToken.None))!;
            var result = await gateway.CompleteReleaseAsync(conn, tx, obj, obj.IterationId, MbomEnv.Alice, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.True(result.IsOk);
            Assert.Equal(before + 1, await CountInTransactionAsync(conn, tx)); // visible inside the transaction only
            await tx.RollbackAsync();
        }
        Assert.Equal(before, (await integration.EventsAsync(EventTypes.MbomReleased)).Count);
        Assert.Equal("UnderReview", (await env.GetAsync(top)).Summary.State);

        var done = await env.SignAllAsync(started.Value!);
        Assert.Equal("Approved", done.Status);
        Assert.Equal(before + 1, (await integration.EventsAsync(EventTypes.MbomReleased)).Count);
    }

    private static async Task<long> CountInTransactionAsync(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx)
    {
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT count(*) FROM integration.outbox WHERE event_type = 'mbom.released'", conn, tx);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task A_child_the_mbom_markings_do_not_cover_is_a_restricted_placeholder_and_never_leaks()
    {
        var rig = await StartAsync();
        var (env, integration) = (rig.Mbom, rig.Integration);
        var secret = await env.C.ReleasedComponentAsync("Secret plate", ConfigEnv.Itar());
        var screw = await env.BuyPartAsync("Open screw");
        var kit = await env.C.AssemblyAsync("Open kit", (screw, 4m), (secret, 3m));
        await env.C.ReleaseAsync(kit);
        var mbom = (await env.GenerateAsync(kit)).Summary.Number;
        Assert.Equal("None", await env.TextAsync($"SELECT export_jurisdiction FROM mbom.mbom WHERE number = '{mbom}'")); // the MBOM is open, its child is ITAR
        await env.ReconcileAsync(mbom);
        await env.ReleaseAsync(mbom);

        var e = Assert.Single(await integration.EventsAsync(EventTypes.MbomReleased));
        var payload = JsonNode.Parse(e.Payload)!;
        Assert.Equal(2, payload["lineCount"]!.GetValue<int>());
        Assert.Equal(1, payload["restrictedLineCount"]!.GetValue<int>());
        var placeholder = payload["lines"]!.AsArray().Single(l => l!["restricted"]!.GetValue<bool>())!;
        Assert.Equal(3m, placeholder["quantity"]!.GetValue<decimal>());
        Assert.Equal("EA", placeholder["unit"]!.GetValue<string>());
        Assert.Equal(["alternate", "findNumber", "quantity", "restricted", "unit"], placeholder.AsObject().Select(kv => kv.Key).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(secret, e.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret plate", e.Payload, StringComparison.Ordinal);
        Assert.Equal(screw, payload["lines"]!.AsArray().Single(l => !l!["restricted"]!.GetValue<bool>())!["child"]!["number"]!.GetValue<string>());
        IntegrationEnv.AssertPayloadValid(e.Type, 1, e.Payload);
    }

    [Fact]
    public async Task The_clearance_matrix_decides_which_sinks_receive_the_event_and_an_itar_mbom_never_reaches_an_uncleared_sink()
    {
        var rig = await StartAsync();
        var (env, integration) = (rig.Mbom, rig.Integration);
        await integration.SaveProfileAsync(IntegrationEnv.Profile("cleared"));
        await integration.SaveProfileAsync(IntegrationEnv.Profile("no-program", programs: []));
        await integration.SaveProfileAsync(IntegrationEnv.Profile("foreign", category: "Foreign", usOnly: false, statements: ["A"]));
        await integration.SaveProfileAsync(IntegrationEnv.Profile("open-only", jurisdictions: ["None"]));
        foreach (var (sink, profile) in new[] { ("drop-cleared", "cleared"), ("drop-noprogram", "no-program"), ("drop-foreign", "foreign"), ("drop-open", "open-only") })
            await integration.SaveFileSinkAsync(sink, profile);

        var (_, top, _) = await ReconciledAsync(env, ConfigEnv.Itar());
        await env.ReleaseAsync(top);
        await integration.DrainAsync();

        var deliveries = (await integration.DeliveriesAsync()).Where(d => d.EventType == EventTypes.MbomReleased).ToList();
        string Status(string sink) => deliveries.Where(d => d.SinkName == sink).Select(d => d.Status).Distinct().Single();
        Assert.Equal("delivered", Status("drop-cleared"));
        Assert.Equal("withheld", Status("drop-noprogram"));
        Assert.Equal("withheld", Status("drop-foreign"));
        Assert.Equal("withheld", Status("drop-open"));
        // The withheld event leaves nothing behind in the uncleared sinks' directories.
        foreach (var sink in new[] { "drop-noprogram", "drop-foreign", "drop-open" })
        {
            var dir = integration.DropDirectory(sink);
            Assert.False(Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any(f => File.ReadAllText(f).Contains(top, StringComparison.Ordinal)), sink);
        }
        Assert.Contains(await env.AuditAsync(), a => a.Action == "integration.delivery.withheld" && a.Outcome == AuditOutcome.Denied);

        // The delivered file is the envelope and validates against both schemas.
        var file = Directory.EnumerateFiles(integration.DropDirectory("drop-cleared"), "*.json").Where(f => !f.EndsWith(".manifest.json", StringComparison.Ordinal))
            .Single(f => File.ReadAllText(f).Contains("mbom.released", StringComparison.Ordinal) && File.ReadAllText(f).Contains(top, StringComparison.Ordinal));
        IntegrationEnv.AssertEnvelopeValid(JsonNode.Parse(File.ReadAllText(file))!);
    }

    [Fact]
    public async Task A_sink_subscribed_to_other_types_does_not_receive_the_event_and_one_subscribed_to_it_does()
    {
        var rig = await StartAsync();
        var (env, integration) = (rig.Mbom, rig.Integration);
        await integration.SaveProfileAsync(IntegrationEnv.Profile("cleared"));
        await integration.SaveFileSinkAsync("only-parts", "cleared", types: [EventTypes.PartReleased]);
        await integration.SaveFileSinkAsync("only-mboms", "cleared", types: [EventTypes.MbomReleased]);
        var (_, top, _) = await ReconciledAsync(env, ConfigEnv.Open);
        await env.ReleaseAsync(top);
        var deliveries = await integration.DeliveriesAsync();
        Assert.DoesNotContain(deliveries, d => d.SinkName == "only-parts" && d.EventType == EventTypes.MbomReleased);
        Assert.Contains(deliveries, d => d.SinkName == "only-mboms" && d.EventType == EventTypes.MbomReleased);
    }

    [Fact]
    public async Task The_released_mbom_read_equals_the_policy_oracle_for_every_subject()
    {
        var rig = await StartAsync();
        var env = rig.Mbom;
        // An open MBOM whose children are an ITAR plate (program ORCA) and an open screw.
        var itar = await env.C.ReleasedComponentAsync("Itar plate", ConfigEnv.Itar());
        var screw = await env.BuyPartAsync("Open screw");
        var kit = await env.C.AssemblyAsync("Open kit", (screw, 4m), (itar, 1m));
        await env.C.ReleaseAsync(kit);
        var mbom = (await env.GenerateAsync(kit)).Summary.Number;
        await env.ReconcileAsync(mbom);
        await env.ReleaseAsync(mbom);

        Subject Service(string name, bool us = true, string[]? programs = null) => WorkflowEnv.User(name, [Roles.Integration, Roles.Viewer], us, programs: programs ?? ["ORCA"], cui: ["EXPT", "CTI"]);
        var subjects = new[] { Service("erp-orca"), Service("erp-kraken", programs: ["KRAKEN"]), Service("erp-none", programs: []), Service("erp-nonus", us: false) };
        var partMarkings = new Dictionary<int, Markings> { [10] = ConfigEnv.Open, [20] = ConfigEnv.Itar() };
        foreach (var s in subjects)
        {
            var r = await env.Source.Source.GetReleasedAsync(s, kit, MbomEnv.Plant, null, "corr");
            Assert.True(AccessPolicy.Evaluate(s, ConfigEnv.Open, PlmAction.Read).Permitted);
            Assert.True(r.IsOk, $"{s.Username}: {r.Kind}");
            var lines = r.Value!.Lines;
            Assert.Equal(2, lines.Count);
            foreach (var (find, markings) in partMarkings)
            {
                var line = lines.Single(l => l.FindNumber == find);
                Assert.Equal(!AccessPolicy.Evaluate(s, markings, PlmAction.Read).Permitted, line.Redacted);
                if (line.Redacted) { Assert.Null(line.ChildNumber); Assert.Null(line.ChildName); }
            }
        }
        Assert.Contains(subjects, s => !AccessPolicy.Evaluate(s, ConfigEnv.Itar(), PlmAction.Read).Permitted); // the oracle really differs between subjects
    }

    [Fact]
    public async Task The_pull_endpoint_requires_the_integration_role_a_plant_and_applies_the_callers_markings()
    {
        using var f = await ApiFactory.CreateAsync(pg);
        var parts = f.Services.GetRequiredService<Keel.Parts.PartService>();
        var bom = f.Services.GetRequiredService<BomService>();
        var mbom = f.Services.GetRequiredService<MbomService>();
        var flow = f.Services.GetRequiredService<WorkflowService>();
        var directory = f.Services.GetRequiredService<UserDirectory>();
        foreach (var u in new[] { ConfigEnv.Alice, ConfigEnv.Rita, ConfigEnv.Bob, ConfigEnv.Carol }) await directory.UpsertAsync(u);
        var alice = ConfigEnv.Alice;
        async Task<string> Part(string name, string type, Markings m) => (await parts.CreateAsync(alice, new Keel.Parts.CreatePartCommand(name, type, "EA", "Make", m), null)).Value!.Number;
        var open = await Part("Open washer", "Component", ConfigEnv.Open);
        var secret = await Part("Secret bracket", "Component", ConfigEnv.Itar());
        var asm = await Part("Open kit", "Assembly", ConfigEnv.Open);
        Assert.True((await parts.CheckOutAsync(alice, asm, null)).IsOk);
        Assert.True((await bom.AddUsageAsync(alice, asm, new AddUsageCommand(open, 4m, null, 10), null)).IsOk);
        Assert.True((await bom.AddUsageAsync(alice, asm, new AddUsageCommand(secret, 2m, null, 20), null)).IsOk);
        Assert.True((await parts.CheckInAsync(alice, asm, "structure", null)).IsOk);
        foreach (var p in new[] { open, secret, asm }) Assert.True((await parts.SetStateAsync(ConfigEnv.Ada, p, "Released", "load", true, null)).IsOk);
        var generated = await mbom.GenerateAsync(alice, new GenerateMbomCommand(asm, MbomEnv.Plant, null, false), "c");
        Assert.True(generated.IsOk, string.Join("; ", generated.Errors));
        var number = generated.Value!.Items[0].MbomNumber;
        Assert.True((await mbom.ReconcileAsync(alice, number, null, "c")).IsOk);
        var started = await flow.StartAsync(alice, new StartReviewCommand(MbomService.ObjectType, number, new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob", "carol"] }), "c");
        Assert.True(started.IsOk, string.Join("; ", started.Errors));
        var view = started.Value!;
        foreach (var user in new[] { ConfigEnv.Rita, ConfigEnv.Bob, ConfigEnv.Carol })
            while (view.Status == "Active" && view.Tasks.Any(t => t.Status == "Open" && t.AssigneeUsername == user.Username))
            {
                var task = view.Tasks.First(t => t.Status == "Open" && t.AssigneeUsername == user.Username);
                var signed = await flow.SignAsync(WorkflowEnv.Fresh(user), task.Id, task.Kind == "review" ? "Reviewed" : "Approved", null, "c");
                Assert.True(signed.IsOk, string.Join("; ", signed.Errors));
                view = signed.Value!;
            }
        Assert.Equal("Approved", view.Status);

        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync($"/api/v1/integration/mboms/{asm}?plant=PLANT-A")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.ClientFor(ApiFactory.Token(roles: ["plm-viewer"])).GetAsync($"/api/v1/integration/mboms/{asm}?plant=PLANT-A")).StatusCode);
        string Erp(bool us = true, string[]? programs = null) => ApiFactory.Token("u-erp", us, roles: ["plm-integration"], programs: programs ?? ["ORCA"], cui: ["EXPT", "CTI"]);
        var erp = f.ClientFor(Erp());
        Assert.Equal(HttpStatusCode.BadRequest, (await erp.GetAsync($"/api/v1/integration/mboms/{asm}")).StatusCode); // the plant is required
        Assert.Equal(HttpStatusCode.NotFound, (await erp.GetAsync($"/api/v1/integration/mboms/{asm}?plant=PLANT-Z")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await erp.GetAsync("/api/v1/integration/mboms/P-999999?plant=PLANT-A")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await erp.GetAsync($"/api/v1/integration/mboms/{asm}?plant=PLANT-A&revision=Q")).StatusCode);

        var full = JsonNode.Parse(await erp.GetStringAsync($"/api/v1/integration/mboms/{asm}?plant=PLANT-A&revision=A"))!;
        Assert.Equal(number, full["mbomNumber"]!.GetValue<string>());
        Assert.Equal("Released", full["state"]!.GetValue<string>());
        Assert.Equal(0, full["restrictedLines"]!.GetValue<int>());
        Assert.Equal([open, secret], full["lines"]!.AsArray().Select(l => l!["number"]!.GetValue<string>()));
        // A non-US account without programs reads the open MBOM but only sees a placeholder for the ITAR child.
        var limited = JsonNode.Parse(await f.ClientFor(Erp(us: false, programs: [])).GetStringAsync($"/api/v1/integration/mboms/{asm}?plant=PLANT-A"))!;
        Assert.Equal(1, limited["restrictedLines"]!.GetValue<int>());
        var hidden = limited["lines"]!.AsArray().Single(l => l!["restricted"]!.GetValue<bool>())!;
        Assert.Null(hidden["number"]);
        Assert.Equal(2m, hidden["quantity"]!.GetValue<decimal>());
        Assert.DoesNotContain(secret, limited.ToJsonString(), StringComparison.Ordinal);
    }
}
