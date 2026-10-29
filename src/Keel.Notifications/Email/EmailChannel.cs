using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Keel.Notifications;

public enum ChannelResult
{
    Sent,
    /// <summary>Nothing was sent: the user switched mail off, has no usable address, or the address is outside the allowed domains.</summary>
    Skipped,
}

/// <summary>
/// A delivery channel for notifications beyond the application itself (the bell and the notifications page read the table directly). A channel is told only
/// WHO has something new, never what: there is deliberately no kind, object or text in the signature, so no channel can carry marked data.
/// </summary>
public interface INotificationChannel
{
    string Name { get; }

    /// <summary>Delivers the pointer ("you have a notification") to one user. Throws <see cref="SmtpTransportException"/> (or another channel failure) when the attempt should be retried or given up on.</summary>
    Task<ChannelResult> DeliverAsync(string recipientSub, CancellationToken ct);
}

/// <summary>
/// The pointer-only text of a notification mail (ADR-0032). Every character is a constant of this class plus the configured base address: no object number or
/// name, no notification kind, no actor, no marked text. A recipient learns that something waits, signs in, and the notifications page re-authorizes every row.
/// </summary>
public static class EmailContent
{
    public const string Subject = "You have a notification in Keel PLM";

    public static string Body(Uri baseUrl)
    {
        var root = baseUrl.AbsoluteUri.TrimEnd('/');
        return "You have a notification in Keel PLM.\r\n\r\n" +
               "Open it here. You will be asked to sign in:\r\n" +
               root + "/notifications\r\n\r\n" +
               "This message does not contain the notification itself.\r\n" +
               "You get it because you turned on e-mail notifications in Keel PLM. Change this at:\r\n" +
               root + "/notifications/preferences\r\n";
    }
}

/// <summary>
/// The e-mail channel: opt-in per user (an address and a switch on the preferences page), pointer-only content, TLS to an on-premises relay (<see cref="SmtpSender"/>).
/// It re-checks the user's switch and the allowed domains at send time, so a user who opted out after a mail was queued is not mailed.
/// </summary>
public sealed class EmailNotificationChannel(NpgsqlDataSource dataSource, NotificationOptions options, SmtpSender sender) : INotificationChannel
{
    public string Name => "email";

    public async Task<ChannelResult> DeliverAsync(string recipientSub, CancellationToken ct)
    {
        var email = options.Email;
        if (!email.Enabled) return ChannelResult.Skipped;
        string? address;
        await using (var cmd = dataSource.CreateCommand("SELECT email_address FROM ntf.preference WHERE user_sub = @u AND email_enabled"))
        {
            cmd.Parameters.AddWithValue("u", recipientSub);
            address = await cmd.ExecuteScalarAsync(ct) as string;
        }
        if (address is null || !EmailAddress.TryNormalize(address, out var to) || !email.IsRecipientAllowed(to)) return ChannelResult.Skipped;
        // BaseUrl and FromAddress were validated at start-up (EmailOptions.Validate).
        await sender.SendAsync(new OutgoingMail(email.FromAddress, to, EmailContent.Subject, EmailContent.Body(new Uri(email.BaseUrl))), ct);
        return ChannelResult.Sent;
    }
}

/// <summary>
/// Sends the queued mails (<c>ntf.email_queue</c>, at most one pending per user). Any number of nodes may run it: a claim leases rows with SKIP LOCKED.
/// Failures back off and end as <c>failed</c> after <c>MaxAttempts</c>; a sent mail and a final failure are audited without any address or text.
/// </summary>
public sealed class EmailDispatchService(NpgsqlDataSource dataSource, EmailNotificationChannel channel, NotificationOptions options, IAuditLog audit, TimeProvider clock, ILogger<EmailDispatchService> logger) : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Email.Enabled)
        {
            logger.LogInformation("The e-mail notification channel is off (Keel:Notifications:Email:Enabled).");
            return;
        }
        logger.LogInformation("The e-mail notification channel sends through {Host}:{Port} ({Security}).", options.Email.Host, options.Email.Port.ToString(CultureInfo.InvariantCulture), options.Email.Security);
        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;
            try { handled = await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                logger.LogWarning(e, "A round of e-mail delivery failed; it retries.");
            }
            if (handled == 0)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(options.Email.PollSeconds), clock, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>One claim-and-send round; returns how many queue rows it handled. Public so tests can drive it without waiting for the timer.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var claimed = new List<(Guid Id, string Sub, int Attempts)>();
        await using (var cmd = dataSource.CreateCommand(
            """
            UPDATE ntf.email_queue SET claimed_until = @until, attempts = attempts + 1
            WHERE id IN (SELECT id FROM ntf.email_queue
                         WHERE status = 'pending' AND not_before <= @now AND (claimed_until IS NULL OR claimed_until < @now)
                         ORDER BY not_before, id LIMIT 20 FOR UPDATE SKIP LOCKED)
            RETURNING id, recipient_sub, attempts
            """))
        {
            cmd.Parameters.AddWithValue("until", (now + Lease).ToUniversalTime());
            cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) claimed.Add((r.GetGuid(0), r.GetString(1), r.GetInt32(2)));
        }
        foreach (var (id, sub, attempts) in claimed) await HandleAsync(id, sub, attempts, ct);
        await CleanUpAsync(ct);
        return claimed.Count;
    }

    private async Task HandleAsync(Guid id, string sub, int attempts, CancellationToken ct)
    {
        try
        {
            var result = await channel.DeliverAsync(sub, ct);
            if (result == ChannelResult.Sent)
            {
                await FinishAsync(id, "sent", null, ct);
                await AuditAsync(sub, "notification.email.sent", AuditOutcome.Success, ("attempts", attempts.ToString(CultureInfo.InvariantCulture)));
            }
            else await FinishAsync(id, "skipped", "opted out, no usable address, or address outside the allowed domains", ct);
        }
        catch (SmtpTransportException e)
        {
            var give = e.Permanent || attempts >= options.Email.MaxAttempts;
            // The message names the relay's answer or the transport problem, never an address or text of ours.
            logger.LogWarning("E-mail delivery attempt {Attempt} failed: {Error}", attempts.ToString(CultureInfo.InvariantCulture), e.Message);
            if (give)
            {
                await FinishAsync(id, "failed", Truncate(e.Message), ct);
                await AuditAsync(sub, "notification.email.failed", AuditOutcome.Failure, ("attempts", attempts.ToString(CultureInfo.InvariantCulture)), ("permanent", e.Permanent.ToString()));
            }
            else
            {
                var delay = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, attempts - 1)));
                await using var retry = dataSource.CreateCommand("UPDATE ntf.email_queue SET claimed_until = NULL, not_before = @next, last_error = @err WHERE id = @id AND status = 'pending'");
                retry.Parameters.AddWithValue("next", (clock.GetUtcNow() + delay).ToUniversalTime());
                retry.Parameters.AddWithValue("err", Truncate(e.Message));
                retry.Parameters.AddWithValue("id", id);
                await retry.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private async Task FinishAsync(Guid id, string status, string? error, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE ntf.email_queue SET status = @s, claimed_until = NULL, sent_at = CASE WHEN @s = 'sent' THEN @now ELSE sent_at END, last_error = @err WHERE id = @id");
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow().ToUniversalTime());
        cmd.Parameters.Add(new NpgsqlParameter("err", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task AuditAsync(string sub, string action, AuditOutcome outcome, params (string, string?)[] details)
    {
        try
        {
            await audit.AppendAsync(new AuditEntry
            {
                Actor = ActorSnapshot.System, Action = action, Outcome = outcome, ObjectType = "notification", ObjectId = sub, Details = AuditDetails.Of(details),
            }, CancellationToken.None);
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
        {
            logger.LogError(e, "Could not audit {Action}", action);
        }
    }

    /// <summary>Finished queue rows are bookkeeping; they go after 30 days (the pending row of a user is never touched).</summary>
    private async Task CleanUpAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (now - _lastCleanup < TimeSpan.FromHours(1)) return;
        _lastCleanup = now;
        await using var cmd = dataSource.CreateCommand("DELETE FROM ntf.email_queue WHERE status <> 'pending' AND created_at < @cut");
        cmd.Parameters.AddWithValue("cut", (now - TimeSpan.FromDays(30)).ToUniversalTime());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Truncate(string s) => s.Length <= 290 ? s : s[..290];
}

/// <summary>A user's e-mail settings as the preferences page shows them.</summary>
public sealed record EmailSettings(bool ChannelAvailable, bool Enabled, string? Address, IReadOnlyList<string> AllowedDomains, IReadOnlySet<string> KindsOff, int MinIntervalMinutes);

public enum EmailSaveResult { Saved, ChannelUnavailable, AddressRequired, InvalidAddress, DomainNotAllowed }

/// <summary>The user's own e-mail opt-in, address and per-kind choice. Every method works on the given subject's own rows only.</summary>
public interface IEmailPreferences
{
    Task<EmailSettings> GetAsync(Subject subject, CancellationToken ct = default);

    /// <summary>Switches mail on or off and stores the address. Switching on needs a valid address inside an allowed domain.</summary>
    Task<EmailSaveResult> SetAsync(Subject subject, bool enabled, string? address, CancellationToken ct = default);

    /// <summary>Whether one notification kind also sends mail (once mail is on). Returns false for an unknown kind.</summary>
    Task<bool> SetKindAsync(Subject subject, string kind, bool email, CancellationToken ct = default);
}

public sealed class EmailPreferenceService(NpgsqlDataSource dataSource, NotificationOptions options, IAuditLog audit, TimeProvider clock) : IEmailPreferences
{
    public async Task<EmailSettings> GetAsync(Subject subject, CancellationToken ct = default)
    {
        var enabled = false;
        string? address = null;
        var off = new HashSet<string>(StringComparer.Ordinal);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var cmd = new NpgsqlCommand("SELECT email_enabled, email_address FROM ntf.preference WHERE user_sub = @u", conn))
        {
            cmd.Parameters.AddWithValue("u", subject.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { enabled = r.GetBoolean(0); address = r.IsDBNull(1) ? null : r.GetString(1); }
        }
        await using (var cmd = new NpgsqlCommand("SELECT kind FROM ntf.kind_preference WHERE user_sub = @u AND NOT email", conn))
        {
            cmd.Parameters.AddWithValue("u", subject.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) off.Add(r.GetString(0));
        }
        return new EmailSettings(options.Email.Enabled, enabled && options.Email.Enabled, address, options.Email.AllowedRecipientDomains, off, options.Email.MinIntervalMinutes);
    }

    public async Task<EmailSaveResult> SetAsync(Subject subject, bool enabled, string? address, CancellationToken ct = default)
    {
        if (!options.Email.Enabled) return EmailSaveResult.ChannelUnavailable;
        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(address))
        {
            if (!EmailAddress.TryNormalize(address, out var n)) return EmailSaveResult.InvalidAddress;
            if (!options.Email.IsRecipientAllowed(n)) return EmailSaveResult.DomainNotAllowed;
            normalized = n;
        }
        if (enabled && normalized is null) return EmailSaveResult.AddressRequired;
        await using var cmd = dataSource.CreateCommand(
            """
            INSERT INTO ntf.preference (user_sub, auto_watch, updated_at, email_enabled, email_address) VALUES (@u, true, @now, @e, @a)
            ON CONFLICT (user_sub) DO UPDATE SET email_enabled = @e, email_address = @a, updated_at = @now
            """);
        cmd.Parameters.AddWithValue("u", subject.Id);
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow().ToUniversalTime());
        cmd.Parameters.AddWithValue("e", enabled);
        cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)normalized ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
        // The address is personal data and is never written to the audit log: only the switch.
        await audit.AppendAsync(new AuditEntry
        {
            Actor = ActorSnapshot.From(subject), Action = "notification.email.preference", Outcome = AuditOutcome.Success, ObjectType = "notification", ObjectId = subject.Id,
            Details = AuditDetails.Of(("enabled", enabled.ToString()), ("addressSet", (normalized is not null).ToString())),
        }, ct);
        return EmailSaveResult.Saved;
    }

    public async Task<bool> SetKindAsync(Subject subject, string kind, bool email, CancellationToken ct = default)
    {
        if (!NotificationKinds.IsKnown(kind)) return false;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO ntf.kind_preference (user_sub, kind, in_app, notify_own, email) VALUES (@u, @k, true, false, @e)
            ON CONFLICT (user_sub, kind) DO UPDATE SET email = @e
            """, conn))
        {
            cmd.Parameters.AddWithValue("u", subject.Id);
            cmd.Parameters.AddWithValue("k", kind);
            cmd.Parameters.AddWithValue("e", email);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        // A row that says nothing but the defaults is not kept.
        await using var tidy = new NpgsqlCommand("DELETE FROM ntf.kind_preference WHERE user_sub = @u AND kind = @k AND in_app AND NOT notify_own AND email", conn);
        tidy.Parameters.AddWithValue("u", subject.Id);
        tidy.Parameters.AddWithValue("k", kind);
        await tidy.ExecuteNonQueryAsync(ct);
        return true;
    }
}
