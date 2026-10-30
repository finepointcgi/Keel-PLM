using System.Text;

namespace Keel.Reports.Tabular;

/// <summary>Reads an uploaded table: UTF-8 CSV here, .xlsx through <see cref="XlsxReader"/>. Both give rows of text with the export's neutralizing apostrophe removed (ADR-0044).</summary>
public static class TableReader
{
    public static IReadOnlyList<IReadOnlyList<string>> Read(string fileName, byte[] content) =>
        fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? ReadCsv(content) : XlsxReader.ReadFirstSheet(content);

    public static List<IReadOnlyList<string>> ReadCsv(byte[] content)
    {
        if (content.Length > 5 * 1024 * 1024) throw new XlsxFormatException("The file is larger than 5 MB.");
        var text = Encoding.UTF8.GetString(content).TrimStart('﻿');
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>(); var sb = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted) { if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; } else quoted = false; } else sb.Append(ch); continue; }
            switch (ch)
            {
                case '"': quoted = true; break;
                case ',': row.Add(CellSafety.Restore(sb.ToString())); sb.Clear(); break;
                case '\r': break;
                case '\n': row.Add(CellSafety.Restore(sb.ToString())); sb.Clear(); rows.Add(row); row = []; if (rows.Count > 21_000) throw new XlsxFormatException("The file has too many rows."); break;
                default: sb.Append(ch); break;
            }
        }
        if (sb.Length > 0 || row.Count > 0) { row.Add(CellSafety.Restore(sb.ToString())); rows.Add(row); }
        return rows;
    }
}
