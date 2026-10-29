using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Keel.Reports.Tabular;

public sealed record SheetData(string Name, IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<Cell>> Rows, IReadOnlyList<string>? Preface = null);

/// <summary>
/// A minimal .xlsx writer (ADR-0044): a ZIP of a few XML parts, text as inline strings (never formulas), numbers as numbers, a bold header row. In-house over
/// System.IO.Compression, so there is no third-party package to license, patch or vendor. Every text cell is neutralized against formula injection.
/// </summary>
public static class XlsxWriter
{
    public const int MaxRows = 200_000;
    public const int MaxColumns = 64;

    public static byte[] Write(IReadOnlyList<SheetData> sheets)
    {
        if (sheets.Count is 0 or > 16) throw new ArgumentException("A workbook has 1 to 16 sheets.", nameof(sheets));
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            Add(zip, "[Content_Types].xml", w =>
            {
                w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                Default(w, "rels", "application/vnd.openxmlformats-package.relationships+xml");
                Default(w, "xml", "application/xml");
                Override(w, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
                Override(w, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
                for (var i = 1; i <= sheets.Count; i++) Override(w, $"/xl/worksheets/sheet{i}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                w.WriteEndElement();
            });
            Add(zip, "_rels/.rels", w =>
            {
                w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                Rel(w, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
                w.WriteEndElement();
            });
            Add(zip, "xl/workbook.xml", w =>
            {
                w.WriteStartElement("workbook", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                w.WriteAttributeString("xmlns", "r", null, "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                w.WriteStartElement("sheets");
                for (var i = 0; i < sheets.Count; i++)
                {
                    w.WriteStartElement("sheet");
                    w.WriteAttributeString("name", SheetName(sheets[i].Name, i));
                    w.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships", $"rId{i + 1}");
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                w.WriteEndElement();
            });
            Add(zip, "xl/_rels/workbook.xml.rels", w =>
            {
                w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                for (var i = 1; i <= sheets.Count; i++) Rel(w, $"rId{i}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", $"worksheets/sheet{i}.xml");
                Rel(w, "rId" + (sheets.Count + 1), "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml");
                w.WriteEndElement();
            });
            Add(zip, "xl/styles.xml", w =>
            {
                w.WriteStartElement("styleSheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                w.WriteStartElement("fonts"); w.WriteAttributeString("count", "2");
                w.WriteStartElement("font"); w.WriteStartElement("sz"); w.WriteAttributeString("val", "11"); w.WriteEndElement(); w.WriteStartElement("name"); w.WriteAttributeString("val", "Calibri"); w.WriteEndElement(); w.WriteEndElement();
                w.WriteStartElement("font"); w.WriteElementString("b", ""); w.WriteStartElement("sz"); w.WriteAttributeString("val", "11"); w.WriteEndElement(); w.WriteStartElement("name"); w.WriteAttributeString("val", "Calibri"); w.WriteEndElement(); w.WriteEndElement();
                w.WriteEndElement();
                w.WriteStartElement("fills"); w.WriteAttributeString("count", "2");
                w.WriteStartElement("fill"); w.WriteStartElement("patternFill"); w.WriteAttributeString("patternType", "none"); w.WriteEndElement(); w.WriteEndElement();
                w.WriteStartElement("fill"); w.WriteStartElement("patternFill"); w.WriteAttributeString("patternType", "gray125"); w.WriteEndElement(); w.WriteEndElement();
                w.WriteEndElement();
                w.WriteStartElement("borders"); w.WriteAttributeString("count", "1"); w.WriteStartElement("border"); w.WriteEndElement(); w.WriteEndElement();
                w.WriteStartElement("cellStyleXfs"); w.WriteAttributeString("count", "1"); w.WriteStartElement("xf"); w.WriteEndElement(); w.WriteEndElement();
                w.WriteStartElement("cellXfs"); w.WriteAttributeString("count", "2");
                w.WriteStartElement("xf"); w.WriteAttributeString("fontId", "0"); w.WriteEndElement();
                w.WriteStartElement("xf"); w.WriteAttributeString("fontId", "1"); w.WriteAttributeString("applyFont", "1"); w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();
            });
            for (var i = 0; i < sheets.Count; i++) AddSheet(zip, i + 1, sheets[i]);
        }
        return ms.ToArray();
    }

    private static void AddSheet(ZipArchive zip, int index, SheetData sheet)
    {
        if (sheet.Rows.Count > MaxRows) throw new ArgumentException($"A sheet holds at most {MaxRows} rows.");
        if (sheet.Header.Count > MaxColumns) throw new ArgumentException($"A sheet holds at most {MaxColumns} columns.");
        Add(zip, $"xl/worksheets/sheet{index}.xml", w =>
        {
            w.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            w.WriteStartElement("sheetData");
            var r = 1;
            foreach (var line in sheet.Preface ?? []) { WriteRow(w, r++, [Cell.T(line)], false); }
            WriteRow(w, r++, [.. sheet.Header.Select(Cell.T)], true);
            foreach (var row in sheet.Rows) WriteRow(w, r++, row, false);
            w.WriteEndElement();
            w.WriteEndElement();
        });
    }

    private static void WriteRow(XmlWriter w, int r, IReadOnlyList<Cell> cells, bool header)
    {
        w.WriteStartElement("row");
        w.WriteAttributeString("r", r.ToString(CultureInfo.InvariantCulture));
        for (var c = 0; c < cells.Count; c++)
        {
            var cell = cells[c];
            w.WriteStartElement("c");
            w.WriteAttributeString("r", ColumnName(c) + r.ToString(CultureInfo.InvariantCulture));
            if (header) w.WriteAttributeString("s", "1");
            if (cell.IsNumber)
            {
                w.WriteElementString("v", cell.Number!.Value.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                w.WriteAttributeString("t", "inlineStr");
                w.WriteStartElement("is");
                w.WriteStartElement("t");
                w.WriteAttributeString("xml", "space", null, "preserve");
                w.WriteString(CellSafety.Neutralize(cell.Text));
                w.WriteEndElement();
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    public static string ColumnName(int zeroBased)
    {
        var s = "";
        for (var n = zeroBased + 1; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    private static string SheetName(string name, int i)
    {
        var clean = new string([.. name.Where(c => c is not ('\\' or '/' or '?' or '*' or '[' or ']' or ':'))]).Trim('\'');
        if (clean.Length == 0) clean = "Sheet" + (i + 1);
        return clean.Length > 31 ? clean[..31] : clean;
    }

    private static void Default(XmlWriter w, string ext, string type) { w.WriteStartElement("Default"); w.WriteAttributeString("Extension", ext); w.WriteAttributeString("ContentType", type); w.WriteEndElement(); }
    private static void Override(XmlWriter w, string part, string type) { w.WriteStartElement("Override"); w.WriteAttributeString("PartName", part); w.WriteAttributeString("ContentType", type); w.WriteEndElement(); }
    private static void Rel(XmlWriter w, string id, string type, string target) { w.WriteStartElement("Relationship"); w.WriteAttributeString("Id", id); w.WriteAttributeString("Type", type); w.WriteAttributeString("Target", target); w.WriteEndElement(); }

    private static void Add(ZipArchive zip, string name, Action<XmlWriter> body)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var s = entry.Open();
        using var w = XmlWriter.Create(s, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false });
        w.WriteStartDocument(true);
        body(w);
        w.WriteEndDocument();
    }
}
