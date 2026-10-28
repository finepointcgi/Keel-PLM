using System.Text;
using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.Documents;

namespace Keel.IntegrationTests;

/// <summary>Documents pinned by a baseline (describes and references links, resolved at creation) and the redaction of pinned documents.</summary>
[Collection(PostgresCollection.Name)]
public sealed class BaselineDocumentTests(PostgresFixture pg)
{
    private static readonly Subject Kim = WorkflowEnv.User("kim", [Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]);

    private static FileUpload File(string name) => new(name, "text/plain", new MemoryStream(Encoding.UTF8.GetBytes($"{name} {Guid.NewGuid()}")));

    [Fact]
    public async Task Linked_documents_are_pinned_at_creation_and_shown_with_redaction()
    {
        var env = await ConfigEnv.CreateAsync(pg, documents: true);
        var drawing = (await env.Docs!.CreateAsync(ConfigEnv.Alice, new CreateDocumentCommand("Plate drawing", "Drawing", ConfigEnv.Open), File("plate.dwg"), null)).Value!.Number;
        var spec = (await env.Docs.CreateAsync(ConfigEnv.Alice, new CreateDocumentCommand("Anodizing specification", "Specification", ConfigEnv.Open), File("spec.txt"), null)).Value!.Number;
        var plate = await env.NewPartAsync("Plate");
        await env.CheckOutAsync(plate);
        Assert.True((await env.Docs.AddLinkToWorkingCopyAsync(ConfigEnv.Alice, plate, drawing, "describes", null, null)).IsOk);
        Assert.True((await env.Docs.AddLinkToWorkingCopyAsync(ConfigEnv.Alice, plate, spec, "references", null, null)).IsOk);
        await env.CheckInAsync(plate);
        await env.ReleaseAsync(plate);
        var top = await env.ReleasedAssemblyAsync("Top", (plate, 2m));

        var bl = await env.BaselineAsync(top, docs: true);
        Assert.Equal(2, bl.DocumentCount);
        var documents = (await env.Baselines.GetDocumentsAsync(ConfigEnv.Alice, bl.Summary.Number, "corr-docs")).Value!;
        Assert.Equal([drawing, spec], documents.Rows.Select(r => r.Number).Order(StringComparer.Ordinal));
        Assert.All(documents.Rows, r => { Assert.Equal("10", r.ItemPath); Assert.Equal("A", r.Revision); Assert.False(r.Redacted); });
        Assert.Contains(await env.AuditAsync(), e => e.Action == "baseline.documents" && e.CorrelationId == "corr-docs");

        // The pinned revision ids are in the stored rows and in the content hash (verification still passes).
        Assert.Equal(2, await env.ScalarAsync("SELECT count(*) FROM cfg.baseline_document"));
        Assert.True((await env.Baselines.VerifyAsync(ConfigEnv.Alice, bl.Summary.Number, null)).Value!.Intact);
        Assert.Equal(0, (await env.BaselineAsync(top, name: "No documents")).DocumentCount);

        // Raise the specification's markings after the fact: a reader without the program sees a placeholder, never the document.
        await env.Db.ExecuteAsSuperuserAsync("UPDATE plm.document_master SET export_jurisdiction = 'Itar', usml_category = 'XI(d)', distribution_statement = 'D', " +
            "controlling_office = 'ACME', distribution_reason = 'Critical technology', program = 'KRAKEN', cui_categories = ARRAY['EXPT'] WHERE number = '" + spec + "'");
        var eve = (await env.Baselines.GetDocumentsAsync(ConfigEnv.Eve, bl.Summary.Number, null)).Value!;
        Assert.Contains(eve.Rows, r => r.Redacted && r.Number is null && r.Name is null && r.Markings is null);
        Assert.DoesNotContain(spec, string.Join(' ', eve.Rows.Select(r => r.Number)), StringComparison.Ordinal);
        Assert.Contains(eve.Rows, r => r.Number == drawing);
    }

    [Fact]
    public async Task A_creator_who_cannot_read_a_linked_document_cannot_pin_it()
    {
        var env = await ConfigEnv.CreateAsync(pg, documents: true);
        var report = (await env.Docs!.CreateAsync(Kim, new CreateDocumentCommand("Kraken load report", "Report", ConfigEnv.Itar("KRAKEN")), File("loads.txt"), null)).Value!.Number;
        var plate = await env.NewPartAsync("Plate");
        await env.CheckOutAsync(plate, Kim);
        Assert.True((await env.Docs.AddLinkToWorkingCopyAsync(Kim, plate, report, "references", null, null)).IsOk);
        await env.CheckInAsync(plate, Kim);
        var top = await env.AssemblyAsync("Top", (plate, 1m));

        var refused = await env.Baselines.CreateAsync(ConfigEnv.Alice, new CreateBaselineCommand(top, "With docs", "Design", BomConfigSpec.Latest, IncludeDocuments: true), "corr-docs");
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        Assert.Contains("1 linked document is not accessible", refused.Errors[0], StringComparison.Ordinal);
        Assert.DoesNotContain(report, refused.Errors[0], StringComparison.Ordinal);
        Assert.Contains(await env.AuditAsync(), e => e.CorrelationId == "corr-docs" && e.Outcome == AuditOutcome.Denied && e.Payload.Contains("documents.unreadable", StringComparison.Ordinal));
        // Without pinning documents the same structure can be baselined.
        Assert.True((await env.Baselines.CreateAsync(ConfigEnv.Alice, new CreateBaselineCommand(top, "Without docs", "Design", BomConfigSpec.Latest), null)).IsOk);
        // Kim can read the document, so the baseline's markings are raised to cover it (with confirmation).
        var kim = await env.Baselines.CreateAsync(Kim, new CreateBaselineCommand(top, "Kim's", "Design", BomConfigSpec.Latest, IncludeDocuments: true, Markings: ConfigEnv.Open), null);
        Assert.Equal(OutcomeKind.Invalid, kim.Kind);
        Assert.Contains("do not cover", kim.Errors[0], StringComparison.Ordinal);
        Assert.True((await env.Baselines.CreateAsync(Kim, new CreateBaselineCommand(top, "Kim's", "Design", BomConfigSpec.Latest, IncludeDocuments: true, Markings: ConfigEnv.Open, ConfirmRaiseMarkings: true), null)).IsOk);
    }
}
