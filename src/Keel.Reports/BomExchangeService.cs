using System.Globalization;
using System.Text;
using System.Text.Json;
using Keel.Audit;
using Keel.Core;
using Keel.Core.Reports;
using Keel.Reports.Tabular;
using Npgsql;

namespace Keel.Reports;

public sealed record ExportFile(string FileName, string ContentType, byte[] Content, string Banner, int Lines, int RedactedCount);

public sealed record ImportRow(int Row, string Action, string Parent, string Child, decimal? Quantity, string? Unit, int? FindNumber, int? CurrentFindNumber, IReadOnlyList<string> Designators, string? Notes, IReadOnlyList<string> Errors);

public sealed record ImportSummary(int Rows, int Adds, int Updates, int Removes, int ErrorRows, IReadOnlyList<string> Parents);

public sealed record ImportProposal(string Number, string ParentSummary, string FileName, string Status, ImportSummary Summary, IReadOnlyList<ImportRow> Rows, IReadOnlyList<string> Errors, DateTimeOffset CreatedAt, string CreatedBy, string? Result)
{
    public bool CanApply => Status == "draft" && Summary.ErrorRows == 0 && Errors.Count == 0 && Summary.Rows > 0;
}

/// <summary>
/// Excel and CSV BOM exchange (ADR-0044). Export is the read-filtered structure (<see cref="IBomExchangeSource"/>) with the aggregate marking on top and a markings column per
/// line; every text cell is neutralized against formula injection. Import never changes data directly: a workbook becomes a reviewable proposal (a dry run with row-level
/// errors), and applying it goes through <see cref="ICadPartPort"/>, i.e. check-out, BOM working-copy rules and check-in, so release locks, iterations and audit are the normal ones.
/// </summary>
public sealed class BomExchangeService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, IBomExchangeSource source, ICadPartPort parts)
{
    public static readonly IReadOnlyList<string> TemplateHeader = ["Action", "Parent", "Child", "Quantity", "Unit", "Find number", "Reference designators", "Notes", "Current find number"];
    public static readonly IReadOnlyList<string> ExportHeader = ["Level", "Find number", "Part number", "Name", "Revision", "State", "Quantity", "Unit", "Reference designators", "Effectivity", "Option condition", "Notes", "Markings"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---------------------------------------------------------------- export

    public async Task<Outcome<ExportFile>> ExportAsync(Subject subject, BomExchangeRequest request, string format, string? correlationId, CancellationToken ct = default)
    {
        format = (format ?? "xlsx").ToLowerInvariant();
        if (format is not ("xlsx" or "csv")) return Outcome<ExportFile>.Invalid(["Format must be xlsx or csv."]);
        var resolved = await source.ResolveAsync(subject, request, correlationId, ct);
        if (!resolved.IsOk) return new Outcome<ExportFile>(resolved.Kind, null, resolved.Errors);
        var r = resolved.Value!;
        if (r.Lines.Count > ReportsLimits.MaxBomExportLines) return Outcome<ExportFile>.Invalid([$"The structure has more than {ReportsLimits.MaxBomExportLines} lines. Export it in parts (a depth limit or a find-number path)."]);
        var rows = r.Lines.Select(l => (IReadOnlyList<Cell>)
        [
            Cell.N(l.Level), Cell.N(l.FindNumber), l.Redacted ? "(restricted)" : l.Number, l.Redacted ? "" : l.Name, l.Revision, l.State, Cell.N(l.Quantity), l.Unit,
            string.Join(", ", l.Designators), l.Effectivity, l.Redacted ? "" : l.Condition, l.Redacted ? "" : l.Notes, l.Redacted ? "(restricted)" : l.Banner,
        ]).ToList();
        var preface = new List<string>
        {
            r.Banner,
            $"{(r.Kind == BomExchangeKinds.Mbom ? "MBOM" : "EBOM")} {r.RootNumber}: {r.Description}",
            $"Exported {clock.GetUtcNow().UtcDateTime:yyyy-MM-dd HH:mm} UTC by {subject.DisplayName}. {r.RedactedCount} line(s) not readable by the exporter appear as restricted placeholders.",
        };
        var stem = $"{r.Kind}-{Safe(r.RootNumber)}";
        byte[] bytes; string type;
        if (format == "csv") { bytes = Csv.Write(ExportHeader, rows, preface); type = "text/csv; charset=utf-8"; }
        else
        {
            bytes = XlsxWriter.Write([new SheetData("BOM", ExportHeader, rows, preface)]);
            type = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        }
        await audit.AppendAsync(ReportsAudit.Entry(subject, "output.bom.export", AuditOutcome.Success, "part", r.RootNumber, correlationId,
            ("kind", r.Kind), ("format", format), ("lines", r.Lines.Count.ToString(CultureInfo.InvariantCulture)), ("redacted", r.RedactedCount.ToString(CultureInfo.InvariantCulture)), ("banner", r.Banner)), ct);
        return Outcome<ExportFile>.Ok(new ExportFile($"{stem}.{format}", type, bytes, r.Banner, r.Lines.Count, r.RedactedCount));
    }

    /// <summary>The template workbook: a header, one example row and an instruction sheet.</summary>
    public static byte[] Template() => XlsxWriter.Write(
    [
        new SheetData("BOM changes", TemplateHeader, [[ "add", "P-000001", "P-000002", Cell.N(2), "EA", Cell.N(10), "R1, R2", "Example row: delete it", ""]]),
        new SheetData("Instructions", ["Column", "Meaning"],
        [
            ["Action", "add, update or remove."],
            ["Parent", "The assembly whose structure changes. Its latest iteration is changed; a released assembly is refused (change it through an ECN)."],
            ["Child", "The part number of the child."],
            ["Quantity", "Required for add and update."],
            ["Unit", "Optional; the child's unit when empty."],
            ["Find number", "Optional for add (next free), the new find number for update."],
            ["Current find number", "For update and remove, when the child is used on several lines."],
            ["Reference designators", "Comma separated."],
            ["Notes", "Free text. A leading = + - or @ is kept as text."],
        ]),
    ]);

    // ---------------------------------------------------------------- import: dry run

    public async Task<Outcome<ImportProposal>> CreateProposalAsync(Subject subject, string fileName, byte[] content, string? correlationId, CancellationToken ct = default)
    {
        fileName = Path.GetFileName(fileName ?? "bom-changes.xlsx");
        IReadOnlyList<IReadOnlyList<string>> table;
        try
        {
            table = fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? ReadCsv(content) : XlsxReader.ReadFirstSheet(content);
        }
        catch (XlsxFormatException e)
        {
            await audit.AppendAsync(ReportsAudit.Entry(subject, "output.bom.import_proposal", AuditOutcome.Failure, "output", null, correlationId, ("reason", "format")), ct);
            return Outcome<ImportProposal>.Invalid([e.Message]);
        }
        var parsed = Parse(table);
        if (parsed.Errors.Count > 0 && parsed.Rows.Count == 0) return Outcome<ImportProposal>.Invalid(parsed.Errors);
        var rows = await ValidateAsync(subject, parsed.Rows, ct);
        var parents = rows.Select(r => r.Parent).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var summary = new ImportSummary(rows.Count, rows.Count(r => r.Action == "add"), rows.Count(r => r.Action == "update"), rows.Count(r => r.Action == "remove"), rows.Count(r => r.Errors.Count > 0), parents);
        var id = Guid.CreateVersion7();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        long seq;
        await using (var s = new NpgsqlCommand("SELECT nextval('rpt.import_number_seq')", conn, tx)) seq = (long)(await s.ExecuteScalarAsync(ct))!;
        var number = "BIP-" + seq.ToString("D6", CultureInfo.InvariantCulture);
        var now = clock.GetUtcNow();
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO rpt.bom_import (id, number, parent_number, file_name, status, rows, errors, summary, created_at, created_by, created_by_sub) VALUES (@id, @n, @p, @f, 'draft', @rows::jsonb, @err::jsonb, @sum::jsonb, @at, @by, @sub)", conn, tx))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("n", number);
            ins.Parameters.AddWithValue("p", string.Join(", ", parents.Take(5)) + (parents.Count > 5 ? $" and {parents.Count - 5} more" : ""));
            ins.Parameters.AddWithValue("f", fileName.Length > 200 ? fileName[..200] : fileName);
            ins.Parameters.AddWithValue("rows", JsonSerializer.Serialize(rows, Json));
            ins.Parameters.AddWithValue("err", JsonSerializer.Serialize(parsed.Errors, Json));
            ins.Parameters.AddWithValue("sum", JsonSerializer.Serialize(summary, Json));
            ins.Parameters.AddWithValue("at", now);
            ins.Parameters.AddWithValue("by", subject.Username ?? subject.DisplayName);
            ins.Parameters.AddWithValue("sub", subject.Id);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, ReportsAudit.Entry(subject, "output.bom.import_proposal", AuditOutcome.Success, "output", number, correlationId,
            ("rows", rows.Count.ToString(CultureInfo.InvariantCulture)), ("error_rows", summary.ErrorRows.ToString(CultureInfo.InvariantCulture)), ("parents", parents.Count.ToString(CultureInfo.InvariantCulture))), ct);
        await tx.CommitAsync(ct);
        return Outcome<ImportProposal>.Ok(new ImportProposal(number, string.Join(", ", parents.Take(5)), fileName, "draft", summary, rows, parsed.Errors, now, subject.Username ?? subject.DisplayName, null));
    }

    internal sealed record Parsed(IReadOnlyList<ImportRow> Rows, IReadOnlyList<string> Errors);

    internal static Parsed Parse(IReadOnlyList<IReadOnlyList<string>> table)
    {
        var errors = new List<string>();
        var headerIndex = -1;
        for (var i = 0; i < Math.Min(table.Count, 30); i++)
            if (table[i].Count > 0 && string.Equals(table[i][0].Trim(), "Action", StringComparison.OrdinalIgnoreCase)) { headerIndex = i; break; }
        if (headerIndex < 0) return new Parsed([], ["The header row was not found. Start from the template: the first column of the header row is 'Action'."]);
        var header = table[headerIndex].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => header.IndexOf(name);
        var cAction = Col("action"); var cParent = Col("parent"); var cChild = Col("child"); var cQty = Col("quantity"); var cUnit = Col("unit");
        var cFind = Col("find number"); var cDes = Col("reference designators"); var cNotes = Col("notes"); var cCur = Col("current find number");
        if (cParent < 0 || cChild < 0) return new Parsed([], ["The header needs at least the columns Action, Parent and Child."]);
        string Get(IReadOnlyList<string> r, int c) => c >= 0 && c < r.Count ? r[c].Trim() : "";
        var rows = new List<ImportRow>();
        for (var i = headerIndex + 1; i < table.Count; i++)
        {
            var r = table[i];
            if (r.All(string.IsNullOrWhiteSpace)) continue;
            if (rows.Count >= ReportsLimits.MaxImportRows) { errors.Add($"More than {ReportsLimits.MaxImportRows} rows. Split the file."); break; }
            var e = new List<string>();
            var action = Get(r, cAction).ToLowerInvariant();
            if (action is not ("add" or "update" or "remove")) { e.Add("Action must be add, update or remove."); }
            var parent = Get(r, cParent); var child = Get(r, cChild);
            if (parent.Length is 0 or > 64) e.Add("Parent is required (at most 64 characters).");
            if (child.Length is 0 or > 64) e.Add("Child is required (at most 64 characters).");
            decimal? qty = null;
            if (action != "remove")
            {
                if (!decimal.TryParse(Get(r, cQty), NumberStyles.Number, CultureInfo.InvariantCulture, out var q) || q <= 0 || q > 1_000_000m) e.Add("Quantity must be a number above 0 and at most 1,000,000.");
                else qty = q;
            }
            int? find = null, cur = null;
            if (Get(r, cFind).Length > 0) { if (int.TryParse(Get(r, cFind), NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 999_999) find = f; else e.Add("Find number must be a whole number from 1 to 999999."); }
            if (Get(r, cCur).Length > 0) { if (int.TryParse(Get(r, cCur), NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 999_999) cur = f; else e.Add("Current find number must be a whole number from 1 to 999999."); }
            var designators = Get(r, cDes).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
            if (designators.Any(d => d.Length > 40)) e.Add("A reference designator is longer than 40 characters.");
            var notes = Get(r, cNotes); if (notes.Length > 500) e.Add("Notes are limited to 500 characters.");
            rows.Add(new ImportRow(i + 1, action, parent, child, qty, Get(r, cUnit) is { Length: > 0 } u ? u : null, find, cur, designators, notes.Length == 0 ? null : notes, e));
        }
        if (rows.Count == 0) errors.Add("The file has no data rows.");
        return new Parsed(rows, errors);
    }

    private async Task<List<ImportRow>> ValidateAsync(Subject subject, IReadOnlyList<ImportRow> rows, CancellationToken ct)
    {
        var numbers = rows.SelectMany(r => new[] { r.Parent, r.Child }).Where(n => n.Length is > 0 and <= 64).Distinct(StringComparer.Ordinal).ToList();
        var found = (await parts.FindManyAsync(subject, numbers, ct)).ToDictionary(p => p.Number, StringComparer.Ordinal);
        var boms = new Dictionary<string, CadBomSnapshot?>(StringComparer.Ordinal);
        var result = new List<ImportRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var errors = row.Errors.ToList();
            // A part the caller may not read looks exactly like one that does not exist.
            if (row.Parent.Length > 0 && !found.TryGetValue(row.Parent, out var parent)) { errors.Add($"Parent {row.Parent} was not found."); parent = null; }
            else parent = row.Parent.Length > 0 ? found[row.Parent] : null;
            if (row.Child.Length > 0 && !found.ContainsKey(row.Child) && row.Action != "remove") errors.Add($"Child {row.Child} was not found.");
            if (parent is not null)
            {
                if (parent.PartType != "Assembly") errors.Add($"{parent.Number} is not an assembly.");
                if (parent.IsLocked) errors.Add($"{parent.Number} revision {parent.Revision} is {parent.State} and locked. Change it through an ECN.");
                if (row.Action is "update" or "remove")
                {
                    if (!boms.TryGetValue(parent.Number, out var snap)) { var b = await parts.GetBomAsync(subject, parent.Number, null, ct); boms[parent.Number] = snap = b.IsOk ? b.Value : null; }
                    if (snap is null) errors.Add($"The structure of {parent.Number} cannot be read.");
                    else
                    {
                        var matches = snap.Lines.Count(l => l.ChildNumber == row.Child && (row.CurrentFindNumber is null || l.FindNumber == row.CurrentFindNumber));
                        if (matches == 0) errors.Add($"{parent.Number} has no line for {row.Child}{(row.CurrentFindNumber is null ? "" : $" at find number {row.CurrentFindNumber}")}.");
                        else if (matches > 1) errors.Add($"{row.Child} is used on several lines of {parent.Number}; give the current find number.");
                    }
                }
            }
            if (!seen.Add($"{row.Action}|{row.Parent}|{row.Child}|{row.CurrentFindNumber}|{row.FindNumber}")) errors.Add("This row repeats an earlier row.");
            result.Add(row with { Errors = errors });
        }
        return result;
    }

    private static List<IReadOnlyList<string>> ReadCsv(byte[] content)
    {
        if (content.Length > 5 * 1024 * 1024) throw new XlsxFormatException("The file is larger than 5 MB.");
        var text = Encoding.UTF8.GetString(content).TrimStart('﻿');
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>(); var sb = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted) { if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; } else quoted = false; } else sb.Append(ch); continue; }
            switch (ch)
            {
                case '"': quoted = true; break;
                case ',': row.Add(CellSafety.Restore(sb.ToString())); sb.Clear(); break;
                case '\r': break;
                case '\n': row.Add(CellSafety.Restore(sb.ToString())); sb.Clear(); rows.Add(row); row = []; if (rows.Count > 21_000) throw new XlsxFormatException("The file has too many rows."); break;
                default: sb.Append(ch); break;
            }
        }
        if (sb.Length > 0 || row.Count > 0) { row.Add(CellSafety.Restore(sb.ToString())); rows.Add(row); }
        return rows;
    }

    // ---------------------------------------------------------------- import: read back, apply

    private async Task<(ImportProposal? P, Guid Id, string Sub)> LoadAsync(string number, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id, parent_number, file_name, status, rows::text, errors::text, summary::text, result::text, created_at, created_by, created_by_sub, number FROM rpt.bom_import WHERE number = @n", conn);
        cmd.Parameters.AddWithValue("n", number ?? "");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (null, Guid.Empty, "");
        var p = new ImportProposal(r.GetString(11), r.GetString(1), r.GetString(2), r.GetString(3), JsonSerializer.Deserialize<ImportSummary>(r.GetString(6), Json)!,
            JsonSerializer.Deserialize<List<ImportRow>>(r.GetString(4), Json)!, JsonSerializer.Deserialize<List<string>>(r.GetString(5), Json)!, r.GetFieldValue<DateTimeOffset>(8), r.GetString(9),
            r.IsDBNull(7) ? null : r.GetString(7));
        return (p, r.GetGuid(0), r.GetString(10));
    }

    /// <summary>A proposal belongs to its creator; anyone else gets the answer for an unknown number.</summary>
    public async Task<Outcome<ImportProposal>> GetProposalAsync(Subject subject, string number, CancellationToken ct = default)
    {
        var (p, _, sub) = await LoadAsync(number, ct);
        return p is null || sub != subject.Id ? Outcome<ImportProposal>.NotFound() : Outcome<ImportProposal>.Ok(p);
    }

    public async Task<IReadOnlyList<ImportProposal>> ListProposalsAsync(Subject subject, CancellationToken ct = default)
    {
        var numbers = new List<string>();
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var cmd = new NpgsqlCommand("SELECT number FROM rpt.bom_import WHERE created_by_sub = @s ORDER BY created_at DESC LIMIT 50", conn))
        {
            cmd.Parameters.AddWithValue("s", subject.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) numbers.Add(r.GetString(0));
        }
        var list = new List<ImportProposal>();
        foreach (var n in numbers) if ((await LoadAsync(n, ct)).P is { } p) list.Add(p);
        return list;
    }

    public async Task<Outcome<ImportProposal>> ApplyAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        var (p, id, sub) = await LoadAsync(number, ct);
        if (p is null || sub != subject.Id) return Outcome<ImportProposal>.NotFound();
        if (p.Status != "draft") return Outcome<ImportProposal>.Conflict($"The proposal is {p.Status}.");
        if (!p.CanApply) return Outcome<ImportProposal>.Invalid(["The proposal has errors. Fix the workbook and upload it again."]);
        // The structure may have changed since the dry run: check every row again before anything is applied.
        var again = await ValidateAsync(subject, p.Rows, ct);
        if (again.Any(r => r.Errors.Count > 0))
            return Outcome<ImportProposal>.Conflict("The structure changed since the dry run and some rows no longer apply. Upload the workbook again.");
        var done = p.Result is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(p.Result, Json) ?? [];
        var failures = new List<string>();
        foreach (var group in p.Rows.GroupBy(r => r.Parent, StringComparer.Ordinal))
        {
            if (done.TryGetValue(group.Key, out var prior) && prior.StartsWith("applied", StringComparison.Ordinal)) continue; // idempotent retry
            var edits = group.Select(r => new CadBomEdit(r.Action switch { "add" => CadBomEditKind.Add, "update" => CadBomEditKind.Update, _ => CadBomEditKind.Remove },
                r.Child, r.Quantity ?? 0, r.Unit ?? "", r.FindNumber, r.Designators, r.Notes, r.CurrentFindNumber)).ToList();
            var applied = await parts.ApplyBomAsync(subject, group.Key, edits, $"BOM import {number}", correlationId, ct);
            if (applied.IsOk) done[group.Key] = "applied " + applied.Value;
            else { done[group.Key] = "failed: " + string.Join(" ", applied.Errors); failures.Add(group.Key); }
        }
        var allDone = failures.Count == 0;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var upd = new NpgsqlCommand("UPDATE rpt.bom_import SET status = @s, result = @r::jsonb, decided_at = CASE WHEN @s = 'applied' THEN @at ELSE NULL END WHERE id = @id", conn, tx))
        {
            upd.Parameters.AddWithValue("s", allDone ? "applied" : "draft");
            upd.Parameters.AddWithValue("r", JsonSerializer.Serialize(done, Json));
            upd.Parameters.AddWithValue("at", clock.GetUtcNow());
            upd.Parameters.AddWithValue("id", id);
            await upd.ExecuteNonQueryAsync(ct);
        }
        await audit.AppendAsync(tx, ReportsAudit.Entry(subject, "output.bom.import_apply", allDone ? AuditOutcome.Success : AuditOutcome.Failure, "output", number, correlationId,
            ("parents", done.Count.ToString(CultureInfo.InvariantCulture)), ("failed", string.Join(",", failures))), ct);
        await tx.CommitAsync(ct);
        var reloaded = (await LoadAsync(number, ct)).P!;
        return allDone ? Outcome<ImportProposal>.Ok(reloaded) : new Outcome<ImportProposal>(OutcomeKind.Conflict, reloaded, [$"Some assemblies were not changed: {string.Join(", ", failures)}. Applied ones stay applied; apply again to retry the rest."]);
    }

    public async Task<Outcome<ImportProposal>> DiscardAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default)
    {
        var (p, id, sub) = await LoadAsync(number, ct);
        if (p is null || sub != subject.Id) return Outcome<ImportProposal>.NotFound();
        if (p.Status != "draft") return Outcome<ImportProposal>.Conflict($"The proposal is {p.Status}.");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var upd = new NpgsqlCommand("UPDATE rpt.bom_import SET status = 'discarded', decided_at = @at WHERE id = @id", conn, tx))
        { upd.Parameters.AddWithValue("at", clock.GetUtcNow()); upd.Parameters.AddWithValue("id", id); await upd.ExecuteNonQueryAsync(ct); }
        await audit.AppendAsync(tx, ReportsAudit.Entry(subject, "output.bom.import_discard", AuditOutcome.Success, "output", number, correlationId), ct);
        await tx.CommitAsync(ct);
        return Outcome<ImportProposal>.Ok(p with { Status = "discarded" });
    }

    private static string Safe(string s) => new([.. s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
}
