using System.Globalization;
using System.Text.Json;
using Keel.Audit;
using Keel.Core;
using Keel.Core.Reports;
using Keel.Reports.Pdf;
using Keel.Reports.Tabular;
using Npgsql;

namespace Keel.Reports;

public sealed record ReportSpec(IReadOnlyList<string> Columns, IReadOnlyList<DatasetFilter>? Filters = null, string? Text = null, string? Parameter = null, string? SortColumn = null, bool Descending = false, string? GroupBy = null);

public sealed record ReportDefinition(Guid Id, string Name, string Dataset, ReportSpec Spec, string OwnerName, bool Shared, bool Mine, DateTimeOffset UpdatedAt);

public sealed record ReportRow(IReadOnlyList<string?> Cells, string? Banner);

public sealed record ReportGroup(string Value, int Count);

public sealed record ReportResult(string Dataset, string Title, IReadOnlyList<string> ColumnLabels, IReadOnlyList<string> ColumnTypes, IReadOnlyList<ReportRow> Rows, IReadOnlyList<ReportGroup> Groups,
    int RowCount, bool Truncated, string Banner, IReadOnlyList<string> Notices);

public sealed record DatasetInfo(string Id, string Title, string Description, string? ParameterLabel, IReadOnlyList<DatasetColumn> Columns);

/// <summary>
/// The report builder (ADR-0044). A definition is data (a dataset from a fixed catalog, columns, filters, sort, grouping); running it asks the dataset for the rows the
/// caller may read, through the owning module's access-filtered query, and only then filters, sorts, groups and counts those rows. No user text reaches SQL, and no step can add
/// a row the query did not return. The result carries the aggregate banner of the rows it shows. Scheduling is out of scope (docs/increments/8.9-output-bulk.md).
/// </summary>
public sealed class ReportBuilderService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, IEnumerable<IReportDataset> datasets, Func<Markings, IReadOnlyList<string>> notices, Action ensureFonts)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, IReportDataset> catalog = datasets.ToDictionary(d => d.Id, StringComparer.Ordinal);

    public IReadOnlyList<DatasetInfo> Catalog() => [.. catalog.Values.OrderBy(d => d.Title, StringComparer.Ordinal).Select(d => new DatasetInfo(d.Id, d.Title, d.Description, d.ParameterLabel, d.Columns))];

    // ---------------------------------------------------------------- validation

    public IReadOnlyList<string> Validate(string datasetId, ReportSpec spec)
    {
        var errors = new List<string>();
        if (!catalog.TryGetValue(datasetId ?? "", out var ds)) return ["Choose one of the datasets offered."];
        var cols = ds.Columns.ToDictionary(c => c.Key, StringComparer.Ordinal);
        if (spec.Columns is null || spec.Columns.Count == 0) errors.Add("Choose at least one column.");
        else
        {
            if (spec.Columns.Count > 30) errors.Add("Choose at most 30 columns.");
            foreach (var c in spec.Columns.Where(c => !cols.ContainsKey(c))) errors.Add($"'{c}' is not a column of this dataset.");
            if (spec.Columns.Distinct(StringComparer.Ordinal).Count() != spec.Columns.Count) errors.Add("A column is chosen twice.");
        }
        var filters = spec.Filters ?? [];
        if (filters.Count > ReportsLimits.MaxFilters) errors.Add($"Use at most {ReportsLimits.MaxFilters} filters.");
        foreach (var f in filters)
        {
            if (!cols.TryGetValue(f.Column ?? "", out var col) || !col.Filterable) errors.Add($"Cannot filter by '{f.Column}'.");
            if (!FilterOps.All.Contains(f.Op ?? "")) errors.Add($"Filter operator '{f.Op}' is not offered.");
            if ((f.Value ?? "").Length > 200) errors.Add("A filter value is limited to 200 characters.");
            else if (col is not null && f.Op is FilterOps.AtLeast or FilterOps.AtMost && col.Type == "number" && !decimal.TryParse(f.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) errors.Add($"'{f.Value}' is not a number.");
        }
        if ((spec.Text ?? "").Length > 200) errors.Add("The search text is limited to 200 characters.");
        if (spec.SortColumn is not null && !SortMapFor(ds).TryResolve(spec.SortColumn, spec.Descending ? "desc" : "asc", out _, out var sortError)) errors.Add(sortError!);
        if (spec.GroupBy is not null && (!cols.TryGetValue(spec.GroupBy, out var g) || !g.Groupable)) errors.Add($"Cannot group by '{spec.GroupBy}'.");
        if (ds.ParameterLabel is not null && string.IsNullOrWhiteSpace(spec.Parameter)) errors.Add($"{ds.ParameterLabel} is required for this dataset.");
        if ((spec.Parameter ?? "").Length > 64) errors.Add("The dataset input is limited to 64 characters.");
        return errors;
    }

    /// <summary>The sort keys a dataset accepts: its sortable columns, looked up and never used as SQL text (the same whitelist type every list uses).</summary>
    public static SortMap SortMapFor(IReportDataset ds) => new([.. ds.Columns.Where(c => c.Sortable).Select(c => (c.Key, new[] { c.Key }))]);

    // ---------------------------------------------------------------- run

    public async Task<Outcome<ReportResult>> RunAsync(Subject subject, string datasetId, ReportSpec spec, string? correlationId, CancellationToken ct = default)
    {
        var errors = Validate(datasetId, spec);
        if (errors.Count > 0) return Outcome<ReportResult>.Invalid(errors);
        var ds = catalog[datasetId];
        var queried = await ds.QueryAsync(subject, new DatasetQuery(string.IsNullOrWhiteSpace(spec.Text) ? null : spec.Text.Trim(), spec.Parameter?.Trim(), ReportsLimits.MaxReportRows), correlationId, ct);
        if (!queried.IsOk)
        {
            if (queried.Kind == OutcomeKind.Forbidden)
                await audit.AppendAsync(ReportsAudit.Entry(subject, "output.report.run", AuditOutcome.Denied, "output", datasetId, correlationId, ("reasons", string.Join(",", queried.Errors))), ct);
            return new Outcome<ReportResult>(queried.Kind, null, queried.Errors);
        }
        var index = ds.Columns.Select((c, i) => (c.Key, i)).ToDictionary(x => x.Key, x => x.i, StringComparer.Ordinal);
        IEnumerable<DatasetRow> rows = queried.Value!.Rows;
        foreach (var f in spec.Filters ?? []) { var col = ds.Columns[index[f.Column]]; var i = index[f.Column]; rows = rows.Where(r => Matches(r.Cells[i], f, col.Type)); }
        var list = rows.ToList();
        if (spec.SortColumn is not null)
        {
            var i = index[spec.SortColumn]; var numeric = ds.Columns[i].Type == "number";
            Comparison<DatasetRow> cmp = (a, b) => numeric ? CompareNumber(a.Cells[i], b.Cells[i]) : string.Compare(a.Cells[i], b.Cells[i], StringComparison.OrdinalIgnoreCase);
            var sorted = list.Select((r, n) => (r, n)).ToList();
            sorted.Sort((x, y) => { var c = (x.r.Cells[i] is null ? 1 : 0) - (y.r.Cells[i] is null ? 1 : 0); if (c != 0) return c; c = cmp(x.r, y.r); if (spec.Descending) c = -c; return c != 0 ? c : x.n.CompareTo(y.n); });
            list = [.. sorted.Select(x => x.r)];
        }
        var groups = spec.GroupBy is null ? [] : list.GroupBy(r => r.Cells[index[spec.GroupBy]] ?? "(none)").Select(g => new ReportGroup(g.Key, g.Count())).OrderByDescending(g => g.Count).ThenBy(g => g.Value, StringComparer.OrdinalIgnoreCase).ToList();
        var marks = list.Where(r => r.Markings is not null).Select(r => r.Markings!).ToList();
        var banner = marks.Count == 0 ? "UNCLASSIFIED" : Markings.AggregateBanner(marks);
        var projected = list.Select(r => new ReportRow([.. spec.Columns.Select(c => r.Cells[index[c]])], r.Markings?.BannerText())).ToList();
        await audit.AppendAsync(ReportsAudit.Entry(subject, "output.report.run", AuditOutcome.Success, "output", datasetId, correlationId,
            ("dataset", datasetId), ("rows", projected.Count.ToString(CultureInfo.InvariantCulture)), ("filters", (spec.Filters?.Count ?? 0).ToString(CultureInfo.InvariantCulture)), ("banner", banner)), ct);
        return Outcome<ReportResult>.Ok(new ReportResult(datasetId, ds.Title, [.. spec.Columns.Select(c => ds.Columns[index[c]].Label)], [.. spec.Columns.Select(c => ds.Columns[index[c]].Type)],
            projected, groups, projected.Count, queried.Value.Truncated, banner, marks.Count == 0 ? [] : DistinctNotices(marks)));
    }

    private IReadOnlyList<string> DistinctNotices(List<Markings> marks) => PackageMarking.For(marks, notices).Notices;

    private static bool Matches(string? cell, DatasetFilter f, string type)
    {
        var c = cell ?? "";
        switch (f.Op)
        {
            case FilterOps.Eq: return string.Equals(c, f.Value, StringComparison.OrdinalIgnoreCase);
            case FilterOps.Contains: return c.Contains(f.Value, StringComparison.OrdinalIgnoreCase);
            case FilterOps.StartsWith: return c.StartsWith(f.Value, StringComparison.OrdinalIgnoreCase);
            default:
                var cmp = type == "number" ? CompareNumber(cell, f.Value) : string.CompareOrdinal(c, f.Value);
                if (cell is null) return false;
                return f.Op == FilterOps.AtLeast ? cmp >= 0 : cmp <= 0;
        }
    }

    private static int CompareNumber(string? a, string? b)
    {
        var x = decimal.TryParse(a, NumberStyles.Number, CultureInfo.InvariantCulture, out var da) ? da : decimal.MinValue;
        var y = decimal.TryParse(b, NumberStyles.Number, CultureInfo.InvariantCulture, out var db) ? db : decimal.MinValue;
        return x.CompareTo(y);
    }

    // ---------------------------------------------------------------- export

    public async Task<Outcome<ExportFile>> ExportAsync(Subject subject, string datasetId, ReportSpec spec, string format, string? correlationId, CancellationToken ct = default)
    {
        format = (format ?? "csv").ToLowerInvariant();
        if (format is not ("csv" or "xlsx" or "pdf")) return Outcome<ExportFile>.Invalid(["Format must be csv, xlsx or pdf."]);
        var run = await RunAsync(subject, datasetId, spec, correlationId, ct);
        if (!run.IsOk) return new Outcome<ExportFile>(run.Kind, null, run.Errors);
        var r = run.Value!;
        var now = clock.GetUtcNow();
        var header = new List<string>(r.ColumnLabels) { "Markings" };
        var rows = r.Rows.Select(x => (IReadOnlyList<Cell>)[.. x.Cells.Select((c, i) => r.ColumnTypes[i] == "number" && decimal.TryParse(c, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? Cell.N(n) : Cell.T(c)), Cell.T(x.Banner)]).ToList();
        var preface = new List<string> { r.Banner, $"{r.Title}: {r.RowCount} row(s){(r.Truncated ? $", limited to the first {ReportsLimits.MaxReportRows}" : "")}. Exported {now.UtcDateTime:yyyy-MM-dd HH:mm} UTC by {subject.DisplayName}." };
        byte[] bytes; string type;
        switch (format)
        {
            case "csv": bytes = Csv.Write(header, rows, preface); type = "text/csv; charset=utf-8"; break;
            case "xlsx": bytes = XlsxWriter.Write([new SheetData("Report", header, rows, preface)]); type = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"; break;
            default:
                var n = header.Count; var w = 540.0 / n;
                var marking = r.Notices.Count == 0 && r.Banner == "UNCLASSIFIED" ? new PackageMarking("UNCLASSIFIED", [], []) : new PackageMarking(r.Banner, r.Notices, []);
                bytes = ReleasePackagePdf.RenderTable(r.Title, preface[1], [.. header.Select(h => (h, w, false))], [.. r.Rows.Select(x => (IReadOnlyList<string>)[.. x.Cells.Select(c => c ?? ""), x.Banner ?? ""])],
                    marking, new PackageIdentity("RPT-" + now.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture), now, subject.DisplayName), ensureFonts);
                type = "application/pdf"; break;
        }
        await audit.AppendAsync(ReportsAudit.Entry(subject, "output.report.export", AuditOutcome.Success, "output", datasetId, correlationId, ("dataset", datasetId), ("format", format), ("rows", r.RowCount.ToString(CultureInfo.InvariantCulture)), ("banner", r.Banner)), ct);
        return Outcome<ExportFile>.Ok(new ExportFile($"report-{datasetId}.{format}", type, bytes, r.Banner, r.RowCount, 0));
    }

    // ---------------------------------------------------------------- definitions

    public async Task<Outcome<ReportDefinition>> SaveAsync(Subject subject, Guid? id, string name, string datasetId, ReportSpec spec, bool shared, string? correlationId, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        var errors = Validate(datasetId, spec).ToList();
        if (name.Length is 0 or > 120) errors.Add("Give the report a name (at most 120 characters).");
        if (errors.Count > 0) return Outcome<ReportDefinition>.Invalid(errors);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var specJson = JsonSerializer.Serialize(spec, Json);
        try
        {
            if (id is null)
            {
                await using (var count = new NpgsqlCommand("SELECT count(*) FROM rpt.definition WHERE owner_sub = @o", conn, tx))
                {
                    count.Parameters.AddWithValue("o", subject.Id);
                    if ((long)(await count.ExecuteScalarAsync(ct))! >= ReportsLimits.MaxDefinitionsPerUser) return Outcome<ReportDefinition>.Conflict($"You can keep {ReportsLimits.MaxDefinitionsPerUser} saved reports. Delete one first.");
                }
                id = Guid.CreateVersion7();
                await using var ins = new NpgsqlCommand("INSERT INTO rpt.definition (id, name, dataset, spec, owner_sub, owner_name, visible_to_all, created_at, updated_at) VALUES (@id, @n, @d, @s::jsonb, @o, @on, @sh, @at, @at)", conn, tx);
                Bind(ins, id.Value, name, datasetId, specJson, subject, shared, now);
                await ins.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var upd = new NpgsqlCommand("UPDATE rpt.definition SET name = @n, dataset = @d, spec = @s::jsonb, visible_to_all = @sh, updated_at = @at WHERE id = @id AND owner_sub = @o", conn, tx);
                Bind(upd, id.Value, name, datasetId, specJson, subject, shared, now);
                if (await upd.ExecuteNonQueryAsync(ct) == 0) return Outcome<ReportDefinition>.NotFound();
            }
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return Outcome<ReportDefinition>.Conflict("You already have a saved report with that name."); }
        await audit.AppendAsync(tx, ReportsAudit.Entry(subject, "output.report.save", AuditOutcome.Success, "output", id.Value.ToString(), correlationId, ("dataset", datasetId), ("shared", shared ? "yes" : "no")), ct);
        await tx.CommitAsync(ct);
        return Outcome<ReportDefinition>.Ok(new ReportDefinition(id.Value, name, datasetId, spec, subject.DisplayName, shared, true, now));
    }

    private static void Bind(NpgsqlCommand c, Guid id, string name, string dataset, string spec, Subject s, bool shared, DateTimeOffset at)
    {
        c.Parameters.AddWithValue("id", id); c.Parameters.AddWithValue("n", name); c.Parameters.AddWithValue("d", dataset); c.Parameters.AddWithValue("s", spec);
        c.Parameters.AddWithValue("o", s.Id); c.Parameters.AddWithValue("on", s.DisplayName); c.Parameters.AddWithValue("sh", shared); c.Parameters.AddWithValue("at", at);
    }

    public async Task<IReadOnlyList<ReportDefinition>> ListAsync(Subject subject, CancellationToken ct = default)
    {
        var list = new List<ReportDefinition>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id, name, dataset, spec::text, owner_name, visible_to_all, owner_sub, updated_at FROM rpt.definition WHERE owner_sub = @o OR visible_to_all ORDER BY lower(name) LIMIT 300", conn);
        cmd.Parameters.AddWithValue("o", subject.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (catalog.ContainsKey(r.GetString(2))) list.Add(new ReportDefinition(r.GetGuid(0), r.GetString(1), r.GetString(2), JsonSerializer.Deserialize<ReportSpec>(r.GetString(3), Json)!, r.GetString(4), r.GetBoolean(5), r.GetString(6) == subject.Id, r.GetFieldValue<DateTimeOffset>(7)));
        return list;
    }

    public async Task<Outcome<ReportDefinition>> GetAsync(Subject subject, Guid id, CancellationToken ct = default)
    {
        var d = (await ListAsync(subject, ct)).FirstOrDefault(x => x.Id == id);
        return d is null ? Outcome<ReportDefinition>.NotFound() : Outcome<ReportDefinition>.Ok(d);
    }

    public async Task<Outcome<bool>> DeleteAsync(Subject subject, Guid id, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var del = new NpgsqlCommand("DELETE FROM rpt.definition WHERE id = @id AND owner_sub = @o", conn, tx);
        del.Parameters.AddWithValue("id", id); del.Parameters.AddWithValue("o", subject.Id);
        if (await del.ExecuteNonQueryAsync(ct) == 0) return Outcome<bool>.NotFound();
        await audit.AppendAsync(tx, ReportsAudit.Entry(subject, "output.report.delete", AuditOutcome.Success, "output", id.ToString(), correlationId), ct);
        await tx.CommitAsync(ct);
        return Outcome<bool>.Ok(true);
    }
}
