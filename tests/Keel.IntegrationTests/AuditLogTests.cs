using System.Security.Cryptography;
using Keel.Audit;
using Npgsql;

namespace Keel.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class AuditLogTests(PostgresFixture pg)
{
    private static AuditEntry Entry(string action, string? objectId = null) => new()
    {
        Actor = new ActorSnapshot("user-1", true, "USA", "ENG", ["ORCA"], [], ["plm-author"]),
        Action = action,
        Outcome = AuditOutcome.Success,
        ObjectType = "part",
        ObjectId = objectId,
        CorrelationId = Guid.NewGuid().ToString("D"),
        Details = AuditDetails.Of(("note", "ünïcødé and \"quotes\""), ("empty", null)),
    };

    private static async Task<VerificationResult> Verify(TestDatabase db, params ECDsa[] keys)
    {
        await using var conn = new NpgsqlConnection(db.SuperuserConnectionString);
        await conn.OpenAsync();
        return await AuditVerifier.VerifyAsync(conn, keys);
    }

    [Fact]
    public async Task Appended_events_form_a_valid_chain()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var ds = db.AppDataSource();
        var log = new PostgresAuditLog(ds, TimeProvider.System);

        var first = await log.AppendAsync(Entry("part.create", "P-1"));
        var second = await log.AppendAsync(Entry("part.read", "P-1"));

        Assert.Equal(1, first.Seq);
        Assert.Equal(AuditChain.GenesisHash, first.PrevHash);
        Assert.Equal(first.Hash, second.PrevHash);
        var result = await Verify(db);
        Assert.True(result.Valid, string.Join("\n", result.Failures));
        Assert.Equal(2, result.EventsChecked);
    }

    [Fact]
    public async Task Concurrent_appends_stay_gapless_and_valid()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var ds = db.AppDataSource();
        var log = new PostgresAuditLog(ds, TimeProvider.System);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() => log.AppendAsync(Entry("part.read", $"P-{i}")))));

        var result = await Verify(db);
        Assert.True(result.Valid, string.Join("\n", result.Failures));
        Assert.Equal(200, result.EventsChecked);
        Assert.Equal(200, result.LastSeq);
    }

    [Fact]
    public async Task Audit_event_rolls_back_with_the_business_transaction()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var ds = db.AppDataSource();
        var log = new PostgresAuditLog(ds, TimeProvider.System);

        await using (var conn = await ds.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await log.AppendAsync(tx, Entry("part.create", "P-rolled-back"));
            await tx.RollbackAsync();
        }
        await log.AppendAsync(Entry("part.create", "P-kept"));

        var events = await new AuditQueries(ds).ListAsync(new AuditQuery());
        var only = Assert.Single(events);
        Assert.Equal("P-kept", only.ObjectId);
        Assert.Equal(1, only.Seq);
        Assert.True((await Verify(db)).Valid);
    }

    [Theory]
    [InlineData("UPDATE audit.event SET actor = 'mallory'")]
    [InlineData("DELETE FROM audit.event")]
    [InlineData("TRUNCATE audit.event")]
    [InlineData("UPDATE audit.event SET actor = 'mallory' WHERE seq = 1")]
    public async Task Application_role_cannot_modify_audit_records(string sql)
    {
        var db = await pg.CreateDatabaseAsync();
        await using var ds = db.AppDataSource();
        await new PostgresAuditLog(ds, TimeProvider.System).AppendAsync(Entry("part.read"));

        await using var cmd = ds.CreateCommand(sql);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Owner_role_is_blocked_by_immutability_trigger()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var ds = db.AppDataSource();
        await new PostgresAuditLog(ds, TimeProvider.System).AppendAsync(Entry("part.read"));

        await using var owner = NpgsqlDataSource.Create(db.MigratorConnectionString);
        await using var cmd = owner.CreateCommand("DELETE FROM audit.event");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Contains("audit records are immutable", ex.MessageText);
    }

    [Fact]
    public async Task Superuser_payload_edit_is_detected()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var ds = db.AppDataSource();
        var log = new PostgresAuditLog(ds, TimeProvider.System);
        for (var i = 0; i < 5; i++) await log.AppendAsync(Entry("part.read", $"P-{i}"));

        await db.ExecuteAsSuperuserAsync("""
            ALTER TABLE audit.event DISABLE TRIGGER event_immutable;
            UPDATE audit.event SET payload = replace(payload, 'P-2', 'P-9'), object_id = 'P-9' WHERE seq = 3;
            ALTER TABLE audit.event ENABLE TRIGGER event_immutable;
            """);

        var result = await Verify(db);
        Assert.False(result.Valid);
        Assert.Contains("seq 3: hash does not match payload", result.Failures);
    }

    [Fact]
    public async Task Rehashed_rewrite_is_caught_by_signed_checkpoint()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var ds = db.AppDataSource();
        var log = new PostgresAuditLog(ds, TimeProvider.System);
        for (var i = 0; i < 5; i++) await log.AppendAsync(Entry("part.read", $"P-{i}"));
        using var key = CheckpointKeys.Generate();
        var cp = await new CheckpointSigner(key, ds, TimeProvider.System).CreateAsync();
        Assert.NotNull(cp);
        Assert.Equal(5, cp.Seq);
        Assert.True((await Verify(db, key)).Valid);

        // An attacker with superuser rewrites event 2 and recomputes every later hash so the chain itself is consistent.
        await using (var conn = new NpgsqlConnection(db.SuperuserConnectionString))
        {
            await conn.OpenAsync();
            await using (var off = new NpgsqlCommand("ALTER TABLE audit.event DISABLE TRIGGER event_immutable", conn)) await off.ExecuteNonQueryAsync();
            var rows = new List<(long Seq, string Payload)>();
            await using (var read = new NpgsqlCommand("SELECT seq, payload FROM audit.event ORDER BY seq", conn))
            await using (var r = await read.ExecuteReaderAsync())
                while (await r.ReadAsync()) rows.Add((r.GetInt64(0), r.GetString(1)));
            var prev = AuditChain.GenesisHash;
            foreach (var (seq, payload) in rows)
            {
                var p = seq == 2 ? payload.Replace("P-1", "P-X", StringComparison.Ordinal) : payload;
                var h = AuditChain.ComputeHash(prev, seq, p);
                await using var upd = new NpgsqlCommand("UPDATE audit.event SET payload = @p, object_id = CASE WHEN seq = 2 THEN 'P-X' ELSE object_id END, prev_hash = @prev, hash = @h WHERE seq = @s", conn);
                upd.Parameters.AddWithValue("p", p);
                upd.Parameters.AddWithValue("prev", prev);
                upd.Parameters.AddWithValue("h", h);
                upd.Parameters.AddWithValue("s", seq);
                await upd.ExecuteNonQueryAsync();
                prev = h;
            }
        }

        var result = await Verify(db, key);
        Assert.False(result.Valid);
        Assert.Single(result.Failures);
        Assert.StartsWith("seq 5: does not match checkpoint", result.Failures[0]);
    }

    [Fact]
    public async Task Tail_deletion_before_a_checkpoint_is_detected_and_unknown_keys_are_rejected()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var ds = db.AppDataSource();
        var log = new PostgresAuditLog(ds, TimeProvider.System);
        for (var i = 0; i < 3; i++) await log.AppendAsync(Entry("part.read"));
        using var key = CheckpointKeys.Generate();
        await new CheckpointSigner(key, ds, TimeProvider.System).CreateAsync();

        using var otherKey = CheckpointKeys.Generate();
        var untrusted = await Verify(db, otherKey);
        Assert.Contains(untrusted.Failures, f => f.Contains("unknown key", StringComparison.Ordinal));

        // Superuser bypasses triggers and the checkpoint foreign key to delete the newest event.
        await db.ExecuteAsSuperuserAsync("""
            SET session_replication_role = replica;
            DELETE FROM audit.event WHERE seq = 3;
            """);

        var result = await Verify(db, key);
        Assert.False(result.Valid);
        Assert.Equal(["checkpoint at seq 3 is beyond the last event 2 (events deleted)"], result.Failures);
    }

    [Fact]
    public async Task Checkpoint_is_skipped_when_head_already_signed()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var ds = db.AppDataSource();
        using var key = CheckpointKeys.Generate();
        var signer = new CheckpointSigner(key, ds, TimeProvider.System);
        Assert.Null(await signer.CreateAsync());
        await new PostgresAuditLog(ds, TimeProvider.System).AppendAsync(Entry("part.read"));
        Assert.NotNull(await signer.CreateAsync());
        Assert.Null(await signer.CreateAsync());
    }
}
