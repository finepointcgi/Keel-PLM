using System.Text;

namespace Keel.Reports.Tabular;

/// <summary>RFC 4180 CSV with UTF-8 BOM, CRLF, quoted when needed, and every text cell neutralized against formula injection.</summary>
public static class Csv
{
    public static byte[] Write(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<Cell>> rows, IReadOnlyList<string>? preface = null)
    {
        var sb = new StringBuilder();
        foreach (var line in preface ?? []) Row(sb, [CellSafety.Neutralize(line)]);
        Row(sb, [.. header.Select(CellSafety.Neutralize)]);
        foreach (var row in rows) Row(sb, [.. row.Select(c => c.IsNumber ? c.Number!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : CellSafety.Neutralize(c.Text))]);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static void Row(StringBuilder sb, IReadOnlyList<string> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var c = cells[i];
            if (c.IndexOfAny([',', '"', '\r', '\n']) >= 0) sb.Append('"').Append(c.Replace("\"", "\"\"")).Append('"');
            else sb.Append(c);
        }
        sb.Append("\r\n");
    }
}

/// <summary>A table cell: text or a number. Text is neutralized by the writers.</summary>
public readonly record struct Cell(string? Text, decimal? Number = null)
{
    public bool IsNumber => Number.HasValue;
    public static Cell T(string? text) => new(text);
    public static Cell N(decimal n) => new(null, n);
    public static implicit operator Cell(string? text) => new(text);
}
