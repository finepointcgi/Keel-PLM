using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Keel.Integration;

public enum DeliveryResult
{
    Delivered,
    /// <summary>Try again later (network error, timeout, 5xx, 408, 429).</summary>
    Retry,
    /// <summary>Retrying cannot help (4xx other than 408/429, a refused redirect, an unusable configuration): the delivery becomes a dead letter.</summary>
    Permanent,
}

public sealed record DeliveryOutcome(DeliveryResult Result, string? Error = null)
{
    public static readonly DeliveryOutcome Ok = new(DeliveryResult.Delivered);
    public static DeliveryOutcome Retry(string error) => new(DeliveryResult.Retry, error);
    public static DeliveryOutcome Permanent(string error) => new(DeliveryResult.Permanent, error);
}

/// <summary>What a sink receives for one event: the stable body bytes and the identifiers that go into transport headers.</summary>
public sealed record EnvelopeMessage(Guid EventId, string EventType, long Seq, DateTimeOffset OccurredAt, byte[] Body, int Attempt);

/// <summary>
/// A destination for events. A message broker sink is added later by implementing this interface (Phase 4b); until then the broker kind
/// refuses to be configured or to deliver.
/// </summary>
public interface IEventSink
{
    string Kind { get; }

    /// <summary>Configuration errors as messages for the administrator; empty when the configuration is usable.</summary>
    IReadOnlyList<string> ValidateConfig(JsonObject config, bool enabled);

    Task<DeliveryOutcome> DeliverAsync(SinkRecord sink, EnvelopeMessage message, CancellationToken ct);
}

/// <summary>A sink that writes several events at once (one file for a batch). Results are in the order of the messages.</summary>
public interface IBatchEventSink : IEventSink
{
    Task<IReadOnlyList<DeliveryOutcome>> DeliverBatchAsync(SinkRecord sink, IReadOnlyList<EnvelopeMessage> messages, CancellationToken ct);
}

public sealed class SinkRegistry(IEnumerable<IEventSink> sinks)
{
    private readonly Dictionary<string, IEventSink> _byKind = sinks.ToDictionary(s => s.Kind, StringComparer.Ordinal);

    public IEventSink For(string kind) => _byKind.TryGetValue(kind, out var s) ? s : throw new NotSupportedException($"Sink kind '{kind}' is not supported.");
}

/// <summary>The message broker kind is part of the model so sinks can be defined for it later; it refuses to be configured or to deliver and names the phase that delivers it.</summary>
public sealed class BrokerSink : IEventSink
{
    public const string Message = "Message broker sinks are delivered in Phase 4b. Use a webhook or file-drop sink until then.";

    public string Kind => SinkKinds.Broker;

    public IReadOnlyList<string> ValidateConfig(JsonObject config, bool enabled) => throw new NotSupportedException(Message);

    public Task<DeliveryOutcome> DeliverAsync(SinkRecord sink, EnvelopeMessage message, CancellationToken ct) => throw new NotSupportedException(Message);
}

/// <summary>
/// HTTPS POST of the signed envelope to an on-premises endpoint (ADR-0025). TLS is verified against the configured CA bundle (or the
/// system store), optional mutual TLS, no redirects, no proxy, bounded time, and every connection is checked against the SSRF rules.
/// </summary>
public sealed class WebhookSink(IntegrationOptions options, SecretResolver secrets, SsrfGuard guard, TimeProvider clock) : IEventSink, IDisposable
{
    public const string EventIdHeader = "X-Keel-Event-Id";
    public const string EventTypeHeader = "X-Keel-Event-Type";
    public const string TimestampHeader = "X-Keel-Timestamp";
    public const string SignatureHeader = "X-Keel-Signature";
    public const string AttemptHeader = "X-Keel-Delivery-Attempt";

    private readonly ConcurrentDictionary<(Guid, int), HttpClient> _clients = new();

    public string Kind => SinkKinds.Webhook;

    public IReadOnlyList<string> ValidateConfig(JsonObject config, bool enabled)
    {
        var errors = new List<string>();
        foreach (var key in config.Select(p => p.Key))
            if (key is not ("url" or "secretName" or "secondarySecretName" or "clientCertName" or "caBundleName" or "timeoutSeconds"))
                errors.Add($"Unknown setting '{key}'.");
        string? S(string k) => config[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        var url = S("url");
        if (string.IsNullOrWhiteSpace(url)) errors.Add("The endpoint URL is required.");
        else errors.AddRange(guard.CheckUrl(url));

        var primary = S("secretName");
        if (!SecretResolver.IsValidName(primary)) errors.Add("secretName is required: letters, digits, '_' and '-', up to 41 characters, starting with a letter.");
        else if (enabled && !secrets.SecretExists(primary!)) errors.Add($"Secret '{primary}' is not provisioned: provide Keel__Integration__Secrets__{primary} in the secrets directory.");
        if (S("secondarySecretName") is { } secondary)
        {
            if (!SecretResolver.IsValidName(secondary)) errors.Add("secondarySecretName is not a valid secret name.");
            else if (string.Equals(secondary, primary, StringComparison.Ordinal)) errors.Add("secondarySecretName must differ from secretName.");
            else if (enabled && !secrets.SecretExists(secondary)) errors.Add($"Secret '{secondary}' is not provisioned.");
        }
        if (S("clientCertName") is { } cert && !(SecretResolver.IsValidName(cert) && (!enabled || secrets.ClientCertificateExists(cert))))
            errors.Add($"Client certificate '{cert}' is not configured (Keel:Integration:ClientCertificates:{cert}).");
        if (S("caBundleName") is { } ca && !(SecretResolver.IsValidName(ca) && (!enabled || secrets.CaBundleExists(ca))))
            errors.Add($"CA bundle '{ca}' is not configured (Keel:Integration:CaBundles:{ca}).");
        if (config["timeoutSeconds"] is not null && !(config["timeoutSeconds"] is JsonValue t && t.TryGetValue<int>(out var seconds) && seconds is >= 1 and <= 120))
            errors.Add("timeoutSeconds must be a whole number from 1 to 120.");
        return errors;
    }

    public async Task<DeliveryOutcome> DeliverAsync(SinkRecord sink, EnvelopeMessage message, CancellationToken ct)
    {
        var url = sink.ConfigString("url");
        var primary = sink.ConfigString("secretName");
        if (url is null || primary is null) return DeliveryOutcome.Permanent("The sink has no url or secretName.");

        var secretValues = new List<string>();
        foreach (var name in new[] { primary, sink.ConfigString("secondarySecretName") })
        {
            if (name is null) continue;
            var v = secrets.Secret(name);
            if (v is null) return DeliveryOutcome.Retry($"Secret '{name}' is not provisioned."); // an operator can fix this: retry, do not kill the event
            secretValues.Add(v);
        }

        HttpClient client;
        try
        {
            client = _clients.GetOrAdd((sink.Id, sink.Revision), _ => Create(sink));
        }
        catch (Exception e) when (e is InvalidOperationException or CryptographicException or IOException)
        {
            return DeliveryOutcome.Retry("The sink's TLS material could not be loaded: " + e.Message);
        }

        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(message.Body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation(EventIdHeader, message.EventId.ToString("D"));
        request.Headers.TryAddWithoutValidation(EventTypeHeader, message.EventType);
        request.Headers.TryAddWithoutValidation(TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(SignatureHeader, WebhookSignature.Header(secretValues, timestamp, message.Body));
        request.Headers.TryAddWithoutValidation(AttemptHeader, message.Attempt.ToString(CultureInfo.InvariantCulture));
        request.Headers.UserAgent.ParseAdd("Keel-PLM-Webhook/1");

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return Classify(response.StatusCode, response.Headers.Location);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown: the dispatcher releases the lease, the attempt does not count
        }
        catch (TaskCanceledException)
        {
            return DeliveryOutcome.Retry("The endpoint did not answer within the timeout.");
        }
        catch (HttpRequestException e)
        {
            // The SSRF guard refuses inside the connect callback; that is a configuration problem, not a transient one.
            return e.InnerException is SsrfRefusedException refused
                ? DeliveryOutcome.Permanent("Refused by the SSRF rules: " + refused.Message)
                : DeliveryOutcome.Retry("Connection failed: " + (e.InnerException?.Message ?? e.Message));
        }
    }

    /// <summary>Response handling rules (documented in erp-contract.md): 2xx delivered; 408, 429 and 5xx retry; 3xx and every other 4xx (including 410) are permanent.</summary>
    public static DeliveryOutcome Classify(HttpStatusCode status, Uri? location = null)
    {
        var code = (int)status;
        if (code is >= 200 and < 300) return DeliveryOutcome.Ok;
        if (code is >= 300 and < 400) return DeliveryOutcome.Permanent($"HTTP {code}: redirects are never followed{(location is null ? "" : $" (to {location.GetLeftPart(UriPartial.Path)})")}; fix the endpoint URL.");
        if (code == 410) return DeliveryOutcome.Permanent("HTTP 410: the endpoint says it is gone; disable or fix the sink, then requeue.");
        if (code is 408 or 429) return DeliveryOutcome.Retry($"HTTP {code}");
        if (code is >= 400 and < 500) return DeliveryOutcome.Permanent($"HTTP {code}: the endpoint rejected the event permanently.");
        return DeliveryOutcome.Retry($"HTTP {code}");
    }

    private HttpClient Create(SinkRecord sink)
    {
        var caName = sink.ConfigString("caBundleName");
        var customCa = caName is null ? null : secrets.CaBundle(caName);
        var certName = sink.ConfigString("clientCertName");
        var ssl = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck, // air-gapped: no OCSP/CRL fetches (no egress)
            RemoteCertificateValidationCallback = (_, cert, chain, errors) => ValidateServer(cert, chain, errors, customCa),
        };
        if (certName is not null) ssl.ClientCertificates = [secrets.ClientCertificate(certName)];

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(10, sink.ConfigInt("timeoutSeconds") ?? options.WebhookTimeoutSeconds)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), // re-resolve DNS regularly
            SslOptions = ssl,
            ConnectCallback = async (context, token) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = IPAddress.TryParse(host, out var literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(host, token);
                // Every resolved address must pass, so a name that also resolves somewhere forbidden is refused outright.
                foreach (var a in addresses)
                    if (guard.CheckAddress(host, a) is { } problem) throw new SsrfRefusedException(problem);
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(sink.ConfigInt("timeoutSeconds") ?? options.WebhookTimeoutSeconds),
            MaxResponseContentBufferSize = 64 * 1024,
        };
    }

    private static bool ValidateServer(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors, X509Certificate2Collection? customCa)
    {
        if (certificate is null) return false;
        if ((errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
        if (customCa is null) return errors == SslPolicyErrors.None;

        using var leaf = new X509Certificate2(certificate);
        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.AddRange(customCa);
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (chain is not null)
            foreach (var element in chain.ChainElements.Skip(1)) custom.ChainPolicy.ExtraStore.Add(element.Certificate);
        return custom.Build(leaf);
    }

    public void Dispose()
    {
        foreach (var c in _clients.Values) c.Dispose();
        _clients.Clear();
    }
}

public sealed class SsrfRefusedException(string message) : Exception(message);

/// <summary>
/// Writes events as files for a batch ERP import (ADR-0025). Every file is written to a temporary name and renamed, so a reader never sees
/// a partial file; each data file is followed by <c>{file}.manifest.json</c> (SHA-256, size, the event ids inside) which is the commit
/// marker: a reader processes only files that have a manifest and verifies the hash.
/// </summary>
public sealed class FileDropSink(IntegrationOptions options, TimeProvider clock) : IBatchEventSink
{
    public string Kind => SinkKinds.FileDrop;

    public IReadOnlyList<string> ValidateConfig(JsonObject config, bool enabled)
    {
        var errors = new List<string>();
        foreach (var key in config.Select(p => p.Key))
            if (key is not ("directory" or "format")) errors.Add($"Unknown setting '{key}'.");
        var format = config["format"] is JsonValue f && f.TryGetValue<string>(out var fs) ? fs : "json";
        if (format is not ("json" or "ndjson")) errors.Add("format must be 'json' (one file per event) or 'ndjson' (one file per batch).");
        if (string.IsNullOrWhiteSpace(options.FileDropRoot)) { errors.Add("File-drop sinks are refused: Keel:Integration:FileDropRoot is not configured (mount a volume and set it)."); return errors; }
        var directory = config["directory"] is JsonValue d && d.TryGetValue<string>(out var ds) ? ds : null;
        if (string.IsNullOrWhiteSpace(directory)) { errors.Add("directory is required (relative to the file-drop root)."); return errors; }
        if (TryResolve(directory, out var full, out var problem))
        {
            if (enabled)
            {
                try { Directory.CreateDirectory(full); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add($"The directory cannot be created or written: {e.Message}"); }
            }
        }
        else errors.Add(problem!);
        return errors;
    }

    /// <summary>Resolves a sink directory under the root; refuses absolute paths, '..' and anything that escapes the root.</summary>
    public bool TryResolve(string directory, out string full, out string? problem)
    {
        full = "";
        problem = null;
        if (Path.IsPathRooted(directory) || directory.Contains("..", StringComparison.Ordinal) || directory.IndexOfAny(['\\', '\0']) >= 0)
        {
            problem = "directory must be a relative path inside the file-drop root, without '..'.";
            return false;
        }
        var root = Path.GetFullPath(options.FileDropRoot);
        full = Path.GetFullPath(Path.Combine(root, directory));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            problem = "directory escapes the file-drop root.";
            return false;
        }
        return true;
    }

    public async Task<DeliveryOutcome> DeliverAsync(SinkRecord sink, EnvelopeMessage message, CancellationToken ct) =>
        (await DeliverBatchAsync(sink, [message], ct))[0];

    public async Task<IReadOnlyList<DeliveryOutcome>> DeliverBatchAsync(SinkRecord sink, IReadOnlyList<EnvelopeMessage> messages, CancellationToken ct)
    {
        var directory = sink.ConfigString("directory");
        var full = "";
        string? problem = directory is null ? "The sink has no directory." : null;
        if (problem is null && !TryResolve(directory!, out full, out problem))
            problem ??= "Invalid directory.";
        if (problem is not null)
            return [.. messages.Select(_ => DeliveryOutcome.Permanent(problem))];
        var format = sink.ConfigString("format") ?? "json";
        var results = new DeliveryOutcome[messages.Count];
        try
        {
            Directory.CreateDirectory(full);
            if (format == "ndjson")
            {
                var stamp = clock.GetUtcNow().ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
                var name = $"keel-{stamp}-{messages[0].Seq.ToString(CultureInfo.InvariantCulture)}-{messages.Count.ToString(CultureInfo.InvariantCulture)}.ndjson";
                var bytes = new MemoryStream();
                foreach (var m in messages)
                {
                    bytes.Write(m.Body);
                    bytes.WriteByte((byte)'\n');
                }
                await WriteFileWithManifestAsync(full, name, bytes.ToArray(), messages, ct);
            }
            else
            {
                foreach (var m in messages)
                {
                    var name = $"keel-{m.Seq.ToString("D12", CultureInfo.InvariantCulture)}-{m.EventId:D}.json";
                    await WriteFileWithManifestAsync(full, name, [.. m.Body, (byte)'\n'], [m], ct);
                }
            }
            Array.Fill(results, DeliveryOutcome.Ok);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Array.Fill(results, DeliveryOutcome.Retry("File write failed: " + e.Message));
        }
        return results;
    }

    private async Task WriteFileWithManifestAsync(string directory, string name, byte[] content, IReadOnlyList<EnvelopeMessage> messages, CancellationToken ct)
    {
        await AtomicWriteAsync(Path.Combine(directory, name), content, ct);
        var manifest = new JsonObject
        {
            ["file"] = name,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(content)),
            ["sizeBytes"] = content.Length,
            ["writtenAt"] = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["events"] = new JsonArray([.. messages.Select(m => (JsonNode?)new JsonObject { ["id"] = m.EventId.ToString("D"), ["type"] = m.EventType })]),
        };
        // The manifest is written last: its presence says the data file is complete.
        await AtomicWriteAsync(Path.Combine(directory, name + ".manifest.json"), Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n"), ct);
    }

    private static async Task AtomicWriteAsync(string path, byte[] content, CancellationToken ct)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, ct);
                await stream.FlushAsync(ct);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { /* best effort: the temp name never matches a reader's pattern */ }
            throw;
        }
    }
}
