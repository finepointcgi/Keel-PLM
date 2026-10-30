using System.Text;
using Keel.Cad;
using Keel.Core;
using Keel.Core.Viewing;

namespace Keel.UnitTests;

/// <summary>The rules of the Viewer tab's service (ADR-0052) with a scripted file source: nothing is read before authorization, gates, size and type are checked, every refusal is recorded once.</summary>
public sealed class AssemblyViewServiceTests
{
    private static readonly Subject User = new() { Id = "sub-u", Username = "u", DisplayName = "u", UsPerson = true, Org = "ENG" };
    private static readonly Markings Open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cad", name));

    private sealed class ScriptedSource(byte[] bytes, string fileName, bool allowed = true, long? size = null) : IViewableFileSource
    {
        private readonly ViewableFile _file = new("D-000001", "Frame", Guid.NewGuid(), fileName, "application/octet-stream", size ?? bytes.Length, new string('c', 64), "A.1", Open, Open.BannerText(), []);
        public int Reads;
        public List<ViewRecord> Records { get; } = [];

        public Task<Outcome<ViewableFile>> DescribeAsync(Subject subject, string number, Guid fileId, string? correlationId, CancellationToken ct = default) =>
            Task.FromResult(allowed ? Outcome<ViewableFile>.Ok(_file) : Outcome<ViewableFile>.NotFound());
        public Task<Outcome<ViewableFile>> FindPrimaryAsync(Subject subject, string number, string? revision, string? correlationId, CancellationToken ct = default) =>
            Task.FromResult(allowed ? Outcome<ViewableFile>.Ok(_file) : Outcome<ViewableFile>.NotFound());
        public Task<Outcome<ViewContent>> ReadAsync(Subject subject, string number, Guid fileId, string? correlationId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult(Outcome<ViewContent>.Ok(new ViewContent(_file, bytes, false)));
        }
        public Task<byte[]> ReadHeadAsync(ViewableFile file, int count, CancellationToken ct = default) => Task.FromResult(bytes[..Math.Min(count, bytes.Length)]);
        public Task RecordAsync(Subject subject, ViewableFile file, ViewRecord record, string? correlationId, CancellationToken ct = default) { Records.Add(record); return Task.CompletedTask; }
    }

    private sealed class Refuse : IFileViewGate
    {
        public ValueTask<ViewGateDecision> CheckAsync(ViewableFile file, CancellationToken ct = default) => ValueTask.FromResult(ViewGateDecision.Refuse("Not scanned yet."));
    }

    [Fact]
    public async Task A_step_file_is_read_once_audited_as_delivered_and_returns_the_placed_structure()
    {
        var source = new ScriptedSource(Fixture("frame-asm-ap214.step"), "frame.step");
        using var service = new AssemblyViewService(source, []);
        var r = await service.OpenAsync(User, "D-000001", null, null, null);
        Assert.True(r.IsOk);
        Assert.Equal((AssemblyViewKinds.Assembly, "step"), (r.Kind, r.Format));
        Assert.Equal(24, r.Layout!.Instances.Count);
        Assert.Equal(1, source.Reads);
        var record = Assert.Single(source.Records);
        Assert.Equal((ViewOutcome.Delivered, "model3d", "step", "assembly"), (record.Outcome, record.ViewerType, record.Format, record.Handling));
    }

    [Fact]
    public async Task A_caller_who_may_not_view_the_document_gets_nothing_and_nothing_is_read()
    {
        var source = new ScriptedSource(Fixture("frame-asm-ap214.step"), "frame.step", allowed: false);
        using var service = new AssemblyViewService(source, []);
        var r = await service.OpenAsync(User, "D-000001", Guid.NewGuid(), null, null);
        Assert.Equal(AssemblyViewStatus.NotFound, r.Status);
        Assert.Null(r.File); Assert.Null(r.Message); Assert.Null(r.Model); Assert.Null(r.Layout);
        Assert.Equal(0, source.Reads);
        Assert.Empty(source.Records); // the file source audits the denial itself
    }

    [Fact]
    public async Task Gates_the_size_limit_and_the_file_type_refuse_before_the_file_is_read()
    {
        var gated = new ScriptedSource(Fixture("plate.step"), "plate.step");
        using (var s = new AssemblyViewService(gated, [new Refuse()]))
        {
            var r = await s.OpenAsync(User, "D-000001", null, null, null);
            Assert.Equal((AssemblyViewStatus.Blocked, "Not scanned yet."), (r.Status, r.Message));
        }
        Assert.Equal(0, gated.Reads);
        Assert.Equal(ViewOutcome.Refused, Assert.Single(gated.Records).Outcome);

        var big = new ScriptedSource(Fixture("plate.step"), "plate.step", size: 70L * 1024 * 1024);
        using (var s = new AssemblyViewService(big, []))
            Assert.Equal(AssemblyViewStatus.TooLarge, (await s.OpenAsync(User, "D-000001", null, null, null)).Status);
        Assert.Equal(0, big.Reads);

        var drawing = new ScriptedSource([1, 2, 3], "drawing.dwg");
        using (var s = new AssemblyViewService(drawing, []))
            Assert.Equal(AssemblyViewStatus.NoPreview, (await s.OpenAsync(User, "D-000001", null, null, null)).Status);
        Assert.Equal(0, drawing.Reads);
    }

    [Theory]
    [InlineData("part.stl", AssemblyViewKinds.Mesh)]
    [InlineData("part.obj", AssemblyViewKinds.Mesh)]
    [InlineData("part.3mf", AssemblyViewKinds.Mesh)]
    [InlineData("part.glb", AssemblyViewKinds.Other)]
    [InlineData("part.igs", AssemblyViewKinds.Other)]
    public async Task Other_model_formats_are_described_without_reading_their_bytes_here(string fileName, string kind)
    {
        var source = new ScriptedSource([1, 2, 3], fileName);
        using var service = new AssemblyViewService(source, []);
        var r = await service.OpenAsync(User, "D-000001", null, null, null);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(0, source.Reads); // the browser fetches triangles through the access-checked mesh route, which audits the delivery
        Assert.Empty(source.Records);
    }

    [Fact]
    public async Task A_step_file_that_cannot_be_read_is_refused_with_a_safe_message_and_recorded()
    {
        var source = new ScriptedSource(Encoding.ASCII.GetBytes("ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\n#1=NOTHING();\nENDSEC;\nEND-ISO-10303-21;\n"), "bad.step");
        using var service = new AssemblyViewService(source, []);
        var r = await service.OpenAsync(User, "D-000001", null, null, null);
        Assert.Equal(AssemblyViewStatus.Unprocessable, r.Status);
        Assert.Contains("PRODUCT", r.Message);
        Assert.Equal(ViewOutcome.Refused, Assert.Single(source.Records).Outcome);
    }
}
