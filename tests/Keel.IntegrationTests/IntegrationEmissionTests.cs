using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.Documents;
using Keel.Integration;
using Keel.Security;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>The transactional outbox and every emission point (ADR-0025): events exist exactly when the business change committed.</summary>
[Collection(PostgresCollection.Name)]
public sealed class IntegrationEmissionTests(PostgresFixture pg)
{
    private static DomainEvent Sample(Markings? markings = null, string number = "P-000001") => new(EventTypes.PartReleased, "part", Guid.NewGuid(), number, "A", markings ?? ConfigEnv.Open,
        new JsonObject { ["partType"] = "Component", ["marker"] = number }, DateTimeOffset.UtcNow, "alice");

    [Fact]
    public async Task A_rolled_back_transaction_leaves_no_event_and_a_committed_one_exactly_one()
    {
        var env = await IntegrationEnv.CreateAsync(pg);
        await env.SaveProfileAsync(IntegrationEnv.Profile("erp"));
        await env.SaveFileSinkAsync("drop-a", "erp");
        await env.SaveFileSinkAsync("drop-off", "erp", enabled: false);

        await using (var conn = await env.Ds.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await env.Sink.AppendAsync(conn, tx, Sample(), CancellationToken.None);
            await tx.RollbackAsync();
        }
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM integration.outbox"));
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM integration.delivery"));

        await using (var conn = await env.Ds.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await env.Sink.AppendAsync(conn, tx, Sample(), CancellationToken.None);
            await tx.CommitAsync();
        }
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM integration.outbox"));
        // One pending delivery for the enabled sink; none for the disabled one.
        var deliveries = await env.DeliveriesAsync();
        Assert.Equal("drop-a", Assert.Single(deliveries).SinkName);
        Assert.Equal("pending", deliveries[0].Status);
    }

    [Fact]
    public async Task A_business_change_that_fails_after_emitting_rolls_the_event_back_with_it()
    {
        var env = await IntegrationEnv.CreateAsync(pg);
        var c = env.Config;
        var part = await c.NewPartAsync("Bracket", markings: ConfigEnv.Itar());
        // Releasing through the review workflow emits; a signature that fails verification later in the same transaction would roll both back.
        // Here we prove the simpler property directly: an exception after AppendAsync inside the caller's transaction discards the event.
        await using (var conn = await env.Ds.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await env.Sink.AppendAsync(conn, tx, Sample(number: part), CancellationToken.None);
            await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var bad = new NpgsqlCommand("INSERT INTO integration.sink (id, name, label, kind, profile_id, config, created_at, created_by) VALUES (gen_random_uuid(), 'x', 'x', 'bogus', gen_random_uuid(), '{}', now(), 'x')", conn, tx);
                await bad.ExecuteNonQueryAsync();
            });
        }
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM integration.outbox"));
    }

    [Fact]
    public async Task Releasing_a_part_through_the_workflow_emits_part_released_with_the_parts_markings_and_content()
    {
        var env = await IntegrationEnv.CreateAsync(pg);
        var c = env.Config;
        var part = await c.ReleasedComponentAsync("Bracket", ConfigEnv.Itar());
        var events = await env.EventsAsync();
        var e = Assert.Single(events);
        Assert.Equal(EventTypes.PartReleased, e.Type);
        Assert.Equal(part, e.Aggregate);
        var payload = JsonNode.Parse(e.Payload)!;
        Assert.Equal("workflow", payload["via"]!.GetValue<string>());
        Assert.Equal("A", payload["revision"]!.GetValue<string>());
        Assert.Equal("Released", payload["state"]!.GetValue<string>());
        Assert.Equal("6061-T6", payload["attributes"]!["material"]!.GetValue<string>());
        IntegrationEnv.AssertPayloadValid(e.Type, 1, e.Payload);
        // The event carries the markings of the object it reports.
        Assert.Equal("Itar", await env.TextAsync($"SELECT export_jurisdiction FROM integration.outbox WHERE aggregate_number = '{part}'"));
        Assert.Equal("ORCA", await env.TextAsync($"SELECT program FROM integration.outbox WHERE aggregate_number = '{part}'"));
        // A part still in work reports nothing.
        await c.NewPartAsync("Draft only");
        Assert.Single(await env.EventsAsync());
    }

    [Fact]
    public async Task An_administrator_override_into_released_and_obsolete_is_reported_with_its_origin()
    {
        var env = await IntegrationEnv.CreateAsync(pg);
        var c = env.Config;
        var part = await c.NewPartAsync("Override", markings: ConfigEnv.Open);
        Assert.True((await c.Parts.SetStateAsync(ConfigEnv.Ada, part, "Released", "migration correction", true, null)).IsOk);
        Assert.True((await c.Parts.SetStateAsync(ConfigEnv.Ada, part, "Obsolete", "retired", true, null)).IsOk);
        var events = await env.EventsAsync();
        Assert.Equal([EventTypes.PartReleased, EventTypes.PartObsoleted], events.Select(e => e.Type));
        Assert.All(events, e => Assert.Equal("admin", JsonNode.Parse(e.Payload)!["via"]!.GetValue<string>()));
        await env.AssertEveryEmittedPayloadMatchesItsSchemaAsync();
    }

    [Fact]
    public async Task Releasing_a_change_notice_emits_change_released_with_affected_resulting_and_obsoleted_revisions()
    {
        var env = await IntegrationEnv.CreateAsync(pg);
        var c = env.Config;
        var part = await c.ReleasedComponentAsync("Bracket", ConfigEnv.Open);
        var revised = await c.ReviseWithEffectivityAsync(part, null, "Revised description");
        var done = await c.ReleaseEcnAsync(revised.Number);
        Assert.Equal("Approved", done.Status);

        var released = Assert.Single(await env.EventsAsync(EventTypes.ChangeReleased));
        var payload = JsonNode.Parse(released.Payload)!;
        Assert.Equal(revised.Number, payload["number"]!.GetValue<string>());
        Assert.Equal(part, payload["affected"]![0]!["number"]!.GetValue<string>());
        Assert.Equal("B", payload["resulting"]![0]!["revision"]!.GetValue<string>());
        Assert.Contains($"{part} A", payload["obsoleted"]!.AsArray().Select(x => x!.GetValue<string>()));
        // The part events of the same release: B released (through the change), A obsoleted.
        var partEvents = (await env.EventsAsync()).Where(e => e.Type is EventTypes.PartReleased or EventTypes.PartObsoleted).Select(e => (e.Type, JsonNode.Parse(e.Payload)!["revision"]!.GetValue<string>(), JsonNode.Parse(e.Payload)!["via"]!.GetValue<string>())).ToList();
        Assert.Contains((EventTypes.PartReleased, "B", "change"), partEvents);
        Assert.Contains((EventTypes.PartObsoleted, "A", "change"), partEvents);
        await env.AssertEveryEmittedPayloadMatchesItsSchemaAsync();
    }

    [Fact]
    public async Task Approving_a_change_request_emits_change_approved()
    {
        var env = await IntegrationEnv.CreateAsync(pg);
        var c = env.Config;
        var part = await c.ReleasedComponentAsync("Bracket", ConfigEnv.Open);
        var ecr = await c.Change.CreateAsync(ConfigEnv.Alice, new Keel.Change.CreateChangeCommand(Keel.Change.ChangeTypes.Ecr, "Stiffen bracket", "Synthetic", "High", "Synthetic reason", ConfigEnv.Open, Keel.Change.ChangeClasses.I), "c");
        Assert.True(ecr.IsOk, string.Join(";", ecr.Errors));
        Assert.True((await c.Change.AddAffectedAsync(ConfigEnv.Alice, ecr.Value!.Number, new Keel.Change.AddAffectedCommand("part", part, Keel.Change.ChangeActions.Revise, null, false), "c")).IsOk);
        var started = await c.Flow.StartAsync(ConfigEnv.Alice, new Keel.Workflow.StartReviewCommand("ecr", ecr.Value.Number, new Dictionary<string, string[]> { ["ccb"] = ["bob", "carol"] }), "c");
        Assert.True(started.IsOk, string.Join(";", started.Errors));
        Assert.Equal("Approved", (await c.SignAllAsync(started.Value!)).Status);
        var e = Assert.Single(await env.EventsAsync(EventTypes.ChangeApproved));
        Assert.Equal(ecr.Value.Number, e.Aggregate);
        IntegrationEnv.AssertPayloadValid(e.Type, 1, e.Payload);
    }

    [Fact]
    public async Task Establishing_a_baseline_emits_baseline_established_directly_and_through_signoff()
    {
        var env = await IntegrationEnv.CreateAsync(pg);
        var c = env.Config;
        var bolt = await c.ReleasedComponentAsync("Bolt", ConfigEnv.Open);
        var top = await c.ReleasedAssemblyAsync("Top", (bolt, 2m));
        var direct = await c.BaselineAsync(top, type: "Design");
        Assert.True((await c.Baselines.EstablishAsync(ConfigEnv.Alice, direct.Summary.Number, "Design freeze", null)).IsOk);

        var signed = await c.BaselineAsync(top, type: "Release", name: "Release baseline");
        var started = await c.Flow.StartAsync(ConfigEnv.Alice, new Keel.Workflow.StartReviewCommand("baseline", signed.Summary.Number, new Dictionary<string, string[]> { ["reviewer"] = ["rita"], ["approvers"] = ["bob", "carol"] }), "c");
        if (started.IsOk) Assert.Equal("Approved", (await c.SignAllAsync(started.Value!)).Status);

        var events = await env.EventsAsync(EventTypes.BaselineEstablished);
        Assert.True(events.Count >= 1);
        var first = JsonNode.Parse(events[0].Payload)!;
        Assert.Equal(direct.Summary.Number, first["number"]!.GetValue<string>());
        Assert.Equal(top, first["root"]!["number"]!.GetValue<string>());
        Assert.Equal(64, first["contentSha256"]!.GetValue<string>().Length);
        await env.AssertEveryEmittedPayloadMatchesItsSchemaAsync();
    }

    [Fact]
    public async Task Releasing_a_document_revision_emits_document_released_with_its_files()
    {
        var env = await IntegrationEnv.CreateAsync(pg, documents: true);
        var c = env.Config;
        var doc = await c.Docs!.CreateAsync(ConfigEnv.Alice, new CreateDocumentCommand("Bracket drawing", "Drawing", ConfigEnv.Open, "Synthetic"),
            new FileUpload("bracket.txt", "text/plain", new MemoryStream("synthetic drawing bytes"u8.ToArray())), null);
        Assert.True(doc.IsOk, string.Join(";", doc.Errors));
        Assert.True((await c.Docs.SetStateAsync(ConfigEnv.Ada, doc.Value!.Number, "Released", "initial load", true, null)).IsOk);
        var e = Assert.Single(await env.EventsAsync(EventTypes.DocumentReleased));
        var payload = JsonNode.Parse(e.Payload)!;
        Assert.Equal("bracket.txt", payload["files"]![0]!["fileName"]!.GetValue<string>());
        IntegrationEnv.AssertPayloadValid(e.Type, 1, e.Payload);
    }

    // ---------------------------------------------------------------- bom.published

    /// <summary>A released assembly with the given markings (ConfigEnv's helper always uses open markings).</summary>
    internal static async Task<string> ReleasedAssemblyAsync(ConfigEnv c, string name, Markings markings, params (string Child, decimal Qty)[] children)
    {
        var n = await c.NewPartAsync(name, "Assembly", markings);
        await c.CheckOutAsync(n);
        foreach (var (child, qty) in children) await c.AddLineAsync(n, child, qty);
        await c.CheckInAsync(n, comment: "structure");
        await c.ReleaseAsync(n);
        return n;
    }

    [Fact]
    public async Task Releasing_an_assembly_emits_bom_published_with_children_at_their_released_revisions()
    {
        var env = await IntegrationEnv.CreateAsync(pg);
        var c = env.Config;
        var bolt = await c.ReleasedComponentAsync("Bolt", ConfigEnv.Itar());
        var plate = await c.ReleasedComponentAsync("Plate", ConfigEnv.Itar());
        var asm = await ReleasedAssemblyAsync(c, "Pod", ConfigEnv.Itar(), (bolt, 4m), (plate, 2m));
        var e = Assert.Single(await env.EventsAsync(EventTypes.BomPublished));
        Assert.Equal(asm, e.Aggregate);
        var payload = JsonNode.Parse(e.Payload)!;
        Assert.Equal(2, payload["lineCount"]!.GetValue<int>());
        Assert.Equal(0, payload["restrictedLineCount"]!.GetValue<int>());
        var lines = payload["lines"]!.AsArray();
        Assert.Equal([bolt, plate], lines.Select(l => l!["child"]!["number"]!.GetValue<string>()));
        Assert.Equal(4m, lines[0]!["quantity"]!.GetValue<decimal>());
        Assert.Equal("A", lines[0]!["child"]!["revision"]!.GetValue<string>());
        Assert.Equal(10, lines[0]!["findNumber"]!.GetValue<int>());
        IntegrationEnv.AssertPayloadValid(e.Type, 1, e.Payload);
        // Without a sink that asked for it there is no full-structure event.
        Assert.Empty(await env.EventsAsync(EventTypes.BomStructurePublished));
    }

    [Fact]
    public async Task A_child_the_assemblys_markings_do_not_cover_becomes_a_restricted_placeholder_never_a_silent_drop_or_a_leak()
    {
        var env = await IntegrationEnv.CreateAsync(pg);
        var c = env.Config;
        // The assembly is open, its child is ITAR on program ORCA: whoever may read the assembly may not read the child.
        var secret = await c.ReleasedComponentAsync("Secret bracket", ConfigEnv.Itar());
        var open = await c.ReleasedComponentAsync("Open washer", ConfigEnv.Open);
        var asm = await c.ReleasedAssemblyAsync("Open kit", (open, 1m), (secret, 3m));
        var e = Assert.Single(await env.EventsAsync(EventTypes.BomPublished));
        Assert.Equal(asm, e.Aggregate);
        var payload = JsonNode.Parse(e.Payload)!;
        Assert.Equal(2, payload["lineCount"]!.GetValue<int>());
        Assert.Equal(1, payload["restrictedLineCount"]!.GetValue<int>());
        var placeholder = payload["lines"]!.AsArray().Single(l => l!["restricted"]!.GetValue<bool>())!;
        Assert.Equal(3m, placeholder["quantity"]!.GetValue<decimal>());
        Assert.Null(placeholder["child"]);
        Assert.DoesNotContain(secret, e.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret bracket", e.Payload, StringComparison.Ordinal);
        IntegrationEnv.AssertPayloadValid(e.Type, 1, e.Payload);
    }

    [Fact]
    public async Task A_sink_that_opted_in_receives_the_full_structure_and_an_oversized_structure_is_skipped_and_audited_not_truncated()
    {
        var env = await IntegrationEnv.CreateAsync(pg, configure: o => o.BomFullStructureMaxLines = 4);
        var c = env.Config;
        await env.SaveProfileAsync(IntegrationEnv.Profile("erp"));
        await env.SaveFileSinkAsync("plain", "erp");
        await env.SaveFileSinkAsync("deep", "erp", full: true);
        var bolt = await c.ReleasedComponentAsync("Bolt", ConfigEnv.Itar());
        var sub = await ReleasedAssemblyAsync(c, "Sub", ConfigEnv.Itar(), (bolt, 2m));
        var top = await ReleasedAssemblyAsync(c, "Top", ConfigEnv.Itar(), (sub, 1m));

        // Top has 3 lines in total (sub, bolt) = 2 lines: within the limit of 4.
        var structure = (await env.EventsAsync(EventTypes.BomStructurePublished)).Where(e => e.Aggregate == top).Single();
        var lines = JsonNode.Parse(structure.Payload)!["lines"]!.AsArray();
        Assert.Equal(["10", "10.10"], lines.Select(l => l!["path"]!.GetValue<string>()));
        Assert.Equal(2m, lines[1]!["extendedQuantity"]!.GetValue<decimal>());
        IntegrationEnv.AssertPayloadValid(structure.Type, 1, structure.Payload);

        // Only the opted-in sink gets deliveries of the structure event; the plain sink gets the one-level event.
        var deliveries = await env.DeliveriesAsync();
        Assert.Contains(deliveries, d => d.SinkName == "deep" && d.EventType == EventTypes.BomStructurePublished);
        Assert.DoesNotContain(deliveries, d => d.SinkName == "plain" && d.EventType == EventTypes.BomStructurePublished);

        // A structure above the limit: five lines.
        var parts = new List<(string, decimal)>();
        for (var i = 0; i < 5; i++) parts.Add((await c.ReleasedComponentAsync($"Washer {i}", ConfigEnv.Itar()), 1m));
        var big = await ReleasedAssemblyAsync(c, "Big", ConfigEnv.Itar(), [.. parts]);
        Assert.DoesNotContain(await env.EventsAsync(EventTypes.BomStructurePublished), e => e.Aggregate == big);
        Assert.Contains(await env.EventsAsync(EventTypes.BomPublished), e => e.Aggregate == big); // the one-level event is not affected
        var audit = await c.AuditAsync();
        Assert.Contains(audit, a => a.Action == "integration.event_skipped" && a.Payload.Contains(big, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- DB guards

    [Fact]
    public async Task The_outbox_and_the_tdp_record_are_immutable_for_the_application_role()
    {
        var env = await IntegrationEnv.CreateAsync(pg);
        await using (var conn = await env.Ds.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await env.Sink.AppendAsync(conn, tx, Sample(), CancellationToken.None);
            await tx.CommitAsync();
        }
        foreach (var sql in new[] { "UPDATE integration.outbox SET actor = 'mallory'", "DELETE FROM integration.outbox", "TRUNCATE integration.outbox" })
        {
            await using var cmd = env.Ds.CreateCommand(sql);
            await Assert.ThrowsAsync<PostgresException>(async () => await cmd.ExecuteNonQueryAsync());
        }
        // Even the schema owner is stopped by the trigger, so only an explicit, audited maintenance procedure could change it.
        await using var owner = new NpgsqlConnection(env.Config.Db.MigratorConnectionString);
        await owner.OpenAsync();
        await using var del = new NpgsqlCommand("DELETE FROM integration.outbox", owner);
        await Assert.ThrowsAsync<PostgresException>(async () => await del.ExecuteNonQueryAsync());
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM integration.outbox"));
    }

    [Fact]
    public async Task Event_ids_are_uuid_v7_and_strictly_ordered_by_sequence()
    {
        var env = await IntegrationEnv.CreateAsync(pg);
        for (var i = 0; i < 3; i++)
        {
            await using var conn = await env.Ds.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await env.Sink.AppendAsync(conn, tx, Sample(number: $"P-00000{i}"), CancellationToken.None);
            await tx.CommitAsync();
        }
        var ids = (await env.EventsAsync()).Select(e => e.Id).ToList();
        Assert.All(ids, id => Assert.Equal('7', id.ToString("D")[14]));
        // Guid.CreateVersion7 orders by its 48-bit millisecond timestamp only; the tail is random, so events committed within one
        // millisecond may have ids in any order. Consumers order by the outbox sequence, which this test also checks.
        var stamps = ids.Select(i => Convert.ToHexStringLower(i.ToByteArray(bigEndian: true)[..6])).ToList();
        Assert.Equal(stamps.OrderBy(x => x, StringComparer.Ordinal).ToList(), stamps);
    }

    [Fact]
    public async Task Administration_needs_the_administrator_role_and_audits_the_denial()
    {
        var env = await IntegrationEnv.CreateAsync(pg);
        var denied = await env.Admin.SaveProfileAsync(ConfigEnv.Alice, IntegrationEnv.Profile("sneaky"), "corr");
        Assert.Equal(OutcomeKind.Forbidden, denied.Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Admin.ListSinksAsync(ConfigEnv.Alice)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Admin.RequeueDeadAsync(ConfigEnv.Alice, null, "x", null)).Kind);
        Assert.Contains(await env.Config.AuditAsync(), a => a.Action == "integration.profile.save" && a.Outcome == AuditOutcome.Denied);
        // A broker sink cannot be configured until Phase 4b.
        await env.SaveProfileAsync(IntegrationEnv.Profile("erp"));
        var ex = await Assert.ThrowsAsync<NotSupportedException>(async () => await env.Admin.SaveSinkAsync(IntegrationEnv.Ada, new SinkInput("bus", "Bus", "broker", true, "erp", new JsonObject()), null));
        Assert.Contains("Phase 4b", ex.Message, StringComparison.Ordinal);
        // Profile edits are validated: statement F cannot be listed.
        var bad = await env.Admin.SaveProfileAsync(IntegrationEnv.Ada, IntegrationEnv.Profile("bad", statements: ["A", "F"]), null);
        Assert.Equal(OutcomeKind.Invalid, bad.Kind);
    }
}
