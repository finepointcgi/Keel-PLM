namespace Keel.Core;

/// <summary>Which scanner looked at the bytes: recorded with every scan so auditors can see coverage and signature currency.</summary>
public sealed record ScannerInfo(string Name, string? Version, DateTimeOffset? SignatureDatabaseDate);

public enum ScanVerdict
{
    Clean,
    Infected,
    /// <summary>The scanner answered but could not give a verdict (for example its own size limit): the bytes are NOT known to be clean.</summary>
    Error,
}

/// <summary>Result of scanning one stream: <see cref="Clean"/>, <see cref="Infected"/> (with the signature name) or <see cref="Error"/> (with the reason).</summary>
public sealed record ScanResult(ScanVerdict Verdict, string? Signature, string? Reason, ScannerInfo? Scanner)
{
    public static ScanResult Clean(ScannerInfo scanner) => new(ScanVerdict.Clean, null, null, scanner);
    public static ScanResult Infected(string signature, ScannerInfo scanner) => new(ScanVerdict.Infected, signature, null, scanner);
    public static ScanResult Error(string reason, ScannerInfo? scanner = null) => new(ScanVerdict.Error, null, reason, scanner);
}

/// <summary>The scanner could not be reached or the connection failed while streaming. The bytes are not known to be clean.</summary>
public sealed class ScannerUnavailableException(string reason, Exception? inner = null)
    : Exception("The malware scanner is unavailable: " + reason, inner)
{
    public string Reason { get; } = reason;
}

/// <summary>One streaming scan session: write the content in chunks as it flows past, then ask for the verdict. No second pass over the data.</summary>
public interface IUploadScan : IAsyncDisposable
{
    /// <summary>Feeds the next chunk. Throws <see cref="ScannerUnavailableException"/> when the scanner connection fails.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default);

    /// <summary>Ends the stream and returns the verdict. Throws <see cref="ScannerUnavailableException"/> when no answer arrives.</summary>
    Task<ScanResult> FinishAsync(CancellationToken ct = default);
}

/// <summary>
/// Streaming malware scanner for uploads (NIST SP 800-171 3.14.2). Keel.Documents implements it for clamd (INSTREAM) and as an explicit
/// no-op; the vault tees every upload into it while the bytes are hashed.
/// </summary>
public interface IUploadScanner
{
    string Name { get; }

    /// <summary>Opens a scan session. Throws <see cref="ScannerUnavailableException"/> when the scanner cannot be reached.</summary>
    Task<IUploadScan> BeginAsync(CancellationToken ct = default);

    /// <summary>Checks now (never cached) that the scanner answers; for readiness. Throws <see cref="ScannerUnavailableException"/> when it does not.</summary>
    Task<ScannerInfo> ProbeAsync(CancellationToken ct = default);

    /// <summary>Scanner name, engine version and signature database date (cached briefly). Throws <see cref="ScannerUnavailableException"/> when unreachable.</summary>
    Task<ScannerInfo> GetInfoAsync(CancellationToken ct = default);
}
