using System.Text.Json.Nodes;

namespace Keel.Core;

/// <summary>One stored version of a workflow template, as the admin UI shows it.</summary>
public sealed record TemplateVersionInfo(
    string Id, int Version, string Name, string? Description, string ObjectType, bool IsActive, DateTimeOffset CreatedAt, string CreatedBy, JsonNode Definition,
    /// <summary>Reviews of this version that are still running. They keep this version's definition even after a newer one is published.</summary>
    int RunningReviews = 0);

/// <summary>A saved work-in-progress definition (not a version): it may be invalid and is never used to start a review.</summary>
public sealed record TemplateDraftInfo(string Id, int BaseVersion, string Definition, DateTimeOffset UpdatedAt, string UpdatedBy);

/// <summary>A signature meaning a task can be signed with, as stored in the engine's reason codes. <see cref="AppliesTo"/> lists the task kinds (review, approval).</summary>
public sealed record SignatureMeaning(string Code, string Label, string Decision, bool RequiresComment, IReadOnlyList<string> AppliesTo);

/// <summary>Which template the review of an object type starts with when the user does not choose (e.g. "Submit for review" on a part).</summary>
public sealed record TemplatePin(string ObjectType, string TemplateId, DateTimeOffset PinnedAt, string PinnedBy);

/// <summary>
/// Template management for the admin module, implemented in Keel.Workflow (which Keel.Admin cannot reference). Every method checks that
/// the caller is an administrator, validates through the engine's own parser and audits (including denials) like the rest of the module.
/// </summary>
public interface IWorkflowTemplateAdmin
{
    Task<Outcome<IReadOnlyList<TemplateVersionInfo>>> ListVersionsAsync(Subject subject, bool allVersions, string? correlationId, CancellationToken ct = default);

    /// <summary>Validates a definition with the engine's parser and rules without storing it. Errors are the messages the upload would return.</summary>
    Task<Outcome<TemplateVersionInfo>> ValidateAsync(Subject subject, string definitionJson, CancellationToken ct = default);

    /// <summary>Stores the definition as the next version of its template id and activates it (audited by the workflow module).</summary>
    Task<Outcome<TemplateVersionInfo>> PublishAsync(Subject subject, string definitionJson, string? correlationId, CancellationToken ct = default);

    Task<Outcome<IReadOnlyList<TemplatePin>>> ListPinsAsync(Subject subject, CancellationToken ct = default);

    /// <summary>Pins (or, with a null template id, unpins) the template an object type's review starts with. Audited with before and after.</summary>
    Task<Outcome<TemplatePin?>> PinAsync(Subject subject, string objectType, string? templateId, string? correlationId, CancellationToken ct = default);

    /// <summary>Saved drafts, one per template id.</summary>
    Task<Outcome<IReadOnlyList<TemplateDraftInfo>>> ListDraftsAsync(Subject subject, CancellationToken ct = default);

    /// <summary>Saves (replaces) the draft of a template id. The text is stored as written: it need not be valid yet. Audited.</summary>
    Task<Outcome<TemplateDraftInfo>> SaveDraftAsync(Subject subject, string templateId, int baseVersion, string definitionJson, string? correlationId, CancellationToken ct = default);

    /// <summary>Deletes the draft of a template id (the published versions are not touched). Audited.</summary>
    Task<Outcome<bool>> DiscardDraftAsync(Subject subject, string templateId, string? correlationId, CancellationToken ct = default);

    /// <summary>The meanings a signature can carry, per task kind: what a step will sign as.</summary>
    Task<Outcome<IReadOnlyList<SignatureMeaning>>> ListSignatureMeaningsAsync(Subject subject, CancellationToken ct = default);

    /// <summary>Object types some workflow can run on (those with a registered gateway).</summary>
    IReadOnlyList<string> ReviewableObjectTypes { get; }
}
