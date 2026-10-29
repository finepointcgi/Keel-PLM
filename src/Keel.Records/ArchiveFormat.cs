using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Keel.Audit;

namespace Keel.Records;

public sealed record ArchiveEntryInfo(string Path, string Sha256, long Size);

public sealed record ManifestObject(string Kind, string Number, int Revisions);

/// <summary>The manifest of an archive package (format keel-archive/1): what the package holds, with the SHA-256 and size of every other entry. It is signed (manifest.sig.json).</summary>
public sealed record ArchiveManifest(
    string Format, string Archive, string Title, string CreatedAt, string CreatedBy, string KeyId, string Banner, IReadOnlyList<ManifestObject> Objects, IReadOnlyList<ArchiveEntryInfo> Entries);

public sealed record ArchiveSignatureFile(string Format, string ManifestSha256, string KeyId, string Algorithm, string Signature);

public sealed record AuditExtractEvent(long Seq, string Id, string OccurredAt, string Actor, string Action, string? ObjectType, string? ObjectId, string Outcome, string? CorrelationId, string Payload, string PrevHash, string Hash);

public sealed record AuditExtractCheckpoint(long Seq, string Hash, string CreatedAt, string KeyId, string Signature);

public sealed record AuditExtract(string Format, IReadOnlyList<AuditExtractEvent> Events, AuditExtractCheckpoint? Checkpoint);

public static class ArchiveFormat
{
    public const string Format = "keel-archive/1";
    public const string ManifestPath = "manifest.json";
    public const string SignaturePath = "manifest.sig.json";
    public const string AuditPath = "audit-extract.json";
    public const string SignaturesPath = "signatures.json";

    public static string ObjectPath(string kind, string number) => $"objects/{kind}/{number}.json";
    public static string FilePath(string sha256) => $"files/{sha256}";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}

/// <summary>Builds the ZIP of an archive package in a temporary file (it can be large), recording SHA-256 and size of every entry as it is written.</summary>
internal sealed class ArchiveWriter : IDisposable
{
    private readonly FileStream _file = new(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
    private readonly ZipArchive _zip;
    private readonly DateTimeOffset _stamp;
    private bool _finished;

    public ArchiveWriter(DateTimeOffset stamp)
    {
        _zip = new ZipArchive(_file, ZipArchiveMode.Create, leaveOpen: true);
        _stamp = stamp < new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) : stamp; // the ZIP format cannot represent earlier times
    }

    public List<ArchiveEntryInfo> Entries { get; } = [];

    public ArchiveEntryInfo AddBytes(string path, byte[] data, bool record = true)
    {
        var entry = _zip.CreateEntry(path, CompressionLevel.Fastest);
        entry.LastWriteTime = _stamp;
        using (var s = entry.Open()) s.Write(data);
        var info = new ArchiveEntryInfo(path, RecordsSigner.Sha256Hex(data), data.Length);
        if (record) Entries.Add(info);
        return info;
    }

    public async Task<ArchiveEntryInfo> AddStreamAsync(string path, Stream content, CancellationToken ct)
    {
        var entry = _zip.CreateEntry(path, CompressionLevel.Fastest);
        entry.LastWriteTime = _stamp;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        var buffer = new byte[81920];
        await using (var s = entry.Open())
        {
            int n;
            while ((n = await content.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, n);
                await s.WriteAsync(buffer.AsMemory(0, n), ct);
                size += n;
            }
        }
        var info = new ArchiveEntryInfo(path, Convert.ToHexStringLower(hash.GetHashAndReset()), size);
        Entries.Add(info);
        return info;
    }

    public long Length => _file.Length;

    /// <summary>Closes the archive and returns the file positioned at its start. The caller disposes the stream (the file is deleted then).</summary>
    public FileStream Finish()
    {
        if (_finished) throw new InvalidOperationException("Already finished.");
        _finished = true;
        _zip.Dispose();
        _file.Position = 0;
        return _file;
    }

    public void Dispose()
    {
        if (!_finished) { _zip.Dispose(); _file.Dispose(); }
    }
}

/// <summary>What reading a package found: the verification verdict and the parsed content (empty when the package is not a valid archive).</summary>
public sealed record ArchiveRead(
    ArchiveVerification Verification, string? ManifestSha256, string? Number, string? KeyId, IReadOnlyList<ArchivedObject> Objects, IReadOnlyList<ArchivedSignature> Signatures, IReadOnlyList<ArchivedEvent> Events);

/// <summary>
/// Verifies and reads an archive package without a database or a vault (used by the application, the <c>keel-vault verify-archive</c> command and tests): the manifest
/// signature under a trusted key, the SHA-256 and size of every entry against the manifest, no entry the manifest does not list, and the hash of every audit event
/// recomputed from its stored payload (ADR-0005). A package that fails any check is reported as not valid; nothing is ever repaired.
/// </summary>
public static class ArchiveReader
{
    public static ArchiveRead Read(Stream zipStream, IReadOnlyCollection<ECDsa> trustedKeys)
    {
        var problems = new List<string>();
        ArchiveRead Fail() => new(new ArchiveVerification(false, problems, 0, 0, false, false, null), null, null, null, [], [], []);
        ZipArchive zip;
        try { zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { problems.Add("The file is not a ZIP package."); return Fail(); }
        using (zip)
        {
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var e in zip.Entries)
            {
                if (e.FullName.Contains("..", StringComparison.Ordinal) || e.FullName.StartsWith('/') || e.FullName.EndsWith('/') || e.FullName.Contains('\\', StringComparison.Ordinal)) { problems.Add($"Entry '{e.FullName}' has an unsafe name."); continue; }
                if (!entries.TryAdd(e.FullName, e)) problems.Add($"Entry '{e.FullName}' appears twice.");
            }
            if (!entries.TryGetValue(ArchiveFormat.ManifestPath, out var manifestEntry) || !entries.TryGetValue(ArchiveFormat.SignaturePath, out var sigEntry))
            {
                problems.Add("The package has no signed manifest.");
                return Fail();
            }
            var manifestBytes = ReadAll(manifestEntry);
            var manifestSha = RecordsSigner.Sha256Hex(manifestBytes);
            ArchiveManifest? manifest; ArchiveSignatureFile? sig;
            try
            {
                manifest = JsonSerializer.Deserialize<ArchiveManifest>(manifestBytes, ArchiveFormat.Json);
                sig = JsonSerializer.Deserialize<ArchiveSignatureFile>(ReadAll(sigEntry), ArchiveFormat.Json);
            }
            catch (JsonException) { problems.Add("The manifest or its signature is not readable."); return Fail(); }
            if (manifest is null || sig is null || manifest.Format != ArchiveFormat.Format) { problems.Add("The manifest has an unknown format."); return Fail(); }

            var signed = false;
            if (sig.ManifestSha256 != manifestSha) problems.Add("The manifest does not match the hash that was signed.");
            else
            {
                byte[] signature;
                try { signature = Convert.FromBase64String(sig.Signature); } catch (FormatException) { signature = []; }
                if (!RecordsSigner.Verify(trustedKeys, RecordsSigner.ArchiveDomain, manifestSha, sig.KeyId, signature, out var known))
                    problems.Add(known ? "The manifest signature is not valid." : $"No trusted public key has the signing key id {sig.KeyId}.");
                else signed = true;
            }

            var listed = manifest.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
            foreach (var e in manifest.Entries)
            {
                if (!entries.TryGetValue(e.Path, out var ze)) { problems.Add($"Entry '{e.Path}' is listed but missing."); continue; }
                var (hash, size) = HashOf(ze);
                if (hash != e.Sha256 || size != e.Size) problems.Add($"Entry '{e.Path}' does not match the manifest.");
                if (e.Path.StartsWith("files/", StringComparison.Ordinal) && e.Path != ArchiveFormat.FilePath(e.Sha256)) problems.Add($"File entry '{e.Path}' is not named by its content hash.");
            }
            foreach (var name in entries.Keys.Where(n => n is not (ArchiveFormat.ManifestPath or ArchiveFormat.SignaturePath) && !listed.ContainsKey(n)))
                problems.Add($"Entry '{name}' is in the package but not in the manifest.");

            var objects = new List<ArchivedObject>();
            foreach (var o in manifest.Objects)
            {
                if (!entries.TryGetValue(ArchiveFormat.ObjectPath(o.Kind, o.Number), out var oe)) { problems.Add($"Object {o.Number} has no description entry."); continue; }
                try { if (JsonSerializer.Deserialize<ArchivedObject>(ReadAll(oe), ArchiveFormat.Json) is { } obj) objects.Add(obj); }
                catch (JsonException) { problems.Add($"The description of {o.Number} is not readable."); }
            }

            var signatures = new List<ArchivedSignature>();
            if (entries.TryGetValue(ArchiveFormat.SignaturesPath, out var se))
                try { signatures.AddRange(JsonSerializer.Deserialize<List<ArchivedSignature>>(ReadAll(se), ArchiveFormat.Json) ?? []); }
                catch (JsonException) { problems.Add("The signatures entry is not readable."); }

            var events = new List<ArchivedEvent>();
            var checkpointVerified = false;
            string? note = null;
            if (entries.TryGetValue(ArchiveFormat.AuditPath, out var ae))
            {
                try
                {
                    var extract = JsonSerializer.Deserialize<AuditExtract>(ReadAll(ae), ArchiveFormat.Json);
                    foreach (var ev in extract?.Events ?? [])
                    {
                        byte[] prev, hash;
                        try { prev = Convert.FromHexString(ev.PrevHash); hash = Convert.FromHexString(ev.Hash); }
                        catch (FormatException) { problems.Add($"Audit event {ev.Seq} has unreadable hashes."); continue; }
                        if (prev.Length != 32 || !CryptographicOperations.FixedTimeEquals(AuditChain.ComputeHash(prev, ev.Seq, ev.Payload), hash)) problems.Add($"Audit event {ev.Seq} does not match its hash.");
                        events.Add(new ArchivedEvent(ev.Seq, DateTimeOffset.Parse(ev.OccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal), ev.Action, ev.Outcome));
                    }
                    if (extract?.Checkpoint is { } cp)
                    {
                        try
                        {
                            var at = DateTimeOffset.Parse(cp.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                            var message = CheckpointKeys.Message(cp.Seq, Convert.FromHexString(cp.Hash), at);
                            foreach (var key in trustedKeys.Where(k => CheckpointKeys.KeyId(k) == cp.KeyId))
                                if (key.VerifyData(message, Convert.FromBase64String(cp.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) checkpointVerified = true;
                        }
                        catch (Exception e) when (e is FormatException or CryptographicException) { checkpointVerified = false; }
                        if (!checkpointVerified) problems.Add("The audit checkpoint in the package does not verify against a trusted key.");
                        else if (extract.Events.FirstOrDefault(x => x.Seq == cp.Seq) is { } same && !string.Equals(same.Hash, cp.Hash, StringComparison.Ordinal)) { checkpointVerified = false; problems.Add("The audit checkpoint disagrees with the event it covers."); }
                    }
                    else note = "No audit checkpoint covered these events when the archive was made; each event's own hash was verified, and the chain around them can be checked against the live audit log.";
                }
                catch (JsonException) { problems.Add("The audit extract is not readable."); }
            }
            return new ArchiveRead(new ArchiveVerification(problems.Count == 0, problems, manifest.Entries.Count, events.Count, signed, checkpointVerified, note), manifestSha, manifest.Archive, sig.KeyId, objects, signatures, events);
        }
    }

    private static byte[] ReadAll(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static (string Hash, long Size) HashOf(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0) { h.AppendData(buffer, 0, n); size += n; }
        return (Convert.ToHexStringLower(h.GetHashAndReset()), size);
    }

    /// <summary>Reads one file out of a package after the caller has verified it (CLI extract); the entry name is the content hash and is checked again.</summary>
    public static bool TryExtractFile(Stream zipStream, string sha256, Stream destination)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry(ArchiveFormat.FilePath(sha256));
        if (entry is null) return false;
        using var s = entry.Open();
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int n;
        using var temp = new MemoryStream();
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0) { h.AppendData(buffer, 0, n); temp.Write(buffer, 0, n); }
        if (!string.Equals(Convert.ToHexStringLower(h.GetHashAndReset()), sha256, StringComparison.Ordinal)) return false;
        temp.Position = 0;
        temp.CopyTo(destination);
        return true;
    }

    internal static string Text(byte[] utf8) => Encoding.UTF8.GetString(utf8);
}
