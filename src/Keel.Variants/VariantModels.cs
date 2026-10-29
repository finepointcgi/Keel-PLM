using Keel.Core;
using Keel.Core.Variants;

namespace Keel.Variants;

public static class VariantLimits
{
    public const int MaxFamilies = 40;
    public const int MaxValuesPerFamily = 60;
    public const int MaxRules = 200;
    public const int MaxVariants = 200;
    public const int MaxMembersPerRule = 40;
}

public static class VariantStates
{
    public const string Draft = "Draft";
    public const string Released = "Released";
    public const string Retired = "Retired";

    public static bool IsKnown(string? s) => s is Draft or Released or Retired;
}

/// <summary>A product that has an option model, as the list shows it. Counts are of a product the caller may read, so they reveal nothing else.</summary>
public sealed record ProductRow(string Number, string Name, int Families, int Variants, int ReleasedVariants, string Banner, Markings Markings);

public sealed record ProductList(IReadOnlyList<ProductRow> Rows, string Banner, int Total);

public sealed record ValueView(Guid Id, string Key, string Name, string? Description, bool Retired, int SortOrder);

public sealed record FamilyView(Guid Id, string Key, string Name, string? Description, bool Required, bool Retired, int SortOrder, IReadOnlyList<ValueView> Values);

public sealed record RuleView(Guid Id, RuleKind Kind, OptionRef? Subject, IReadOnlyList<OptionRef> Members, string? Reason, string Text);

public sealed record VariantView(
    Guid Id, string Name, string? Description, string State, IReadOnlyDictionary<string, string> Selection,
    bool Valid, IReadOnlyList<string> Problems, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset? ReleasedAt, string? ReleasedBy);

public sealed record ModelView(
    string ProductNumber, string ProductName, string ProductType, int Version,
    IReadOnlyList<FamilyView> Families, IReadOnlyList<RuleView> Rules, IReadOnlyList<VariantView> Variants,
    IReadOnlyList<ModelFinding> Findings, long RawCombinations, long ValidCombinations, bool AnalysisComplete,
    string Banner, Markings Markings, bool CanModify = false);

public sealed record NewFamily(string Key, string Name, string? Description = null, bool Required = true, int SortOrder = 0);
public sealed record FamilyChange(string? Name = null, string? Description = null, bool? Required = null, bool? Retired = null, int? SortOrder = null);
public sealed record NewValue(string Key, string Name, string? Description = null, int SortOrder = 0);
public sealed record ValueChange(string? Name = null, string? Description = null, bool? Retired = null, int? SortOrder = null);
public sealed record NewRule(RuleKind Kind, OptionRef? Subject, IReadOnlyList<OptionRef> Members, string? Reason = null);
public sealed record NewVariant(string Name, IReadOnlyDictionary<string, string> Selection, string? Description = null);
public sealed record VariantChange(string? Name = null, string? Description = null, IReadOnlyDictionary<string, string>? Selection = null);

/// <summary>What the condition editor shows: the canonical text, a sentence, and problems (errors stop the save; warnings do not).</summary>
public sealed record ConditionPreview(bool Valid, string? Canonical, string? Description, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

/// <summary>A configuration checked against the rules of a product.</summary>
public sealed record ConfigurationResult(bool Valid, IReadOnlyList<string> Problems, IReadOnlyDictionary<string, string> Selection);

public sealed record SelectionChoice(string Family, string FamilyName, string Value, string ValueName);

/// <summary>A variant ready to resolve a structure for: the selection the BOM queries take, the state and the named choices for headings.</summary>
public sealed record ResolvedVariant(string ProductNumber, string Name, string State, IReadOnlyList<SelectionChoice> Choices, VariantSelection Selection);
