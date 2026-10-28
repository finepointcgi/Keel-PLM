using System.Globalization;
using System.Text;

namespace Keel.Seed;

/// <summary>
/// Writes small, valid, single-page PDF files (base-14 Helvetica, no embedded fonts) for the demo documents, so the marked-download
/// path (PDF stamping) has real PDFs to work on without shipping binary fixtures. The text says plainly that the content is synthetic.
/// </summary>
public static class SyntheticPdf
{
    public const string Notice = "SYNTHETIC DEMONSTRATION DATA - NOT REAL ENGINEERING DATA";

    public static byte[] Create(string title, IEnumerable<string> lines)
    {
        var text = new StringBuilder();
        text.Append("BT /F1 16 Tf 72 720 Td (").Append(Escape(title)).Append(") Tj ET\n");
        text.Append("BT /F1 9 Tf 72 700 Td (").Append(Escape(Notice)).Append(") Tj ET\n");
        var y = 670;
        foreach (var line in lines.Take(40))
        {
            text.Append(CultureInfo.InvariantCulture, $"BT /F1 11 Tf 72 {y} Td (").Append(Escape(line)).Append(") Tj ET\n");
            y -= 16;
        }
        var content = text.ToString();
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) pdf.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    // Only printable ASCII goes into the content stream; anything else becomes '?'.
    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c is '(' or ')' or '\\' ? "\\" + c : c is < ' ' or > '~' ? "?" : c.ToString());
        return sb.ToString();
    }
}
