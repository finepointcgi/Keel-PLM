using System.Data.Common;
using System.Globalization;

namespace Keel.Core;

/// <summary>The three effectivity dimensions of A19, in order of precedence: serial number, then lot or block, then date.</summary>
public static class EffectivityKinds
{
    public const string Serial = "serial";
    public const string Lot = "lot";
    public const string Date = "date";

    public static readonly IReadOnlyList<string> All = [Serial, Lot, Date];

    public static bool IsValid(string? kind) => kind is Serial or Lot or Date;
}

/// <summary>
/// An effectivity range as stored (ADR-0021). Serials and lots are positive integers inside an end-item context; dates are UTC days
/// stored as the number of days since 1970-01-01. <see cref="From"/> or <see cref="To"/> may be null for an open end, never both.
/// </summary>
public sealed record EffectivityRange(string Kind, string? Context, long? From, long? To)
{
    public static readonly DateOnly Epoch = new(1970, 1, 1);

    public static long DayNumber(DateOnly d) => d.DayNumber - Epoch.DayNumber;
    public static DateOnly DayOf(long n) => DateOnly.FromDayNumber(checked((int)n) + Epoch.DayNumber);

    /// <summary>True when a unit with this value (in the same dimension and context) is covered.</summary>
    public bool Covers(string kind, string? context, long value) =>
        Kind == kind && string.Equals(Context, context, StringComparison.Ordinal) && (From is null || From <= value) && (To is null || To >= value);

    /// <summary>True when the two ranges are in the same dimension and context and share at least one value.</summary>
    public bool Overlaps(EffectivityRange other) =>
        Kind == other.Kind && string.Equals(Context, other.Context, StringComparison.Ordinal)
        && (From ?? long.MinValue) <= (other.To ?? long.MaxValue) && (other.From ?? long.MinValue) <= (To ?? long.MaxValue);

    /// <summary>Readable text such as "serial 101 and up (ORCA-POD)", "lot 2-3 (ORCA-POD)", "date 2027-03-01 to 2027-06-30".</summary>
    public string Describe()
    {
        var isDate = Kind == EffectivityKinds.Date;
        string V(long n) => isDate ? DayOf(n).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : n.ToString(CultureInfo.InvariantCulture);
        var span = (From, To) switch
        {
            (null, null) => "all",
            ({ } f, null) => isDate ? $"from {V(f)}" : $"{V(f)} and up",
            (null, { } t) => $"up to {V(t)}",
            ({ } f, { } t) when f == t => V(f),
            ({ } f, { } t) => isDate ? $"{V(f)} to {V(t)}" : $"{V(f)}-{V(t)}",
        };
        return $"{Kind} {span}{(Context is null ? "" : $" ({Context})")}";
    }
}

/// <summary>Effectivity as a user types it: serial and lot as integers (or a block name known to the context), dates as yyyy-MM-dd.</summary>
public sealed record EffectivityInput(string Kind, string? Context, string? From, string? To);

/// <summary>One revision of a master whose stored effectivity the release rule changed.</summary>
public sealed record RevisionEffectivityChange(Guid RevisionId, string Revision, EffectivityRange? Before, EffectivityRange After);

/// <summary>What releasing a revision with an effectivity does to the other Released revisions of the same master (ADR-0021).</summary>
public sealed record SupersessionPlan(
    IReadOnlyList<Guid> ObsoleteRevisionIds,
    IReadOnlyList<RevisionEffectivityChange> Narrowed,
    IReadOnlyList<string> Notes,
    /// <summary>Set when the rule cannot be applied (the release must be refused with this message).</summary>
    string? Error = null);

/// <summary>
/// Effectivity of part revisions as change management needs it (implemented by Keel.Bom, which owns the effectivity tables). A change
/// notice's affected object may carry "revision B is effective from serial N"; the rule at release decides which earlier Released
/// revisions stay Released with a closed range and which become Obsolete. All calls run in the caller's transaction.
/// </summary>
public interface IRevisionEffectivity
{
    /// <summary>Parses and validates an input; returns the range or a user-facing error.</summary>
    Task<(EffectivityRange? Range, string? Error)> ParseAsync(DbConnection conn, DbTransaction tx, Subject actor, EffectivityInput input, CancellationToken ct);

    /// <summary>Checks that a revision of this master can carry the effectivity (open-ended, dimension agrees with existing ranges).</summary>
    Task<string?> ValidateForReviseAsync(DbConnection conn, DbTransaction tx, Guid masterId, EffectivityRange range, CancellationToken ct);

    /// <summary>Stores or (null) clears the effectivity of an affected object of a draft change notice.</summary>
    Task SetForAffectedAsync(DbConnection conn, DbTransaction tx, Guid affectedObjectId, EffectivityRange? range, Subject actor, DateTimeOffset now, CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, EffectivityRange>> GetForAffectedAsync(DbConnection conn, DbTransaction tx, IReadOnlyCollection<Guid> affectedObjectIds, CancellationToken ct);

    /// <summary>
    /// Called while releasing a change notice, after the resulting revision is Released and before any affected revision is obsoleted:
    /// records the resulting revision's effectivity, narrows the other Released revisions and returns the ones that must become Obsolete.
    /// With a null range every other Released revision is superseded completely (the classic behaviour).
    /// </summary>
    Task<SupersessionPlan> ApplyReleaseAsync(DbConnection conn, DbTransaction tx, Guid masterId, Guid resultingRevisionId, EffectivityRange? range, string changeNumber, Subject actor, DateTimeOffset now, CancellationToken ct);
}

/// <summary>A document revision pinned for a baseline, with its markings when the subject may read it.</summary>
public sealed record PinnedDocument(
    Guid PartIterationId, string LinkType, Guid DocumentMasterId, Guid DocumentRevisionId, bool Readable, Markings? Markings);

/// <summary>Resolves the documents linked to part iterations to concrete document revisions (implemented by Keel.Documents).</summary>
public interface IDocumentPinProvider
{
    /// <summary>
    /// For each part iteration, its describes and references links, each resolved to one document revision: a pinned revision stays,
    /// a references link resolves to the latest Released revision, else the latest (A23). Markings are returned only when the subject may read the document.
    /// </summary>
    Task<IReadOnlyList<PinnedDocument>> ResolveAsync(DbConnection conn, DbTransaction tx, Subject subject, IReadOnlyCollection<Guid> partIterationIds, CancellationToken ct);

    /// <summary>Display data for pinned document revisions; documents the subject may not read come back with <c>Readable</c> false and nothing else.</summary>
    Task<IReadOnlyDictionary<Guid, PinnedDocumentInfo>> DescribeAsync(DbConnection conn, DbTransaction tx, Subject subject, IReadOnlyCollection<Guid> documentRevisionIds, CancellationToken ct);
}

/// <summary>A pinned document revision as shown to a reader (A4: nothing but <see cref="Readable"/> when the reader may not see it).</summary>
public sealed record PinnedDocumentInfo(
    Guid RevisionId, bool Readable, string? Number, string? Name, string? DocumentType, string? Revision, string? State, string? Banner, Markings? Markings);
