using Keel.Core;
using Keel.Workflow;
using static Keel.IntegrationTests.WorkflowEnv;

namespace Keel.IntegrationTests;

/// <summary>
/// Races on one workflow: everything serializes on the instance row then the object row, so there are no lost updates,
/// double releases or deadlocks (a deadlock would surface as a PostgresException and fail these tests).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkflowConcurrencyTests(PostgresFixture pg)
{
    [Fact]
    public async Task Two_approvers_signing_at_the_same_instant_both_count_and_release_exactly_once()
    {
        var env = await CreateAsync(pg);
        for (var round = 0; round < 6; round++)
        {
            var part = await env.CreatePartAsync($"Racer {round}");
            var started = await env.StartAsync(part, "rita", "bob", "carol", "dave");
            var afterReview = await env.SignAsync(Rita, started.Tasks[0].Id, "Reviewed");

            using var gate = new ManualResetEventSlim(false);
            var signers = new[] { Bob, Carol, Dave };
            var attempts = signers.Select(s => Task.Run(async () =>
            {
                gate.Wait();
                return await env.Flow.SignAsync(Fresh(s), TaskOf(afterReview, s.Username!), "Approved", null, null);
            })).ToList();
            await Task.Delay(100);
            gate.Set();
            var results = await Task.WhenAll(attempts);

            Assert.Equal(3, results.Count(r => r.IsOk));
            var final = await env.ViewAsync(Alice, started.Id);
            Assert.Equal("Approved", final.Status);
            Assert.Equal(4, final.Signatures.Count);
            Assert.Equal("Released", await env.PartStateAsync(part));
            Assert.Equal(1, await env.ScalarAsync($"SELECT count(*) FROM audit.event WHERE action = 'workflow.complete' AND object_id = '{part}'"));
        }
    }

    [Fact]
    public async Task Concurrent_double_sign_of_one_task_records_exactly_one_signature()
    {
        var env = await CreateAsync(pg);
        var part = await env.CreatePartAsync();
        var started = await env.StartAsync(part, "rita", "bob");
        var afterReview = await env.SignAsync(Rita, started.Tasks[0].Id, "Reviewed");
        var taskId = TaskOf(afterReview, "bob");

        using var gate = new ManualResetEventSlim(false);
        var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            gate.Wait();
            return await env.Flow.SignAsync(Fresh(Bob), taskId, "Approved", null, null);
        })).ToList();
        await Task.Delay(100);
        gate.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.All(results.Where(r => !r.IsOk), r => Assert.Equal(OutcomeKind.Conflict, r.Kind));
        Assert.Equal(1, await env.ScalarAsync($"SELECT count(*) FROM wf.signature WHERE task_id = '{taskId}'"));
        Assert.Equal(2, await env.ScalarAsync("SELECT count(*) FROM wf.signature"));
        Assert.Equal("Released", await env.PartStateAsync(part));
    }

    [Fact]
    public async Task Two_simultaneous_starts_create_one_workflow()
    {
        var env = await CreateAsync(pg);
        var part = await env.CreatePartAsync();
        using var gate = new ManualResetEventSlim(false);
        var attempts = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            gate.Wait();
            return await env.Flow.StartAsync(Alice, Start(part, "rita", "bob"), null);
        })).ToList();
        await Task.Delay(100);
        gate.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.All(results.Where(r => !r.IsOk), r => Assert.Equal(OutcomeKind.Conflict, r.Kind));
        Assert.Equal(1, await env.ScalarAsync("SELECT count(*) FROM wf.instance"));
        Assert.Equal("UnderReview", await env.PartStateAsync(part));
    }

    [Fact]
    public async Task Start_racing_cancel_never_leaves_two_active_workflows_or_a_part_without_its_workflow()
    {
        var env = await CreateAsync(pg);
        for (var round = 0; round < 6; round++)
        {
            var part = await env.CreatePartAsync($"Start-cancel {round}");
            var first = await env.StartAsync(part);
            using var gate = new ManualResetEventSlim(false);
            var cancel = Task.Run(async () => { gate.Wait(); return await env.Flow.CancelAsync(Alice, first.Id, "Racing", null); });
            var start = Task.Run(async () => { gate.Wait(); return await env.Flow.StartAsync(Alice, Start(part, "rita", "carol"), null); });
            await Task.Delay(30);
            gate.Set();
            var (cancelled, restarted) = (await cancel, await start);

            Assert.True(cancelled.IsOk);
            Assert.True(restarted.IsOk || restarted.Kind == OutcomeKind.Conflict, restarted.Kind.ToString());
            var active = await env.ScalarAsync($"SELECT count(*) FROM wf.instance WHERE object_key = '{part}' AND status = 'Active'");
            Assert.Equal(restarted.IsOk ? 1 : 0, active);
            Assert.Equal(restarted.IsOk ? "UnderReview" : "InWork", await env.PartStateAsync(part));
        }
    }

    [Fact]
    public async Task The_last_approval_racing_a_cancel_has_exactly_one_winner_and_a_consistent_part()
    {
        var env = await CreateAsync(pg);
        for (var round = 0; round < 8; round++)
        {
            var part = await env.CreatePartAsync($"Sign-cancel {round}");
            var started = await env.StartAsync(part, "rita", "bob");
            var afterReview = await env.SignAsync(Rita, started.Tasks[0].Id, "Reviewed");
            var bobTask = TaskOf(afterReview, "bob");

            using var gate = new ManualResetEventSlim(false);
            var sign = Task.Run(async () => { gate.Wait(); return await env.Flow.SignAsync(Fresh(Bob), bobTask, "Approved", null, null); });
            var cancel = Task.Run(async () => { gate.Wait(); return await env.Flow.CancelAsync(Alice, started.Id, "Racing", null); });
            await Task.Delay(30);
            gate.Set();
            var (signed, cancelled) = (await sign, await cancel);

            Assert.True(signed.IsOk ^ cancelled.IsOk, $"sign {signed.Kind}, cancel {cancelled.Kind}");
            var final = await env.ViewAsync(Alice, started.Id);
            if (signed.IsOk)
            {
                Assert.Equal(("Approved", "Released"), (final.Status, await env.PartStateAsync(part)));
                Assert.Equal(OutcomeKind.Conflict, cancelled.Kind);
            }
            else
            {
                Assert.Equal(("Cancelled", "InWork"), (final.Status, await env.PartStateAsync(part)));
                Assert.Equal(OutcomeKind.Conflict, signed.Kind);
                Assert.Equal(1, await env.ScalarAsync($"SELECT count(*) FROM wf.signature WHERE instance_id = '{started.Id}'"));   // only Rita's
            }
        }
    }

    [Fact]
    public async Task A_rejection_racing_an_approval_decides_once()
    {
        var env = await CreateAsync(pg);
        for (var round = 0; round < 6; round++)
        {
            var part = await env.CreatePartAsync($"Vote race {round}");
            var started = await env.StartAsync(part, "rita", "bob", "carol");
            var afterReview = await env.SignAsync(Rita, started.Tasks[0].Id, "Reviewed");

            using var gate = new ManualResetEventSlim(false);
            var approve = Task.Run(async () => { gate.Wait(); return await env.Flow.SignAsync(Fresh(Bob), TaskOf(afterReview, "bob"), "Approved", null, null); });
            var reject = Task.Run(async () => { gate.Wait(); return await env.Flow.SignAsync(Fresh(Carol), TaskOf(afterReview, "carol"), "Rejected", "No", null); });
            await Task.Delay(30);
            gate.Set();
            var (a, r) = (await approve, await reject);

            // Whichever order the database serialized them in: the rejection decides the All vote; an approval that arrives later is refused.
            Assert.True(r.IsOk);
            var final = await env.ViewAsync(Alice, started.Id);
            Assert.Equal("Rejected", final.Status);
            Assert.Equal("InWork", await env.PartStateAsync(part));
            Assert.True(a.IsOk || a.Kind == OutcomeKind.Conflict);
            Assert.Equal(a.IsOk ? 3 : 2, final.Signatures.Count);
        }
    }
}
