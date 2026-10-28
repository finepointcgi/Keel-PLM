using System.Globalization;
using Keel.Admin;
using Keel.Core;
using Keel.Parts;

namespace Keel.Web.Services;

/// <summary>An editable lifecycle state in the editor. The key of an existing state never changes.</summary>
public sealed class StateRow
{
    public string State { get; set; } = "";
    public string Label { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsLocked { get; set; }
    public bool IsFinal { get; set; }
    public bool IsExisting { get; init; }
}

public sealed class TransitionRow
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Via { get; set; } = "manual";
}

/// <summary>
/// The lifecycle editor's working copy: plain mutable rows converted to and from <see cref="LifecycleDefinition"/>. All rules (initial state,
/// reachability, protected states, workflow binding) are applied by <see cref="LifecycleRules"/> on the server when the draft is validated or published.
/// </summary>
public sealed class LifecycleDraft
{
    public string Family { get; init; } = "";
    public string Name { get; set; } = "";
    public string ObjectType { get; init; } = "part";
    public string Initial { get; set; } = "";
    public List<StateRow> States { get; } = [];
    public List<TransitionRow> Transitions { get; } = [];

    public static LifecycleDraft From(LifecycleDefinition d)
    {
        var draft = new LifecycleDraft { Family = d.Family, Name = d.Name, ObjectType = d.ObjectType, Initial = d.States.FirstOrDefault(s => s.IsInitial)?.State ?? "" };
        foreach (var s in d.States) draft.States.Add(new StateRow { State = s.State, Label = s.Label, SortOrder = s.SortOrder, IsLocked = s.IsLocked, IsFinal = s.IsFinal, IsExisting = true });
        foreach (var t in d.Transitions) draft.Transitions.Add(new TransitionRow { From = t.From, To = t.To, Via = t.Via });
        return draft;
    }

    public LifecycleDefinition ToDefinition() => new(Family, Name, ObjectType, 0, "draft",
        [.. States.Select(s => new LifecycleStateDef(s.State.Trim(), s.Label.Trim(), s.SortOrder, s.State == Initial, s.IsLocked, s.IsFinal))],
        [.. Transitions.Select(t => new LifecycleTransitionDef(t.From, t.To, t.Via))]);

    public void AddState() => States.Add(new StateRow { SortOrder = (States.Count == 0 ? 0 : States.Max(s => s.SortOrder)) + 10 });

    public void RemoveState(StateRow row)
    {
        States.Remove(row);
        Transitions.RemoveAll(t => t.From == row.State || t.To == row.State);
        if (Initial == row.State) Initial = "";
    }

    public void AddTransition() => Transitions.Add(new TransitionRow { From = States.FirstOrDefault()?.State ?? "", To = States.Skip(1).FirstOrDefault()?.State ?? "" });
}

public static class AdminUi
{
    /// <summary>The classification tree as indented rows (parents before children, siblings by order), for pickers and navigation.</summary>
    public static IReadOnlyList<(ClassNodeDto Node, int Indent)> Rows(ClassTree tree, bool activeOnly = false)
    {
        var rows = new List<(ClassNodeDto, int)>();
        void Walk(Guid? parent, int indent)
        {
            foreach (var n in tree.Children(parent))
            {
                if (activeOnly && !n.IsActive) continue;
                rows.Add((n, indent));
                Walk(n.Id, indent + 1);
            }
        }
        Walk(null, 0);
        return rows;
    }

    public static string Indented(ClassNodeDto n, int indent) => new string(' ', indent * 3) + n.Code + " · " + n.Name + (n.IsActive ? "" : " (retired)");

    /// <summary>Class attributes use the same form fields as part attributes.</summary>
    public static IReadOnlyList<AttributeDefinition> ToFormDefinitions(IEnumerable<EffectiveAttribute> effective) =>
        [.. effective.Select(e => e.Definition).Where(d => !d.Retired).Select(d => new AttributeDefinition(d.Name, d.Label, d.DataType, d.Unit, d.EnumValues, d.Required, d.MaxLength, d.SortOrder, d.HelpText, d.Group, d.Pattern, d.Min, d.Max, d.Retired))];

    public static string Number(decimal? v) => v?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";

    public static decimal? ParseNumber(string? s) => decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public static string Constraints(AttributeDefinitionDto d) => string.Join(", ", new[]
    {
        d.MaxLength is { } m ? $"max {m} characters" : null,
        d.Pattern is { } p ? $"format {p}" : null,
        d.Min is not null || d.Max is not null ? $"range {Number(d.Min)} to {Number(d.Max)}" : null,
        d.EnumValues is { } e ? string.Join(" | ", e) : null,
    }.Where(x => x is not null));

    public static string Utc(DateTimeOffset t) => UiText.Utc(t);
}

/// <summary>The attribute filters chosen in the facet panel, converted to the search's <see cref="FacetFilter"/> list.</summary>
public sealed class FacetSelection
{
    public Dictionary<string, HashSet<string>> Values { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, (string? Min, string? Max)> Ranges { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, bool> Flags { get; } = new(StringComparer.Ordinal);

    public bool IsEmpty => Values.Count == 0 && Ranges.Count == 0 && Flags.Count == 0;

    public void Clear()
    {
        Values.Clear();
        Ranges.Clear();
        Flags.Clear();
    }

    public void Toggle(string attribute, string value, bool on)
    {
        if (!Values.TryGetValue(attribute, out var set)) Values[attribute] = set = new HashSet<string>(StringComparer.Ordinal);
        if (on) set.Add(value); else set.Remove(value);
        if (set.Count == 0) Values.Remove(attribute);
    }

    public void SetRange(string attribute, string? min, string? max)
    {
        if (string.IsNullOrWhiteSpace(min) && string.IsNullOrWhiteSpace(max)) Ranges.Remove(attribute);
        else Ranges[attribute] = (min, max);
    }

    public void SetFlag(string attribute, bool? value)
    {
        if (value is { } v) Flags[attribute] = v; else Flags.Remove(attribute);
    }

    public IReadOnlyList<FacetFilter> ToFilters() =>
    [
        .. Values.Select(v => new FacetFilter(v.Key, AnyOf: [.. v.Value.Order(StringComparer.Ordinal)])),
        .. Ranges.Select(r => new FacetFilter(r.Key, Min: AdminUi.ParseNumber(r.Value.Min), Max: AdminUi.ParseNumber(r.Value.Max))).Where(f => f.Min is not null || f.Max is not null),
        .. Flags.Select(f => new FacetFilter(f.Key, Flag: f.Value)),
    ];
}
