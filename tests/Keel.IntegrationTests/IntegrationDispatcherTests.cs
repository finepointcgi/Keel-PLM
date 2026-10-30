using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Integration;
using Keel.Security;
using Keel.TestSupport;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Delivery semantics against a real reference receiver over HTTPS with a throwaway CA (ADR-0025): signing, retry, dead letters, SSRF refusal,
/// withheld events, duplicates, concurrency, ordering, shutdown and the file sink.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class IntegrationDispatcherTests(PostgresFixture pg)
{
    private sealed class Rig : IAsyncDisposable
    {
        public required IntegrationEnv Env { get; init; }
        public required TestPki Pki { get; init; }
        public required ReferenceReceiver Receiver { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Receiver.DisposeAsync();
            Pki.Dispose();
        }

        public Task<SinkRecord> Webhook(string name = "erp", string profile = "erp", Action<JsonObject>? tweak = null)
        {
            var config = new JsonObject { ["url"] = Receiver.Url.ToString(), ["secretName"] = "erp-main", ["caBundleName"] = "test-ca", ["timeoutSeconds"] = 5 };
            tweak?.Invoke(config);
            return Env.SaveSinkAsync(name, "webhook", profile, config);
        }
    }

    private async Task<Rig> StartAsync(Action<IntegrationOptions>? configure = null, bool mtls = false, string[]? receiverSecrets = null)
    {
        var pki = new TestPki();
        var receiver = await ReferenceReceiver.StartAsync(pki, receiverSecrets ?? ["s3cret"], mtls);
        var env = await IntegrationEnv.CreateAsync(pg, configure: o =>
        {
            o.AllowedHosts = ["localhost"];
            o.AllowDangerousAddresses = true; // the receiver listens on loopback
            o.Secrets["erp-main"] = "s3cret";
            o.Secrets["erp-old"] = "old-secret";
            o.CaBundles["test-ca"] = pki.CaBundlePath;
            o.ClientCertificates["erp-client"] = pki.WriteClientPem("keel-client");
            configure?.Invoke(o);
        });
        await env.SaveProfileAsync(IntegrationEnv.Profile("erp"));
        return new Rig { Env = env, Pki = pki, Receiver = receiver };
    }

    private static async Task<string> ReleaseItarPartAsync(ConfigEnv c, string name = "Bracket") => await c.ReleasedComponentAsync(name, ConfigEnv.Itar());

    private static DomainEvent Event(string aggregate, Markings? markings = null, string type = EventTypes.PartReleased, string? marker = null) => new(type, "part", Guid.NewGuid(), aggregate, "A",
        markings ?? ConfigEnv.Open, new JsonObject { ["partType"] = "Component", ["marker"] = marker ?? aggregate }, DateTimeOffset.UtcNow, "alice");

    private static async Task AppendAsync(IntegrationEnv env, DomainEvent e)
    {
        await using var conn = await env.Ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await env.Sink.AppendAsync(conn, tx, e, CancellationToken.None);
        await tx.CommitAsync();
    }

    [Fact]
    public async Task A_released_part_reaches_the_endpoint_signed_and_valid_and_a_duplicate_is_deduplicated_by_event_id()
    {
        await using var rig = await StartAsync();
        await rig.Webhook();
        var part = await ReleaseItarPartAsync(rig.Env.Config);
        await rig.Env.DrainAsync();

        var received = Assert.Single(rig.Receiver.Events);
        Assert.True(received.SignatureValid);
        Assert.Equal("part.released", received.EventType);
        var envelope = JsonNode.Parse(received.Body)!;
        IntegrationEnv.AssertEnvelopeValid(envelope);
        Assert.Equal(part, envelope["aggregate"]!["number"]!.GetValue<string>());
        Assert.StartsWith("CUI//EXPT", envelope["markings"]!["banner"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("1", received.Attempt);
        var delivery = Assert.Single(await rig.Env.DeliveriesAsync());
        Assert.Equal("delivered", delivery.Status);
        Assert.Equal(1, delivery.Attempts);

        // At-least-once: the same event may arrive twice (a crash after the response, a requeue); the consumer recognises the event id.
        await rig.Env.ScalarAsync("UPDATE integration.delivery SET status = 'pending', next_attempt_at = now()");
        await rig.Env.DrainAsync();
        Assert.Single(rig.Receiver.Events);
        Assert.Equal(1, rig.Receiver.Duplicates);
        Assert.Equal(2, rig.Receiver.Requests.Count);
        Assert.Equal(rig.Receiver.Requests.First().Body, rig.Receiver.Requests.Last().Body); // identical bytes for every attempt
    }

    [Fact]
    public async Task Server_errors_are_retried_with_backoff_until_delivered_and_the_attempts_are_counted()
    {
        await using var rig = await StartAsync();
        await rig.Webhook();
        var calls = 0;
        rig.Receiver.Behavior = _ => Task.FromResult(Interlocked.Increment(ref calls) <= 2 ? 503 : 204);
        await ReleaseItarPartAsync(rig.Env.Config);
        await rig.Env.DrainAsync();
        var d = Assert.Single(await rig.Env.DeliveriesAsync());
        Assert.True(d.Status == "delivered", d.Status + ": " + d.LastError);
        Assert.Equal(3, d.Attempts);
        Assert.Equal(3, rig.Receiver.Requests.Count);
        Assert.Equal(["1", "2", "3"], rig.Receiver.Requests.Select(r => r.Attempt));
    }

    [Fact]
    public async Task A_delivery_that_keeps_failing_becomes_a_dead_letter_after_the_configured_attempts_and_can_be_requeued()
    {
        await using var rig = await StartAsync(o => o.MaxAttempts = 3);
        await rig.Webhook();
        var fail = true;
        rig.Receiver.Behavior = _ => Task.FromResult(fail ? 500 : 200);
        await ReleaseItarPartAsync(rig.Env.Config);
        await rig.Env.DrainAsync();
        var dead = Assert.Single(await rig.Env.DeliveriesAsync("dead"));
        Assert.Equal(3, dead.Attempts);
        Assert.Contains("HTTP 500", dead.LastError, StringComparison.Ordinal);
        Assert.Contains(await rig.Env.Config.AuditAsync(), a => a.Action == "integration.delivery.dead" && a.Outcome == AuditOutcome.Failure);

        // An administrator requeues it after the endpoint is fixed; attempts start again and the event is delivered.
        fail = false;
        var requeued = await rig.Env.Admin.RequeueDeadAsync(IntegrationEnv.Ada, dead.Id, null, "corr");
        Assert.Equal(1, requeued.Value);
        await rig.Env.DrainAsync();
        Assert.Equal("delivered", Assert.Single(await rig.Env.DeliveriesAsync()).Status);
        Assert.Contains(await rig.Env.Config.AuditAsync(), a => a.Action == "integration.delivery.requeue");
        Assert.Equal(OutcomeKind.Forbidden, (await rig.Env.Admin.RequeueDeadAsync(ConfigEnv.Alice, dead.Id, null, null)).Kind);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(422)]
    public async Task Client_errors_other_than_408_and_429_are_permanent_and_dead_letter_at_once(int status)
    {
        await using var rig = await StartAsync();
        await rig.Webhook();
        rig.Receiver.Behavior = _ => Task.FromResult(status);
        await ReleaseItarPartAsync(rig.Env.Config);
        await rig.Env.DrainAsync();
        var d = Assert.Single(await rig.Env.DeliveriesAsync("dead"));
        Assert.Equal(1, d.Attempts);
        Assert.Single(rig.Receiver.Requests);
    }

    [Fact]
    public async Task Rate_limiting_and_timeouts_responses_429_and_408_are_retried()
    {
        await using var rig = await StartAsync();
        await rig.Webhook();
        var calls = 0;
        rig.Receiver.Behavior = _ => Task.FromResult(Interlocked.Increment(ref calls) switch { 1 => 429, 2 => 408, _ => 200 });
        await ReleaseItarPartAsync(rig.Env.Config);
        await rig.Env.DrainAsync();
        Assert.Equal(3, Assert.Single(await rig.Env.DeliveriesAsync("delivered")).Attempts);
    }

    [Fact]
    public async Task Redirects_are_never_followed()
    {
        await using var rig = await StartAsync();
        await rig.Webhook();
        rig.Receiver.Behavior = _ => Task.FromResult(302);
        await ReleaseItarPartAsync(rig.Env.Config);
        await rig.Env.DrainAsync();
        var d = Assert.Single(await rig.Env.DeliveriesAsync("dead"));
        Assert.Contains("redirect", d.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Single(rig.Receiver.Requests); // the Location was not requested
    }

    [Fact]
    public async Task A_request_that_exceeds_the_timeout_is_a_retryable_failure()
    {
        await using var rig = await StartAsync(o => o.MaxAttempts = 2);
        await rig.Webhook(tweak: c => c["timeoutSeconds"] = 1);
        rig.Receiver.Behavior = async _ => { await Task.Delay(2500); return 204; };
        await ReleaseItarPartAsync(rig.Env.Config);
        await rig.Env.DrainAsync(TimeSpan.FromSeconds(30));
        var d = Assert.Single(await rig.Env.DeliveriesAsync());
        Assert.Equal("dead", d.Status); // out of attempts, not a permanent error: it was retried first
        Assert.Equal(2, d.Attempts);
        Assert.Contains("timeout", d.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sinks_connecting_to_loopback_are_refused_by_the_ssrf_rules_unless_the_development_switch_is_on()
    {
        await using var rig = await StartAsync(o => o.AllowDangerousAddresses = false);
        // The configuration is refused outright... the URL's host is allow-listed by name, the resolved address is the problem.
        await rig.Webhook();
        await ReleaseItarPartAsync(rig.Env.Config);
        await rig.Env.DrainAsync();
        var d = Assert.Single(await rig.Env.DeliveriesAsync("dead"));
        Assert.Contains("SSRF", d.LastError, StringComparison.Ordinal);
        Assert.Empty(rig.Receiver.Requests);
    }

    [Fact]
    public async Task A_sink_url_outside_the_allow_list_is_refused_when_it_is_configured()
    {
        await using var rig = await StartAsync(o => o.AllowedHosts = []);
        var r = await rig.Env.Admin.SaveSinkAsync(IntegrationEnv.Ada, new SinkInput("evil", "Evil", "webhook", true, "erp",
            new JsonObject { ["url"] = "https://metadata.internal:8443/x", ["secretName"] = "erp-main" }), null);
        Assert.Equal(OutcomeKind.Invalid, r.Kind);
        Assert.Contains(r.Errors, e => e.Contains("refused by default", StringComparison.OrdinalIgnoreCase) || e.Contains("allow", StringComparison.OrdinalIgnoreCase));
        var meta = await rig.Env.Admin.SaveSinkAsync(IntegrationEnv.Ada, new SinkInput("meta", "Meta", "webhook", true, "erp",
            new JsonObject { ["url"] = "https://169.254.169.254/latest", ["secretName"] = "erp-main" }), null);
        Assert.Equal(OutcomeKind.Invalid, meta.Kind);
    }

    [Fact]
    public async Task The_endpoints_certificate_must_chain_to_the_configured_ca_bundle()
    {
        await using var rig = await StartAsync(o => o.MaxAttempts = 1);
        using var otherPki = new TestPki();
        rig.Env.Options.CaBundles["wrong-ca"] = otherPki.CaBundlePath;
        await rig.Webhook(tweak: c => c["caBundleName"] = "wrong-ca");
        await ReleaseItarPartAsync(rig.Env.Config);
        await rig.Env.DrainAsync();
        Assert.Equal("dead", Assert.Single(await rig.Env.DeliveriesAsync()).Status);
        Assert.Empty(rig.Receiver.Requests); // no request was sent over an unverified connection
    }

    [Fact]
    public async Task Mutual_tls_presents_the_configured_client_certificate_and_fails_without_it()
    {
        await using var rig = await StartAsync(o => o.MaxAttempts = 1, mtls: true);
        await rig.Webhook(tweak: c => c["clientCertName"] = "erp-client");
        await ReleaseItarPartAsync(rig.Env.Config);
        await rig.Env.DrainAsync();
        Assert.True(rig.Receiver.Events.Count == 1, string.Join("|", (await rig.Env.DeliveriesAsync()).Select(d => d.Status + ":" + d.LastError)));
        var received = Assert.Single(rig.Receiver.Events);
        Assert.Contains("keel-client", received.ClientCertificate, StringComparison.Ordinal);

        await rig.Webhook(name: "no-cert");
        await rig.Env.ScalarAsync("UPDATE integration.sink SET enabled = false WHERE name = 'erp'");
        await ReleaseItarPartAsync(rig.Env.Config, "Second bracket");
        await rig.Env.DrainAsync();
        Assert.Contains(await rig.Env.DeliveriesAsync("dead", "no-cert"), _ => true);
    }

    [Fact]
    public async Task During_secret_rotation_the_signature_header_carries_both_secrets()
    {
        // The receiver still knows only the old secret; the sink signs with the new one and the old one.
        await using var rig = await StartAsync(receiverSecrets: ["old-secret"]);
        await rig.Webhook(tweak: c => c["secondarySecretName"] = "erp-old");
        await ReleaseItarPartAsync(rig.Env.Config);
        await rig.Env.DrainAsync();
        Assert.Single(rig.Receiver.Events);
        Assert.Equal(2, rig.Receiver.Requests.First().SignatureHeader.Split(',').Length);
    }

    [Fact]
    public async Task A_wrong_secret_is_rejected_by_the_receiver_as_unauthorized_and_never_delivers()
    {
        await using var rig = await StartAsync(o => o.MaxAttempts = 1, receiverSecrets: ["other"]);
        await rig.Webhook();
        await ReleaseItarPartAsync(rig.Env.Config);
        await rig.Env.DrainAsync();
        Assert.Empty(rig.Receiver.Events);
        Assert.Equal("dead", Assert.Single(await rig.Env.DeliveriesAsync()).Status); // 401 is permanent
    }

    // ---------------------------------------------------------------- outbound release check

    [Fact]
    public async Task An_event_the_sink_is_not_cleared_for_is_withheld_audited_never_sent_and_only_an_explicit_reevaluation_can_release_it()
    {
        await using var rig = await StartAsync();
        // The profile lacks the ORCA program.
        await rig.Env.SaveProfileAsync(IntegrationEnv.Profile("narrow", programs: []));
        await rig.Webhook("narrow-erp", "narrow");
        var part = await ReleaseItarPartAsync(rig.Env.Config);
        await rig.Env.DrainAsync();

        Assert.Empty(rig.Receiver.Requests);
        var withheld = Assert.Single(await rig.Env.DeliveriesAsync("withheld"));
        Assert.Contains(OutboundRelease.ProgramNotAllowed, withheld.WithheldReasons);
        Assert.Equal(0, withheld.Attempts);
        var audit = (await rig.Env.Config.AuditAsync()).Single(a => a.Action == "integration.delivery.withheld");
        Assert.Equal(AuditOutcome.Denied, audit.Outcome);
        Assert.Contains("program_not_allowed", audit.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(part, audit.Payload, StringComparison.Ordinal); // the audit event does not name the object

        // Changing the profile does not send anything by itself...
        await rig.Env.SaveProfileAsync(IntegrationEnv.Profile("narrow", programs: ["ORCA"]));
        await rig.Env.DrainAsync();
        Assert.Empty(rig.Receiver.Requests);
        Assert.Single(await rig.Env.DeliveriesAsync("withheld"));

        // ...an administrator re-evaluates it explicitly (audited), and then it is delivered.
        Assert.Equal(OutcomeKind.Forbidden, (await rig.Env.Admin.ReevaluateWithheldAsync(ConfigEnv.Alice, null, "narrow-erp", null)).Kind);
        Assert.Equal(1, (await rig.Env.Admin.ReevaluateWithheldAsync(IntegrationEnv.Ada, null, "narrow-erp", "corr")).Value);
        await rig.Env.DrainAsync();
        Assert.Single(rig.Receiver.Events);
        Assert.Equal("delivered", Assert.Single(await rig.Env.DeliveriesAsync()).Status);
        Assert.Contains(await rig.Env.Config.AuditAsync(), a => a.Action == "integration.delivery.reevaluate" && a.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task Re_evaluation_with_a_profile_that_still_does_not_clear_the_event_withholds_it_again()
    {
        await using var rig = await StartAsync();
        await rig.Env.SaveProfileAsync(IntegrationEnv.Profile("narrow", programs: []));
        await rig.Webhook("narrow-erp", "narrow");
        await ReleaseItarPartAsync(rig.Env.Config);
        await rig.Env.DrainAsync();
        await rig.Env.Admin.ReevaluateWithheldAsync(IntegrationEnv.Ada, null, "narrow-erp", null);
        await rig.Env.DrainAsync();
        Assert.Single(await rig.Env.DeliveriesAsync("withheld"));
        Assert.Equal(2, (await rig.Env.Config.AuditAsync()).Count(a => a.Action == "integration.delivery.withheld"));
        Assert.Empty(rig.Receiver.Requests);
    }

    [Fact]
    public async Task Property_no_event_ever_reaches_a_sink_whose_profile_does_not_clear_its_markings()
    {
        var env = await IntegrationEnv.CreateAsync(pg);
        var profiles = new[]
        {
            IntegrationEnv.Profile("p-dod", "Dod"),
            IntegrationEnv.Profile("p-contractor", "DodContractor", statements: ["A", "C", "D"]),
            IntegrationEnv.Profile("p-usg", "Usg", statements: ["A", "B", "C"], programs: []),
            IntegrationEnv.Profile("p-foreign", "Foreign", usOnly: false, auths: ["TAA-0042"], statements: ["A", "B", "C", "D", "E"]),
            IntegrationEnv.Profile("p-public", "Public", usOnly: false, jurisdictions: ["None"], cui: [], programs: [], statements: ["A"]),
        };
        foreach (var p in profiles)
        {
            await env.SaveProfileAsync(p);
            await env.SaveFileSinkAsync("drop-" + p.Name[2..], p.Name);
        }

        var rnd = new Random(31000);
        var statements = Enum.GetValues<DistributionStatement>();
        var expected = new Dictionary<(string Sink, Guid Event), bool>();
        var aggregates = new Dictionary<Guid, string>();
        for (var i = 0; i < 40; i++)
        {
            var jur = (ExportJurisdiction)rnd.Next(3);
            var stmt = statements[rnd.Next(statements.Length)];
            var eccn = jur == ExportJurisdiction.Ear ? (rnd.Next(2) == 0 ? "EAR99" : "9A610") : null;
            var controlled = jur == ExportJurisdiction.Itar || (jur == ExportJurisdiction.Ear && eccn != "EAR99");
            var cui = controlled ? new[] { "EXPT" } : rnd.Next(2) == 0 ? ["CTI"] : [];
            var program = stmt == DistributionStatement.F ? "ORCA" : rnd.Next(3) == 0 ? "ORCA" : null;
            var markings = Markings.Create(jur, stmt, "ENG", eccn, jur == ExportJurisdiction.Itar ? "XI(d)" : null, cui, stmt == DistributionStatement.A ? null : "ACME",
                stmt == DistributionStatement.A ? null : "reason", program, jur != ExportJurisdiction.None && rnd.Next(3) == 0 ? ["TAA-0042"] : null);
            if (!markings.IsSuccess) { i--; continue; }
            var e = Event($"P-{i:D6}", markings.Value);
            await AppendAsync(env, e);
        }
        var events = await env.EventsAsync();
        Assert.Equal(40, events.Count);
        await env.DrainAsync();

        // Oracle: the pure function, evaluated on the stored markings of every event, for every sink's stored profile.
        var rows = await env.Admin.ListProfilesAsync(IntegrationEnv.Ada);
        foreach (var ev in events)
        {
            var markings = await ReadMarkingsAsync(env, ev.Id);
            foreach (var p in rows.Value!.Where(p => p.Name.StartsWith("p-", StringComparison.Ordinal)))
                expected[("drop-" + p.Name[2..], ev.Id)] = OutboundRelease.Evaluate(p.ToProfile(), markings).Permitted;
        }
        var deliveries = await env.DeliveriesAsync();
        Assert.Equal(events.Count * profiles.Length, deliveries.Count);
        foreach (var d in deliveries)
        {
            var permitted = expected[(d.SinkName, d.EventId)];
            Assert.Equal(permitted ? "delivered" : "withheld", d.Status);
            var written = Directory.Exists(env.DropDirectory(d.SinkName)) && Directory.GetFiles(env.DropDirectory(d.SinkName), $"*{d.EventId:D}*.json").Any(f => !f.EndsWith(".manifest.json", StringComparison.Ordinal));
            Assert.Equal(permitted, written); // a withheld event leaves no file behind
        }
        Assert.Contains(expected.Values, v => v);
        Assert.Contains(expected.Values, v => !v);
        // Statement F is never delivered anywhere.
        foreach (var ev in events)
            if (JsonNode.Parse(ev.Payload) is not null && (await ReadMarkingsAsync(env, ev.Id)).DistributionStatement == DistributionStatement.F)
                Assert.All(deliveries.Where(d => d.EventId == ev.Id), d => Assert.Equal("withheld", d.Status));
    }

    private static async Task<Markings> ReadMarkingsAsync(IntegrationEnv env, Guid id)
    {
        await using var conn = new NpgsqlConnection(env.Config.Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT {MarkingColumns.Select("o")} FROM integration.outbox o WHERE o.id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return MarkingColumns.Read(r, 0);
    }

    // ---------------------------------------------------------------- concurrency, ordering, shutdown

    [Fact]
    public async Task Two_dispatchers_never_deliver_the_same_delivery_twice_and_together_deliver_everything()
    {
        await using var rig = await StartAsync(o => o.BatchSize = 5);
        await rig.Webhook();
        for (var i = 0; i < 40; i++) await AppendAsync(rig.Env, Event($"P-{i:D6}"));
        rig.Receiver.Behavior = async _ => { await Task.Delay(5); return 204; };
        await rig.Env.DrainAsync(TimeSpan.FromSeconds(60), rig.Env.NewDispatcher(), rig.Env.NewDispatcher());
        Assert.Equal(40, rig.Receiver.Events.Count);
        Assert.Equal(0, rig.Receiver.Duplicates);
        Assert.Equal(40, rig.Receiver.Requests.Count); // one request per event: SKIP LOCKED plus the lease kept the two apart
        Assert.All(await rig.Env.DeliveriesAsync(), d => Assert.Equal(("delivered", 1), (d.Status, d.Attempts)));
    }

    [Fact]
    public async Task Events_of_one_aggregate_arrive_in_order_even_when_the_first_attempt_fails()
    {
        await using var rig = await StartAsync();
        await rig.Webhook();
        for (var i = 1; i <= 3; i++) await AppendAsync(rig.Env, Event("P-000042", marker: $"change-{i}"));
        for (var i = 0; i < 5; i++) await AppendAsync(rig.Env, Event($"P-9000{i}"));
        var firstSeen = true;
        rig.Receiver.Behavior = r =>
        {
            var marker = JsonNode.Parse(r.Body)!["data"]!["marker"]!.GetValue<string>();
            if (marker == "change-1" && firstSeen) { firstSeen = false; return Task.FromResult(500); }
            return Task.FromResult(204);
        };
        await rig.Env.DrainAsync(TimeSpan.FromSeconds(30), rig.Env.NewDispatcher(), rig.Env.NewDispatcher());
        var order = rig.Receiver.Requests.Where(r => r.SignatureValid).Select(r => JsonNode.Parse(r.Body)!["data"]!["marker"]!.GetValue<string>()).Where(m => m.StartsWith("change-", StringComparison.Ordinal)).ToList();
        // change-1 fails once, so it appears twice; change-2 and change-3 only after its success, in sequence.
        Assert.Equal(["change-1", "change-1", "change-2", "change-3"], order);
        Assert.Equal(8, rig.Receiver.Events.Count);
    }

    [Fact]
    public async Task Graceful_shutdown_gives_the_delivery_back_without_counting_an_attempt()
    {
        await using var rig = await StartAsync();
        await rig.Webhook();
        await AppendAsync(rig.Env, Event("P-000001"));
        rig.Receiver.Behavior = async _ => { await Task.Delay(5000); return 204; };
        using var cts = new CancellationTokenSource();
        var run = rig.Env.NewDispatcher().RunOnceAsync(cts.Token);
        await Task.Delay(700);
        await cts.CancelAsync();
        await run; // returns promptly: the in-flight request is abandoned
        var d = Assert.Single(await rig.Env.DeliveriesAsync());
        Assert.Equal(("pending", 0), (d.Status, d.Attempts));
        Assert.Equal(0, await rig.Env.ScalarAsync("SELECT count(*) FROM integration.delivery WHERE claimed_until IS NOT NULL"));
        // The next dispatcher delivers it.
        rig.Receiver.Behavior = _ => Task.FromResult(204);
        await rig.Env.DrainAsync();
        Assert.Equal("delivered", Assert.Single(await rig.Env.DeliveriesAsync()).Status);
    }

    [Fact]
    public async Task A_crashed_dispatchers_lease_expires_and_another_dispatcher_takes_over()
    {
        await using var rig = await StartAsync();
        await rig.Webhook();
        await AppendAsync(rig.Env, Event("P-000001"));
        var claimed = await rig.Env.Store.ClaimAsync("crashed-instance", 10, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Single(claimed);
        // While the lease holds, nobody else gets it.
        Assert.Empty(await rig.Env.Store.ClaimAsync("other", 10, TimeSpan.FromSeconds(30), CancellationToken.None));
        await rig.Env.ScalarAsync("UPDATE integration.delivery SET claimed_until = now() - interval '1 second'");
        await rig.Env.DrainAsync();
        Assert.Single(rig.Receiver.Events);
    }

    [Fact]
    public async Task A_broker_row_without_a_host_is_a_dead_letter_with_the_reason_and_the_loop_survives()
    {
        await using var rig = await StartAsync();
        await rig.Webhook();
        await AppendAsync(rig.Env, Event("P-000001"));
        // A poison condition: the sink row points at a kind the registry cannot serve. The loop must not stop; the delivery dies with the reason.
        await rig.Env.ScalarAsync("ALTER TABLE integration.sink DROP CONSTRAINT sink_kind_check");
        await rig.Env.ScalarAsync("UPDATE integration.sink SET kind = 'broker'");
        var d = rig.Env.NewDispatcher();
        await d.RunOnceAsync(CancellationToken.None);
        var dead = Assert.Single(await rig.Env.DeliveriesAsync("dead"));
        Assert.Contains("no broker host", dead.LastError, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- file sink and operations

    [Fact]
    public async Task The_file_sink_writes_atomic_files_with_manifests_that_match_their_content()
    {
        var env = await IntegrationEnv.CreateAsync(pg);
        await env.SaveProfileAsync(IntegrationEnv.Profile("erp"));
        await env.SaveFileSinkAsync("batch-erp", "erp", "ndjson");
        await env.SaveFileSinkAsync("single-erp", "erp", "json");
        var part = await ReleaseItarPartAsync(env.Config);
        await env.DrainAsync();
        foreach (var sink in new[] { "batch-erp", "single-erp" })
        {
            var files = Directory.GetFiles(env.DropDirectory(sink));
            Assert.DoesNotContain(files, f => f.EndsWith(".tmp", StringComparison.Ordinal));
            var manifest = Directory.GetFiles(env.DropDirectory(sink), "*.manifest.json").Single();
            var m = JsonNode.Parse(await File.ReadAllTextAsync(manifest))!;
            var data = Path.Combine(env.DropDirectory(sink), m["file"]!.GetValue<string>());
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(data))), m["sha256"]!.GetValue<string>());
            var line = JsonNode.Parse((await File.ReadAllLinesAsync(data))[0])!;
            IntegrationEnv.AssertEnvelopeValid(line);
            Assert.Equal(part, line["aggregate"]!["number"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Health_reports_degraded_when_a_due_delivery_is_waiting_too_long_and_metrics_count_deliveries()
    {
        await using var rig = await StartAsync(o => o.LagDegradedSeconds = 0);
        await rig.Webhook();
        await AppendAsync(rig.Env, Event("P-000001"));
        await Task.Delay(1200);
        var check = new IntegrationHealthCheck(rig.Env.Store, rig.Env.Options);
        var degraded = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, degraded.Status);
        var before = IntegrationMetrics.Deliveries.WithLabels("erp", "delivered").Value;
        await rig.Env.DrainAsync();
        Assert.True(IntegrationMetrics.Deliveries.WithLabels("erp", "delivered").Value >= before + 1);
        var dispatcher = rig.Env.NewDispatcher();
        await dispatcher.RefreshMetricsAsync(CancellationToken.None);
        Assert.Equal(1, IntegrationMetrics.Backlog.WithLabels("delivered").Value);
        var healthy = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy, healthy.Status);
    }
}
