using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Keel.TestSupport;

/// <summary>
/// A protocol-faithful clamd for tests: a real TCP (or UNIX socket) listener that speaks the clamd wire protocol. It understands the NUL-terminated
/// ("z") and newline ("n") command forms of PING, VERSION and INSTREAM; INSTREAM reads length-prefixed chunks (4 bytes big-endian) until the zero
/// terminator exactly like clamd, enforces a <see cref="StreamMaxLength"/> and answers <c>stream: OK</c>, <c>stream: &lt;signature&gt; FOUND</c> or
/// <c>INSTREAM size limit exceeded. ERROR</c>. It recognizes the standard EICAR antivirus test string, wherever it appears in the stream. Faults can be
/// injected: delay before the verdict, no verdict at all, connection drop after N bytes, refusing connections.
/// </summary>
public sealed class FakeClamd : IAsyncDisposable
{
    public const string EicarSignature = "Win.Test.EICAR_HDB-1";
    // The standard EICAR test string, assembled at runtime so that this source file is not itself flagged by endpoint protection.
    public static readonly byte[] Eicar = Encoding.ASCII.GetBytes("X5O!P%@AP[4\\PZX54(P^)7CC)7}$" + "EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

    private readonly Socket _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly List<Task> _handlers = [];
    private readonly string? _socketPath;
    private long _bytesReceived;
    private int _maxChunkSeen;
    private int _instreams;
    private int _connections;

    public FakeClamd(string? unixSocketPath = null)
    {
        _socketPath = unixSocketPath;
        if (unixSocketPath is null)
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_listener.LocalEndPoint!).Port;
        }
        else
        {
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(unixSocketPath));
        }
        _listener.Listen(64);
        _acceptLoop = Task.Run(AcceptAsync);
    }

    public int Port { get; }
    public string Version { get; set; } = "ClamAV 1.4.1/27500/Mon Mar 10 08:23:39 2025";
    /// <summary>clamd's StreamMaxLength: more bytes than this in one INSTREAM are answered with the size-limit error and the connection is closed.</summary>
    public long StreamMaxLength { get; set; } = 25L * 1024 * 1024;
    /// <summary>Pause between the terminator and the verdict (a busy scanner).</summary>
    public TimeSpan VerdictDelay { get; set; }
    /// <summary>Never answer after the terminator (a hung scanner): the client must time out.</summary>
    public bool NeverAnswer { get; set; }
    /// <summary>Close the connection once this many stream bytes were received (a crash or restart mid-upload). 0 = never.</summary>
    public long DropAfterBytes { get; set; }
    /// <summary>Close every new connection immediately, as a scanner that is shutting down does.</summary>
    public bool DropConnections { get; set; }
    /// <summary>Answer INSTREAM with an ERROR reply instead of a verdict.</summary>
    public string? ErrorReply { get; set; }

    public long BytesReceived => Interlocked.Read(ref _bytesReceived);
    public int LargestChunk => Volatile.Read(ref _maxChunkSeen);
    public int InstreamCount => Volatile.Read(ref _instreams);
    public int ConnectionCount => Volatile.Read(ref _connections);

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try { client = await _listener.AcceptAsync(_stop.Token); }
            catch (Exception) { return; }
            Interlocked.Increment(ref _connections);
            var task = Task.Run(() => HandleAsync(client));
            lock (_handlers) _handlers.Add(task);
        }
    }

    private async Task HandleAsync(Socket socket)
    {
        using var s = socket;
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        try
        {
            if (DropConnections) return;
            var command = await ReadCommandAsync(stream);
            switch (command)
            {
                case "PING": await ReplyAsync(stream, "PONG"); break;
                case "VERSION": await ReplyAsync(stream, Version); break;
                case "INSTREAM": await InstreamAsync(stream); break;
                default: await ReplyAsync(stream, "UNKNOWN COMMAND"); break;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // A client that goes away is normal for a scanner.
        }
    }

    private static async Task<string> ReadCommandAsync(Stream stream)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (await stream.ReadAsync(one) == 1)
        {
            if (one[0] is 0 or (byte)'\n') break;
            sb.Append((char)one[0]);
        }
        var text = sb.ToString();
        // "zPING" / "nPING" are the NUL- and newline-terminated forms of the same command.
        return text.Length > 1 && text[0] is 'z' or 'n' ? text[1..] : text;
    }

    private async Task InstreamAsync(Stream stream)
    {
        Interlocked.Increment(ref _instreams);
        long total = 0;
        var window = new List<byte>(Eicar.Length * 2);
        var found = false;
        var prefix = new byte[4];
        var buffer = new byte[64 * 1024];
        while (true)
        {
            if (!await ReadExactAsync(stream, prefix)) return;
            var size = BinaryPrimitives.ReadUInt32BigEndian(prefix);
            if (size == 0) break;
            if (total + size > StreamMaxLength)
            {
                await ReplyAsync(stream, "INSTREAM size limit exceeded. ERROR");
                return; // clamd closes the connection
            }
            var chunkMax = (int)Math.Min(size, int.MaxValue);
            if (chunkMax > Volatile.Read(ref _maxChunkSeen)) Volatile.Write(ref _maxChunkSeen, chunkMax);
            var remaining = (long)size;
            while (remaining > 0)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
                if (n == 0) return;
                remaining -= n;
                total += n;
                Interlocked.Add(ref _bytesReceived, n);
                if (!found) found = Scan(window, buffer.AsSpan(0, n));
                if (DropAfterBytes > 0 && total >= DropAfterBytes) return;
            }
        }
        if (NeverAnswer) { await Task.Delay(Timeout.Infinite, _stop.Token); return; }
        if (VerdictDelay > TimeSpan.Zero) await Task.Delay(VerdictDelay, _stop.Token);
        await ReplyAsync(stream, ErrorReply ?? (found ? $"stream: {EicarSignature} FOUND" : "stream: OK"));
    }

    /// <summary>Rolling search for EICAR across chunk boundaries, keeping only the last Eicar.Length - 1 bytes.</summary>
    private static bool Scan(List<byte> window, ReadOnlySpan<byte> data)
    {
        window.AddRange(data.ToArray());
        if (System.Runtime.InteropServices.CollectionsMarshal.AsSpan(window).IndexOf(Eicar) >= 0) return true;
        var keep = Eicar.Length - 1;
        if (window.Count > keep) window.RemoveRange(0, window.Count - keep);
        return false;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read));
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private static async Task ReplyAsync(Stream stream, string text)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text + "\0"));
        await stream.FlushAsync();
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _stop.CancelAsync();
        _listener.Dispose();
        try { await _acceptLoop; } catch (Exception) { /* listener closed */ }
        Task[] handlers;
        lock (_handlers) handlers = [.. _handlers];
        try { await Task.WhenAll(handlers).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { /* clients gone */ }
        _stop.Dispose();
        if (_socketPath is not null && File.Exists(_socketPath)) File.Delete(_socketPath);
    }
}
