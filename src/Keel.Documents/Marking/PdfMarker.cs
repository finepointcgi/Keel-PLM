using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Keel.Documents.Marking;

/// <summary>The PDF could not be opened for stamping (encrypted, damaged or unsupported). Callers fall back to a marked ZIP.</summary>
public sealed class PdfStampException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Stamps PDFs in place (A10): every page grows a header band with the marking banner and a footer band with the
/// banner, the notices of <see cref="MarkingStatements.Notices"/> and a traceability line. Pages are enlarged rather than
/// overprinted, so no engineering content is hidden under the marks.
/// </summary>
public static class PdfMarker
{
    private const double Margin = 18;          // 1/4 inch
    private const double BannerSize = 9;
    private const double NoticeSize = 6.5;
    private const double Leading = 1.25;

    /// <summary>True when the bytes start with a PDF header (within the first 1 KiB, as readers allow).</summary>
    public static bool LooksLikePdf(ReadOnlySpan<byte> head)
    {
        var window = head[..Math.Min(head.Length, 1024)];
        return window.IndexOf("%PDF-"u8) >= 0;
    }

    public static byte[] Stamp(byte[] pdf, MarkingContext ctx)
    {
        EmbeddedFontResolver.EnsureInstalled();
        try
        {
            // Encrypted PDFs without a known password fail to open here and are delivered in a marked ZIP instead.
            using var doc = PdfReader.Open(new MemoryStream(pdf, writable: false), PdfDocumentOpenMode.Modify);
            if (doc.PageCount == 0) throw new PdfStampException("The PDF has no pages.");
            var fonts = new Fonts();
            var notices = MarkingStatements.Notices(ctx.Markings);
            foreach (var page in doc.Pages) StampPage(page, ctx, notices, fonts);
            doc.Info.Subject = ctx.Banner;
            doc.Info.Keywords = $"{ctx.DocumentNumber} {ctx.IterationLabel}";
            using var output = new MemoryStream();
            doc.Save(output, closeStream: false);
            return output.ToArray();
        }
        catch (PdfStampException)
        {
            throw;
        }
        // PDFsharp signals malformed input with assorted exception types, including System.Exception itself.
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            throw new PdfStampException("The PDF cannot be stamped: " + e.Message, e);
        }
    }

    private static void StampPage(PdfPage page, MarkingContext ctx, IReadOnlyList<string> notices, Fonts fonts)
    {
        var box = page.MediaBox;
        var (w, h) = (box.Width, box.Height);
        var rotate = ((page.Rotate % 360) + 360) % 360;
        // Visual size after the page's /Rotate.
        var (vw, vh) = rotate is 90 or 270 ? (h, w) : (w, h);

        var measure = XGraphics.CreateMeasureContext(new XSize(vw, vh), XGraphicsUnit.Point, XPageDirection.Downwards);
        var textWidth = Math.Max(72, vw - 2 * Margin);
        var noticeLines = notices.SelectMany(n => Wrap(measure, n, fonts.Notice, textWidth)).ToList();
        var traceLines = Wrap(measure, ctx.TraceLine, fonts.Trace, textWidth);
        var noticeLine = NoticeSize * Leading;
        var top = Margin + BannerSize * Leading;
        var bottom = Margin + (noticeLines.Count + traceLines.Count) * noticeLine + 4 + BannerSize * Leading + Margin / 2;

        // Normalize the page to an unrotated box with origin (0,0), move the original content up by the footer band,
        // and enlarge the box by both bands. The original content is wrapped in q/Q so its state cannot leak.
        var (a, b, c, d, e, f) = rotate switch
        {
            90 => (0d, -1d, 1d, 0d, 0d, w),
            180 => (-1d, 0d, 0d, -1d, w, h),
            270 => (0d, 1d, -1d, 0d, h, 0d),
            _ => (1d, 0d, 0d, 1d, 0d, 0d),
        };
        var (x0, y0) = (box.X1, box.Y1);
        var tx = e - a * x0 - c * y0;
        var ty = f - b * x0 - d * y0 + bottom;
        var matrix = string.Join(' ', new[] { a, b, c, d, tx, ty }.Select(v => v.ToString("0.####", CultureInfo.InvariantCulture)));
        // Clip to the original box so content that used to be cropped away does not show in the bands.
        var clip = string.Join(' ', new[] { x0, y0, w, h }.Select(v => v.ToString("0.####", CultureInfo.InvariantCulture)));
        page.Contents.PrependContent().CreateStream(Encoding.ASCII.GetBytes($"q {matrix} cm {clip} re W n\n"));
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes("\nQ\n"));
        MoveAnnotations(page, (a, b, c, d, tx, ty));

        var newHeight = vh + top + bottom;
        page.Elements.SetInteger("/Rotate", 0);
        page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(vw, newHeight));
        foreach (var key in new[] { "/CropBox", "/BleedBox", "/TrimBox", "/ArtBox" }) page.Elements.Remove(key);

        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        var white = XBrushes.White;
        gfx.DrawRectangle(white, 0, 0, vw, top);
        gfx.DrawRectangle(white, 0, newHeight - bottom, vw, bottom);
        gfx.DrawString(ctx.Banner, fonts.Banner, XBrushes.Black, new XRect(Margin, Margin / 2, textWidth, BannerSize * Leading), XStringFormats.Center);

        var y = newHeight - bottom + Margin / 2;
        foreach (var line in noticeLines)
        {
            gfx.DrawString(line, fonts.Notice, XBrushes.Black, new XRect(Margin, y, textWidth, noticeLine), XStringFormats.TopLeft);
            y += noticeLine;
        }
        foreach (var line in traceLines)
        {
            gfx.DrawString(line, fonts.Trace, XBrushes.DimGray, new XRect(Margin, y, textWidth, noticeLine), XStringFormats.TopLeft);
            y += noticeLine;
        }
        gfx.DrawString(ctx.Banner, fonts.Banner, XBrushes.Black, new XRect(Margin, y + 4, textWidth, BannerSize * Leading), XStringFormats.Center);
    }

    private static void MoveAnnotations(PdfPage page, (double A, double B, double C, double D, double E, double F) m)
    {
        if (page.Elements.GetArray("/Annots") is not { } annots) return;
        foreach (var item in annots.Elements)
        {
            var annot = item is PdfReference r ? r.Value as PdfDictionary : item as PdfDictionary;
            if (annot?.Elements.GetArray("/Rect") is not { Elements.Count: 4 } rect) continue;
            var xs = new[] { rect.Elements.GetReal(0), rect.Elements.GetReal(2) };
            var ys = new[] { rect.Elements.GetReal(1), rect.Elements.GetReal(3) };
            var pts = xs.SelectMany(x => ys.Select(y => (X: m.A * x + m.C * y + m.E, Y: m.B * x + m.D * y + m.F))).ToList();
            annot.Elements.SetValue("/Rect", new PdfArray(page.Owner,
                new PdfReal(pts.Min(p => p.X)), new PdfReal(pts.Min(p => p.Y)), new PdfReal(pts.Max(p => p.X)), new PdfReal(pts.Max(p => p.Y))));
        }
    }

    // ---------------------------------------------------------------- cover sheet

    /// <summary>A one-or-more page US Letter cover sheet describing the file and its markings.</summary>
    public static byte[] CoverSheet(MarkingContext ctx)
    {
        EmbeddedFontResolver.EnsureInstalled();
        using var doc = new PdfDocument();
        doc.Info.Title = $"Marking cover sheet {ctx.DocumentNumber} {ctx.IterationLabel}";
        doc.Info.Subject = ctx.Banner;
        var fonts = new Fonts();
        var writer = new CoverWriter(doc, ctx, fonts);
        writer.Heading("Marking cover sheet");
        writer.Paragraph("This file is controlled. Read the markings below before opening, copying or forwarding it.", fonts.BodyLatin);
        writer.Gap();
        foreach (var (label, value) in CoverFields(ctx)) writer.Field(label, value);
        writer.Gap();
        foreach (var notice in MarkingStatements.Notices(ctx.Markings))
        {
            writer.Paragraph(notice, fonts.BodyLatin);
            writer.Gap(4);
        }
        writer.Finish();
        using var output = new MemoryStream();
        doc.Save(output, closeStream: false);
        return output.ToArray();
    }

    public static IReadOnlyList<(string Label, string Value)> CoverFields(MarkingContext ctx) =>
    [
        ("Markings", ctx.Banner),
        ("Document", $"{ctx.DocumentNumber}, {ctx.DocumentName}"),
        ("Iteration", ctx.IterationLabel),
        ("File", ctx.FileName),
        ("Media type", ctx.MediaType),
        ("Size", ctx.SizeBytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes"),
        ("SHA-256", ctx.Sha256),
        ("Retrieved", $"{ctx.RetrievedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC by {ctx.RetrievedBy}"),
    ];

    /// <summary>Plain-text manifest shipped as MARKINGS.txt next to the original file.</summary>
    public static string Manifest(MarkingContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append(ctx.Banner).Append('\n').Append('\n');
        foreach (var (label, value) in CoverFields(ctx)) sb.Append(label).Append(": ").Append(value).Append('\n');
        sb.Append('\n');
        foreach (var notice in MarkingStatements.Notices(ctx.Markings)) sb.Append(notice).Append('\n').Append('\n');
        sb.Append(ctx.Banner).Append('\n');
        return sb.ToString();
    }

    private sealed class CoverWriter
    {
        private const double PageWidth = 612, PageHeight = 792, Side = 54;
        private readonly PdfDocument _doc;
        private readonly MarkingContext _ctx;
        private readonly Fonts _fonts;
        private XGraphics _gfx = null!;
        private double _y;

        public CoverWriter(PdfDocument doc, MarkingContext ctx, Fonts fonts)
        {
            (_doc, _ctx, _fonts) = (doc, ctx, fonts);
            NewPage();
        }

        private const double Width = PageWidth - 2 * Side;
        private const double Limit = PageHeight - 54;

        private void NewPage()
        {
            _gfx?.Dispose();
            var page = _doc.AddPage();
            page.Width = XUnit.FromPoint(PageWidth);
            page.Height = XUnit.FromPoint(PageHeight);
            _gfx = XGraphics.FromPdfPage(page);
            _gfx.DrawString(_ctx.Banner, _fonts.Banner, XBrushes.Black, new XRect(Side, 18, Width, 14), XStringFormats.Center);
            _gfx.DrawString(_ctx.Banner, _fonts.Banner, XBrushes.Black, new XRect(Side, PageHeight - 32, Width, 14), XStringFormats.Center);
            _y = 54;
        }

        private void Line(string text, XFont font, double x, double width)
        {
            var height = font.Size * Leading;
            if (_y + height > Limit) NewPage();
            _gfx.DrawString(text, font, XBrushes.Black, new XRect(x, _y, width, height), XStringFormats.TopLeft);
            _y += height;
        }

        public void Heading(string text) { Line(text, _fonts.Heading, Side, Width); Gap(); }

        public void Paragraph(string text, XFont font)
        {
            foreach (var line in Wrap(_gfx, text, font, Width)) Line(line, font, Side, Width);
        }

        public void Field(string label, string value)
        {
            const double labelWidth = 90;
            var lines = Wrap(_gfx, value, _fonts.Body, Width - labelWidth);
            var height = _fonts.Body.Size * Leading;
            if (_y + height > Limit) NewPage();
            _gfx.DrawString(label, _fonts.BodyBold, XBrushes.Black, new XRect(Side, _y, labelWidth, height), XStringFormats.TopLeft);
            foreach (var line in lines) Line(line, _fonts.Body, Side + labelWidth, Width - labelWidth);
        }

        public void Gap(double points = 10) => _y += points;

        public void Finish() => _gfx.Dispose();
    }

    // ---------------------------------------------------------------- text helpers

    /// <summary>Greedy word wrap; words longer than a line are split by characters.</summary>
    internal static List<string> Wrap(XGraphics gfx, string text, XFont font, double width)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (gfx.MeasureString(candidate, font).Width <= width)
            {
                current.Clear().Append(candidate);
                continue;
            }
            if (current.Length > 0) lines.Add(current.ToString());
            current.Clear();
            var rest = word;
            while (gfx.MeasureString(rest, font).Width > width && rest.Length > 1)
            {
                var n = rest.Length - 1;
                while (n > 1 && gfx.MeasureString(rest[..n], font).Width > width) n--;
                lines.Add(rest[..n]);
                rest = rest[n..];
            }
            current.Append(rest);
        }
        if (current.Length > 0) lines.Add(current.ToString());
        return lines;
    }

    private sealed class Fonts
    {
        // Banners and notices use WinAnsi encoding: their text is plain Latin and stays extractable from the content
        // stream. Free text (file and document names) uses Unicode encoding so any script renders.
        private static readonly XPdfFontOptions WinAnsi = new(PdfFontEncoding.WinAnsi);
        private static readonly XPdfFontOptions Unicode = new(PdfFontEncoding.Unicode);

        public XFont Banner { get; } = new(EmbeddedFontResolver.FamilyName, BannerSize, XFontStyleEx.Bold, WinAnsi);
        public XFont Notice { get; } = new(EmbeddedFontResolver.FamilyName, NoticeSize, XFontStyleEx.Regular, WinAnsi);
        public XFont Trace { get; } = new(EmbeddedFontResolver.FamilyName, NoticeSize, XFontStyleEx.Regular, Unicode);
        public XFont Heading { get; } = new(EmbeddedFontResolver.FamilyName, 16, XFontStyleEx.Bold, WinAnsi);
        public XFont Body { get; } = new(EmbeddedFontResolver.FamilyName, 9.5, XFontStyleEx.Regular, Unicode);
        public XFont BodyLatin { get; } = new(EmbeddedFontResolver.FamilyName, 9.5, XFontStyleEx.Regular, WinAnsi);
        public XFont BodyBold { get; } = new(EmbeddedFontResolver.FamilyName, 9.5, XFontStyleEx.Bold, WinAnsi);
    }
}
