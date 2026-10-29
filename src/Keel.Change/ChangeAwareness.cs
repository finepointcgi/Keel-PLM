using System.Data.Common;
using Keel.Core;

namespace Keel.Change;

/// <summary>
/// What change management tells the notification sink (UX-4, ADR-0030), inside the transaction of the step that caused it: the assignee of a change task hears about it, and the
/// person who requested a change hears the decision on it (approved, released or rejected). Nothing is sent for a rolled-back step. The sink checks each recipient
/// against the change's markings before a row is written.
/// </summary>
internal static class ChangeAwareness
{
    public static async Task TaskAssignedAsync(INotificationSink? sink, DbConnection conn, DbTransaction tx, ChangeRow change, string assigneeSub, Subject actor, CancellationToken ct)
    {
        if (sink is null) return;
        await sink.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.ChangeTaskAssigned, AwarenessKinds.Change, change.Number, change.Markings, actor.Id, assigneeSub), ct);
    }

    public static async Task DecidedAsync(INotificationSink? sink, DbConnection conn, DbTransaction tx, ChangeRow change, bool approved, Subject actor, CancellationToken ct)
    {
        if (sink is null) return;
        var code = approved ? NotificationKindCodes.ChangeDecisionApproved : NotificationKindCodes.ChangeDecisionRejected;
        await sink.AppendAsync(conn, tx, new NotificationRequest(code, AwarenessKinds.Change, change.Number, change.Markings, actor.Id, change.RequesterSub), ct);
    }
}
