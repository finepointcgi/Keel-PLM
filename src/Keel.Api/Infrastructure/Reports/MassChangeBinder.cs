using Keel.Change;
using Keel.Core;
using Keel.Core.Reports;
using Keel.Security;

namespace Keel.Api.Infrastructure.Reports;

/// <summary>
/// Attaches a mass change to a change notice through <see cref="ChangeService"/> (migration 0029, ADR-0050), so the notice's own rules decide: access to the notice and the part, the notice's
/// markings must cover the part's, one open notice per revision, and a Released part is revised (a new revision in work bound to the notice) before it is edited.
/// </summary>
public sealed class MassChangeBinder(ChangeService changes) : IMassChangeBinder
{
    public async Task<Outcome<MassChangeOrder>> FindAsync(Subject subject, string number, CancellationToken ct = default)
    {
        var got = await changes.GetAsync(subject, number, null, ct);
        if (!got.IsOk) return new Outcome<MassChangeOrder>(got.Kind == OutcomeKind.Forbidden ? OutcomeKind.NotFound : got.Kind, null, got.Errors);
        var c = got.Value!;
        if (c.Type != ChangeTypes.Ecn) return Outcome<MassChangeOrder>.Invalid([$"{c.Number} is a {c.Type}. Only a change notice (ECN) can carry a mass change."]);
        if (!c.Permissions.CanModify) return Outcome<MassChangeOrder>.Forbidden(c.Permissions.ModifyDeniedReasons);
        if (!c.Permissions.Editable) return Outcome<MassChangeOrder>.Conflict($"{c.Number} is {c.StateLabel} and can no longer be edited.");
        return Outcome<MassChangeOrder>.Ok(new MassChangeOrder(c.Number, c.Title, c.Banner));
    }

    public async Task<Outcome<string>> ListPartAsync(Subject subject, string changeNumber, string partNumber, bool locked, string correlationId, CancellationToken ct = default)
    {
        var got = await changes.GetAsync(subject, changeNumber, correlationId, ct);
        if (!got.IsOk) return new Outcome<string>(got.Kind, null, got.Errors);
        if (got.Value!.Affected.Any(a => !a.Redacted && a.ObjectType == "part" && a.Key == partNumber)) return Outcome<string>.Ok($"{partNumber} is already listed in {changeNumber}.");
        var action = locked ? ChangeActions.Revise : ChangeActions.None;
        var added = await changes.AddAffectedAsync(subject, changeNumber, new AddAffectedCommand("part", partNumber, action), correlationId, ct);
        if (!added.IsOk) return new Outcome<string>(added.Kind, null, added.Errors);
        return Outcome<string>.Ok(locked ? $"Listed in {changeNumber} and revised: a new revision is in work." : $"Listed in {changeNumber}.");
    }
}

/// <summary>What the part and assembly targets share about change orders: the check at preview and the listing before an object is changed.</summary>
internal static class MassBinding
{
    public static string? Order(System.Text.Json.Nodes.JsonObject parameters) => parameters[MassParameterNames.Change] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static string? Note(string? order, CadPartInfo p) => order is not null && p.IsLocked && p.State == "Released" ? $"Revision {p.Revision} is Released: it will be revised through {order} (a new revision in work), then changed." : order is not null ? $"Will be listed in {order}." : null;

    /// <summary>Lists the part on the change order before it is changed. Null when there is no order or the listing worked; otherwise the failure.</summary>
    public static async Task<Outcome<T>?> ListAsync<T>(IMassChangeBinder? binder, Subject subject, string? order, CadPartInfo p, string correlationId, CancellationToken ct)
    {
        if (order is null) return null;
        if (binder is null) return Outcome<T>.Conflict("Change orders are not available.");
        var r = await binder.ListPartAsync(subject, order, p.Number, p.IsLocked, correlationId, ct);
        return r.IsOk ? null : new Outcome<T>(r.Kind == OutcomeKind.Ok ? OutcomeKind.Conflict : r.Kind, default, r.Errors);
    }
}
