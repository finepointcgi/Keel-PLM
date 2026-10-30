using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Notifications;

/// <summary>Bounce handling settings, section <c>Keel:Notifications:Email:Bounces</c> (ADR-0055, assumption A80).</summary>
public sealed class EmailBounceOptions
{
    /// <summary>Hard bounces after which an address is suppressed (a complaint suppresses it at once). Two by default: one bad answer can be a misconfigured relay, two are not.</summary>
    public int HardBounceThreshold { get; set; } = 2;
    /// <summary>
    /// Directory that receives the bounce mail the relay delivers for Keel's sender address (a Maildir or a spool folder). Keel reads delivery status notifications (RFC 3464) and
    /// abuse reports (RFC 5965) from it and moves what it cannot use to <c>rejected</c>. Empty (the default) turns the reader off; the REST endpoint works either way.
    /// </summary>
    public string MailboxDirectory { get; set; } = "";
    public int MailboxPollSeconds { get; set; } = 30;
    /// <summary>A file is read only when it has not changed for this long, so a relay that writes in place is never read half written. Maildir delivery is atomic and does not need it.</summary>
    public int MailboxSettleSeconds { get; set; } = 5;
    /// <summary>
    /// Secret that makes a bounce mail believable: every notification mail carries <c>X-Keel-Bounce-Tag</c>, an HMAC of its recipient under this secret, and a mailbox report is
    /// accepted only when the returned headers carry the tag of the address it names. Without it anybody who can mail the bounce address could suppress a colleague.
    /// Provide from the environment or the secrets directory (<c>Keel__Notifications__Email__Bounces__TagSecret</c>), at least 32 characters; it is never stored in the database.
    /// </summary>
    public string TagSecret { get; set; } = "";

    public IReadOnlyList<string> Validate()
    {
        const string p = "Keel:Notifications:Email:Bounces:";
        var errors = new List<string>();
        if (HardBounceThreshold is < 1 or > 10) errors.Add(p + "HardBounceThreshold must be between 1 and 10.");
        if (MailboxPollSeconds is < 1 or > 3600) errors.Add(p + "MailboxPollSeconds must be between 1 and 3600.");
        if (MailboxSettleSeconds is < 0 or > 60) errors.Add(p + "MailboxSettleSeconds must be between 0 and 60.");
        if (TagSecret.Length > 0 && TagSecret.Length < 32) errors.Add(p + "TagSecret must be at least 32 characters.");
        if (MailboxDirectory.Length > 0)
        {
            if (TagSecret.Length == 0) errors.Add(p + "TagSecret is required when MailboxDirectory is set: without it a forged bounce mail could suppress anyone's address.");
            if (!Directory.Exists(MailboxDirectory)) errors.Add(p + "MailboxDirectory does not exist.");
        }
        return errors;
    }
}

/// <summary>The tag that ties a bounce mail to a mail Keel sent: HMAC-SHA256 over the lower-case recipient, 32 hex characters.</summary>
public static class BounceTags
{
    public const string Header = "X-Keel-Bounce-Tag";

    public static string Compute(string secret, string recipient) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(recipient.ToLowerInvariant())))[..32];

    public static bool Matches(string secret, string recipient, string? tag) =>
        tag is not null && tag.Length == 32 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(secret, recipient)), Encoding.ASCII.GetBytes(tag.ToLowerInvariant()));
}

public static class BounceKinds
{
    public const string Hard = "hard";
    public const string Soft = "soft";
    public const string Complaint = "complaint";
    public static readonly IReadOnlyList<string> All = [Hard, Soft, Complaint];
}

public static class BounceSources
{
    public const string Api = "api";
    public const string Mailbox = "mailbox";
}

/// <summary>One report of a failed or complained-about delivery. <see cref="Status"/> is the enhanced status code (for example <c>5.1.1</c>) when the sender knows it.</summary>
public sealed record BounceReport(string Address, string Kind, string? Status = null, string? Diagnostic = null);

/// <summary><c>Accepted</c> reports matched an address of this system; <c>Ignored</c> did not (or named no valid address) and were dropped; <c>Suppressed</c> addresses became suppressed by this batch.</summary>
public sealed record BounceBatchResult(int Accepted, int Ignored, int Suppressed);

public sealed record SuppressedAddress(string Address, string Reason, int HardBounces, DateTimeOffset SuppressedAt, int Users);
public sealed record BounceEventView(string Address, string Kind, string Source, string? Status, string? Diagnostic, DateTimeOffset ReceivedAt);
public sealed record EmailHealth(bool ChannelAvailable, int HardBounceThreshold, bool MailboxReaderOn, IReadOnlyList<SuppressedAddress> Suppressed, IReadOnlyList<BounceEventView> Recent);

/// <summary>
/// Records bounces and complaints and suppresses addresses (ADR-0055). Reports come from the authenticated REST endpoint (the relay or a script) or from the mailbox reader.
/// Only addresses of this system are recorded, so the tables stay bounded and an outsider cannot fill them; soft bounces are logged but never suppress; an address is suppressed
/// after the configured number of hard bounces or one complaint. A suppressed address receives nothing, including verification links, until an administrator lifts it, which
/// also clears the verification of the users who used it so each of them confirms again.
/// </summary>
public sealed partial class EmailBounceService(NpgsqlDataSource dataSource, NotificationOptions options, IAuditLog audit, TimeProvider clock)
{
    public const int MaxReportsPerBatch = 100;

    [GeneratedRegex(@"\S+@\S+", RegexOptions.None, 1000)]
    private static partial Regex AddressLike();

    /// <summary>Removes anything that looks like an address and cuts the text to 200 characters: a diagnostic is stored, but never an address or a message.</summary>
    public static string? Sanitize(string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic)) return null;
        var clean = new string([.. AddressLike().Replace(diagnostic.Trim(), "[address]").Select(c => char.IsControl(c) ? ' ' : c)]);
        return clean.Length <= 200 ? clean : clean[..200];
    }

    public async Task<Outcome<BounceBatchResult>> RecordAsync(ActorSnapshot actor, IReadOnlyList<BounceReport> reports, string source, string? corr, CancellationToken ct = default)
    {
        if (reports.Count is 0 or > MaxReportsPerBatch) return Outcome<BounceBatchResult>.Invalid([$"Send between 1 and {MaxReportsPerBatch} reports per request."]);
        var errors = new List<string>();
        for (var i = 0; i < reports.Count; i++)
        {
            if (!BounceKinds.All.Contains(reports[i].Kind)) errors.Add($"Report {i + 1}: kind must be hard, soft or complaint.");
            if (reports[i].Status is { } st && !StatusPattern().IsMatch(st)) errors.Add($"Report {i + 1}: status must look like 5.1.1.");
        }
        if (errors.Count > 0) return Outcome<BounceBatchResult>.Invalid(errors);

        var now = clock.GetUtcNow().ToUniversalTime();
        int accepted = 0, ignored = 0;
        var newlySuppressed = new List<(string Hash, string Reason, int Hard)>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var report in reports)
        {
            if (!EmailAddress.TryNormalize(report.Address, out var normalized)) { ignored++; continue; }
            var address = normalized.ToLowerInvariant();
            await using (var known = new NpgsqlCommand(
                """
                SELECT EXISTS (SELECT 1 FROM ntf.preference WHERE lower(email_address) = @a)
                    OR EXISTS (SELECT 1 FROM ntf.email_verification WHERE lower(address) = @a AND created_at > @recent)
                """, conn, tx))
            {
                known.Parameters.AddWithValue("a", address);
                known.Parameters.AddWithValue("recent", now.AddDays(-7));
                if (!(bool)(await known.ExecuteScalarAsync(ct))!) { ignored++; continue; }
            }
            accepted++;
            await using (var ev = new NpgsqlCommand(
                "INSERT INTO ntf.email_bounce_event (id, address, kind, source, status_code, diagnostic, received_at) VALUES (@id, @a, @k, @s, @st, @d, @now)", conn, tx))
            {
                ev.Parameters.AddWithValue("id", Guid.CreateVersion7());
                ev.Parameters.AddWithValue("a", address);
                ev.Parameters.AddWithValue("k", report.Kind);
                ev.Parameters.AddWithValue("s", source);
                ev.Parameters.Add(new NpgsqlParameter("st", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)report.Status ?? DBNull.Value });
                ev.Parameters.Add(new NpgsqlParameter("d", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)Sanitize(report.Diagnostic) ?? DBNull.Value });
                ev.Parameters.AddWithValue("now", now);
                await ev.ExecuteNonQueryAsync(ct);
            }
            if (report.Kind == BounceKinds.Soft) continue;

            int hard = 0;
            bool wasSuppressed = false, exists = false;
            await using (var read = new NpgsqlCommand("SELECT hard_bounces, suppressed_at IS NOT NULL FROM ntf.email_address_health WHERE address = @a FOR UPDATE", conn, tx))
            {
                read.Parameters.AddWithValue("a", address);
                await using var r = await read.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) { exists = true; hard = r.GetInt32(0); wasSuppressed = r.GetBoolean(1); }
            }
            if (report.Kind == BounceKinds.Hard) hard++;
            var suppress = !wasSuppressed && (report.Kind == BounceKinds.Complaint || hard >= options.Email.Bounces.HardBounceThreshold);
            var reason = report.Kind == BounceKinds.Complaint ? "complaint" : "hard_bounce";
            await using (var write = new NpgsqlCommand(
                exists
                    ? "UPDATE ntf.email_address_health SET hard_bounces = @h, last_event_at = @now, suppressed_at = COALESCE(suppressed_at, CASE WHEN @sup THEN @now END), suppress_reason = COALESCE(suppress_reason, CASE WHEN @sup THEN @reason END) WHERE address = @a"
                    : "INSERT INTO ntf.email_address_health (address, hard_bounces, last_event_at, suppressed_at, suppress_reason) VALUES (@a, @h, @now, CASE WHEN @sup THEN @now END, CASE WHEN @sup THEN @reason END)",
                conn, tx))
            {
                write.Parameters.AddWithValue("a", address);
                write.Parameters.AddWithValue("h", hard);
                write.Parameters.AddWithValue("now", now);
                write.Parameters.AddWithValue("sup", suppress);
                write.Parameters.AddWithValue("reason", reason);
                await write.ExecuteNonQueryAsync(ct);
            }
            if (suppress) newlySuppressed.Add((EmailVerificationService.AddressHash(address), reason, hard));
        }
        await audit.AppendAsync(tx, new AuditEntry
        {
            Actor = actor, Action = "notification.email.bounce_report", Outcome = AuditOutcome.Success, ObjectType = "notification", ObjectId = "email", CorrelationId = corr,
            Details = AuditDetails.Of(("source", source), ("accepted", accepted.ToString(CultureInfo.InvariantCulture)), ("ignored", ignored.ToString(CultureInfo.InvariantCulture)),
                ("suppressed", newlySuppressed.Count.ToString(CultureInfo.InvariantCulture))),
        }, ct);
        foreach (var (hash, reason, hard) in newlySuppressed)
            await audit.AppendAsync(tx, new AuditEntry
            {
                Actor = actor, Action = "notification.email.suppressed", Outcome = AuditOutcome.Success, ObjectType = "notification", ObjectId = "email", CorrelationId = corr,
                Details = AuditDetails.Of(("address", hash), ("reason", reason), ("hardBounces", hard.ToString(CultureInfo.InvariantCulture)), ("source", source)),
            }, ct);
        await tx.CommitAsync(ct);
        return Outcome<BounceBatchResult>.Ok(new BounceBatchResult(accepted, ignored, newlySuppressed.Count));
    }

    [GeneratedRegex(@"^[245]\.[0-9]{1,3}\.[0-9]{1,3}$", RegexOptions.None, 1000)]
    private static partial Regex StatusPattern();

    private async Task<Outcome<T>?> RequireAdminAsync<T>(Subject s, string action, string? corr, CancellationToken ct)
    {
        if (AccessPolicy.HasRoleFor(s, PlmAction.Administer)) return null;
        await audit.AppendAsync(new AuditEntry
        {
            Actor = ActorSnapshot.From(s), Action = action, Outcome = AuditOutcome.Denied, ObjectType = "notification", ObjectId = "email", CorrelationId = corr,
            Details = AuditDetails.Of(("reasons", AccessPolicy.RoleMissing)),
        }, ct);
        return Outcome<T>.Forbidden([AccessPolicy.RoleMissing]);
    }

    /// <summary>The administrator's view: suppressed addresses and the recent reports (administrators only; the addresses are personal data).</summary>
    public async Task<Outcome<EmailHealth>> GetHealthAsync(Subject s, string? corr = null, CancellationToken ct = default)
    {
        if (await RequireAdminAsync<EmailHealth>(s, "notification.email.health_list", corr, ct) is { } denied) return denied;
        var suppressed = new List<SuppressedAddress>();
        await using (var cmd = dataSource.CreateCommand(
            """
            SELECT h.address, h.suppress_reason, h.hard_bounces, h.suppressed_at, (SELECT count(*) FROM ntf.preference p WHERE lower(p.email_address) = h.address)
            FROM ntf.email_address_health h WHERE h.suppressed_at IS NOT NULL ORDER BY h.suppressed_at DESC, h.address LIMIT 500
            """))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) suppressed.Add(new SuppressedAddress(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetFieldValue<DateTimeOffset>(3), (int)r.GetInt64(4)));
        }
        var recent = new List<BounceEventView>();
        await using (var cmd = dataSource.CreateCommand("SELECT address, kind, source, status_code, diagnostic, received_at FROM ntf.email_bounce_event ORDER BY received_at DESC, id DESC LIMIT 100"))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                recent.Add(new BounceEventView(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetFieldValue<DateTimeOffset>(5)));
        }
        return Outcome<EmailHealth>.Ok(new EmailHealth(options.Email.Enabled, options.Email.Bounces.HardBounceThreshold, options.Email.Bounces.MailboxDirectory.Length > 0, suppressed, recent));
    }

    /// <summary>
    /// Lifts the suppression of an address and resets its bounce count. Every user who has this address saved loses the verification, so each has to confirm the address again
    /// (the re-verify flow); until then nothing is sent to it. Returns how many users must verify again.
    /// </summary>
    public async Task<Outcome<int>> ClearSuppressionAsync(Subject s, string address, string? corr, CancellationToken ct = default)
    {
        if (await RequireAdminAsync<int>(s, "notification.email.suppression_cleared", corr, ct) is { } denied) return denied;
        if (!EmailAddress.TryNormalize(address, out var normalized)) return Outcome<int>.Invalid(["That is not a valid e-mail address."]);
        var lower = normalized.ToLowerInvariant();
        var now = clock.GetUtcNow().ToUniversalTime();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int removed;
        await using (var del = new NpgsqlCommand("DELETE FROM ntf.email_address_health WHERE address = @a", conn, tx))
        {
            del.Parameters.AddWithValue("a", lower);
            removed = await del.ExecuteNonQueryAsync(ct);
        }
        if (removed == 0) return Outcome<int>.NotFound();
        int users;
        await using (var reset = new NpgsqlCommand(
            "UPDATE ntf.preference SET email_verified_address = NULL, email_verified_at = NULL, updated_at = @now WHERE lower(email_address) = @a", conn, tx))
        {
            reset.Parameters.AddWithValue("a", lower);
            reset.Parameters.AddWithValue("now", now);
            users = await reset.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, new AuditEntry
        {
            Actor = ActorSnapshot.From(s), Action = "notification.email.suppression_cleared", Outcome = AuditOutcome.Success, ObjectType = "notification", ObjectId = "email", CorrelationId = corr,
            Details = AuditDetails.Of(("address", EmailVerificationService.AddressHash(lower)), ("usersToVerify", users.ToString(CultureInfo.InvariantCulture))),
        }, ct);
        await tx.CommitAsync(ct);
        return Outcome<int>.Ok(users);
    }

    /// <summary>Old bounce events (90 days), verification rows (7 days) and health rows that never led to suppression (90 days) are bookkeeping and go.</summary>
    public async Task PurgeAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().ToUniversalTime();
        await using var cmd = dataSource.CreateCommand(
            """
            DELETE FROM ntf.email_verification WHERE created_at < @week;
            DELETE FROM ntf.email_bounce_event WHERE received_at < @quarter;
            DELETE FROM ntf.email_address_health WHERE suppressed_at IS NULL AND last_event_at < @quarter;
            """);
        cmd.Parameters.AddWithValue("week", now.AddDays(-7));
        cmd.Parameters.AddWithValue("quarter", now.AddDays(-90));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
