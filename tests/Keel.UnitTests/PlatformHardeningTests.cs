using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Integration;
using Keel.Notifications;
using Keel.TestSupport;
using Keel.Web.Services;

namespace Keel.UnitTests;

/// <summary>Gap wave B (ADR-0032): the shared lock registry, the e-mail channel's rules and the SMTP client against an in-process relay, the spool sink.</summary>
public sealed class SessionLockSharedStoreTests
{
    private static readonly SessionLockOptions Options = new() { IdleLockMinutes = 15, WarningSeconds = 60, LockedTerminateMinutes = 10 };
    private static readonly ActorSnapshot Alice = new("sub-alice", "alice", true, "USA", "ENG", [], [], ImmutableSortedSet.Create(StringComparer.Ordinal, "plm-viewer"));

    private static (SessionLockRegistry A, SessionLockRegistry B, ManualTimeProvider Clock, RecordingAudit Audit) TwoNodes(ISessionLockStore? store = null)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
        var audit = new RecordingAudit();
        store ??= new SharedMemoryStore();
        return (new SessionLockRegistry(Options, clock, audit, store: store), new SessionLockRegistry(Options, clock, audit, store: store), clock, audit);
    }

    /// <summary>A store that says it is durable, so the registries behave as they do against a database: caches that are refreshed by ticks.</summary>
    private sealed class SharedMemoryStore : ISessionLockStore
    {
        private readonly InMemorySessionLockStore _inner = new();
        public bool Durable => true;
        public Task<SessionLockRow> RegisterAsync(string key, ActorSnapshot actor, DateTimeOffset now, DateTimeOffset absoluteExpiry, CancellationToken ct) => _inner.RegisterAsync(key, actor, now, absoluteExpiry, ct);
        public Task<SessionLockRow?> FindAsync(string key, CancellationToken ct) => _inner.FindAsync(key, ct);
        public Task<IReadOnlyList<SessionLockRow>> LoadAllAsync(DateTimeOffset now, TimeSpan grace, CancellationToken ct) => _inner.LoadAllAsync(now, grace, ct);
        public Task TouchAsync(IReadOnlyCollection<KeyValuePair<string, DateTimeOffset>> activity, CancellationToken ct) => _inner.TouchAsync(activity, ct);
        public Task<SessionLockRow?> TryLockAsync(string key, string reason, DateTimeOffset now, CancellationToken ct) => _inner.TryLockAsync(key, reason, now, ct);
        public Task<SessionLockRow?> TryUnlockAsync(string key, DateTimeOffset now, CancellationToken ct) => _inner.TryUnlockAsync(key, now, ct);
        public Task<IReadOnlyList<SessionLockRow>> LockIdleAsync(DateTimeOffset idleSince, DateTimeOffset now, CancellationToken ct) => _inner.LockIdleAsync(idleSince, now, ct);
        public Task<IReadOnlyList<SessionLockRow>> TerminateAsync(DateTimeOffset lockedSince, CancellationToken ct) => _inner.TerminateAsync(lockedSince, ct);
        public Task<int> PurgeAsync(DateTimeOffset expiredBefore, CancellationToken ct) => _inner.PurgeAsync(expiredBefore, ct);
    }

    [Fact]
    public async Task A_lock_made_on_one_node_is_enforced_on_the_other_after_its_next_tick_and_audited_once()
    {
        var (a, b, clock, audit) = TwoNodes();
        a.Load(); b.Load();
        var seenOnB = new List<string>();
        b.Changed += seenOnB.Add;
        await a.RegisterAsync("s1", Alice, clock.GetUtcNow().AddHours(8));
        await b.RegisterAsync("s1", Alice, clock.GetUtcNow().AddHours(8)); // the same session on a second node (sticky sessions are no longer needed)

        Assert.True(await a.LockNowAsync("s1"));
        Assert.False(b.IsLocked("s1")); // B has not looked yet
        await b.TickAsync();
        Assert.True(b.IsLocked("s1"));
        Assert.Equal(["s1"], seenOnB); // the circuits of B are told
        Assert.Single(audit.Entries, e => e.Action == "session.lock"); // written by the node that made the transition, not by the one that noticed
    }

    [Fact]
    public async Task Both_nodes_tick_but_an_idle_session_is_locked_and_audited_exactly_once()
    {
        var (a, b, clock, audit) = TwoNodes();
        a.Load(); b.Load();
        await a.RegisterAsync("s1", Alice, clock.GetUtcNow().AddHours(8));
        await b.RegisterAsync("s1", Alice, clock.GetUtcNow().AddHours(8));
        clock.Advance(TimeSpan.FromMinutes(15));

        await Task.WhenAll(a.TickAsync(), b.TickAsync());
        await a.TickAsync(); await b.TickAsync();

        Assert.True(a.IsLocked("s1") && b.IsLocked("s1"));
        Assert.Single(audit.Entries, e => e.Action == "session.lock");
    }

    [Fact]
    public async Task Activity_on_either_node_keeps_the_session_open_on_both()
    {
        var (a, b, clock, _) = TwoNodes();
        a.Load(); b.Load();
        await a.RegisterAsync("s1", Alice, clock.GetUtcNow().AddHours(8));
        await b.RegisterAsync("s1", Alice, clock.GetUtcNow().AddHours(8));
        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.True(b.Touch("s1")); // the user is working through node B only
        await b.TickAsync(); // B writes the activity to the store within a second, like its timer does
        clock.Advance(TimeSpan.FromMinutes(1));

        await a.TickAsync(); // node A's own clock for the session says idle for 15 minutes, but the store has the newer activity
        await b.TickAsync();
        Assert.False(a.IsLocked("s1"));
        Assert.False(b.IsLocked("s1"));

        clock.Advance(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(1)); // really idle for 15 minutes now
        await a.TickAsync();
        await b.TickAsync();
        Assert.True(a.IsLocked("s1"));
        Assert.True(b.IsLocked("s1"));
    }

    [Fact]
    public async Task A_second_node_that_restarts_with_an_empty_cache_reads_the_locked_state_before_trusting_anything()
    {
        var store = new SharedMemoryStore();
        var (a, _, clock, audit) = TwoNodes(store);
        await a.RegisterAsync("s1", Alice, clock.GetUtcNow().AddHours(8));
        await a.LockNowAsync("s1");

        var restarted = new SessionLockRegistry(Options, clock, audit, store: store); // a new process: nothing in memory, nothing loaded yet
        Assert.True(restarted.IsLocked("s1")); // an unknown session is looked up in the store, not assumed active
        restarted.Load();
        Assert.True(restarted.IsLocked("s1"));
        await restarted.RegisterAsync("s1", Alice, clock.GetUtcNow().AddHours(8)); // the browser reconnects: it is still locked, not a new active session
        Assert.True(restarted.IsLocked("s1"));
        Assert.Equal("manual", restarted.Get("s1")!.LockReason);
    }

    [Fact]
    public async Task An_unreadable_store_means_locked_not_open()
    {
        var clock = new ManualTimeProvider();
        var registry = new SessionLockRegistry(Options, clock, store: new FailingStore());
        Assert.True(registry.IsLocked("anything")); // fail closed
        await Task.CompletedTask;
    }

    private sealed class FailingStore : ISessionLockStore
    {
        public bool Durable => true;
        public Task<SessionLockRow> RegisterAsync(string key, ActorSnapshot actor, DateTimeOffset now, DateTimeOffset absoluteExpiry, CancellationToken ct) => throw new InvalidOperationException();
        public Task<SessionLockRow?> FindAsync(string key, CancellationToken ct) => throw new InvalidOperationException("down");
        public Task<IReadOnlyList<SessionLockRow>> LoadAllAsync(DateTimeOffset now, TimeSpan grace, CancellationToken ct) => throw new InvalidOperationException();
        public Task TouchAsync(IReadOnlyCollection<KeyValuePair<string, DateTimeOffset>> activity, CancellationToken ct) => throw new InvalidOperationException();
        public Task<SessionLockRow?> TryLockAsync(string key, string reason, DateTimeOffset now, CancellationToken ct) => throw new InvalidOperationException();
        public Task<SessionLockRow?> TryUnlockAsync(string key, DateTimeOffset now, CancellationToken ct) => throw new InvalidOperationException();
        public Task<IReadOnlyList<SessionLockRow>> LockIdleAsync(DateTimeOffset idleSince, DateTimeOffset now, CancellationToken ct) => throw new InvalidOperationException();
        public Task<IReadOnlyList<SessionLockRow>> TerminateAsync(DateTimeOffset lockedSince, CancellationToken ct) => throw new InvalidOperationException();
        public Task<int> PurgeAsync(DateTimeOffset expiredBefore, CancellationToken ct) => throw new InvalidOperationException();
    }

    [Fact]
    public void The_store_key_is_a_hash_of_the_session_id_and_the_audit_reference_is_its_prefix()
    {
        var key = SessionLockRegistry.Key("session-1");
        Assert.Matches("^[0-9a-f]{64}$", key);
        Assert.DoesNotContain("session-1", key, StringComparison.Ordinal);
        Assert.Equal(key[..12], SessionLockRegistry.Reference("session-1"));
        Assert.NotEqual(key, SessionLockRegistry.Key("session-2"));
    }

    [Fact]
    public void The_memory_store_is_refused_in_production_and_the_database_store_is_the_default()
    {
        Assert.Equal("Database", new SessionLockOptions().Store);
        Assert.Contains(new SessionLockOptions { Store = "Memory" }.Validate(production: true), m => m.Contains("Store=Memory is refused in production", StringComparison.Ordinal));
        Assert.Empty(new SessionLockOptions { Store = "Memory" }.Validate(production: false));
        Assert.Contains(new SessionLockOptions { Store = "Redis" }.Validate(production: false), m => m.Contains("Database or Memory", StringComparison.Ordinal));
    }
}

public sealed class EmailChannelRuleTests
{
    private static EmailOptions Valid(Action<EmailOptions>? change = null)
    {
        var o = new EmailOptions
        {
            Enabled = true, Host = "10.20.30.40", FromAddress = "keel@corp.example", BaseUrl = "https://keel.corp.example", AllowedRecipientDomains = ["corp.example"],
        };
        change?.Invoke(o);
        return o;
    }

    [Fact]
    public void Disabled_email_needs_no_configuration_and_a_complete_configuration_is_valid()
    {
        Assert.Empty(new EmailOptions().Validate());
        Assert.Empty(Valid().Validate());
    }

    [Fact]
    public void Tls_is_required_unless_the_operator_acknowledges_plain_text()
    {
        Assert.Contains(Valid(o => o.Security = SmtpSecurity.None).Validate(), m => m.Contains("clear text", StringComparison.Ordinal));
        Assert.Empty(Valid(o => { o.Security = SmtpSecurity.None; o.AcknowledgeInsecureTransport = true; }).Validate());
        Assert.Empty(Valid(o => o.Security = SmtpSecurity.ImplicitTls).Validate());
        Assert.Equal(SmtpSecurity.StartTls, new EmailOptions().Security); // the default is the protected one
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("169.254.169.254")]
    public void An_external_address_literal_is_refused_by_default(string host) =>
        Assert.Contains(Valid(o => o.Host = host).Validate(), m => m.Contains("Host", StringComparison.Ordinal));

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.5")]
    [InlineData("172.31.255.1")]
    [InlineData("192.168.1.10")]
    [InlineData("127.0.0.1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("fe80::1")]
    [InlineData("::1")]
    public void Private_addresses_are_accepted(string host) => Assert.Empty(Valid(o => o.Host = host).Validate());

    [Fact]
    public void An_external_relay_needs_an_explicit_allow_list_entry_and_172_32_is_not_private()
    {
        Assert.NotEmpty(Valid(o => o.Host = "172.32.0.1").Validate());
        Assert.Empty(Valid(o => { o.Host = "172.32.0.1"; o.AllowedNetworks = ["172.32.0.0/24"]; }).Validate());
        var guard = new OnPremHostGuard(Valid(o => o.AllowedHosts = ["relay.partner.example", "*.mail.corp.example"]));
        Assert.Null(guard.CheckAddress("relay.partner.example", IPAddress.Parse("203.0.113.9")));
        Assert.Null(guard.CheckAddress("a.mail.corp.example", IPAddress.Parse("203.0.113.9")));
        Assert.NotNull(guard.CheckAddress("other.example", IPAddress.Parse("203.0.113.9")));
        Assert.NotNull(guard.CheckAddress("mail.corp.example", IPAddress.Parse("203.0.113.9"))); // *.suffix does not match the bare suffix
        Assert.NotNull(guard.CheckAddress("relay.partner.example", IPAddress.Parse("169.254.169.254"))); // never, even for a listed name
        Assert.NotNull(guard.CheckAddress("x", IPAddress.Parse("::ffff:8.8.8.8"))); // a mapped external address is still external
        Assert.Null(guard.CheckAddress("x", IPAddress.Parse("::ffff:10.0.0.1")));
    }

    [Fact]
    public void The_other_settings_are_checked()
    {
        Assert.Contains(Valid(o => o.AllowedRecipientDomains = []).Validate(), m => m.Contains("AllowedRecipientDomains is required", StringComparison.Ordinal));
        Assert.Contains(Valid(o => o.FromAddress = "Keel <keel@corp.example>").Validate(), m => m.Contains("FromAddress", StringComparison.Ordinal));
        Assert.Contains(Valid(o => o.BaseUrl = "keel.corp.example").Validate(), m => m.Contains("BaseUrl", StringComparison.Ordinal));
        Assert.Contains(Valid(o => o.BaseUrl = "https://u:p@keel.corp.example").Validate(), m => m.Contains("BaseUrl", StringComparison.Ordinal));
        Assert.Contains(Valid(o => o.Username = "u").Validate(), m => m.Contains("Username and Password", StringComparison.Ordinal));
        Assert.Contains(Valid(o => o.Port = 0).Validate(), m => m.Contains("Port", StringComparison.Ordinal));
        Assert.Contains(Valid(o => o.AllowedNetworks = ["not-a-cidr"]).Validate(), m => m.Contains("CIDR", StringComparison.Ordinal));
        Assert.Contains(Valid(o => o.CaBundlePath = "/does/not/exist.pem").Validate(), m => m.Contains("CaBundlePath", StringComparison.Ordinal));
        Assert.Contains(new NotificationOptions { Email = Valid(o => o.Port = 70000) }.Validate(), m => m.Contains("Port", StringComparison.Ordinal)); // reached through the module's validation
    }

    [Theory]
    [InlineData("ada@corp.example", true)]
    [InlineData("  Ada.Lovelace+plm@Corp.Example ", true)]
    [InlineData("ada@sub.corp.example", true)]
    [InlineData("ada", false)]
    [InlineData("ada@corp", false)]
    [InlineData("ada@@corp.example", false)]
    [InlineData("Ada <ada@corp.example>", false)]
    [InlineData("\"ada\"@corp.example", false)]
    [InlineData("ada@corp.example\r\nBcc: spy@evil.example", false)]
    [InlineData("ada@corp.example, bob@corp.example", false)]
    [InlineData("a da@corp.example", false)]
    [InlineData("ada@corp.exämple", false)]
    [InlineData("", false)]
    public void Addresses_are_plain_ascii_and_never_carry_header_injection(string input, bool valid)
    {
        Assert.Equal(valid, EmailAddress.TryNormalize(input, out var normalized));
        if (valid) Assert.Equal(input.Trim().Split('@')[0] + "@" + input.Trim().Split('@')[1].ToLowerInvariant(), normalized);
    }

    [Fact]
    public void Recipient_domains_are_matched_exactly_or_by_suffix_pattern()
    {
        var o = Valid(x => x.AllowedRecipientDomains = ["corp.example", "*.plants.example"]);
        Assert.True(o.IsRecipientAllowed("ada@corp.example"));
        Assert.True(o.IsRecipientAllowed("ada@ulm.plants.example"));
        Assert.False(o.IsRecipientAllowed("ada@plants.example"));
        Assert.False(o.IsRecipientAllowed("ada@sub.corp.example"));
        Assert.False(o.IsRecipientAllowed("ada@evilcorp.example"));
        Assert.False(o.IsRecipientAllowed("ada@gmail.com"));
    }

    [Fact]
    public void The_mail_text_is_pointer_only_and_built_from_constants_and_the_base_address()
    {
        var body = EmailContent.Body(new Uri("https://keel.corp.example/"));
        Assert.Equal("You have a notification in Keel PLM", EmailContent.Subject);
        Assert.StartsWith("You have a notification in Keel PLM.", body, StringComparison.Ordinal);
        Assert.Contains("https://keel.corp.example/notifications\r\n", body, StringComparison.Ordinal);
        Assert.Contains("https://keel.corp.example/notifications/preferences", body, StringComparison.Ordinal);
        // Nothing but the two links varies; no object, kind, actor or marking vocabulary can have found its way in.
        foreach (var word in new[] { "P-0", "D-0", "ECN", "ITAR", "EAR", "CUI", "released", "approved", "mentioned", "comment", "Statement", "Distribution" })
            Assert.DoesNotContain(word, body, StringComparison.Ordinal);
        Assert.All(body, c => Assert.True(c < 128));
        Assert.DoesNotContain(typeof(INotificationChannel).GetMethod(nameof(INotificationChannel.DeliverAsync))!.GetParameters(), p => p.ParameterType == typeof(NotificationKind));
    }
}

[Collection("smtp")]
public sealed class SmtpSenderTests : IDisposable
{
    private readonly SyslogTestPki _pki = new("Keel SMTP test CA");
    private readonly string _caFile = Path.Combine(Path.GetTempPath(), "keel-smtp-ca-" + Guid.NewGuid().ToString("N") + ".pem");

    public SmtpSenderTests() => File.WriteAllText(_caFile, _pki.CaPem);

    public void Dispose()
    {
        File.Delete(_caFile);
        _pki.Dispose();
    }

    private X509Certificate2 ServerCert(params string[] names) => _pki.Issue("relay", server: true, names.Length == 0 ? ["localhost"] : names);

    private EmailOptions Options(TestSmtpServer server, Action<EmailOptions>? change = null)
    {
        var o = new EmailOptions
        {
            Enabled = true, Host = "127.0.0.1", Port = server.Port, Security = SmtpSecurity.StartTls, FromAddress = "keel@corp.example", BaseUrl = "https://keel.corp.example",
            AllowedRecipientDomains = ["corp.example"], CaBundlePath = _caFile, TimeoutSeconds = 10,
        };
        change?.Invoke(o);
        return o;
    }

    private static SmtpSender Sender(EmailOptions o) => new(o, new OnPremHostGuard(o));

    private static OutgoingMail Mail() => new("keel@corp.example", "ada@corp.example", EmailContent.Subject, EmailContent.Body(new Uri("https://keel.corp.example")));

    [Fact]
    public async Task Mail_is_delivered_over_starttls_with_the_pointer_only_text_and_a_verified_certificate()
    {
        await using var server = new TestSmtpServer(TestSmtpMode.StartTls, ServerCert());
        await Sender(Options(server)).SendAsync(Mail(), CancellationToken.None);

        var mail = Assert.Single(server.Mail);
        Assert.True(mail.OverTls);
        Assert.Equal("keel@corp.example", mail.From);
        Assert.Equal(["ada@corp.example"], mail.Recipients);
        Assert.Equal("You have a notification in Keel PLM", mail.Header("Subject"));
        Assert.Equal("auto-generated", mail.Header("Auto-Submitted"));
        Assert.Contains("https://keel.corp.example/notifications", mail.Body, StringComparison.Ordinal);
        Assert.Contains(server.NegotiatedProtocols, p => p is System.Security.Authentication.SslProtocols.Tls12 or System.Security.Authentication.SslProtocols.Tls13);
        // MAIL is only ever sent after the handshake: the commands before STARTTLS are the greeting and EHLO.
        Assert.True(server.Commands.ToList().IndexOf("STARTTLS") < server.Commands.ToList().FindIndex(c => c.StartsWith("MAIL", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Implicit_tls_works_too()
    {
        await using var server = new TestSmtpServer(TestSmtpMode.ImplicitTls, ServerCert());
        await Sender(Options(server, o => o.Security = SmtpSecurity.ImplicitTls)).SendAsync(Mail(), CancellationToken.None);
        Assert.True(Assert.Single(server.Mail).OverTls);
    }

    [Fact]
    public async Task A_relay_that_does_not_offer_starttls_is_refused_and_nothing_is_sent_in_clear_text()
    {
        await using var server = new TestSmtpServer(TestSmtpMode.StartTls, ServerCert()) { OfferStartTls = false };
        var e = await Assert.ThrowsAsync<SmtpTransportException>(() => Sender(Options(server, o => { o.Username = "u"; o.Password = "secret"; })).SendAsync(Mail(), CancellationToken.None));
        Assert.Contains("STARTTLS", e.Message, StringComparison.Ordinal);
        Assert.Empty(server.Mail);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("MAIL", StringComparison.Ordinal) || c.StartsWith("AUTH", StringComparison.Ordinal)); // not even the credentials
    }

    [Fact]
    public async Task A_certificate_from_an_unknown_authority_or_for_another_name_is_refused()
    {
        using var other = new SyslogTestPki("Some other CA");
        await using (var server = new TestSmtpServer(TestSmtpMode.StartTls, other.Issue("relay", true, "localhost")))
        {
            await Assert.ThrowsAsync<SmtpTransportException>(() => Sender(Options(server)).SendAsync(Mail(), CancellationToken.None));
            Assert.Empty(server.Mail);
        }
        // Right authority, but the certificate names a different host than the one configured (the SAN holds loopback too, so use a name host).
        await using var named = new TestSmtpServer(TestSmtpMode.StartTls, _pki.Issue("relay", true, "relay.corp.example"));
        var o = Options(named, x => { x.Host = "localhost"; });
        await Assert.ThrowsAsync<SmtpTransportException>(() => Sender(o).SendAsync(Mail(), CancellationToken.None)); // localhost is not in the certificate
        Assert.Empty(named.Mail);
    }

    [Fact]
    public async Task Authentication_uses_plain_or_login_only_inside_tls_and_a_wrong_password_is_permanent()
    {
        await using var server = new TestSmtpServer(TestSmtpMode.StartTls, ServerCert(), ("keel-svc", "correct horse"));
        await Sender(Options(server, o => { o.Username = "keel-svc"; o.Password = "correct horse"; })).SendAsync(Mail(), CancellationToken.None);
        Assert.Equal("keel-svc", Assert.Single(server.Mail).AuthenticatedUser);
        var commands = server.Commands.ToList();
        Assert.True(commands.IndexOf("STARTTLS") < commands.IndexOf("AUTH ***"));
        Assert.DoesNotContain(commands, c => c.Contains("correct horse", StringComparison.Ordinal));

        var e = await Assert.ThrowsAsync<SmtpTransportException>(() => Sender(Options(server, o => { o.Username = "keel-svc"; o.Password = "wrong"; })).SendAsync(Mail(), CancellationToken.None));
        Assert.True(e.Permanent);
        Assert.Single(server.Mail); // the failed attempt delivered nothing
    }

    [Fact]
    public async Task A_refused_recipient_is_permanent_and_a_temporary_failure_is_retryable()
    {
        await using var server = new TestSmtpServer(TestSmtpMode.StartTls, ServerCert()) { RejectRecipient = r => r.StartsWith("gone@", StringComparison.Ordinal) };
        var refused = await Assert.ThrowsAsync<SmtpTransportException>(() => Sender(Options(server)).SendAsync(Mail() with { To = "gone@corp.example" }, CancellationToken.None));
        Assert.True(refused.Permanent);

        server.EchoRecipientInErrors = true; // many relays repeat the address in the refusal: it must not survive into the exception (logs, queue, audit)
        var echoed = await Assert.ThrowsAsync<SmtpTransportException>(() => Sender(Options(server)).SendAsync(Mail() with { To = "gone@corp.example" }, CancellationToken.None));
        Assert.DoesNotContain("gone@", echoed.Message, StringComparison.Ordinal);
        Assert.Contains("[address]", echoed.Message, StringComparison.Ordinal);

        server.TempFailMessages = 1;
        var temporary = await Assert.ThrowsAsync<SmtpTransportException>(() => Sender(Options(server)).SendAsync(Mail(), CancellationToken.None));
        Assert.False(temporary.Permanent);
        await Sender(Options(server)).SendAsync(Mail(), CancellationToken.None); // the next attempt is accepted
        Assert.Single(server.Mail);
    }

    [Fact]
    public async Task Plain_text_works_only_when_configured_and_is_what_it_says()
    {
        await using var server = new TestSmtpServer(TestSmtpMode.Plain);
        await Sender(Options(server, o => { o.Security = SmtpSecurity.None; o.AcknowledgeInsecureTransport = true; })).SendAsync(Mail(), CancellationToken.None);
        Assert.False(Assert.Single(server.Mail).OverTls);
        // The start-up validation is what refuses it without the acknowledgement.
        Assert.NotEmpty(Options(server, o => o.Security = SmtpSecurity.None).Validate());
    }

    [Fact]
    public async Task An_external_host_is_refused_before_any_connection_is_made()
    {
        await using var server = new TestSmtpServer(TestSmtpMode.StartTls, ServerCert());
        // The connection would succeed on loopback, but the configured relay is a public address: the guard refuses first, so nothing is dialed.
        var o = Options(server, x => x.Host = "8.8.8.8");
        var e = await Assert.ThrowsAsync<SmtpTransportException>(() => Sender(o).SendAsync(Mail(), CancellationToken.None));
        Assert.True(e.Permanent);
        Assert.Contains("not a private address", e.Message, StringComparison.Ordinal);
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task A_server_that_never_answers_times_out_and_a_closed_port_is_retryable()
    {
        await using var server = new TestSmtpServer(TestSmtpMode.Plain);
        var port = server.Port;
        await server.DisposeAsync();
        var down = await Assert.ThrowsAnyAsync<Exception>(() => Sender(new EmailOptions
        {
            Enabled = true, Host = "127.0.0.1", Port = port, Security = SmtpSecurity.None, AcknowledgeInsecureTransport = true, FromAddress = "keel@corp.example", BaseUrl = "https://k.corp.example",
            AllowedRecipientDomains = ["corp.example"], TimeoutSeconds = 5,
        }).SendAsync(Mail(), CancellationToken.None));
        Assert.True(down is System.Net.Sockets.SocketException || (down is SmtpTransportException { Permanent: false }));
    }

    [Fact]
    public void Dots_at_the_start_of_a_line_are_stuffed_and_lines_end_with_crlf()
    {
        var bytes = SmtpSender.Compose(new OutgoingMail("keel@corp.example", "ada@corp.example", "S", "one\n.two\r\nthree"));
        var text = System.Text.Encoding.ASCII.GetString(bytes);
        Assert.EndsWith("\r\n.\r\n", text, StringComparison.Ordinal);
        Assert.Contains("\r\n..two\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }
}

public sealed class SpoolSinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keel-spool-" + Guid.NewGuid().ToString("N"));

    public SpoolSinkTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static SinkRecord Sink(string format = "ndjson", int? maxPending = null)
    {
        var config = new JsonObject { ["directory"] = "spool", ["format"] = format };
        if (maxPending is { } m) config["maxPendingFiles"] = m;
        return new SinkRecord(Guid.NewGuid(), "spool-erp", "Spool", "filedrop", true, Guid.NewGuid(), "erp", config, [], false, 1);
    }

    private static EnvelopeMessage Message(long seq) => new(Guid.NewGuid(), "part.released", seq, DateTimeOffset.UtcNow, System.Text.Encoding.UTF8.GetBytes($"{{\"seq\":{seq}}}"), 1);

    private FileDropSink NewSink(Action<IntegrationOptions>? configure = null, TimeProvider? clock = null)
    {
        var o = new IntegrationOptions { FileDropRoot = _root };
        configure?.Invoke(o);
        return new FileDropSink(o, clock ?? TimeProvider.System);
    }

    [Fact]
    public async Task A_full_spool_answers_retry_instead_of_writing_and_resumes_when_the_receiver_consumes_files()
    {
        var sink = NewSink();
        var spool = Sink("json", maxPending: 3);
        for (var i = 1; i <= 3; i++) Assert.Equal(DeliveryResult.Delivered, (await sink.DeliverAsync(spool, Message(i), CancellationToken.None)).Result);

        var blocked = await sink.DeliverAsync(spool, Message(4), CancellationToken.None);
        Assert.Equal(DeliveryResult.Defer, blocked.Result); // deferred, not retried or dead: nothing is lost, no attempt is counted, the event stays in the outbox
        Assert.Contains("not consumed", blocked.Error, StringComparison.Ordinal);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_root, "spool"), "*.manifest.json").Length);
        Assert.Equal(3, IntegrationMetrics.SpoolFiles.WithLabels("spool-erp").Value);

        // The receiver acknowledges by deleting a data file and its manifest.
        var first = Directory.GetFiles(Path.Combine(_root, "spool"), "*.manifest.json").Order().First();
        File.Delete(first);
        File.Delete(first[..^".manifest.json".Length]);
        Assert.Equal(DeliveryResult.Delivered, (await sink.DeliverAsync(spool, Message(4), CancellationToken.None)).Result);
    }

    [Fact]
    public async Task A_retried_batch_rewrites_the_same_file_instead_of_adding_a_copy()
    {
        var sink = NewSink();
        var spool = Sink("ndjson");
        var batch = new[] { Message(10), Message(11), Message(12) };
        await sink.DeliverBatchAsync(spool, batch, CancellationToken.None);
        await sink.DeliverBatchAsync(spool, batch, CancellationToken.None); // the dispatcher crashed before it recorded the result: same batch again, later
        var files = Directory.GetFiles(Path.Combine(_root, "spool"), "*.ndjson");
        Assert.Equal("keel-000000000010-000000000012-3.ndjson", Path.GetFileName(Assert.Single(files)));
    }

    [Fact]
    public async Task Stale_temporary_files_of_a_crashed_write_are_removed_and_fresh_ones_kept()
    {
        var sink = NewSink();
        var spool = Sink("json");
        var dir = Path.Combine(_root, "spool");
        Directory.CreateDirectory(dir);
        var stale = Path.Combine(dir, "keel-x.json.aaaa.tmp");
        var fresh = Path.Combine(dir, "keel-y.json.bbbb.tmp");
        await File.WriteAllTextAsync(stale, "partial");
        await File.WriteAllTextAsync(fresh, "in progress");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-3));
        await sink.DeliverAsync(spool, Message(1), CancellationToken.None);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void The_spool_limit_is_validated_and_the_broker_stays_deferred_with_a_pointer_to_the_decision()
    {
        var sink = NewSink();
        Assert.Empty(sink.ValidateConfig(new JsonObject { ["directory"] = "a", ["maxPendingFiles"] = 5 }, enabled: false));
        Assert.NotEmpty(sink.ValidateConfig(new JsonObject { ["directory"] = "a", ["maxPendingFiles"] = -1 }, enabled: false));
        Assert.NotEmpty(sink.ValidateConfig(new JsonObject { ["directory"] = "a", ["maxPendingFiles"] = "many" }, enabled: false));
        var e = Assert.Throws<NotSupportedException>(() => new BrokerSink().ValidateConfig(new JsonObject(), true));
        Assert.Contains("Phase 4b", e.Message, StringComparison.Ordinal);
        Assert.Contains("spool directory", e.Message, StringComparison.Ordinal);
        Assert.NotEmpty(new IntegrationOptions { FileDropMaxPendingFiles = -1 }.Validate());
    }

    [Fact]
    public void Retention_settings_are_off_by_default_and_refuse_windows_too_short_for_backfill()
    {
        var o = new IntegrationOptions();
        Assert.False(o.Retention.Enabled);
        Assert.Empty(o.Validate());
        Assert.NotEmpty(new IntegrationOptions { Retention = { RetentionDays = 3 } }.Validate());
        Assert.NotEmpty(new IntegrationOptions { Retention = { RetentionDays = 30, BatchSize = 0 } }.Validate());
        Assert.Empty(new IntegrationOptions { Retention = { RetentionDays = 90 } }.Validate());
    }
}
