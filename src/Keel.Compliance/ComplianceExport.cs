using System.Globalization;
using System.Text;
using System.Xml;

namespace Keel.Compliance;

public static class ExportFormats
{
    public const string Cmrt = "cmrt";
    public const string Ipc1752 = "ipc1752";
}

/// <summary>
/// File exports of a compliance report (ADR-0040). The CSV follows the shape of a conflict minerals reporting template (one row per purchased part with the 3TG status and the
/// declaration behind it); the XML is a material declaration in the spirit of IPC-1752 (items, declarations, composition per substance, standing per regulation) but is
/// NOT a conformant IPC-1752A or B document: its own schema is documented in docs/compliance/exports.md. Both are built only from the report, which holds only what the
/// caller may read. Cell text that a spreadsheet would run as a formula is neutralized; XML text is escaped and stripped of characters XML cannot carry.
/// </summary>
public static class ComplianceExport
{
    public static readonly string[] CmrtHeader =
    [
        "Part number", "Revision", "Description", "Quantity", "Unit", "Declaration", "Declared on", "Expires on", "Supplier reference", "Manufacturer part reference",
        "Tin", "Tantalum", "Tungsten", "Gold", "Conflict-free status",
    ];

    public static ExportFile Cmrt(ComplianceReport report)
    {
        var sb = new StringBuilder();
        sb.Append(Row(CmrtHeader));
        foreach (var l in report.Lines.Where(l => l.Required))
        {
            string M(string mineral) => MineralText(l.Minerals.GetValueOrDefault(mineral, "no_declaration"));
            var all = Minerals.All.Select(m => l.Minerals.GetValueOrDefault(m, "no_declaration")).ToList();
            var overall = all.Any(s => s == "not_conflict_free") ? "Not conflict-free"
                : all.Any(s => s is "unknown" or "no_declaration") ? "Unknown"
                : "Conflict-free";
            sb.Append(Row(
            [
                l.Number, l.Revision ?? "", l.Name, ComplianceRules.Num(l.Quantity), l.Unit, string.Join(" ", l.Declarations), l.DeclaredOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                l.ExpiresOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", l.SupplierRef ?? "", l.MpnRef ?? "", M("tin"), M("tantalum"), M("tungsten"), M("gold"), overall,
            ]));
        }
        return new ExportFile(sb.ToString(), "text/csv; charset=utf-8", $"{SafeName(report.Number)}-cmrt.csv");
    }

    private static string MineralText(string state) => state switch
    {
        "none" => "No",
        "conflict_free" => "Yes, conflict-free",
        "not_conflict_free" => "Yes, not conflict-free",
        "unknown" => "Yes, status unknown",
        _ => "No declaration",
    };

    private static string Row(IEnumerable<string> cells) => string.Join(',', cells.Select(Cell)) + "\r\n";

    /// <summary>One CSV cell: quoted when needed, and a leading formula character is neutralized so spreadsheets do not run it.</summary>
    public static string Cell(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }

    public static ExportFile Ipc1752(ComplianceReport report)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false, Encoding = new UTF8Encoding(false), CheckCharacters = true };
        using (var w = XmlWriter.Create(new Utf8StringWriter(sb), settings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("MaterialDeclaration");
            w.WriteAttributeString("format", "keel-ipc1752-like/1");
            w.WriteAttributeString("asOf", report.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            w.WriteAttributeString("marking", X(report.Banner));
            w.WriteStartElement("Product");
            w.WriteAttributeString("mode", X(report.Mode));
            w.WriteAttributeString("number", X(report.Number));
            w.WriteAttributeString("name", X(report.Name));
            w.WriteEndElement();
            w.WriteStartElement("Regulations");
            foreach (var r in report.Regulations)
            {
                var s = report.Summaries.First(x => x.Code == r.Code);
                w.WriteStartElement("Regulation");
                w.WriteAttributeString("code", X(r.Code));
                w.WriteAttributeString("name", X(r.Name));
                w.WriteAttributeString("kind", X(r.Kind));
                w.WriteAttributeString("version", r.Version.ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("effectiveFrom", r.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                w.WriteAttributeString("standing", s.Standing);
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteStartElement("Items");
            foreach (var l in report.Lines.Where(l => l.Required))
            {
                w.WriteStartElement("Item");
                w.WriteAttributeString("path", X(l.Path));
                w.WriteAttributeString("partNumber", X(l.Number));
                if (l.Revision is not null) w.WriteAttributeString("revision", X(l.Revision));
                w.WriteAttributeString("name", X(l.Name));
                w.WriteAttributeString("quantity", ComplianceRules.Num(l.Quantity));
                w.WriteAttributeString("unit", X(l.Unit));
                foreach (var (code, st) in l.Standings.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    w.WriteStartElement("Compliance");
                    w.WriteAttributeString("regulation", X(code));
                    w.WriteAttributeString("standing", st.Standing);
                    foreach (var f in st.Findings)
                    {
                        w.WriteStartElement("Finding");
                        w.WriteAttributeString("cas", X(f.CasNumber));
                        w.WriteAttributeString("reason", f.Reason);
                        w.WriteAttributeString("massPercent", ComplianceRules.Num(f.Percent));
                        w.WriteAttributeString("thresholdPercent", ComplianceRules.Num(f.ThresholdPct));
                        if (f.ExemptionCode is not null) w.WriteAttributeString("exemption", X(f.ExemptionCode));
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                }
                foreach (var d in l.Declarations)
                {
                    w.WriteStartElement("Declaration");
                    w.WriteAttributeString("number", X(d));
                    if (l.DeclaredOn is { } on) w.WriteAttributeString("declaredOn", on.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    if (l.ExpiresOn is { } ex) w.WriteAttributeString("expiresOn", ex.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    if (l.SupplierRef is not null) w.WriteAttributeString("supplierRef", X(l.SupplierRef));
                    if (l.MpnRef is not null) w.WriteAttributeString("manufacturerPartRef", X(l.MpnRef));
                    w.WriteStartElement("Composition");
                    foreach (var c in l.Composition.Where(c => c.DeclarationNumber == d))
                    {
                        w.WriteStartElement("Substance");
                        w.WriteAttributeString("cas", X(c.CasNumber));
                        w.WriteAttributeString("name", X(c.SubstanceName));
                        if (c.Percent is { } p) w.WriteAttributeString("massPercent", ComplianceRules.Num(p));
                        if (c.MassMg is { } m) w.WriteAttributeString("massMg", ComplianceRules.Num(m));
                        if (c.ExemptionCode is not null) w.WriteAttributeString("exemption", X(c.ExemptionCode));
                        if (c.ConflictFree is { } cf) w.WriteAttributeString("conflictFree", cf ? "true" : "false");
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndDocument();
        }
        return new ExportFile(sb.ToString(), "application/xml; charset=utf-8", $"{SafeName(report.Number)}-ipc1752.xml");
    }

    /// <summary>Text with the characters XML cannot carry removed.</summary>
    private static string X(string? s) => s is null ? "" : new string([.. s.Where(c => XmlConvert.IsXmlChar(c))]);

    private static string SafeName(string s)
    {
        var cleaned = new string([.. s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')]);
        return cleaned.Length == 0 ? "compliance" : cleaned;
    }

    /// <summary>A writer into a string builder that reports UTF-8, so the XML declaration says what the file is.</summary>
    private sealed class Utf8StringWriter(StringBuilder sb) : StringWriter(sb, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
