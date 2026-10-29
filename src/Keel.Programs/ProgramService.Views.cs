using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Programs;

public sealed partial class ProgramService
{
    /// <summary>
    /// The dashboard of one program. Every number is computed over what the caller may read (the item and deliverable read filters), so a count, a percentage or a date never reflects an item
    /// the caller cannot see. Deliverables whose target the caller cannot read are counted once as "not visible".
    /// </summary>
    public Task<Outcome<ProgramDashboard>> GetDashboardAsync(Subject subject, string code, DateOnly? asOf, string? corr, CancellationToken ct = default) =>
        ReadAsync(subject, code, "program.dashboard", corr, async (conn, tx, head) =>
        {
            var today = asOf ?? Today;
            var sched = await LoadScheduleAsync(conn, tx, subject, head, null, today, ct);
            var deliverables = await LoadDeliverablesAsync(conn, tx, subject, head, null, ct);
            var open = sched.Items.Where(i => i.Kind != ProgramKinds.Phase && ItemStatuses.Open(i.Status)).ToList();
            var byStatus = ItemStatuses.All.Select(s => new CountBy(s, sched.Items.Count(i => i.Status == s))).ToList();
            var upcoming = open.Where(i => i.Kind == ProgramKinds.Milestone && i.PlannedEnd >= today && i.PlannedEnd <= today.AddDays(UpcomingDays)).OrderBy(i => i.PlannedEnd).ThenBy(i => i.Key, StringComparer.Ordinal).Take(10).ToList();
            var overdue = open.Where(i => i.PlannedEnd < today).OrderBy(i => i.PlannedEnd).ThenBy(i => i.Key, StringComparer.Ordinal).Take(20).ToList();
            var readiness = new[] { Readiness.Ready, Readiness.InReview, Readiness.InWork, Readiness.Ended, Readiness.NotVisible }
                .Select(r => new CountBy(r, deliverables.Items.Count(d => d.Readiness == r))).ToList();
            var gates = sched.Items.Where(i => i.GateNumber is not null).OrderBy(i => i.PlannedEnd).ThenBy(i => i.Key, StringComparer.Ordinal).ToList();
            var slipped = open.Where(i => i.SlipDays > 0).ToList();
            return new ProgramDashboard(head.Code, today, sched.OverallPercent, sched.Items.Count, byStatus, upcoming, overdue, readiness, deliverables.Items.Count(d => d.Overdue), deliverables.Items.Count,
                gates, sched.Cyclic ? null : sched.Items.Count(i => i.Critical), sched.Finish, slipped.Count == 0 ? 0 : slipped.Max(i => i.SlipDays), slipped.Count, sched.Compared,
                Markings.AggregateBanner([head.Markings, .. sched.Items.Select(i => i.Markings), .. deliverables.Items.Select(d => d.Markings)]));
        }, d => [("items", ProgramStore.Int(d.ItemCount))], ct);

    /// <summary>Items and deliverables owned by the caller across every program the caller can read: the "my work" card of Home. Overdue first, then by date.</summary>
    public async Task<MyProgramWork> MyWorkAsync(Subject subject, int take, string? corr, CancellationToken ct = default)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "i");
        var pFilter = AccessPolicy.BuildReadFilter(subject, "p");
        var dFilter = AccessPolicy.BuildReadFilter(subject, "d");
        var today = Today;
        var items = new List<MyWorkItem>();
        var deliverables = new List<MyDeliverable>();
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT p.code, p.name, i.kind, i.key, i.title, i.status, i.planned_end, i.gate_number, {MarkingColumns.Select("i")}
            FROM prg.item i JOIN prg.program p ON p.id = i.program_id
            WHERE {filter.Sql} AND {pFilter.Sql} AND i.owner_sub = @me AND i.status IN ('NotStarted', 'InProgress', 'Blocked') AND i.kind <> 'Phase' AND p.status NOT IN ('Complete', 'Cancelled')
            ORDER BY i.planned_end, p.code, i.key LIMIT @take
            """, conn, tx))
        {
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            foreach (var (k, v) in pFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("me", subject.Id);
            cmd.Parameters.AddWithValue("take", Math.Clamp(take, 1, 200));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var due = ProgramStore.Date(r, 6);
                items.Add(new MyWorkItem(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), due, due < today, ProgramStore.Str(r, 7), MarkingColumns.Read(r, 8)));
            }
        }
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT p.code, d.number, d.title, d.object_kind, d.object_key, d.due_date, {MarkingColumns.Select("d")}
            FROM prg.deliverable d JOIN prg.program p ON p.id = d.program_id
            WHERE {dFilter.Sql} AND {pFilter.Sql} AND d.owner_sub = @me AND d.removed_at IS NULL AND p.status NOT IN ('Complete', 'Cancelled')
            ORDER BY d.due_date, p.code, d.number LIMIT @take
            """, conn, tx))
        {
            foreach (var (k, v) in dFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
            foreach (var (k, v) in pFilter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("me", subject.Id);
            cmd.Parameters.AddWithValue("take", Math.Clamp(take, 1, 200));
            var raw = new List<(string, string, string, string, string, DateOnly, Markings)>();
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) raw.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), ProgramStore.Date(r, 5), MarkingColumns.Read(r, 6)));
            foreach (var (pc, number, title, kind, key, due, m) in raw)
            {
                var (readiness, _, _, _) = await ResolveTargetAsync(subject, kind, key, ct);
                if (readiness == Readiness.Ready) continue;
                deliverables.Add(new MyDeliverable(pc, number, title, readiness, due, due < today, m));
            }
        }
        await _audit.AppendAsync(tx, ProgramStore.Entry(subject, "program.my_work", AuditOutcome.Success, ProgramStore.ObjectType, null, corr, ("items", ProgramStore.Int(items.Count)), ("deliverables", ProgramStore.Int(deliverables.Count))), ct);
        await tx.CommitAsync(ct);
        items = [.. items.OrderByDescending(i => i.Overdue).ThenBy(i => i.Due).ThenBy(i => i.ProgramCode, StringComparer.Ordinal).ThenBy(i => i.Key, StringComparer.Ordinal)];
        return new MyProgramWork(items, deliverables, Markings.AggregateBanner(items.Select(i => i.Markings).Concat(deliverables.Select(d => d.Markings))));
    }
}
