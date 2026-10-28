using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Keel.Documents.Vault;

/// <summary>Content stored in the vault: its object key is <see cref="Sha256"/> (lowercase hex).</summary>
public sealed record StoredContent(string Sha256, long SizeBytes);

/// <summary>The upload exceeded <see cref="VaultOptions.MaxUploadBytes"/>; nothing was kept in the vault.</summary>
public sealed class UploadTooLargeException(long limit) : Exception($"The file exceeds the maximum upload size of {limit} bytes.")
{
    public long Limit { get; } = limit;
}

/// <summary>
/// The store did not answer in time (<see cref="VaultOptions.RequestTimeoutSeconds"/>) or could not be reached. The API
/// maps it to 503 with a Retry-After header; nothing is lost, the request can be repeated.
/// </summary>
public sealed class VaultUnavailableException(string detail, Exception? inner = null)
    : Exception("The document vault is unavailable: " + detail, inner);

/// <summary>A stored object does not match the SHA-256 or size recorded for it: corruption or tampering (ADR-0015).</summary>
public sealed class VaultIntegrityException(string sha256, string detail)
    : Exception($"Vault object {sha256} failed integrity verification: {detail}. The file was not delivered.")
{
    public string Sha256 { get; } = sha256;
}

/// <summary>
/// Immutable, content-addressed object storage over the S3 API (ADR-0015). Uploads are hashed with SHA-256 while they
/// stream, holding at most one part in memory; downloads are verified against the recorded hash and size.
/// </summary>
public sealed class ObjectVault : IDisposable
{
    private const string StagingPrefix = "staging/";
    private readonly AmazonS3Client _s3;
    private readonly VaultOptions _options;

    public ObjectVault(VaultOptions options)
    {
        var errors = options.Validate();
        if (errors.Count > 0) throw new InvalidOperationException("Invalid vault configuration: " + string.Join(" ", errors));
        _options = options;
        _s3 = CreateClient(options);
    }

    public VaultOptions Options => _options;

    /// <summary>
    /// S3 client for an on-prem store: path-style addressing, explicit credentials and region, so the SDK never
    /// reaches for AWS endpoints, instance metadata or credential discovery.
    /// </summary>
    public static AmazonS3Client CreateClient(VaultOptions o) => new(
        new BasicAWSCredentials(o.AccessKey, o.SecretKey),
        new AmazonS3Config
        {
            ServiceURL = o.ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = o.Region,
            // ADR-0008: the SDK's default flexible checksums are CRC-based (and older SDKs used MD5). Turn them off:
            // integrity in transit comes from SigV4 (SHA-256 payload hashes, HMAC-SHA-256 signatures) and TLS, and
            // integrity at rest from our own SHA-256 content addressing, verified on every download.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            MaxErrorRetry = 2,
            // The SDK's own Timeout would also cut long streaming downloads, so every call is bounded by CallAsync instead
            // (retries included) and each read of a download by VerifyingStream.
        });

    public static string KeyFor(string sha256) => sha256;

    /// <summary>Readiness probe: the bucket is reachable with the configured credentials.</summary>
    public async Task PingAsync(CancellationToken ct = default) =>
        await CallAsync(t => _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _options.Bucket, MaxKeys = 1 }, t), ct);

    /// <summary>
    /// Runs one store request under <see cref="VaultOptions.RequestTimeout"/> (retries included). A timeout or a
    /// connection failure becomes <see cref="VaultUnavailableException"/>; cancellation by the caller stays a cancellation.
    /// </summary>
    private async Task<T> CallAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            return await operation(timeout.Token);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new VaultUnavailableException($"no answer within {_options.RequestTimeoutSeconds} s", e);
        }
        catch (Exception e) when (e is HttpRequestException or AmazonClientException
            || (e is AmazonServiceException s && (int)s.StatusCode >= 500))
        {
            throw new VaultUnavailableException("the store could not be reached or failed", e);
        }
    }

    /// <summary>Streams <paramref name="content"/> into the vault and returns its SHA-256 and size.</summary>
    public async Task<StoredContent> PutAsync(Stream content, CancellationToken ct = default)
    {
        var partSize = _options.PartSizeBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(partSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var filled = await FillAsync(content, buffer, partSize, ct);
            long total = filled;
            if (total > _options.MaxUploadBytes) throw new UploadTooLargeException(_options.MaxUploadBytes);
            hash.AppendData(buffer, 0, filled);

            if (filled < partSize)
            {
                // Small file: the hash is known before anything is sent, so it goes straight to its final key.
                var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (!await ExistsAsync(sha, total, ct))
                {
                    using var body = new MemoryStream(buffer, 0, filled, writable: false);
                    await CallAsync(t =>
                    {
                        body.Position = 0; // a retry after a timeout resends the whole (small) body
                        return _s3.PutObjectAsync(Encrypt(new PutObjectRequest
                        {
                            BucketName = _options.Bucket, Key = KeyFor(sha), InputStream = body, AutoCloseStream = false,
                            ContentType = "application/octet-stream",
                        }), t);
                    }, ct);
                }
                return new StoredContent(sha, total);
            }
            return await PutLargeAsync(content, buffer, filled, total, hash, ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Large file: the key (its hash) is unknown until the last byte, so it is uploaded in parts to a staging key,
    /// then copied server side to its content address and the staging object removed.
    /// </summary>
    private async Task<StoredContent> PutLargeAsync(Stream content, byte[] buffer, int filled, long total, IncrementalHash hash, CancellationToken ct)
    {
        var staging = StagingPrefix + Guid.CreateVersion7().ToString("N");
        var init = await CallAsync(t => _s3.InitiateMultipartUploadAsync(Encrypt(new InitiateMultipartUploadRequest
        {
            BucketName = _options.Bucket, Key = staging, ContentType = "application/octet-stream",
        }), t), ct);
        var completed = false;
        try
        {
            var parts = new List<PartETag>();
            for (var partNumber = 1; filled > 0; partNumber++)
            {
                using (var body = new MemoryStream(buffer, 0, filled, writable: false))
                {
                    var size = filled;
                    var part = await CallAsync(t =>
                    {
                        body.Position = 0;
                        return _s3.UploadPartAsync(new UploadPartRequest
                        {
                            BucketName = _options.Bucket, Key = staging, UploadId = init.UploadId, PartNumber = partNumber,
                            InputStream = body, PartSize = size,
                        }, t);
                    }, ct);
                    parts.Add(new PartETag(partNumber, part.ETag));
                }
                filled = await FillAsync(content, buffer, _options.PartSizeBytes, ct);
                total += filled;
                if (total > _options.MaxUploadBytes) throw new UploadTooLargeException(_options.MaxUploadBytes);
                hash.AppendData(buffer, 0, filled);
            }
            await CallAsync(t => _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _options.Bucket, Key = staging, UploadId = init.UploadId, PartETags = parts,
            }, t), ct);
            completed = true;

            var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!await ExistsAsync(sha, total, ct))
            {
                await CallAsync(t => _s3.CopyObjectAsync(Encrypt(new CopyObjectRequest
                {
                    SourceBucket = _options.Bucket, SourceKey = staging, DestinationBucket = _options.Bucket, DestinationKey = KeyFor(sha),
                    ContentType = "application/octet-stream",
                }), t), ct);
            }
            return new StoredContent(sha, total);
        }
        finally
        {
            // Staging objects never outlive the upload, whether it succeeded or not.
            // Bounded like every other call; when the store is down the leftover staging object is all that remains.
            try
            {
                if (completed) await CallAsync(t => _s3.DeleteObjectAsync(_options.Bucket, staging, t), CancellationToken.None);
                else await CallAsync(t => _s3.AbortMultipartUploadAsync(_options.Bucket, staging, init.UploadId, t), CancellationToken.None);
            }
            catch (VaultUnavailableException)
            {
            }
        }
    }

    /// <summary>True when an object with this content address and the expected size exists.</summary>
    public async Task<bool> ExistsAsync(string sha256, long sizeBytes, CancellationToken ct = default)
    {
        try
        {
            var head = await CallAsync(t => _s3.GetObjectMetadataAsync(_options.Bucket, KeyFor(sha256), t), ct);
            // A different size means the stored object is damaged; rewriting it with the bytes its key names repairs it.
            return head.ContentLength == sizeBytes;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens a stored object for reading. The stream verifies size and SHA-256 as it is read and throws
    /// <see cref="VaultIntegrityException"/> at the first sign of a mismatch, at the latest at end of stream.
    /// </summary>
    public async Task<Stream> OpenReadAsync(string sha256, long sizeBytes, CancellationToken ct = default)
    {
        GetObjectResponse response;
        try
        {
            response = await CallAsync(t => _s3.GetObjectAsync(_options.Bucket, KeyFor(sha256), t), ct);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            throw new VaultIntegrityException(sha256, "the object is missing from the vault");
        }
        return new VerifyingStream(response.ResponseStream, response, sha256, sizeBytes, _options.RequestTimeout);
    }

    /// <summary>Reads the whole object once, verifying it, without keeping it in memory.</summary>
    public async Task VerifyAsync(string sha256, long sizeBytes, CancellationToken ct = default)
    {
        await using var s = await OpenReadAsync(sha256, sizeBytes, ct);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (await s.ReadAsync(buffer, ct) > 0) { }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads the whole object into memory, verified. For bounded sizes only (PDF stamping).</summary>
    public async Task<byte[]> ReadAllAsync(string sha256, long sizeBytes, CancellationToken ct = default)
    {
        await using var s = await OpenReadAsync(sha256, sizeBytes, ct);
        var bytes = new byte[sizeBytes];
        var read = 0;
        while (read < bytes.Length)
        {
            var n = await s.ReadAsync(bytes.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }
        // Drain to end of stream so the verifier sees the true length and computes the final hash.
        var probe = new byte[1];
        if (await s.ReadAsync(probe, ct) != 0 || read != bytes.Length)
            throw new VaultIntegrityException(sha256, "the stored size differs from the recorded size");
        return bytes;
    }

    private T Encrypt<T>(T request) where T : AmazonWebServiceRequest
    {
        if (!_options.ServerSideEncryption) return request;
        switch (request)
        {
            case PutObjectRequest p: p.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256; break;
            case InitiateMultipartUploadRequest i: i.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256; break;
            case CopyObjectRequest c: c.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256; break;
        }
        return request;
    }

    private static async Task<int> FillAsync(Stream s, byte[] buffer, int count, CancellationToken ct)
    {
        var filled = 0;
        while (filled < count)
        {
            var n = await s.ReadAsync(buffer.AsMemory(filled, count - filled), ct);
            if (n == 0) break;
            filled += n;
        }
        return filled;
    }

    public void Dispose() => _s3.Dispose();

    private sealed class VerifyingStream(Stream inner, IDisposable owner, string sha256, long expectedSize, TimeSpan readTimeout) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _read;
        private bool _verified;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => expectedSize;
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Account(buffer.AsSpan(offset), inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Each read is bounded: a store that stalls mid-download must not hold the request open forever.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(readTimeout);
            int n;
            try
            {
                n = await inner.ReadAsync(buffer, timeout.Token);
            }
            catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
            {
                throw new VaultUnavailableException("the store stopped sending data", e);
            }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                throw new VaultUnavailableException("the connection to the store failed", e);
            }
            return Account(buffer.Span, n);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Account(Span<byte> data, int n)
        {
            if (n > 0)
            {
                _read += n;
                if (_read > expectedSize) throw new VaultIntegrityException(sha256, "the stored object is larger than recorded");
                _hash.AppendData(data[..n]);
                return n;
            }
            if (!_verified)
            {
                _verified = true;
                if (_read != expectedSize) throw new VaultIntegrityException(sha256, "the stored object is smaller than recorded");
                var actual = Convert.ToHexStringLower(_hash.GetHashAndReset());
                if (!string.Equals(actual, sha256, StringComparison.Ordinal))
                    throw new VaultIntegrityException(sha256, $"its content hashes to {actual}");
            }
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
                inner.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
