using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Keel.Core;

namespace Keel.Bom;

/// <summary>Pure validation and hashing rules of manufacturing BOMs (ADR-0024). Database state (cycles, existence, access) is checked by <see cref="MbomService"/>.</summary>
public static partial class MbomRules
{
    public static readonly Regex IterationLabel = IterationLabelPattern();

    /// <summary>
    /// Validates one MBOM line. Quantity, unit, find number, designators and notes follow the EBOM line rules, except that the designator count is not tied to the
    /// quantity (a manufacturing line may group designators differently); scrap is a non-negative factor, the operation reference is a short free-text key.
    /// </summary>
    public static (IReadOnlyList<string> Errors, string[] Designators) ValidateLine(
        decimal quantity, string? unit, IReadOnlyList<string>? designators, string? notes, int? findNumber, decimal? scrap, string? operationReference,
        string? supplyType, IReadOnlyCollection<string> allowedUnits, IReadOnlyCollection<string> allowedSupplyTypes)
    {
        var errors = new List<string>();
        if (unit is null || !allowedUnits.Contains(unit)) errors.Add($"Unit '{unit}' is not valid; use one of: {string.Join(", ", allowedUnits)}.");
        if (quantity <= 0) errors.Add("Quantity must be greater than zero.");
        else if (quantity > BomLimits.MaxQuantity) errors.Add($"Quantity must be at most {BomLimits.MaxQuantity.ToString("N0", CultureInfo.InvariantCulture)}.");
        else if (decimal.Round(quantity, 6) != quantity) errors.Add("Quantity may have at most 6 decimal places.");
        if (unit == UnitsOfMeasure.Each && quantity > 0 && quantity != decimal.Truncate(quantity)) errors.Add("Quantity must be a whole number for unit EA.");
        if (findNumber is { } f && (f < 1 || f > BomLimits.MaxFindNumber)) errors.Add($"Find number must be between 1 and {BomLimits.MaxFindNumber}.");
        if ((notes?.Length ?? 0) > 1000) errors.Add("Notes must be at most 1000 characters.");
        if (scrap is { } s && (s < 0 || s > MbomLimits.MaxScrap)) errors.Add($"The scrap factor is a fraction between 0 and {MbomLimits.MaxScrap.ToString("0", CultureInfo.InvariantCulture)} (0.05 means 5 percent planned loss).");
        else if (scrap is { } s2 && decimal.Round(s2, 6) != s2) errors.Add("The scrap factor may have at most 6 decimal places.");
        if (operationReference is { } op && (op.Trim().Length is < 1 or > MbomLimits.MaxOperationRef || OperationRefPattern().IsMatch(op.Trim()) is false))
            errors.Add($"The operation reference is a key of the ERP or MES routing operation: 1 to {MbomLimits.MaxOperationRef} letters, digits and . _ - / (the routing itself stays in ERP).");
        if (supplyType is null || !allowedSupplyTypes.Contains(supplyType)) errors.Add($"Supply type must be one of: {string.Join(", ", allowedSupplyTypes)}.");

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
        return (errors, [.. list]);
    }

    /// <summary>Validates one link request against the line it belongs to and the EBOM line it names. Part identity checks need the lines, so they take them.</summary>
    public static IReadOnlyList<string> ValidateLink(string status, string? reason, bool lineIsMbomOnly)
    {
        var errors = new List<string>();
        if (!LinkStatuses.All.Contains(status)) errors.Add($"Link status must be one of: {string.Join(", ", LinkStatuses.All)}.");
        var needsReason = status is LinkStatuses.QuantityAdjusted or LinkStatuses.Substituted;
        if (needsReason && string.IsNullOrWhiteSpace(reason)) errors.Add($"A link with status '{status}' needs a reason.");
        if (status == LinkStatuses.Matched && !string.IsNullOrWhiteSpace(reason)) errors.Add("A matched link carries no reason; use quantity_adjusted or substituted to declare a difference.");
        if ((reason?.Trim().Length ?? 0) > MbomLimits.MaxReasonLength) errors.Add($"The reason must be at most {MbomLimits.MaxReasonLength} characters.");
        if (lineIsMbomOnly) errors.Add("A line flagged MBOM-only has no EBOM source; remove the flag to link it.");
        return errors;
    }

    /// <summary>
    /// Default supply type of a generated line, from the child part: a part of type Consumable is consumed, a Buy part is bought, anything else
    /// is made. The engineer can change it on the line.
    /// </summary>
    public static string DefaultSupplyType(string partType, string source) =>
        string.Equals(partType, "Consumable", StringComparison.Ordinal) ? "Consume" : string.Equals(source, "Buy", StringComparison.Ordinal) ? "Buy" : "Make";

    /// <summary>The net quantity requirement with planned scrap: <c>quantity * (1 + scrap)</c>. Rounded to 6 places like every quantity.</summary>
    public static decimal Gross(decimal quantity, decimal? scrap) => decimal.Round(quantity * (1m + (scrap ?? 0m)), 6);

    // ---------------------------------------------------------------- canonical content hash

    /// <summary>One line as the content hash sees it. The line is identified by (find number, alternate), not by row id, so a copy has the same hash.</summary>
    public sealed record HashLine(
        int Find, int Alt, Guid Child, decimal Quantity, string Unit, string Designators, decimal? Scrap, bool Phantom, string SupplyType, string? OperationRef, bool MbomOnly,
        string? Notes, string Effectivity);

    public sealed record HashLink(int Find, int Alt, Guid EbomUsage, string Status, string? Reason);

    /// <summary>
    /// SHA-256 over the canonical lines, links and derivation of one iteration. The same function runs when an iteration is written and when it is
    /// verified (release review, tamper check), so changing a stored line or link by any means changes the digest.
    /// </summary>
    public static string ContentHash(Guid? derivedFrom, IEnumerable<HashLine> lines, IEnumerable<HashLink> links)
    {
        var sb = new StringBuilder(1 << 14);
        sb.Append("keel-mbom/1|").Append(derivedFrom?.ToString("N") ?? "-").Append('\n');
        foreach (var l in lines.OrderBy(x => x.Find).ThenBy(x => x.Alt))
            sb.Append("l|").Append(l.Find.ToString(CultureInfo.InvariantCulture)).Append('|').Append(l.Alt.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(l.Child.ToString("N")).Append('|').Append(MbomText.Qty(l.Quantity)).Append('|').Append(l.Unit).Append('|').Append(l.Designators).Append('|')
              .Append(l.Scrap is { } s ? MbomText.Qty(s) : "").Append('|').Append(l.Phantom ? '1' : '0').Append('|').Append(l.SupplyType).Append('|').Append(l.OperationRef).Append('|')
              .Append(l.MbomOnly ? '1' : '0').Append('|').Append(l.Notes).Append('|').Append(l.Effectivity).Append('\n');
        foreach (var k in links.OrderBy(x => x.Find).ThenBy(x => x.Alt).ThenBy(x => x.EbomUsage))
            sb.Append("k|").Append(k.Find.ToString(CultureInfo.InvariantCulture)).Append('|').Append(k.Alt.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(k.EbomUsage.ToString("N")).Append('|').Append(k.Status).Append('|').Append(k.Reason).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public static string EffectivityText(EffectivityRange? r) =>
        r is null ? "" : $"{r.Kind}:{r.Context}:{r.From?.ToString(CultureInfo.InvariantCulture)}:{r.To?.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The digest a release review signs: the iteration content, the reconciliation run it was released with and that run's own digest.</summary>
    public static string ReviewHash(string iterationSha256, Guid runId, string runSha256) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"keel-mbom-review/1|{iterationSha256}|{runId:N}|{runSha256}")));

    [GeneratedRegex(@"^([A-Z]{1,3})\.([0-9]{1,6})$")]
    private static partial Regex IterationLabelPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-/]{0,59}$")]
    private static partial Regex OperationRefPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-/]{0,31}$")]
    private static partial Regex DesignatorPattern();
}
