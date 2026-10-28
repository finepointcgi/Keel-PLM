using Keel.Core;

namespace Keel.Documents;

/// <summary>
/// Gives change management (Keel.Change) the documents linked to a part iteration through <see cref="ILinkedDocumentProvider"/>, over
/// <see cref="DocumentService.GetPartLinksAsync"/>: the caller's read access decides which documents are shown, the rest are
/// redacted placeholders (ADR-0016, A4).
/// </summary>
public sealed class LinkedDocumentProvider(DocumentService documents) : ILinkedDocumentProvider
{
    public string ObjectType => "part";

    public async Task<Outcome<IReadOnlyList<LinkedDocument>>> GetLinkedAsync(Subject subject, string key, string? revision, int? iteration, string? correlationId, CancellationToken ct)
    {
        var result = await documents.GetPartLinksAsync(subject, key, revision, iteration, correlationId, ct);
        if (!result.IsOk) return new Outcome<IReadOnlyList<LinkedDocument>>(result.Kind, default, result.Errors);
        IReadOnlyList<LinkedDocument> links =
        [
            .. result.Value!.Links.Select(l => l.Redacted || l.Document is null
                ? new LinkedDocument(true, l.LinkType, null, null, null, null, null, null, null)
                : new LinkedDocument(false, l.LinkType, l.Document.Number, l.Document.Name, l.Document.DocumentType,
                    l.PinnedRevision ?? l.Document.Revision, l.Document.State, l.Document.Banner, l.Document.Markings)),
        ];
        return Outcome<IReadOnlyList<LinkedDocument>>.Ok(links);
    }
}
