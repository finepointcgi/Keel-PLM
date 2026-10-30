using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Keel.Integration;

/// <summary>
/// The message broker sink (ADR-0055): publishes the signed envelope of an outbox event to an on-premises AMQP 0-9-1 broker (RabbitMQ and compatible).
/// <list type="bullet">
/// <item><b>At least once.</b> Every publish waits for the broker's publisher confirm; a nack, an unroutable return (the message is published mandatory, so a missing binding is not
/// silently lost), a timeout or a dropped connection is a retry. The outbox retries with backoff and ends in a dead letter exactly as for the other sinks. Messages are persistent.</item>
/// <item><b>Idempotent for the consumer.</b> The AMQP <c>message-id</c> and the header <c>x-keel-idempotency-key</c> are the event id, stable over every retry and over a requeue
/// of a dead letter, so a consumer that deduplicates on it sees each event once. The event id is also in the body (<c>id</c>).</item>
/// <item><b>Secrets by reference.</b> The configuration holds names only; user name, password and the optional signing secret come from <c>Keel:Integration:Secrets</c> (environment or
/// secrets directory), the client certificate and CA bundle from <c>Keel:Integration:ClientCertificates</c> and <c>CaBundles</c>. Nothing secret is stored or audited.</item>
/// <item><b>TLS.</b> TLS 1.2 or 1.3 with the broker certificate verified against the CA bundle (or the system store) and no revocation lookups (air-gapped). Plain AMQP is refused
/// unless the administrator acknowledges it. Mutual TLS: a client certificate, optionally with SASL EXTERNAL instead of a user name.</item>
/// <item><b>On-premises only.</b> The host passes the same allow-list rules as webhook hosts; the connection goes to the address that passed the check (the name is only used
/// for certificate validation), so a name cannot be re-pointed between the check and the connect.</item>
/// </list>
/// Export control is not decided here: the dispatcher evaluates the sink's clearance profile before any sink is called, so an event the profile does not clear is withheld
/// and never reaches the broker. One connection and channel per sink revision is kept and rebuilt after any failure.
/// </summary>
public sealed partial class BrokerSink(IntegrationOptions options, SecretResolver secrets, SsrfGuard guard, TimeProvider clock) : IEventSink, IAsyncDisposable, IDisposable
{
    public const string Protocol = "amqp091";
    public const string IdempotencyHeader = "x-keel-idempotency-key";
    public const string EventTypeHeader = "x-keel-event-type";
    public const string SequenceHeader = "x-keel-seq";
    public const string AttemptHeader = "x-keel-delivery-attempt";
    public const string TimestampHeader = "x-keel-timestamp";
    public const string SignatureHeader = "x-keel-signature";

    private static readonly string[] Known =
    [
        "protocol", "host", "port", "virtualHost", "exchange", "routingKey", "declareExchange", "tls", "acknowledgeInsecureTransport", "caBundleName", "clientCertName",
        "usernameSecretName", "passwordSecretName", "signingSecretName", "timeoutSeconds",
    ];

    private readonly ConcurrentDictionary<(Guid, int), Link> _links = new();

    public string Kind => SinkKinds.Broker;

    [GeneratedRegex("^[A-Za-z0-9_.:\\-]{0,255}$", RegexOptions.None, 1000)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z0-9.\\-:\\[\\]]{1,253}$", RegexOptions.None, 1000)]
    private static partial Regex HostPattern();

    public IReadOnlyList<string> ValidateConfig(JsonObject config, bool enabled)
    {
        var errors = new List<string>();
        foreach (var key in config.Select(p => p.Key))
            if (!Known.Contains(key, StringComparer.Ordinal)) errors.Add($"Unknown setting '{key}'.");
        string? S(string k) => config[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        bool B(string k) => config[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

        var protocol = S("protocol") ?? Protocol;
        if (protocol is "amqp10" or "kafka") errors.Add($"The protocol '{protocol}' is not available: this release delivers AMQP 0-9-1 (protocol \"{Protocol}\", RabbitMQ and compatible brokers).");
        else if (protocol != Protocol) errors.Add($"Unknown protocol '{protocol}'. Use \"{Protocol}\".");

        var tls = S("tls") ?? "required";
        if (tls is not ("required" or "none")) errors.Add("tls must be \"required\" (default) or \"none\".");
        if (tls == "none" && !B("acknowledgeInsecureTransport"))
            errors.Add("tls is \"none\": events would cross the network in clear text. Use tls \"required\", or set acknowledgeInsecureTransport to state that the link to the broker is protected another way.");

        var host = S("host");
        if (host is null) errors.Add("The broker host is required.");
        else if (!HostPattern().IsMatch(host)) errors.Add("host is not a host name or address.");
        else errors.AddRange(guard.CheckHost(host, "broker sinks"));

        if (config["port"] is not null && !(config["port"] is JsonValue p && p.TryGetValue<int>(out var port) && port is >= 1 and <= 65535))
            errors.Add("port must be a whole number from 1 to 65535.");

        var vhost = S("virtualHost");
        if (vhost is not null && (vhost.Length > 255 || vhost.Any(char.IsControl))) errors.Add("virtualHost is not valid.");

        var exchange = S("exchange") ?? "";
        var routingKey = S("routingKey");
        if (!NamePattern().IsMatch(exchange)) errors.Add("exchange may use letters, digits, '_', '-', '.' and ':' (up to 255 characters). Empty means the default exchange.");
        if (routingKey is not null && !NamePattern().IsMatch(routingKey)) errors.Add("routingKey may use letters, digits, '_', '-', '.' and ':' (up to 255 characters; no wildcards).");
        if (exchange.Length == 0 && routingKey is null) errors.Add("With the default exchange the routingKey (the queue name) is required.");
        if (exchange.Length == 0 && B("declareExchange")) errors.Add("declareExchange needs a named exchange.");

        var user = S("usernameSecretName");
        var password = S("passwordSecretName");
        var cert = S("clientCertName");
        if (user is null && password is not null) errors.Add("passwordSecretName needs usernameSecretName.");
        if (user is not null && password is null) errors.Add("usernameSecretName needs passwordSecretName.");
        if (user is null && cert is null) errors.Add("Authentication is required: name a user name and password secret (usernameSecretName, passwordSecretName) or a client certificate (clientCertName) for SASL EXTERNAL.");
        if (user is null && cert is not null && tls == "none") errors.Add("A client certificate needs tls \"required\".");
        foreach (var (key, name) in new[] { ("usernameSecretName", user), ("passwordSecretName", password), ("signingSecretName", S("signingSecretName")) })
        {
            if (name is null) continue;
            if (!SecretResolver.IsValidName(name)) errors.Add($"{key} is not a valid secret name (letters, digits, '_' and '-', up to 41 characters, starting with a letter).");
            else if (enabled && !secrets.SecretExists(name)) errors.Add($"Secret '{name}' is not provisioned: provide Keel__Integration__Secrets__{name} in the secrets directory.");
        }
        if (cert is not null && !(SecretResolver.IsValidName(cert) && (!enabled || secrets.ClientCertificateExists(cert))))
            errors.Add($"Client certificate '{cert}' is not configured (Keel:Integration:ClientCertificates:{cert}).");
        if (S("caBundleName") is { } ca && !(SecretResolver.IsValidName(ca) && (!enabled || secrets.CaBundleExists(ca))))
            errors.Add($"CA bundle '{ca}' is not configured (Keel:Integration:CaBundles:{ca}).");
        if (config["timeoutSeconds"] is not null && !(config["timeoutSeconds"] is JsonValue t && t.TryGetValue<int>(out var seconds) && seconds is >= 1 and <= 120))
            errors.Add("timeoutSeconds must be a whole number from 1 to 120.");
        return errors;
    }

    public async Task<DeliveryOutcome> DeliverAsync(SinkRecord sink, EnvelopeMessage message, CancellationToken ct)
    {
        var host = sink.ConfigString("host");
        if (host is null) return DeliveryOutcome.Permanent("The sink has no broker host.");
        var timeout = TimeSpan.FromSeconds(sink.ConfigInt("timeoutSeconds") ?? options.WebhookTimeoutSeconds);

        string? user = null, password = null, signing = null;
        foreach (var (field, target) in new[] { ("usernameSecretName", 0), ("passwordSecretName", 1), ("signingSecretName", 2) })
        {
            if (sink.ConfigString(field) is not { } name) continue;
            var v = secrets.Secret(name);
            if (v is null) return DeliveryOutcome.Retry($"Secret '{name}' is not provisioned."); // an operator can fix this: retry, do not kill the event
            if (target == 0) user = v; else if (target == 1) password = v; else signing = v;
        }

        var link = _links.GetOrAdd((sink.Id, sink.Revision), _ => new Link());
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        await link.Gate.WaitAsync(ct);
        try
        {
            if (!link.IsOpen)
            {
                var connected = await ConnectAsync(link, sink, host, user, password, linked.Token);
                if (connected is not null) return connected;
            }
            var exchange = sink.ConfigString("exchange") ?? "";
            var routingKey = sink.ConfigString("routingKey") ?? message.EventType;
            var properties = Properties(message, signing);
            await link.Channel!.BasicPublishAsync(exchange, routingKey, mandatory: true, properties, message.Body, linked.Token);
            return DeliveryOutcome.Ok;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            link.Reset();
            throw; // shutdown: the dispatcher releases the lease, the attempt does not count
        }
        catch (OperationCanceledException)
        {
            link.Reset();
            return DeliveryOutcome.Retry("The broker did not confirm within the timeout.");
        }
        catch (PublishException e)
        {
            // The channel stays usable after a return or a nack, but a fresh one is the simplest guarantee that no late confirm is mixed up with the next message.
            link.Reset();
            return DeliveryOutcome.Retry(e.IsReturn
                ? "The broker could not route the message (no queue is bound to the exchange with that routing key); bind a queue or fix exchange and routingKey."
                : "The broker did not accept the message (negative acknowledgement).");
        }
        catch (Exception e) when (e is BrokerUnreachableException or AlreadyClosedException or OperationInterruptedException or IOException or SocketException or AuthenticationException
                                      or TimeoutException or AuthenticationFailureException or PossibleAuthenticationFailureException or ConnectFailureException)
        {
            link.Reset();
            return DeliveryOutcome.Retry(Describe(e));
        }
        finally
        {
            link.Gate.Release();
        }
    }

    private async Task<DeliveryOutcome?> ConnectAsync(Link link, SinkRecord sink, string host, string? user, string? password, CancellationToken ct)
    {
        link.Reset();
        var bare = host.Trim('[', ']');
        var addresses = IPAddress.TryParse(bare, out var literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(bare, ct);
        // Every resolved address must pass, so a name that also resolves somewhere forbidden is refused outright.
        foreach (var a in addresses)
            if (guard.CheckAddress(bare, a) is { } problem) return DeliveryOutcome.Permanent("Refused by the SSRF rules: " + problem);

        var secure = (sink.ConfigString("tls") ?? "required") != "none";
        X509Certificate2Collection? customCa = null;
        X509Certificate2? clientCert = null;
        try
        {
            if (secure)
            {
                if (sink.ConfigString("caBundleName") is { } ca) customCa = secrets.CaBundle(ca);
                if (sink.ConfigString("clientCertName") is { } cc) clientCert = secrets.ClientCertificate(cc);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or CryptographicException or IOException)
        {
            return DeliveryOutcome.Retry("The sink's TLS material could not be loaded: " + e.Message);
        }

        var port = sink.ConfigInt("port") ?? (secure ? 5671 : 5672);
        Exception? last = null;
        foreach (var address in addresses)
        {
            var factory = new ConnectionFactory
            {
                HostName = address.ToString(),
                Port = port,
                VirtualHost = sink.ConfigString("virtualHost") ?? "/",
                ClientProvidedName = "keel-plm",
                AutomaticRecoveryEnabled = false, // the outbox is the retry: a broken link is rebuilt on the next delivery, never silently half-recovered
                TopologyRecoveryEnabled = false,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(Math.Min(10, sink.ConfigInt("timeoutSeconds") ?? options.WebhookTimeoutSeconds)),
                ContinuationTimeout = TimeSpan.FromSeconds(sink.ConfigInt("timeoutSeconds") ?? options.WebhookTimeoutSeconds),
                HandshakeContinuationTimeout = TimeSpan.FromSeconds(sink.ConfigInt("timeoutSeconds") ?? options.WebhookTimeoutSeconds),
            };
            if (user is not null) { factory.UserName = user; factory.Password = password!; }
            else factory.AuthMechanisms = [new ExternalMechanismFactory()];
            if (secure)
            {
                var ssl = new SslOption
                {
                    Enabled = true,
                    ServerName = bare, // the certificate is checked against the name the administrator gave, not the address that was dialled
                    Version = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CheckCertificateRevocation = false, // air-gapped: no OCSP/CRL fetches (no egress)
                    CertificateValidationCallback = (_, cert, chain, errors) => WebhookSink.ValidateServer(cert, chain, errors, customCa),
                };
                if (clientCert is not null) ssl.Certs = [clientCert];
                factory.Ssl = ssl;
            }
            try
            {
                link.Connection = await factory.CreateConnectionAsync(ct);
                link.Channel = await link.Connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
                if (sink.Config["declareExchange"] is JsonValue d && d.TryGetValue<bool>(out var declare) && declare && sink.ConfigString("exchange") is { Length: > 0 } exchange)
                    await link.Channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
                return null;
            }
            catch (Exception e) when (e is BrokerUnreachableException or SocketException or IOException or AuthenticationException)
            {
                last = e;
                link.Reset();
            }
        }
        return DeliveryOutcome.Retry(last is null ? "No broker address." : Describe(last));
    }

    private BasicProperties Properties(EnvelopeMessage message, string? signing)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [IdempotencyHeader] = message.EventId.ToString("D"),
            [EventTypeHeader] = message.EventType,
            [SequenceHeader] = message.Seq,
            [AttemptHeader] = message.Attempt,
        };
        if (signing is not null)
        {
            var timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
            headers[TimestampHeader] = timestamp;
            headers[SignatureHeader] = WebhookSignature.Header([signing], timestamp, message.Body);
        }
        return new BasicProperties
        {
            MessageId = message.EventId.ToString("D"),
            CorrelationId = message.EventId.ToString("D"),
            Type = message.EventType,
            AppId = "keel-plm",
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds()),
            Headers = headers,
        };
    }

    /// <summary>A short, address-free description of a transport failure for the delivery's last error. It looks through wrapped exceptions for the real cause.</summary>
    private static string Describe(Exception e)
    {
        for (var inner = e; inner is not null; inner = inner.InnerException)
        {
            switch (inner)
            {
                case AuthenticationFailureException or PossibleAuthenticationFailureException:
                    return "The broker refused the credentials (check the user name and password secrets and the client certificate).";
                case OperationInterruptedException { ShutdownReason.ReplyCode: 403 }:
                    return "The broker refused access (ACCESS_REFUSED): the user lacks permission on the virtual host or exchange.";
                case OperationInterruptedException { ShutdownReason.ReplyCode: 404 }:
                    return "The broker says the exchange does not exist (NOT_FOUND): create it or set declareExchange.";
                case AuthenticationException:
                    return "The TLS handshake with the broker failed (check the CA bundle, the broker certificate and the host name): " + inner.Message;
            }
        }
        return e is BrokerUnreachableException ? "The broker could not be reached: " + (e.InnerException?.Message ?? e.Message) : "The broker connection failed: " + (e.InnerException?.Message ?? e.Message);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var l in _links.Values) await l.CloseAsync();
        _links.Clear();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class Link
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public IConnection? Connection { get; set; }
        public IChannel? Channel { get; set; }

        public bool IsOpen => Connection is { IsOpen: true } && Channel is { IsOpen: true };

        public void Reset()
        {
            try { Channel?.Dispose(); } catch (Exception e) when (e is AlreadyClosedException or IOException or ObjectDisposedException) { /* already gone */ }
            try { Connection?.Dispose(); } catch (Exception e) when (e is AlreadyClosedException or IOException or ObjectDisposedException) { /* already gone */ }
            Channel = null;
            Connection = null;
        }

        public Task CloseAsync()
        {
            Reset();
            return Task.CompletedTask;
        }
    }
}
