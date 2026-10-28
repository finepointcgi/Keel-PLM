using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Security;
using Npgsql;

namespace Keel.Admin;

/// <summary>One attribute filter: numbers use a range, enums and text a list of values or a substring, booleans "true" or "false".</summary>
public sealed record FacetFilter(string Attribute, IReadOnlyList<string>? AnyOf = null, decimal? Min = null, decimal? Max = null, string? Contains = null, bool? Flag = null);

public sealed record ClassSearchQuery(string? ClassCode, string? Text = null, string? State = null, IReadOnlyList<FacetFilter>? Filters = null, int Limit = 50, int Offset = 0);

public sealed record ClassSearchHit(string Number, string Name, string PartType, string Revision, int Iteration, string State, string Banner, Markings Markings, string ClassCode, JsonObject ClassAttributes);

public sealed record FacetValue(string Value, long Count);

/// <summary>Counts for one attribute of the selected class: value counts for enums and booleans, the observed range for numbers.</summary>
public sealed record AttributeFacet(string Attribute, string Label, string DataType, string? Unit, IReadOnlyList<FacetValue> Values, decimal? Min, decimal? Max, long WithValue);

/// <summary>Number of matching parts in the class and its subclasses, for the class tree navigation.</summary>
public sealed record ClassCount(string Code, long Count);

public sealed record ClassSearchResult(IReadOnlyList<ClassSearchHit> Items, long Total, string PageBanner, IReadOnlyList<ClassCount> ClassCounts, IReadOnlyList<AttributeFacet> Facets);

public sealed partial class ClassificationService
{
    public const int MaxSearchLimit = 200;
    public const int MaxFilters = 12;

    /// <summary>
    /// Faceted classification search over the latest iteration of every classified part (ADR-0022). Every query, including every count and facet,
    /// runs under <see cref="AccessPolicy.BuildReadFilter"/>: the numbers a user sees never include a part they may not read.
    /// </summary>
    public async Task<Outcome<ClassSearchResult>> SearchAsync(Subject subject, ClassSearchQuery q, string? correlationId, CancellationToken ct = default)
    {
        var limit = Math.Clamp(q.Limit, 1, MaxSearchLimit);
        var offset = Math.Max(0, q.Offset);
        var filters = q.Filters ?? [];
        if (filters.Count > MaxFilters) return Outcome<ClassSearchResult>.Invalid([$"At most {MaxFilters} attribute filters are allowed."]);
        var filter = AccessPolicy.BuildReadFilter(subject, "m");

        await using var conn = await run.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var tree = await LoadTreeAsync(conn, tx, ct);
        ClassNodeDto? selected = null;
        if (!string.IsNullOrWhiteSpace(q.ClassCode))
        {
            selected = tree.ByCode(q.ClassCode);
            if (selected is null) return Outcome<ClassSearchResult>.Invalid([$"Class '{q.ClassCode}' does not exist."]);
        }
        // Filters are typed by the attributes of the selected class and everything below it; without a class there is nothing to type them with.
        var known = selected is null ? [] : tree.Subtree(selected.Id).SelectMany(n => tree.Effective(n.Id)).Select(e => e.Definition).DistinctBy(d => d.Name).ToDictionary(d => d.Name);
        var errors = new List<string>();
        foreach (var f in filters)
        {
            if (!known.TryGetValue(f.Attribute ?? "", out var def)) { errors.Add($"'{f.Attribute}' is not an attribute of the selected class."); continue; }
            var ok = def.DataType switch
            {
                "number" => f.Min is not null || f.Max is not null,
                "boolean" => f.Flag is not null,
                "enum" => f.AnyOf is { Count: > 0 },
                _ => f.AnyOf is { Count: > 0 } || !string.IsNullOrWhiteSpace(f.Contains),
            };
            if (!ok) errors.Add($"The filter on {def.Label} has no value.");
        }
        if (errors.Count > 0) return Outcome<ClassSearchResult>.Invalid(errors);

        string Conditions(bool withClass, string? exceptAttribute, List<(string, object?)> ps)
        {
            var sb = new StringBuilder();
            if (withClass && selected is not null) { sb.Append(" AND cn.path LIKE @prefix"); ps.Add(("prefix", EscapeLike(selected.Path) + "%")); }
            if (!string.IsNullOrWhiteSpace(q.Text))
            {
                var term = q.Text.Trim();
                sb.Append(" AND (m.search @@ websearch_to_tsquery('english', @q) OR m.number ILIKE @like OR m.name ILIKE @like)");
                ps.Add(("q", term));
                ps.Add(("like", "%" + EscapeLike(term) + "%"));
            }
            if (!string.IsNullOrWhiteSpace(q.State)) { sb.Append(" AND r.lifecycle_state = @state"); ps.Add(("state", q.State)); }
            for (var k = 0; k < filters.Count; k++)
            {
                var f = filters[k];
                if (f.Attribute == exceptAttribute) continue;
                var def = known[f.Attribute];
                sb.Append(CultureInfo.InvariantCulture, $" AND EXISTS (SELECT 1 FROM cfg.part_class_value fv{k} WHERE fv{k}.iteration_id = i.id AND fv{k}.attr = @fa{k}");
                ps.Add(($"fa{k}", f.Attribute));
                switch (def.DataType)
                {
                    case "number":
                        if (f.Min is { } lo) { sb.Append(CultureInfo.InvariantCulture, $" AND fv{k}.num >= @flo{k}"); ps.Add(($"flo{k}", lo)); }
                        if (f.Max is { } hi) { sb.Append(CultureInfo.InvariantCulture, $" AND fv{k}.num <= @fhi{k}"); ps.Add(($"fhi{k}", hi)); }
                        break;
                    case "boolean":
                        sb.Append(CultureInfo.InvariantCulture, $" AND fv{k}.flag = @fb{k}");
                        ps.Add(($"fb{k}", f.Flag!.Value));
                        break;
                    default:
                        if (f.AnyOf is { Count: > 0 }) { sb.Append(CultureInfo.InvariantCulture, $" AND fv{k}.txt = ANY(@fv{k})"); ps.Add(($"fv{k}", f.AnyOf.ToArray())); }
                        else { sb.Append(CultureInfo.InvariantCulture, $" AND fv{k}.txt ILIKE @fl{k}"); ps.Add(($"fl{k}", "%" + EscapeLike(f.Contains!.Trim()) + "%")); }
                        break;
                }
                sb.Append(')');
            }
            return sb.ToString();
        }

        NpgsqlCommand Command(string select, string extraJoin, string conditions, string tail, List<(string, object?)> ps)
        {
            // The access filter is part of the same statement as the part table in every query below.
            var cmd = AdminRunner.Cmd(conn, tx,
                $"""
                {select} FROM plm.part_master m JOIN plm.part_revision r ON r.master_id = m.id AND r.is_latest
                JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
                JOIN cfg.part_classification pc ON pc.iteration_id = i.id JOIN cfg.class_node cn ON cn.id = pc.class_id {extraJoin}
                WHERE {filter.Sql}{conditions} {tail}
                """, [.. ps]);
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            return cmd;
        }

        // ---- page of hits
        var items = new List<ClassSearchHit>();
        var ps0 = new List<(string, object?)>();
        var cond0 = Conditions(true, null, ps0);
        long total;
        await using (var cmd = Command("SELECT count(*)", "", cond0, "", ps0)) total = (long)(await cmd.ExecuteScalarAsync(ct))!;
        var ps1 = new List<(string, object?)> { ("limit", limit), ("offset", offset) };
        var cond1 = Conditions(true, null, ps1);
        await using (var cmd = Command($"SELECT m.number, m.name, m.part_type, r.revision, r.latest_iteration, r.lifecycle_state, cn.code, pc.attributes::text, {MarkingColumns.Select("m")}", "", cond1,
            "ORDER BY m.number LIMIT @limit OFFSET @offset", ps1))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var m = MarkingColumns.Read(r, 8);
                items.Add(new ClassSearchHit(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), m.BannerText(), m, r.GetString(6), JsonNode.Parse(r.GetString(7))!.AsObject()));
            }
        }

        // ---- class tree counts: everything but the class restriction applies, so siblings show what choosing them would give
        var byPath = new Dictionary<string, long>(StringComparer.Ordinal);
        var ps2 = new List<(string, object?)>();
        var cond2 = Conditions(false, null, ps2);
        await using (var cmd = Command("SELECT cn.path, count(*)", "", cond2, "GROUP BY cn.path", ps2))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) byPath[r.GetString(0)] = r.GetInt64(1);
        }
        var classCounts = tree.Nodes.Select(n => new ClassCount(n.Code, byPath.Where(p => p.Key.StartsWith(n.Path, StringComparison.Ordinal)).Sum(p => p.Value))).Where(c => c.Count > 0).ToList();

        // ---- attribute facets of the selected subtree, each ignoring its own filter
        var facets = new List<AttributeFacet>();
        if (selected is not null)
        {
            // The attributes of the selected class and of every class below it (a facet counts the parts that have a value).
            foreach (var d in known.Values.Where(d => !d.Retired && d.DataType is "enum" or "boolean" or "number").OrderBy(d => d.SortOrder).ThenBy(d => d.Name, StringComparer.Ordinal))
            {
                var ps3 = new List<(string, object?)> { ("fx", d.Name) };
                var cond3 = Conditions(true, d.Name, ps3);
                var join = "JOIN cfg.part_class_value fx ON fx.iteration_id = i.id AND fx.attr = @fx";
                if (d.DataType == "number")
                {
                    await using var cmd = Command("SELECT min(fx.num), max(fx.num), count(*)", join, cond3, "", ps3);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    await r.ReadAsync(ct);
                    if (r.GetInt64(2) > 0) facets.Add(new AttributeFacet(d.Name, d.Label, d.DataType, d.Unit, [], r.GetDecimal(0), r.GetDecimal(1), r.GetInt64(2)));
                }
                else
                {
                    var column = d.DataType == "boolean" ? "fx.flag::text" : "fx.txt";
                    await using var cmd = Command($"SELECT {column}, count(*)", join, cond3, $"GROUP BY {column} ORDER BY count(*) DESC, {column}", ps3);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    var values = new List<FacetValue>();
                    while (await r.ReadAsync(ct)) values.Add(new FacetValue(r.GetString(0), r.GetInt64(1)));
                    if (values.Count > 0) facets.Add(new AttributeFacet(d.Name, d.Label, d.DataType, d.Unit, values, null, null, values.Sum(v => v.Count)));
                }
            }
        }

        await audit.AppendAsync(tx, PartEntry(subject, "part.classification_search", AuditOutcome.Success, "*", correlationId,
            ("class", q.ClassCode), ("filters", filters.Count.ToString(CultureInfo.InvariantCulture)), ("query", q.Text), ("offset", offset.ToString(CultureInfo.InvariantCulture)),
            ("returned", items.Count.ToString(CultureInfo.InvariantCulture)), ("numbers", string.Join(",", items.Select(i => i.Number)))), ct);
        await tx.CommitAsync(ct);
        return Outcome<ClassSearchResult>.Ok(new ClassSearchResult(items, total, Markings.AggregateBanner(items.Select(i => i.Markings)), classCounts, facets));
    }
}
