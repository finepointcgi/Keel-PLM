using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Keel.Core;
using Keel.Documents.Marking;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace Keel.UnitTests;

public sealed class MarkingOutputTests
{
    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)",
        cuiCategories: ["EXPT"], controllingOffice: "ACME Program Office", distributionReason: "Critical technology", program: "ORCA").Value;

    private static readonly Markings Open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;

    private static MarkingContext Ctx(Markings m, string file = "drawing.pdf", byte[]? bytes = null) =>
        new(m, "D-000001", "Bracket drawing", "A.2", file, "application/pdf", bytes?.Length ?? 1234,
            Convert.ToHexStringLower(SHA256.HashData(bytes ?? [1, 2, 3])), "alice", new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));

    /// <summary>A synthetic multi-page PDF, optionally with a rotated page, made with PDFsharp itself.</summary>
    private static byte[] SamplePdf(int pages, bool rotateSecond = false)
    {
        EmbeddedFontResolver.EnsureInstalled();
        using var doc = new PdfDocument();
        var font = new XFont(EmbeddedFontResolver.FamilyName, 12, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.WinAnsi));
        for (var i = 1; i <= pages; i++)
        {
            var page = doc.AddPage();
            using (var gfx = XGraphics.FromPdfPage(page)) gfx.DrawString($"Synthetic page {i}", font, XBrushes.Black, new XPoint(72, 72));
            if (rotateSecond && i == 2) page.Rotate = 90;
        }
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>All strings shown by text operators on a page (works for WinAnsi-encoded fonts).</summary>
    private static string PageText(PdfPage page)
    {
        var sb = new StringBuilder();
        void Walk(CObject o)
        {
            switch (o)
            {
                case COperator op:
                    foreach (var operand in op.Operands) Walk(operand);
                    break;
                case CSequence seq:
                    foreach (var item in seq) Walk(item);
                    break;
                case CString s:
                    sb.Append(s.Value).Append('\n');
                    break;
            }
        }
        Walk(ContentReader.ReadContent(page));
        return sb.ToString();
    }

    [Fact]
    public void Stamped_pdf_keeps_pages_and_carries_banner_and_notices_on_every_page()
    {
        var original = SamplePdf(3);
        var stamped = PdfMarker.Stamp(original, Ctx(Itar, bytes: original));

        using var doc = PdfReader.Open(new MemoryStream(stamped), PdfDocumentOpenMode.Import);
        Assert.Equal(3, doc.PageCount);
        Assert.Equal("CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D · PROGRAM ORCA", doc.Info.Subject);
        foreach (var page in doc.Pages)
        {
            var text = PageText(page);
            // Banner at top and bottom.
            Assert.Equal(2, text.Split('\n').Count(l => l == Itar.BannerText()));
            Assert.Contains("DISTRIBUTION STATEMENT D.", text);
            Assert.Contains("Department of Defense and U.S. DoD contractors only", text);
            Assert.Contains("WARNING - This document contains technical data whose export is restricted", text);
            Assert.Contains("CUI designation", text);
            Assert.Contains("Synthetic page", text);
            // The page grew; the original content was not overprinted.
            Assert.True(page.Height.Point > 792);
        }
    }

    [Fact]
    public void Stamped_pdf_normalizes_rotated_pages()
    {
        var stamped = PdfMarker.Stamp(SamplePdf(2, rotateSecond: true), Ctx(Open));
        using var doc = PdfReader.Open(new MemoryStream(stamped), PdfDocumentOpenMode.Import);
        var rotated = doc.Pages[1];
        Assert.Equal(0, rotated.Rotate);
        Assert.Equal(842, rotated.MediaBox.Width, 3); // the A4 portrait page shown landscape keeps its visual width
        Assert.Contains("DISTRIBUTION STATEMENT A. Approved for public release: distribution is unlimited.", PageText(rotated));
        Assert.DoesNotContain("WARNING", PageText(rotated));
    }

    [Fact]
    public void Garbage_is_not_stampable()
    {
        Assert.False(PdfMarker.LooksLikePdf("hello world"u8));
        Assert.True(PdfMarker.LooksLikePdf("%PDF-1.7\n..."u8));
        Assert.Throws<PdfStampException>(() => PdfMarker.Stamp(Encoding.ASCII.GetBytes("%PDF-1.7\nthis is not really a pdf"), Ctx(Open)));
    }

    [Fact]
    public async Task Zip_contains_cover_sheet_manifest_and_unchanged_original()
    {
        var original = Encoding.UTF8.GetBytes("solid bracket\nendsolid\n");
        var ctx = Ctx(Itar, "bracket.stl", original) with { MediaType = "model/stl" };
        using var output = new MemoryStream();
        await MarkedZip.WriteAsync(output, ctx, new MemoryStream(original), CancellationToken.None);

        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read);
        Assert.Equal(["MARKINGS.pdf", "MARKINGS.txt", "bracket.stl"], zip.Entries.Select(e => e.FullName));

        using (var s = zip.GetEntry("bracket.stl")!.Open())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            Assert.Equal(original, ms.ToArray());
        }
        using (var reader = new StreamReader(zip.GetEntry("MARKINGS.txt")!.Open()))
        {
            var text = reader.ReadToEnd();
            Assert.StartsWith(Itar.BannerText() + "\n", text);
            Assert.Contains("SHA-256: " + ctx.Sha256, text);
            Assert.Contains("File: bracket.stl", text);
            Assert.Contains(MarkingStatements.ExportControlWarning, text);
        }
        using (var cover = zip.GetEntry("MARKINGS.pdf")!.Open())
        using (var ms = new MemoryStream())
        {
            cover.CopyTo(ms);
            using var pdf = PdfReader.Open(new MemoryStream(ms.ToArray()), PdfDocumentOpenMode.Import);
            Assert.True(pdf.PageCount >= 1);
            var text = PageText(pdf.Pages[0]);
            Assert.Contains(Itar.BannerText(), text);
            Assert.Contains("DISTRIBUTION STATEMENT D.", text);
        }
    }

    [Theory]
    [InlineData(DistributionStatement.A, "DISTRIBUTION STATEMENT A. Approved for public release: distribution is unlimited.")]
    [InlineData(DistributionStatement.B, "DISTRIBUTION STATEMENT B. Distribution authorized to U.S. Government agencies only; Proprietary information. Other requests for this document shall be referred to ACME Program Office.")]
    [InlineData(DistributionStatement.C, "DISTRIBUTION STATEMENT C. Distribution authorized to U.S. Government agencies and their contractors; Proprietary information. Other requests for this document shall be referred to ACME Program Office.")]
    [InlineData(DistributionStatement.D, "DISTRIBUTION STATEMENT D. Distribution authorized to the Department of Defense and U.S. DoD contractors only; Proprietary information. Other requests for this document shall be referred to ACME Program Office.")]
    [InlineData(DistributionStatement.E, "DISTRIBUTION STATEMENT E. Distribution authorized to DoD Components only; Proprietary information. Other requests for this document shall be referred to ACME Program Office.")]
    [InlineData(DistributionStatement.F, "DISTRIBUTION STATEMENT F. Further dissemination only as directed by ACME Program Office or higher DoD authority.")]
    public void Distribution_statements_use_dodi_5230_24_wording(DistributionStatement statement, string expected)
    {
        var m = Markings.Create(ExportJurisdiction.None, statement, "ENG", controllingOffice: statement == DistributionStatement.A ? null : "ACME Program Office",
            distributionReason: statement == DistributionStatement.A ? null : "Proprietary information", program: "ORCA").Value;
        Assert.Equal(expected, MarkingStatements.DistributionStatementText(m));
    }

    [Fact]
    public void Notices_depend_on_markings()
    {
        var open = MarkingStatements.Notices(Open);
        Assert.Equal(["DISTRIBUTION STATEMENT A. Approved for public release: distribution is unlimited."], open);

        var itar = MarkingStatements.Notices(Itar);
        Assert.Equal(5, itar.Count);
        Assert.StartsWith("CUI designation - Controlled by: ENG. CUI categories: EXPT.", itar[0]);
        Assert.Equal("Export classification: ITAR, USML Category XI(d).", itar[1]);
        Assert.Equal(MarkingStatements.ExportControlWarning, itar[3]);
        Assert.Equal(MarkingStatements.DestructionNotice, itar[4]);

        var ear99 = Markings.Create(ExportJurisdiction.Ear, DistributionStatement.C, "ENG", eccn: "EAR99", controllingOffice: "ACME", distributionReason: "Admin").Value;
        Assert.DoesNotContain(MarkingStatements.ExportControlWarning, MarkingStatements.Notices(ear99));
        Assert.Contains("Export classification: EAR, ECCN EAR99.", MarkingStatements.Notices(ear99));
    }
}
