using System.IO.Compression;
using System.Text;

namespace Keel.Documents.Marking;

/// <summary>
/// Marked delivery for formats that cannot be stamped (A10): a ZIP holding MARKINGS.pdf (cover sheet), MARKINGS.txt and
/// the original file, unchanged. The markings entries come first so they are what a user sees on opening the archive.
/// </summary>
public static class MarkedZip
{
    public const string CoverSheetName = "MARKINGS.pdf";
    public const string ManifestName = "MARKINGS.txt";

    public static string ArchiveName(string fileName) => fileName + ".zip";

    /// <summary>Writes the archive to <paramref name="destination"/>, streaming the original from <paramref name="original"/>.</summary>
    public static async Task WriteAsync(Stream destination, MarkingContext ctx, Stream original, CancellationToken ct)
    {
        var cover = PdfMarker.CoverSheet(ctx);
        var manifest = Encoding.UTF8.GetBytes(PdfMarker.Manifest(ctx));
        var output = new SyncWriteBuffer(destination);
        await using (var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8, ct))
        {
        // Entries are stored, not deflated: engineering formats are usually compressed already, and storing keeps the
        // original bytes identical to the vault object (its SHA-256 is printed on the cover sheet).
            await AddAsync(zip, CoverSheetName, new MemoryStream(cover), ctx.RetrievedAt, ct);
            await AddAsync(zip, ManifestName, new MemoryStream(manifest), ctx.RetrievedAt, ct);
            await AddAsync(zip, ctx.FileName, original, ctx.RetrievedAt, ct);
        }
        await output.FlushAsync(ct);
    }

    private static async Task AddAsync(ZipArchive zip, string name, Stream content, DateTimeOffset at, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = at;
        await using var target = await entry.OpenAsync(ct);
        await content.CopyToAsync(target, ct);
    }

    /// <summary>
    /// ZipArchive still writes small records (entry headers, data descriptors) synchronously when an entry closes, and
    /// ASP.NET response streams reject synchronous I/O. Those writes are held here and sent with the next asynchronous
    /// write or flush; bulk data always goes through asynchronously, so memory stays bounded by the record sizes.
    /// </summary>
    private sealed class SyncWriteBuffer(Stream inner) : Stream
    {
        private readonly MemoryStream _pending = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => _pending.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _pending.Write(buffer);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await DrainAsync(cancellationToken);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await DrainAsync(cancellationToken);
            await inner.FlushAsync(cancellationToken);
        }

        // Flush() is called synchronously by ZipArchive; the data is sent by the next asynchronous operation.
        public override void Flush() { }

        private async Task DrainAsync(CancellationToken ct)
        {
            if (_pending.Length == 0) return;
            await inner.WriteAsync(_pending.GetBuffer().AsMemory(0, (int)_pending.Length), ct);
            _pending.SetLength(0);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _pending.Dispose();
            base.Dispose(disposing);
        }
    }
}
