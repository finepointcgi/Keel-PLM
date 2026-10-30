using System.Text.Json.Nodes;
using Keel.Core;
using Keel.Documents;
using Keel.Parts;
using Keel.Security;
using Keel.Web.Components.Shared;

namespace Keel.Web.Services;

/// <summary>What a person can do about editing a part right now, in the words the page shows (ADR 0061).</summary>
public enum PartEditState
{
    /// <summary>Edit starts a draft (the service checks the part out in the same step).</summary>
    CanEdit,
    /// <summary>The person already has the part checked out; Edit simply continues with their draft.</summary>
    Resume,
    /// <summary>Somebody else is editing; the page names them.</summary>
    CheckedOutByOther,
    /// <summary>The revision is released (locked): it changes only through a change order.</summary>
    NeedsChange,
    /// <summary>The revision is under review (locked until the review ends).</summary>
    InReview,
    /// <summary>An older revision: only the latest revision is edited.</summary>
    NotLatest,
    /// <summary>The access policy does not let the person modify the part; the reasons are on the part.</summary>
    ReadOnly,
}

/// <summary>The decision and the one sentence that explains it.</summary>
public sealed record PartEditAdvice(PartEditState State, string Message)
{
    public bool CanStart => State is PartEditState.CanEdit or PartEditState.Resume;
}

/// <summary>Result of putting a document on a part in one step.</summary>
/// <param name="Outcome">The outcome of the link itself (Ok when the part now has, or will get, the link).</param>
/// <param name="Message">What happened, or exactly why it did not, in plain words.</param>
/// <param name="CheckedIn">True when the part got a new version with the link; false when the link waits in the person's own draft.</param>
public sealed record AddDocumentResult(bool IsOk, string Message, bool CheckedIn, string? PartVersion);

/// <summary>
/// The one-step flows of the simplified screens (ADR 0061): Edit then Save, Discard, and "Add document" on a part. Each step is the same module call the
/// advanced controls make (check-out, save working copy, check-in, undo check-out, add link), so the audit trail, the release locks, the ABAC checks and
/// the document-link rules are exactly the same; this class only chains them and turns refusals into plain sentences. It holds no rule of its own: when a
/// step is refused, the module's answer is what the person reads, and a chain that fails halfway gives back what it took (it never leaves a part locked
/// to a person who did not ask to keep it).
/// </summary>
public sealed class SimpleFlows(PartService parts, DocumentService docs)
{
    /// <summary>Decides, from what the part page already loaded, which sentence and which action apply. No data is read here.</summary>
    public static PartEditAdvice Advise(PartDetail part)
    {
        var co = part.Checkout;
        if (!part.Revision.IsLatest) return new(PartEditState.NotLatest, $"You are looking at revision {part.Revision.Revision}, which is not the latest. Open the latest revision to edit.");
        if (co?.IsMine == true) return new(PartEditState.Resume, "You are editing this part. Your changes stay private until you save.");
        if (part.Revision.IsLocked && part.Revision.State == "UnderReview")
            return new(PartEditState.InReview, $"This part is under review (revision {part.Revision.Revision}), so it cannot be edited until the review is finished.");
        if (part.Revision.IsLocked)
            return new(PartEditState.NeedsChange, $"This part is {part.Revision.StateLabel.ToLowerInvariant()} (revision {part.Revision.Revision}), so it cannot be edited directly. Changes to a {part.Revision.StateLabel.ToLowerInvariant()} part go through a change order.");
        if (co is not null) return new(PartEditState.CheckedOutByOther, $"{co.UserName ?? co.UserId} is editing this part (since {UiText.Utc(co.CheckedOutAt)}). You can edit it after they save or discard their changes.");
        if (!part.Permissions.CanModify)
            return new(PartEditState.ReadOnly, "You can view this part but not change it: " + string.Join(" ", part.Permissions.ModifyDeniedReasons.Select(UiText.Reason)));
        return new(PartEditState.CanEdit, "");
    }

    /// <summary>Edit: continues the person's own draft, or starts one (check-out). Refusals carry the module's reason.</summary>
    public async Task<Outcome<PartDetail>> BeginEditAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        var current = await parts.GetAsync(subject, number, null, correlationId, ct);
        if (!current.IsOk) return current;
        var advice = Advise(current.Value!);
        if (advice.State == PartEditState.Resume) return current;
        if (advice.State is PartEditState.NeedsChange or PartEditState.InReview or PartEditState.NotLatest or PartEditState.CheckedOutByOther)
            return Outcome<PartDetail>.Conflict(advice.Message);
        // CanEdit and ReadOnly both go to the module: it is the module that decides and audits (a refusal is an audited denial).
        return await parts.CheckOutAsync(subject, number, correlationId, ct);
    }

    /// <summary>Save: stores the draft and checks the part in as a new version, with an optional note. Both steps are the module's own.</summary>
    public async Task<Outcome<PartDetail>> SaveAsync(Subject subject, string number, string? description, JsonObject? attributes, string? note, string? correlationId, CancellationToken ct = default)
    {
        var saved = await parts.UpdateWorkingCopyAsync(subject, number, description, attributes, correlationId, ct);
        if (!saved.IsOk) return saved; // nothing was checked in; the person's draft is as it was
        return await parts.CheckInAsync(subject, number, note, correlationId, ct);
    }

    /// <summary>Discard changes: the module's undo check-out (owner or administrator only; edits not saved are lost).</summary>
    public Task<Outcome<PartDetail>> DiscardAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        parts.UndoCheckOutAsync(subject, number, correlationId, ct);

    /// <summary>
    /// Why a document cannot be added to this part right now, or null when it can. The same rules the module applies, read from the part the caller
    /// already has, so a person is told before a document is created for a part that would refuse the link.
    /// </summary>
    public static string? LinkBlocker(PartDetail part)
    {
        var advice = Advise(part);
        return advice.State switch
        {
            PartEditState.CanEdit or PartEditState.Resume => null,
            PartEditState.NeedsChange => $"{part.Number} is {part.Revision.StateLabel.ToLowerInvariant()}, so documents are added to it through a change order.",
            _ => advice.Message,
        };
    }

    /// <summary>
    /// Puts an existing document on a part. If the person is editing the part the link joins their draft (it is applied when they save); otherwise the part
    /// is checked out, linked and checked in again as one action, with a note naming the document. Anything that fails after the check-out is rolled back by
    /// an undo of that check-out.
    /// </summary>
    public async Task<AddDocumentResult> AddDocumentAsync(Subject subject, string partNumber, string documentNumber, string linkType, string? documentRevision, string? correlationId, CancellationToken ct = default)
    {
        documentNumber = documentNumber.Trim().ToUpperInvariant();
        var current = await parts.GetAsync(subject, partNumber, null, correlationId, ct);
        if (!current.IsOk) return new(false, $"Part {partNumber} was not found, or you are not allowed to see it.", false, null);
        var part = current.Value!;
        if (LinkBlocker(part) is { } blocked) return new(false, blocked, false, null);
        var mineAlready = part.Checkout?.IsMine == true;
        if (!mineAlready)
        {
            var co = await parts.CheckOutAsync(subject, partNumber, correlationId, ct);
            if (!co.IsOk) return new(false, UiText.Message(co), false, null);
        }
        var link = await docs.AddLinkToWorkingCopyAsync(subject, partNumber, documentNumber, linkType, linkType == LinkTypes.Describes ? documentRevision : null, correlationId, ct);
        if (!link.IsOk)
        {
            if (!mineAlready) await parts.UndoCheckOutAsync(subject, partNumber, correlationId, ct); // give back what this action took
            return new(false, UiText.Message(link), false, null);
        }
        if (mineAlready) return new(true, $"{documentNumber} is added to your draft of {partNumber}. It is applied when you save.", false, null);
        var done = await parts.CheckInAsync(subject, partNumber, $"Added document {documentNumber}", correlationId, ct);
        if (!done.IsOk)
        {
            await parts.UndoCheckOutAsync(subject, partNumber, correlationId, ct);
            return new(false, UiText.Message(done), false, null);
        }
        return new(true, $"{documentNumber} is now linked to {partNumber}.", true, done.Value!.Iteration.Label);
    }

    /// <summary>Creates a document and links it to a part: the link is checked first so no document is created for a part that would refuse it.</summary>
    public async Task<(Outcome<DocumentDetail> Document, AddDocumentResult? Link)> CreateDocumentAndLinkAsync(
        Subject subject, CreateDocumentCommand command, FileUpload? primary, string? partNumber, string linkType, string? correlationId, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(partNumber))
        {
            var current = await parts.GetAsync(subject, partNumber.Trim().ToUpperInvariant(), null, correlationId, ct);
            if (!current.IsOk) return (Outcome<DocumentDetail>.Invalid([$"Part {partNumber.Trim()} was not found, or you are not allowed to see it."]), null);
            if (LinkBlocker(current.Value!) is { } blocked) return (Outcome<DocumentDetail>.Invalid([blocked + " The document was not created."]), null);
            partNumber = current.Value!.Number;
        }
        var created = await docs.CreateAsync(subject, command, primary, correlationId, ct);
        if (!created.IsOk || string.IsNullOrWhiteSpace(partNumber)) return (created, null);
        var link = await AddDocumentAsync(subject, partNumber, created.Value!.Number, linkType, null, correlationId, ct);
        return (created, link);
    }

    // ---------------------------------------------------------------- pickers

    /// <summary>Parts the person may read (the module's own read filter), each marked when it cannot take a document: no right to modify, or released.</summary>
    public async Task<IReadOnlyList<PickItem>> FindPartsAsync(Subject subject, string query, CancellationToken ct = default)
    {
        var found = await parts.SearchAsync(subject, query, null, 8, 0, CurrentUser.NewCorrelationId(), ct);
        return [.. found.Items.Select(p =>
        {
            string? note = null;
            if (!AccessPolicy.Evaluate(subject, p.Markings, PlmAction.Modify).Permitted) note = "You can see this part but not change it.";
            else if (p.State == "UnderReview") note = "Under review: wait until the review is finished.";
            else if (p.State is "Released" or "Obsolete") note = $"{StateText.Label(p.State)}: documents are added through a change order.";
            return new PickItem(p.Number, p.Name, p.Banner, note, note is not null);
        })];
    }

    /// <summary>Documents the person may read (the module's own read filter).</summary>
    public async Task<IReadOnlyList<PickItem>> FindDocumentsAsync(Subject subject, string query, CancellationToken ct = default)
    {
        var found = await docs.SearchAsync(subject, query, null, null, 8, 0, CurrentUser.NewCorrelationId(), ct);
        return [.. found.Items.Select(d => new PickItem(d.Number, d.Name, d.Banner, $"{d.DocumentType}, revision {d.Revision}"))];
    }
}
