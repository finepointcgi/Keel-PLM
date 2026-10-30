using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Keel.Notifications;

/// <summary>A failure to hand a mail to the relay. <see cref="Permanent"/> means retrying cannot help (the relay refused the address or the configuration is wrong).</summary>
public sealed class SmtpTransportException(string message, bool permanent, Exception? inner = null) : Exception(message, inner)
{
    public bool Permanent { get; } = permanent;
}

/// <summary>One plain-text mail. The body must be ASCII and is sent as is (the builder of the pointer-only mail guarantees it).</summary>
public sealed record OutgoingMail(string From, string To, string Subject, string Body, string? BounceTag = null);

/// <summary>
/// A small, strict SMTP client for an on-premises relay (RFC 5321, STARTTLS RFC 3207, AUTH PLAIN/LOGIN): no dependency, TLS 1.2/1.3 only, the relay's
/// certificate and name verified (optionally against a private CA bundle, no revocation lookups because there is no internet), and the connection made to
/// the address that passed <see cref="OnPremHostGuard"/>, so a name cannot be re-pointed at an external host between the check and the connect. A relay that
/// does not offer STARTTLS in StartTls mode is refused rather than used in clear text. Nothing about the recipient or the text is logged by this class.
/// </summary>
public sealed class SmtpSender(EmailOptions options, OnPremHostGuard guard)
{
    private const int MaxLineLength = 4096;
    private const int MaxResponseLines = 200;
    private readonly Lazy<X509Certificate2Collection?> _ca = new(() =>
    {
        if (string.IsNullOrEmpty(options.CaBundlePath)) return null;
        var c = new X509Certificate2Collection();
        c.ImportFromPemFile(options.CaBundlePath);
        return c;
    });

    public async Task SendAsync(OutgoingMail mail, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try { await SendCoreAsync(mail, linked.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SmtpTransportException("The mail server did not answer within the timeout.", false); }
        catch (Exception e) when (e is IOException or SocketException or AuthenticationException)
        {
            throw new SmtpTransportException("The connection to the mail server failed: " + (e.InnerException?.Message ?? e.Message), false, e);
        }
    }

    private async Task SendCoreAsync(OutgoingMail mail, CancellationToken ct)
    {
        using var tcp = await ConnectAsync(ct);
        Stream stream = tcp.GetStream();
        if (options.Security == SmtpSecurity.ImplicitTls) stream = await UpgradeAsync(stream, ct);
        var session = new Session(stream);
        try
        {
            Expect(await session.ReadAsync(ct), 220, "greeting");
            var capabilities = await EhloAsync(session, ct);
            if (options.Security == SmtpSecurity.StartTls)
            {
                if (!capabilities.Contains("STARTTLS")) throw new SmtpTransportException("The mail server does not offer STARTTLS; mail is not sent in clear text.", false);
                await session.WriteAsync("STARTTLS", ct);
                Expect(await session.ReadAsync(ct), 220, "STARTTLS");
                stream = await UpgradeAsync(stream, ct);
                session.Dispose();
                session = new Session(stream); // a fresh reader: anything the server sent before the handshake is discarded
                capabilities = await EhloAsync(session, ct);
            }
            if (options.Username.Length > 0) await AuthenticateAsync(session, capabilities, ct);

            await session.WriteAsync($"MAIL FROM:<{mail.From}>", ct);
            Expect(await session.ReadAsync(ct), 250, "MAIL FROM");
            await session.WriteAsync($"RCPT TO:<{mail.To}>", ct);
            Expect(await session.ReadAsync(ct), 250, "RCPT TO", also: 251);
            await session.WriteAsync("DATA", ct);
            Expect(await session.ReadAsync(ct), 354, "DATA");
            await session.WriteRawAsync(Compose(options.Bounces.TagSecret.Length > 0 ? mail with { BounceTag = BounceTags.Compute(options.Bounces.TagSecret, mail.To) } : mail), ct);
            Expect(await session.ReadAsync(ct), 250, "message");
            try
            {
                await session.WriteAsync("QUIT", ct);
                await session.ReadAsync(ct);
            }
            catch (Exception e) when (e is IOException or SmtpTransportException) { /* the mail was accepted; a relay that closes without a goodbye is not a failure */ }
        }
        finally
        {
            session.Dispose();
            await stream.DisposeAsync();
        }
    }

    private async Task<TcpClient> ConnectAsync(CancellationToken ct)
    {
        var host = options.Host.Trim('[', ']');
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        if (addresses.Length == 0) throw new SmtpTransportException("The mail server's name did not resolve.", false);
        // Every resolved address must pass: a name that also points somewhere external is refused outright.
        foreach (var a in addresses)
            if (guard.CheckAddress(host, a) is { } problem) throw new SmtpTransportException("Refused by the on-premises rule: " + problem, true);
        SocketException? last = null;
        foreach (var a in addresses)
        {
            var client = new TcpClient(a.AddressFamily) { NoDelay = true };
            try
            {
                await client.ConnectAsync(a, options.Port, ct);
                return client;
            }
            catch (SocketException e) { client.Dispose(); last = e; }
            catch { client.Dispose(); throw; }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    private async Task<Stream> UpgradeAsync(Stream inner, CancellationToken ct)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        var ca = _ca.Value;
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = options.Host.Trim('[', ']'),
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck, // air-gapped: no OCSP/CRL fetches
            RemoteCertificateValidationCallback = (_, cert, chain, errors) => ValidateServer(cert, chain, errors, ca),
        }, ct);
        return ssl;
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

    private static async Task<HashSet<string>> EhloAsync(Session session, CancellationToken ct)
    {
        await session.WriteAsync("EHLO " + LocalName(), ct);
        var r = await session.ReadAsync(ct);
        Expect(r, 250, "EHLO");
        return [.. r.Lines.Skip(1).Select(l => l.Trim().ToUpperInvariant())];
    }

    private async Task AuthenticateAsync(Session session, HashSet<string> capabilities, CancellationToken ct)
    {
        var mechanisms = capabilities.FirstOrDefault(c => c.StartsWith("AUTH ", StringComparison.Ordinal) || c.StartsWith("AUTH=", StringComparison.Ordinal));
        var offered = mechanisms is null ? [] : mechanisms[5..].Split([' ', '='], StringSplitOptions.RemoveEmptyEntries);
        string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        if (offered.Contains("PLAIN"))
        {
            await session.WriteAsync("AUTH PLAIN " + B64("\0" + options.Username + "\0" + options.Password), ct);
            Expect(await session.ReadAsync(ct), 235, "authentication");
        }
        else if (offered.Contains("LOGIN"))
        {
            await session.WriteAsync("AUTH LOGIN", ct);
            Expect(await session.ReadAsync(ct), 334, "authentication");
            await session.WriteAsync(B64(options.Username), ct);
            Expect(await session.ReadAsync(ct), 334, "authentication");
            await session.WriteAsync(B64(options.Password), ct);
            Expect(await session.ReadAsync(ct), 235, "authentication");
        }
        else throw new SmtpTransportException("The mail server offers no supported authentication (PLAIN or LOGIN).", true);
    }

    /// <summary>The message as sent after DATA: headers, a blank line, the dot-stuffed body and the end marker. Always CRLF.</summary>
    internal static byte[] Compose(OutgoingMail mail)
    {
        var domain = mail.From[(mail.From.LastIndexOf('@') + 1)..];
        var sb = new StringBuilder();
        sb.Append("From: ").Append(mail.From).Append("\r\n");
        sb.Append("To: ").Append(mail.To).Append("\r\n");
        sb.Append("Subject: ").Append(mail.Subject).Append("\r\n");
        sb.Append("Date: ").Append(DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append('@').Append(domain).Append(">\r\n");
        sb.Append("MIME-Version: 1.0\r\nContent-Type: text/plain; charset=us-ascii\r\nContent-Transfer-Encoding: 7bit\r\n");
        sb.Append("Auto-Submitted: auto-generated\r\nPrecedence: bulk\r\n");
        if (mail.BounceTag is not null) sb.Append(BounceTags.Header).Append(": ").Append(mail.BounceTag).Append("\r\n");
        sb.Append("\r\n");
        foreach (var line in mail.Body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            sb.Append(line.StartsWith('.') ? "." + line : line).Append("\r\n");
        sb.Append(".\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string LocalName()
    {
        var name = new string([.. Environment.MachineName.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')]);
        return name.Length == 0 ? "localhost" : name;
    }

    private static void Expect(Reply reply, int code, string step, int? also = null)
    {
        if (reply.Code == code || reply.Code == also) return;
        var permanent = reply.Code is >= 500 and < 600;
        throw new SmtpTransportException($"The mail server answered {reply.Code.ToString(CultureInfo.InvariantCulture)} to {step}: {Scrub(reply.Text)}", permanent);
    }

    /// <summary>Relays often repeat the recipient in an error text; addresses are personal data and never reach a log, the queue or the audit log.</summary>
    private static string Scrub(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"[^\s<>""']+@[^\s<>""']+", "[address]", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));

    private sealed record Reply(int Code, IReadOnlyList<string> Lines)
    {
        public string Text => Lines.Count > 0 ? Lines[0] : "";
    }

    private sealed class Session(Stream stream) : IDisposable
    {
        public void Dispose() => _reader.Dispose();

        private readonly StreamReader _reader = new(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

        public async Task WriteAsync(string command, CancellationToken ct) => await WriteRawAsync(Encoding.ASCII.GetBytes(command + "\r\n"), ct);

        public async Task WriteRawAsync(byte[] bytes, CancellationToken ct)
        {
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        public async Task<Reply> ReadAsync(CancellationToken ct)
        {
            var lines = new List<string>();
            while (true)
            {
                var line = await _reader.ReadLineAsync(ct) ?? throw new IOException("The mail server closed the connection.");
                if (line.Length > MaxLineLength || lines.Count >= MaxResponseLines) throw new SmtpTransportException("The mail server's answer is too long.", false);
                if (line.Length < 3 || !int.TryParse(line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                    throw new SmtpTransportException("The mail server sent an answer that is not SMTP.", false);
                lines.Add(line.Length > 4 ? line[4..] : "");
                if (line.Length == 3 || line[3] == ' ') return new Reply(code, lines);
                if (line[3] != '-') throw new SmtpTransportException("The mail server sent an answer that is not SMTP.", false);
            }
        }
    }
}
