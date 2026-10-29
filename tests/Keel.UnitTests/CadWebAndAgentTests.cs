using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bunit;
using Keel.Cad;
using Keel.CadAgent;
using Keel.Core;
using Keel.Web.Components.Pages.Cad;

namespace Keel.UnitTests;

public sealed class CadWebAndAgentTests : BunitContext
{
    private static DependencyNode Node(string? number, bool stale = false, bool redacted = false, params DependencyNode[] children) =>
        redacted
            ? new DependencyNode(null, null, null, null, null, null, null, null, true, "A", 1, stale, false, [])
            : new DependencyNode(number, "Housing", "model", "A", 3, "InWork", "UNCLASSIFIED", null, false, "A", 2, stale, false, children);

    [Fact]
    public void The_dependency_tree_states_staleness_in_words_and_names_nothing_about_hidden_documents()
    {
        var cut = Render<DependencyTree>(p => p.Add(x => x.Nodes, [Node("D-000001", stale: true, children: [Node("D-000002"), Node(null, stale: true, redacted: true)])]));
        Assert.Contains("Stale", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("built against A.2, now A.3", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("A document you are not authorized to see", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(2, cut.FindAll("a").Count); // only the two visible documents link anywhere
        Assert.Equal("documents/D-000001", cut.FindAll("a")[0].GetAttribute("href"));
    }

    private sealed class FakeServer(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery));
            return Task.FromResult(respond(request));
        }
    }

    private static byte[] ZipOf(string fileName, byte[] content)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using (var w = zip.CreateEntry("MARKINGS.txt").Open()) w.Write("UNCLASSIFIED"u8);
            using var f = zip.CreateEntry(fileName).Open();
            f.Write(content);
        }
        return ms.ToArray();
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Checkout_downloads_verifies_and_tracks_and_a_failed_download_releases_the_set()
    {
        var bytes = Encoding.UTF8.GetBytes("model bytes");
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var id = Guid.NewGuid();
        string Manifest(string hash) => $$"""{"id":"{{id}}","members":[{"number":"D-000001","cadRole":"model","references":[],"files":[{"fileName":"housing.prt","role":"primary","sha256":"{{hash}}"}]}]}""";
        var good = new FakeServer(r => r.RequestUri!.AbsolutePath.EndsWith("/checkout", StringComparison.Ordinal) ? Json(HttpStatusCode.Created, Manifest(sha))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ZipOf("housing.prt", bytes)) });
        var dir = Directory.CreateTempSubdirectory("keel-cad-test-").FullName;
        var agent = new CadConnector(new HttpClient(good) { BaseAddress = new Uri("http://keel.test") });
        var state = await agent.CheckOutAsync("D-000001", dir, null, false);
        Assert.Equal(id, state.CheckoutId);
        Assert.Equal("model bytes", await File.ReadAllTextAsync(Path.Combine(dir, "housing.prt")));
        Assert.True(File.Exists(Path.Combine(dir, "housing.prt.markings.txt")));
        // Nothing changed locally: a push uploads nothing.
        Assert.Empty(await agent.PushChangesAsync(dir));
        Assert.DoesNotContain(good.Requests, q => q.Method == HttpMethod.Put);
        // Changing the file makes the next push upload it, with the new hash.
        await File.WriteAllTextAsync(Path.Combine(dir, "housing.prt"), "changed");
        var up = new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var pushed = await new CadConnector(new HttpClient(up) { BaseAddress = new Uri("http://keel.test") }).PushChangesAsync(dir);
        Assert.Equal(["housing.prt"], pushed);
        Assert.Contains(up.Requests, q => q.Method == HttpMethod.Put && q.Path.Contains("fileName=housing.prt", StringComparison.Ordinal));

        // A download whose bytes do not match the manifest hash fails, leaves no state and releases the set.
        var bad = new FakeServer(r =>
            r.RequestUri!.AbsolutePath.EndsWith("/checkout", StringComparison.Ordinal) ? Json(HttpStatusCode.Created, Manifest(new string('0', 64)))
            : r.RequestUri.AbsolutePath.EndsWith("/undo", StringComparison.Ordinal) ? Json(HttpStatusCode.OK, "[]")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ZipOf("housing.prt", bytes)) });
        var dir2 = Directory.CreateTempSubdirectory("keel-cad-test-").FullName;
        var e = await Assert.ThrowsAsync<AgentException>(() => new CadConnector(new HttpClient(bad) { BaseAddress = new Uri("http://keel.test") }).CheckOutAsync("D-000001", dir2, null, false));
        Assert.Equal("cad.integrity_mismatch", e.Code);
        Assert.Contains(bad.Requests, q => q.Path.EndsWith("/undo", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(dir2, ".keel-cad")));
    }

    [Fact]
    public async Task The_cli_reports_usage_errors_server_refusals_and_exit_codes()
    {
        var server = new FakeServer(_ => Json(HttpStatusCode.Conflict, """{"title":"D-000001: Already checked out by bob.","code":"cad.locked"}"""));
        var client = new HttpClient(server) { BaseAddress = new Uri("http://keel.test") };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "t");
        var @out = new StringWriter(); var err = new StringWriter();
        Assert.Equal(1, await Cli.RunAsync(["checkout"], @out, err, CancellationToken.None, client));
        Assert.Equal(1, await Cli.RunAsync(["nonsense"], @out, err, CancellationToken.None, client));
        err.GetStringBuilder().Clear();
        var dir = Path.Combine(Directory.CreateTempSubdirectory("keel-cad-test-").FullName, "pkg");
        Assert.Equal(2, await Cli.RunAsync(["checkout", "D-000001", "--dir", dir], @out, err, CancellationToken.None, client));
        Assert.Contains("[cad.locked]", err.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await Cli.RunAsync(["bom", "push", "x.csv", "--title", "t"], @out, err, CancellationToken.None, client)); // markings are required
        Assert.Equal("step", CadConnector.FormatFromExtension("a.STP"));
        Assert.Null(CadConnector.FormatFromExtension("a.bin"));
        // No token, no call.
        Assert.Equal(1, await Cli.RunAsync(["push", "--dir", dir], @out, err, CancellationToken.None, new HttpClient(server) { BaseAddress = new Uri("http://keel.test") }));
        _ = JsonNode.Parse("{}");
    }
}
