using Keel.Core;
using Keel.Core.Viewing;
using Keel.TestSupport;
using Keel.Viewing;

namespace Keel.UnitTests;

/// <summary>
/// The viewer service's own rules, with a scripted file source (the persistence-backed behaviour is covered by the integration tests):
/// the concurrency bound on files held in memory and the order of checks (refusals happen before a file is read).
/// </summary>
public sealed class ViewerServiceTests
{
    private static readonly Subject User = new() { Id = "sub-u", Username = "u", DisplayName = "u", UsPerson = true, Org = "ENG" };
    private static readonly Markings Open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;

    private sealed class ScriptedSource(byte[] bytes, string fileName) : IViewableFileSource
    {
        private readonly ViewableFile _file = new("D-000001", "Doc", Guid.NewGuid(), fileName, "application/octet-stream", bytes.Length, new string('c', 64), "A.1", Open, Open.BannerText(), []);
        public int Reads;
        public TaskCompletionSource<bool>? Hold;
        public List<ViewRecord> Records { get; } = [];

        public Task<Outcome<ViewableFile>> DescribeAsync(Subject subject, string number, Guid fileId, string? correlationId, CancellationToken ct = default) =>
            Task.FromResult(Outcome<ViewableFile>.Ok(_file));

        public Task<Outcome<ViewableFile>> FindPrimaryAsync(Subject subject, string number, string? revision, string? correlationId, CancellationToken ct = default) =>
            Task.FromResult(Outcome<ViewableFile>.Ok(_file));

        public async Task<Outcome<ViewContent>> ReadAsync(Subject subject, string number, Guid fileId, string? correlationId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Reads);
            if (Hold is not null) await Hold.Task;
            return Outcome<ViewContent>.Ok(new ViewContent(_file, bytes, false));
        }

        public Task<byte[]> ReadHeadAsync(ViewableFile file, int count, CancellationToken ct = default) => Task.FromResult(bytes[..Math.Min(count, bytes.Length)]);

        public Task RecordAsync(Subject subject, ViewableFile file, ViewRecord record, string? correlationId, CancellationToken ct = default)
        {
            lock (Records) Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class Refuse : IFileViewGate
    {
        public ValueTask<ViewGateDecision> CheckAsync(ViewableFile file, CancellationToken ct = default) => ValueTask.FromResult(ViewGateDecision.Refuse("Not scanned yet."));
    }

    [Fact]
    public async Task Only_a_bounded_number_of_files_are_held_in_memory_and_waiting_callers_get_busy()
    {
        var source = new ScriptedSource(ViewerSamples.AsciiStlCube(), "a.stl") { Hold = new TaskCompletionSource<bool>() };
        using var service = new ViewerService(source, new ViewerOptions { MaxConcurrentReads = 1, ReadWaitSeconds = 1 }, []);

        var first = service.OpenAsync(User, "D-000001", Guid.NewGuid(), null);
        while (Volatile.Read(ref source.Reads) == 0) await Task.Delay(10);

        var second = await service.OpenAsync(User, "D-000001", Guid.NewGuid(), null);
        Assert.Equal(ViewerContentStatus.Busy, second.Status);
        Assert.Equal(1, source.Reads); // the waiting caller never read the file

        source.Hold.SetResult(true);
        Assert.True((await first).IsOk);
        Assert.True((await service.OpenAsync(User, "D-000001", Guid.NewGuid(), null)).IsOk); // the slot is released
    }

    [Fact]
    public async Task Gates_and_size_limits_refuse_before_the_file_is_read_and_each_refusal_is_recorded_once()
    {
        var gated = new ScriptedSource(ViewerSamples.AsciiStlCube(), "a.stl");
        using var withGate = new ViewerService(gated, new ViewerOptions(), [new Refuse()]);
        var blocked = await withGate.OpenAsync(User, "D-000001", Guid.NewGuid(), null);
        Assert.Equal((ViewerContentStatus.Blocked, "Not scanned yet."), (blocked.Status, blocked.Message));
        Assert.Equal(0, gated.Reads);
        Assert.Equal(ViewOutcome.Refused, Assert.Single(gated.Records).Outcome);

        var big = new ScriptedSource(ViewerSamples.AsciiStlCube(), "a.stl");
        using var small = new ViewerService(big, new ViewerOptions { MaxModelBytes = 10 }, []);
        Assert.Equal(ViewerContentStatus.TooLarge, (await small.OpenAsync(User, "D-000001", Guid.NewGuid(), null)).Status);
        Assert.Equal(0, big.Reads);
        Assert.Equal(ViewOutcome.Refused, Assert.Single(big.Records).Outcome);

        var ok = new ScriptedSource(ViewerSamples.AsciiStlCube(), "a.stl");
        using var fine = new ViewerService(ok, new ViewerOptions(), []);
        var delivered = await fine.OpenAsync(User, "D-000001", Guid.NewGuid(), null);
        Assert.True(delivered.IsOk);
        var record = Assert.Single(ok.Records);
        Assert.Equal((ViewOutcome.Delivered, "model3d", "stl", "overlay"), (record.Outcome, record.ViewerType, record.Format, record.Handling));
    }

    [Fact]
    public async Task A_site_can_switch_the_lgpl_cad_importer_off_and_step_then_shows_facts_only()
    {
        var step = new ScriptedSource(ViewerSamples.StepBox(), "box.step");
        using var off = new ViewerService(step, new ViewerOptions { EnableCadImport = false }, []);
        var described = (await off.DescribeAsync(User, "D-000001", Guid.NewGuid(), null)).Value!;
        Assert.Equal(ViewerStatus.NoPreview, described.Status);
        Assert.Contains("switched off", described.Message, StringComparison.Ordinal);
        Assert.Equal(ViewerContentStatus.NoPreview, (await off.OpenAsync(User, "D-000001", Guid.NewGuid(), null)).Status);
        Assert.Equal(0, step.Reads);

        using var on = new ViewerService(new ScriptedSource(ViewerSamples.StepBox(), "box.step"), new ViewerOptions(), []);
        Assert.Equal(ViewerStatus.Ready, (await on.DescribeAsync(User, "D-000001", Guid.NewGuid(), null)).Value!.Status);
        // Other 3D formats are unaffected by the switch.
        using var stl = new ViewerService(new ScriptedSource(ViewerSamples.AsciiStlCube(), "a.stl"), new ViewerOptions { EnableCadImport = false }, []);
        Assert.Equal(ViewerStatus.Ready, (await stl.DescribeAsync(User, "D-000001", Guid.NewGuid(), null)).Value!.Status);
    }

    [Fact]
    public void Options_validation_rejects_nonsense()
    {
        Assert.Empty(new ViewerOptions().Validate());
        Assert.NotEmpty(new ViewerOptions { MaxModelBytes = 0 }.Validate());
        Assert.NotEmpty(new ViewerOptions { MaxTriangles = 10 }.Validate());
        Assert.NotEmpty(new ViewerOptions { MaxConcurrentReads = 0 }.Validate());
        Assert.Equal(100 * ViewerOptions.MiB, new ViewerOptions().MaxModelBytes);
        Assert.Equal(50 * ViewerOptions.MiB, new ViewerOptions().MaxPdfBytes);
    }
}
