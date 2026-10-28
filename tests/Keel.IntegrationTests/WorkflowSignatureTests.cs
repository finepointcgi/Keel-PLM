using Keel.Audit;
using Keel.Core;
using Keel.Workflow;
using Npgsql;
using static Keel.IntegrationTests.WorkflowEnv;

namespace Keel.IntegrationTests;

/// <summary>E-signatures: step-up, immutability and tamper detection (A9, ADR-0007).</summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkflowSignatureTests(PostgresFixture pg)
{
    private static async Task<(WorkflowEnv Env, string Part, InstanceView Started)> Started(PostgresFixture pg)
    {
        var env = await CreateAsync(pg);
        var part = await env.CreatePartAsync();
        return (env, part, await env.StartAsync(part, "rita", "bob"));
    }

    // ---------------------------------------------------------------- step-up

    [Fact]
    public async Task Signing_with_a_stale_authentication_is_refused_with_a_distinct_outcome_and_changes_nothing()
    {
        var (env, part, started) = await Started(pg);
        var taskId = started.Tasks[0].Id;
        var stale = Rita with { AuthTime = DateTimeOffset.UtcNow.AddSeconds(-301) };

        var r = await env.Flow.SignAsync(stale, taskId, "Reviewed", null, "corr-stale");
        Assert.Equal(OutcomeKind.StepUpRequired, r.Kind);
        Assert.Equal(["stepup.required", "300"], r.Errors);
        Assert.Equal(OutcomeKind.StepUpRequired, (await env.Flow.SignAsync(WithoutAuthTime(Rita), taskId, "Reviewed", null, "corr-stale")).Kind);

        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM wf.signature"));
        Assert.Equal("Open", (await env.ViewAsync(Alice, started.Id)).Tasks[0].Status);
        var denial = (await env.AuditAsync()).Last(e => e.Action == "workflow.sign");
        Assert.Equal((AuditOutcome.Denied, "corr-stale"), (denial.Outcome, denial.CorrelationId));
        Assert.Contains("stepup.required", denial.Payload, StringComparison.Ordinal);

        // The task view tells the UI to ask for re-authentication before the user commits to a decision.
        Assert.True((await env.Flow.GetTaskAsync(stale, taskId, null)).Value!.StepUpRequired);
        Assert.False((await env.Flow.GetTaskAsync(Fresh(Rita), taskId, null)).Value!.StepUpRequired);

        // A fresh authentication is accepted and recorded on the signature.
        var authTime = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddSeconds(-299).ToUnixTimeSeconds());   // IdPs release whole seconds
        var ok = await env.Flow.SignAsync(Rita with { AuthTime = authTime }, taskId, "Reviewed", null, null);
        Assert.True(ok.IsOk);
        Assert.Equal(1, await env.ScalarAsync($"SELECT count(*) FROM wf.signature WHERE auth_time = to_timestamp({authTime.ToUnixTimeSeconds()})"));
        Assert.Equal("UnderReview", await env.PartStateAsync(part));
    }

    [Fact]
    public async Task The_window_is_measured_against_the_clock_and_is_configurable()
    {
        var (env, _, started) = await Started(pg);
        var signedIn = env.Clock.GetUtcNow();
        var rita = Rita with { AuthTime = signedIn };
        var taskId = started.Tasks[0].Id;

        env.Clock.Advance(TimeSpan.FromSeconds(290));
        Assert.False((await env.Flow.GetTaskAsync(rita, taskId, null)).Value!.StepUpRequired);
        env.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(OutcomeKind.StepUpRequired, (await env.Flow.SignAsync(rita, taskId, "Reviewed", null, null)).Kind);

        env.Options.StepUpMaxAgeSeconds = 900;                    // an operator widens the window
        Assert.True((await env.Flow.SignAsync(rita, taskId, "Reviewed", null, null)).IsOk);
    }

    [Fact]
    public async Task Bad_input_and_missing_rights_are_reported_before_asking_for_re_authentication()
    {
        var (env, _, started) = await Started(pg);
        var taskId = started.Tasks[0].Id;
        var stale = Rita with { AuthTime = DateTimeOffset.UtcNow.AddHours(-3) };

        Assert.Equal(OutcomeKind.Invalid, (await env.Flow.SignAsync(stale, taskId, "Rejected", null, null)).Kind);          // comment missing
        Assert.Equal(OutcomeKind.Invalid, (await env.Flow.SignAsync(stale, taskId, "Approved", null, null)).Kind);          // not a review meaning
        Assert.Equal(OutcomeKind.Invalid, (await env.Flow.SignAsync(stale, taskId, "Nonsense", null, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Flow.SignAsync(stale, taskId, "Reviewed", new string('x', 2001), null)).Kind);
        var stranger = Bob with { AuthTime = DateTimeOffset.UtcNow.AddHours(-3) };
        Assert.Equal(OutcomeKind.Forbidden, (await env.Flow.SignAsync(stranger, taskId, "Reviewed", null, null)).Kind);     // not the assignee
        Assert.Equal(OutcomeKind.NotFound, (await env.Flow.SignAsync(stale, Guid.NewGuid(), "Reviewed", null, null)).Kind);
    }

    [Fact]
    public async Task The_submitter_cannot_take_part_in_their_own_review()
    {
        var (env, _, started) = await Started(pg);
        // Alice (submitter) holds the author role and asks to sign Rita's task: wrong assignee.
        var r = await env.Flow.SignAsync(Fresh(Alice), started.Tasks[0].Id, "Reviewed", null, null);
        Assert.Equal(OutcomeKind.Forbidden, r.Kind);
        // Even if an administrator reassigned a task to the submitter, separation of duties holds; the reassignment itself is refused.
        var reassign = await env.Flow.ReassignTaskAsync(Ada, started.Tasks[0].Id, "alice", "Trying to self-approve", null);
        Assert.Equal(OutcomeKind.Invalid, reassign.Kind);
        Assert.Contains("submitted this review", reassign.Errors[0], StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- immutability

    [Fact]
    public async Task Signatures_cannot_be_changed_or_removed_by_anyone()
    {
        var (env, _, started) = await Started(pg);
        await env.SignAsync(Rita, started.Tasks[0].Id, "Reviewed");

        foreach (var sql in new[] { "UPDATE wf.signature SET comment = 'edited'", "DELETE FROM wf.signature", "UPDATE wf.event SET detail = 'edited'", "DELETE FROM wf.event" })
        {
            // As the application role and as the schema owner: the trigger rejects both.
            await using var app = env.Ds.CreateCommand(sql);
            var e1 = await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, e1.SqlState);
            await using var owner = new NpgsqlConnection(env.Db.MigratorConnectionString);
            await owner.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, owner);
            var e2 = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, e2.SqlState);
        }
        await using var truncateConn = new NpgsqlConnection(env.Db.MigratorConnectionString);
        await truncateConn.OpenAsync();
        await using var truncate = new NpgsqlCommand("TRUNCATE wf.signature CASCADE", truncateConn);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync())).SqlState);
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM wf.signature"));

        // One signature per task, enforced by the database.
        await using var dup = env.Ds.CreateCommand(
            "INSERT INTO wf.signature SELECT (jsonb_populate_record(NULL::wf.signature, to_jsonb(s) || jsonb_build_object('id', gen_random_uuid()))).* FROM wf.signature s");
        Assert.Equal("23505", (await Assert.ThrowsAsync<PostgresException>(() => dup.ExecuteNonQueryAsync())).SqlState);
    }

    // ---------------------------------------------------------------- verification

    [Fact]
    public async Task Verification_recomputes_hashes_and_detects_tampering_with_the_stored_iteration()
    {
        var (env, part, started) = await Started(pg);
        var afterReview = await env.SignAsync(Rita, started.Tasks[0].Id, "Reviewed");
        var done = await env.SignAsync(Bob, TaskOf(afterReview, "bob"), "Approved");

        var clean = (await env.Flow.VerifyInstanceAsync(Alice, done.Id, "corr-verify")).Value!;
        Assert.Equal(2, clean.Count);
        Assert.All(clean, v => Assert.True(v.Valid, string.Join("; ", v.Problems)));
        Assert.All(clean, v => Assert.False(v.MarkingsChangedSince));
        Assert.StartsWith("Signed by Rita Example (rita) on ", clean[0].Manifestation, StringComparison.Ordinal);

        // An attacker with database access edits the signed iteration (bypassing the immutability trigger as a superuser).
        await env.Db.ExecuteAsSuperuserAsync("SET session_replication_role = replica; UPDATE plm.part_iteration SET description = 'Tampered description'");
        var tampered = (await env.Flow.VerifyInstanceAsync(Alice, done.Id, null)).Value!;
        Assert.All(tampered, v =>
        {
            Assert.False(v.Valid);
            Assert.Contains(v.Problems, p => p.Contains("iteration content", StringComparison.Ordinal) && p.Contains("changed after signing", StringComparison.Ordinal));
        });
        var audit = (await env.AuditAsync()).Where(e => e.Action == "workflow.verify").ToList();
        Assert.Equal([AuditOutcome.Success, AuditOutcome.Failure], audit.Select(a => a.Outcome));
        Assert.Contains(done.Signatures[0].Id.ToString("D"), audit[1].Payload, StringComparison.Ordinal);

        // Restoring the original text makes the hashes verify again: the check is purely content-based.
        await env.Db.ExecuteAsSuperuserAsync("SET session_replication_role = replica; UPDATE plm.part_iteration SET description = 'Synthetic bracket'");
        Assert.All((await env.Flow.VerifyInstanceAsync(Alice, done.Id, null)).Value!, v => Assert.True(v.Valid));

        // Editing attributes is caught as well.
        await env.Db.ExecuteAsSuperuserAsync("SET session_replication_role = replica; UPDATE plm.part_iteration SET attributes = jsonb_set(attributes, '{mass}', '0.01')");
        Assert.All((await env.Flow.VerifyInstanceAsync(Alice, done.Id, null)).Value!, v => Assert.False(v.Valid));
        Assert.NotEmpty(part);
    }

    [Fact]
    public async Task Verification_detects_edits_to_the_signature_record_itself()
    {
        var (env, _, started) = await Started(pg);
        var afterReview = await env.SignAsync(Rita, started.Tasks[0].Id, "Reviewed");
        var done = await env.SignAsync(Bob, TaskOf(afterReview, "bob"), "Approved");
        await env.Db.ExecuteAsSuperuserAsync("SET session_replication_role = replica; UPDATE wf.signature SET comment = 'forged', meaning_code = 'Approved' WHERE signer_username = 'rita'");

        var results = (await env.Flow.VerifyInstanceAsync(Alice, done.Id, null)).Value!;
        var rita = results.Single(r => r.Manifestation.Contains("Rita", StringComparison.Ordinal));
        Assert.False(rita.Valid);
        Assert.Contains(rita.Problems, p => p.Contains("signature record does not match its hash", StringComparison.Ordinal));
        Assert.True(results.Single(r => r.Manifestation.Contains("Bob", StringComparison.Ordinal)).Valid);
    }

    [Fact]
    public async Task Markings_that_change_after_signing_are_reported_but_are_not_tampering()
    {
        var (env, _, started) = await Started(pg);
        var afterReview = await env.SignAsync(Rita, started.Tasks[0].Id, "Reviewed");
        var done = await env.SignAsync(Bob, TaskOf(afterReview, "bob"), "Approved");
        await env.Db.ExecuteAsSuperuserAsync("UPDATE plm.part_master SET distribution_statement = 'E'");
        var changed = (await env.Flow.VerifyInstanceAsync(Alice, done.Id, null)).Value!;
        Assert.All(changed, v => { Assert.True(v.Valid); Assert.True(v.MarkingsChangedSince); });
    }

    [Fact]
    public async Task Only_readers_can_verify_and_the_denial_is_audited()
    {
        var (env, _, started) = await Started(pg);
        Assert.Equal(OutcomeKind.NotFound, (await env.Flow.VerifyInstanceAsync(Lena, started.Id, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await env.Flow.VerifyInstanceAsync(Alice, Guid.NewGuid(), null)).Kind);
        Assert.Contains((await env.AuditAsync()), e => e.Action == "workflow.verify" && e.Outcome == AuditOutcome.Denied);
        Assert.Empty((await env.Flow.VerifyInstanceAsync(Alice, started.Id, null)).Value!);
    }

    [Fact]
    public async Task The_signature_covers_the_markings_banner_and_identity_at_signing_time()
    {
        var (env, part, started) = await Started(pg);
        var signed = await env.SignAsync(Rita, started.Tasks[0].Id, "Reviewed");
        var s = Assert.Single(signed.Signatures);
        Assert.Equal((part, "A", "A.1", "CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA", "rita", "Rita Example"),
            (s.ObjectKey, s.Revision, s.IterationLabel, s.MarkingBanner, s.SignerUsername, s.SignerName));
        Assert.Equal(64, s.ContentHash.Length);
        Assert.NotEqual(s.ContentHash, s.SignatureHash);
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM wf.signature WHERE octet_length(content_hash) = 32 AND octet_length(signature_hash) = 32 AND signer_org = 'ENG'"));
    }
}
