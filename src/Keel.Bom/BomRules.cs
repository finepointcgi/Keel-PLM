using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Keel.Core;

namespace Keel.Bom;

/// <summary>Pure validation rules for BOM lines (ADR-0017). Database state (cycles, duplicates across a parent) is checked by <see cref="BomService"/>.</summary>
public static partial class BomRules
{
    /// <summary>Validates quantity, unit, reference designators and notes of one line; returns normalized designators.</summary>
    public static (IReadOnlyList<string> Errors, string[] Designators) ValidateLine(
        decimal quantity, string? unit, IReadOnlyList<string>? designators, string? notes, int? findNumber)
    {
        var errors = new List<string>();
        if (!UnitsOfMeasure.IsValid(unit))
            errors.Add($"Unit '{unit}' is not valid; use one of: {string.Join(", ", UnitsOfMeasure.All)}.");
        if (quantity <= 0) errors.Add("Quantity must be greater than zero.");
        else if (quantity > BomLimits.MaxQuantity) errors.Add($"Quantity must be at most {BomLimits.MaxQuantity.ToString("N0", CultureInfo.InvariantCulture)}.");
        else if (decimal.Round(quantity, 6) != quantity) errors.Add("Quantity may have at most 6 decimal places.");
        if (findNumber is { } f && (f < 1 || f > BomLimits.MaxFindNumber)) errors.Add($"Find number must be between 1 and {BomLimits.MaxFindNumber}.");
        if ((notes?.Length ?? 0) > 1000) errors.Add("Notes must be at most 1000 characters.");

        var list = new List<string>();
        foreach (var raw in designators ?? [])
        {
            var d = raw?.Trim() ?? "";
            if (d.Length == 0) continue;
            if (!DesignatorPattern().IsMatch(d)) errors.Add($"Reference designator '{d}' may contain only letters, digits, '.', '_', '-', '/' (max 32).");
            else list.Add(d);
        }
        var duplicates = list.GroupBy(d => d, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0) errors.Add($"Reference designators must be unique within the line: {string.Join(", ", duplicates)}.");

        if (unit == UnitsOfMeasure.Each && quantity > 0)
        {
            if (quantity != decimal.Truncate(quantity)) errors.Add("Quantity must be a whole number for unit EA.");
            else if (list.Count > 0 && list.Count != quantity)
                errors.Add($"{list.Count} reference designator(s) given but the quantity is {quantity.ToString("0", CultureInfo.InvariantCulture)}; the counts must match for unit EA.");
        }
        return (errors, [.. list]);
    }

    /// <summary>Next free find number: the next multiple of 10 above the highest in use (10 when there are none).</summary>
    public static int NextFindNumber(IEnumerable<int> existing)
    {
        var max = 0;
        foreach (var n in existing) max = Math.Max(max, n);
        return ((max / 10) + 1) * 10;
    }

    /// <summary>Designators already used elsewhere under the same parent (ASSUMPTION: unique across the parent).</summary>
    public static IReadOnlyList<string> DesignatorClashes(IEnumerable<string> candidate, IEnumerable<string> existingElsewhere)
    {
        var used = new HashSet<string>(existingElsewhere, StringComparer.OrdinalIgnoreCase);
        return [.. candidate.Where(used.Contains)];
    }

    public static string CycleMessage(string parentNumber, string childNumber, IReadOnlyList<string> path) =>
        parentNumber == childNumber
            ? $"{parentNumber} cannot contain itself."
            : $"Adding {childNumber} under {parentNumber} would create a circular structure: {string.Join(" -> ", path)}.";

    /// <summary>Parses a find-number path such as "10.20.30"; null when malformed or deeper than the maximum depth.</summary>
    public static int[]? ParsePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];
        var parts = path.Split('.');
        if (parts.Length > BomLimits.MaxDepth * 10) return null;
        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out result[i]) || result[i] < 1) return null;
        return result;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-/]{0,31}$")]
    private static partial Regex DesignatorPattern();
}

/// <summary>CSV rendering of an exploded structure. The aggregate marking banner is the first and the last line (ADR-0004).</summary>
public static class BomCsv
{
    public const string RestrictedLabel = "Restricted item";

    public static string Render(string banner, IEnumerable<BomLine> lines)
    {
        var sb = new StringBuilder(1 << 20);
        sb.Append(Cell(banner)).Append("\r\n");
        sb.Append("Level,Path,Find number,Part number,Name,Revision,State,Quantity,Unit,Extended quantity,Reference designators,Markings,Flags\r\n");
        foreach (var l in lines)
        {
            var flags = l.Redacted ? "restricted" : l.Unresolved ? "unresolved: no released revision" : l.Circular ? "circular reference" : "";
            sb.Append(l.Level.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Cell(l.Path)).Append(',')
              .Append(l.FindNumber.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Cell(l.Redacted ? RestrictedLabel : l.Number)).Append(',')
              .Append(Cell(l.Name)).Append(',')
              .Append(Cell(l.Revision is null ? null : $"{l.Revision}.{l.Iteration}")).Append(',')
              .Append(Cell(l.StateLabel)).Append(',')
              .Append(l.Quantity.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
              .Append(Cell(l.Unit)).Append(',')
              .Append(l.ExtendedQuantity.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
              .Append(Cell(string.Join(' ', l.ReferenceDesignators))).Append(',')
              .Append(Cell(l.Banner)).Append(',')
              .Append(Cell(flags)).Append("\r\n");
        }
        sb.Append(Cell(banner)).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Quotes a cell and neutralizes spreadsheet formula injection (leading = + - @ tab CR).</summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var v = value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return v.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }
}
