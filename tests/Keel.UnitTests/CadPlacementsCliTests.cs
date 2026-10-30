using System.Net;
using System.Text.Json;
using Keel.Cad;
using Keel.Cad.Structure;
using Keel.CadAgent;
using Keel.Core;
using Keel.Core.Viewing;

namespace Keel.UnitTests;

/// <summary>The <c>keel-cad placements</c> and <c>keel-cad bom placements</c> commands against the JSON the CAD API produces (ADR-0052).</summary>
public sealed class CadPlacementsCliTests
{
    private sealed class FakeServer(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string ApiJson()
    {
        var model = StructureReader.Read("step", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cad", "bracket-asm.step")));
        var markings = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;
        var file = new ViewableFile("D-000001", "Bracket", Guid.NewGuid(), "bracket-asm.step", "application/octet-stream", 1, new string('a', 64), "A.1", markings, markings.BannerText(), []);
        var result = new AssemblyViewResult(AssemblyViewStatus.Ok, null, file, AssemblyViewKinds.Assembly, "step", model, AssemblyLayouts.Expand(model));
        return JsonSerializer.Serialize(DocumentPlacements.From(result), Web);
    }

    private static HttpClient Client(FakeServer server)
    {
        var client = new HttpClient(server) { BaseAddress = new Uri("http://keel.test") };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "t");
        return client;
    }

    [Fact]
    public async Task The_document_command_prints_the_instances_with_positions_and_boxes()
    {
        var server = new FakeServer(ApiJson());
        var @out = new StringWriter(); var err = new StringWriter();
        Assert.Equal(0, await Cli.RunAsync(["placements", "D-000001", "--revision", "B"], @out, err, CancellationToken.None, Client(server)));
        Assert.Equal("/api/v1/cad/documents/D-000001/placements?revision=B", Assert.Single(server.Paths));
        var text = @out.ToString();
        Assert.Contains("mm", text, StringComparison.Ordinal);
        Assert.Contains("KA-3001", text, StringComparison.Ordinal);
        Assert.Contains("KF-2001 Screw M5x16 [0/2] at 5,5,3; box 1,1.536,3 to 9,8.464,22", text, StringComparison.Ordinal);
        Assert.Equal("", err.ToString());
    }

    [Fact]
    public async Task The_proposal_command_can_print_the_raw_json_and_a_refusal_is_reported_with_its_exit_code()
    {
        var server = new FakeServer(ApiJson());
        var @out = new StringWriter(); var err = new StringWriter();
        Assert.Equal(0, await Cli.RunAsync(["bom", "placements", "CBP-000007", "--json"], @out, err, CancellationToken.None, Client(server)));
        Assert.Equal("/api/v1/cad/bom-proposals/CBP-000007/placements", Assert.Single(server.Paths));
        using var doc = JsonDocument.Parse(@out.ToString());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("instances").ValueKind);

        var missing = new FakeServer("""{"title":"Not found"}""", HttpStatusCode.NotFound);
        Assert.Equal(2, await Cli.RunAsync(["bom", "placements", "CBP-000009"], @out, err, CancellationToken.None, Client(missing)));
        Assert.Contains("404", err.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await Cli.RunAsync(["placements"], @out, err, CancellationToken.None, Client(server)));
    }
}
