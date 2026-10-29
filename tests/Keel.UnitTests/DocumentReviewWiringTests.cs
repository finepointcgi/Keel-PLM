using System.Text.RegularExpressions;
using Keel.Workflow;

namespace Keel.UnitTests;

/// <summary>Document review (7.6, ADR-0033): the wiring that makes documents reviewable like parts is present in the host, the seeder and the data.</summary>
public sealed class DocumentReviewWiringTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    [Fact]
    public void The_host_and_the_seeder_register_the_document_gateway_next_to_the_part_gateway()
    {
        Assert.Contains("IWorkflowObjectGateway, Keel.Documents.DocumentWorkflowGateway", Read("src", "Keel.Api", "Program.cs"), StringComparison.Ordinal);
        Assert.Contains("IWorkflowObjectGateway, DocumentWorkflowGateway", Read("src", "Keel.Seed", "SeedHost.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_seeded_document_template_in_migration_0017_is_a_valid_template_for_documents()
    {
        var sql = Read("db", "migrations", "0017_document_review.sql");
        var json = Regex.Match(sql, @"\$tpl\$(?<json>[\s\S]*?)\$tpl\$").Groups["json"].Value;
        var parsed = TemplateParser.Parse(json);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Errors));
        Assert.Equal(("document-release-review", "document"), (parsed.Value.Id, parsed.Value.ObjectType));
        Assert.Equal(["reviewer", "approvers"], parsed.Value.Slots.Select(s => s.Name));
        // Like every migration it is forward-only and does not touch tables, only lifecycle data and one template row.
        Assert.DoesNotContain("CREATE TABLE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP ", sql, StringComparison.Ordinal);
        Assert.Contains("family = 'document-default'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_document_page_hosts_the_review_tab_and_the_workflow_pages_link_back_to_documents()
    {
        var page = Read("src", "Keel.Web", "Components", "Pages", "Documents", "DocumentPage.razor");
        Assert.Contains("new(\"review\", \"Review & release\")", page, StringComparison.Ordinal);
        Assert.Contains("<DocumentReviewPanel", page, StringComparison.Ordinal);
        Assert.Contains("\"document\" => $\"documents/", Read("src", "Keel.Web", "Components", "Pages", "Workflow", "WorkflowUi.cs"), StringComparison.Ordinal);
        Assert.Contains("\"document\" => \"document\"", Read("src", "Keel.Web", "Components", "Pages", "Workflow", "StartReviewDialog.razor"), StringComparison.Ordinal);
    }

    [Fact]
    public void Document_submission_and_status_endpoints_exist_beside_the_part_ones()
    {
        var endpoints = Read("src", "Keel.Api", "Endpoints", "WorkflowEndpoints.cs");
        Assert.Contains("\"/documents/{number}/review\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("\"/documents/{number}/workflow\"", endpoints, StringComparison.Ordinal);
    }

    [Fact]
    public void The_released_document_event_schema_allows_the_workflow_as_the_way_a_document_was_released()
    {
        var schema = Read("docs", "integration", "schemas", "document.released.v1.json");
        Assert.Matches(@"""via""\s*:\s*\{\s*""type""\s*:\s*""string"",\s*""enum""\s*:\s*\[\s*""admin"",\s*""workflow""\s*\]", schema);
    }
}
