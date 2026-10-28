using System.Globalization;
using Keel.Bom;
using Keel.Core;

namespace Keel.Web.Services;

/// <summary>Editable form state for one effectivity range; blank type means "no effectivity". Parsing and validation are the service's job.</summary>
public sealed class EffectivityForm
{
    public string Kind { get; set; } = "";
    public string Context { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";

    public bool IsBlank => string.IsNullOrWhiteSpace(Kind);

    public EffectivityInput? ToInput() =>
        IsBlank ? null : new EffectivityInput(Kind, string.IsNullOrWhiteSpace(Context) ? null : Context.Trim(), string.IsNullOrWhiteSpace(From) ? null : From.Trim(), string.IsNullOrWhiteSpace(To) ? null : To.Trim());

    public static EffectivityForm FromRange(EffectivityRange? r) => r is null ? new() : new()
    {
        Kind = r.Kind,
        Context = r.Context ?? "",
        From = Text(r, r.From),
        To = Text(r, r.To),
    };

    private static string Text(EffectivityRange r, long? v) =>
        v is null ? "" : r.Kind == EffectivityKinds.Date ? EffectivityRange.DayOf(v.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : v.Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The unit a structure is resolved for (the "as of serial / lot / date" selector). Values are typed as text and checked here.</summary>
public sealed class UnitForm
{
    public string Context { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Lot { get; set; } = "";
    public string Date { get; set; } = "";

    public bool IsBlank => string.IsNullOrWhiteSpace(Serial) && string.IsNullOrWhiteSpace(Lot) && string.IsNullOrWhiteSpace(Date);

    /// <summary>The request, or an error in words. Range checks beyond number and date syntax are the service's.</summary>
    public (EffectivityRequest? Request, string? Error) ToRequest()
    {
        if (IsBlank) return (null, "Give a serial number, a lot or a date.");
        long? serial = null, lot = null, day = null;
        if (!string.IsNullOrWhiteSpace(Serial))
        {
            if (!long.TryParse(Serial.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var s)) return (null, "The serial number must be a whole number.");
            serial = s;
        }
        if (!string.IsNullOrWhiteSpace(Lot))
        {
            if (!long.TryParse(Lot.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var l)) return (null, "The lot must be a whole number.");
            lot = l;
        }
        if (!string.IsNullOrWhiteSpace(Date))
        {
            if (!DateOnly.TryParseExact(Date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return (null, "The date must be written yyyy-MM-dd (UTC).");
            day = EffectivityRange.DayNumber(d);
        }
        var request = new EffectivityRequest(string.IsNullOrWhiteSpace(Context) ? null : Context.Trim().ToUpperInvariant(), serial, lot, day);
        var errors = request.Validate();
        return errors.Count > 0 ? (null, string.Join(" ", errors)) : (request, null);
    }

    /// <summary>The query-string form used by the cookie-authenticated download routes.</summary>
    public string Query(string suffix = "") =>
        $"context={Uri.EscapeDataString(Context.Trim())}&serial{suffix}={Uri.EscapeDataString(Serial.Trim())}&lot{suffix}={Uri.EscapeDataString(Lot.Trim())}&date{suffix}={Uri.EscapeDataString(Date.Trim())}";
}

/// <summary>Wording for baselines and effectivity in the UI.</summary>
public static class ConfigurationText
{
    public static string StateClass(string state) => "state-" + state.ToLowerInvariant();

    public static string ChangeKind(BomChangeKind kind) => kind switch
    {
        BomChangeKind.Added => "Added",
        BomChangeKind.Removed => "Removed",
        _ => "Changed",
    };

    public static string Spec(BomConfigSpec spec) => spec switch
    {
        BomConfigSpec.Latest => "latest",
        BomConfigSpec.LatestReleased => "latest released",
        BomConfigSpec.Effectivity => "as of a unit",
        _ => "baseline",
    };
}
