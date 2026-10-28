using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Keel.TestSupport;

/// <summary>A throwaway certificate authority and the certificates issued by it (ECDSA P-256), for TLS and mTLS tests. Synthetic only.</summary>
public sealed class TestPki : IDisposable
{
    private readonly X509Certificate2 _ca;
    private readonly string _directory = Directory.CreateTempSubdirectory("keel-pki-").FullName;

    public TestPki()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=Keel Test CA", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        _ca = X509CertificateLoader.LoadPkcs12(ca.Export(X509ContentType.Pfx, "test"), "test", X509KeyStorageFlags.Exportable);
    }

    public string CaBundlePath
    {
        get
        {
            var path = Path.Combine(_directory, "ca.pem");
            if (!File.Exists(path)) File.WriteAllText(path, _ca.ExportCertificatePem());
            return path;
        }
    }

    public X509Certificate2 Issue(string subject, bool server)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], false));
        if (server)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            req.CertificateExtensions.Add(san.Build());
        }
        using var cert = req.Create(_ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29), RandomNumberGenerator.GetBytes(16));
        using var withKey = cert.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx, "test"), "test", X509KeyStorageFlags.Exportable);
    }

    /// <summary>A client certificate and its key in one PEM file, the form Keel:Integration:ClientCertificates expects.</summary>
    public string WriteClientPem(string name)
    {
        using var cert = Issue(name, server: false);
        var path = Path.Combine(_directory, name + ".pem");
        File.WriteAllText(path, cert.ExportCertificatePem() + "\n" + cert.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem() + "\n");
        return path;
    }

    public bool IsIssuedByCa(X509Certificate2 cert)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(cert);
    }

    public void Dispose()
    {
        _ca.Dispose();
        try { Directory.Delete(_directory, true); } catch (IOException) { /* best effort */ }
    }
}

public sealed record ReceivedRequest(Guid EventId, string EventType, string Timestamp, string SignatureHeader, byte[] Body, bool SignatureValid, string Attempt, string? ClientCertificate, DateTimeOffset At);

/// <summary>
/// A reference ERP-side consumer for tests and demos (docs/integration/erp-contract.md): an HTTPS listener that verifies the HMAC signature
/// (its own implementation, independent of the sender's), checks the timestamp, deduplicates by event id and records what it received.
/// A behavior hook decides the response so tests can simulate failures.
/// </summary>
public sealed class ReferenceReceiver : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<ReceivedRequest> _requests = new();
    private readonly ConcurrentDictionary<Guid, ReceivedRequest> _events = new();
    private readonly string[] _secrets;
    private int _duplicates;

    /// <summary>Returns the status code to answer with for a request (called with the verified request); default 204. 302 adds a Location header.</summary>
    public Func<ReceivedRequest, Task<int>> Behavior { get; set; } = _ => Task.FromResult(204);
    public Uri Url { get; }
    public IReadOnlyCollection<ReceivedRequest> Requests => [.. _requests];
    /// <summary>Verified, deduplicated events in the order first received.</summary>
    public IReadOnlyList<ReceivedRequest> Events => [.. _requests.Where(r => r.SignatureValid).GroupBy(r => r.EventId).Select(g => g.First())];
    public int Duplicates => Volatile.Read(ref _duplicates);

    private ReferenceReceiver(WebApplication app, Uri url, string[] secrets)
    {
        _app = app;
        Url = url;
        _secrets = secrets;
    }

    public static async Task<ReferenceReceiver> StartAsync(TestPki pki, string[] secrets, bool requireClientCertificate = false, TimeSpan? tolerance = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var certificate = pki.Issue("localhost", server: true);
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.UseHttps(https =>
        {
            https.ServerCertificate = certificate;
            if (requireClientCertificate)
            {
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (c, _, _) => pki.IsIssuedByCa(c);
            }
        })));
        var app = builder.Build();
        ReferenceReceiver? self = null;
        app.MapPost("/{**path}", (HttpContext ctx) => self!.HandleAsync(ctx, tolerance ?? TimeSpan.FromMinutes(5)));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var port = new Uri(address).Port;
        self = new ReferenceReceiver(app, new Uri($"https://localhost:{port.ToString(CultureInfo.InvariantCulture)}/keel/events"), secrets);
        return self;
    }

    private async Task HandleAsync(HttpContext ctx, TimeSpan tolerance)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        var body = ms.ToArray();
        var timestamp = ctx.Request.Headers["X-Keel-Timestamp"].ToString();
        var header = ctx.Request.Headers["X-Keel-Signature"].ToString();
        var valid = Verify(timestamp, header, body, tolerance);
        _ = Guid.TryParse(ctx.Request.Headers["X-Keel-Event-Id"].ToString(), out var id);
        var received = new ReceivedRequest(id, ctx.Request.Headers["X-Keel-Event-Type"].ToString(), timestamp, header, body, valid, ctx.Request.Headers["X-Keel-Delivery-Attempt"].ToString(),
            ctx.Connection.ClientCertificate?.Subject, DateTimeOffset.UtcNow);
        _requests.Enqueue(received);
        if (!valid) { ctx.Response.StatusCode = 401; return; }
        if (!_events.TryAdd(id, received)) Interlocked.Increment(ref _duplicates);
        var status = await Behavior(received);
        if (status is >= 300 and < 400) ctx.Response.Headers.Location = "/elsewhere";
        ctx.Response.StatusCode = status;
    }

    /// <summary>The documented algorithm: HMAC-SHA-256 over "{timestamp}.{body}" with each configured secret; any "v1=" value may match; the timestamp must be recent.</summary>
    private bool Verify(string timestamp, string header, byte[] body, TimeSpan tolerance)
    {
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var ts)) return false;
        if (Math.Abs((DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(ts)).TotalSeconds) > tolerance.TotalSeconds) return false;
        var offered = header.Split(',', StringSplitOptions.TrimEntries).Where(p => p.StartsWith("v1=", StringComparison.Ordinal)).Select(p => p[3..]).ToList();
        var message = Encoding.ASCII.GetBytes(timestamp + ".").Concat(body).ToArray();
        foreach (var secret in _secrets)
        {
            var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));
            if (offered.Any(o => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(o), Encoding.ASCII.GetBytes(expected)))) return true;
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
