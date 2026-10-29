using Keel.Core;
using Npgsql;

namespace Keel.Workflow;

/// <summary>
/// What the review workflow tells the notification sink (UX-4, ADR-0030), inside the transaction of the step that caused it: a person who is assigned a task or asked to review
/// hears about it, the person who submitted hears about the decision, and whoever signs starts watching the object. Rolled-back steps leave no notification.
/// The sink checks every recipient against the object's markings before a row is written.
/// </summary>
internal static class WorkflowAwareness
{
    /// <summary>The awareness kind of a workflow object type (parts, documents, baselines, MBOMs; changes are "pr", "ecr", "ecn"), or null.</summary>
    private static string? KindOf(string objectType) => AwarenessKinds.FromAuditObjectType(objectType);

    public static async Task TaskAssignedAsync(INotificationSink? sink, NpgsqlConnection conn, NpgsqlTransaction tx, InstanceRow instance, TaskState task, Subject actor, CancellationToken ct)
    {
        if (sink is null || task.AssigneeSub is null || KindOf(instance.ObjectType) is not { } kind) return;
        var code = task.Kind == TaskKinds.Review ? NotificationKindCodes.ReviewRequested : NotificationKindCodes.TaskAssigned;
        await sink.AppendAsync(conn, tx, new NotificationRequest(code, kind, instance.ObjectKey, instance.Markings, actor.Id, task.AssigneeSub, task.Id), ct);
    }

    public static async Task DecidedAsync(INotificationSink? sink, NpgsqlConnection conn, NpgsqlTransaction tx, InstanceRow instance, bool approved, Markings markings, Subject actor, CancellationToken ct)
    {
        if (sink is null || KindOf(instance.ObjectType) is not { } kind) return;
        var code = approved ? NotificationKindCodes.ReviewApproved : NotificationKindCodes.ReviewRejected;
        await sink.AppendAsync(conn, tx, new NotificationRequest(code, kind, instance.ObjectKey, markings, actor.Id, instance.StartedBy, instance.Id), ct);
    }

    public static async Task SignedAsync(INotificationSink? sink, NpgsqlConnection conn, NpgsqlTransaction tx, InstanceRow instance, Subject actor, CancellationToken ct)
    {
        if (sink is null || KindOf(instance.ObjectType) is not { } kind) return;
        await sink.AutoWatchAsync(conn, tx, actor.Id, kind, instance.ObjectKey, ct);
    }
}
