using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Keel.Change;

/// <summary>An object a submitted change lists, as frozen at submission.</summary>
internal sealed record SnapshotAffected(string ObjectType, string Key, string Name, string Revision, Guid RevisionId, string Action);

/// <summary>A resulting revision as frozen at submission: the exact iteration reviewed and the fingerprint of its content.</summary>
internal sealed record SnapshotResulting(string ObjectType, string Key, string Revision, Guid RevisionId, string IterationLabel, Guid IterationId, string Fingerprint);

/// <summary>
/// Everything a review of a change object covers (ADR-0019): the change's own content, the affected objects, and the resulting iterations.
/// Stored as text at submission; the review's signatures hash it together with the live fingerprint of every resulting iteration, so
/// tampering with either the stored change or a resulting part's content is detected.
/// </summary>
internal sealed record SnapshotData(
    int Format,
    string Type,
    string Number,
    int Submission,
    string Title,
    string Description,
    string Priority,
    string? ChangeClass,
    string Reason,
    string? SourceNumber,
    string MarkingBanner,
    IReadOnlyList<SnapshotAffected> Affected,
    IReadOnlyList<SnapshotResulting> Resulting);

internal static class ChangeSnapshot
{
    public const int CurrentFormat = 1;
    public const string ContentFormat = "change-content/1";

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    /// <summary>Deterministic: record property order is fixed and lists are sorted by the caller.</summary>
    public static string Serialize(SnapshotData data) => JsonSerializer.Serialize(data, Options);

    public static SnapshotData Parse(string text) =>
        JsonSerializer.Deserialize<SnapshotData>(text, Options) ?? throw new InvalidOperationException("The stored change snapshot is empty.");

    public static byte[] Sha256(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    /// <summary>The id the workflow engine binds an instance to: derived from the change and the submission number (SHA-256, first 16 bytes).</summary>
    public static Guid SubmissionId(Guid changeId, int submission) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"keel-change-submission:{changeId:D}:{submission}")).AsSpan(0, 16));

    /// <summary>Readable text of the frozen change, shown to reviewers on the task page and covered by the signature hash.</summary>
    public static string Describe(SnapshotData d)
    {
        var sb = new StringBuilder();
        sb.Append(d.Number).Append(d.ChangeClass is null ? "" : $" (Class {d.ChangeClass})").Append(": ").AppendLine(d.Title);
        sb.Append("Priority: ").AppendLine(d.Priority);
        if (d.Reason.Length > 0) sb.Append("Reason: ").AppendLine(d.Reason);
        if (d.Description.Length > 0) sb.Append("Description: ").AppendLine(d.Description);
        if (d.SourceNumber is not null) sb.Append("Created from ").AppendLine(d.SourceNumber);
        sb.Append("Markings: ").AppendLine(d.MarkingBanner);
        sb.AppendLine("Affected objects:");
        foreach (var a in d.Affected) sb.Append("  ").Append(a.Key).Append(" revision ").Append(a.Revision).Append(": ").AppendLine(a.Action);
        if (d.Resulting.Count > 0)
        {
            sb.AppendLine("Resulting revisions:");
            foreach (var r in d.Resulting) sb.Append("  ").Append(r.Key).Append(" revision ").Append(r.Revision).Append(" (").Append(r.IterationLabel).AppendLine(")");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The structured part of the signed content: the format version, the stored snapshot's hash, and the live content fingerprint of
    /// every resulting iteration (not the stored one), so editing a resulting iteration after signing changes the hash.
    /// </summary>
    public static JsonObject Attributes(SnapshotData d, string snapshotSha256Hex, IReadOnlyDictionary<Guid, string?> liveFingerprints)
    {
        var o = new JsonObject
        {
            ["format"] = ContentFormat,
            ["submission"] = d.Submission,
            ["snapshotSha256"] = snapshotSha256Hex,
        };
        for (var i = 0; i < d.Affected.Count; i++)
            o[$"affected {i + 1}"] = $"{d.Affected[i].Key} revision {d.Affected[i].Revision}: {d.Affected[i].Action}";
        for (var i = 0; i < d.Resulting.Count; i++)
        {
            var r = d.Resulting[i];
            o[$"resulting {i + 1}"] = $"{r.Key} revision {r.Revision} ({r.IterationLabel}), content {liveFingerprints.GetValueOrDefault(r.IterationId) ?? "missing"}";
        }
        return o;
    }
}
