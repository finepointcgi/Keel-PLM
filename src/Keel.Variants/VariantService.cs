using System.Globalization;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Core.Variants;
using Keel.Security;
using Npgsql;

namespace Keel.Variants;

/// <summary>
/// Option models and variants (ADR-0039). An option model belongs to a product, which is an Assembly part: it is readable and changeable exactly as that part is
/// (<see cref="AccessPolicy"/> on the part's markings), so nothing about a product's options, rules, variants or their counts reaches someone who cannot read the
/// product; a product the caller cannot read answers as missing. Every call writes an audit event in the transaction of its change, denials included. Writers of one
/// product serialize on its model row. A released variant is frozen: a change to the model that would make one invalid is refused.
/// </summary>
public sealed partial class VariantService(NpgsqlDataSource dataSource, IAuditLog audit, TimeProvider clock, IDomainEventSink? events = null) : IVariantSelectionSource
{
    /// <summary>Audit object type: variant actions are events of the product part, so the part's activity feed and timeline show them.</summary>
    public const string ObjectType = "part";

    private sealed record Step<T>(OutcomeKind Kind, IReadOnlyList<string> Errors, T? Value, (string, string?)[] Details)
    {
        public static Step<T> Done(T value, params (string, string?)[] details) => new(OutcomeKind.Ok, [], value, details);
        public static Step<T> Invalid(params string[] errors) => new(OutcomeKind.Invalid, errors, default, []);
        public static Step<T> Conflict(string message) => new(OutcomeKind.Conflict, [message], default, []);
        public static Step<T> Missing() => new(OutcomeKind.NotFound, [], default, []);
    }

    private static string Fmt(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Who(Subject s) => s.Username ?? s.Id;

    private static AuditEntry Entry(Subject s, string action, AuditOutcome outcome, string? number, string? correlationId, params (string, string?)[] details) => new()
    {
        Actor = ActorSnapshot.From(s),
        Action = action,
        Outcome = outcome,
        ObjectType = ObjectType,
        ObjectId = number,
        CorrelationId = correlationId,
        Details = AuditDetails.Of(details),
    };

    // ---------------------------------------------------------------- scaffolding

    /// <summary>Loads the product, evaluates Read (a refusal is audited and answers NotFound), runs the body, audits success when <paramref name="action"/> is given.</summary>
    private async Task<Outcome<T>> ReadAsync<T>(Subject subject, string number, string? action, string? correlationId,
        Func<NpgsqlConnection, NpgsqlTransaction, ProductRef, ModelData?, Task<Step<T>>> body, CancellationToken ct, bool requireModel = true)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var product = await VariantStore.LoadProductAsync(conn, tx, number, ct);
        if (product is null) return Outcome<T>.NotFound();
        var read = AccessPolicy.Evaluate(subject, product.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action ?? "variant.view", AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.NotFound();
        }
        var model = await VariantStore.LoadModelAsync(conn, tx, product.Id, ct);
        if (model is null && requireModel)
        {
            await tx.RollbackAsync(ct);
            return Outcome<T>.NotFound();
        }
        var step = await body(conn, tx, product, model);
        if (step.Kind != OutcomeKind.Ok)
        {
            await tx.RollbackAsync(ct);
            return Fail<T>(step);
        }
        if (action is not null) await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Success, number, correlationId, step.Details), ct);
        await tx.CommitAsync(ct);
        return Outcome<T>.Ok(step.Value!);
    }

    private static Outcome<T> Fail<T>(Step<T> step) => step.Kind switch
    {
        OutcomeKind.Invalid => Outcome<T>.Invalid(step.Errors),
        OutcomeKind.NotFound => Outcome<T>.NotFound(),
        _ => Outcome<T>.Conflict(step.Errors[0]),
    };

    /// <summary>
    /// Loads the product, needs Read then Modify, requires an Assembly with an option model (unless <paramref name="creating"/>), locks the model row and runs the body.
    /// With <paramref name="guardReleased"/> the changed model is re-read afterwards and the change is refused when a released variant would stop being valid.
    /// </summary>
    private async Task<Outcome<T>> WriteAsync<T>(Subject subject, string number, string action, string? correlationId, bool guardReleased,
        Func<NpgsqlConnection, NpgsqlTransaction, ProductRef, ModelData, Task<Step<T>>> body, CancellationToken ct, bool creating = false)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var product = await VariantStore.LoadProductAsync(conn, tx, number, ct);
        if (product is null) return Outcome<T>.NotFound();
        var read = AccessPolicy.Evaluate(subject, product.Markings, PlmAction.Read);
        if (!read.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", read.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.NotFound();
        }
        var modify = AccessPolicy.Evaluate(subject, product.Markings, PlmAction.Modify);
        if (!modify.Permitted)
        {
            await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Denied, number, correlationId, ("reasons", string.Join(",", modify.Reasons))), ct);
            await tx.CommitAsync(ct);
            return Outcome<T>.Forbidden(modify.Reasons);
        }

        Step<T> step;
        ModelData? before = null;
        if (product.PartType != "Assembly") step = Step<T>.Conflict($"{product.Number} is a {product.PartType}; only an Assembly can have options and variants.");
        else if (creating)
        {
            await using var ins = new NpgsqlCommand(
                "INSERT INTO var.model (product_master_id, created_at, created_by, updated_at, updated_by) VALUES (@p, @at, @by, @at, @by) ON CONFLICT DO NOTHING", conn, tx);
            ins.Parameters.AddWithValue("p", product.Id);
            ins.Parameters.AddWithValue("at", clock.GetUtcNow());
            ins.Parameters.AddWithValue("by", Who(subject));
            step = await ins.ExecuteNonQueryAsync(ct) == 0 ? Step<T>.Conflict($"{product.Number} already has an option model.") : null!;
            if (step is null)
            {
                before = (await VariantStore.LoadModelAsync(conn, tx, product.Id, ct))!;
                step = await body(conn, tx, product, before);
            }
        }
        else if (await VariantStore.LockModelAsync(conn, tx, product.Id, ct) is null) step = Step<T>.Conflict($"{product.Number} has no option model yet; create one first.");
        else
        {
            before = (await VariantStore.LoadModelAsync(conn, tx, product.Id, ct))!;
            step = await body(conn, tx, product, before);
            if (step.Kind == OutcomeKind.Ok && guardReleased)
            {
                var after = (await VariantStore.LoadModelAsync(conn, tx, product.Id, ct))!;
                var def = after.ToDef();
                var broken = after.Variants.Where(v => v.State == VariantStates.Released && !RuleEngine.Check(def, v.Selection).Valid).Select(v => v.Name).ToList();
                if (broken.Count > 0)
                    step = Step<T>.Conflict($"{broken.Count} released variant(s) would become invalid ({string.Join(", ", broken)}). Retire them first; a released variant is never changed.");
            }
        }

        if (step.Kind != OutcomeKind.Ok)
        {
            await tx.RollbackAsync(ct);
            await audit.AppendAsync(Entry(subject, action, AuditOutcome.Failure, number, correlationId, ("error", string.Join(" ", step.Errors)), ("kind", step.Kind.ToString())), ct);
            return Fail<T>(step);
        }
        await VariantStore.BumpAsync(conn, tx, product.Id, Who(subject), clock.GetUtcNow(), ct);
        await audit.AppendAsync(tx, Entry(subject, action, AuditOutcome.Success, number, correlationId, step.Details), ct);
        await tx.CommitAsync(ct);
        return Outcome<T>.Ok(step.Value!);
    }

    // ---------------------------------------------------------------- reading

    private static readonly SortMap ProductSorts = new(
        ("number", ["m.number"]), ("name", ["lower(m.name)"]),
        ("families", ["(SELECT count(*) FROM var.option_family x WHERE x.product_master_id = m.id)"]),
        ("variants", ["(SELECT count(*) FROM var.variant x WHERE x.product_master_id = m.id)"]));

    public static IReadOnlyCollection<string> ProductSortKeys => ProductSorts.Keys;

    public static bool TryResolveSort(string? key, string? direction, out SortSpec? spec, out string? error) => ProductSorts.TryResolve(key, direction, out spec, out error);

    /// <summary>Products with an option model that the caller may read, through the SQL read filter. <paramref name="numbers"/> limits the list to those part numbers.</summary>
    public async Task<ProductList> ListProductsAsync(Subject subject, string? search, IReadOnlyCollection<string>? numbers, SortSpec? sort, int limit, CancellationToken ct = default)
    {
        var filter = AccessPolicy.BuildReadFilter(subject, "m");
        var order = ProductSorts.OrderBy(sort, "m.number", "m.number");
        await using var cmd = dataSource.CreateCommand(
            $"""
            SELECT m.id, m.number, m.name,
                   (SELECT count(*) FROM var.option_family x WHERE x.product_master_id = m.id AND NOT x.retired),
                   (SELECT count(*) FROM var.variant x WHERE x.product_master_id = m.id AND x.state <> 'Retired'),
                   (SELECT count(*) FROM var.variant x WHERE x.product_master_id = m.id AND x.state = 'Released'),
                   {MarkingColumns.Select("m")}
            FROM var.model vm JOIN plm.part_master m ON m.id = vm.product_master_id
            WHERE {filter.Sql}
              AND (@search::text IS NULL OR m.number ILIKE '%' || @search || '%' OR m.name ILIKE '%' || @search || '%')
              AND (@numbers::text[] IS NULL OR m.number = ANY(@numbers))
            ORDER BY {order}
            LIMIT @limit
            """);
        foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
        cmd.Parameters.AddWithValue("search", string.IsNullOrWhiteSpace(search) ? DBNull.Value : search.Trim());
        cmd.Parameters.Add(new NpgsqlParameter("numbers", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = numbers is null ? DBNull.Value : numbers.ToArray() });
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        var rows = new List<ProductRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var markings = MarkingColumns.Read(r, 6);
            rows.Add(new ProductRow(r.GetString(1), r.GetString(2), (int)r.GetInt64(3), (int)r.GetInt64(4), (int)r.GetInt64(5), markings.BannerText(), markings));
        }
        return new ProductList(rows, Markings.AggregateBanner(rows.Select(x => x.Markings)), rows.Count);
    }

    public Task<Outcome<ModelView>> GetModelAsync(Subject subject, string number, string? correlationId, CancellationToken ct = default) =>
        ReadAsync<ModelView>(subject, number, "variant.view", correlationId, (_, _, product, model) =>
        {
            var view = BuildView(product, model!) with { CanModify = AccessPolicy.Evaluate(subject, product.Markings, PlmAction.Modify).Permitted && product.PartType == "Assembly" };
            return Task.FromResult(Step<ModelView>.Done(view, ("families", Fmt(view.Families.Count)), ("variants", Fmt(view.Variants.Count)), ("banner", view.Banner)));
        }, ct);

    /// <summary>True when the product has an option model (a key lookup after the read check; false for a product the caller cannot read).</summary>
    public async Task<bool> HasModelAsync(Subject subject, string number, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var product = await VariantStore.LoadProductAsync(conn, null, number, ct);
        if (product is null || !AccessPolicy.Evaluate(subject, product.Markings, PlmAction.Read).Permitted) return false;
        return await VariantStore.LoadModelAsync(conn, null, product.Id, ct) is not null;
    }

    private static ModelView BuildView(ProductRef product, ModelData model)
    {
        var def = model.ToDef();
        string Name(OptionRef o)
        {
            var f = model.Families.FirstOrDefault(x => x.Key == o.Family);
            var v = f?.Values.FirstOrDefault(x => x.Key == o.Value);
            return f is null || v is null ? $"{o.Family} is {o.Value}" : $"{f.Name} is {v.Name}";
        }
        var families = model.Families.OrderBy(f => f.SortOrder).ThenBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new FamilyView(f.Id, f.Key, f.Name, f.Description, f.Required, f.Retired, f.SortOrder,
                [.. f.Values.OrderBy(v => v.SortOrder).ThenBy(v => v.Key, StringComparer.Ordinal).Select(v => new ValueView(v.Id, v.Key, v.Name, v.Description, v.Retired, v.SortOrder))])).ToList();
        var rules = model.Rules.Select(r => new RuleView(r.Id, r.Kind, r.Subject, r.Members, r.Reason, RuleEngine.Describe(new OptionRuleDef(r.Id, r.Kind, r.Subject, r.Members, r.Reason), Name))).ToList();
        var variants = model.Variants.Select(v =>
        {
            var check = v.State == VariantStates.Retired ? new ConfigurationCheck(true, []) : RuleEngine.Check(def, v.Selection);
            return new VariantView(v.Id, v.Name, v.Description, v.State, v.Selection, check.Valid, check.Problems, v.CreatedAt, v.CreatedBy, v.ReleasedAt, v.ReleasedBy);
        }).ToList();
        var space = RuleEngine.Enumerate(def);
        return new ModelView(product.Number, product.Name, product.PartType, model.Version, families, rules, variants, RuleEngine.ValidateModel(def),
            RuleEngine.RawSpace(def), space.ValidCount, space.Complete, product.Markings.BannerText(), product.Markings);
    }

    /// <summary>Checks a selection against the rules of the product's model (audited as a read).</summary>
    public Task<Outcome<ConfigurationResult>> CheckConfigurationAsync(Subject subject, string number, IReadOnlyDictionary<string, string> selection, string? correlationId, CancellationToken ct = default) =>
        ReadAsync<ConfigurationResult>(subject, number, "variant.check", correlationId, (_, _, _, model) =>
        {
            var check = RuleEngine.Check(model!.ToDef(), selection);
            return Task.FromResult(Step<ConfigurationResult>.Done(new ConfigurationResult(check.Valid, check.Problems, selection), ("valid", check.Valid.ToString()), ("choices", Fmt(selection.Count))));
        }, ct);

    // ---------------------------------------------------------------- resolving a variant for a BOM query

    private static ResolvedVariant ToResolved(ProductRef product, ModelData model, string name, string state, IReadOnlyDictionary<string, string> selection)
    {
        var choices = new List<SelectionChoice>();
        foreach (var (fk, vk) in selection.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var f = model.Families.FirstOrDefault(x => x.Key == fk);
            var v = f?.Values.FirstOrDefault(x => x.Key == vk);
            choices.Add(new SelectionChoice(fk, f?.Name ?? fk, vk, v?.Name ?? vk));
        }
        return new ResolvedVariant(product.Number, name, state, choices, new VariantSelection(name, selection));
    }

    /// <summary>The selection of a named variant, for <c>BomQuery.Variant</c>. Draft variants resolve too, so a design can be previewed before it is released.</summary>
    public Task<Outcome<ResolvedVariant>> ResolveAsync(Subject subject, string productNumber, string variantName, string? correlationId, CancellationToken ct = default) =>
        ReadAsync<ResolvedVariant>(subject, productNumber, "variant.resolve", correlationId, (_, _, product, model) =>
        {
            var v = model!.Variants.FirstOrDefault(x => string.Equals(x.Name, variantName, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(v is null
                ? Step<ResolvedVariant>.Missing()
                : Step<ResolvedVariant>.Done(ToResolved(product, model, v.Name, v.State, v.Selection), ("variant", v.Name), ("state", v.State)));
        }, ct);

    async Task<Outcome<VariantSelection>> IVariantSelectionSource.ResolveAsync(Subject subject, string productNumber, string variantName, string? correlationId, CancellationToken ct)
    {
        var r = await ResolveAsync(subject, productNumber, variantName, correlationId, ct);
        return r.IsOk ? Outcome<VariantSelection>.Ok(r.Value!.Selection) : new Outcome<VariantSelection>(r.Kind, default, r.Errors);
    }

    /// <summary>An ad-hoc choice of options (not a named variant). It must pass the rules.</summary>
    public Task<Outcome<ResolvedVariant>> ResolveCustomAsync(Subject subject, string productNumber, IReadOnlyDictionary<string, string> selection, string? correlationId, CancellationToken ct = default) =>
        ReadAsync<ResolvedVariant>(subject, productNumber, "variant.resolve", correlationId, (_, _, product, model) =>
        {
            var check = RuleEngine.Check(model!.ToDef(), selection);
            if (!check.Valid) return Task.FromResult(Step<ResolvedVariant>.Invalid([.. check.Problems]));
            var label = "custom (" + OptionExpressions.DescribeSelection(selection) + ")";
            return Task.FromResult(Step<ResolvedVariant>.Done(ToResolved(product, model, label, "Custom", selection), ("variant", "custom"), ("choices", Fmt(selection.Count))));
        }, ct);

    // ---------------------------------------------------------------- conditions

    /// <summary>
    /// What the condition editor shows. With a product, references are checked against its model: an unknown option or value is an error; a retired one a warning.
    /// Without one only the syntax is checked.
    /// </summary>
    public async Task<ConditionPreview> PreviewConditionAsync(Subject subject, string? productNumber, string? text, CancellationToken ct = default)
    {
        var parsed = OptionExpressions.Parse(text);
        if (!parsed.IsOk) return new ConditionPreview(false, null, null, [.. parsed.Errors.Select(e => $"Position {e.Position + 1}: {e.Message}")], []);
        var expr = parsed.Expression!;
        if (string.IsNullOrWhiteSpace(productNumber))
            return new ConditionPreview(true, expr.ToCanonical(), OptionExpressions.Describe(expr), [], ["No product is chosen, so the option names have not been checked."]);
        ModelData? model = null;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        {
            var product = await VariantStore.LoadProductAsync(conn, null, productNumber.Trim(), ct);
            if (product is not null && AccessPolicy.Evaluate(subject, product.Markings, PlmAction.Read).Permitted) model = await VariantStore.LoadModelAsync(conn, null, product.Id, ct);
        }
        if (model is null) return new ConditionPreview(false, expr.ToCanonical(), null, [$"'{productNumber}' has no option model, or you cannot read it."], []);
        var (errors, warnings) = CheckReferences(expr, model);
        string? FamilyName(string k) => model.Families.FirstOrDefault(f => f.Key == k)?.Name;
        string? ValueName(string f, string k) => model.Families.FirstOrDefault(x => x.Key == f)?.Values.FirstOrDefault(v => v.Key == k)?.Name;
        return new ConditionPreview(errors.Count == 0, expr.ToCanonical(), OptionExpressions.Describe(expr, FamilyName, ValueName), errors, warnings);
    }

    private static (List<string> Errors, List<string> Warnings) CheckReferences(OptionExpr expr, ModelData model)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (var (fk, vk) in expr.References().OrderBy(r => r.Family, StringComparer.Ordinal).ThenBy(r => r.Value, StringComparer.Ordinal))
        {
            var f = model.Families.FirstOrDefault(x => x.Key == fk);
            if (f is null) { errors.Add($"The option '{fk}' does not exist for this product."); continue; }
            var v = f.Values.FirstOrDefault(x => x.Key == vk);
            if (v is null) errors.Add($"'{vk}' is not a value of '{f.Name}'.");
            else if (v.Retired || f.Retired) warnings.Add($"{f.Name} = {v.Name} is retired.");
        }
        return (errors, warnings);
    }

    /// <summary>A validator for <c>BomService.SetLineConditionAsync</c>: every option the condition names must exist in the product's model.</summary>
    public Func<OptionExpr, CancellationToken, Task<IReadOnlyList<string>>> ConditionValidatorFor(Subject subject, string productNumber) => async (expr, ct) =>
    {
        var p = await PreviewConditionAsync(subject, productNumber, expr.ToCanonical(), ct);
        return p.Errors;
    };

    // ---------------------------------------------------------------- JSON helpers for the event payload

    private static JsonObject Choices(ProductRef product, ModelData model, VariantRow v)
    {
        var arr = new JsonArray();
        foreach (var (fk, vk) in v.Selection.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var f = model.Families.First(x => x.Key == fk);
            var val = f.Values.First(x => x.Key == vk);
            arr.Add(new JsonObject { ["family"] = fk, ["familyName"] = f.Name, ["value"] = vk, ["valueName"] = val.Name });
        }
        return new JsonObject { ["choices"] = arr };
    }
}
