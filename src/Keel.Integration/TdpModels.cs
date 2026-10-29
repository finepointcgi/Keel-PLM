using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Npgsql;

namespace Keel.Integration;

/// <summary>
/// The element categories a Technical Data Package is organized by. ASSUMPTION (INT-12): the list follows the technical data package elements
/// of MIL-STD-31000 as far as Keel holds the data; the standard's text is not available offline and the mapping is editable by an administrator.
/// </summary>
public static class TdpCategories
{
    public static readonly IReadOnlyList<string> All =
        ["DRAWINGS", "MODELS", "ASSOCIATED_LISTS", "SPECIFICATIONS", "STANDARDS", "PERFORMANCE_REQUIREMENTS", "QUALITY_ASSURANCE", "PACKAGING", "OTHER"];

    public const string Other = "OTHER";

    public static string Label(string category) => category switch
    {
        "DRAWINGS" => "Drawings",
        "MODELS" => "Models",
        "ASSOCIATED_LISTS" => "Associated lists",
        "SPECIFICATIONS" => "Specifications",
        "STANDARDS" => "Standards",
        "PERFORMANCE_REQUIREMENTS" => "Performance requirements",
        "QUALITY_ASSURANCE" => "Quality assurance provisions",
        "PACKAGING" => "Packaging data",
        _ => "Other technical data",
    };

    public static async Task<IReadOnlyList<TdpCategoryRule>> LoadAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT document_type, link_type, category FROM integration.tdp_category ORDER BY document_type, link_type");
        var rules = new List<TdpCategoryRule>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rules.Add(new TdpCategoryRule(r.GetString(0), r.GetString(1), r.GetString(2)));
        return rules;
    }

    /// <summary>Exact (type, link) rule first, then (type, any link); null when unmapped (the document then goes into "Other technical data" and is reported).</summary>
    public static string? Resolve(IReadOnlyList<TdpCategoryRule> rules, string documentType, string linkType) =>
        rules.FirstOrDefault(r => r.DocumentType == documentType && r.LinkType == linkType)?.Category
        ?? rules.FirstOrDefault(r => r.DocumentType == documentType && r.LinkType == "*")?.Category;
}

public sealed record TdpCategoryRule(string DocumentType, string LinkType, string Category);

public static class TdpObjectTypes
{
    public const string Part = "part";
    public const string Baseline = "baseline";
}

public sealed record TdpRequest(string ObjectType, string Number, string? Revision, string RecipientProfile, Keel.Core.Variants.VariantSelection? Variant = null);

/// <summary>An item of the package. <see cref="Path"/> is its place in the archive; document files are marked downloads.</summary>
public sealed record TdpElement(
    string Kind, string Category, string Path, string? DocumentNumber, string? DocumentRevision, string? DocumentName, Guid? FileId, string? FileName,
    long? SourceSizeBytes, string? SourceSha256, string? State, bool Mapped);

/// <summary>A reason the export cannot happen, with how many objects it concerns. Never names an object the exporter may not read.</summary>
public sealed record TdpRefusal(string Reason, int Count, string Message);

public sealed record TdpPlan(
    TdpRequest Request, string RecipientProfile, string RecipientCategory, string RootNumber, string RootRevision, string Banner, Markings? OverallMarkings,
    IReadOnlyList<TdpElement> Elements, IReadOnlyList<TdpRefusal> Refusals, int PartCount, int DocumentCount, int StructureLines, int UnmappedDocuments,
    IReadOnlyList<string> SourceVersions)
{
    public bool Permitted => Refusals.Count == 0;
}

public sealed record TdpExportRecord(
    Guid Id, DateTimeOffset CreatedAt, string ExporterName, string ObjectType, string ObjectNumber, string? ObjectRevision, string RecipientProfile, string RecipientCategory,
    string ManifestSha256, string SignatureKeyId, string Signature, JsonObject Counts, string Banner, Markings Markings, string? ManifestText);

/// <summary>A package ready to stream. The record is written when the content has been streamed and the manifest is complete, before the manifest itself is sent.</summary>
public sealed record TdpDownload(Guid ExportId, string FileName, string ContentType, string Banner, Func<Stream, CancellationToken, Task<TdpExportRecord>> WriteToAsync);

public sealed record TdpCheck(string Name, bool Ok, string Detail);

public sealed record TdpVerification(bool Valid, Guid? ExportId, IReadOnlyList<TdpCheck> Checks);

/// <summary>Texts printed on the cover sheet and carried in the manifest.</summary>
public static class TdpTexts
{
    public const string ExportWarning =
        "WARNING - This package contains technical data whose export is restricted by the Arms Export Control Act (Title 22, U.S.C., Sec. 2751 et seq.) " +
        "or the Export Administration Regulations (Title 15, C.F.R., Parts 730-774). Violations of these export laws are subject to severe criminal penalties. " +
        "Disseminate only in accordance with the provisions of DoD Directive 5230.25.";

    public const string DestructionNotice =
        "DESTRUCTION NOTICE - Destroy by any method that will prevent disclosure of the contents or reconstruction of the package. " +
        "Do not return it to the originator unless directed.";

    public static string Statement(Markings m) => m.DistributionStatement switch
    {
        DistributionStatement.A => "DISTRIBUTION STATEMENT A. Approved for public release; distribution is unlimited.",
        DistributionStatement.B => $"DISTRIBUTION STATEMENT B. Distribution authorized to U.S. Government agencies only ({m.DistributionReason}). Other requests shall be referred to {m.ControllingOffice}.",
        DistributionStatement.C => $"DISTRIBUTION STATEMENT C. Distribution authorized to U.S. Government agencies and their contractors ({m.DistributionReason}). Other requests shall be referred to {m.ControllingOffice}.",
        DistributionStatement.D => $"DISTRIBUTION STATEMENT D. Distribution authorized to the Department of Defense and U.S. DoD contractors only ({m.DistributionReason}). Other requests shall be referred to {m.ControllingOffice}.",
        DistributionStatement.E => $"DISTRIBUTION STATEMENT E. Distribution authorized to DoD components only ({m.DistributionReason}). Other requests shall be referred to {m.ControllingOffice}.",
        _ => $"DISTRIBUTION STATEMENT F. Further dissemination only as directed by {m.ControllingOffice} or higher DoD authority.",
    };
}

/// <summary>A stream that hashes and counts what is written through it, for the manifest.</summary>
internal sealed class HashingStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    public long Written { get; private set; }

    public string Finish() => Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hash.AppendData(buffer);
        Written += buffer.Length;
        inner.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _hash.AppendData(buffer.Span);
        Written += buffer.Length;
        await inner.WriteAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// ZipArchive writes small records synchronously when an entry closes, which ASP.NET response streams reject. Those are held and sent with the
/// next asynchronous write or flush; bulk data always goes through asynchronously, so memory stays bounded by the record sizes.
/// </summary>
internal sealed class SyncSafeStream(Stream inner) : Stream
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

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        await WriteAsync(buffer.AsMemory(offset, count), cancellationToken);

    private async Task DrainAsync(CancellationToken ct)
    {
        if (_pending.Length == 0) return;
        _pending.Position = 0;
        await _pending.CopyToAsync(inner, ct);
        _pending.SetLength(0);
    }

    public override void Flush() { }
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await DrainAsync(cancellationToken);
        await inner.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>The TDP signing key (ECDSA P-256, FIPS-approved) and verification keys, from configuration.</summary>
public sealed class TdpSigner : IDisposable
{
    private readonly ECDsa? _signing;
    private readonly List<ECDsa> _trusted = [];

    public TdpSigner(IntegrationOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.TdpSigningKeyPath))
        {
            _signing = CheckpointKeys.LoadPem(options.TdpSigningKeyPath);
            _trusted.Add(PublicOnly(_signing));
        }
        foreach (var path in options.TdpTrustedPublicKeyPaths) _trusted.Add(CheckpointKeys.LoadPem(path));
    }

    private static ECDsa PublicOnly(ECDsa key)
    {
        var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo(), out _);
        return pub;
    }

    public bool CanSign => _signing is not null;
    public string KeyId => CheckpointKeys.KeyId(_signing ?? throw NoKey());
    public string PublicKeyPem => _signing is null ? throw NoKey() : _signing.ExportSubjectPublicKeyInfoPem();

    private static InvalidOperationException NoKey() => new("TDP export is not configured: set Keel:Integration:TdpSigningKeyPath to an ECDSA P-256 private key (PEM).");

    /// <summary>Detached signature over the manifest bytes: ECDSA P-256 with SHA-256, IEEE P1363 encoding (r||s), base64.</summary>
    public string Sign(byte[] manifest) =>
        Convert.ToBase64String((_signing ?? throw NoKey()).SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public bool IsTrusted(string keyId) => _trusted.Any(k => CheckpointKeys.KeyId(k) == keyId);

    public bool Verify(byte[] manifest, string signatureBase64, string keyId)
    {
        var key = _trusted.FirstOrDefault(k => CheckpointKeys.KeyId(k) == keyId);
        if (key is null) return false;
        try
        {
            return key.VerifyData(manifest, Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _signing?.Dispose();
        foreach (var k in _trusted) k.Dispose();
    }
}
