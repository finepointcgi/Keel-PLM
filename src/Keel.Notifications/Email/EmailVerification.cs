using System.Security.Cryptography;
using System.Text;
using Keel.Audit;
using Keel.Core;
using Npgsql;

namespace Keel.Notifications;

/// <summary>
/// Address verification and bounce settings that belong to the e-mail channel (ADR-0055, assumptions A79 and A80). Configuration section
/// <c>Keel:Notifications:Email:Verification</c>; the defaults suit most sites.
/// </summary>
public sealed class EmailVerificationOptions
{
    /// <summary>How long a verification link works. A link is single-use and also dies when the user changes the address or asks for a new link.</summary>
    public int LinkMinutes { get; set; } = 1440;
    /// <summary>The shortest time between two links for one user.</summary>
    public int MinIntervalSeconds { get; set; } = 60;
    /// <summary>Links one user may request per 24 hours.</summary>
    public int MaxPerUserPerDay { get; set; } = 5;
    /// <summary>Links that may be sent to one address per 24 hours, whoever asks: the application cannot be used to flood someone's mailbox.</summary>
    public int MaxPerAddressPerDay { get; set; } = 3;

    public IReadOnlyList<string> Validate()
    {
        const string p = "Keel:Notifications:Email:Verification:";
        var errors = new List<string>();
        if (LinkMinutes is < 10 or > 10_080) errors.Add(p + "LinkMinutes must be between 10 and 10080 (seven days).");
        if (MinIntervalSeconds is < 0 or > 3600) errors.Add(p + "MinIntervalSeconds must be between 0 and 3600.");
        if (MaxPerUserPerDay is < 1 or > 50) errors.Add(p + "MaxPerUserPerDay must be between 1 and 50.");
        if (MaxPerAddressPerDay is < 1 or > 50) errors.Add(p + "MaxPerAddressPerDay must be between 1 and 50.");
        return errors;
    }
}

/// <summary>What happened to a request for a verification link. Every answer is about the caller's own address and own limits, never about another account.</summary>
public enum VerificationRequestResult
{
    /// <summary>"If the address can receive mail, a link is on its way." Also the answer when the address is suppressed or its per-address limit is reached: the caller cannot tell the difference.</summary>
    Sent,
    AlreadyVerified,
    ChannelUnavailable,
    AddressRequired,
    /// <summary>The caller asked too often: wait and try again.</summary>
    RateLimited,
    /// <summary>The relay refused or could not be reached; nothing was sent and the link is void.</summary>
    SendFailed,
}

public enum VerificationConfirmResult
{
    Confirmed,
    /// <summary>Unknown, used, expired, replaced, for someone else, for another address, or the address is suppressed: one answer for all of them.</summary>
    Invalid,
}

public interface IEmailVerification
{
    /// <summary>Sends a one-time link to the user's own saved address (the page that saves the address calls this).</summary>
    Task<VerificationRequestResult> RequestAsync(Subject subject, CancellationToken ct = default);

    /// <summary>Redeems the token from the link. It only works for the signed-in user it was made for.</summary>
    Task<VerificationConfirmResult> ConfirmAsync(Subject subject, string? token, CancellationToken ct = default);
}

/// <summary>The text of the verification mail: a constant plus the link. No object, kind, actor or marking can be in it.</summary>
public static class VerificationMailContent
{
    public const string Subject = "Confirm your e-mail address for Keel PLM";

    public static string Body(Uri baseUrl, string token)
    {
        var root = baseUrl.AbsoluteUri.TrimEnd('/');
        return "Someone, probably you, asked Keel PLM to send notification e-mails to this address.\r\n\r\n" +
               "To confirm that this address is yours, open this link and press the Confirm button. You will be asked to sign in:\r\n" +
               root + "/notifications/verify-email/" + token + "\r\n\r\n" +
               "The link works once and stops working after a day. If you did not ask for this, ignore this message: no e-mail notifications are sent to this address.\r\n";
    }
}

/// <summary>
/// One-time address verification (ADR-0055). A user's notification address receives mail only after its owner proved control of the mailbox: a random 256-bit token is
/// mailed in a link, only its SHA-256 is stored, and redeeming it needs the same signed-in user. Limits per user and per address bound the mail the application can cause;
/// answers never reveal whether an address is suppressed, throttled or used by someone else (no account enumeration); the address and the token never reach the audit log.
/// </summary>
public sealed class EmailVerificationService(NpgsqlDataSource dataSource, NotificationOptions options, SmtpSender sender, IAuditLog audit, TimeProvider clock) : IEmailVerification
{
    public async Task<VerificationRequestResult> RequestAsync(Subject subject, CancellationToken ct = default)
    {
        if (!options.Email.Enabled) return VerificationRequestResult.ChannelUnavailable;
        var v = options.Email.Verification;
        var now = clock.GetUtcNow().ToUniversalTime();
        string address;
        string token;
        Guid id;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", conn, tx))
            {
                lockCmd.Parameters.AddWithValue("k", "ntfv:" + subject.Id);
                await lockCmd.ExecuteNonQueryAsync(ct);
            }
            string? saved = null, verified = null;
            await using (var cmd = new NpgsqlCommand("SELECT email_address, email_verified_address FROM ntf.preference WHERE user_sub = @u", conn, tx))
            {
                cmd.Parameters.AddWithValue("u", subject.Id);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    saved = r.IsDBNull(0) ? null : r.GetString(0);
                    verified = r.IsDBNull(1) ? null : r.GetString(1);
                }
            }
            if (saved is null || !EmailAddress.TryNormalize(saved, out var normalized) || !options.Email.IsRecipientAllowed(normalized)) return VerificationRequestResult.AddressRequired;
            address = normalized;
            var lower = address.ToLowerInvariant();
            var suppressed = await ScalarAsync<bool>(conn, tx, "SELECT EXISTS (SELECT 1 FROM ntf.email_address_health WHERE address = @a AND suppressed_at IS NOT NULL)", ("a", lower), ct);
            if (string.Equals(verified, address, StringComparison.OrdinalIgnoreCase) && !suppressed) return VerificationRequestResult.AlreadyVerified;
            if (suppressed)
            {
                // The address bounced before. Nothing is sent, and the answer is the same as for a sent link; the administrator can lift the suppression.
                await AuditAsync(subject, "notification.email.verification_throttled", AuditOutcome.Failure, ("reason", "suppressed"), ("address", AddressHash(lower)));
                return VerificationRequestResult.Sent;
            }

            var last = await ScalarAsync<DateTime?>(conn, tx, "SELECT max(created_at) FROM ntf.email_verification WHERE user_sub = @u", ("u", subject.Id), ct);
            var perUser = await ScalarAsync<long>(conn, tx, "SELECT count(*) FROM ntf.email_verification WHERE user_sub = @u AND created_at > @since", ("u", subject.Id), ct, ("since", now.AddDays(-1)));
            if ((last is { } l && now - new DateTimeOffset(DateTime.SpecifyKind(l, DateTimeKind.Utc)) < TimeSpan.FromSeconds(v.MinIntervalSeconds)) || perUser >= v.MaxPerUserPerDay)
            {
                await AuditAsync(subject, "notification.email.verification_throttled", AuditOutcome.Failure, ("reason", "user"));
                return VerificationRequestResult.RateLimited;
            }
            var perAddress = await ScalarAsync<long>(conn, tx, "SELECT count(*) FROM ntf.email_verification WHERE lower(address) = @a AND created_at > @since", ("a", lower), ct, ("since", now.AddDays(-1)));
            if (perAddress >= v.MaxPerAddressPerDay)
            {
                await AuditAsync(subject, "notification.email.verification_throttled", AuditOutcome.Failure, ("reason", "address"), ("address", AddressHash(lower)));
                return VerificationRequestResult.Sent; // the same answer as a sent link: who else asked for this address is not the caller's business
            }

            await using (var supersede = new NpgsqlCommand("UPDATE ntf.email_verification SET invalidated_at = @now WHERE user_sub = @u AND consumed_at IS NULL AND invalidated_at IS NULL", conn, tx))
            {
                supersede.Parameters.AddWithValue("now", now);
                supersede.Parameters.AddWithValue("u", subject.Id);
                await supersede.ExecuteNonQueryAsync(ct);
            }
            token = Base64Url(RandomNumberGenerator.GetBytes(32));
            id = Guid.CreateVersion7();
            await using (var insert = new NpgsqlCommand(
                "INSERT INTO ntf.email_verification (id, user_sub, address, token_hash, created_at, expires_at) VALUES (@id, @u, @a, @h, @now, @exp)", conn, tx))
            {
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("u", subject.Id);
                insert.Parameters.AddWithValue("a", address);
                insert.Parameters.AddWithValue("h", Hash(token));
                insert.Parameters.AddWithValue("now", now);
                insert.Parameters.AddWithValue("exp", now.AddMinutes(v.LinkMinutes));
                await insert.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }

        try
        {
            // BaseUrl and FromAddress were validated at start-up (EmailOptions.Validate).
            await sender.SendAsync(new OutgoingMail(options.Email.FromAddress, address, VerificationMailContent.Subject, VerificationMailContent.Body(new Uri(options.Email.BaseUrl), token)), ct);
        }
        catch (SmtpTransportException e)
        {
            await using (var voidIt = dataSource.CreateCommand("UPDATE ntf.email_verification SET invalidated_at = @now WHERE id = @id"))
            {
                voidIt.Parameters.AddWithValue("now", clock.GetUtcNow().ToUniversalTime());
                voidIt.Parameters.AddWithValue("id", id);
                await voidIt.ExecuteNonQueryAsync(CancellationToken.None);
            }
            await AuditAsync(subject, "notification.email.verification_sent", AuditOutcome.Failure, ("permanent", e.Permanent.ToString()));
            return VerificationRequestResult.SendFailed;
        }
        await AuditAsync(subject, "notification.email.verification_sent", AuditOutcome.Success, ("expiresMinutes", options.Email.Verification.LinkMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return VerificationRequestResult.Sent;
    }

    public async Task<VerificationConfirmResult> ConfirmAsync(Subject subject, string? token, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().ToUniversalTime();
        if (string.IsNullOrEmpty(token) || token.Length is < 20 or > 100 || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            await AuditAsync(subject, "notification.email.verify_failed", AuditOutcome.Failure);
            return VerificationConfirmResult.Invalid;
        }
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid? id = null;
        string? owner = null, address = null;
        DateTime? expires = null, consumed = null, invalidated = null;
        await using (var find = new NpgsqlCommand("SELECT id, user_sub, address, expires_at, consumed_at, invalidated_at FROM ntf.email_verification WHERE token_hash = @h FOR UPDATE", conn, tx))
        {
            find.Parameters.AddWithValue("h", Hash(token));
            await using var r = await find.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                id = r.GetGuid(0);
                owner = r.GetString(1);
                address = r.GetString(2);
                expires = r.GetDateTime(3);
                consumed = r.IsDBNull(4) ? null : r.GetDateTime(4);
                invalidated = r.IsDBNull(5) ? null : r.GetDateTime(5);
            }
        }
        var valid = id is not null && owner == subject.Id && consumed is null && invalidated is null && new DateTimeOffset(DateTime.SpecifyKind(expires!.Value, DateTimeKind.Utc)) > now;
        if (valid)
        {
            // The link is for the address that is saved now; a changed address or a suppressed one makes it void.
            var current = await ScalarAsync<string?>(conn, tx, "SELECT email_address FROM ntf.preference WHERE user_sub = @u", ("u", subject.Id), ct);
            var suppressed = await ScalarAsync<bool>(conn, tx, "SELECT EXISTS (SELECT 1 FROM ntf.email_address_health WHERE address = @a AND suppressed_at IS NOT NULL)", ("a", address!.ToLowerInvariant()), ct);
            valid = string.Equals(current, address, StringComparison.OrdinalIgnoreCase) && !suppressed;
        }
        if (!valid)
        {
            await tx.RollbackAsync(ct);
            await AuditAsync(subject, "notification.email.verify_failed", AuditOutcome.Failure);
            return VerificationConfirmResult.Invalid;
        }
        await using (var use = new NpgsqlCommand("UPDATE ntf.email_verification SET consumed_at = @now WHERE id = @id", conn, tx))
        {
            use.Parameters.AddWithValue("now", now);
            use.Parameters.AddWithValue("id", id!.Value);
            await use.ExecuteNonQueryAsync(ct);
        }
        await using (var mark = new NpgsqlCommand("UPDATE ntf.preference SET email_verified_address = @a, email_verified_at = @now, updated_at = @now WHERE user_sub = @u", conn, tx))
        {
            mark.Parameters.AddWithValue("a", address!);
            mark.Parameters.AddWithValue("now", now);
            mark.Parameters.AddWithValue("u", subject.Id);
            await mark.ExecuteNonQueryAsync(ct);
        }
        await using (var others = new NpgsqlCommand("UPDATE ntf.email_verification SET invalidated_at = @now WHERE user_sub = @u AND consumed_at IS NULL AND invalidated_at IS NULL", conn, tx))
        {
            others.Parameters.AddWithValue("now", now);
            others.Parameters.AddWithValue("u", subject.Id);
            await others.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, new AuditEntry
        {
            Actor = ActorSnapshot.From(subject), Action = "notification.email.verified", Outcome = AuditOutcome.Success, ObjectType = "notification", ObjectId = subject.Id,
            Details = AuditDetails.Of(("address", AddressHash(address!.ToLowerInvariant()))),
        }, ct);
        await tx.CommitAsync(ct);
        return VerificationConfirmResult.Confirmed;
    }

    private async Task AuditAsync(Subject subject, string action, AuditOutcome outcome, params (string, string?)[] details) =>
        await audit.AppendAsync(new AuditEntry
        {
            Actor = ActorSnapshot.From(subject), Action = action, Outcome = outcome, ObjectType = "notification", ObjectId = subject.Id, Details = AuditDetails.Of(details),
        }, CancellationToken.None);

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, (string, object) p, CancellationToken ct, (string, object)? p2 = null)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue(p.Item1, p.Item2);
        if (p2 is { } q) cmd.Parameters.AddWithValue(q.Item1, q.Item2);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default! : (T)value;
    }

    /// <summary>A short, one-way tag that identifies an address in the audit log without being the address (personal data never goes there).</summary>
    public static string AddressHash(string lowerAddress) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(lowerAddress)))[..12];

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
