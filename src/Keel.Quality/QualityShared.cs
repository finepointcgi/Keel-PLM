using System.Data.Common;
using System.Globalization;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Quality;

/// <summary>
/// Key lookups of the objects a quality record points at: parts (through the change gateway of Keel.Core, implemented by the parts module) and other records
/// (through <see cref="IObjectAccess"/> for changes, and the module's own tables). A lookup makes no access decision; callers evaluate the policy on the markings.
/// </summary>
internal sealed class QualityLookups(IEnumerable<IChangeObjectGateway> partGateways, IEnumerable<IObjectAccess> objects)
{
    private readonly IChangeObjectGateway? _parts = partGateways.FirstOrDefault(g => g.ObjectType == "part");

    public Task<ChangeObject?> FindPartAsync(DbConnection conn, DbTransaction tx, string key, string? revision, bool forUpdate, CancellationToken ct) =>
        _parts is null ? Task.FromResult<ChangeObject?>(null) : _parts.FindByKeyAsync(conn, tx, key, revision, forUpdate, ct);

    public Task<IReadOnlyDictionary<Guid, ChangeObject>> FindPartRevisionsAsync(DbConnection conn, DbTransaction tx, IReadOnlyCollection<Guid> revisionIds, CancellationToken ct) =>
        _parts is null ? Task.FromResult<IReadOnlyDictionary<Guid, ChangeObject>>(new Dictionary<Guid, ChangeObject>()) : _parts.FindByRevisionsAsync(conn, tx, revisionIds, ct);

    public sealed record Target(string Kind, string Key, string Title, string StateLabel, string Href, Markings Markings);

    public async Task<Target?> ResolveAsync(DbConnection conn, DbTransaction tx, string kind, string key, CancellationToken ct)
    {
        switch (kind)
        {
            case "part":
            {
                var p = _parts is null ? null : await _parts.FindByKeyAsync(conn, tx, key, null, false, ct);
                return p is null ? null : new Target(kind, p.Key, p.Name, p.StateLabel, AwarenessKinds.Path(AwarenessKinds.Part, p.Key), p.Markings);
            }
            case "change":
            {
                var o = objects.FirstOrDefault(x => x.Kind == AwarenessKinds.Change);
                var info = o is null ? null : await o.FindAsync(key, ct);
                return info is null ? null : new Target(kind, info.Number, info.Name, info.StateLabel, AwarenessKinds.Path(AwarenessKinds.Change, info.Number), info.Markings);
            }
            case QualityKinds.Ncr or QualityKinds.Capa or QualityKinds.Deviation:
            {
                await using var cmd = QualityStore.Cmd(conn, tx,
                    $"SELECT c.number, c.title, COALESCE(ls.label, c.lifecycle_state), {MarkingColumns.Select("c")} FROM {QualityKinds.Table(kind)} c " +
                    "LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = c.lifecycle_id AND ls.state = c.lifecycle_state WHERE c.number = @n");
                cmd.Parameters.AddWithValue("n", key);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                return await r.ReadAsync(ct) ? new Target(kind, r.GetString(0), r.GetString(1), r.GetString(2), AwarenessKinds.Path(kind, r.GetString(0)), MarkingColumns.Read(r, 3)) : null;
            }
            default:
                return null;
        }
    }
}

/// <summary>Detail-loading pieces shared by the three record types: action items, links, history and the transition buttons.</summary>
internal static class QualityShared
{
    public static async Task<List<ActionView>> LoadActionsAsync(DbConnection conn, DbTransaction tx, Subject subject, string parentKind, Guid parentId, bool parentFinal, bool canAdminister, DateOnly today, CancellationToken ct)
    {
        var list = new List<ActionView>();
        await using var cmd = QualityStore.Cmd(conn, tx,
            """
            SELECT a.id, a.number, a.action_type, a.description, a.owner_name, a.owner_username, a.owner_sub, a.due_date, a.status, a.completion_note, a.cancel_reason
            FROM qual.action a WHERE a.parent_kind = @k AND a.parent_id = @id ORDER BY a.number
            """);
        cmd.Parameters.AddWithValue("k", parentKind);
        cmd.Parameters.AddWithValue("id", parentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var status = r.GetString(8);
            var active = status is "Open" or "InProgress";
            var mine = r.GetString(6) == subject.Id;
            DateOnly? due = r.IsDBNull(7) ? null : DateOnly.FromDateTime(r.GetDateTime(7));
            list.Add(new ActionView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), due, active && due is { } d && d < today, status,
                r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10),
                CanStart: status == "Open" && mine && !parentFinal, CanComplete: active && (mine || canAdminister) && !parentFinal, CanCancel: active && canAdminister && !parentFinal));
        }
        return list;
    }

    public static async Task<List<HistoryView>> LoadHistoryAsync(DbConnection conn, DbTransaction tx, string kind, Guid id, CancellationToken ct)
    {
        var list = new List<HistoryView>();
        await using var cmd = QualityStore.Cmd(conn, tx, "SELECT h.at, h.actor_name, h.action, h.detail FROM qual.history h WHERE h.object_kind = @k AND h.object_id = @id ORDER BY h.id");
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new HistoryView(QualityStore.Utc(r, 0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    /// <summary>
    /// The links of a record in both directions. A target the viewer may not read now is a placeholder: no number, title, state or banner. The markings of every
    /// readable target are added to <paramref name="shown"/> so the page banner covers them.
    /// </summary>
    public static async Task<List<LinkView>> LoadLinksAsync(DbConnection conn, DbTransaction tx, Subject subject, QualityLookups lookups, string kind, Guid id, string number, bool canEdit, List<Markings> shown, CancellationToken ct)
    {
        var raw = new List<(Guid Id, string Direction, string Kind, string Key)>();
        await using (var cmd = QualityStore.Cmd(conn, tx,
            """
            SELECT l.id, 'out', l.to_kind, l.to_key FROM qual.link l WHERE l.from_kind = @k AND l.from_id = @id AND l.removed_at IS NULL
            UNION ALL
            SELECT l.id, 'in', l.from_kind, l.from_id::text FROM qual.link l WHERE l.to_kind = @k AND l.to_key = @n AND l.removed_at IS NULL
            """))
        {
            cmd.Parameters.AddWithValue("k", kind);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("n", number);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) raw.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }
        var views = new List<LinkView>();
        foreach (var (linkId, direction, targetKind, key) in raw)
        {
            var targetKey = key;
            if (direction == "in")
            {
                // The incoming link names the source by id; its number is looked up in the source table.
                var head = Guid.TryParse(key, out var sid) ? await QualityStore.LoadByIdAsync(conn, tx, targetKind, sid, false, ct) : null;
                if (head is null) continue;
                targetKey = head.Number;
            }
            var target = await lookups.ResolveAsync(conn, tx, targetKind, targetKey, ct);
            if (target is null || !AccessPolicy.Evaluate(subject, target.Markings, PlmAction.Read).Permitted)
            {
                views.Add(new LinkView(linkId, true, direction, targetKind, null, null, null, null, null, canEdit && direction == "out"));
                continue;
            }
            shown.Add(target.Markings);
            views.Add(new LinkView(linkId, false, direction, targetKind, target.Key, target.Title, target.StateLabel, target.Href, target.Markings.BannerText(), canEdit && direction == "out"));
        }
        return views;
    }

    /// <summary>The lifecycle steps out of the current state as buttons: manual ones can be taken (subject to <paramref name="rule"/>), review ones say how they are reached.</summary>
    public static List<QualityStepView> Transitions(Lifecycle lifecycle, string state, bool canModify, Func<string, string?> rule, Func<string, bool> needsReason)
    {
        var list = new List<QualityStepView>();
        foreach (var t in lifecycle.Transitions.Where(t => t.From == state && t.Via != "system"))
        {
            var label = lifecycle.State(t.To)?.Label ?? t.To;
            string? why = t.Via == "workflow" ? "Reached through the review." : !canModify ? "You may not modify this record." : rule(t.To);
            list.Add(new QualityStepView(t.To, label, t.Via, why is null, why, needsReason(t.To)));
        }
        return list;
    }

    /// <summary>A state with no way out ends the record (Closed, Cancelled, Expired, ...): the lifecycle data decides, not a list in code.</summary>
    public static bool IsFinal(Lifecycle lifecycle, string state) => lifecycle.State(state) is null || lifecycle.Transitions.All(t => t.From != state);

    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public static string Int(long n) => n.ToString(CultureInfo.InvariantCulture);

    public static DateOnly? DateAt(DbDataReader r, int i) => r.IsDBNull(i) ? null : DateOnly.FromDateTime(r.GetDateTime(i));
}
