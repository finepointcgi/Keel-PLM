using System.Diagnostics.Metrics;
using Keel.Core;

namespace Keel.Documents.Scanning;

/// <summary>Scan status recorded per stored content (<c>plm.file_scan.status</c>).</summary>
public static class ScanStatus
{
    public const string Clean = "clean";
    public const string Infected = "infected";
    public const string Unscanned = "unscanned";
    public const string Skipped = "skipped";
}

/// <summary>What the scan of one upload found; written next to the file row so auditors can see coverage.</summary>
public sealed record ScanRecord(string Status, string? ScannerName, string? ScannerVersion, DateTimeOffset? SignatureDatabaseDate,
    DateTimeOffset ScannedAt, string? Reason, string? Signature = null);

/// <summary>The scanner found a signature. Nothing was stored under the content address; the bytes were never referenced.</summary>
public sealed class UploadInfectedException(string signature, string sha256, long sizeBytes, ScanRecord scan)
    : Exception($"The upload was refused: the malware scanner detected {signature}.")
{
    public string Signature { get; } = signature;
    /// <summary>SHA-256 of the bytes received (the whole upload: the scan verdict arrives after the last byte).</summary>
    public string Sha256 { get; } = sha256;
    public long SizeBytes { get; } = sizeBytes;
    public ScanRecord Scan { get; } = scan;
}

/// <summary>
/// The upload could not be scanned and scanning is required: the scanner is down or failed, or the file is larger than it can take.
/// The API maps it to 503 with Retry-After (413 when <see cref="TooLargeToScan"/>); nothing is stored.
/// </summary>
public sealed class UploadNotScannedException(string reason, bool tooLargeToScan = false, Exception? inner = null)
    : Exception("The upload was refused because it could not be scanned for malware: " + reason, inner)
{
    public string Reason { get; } = reason;
    public bool TooLargeToScan { get; } = tooLargeToScan;
}

/// <summary>Owns the scanning policy (<see cref="ScanMode"/>) and hands out one <see cref="ScanTee"/> per upload.</summary>
public sealed class UploadScanning(IUploadScanner scanner, ScanningOptions options, TimeProvider clock)
{
    internal static readonly Meter Meter = new("Keel.Documents");
    internal static readonly Counter<long> Scans = Meter.CreateCounter<long>("keel_upload_scans", description: "Uploads by scan result (clean, infected, unscanned, skipped, refused).");

    public ScanningOptions Options => options;
    public IUploadScanner Scanner => scanner;
    public TimeProvider Clock => clock;

    public static void Count(string result) => Scans.Add(1, new KeyValuePair<string, object?>("result", result));

    /// <summary>
    /// Starts scanning one upload. Required mode and an unreachable scanner: throws <see cref="UploadNotScannedException"/> before a byte is read.
    /// BestEffort: the tee is created in its failed state and the upload will be recorded as unscanned.
    /// </summary>
    public async Task<ScanTee> BeginAsync(CancellationToken ct = default)
    {
        if (options.Mode == ScanMode.Disabled) return new ScanTee(this, null, "scanning is disabled", disabled: true);
        try
        {
            return new ScanTee(this, await scanner.BeginAsync(ct), null, disabled: false);
        }
        catch (ScannerUnavailableException e)
        {
            if (options.Mode == ScanMode.Required)
            {
                Count("refused");
                throw new UploadNotScannedException(e.Reason, inner: e);
            }
            return new ScanTee(this, null, e.Reason, disabled: false);
        }
    }

    /// <summary>
    /// Scans stored content read back from the vault (rescan). Unlike an upload this never refuses: the caller gets the verdict, or
    /// <see cref="ScanVerdict.Error"/> with the reason when the scanner cannot be used.
    /// </summary>
    public async Task<ScanResult> ScanStreamAsync(Stream content, CancellationToken ct = default)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Min(options.ChunkBytes, 1024 * 1024));
        long total = 0;
        try
        {
            await using var scan = await scanner.BeginAsync(ct);
            int n;
            while ((n = await content.ReadAsync(buffer, ct)) > 0)
            {
                total += n;
                if (total > options.MaxScanBytes) return ScanResult.Error($"larger than the scan limit of {options.MaxScanBytes} bytes");
                await scan.WriteAsync(buffer.AsMemory(0, n), ct);
            }
            return await scan.FinishAsync(ct);
        }
        catch (ScannerUnavailableException e)
        {
            return ScanResult.Error(e.Reason);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// One upload's scan session. The vault feeds it every buffer it reads (the same buffers it hashes: no second pass, no buffering of the
/// file) and asks for the verdict once the whole content is known, before the object is published under its content address.
/// </summary>
public sealed class ScanTee : IAsyncDisposable
{
    private readonly UploadScanning _owner;
    private IUploadScan? _scan;
    private readonly bool _disabled;
    private string? _failure;
    private bool _oversize;
    private long _fed;

    internal ScanTee(UploadScanning owner, IUploadScan? scan, string? failure, bool disabled)
    {
        _owner = owner;
        _scan = scan;
        _failure = failure;
        _disabled = disabled;
    }

    public long BytesFed => _fed;

    /// <summary>Passes the next buffer to the scanner. Required mode: a scanner failure or an oversize stream throws <see cref="UploadNotScannedException"/> at once.</summary>
    public async ValueTask FeedAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        _fed += data.Length;
        if (_disabled || _scan is null) return;
        var options = _owner.Options;
        if (_fed > options.MaxScanBytes)
        {
            await DropAsync();
            _oversize = true;
            _failure = $"the file is larger than the scan limit of {options.MaxScanBytes} bytes";
            if (options.Mode == ScanMode.Required)
            {
                UploadScanning.Count("refused");
                throw new UploadNotScannedException(_failure, tooLargeToScan: true);
            }
            return;
        }
        try
        {
            await _scan.WriteAsync(data, ct);
        }
        catch (ScannerUnavailableException e)
        {
            await DropAsync();
            _failure = e.Reason;
            if (options.Mode == ScanMode.Required)
            {
                UploadScanning.Count("refused");
                throw new UploadNotScannedException(e.Reason, inner: e);
            }
        }
    }

    /// <summary>
    /// Asks for the verdict. Infected: throws <see cref="UploadInfectedException"/>. Clean: the record to store. Scanner failure: Required throws
    /// <see cref="UploadNotScannedException"/>, BestEffort returns an <c>unscanned</c> record.
    /// </summary>
    public async Task<ScanRecord> CompleteAsync(string sha256, long sizeBytes, CancellationToken ct = default)
    {
        var now = _owner.Clock.GetUtcNow();
        var options = _owner.Options;
        if (_disabled)
        {
            UploadScanning.Count("skipped");
            return new ScanRecord(ScanStatus.Skipped, NoScanner.ScannerName, null, null, now, _failure);
        }

        ScanResult? result = null;
        if (_scan is not null)
        {
            try
            {
                result = await _scan.FinishAsync(ct);
            }
            catch (ScannerUnavailableException e)
            {
                _failure = e.Reason;
            }
            await DropAsync();
        }

        if (result is { Verdict: ScanVerdict.Clean })
        {
            UploadScanning.Count("clean");
            return new ScanRecord(ScanStatus.Clean, result.Scanner?.Name, result.Scanner?.Version, result.Scanner?.SignatureDatabaseDate, now, null);
        }
        if (result is { Verdict: ScanVerdict.Infected })
        {
            UploadScanning.Count("infected");
            var record = new ScanRecord(ScanStatus.Infected, result.Scanner?.Name, result.Scanner?.Version, result.Scanner?.SignatureDatabaseDate, now, null, result.Signature);
            throw new UploadInfectedException(result.Signature!, sha256, sizeBytes, record);
        }

        // No verdict: the scanner failed, answered with an error, or the stream was too large to send.
        var reason = result?.Reason ?? _failure ?? "the scanner gave no verdict";
        if (options.Mode == ScanMode.Required)
        {
            UploadScanning.Count("refused");
            throw new UploadNotScannedException(reason, tooLargeToScan: _oversize);
        }
        var status = _oversize ? ScanStatus.Skipped : ScanStatus.Unscanned;
        UploadScanning.Count(status);
        return new ScanRecord(status, result?.Scanner?.Name ?? _owner.Scanner.Name, result?.Scanner?.Version, result?.Scanner?.SignatureDatabaseDate, now, reason);
    }

    private async ValueTask DropAsync()
    {
        var scan = _scan;
        _scan = null;
        if (scan is not null) await scan.DisposeAsync();
    }

    public ValueTask DisposeAsync() => DropAsync();
}
