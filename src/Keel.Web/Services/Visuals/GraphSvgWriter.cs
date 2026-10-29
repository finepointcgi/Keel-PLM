using System.Globalization;
using System.Security;
using System.Text;
using Keel.Documents.Marking;

namespace Keel.Web.Services.Visuals;

/// <summary>
/// Writes the impact map as ONE self-contained SVG file for download or printing (UX-3, ADR-0029): no script, no style sheet, no external reference, only presentation
/// attributes, so it renders the same in any viewer. The marking banner (top and bottom bar) and the distribution statement are part of the picture, taken from the
/// banner of the nodes that are drawn. Shapes and hatching distinguish the kinds, so a black-and-white print keeps its meaning. Restricted items are drawn as
/// "Restricted item" and carry nothing else.
/// </summary>
public static class GraphSvgWriter
{
    private const double BarHeight = 28;
    private const double Footer = 96;

    public static string Write(ImpactGraph graph, GraphLayout layout, string title, string generatedBy, DateTimeOffset generatedAt)
    {
        var width = Math.Max(layout.Width, 760);
        var top = BarHeight + 40;
        var height = top + layout.Height + Footer + BarHeight;
        var banner = graph.Banner;
        var tone = banner.StartsWith("CUI", StringComparison.Ordinal) ? "#502b85" : "#007a33";
        var byId = layout.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        var sb = new StringBuilder();
        string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(width)}\" height=\"{F(height)}\" viewBox=\"0 0 {F(width)} {F(height)}\" role=\"img\" font-family=\"Arial, Helvetica, sans-serif\">\n");
        sb.Append(CultureInfo.InvariantCulture, $"<title>{Esc(title)}</title>\n<desc>{Esc($"{title}. {graph.Nodes.Count} items. Marking: {banner}.")}</desc>\n");
        sb.Append("<defs><pattern id=\"hatch\" width=\"8\" height=\"8\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(45)\"><line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"8\" stroke=\"#555555\" stroke-width=\"2\"/></pattern>");
        sb.Append("<pattern id=\"dots\" width=\"8\" height=\"8\" patternUnits=\"userSpaceOnUse\"><circle cx=\"4\" cy=\"4\" r=\"1.5\" fill=\"#b3261e\"/></pattern></defs>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"0\" y=\"0\" width=\"{F(width)}\" height=\"{F(height)}\" fill=\"#ffffff\"/>\n");
        Bar(sb, 0, width, tone, banner, F);
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"16\" y=\"{F(BarHeight + 26)}\" font-size=\"16\" font-weight=\"bold\" fill=\"#111111\">{Esc(title)}</text>\n");

        sb.Append(CultureInfo.InvariantCulture, $"<g transform=\"translate(0 {F(top)})\">\n");
        foreach (var e in layout.Edges)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<path d=\"{e.Path}\" fill=\"none\" stroke=\"#444444\" stroke-width=\"1.5\"/>\n");
        }
        foreach (var n in layout.Nodes)
        {
            var g = nodes[n.Id];
            var outline = GraphGeometry.Outline(g.Kind, n.X, n.Y, n.Width, n.Height);
            var fill = g.Kind switch { GraphNodeKind.Restricted => "url(#hatch)", GraphNodeKind.Conflict => "#fde7e5", GraphNodeKind.Change => "#e3effc", _ => "#ffffff" };
            sb.Append(CultureInfo.InvariantCulture, $"<path d=\"{outline}\" fill=\"#ffffff\" stroke=\"#111111\" stroke-width=\"{(g.Kind == GraphNodeKind.Change ? 3 : 1.5)}\"{(g.Kind is GraphNodeKind.More or GraphNodeKind.Restricted ? " stroke-dasharray=\"5 3\"" : "")}/>\n");
            if (fill != "#ffffff") sb.Append(CultureInfo.InvariantCulture, $"<path d=\"{outline}\" fill=\"{fill}\" stroke=\"none\" opacity=\"{(g.Kind == GraphNodeKind.Restricted ? "0.55" : "1")}\"/>\n");
            if (g.Kind == GraphNodeKind.Conflict) sb.Append(CultureInfo.InvariantCulture, $"<path d=\"{outline}\" fill=\"url(#dots)\" stroke=\"none\"/>\n");
            var label = g.Kind == GraphNodeKind.Restricted ? GraphGeometry.KindName(g.Kind) : g.Label;
            var kindName = GraphGeometry.KindName(g.Kind);
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{F(n.X + 10)}\" y=\"{F(n.Y + 20)}\" font-size=\"13\" font-weight=\"bold\" fill=\"#111111\">{Esc(GraphGeometry.Shorten(label))}</text>\n");
            var sub = g.Kind == GraphNodeKind.Restricted ? "You may not see this" : g.Sublabel;
            if (!string.IsNullOrEmpty(sub)) sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{F(n.X + 10)}\" y=\"{F(n.Y + 36)}\" font-size=\"11\" fill=\"#333333\">{Esc(GraphGeometry.Shorten(sub, 28))}</text>\n");
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{F(n.X + 10)}\" y=\"{F(n.Y + n.Height - 6)}\" font-size=\"9\" fill=\"#555555\">{Esc(kindName)}</text>\n");
        }
        foreach (var e in layout.Edges)
        {
            var label = graph.Edges.FirstOrDefault(x => x.From == e.From && x.To == e.To)?.Label;
            if (string.IsNullOrEmpty(label)) continue;
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{F((e.X1 + e.X2) / 2)}\" y=\"{F((e.Y1 + e.Y2) / 2 - 4)}\" font-size=\"9\" text-anchor=\"middle\" fill=\"#222222\" stroke=\"#ffffff\" stroke-width=\"3\" paint-order=\"stroke\">{Esc(GraphGeometry.Shorten(label, 34))}</text>\n");
        }
        sb.Append("</g>\n");

        var y = top + layout.Height + 16;
        foreach (var line in Notices(banner, generatedBy, generatedAt))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"16\" y=\"{F(y)}\" font-size=\"10\" fill=\"#222222\">{Esc(line)}</text>\n");
            y += 14;
        }
        Bar(sb, height - BarHeight, width, tone, banner, F);
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static void Bar(StringBuilder sb, double y, double width, string tone, string banner, Func<double, string> f)
    {
        sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"0\" y=\"{f(y)}\" width=\"{f(width)}\" height=\"{f(BarHeight)}\" fill=\"{tone}\"/>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{f(width / 2)}\" y=\"{f(y + 19)}\" font-size=\"13\" font-weight=\"bold\" text-anchor=\"middle\" fill=\"#ffffff\">{Esc(banner)}</text>\n");
    }

    /// <summary>The distribution statement (by the letter in the banner), the export warning when the banner names ITAR or EAR, and the traceability line.</summary>
    internal static IReadOnlyList<string> Notices(string banner, string generatedBy, DateTimeOffset generatedAt)
    {
        var lines = new List<string>();
        var marker = "DISTRIBUTION ";
        var at = banner.IndexOf(marker, StringComparison.Ordinal);
        var letter = at >= 0 && at + marker.Length < banner.Length ? banner[at + marker.Length] : 'F';
        lines.Add(letter switch
        {
            'A' => "DISTRIBUTION STATEMENT A. Approved for public release: distribution is unlimited.",
            'B' => "DISTRIBUTION STATEMENT B. Distribution authorized to U.S. Government agencies only. Other requests shall be referred to the controlling office.",
            'C' => "DISTRIBUTION STATEMENT C. Distribution authorized to U.S. Government agencies and their contractors. Other requests shall be referred to the controlling office.",
            'D' => "DISTRIBUTION STATEMENT D. Distribution authorized to the Department of Defense and U.S. DoD contractors only. Other requests shall be referred to the controlling office.",
            'E' => "DISTRIBUTION STATEMENT E. Distribution authorized to DoD Components only. Other requests shall be referred to the controlling office.",
            _ => "DISTRIBUTION STATEMENT F. Further dissemination only as directed by the controlling office or higher DoD authority.",
        });
        if (banner.Contains("ITAR", StringComparison.Ordinal) || banner.Contains("EAR ", StringComparison.Ordinal))
            lines.AddRange(Wrap(MarkingStatements.ExportControlWarning, 170));
        lines.Add($"Keel PLM impact map · generated {generatedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC by {generatedBy} · the banner is the union of the items shown.");
        return lines;
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            if (line.Length + word.Length + 1 > width) { yield return line.ToString(); line.Clear(); }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    private static string Esc(string text) => SecurityElement.Escape(text) ?? "";
}
