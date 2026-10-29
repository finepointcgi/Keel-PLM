using Keel.Core;
using Keel.Documents.Vault;

namespace Keel.Documents;

/// <summary>
/// The document side of CAD integration (ADR-0035): <see cref="ICadDocumentPort"/> over <see cref="DocumentService"/>. Every call is the
/// service's own command, so access, markings, malware scanning, vault hashing, locks and audit are the ones of ordinary documents.
/// </summary>
public sealed class CadDocumentPort(DocumentService documents) : ICadDocumentPort
{
    public async Task<Outcome<CadDocumentInfo>> GetAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        Map(await documents.GetAsync(subject, number, null, correlationId, ct));

    public async Task<Outcome<IReadOnlyList<CadDocumentInfo>>> CheckOutSetAsync(Subject subject, IReadOnlyList<string> numbers, string? correlationId,
        Func<System.Data.Common.DbConnection, System.Data.Common.DbTransaction, CancellationToken, Task>? inTransaction, CancellationToken ct = default)
    {
        var result = await documents.CheckOutSetAsync(subject, numbers, correlationId, inTransaction, ct);
        if (!result.IsOk) return new Outcome<IReadOnlyList<CadDocumentInfo>>(result.Kind, null, result.Errors);
        var infos = new List<CadDocumentInfo>();
        foreach (var number in result.Value!)
        {
            var info = await GetAsync(subject, number, correlationId, ct);
            if (!info.IsOk) return new Outcome<IReadOnlyList<CadDocumentInfo>>(info.Kind, null, info.Errors);
            infos.Add(info.Value!);
        }
        return Outcome<IReadOnlyList<CadDocumentInfo>>.Ok(infos);
    }

    public async Task<Outcome<CadDocumentInfo>> CreateAsync(Subject subject, string name, string documentType, Markings markings, string? description, string? correlationId, CancellationToken ct = default) =>
        Map(await documents.CreateAsync(subject, new CreateDocumentCommand(name, documentType, markings, description), null, correlationId, ct));

    public async Task<Outcome<CadDocumentInfo>> UploadAsync(Subject subject, string number, string role, string fileName, string? mediaType, Stream content, string? correlationId, CancellationToken ct = default) =>
        Map(await documents.UploadFileAsync(subject, number, role, new FileUpload(fileName, mediaType, content), correlationId, ct));

    public Task<Outcome<IReadOnlyList<CadCheckedIn>>> CheckInSetAsync(Subject subject, IReadOnlyList<CadCheckInItem> items, string? correlationId,
        Func<System.Data.Common.DbConnection, System.Data.Common.DbTransaction, IReadOnlyList<CadCheckedIn>, CancellationToken, Task>? inTransaction, CancellationToken ct = default) =>
        documents.CheckInSetAsync(subject, items, correlationId, inTransaction, ct);

    public Task<Outcome<IReadOnlyList<string>>> UndoCheckOutSetAsync(Subject subject, IReadOnlyList<string> numbers, string? correlationId, CancellationToken ct = default) =>
        documents.UndoCheckOutSetAsync(subject, numbers, correlationId, ct);

    public async Task<Outcome<CadDownload>> DownloadLatestAsync(Subject subject, string number, string sha256, string? correlationId, CancellationToken ct = default)
    {
        var result = await documents.DownloadPackageMemberAsync(subject, number, sha256, correlationId, ct);
        if (!result.IsOk) return new Outcome<CadDownload>(result.Kind, null, result.Errors);
        var d = result.Value!;
        return Outcome<CadDownload>.Ok(new CadDownload(d.FileName, d.Banner, d.WriteToAsync));
    }

    private static Outcome<CadDocumentInfo> Map(Outcome<DocumentDetail> o)
    {
        if (!o.IsOk) return new Outcome<CadDocumentInfo>(o.Kind, null, o.Errors);
        var d = o.Value!;
        static CadFileInfo F(DocumentFileView f) => new(f.Role, f.FileName, f.MediaType, f.SizeBytes, f.Sha256);
        return Outcome<CadDocumentInfo>.Ok(new CadDocumentInfo(d.Id, d.Number, d.Name, d.DocumentType, d.Markings, d.Banner, d.Revision.Revision, d.Iteration.Iteration,
            d.Revision.State, d.Revision.IsLocked, d.Checkout?.UserName ?? d.Checkout?.UserId, d.Checkout is { IsMine: true },
            [.. d.Iteration.Files.Select(F)], d.Checkout is { IsMine: true, Files: { } wf } ? [.. wf.Select(F)] : null));
    }
}
