using Keel.Core;

namespace Keel.Documents.Compare;

/// <summary>One side of a comparison: a document number and optionally one of its versions (iteration id); no id means the latest version of the latest revision.</summary>
public sealed record CompareRef(string Number, Guid? IterationId = null);

/// <summary>A document version as the comparison shows it.</summary>
public sealed record CompareSide(
    string Number, string Name, Guid IterationId, string Label, string State, DateTimeOffset CreatedAt, string CreatedBy, string? ChangeNote,
    string FileName, string MediaType, long SizeBytes, string Sha256, string Banner, string Format);

public sealed record MetadataRow(string Field, string Left, string Right, bool Same);

public sealed record RedlineReply(Guid Id, string AuthorName, DateTimeOffset At, string Body);

/// <summary>
/// A comment thread on one changed range. <see cref="Detached"/> means the change it was written on is no longer there (the diff or the text reader changed since):
/// the comment is kept and shown, but not drawn on a change.
/// </summary>
public sealed record RedlineThread(
    Guid Id, int ChangeIndex, string ChangeKind, bool Detached, DateTimeOffset At, string AuthorName, string Body, bool Resolved, DateTimeOffset? ResolvedAt, string? ResolvedByName,
    IReadOnlyList<RedlineReply> Replies, bool CanResolve);

public static class CompareModes
{
    /// <summary>Both files were read as text and compared word by word.</summary>
    public const string Text = "text";
    /// <summary>The content is not compared; the table of size, hash and version facts is.</summary>
    public const string Metadata = "metadata";
    /// <summary>Both files have the same bytes.</summary>
    public const string Identical = "identical";
}

/// <summary>
/// The result of comparing two document versions (increment 9.5, ADR-0053). Whoever receives it has been authorized to read both documents; the content of a text
/// comparison needs permission to download both. <see cref="Banner"/> is the aggregate marking of BOTH documents and must be shown on whatever renders this.
/// </summary>
public sealed record DocumentComparison(
    CompareSide Left,
    CompareSide Right,
    string Mode,
    bool SameDocument,
    bool SameBytes,
    string? NotComparableReason,
    string Banner,
    IReadOnlyList<string> Notices,
    TextDiff? Diff,
    IReadOnlyList<MetadataRow> Metadata,
    Guid? ReviewId,
    IReadOnlyList<RedlineThread> Threads,
    bool CanComment)
{
    public int OpenThreads => Threads.Count(t => !t.Resolved);
}

/// <summary>A posted redline comment.</summary>
public sealed record PostedRedlineComment(Guid Id, Guid ReviewId, bool IsReply);

/// <summary>A redline report, ready to download.</summary>
public sealed record RedlineExport(string FileName, string ContentType, string Banner, byte[] Content);

/// <summary>One review in a list: the two versions and how many comment threads are open.</summary>
public sealed record RedlineReviewSummary(
    Guid ReviewId, string LeftNumber, string LeftName, string LeftLabel, string RightNumber, string RightName, string RightLabel,
    int OpenThreads, int TotalThreads, DateTimeOffset CreatedAt, string CreatedByName, string Banner);

/// <summary>A version of a document offered when choosing what to compare.</summary>
public sealed record CompareVersionOption(Guid IterationId, string Label, string State, bool IsLatest, DateTimeOffset CreatedAt, string CreatedBy, string FileName, long SizeBytes);

/// <summary>The versions of a document that can be compared (those with a primary file that is not quarantined), oldest first, with its name and banner.</summary>
public sealed record CompareVersionList(string Number, string Name, string Banner, IReadOnlyList<CompareVersionOption> Versions);

/// <summary>What a review pointer resolves to, after authorization: the two versions to open.</summary>
public sealed record RedlineReviewTarget(Guid ReviewId, CompareRef Left, CompareRef Right);
