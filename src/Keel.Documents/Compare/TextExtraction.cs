using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Keel.Documents.Compare;

/// <summary>What could be read from a file: its text, or an honest reason why not. <see cref="Format"/> is a short code for the UI (text, markdown, docx, xlsx, pdf, other).</summary>
public sealed record ExtractedText(string Format, string? Text, string? Reason)
{
    public bool IsText => Text is not null;
    public static ExtractedText Of(string format, string text) => new(format, text, null);
    public static ExtractedText Not(string format, string reason) => new(format, null, reason);
}

/// <summary>
/// Reads the text of the formats a redline can compare (increment 9.5, ADR-0053): plain text and Markdown (UTF-8 or UTF-16), DOCX and XLSX (ZIP of XML, read with the
/// defences of the other in-house Office readers: bounded unpacking, no DTDs, no external entities, no macros), and PDF (PdfPig, Apache-2.0, managed code). Everything else
/// is "not comparable". Nothing is guessed: a file that cannot be read says why, and a PDF with no text layer is reported as such (there is no OCR).
/// </summary>
/// <remarks>
/// ASSUMPTION A70: DOCX comparison reads the text as the document currently shows it (tracked insertions included, tracked deletions excluded; tables as lines; no text
/// boxes, headers, footers, footnotes or comments). XLSX comparison lists every non-empty cell of every sheet as "Sheet!A1: value" and formulas as "=formula = cached value".
/// PDF comparison reads words in content order and joins each page into one paragraph, so re-wrapping lines is not a change; reading order of multi-column PDFs follows the
/// file's own drawing order, which may differ from the visual order.
/// </remarks>
public static class TextExtractor
{
    /// <summary>Bumped whenever extraction output for the same bytes can change; recorded on every review.</summary>
    public const int ExtractorVersion = 1;

    public const long MaxFileBytes = 50L * 1024 * 1024;
    public const int MaxChars = 3_000_000;
    private const long MaxEntryBytes = 60L * 1024 * 1024;
    private const int MaxZipEntries = 5000;
    private const int MaxCells = 500_000;
    private const int MaxPdfPages = 400;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".log", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".toml", ".rst", ".tex", ".sql", ".sh", ".py", ".cs", ".js", ".ts", ".c", ".h", ".cpp", ".java", ".html", ".htm", ".css",
    };

    /// <summary>The format code of a file by its name and media type: text, markdown, docx, xlsx, pdf, or other (not read).</summary>
    public static string FormatOf(string fileName, string? mediaType)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".md" or ".markdown" => "markdown", ".docx" => "docx", ".xlsx" => "xlsx", ".pdf" => "pdf",
            _ => TextExtensions.Contains(ext) || (mediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ?? false) ? "text" : "other",
        };
    }

    public static ExtractedText Extract(string fileName, string? mediaType, byte[] bytes)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        var format = FormatOf(fileName ?? "", mediaType);
        if (format == "other")
            return ExtractedText.Not("other", ext is ".doc" or ".xls" or ".ppt" or ".pptx" or ".odt" or ".ods"
                ? $"{ext} files are not read by the comparison; only text, Markdown, DOCX, XLSX and PDF are."
                : "This file type is not text Keel can read, so its content is not compared.");
        if (bytes.Length > MaxFileBytes) return ExtractedText.Not(format, $"The file is larger than {MaxFileBytes / 1024 / 1024} MB, too large to compare word by word.");
        if (bytes.Length == 0) return ExtractedText.Of(format, "");
        try
        {
            var result = format switch
            {
                "text" or "markdown" => PlainText(format, bytes),
                "docx" => Docx(bytes),
                "xlsx" => Xlsx(bytes),
                _ => Pdf(bytes),
            };
            if (result.Text is { Length: > MaxChars }) return ExtractedText.Not(format, $"The text is longer than {MaxChars:N0} characters, too long to compare word by word.");
            return result;
        }
        catch (Exception e) when (e is InvalidDataException or XmlException or InvalidOperationException or IOException or FormatException or ArgumentException or NotSupportedException or OverflowException
                                      or IndexOutOfRangeException or NullReferenceException or KeyNotFoundException or UnauthorizedAccessException or UglyToad.PdfPig.Core.PdfDocumentFormatException)
        {
            // Untrusted bytes: any parser failure is "cannot be read", never a crash and never a guess.
            return ExtractedText.Not(format, format switch
            {
                "pdf" => "The PDF could not be read (it may be damaged or password protected).",
                "docx" => "The Word file could not be read (it may be damaged, password protected or not a DOCX).",
                "xlsx" => "The workbook could not be read (it may be damaged, password protected or not an XLSX).",
                _ => "The file could not be read as text.",
            });
        }
    }

    // ---------------------------------------------------------------- plain text and Markdown

    private static ExtractedText PlainText(string format, byte[] bytes)
    {
        string text;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) text = StrictUtf8().GetString(bytes, 3, bytes.Length - 3);
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) text = new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2);
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) text = new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2);
        else
        {
            if (Array.IndexOf(bytes, (byte)0) >= 0) return ExtractedText.Not(format, "The file contains binary data (NUL bytes), so it is not read as text.");
            try { text = StrictUtf8().GetString(bytes); }
            catch (DecoderFallbackException) { return ExtractedText.Not(format, "The file is not valid UTF-8 or UTF-16 text, so it is not compared."); }
        }
        if (text.Contains('\0', StringComparison.Ordinal)) return ExtractedText.Not(format, "The file contains binary data (NUL characters), so it is not read as text.");
        return ExtractedText.Of(format, Normalize(text));
    }

    private static UTF8Encoding StrictUtf8() => new(false, true);

    internal static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    // ---------------------------------------------------------------- OOXML helpers

    private sealed class Budget(long max) { public long Remaining = max; }

    private static ZipArchive OpenZip(byte[] bytes, out Dictionary<string, ZipArchiveEntry> entries)
    {
        if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'K') throw new InvalidDataException("not a ZIP package");
        var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read, leaveOpen: false);
        try
        {
            if (zip.Entries.Count > MaxZipEntries) throw new InvalidDataException("too many parts");
            entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var e in zip.Entries)
            {
                if (e.FullName.Contains("..", StringComparison.Ordinal) || e.FullName.StartsWith('/')) throw new InvalidDataException("invalid part name");
                if (e.Length > MaxEntryBytes) throw new InvalidDataException("part too large");
                if (e.CompressedLength > 0 && e.Length / (double)e.CompressedLength > 200 && e.Length > 1024 * 1024) throw new InvalidDataException("suspicious compression");
                total += e.Length;
                if (total > 3 * MaxEntryBytes) throw new InvalidDataException("package too large");
                entries[e.FullName] = e;
            }
            return zip;
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    private static XmlReader Xml(ZipArchiveEntry entry, Budget budget)
    {
        var stream = new CappedStream(entry.Open(), Math.Min(MaxEntryBytes, budget.Remaining), budget);
        return XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0, MaxCharactersInDocument = MaxEntryBytes,
            IgnoreComments = true, IgnoreProcessingInstructions = true, CloseInput = true,
        });
    }

    /// <summary>Stops reading after a byte limit whatever the central directory claimed, so a ZIP bomb cannot exhaust memory.</summary>
    private sealed class CappedStream(Stream inner, long limit, Budget budget) : Stream
    {
        private long read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var allowed = (int)Math.Min(count, Math.Max(0, Math.Min(limit - read, budget.Remaining)));
            if (allowed == 0 && count > 0)
            {
                // At the limit: one more byte would be over it.
                if (inner.Read(new byte[1], 0, 1) > 0) throw new InvalidDataException("part larger than allowed");
                return 0;
            }
            var n = inner.Read(buffer, offset, allowed);
            read += n;
            budget.Remaining -= n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>The text of the current element, leaving the reader on its end tag (so the caller's loop reads on from there without skipping a sibling).</summary>
    private static string ElementText(XmlReader r)
    {
        if (r.IsEmptyElement) return "";
        var sb = new StringBuilder();
        using var sub = r.ReadSubtree();
        sub.Read();
        while (sub.Read())
            if (sub.NodeType is XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace or XmlNodeType.CDATA) sb.Append(sub.Value);
        return sb.ToString();
    }

    // ---------------------------------------------------------------- DOCX

    private static ExtractedText Docx(byte[] bytes)
    {
        using var zip = OpenZip(bytes, out var entries);
        if (entries.Keys.Any(k => k.Contains("EncryptedPackage", StringComparison.OrdinalIgnoreCase))) return ExtractedText.Not("docx", "The Word file is password protected.");
        if (!entries.TryGetValue("word/document.xml", out var main)) return ExtractedText.Not("docx", "This is not a Word document (word/document.xml is missing).");
        var budget = new Budget(3 * MaxEntryBytes);
        var sb = new StringBuilder();
        using var r = Xml(main, budget);
        while (r.Read())
        {
            if (sb.Length > MaxChars + 1000) break;
            if (r.NodeType == XmlNodeType.Element)
            {
                switch (r.LocalName)
                {
                    case "t":
                        sb.Append(ElementText(r));
                        break;
                    case "tab": sb.Append('\t'); break;
                    case "br" or "cr": sb.Append('\n'); break;
                    case "noBreakHyphen": sb.Append('-'); break;
                }
            }
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "p") sb.Append('\n');
        }
        return ExtractedText.Of("docx", Normalize(sb.ToString()).TrimEnd('\n'));
    }

    // ---------------------------------------------------------------- XLSX

    private static ExtractedText Xlsx(byte[] bytes)
    {
        using var zip = OpenZip(bytes, out var entries);
        if (entries.Keys.Any(k => k.Contains("EncryptedPackage", StringComparison.OrdinalIgnoreCase))) return ExtractedText.Not("xlsx", "The workbook is password protected.");
        if (!entries.TryGetValue("xl/workbook.xml", out var wb)) return ExtractedText.Not("xlsx", "This is not an Excel workbook (xl/workbook.xml is missing).");
        var budget = new Budget(3 * MaxEntryBytes);
        const string relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        var sheets = new List<(string Name, string RelId)>();
        using (var r = Xml(wb, budget))
            while (r.Read())
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "sheet" && r.GetAttribute("id", relNs) is { } id)
                    sheets.Add((r.GetAttribute("name") ?? "Sheet" + (sheets.Count + 1).ToString(CultureInfo.InvariantCulture), id));
        if (sheets.Count == 0) return ExtractedText.Not("xlsx", "The workbook has no sheets.");

        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (entries.TryGetValue("xl/_rels/workbook.xml.rels", out var rels))
            using (var r = Xml(rels, budget))
                while (r.Read())
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship" && r.GetAttribute("Id") is { } rid && r.GetAttribute("Target") is { } target)
                        targets[rid] = target;

        var shared = new List<string>();
        if (entries.TryGetValue("xl/sharedStrings.xml", out var ss))
            using (var r = Xml(ss, budget))
            {
                var current = new StringBuilder();
                var inSi = false;
                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "si") { inSi = true; current.Clear(); }
                    else if (r.NodeType == XmlNodeType.Element && r.LocalName == "t" && inSi) current.Append(ElementText(r));
                    else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "si") { shared.Add(current.ToString()); inSi = false; if (shared.Count > 2_000_000) throw new InvalidDataException("too many strings"); }
                }
            }

        var sb = new StringBuilder();
        var cells = 0;
        foreach (var (name, relId) in sheets)
        {
            if (!targets.TryGetValue(relId, out var target)) continue;
            var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            if (!entries.TryGetValue(path, out var sheet)) continue;
            sb.Append("[Sheet: ").Append(name).Append("]\n");
            using var r = Xml(sheet, budget);
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element || r.LocalName != "c") continue;
                var cellRef = r.GetAttribute("r") ?? "?";
                var type = r.GetAttribute("t");
                string? value = null, formula = null;
                if (!r.IsEmptyElement)
                {
                    using var cell = r.ReadSubtree();
                    cell.Read();
                    while (cell.Read())
                    {
                        if (cell.NodeType != XmlNodeType.Element) continue;
                        if (cell.LocalName == "v") value = ElementText(cell);
                        else if (cell.LocalName == "f") formula = ElementText(cell);
                        else if (cell.LocalName == "t" && type == "inlineStr") value = (value ?? "") + ElementText(cell);
                    }
                }
                if (type == "s" && value is not null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var idx)) value = idx < shared.Count ? shared[idx] : null;
                else if (type == "b" && value is not null) value = value == "1" ? "TRUE" : "FALSE";
                if (string.IsNullOrEmpty(value) && string.IsNullOrEmpty(formula)) continue;
                if (++cells > MaxCells) return ExtractedText.Not("xlsx", $"The workbook has more than {MaxCells:N0} filled cells, too many to compare.");
                var shown = (value ?? "").Replace('\n', ' ').Replace('\r', ' ');
                sb.Append(name).Append('!').Append(cellRef).Append(": ");
                sb.Append(formula is null ? shown : "=" + formula + (shown.Length > 0 ? " = " + shown : ""));
                sb.Append('\n');
                if (sb.Length > MaxChars + 1000) return ExtractedText.Not("xlsx", $"The text is longer than {MaxChars:N0} characters, too long to compare word by word.");
            }
        }
        return ExtractedText.Of("xlsx", sb.ToString().TrimEnd('\n'));
    }

    // ---------------------------------------------------------------- PDF

    private static readonly SemaphoreSlim PdfSlots = new(2, 2);

    private static ExtractedText Pdf(byte[] bytes)
    {
        if (bytes.Length < 5 || bytes[0] != '%' || bytes[1] != 'P' || bytes[2] != 'D' || bytes[3] != 'F') return ExtractedText.Not("pdf", "This file does not start like a PDF.");
        PdfSlots.Wait();
        try
        {
            using var doc = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            if (doc.NumberOfPages > MaxPdfPages) return ExtractedText.Not("pdf", $"The PDF has more than {MaxPdfPages} pages, too many to compare.");
            var sb = new StringBuilder();
            foreach (Page page in doc.GetPages())
            {
                var words = page.GetWords().Select(w => w.Text.Trim()).Where(w => w.Length > 0);
                var line = string.Join(' ', words);
                if (line.Length > 0) sb.Append(line).Append("\n\n");
                if (sb.Length > MaxChars + 1000) break;
            }
            var text = sb.ToString().TrimEnd('\n');
            return text.Length == 0
                ? ExtractedText.Not("pdf", "The PDF has no text layer (for example a scan). Keel does not read images, so its content is not compared.")
                : ExtractedText.Of("pdf", text);
        }
        finally
        {
            PdfSlots.Release();
        }
    }
}
