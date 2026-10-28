using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Keel.Siem;

/// <summary>A destination's connection. <see cref="SendAsync"/> throws when the batch was not fully handed over; the forwarder then resends the whole batch after a backoff.</summary>
public interface ISiemSink : IAsyncDisposable
{
    string Name { get; }
    string Type { get; }
    Task SendAsync(IReadOnlyList<SiemMessage> batch, CancellationToken ct);
}

public sealed class SiemSendException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// RFC 5424 over TLS with RFC 5425 octet-counting framing. TLS 1.2 or 1.3 only; the server certificate is validated against the configured CA
/// bundle (or the system trust store) and must match the server name; an optional client certificate gives mutual TLS. Revocation is not checked
/// (no network path to an OCSP responder or CRL in an air gap). A broken connection is dropped and re-established by the next send.
/// </summary>
public sealed class SyslogTlsSink : ISiemSink
{
    private readonly SiemDestinationOptions _o;
    private readonly X509Certificate2Collection? _trustedRoots;
    private readonly X509Certificate2? _clientCertificate;
    private TcpClient? _tcp;
    private Stream? _stream;

    /// <summary>The protocol the current connection negotiated (for diagnostics and tests).</summary>
    public SslProtocols? NegotiatedProtocol { get; private set; }

    public SyslogTlsSink(SiemDestinationOptions options, string? secretsDirectory)
    {
        _o = options;
        if (options.Transport == SyslogTransport.Tls)
        {
            if (!string.IsNullOrEmpty(options.CaBundlePath))
            {
                _trustedRoots = [];
                _trustedRoots.ImportFromPemFile(Resolve(secretsDirectory, options.CaBundlePath));
                if (_trustedRoots.Count == 0) throw new InvalidOperationException($"CaBundlePath of destination '{options.Name}' holds no certificate.");
            }
            if (!string.IsNullOrEmpty(options.ClientCertificatePath))
            {
                var certPath = Resolve(secretsDirectory, options.ClientCertificatePath);
                var keyPath = Resolve(secretsDirectory, options.ClientKeyPath);
                using var pem = string.IsNullOrEmpty(options.ClientKeyPassword)
                    ? X509Certificate2.CreateFromPemFile(certPath, keyPath)
                    : X509Certificate2.CreateFromEncryptedPemFile(certPath, options.ClientKeyPassword, keyPath);
                // SslStream needs a certificate whose key it can use for the handshake on every platform: round-trip through PKCS#12.
                _clientCertificate = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
            }
        }
    }

    public string Name => _o.Name;
    public string Type => "syslog-tls";

    public static string Resolve(string? secretsDirectory, string path) =>
        Path.IsPathRooted(path) || string.IsNullOrWhiteSpace(secretsDirectory) ? path : Path.Combine(secretsDirectory, path);

    public async Task SendAsync(IReadOnlyList<SiemMessage> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        using var bytes = new MemoryStream();
        foreach (var m in batch) bytes.Write(SyslogFormatter.Frame(m.Syslog));
        try
        {
            // A collector never sends anything: a readable socket means the peer closed (or sent a TLS alert). Writing into such a connection would
            // succeed locally and the batch would vanish, so a connection that is not quiet is dropped and re-established before anything is sent.
            if (_tcp is { Client: { } socket } && socket.Poll(0, SelectMode.SelectRead)) Drop();
            var stream = _stream ?? await ConnectAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_o.SendTimeoutSeconds));
            await stream.WriteAsync(bytes.GetBuffer().AsMemory(0, (int)bytes.Length), timeout.Token);
            await stream.FlushAsync(timeout.Token);
            // TCP gives no acknowledgement. If the peer closed while the batch was written, the connection is readable (EOF or reset) by now:
            // report failure so the batch is sent again rather than counted as delivered.
            if (_tcp is { Client: { } after } && after.Poll(0, SelectMode.SelectRead)) throw new IOException("the collector closed the connection while the batch was written");
        }
        catch (Exception e) when (e is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            Drop();
            if (e is OperationCanceledException && ct.IsCancellationRequested) throw;
            throw new SiemSendException($"Sending to '{_o.Name}' ({_o.Host}:{_o.Port}) failed: {Describe(e)}", e);
        }
    }

    private static string Describe(Exception e) => e is AuthenticationException { InnerException: { } inner } ? $"{e.Message} ({inner.Message})" : e.Message;

    private async Task<Stream> ConnectAsync(CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(_o.ConnectTimeoutSeconds));
                await tcp.ConnectAsync(_o.Host, _o.Port, connectTimeout.Token);
            }
            Stream stream = tcp.GetStream();
            if (_o.Transport == SyslogTransport.Tls)
            {
                var tls = new SslStream(stream, leaveInnerStreamOpen: false, ValidateServerCertificate);
                var options = new SslClientAuthenticationOptions
                {
                    TargetHost = string.IsNullOrEmpty(_o.ServerName) ? _o.Host : _o.ServerName,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                };
                if (_clientCertificate is not null) options.ClientCertificates = [_clientCertificate];
                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(_o.ConnectTimeoutSeconds));
                await tls.AuthenticateAsClientAsync(options, handshakeTimeout.Token);
                NegotiatedProtocol = tls.SslProtocol;
                stream = tls;
            }
            _tcp = tcp;
            _stream = stream;
            return stream;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null) return false;
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return false;
        if (_trustedRoots is null) return errors == SslPolicyErrors.None; // the operating system's trust store decided
        // Our own roots: build the chain against exactly the configured CA bundle and nothing else.
        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.AddRange(_trustedRoots);
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (chain is not null) foreach (var element in chain.ChainElements.Skip(1)) custom.ChainPolicy.ExtraStore.Add(element.Certificate);
        return custom.Build(new X509Certificate2(certificate));
    }

    private void Drop()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public ValueTask DisposeAsync()
    {
        Drop();
        _clientCertificate?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Newline-delimited JSON for a log forwarder (Splunk universal forwarder, filebeat, rsyslog imfile). The whole batch is appended with one write on a
/// file opened in append mode, then flushed to disk, so lines are never interleaved or split and a crash loses no more than the batch in flight.
/// The file is reopened for every batch: when logrotate renames it, the next batch creates a fresh file (and copytruncate is safe too).
/// </summary>
public sealed class FileSink(SiemDestinationOptions options, string? secretsDirectory) : ISiemSink
{
    private readonly string _path = SyslogTlsSink.Resolve(secretsDirectory, options.FilePath);

    public string Name => options.Name;
    public string Type => "file";

    public Task SendAsync(IReadOnlyList<SiemMessage> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return Task.CompletedTask;
        var text = new StringBuilder();
        foreach (var m in batch) text.Append(m.Json).Append('\n');
        var bytes = Encoding.UTF8.GetBytes(text.ToString());
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(_path)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            // bufferSize 0: the single Write below is one write(2) on an O_APPEND descriptor.
            var fileOptions = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete, BufferSize = 0 };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead; // 0640: the forwarder's group reads it
            using var stream = new FileStream(_path, fileOptions);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return Task.CompletedTask;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SiemSendException($"Appending to '{_path}' failed: {e.Message}", e);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
