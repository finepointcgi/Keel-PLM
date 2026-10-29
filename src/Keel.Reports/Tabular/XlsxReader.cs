using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Keel.Reports.Tabular;

/// <summary>The workbook was refused: not a plain .xlsx, over a limit, or built to exhaust memory. The message is safe to show.</summary>
public sealed class XlsxFormatException(string message) : Exception(message);

public sealed record XlsxLimits(
    long MaxFileBytes = 5 * 1024 * 1024, int MaxEntries = 60, long MaxEntryBytes = 40 * 1024 * 1024, long MaxTotalBytes = 80 * 1024 * 1024,
    int MaxRatio = 100, int MaxRows = 20_000, int MaxColumns = 40, int MaxSharedStrings = 200_000, int MaxCellChars = 4_000);

/// <summary>
/// Reads the first sheet of an .xlsx as rows of text (ADR-0044). Defences, all enforced while reading and not only from declared sizes: file size, entry count,
/// per-entry and total decompressed bytes (the stream is cut, never trusted), compression ratio, no DTDs and no external entities, no macros or external links,
/// row, column and cell limits. Formulas are never evaluated: a formula cell yields its cached text or nothing. Cells are returned as text with the export's
/// leading apostrophe removed.
/// </summary>
public static class XlsxReader
{
    public static IReadOnlyList<IReadOnlyList<string>> ReadFirstSheet(byte[] file, XlsxLimits? limits = null)
    {
        limits ??= new XlsxLimits();
        if (file.Length == 0) throw new XlsxFormatException("The file is empty.");
        if (file.Length > limits.MaxFileBytes) throw new XlsxFormatException($"The file is larger than {limits.MaxFileBytes / 1024 / 1024} MB.");
        if (file.Length < 4 || file[0] != 'P' || file[1] != 'K') throw new XlsxFormatException("This is not an .xlsx workbook. Save the template as an Excel workbook (.xlsx).");
        ZipArchive zip;
        try { zip = new ZipArchive(new MemoryStream(file, writable: false), ZipArchiveMode.Read, leaveOpen: false); }
        catch (InvalidDataException) { throw new XlsxFormatException("The workbook is damaged."); }
        using (zip)
        {
            if (zip.Entries.Count > limits.MaxEntries) throw new XlsxFormatException("The workbook has too many parts.");
            long total = 0;
            var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in zip.Entries)
            {
                if (e.FullName.Contains("..", StringComparison.Ordinal) || e.FullName.StartsWith('/')) throw new XlsxFormatException("The workbook has an invalid part name.");
                if (e.FullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase) || e.FullName.Contains("externalLinks", StringComparison.OrdinalIgnoreCase) || e.FullName.Contains("embeddings", StringComparison.OrdinalIgnoreCase))
                    throw new XlsxFormatException("Workbooks with macros, external links or embedded objects are not accepted. Save a plain .xlsx.");
                if (e.Length > limits.MaxEntryBytes) throw new XlsxFormatException("A part of the workbook is too large when unpacked.");
                if (e.CompressedLength > 0 && e.Length / (double)e.CompressedLength > limits.MaxRatio && e.Length > 1024 * 1024) throw new XlsxFormatException("The workbook is compressed suspiciously well and was refused.");
                total += e.Length;
                if (total > limits.MaxTotalBytes) throw new XlsxFormatException("The workbook is too large when unpacked.");
                byName[e.FullName] = e;
            }
            var budget = new Budget(limits.MaxTotalBytes);
            if (!byName.TryGetValue("xl/workbook.xml", out var wbEntry)) throw new XlsxFormatException("This is not an Excel workbook (xl/workbook.xml is missing).");
            string? sheetRel = null;
            using (var r = Open(wbEntry, limits, budget))
                while (r.Read())
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "sheet") { sheetRel = r.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"); break; }
            if (sheetRel is null) throw new XlsxFormatException("The workbook has no sheet.");
            string? target = null;
            if (byName.TryGetValue("xl/_rels/workbook.xml.rels", out var relEntry))
                using (var r = Open(relEntry, limits, budget))
                    while (r.Read())
                        if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship" && r.GetAttribute("Id") == sheetRel) { target = r.GetAttribute("Target"); break; }
            target ??= "worksheets/sheet1.xml";
            var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            if (!byName.TryGetValue(path, out var sheetEntry)) throw new XlsxFormatException("The first sheet could not be found in the workbook.");

            var shared = new List<string>();
            if (byName.TryGetValue("xl/sharedStrings.xml", out var ssEntry)) shared = ReadShared(ssEntry, limits, budget);
            return ReadSheet(sheetEntry, shared, limits, budget);
        }
    }

    private sealed class Budget(long max) { public long Remaining = max; }

    private static XmlReader Open(ZipArchiveEntry entry, XlsxLimits limits, Budget budget)
    {
        // The decompressed stream is cut at the entry limit and the shared budget, whatever the central directory claims.
        var stream = new LimitedStream(entry.Open(), Math.Min(limits.MaxEntryBytes, budget.Remaining), budget);
        return XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0, MaxCharactersInDocument = limits.MaxEntryBytes,
            IgnoreComments = true, IgnoreProcessingInstructions = true, CloseInput = true,
        });
    }

    private static List<string> ReadShared(ZipArchiveEntry entry, XlsxLimits limits, Budget budget)
    {
        var list = new List<string>();
        try
        {
            using var r = Open(entry, limits, budget);
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element || r.LocalName != "si") continue;
                var sb = new StringBuilder();
                using (var si = r.ReadSubtree())
                {
                    si.Read();
                    while (!si.EOF)
                    {
                        if (si.NodeType == XmlNodeType.Element && si.LocalName == "t" && !si.IsEmptyElement)
                        {
                            var t = si.ReadElementContentAsString(); // leaves the reader after the element: do not Read again
                            if (sb.Length < limits.MaxCellChars) sb.Append(t);
                        }
                        else si.Read();
                    }
                }
                if (list.Count >= limits.MaxSharedStrings) throw new XlsxFormatException("The workbook has too many distinct texts.");
                list.Add(sb.Length > limits.MaxCellChars ? sb.ToString(0, limits.MaxCellChars) : sb.ToString());
            }
        }
        catch (XmlException) { throw new XlsxFormatException("The workbook contains damaged or unsupported XML."); }
        return list;
    }

    private static List<IReadOnlyList<string>> ReadSheet(ZipArchiveEntry entry, List<string> shared, XlsxLimits limits, Budget budget)
    {
        var rows = new List<IReadOnlyList<string>>();
        try
        {
            using var r = Open(entry, limits, budget);
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element || r.LocalName != "row") continue;
                if (rows.Count >= limits.MaxRows) throw new XlsxFormatException($"The sheet has more than {limits.MaxRows} rows. Split the file.");
                var rowIndex = int.TryParse(r.GetAttribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var ri) ? ri : rows.Count + 1;
                if (rowIndex > limits.MaxRows) throw new XlsxFormatException($"The sheet has more than {limits.MaxRows} rows. Split the file.");
                while (rows.Count < rowIndex - 1) rows.Add([]); // blank rows keep the row numbers of the sheet
                var cells = new List<string>();
                using (var row = r.ReadSubtree())
                {
                    row.Read();
                    row.Read();
                    while (!row.EOF)
                    {
                        if (row.NodeType != XmlNodeType.Element || row.LocalName != "c") { row.Read(); continue; }
                        var col = ColumnIndex(row.GetAttribute("r"), cells.Count);
                        if (col >= limits.MaxColumns) throw new XlsxFormatException($"The sheet has more than {limits.MaxColumns} columns.");
                        var type = row.GetAttribute("t");
                        var text = "";
                        using (var cell = row.ReadSubtree())
                        {
                            cell.Read();
                            while (!cell.EOF)
                            {
                                if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "v" && !cell.IsEmptyElement)
                                {
                                    var v = cell.ReadElementContentAsString();
                                    text = type == "s" ? (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var si) && si < shared.Count ? shared[si] : "") : v;
                                }
                                else if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "is" && !cell.IsEmptyElement)
                                {
                                    var sb = new StringBuilder();
                                    using (var inline = cell.ReadSubtree())
                                    {
                                        inline.Read();
                                        while (!inline.EOF)
                                        {
                                            if (inline.NodeType == XmlNodeType.Element && inline.LocalName == "t" && !inline.IsEmptyElement) sb.Append(inline.ReadElementContentAsString());
                                            else inline.Read();
                                        }
                                    }
                                    text = sb.ToString();
                                }
                                else cell.Read(); // <f> (formula) elements are skipped: only the cached value is read, nothing is evaluated
                            }
                        }
                        if (text.Length > limits.MaxCellChars) throw new XlsxFormatException($"A cell holds more than {limits.MaxCellChars} characters.");
                        while (cells.Count < col) cells.Add("");
                        cells.Add(CellSafety.Restore(text));
                        row.Read();
                    }
                }
                rows.Add(cells);
            }
        }
        catch (XmlException) { throw new XlsxFormatException("The workbook contains damaged or unsupported XML."); }
        return rows;
    }

    private static int ColumnIndex(string? reference, int fallback)
    {
        if (string.IsNullOrEmpty(reference)) return fallback;
        var n = 0;
        foreach (var ch in reference)
        {
            if (ch is < 'A' or > 'Z') break;
            n = n * 26 + (ch - 'A' + 1);
            if (n > 100_000) return fallback;
        }
        return n == 0 ? fallback : n - 1;
    }

    /// <summary>Reads at most <c>limit</c> bytes and fails (instead of reading on) when the entry decompresses to more.</summary>
    private sealed class LimitedStream(Stream inner, long limit, Budget budget) : Stream
    {
        private long read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            read += n;
            budget.Remaining -= n;
            if (read > limit || budget.Remaining < 0) throw new XlsxFormatException("The workbook is too large when unpacked.");
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
