using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Keel.TestSupport;

/// <summary>A throwaway certificate authority for tests (ECDSA P-256): issues server and client certificates and exports PEM files.</summary>
public sealed class SyslogTestPki : IDisposable
{
    private readonly ECDsa _caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private long _serial = 100;

    public SyslogTestPki(string name = "Keel test CA")
    {
        var request = new CertificateRequest($"CN={name}", _caKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        Ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    public X509Certificate2 Ca { get; }
    public string CaPem => Ca.ExportCertificatePem();

    /// <summary>A leaf certificate signed by this CA; the returned certificate carries its private key.</summary>
    public X509Certificate2 Issue(string commonName, bool server, params string[] dnsNames)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], false));
        if (server)
        {
            var san = new SubjectAlternativeNameBuilder();
            foreach (var d in dnsNames) san.AddDnsName(d);
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
        }
        var serial = BitConverter.GetBytes(Interlocked.Increment(ref _serial));
        using var signed = request.Create(Ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29), serial);
        using var withKey = signed.CopyWithPrivateKey(key);
        // Round-trip through PKCS#12 so SslStream can use the key on every platform.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>Writes the certificate and its key as PEM files (client certificates for mTLS).</summary>
    public static (string CertPath, string KeyPath) WritePem(X509Certificate2 certificate, string directory, string name)
    {
        var cert = Path.Combine(directory, name + ".crt");
        var key = Path.Combine(directory, name + ".key");
        File.WriteAllText(cert, certificate.ExportCertificatePem());
        using var ecdsa = certificate.GetECDsaPrivateKey() ?? throw new InvalidOperationException("The certificate has no ECDSA key.");
        File.WriteAllText(key, ecdsa.ExportPkcs8PrivateKeyPem());
        return (cert, key);
    }

    public void Dispose()
    {
        Ca.Dispose();
        _caKey.Dispose();
    }
}

/// <summary>
/// A real syslog-over-TLS collector (RFC 5425) on a loopback port for tests: TLS 1.2/1.3, optional mutual TLS against a client CA, octet-counting frame
/// parsing, recorded messages, and controls to stop and restart on the same port, drop connections after N messages, or stop reading (a stuck collector).
/// </summary>
public sealed class TlsSyslogListener : IAsyncDisposable
{
    private readonly X509Certificate2 _server;
    private readonly X509Certificate2? _clientCa;
    private readonly List<string> _messages = [];
    private readonly List<Socket> _connections = [];
    private readonly object _gate = new();
    private Socket? _listener;
    private CancellationTokenSource _stop = new();
    private int _received;

    public TlsSyslogListener(X509Certificate2 serverCertificate, X509Certificate2? requireClientCertificatesFrom = null, SslProtocols protocols = SslProtocols.Tls12 | SslProtocols.Tls13)
    {
        _server = serverCertificate;
        _clientCa = requireClientCertificatesFrom;
        Protocols = protocols;
        Start(0);
    }

    public int Port { get; private set; }
    public SslProtocols Protocols { get; }
    public int ConnectionCount { get; private set; }
    public int RejectedHandshakes { get; private set; }
    public List<string> FramingErrors { get; } = [];
    public List<SslProtocols> NegotiatedProtocols { get; } = [];
    public List<string> ClientCertificateSubjects { get; } = [];
    /// <summary>While true the collector accepts connections but stops reading, so the sender's socket buffers fill (a stuck SIEM).</summary>
    public bool PauseReading { get => Volatile.Read(ref _pause); set => Volatile.Write(ref _pause, value); }
    private bool _pause;
    /// <summary>Close the connection after this many messages in total have been received (0 = never), simulating a collector that dies mid-batch.</summary>
    public int DropAfterTotal { get; set; }

    public IReadOnlyList<string> Messages { get { lock (_gate) return [.. _messages]; } }

    public void Start(int port)
    {
        _stop = new CancellationTokenSource();
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, port));
        listener.Listen(16);
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        _ = Task.Run(() => AcceptLoopAsync(listener, _stop.Token));
    }

    /// <summary>Stops listening and drops every connection; received messages are kept.</summary>
    public void Stop()
    {
        _stop.Cancel();
        _listener?.Dispose();
        lock (_gate) { foreach (var c in _connections) c.Dispose(); _connections.Clear(); }
    }

    public async Task<bool> WaitForCountAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Messages.Count >= count) return true;
            await Task.Delay(25);
        }
        return Messages.Count >= count;
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try { client = await listener.AcceptAsync(ct); }
            catch (Exception) { return; }
            lock (_gate) _connections.Add(client);
            _ = Task.Run(() => HandleAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleAsync(Socket socket, CancellationToken ct)
    {
        try
        {
            await using var tls = new SslStream(new NetworkStream(socket, ownsSocket: true), false, ValidateClient);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _server,
                EnabledSslProtocols = Protocols,
                ClientCertificateRequired = _clientCa is not null,
            }, ct);
            lock (_gate)
            {
                ConnectionCount++;
                NegotiatedProtocols.Add(tls.SslProtocol);
                if (tls.RemoteCertificate is { } rc) ClientCertificateSubjects.Add(rc.Subject);
            }
            await ReadFramesAsync(tls, socket, ct);
        }
        catch (Exception e) when (e is IOException or AuthenticationException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            lock (_gate) if (e is AuthenticationException || e.InnerException is AuthenticationException) RejectedHandshakes++;
        }
    }

    private bool ValidateClient(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (_clientCa is null) return true;
        if (certificate is null) return false;
        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.Add(_clientCa);
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return custom.Build(new X509Certificate2(certificate));
    }

    /// <summary>RFC 5425 section 4.3: MSG-LEN SP SYSLOG-MSG, MSG-LEN in decimal octets.</summary>
    private async Task ReadFramesAsync(Stream stream, Socket socket, CancellationToken ct)
    {
        var one = new byte[1];
        while (!ct.IsCancellationRequested)
        {
            while (PauseReading && !ct.IsCancellationRequested) await Task.Delay(25, CancellationToken.None);
            var length = 0;
            var digits = 0;
            while (true)
            {
                if (await stream.ReadAsync(one, ct) == 0) return;
                if (one[0] == (byte)' ') break;
                if (one[0] is < (byte)'0' or > (byte)'9' || ++digits > 8)
                {
                    lock (_gate) FramingErrors.Add($"unexpected byte 0x{one[0]:X2} in the length prefix");
                    return;
                }
                length = length * 10 + (one[0] - '0');
            }
            var body = new byte[length];
            var read = 0;
            while (read < length)
            {
                var n = await stream.ReadAsync(body.AsMemory(read), ct);
                if (n == 0) { lock (_gate) FramingErrors.Add($"stream ended inside a frame ({read} of {length} octets)"); return; }
                read += n;
            }
            lock (_gate) _messages.Add(Encoding.UTF8.GetString(body));
            var total = Interlocked.Increment(ref _received);
            if (DropAfterTotal > 0 && total >= DropAfterTotal) { socket.Dispose(); return; }
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }
}
