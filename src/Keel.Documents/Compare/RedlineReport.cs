using System.Globalization;
using System.Net;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Keel.Documents.Compare;

public sealed record RedlineIdentity(string GeneratedBy, DateTimeOffset GeneratedAt);

/// <summary>
/// The redline as a report file (increment 9.5, ADR-0053): HTML (one self-contained file, no scripts, no external resources) or PDF (PDFsharp, MIT, with the embedded DejaVu
/// Sans faces: no system fonts, no network). Both carry the aggregate marking banner at the top and bottom (on every printed page for HTML, every page for PDF), every
/// notice either document requires, the identification of both versions with their SHA-256, the summary counts and the review threads. Insertions and deletions are marked
/// by more than colour: insertions are underlined and set between [+ and +], deletions are struck through and set between [− and −].
/// </summary>
public static class RedlineReport
{
    public static string SafeName(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' ? c : '-');
        return sb.Length == 0 ? "x" : sb.ToString();
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    private static string UtcText(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    private static string Pct(double similarity) => (similarity * 100).ToString("0.#", CultureInfo.InvariantCulture) + " %";

    // ================================================================ HTML

    public static byte[] Html(DocumentComparison c, RedlineIdentity id)
    {
        var sb = new StringBuilder(16 * 1024);
        var title = $"Redline: {c.Left.Number} {c.Left.Label} against {c.Right.Number} {c.Right.Label}";
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
          .Append("<title>").Append(Enc(title)).Append("</title>\n<style>\n").Append(Css).Append("</style></head>\n<body>\n");
        sb.Append("<div class=\"banner top\" role=\"region\" aria-label=\"Marking banner, top\">").Append(Enc(c.Banner)).Append("</div>\n<main>\n");
        sb.Append("<h1>Redline report</h1>\n");
        sb.Append("<p class=\"muted\">Generated ").Append(Enc(UtcText(id.GeneratedAt))).Append(" by ").Append(Enc(id.GeneratedBy)).Append(" from Keel PLM.</p>\n");

        sb.Append("<h2>Compared versions</h2>\n<table class=\"facts\"><caption class=\"sr\">The two compared versions</caption><thead><tr><th scope=\"col\"></th><th scope=\"col\">Left (base)</th><th scope=\"col\">Right (changed)</th></tr></thead><tbody>\n");
        foreach (var row in Rows(c)) sb.Append("<tr><th scope=\"row\">").Append(Enc(row.Field)).Append("</th><td>").Append(Enc(row.Left)).Append("</td><td>").Append(Enc(row.Right)).Append("</td></tr>\n");
        sb.Append("</tbody></table>\n");

        if (c.Mode == CompareModes.Identical) sb.Append("<p class=\"note\"><strong>The two files are byte-for-byte identical.</strong> There is nothing to redline.</p>\n");
        else if (c.Mode == CompareModes.Metadata) sb.Append("<p class=\"note\" role=\"note\"><strong>").Append(Enc(c.NotComparableReason ?? "Content not comparable.")).Append("</strong></p>\n");
        else if (c.Diff is { } d)
        {
            var n = d.Counts;
            sb.Append("<h2>Summary</h2>\n<table class=\"facts\"><caption class=\"sr\">Counts of changes</caption><tbody>\n");
            sb.Append("<tr><th scope=\"row\">Changes</th><td>").Append(n.Changes.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>\n");
            sb.Append("<tr><th scope=\"row\">Inserted words</th><td>").Append(n.InsertedWords.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>\n");
            sb.Append("<tr><th scope=\"row\">Deleted words</th><td>").Append(n.DeletedWords.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>\n");
            sb.Append("<tr><th scope=\"row\">Unchanged words</th><td>").Append(n.UnchangedWords.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>\n");
            sb.Append("<tr><th scope=\"row\">Similarity</th><td>").Append(Pct(n.Similarity)).Append("</td></tr>\n");
            sb.Append("<tr><th scope=\"row\">Open comment threads</th><td>").Append(c.OpenThreads.ToString(CultureInfo.InvariantCulture)).Append(" of ").Append(c.Threads.Count.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>\n");
            sb.Append("</tbody></table>\n");
            if (d.Coarse) sb.Append("<p class=\"note\">Some passages were too different to align word by word and are shown as replaced as a whole.</p>\n");
            if (!d.HasChanges) sb.Append("<p class=\"note\"><strong>The text is the same.</strong> The files differ in bytes but not in words.</p>\n");
            sb.Append("<p class=\"legend\"><strong>Key.</strong> <ins>Inserted text</ins> is underlined and set between [+ and +]. <del>Deleted text</del> is struck through and set between [&#8722; and &#8722;]. Differences in spacing and line breaks are not changes.</p>\n");
            sb.Append("<h2>Redline</h2>\n<div class=\"redline\">");
            foreach (var s in d.Segments)
            {
                var text = Enc(s.Text);
                switch (s.Op)
                {
                    case DiffOp.Equal: sb.Append(text); break;
                    case DiffOp.Insert: sb.Append("<ins data-change=\"").Append(((s.Change ?? 0) + 1).ToString(CultureInfo.InvariantCulture)).Append("\" id=\"change-").Append(((s.Change ?? 0) + 1).ToString(CultureInfo.InvariantCulture)).Append("i\">").Append(text).Append("</ins>"); break;
                    default: sb.Append("<del data-change=\"").Append(((s.Change ?? 0) + 1).ToString(CultureInfo.InvariantCulture)).Append("\" id=\"change-").Append(((s.Change ?? 0) + 1).ToString(CultureInfo.InvariantCulture)).Append("d\">").Append(text).Append("</del>"); break;
                }
            }
            sb.Append("</div>\n");
            if (d.Changes.Count > 0)
            {
                sb.Append("<h2>Changes</h2>\n<table class=\"changes\"><caption class=\"sr\">List of changes</caption><thead><tr><th scope=\"col\">No.</th><th scope=\"col\">Kind</th><th scope=\"col\">Removed</th><th scope=\"col\">Added</th><th scope=\"col\">Comment threads</th></tr></thead><tbody>\n");
                foreach (var ch in d.Changes)
                {
                    var threads = c.Threads.Where(t => !t.Detached && t.ChangeIndex == ch.Index).ToList();
                    sb.Append("<tr><th scope=\"row\">").Append((ch.Index + 1).ToString(CultureInfo.InvariantCulture)).Append("</th><td>").Append(KindLabel(ch.Kind)).Append("</td><td>").Append(Enc(ch.DeletedPreview))
                      .Append("</td><td>").Append(Enc(ch.InsertedPreview)).Append("</td><td>").Append(threads.Count == 0 ? "None" : $"{threads.Count(t => !t.Resolved)} open, {threads.Count(t => t.Resolved)} resolved").Append("</td></tr>\n");
                }
                sb.Append("</tbody></table>\n");
            }
            if (c.Threads.Count > 0)
            {
                sb.Append("<h2>Review comments</h2>\n");
                foreach (var t in c.Threads.OrderBy(t => t.ChangeIndex).ThenBy(t => t.At))
                {
                    sb.Append("<section class=\"thread\"><h3>").Append(t.Detached ? "Comment on a change that is no longer present" : $"Change {t.ChangeIndex + 1} ({KindLabel(t.ChangeKind)})")
                      .Append(" <span class=\"status\">").Append(t.Resolved ? "Resolved" : "Open").Append("</span></h3>\n");
                    sb.Append("<p><strong>").Append(Enc(t.AuthorName)).Append("</strong>, ").Append(Enc(UtcText(t.At))).Append("</p><p class=\"body\">").Append(Enc(t.Body)).Append("</p>\n");
                    foreach (var r in t.Replies) sb.Append("<blockquote><p><strong>").Append(Enc(r.AuthorName)).Append("</strong>, ").Append(Enc(UtcText(r.At))).Append("</p><p class=\"body\">").Append(Enc(r.Body)).Append("</p></blockquote>\n");
                    if (t.Resolved) sb.Append("<p class=\"muted\">Resolved by ").Append(Enc(t.ResolvedByName ?? "")).Append(", ").Append(Enc(t.ResolvedAt is { } at ? UtcText(at) : "")).Append(".</p>\n");
                    sb.Append("</section>\n");
                }
            }
        }
        sb.Append("<h2>Notices</h2>\n<ul class=\"notices\">");
        foreach (var n in c.Notices) sb.Append("<li>").Append(Enc(n)).Append("</li>");
        sb.Append("</ul>\n</main>\n<div class=\"banner bottom\" role=\"region\" aria-label=\"Marking banner, bottom\">").Append(Enc(c.Banner)).Append("</div>\n</body></html>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string KindLabel(string kind) => kind switch { "insert" => "Insertion", "delete" => "Deletion", _ => "Replacement" };

    private static IEnumerable<MetadataRow> Rows(DocumentComparison c) => c.Metadata;

    private const string Css = """
        :root { color-scheme: light; }
        body { font-family: system-ui, "Segoe UI", Arial, sans-serif; margin: 0 auto; max-width: 62rem; padding: 3rem 1rem; line-height: 1.5; color: #111; background: #fff; }
        .banner { font-weight: 700; text-align: center; padding: .35rem .5rem; border: 2px solid #111; background: #f2f2f2; position: fixed; left: 0; right: 0; z-index: 2; letter-spacing: .02em; }
        .banner.top { top: 0; } .banner.bottom { bottom: 0; }
        main { padding: .5rem 0 2rem; }
        h1 { font-size: 1.6rem; } h2 { font-size: 1.2rem; margin-top: 2rem; border-bottom: 1px solid #999; padding-bottom: .2rem; }
        .muted { color: #444; } .sr { position: absolute; left: -9999px; }
        table { border-collapse: collapse; width: 100%; margin: .5rem 0; } th, td { border: 1px solid #888; padding: .3rem .5rem; text-align: left; vertical-align: top; word-break: break-word; }
        th[scope=row] { background: #f4f4f4; width: 12rem; }
        .redline { white-space: pre-wrap; word-wrap: break-word; border: 1px solid #888; padding: .75rem; font-family: ui-monospace, Consolas, monospace; font-size: .92rem; }
        ins { text-decoration: underline; background: #e3f6e3; } ins::before { content: "[+ "; font-weight: 700; } ins::after { content: " +]"; font-weight: 700; }
        del { text-decoration: line-through; background: #fbe3e3; } del::before { content: "[\2212 "; font-weight: 700; } del::after { content: " \2212]"; font-weight: 700; }
        .note { border-left: 4px solid #111; padding: .4rem .75rem; background: #f4f4f4; }
        .thread { border: 1px solid #888; padding: .5rem .75rem; margin: .75rem 0; } .thread h3 { font-size: 1rem; margin: 0 0 .25rem; } .status { font-weight: 700; border: 1px solid #111; padding: 0 .4rem; margin-left: .4rem; }
        .body { white-space: pre-wrap; } blockquote { margin: .5rem 0 .5rem 1.25rem; border-left: 3px solid #888; padding-left: .75rem; }
        .notices { font-size: .85rem; }
        @media print { body { padding: 2.2rem 0; } .banner { background: #fff; } h2 { break-after: avoid; } .thread, tr { break-inside: avoid; } }
        @media (prefers-color-scheme: dark) { :root { color-scheme: light; } }
        """;

    // ================================================================ PDF

    private const double PageW = 612, PageH = 792, Side = 36, Top = 22, Leading = 1.22;
    /// <summary>Above this many characters of segments the PDF shows each change with its surrounding words instead of the whole text (the HTML always shows everything).</summary>
    private const int FullTextLimit = 250_000;
    private const int ContextWords = 30;

    private enum RunStyle { Normal, Insert, Delete }

    private sealed record Word(string Text, RunStyle Style, bool NewLine);

    public static byte[] Pdf(DocumentComparison c, RedlineIdentity id)
    {
        if (GlobalFontSettings.FontResolver is null) throw new InvalidOperationException("No font resolver is installed.");
        var fonts = new Fonts();
        var measure = XGraphics.CreateMeasureContext(new XSize(PageW, PageH), XGraphicsUnit.Point, XPageDirection.Downwards);
        var innerW = PageW - 2 * Side;
        var noticeLines = c.Notices.SelectMany(n => Wrap(measure, n, fonts.Notice, innerW)).ToList();
        var bannerLines = Wrap(measure, c.Banner, fonts.Banner, innerW);
        var trace = $"Keel PLM redline report · {c.Left.Number} {c.Left.Label} (SHA-256 {c.Left.Sha256[..16]}…) against {c.Right.Number} {c.Right.Label} (SHA-256 {c.Right.Sha256[..16]}…) · generated {id.GeneratedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC by {id.GeneratedBy}";
        var traceLines = Wrap(measure, trace, fonts.Notice, innerW);
        var headerH = bannerLines.Count * 9 * Leading + 6;
        var footerH = noticeLines.Count * 6.5 * Leading + traceLines.Count * 6.5 * Leading + bannerLines.Count * 9 * Leading + 14;
        var bodyTop = Top + headerH + 6;
        var bodyBottom = PageH - footerH - 22;

        var total = Layout(null, c, fonts, measure, bodyTop, bodyBottom);
        using var doc = new PdfDocument();
        doc.Info.Title = $"Redline: {c.Left.Number} {c.Left.Label} against {c.Right.Number} {c.Right.Label}";
        doc.Info.Subject = c.Banner;
        doc.Info.Author = id.GeneratedBy;
        doc.Info.CreationDate = id.GeneratedAt.UtcDateTime;
        var pageNo = 0;
        var frame = new Frame(doc, p =>
        {
            p.Size = PdfSharp.PageSize.Letter;
            var g = XGraphics.FromPdfPage(p);
            pageNo++;
            var y = Top;
            foreach (var l in bannerLines) { g.DrawString(l, fonts.Banner, XBrushes.Black, new XRect(Side, y, innerW, 9 * Leading), XStringFormats.Center); y += 9 * Leading; }
            g.DrawLine(XPens.Black, Side, Top + headerH, PageW - Side, Top + headerH);
            var fy = PageH - footerH - 12;
            g.DrawLine(XPens.Black, Side, fy, PageW - Side, fy);
            fy += 3;
            foreach (var l in noticeLines) { g.DrawString(l, fonts.Notice, XBrushes.Black, new XRect(Side, fy, innerW, 6.5 * Leading), XStringFormats.TopLeft); fy += 6.5 * Leading; }
            foreach (var l in traceLines) { g.DrawString(l, fonts.Notice, XBrushes.DimGray, new XRect(Side, fy, innerW, 6.5 * Leading), XStringFormats.TopLeft); fy += 6.5 * Leading; }
            fy += 2;
            foreach (var l in bannerLines) { g.DrawString(l, fonts.Banner, XBrushes.Black, new XRect(Side, fy, innerW, 9 * Leading), XStringFormats.Center); fy += 9 * Leading; }
            g.DrawString($"Page {pageNo} of {total}", fonts.Notice, XBrushes.Black, new XRect(Side, PageH - 20, innerW, 9), XStringFormats.TopRight);
            return g;
        });
        Layout(frame, c, fonts, measure, bodyTop, bodyBottom);
        frame.Dispose();
        using var output = new MemoryStream();
        doc.Save(output, closeStream: false);
        return output.ToArray();
    }

    private sealed class Frame(PdfDocument doc, Func<PdfPage, XGraphics> decorate) : IDisposable
    {
        public XGraphics? G;
        public void NewPage() { G?.Dispose(); G = decorate(doc.AddPage()); }
        public void Dispose() => G?.Dispose();
    }

    private sealed class Cursor(Frame? frame, XGraphics measure, double top, double bottom)
    {
        private readonly double startY = top;
        public int Pages;
        public double Y;
        public void Start() { Pages++; Y = startY; frame?.NewPage(); }
        public void Need(double h) { if (Y + h > bottom) Start(); }
        public XGraphics Measure => measure;
        public XGraphics? Draw => frame?.G;
    }

    private static int Layout(Frame? frame, DocumentComparison c, Fonts f, XGraphics measure, double top, double bottom)
    {
        var cur = new Cursor(frame, measure, top, bottom);
        cur.Start();
        Heading(cur, "Redline report", f.Heading, 22);
        Para(cur, "Compared versions", f.BodyBold);
        foreach (var row in c.Metadata) Field(cur, f, row.Field, row.Same ? row.Left : $"{row.Left}   |   {row.Right}", row.Same ? "(same on both sides)" : "left | right");
        cur.Y += 6;

        if (c.Mode == CompareModes.Identical) Para(cur, "The two files are byte-for-byte identical. There is nothing to redline.", f.BodyBold);
        else if (c.Mode == CompareModes.Metadata) Para(cur, c.NotComparableReason ?? "Content not comparable.", f.BodyBold);
        else if (c.Diff is { } d)
        {
            var n = d.Counts;
            Para(cur, "Summary", f.BodyBold);
            Para(cur, $"Changes: {n.Changes} · Inserted words: {n.InsertedWords} · Deleted words: {n.DeletedWords} · Unchanged words: {n.UnchangedWords} · Similarity: {Pct(n.Similarity)} · Comment threads: {c.OpenThreads} open of {c.Threads.Count}", f.Body);
            if (d.Coarse) Para(cur, "Some passages were too different to align word by word and are shown as replaced as a whole.", f.Small);
            if (!d.HasChanges) Para(cur, "The text is the same. The files differ in bytes but not in words.", f.BodyBold);
            Para(cur, "Key: inserted text is underlined and set between [+ and +]; deleted text is struck through and set between [− and −]. Differences in spacing and line breaks are not changes.", f.Small);
            cur.Y += 6;
            var full = d.Segments.Sum(s => s.Text.Length) <= FullTextLimit;
            Para(cur, full ? "Redline" : "Changes with surrounding words", f.BodyBold);
            if (!full) Para(cur, $"The text is long, so each change is shown with {ContextWords} words before and after. The HTML report shows the whole text.", f.Small);
            Runs(cur, f, full ? Words(d.Segments) : Words(Context(d.Segments)));
            if (d.Changes.Count > 0)
            {
                cur.Y += 8;
                Para(cur, "Change list", f.BodyBold);
                foreach (var ch in d.Changes)
                {
                    var threads = c.Threads.Where(t => !t.Detached && t.ChangeIndex == ch.Index).ToList();
                    var line = $"{ch.Index + 1}. {KindLabel(ch.Kind)}" + (ch.DeletedPreview.Length > 0 ? $" · removed: {Short(ch.DeletedPreview)}" : "") + (ch.InsertedPreview.Length > 0 ? $" · added: {Short(ch.InsertedPreview)}" : "")
                        + (threads.Count > 0 ? $" · {threads.Count(t => !t.Resolved)} open / {threads.Count(t => t.Resolved)} resolved comment thread(s)" : "");
                    Para(cur, line, f.Small);
                }
            }
            if (c.Threads.Count > 0)
            {
                cur.Y += 8;
                Para(cur, "Review comments", f.BodyBold);
                foreach (var t in c.Threads.OrderBy(t => t.ChangeIndex).ThenBy(t => t.At))
                {
                    cur.Need(40);
                    Para(cur, (t.Detached ? "Comment on a change that is no longer present" : $"Change {t.ChangeIndex + 1} ({KindLabel(t.ChangeKind)})") + $" · {(t.Resolved ? "Resolved" : "Open")}", f.BodyBold);
                    Para(cur, $"{t.AuthorName}, {UtcText(t.At)}: {t.Body}", f.Body);
                    foreach (var r in t.Replies) Para(cur, $"    Reply by {r.AuthorName}, {UtcText(r.At)}: {r.Body}", f.Body);
                    if (t.Resolved) Para(cur, $"    Resolved by {t.ResolvedByName}, {(t.ResolvedAt is { } at ? UtcText(at) : "")}", f.Small);
                    cur.Y += 3;
                }
            }
        }
        return cur.Pages;
    }

    private static string Short(string s) => s.Length <= 120 ? s.Replace('\n', ' ') : s[..120].Replace('\n', ' ') + "…";

    private static IEnumerable<DiffSegment> Context(IReadOnlyList<DiffSegment> segments)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (s.Op != DiffOp.Equal) { yield return s; continue; }
            var tokens = s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var before = i > 0;
            var after = i < segments.Count - 1;
            if (tokens.Length <= ContextWords * (before && after ? 2 : 1)) { yield return s; continue; }
            if (before) yield return new DiffSegment(DiffOp.Equal, " " + string.Join(' ', tokens.Take(ContextWords)), null);
            yield return new DiffSegment(DiffOp.Equal, " … ", null);
            if (after) yield return new DiffSegment(DiffOp.Equal, " " + string.Join(' ', tokens.Skip(tokens.Length - ContextWords)), null);
        }
    }

    private static List<Word> Words(IEnumerable<DiffSegment> segments)
    {
        var words = new List<Word>();
        foreach (var s in segments)
        {
            var style = s.Op switch { DiffOp.Insert => RunStyle.Insert, DiffOp.Delete => RunStyle.Delete, _ => RunStyle.Normal };
            var startIndex = words.Count;
            var newline = false;
            var i = 0;
            var text = s.Text;
            while (i < text.Length)
            {
                if (text[i] == '\n') { newline = true; i++; continue; }
                if (char.IsWhiteSpace(text[i])) { i++; continue; }
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                words.Add(new Word(text[start..i], style, newline));
                newline = false;
            }
            if (style != RunStyle.Normal && words.Count > startIndex)
            {
                var (open, close) = style == RunStyle.Insert ? ("[+", "+]") : ("[−", "−]");
                words[startIndex] = words[startIndex] with { Text = open + words[startIndex].Text };
                words[^1] = words[^1] with { Text = words[^1].Text + close };
            }
            if (newline && words.Count > 0 && s.Op == DiffOp.Equal) { /* trailing break is carried by the next word's gap */ }
        }
        return words;
    }

    private static void Runs(Cursor cur, Fonts f, List<Word> words)
    {
        var font = f.Body;
        var h = font.Size * Leading;
        var space = cur.Measure.MeasureString(" ", font).Width;
        var width = PageW - 2 * Side;
        var x = 0.0;
        var first = true;
        cur.Need(h);
        foreach (var w in words)
        {
            var textW = cur.Measure.MeasureString(w.Text, font).Width;
            if (w.NewLine && !first) { cur.Y += h; x = 0; cur.Need(h); }
            else if (x > 0 && x + space + textW > width) { cur.Y += h; x = 0; cur.Need(h); }
            if (x > 0) x += space;
            if (textW > width) { // a single token wider than the line (a long hash or URL): break it by characters
                var rest = w.Text;
                while (rest.Length > 0)
                {
                    var n = rest.Length;
                    while (n > 1 && cur.Measure.MeasureString(rest[..n], font).Width > width - x) n--;
                    DrawWord(cur, font, new Word(rest[..n], w.Style, false), x, textW: cur.Measure.MeasureString(rest[..n], font).Width, h);
                    x += cur.Measure.MeasureString(rest[..n], font).Width;
                    rest = rest[n..];
                    if (rest.Length > 0) { cur.Y += h; x = 0; cur.Need(h); }
                }
            }
            else
            {
                DrawWord(cur, font, w, x, textW, h);
                x += textW;
            }
            first = false;
        }
        cur.Y += h + 2;
    }

    private static void DrawWord(Cursor cur, XFont font, Word w, double x, double textW, double h)
    {
        if (cur.Draw is not { } g) return;
        var brush = w.Style switch { RunStyle.Insert => new XSolidBrush(XColor.FromArgb(0, 96, 0)), RunStyle.Delete => new XSolidBrush(XColor.FromArgb(160, 0, 0)), _ => XBrushes.Black };
        g.DrawString(w.Text, font, brush, new XPoint(Side + x, cur.Y + font.Size), XStringFormats.BottomLeft);
        if (w.Style == RunStyle.Insert) g.DrawLine(new XPen(XColor.FromArgb(0, 96, 0), 0.7), Side + x, cur.Y + font.Size + 1.6, Side + x + textW, cur.Y + font.Size + 1.6);
        else if (w.Style == RunStyle.Delete) g.DrawLine(new XPen(XColor.FromArgb(160, 0, 0), 0.7), Side + x, cur.Y + font.Size * 0.62, Side + x + textW, cur.Y + font.Size * 0.62);
    }

    private static void Heading(Cursor cur, string text, XFont font, double h)
    {
        cur.Need(h + 6);
        if (cur.Draw is { } g) g.DrawString(text, font, XBrushes.Black, new XRect(Side, cur.Y, PageW - 2 * Side, h), XStringFormats.TopLeft);
        cur.Y += h + 4;
    }

    private static void Field(Cursor cur, Fonts f, string label, string value, string note)
    {
        _ = note;
        var lines = Wrap(cur.Measure, value, f.Small, PageW - 2 * Side - 90);
        cur.Need(lines.Count * 10 + 2);
        if (cur.Draw is { } g) g.DrawString(label, f.SmallBold, XBrushes.Black, new XRect(Side, cur.Y, 86, 10), XStringFormats.TopLeft);
        foreach (var l in lines)
        {
            if (cur.Draw is { } g2) g2.DrawString(l, f.Small, XBrushes.Black, new XRect(Side + 90, cur.Y, PageW - 2 * Side - 90, 10), XStringFormats.TopLeft);
            cur.Y += 10;
        }
        cur.Y += 2;
    }

    private static void Para(Cursor cur, string text, XFont font)
    {
        var h = font.Size * Leading;
        foreach (var l in Wrap(cur.Measure, text, font, PageW - 2 * Side))
        {
            cur.Need(h);
            if (cur.Draw is { } g && l.Length > 0) g.DrawString(l, font, XBrushes.Black, new XRect(Side, cur.Y, PageW - 2 * Side, h), XStringFormats.TopLeft);
            cur.Y += h;
        }
        cur.Y += 1.5;
    }

    private static List<string> Wrap(XGraphics g, string text, XFont font, double width)
    {
        var lines = new List<string>();
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var current = "";
            foreach (var word in para.Split(' ', StringSplitOptions.None))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (g.MeasureString(candidate, font).Width <= width) { current = candidate; continue; }
                if (current.Length > 0) { lines.Add(current); current = ""; }
                var rest = word;
                while (g.MeasureString(rest, font).Width > width && rest.Length > 1)
                {
                    var n = rest.Length - 1;
                    while (n > 1 && g.MeasureString(rest[..n], font).Width > width) n--;
                    lines.Add(rest[..n]);
                    rest = rest[n..];
                }
                current = rest;
            }
            lines.Add(current);
        }
        return lines;
    }

    private sealed class Fonts
    {
        private const string Family = "Keel Sans";
        private static readonly XPdfFontOptions WinAnsi = new(PdfFontEncoding.WinAnsi);
        private static readonly XPdfFontOptions Unicode = new(PdfFontEncoding.Unicode);
        public XFont Banner { get; } = new(Family, 9, XFontStyleEx.Bold, WinAnsi);
        public XFont Notice { get; } = new(Family, 6.5, XFontStyleEx.Regular, WinAnsi);
        public XFont Heading { get; } = new(Family, 16, XFontStyleEx.Bold, WinAnsi);
        public XFont Body { get; } = new(Family, 9, XFontStyleEx.Regular, Unicode);
        public XFont BodyBold { get; } = new(Family, 9.5, XFontStyleEx.Bold, Unicode);
        public XFont Small { get; } = new(Family, 8, XFontStyleEx.Regular, Unicode);
        public XFont SmallBold { get; } = new(Family, 8, XFontStyleEx.Bold, Unicode);
    }
}
