using System.Globalization;
using Keel.Core.Reports;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Keel.Reports.Pdf;

public sealed record PackageIdentity(string Number, DateTimeOffset GeneratedAt, string GeneratedBy);

/// <summary>
/// Renders a release package as one PDF (ADR-0044): cover sheet, BOM report, document list with hashes, signature page, change summary. The aggregate banner and
/// every distribution notice are printed at the top and bottom of every page. Layout is two-pass (the page count is printed as "page n of m"). PDFsharp (MIT) with the
/// embedded DejaVu Sans faces: no system fonts, no network.
/// </summary>
public static class ReleasePackagePdf
{
    private const double PageW = 612, PageH = 792, Side = 36, Top = 22;
    private const double Leading = 1.22;

    /// <summary>With <paramref name="signature"/> the document reserves an invisible signature field that <see cref="Keel.Reports.Signing.PdfSigner"/> fills; the pages are identical either way.</summary>
    public static byte[] Render(ReleasePackageContent content, PackageMarking marking, PackageIdentity identity, Action ensureFonts, Keel.Reports.Signing.SignatureReservation? signature = null) =>
        RenderCore((cur, f) => Body(cur, content, marking, identity, f), marking, identity,
            $"Keel PLM release package {identity.Number} · {content.Kind} {content.Key} {content.Revision} · generated {identity.GeneratedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC by {identity.GeneratedBy}",
            $"Release package {identity.Number}: {content.Key} {content.Revision}", ensureFonts, signature);

    /// <summary>A report as a PDF: a title, an optional note, and one table, under the same banner, notices and page numbering as a package.</summary>
    public static byte[] RenderTable(string title, string note, IReadOnlyList<(string Title, double Width, bool Right)> columns, IReadOnlyList<IReadOnlyList<string>> rows,
        PackageMarking marking, PackageIdentity identity, Action ensureFonts) =>
        RenderCore((cur, f) =>
        {
            Heading(cur, title, f);
            if (note.Length > 0) Para(cur, note, f.Small);
            if (rows.Count == 0) Para(cur, "The report has no rows.", f.Body); else Table(cur, f, [.. columns], rows);
        }, marking, identity,
            $"Keel PLM report {identity.Number} · generated {identity.GeneratedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC by {identity.GeneratedBy}", $"Report: {title}", ensureFonts);

    private static byte[] RenderCore(Action<Cursor, Fonts> body, PackageMarking marking, PackageIdentity identity, string trace, string docTitle, Action ensureFonts, Keel.Reports.Signing.SignatureReservation? signature = null)
    {
        ensureFonts();
        if (GlobalFontSettings.FontResolver is null) throw new InvalidOperationException("No font resolver is installed; the host must register one.");
        var fonts = new Fonts();
        var measure = XGraphics.CreateMeasureContext(new XSize(PageW, PageH), XGraphicsUnit.Point, XPageDirection.Downwards);
        var innerW = PageW - 2 * Side;
        var noticeLines = marking.Notices.SelectMany(n => Wrap(measure, n, fonts.Notice, innerW)).ToList();
        var bannerLines = Wrap(measure, marking.Banner, fonts.Banner, innerW);
        var traceLines = Wrap(measure, trace, fonts.Notice, innerW);
        var headerH = bannerLines.Count * 9 * Leading + 6;
        var footerH = noticeLines.Count * 6.5 * Leading + traceLines.Count * 6.5 * Leading + bannerLines.Count * 9 * Leading + 14;
        var bodyTop = Top + headerH + 6;
        var bodyBottom = PageH - footerH - 22;

        // Pass 1 counts the pages, pass 2 draws them.
        var total = Layout(null, body, fonts, measure, bodyTop, bodyBottom);
        using var doc = new PdfDocument();
        doc.Info.Title = docTitle;
        doc.Info.Subject = marking.Banner;
        doc.Info.Author = identity.GeneratedBy;
        doc.Info.CreationDate = identity.GeneratedAt.UtcDateTime;
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
        Layout(frame, body, fonts, measure, bodyTop, bodyBottom);
        frame.Dispose();
        if (signature is not null) Keel.Reports.Signing.PdfSigner.Reserve(doc, signature);
        using var output = new MemoryStream();
        doc.Save(output, closeStream: false);
        return output.ToArray();
    }

    // ------------------------------------------------------------------ layout

    internal sealed class Frame(PdfDocument doc, Func<PdfPage, XGraphics> decorate) : IDisposable
    {
        public XGraphics? G;
        public void NewPage() { G?.Dispose(); G = decorate(doc.AddPage()); }
        public void Dispose() => G?.Dispose();
    }

    internal sealed class Cursor(Frame? frame, XGraphics measure, double top, double bottom)
    {
        private readonly double startY = top;
        public int Pages;
        public double Y;
        public void Start() { Pages++; Y = startY; frame?.NewPage(); }
        public void Need(double h) { if (Y + h > bottom) Start(); }
        public XGraphics Measure => measure;
        public XGraphics? Draw => frame?.G;
        public double Bottom => bottom;
    }

    private static int Layout(Frame? frame, Action<Cursor, Fonts> body, Fonts f, XGraphics measure, double top, double bottom)
    {
        var cur = new Cursor(frame, measure, top, bottom);
        cur.Start();
        body(cur, f);
        return cur.Pages;
    }

    private static void Body(Cursor cur, ReleasePackageContent c, PackageMarking marking, PackageIdentity id, Fonts f)
    {
        Cover(cur, c, marking, id, f);
        cur.Start();
        Heading(cur, "BOM report", f);
        if (c.Bom.Count == 0) Para(cur, "This object has no structure below it, or the package kind has no bill of materials.", f.Body);
        else Table(cur, f,
            [("Level", 28, false), ("Find", 28, false), ("Number", 70, false), ("Name", 118, false), ("Rev", 26, false), ("State", 46, false), ("Qty", 34, true), ("Unit", 24, false), ("Effectivity", 62, false), ("Markings", 104, false)],
            c.Bom.Select(l => (IReadOnlyList<string>)[l.Level.ToString(CultureInfo.InvariantCulture), l.FindNumber.ToString(CultureInfo.InvariantCulture), l.Number, l.Name, l.Revision, l.State,
                l.Quantity.ToString("0.####", CultureInfo.InvariantCulture), l.Unit, l.Effectivity ?? "", l.Markings.BannerText()]).ToList());
        cur.Start();
        Heading(cur, "Documents", f);
        if (c.Documents.Count == 0) Para(cur, "No documents are linked to the included objects.", f.Body);
        foreach (var d in c.Documents)
        {
            cur.Need(60);
            Para(cur, $"{d.Number}  {d.Name}", f.BodyBold);
            Para(cur, $"{d.DocumentType} · revision {d.Revision} · {d.State} · linked to {d.LinkedTo} · {d.Markings.BannerText()}", f.Small);
            if (d.Files.Count == 0) Para(cur, "No files.", f.Small);
            foreach (var file in d.Files) Para(cur, $"File {file.FileName} · {file.SizeBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes · SHA-256 {file.Sha256}", f.Mono);
            cur.Y += 5;
        }
        cur.Start();
        Heading(cur, "Signatures", f);
        if (c.Signatures.Count == 0) Para(cur, "No e-signatures are recorded for the included objects.", f.Body);
        else Table(cur, f,
            [("Object", 66, false), ("Rev", 26, false), ("Signer", 92, false), ("Meaning", 112, false), ("Decision", 56, false), ("Signed (UTC)", 76, false), ("Comment", 112, false)],
            c.Signatures.Select(s => (IReadOnlyList<string>)[s.ObjectKey, s.Revision, s.SignerName, s.Meaning, s.Decision, s.SignedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), s.Comment ?? ""]).ToList());
        cur.Start();
        Heading(cur, "Change summary", f);
        if (c.Changes.Count == 0) Para(cur, "No change notices are recorded for the included objects.", f.Body);
        foreach (var ch in c.Changes)
        {
            cur.Need(50);
            Para(cur, $"{ch.Number}  {ch.Title}", f.BodyBold);
            Para(cur, $"{ch.Type} · {ch.State}{(ch.ChangeClass is null ? "" : " · class " + ch.ChangeClass)}", f.Small);
            if (!string.IsNullOrWhiteSpace(ch.Reason)) Para(cur, "Reason: " + ch.Reason, f.Body);
            foreach (var a in ch.Affected) Para(cur, "  " + a, f.Small);
            cur.Y += 5;
        }
    }

    private static void Cover(Cursor cur, ReleasePackageContent c, PackageMarking marking, PackageIdentity id, Fonts f)
    {
        Heading(cur, "Release package", f);
        Field(cur, f, "Package number", id.Number);
        Field(cur, f, "Subject", $"{c.Kind} {c.Key}");
        Field(cur, f, "Title", c.Title);
        Field(cur, f, "Revision", c.Revision);
        Field(cur, f, "Generated", $"{id.GeneratedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC by {id.GeneratedBy}");
        Field(cur, f, "Objects included", c.Objects.Count.ToString(CultureInfo.InvariantCulture));
        cur.Y += 6;
        Para(cur, "Marking of this package", f.BodyBold);
        Para(cur, marking.Banner, f.Banner);
        cur.Y += 4;
        foreach (var n in marking.Notices) { Para(cur, n, f.Notice10); cur.Y += 3; }
        cur.Y += 6;
        Para(cur, "Contents", f.BodyBold);
        foreach (var s in new[] { "BOM report", "Documents, with file hashes", "Signatures, with meanings and times", "Change summary" }) Para(cur, "  " + s, f.Body);
        cur.Y += 6;
        Para(cur, "This package lists every object it draws on. It can be downloaded only by people who may read all of them. Its SHA-256 is recorded in the vault record; a file cannot contain its own hash.", f.Small);
        cur.Y += 6;
        Para(cur, "Included objects", f.BodyBold);
        foreach (var o in c.Objects.Take(60)) Para(cur, $"  {o.ObjectType} {o.Key}  {o.Revision}  {o.State}", f.Small);
        if (c.Objects.Count > 60) Para(cur, $"  and {c.Objects.Count - 60} more (all are covered by the banner above).", f.Small);
    }

    private static void Heading(Cursor cur, string text, Fonts f)
    {
        cur.Need(30);
        Draw(cur, text, f.Heading, 0, cur.Y, PageW - 2 * Side, 20);
        cur.Y += 24;
    }

    private static void Field(Cursor cur, Fonts f, string label, string value)
    {
        var lines = Wrap(cur.Measure, value, f.Body, PageW - 2 * Side - 110);
        cur.Need(lines.Count * 12 + 2);
        Draw(cur, label, f.BodyBold, 0, cur.Y, 106, 12);
        foreach (var l in lines) { Draw(cur, l, f.Body, 110, cur.Y, PageW - 2 * Side - 110, 12); cur.Y += 12; }
        cur.Y += 2;
    }

    private static void Para(Cursor cur, string text, XFont font)
    {
        var h = font.Size * Leading;
        foreach (var l in Wrap(cur.Measure, text, font, PageW - 2 * Side))
        {
            cur.Need(h);
            Draw(cur, l, font, 0, cur.Y, PageW - 2 * Side, h);
            cur.Y += h;
        }
        cur.Y += 1.5;
    }

    private static void Table(Cursor cur, Fonts f, (string Title, double Width, bool Right)[] cols, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var font = f.Table;
        var h = font.Size * Leading;
        void Header()
        {
            cur.Need(h + 8);
            var x = 0.0;
            foreach (var (title, w, right) in cols) { Draw(cur, title, f.TableBold, x, cur.Y, w - 3, h, right); x += w; }
            cur.Y += h + 1;
            cur.Draw?.DrawLine(XPens.Gray, Side, cur.Y, PageW - Side, cur.Y);
            cur.Y += 2;
        }
        Header();
        foreach (var row in rows)
        {
            var wrapped = row.Select((cell, i) => Wrap(cur.Measure, cell, font, cols[i].Width - 4)).ToList();
            var lines = Math.Max(1, wrapped.Max(w => w.Count));
            var rowH = lines * h + 2;
            if (cur.Y + rowH > cur.Bottom) { cur.Start(); Header(); }
            var x = 0.0;
            for (var i = 0; i < cols.Length; i++)
            {
                var y = cur.Y;
                foreach (var l in wrapped[i]) { Draw(cur, l, font, x, y, cols[i].Width - 3, h, cols[i].Right); y += h; }
                x += cols[i].Width;
            }
            cur.Y += rowH;
        }
    }


    private static void Draw(Cursor cur, string text, XFont font, double x, double y, double w, double h, bool right = false)
    {
        if (cur.Draw is not { } g || text.Length == 0) return;
        g.DrawString(text, font, XBrushes.Black, new XRect(Side + x, y, w, h), right ? XStringFormats.TopRight : XStringFormats.TopLeft);
    }

    internal static List<string> Wrap(XGraphics g, string text, XFont font, double width)
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

    internal sealed class Fonts
    {
        private const string Family = "Keel Sans";
        private static readonly XPdfFontOptions WinAnsi = new(PdfFontEncoding.WinAnsi);
        private static readonly XPdfFontOptions Unicode = new(PdfFontEncoding.Unicode);
        public XFont Banner { get; } = new(Family, 9, XFontStyleEx.Bold, WinAnsi);
        public XFont Notice { get; } = new(Family, 6.5, XFontStyleEx.Regular, WinAnsi);
        public XFont Notice10 { get; } = new(Family, 8.5, XFontStyleEx.Regular, WinAnsi);
        public XFont Heading { get; } = new(Family, 16, XFontStyleEx.Bold, WinAnsi);
        public XFont Body { get; } = new(Family, 9.5, XFontStyleEx.Regular, Unicode);
        public XFont BodyBold { get; } = new(Family, 9.5, XFontStyleEx.Bold, Unicode);
        public XFont Small { get; } = new(Family, 8, XFontStyleEx.Regular, Unicode);
        public XFont Mono { get; } = new(Family, 6.8, XFontStyleEx.Regular, Unicode);
        public XFont Table { get; } = new(Family, 7, XFontStyleEx.Regular, Unicode);
        public XFont TableBold { get; } = new(Family, 7, XFontStyleEx.Bold, WinAnsi);
    }
}
