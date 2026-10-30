using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Core;
using Keel.Notifications;
using Keel.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// E-mail address verification and bounce handling against a real PostgreSQL and an in-process relay (ADR-0055): a one-time, expiring, rate-limited link before any
/// notification mail, no enumeration through the answers, suppression after hard bounces or a complaint from the REST endpoint and the mailbox reader, the
/// administrator's view and the re-verify flow.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed partial class EmailVerificationTests(PostgresFixture pg) : IDisposable
{
    private const string TagSecret = "tag-secret-0123456789abcdef0123456789abcdef";
    private readonly SyslogTestPki _pki = new("Keel verification test CA");
    private readonly string _caFile = Path.Combine(Path.GetTempPath(), "keel-verify-ca-" + Guid.NewGuid().ToString("N") + ".pem");
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        File.Delete(_caFile);
        _pki.Dispose();
        foreach (var d in _dirs) if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
    }

    private sealed record Rig(AwarenessEnv Env, TestSmtpServer Smtp, EmailDispatchService Dispatch, EmailPreferenceService Prefs, EmailVerificationService Verify, EmailBounceService Bounces, string? Mailbox)
    {
        public Task<long> CountAsync(string sql) => ScalarAsync(Env, sql);
        public async Task<string> TextAsync(string sql)
        {
            await using var conn = new NpgsqlConnection(Env.Db.SuperuserConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            return Convert.ToString(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
    }

    private static async Task<long> ScalarAsync(AwarenessEnv env, string sql)
    {
        await using var conn = new NpgsqlConnection(env.Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<Rig> NewRigAsync(bool mailbox = false, Action<EmailOptions>? configure = null)
    {
        await File.WriteAllTextAsync(_caFile, _pki.CaPem);
        var smtp = new TestSmtpServer(TestSmtpMode.StartTls, _pki.Issue("relay", true, "localhost"));
        string? box = null;
        if (mailbox)
        {
            box = Directory.CreateTempSubdirectory("keel-bounce-box-").FullName;
            _dirs.Add(box);
        }
        var env = await AwarenessEnv.CreateAsync(pg, o =>
        {
            o.Email = new EmailOptions
            {
                Enabled = true, Host = "127.0.0.1", Port = smtp.Port, Security = SmtpSecurity.StartTls, FromAddress = "keel@corp.example", BaseUrl = "https://keel.corp.example",
                AllowedRecipientDomains = ["corp.example"], CaBundlePath = _caFile, MinIntervalMinutes = 10, MaxAttempts = 3, TimeoutSeconds = 10,
                Bounces = { TagSecret = TagSecret, MailboxDirectory = box ?? "", MailboxSettleSeconds = 0 },
            };
            configure?.Invoke(o.Email);
        });
        var sender = new SmtpSender(env.Options.Email, new OnPremHostGuard(env.Options.Email));
        var bounces = new EmailBounceService(env.Ds, env.Options, env.Audit, env.Clock);
        var channel = new EmailNotificationChannel(env.Ds, env.Options, sender);
        return new Rig(env, smtp, new EmailDispatchService(env.Ds, channel, env.Options, env.Audit, env.Clock, NullLogger<EmailDispatchService>.Instance, bounces),
            new EmailPreferenceService(env.Ds, env.Options, env.Audit, env.Clock), new EmailVerificationService(env.Ds, env.Options, sender, env.Audit, env.Clock), bounces, box);
    }

    [GeneratedRegex(@"/notifications/verify-email/([A-Za-z0-9_\-]+)")]
    private static partial Regex LinkPattern();

    private static string TokenIn(ReceivedMail mail) => LinkPattern().Match(mail.Data).Groups[1].Value;

    private static async Task<List<AuditRecord>> AuditAsync(AwarenessEnv env) => [.. await new AuditQueries(env.Ds).ListAsync(new AuditQuery(Limit: 5000))];

    private static async Task SaveAsync(Rig rig, Subject who, string address = "rita@corp.example") => Assert.Equal(EmailSaveResult.Saved, await rig.Prefs.SetAsync(who, true, address));

    /// <summary>Saves the address, asks for the link, waits for the mail and returns its token.</summary>
    private static async Task<string> RequestAsync(Rig rig, Subject who, string address = "rita@corp.example")
    {
        var before = rig.Smtp.Mail.Count;
        await SaveAsync(rig, who, address);
        Assert.Equal(VerificationRequestResult.Sent, await rig.Verify.RequestAsync(who));
        Assert.True(await rig.Smtp.WaitForMailAsync(before + 1, TimeSpan.FromSeconds(5)));
        return TokenIn(rig.Smtp.Mail[^1]);
    }

    private static async Task NotifyAsync(Rig rig, string name = "Bracket, sensor") => await rig.Env.StartReviewAsync(await rig.Env.NewPartAsync(name));

    // ---------------------------------------------------------------- verification

    [Fact]
    public async Task No_notification_mail_goes_to_an_unverified_address_and_one_pointer_mail_goes_after_the_link_is_confirmed()
    {
        var rig = await NewRigAsync();
        await using var _ = rig.Smtp;
        await SaveAsync(rig, AwarenessEnv.Rita);
        var settings = await rig.Prefs.GetAsync(AwarenessEnv.Rita);
        Assert.False(settings.AddressVerified);

        await NotifyAsync(rig); // rita is asked to review, but her address is not confirmed: nothing is queued, nothing sent
        Assert.Equal(0L, await rig.CountAsync("SELECT count(*) FROM ntf.email_queue"));
        Assert.Equal(0, await rig.Dispatch.RunOnceAsync(CancellationToken.None));
        Assert.Empty(rig.Smtp.Mail);

        Assert.Equal(VerificationRequestResult.Sent, await rig.Verify.RequestAsync(AwarenessEnv.Rita));
        var mail = Assert.Single(rig.Smtp.Mail);
        Assert.Equal(["rita@corp.example"], mail.Recipients);
        Assert.Equal(VerificationMailContent.Subject, mail.Header("Subject"));
        Assert.Matches("^[0-9a-f]{32}$", mail.Header("X-Keel-Bounce-Tag"));
        Assert.True((await rig.Prefs.GetAsync(AwarenessEnv.Rita)).VerificationPending);
        var token = TokenIn(mail);
        Assert.Equal(43, token.Length);

        Assert.Equal(VerificationConfirmResult.Confirmed, await rig.Verify.ConfirmAsync(AwarenessEnv.Rita, token));
        var after = await rig.Prefs.GetAsync(AwarenessEnv.Rita);
        Assert.True(after.AddressVerified);
        Assert.False(after.VerificationPending);

        await NotifyAsync(rig, "Second bracket");
        Assert.Equal(1, await rig.Dispatch.RunOnceAsync(CancellationToken.None));
        Assert.Equal(2, rig.Smtp.Mail.Count);
        Assert.Equal("You have a notification in Keel PLM", rig.Smtp.Mail[1].Header("Subject")); // the pointer-only content rule is unchanged
        Assert.DoesNotContain("Second bracket", rig.Smtp.Mail[1].Data, StringComparison.Ordinal);
        Assert.DoesNotContain("verify-email", rig.Smtp.Mail[1].Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_token_is_stored_only_as_a_hash_and_neither_it_nor_the_address_reaches_the_audit_log()
    {
        var rig = await NewRigAsync();
        await using var _ = rig.Smtp;
        var token = await RequestAsync(rig, AwarenessEnv.Rita);
        Assert.Equal(1L, await rig.CountAsync("SELECT count(*) FROM ntf.email_verification WHERE token_hash ~ '^[0-9a-f]{64}$'"));
        Assert.Equal(0L, await rig.CountAsync($"SELECT count(*) FROM ntf.email_verification WHERE token_hash = '{token}' OR user_sub = '{token}' OR address = '{token}'"));
        Assert.Equal(VerificationConfirmResult.Confirmed, await rig.Verify.ConfirmAsync(AwarenessEnv.Rita, token));
        var audit = await AuditAsync(rig.Env);
        Assert.Contains(audit, a => a.Action == "notification.email.verification_sent" && a.Outcome == AuditOutcome.Success);
        Assert.Contains(audit, a => a.Action == "notification.email.verified" && a.Outcome == AuditOutcome.Success);
        Assert.DoesNotContain(audit, a => a.Payload.Contains("rita@corp.example", StringComparison.OrdinalIgnoreCase) || a.Payload.Contains(token, StringComparison.Ordinal));
        // SQL oracle: nothing in the audit table holds the address or the token either.
        Assert.Equal(0L, await rig.CountAsync($"SELECT count(*) FROM audit.event WHERE payload::text ILIKE '%rita@corp%' OR payload::text LIKE '%{token}%'"));
    }

    [Fact]
    public async Task A_link_works_once_only_for_its_user_and_dies_with_a_new_link_an_expiry_or_a_changed_address()
    {
        var rig = await NewRigAsync();
        await using var _ = rig.Smtp;
        var first = await RequestAsync(rig, AwarenessEnv.Rita);
        // Someone else who has the link (a forwarded mail) cannot use it: the answer is the generic one.
        Assert.Equal(VerificationConfirmResult.Invalid, await rig.Verify.ConfirmAsync(AwarenessEnv.Bob, first));
        Assert.False((await rig.Prefs.GetAsync(AwarenessEnv.Rita)).AddressVerified);
        Assert.Equal(VerificationConfirmResult.Invalid, await rig.Verify.ConfirmAsync(AwarenessEnv.Rita, "does-not-exist-0123456789abcdef0123456789a"));
        Assert.Equal(VerificationConfirmResult.Invalid, await rig.Verify.ConfirmAsync(AwarenessEnv.Rita, "bad token!"));
        Assert.Equal(VerificationConfirmResult.Invalid, await rig.Verify.ConfirmAsync(AwarenessEnv.Rita, null));

        // A newer link replaces the older one.
        rig.Env.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(VerificationRequestResult.Sent, await rig.Verify.RequestAsync(AwarenessEnv.Rita));
        var second = TokenIn(rig.Smtp.Mail[^1]);
        Assert.Equal(VerificationConfirmResult.Invalid, await rig.Verify.ConfirmAsync(AwarenessEnv.Rita, first));
        Assert.Equal(VerificationConfirmResult.Confirmed, await rig.Verify.ConfirmAsync(AwarenessEnv.Rita, second));
        Assert.Equal(VerificationConfirmResult.Invalid, await rig.Verify.ConfirmAsync(AwarenessEnv.Rita, second)); // single use

        // An expired link.
        rig.Env.Clock.Advance(TimeSpan.FromMinutes(2));
        await SaveAsync(rig, AwarenessEnv.Rita, "rita.new@corp.example");
        Assert.False((await rig.Prefs.GetAsync(AwarenessEnv.Rita)).AddressVerified); // a changed address is not verified
        Assert.Equal(VerificationRequestResult.Sent, await rig.Verify.RequestAsync(AwarenessEnv.Rita));
        var third = TokenIn(rig.Smtp.Mail[^1]);
        rig.Env.Clock.Advance(TimeSpan.FromMinutes(1441));
        Assert.Equal(VerificationConfirmResult.Invalid, await rig.Verify.ConfirmAsync(AwarenessEnv.Rita, third));

        // A link for the old address does not verify a newly saved one.
        rig.Env.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(VerificationRequestResult.Sent, await rig.Verify.RequestAsync(AwarenessEnv.Rita));
        var fourth = TokenIn(rig.Smtp.Mail[^1]);
        await SaveAsync(rig, AwarenessEnv.Rita, "rita.other@corp.example");
        Assert.Equal(VerificationConfirmResult.Invalid, await rig.Verify.ConfirmAsync(AwarenessEnv.Rita, fourth));
        Assert.False((await rig.Prefs.GetAsync(AwarenessEnv.Rita)).AddressVerified);
        var failures = (await AuditAsync(rig.Env)).Count(a => a.Action == "notification.email.verify_failed");
        Assert.True(failures >= 6, failures.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Requests_are_rate_limited_per_user_and_silently_per_address_and_a_suppressed_address_answers_like_a_sent_one()
    {
        var rig = await NewRigAsync(configure: e => { e.Verification.MinIntervalSeconds = 60; e.Verification.MaxPerUserPerDay = 3; e.Verification.MaxPerAddressPerDay = 3; });
        await using var _ = rig.Smtp;
        await SaveAsync(rig, AwarenessEnv.Rita);
        Assert.Equal(VerificationRequestResult.Sent, await rig.Verify.RequestAsync(AwarenessEnv.Rita));
        Assert.Equal(VerificationRequestResult.RateLimited, await rig.Verify.RequestAsync(AwarenessEnv.Rita)); // too soon
        Assert.Single(rig.Smtp.Mail);
        rig.Env.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(VerificationRequestResult.Sent, await rig.Verify.RequestAsync(AwarenessEnv.Rita));
        rig.Env.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(VerificationRequestResult.Sent, await rig.Verify.RequestAsync(AwarenessEnv.Rita));
        rig.Env.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(VerificationRequestResult.RateLimited, await rig.Verify.RequestAsync(AwarenessEnv.Rita)); // three a day
        Assert.Equal(3, rig.Smtp.Mail.Count);
        Assert.Contains(await AuditAsync(rig.Env), a => a.Action == "notification.email.verification_throttled" && a.Outcome == AuditOutcome.Failure);

        // Another user types the same address: the per-address limit is reached, yet the answer is "sent" and nothing is mailed (the caller learns nothing about who asked).
        await SaveAsync(rig, AwarenessEnv.Bob, "rita@corp.example");
        Assert.Equal(VerificationRequestResult.Sent, await rig.Verify.RequestAsync(AwarenessEnv.Bob));
        Assert.Equal(3, rig.Smtp.Mail.Count);
        Assert.Equal(0L, await rig.CountAsync($"SELECT count(*) FROM ntf.email_verification WHERE user_sub = '{AwarenessEnv.Bob.Id}'"));

        // A suppressed address gets the same answer and no mail.
        await SaveAsync(rig, AwarenessEnv.Carol, "dead@corp.example");
        await rig.Bounces.RecordAsync(ActorSnapshot.System, [new BounceReport("dead@corp.example", BounceKinds.Complaint)], BounceSources.Api, null);
        Assert.Equal(VerificationRequestResult.Sent, await rig.Verify.RequestAsync(AwarenessEnv.Carol));
        Assert.Equal(3, rig.Smtp.Mail.Count);
    }

    [Fact]
    public async Task A_relay_that_refuses_the_link_mail_voids_the_link_reports_failure_and_stores_no_address_in_the_error()
    {
        var rig = await NewRigAsync();
        await using var _ = rig.Smtp;
        rig.Smtp.RejectRecipient = a => a.Contains("rita", StringComparison.Ordinal);
        rig.Smtp.EchoRecipientInErrors = true;
        await SaveAsync(rig, AwarenessEnv.Rita);
        Assert.Equal(VerificationRequestResult.SendFailed, await rig.Verify.RequestAsync(AwarenessEnv.Rita));
        Assert.Empty(rig.Smtp.Mail);
        Assert.Equal(0L, await rig.CountAsync("SELECT count(*) FROM ntf.email_verification WHERE invalidated_at IS NULL"));
        Assert.Contains(await AuditAsync(rig.Env), a => a.Action == "notification.email.verification_sent" && a.Outcome == AuditOutcome.Failure);
        Assert.Equal(VerificationRequestResult.AddressRequired, await rig.Verify.RequestAsync(AwarenessEnv.Mallory)); // nothing saved yet: nothing to verify
    }

    [Fact]
    public async Task The_channel_being_off_or_the_address_missing_is_reported_without_sending()
    {
        var rig = await NewRigAsync();
        await using var _ = rig.Smtp;
        Assert.Equal(VerificationRequestResult.AddressRequired, await rig.Verify.RequestAsync(AwarenessEnv.Rita));
        await SaveAsync(rig, AwarenessEnv.Rita);
        var token = await RequestAsync(rig, AwarenessEnv.Rita);
        Assert.Equal(VerificationConfirmResult.Confirmed, await rig.Verify.ConfirmAsync(AwarenessEnv.Rita, token));
        Assert.Equal(VerificationRequestResult.AlreadyVerified, await rig.Verify.RequestAsync(AwarenessEnv.Rita));
        var off = await NewRigAsync(configure: e => e.Enabled = false);
        await using var __ = off.Smtp;
        Assert.Equal(VerificationRequestResult.ChannelUnavailable, await off.Verify.RequestAsync(AwarenessEnv.Rita));
    }

    // ---------------------------------------------------------------- bounces

    [Fact]
    public async Task Two_hard_bounces_suppress_an_address_soft_bounces_never_do_and_unknown_addresses_are_dropped()
    {
        var rig = await NewRigAsync();
        await using var _ = rig.Smtp;
        var token = await RequestAsync(rig, AwarenessEnv.Rita);
        Assert.Equal(VerificationConfirmResult.Confirmed, await rig.Verify.ConfirmAsync(AwarenessEnv.Rita, token));
        var actor = ActorSnapshot.From(AwarenessEnv.Ada);

        var soft = await rig.Bounces.RecordAsync(actor, [new BounceReport("rita@corp.example", BounceKinds.Soft, "4.2.2", "mailbox full"), new BounceReport("RITA@Corp.Example", BounceKinds.Soft)], BounceSources.Api, "c1");
        Assert.Equal(new BounceBatchResult(2, 0, 0), soft.Value);
        var firstHard = await rig.Bounces.RecordAsync(actor, [new BounceReport("rita@corp.example", BounceKinds.Hard, "5.1.1", "550 <rita@corp.example> user unknown")], BounceSources.Api, "c2");
        Assert.Equal(new BounceBatchResult(1, 0, 0), firstHard.Value);
        Assert.True((await rig.Prefs.GetAsync(AwarenessEnv.Rita)).AddressVerified); // one hard bounce is not enough
        var secondHard = await rig.Bounces.RecordAsync(actor, [new BounceReport("rita@corp.example", BounceKinds.Hard, "5.1.1")], BounceSources.Api, "c3");
        Assert.Equal(new BounceBatchResult(1, 0, 1), secondHard.Value);

        var after = await rig.Prefs.GetAsync(AwarenessEnv.Rita);
        Assert.False(after.AddressVerified);
        Assert.True(after.AddressSuppressed); // the user is told, because it was her confirmed address
        var unknown = await rig.Bounces.RecordAsync(actor, [new BounceReport("stranger@corp.example", BounceKinds.Hard), new BounceReport("not an address", BounceKinds.Hard)], BounceSources.Api, null);
        Assert.Equal(new BounceBatchResult(0, 2, 0), unknown.Value);
        Assert.Equal(0L, await rig.CountAsync("SELECT count(*) FROM ntf.email_address_health WHERE address LIKE 'stranger%'")); // the tables hold only addresses of this system
        Assert.Equal(OutcomeKind.Invalid, (await rig.Bounces.RecordAsync(actor, [new BounceReport("rita@corp.example", "weird")], BounceSources.Api, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await rig.Bounces.RecordAsync(actor, [], BounceSources.Api, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await rig.Bounces.RecordAsync(actor, [new BounceReport("rita@corp.example", BounceKinds.Hard, "9.9.9")], BounceSources.Api, null)).Kind);

        // Nothing more is queued or sent to a suppressed address, even for a user who was verified.
        await NotifyAsync(rig);
        Assert.Equal(0L, await rig.CountAsync("SELECT count(*) FROM ntf.email_queue"));
        var audit = await AuditAsync(rig.Env);
        var suppressed = Assert.Single(audit, a => a.Action == "notification.email.suppressed");
        Assert.DoesNotContain("rita@", suppressed.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, audit.Count(a => a.Action == "notification.email.bounce_report" && a.Outcome == AuditOutcome.Success)); // one per accepted batch; refused requests record nothing
    }

    [Fact]
    public async Task A_complaint_suppresses_at_once_a_queued_mail_is_skipped_and_the_administrator_sees_and_lifts_it_so_the_user_verifies_again()
    {
        var rig = await NewRigAsync();
        await using var _ = rig.Smtp;
        var token = await RequestAsync(rig, AwarenessEnv.Rita);
        await rig.Verify.ConfirmAsync(AwarenessEnv.Rita, token);
        await NotifyAsync(rig);
        Assert.Equal(1L, await rig.CountAsync("SELECT count(*) FROM ntf.email_queue WHERE status = 'pending'")); // queued while the address was good

        var report = await rig.Bounces.RecordAsync(ActorSnapshot.System, [new BounceReport("rita@corp.example", BounceKinds.Complaint, null, "Feedback report: abuse")], BounceSources.Api, null);
        Assert.Equal(1, report.Value!.Suppressed);
        Assert.Equal(1, await rig.Dispatch.RunOnceAsync(CancellationToken.None));
        Assert.Single(rig.Smtp.Mail); // only the verification mail: the notification mail was skipped at send time
        Assert.Equal(1L, await rig.CountAsync("SELECT count(*) FROM ntf.email_queue WHERE status = 'skipped'"));

        // Administrator view: the address, the reason, the users and the latest reports; others are refused and audited.
        var denied = await rig.Bounces.GetHealthAsync(AwarenessEnv.Alice);
        Assert.Equal(OutcomeKind.Forbidden, denied.Kind);
        Assert.Contains(await AuditAsync(rig.Env), a => a.Action == "notification.email.health_list" && a.Outcome == AuditOutcome.Denied);
        var health = (await rig.Bounces.GetHealthAsync(AwarenessEnv.Ada)).Value!;
        var row = Assert.Single(health.Suppressed);
        Assert.Equal(("rita@corp.example", "complaint", 1), (row.Address, row.Reason, row.Users));
        Assert.Contains(health.Recent, e => e.Kind == "complaint" && e.Source == "api" && e.Diagnostic == "Feedback report: abuse");
        Assert.Equal(OutcomeKind.Forbidden, (await rig.Bounces.ClearSuppressionAsync(AwarenessEnv.Alice, "rita@corp.example", null)).Kind);

        // Lifting it makes the user confirm again: the verification is gone, mail resumes only after a new link is used.
        var lifted = await rig.Bounces.ClearSuppressionAsync(AwarenessEnv.Ada, "RITA@corp.example", "c9");
        Assert.Equal(1, lifted.Value);
        Assert.Equal(OutcomeKind.NotFound, (await rig.Bounces.ClearSuppressionAsync(AwarenessEnv.Ada, "rita@corp.example", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await rig.Bounces.ClearSuppressionAsync(AwarenessEnv.Ada, "nonsense", null)).Kind);
        Assert.False((await rig.Prefs.GetAsync(AwarenessEnv.Rita)).AddressVerified);
        await NotifyAsync(rig, "After lift");
        Assert.Equal(0, await rig.Dispatch.RunOnceAsync(CancellationToken.None)); // unverified: nothing queued
        rig.Env.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(VerificationRequestResult.Sent, await rig.Verify.RequestAsync(AwarenessEnv.Rita));
        Assert.Equal(VerificationConfirmResult.Confirmed, await rig.Verify.ConfirmAsync(AwarenessEnv.Rita, TokenIn(rig.Smtp.Mail[^1])));
        await NotifyAsync(rig, "After verify");
        Assert.Equal(1, await rig.Dispatch.RunOnceAsync(CancellationToken.None));
        var audit = await AuditAsync(rig.Env);
        Assert.Contains(audit, a => a.Action == "notification.email.suppression_cleared" && a.Outcome == AuditOutcome.Success && a.Actor != ActorSnapshot.System.Id);
        Assert.DoesNotContain(audit, a => a.Payload.Contains("rita@corp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Old_bookkeeping_rows_are_purged_and_suppressions_are_kept()
    {
        var rig = await NewRigAsync();
        await using var _ = rig.Smtp;
        await RequestAsync(rig, AwarenessEnv.Rita);
        await rig.Bounces.RecordAsync(ActorSnapshot.System, [new BounceReport("rita@corp.example", BounceKinds.Hard)], BounceSources.Api, null);
        await rig.Bounces.RecordAsync(ActorSnapshot.System, [new BounceReport("rita@corp.example", BounceKinds.Complaint)], BounceSources.Api, null);
        rig.Env.Clock.Advance(TimeSpan.FromDays(100));
        await rig.Bounces.PurgeAsync(CancellationToken.None);
        Assert.Equal(0L, await rig.CountAsync("SELECT count(*) FROM ntf.email_verification"));
        Assert.Equal(0L, await rig.CountAsync("SELECT count(*) FROM ntf.email_bounce_event"));
        Assert.Equal(1L, await rig.CountAsync("SELECT count(*) FROM ntf.email_address_health WHERE suppressed_at IS NOT NULL"));
    }

    // ---------------------------------------------------------------- mailbox reader

    private static string Dsn(string address, string tag, string status = "5.1.1") =>
        "From: MAILER-DAEMON@corp.example\r\nTo: keel@corp.example\r\nSubject: Undelivered Mail Returned to Sender\r\nContent-Type: multipart/report; report-type=delivery-status; boundary=\"B\"\r\n\r\n" +
        "--B\r\nContent-Type: text/plain\r\n\r\nUndelivered\r\n" +
        $"--B\r\nContent-Type: message/delivery-status\r\n\r\nReporting-MTA: dns; relay.corp.example\r\n\r\nFinal-Recipient: rfc822; {address}\r\nAction: failed\r\nStatus: {status}\r\nDiagnostic-Code: smtp; 550 no such user {address}\r\n\r\n" +
        $"--B\r\nContent-Type: text/rfc822-headers\r\n\r\nFrom: keel@corp.example\r\nTo: {address}\r\nX-Keel-Bounce-Tag: {tag}\r\n\r\n--B--\r\n";

    [Fact]
    public async Task The_mailbox_reader_suppresses_on_tagged_bounces_and_rejects_untagged_forged_or_unreadable_mail()
    {
        var rig = await NewRigAsync(mailbox: true);
        await using var _ = rig.Smtp;
        await SaveAsync(rig, AwarenessEnv.Rita);
        await SaveAsync(rig, AwarenessEnv.Bob, "bob@corp.example");
        var reader = new BounceMailboxReader(rig.Bounces, rig.Env.Options, rig.Env.Clock, NullLogger<BounceMailboxReader>.Instance);
        var box = rig.Mailbox!;
        Directory.CreateDirectory(Path.Combine(box, "new"));
        var tag = BounceTags.Compute(TagSecret, "rita@corp.example");

        await File.WriteAllTextAsync(Path.Combine(box, "new", "1.eml"), Dsn("rita@corp.example", tag));
        await File.WriteAllTextAsync(Path.Combine(box, "2.eml"), Dsn("rita@corp.example", tag));
        Assert.Equal(2, await reader.RunOnceAsync(CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(box, "*.eml"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(box, "new")));
        Assert.Equal(1L, await rig.CountAsync("SELECT count(*) FROM ntf.email_address_health WHERE address = 'rita@corp.example' AND suppressed_at IS NOT NULL AND hard_bounces = 2"));
        Assert.Equal(2L, await rig.CountAsync("SELECT count(*) FROM ntf.email_bounce_event WHERE source = 'mailbox' AND status_code = '5.1.1' AND diagnostic NOT LIKE '%@%'"));

        // Forgeries and junk: Rita's tag does not vouch for Bob; no tag; wrong secret; a plain mail; an oversize file. All end in "rejected" and change nothing.
        await File.WriteAllTextAsync(Path.Combine(box, "forged.eml"), Dsn("bob@corp.example", tag));
        await File.WriteAllTextAsync(Path.Combine(box, "notag.eml"), Dsn("bob@corp.example", ""));
        await File.WriteAllTextAsync(Path.Combine(box, "wrong.eml"), Dsn("bob@corp.example", BounceTags.Compute("another-secret-another-secret-another", "bob@corp.example")));
        await File.WriteAllTextAsync(Path.Combine(box, "plain.eml"), "From: x@y.example\r\nSubject: out of office\r\n\r\nI am away.");
        await File.WriteAllTextAsync(Path.Combine(box, "big.eml"), new string('x', DsnParser.MaxBytes + 10));
        await File.WriteAllTextAsync(Path.Combine(box, ".hidden"), "ignored");
        Assert.Equal(5, await reader.RunOnceAsync(CancellationToken.None));
        Assert.Equal(5, Directory.EnumerateFiles(Path.Combine(box, "rejected")).Count());
        Assert.True(File.Exists(Path.Combine(box, ".hidden")));
        Assert.Equal(0L, await rig.CountAsync("SELECT count(*) FROM ntf.email_address_health WHERE address = 'bob@corp.example'"));
        Assert.Contains(await AuditAsync(rig.Env), a => a.Action == "notification.email.bounce_report" && a.Payload.Contains("mailbox", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_that_changed_a_moment_ago_waits_for_the_next_pass()
    {
        var rig = await NewRigAsync(mailbox: true, configure: e => e.Bounces.MailboxSettleSeconds = 30);
        await using var _ = rig.Smtp;
        await SaveAsync(rig, AwarenessEnv.Rita);
        var reader = new BounceMailboxReader(rig.Bounces, rig.Env.Options, rig.Env.Clock, NullLogger<BounceMailboxReader>.Instance);
        await File.WriteAllTextAsync(Path.Combine(rig.Mailbox!, "fresh.eml"), Dsn("rita@corp.example", BounceTags.Compute(TagSecret, "rita@corp.example")));
        Assert.Equal(0, await reader.RunOnceAsync(CancellationToken.None));
        File.SetLastWriteTimeUtc(Path.Combine(rig.Mailbox!, "fresh.eml"), DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(1, await reader.RunOnceAsync(CancellationToken.None));
    }

    // ---------------------------------------------------------------- the REST endpoint

    [Fact]
    public async Task The_bounce_endpoint_needs_a_service_account_records_reports_and_validates_the_request()
    {
        using var f = await ApiFactory.CreateAsync(pg);
        await f.Database.ExecuteAsSuperuserAsync("INSERT INTO ntf.preference (user_sub, auto_watch, updated_at, email_enabled, email_address) VALUES ('u-rita', true, now(), true, 'rita@corp.example')");
        var body = new { reports = new[] { new { address = "rita@corp.example", kind = "hard", status = (string?)"5.1.1", diagnostic = (string?)"user unknown" }, new { address = "stranger@corp.example", kind = "hard", status = (string?)null, diagnostic = (string?)null } } };

        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().PostAsJsonAsync("/api/v1/notifications/email/bounces", body)).StatusCode);
        foreach (var roles in new[] { new[] { "plm-viewer" }, new[] { "plm-admin" }, new[] { "plm-author" } })
            Assert.Equal(HttpStatusCode.Forbidden, (await f.ClientFor(ApiFactory.Token(roles: roles)).PostAsJsonAsync("/api/v1/notifications/email/bounces", body)).StatusCode);

        var service = f.ClientFor(ApiFactory.Token("u-mta", roles: ["plm-integration"]));
        var ok = await service.PostAsJsonAsync("/api/v1/notifications/email/bounces", body);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var result = JsonNode.Parse(await ok.Content.ReadAsStringAsync())!;
        Assert.Equal(1, result["accepted"]!.GetValue<int>());
        Assert.Equal(1, result["ignored"]!.GetValue<int>()); // an address that is not one of ours says nothing about anyone
        Assert.Equal(0, result["suppressed"]!.GetValue<int>());

        var again = await service.PostAsJsonAsync("/api/v1/notifications/email/bounces", new { reports = new[] { new { address = "rita@corp.example", kind = "Hard", status = (string?)null, diagnostic = (string?)null } } });
        Assert.Equal(1, JsonNode.Parse(await again.Content.ReadAsStringAsync())!["suppressed"]!.GetValue<int>()); // kind is case-insensitive; the second hard bounce suppresses

        Assert.Equal(HttpStatusCode.BadRequest, (await service.PostAsJsonAsync("/api/v1/notifications/email/bounces", new { reports = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await service.PostAsJsonAsync("/api/v1/notifications/email/bounces", new { reports = new[] { new { address = "rita@corp.example", kind = "bogus" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await service.PostAsJsonAsync("/api/v1/notifications/email/bounces", new { reports = Enumerable.Range(0, 101).Select(_ => new { address = "rita@corp.example", kind = "soft" }) })).StatusCode);
    }
}
