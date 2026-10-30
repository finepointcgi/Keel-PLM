using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Integration;
using Keel.Notifications;
using Keel.TestSupport;
using Keel.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>The idle lock registry on PostgreSQL (migration 0016): state survives a restart and is shared by several nodes.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SessionLockDatabaseTests(PostgresFixture pg)
{
    private static readonly SessionLockOptions Options = new() { IdleLockMinutes = 15, WarningSeconds = 60, LockedTerminateMinutes = 10 };
    private static readonly ActorSnapshot Alice = new("u-alice", "alice", true, "USA", "ENG", [], [], ImmutableSortedSet.Create(StringComparer.Ordinal, "plm-viewer"));

    private sealed class Rig(TestDatabase db, NpgsqlDataSource ds, ManualTimeProvider clock, PostgresAuditLog audit)
    {
        public TestDatabase Db => db;
        public NpgsqlDataSource Ds => ds;
        public ManualTimeProvider Clock => clock;
        /// <summary>A new "node": its own registry over the one shared database.</summary>
        public SessionLockRegistry Node() => new(Options, clock, audit, store: new PostgresSessionLockStore(ds));
        public async Task<List<AuditRecord>> EventsAsync() => [.. await new AuditQueries(ds).ListAsync(new AuditQuery(Limit: 500))];
    }

    private async Task<Rig> NewRigAsync()
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        return new Rig(db, ds, clock, new PostgresAuditLog(ds, clock));
    }

    [Fact]
    public async Task A_locked_session_is_still_locked_after_a_restart_and_the_browser_reconnecting_does_not_revive_it()
    {
        var rig = await NewRigAsync();
        var first = rig.Node();
        first.Load();
        await first.RegisterAsync("session-1", Alice, rig.Clock.GetUtcNow().AddHours(8));
        Assert.True(await first.LockNowAsync("session-1"));

        var restarted = rig.Node(); // the process was restarted: nothing in memory
        Assert.True(restarted.IsLocked("session-1")); // judged from the database, even before the first load
        restarted.Load();
        Assert.True(restarted.IsLocked("session-1"));
        await restarted.RegisterAsync("session-1", Alice, rig.Clock.GetUtcNow().AddHours(8)); // the circuit reconnects
        Assert.True(restarted.IsLocked("session-1"));
        Assert.Equal("manual", restarted.Get("session-1")!.LockReason);
        Assert.False(restarted.Touch("session-1")); // activity does not revive it

        Assert.True(await restarted.UnlockAsync("session-1", "step-up"));
        Assert.False(restarted.IsLocked("session-1"));
        await first.TickAsync();
        Assert.False(first.IsLocked("session-1")); // the node that locked it learns of the unlock
    }

    [Fact]
    public async Task An_idle_timeout_and_a_termination_survive_restarts_and_are_audited_once_even_when_two_nodes_tick()
    {
        var rig = await NewRigAsync();
        var a = rig.Node();
        var b = rig.Node();
        a.Load(); b.Load();
        await a.RegisterAsync("session-2", Alice, rig.Clock.GetUtcNow().AddHours(8));
        await b.RegisterAsync("session-2", Alice, rig.Clock.GetUtcNow().AddHours(8));

        rig.Clock.Advance(TimeSpan.FromMinutes(15));
        await Task.WhenAll(a.TickAsync(), b.TickAsync());
        await Task.WhenAll(a.TickAsync(), b.TickAsync());
        Assert.True(a.IsLocked("session-2") && b.IsLocked("session-2"));
        Assert.Single(await rig.EventsAsync(), e => e.Action == "session.lock");

        rig.Clock.Advance(TimeSpan.FromMinutes(10));
        await Task.WhenAll(a.TickAsync(), b.TickAsync());
        Assert.True(a.IsTerminated("session-2") && b.IsTerminated("session-2"));
        var restarted = rig.Node();
        Assert.True(restarted.IsTerminated("session-2"));
        var events = await rig.EventsAsync();
        Assert.Single(events, e => e.Action == "session.lock_expired");
        Assert.Single(events, e => e.Action == "session.terminated_idle");
        Assert.False(await restarted.UnlockAsync("session-2", "step-up")); // a terminated session needs a new sign-in
    }

    [Fact]
    public async Task Activity_through_one_node_keeps_the_session_open_for_every_node()
    {
        var rig = await NewRigAsync();
        var a = rig.Node();
        var b = rig.Node();
        a.Load(); b.Load();
        await a.RegisterAsync("session-3", Alice, rig.Clock.GetUtcNow().AddHours(8));
        await b.RegisterAsync("session-3", Alice, rig.Clock.GetUtcNow().AddHours(8));
        for (var i = 0; i < 4; i++)
        {
            rig.Clock.Advance(TimeSpan.FromMinutes(10));
            Assert.True(b.Touch("session-3"));
            await b.TickAsync();
            await a.TickAsync();
        }
        Assert.False(a.IsLocked("session-3"));
        Assert.False(b.IsLocked("session-3"));
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Action == "session.lock");
    }

    [Fact]
    public async Task The_table_holds_a_hash_of_the_session_id_never_the_id_and_expired_rows_are_purged()
    {
        var rig = await NewRigAsync();
        var node = rig.Node();
        node.Load();
        await node.RegisterAsync("secret-session-id", Alice, rig.Clock.GetUtcNow().AddMinutes(30));
        await using (var conn = new NpgsqlConnection(rig.Db.SuperuserConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT session_key, actor::text FROM identity.session_lock", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.Equal(SessionLockRegistry.Key("secret-session-id"), r.GetString(0));
            Assert.DoesNotContain("secret-session-id", r.GetString(1), StringComparison.Ordinal);
        }
        await rig.Db.ExecuteAsSuperuserAsync("INSERT INTO identity.session_lock (session_key, actor, phase, last_activity, absolute_expiry, created_at, updated_at) VALUES (repeat('a', 64), '{}', 'active', now(), now(), now(), now())");
        await Assert.ThrowsAsync<PostgresException>(() => rig.Db.ExecuteAsSuperuserAsync("INSERT INTO identity.session_lock (session_key, actor, phase, last_activity, absolute_expiry, created_at, updated_at) VALUES ('not-a-hash', '{}', 'active', now(), now(), now(), now())"));
        await Assert.ThrowsAsync<PostgresException>(() => rig.Db.ExecuteAsSuperuserAsync("INSERT INTO identity.session_lock (session_key, actor, phase, last_activity, absolute_expiry, created_at, updated_at) VALUES (repeat('b', 64), '{}', 'locked', now(), now(), now(), now())")); // locked needs locked_at
        rig.Clock.Advance(TimeSpan.FromHours(1));
        await node.TickAsync();
        await using var count = new NpgsqlConnection(rig.Db.SuperuserConnectionString);
        await count.OpenAsync();
        await using var c = new NpgsqlCommand("SELECT count(*) FROM identity.session_lock", count);
        Assert.Equal(0L, await c.ExecuteScalarAsync());
    }
}

/// <summary>Retention of delivered outbox rows (migration 0016): only delivered, only old, only past the fan-out, only through the audited function.</summary>
[Collection(PostgresCollection.Name)]
public sealed class OutboxRetentionTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Old = DateTimeOffset.UtcNow.AddDays(-100);

    private sealed record Rig(IntegrationEnv Env, SinkRecord SinkA, SinkRecord SinkB, OutboxRetentionService Service)
    {
        public TestDatabase Db => Env.Config.Db;
    }

    private async Task<Rig> NewRigAsync(Action<OutboxRetentionOptions>? configure = null, IAuditLog? audit = null)
    {
        var env = await IntegrationEnv.CreateAsync(pg, configure: o => { o.Retention.RetentionDays = 30; configure?.Invoke(o.Retention); });
        await env.SaveProfileAsync(IntegrationEnv.Profile("erp"));
        var a = await env.SaveFileSinkAsync("sink-a", "erp");
        var b = await env.SaveFileSinkAsync("sink-b", "erp");
        return new Rig(env, a, b, new OutboxRetentionService(env.Ds, audit ?? env.Config.Audit, env.Options, TimeProvider.System, NullLogger<OutboxRetentionService>.Instance));
    }

    private static async Task<Guid> EventAsync(Rig rig, string number, DateTimeOffset at)
    {
        var id = Guid.CreateVersion7();
        await rig.Db.ExecuteAsSuperuserAsync(
            $"INSERT INTO integration.outbox (id, event_type, event_version, aggregate_type, aggregate_number, occurred_at, payload, export_jurisdiction, cui_categories, distribution_statement, owning_org, export_authorizations, actor) " +
            $"VALUES ('{id}', 'part.released', 1, 'part', '{number}', '{at.UtcDateTime:O}', '{{}}', 'None', '{{}}', 'A', 'ENG', '{{}}', 'tester')");
        return id;
    }

    private static Task DeliveryAsync(Rig rig, Guid eventId, SinkRecord sink, string status, DateTimeOffset? deliveredAt = null) =>
        rig.Db.ExecuteAsSuperuserAsync(
            $"INSERT INTO integration.delivery (id, event_id, sink_id, status, attempts, next_attempt_at, created_at, updated_at, delivered_at, withheld_reason) " +
            $"VALUES (gen_random_uuid(), '{eventId}', '{sink.Id}', '{status}', 1, now(), now(), now(), {(deliveredAt is { } d ? $"'{d.UtcDateTime:O}'" : "NULL")}, {(status == "withheld" ? "ARRAY['program_not_allowed']" : "NULL")})");

    private static Task PassFanOutAsync(Rig rig) => rig.Db.ExecuteAsSuperuserAsync(
        "UPDATE ntf.fanout_cursor SET last_xid = (SELECT max(created_in_tx)::text FROM integration.outbox), last_seq = (SELECT max(seq) FROM integration.outbox), updated_at = now()");

    private static async Task<HashSet<string>> RemainingAsync(Rig rig)
    {
        var numbers = new HashSet<string>(StringComparer.Ordinal);
        await using var conn = new NpgsqlConnection(rig.Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT aggregate_number FROM integration.outbox", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) numbers.Add(r.GetString(0));
        return numbers;
    }

    [Fact]
    public async Task Only_old_events_whose_every_delivery_was_delivered_are_removed_and_nothing_undelivered_ever_is()
    {
        var rig = await NewRigAsync();
        var delivered = Old.AddDays(1);
        var ok = await EventAsync(rig, "E-ok", Old);
        await DeliveryAsync(rig, ok, rig.SinkA, "delivered", delivered);
        await DeliveryAsync(rig, ok, rig.SinkB, "delivered", delivered);
        foreach (var (number, status) in new[] { ("E-pending", "pending"), ("E-failed", "failed"), ("E-withheld", "withheld"), ("E-dead", "dead") })
        {
            var id = await EventAsync(rig, number, Old);
            await DeliveryAsync(rig, id, rig.SinkA, status);
        }
        var mixed = await EventAsync(rig, "E-mixed", Old); // delivered to one sink, still pending for the other
        await DeliveryAsync(rig, mixed, rig.SinkA, "delivered", delivered);
        await DeliveryAsync(rig, mixed, rig.SinkB, "pending");
        var recent = await EventAsync(rig, "E-recent", DateTimeOffset.UtcNow.AddDays(-2));
        await DeliveryAsync(rig, recent, rig.SinkA, "delivered", DateTimeOffset.UtcNow.AddDays(-2));
        await EventAsync(rig, "E-unrouted", Old); // written while no sink was enabled
        await PassFanOutAsync(rig);

        var result = await rig.Service.RunOnceAsync(CancellationToken.None);

        Assert.Equal((1, 2), (result.EventsDeleted, result.DeliveriesDeleted));
        var left = await RemainingAsync(rig);
        Assert.DoesNotContain("E-ok", left);
        Assert.Superset(new HashSet<string> { "E-pending", "E-failed", "E-withheld", "E-dead", "E-mixed", "E-recent", "E-unrouted" }, left);
        // The undelivered rows are exactly as they were.
        Assert.Equal(5L, await rig.Env.ScalarAsync("SELECT count(*) FROM integration.delivery WHERE status IN ('pending','failed','withheld','dead')"));
        Assert.Equal(2L, await rig.Env.ScalarAsync("SELECT count(*) FROM integration.delivery WHERE status = 'delivered'")); // E-mixed's and E-recent's
    }

    [Fact]
    public async Task Events_without_deliveries_are_removed_only_when_the_operator_says_so()
    {
        var rig = await NewRigAsync();
        await EventAsync(rig, "E-unrouted", Old);
        await PassFanOutAsync(rig);
        Assert.Equal(0, (await rig.Service.RunOnceAsync(CancellationToken.None)).EventsDeleted);

        var include = await NewRigAsync(o => o.IncludeUnroutedEvents = true);
        await EventAsync(include, "E-unrouted", Old);
        await PassFanOutAsync(include);
        Assert.Equal(1, (await include.Service.RunOnceAsync(CancellationToken.None)).EventsDeleted);
    }

    [Fact]
    public async Task An_event_the_notification_fan_out_has_not_passed_is_kept()
    {
        var rig = await NewRigAsync();
        var id = await EventAsync(rig, "E-fan", Old);
        await DeliveryAsync(rig, id, rig.SinkA, "delivered", Old.AddDays(1));

        Assert.Equal(0, (await rig.Service.RunOnceAsync(CancellationToken.None)).EventsDeleted); // the cursor still sits at the start
        await PassFanOutAsync(rig);
        Assert.Equal(1, (await rig.Service.RunOnceAsync(CancellationToken.None)).EventsDeleted);
    }

    [Fact]
    public async Task A_purge_is_audited_with_counts_and_dates_only_and_recorded_in_the_run_table()
    {
        var rig = await NewRigAsync();
        var id = await EventAsync(rig, "P-SECRET-0042", Old);
        await DeliveryAsync(rig, id, rig.SinkA, "delivered", Old.AddDays(1));
        await PassFanOutAsync(rig);

        await rig.Service.RunOnceAsync(CancellationToken.None);

        var audit = Assert.Single(await rig.Env.Config.AuditAsync(), a => a.Action == "integration.outbox_retention");
        Assert.Equal(AuditOutcome.Success, audit.Outcome);
        Assert.Contains("\"events\":\"1\"", audit.Payload.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("P-SECRET-0042", audit.Payload, StringComparison.Ordinal);
        Assert.Equal(1L, await rig.Env.ScalarAsync("SELECT count(*) FROM integration.retention_run WHERE events_deleted = 1 AND deliveries_deleted = 1 AND NOT dry_run"));
        await Assert.ThrowsAsync<PostgresException>(() => rig.Db.ExecuteAsSuperuserAsync("DELETE FROM integration.retention_run")); // the record itself is immutable
    }

    [Fact]
    public async Task A_purge_that_cannot_be_audited_is_rolled_back_completely()
    {
        var rig = await NewRigAsync(audit: new ThrowingAudit());
        var id = await EventAsync(rig, "E-1", Old);
        await DeliveryAsync(rig, id, rig.SinkA, "delivered", Old.AddDays(1));
        await PassFanOutAsync(rig);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.RunOnceAsync(CancellationToken.None));

        Assert.Contains("E-1", await RemainingAsync(rig));
        Assert.Equal(1L, await rig.Env.ScalarAsync("SELECT count(*) FROM integration.delivery"));
        Assert.Equal(0L, await rig.Env.ScalarAsync("SELECT count(*) FROM integration.retention_run"));
    }

    private sealed class ThrowingAudit : IAuditLog
    {
        public Task<AuditRecord> AppendAsync(NpgsqlTransaction transaction, AuditEntry entry, CancellationToken ct = default) => throw new InvalidOperationException("audit store down");
        public Task<AuditRecord> AppendAsync(AuditEntry entry, CancellationToken ct = default) => throw new InvalidOperationException("audit store down");
    }

    [Fact]
    public async Task A_dry_run_counts_and_records_but_deletes_nothing_and_a_disabled_job_does_nothing()
    {
        var dry = await NewRigAsync(o => o.DryRun = true);
        var id = await EventAsync(dry, "E-dry", Old);
        await DeliveryAsync(dry, id, dry.SinkA, "delivered", Old.AddDays(1));
        await PassFanOutAsync(dry);
        var result = await dry.Service.RunOnceAsync(CancellationToken.None);
        Assert.Equal((1, true), (result.EventsDeleted, result.DryRun));
        Assert.Contains("E-dry", await RemainingAsync(dry));
        Assert.Equal(1L, await dry.Env.ScalarAsync("SELECT count(*) FROM integration.retention_run WHERE dry_run"));

        var off = await NewRigAsync(o => { });
        off.Env.Options.Retention.RetentionDays = 0;
        var id2 = await EventAsync(off, "E-off", Old);
        await DeliveryAsync(off, id2, off.SinkA, "delivered", Old.AddDays(1));
        await PassFanOutAsync(off);
        Assert.Equal(0, (await off.Service.RunOnceAsync(CancellationToken.None)).EventsDeleted);
        Assert.Contains("E-off", await RemainingAsync(off));
    }

    [Fact]
    public async Task Batches_repeat_until_nothing_eligible_is_left_and_two_nodes_never_delete_the_same_rows_twice()
    {
        var rig = await NewRigAsync(o => o.BatchSize = 3);
        for (var i = 0; i < 10; i++)
        {
            var id = await EventAsync(rig, $"E-{i:D2}", Old.AddMinutes(i));
            await DeliveryAsync(rig, id, rig.SinkA, "delivered", Old.AddDays(1));
        }
        await PassFanOutAsync(rig);
        var other = new OutboxRetentionService(rig.Env.Ds, rig.Env.Config.Audit, rig.Env.Options, TimeProvider.System, NullLogger<OutboxRetentionService>.Instance);

        var results = await Task.WhenAll(rig.Service.RunOnceAsync(CancellationToken.None), other.RunOnceAsync(CancellationToken.None));

        Assert.Equal(10, results.Sum(r => r.EventsDeleted));
        Assert.Empty(await RemainingAsync(rig));
    }

    [Fact]
    public async Task The_database_itself_refuses_every_other_way_to_remove_or_change_outbox_rows()
    {
        var rig = await NewRigAsync();
        var id = await EventAsync(rig, "E-guard", Old);
        await DeliveryAsync(rig, id, rig.SinkA, "delivered", Old.AddDays(1));
        await PassFanOutAsync(rig);

        // The application role has no DELETE privilege on either table.
        await using var app = await rig.Env.Ds.OpenConnectionAsync();
        foreach (var sql in new[] { "DELETE FROM integration.outbox", "DELETE FROM integration.delivery", "TRUNCATE integration.outbox" })
        {
            await using var cmd = new NpgsqlCommand(sql, app);
            await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        }
        // Even the owner and a superuser are stopped by the guard triggers: rows go only through the function.
        foreach (var cs in new[] { rig.Db.MigratorConnectionString, rig.Db.SuperuserConnectionString })
        {
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var del = new NpgsqlCommand("DELETE FROM integration.outbox", conn);
            Assert.Contains("purge_delivered_outbox", (await Assert.ThrowsAsync<PostgresException>(() => del.ExecuteNonQueryAsync())).MessageText, StringComparison.Ordinal);
            await using var upd = new NpgsqlCommand("UPDATE integration.outbox SET actor = 'x'", conn);
            Assert.Contains("immutable", (await Assert.ThrowsAsync<PostgresException>(() => upd.ExecuteNonQueryAsync())).MessageText, StringComparison.Ordinal);
        }
        // Setting the flag by hand does not help the application role (no privilege), and does not let an undelivered delivery go for anyone else.
        var pending = await EventAsync(rig, "E-pend", Old);
        await DeliveryAsync(rig, pending, rig.SinkA, "pending");
        await using (var conn = new NpgsqlConnection(rig.Db.SuperuserConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using var set = new NpgsqlCommand("SELECT set_config('keel.outbox_retention', 'on', true)", conn, tx);
            await set.ExecuteScalarAsync();
            await using var del = new NpgsqlCommand($"DELETE FROM integration.delivery WHERE event_id = '{pending}'", conn, tx);
            Assert.Contains("never deleted", (await Assert.ThrowsAsync<PostgresException>(() => del.ExecuteNonQueryAsync())).MessageText, StringComparison.Ordinal);
        }
        // The function itself refuses a cutoff inside the last day and an absurd batch size.
        await using var call = new NpgsqlCommand("SELECT * FROM integration.purge_delivered_outbox(now(), 10, false, false)", app);
        Assert.Contains("one day", (await Assert.ThrowsAsync<PostgresException>(() => call.ExecuteReaderAsync())).MessageText, StringComparison.Ordinal);
        await using var big = new NpgsqlCommand("SELECT * FROM integration.purge_delivered_outbox(now() - interval '40 days', 0, false, false)", app);
        await Assert.ThrowsAsync<PostgresException>(() => big.ExecuteReaderAsync());
        Assert.Contains("E-pend", await RemainingAsync(rig));
    }
}

/// <summary>The e-mail channel against an in-process SMTP relay, over real Postgres: opt-in, pointer-only mail, throttling, retries, audit without addresses.</summary>
[Collection(PostgresCollection.Name)]
public sealed class EmailChannelTests(PostgresFixture pg) : IDisposable
{
    private readonly SyslogTestPki _pki = new("Keel e-mail test CA");
    private readonly string _caFile = Path.Combine(Path.GetTempPath(), "keel-mail-ca-" + Guid.NewGuid().ToString("N") + ".pem");

    public void Dispose()
    {
        File.Delete(_caFile);
        _pki.Dispose();
    }

    private sealed record Rig(AwarenessEnv Env, TestSmtpServer Smtp, EmailDispatchService Dispatch, EmailPreferenceService Prefs)
    {
        public Task<long> CountAsync(string sql) => ScalarAsync(Env, sql);
    }

    private static async Task<long> ScalarAsync(AwarenessEnv env, string sql)
    {
        await using var conn = new NpgsqlConnection(env.Db.SuperuserConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<Rig> NewRigAsync(bool channelOn = true)
    {
        await File.WriteAllTextAsync(_caFile, _pki.CaPem);
        var smtp = new TestSmtpServer(TestSmtpMode.StartTls, _pki.Issue("relay", true, "localhost"));
        var env = await AwarenessEnv.CreateAsync(pg, o => o.Email = new EmailOptions
        {
            Enabled = channelOn, Host = "127.0.0.1", Port = smtp.Port, Security = SmtpSecurity.StartTls, FromAddress = "keel@corp.example", BaseUrl = "https://keel.corp.example",
            AllowedRecipientDomains = ["corp.example"], CaBundlePath = _caFile, MinIntervalMinutes = 10, MaxAttempts = 3, TimeoutSeconds = 10,
        });
        var sender = new SmtpSender(env.Options.Email, new OnPremHostGuard(env.Options.Email));
        var channel = new EmailNotificationChannel(env.Ds, env.Options, sender);
        return new Rig(env, smtp, new EmailDispatchService(env.Ds, channel, env.Options, env.Audit, env.Clock, NullLogger<EmailDispatchService>.Instance, new EmailBounceService(env.Ds, env.Options, env.Audit, env.Clock)),
            new EmailPreferenceService(env.Ds, env.Options, env.Audit, env.Clock));
    }

    private static async Task<List<AuditRecord>> AuditAsync(AwarenessEnv env) => [.. await new AuditQueries(env.Ds).ListAsync(new AuditQuery(Limit: 5000))];

    private static async Task OptInAsync(Rig rig, Subject who, string address = "rita@corp.example")
    {
        Assert.Equal(EmailSaveResult.Saved, await rig.Prefs.SetAsync(who, true, address));
        // These tests are about delivery, not about proving the mailbox (EmailVerificationTests does that): the address counts as confirmed.
        await rig.Env.Db.ExecuteAsSuperuserAsync($"UPDATE ntf.preference SET email_verified_address = email_address, email_verified_at = now() WHERE user_sub = '{who.Id}'");
    }

    private static async Task NotifyRitaAsync(Rig rig, string name = "Bracket, sensor") =>
        await rig.Env.StartReviewAsync(await rig.Env.NewPartAsync(name)); // alice submits: rita is asked to review

    [Fact]
    public async Task A_user_who_opted_in_gets_one_pointer_only_mail_and_nobody_else_gets_anything()
    {
        var rig = await NewRigAsync();
        await using var _ = rig.Smtp;
        await OptInAsync(rig, AwarenessEnv.Rita);
        // Bob is asked to approve later but never opted in; his preference row does not exist.
        var part = await rig.Env.NewPartAsync("Secret bracket, ORCA seeker mount");
        await rig.Env.StartReviewAsync(part);

        Assert.Equal(1L, await rig.CountAsync("SELECT count(*) FROM ntf.email_queue WHERE status = 'pending'"));
        Assert.Equal(1, await rig.Dispatch.RunOnceAsync(CancellationToken.None));

        var mail = Assert.Single(rig.Smtp.Mail);
        Assert.Equal(["rita@corp.example"], mail.Recipients);
        Assert.True(mail.OverTls);
        Assert.Equal("You have a notification in Keel PLM", mail.Header("Subject"));
        Assert.Contains("https://keel.corp.example/notifications", mail.Body, StringComparison.Ordinal);
        // Pointer only: nothing about the object, the kind, the actor or the markings reaches the mail.
        foreach (var secret in new[] { part, "Secret bracket", "ORCA", "ITAR", "alice", "Alice", "review", "approval", "CUI", "seeker" })
            Assert.DoesNotContain(secret, mail.Data, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await rig.CountAsync("SELECT count(*) FROM ntf.email_queue WHERE status = 'sent' AND sent_at IS NOT NULL"));
        Assert.Equal(0, await rig.Dispatch.RunOnceAsync(CancellationToken.None)); // nothing left to send
        Assert.Single(rig.Smtp.Mail);
    }

    [Fact]
    public async Task With_the_channel_off_or_the_user_not_opted_in_nothing_is_queued()
    {
        var off = await NewRigAsync(channelOn: false);
        await using (off.Smtp)
        {
            Assert.Equal(EmailSaveResult.ChannelUnavailable, await off.Prefs.SetAsync(AwarenessEnv.Rita, true, "rita@corp.example"));
            await NotifyRitaAsync(off);
            Assert.Equal(0L, await off.CountAsync("SELECT count(*) FROM ntf.email_queue"));
            Assert.Equal(0, await off.Dispatch.RunOnceAsync(CancellationToken.None));
        }
        var on = await NewRigAsync();
        await using (on.Smtp)
        {
            await NotifyRitaAsync(on);
            Assert.Equal(0L, await on.CountAsync("SELECT count(*) FROM ntf.email_queue")); // the in-app notification exists, but nobody asked for mail
            Assert.True(await on.CountAsync("SELECT count(*) FROM ntf.notification") > 0);
            Assert.Empty(on.Smtp.Mail);
        }
    }

    [Fact]
    public async Task Switching_a_kind_off_or_opting_out_before_the_send_means_no_mail()
    {
        var rig = await NewRigAsync();
        await using var _ = rig.Smtp;
        await OptInAsync(rig, AwarenessEnv.Rita);
        Assert.True(await rig.Prefs.SetKindAsync(AwarenessEnv.Rita, NotificationKinds.ReviewRequested, email: false));
        await NotifyRitaAsync(rig);
        Assert.Equal(0L, await rig.CountAsync("SELECT count(*) FROM ntf.email_queue")); // that kind no longer sends mail (the bell still shows it)
        Assert.True((await rig.Prefs.GetAsync(AwarenessEnv.Rita)).KindsOff.Contains(NotificationKinds.ReviewRequested));
        Assert.True(await rig.Prefs.SetKindAsync(AwarenessEnv.Rita, NotificationKinds.ReviewRequested, email: true));
        Assert.Equal(0L, await rig.CountAsync($"SELECT count(*) FROM ntf.kind_preference WHERE user_sub = '{AwarenessEnv.Rita.Id}'")); // back to defaults: no row kept

        await NotifyRitaAsync(rig, "Second bracket");
        Assert.Equal(1L, await rig.CountAsync("SELECT count(*) FROM ntf.email_queue WHERE status = 'pending'"));
        Assert.Equal(EmailSaveResult.Saved, await rig.Prefs.SetAsync(AwarenessEnv.Rita, false, "rita@corp.example")); // she opts out after the mail was queued
        Assert.Equal(1, await rig.Dispatch.RunOnceAsync(CancellationToken.None));
        Assert.Empty(rig.Smtp.Mail);
        Assert.Equal(1L, await rig.CountAsync("SELECT count(*) FROM ntf.email_queue WHERE status = 'skipped'"));
    }

    [Fact]
    public async Task Several_notifications_collapse_into_one_pending_mail_and_the_minimum_interval_holds_the_next_one_back()
    {
        var rig = await NewRigAsync();
        await using var _ = rig.Smtp;
        await OptInAsync(rig, AwarenessEnv.Rita);
        await NotifyRitaAsync(rig, "One");
        await NotifyRitaAsync(rig, "Two");
        await NotifyRitaAsync(rig, "Three");
        Assert.Equal(1L, await rig.CountAsync("SELECT count(*) FROM ntf.email_queue WHERE status = 'pending'")); // at most one waiting mail per user

        await rig.Dispatch.RunOnceAsync(CancellationToken.None);
        Assert.Single(rig.Smtp.Mail);

        await NotifyRitaAsync(rig, "Four"); // two minutes later: inside the 10 minute interval
        rig.Env.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, await rig.Dispatch.RunOnceAsync(CancellationToken.None));
        Assert.Single(rig.Smtp.Mail);
        rig.Env.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(1, await rig.Dispatch.RunOnceAsync(CancellationToken.None));
        Assert.Equal(2, rig.Smtp.Mail.Count);
    }

    [Fact]
    public async Task A_temporary_failure_backs_off_and_succeeds_later_and_a_refused_address_ends_as_failed_without_the_address_anywhere()
    {
        var rig = await NewRigAsync();
        await using var _ = rig.Smtp;
        await OptInAsync(rig, AwarenessEnv.Rita);
        await NotifyRitaAsync(rig);

        rig.Smtp.TempFailMessages = 1;
        Assert.Equal(1, await rig.Dispatch.RunOnceAsync(CancellationToken.None));
        Assert.Empty(rig.Smtp.Mail);
        Assert.Equal(1L, await rig.CountAsync("SELECT count(*) FROM ntf.email_queue WHERE status = 'pending' AND attempts = 1 AND last_error LIKE '%451%'"));
        Assert.Equal(0, await rig.Dispatch.RunOnceAsync(CancellationToken.None)); // backing off
        rig.Env.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await rig.Dispatch.RunOnceAsync(CancellationToken.None));
        Assert.Single(rig.Smtp.Mail);

        // A permanent refusal: the relay's text repeats the address, which must not be kept.
        rig.Smtp.RejectRecipient = _ => true;
        rig.Smtp.EchoRecipientInErrors = true;
        rig.Env.Clock.Advance(TimeSpan.FromMinutes(11));
        await NotifyRitaAsync(rig, "Later");
        rig.Env.Clock.Advance(TimeSpan.FromMinutes(11));
        await rig.Dispatch.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1L, await rig.CountAsync("SELECT count(*) FROM ntf.email_queue WHERE status = 'failed'"));
        Assert.Equal(0L, await rig.CountAsync("SELECT count(*) FROM ntf.email_queue WHERE last_error LIKE '%@%'"));
        Assert.Equal(1L, await rig.CountAsync("SELECT count(*) FROM ntf.email_queue WHERE status = 'failed' AND last_error LIKE '%[address]%'"));

        var audit = await AuditAsync(rig.Env);
        Assert.Contains(audit, a => a.Action == "notification.email.sent" && a.Outcome == AuditOutcome.Success);
        Assert.Contains(audit, a => a.Action == "notification.email.failed" && a.Outcome == AuditOutcome.Failure);
        Assert.DoesNotContain(audit, a => a.Payload.Contains("rita@corp.example", StringComparison.OrdinalIgnoreCase) || (a.ObjectId ?? "").Contains('@', StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_preference_rules_hold_and_the_address_never_reaches_the_audit_log()
    {
        var rig = await NewRigAsync();
        await using var _ = rig.Smtp;
        Assert.Equal(EmailSaveResult.AddressRequired, await rig.Prefs.SetAsync(AwarenessEnv.Rita, true, null));
        Assert.Equal(EmailSaveResult.InvalidAddress, await rig.Prefs.SetAsync(AwarenessEnv.Rita, true, "rita"));
        Assert.Equal(EmailSaveResult.InvalidAddress, await rig.Prefs.SetAsync(AwarenessEnv.Rita, true, "rita@corp.example\r\nBcc: x@corp.example"));
        Assert.Equal(EmailSaveResult.DomainNotAllowed, await rig.Prefs.SetAsync(AwarenessEnv.Rita, true, "rita@gmail.com"));
        Assert.False((await rig.Prefs.GetAsync(AwarenessEnv.Rita)).Enabled); // nothing was saved by the refusals
        Assert.Equal(EmailSaveResult.Saved, await rig.Prefs.SetAsync(AwarenessEnv.Rita, true, "  Rita@Corp.Example "));
        var settings = await rig.Prefs.GetAsync(AwarenessEnv.Rita);
        Assert.Equal((true, "Rita@corp.example"), (settings.Enabled, settings.Address)); // the domain is lowercased, the local part keeps its case
        Assert.Equal(["corp.example"], settings.AllowedDomains);
        // Another user's settings are not touched or visible.
        Assert.False((await rig.Prefs.GetAsync(AwarenessEnv.Bob)).Enabled);
        Assert.Null((await rig.Prefs.GetAsync(AwarenessEnv.Bob)).Address);
        // The database refuses an address that would break a header, even if the application were bypassed.
        await Assert.ThrowsAsync<PostgresException>(() => rig.Env.Db.ExecuteAsSuperuserAsync($"UPDATE ntf.preference SET email_address = E'a@b.example\\r\\nBcc: x@y.example' WHERE user_sub = '{AwarenessEnv.Rita.Id}'"));
        await Assert.ThrowsAsync<PostgresException>(() => rig.Env.Db.ExecuteAsSuperuserAsync($"UPDATE ntf.preference SET email_address = NULL WHERE user_sub = '{AwarenessEnv.Rita.Id}'")); // enabled needs an address
        var audit = await AuditAsync(rig.Env);
        var events = audit.Where(a => a.Action == "notification.email.preference").ToList();
        Assert.Single(events);
        Assert.DoesNotContain("rita@", events[0].Payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("addressSet", events[0].Payload, StringComparison.Ordinal);
    }
}

/// <summary>The spool directory keeps the clearance rules of every sink: nothing the profile does not clear is ever written.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SpoolClearanceTests(PostgresFixture pg)
{
    [Fact]
    public async Task A_file_sink_whose_profile_does_not_clear_an_event_receives_no_file_and_one_that_does_receives_it_with_its_manifest()
    {
        var env = await IntegrationEnv.CreateAsync(pg);
        await env.SaveProfileAsync(IntegrationEnv.Profile("narrow", programs: []));
        await env.SaveProfileAsync(IntegrationEnv.Profile("erp"));
        await env.SaveSinkAsync("spool-narrow", "filedrop", "narrow", new JsonObject { ["directory"] = "spool-narrow", ["format"] = "ndjson", ["maxPendingFiles"] = 5 });
        await env.SaveSinkAsync("spool-erp", "filedrop", "erp", new JsonObject { ["directory"] = "spool-erp", ["format"] = "ndjson", ["maxPendingFiles"] = 5 });
        var part = await env.Config.ReleasedComponentAsync("Bracket", ConfigEnv.Itar());

        await env.DrainAsync();

        Assert.False(Directory.Exists(env.DropDirectory("spool-narrow")) && Directory.GetFiles(env.DropDirectory("spool-narrow")).Length > 0); // withheld: nothing written, not even a manifest
        Assert.Single(await env.DeliveriesAsync("withheld", "spool-narrow"));
        var files = Directory.GetFiles(env.DropDirectory("spool-erp"), "*.ndjson");
        var data = await File.ReadAllTextAsync(Assert.Single(files));
        Assert.Contains(part, data, StringComparison.Ordinal);
        Assert.True(File.Exists(files[0] + ".manifest.json"));
        Assert.Matches(@"keel-\d{12}-\d{12}-\d+\.ndjson$", files[0]);
    }

    [Fact]
    public async Task A_spool_at_its_limit_leaves_the_events_pending_in_the_outbox_and_they_flow_once_the_receiver_consumes()
    {
        var env = await IntegrationEnv.CreateAsync(pg, configure: o => o.FileDropMaxPendingFiles = 1);
        await env.SaveProfileAsync(IntegrationEnv.Profile("erp"));
        await env.SaveFileSinkAsync("slow-receiver", "erp", "json");
        await env.Config.ReleasedComponentAsync("First", ConfigEnv.Itar());
        await env.Config.ReleasedComponentAsync("Second", ConfigEnv.Itar());

        await env.DrainAsync(TimeSpan.FromSeconds(3)); // the limit is one committed file
        var dir = env.DropDirectory("slow-receiver");
        Assert.Single(Directory.GetFiles(dir, "*.manifest.json"));
        Assert.True(await env.ScalarAsync("SELECT count(*) FROM integration.delivery WHERE status IN ('pending','failed')") > 0); // retried, never dropped, never dead
        Assert.Equal(0L, await env.ScalarAsync("SELECT count(*) FROM integration.delivery WHERE status = 'dead'"));
        Assert.Equal(0L, await env.ScalarAsync("SELECT max(attempts) FROM integration.delivery WHERE status IN ('pending','failed')")); // waiting is not an attempt
        Assert.Contains("not consumed", await env.TextAsync("SELECT last_error FROM integration.delivery WHERE status IN ('pending','failed') LIMIT 1"), StringComparison.Ordinal);

        foreach (var f in Directory.GetFiles(dir)) File.Delete(f); // the receiver processed and removed everything
        await env.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.Single(Directory.GetFiles(dir, "*.manifest.json"));
    }
}
