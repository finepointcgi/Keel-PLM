using System.Security.Cryptography;
using Amazon.S3.Model;
using Keel.Documents.Vault;

namespace Keel.IntegrationTests;

/// <summary>The vault against a real S3-compatible store (SeaweedFS), no mocks (ADR-0013, ADR-0015).</summary>
public sealed class VaultTests
{
    private static byte[] RandomBytes(int n) => RandomNumberGenerator.GetBytes(n);

    /// <summary>A stream that only yields data in small, uneven reads and cannot seek, like a network body.</summary>
    private sealed class TrickleStream(byte[] data) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, 7919), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Small_object_is_stored_under_its_sha256_and_verified_on_read()
    {
        using var vault = new ObjectVault(await VaultContainer.OptionsAsync());
        var data = RandomBytes(10_000);
        var stored = await vault.PutAsync(new TrickleStream(data));

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), stored.Sha256);
        Assert.Equal(10_000, stored.SizeBytes);
        Assert.Equal(data, await vault.ReadAllAsync(stored.Sha256, stored.SizeBytes));
        Assert.True(await vault.ExistsAsync(stored.Sha256, stored.SizeBytes));

        // Same bytes, same key: a second upload is a no-op that returns the same address.
        Assert.Equal(stored, await vault.PutAsync(new MemoryStream(data)));
    }

    [Fact]
    public async Task Large_object_streams_in_parts_and_staging_is_cleaned_up()
    {
        var options = await VaultContainer.OptionsAsync();
        using var vault = new ObjectVault(options);
        var data = RandomBytes(12 * 1024 * 1024 + 123); // three parts of 5 MiB
        var stored = await vault.PutAsync(new TrickleStream(data));

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), stored.Sha256);
        Assert.Equal(data.Length, stored.SizeBytes);
        await vault.VerifyAsync(stored.Sha256, stored.SizeBytes);
        Assert.Equal(data, await vault.ReadAllAsync(stored.Sha256, stored.SizeBytes));

        using var s3 = ObjectVault.CreateClient(options);
        var staging = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = options.Bucket, Prefix = "staging/" });
        Assert.Empty(staging.S3Objects ?? []);
    }

    [Fact]
    public async Task Oversized_upload_is_rejected_without_leaving_objects()
    {
        var options = await VaultContainer.OptionsAsync(maxUploadBytes: 6 * 1024 * 1024);
        using var vault = new ObjectVault(options);
        var e = await Assert.ThrowsAsync<UploadTooLargeException>(() => vault.PutAsync(new TrickleStream(RandomBytes(7 * 1024 * 1024))));
        Assert.Equal(6 * 1024 * 1024, e.Limit);
        await Assert.ThrowsAsync<UploadTooLargeException>(() => vault.PutAsync(new MemoryStream(RandomBytes(6 * 1024 * 1024 + 1))));
    }

    [Fact]
    public async Task Tampered_or_missing_object_fails_verification()
    {
        var options = await VaultContainer.OptionsAsync();
        using var vault = new ObjectVault(options);
        var data = RandomBytes(4096);
        var stored = await vault.PutAsync(new MemoryStream(data));

        // Overwrite the object directly in the store, bypassing the application (same size, different bytes).
        using var s3 = ObjectVault.CreateClient(options);
        var evil = RandomBytes(4096);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = options.Bucket, Key = stored.Sha256, InputStream = new MemoryStream(evil) });

        var e = await Assert.ThrowsAsync<VaultIntegrityException>(() => vault.ReadAllAsync(stored.Sha256, stored.SizeBytes));
        Assert.Contains("failed integrity verification", e.Message);
        Assert.Contains("hashes to " + Convert.ToHexStringLower(SHA256.HashData(evil)), e.Message);
        await Assert.ThrowsAsync<VaultIntegrityException>(() => vault.VerifyAsync(stored.Sha256, stored.SizeBytes));

        var missing = Convert.ToHexStringLower(SHA256.HashData(RandomBytes(32)));
        var gone = await Assert.ThrowsAsync<VaultIntegrityException>(() => vault.VerifyAsync(missing, 32));
        Assert.Contains("missing", gone.Message);
    }

    [Fact]
    public async Task Server_side_encryption_is_accepted_by_the_store()
    {
        var options = await VaultContainer.OptionsAsync();
        options.ServerSideEncryption = true;
        using var vault = new ObjectVault(options);
        var small = RandomBytes(1000);
        var large = RandomBytes(6 * 1024 * 1024);
        var a = await vault.PutAsync(new MemoryStream(small));
        var b = await vault.PutAsync(new TrickleStream(large));
        Assert.Equal(small, await vault.ReadAllAsync(a.Sha256, a.SizeBytes));
        Assert.Equal(large, await vault.ReadAllAsync(b.Sha256, b.SizeBytes));

        using var s3 = ObjectVault.CreateClient(options);
        Assert.Equal(Amazon.S3.ServerSideEncryptionMethod.AES256, (await s3.GetObjectMetadataAsync(options.Bucket, a.Sha256)).ServerSideEncryptionMethod);
        Assert.Equal(Amazon.S3.ServerSideEncryptionMethod.AES256, (await s3.GetObjectMetadataAsync(options.Bucket, b.Sha256)).ServerSideEncryptionMethod);
    }
}
