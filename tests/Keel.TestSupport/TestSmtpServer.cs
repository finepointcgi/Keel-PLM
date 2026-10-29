using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Keel.TestSupport;

public enum TestSmtpMode
{
    /// <summary>No TLS: a relay as it was in 1995.</summary>
    Plain,
    /// <summary>Plain greeting, STARTTLS offered (unless <see cref="TestSmtpServer.OfferStartTls"/> is false).</summary>
    StartTls,
    /// <summary>TLS from the first byte.</summary>
    ImplicitTls,
}

/// <summary>One message the server accepted.</summary>
public sealed record ReceivedMail(string From, IReadOnlyList<string> Recipients, string Data, bool OverTls, string? AuthenticatedUser)
{
    public string Header(string name) => Data.Split("\r\n").TakeWhile(l => l.Length > 0)
        .FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim() ?? "";

    public string Body => Data.Contains("\r\n\r\n", StringComparison.Ordinal) ? Data[(Data.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..] : "";
}

/// <summary>
/// An in-process SMTP relay on a loopback port for tests (RFC 5321 subset: EHLO, STARTTLS, AUTH PLAIN/LOGIN, MAIL, RCPT, DATA, RSET, NOOP, QUIT) with the
/// controls the e-mail channel tests need: TLS modes and protocol versions, optional credentials, recipients it refuses, temporary failures, a relay that
/// hides STARTTLS, and a record of every message and every command (credentials redacted). Nothing leaves the machine.
/// </summary>
public sealed class TestSmtpServer : IAsyncDisposable
{
    private readonly TestSmtpMode _mode;
    private readonly X509Certificate2? _certificate;
    private readonly (string User, string Password)? _credentials;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<ReceivedMail> _mail = [];
    private readonly List<string> _commands = [];
    private readonly List<Socket> _connections = [];
    private readonly object _gate = new();
    private int _tempFailuresLeft;
    private int _disposed;

    public TestSmtpServer(TestSmtpMode mode = TestSmtpMode.StartTls, X509Certificate2? certificate = null, (string User, string Password)? credentials = null, int port = 0)
    {
        if (mode != TestSmtpMode.Plain && certificate is null) throw new ArgumentException("A TLS mode needs a server certificate.", nameof(certificate));
        _mode = mode;
        _certificate = certificate;
        _credentials = credentials;
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, port));
        _listener.Listen(16);
        Port = ((IPEndPoint)_listener.LocalEndPoint!).Port;
        _ = Task.Run(() => AcceptLoopAsync(_stop.Token));
    }

    public int Port { get; }
    /// <summary>False: a StartTls relay stops advertising STARTTLS (a downgrade attempt the client must refuse).</summary>
    public bool OfferStartTls { get; set; } = true;
    /// <summary>Recipients for which RCPT TO answers 550.</summary>
    public Func<string, bool> RejectRecipient { get; set; } = _ => false;
    /// <summary>True: a refusal repeats the recipient address in its text, as many relays do ("550 &lt;addr&gt;: Recipient address rejected").</summary>
    public bool EchoRecipientInErrors { get; set; }
    /// <summary>Answer 451 (try again later) to the end of DATA this many times, then accept.</summary>
    public int TempFailMessages { get => Volatile.Read(ref _tempFailuresLeft); set => Volatile.Write(ref _tempFailuresLeft, value); }
    public SslProtocols Protocols { get; set; } = SslProtocols.Tls12 | SslProtocols.Tls13;
    public List<SslProtocols> NegotiatedProtocols { get; } = [];
    public int ConnectionCount { get; private set; }

    public IReadOnlyList<ReceivedMail> Mail { get { lock (_gate) return [.. _mail]; } }
    /// <summary>Every command line received, in order, with the credentials of AUTH replaced by <c>***</c>.</summary>
    public IReadOnlyList<string> Commands { get { lock (_gate) return [.. _commands]; } }

    public async Task<bool> WaitForMailAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Mail.Count >= count) return true;
            await Task.Delay(25);
        }
        return Mail.Count >= count;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try { client = await _listener.AcceptAsync(ct); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            lock (_gate) { _connections.Add(client); ConnectionCount++; }
            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    private async Task HandleAsync(Socket socket, CancellationToken ct)
    {
        try
        {
            Stream stream = new NetworkStream(socket, ownsSocket: false);
            var tls = false;
            if (_mode == TestSmtpMode.ImplicitTls) { stream = await UpgradeAsync(stream, ct); tls = true; }
            var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

            async Task Send(string line) { await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"), ct); await stream.FlushAsync(ct); }
            void Note(string command) { lock (_gate) _commands.Add(command); }

            await Send("220 test-relay ESMTP ready");
            string? from = null;
            var recipients = new List<string>();
            string? authenticated = null;
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                var verb = line.Split(' ', 2)[0].ToUpperInvariant();
                if (verb == "AUTH") Note("AUTH ***"); else Note(line);
                switch (verb)
                {
                    case "EHLO":
                    case "HELO":
                        var caps = new List<string> { "test-relay" };
                        if (_mode == TestSmtpMode.StartTls && OfferStartTls && !tls) caps.Add("STARTTLS");
                        if (_credentials is not null) caps.Add("AUTH PLAIN LOGIN");
                        caps.Add("8BITMIME");
                        for (var i = 0; i < caps.Count; i++) await Send($"250{(i == caps.Count - 1 ? ' ' : '-')}{caps[i]}");
                        break;
                    case "STARTTLS" when _mode == TestSmtpMode.StartTls && OfferStartTls && !tls:
                        await Send("220 ready to start TLS");
                        reader.Dispose();
                        stream = await UpgradeAsync(stream, ct);
                        reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                        tls = true;
                        from = null; recipients.Clear(); authenticated = null;
                        break;
                    case "AUTH" when _credentials is { } cred:
                        authenticated = await AuthenticateAsync(line, reader, Send, cred, ct);
                        break;
                    case "MAIL":
                        from = Between(line);
                        recipients.Clear();
                        await Send("250 sender ok");
                        break;
                    case "RCPT":
                        var rcpt = Between(line);
                        if (RejectRecipient(rcpt)) await Send(EchoRecipientInErrors ? $"550 5.1.1 <{rcpt}>: Recipient address rejected" : "550 5.1.1 mailbox unavailable");
                        else { recipients.Add(rcpt); await Send("250 recipient ok"); }
                        break;
                    case "DATA":
                        await Send("354 end data with <CR><LF>.<CR><LF>");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync(ct) is { } d)
                        {
                            if (d == ".") break;
                            data.Append(d.StartsWith("..", StringComparison.Ordinal) ? d[1..] : d).Append("\r\n");
                        }
                        if (Interlocked.Decrement(ref _tempFailuresLeft) >= 0) { await Send("451 4.3.0 try again later"); break; }
                        Interlocked.Exchange(ref _tempFailuresLeft, 0);
                        lock (_gate) _mail.Add(new ReceivedMail(from ?? "", [.. recipients], data.ToString(), tls, authenticated));
                        await Send("250 queued");
                        from = null; recipients.Clear();
                        break;
                    case "RSET": from = null; recipients.Clear(); await Send("250 ok"); break;
                    case "NOOP": await Send("250 ok"); break;
                    case "QUIT": await Send("221 bye"); return;
                    default: await Send("502 command not implemented"); break;
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException or NotSupportedException or PlatformNotSupportedException) { /* the client went away or refused the handshake, or the platform cannot speak the configured TLS version */ }
        finally { socket.Dispose(); }
    }

    private async Task<Stream> UpgradeAsync(Stream inner, CancellationToken ct)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: true);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate, EnabledSslProtocols = Protocols, ClientCertificateRequired = false }, ct);
        }
        catch { ssl.Dispose(); throw; }
        lock (_gate) NegotiatedProtocols.Add(ssl.SslProtocol);
        return ssl;
    }

    private static async Task<string?> AuthenticateAsync(string line, StreamReader reader, Func<string, Task> send, (string User, string Password) cred, CancellationToken ct)
    {
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        string Decode(string b64) => Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        if (parts.Length >= 2 && parts[1].Equals("PLAIN", StringComparison.OrdinalIgnoreCase))
        {
            var fields = Decode(parts.Length > 2 ? parts[2] : "").Split('\0');
            if (fields.Length == 3 && fields[1] == cred.User && fields[2] == cred.Password) { await send("235 2.7.0 authenticated"); return cred.User; }
        }
        else if (parts.Length >= 2 && parts[1].Equals("LOGIN", StringComparison.OrdinalIgnoreCase))
        {
            await send("334 VXNlcm5hbWU6");
            var user = Decode(await reader.ReadLineAsync(ct) ?? "");
            await send("334 UGFzc3dvcmQ6");
            var password = Decode(await reader.ReadLineAsync(ct) ?? "");
            if (user == cred.User && password == cred.Password) { await send("235 2.7.0 authenticated"); return cred.User; }
        }
        await send("535 5.7.8 authentication failed");
        return null;
    }

    private static string Between(string line)
    {
        var open = line.IndexOf('<', StringComparison.Ordinal);
        var close = line.IndexOf('>', StringComparison.Ordinal);
        return open >= 0 && close > open ? line[(open + 1)..close] : "";
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _stop.CancelAsync();
        _listener.Dispose();
        lock (_gate) { foreach (var c in _connections) c.Dispose(); _connections.Clear(); }
        _stop.Dispose();
    }
}
