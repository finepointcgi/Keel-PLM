using Keel.Core;
using Npgsql;

namespace Keel.Web.Services;

/// <summary>
/// The states of a data-defined lifecycle, for the stepper on an object page. Accepts a lifecycle id or the family name (then the family's current version).
/// An unknown lifecycle yields no states and the page simply shows no stepper.
/// </summary>
public sealed class LifecycleSteps(NpgsqlDataSource db)
{
    public async Task<IReadOnlyList<LifecycleState>> StatesAsync(string lifecycleIdOrFamily, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var lifecycle = await Lifecycle.LoadAsync(conn, null, lifecycleIdOrFamily, ct);
        if (lifecycle is null)
        {
            try { lifecycle = await Lifecycle.LoadAsync(conn, null, await Lifecycle.CurrentIdAsync(conn, null, lifecycleIdOrFamily, ct), ct); }
            catch (InvalidOperationException) { return []; }
        }
        return lifecycle?.States ?? [];
    }
}
