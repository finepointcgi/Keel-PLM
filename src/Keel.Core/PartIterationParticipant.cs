using System.Data.Common;

namespace Keel.Core;

/// <summary>
/// Extension point that lets modules own data attached to a part iteration (BOM usage links in 1.4, document links in 1.3)
/// without Keel.Parts knowing about them. Keel.Parts calls every registered participant inside the transaction of the
/// check-out, check-in or undo it is performing, so the participant's rows commit or roll back with the part change.
/// </summary>
public interface IPartIterationParticipant
{
    /// <summary>Called after the check-out row exists. Copy the latest iteration's data into the private working copy.</summary>
    Task OnCheckOutAsync(DbTransaction tx, Guid revisionId, Guid latestIterationId, CancellationToken ct);

    /// <summary>
    /// Called after the new iteration row exists and before the check-out row is deleted. Freeze the working copy into the
    /// new iteration. Throw <see cref="IterationVetoException"/> to refuse the check-in with a message for the user.
    /// </summary>
    Task OnCheckInAsync(DbTransaction tx, Guid revisionId, Guid newIterationId, CancellationToken ct);

    /// <summary>
    /// Called when a change notice creates the next revision (increment 1.6), after the new iteration row exists. Copy the data the old
    /// revision's latest iteration owns into the new iteration. Default: nothing to carry forward.
    /// </summary>
    Task OnReviseAsync(DbTransaction tx, Guid fromIterationId, Guid newIterationId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Stable, canonical text describing the data this participant owns for an iteration, included in the content fingerprint that
    /// change-notice signatures cover. Null when the participant owns nothing that belongs in it.
    /// </summary>
    Task<string?> DescribeContentAsync(DbTransaction tx, Guid iterationId, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>
    /// True when this participant's <see cref="DescribeContentAsync"/> text is also covered by the part review's e-signature hash (increment 2.2:
    /// classification). Off by default so the signature hashes of existing content (for example BOM lines) do not change.
    /// </summary>
    bool CoveredByReviewSignature => false;

    /// <summary>Called before the check-out row is deleted by an undo. Working copies normally disappear by cascade; override only if needed.</summary>
    Task OnUndoCheckOutAsync(DbTransaction tx, Guid revisionId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Thrown by a participant to refuse a check-in; the message is shown to the user and the transaction rolls back.</summary>
public sealed class IterationVetoException(string message) : Exception(message);
