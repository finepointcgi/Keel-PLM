using Keel.Core;
using Npgsql;

namespace Keel.Documents;

/// <summary>
/// What document commands tell the notification sink (UX-4, ADR-0030), inside the command's transaction: the creator and whoever checks a document out start watching it,
/// and its watchers hear about a new version or a state change. Releases and obsolescence reach watchers through the integration outbox, not from here.
/// </summary>
internal static class DocumentAwareness
{
    public static async Task AfterAsync(INotificationSink? sink, NpgsqlConnection conn, NpgsqlTransaction tx, string action, string number, Markings markings, Subject actor,
        (string Key, string? Value)[] details, CancellationToken ct)
    {
        if (sink is null) return;
        switch (action)
        {
            case "document.create" or "document.checkout":
                await sink.AutoWatchAsync(conn, tx, actor.Id, AwarenessKinds.Document, number, ct);
                break;
            case "document.checkin":
                await sink.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.WatchedChanged, AwarenessKinds.Document, number, markings, actor.Id), ct);
                break;
            case "document.transition" or "document.set_state":
                var to = details.FirstOrDefault(d => d.Key == "to").Value;
                if (to is not (null or "Released" or "Obsolete"))
                    await sink.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.WatchedState, AwarenessKinds.Document, number, markings, actor.Id), ct);
                break;
        }
    }
}
