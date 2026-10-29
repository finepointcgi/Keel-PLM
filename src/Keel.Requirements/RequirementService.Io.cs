using System.Text;
using Keel.Core;
using Keel.Security;
using static Keel.Requirements.RequirementStore;

namespace Keel.Requirements;

/// <summary>What an import would do with one row of the file. <see cref="Key"/> is the requirement it updates (or null for a new one).</summary>
public sealed record ImportRowPlan(int Line, string? SourceId, string Action, string? Key, string? Title, IReadOnlyList<string> Problems);

/// <summary>The reviewable result of reading a file. Apply the same file with the <see cref="Digest"/> of its dry run: a changed file or a specification that moved on makes the digests differ and nothing is applied.</summary>
public sealed record ImportPlan(string Format, IReadOnlyList<ImportRowPlan> Rows, int Created, int Updated, int Unchanged, int Errors, string Digest, bool Applied, int? Iteration, string Banner);

public sealed record ExportFile(string FileName, string ContentType, byte[] Content, string Banner, int Count);

/// <summary>Plans an import: pure, from the parsed rows and the requirements the caller can read.</summary>
internal static class ImportPlanner
{
    public sealed record Planned(IReadOnlyList<ImportRowPlan> Rows, IReadOnlyList<EditOp> Ops, string Digest);

    public static Planned Plan(IReadOnlyList<ImportRow> rows, IReadOnlyList<RequirementRow> existingReadable, Func<RequirementRow, bool> canModify)
    {
        var existing = existingReadable.Where(r => !r.Redacted).ToDictionary(r => r.Key!, StringComparer.Ordinal);
        var bySource = new Dictionary<string, int>(StringComparer.Ordinal);
        var plans = new ImportRowPlan[rows.Count];
        var actions = new string[rows.Count];
        var problems = rows.Select(_ => new List<string>()).ToArray();
        var merged = new (string Title, string Text, string Type, string Priority, string Method, string Rationale, string Status)[rows.Count];
        var keys = new string?[rows.Count];

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.SourceId is not null && !bySource.TryAdd(r.SourceId, i)) problems[i].Add($"The id {r.SourceId} is used by an earlier row.");
            RequirementRow? ex = null;
            if (r.SourceId is not null)
            {
                if (existing.TryGetValue(r.SourceId, out ex)) { actions[i] = "update"; keys[i] = ex.Key; }
                else if (RequirementRules.IsKey(r.SourceId)) { actions[i] = "error"; problems[i].Add($"Requirement {r.SourceId} does not exist."); }
                else actions[i] = "create"; // a foreign id (another tool's identifier): a new requirement
            }
            else actions[i] = "create";
            if (actions[i] == "error") { merged[i] = ("", "", "", "", "", "", ""); continue; }
            var create = ex is null;
            merged[i] = (r.Title ?? ex?.Title ?? "", r.Text ?? ex?.Text ?? "", r.Type ?? ex?.Type ?? (create ? "functional" : ""), r.Priority ?? ex?.Priority ?? (create ? "medium" : ""),
                r.Method ?? ex?.Method ?? (create ? "test" : ""), r.Rationale ?? ex?.Rationale ?? "", r.Status ?? ex?.Status ?? (create ? ReqStatuses.Draft : ""));
            problems[i].AddRange(RequirementRules.Validate(merged[i].Title, merged[i].Text, merged[i].Type, merged[i].Priority, merged[i].Method, merged[i].Rationale, merged[i].Status));
            if (ex is not null && !canModify(ex)) problems[i].Add($"You are not authorized to change {ex.Key}.");
            if (ex is not null && problems[i].Count == 0 && ex.Title == merged[i].Title && ex.Text == merged[i].Text && ex.Type == merged[i].Type && ex.Priority == merged[i].Priority &&
                ex.Method == merged[i].Method && (ex.Rationale ?? "") == merged[i].Rationale && ex.Status == merged[i].Status) actions[i] = "unchanged";
        }

        // Parents: another row of the file (by its id) or an existing requirement (by key).
        var parentRow = new int?[rows.Count];
        var parentKey = new string?[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var p = rows[i].ParentRef;
            if (p is null || actions[i] == "error") continue;
            if (bySource.TryGetValue(p, out var pi) && pi != i && actions[pi] != "error")
            {
                if (actions[pi] == "create") parentRow[i] = pi; else parentKey[i] = keys[pi];
            }
            else if (existing.ContainsKey(p)) parentKey[i] = p;
            else problems[i].Add($"The parent {p} was not found.");
        }
        // A chain of new rows that leads back to itself cannot be created.
        for (var i = 0; i < rows.Count; i++)
        {
            var seen = new HashSet<int> { i };
            for (var cur = parentRow[i]; cur is { } c; cur = parentRow[c])
                if (!seen.Add(c)) { problems[i].Add("The parents of these rows form a loop."); parentRow[i] = null; break; }
        }
        // A move is only issued for an existing requirement whose parent differs from its current one.
        var moves = new List<EditOp>();
        for (var i = 0; i < rows.Count; i++)
            if (keys[i] is { } k && rows[i].ParentRef is not null && actions[i] != "error" && problems[i].Count == 0 && parentRow[i] is null && parentKey[i] is not null && existing[k].ParentKey != parentKey[i])
                moves.Add(new EditOp("move", k, new RequirementInput("", "", "", "", "", ParentKey: parentKey[i])));
        // Creation order: parents first, otherwise file order.
        var order = new List<int>();
        var placed = new HashSet<int>();
        void Place(int i)
        {
            if (!placed.Add(i)) return;
            if (parentRow[i] is { } pi) Place(pi);
            order.Add(i);
        }
        for (var i = 0; i < rows.Count; i++) if (actions[i] == "create") Place(i);

        var ops = new List<EditOp>();
        for (var i = 0; i < rows.Count; i++)
            if (actions[i] == "update" && problems[i].Count == 0)
                ops.Add(new EditOp("update", keys[i], new RequirementInput(merged[i].Title, merged[i].Text, merged[i].Type, merged[i].Priority, merged[i].Method, merged[i].Rationale, merged[i].Status)));
        var addIndex = new Dictionary<int, int>();
        var adds = 0;
        foreach (var i in order)
        {
            if (problems[i].Count > 0) continue;
            string? parent = parentRow[i] is { } pr ? (addIndex.TryGetValue(pr, out var n) ? "#" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) : null) : parentKey[i];
            ops.Add(new EditOp("add", null, new RequirementInput(merged[i].Title, merged[i].Text, merged[i].Type, merged[i].Priority, merged[i].Method, merged[i].Rationale, merged[i].Status, ParentKey: parent)));
            addIndex[i] = adds++;
        }
        ops.AddRange(moves);

        var digestLines = new List<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            var action = problems[i].Count > 0 ? "error" : actions[i];
            var shaKey = keys[i] ?? rows[i].SourceId ?? "";
            digestLines.Add(string.Join('|', action, shaKey, RequirementRules.ContentSha(shaKey, merged[i].Title, merged[i].Text, merged[i].Type, merged[i].Priority, merged[i].Method, merged[i].Rationale, merged[i].Status),
                rows[i].ParentRef ?? ""));
            plans[i] = new ImportRowPlan(rows[i].Line, rows[i].SourceId, action, keys[i], string.IsNullOrEmpty(merged[i].Title) ? null : merged[i].Title, problems[i]);
        }
        return new Planned(plans, ops, RequirementIo.Digest(digestLines));
    }
}

public sealed partial class RequirementService
{
    /// <summary>
    /// Reads a requirement file (csv, excel or reqif) against the specification. With <paramref name="dryRun"/> it only reports what each row would do and returns the digest of the plan;
    /// without, it applies the plan as one new iteration, but only when <paramref name="expectedDigest"/> equals the digest of the plan computed now (so what was reviewed is what is applied)
    /// and no row has a problem. Nothing is stored for a dry run. Rows naming a requirement the caller cannot read are reported exactly like rows naming one that does not exist.
    /// </summary>
    public Task<Outcome<ImportPlan>> ImportAsync(Subject subject, string number, string format, string content, bool dryRun, string? expectedDigest, string? correlationId, CancellationToken ct = default) =>
        MutateAsync<ImportPlan>(subject, number, dryRun ? "requirement.import_preview" : "requirement.import", correlationId, async (head, conn, tx) =>
        {
            if (!ImportFormats.All.Contains(format)) return Step<ImportPlan>.Invalid($"The format must be one of: {string.Join(", ", ImportFormats.All)}.");
            if (Encoding.UTF8.GetByteCount(content) > RequirementIo.MaxBytes) return Step<ImportPlan>.Invalid($"The file is larger than {RequirementIo.MaxBytes / (1024 * 1024)} MB.");
            if (!head.IsLatest || head.State != SpecStates.InWork) return Step<ImportPlan>.Invalid($"Only the latest revision of a specification that is In Work can be changed; {head.Number} {head.Revision} is {SpecStates.Label(head.State)}.");
            var parsed = format == ImportFormats.ReqIf ? RequirementIo.ParseReqIf(content) : RequirementIo.ParseCsv(content, format == ImportFormats.Excel);
            if (parsed.Errors.Count > 0) return Step<ImportPlan>.Invalid([.. parsed.Errors.Take(20)]);
            var raw = await ReadRowsAsync(conn, tx, subject, head.IterationId, ct);
            var existing = BuildRows(raw);
            var planned = ImportPlanner.Plan(parsed.Rows, existing, r => r.Markings is not null && AccessPolicy.Evaluate(subject, r.Markings, PlmAction.Modify).Permitted);
            int created = planned.Rows.Count(r => r.Action == "create"), updated = planned.Rows.Count(r => r.Action == "update"), unchanged = planned.Rows.Count(r => r.Action == "unchanged"), errors = planned.Rows.Count(r => r.Action == "error");
            var banner = Markings.AggregateBanner(existing.Where(r => r.Markings is not null).Select(r => r.Markings!).Append(head.Markings));
            var plan = new ImportPlan(format, planned.Rows, created, updated, unchanged, errors, planned.Digest, false, null, banner);
            var details = new (string, string?)[] { ("format", format), ("rows", Fmt(planned.Rows.Count)), ("created", Fmt(created)), ("updated", Fmt(updated)), ("unchanged", Fmt(unchanged)), ("errors", Fmt(errors)), ("digest", planned.Digest) };
            if (dryRun) return Step<ImportPlan>.Done(plan, details);
            if (errors > 0) return Step<ImportPlan>.Invalid([.. planned.Rows.Where(r => r.Problems.Count > 0).Take(20).Select(r => $"Line {Fmt(r.Line)}: {string.Join(" ", r.Problems)}")]);
            if (!string.Equals(expectedDigest, planned.Digest, StringComparison.Ordinal))
                return Step<ImportPlan>.Conflict("The file or the specification is not what the preview showed. Run the preview again and apply that.");
            if (planned.Ops.Count == 0) return Step<ImportPlan>.Invalid("Nothing to import: every row is unchanged.");
            var applied = await ApplyEditAsync(subject, head, conn, tx, new EditCommand(head.Iteration, $"Imported from a {ImportFormats.Extension(format)} file", planned.Ops), ct);
            if (applied.Kind != OutcomeKind.Ok) return new Step<ImportPlan>(applied.Kind, applied.Errors, default, applied.Details, applied.Denied);
            return Step<ImportPlan>.Done(plan with { Applied = true, Iteration = applied.Value!.Iteration }, [.. details, ("iteration", Fmt(applied.Value.Iteration)), ("suspectLinks", Fmt(applied.Value.SuspectLinks))]);
        }, ct);

    /// <summary>A requirement file of a revision's readable requirements (a restricted requirement is left out). Needs Download on the specification; carries the marking banner of what it holds.</summary>
    public Task<Outcome<ExportFile>> ExportAsync(Subject subject, string number, string? revision, string format, string? correlationId, CancellationToken ct = default) =>
        ReadAsync<ExportFile>(subject, number, revision, "requirement.export", correlationId, async (conn, tx, head) =>
        {
            if (!ImportFormats.All.Contains(format)) return (Outcome<ExportFile>.Invalid([$"The format must be one of: {string.Join(", ", ImportFormats.All)}."]), []);
            var rows = BuildRows(await ReadRowsAsync(conn, tx, subject, head.IterationId, ct));
            var banner = Markings.AggregateBanner(rows.Where(r => r.Markings is not null).Select(r => r.Markings!).Append(head.Markings));
            var text = format == ImportFormats.ReqIf ? RequirementIo.ExportReqIf(rows, head.Number, banner) : RequirementIo.ExportCsv(rows, banner, format == ImportFormats.Excel);
            var file = new ExportFile($"{head.Number}-{head.Revision}.{ImportFormats.Extension(format)}", ImportFormats.ContentType(format), new UTF8Encoding(false).GetBytes(text), banner, rows.Count(r => !r.Redacted));
            return (Outcome<ExportFile>.Ok(file), [("format", format), ("revision", head.Label), ("requirements", Fmt(file.Count)), ("banner", banner)]);
        }, ct, PlmAction.Download);
}
