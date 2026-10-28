using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Keel.Core;

namespace Keel.Documents.Scanning;

/// <summary>
/// clamd client over the INSTREAM protocol (NULL-terminated commands, "z" prefix): the content is sent in chunks, each prefixed with its
/// length as 4 bytes big-endian, ended by a zero length; clamd answers <c>stream: OK</c>, <c>stream: &lt;signature&gt; FOUND</c> or
/// <c>... ERROR</c>. The connection is a plain socket: TCP to a localhost sidecar or a UNIX socket. The link is not encrypted (ADR-0026).
/// </summary>
public sealed class ClamdScanner : IUploadScanner
{
    public const string ScannerName = "clamd";
    private static readonly TimeSpan InfoTtl = TimeSpan.FromMinutes(5);
    private static readonly byte[] InStream = "zINSTREAM\0"u8.ToArray();
    private static readonly byte[] VersionCommand = "zVERSION\0"u8.ToArray();
    private static readonly byte[] PingCommand = "zPING\0"u8.ToArray();
    private const int MaxReplyBytes = 8 * 1024;

    private readonly ScanningOptions _options;
    private readonly TimeProvider _clock;
    private ScannerInfo? _info;
    private DateTimeOffset _infoAt;

    public ClamdScanner(ScanningOptions options, TimeProvider? clock = null)
    {
        _options = options;
        _clock = clock ?? TimeProvider.System;
    }

    public string Name => ScannerName;

    public async Task<IUploadScan> BeginAsync(CancellationToken ct = default)
    {
        var stream = await ConnectAsync(ct);
        try
        {
            await WriteBoundedAsync(stream, InStream, ct);
            return new Session(this, stream);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    public async Task<ScannerInfo> ProbeAsync(CancellationToken ct = default)
    {
        if (!await PingAsync(ct)) throw new ScannerUnavailableException("the scanner did not answer PING with PONG");
        return await GetInfoAsync(ct);
    }

    /// <summary>True when clamd answers PING with PONG.</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        await using var stream = await ConnectAsync(ct);
        await WriteBoundedAsync(stream, PingCommand, ct);
        return (await ReadReplyAsync(stream, _options.IoTimeout, ct)) == "PONG";
    }

    public async Task<ScannerInfo> GetInfoAsync(CancellationToken ct = default)
    {
        if (_info is { } cached && _clock.GetUtcNow() - _infoAt < InfoTtl) return cached;
        // Concurrent first calls may each ask clamd once; the answer is tiny and identical.
        await using var stream = await ConnectAsync(ct);
        await WriteBoundedAsync(stream, VersionCommand, ct);
        var reply = await ReadReplyAsync(stream, _options.IoTimeout, ct)
            ?? throw new ScannerUnavailableException("the scanner closed the connection without a version");
        var info = ParseVersion(reply);
        _info = info;
        _infoAt = _clock.GetUtcNow();
        return info;
    }

    /// <summary>"ClamAV 1.4.1/27500/Mon Mar 10 08:23:39 2025": engine version, signature database version and its build date.</summary>
    internal static ScannerInfo ParseVersion(string reply)
    {
        var parts = reply.Split('/', 3);
        var engine = parts[0].Trim();
        var version = engine.StartsWith("ClamAV ", StringComparison.OrdinalIgnoreCase) ? engine[7..] : engine;
        DateTimeOffset? dbDate = null;
        if (parts.Length == 3 && DateTimeOffset.TryParseExact(parts[2].Trim(), ["ddd MMM d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy"], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            dbDate = d;
        var db = parts.Length >= 2 ? $"{version} (signatures {parts[1].Trim()})" : version;
        return new ScannerInfo(ScannerName, db, dbDate);
    }

    private async Task<NetworkStream> ConnectAsync(CancellationToken ct)
    {
        Socket socket;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.ConnectTimeout);
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.UnixSocket))
            {
                socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(_options.UnixSocket), timeout.Token); }
                catch { socket.Dispose(); throw; }
            }
            else
            {
                socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await socket.ConnectAsync(_options.Host, _options.Port, timeout.Token); }
                catch { socket.Dispose(); throw; }
            }
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new ScannerUnavailableException($"no connection within {_options.ConnectTimeoutSeconds} s", e);
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            throw new ScannerUnavailableException("cannot connect: " + e.Message, e);
        }
        return new NetworkStream(socket, ownsSocket: true);
    }

    private async Task WriteBoundedAsync(Stream stream, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.IoTimeout);
        try
        {
            await stream.WriteAsync(data, timeout.Token);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new ScannerUnavailableException($"no write progress within {_options.IoTimeoutSeconds} s", e);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            throw new ScannerUnavailableException("connection lost: " + e.Message, e);
        }
    }

    /// <summary>Reads one reply (until NUL, newline or end of stream) within <paramref name="limit"/>. Null when the peer closed without sending anything.</summary>
    private static async Task<string?> ReadReplyAsync(Stream stream, TimeSpan limit, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        var reply = new List<byte>(64);
        var one = new byte[256];
        try
        {
            while (reply.Count < MaxReplyBytes)
            {
                var n = await stream.ReadAsync(one, timeout.Token);
                if (n == 0) break;
                var end = Array.FindIndex(one, 0, n, b => b is 0 or (byte)'\n');
                if (end >= 0) { reply.AddRange(one.AsSpan(0, end).ToArray()); break; }
                reply.AddRange(one.AsSpan(0, n).ToArray());
            }
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new ScannerUnavailableException($"no answer within {limit.TotalSeconds:0} s", e);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            if (reply.Count == 0) throw new ScannerUnavailableException("connection lost: " + e.Message, e);
        }
        return reply.Count == 0 ? null : Encoding.UTF8.GetString(reply.ToArray()).Trim();
    }

    /// <summary>Maps clamd's reply to a verdict; the text of a reply never leaves this assembly except as the signature name or the reason.</summary>
    internal static ScanResult ParseVerdict(string? reply, ScannerInfo info)
    {
        if (reply is null) throw new ScannerUnavailableException("the scanner closed the connection without a verdict");
        if (reply.EndsWith(" OK", StringComparison.Ordinal) || reply == "OK") return ScanResult.Clean(info);
        if (reply.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            var body = reply[..^" FOUND".Length];
            var colon = body.IndexOf(':', StringComparison.Ordinal);
            var signature = (colon >= 0 ? body[(colon + 1)..] : body).Trim();
            return ScanResult.Infected(signature.Length == 0 ? "unknown" : signature, info);
        }
        return ScanResult.Error(reply.Length > 200 ? reply[..200] : reply, info);
    }

    private sealed class Session(ClamdScanner owner, NetworkStream stream) : IUploadScan
    {
        private readonly byte[] _prefix = new byte[4];

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default)
        {
            var chunkBytes = owner._options.ChunkBytes;
            while (!chunk.IsEmpty)
            {
                var piece = chunk[..Math.Min(chunk.Length, chunkBytes)];
                chunk = chunk[piece.Length..];
                BinaryPrimitives.WriteUInt32BigEndian(_prefix, (uint)piece.Length);
                try
                {
                    await owner.WriteBoundedAsync(stream, _prefix, ct);
                    await owner.WriteBoundedAsync(stream, piece, ct);
                }
                catch (ScannerUnavailableException e)
                {
                    // clamd may have answered early (for example its StreamMaxLength is smaller than ours): say so instead of "connection lost".
                    var early = await TryReadEarlyReplyAsync();
                    if (early is not null) throw new ScannerUnavailableException($"the scanner stopped the stream: {early}", e);
                    throw;
                }
            }
        }

        public async Task<ScanResult> FinishAsync(CancellationToken ct = default)
        {
            BinaryPrimitives.WriteUInt32BigEndian(_prefix, 0);
            await owner.WriteBoundedAsync(stream, _prefix, ct);
            var reply = await ReadReplyAsync(stream, owner._options.ResultTimeout, ct);
            ScannerInfo info;
            try { info = await owner.GetInfoAsync(ct); }
            catch (ScannerUnavailableException) { info = new ScannerInfo(ScannerName, null, null); }
            return ParseVerdict(reply, info);
        }

        private async Task<string?> TryReadEarlyReplyAsync()
        {
            try { return await ReadReplyAsync(stream, TimeSpan.FromMilliseconds(500), CancellationToken.None); }
            catch (ScannerUnavailableException) { return null; }
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}

/// <summary>
/// The explicit "no scanner". It never claims a stream is clean: every scan ends in <see cref="ScanVerdict.Error"/> ("no scanner is configured"),
/// so Required mode refuses uploads and BestEffort mode records them as <c>unscanned</c>. Used for wiring a scanner-shaped dependency
/// where none exists (tests, tools); <see cref="ScanMode.Disabled"/> skips scanning before it is ever consulted.
/// </summary>
public sealed class NoScanner : IUploadScanner
{
    public const string ScannerName = "none";
    private static readonly ScannerInfo Info = new(ScannerName, null, null);

    public string Name => ScannerName;
    public Task<IUploadScan> BeginAsync(CancellationToken ct = default) => Task.FromResult<IUploadScan>(new NullScan());
    public Task<ScannerInfo> GetInfoAsync(CancellationToken ct = default) => Task.FromResult(Info);
    public Task<ScannerInfo> ProbeAsync(CancellationToken ct = default) => throw new ScannerUnavailableException("no malware scanner is configured");

    private sealed class NullScan : IUploadScan
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default) => ValueTask.CompletedTask;
        public Task<ScanResult> FinishAsync(CancellationToken ct = default) => Task.FromResult(ScanResult.Error("no malware scanner is configured", Info));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
