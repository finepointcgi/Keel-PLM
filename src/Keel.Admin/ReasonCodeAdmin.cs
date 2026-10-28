using System.Text.RegularExpressions;
using Keel.Core;
using Npgsql;

namespace Keel.Admin;

/// <summary>
/// A reason code: a signature meaning (<c>wf.reason_code</c>, kind "signature") or a change reason (<c>chg.reason_code</c>, kind "change", with a scope such
/// as <c>pr.reject</c>). Codes are retired, never deleted: signatures and history refer to them.
/// </summary>
public sealed record ReasonCodeDto(string Kind, string? Scope, string Code, string Label, string? Decision, bool? RequiresComment, IReadOnlyList<string>? AppliesTo, int SortOrder, bool IsActive);

public sealed partial class ReasonCodeAdminService(AdminRunner run)
{
    public static readonly IReadOnlyList<string> ChangeScopes = ["pr.reject", "pr.close", "ecr.close", "ecn.cancel"];
    public static readonly IReadOnlyList<string> Decisions = ["approve", "reject"];
    public static readonly IReadOnlyList<string> TaskKinds = ["review", "approval"];

    public Task<Outcome<IReadOnlyList<ReasonCodeDto>>> ListAsync(Subject subject, CancellationToken ct = default) =>
        run.ReadAsync<IReadOnlyList<ReasonCodeDto>>(subject, "admin.reason_list", null, conn => LoadAsync(conn, null, ct), ct);

    private static async Task<IReadOnlyList<ReasonCodeDto>> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var list = new List<ReasonCodeDto>();
        await using (var cmd = AdminRunner.Cmd(conn, tx, "SELECT code, label, decision, requires_comment, applies_to, sort_order, is_active FROM wf.reason_code ORDER BY sort_order, code"))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(new ReasonCodeDto("signature", null, r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), (string[])r[4], r.GetInt32(5), r.GetBoolean(6)));
        }
        await using (var cmd = AdminRunner.Cmd(conn, tx, "SELECT scope, code, label, sort_order, is_active FROM chg.reason_code ORDER BY scope, sort_order, code"))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(new ReasonCodeDto("change", r.GetString(0), r.GetString(1), r.GetString(2), null, null, null, r.GetInt32(3), r.GetBoolean(4)));
        }
        return list;
    }

    public Task<Outcome<ReasonCodeDto>> SaveAsync(Subject subject, ReasonCodeDto input, string? correlationId, CancellationToken ct = default) =>
        run.ChangeAsync<ReasonCodeDto>(subject, "admin.reason_save", $"{input.Kind}/{input.Scope}/{input.Code}", correlationId, async (conn, tx) =>
        {
            var errors = new List<string>();
            if (!CodePattern().IsMatch(input.Code ?? "")) errors.Add("The code must start with a letter and use letters and digits only (at most 41 characters).");
            if (string.IsNullOrWhiteSpace(input.Label) || input.Label.Trim().Length > 120) errors.Add("The label is required (at most 120 characters).");
            if (input.Kind == "signature")
            {
                if (input.Decision is null || !Decisions.Contains(input.Decision)) errors.Add("The decision must be approve or reject.");
                if (input.AppliesTo is null || input.AppliesTo.Count == 0 || input.AppliesTo.Any(a => !TaskKinds.Contains(a))) errors.Add("Applies to must list review and/or approval.");
                if (input.Decision == "reject" && input.RequiresComment == false) errors.Add("A rejection always requires a comment.");
            }
            else if (input.Kind == "change")
            {
                if (input.Scope is null || !ChangeScopes.Contains(input.Scope)) errors.Add($"The scope must be one of: {string.Join(", ", ChangeScopes)}.");
            }
            else errors.Add("The kind must be signature or change.");
            if (errors.Count > 0) return Step<ReasonCodeDto>.Invalid(errors);

            var before = (await LoadAsync(conn, tx, ct)).FirstOrDefault(c => c.Kind == input.Kind && c.Scope == input.Scope && c.Code == input.Code);
            if (input.Kind == "signature")
            {
                if (before is not null && before.Decision != input.Decision)
                    return Step<ReasonCodeDto>.Invalid("The decision of an existing meaning cannot change: signatures already recorded carry it. Create a new meaning instead.");
                await using var cmd = AdminRunner.Cmd(conn, tx,
                    """
                    INSERT INTO wf.reason_code (code, label, decision, requires_comment, applies_to, sort_order, is_active) VALUES (@c, @l, @d, @rc, @a, @o, @act)
                    ON CONFLICT (code) DO UPDATE SET label = @l, requires_comment = @rc, applies_to = @a, sort_order = @o
                    """, ("c", input.Code), ("l", input.Label.Trim()), ("d", input.Decision), ("rc", input.RequiresComment ?? false), ("a", input.AppliesTo!.ToArray()), ("o", input.SortOrder), ("act", true));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var cmd = AdminRunner.Cmd(conn, tx,
                    """
                    INSERT INTO chg.reason_code (scope, code, label, sort_order, is_active) VALUES (@s, @c, @l, @o, true)
                    ON CONFLICT (scope, code) DO UPDATE SET label = @l, sort_order = @o
                    """, ("s", input.Scope), ("c", input.Code), ("l", input.Label.Trim()), ("o", input.SortOrder));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            var after = (await LoadAsync(conn, tx, ct)).Single(c => c.Kind == input.Kind && c.Scope == input.Scope && c.Code == input.Code);
            var guard = SignatureCoverage(await LoadAsync(conn, tx, ct));
            if (guard is not null) return Step<ReasonCodeDto>.Invalid(guard);
            return Step<ReasonCodeDto>.Ok(after, ("before", before is null ? null : AdminRunner.ToJson(before)), ("after", AdminRunner.ToJson(after)));
        }, ct);

    public Task<Outcome<ReasonCodeDto>> SetActiveAsync(Subject subject, string kind, string? scope, string code, bool active, string? reason, string? correlationId, CancellationToken ct = default) =>
        run.ChangeAsync<ReasonCodeDto>(subject, active ? "admin.reason_restore" : "admin.reason_retire", $"{kind}/{scope}/{code}", correlationId, async (conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) return Step<ReasonCodeDto>.Invalid("A reason is required.");
            var all = await LoadAsync(conn, tx, ct);
            var before = all.FirstOrDefault(c => c.Kind == kind && c.Scope == scope && c.Code == code);
            if (before is null) return Step<ReasonCodeDto>.NotFound();
            if (before.IsActive == active) return Step<ReasonCodeDto>.Conflict(active ? "Already active." : "Already retired.");
            if (kind == "signature")
            {
                await using var cmd = AdminRunner.Cmd(conn, tx, "UPDATE wf.reason_code SET is_active = @a WHERE code = @c", ("a", active), ("c", code));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var cmd = AdminRunner.Cmd(conn, tx, "UPDATE chg.reason_code SET is_active = @a WHERE scope = @s AND code = @c", ("a", active), ("s", scope), ("c", code));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            var now = await LoadAsync(conn, tx, ct);
            var coverage = SignatureCoverage(now) ?? ChangeCoverage(now);
            if (coverage is not null) return Step<ReasonCodeDto>.Invalid(coverage);
            var after = before with { IsActive = active };
            return Step<ReasonCodeDto>.Ok(after, ("before", AdminRunner.ToJson(before)), ("after", AdminRunner.ToJson(after)), ("reason", reason.Trim()));
        }, ct);

    /// <summary>Every review and approval needs at least one active approving and one active rejecting meaning, or nobody could sign.</summary>
    internal static string? SignatureCoverage(IReadOnlyList<ReasonCodeDto> all)
    {
        foreach (var kind in TaskKinds)
            foreach (var decision in Decisions)
                if (!all.Any(c => c.Kind == "signature" && c.IsActive && c.Decision == decision && c.AppliesTo!.Contains(kind)))
                    return $"At least one active signature meaning that {(decision == "approve" ? "approves" : "rejects")} is needed for a {kind}.";
        return null;
    }

    private static string? ChangeCoverage(IReadOnlyList<ReasonCodeDto> all) =>
        ChangeScopes.Where(s => !all.Any(c => c.Kind == "change" && c.Scope == s && c.IsActive)).Select(s => $"At least one active reason code is needed for {s}.").FirstOrDefault();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,40}$")]
    private static partial Regex CodePattern();
}
