using System.Globalization;
using System.Text;
using Keel.Audit;
using Keel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Keel.Notifications;

/// <summary>
/// Reads the bounce mail a relay drops in a directory (<c>Keel:Notifications:Email:Bounces:MailboxDirectory</c>, a Maildir or a spool folder, plus its <c>new</c> subfolder).
/// No network client is involved: the relay delivers into the directory and this service reads files. Each file is parsed as a delivery status notification or an abuse
/// report (<see cref="DsnParser"/>); a report counts only when the returned headers carry the <c>X-Keel-Bounce-Tag</c> of the address it names (<see cref="BounceTags"/>), so a mail
/// that anybody could send to the bounce address cannot suppress a colleague. Used files are deleted; files that are not usable go to <c>rejected</c> (purged after 14 days).
/// </summary>
public sealed class BounceMailboxReader(EmailBounceService bounces, NotificationOptions options, TimeProvider clock, ILogger<BounceMailboxReader> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Email.Bounces;
        if (!options.Email.Enabled || settings.MailboxDirectory.Length == 0)
        {
            logger.LogInformation("The bounce mailbox reader is off (Keel:Notifications:Email:Bounces:MailboxDirectory).");
            return;
        }
        logger.LogInformation("The bounce mailbox reader watches {Directory}.", settings.MailboxDirectory);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) when (e is NpgsqlException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogWarning(e, "A round of bounce mailbox reading failed; it retries.");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(settings.MailboxPollSeconds), clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One pass over the directory; returns how many files it handled. Public so tests drive it without the timer.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var settings = options.Email.Bounces;
        var root = settings.MailboxDirectory;
        var handled = 0;
        var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(settings.MailboxSettleSeconds); // file times are real time, whatever the injected clock says
        foreach (var dir in new[] { root, Path.Combine(root, "new") }.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal).Take(200))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (name.StartsWith('.') || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || File.GetLastWriteTimeUtc(file) > cutoff) continue;
                handled++;
                await HandleAsync(file, root, ct);
            }
        }
        PurgeRejected(root);
        return handled;
    }

    private async Task HandleAsync(string file, string root, CancellationToken ct)
    {
        var info = new FileInfo(file);
        var accepted = new List<BounceReport>();
        var forged = 0;
        if (info.Length <= DsnParser.MaxBytes)
        {
            var text = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(file, ct));
            foreach (var parsed in DsnParser.Parse(text))
            {
                if (BounceTags.Matches(options.Email.Bounces.TagSecret, parsed.Report.Address, parsed.ReturnedTag)) accepted.Add(parsed.Report);
                else forged++;
            }
        }
        if (accepted.Count == 0)
        {
            Reject(file, root);
            if (forged > 0)
                logger.LogWarning("A bounce mail named {Count} address(es) without the tag of a mail Keel sent; it was rejected.", forged.ToString(CultureInfo.InvariantCulture));
            return;
        }
        foreach (var batch in accepted.Chunk(EmailBounceService.MaxReportsPerBatch))
        {
            var outcome = await bounces.RecordAsync(ActorSnapshot.System, batch, BounceSources.Mailbox, null, ct);
            if (!outcome.IsOk) { Reject(file, root); return; }
        }
        File.Delete(file);
    }

    private static void Reject(string file, string root)
    {
        var rejected = Path.Combine(root, "rejected");
        Directory.CreateDirectory(rejected);
        File.Move(file, Path.Combine(rejected, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(file)), overwrite: false);
    }

    private static void PurgeRejected(string root)
    {
        var rejected = Path.Combine(root, "rejected");
        if (!Directory.Exists(rejected)) return;
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(14);
        foreach (var f in Directory.EnumerateFiles(rejected))
        {
            try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); }
            catch (IOException) { /* in use: the next pass looks again */ }
        }
    }
}
