using Bunit;
using Keel.Core;
using Keel.Documents;
using Keel.Web.Components.Shared;
using Keel.Web.Services;

namespace Keel.UnitTests;

public sealed class DocumentComponentTests : BunitContext
{
    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)",
        cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;

    private static DocumentFileView File(string role, string name) =>
        new(Guid.CreateVersion7(), role, name, "application/pdf", 2_621_440, new string('a', 64), DateTimeOffset.UnixEpoch, "alice");

    [Fact]
    public void File_list_links_to_marked_downloads_without_tokens()
    {
        var primary = File("primary", "housing.pdf");
        var cut = Render<DocumentFileList>(p => p.Add(x => x.Files, [primary, File("attachment", "loads.csv")]).Add(x => x.DownloadNumber, "D-000001"));

        var links = cut.FindAll("a[download]");
        Assert.Equal(2, links.Count);
        Assert.Equal($"downloads/documents/D-000001/files/{primary.Id:D}", links[0].GetAttribute("href"));
        Assert.All(links, a => Assert.DoesNotContain("token", a.GetAttribute("href"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains("2.5 MiB", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("button")); // no remove buttons outside the working copy
    }

    [Fact]
    public void Working_copy_file_list_offers_remove_and_no_download()
    {
        Guid? removed = null;
        var file = File("attachment", "notes.txt");
        var cut = Render<DocumentFileList>(p => p.Add(x => x.Files, [file]).Add(x => x.OnRemove, (Guid id) => removed = id));
        Assert.Empty(cut.FindAll("a[download]"));
        cut.Find("button").Click();
        Assert.Equal(file.Id, removed);

        Assert.Contains("Nothing here", Render<DocumentFileList>(p => p.Add(x => x.Files, []).Add(x => x.Empty, "Nothing here")).Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Link_list_shows_redacted_placeholders_without_document_details()
    {
        var visible = new PartDocumentLinkView(Guid.CreateVersion7(), "describes", false, false,
            new LinkedDocumentView("D-000001", "Bracket drawing", "Drawing", "A", 2, "InWork", Itar.BannerText(), Itar), "A", true, DateTimeOffset.UnixEpoch, "alice");
        var hidden = new PartDocumentLinkView(Guid.CreateVersion7(), "references", false, true, null, null, false, null, null);
        var cut = Render<DocumentLinkList>(p => p.Add(x => x.Links, [visible, hidden]));

        var rows = cut.FindAll("tbody tr");
        Assert.Equal(2, rows.Count);
        Assert.Contains("D-000001", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Newer revision available", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("(pinned)", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Restricted document", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("References", rows[1].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("CUI", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Equal(hidden.LinkId.ToString(), rows[1].GetAttribute("data-link-id"));
    }

    [Fact]
    public void Link_list_remove_button_reports_the_link()
    {
        Guid? removed = null;
        var hidden = new PartDocumentLinkView(Guid.CreateVersion7(), "references", true, true, null, null, false, null, null);
        var cut = Render<DocumentLinkList>(p => p.Add(x => x.Links, [hidden]).Add(x => x.OnRemove, (Guid id) => removed = id));
        cut.Find("button").Click();
        Assert.Equal(hidden.LinkId, removed);
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KiB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 GiB")]
    public void Sizes_are_human_readable(long bytes, string expected) => Assert.Equal(expected, DocumentUi.Size(bytes));
}
