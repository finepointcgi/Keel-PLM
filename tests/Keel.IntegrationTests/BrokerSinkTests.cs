using System.Text;
using System.Text.Json.Nodes;
using Keel.Core;
using Keel.Integration;
using Keel.Security;
using RabbitMQ.Client;

namespace Keel.IntegrationTests;

/// <summary>
/// The message-broker sink against a REAL RabbitMQ (ADR-0055): publisher confirms, the idempotency key, retry and dead letters through the outbox, an outage and recovery, TLS and
/// mutual TLS with a throwaway CA, SASL EXTERNAL, bad credentials, export-control gating (a withheld event never reaches the broker) and the SSRF rules. The tests skip, visibly,
/// when no broker container can be started on the machine.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BrokerSinkTests(PostgresFixture pg, RabbitMqFixture rmq) : IClassFixture<RabbitMqFixture>
{
    private sealed record Names(string Exchange, string Queue)
    {
        public static Names New() => new("x-" + Guid.NewGuid().ToString("N")[..12], "q-" + Guid.NewGuid().ToString("N")[..12]);
    }

    private IntegrationEnv _env = null!;

    private async Task<IntegrationEnv> StartAsync(Action<IntegrationOptions>? configure = null)
    {
        Assert.SkipWhen(rmq.Unavailable is not null, rmq.Unavailable ?? "");
        _env = await IntegrationEnv.CreateAsync(pg, configure: o =>
        {
            o.AllowedHosts = ["127.0.0.1", "localhost"];
            o.AllowDangerousAddresses = true; // the broker listens on loopback
            o.MaxAttempts = 4;
            o.Secrets["bus-user"] = RabbitMqFixture.User;
            o.Secrets["bus-pass"] = rmq.Password;
            o.Secrets["bus-wrong"] = "not-the-password";
            o.Secrets["bus-sign"] = "signing-secret";
            o.CaBundles["bus-ca"] = rmq.CaFile;
            o.ClientCertificates["bus-client"] = rmq.ClientPem;
            configure?.Invoke(o);
        });
        await _env.SaveProfileAsync(IntegrationEnv.Profile("erp"));
        return _env;
    }

    private async Task<IConnection> ConnectAsync() => await new ConnectionFactory { HostName = "127.0.0.1", Port = rmq.AmqpPort, UserName = RabbitMqFixture.User, Password = rmq.Password }.CreateConnectionAsync();

    /// <summary>Declares a durable topic exchange and a queue bound to it with <paramref name="binding"/> (none: the queue exists but nothing routes to it).</summary>
    private async Task<Names> DeclareAsync(string? binding = "#", bool exchange = true)
    {
        var n = Names.New();
        await using var conn = await ConnectAsync();
        await using var ch = await conn.CreateChannelAsync();
        if (exchange) await ch.ExchangeDeclareAsync(n.Exchange, ExchangeType.Topic, durable: true);
        await ch.QueueDeclareAsync(n.Queue, durable: true, exclusive: false, autoDelete: false);
        if (binding is not null && exchange) await ch.QueueBindAsync(n.Queue, n.Exchange, binding);
        return n;
    }

    private async Task BindAsync(Names n, string binding)
    {
        await using var conn = await ConnectAsync();
        await using var ch = await conn.CreateChannelAsync();
        await ch.QueueBindAsync(n.Queue, n.Exchange, binding);
    }

    private async Task<List<BasicGetResult>> DrainQueueAsync(string queue, int expected, TimeSpan? wait = null)
    {
        await using var conn = await ConnectAsync();
        await using var ch = await conn.CreateChannelAsync();
        var list = new List<BasicGetResult>();
        var until = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < until && list.Count < expected)
        {
            var m = await ch.BasicGetAsync(queue, autoAck: true);
            if (m is null) await Task.Delay(100); else list.Add(m);
        }
        return list;
    }

    private async Task<uint> CountAsync(string queue)
    {
        await using var conn = await ConnectAsync();
        await using var ch = await conn.CreateChannelAsync();
        return (await ch.QueueDeclarePassiveAsync(queue)).MessageCount;
    }

    private static string Header(IReadOnlyBasicProperties p, string key) => p.Headers is { } h && h.TryGetValue(key, out var v) ? v switch { byte[] b => Encoding.UTF8.GetString(b), _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "" } : "";

    private JsonObject Config(Names n, Action<JsonObject>? tweak = null)
    {
        var c = new JsonObject { ["host"] = "127.0.0.1", ["port"] = rmq.AmqpPort, ["exchange"] = n.Exchange, ["tls"] = "none", ["acknowledgeInsecureTransport"] = true,
            ["usernameSecretName"] = "bus-user", ["passwordSecretName"] = "bus-pass", ["timeoutSeconds"] = 10 };
        tweak?.Invoke(c);
        return c;
    }

    private JsonObject TlsConfig(Names n, Action<JsonObject>? tweak = null)
    {
        var c = new JsonObject { ["host"] = "localhost", ["port"] = rmq.TlsPort, ["exchange"] = n.Exchange, ["usernameSecretName"] = "bus-user", ["passwordSecretName"] = "bus-pass",
            ["caBundleName"] = "bus-ca", ["timeoutSeconds"] = 10 };
        tweak?.Invoke(c);
        return c;
    }

    private static DomainEvent Event(string aggregate, Markings? markings = null, string type = EventTypes.PartReleased) => new(type, "part", Guid.NewGuid(), aggregate, "A",
        markings ?? ConfigEnv.Open, new JsonObject { ["partType"] = "Component", ["marker"] = aggregate }, DateTimeOffset.UtcNow, "alice");

    private static async Task AppendAsync(IntegrationEnv env, DomainEvent e)
    {
        await using var conn = await env.Ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await env.Sink.AppendAsync(conn, tx, e, CancellationToken.None);
        await tx.CommitAsync();
    }

    // ---------------------------------------------------------------- delivery

    [Fact]
    public async Task An_event_is_published_persistent_with_the_event_id_as_message_id_and_idempotency_key_and_a_valid_signature()
    {
        var env = await StartAsync();
        var n = await DeclareAsync("part.#");
        await env.SaveSinkAsync("bus", "broker", "erp", Config(n, c => c["signingSecretName"] = "bus-sign"));
        var part = await env.Config.ReleasedComponentAsync("Bracket", ConfigEnv.Itar()); // a real release: the payload must match the published schema
        await env.DrainAsync();

        var delivery = Assert.Single(await env.DeliveriesAsync());
        Assert.Equal("delivered", delivery.Status);
        var message = Assert.Single(await DrainQueueAsync(n.Queue, 1));
        var p = message.BasicProperties;
        Assert.Equal(delivery.EventId.ToString("D"), p.MessageId);
        Assert.Equal(delivery.EventId.ToString("D"), Header(p, BrokerSink.IdempotencyHeader));
        Assert.Equal("part.released", p.Type);
        Assert.Equal("part.released", Header(p, BrokerSink.EventTypeHeader)); // the routing key is the event type: the topic binding "part.#" matched
        Assert.Equal(DeliveryModes.Persistent, p.DeliveryMode);
        Assert.Equal("application/json", p.ContentType);
        Assert.Equal("1", Header(p, BrokerSink.AttemptHeader));
        var envelope = JsonNode.Parse(message.Body.ToArray())!;
        IntegrationEnv.AssertEnvelopeValid(envelope);
        Assert.Equal(delivery.EventId.ToString("D"), envelope["id"]!.GetValue<string>());
        Assert.Equal(part, envelope["aggregate"]!["number"]!.GetValue<string>());
        Assert.StartsWith("CUI//EXPT", envelope["markings"]!["banner"]!.GetValue<string>(), StringComparison.Ordinal);

        var timestamp = long.Parse(Header(p, BrokerSink.TimestampHeader), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(WebhookSignature.Header(["signing-secret"], timestamp, message.Body.ToArray()), Header(p, BrokerSink.SignatureHeader));
        // Secrets are referenced, never stored: the sink row and the audit event hold names only.
        Assert.DoesNotContain(rmq.Password, await env.TextAsync("SELECT string_agg(config::text, ' ') FROM integration.sink"), StringComparison.Ordinal);
        Assert.DoesNotContain(rmq.Password, string.Join(' ', (await env.Config.AuditAsync()).Select(a => a.Payload)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fixed_routing_key_and_the_default_exchange_deliver_to_a_named_queue()
    {
        var env = await StartAsync();
        var n = await DeclareAsync(binding: null, exchange: false);
        await env.SaveSinkAsync("direct", "broker", "erp", Config(n, c => { c["exchange"] = ""; c["routingKey"] = n.Queue; }));
        await AppendAsync(env, Event("P-000001"));
        await env.DrainAsync();
        Assert.Equal("delivered", Assert.Single(await env.DeliveriesAsync()).Status);
        Assert.Single(await DrainQueueAsync(n.Queue, 1));

        var topic = await DeclareAsync("erp.events");
        await env.SaveSinkAsync("fixed", "broker", "erp", Config(topic, c => c["routingKey"] = "erp.events"), types: ["part.released"]);
        await AppendAsync(env, Event("P-000002"));
        await env.DrainAsync();
        Assert.Single(await DrainQueueAsync(topic.Queue, 1));
    }

    [Fact]
    public async Task A_missing_exchange_can_be_created_by_the_sink_when_the_administrator_asks_for_it()
    {
        var env = await StartAsync();
        var n = Names.New();
        await using (var conn = await ConnectAsync())
        await using (var ch = await conn.CreateChannelAsync())
            await ch.QueueDeclareAsync(n.Queue, durable: true, exclusive: false, autoDelete: false);
        await env.SaveSinkAsync("make", "broker", "erp", Config(n, c => c["declareExchange"] = true));
        await AppendAsync(env, Event("P-000003"));
        await env.NewDispatcher().RunOnceAsync(CancellationToken.None);
        // The exchange exists now (otherwise the broker would answer NOT_FOUND); nothing is bound to it yet, so the message waits as unroutable.
        var failed = Assert.Single(await env.DeliveriesAsync("failed"));
        Assert.Contains("could not route", failed.LastError, StringComparison.Ordinal);
        await BindAsync(n, "#");
        await env.DrainAsync();
        Assert.Single(await DrainQueueAsync(n.Queue, 1));
    }

    [Fact]
    public async Task An_unroutable_message_is_retried_never_lost_and_arrives_once_a_queue_is_bound_with_the_same_message_id()
    {
        var env = await StartAsync();
        var n = await DeclareAsync(binding: null); // exchange and queue exist, nothing routes between them
        await env.SaveSinkAsync("bus", "broker", "erp", Config(n));
        await AppendAsync(env, Event("P-000004"));
        var dispatcher = env.NewDispatcher();
        await dispatcher.RunOnceAsync(CancellationToken.None);
        var failed = Assert.Single(await env.DeliveriesAsync("failed"));
        Assert.Equal(1, failed.Attempts);
        Assert.Contains("could not route", failed.LastError, StringComparison.Ordinal);
        Assert.Equal(0u, await CountAsync(n.Queue));

        await BindAsync(n, "#");
        await env.DrainAsync();
        var delivered = Assert.Single(await env.DeliveriesAsync());
        Assert.Equal("delivered", delivered.Status);
        Assert.True(delivered.Attempts >= 2);
        var message = Assert.Single(await DrainQueueAsync(n.Queue, 1));
        Assert.Equal(delivered.EventId.ToString("D"), message.BasicProperties.MessageId);
        Assert.Equal(delivered.Attempts.ToString(System.Globalization.CultureInfo.InvariantCulture), Header(message.BasicProperties, BrokerSink.AttemptHeader));
    }

    [Fact]
    public async Task A_delivery_that_keeps_failing_is_a_dead_letter_and_a_requeue_delivers_the_same_event_id()
    {
        var env = await StartAsync(o => o.MaxAttempts = 2);
        var n = await DeclareAsync(binding: null);
        await env.SaveSinkAsync("bus", "broker", "erp", Config(n));
        await AppendAsync(env, Event("P-000005"));
        await env.DrainAsync();
        var dead = Assert.Single(await env.DeliveriesAsync("dead"));
        Assert.Equal(2, dead.Attempts);
        Assert.Contains(await env.Config.AuditAsync(), a => a.Action == "integration.delivery.dead");

        await BindAsync(n, "#");
        Assert.Equal(1, (await env.Admin.RequeueDeadAsync(IntegrationEnv.Ada, dead.Id, null, "corr")).Value);
        await env.DrainAsync();
        var message = Assert.Single(await DrainQueueAsync(n.Queue, 1));
        Assert.Equal(dead.EventId.ToString("D"), message.BasicProperties.MessageId); // the idempotency key survives a requeue
        // A second publish of the same event (a crash after the confirm, a requeue by hand) carries the same key: the consumer deduplicates on it.
        await env.ScalarAsync("UPDATE integration.delivery SET status = 'pending', next_attempt_at = now()");
        await env.DrainAsync();
        var duplicate = Assert.Single(await DrainQueueAsync(n.Queue, 1));
        Assert.Equal(dead.EventId.ToString("D"), duplicate.BasicProperties.MessageId);
        Assert.Equal(message.Body.ToArray(), duplicate.Body.ToArray()); // identical bytes for every attempt
    }

    [Fact]
    public async Task While_the_broker_is_down_deliveries_wait_in_the_outbox_and_are_delivered_when_it_returns()
    {
        var env = await StartAsync(o => o.MaxAttempts = 50);
        var n = await DeclareAsync("#");
        await env.SaveSinkAsync("bus", "broker", "erp", Config(n, c => c["timeoutSeconds"] = 5));
        await AppendAsync(env, Event("P-000006"));
        await env.DrainAsync();
        Assert.Single(await DrainQueueAsync(n.Queue, 1)); // the link is up and cached

        await rmq.StopBrokerAsync();
        try
        {
            await AppendAsync(env, Event("P-000007"));
            var dispatcher = env.NewDispatcher();
            await dispatcher.RunOnceAsync(CancellationToken.None);
            var waiting = Assert.Single(await env.DeliveriesAsync("failed"));
            Assert.True(waiting.Attempts >= 1);
            Assert.NotNull(waiting.LastError);
            Assert.DoesNotContain(rmq.Password, waiting.LastError, StringComparison.Ordinal);
        }
        finally
        {
            await rmq.StartBrokerAsync();
        }
        await env.DrainAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(["delivered", "delivered"], (await env.DeliveriesAsync()).Select(d => d.Status));
        var message = Assert.Single(await DrainQueueAsync(n.Queue, 1, TimeSpan.FromSeconds(15)));
        Assert.Equal("P-000007", JsonNode.Parse(message.Body.ToArray())!["aggregate"]!["number"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- credentials and TLS

    [Fact]
    public async Task Wrong_credentials_are_retried_with_a_clear_message_that_never_contains_the_secret()
    {
        var env = await StartAsync();
        var n = await DeclareAsync("#");
        await env.SaveSinkAsync("bus", "broker", "erp", Config(n, c => c["passwordSecretName"] = "bus-wrong"));
        await AppendAsync(env, Event("P-000008"));
        await env.NewDispatcher().RunOnceAsync(CancellationToken.None);
        var failed = Assert.Single(await env.DeliveriesAsync("failed"));
        Assert.Contains("refused the credentials", failed.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-password", failed.LastError, StringComparison.Ordinal);
        Assert.Equal(0u, await CountAsync(n.Queue));
    }

    [Fact]
    public async Task TLS_is_verified_against_the_ca_bundle_and_a_broker_certificate_that_does_not_chain_is_refused()
    {
        var env = await StartAsync();
        var n = await DeclareAsync("#");
        await env.SaveSinkAsync("tls", "broker", "erp", TlsConfig(n));
        await AppendAsync(env, Event("P-000009"));
        await env.DrainAsync();
        Assert.Equal("delivered", Assert.Single(await env.DeliveriesAsync()).Status);
        Assert.Single(await DrainQueueAsync(n.Queue, 1));

        // A bundle of another CA: the handshake fails and the event stays in the outbox.
        using var other = new Keel.TestSupport.SyslogTestPki("Some other CA");
        var otherFile = Path.Combine(rmq.WorkDir, "other-ca.pem");
        await File.WriteAllTextAsync(otherFile, other.CaPem);
        env.Options.CaBundles["bus-other-ca"] = otherFile;
        await env.SaveSinkAsync("tls", "broker", "erp", TlsConfig(n, c => c["caBundleName"] = "bus-other-ca"));
        await AppendAsync(env, Event("P-000010"));
        await env.NewDispatcher().RunOnceAsync(CancellationToken.None);
        var failed = Assert.Single(await env.DeliveriesAsync("failed"));
        Assert.Contains("TLS", failed.LastError, StringComparison.Ordinal);
        Assert.Equal(0u, await CountAsync(n.Queue));
    }

    [Fact]
    public async Task Mutual_TLS_with_a_client_certificate_works_with_a_password_and_with_SASL_EXTERNAL()
    {
        var env = await StartAsync();
        var n = await DeclareAsync("#");
        await env.SaveSinkAsync("mtls", "broker", "erp", TlsConfig(n, c => c["clientCertName"] = "bus-client"));
        await env.SaveSinkAsync("external", "broker", "erp", TlsConfig(n, c =>
        {
            c["clientCertName"] = "bus-client";
            c.Remove("usernameSecretName");
            c.Remove("passwordSecretName");
        }));
        await AppendAsync(env, Event("P-000011"));
        await env.DrainAsync();
        Assert.All(await env.DeliveriesAsync(), d => Assert.True(d.Status == "delivered", d.Status + ": " + d.LastError));
        Assert.Equal(2, (await DrainQueueAsync(n.Queue, 2)).Count);
    }

    // ---------------------------------------------------------------- export control and SSRF

    [Fact]
    public async Task An_event_the_broker_sink_is_not_cleared_for_is_withheld_and_never_reaches_the_broker()
    {
        var env = await StartAsync();
        var n = await DeclareAsync("#");
        await env.SaveProfileAsync(IntegrationEnv.Profile("narrow", programs: [])); // the profile lacks program ORCA
        await env.SaveSinkAsync("narrow-bus", "broker", "narrow", Config(n));
        await AppendAsync(env, Event("P-000012", ConfigEnv.Itar()));
        await env.DrainAsync();
        var withheld = Assert.Single(await env.DeliveriesAsync("withheld"));
        Assert.Contains(OutboundRelease.ProgramNotAllowed, withheld.WithheldReasons);
        Assert.Equal(0, withheld.Attempts);
        Assert.Equal(0u, await CountAsync(n.Queue)); // oracle on the broker: nothing was published

        // Only an explicit re-evaluation after the profile changed lets it go.
        await env.SaveProfileAsync(IntegrationEnv.Profile("narrow", programs: ["ORCA"]));
        await env.DrainAsync();
        Assert.Equal(0u, await CountAsync(n.Queue));
        await env.Admin.ReevaluateWithheldAsync(IntegrationEnv.Ada, null, "narrow-bus", "corp");
        await env.DrainAsync();
        var message = Assert.Single(await DrainQueueAsync(n.Queue, 1));
        Assert.StartsWith("CUI//EXPT", JsonNode.Parse(message.Body.ToArray())!["markings"]!["banner"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_ssrf_rules_are_checked_again_when_connecting_so_a_forbidden_address_is_a_permanent_failure()
    {
        var env = await StartAsync();
        var n = await DeclareAsync("#");
        await env.SaveSinkAsync("bus", "broker", "erp", Config(n));
        env.Options.AllowDangerousAddresses = false; // the switch is turned off after the sink was configured (or the name was re-pointed)
        await AppendAsync(env, Event("P-000013"));
        await env.NewDispatcher().RunOnceAsync(CancellationToken.None);
        var dead = Assert.Single(await env.DeliveriesAsync("dead"));
        Assert.Contains("SSRF", dead.LastError, StringComparison.Ordinal);
        Assert.Equal(0u, await CountAsync(n.Queue));
    }
}
