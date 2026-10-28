using System.Text.Json.Nodes;

namespace Keel.Core;

/// <summary>One stored version of a workflow template, as the admin UI shows it.</summary>
public sealed record TemplateVersionInfo(
    string Id, int Version, string Name, string? Description, string ObjectType, bool IsActive, DateTimeOffset CreatedAt, string CreatedBy, JsonNode Definition);

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

    /// <summary>Object types some workflow can run on (those with a registered gateway).</summary>
    IReadOnlyList<string> ReviewableObjectTypes { get; }
}
