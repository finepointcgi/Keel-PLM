namespace Keel.Reports.Tabular;

/// <summary>
/// Spreadsheet formula injection (CSV injection) defence for every text cell written to .xlsx or CSV (ADR-0044): a value that a spreadsheet could read as a formula
/// (it starts with =, +, -, @, tab or carriage return) gets a leading apostrophe, which spreadsheets show as plain text. Numbers are written as numbers, never as text.
/// </summary>
public static class CellSafety
{
    public static bool LooksLikeFormula(string s) => s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n';

    public static string Neutralize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var clean = StripControl(value);
        return LooksLikeFormula(clean) ? "'" + clean : clean;
    }

    /// <summary>Undoes <see cref="Neutralize"/> on import so an exported workbook round-trips: an apostrophe in front of a formula trigger is dropped.</summary>
    public static string Restore(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length > 1 && value[0] == '\'' && value[1] is '=' or '+' or '-' or '@' ? value[1..] : value;
    }

    /// <summary>Removes characters XML 1.0 cannot carry (and the NUL family) so the workbook is always well formed.</summary>
    public static string StripControl(string value)
    {
        var needs = false;
        foreach (var c in value) if (c < 0x20 && c is not '\t' and not '\n' and not '\r' || c is '￾' or '￿') { needs = true; break; }
        if (!needs) return value;
        return string.Create(value.Length, value, static (span, v) =>
        {
            for (var i = 0; i < v.Length; i++) span[i] = v[i] < 0x20 && v[i] is not '\t' and not '\n' and not '\r' || v[i] is '￾' or '￿' ? ' ' : v[i];
        });
    }
}
