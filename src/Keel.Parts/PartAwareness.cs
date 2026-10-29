using Keel.Core;
using Npgsql;

namespace Keel.Parts;

/// <summary>
/// What part commands tell the notification sink (UX-4, ADR-0030), inside the command's transaction: the creator and whoever checks a part out start watching it, and the
/// watchers of a part hear about a new version or a state change. Releases and obsolescence are not reported here: they reach watchers through the integration outbox
/// (the fan-out service), so there is exactly one notification per event.
/// </summary>
internal static class PartAwareness
{
    public static async Task AfterAsync(INotificationSink? sink, NpgsqlConnection conn, NpgsqlTransaction tx, string action, string number, Markings markings, Subject actor,
        (string Key, string? Value)[] details, CancellationToken ct)
    {
        if (sink is null) return;
        switch (action)
        {
            case "part.create" or "part.checkout":
                await sink.AutoWatchAsync(conn, tx, actor.Id, AwarenessKinds.Part, number, ct);
                break;
            case "part.checkin":
                await sink.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.WatchedChanged, AwarenessKinds.Part, number, markings, actor.Id), ct);
                break;
            case "part.transition" or "part.set_state":
                var to = details.FirstOrDefault(d => d.Key == "to").Value;
                if (to is not (null or "Released" or "Obsolete"))
                    await sink.AppendAsync(conn, tx, new NotificationRequest(NotificationKindCodes.WatchedState, AwarenessKinds.Part, number, markings, actor.Id), ct);
                break;
        }
    }
}
