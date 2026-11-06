using System.Diagnostics;
using Keel.Core;
using Keel.Documents.Scanning;
using Keel.TestSupport;

namespace Keel.UnitTests;

/// <summary>
/// The clamd INSTREAM client and the scanning policy over a protocol-faithful test server (real TCP, no mocks): framing, EICAR detection across
/// chunk boundaries, timeouts, drops, size limits, the Required / BestEffort / Disabled modes and bounded memory for large streams.
/// </summary>
[Collection("process-memory")]   // the bounded-memory test below reads process-wide managed memory; ThirdPartyLicensesTests allocates heavily and shares this collection so they never overlap
public sealed class ClamdScannerTests
{
    private static ScanningOptions Options(FakeClamd server, ScanMode mode = ScanMode.Required) => new()
    {
        Mode = mode, Host = "127.0.0.1", Port = server.Port, ConnectTimeoutSeconds = 2, IoTimeoutSeconds = 2, ResultTimeoutSeconds = 2, ChunkBytes = 4096,
    };

    private static async Task<ScanResult> ScanAsync(ClamdScanner scanner, byte[] data, int writeSize = 1000)
    {
        await using var scan = await scanner.BeginAsync();
        for (var i = 0; i < data.Length; i += writeSize) await scan.WriteAsync(data.AsMemory(i, Math.Min(writeSize, data.Length - i)));
        return await scan.FinishAsync();
    }

    [Fact]
    public async Task Clean_stream_is_clean_and_reports_engine_and_signature_date()
    {
        await using var server = new FakeClamd();
        var scanner = new ClamdScanner(Options(server));
        var result = await ScanAsync(scanner, new byte[50_000]);

        Assert.Equal(ScanVerdict.Clean, result.Verdict);
        Assert.Equal("clamd", result.Scanner!.Name);
        Assert.Contains("1.4.1", result.Scanner.Version, StringComparison.Ordinal);
        Assert.Equal(new DateTimeOffset(2025, 3, 10, 8, 23, 39, TimeSpan.Zero), result.Scanner.SignatureDatabaseDate);
        Assert.Equal(50_000, server.BytesReceived);
        Assert.True(server.LargestChunk <= 4096, "chunks are bounded by ChunkBytes");
    }

    [Fact]
    public async Task Eicar_is_found_even_when_split_across_chunks()
    {
        await using var server = new FakeClamd();
        var scanner = new ClamdScanner(Options(server));
        var data = new byte[20_000];
        FakeClamd.Eicar.CopyTo(data, 4090); // straddles the 4096-byte chunk boundary
        var result = await ScanAsync(scanner, data, writeSize: 777);

        Assert.Equal(ScanVerdict.Infected, result.Verdict);
        Assert.Equal(FakeClamd.EicarSignature, result.Signature);
    }

    [Fact]
    public async Task Unix_socket_transport_works()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fakeclamd-{Guid.NewGuid():N}.sock");
        await using var server = new FakeClamd(path);
        var options = new ScanningOptions { Mode = ScanMode.Required, UnixSocket = path, IoTimeoutSeconds = 2, ResultTimeoutSeconds = 2 };
        var scanner = new ClamdScanner(options);
        Assert.True(await scanner.PingAsync());
        Assert.Equal(ScanVerdict.Infected, (await ScanAsync(scanner, FakeClamd.Eicar)).Verdict);
    }

    [Fact]
    public async Task Refused_connection_is_scanner_unavailable()
    {
        int port;
        await using (var server = new FakeClamd()) port = server.Port; // stopped: nothing listens any more
        var scanner = new ClamdScanner(new ScanningOptions { Mode = ScanMode.Required, Port = port, ConnectTimeoutSeconds = 1 });
        await Assert.ThrowsAsync<ScannerUnavailableException>(() => scanner.BeginAsync());
        await Assert.ThrowsAsync<ScannerUnavailableException>(() => scanner.GetInfoAsync());
    }

    [Fact]
    public async Task Hung_scanner_times_out_instead_of_blocking_the_upload()
    {
        await using var server = new FakeClamd { NeverAnswer = true };
        var options = Options(server);
        options.ResultTimeoutSeconds = 1;
        var scanner = new ClamdScanner(options);
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<ScannerUnavailableException>(() => ScanAsync(scanner, new byte[1000]));
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.9, 6);
    }

    [Fact]
    public async Task Connection_dropped_mid_stream_is_scanner_unavailable()
    {
        await using var server = new FakeClamd { DropAfterBytes = 10_000 };
        var scanner = new ClamdScanner(Options(server));
        await Assert.ThrowsAsync<ScannerUnavailableException>(() => ScanAsync(scanner, new byte[2_000_000]));
    }

    [Fact]
    public async Task Server_size_limit_error_surfaces_with_its_reason()
    {
        await using var server = new FakeClamd { StreamMaxLength = 10_000 };
        var scanner = new ClamdScanner(Options(server));
        var e = await Assert.ThrowsAnyAsync<ScannerUnavailableException>(() => ScanAsync(scanner, new byte[2_000_000]));
        Assert.Contains("size limit", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Server_error_reply_is_an_error_verdict_not_clean()
    {
        await using var server = new FakeClamd { ErrorReply = "Can't allocate memory. ERROR" };
        var result = await ScanAsync(new ClamdScanner(Options(server)), new byte[100]);
        Assert.Equal(ScanVerdict.Error, result.Verdict);
        Assert.Contains("ERROR", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Large_stream_is_scanned_with_bounded_memory()
    {
        await using var server = new FakeClamd { StreamMaxLength = 1024L * 1024 * 1024 };
        var options = Options(server);
        options.ChunkBytes = 256 * 1024;
        var scanner = new ClamdScanner(options);
        const long total = 400L * 1024 * 1024;
        var block = new byte[1024 * 1024];
        new Random(7).NextBytes(block);

        GC.Collect();
        var baseline = GC.GetTotalMemory(true);
        long peak = 0;
        await using (var scan = await scanner.BeginAsync())
        {
            for (long sent = 0; sent < total; sent += block.Length)
            {
                await scan.WriteAsync(block);
                if ((sent / block.Length) % 16 == 0) peak = Math.Max(peak, GC.GetTotalMemory(true));   // collected first: this measures what is retained, not garbage the GC has not got to yet
            }
            Assert.Equal(ScanVerdict.Clean, (await scan.FinishAsync()).Verdict);
        }
        Assert.Equal(total, server.BytesReceived);
        // 400 MiB went through; the client never held more than a few chunks (the fake server's own window is a few bytes too).
        Assert.True(peak - baseline < 64L * 1024 * 1024, $"managed memory grew by {(peak - baseline) / (1024 * 1024)} MiB");
    }

    // ---------------------------------------------------------------- policy

    private static UploadScanning Policy(FakeClamd server, ScanMode mode, long? maxScan = null)
    {
        var options = Options(server, mode);
        if (maxScan is { } m) options.MaxScanBytes = m;
        return new UploadScanning(new ClamdScanner(options), options, TimeProvider.System);
    }

    private static async Task<ScanRecord> RunTeeAsync(UploadScanning scanning, byte[] data)
    {
        await using var tee = await scanning.BeginAsync();
        await tee.FeedAsync(data);
        return await tee.CompleteAsync(new string('a', 64), data.Length);
    }

    [Fact]
    public async Task Required_mode_clean_upload_is_recorded_clean()
    {
        await using var server = new FakeClamd();
        var record = await RunTeeAsync(Policy(server, ScanMode.Required), new byte[5000]);
        Assert.Equal(ScanStatus.Clean, record.Status);
        Assert.Equal("clamd", record.ScannerName);
    }

    [Theory]
    [InlineData(ScanMode.Required)]
    [InlineData(ScanMode.BestEffort)]
    public async Task Infected_upload_is_refused_in_every_scanning_mode(ScanMode mode)
    {
        await using var server = new FakeClamd();
        var e = await Assert.ThrowsAsync<UploadInfectedException>(() => RunTeeAsync(Policy(server, mode), FakeClamd.Eicar));
        Assert.Equal(FakeClamd.EicarSignature, e.Signature);
        Assert.Equal(FakeClamd.Eicar.Length, e.SizeBytes);
    }

    [Fact]
    public async Task Required_mode_refuses_before_reading_a_byte_when_the_scanner_is_down()
    {
        int port;
        await using (var s = new FakeClamd()) port = s.Port;
        var options = new ScanningOptions { Mode = ScanMode.Required, Port = port, ConnectTimeoutSeconds = 1 };
        var scanning = new UploadScanning(new ClamdScanner(options), options, TimeProvider.System);
        var e = await Assert.ThrowsAsync<UploadNotScannedException>(() => scanning.BeginAsync());
        Assert.False(e.TooLargeToScan);
    }

    [Fact]
    public async Task Best_effort_accepts_unscanned_when_the_scanner_is_down_or_drops_mid_stream()
    {
        int port;
        await using (var s = new FakeClamd()) port = s.Port;
        var down = new ScanningOptions { Mode = ScanMode.BestEffort, Port = port, ConnectTimeoutSeconds = 1 };
        var record = await RunTeeAsync(new UploadScanning(new ClamdScanner(down), down, TimeProvider.System), new byte[100]);
        Assert.Equal(ScanStatus.Unscanned, record.Status);
        Assert.NotNull(record.Reason);

        await using var flaky = new FakeClamd { DropAfterBytes = 5000 };
        var dropped = await RunTeeAsync(Policy(flaky, ScanMode.BestEffort), new byte[100_000]);
        Assert.Equal(ScanStatus.Unscanned, dropped.Status);
    }

    [Fact]
    public async Task Required_mode_refuses_when_the_scanner_drops_mid_stream()
    {
        await using var server = new FakeClamd { DropAfterBytes = 5000 };
        await Assert.ThrowsAsync<UploadNotScannedException>(() => RunTeeAsync(Policy(server, ScanMode.Required), new byte[3_000_000]));
    }

    [Fact]
    public async Task Oversize_upload_is_refused_when_required_and_skipped_when_best_effort()
    {
        await using var server = new FakeClamd();
        var big = new byte[10_000];
        var e = await Assert.ThrowsAsync<UploadNotScannedException>(() => RunTeeAsync(Policy(server, ScanMode.Required, maxScan: 5000), big));
        Assert.True(e.TooLargeToScan);
        var record = await RunTeeAsync(Policy(server, ScanMode.BestEffort, maxScan: 5000), big);
        Assert.Equal(ScanStatus.Skipped, record.Status);
    }

    [Fact]
    public async Task Disabled_mode_records_skipped_without_touching_a_scanner()
    {
        var options = new ScanningOptions { Mode = ScanMode.Disabled };
        var scanning = new UploadScanning(new NoScanner(), options, TimeProvider.System);
        var record = await RunTeeAsync(scanning, FakeClamd.Eicar);
        Assert.Equal(ScanStatus.Skipped, record.Status);
    }

    [Fact]
    public async Task No_scanner_never_claims_clean()
    {
        var required = new ScanningOptions { Mode = ScanMode.Required };
        await Assert.ThrowsAsync<UploadNotScannedException>(() => RunTeeAsync(new UploadScanning(new NoScanner(), required, TimeProvider.System), new byte[10]));
        var bestEffort = new ScanningOptions { Mode = ScanMode.BestEffort };
        Assert.Equal(ScanStatus.Unscanned, (await RunTeeAsync(new UploadScanning(new NoScanner(), bestEffort, TimeProvider.System), new byte[10])).Status);
    }

    [Fact]
    public void Disabled_is_refused_under_RequireFips_without_the_acknowledgement()
    {
        var options = new ScanningOptions { Mode = ScanMode.Disabled };
        Assert.Single(options.Validate(requireFips: true));
        Assert.Empty(options.Validate(requireFips: false));
        options.AcknowledgeUnscannedUploads = true;
        Assert.Empty(options.Validate(requireFips: true));
    }

    [Fact]
    public void A_scanner_on_another_host_needs_the_plaintext_acknowledgement()
    {
        var options = new ScanningOptions { Mode = ScanMode.Required, Host = "clamav.internal", Port = 3310 };
        Assert.Contains(options.Validate(false), e => e.Contains("AllowRemotePlaintext", StringComparison.Ordinal));
        options.AllowRemotePlaintext = true;
        Assert.Empty(options.Validate(false));
        Assert.Empty(new ScanningOptions { Mode = ScanMode.Required, Host = "localhost" }.Validate(true));
        Assert.Empty(new ScanningOptions { Mode = ScanMode.Required, UnixSocket = "/run/clamav/clamd.sock", Host = "elsewhere" }.Validate(true));
    }

    [Fact]
    public void Verdict_parser_maps_every_clamd_reply_shape()
    {
        var info = new ScannerInfo("clamd", null, null);
        Assert.Equal(ScanVerdict.Clean, ClamdScanner.ParseVerdict("stream: OK", info).Verdict);
        var found = ClamdScanner.ParseVerdict("stream: Win.Trojan.Agent-123 FOUND", info);
        Assert.Equal("Win.Trojan.Agent-123", found.Signature);
        Assert.Equal(ScanVerdict.Error, ClamdScanner.ParseVerdict("INSTREAM size limit exceeded. ERROR", info).Verdict);
        Assert.Throws<ScannerUnavailableException>(() => ClamdScanner.ParseVerdict(null, info));
    }
}
