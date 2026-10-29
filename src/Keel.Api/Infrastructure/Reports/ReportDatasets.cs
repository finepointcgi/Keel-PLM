using System.Globalization;
using Keel.Bom;
using Keel.Change;
using Keel.Compliance;
using Keel.Core;
using Keel.Core.Reports;
using Keel.Parts;
using Keel.Quality;
using Keel.Records;
using Keel.Requirements;
using Keel.Security;

namespace Keel.Api.Infrastructure.Reports;

/// <summary>
/// The catalog of report datasets (ADR-0044). Each one calls the owning module's existing access-filtered query, page by page up to the row cap, and returns the rows with their markings.
/// Nothing here builds SQL, and nothing reads a table directly.
/// </summary>
internal static class DatasetKit
{
    public static string Day(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Num(decimal d) => d.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Fetches pages of up to 200 until the limit is reached or the service has no more.</summary>
    public static async Task<(List<T> Items, long Total, bool Truncated)> PagesAsync<T>(int limit, Func<int, int, Task<(IReadOnlyList<T> Items, long Total)>> page)
    {
        var all = new List<T>();
        long total = 0;
        while (all.Count < limit)
        {
            var (items, t) = await page(Math.Min(200, limit - all.Count), all.Count);
            total = t;
            all.AddRange(items);
            if (items.Count == 0 || all.Count >= total) break;
        }
        return (all, total, total > all.Count);
    }
}

public sealed class PartsDataset(PartService parts) : IReportDataset
{
    public string Id => "parts";
    public string Title => "Parts";
    public string Description => "Parts you may read, at their latest revision.";
    public string? ParameterLabel => null;
    public IReadOnlyList<DatasetColumn> Columns { get; } =
    [
        new("number", "Number", "text"), new("name", "Name", "text"), new("type", "Type", "text"), new("revision", "Revision", "text"), new("iteration", "Iteration", "number"), new("state", "State", "text"),
    ];

    public async Task<Outcome<DatasetResult>> QueryAsync(Subject subject, DatasetQuery q, string? corr, CancellationToken ct = default)
    {
        var (items, total, truncated) = await DatasetKit.PagesAsync(q.Limit, async (take, skip) => { var r = await parts.SearchAsync(subject, q.Text, null, take, skip, null, corr, ct); return (r.Items, r.Total); });
        return Outcome<DatasetResult>.Ok(new DatasetResult([.. items.Select(p => new DatasetRow([p.Number, p.Name, p.PartType, p.Revision, p.Iteration.ToString(CultureInfo.InvariantCulture), p.State], p.Markings))], total, truncated));
    }
}

public sealed class ChangesDataset(ChangeService changes) : IReportDataset
{
    public string Id => "changes";
    public string Title => "Changes";
    public string Description => "Problem reports, change requests, change notices and change tasks you may read.";
    public string? ParameterLabel => null;
    public IReadOnlyList<DatasetColumn> Columns { get; } =
    [
        new("number", "Number", "text"), new("type", "Type", "text"), new("title", "Title", "text"), new("state", "State", "text"), new("class", "Class", "text"), new("priority", "Priority", "text"),
        new("requester", "Requester", "text"), new("created", "Created", "date"),
    ];

    public async Task<Outcome<DatasetResult>> QueryAsync(Subject subject, DatasetQuery q, string? corr, CancellationToken ct = default)
    {
        var (items, total, truncated) = await DatasetKit.PagesAsync(q.Limit, async (take, skip) => { var r = await changes.SearchAsync(subject, new ChangeSearchQuery(Text: q.Text, Limit: take, Offset: skip), corr, ct); return (r.Items, r.Total); });
        return Outcome<DatasetResult>.Ok(new DatasetResult([.. items.Select(c => new DatasetRow([c.Number, c.Type, c.Title, c.StateLabel, c.ChangeClass, c.Priority, c.RequesterName, DatasetKit.Day(c.CreatedAt)], c.Markings))], total, truncated));
    }
}

public sealed class BaselinesDataset(BaselineService baselines) : IReportDataset
{
    public string Id => "baselines";
    public string Title => "Baselines";
    public string Description => "Baselines you may read, with their root part and size.";
    public string? ParameterLabel => null;
    public IReadOnlyList<DatasetColumn> Columns { get; } =
    [
        new("number", "Number", "text"), new("name", "Name", "text"), new("type", "Type", "text"), new("state", "State", "text"), new("root", "Root part", "text"), new("items", "Items", "number"),
        new("created", "Created", "date"), new("createdBy", "Created by", "text"),
    ];

    public async Task<Outcome<DatasetResult>> QueryAsync(Subject subject, DatasetQuery q, string? corr, CancellationToken ct = default)
    {
        var (items, total, truncated) = await DatasetKit.PagesAsync(q.Limit, async (take, skip) =>
        {
            var r = await baselines.SearchAsync(subject, new BaselineSearch(Text: q.Text, Limit: take, Offset: skip), corr, ct);
            return r.IsOk ? (r.Value!.Items, (long)r.Value.Total) : ([], 0L);
        });
        return Outcome<DatasetResult>.Ok(new DatasetResult([.. items.Select(b => new DatasetRow([b.Number, b.Name, b.Type, b.StateLabel, b.RootNumber, b.ItemCount.ToString(CultureInfo.InvariantCulture), DatasetKit.Day(b.CreatedAt), b.CreatedBy], b.Markings))], total, truncated));
    }
}

public sealed class QualityDataset(QualityQueries quality) : IReportDataset
{
    public string Id => "quality";
    public string Title => "Quality records";
    public string Description => "Nonconformance reports, corrective and preventive actions, deviations and waivers you may read.";
    public string? ParameterLabel => null;
    public IReadOnlyList<DatasetColumn> Columns { get; } =
    [
        new("kind", "Kind", "text"), new("number", "Number", "text"), new("title", "Title", "text"), new("state", "State", "text"), new("detail1", "Severity, priority or kind", "text"),
        new("detail2", "Part, owner or window", "text"), new("raisedBy", "Raised by", "text"), new("created", "Created", "date"), new("due", "Due", "date"), new("overdue", "Overdue", "text"),
    ];

    public async Task<Outcome<DatasetResult>> QueryAsync(Subject subject, DatasetQuery q, string? corr, CancellationToken ct = default)
    {
        var rows = new List<DatasetRow>();
        long total = 0; var truncated = false;
        foreach (var kind in QualityKinds.All)
        {
            var (items, t, tr) = await DatasetKit.PagesAsync(Math.Max(1, q.Limit - rows.Count), async (take, skip) => { var r = await quality.SearchAsync(subject, kind, new QualitySearchQuery(Text: q.Text, Limit: take, Offset: skip), corr, ct); return (r.Items, r.Total); });
            total += t; truncated |= tr || rows.Count + items.Count >= q.Limit && t > items.Count;
            rows.AddRange(items.Select(i => new DatasetRow([i.Kind, i.Number, i.Title, i.StateLabel, i.Detail1, i.Detail2, i.RaisedBy, DatasetKit.Day(i.CreatedAt), i.Due is { } d ? DatasetKit.Day(d) : null, i.Overdue ? "Yes" : "No"], i.Markings)));
            if (rows.Count >= q.Limit) { truncated = true; break; }
        }
        return Outcome<DatasetResult>.Ok(new DatasetResult(rows, total, truncated));
    }
}

public sealed class RequirementsDataset(RequirementService requirements) : IReportDataset
{
    public string Id => "requirements";
    public string Title => "Requirement sets";
    public string Description => "Requirement specifications you may read, with how many requirements each holds. Coverage detail stays on the specification page.";
    public string? ParameterLabel => null;
    public IReadOnlyList<DatasetColumn> Columns { get; } =
    [
        new("number", "Number", "text"), new("name", "Name", "text"), new("product", "Product", "text"), new("revision", "Revision", "text"), new("state", "State", "text"), new("requirements", "Requirements", "number"),
    ];

    public async Task<Outcome<DatasetResult>> QueryAsync(Subject subject, DatasetQuery q, string? corr, CancellationToken ct = default)
    {
        var (items, total, truncated) = await DatasetKit.PagesAsync(q.Limit, async (take, skip) =>
        {
            var r = await requirements.SearchAsync(subject, new SpecSearch(Query: q.Text, Limit: take, Offset: skip), corr, ct);
            return r.IsOk ? (r.Value!.Items, (long)r.Value.Total) : ([], 0L);
        });
        return Outcome<DatasetResult>.Ok(new DatasetResult([.. items.Select(s => new DatasetRow([s.Number, s.Name, s.Product, s.Revision, s.StateLabel, s.RequirementCount.ToString(CultureInfo.InvariantCulture)], s.Markings))], total, truncated));
    }
}

/// <summary>Compliance roll-up input: the declarations of readable parts. A declaration is marked by its part, so each row carries the part's markings (a key lookup).</summary>
public sealed class DeclarationsDataset(DeclarationService declarations, IEnumerable<IObjectAccess> access) : IReportDataset
{
    public string Id => "compliance";
    public string Title => "Compliance declarations";
    public string Description => "Material and substance declarations of parts you may read, with status and expiry.";
    public string? ParameterLabel => null;
    public IReadOnlyList<DatasetColumn> Columns { get; } =
    [
        new("number", "Declaration", "text"), new("part", "Part", "text"), new("source", "Source", "text"), new("status", "Status", "text"), new("declared", "Declared", "date"), new("expires", "Expires", "date"),
        new("days", "Days to expiry", "number"),
    ];

    public async Task<Outcome<DatasetResult>> QueryAsync(Subject subject, DatasetQuery q, string? corr, CancellationToken ct = default)
    {
        var (items, total, truncated) = await DatasetKit.PagesAsync(q.Limit, async (take, skip) =>
        {
            var r = await declarations.SearchAsync(subject, new DeclarationSearch(PartNumber: string.IsNullOrWhiteSpace(q.Text) ? null : q.Text, Limit: take, Offset: skip), corr, ct);
            return r.IsOk ? (r.Value!.Items, (long)r.Value.Total) : ([], 0L);
        });
        var partAccess = access.FirstOrDefault(a => a.Kind == "part");
        var marks = new Dictionary<string, Markings?>(StringComparer.Ordinal);
        foreach (var n in items.Select(i => i.PartNumber).Distinct(StringComparer.Ordinal)) marks[n] = partAccess is null ? null : (await partAccess.FindAsync(n, ct))?.Markings;
        // A row whose part markings cannot be found is left out rather than shown unmarked.
        return Outcome<DatasetResult>.Ok(new DatasetResult([.. items.Where(i => marks[i.PartNumber] is not null)
            .Select(d => new DatasetRow([d.Number, d.PartNumber, d.Source, d.StatusLabel, DatasetKit.Day(d.DeclaredOn), DatasetKit.Day(d.ExpiresOn), d.DaysToExpiry.ToString(CultureInfo.InvariantCulture)], marks[d.PartNumber]))], total, truncated));
    }
}

/// <summary>Retention schedules of records management (8.6). Schedule data is unmarked reference data. A programs schedule dataset joins later through the same port.</summary>
public sealed class SchedulesDataset(ScheduleService schedules) : IReportDataset
{
    public string Id => "schedule";
    public string Title => "Retention schedules";
    public string Description => "Records retention schedule versions, with status and number of record classes.";
    public string? ParameterLabel => null;
    public IReadOnlyList<DatasetColumn> Columns { get; } =
    [
        new("title", "Title", "text"), new("version", "Version", "number"), new("status", "Status", "text"), new("classes", "Record classes", "number"), new("created", "Created", "date"), new("published", "Published", "date"),
    ];

    public async Task<Outcome<DatasetResult>> QueryAsync(Subject subject, DatasetQuery q, string? corr, CancellationToken ct = default)
    {
        var r = await schedules.ListAsync(subject, corr, ct);
        if (!r.IsOk) return new Outcome<DatasetResult>(r.Kind, null, r.Errors);
        var rows = r.Value!.Where(s => string.IsNullOrWhiteSpace(q.Text) || s.Title.Contains(q.Text, StringComparison.OrdinalIgnoreCase))
            .Select(s => new DatasetRow([s.Title, s.Version.ToString(CultureInfo.InvariantCulture), s.Status, s.Classes.ToString(CultureInfo.InvariantCulture), DatasetKit.Day(s.CreatedAt), s.PublishedAt is { } p ? DatasetKit.Day(p) : null], null)).Take(q.Limit).ToList();
        return Outcome<DatasetResult>.Ok(new DatasetResult(rows, rows.Count, false));
    }
}

/// <summary>The released structure under one part (the input), as the BOM service resolves it for the caller. Lines the caller may not read appear as restricted placeholders.</summary>
public sealed class BomDataset(BomService bom) : IReportDataset
{
    public string Id => "bom";
    public string Title => "Bill of materials";
    public string Description => "The structure under one part (latest released revisions).";
    public string? ParameterLabel => "Root part number";
    public IReadOnlyList<DatasetColumn> Columns { get; } =
    [
        new("level", "Level", "number"), new("find", "Find number", "number"), new("number", "Part number", "text"), new("name", "Name", "text"), new("revision", "Revision", "text"), new("state", "State", "text"),
        new("quantity", "Quantity", "number"), new("unit", "Unit", "text"), new("effectivity", "Effectivity", "text"),
    ];

    public async Task<Outcome<DatasetResult>> QueryAsync(Subject subject, DatasetQuery q, string? corr, CancellationToken ct = default)
    {
        var r = await bom.ExplodeAsync(subject, new BomQuery(q.Parameter ?? "", Spec: BomConfigSpec.LatestReleased), corr, ct);
        if (!r.IsOk) return new Outcome<DatasetResult>(r.Kind, null, r.Errors);
        var lines = r.Value!.Lines;
        var rows = lines.Take(q.Limit).Select(l => new DatasetRow(
            [l.Level.ToString(CultureInfo.InvariantCulture), l.FindNumber.ToString(CultureInfo.InvariantCulture), l.Redacted ? "(restricted)" : l.Number, l.Redacted ? null : l.Name, l.Revision, l.StateLabel, DatasetKit.Num(l.Quantity), l.Unit, l.EffectivityText],
            l.Redacted ? null : l.Markings)).ToList();
        return Outcome<DatasetResult>.Ok(new DatasetResult(rows, lines.Count, lines.Count > rows.Count));
    }
}
