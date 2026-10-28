using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Keel.Documents.Vault;
using Microsoft.AspNetCore.Hosting;

namespace Keel.IntegrationTests;

/// <summary>
/// A store that accepts connections but never answers (a hung gateway, a half-open network path) must produce a bounded
/// failure: <see cref="VaultUnavailableException"/> in the vault, 503 with Retry-After at the API (packaging increment 1.7).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VaultTimeoutTests(PostgresFixture pg)
{
    private static readonly string Alice = ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"]);

    /// <summary>Accepts TCP connections and then says nothing, until disposed.</summary>
    private sealed class BlackHole : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _clients = [];
        private readonly CancellationTokenSource _stop = new();

        public BlackHole()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        lock (_clients) _clients.Add(client);
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            });
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            lock (_clients) foreach (var c in _clients) c.Dispose();
        }
    }

    private static VaultOptions Options(string url, int timeoutSeconds) => new()
    {
        ServiceUrl = url, AccessKey = "k", SecretKey = "s", Bucket = "keel-vault", RequestTimeoutSeconds = timeoutSeconds,
    };

    [Fact]
    public async Task Requests_to_a_silent_store_fail_within_the_timeout()
    {
        using var hole = new BlackHole();
        using var vault = new ObjectVault(Options(hole.Url, 1));

        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAsync<VaultUnavailableException>(() => vault.PingAsync());
        await Assert.ThrowsAsync<VaultUnavailableException>(() => vault.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes("synthetic"))));
        await Assert.ThrowsAsync<VaultUnavailableException>(() => vault.OpenReadAsync(new string('a', 64), 1));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"three calls took {clock.Elapsed}");
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_an_unavailable_vault()
    {
        using var hole = new BlackHole();
        using var vault = new ObjectVault(Options(hole.Url, 30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.PingAsync(cts.Token));
    }

    [Fact]
    public void Timeout_must_be_within_bounds()
    {
        Assert.Empty(Options("http://x", 30).Validate());
        Assert.Contains(Options("http://x", 0).Validate(), e => e.Contains("RequestTimeoutSeconds", StringComparison.Ordinal));
        Assert.Contains(Options("http://x", 601).Validate(), e => e.Contains("RequestTimeoutSeconds", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upload_to_a_silent_store_answers_503_with_retry_after()
    {
        using var hole = new BlackHole();
        await using var api = await ApiFactory.CreateAsync(pg);
        await using var broken = api.WithWebHostBuilder(b =>
        {
            b.UseSetting("Keel:Vault:ServiceUrl", hole.Url);
            b.UseSetting("Keel:Vault:RequestTimeoutSeconds", "1");
        });
        var alice = broken.CreateClient();
        alice.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Alice);

        var metadata = """{"name":"Timeout drawing","documentType":"Drawing","markings":{"exportJurisdiction":"Itar","distributionStatement":"D","owningOrg":"ENG","usmlCategory":"XI(d)","cuiCategories":["EXPT"],"controllingOffice":"ACME","distributionReason":"Critical technology","program":"ORCA"}}""";
        using var form = new MultipartFormDataContent
        {
            { new StringContent(metadata, Encoding.UTF8, "application/json"), "metadata" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("synthetic bytes " + Guid.NewGuid())), "file", "t.txt" },
        };
        var clock = Stopwatch.StartNew();
        var res = await alice.PostAsync("/api/v1/documents", form);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal(5, res.Headers.RetryAfter!.Delta!.Value.TotalSeconds);
        Assert.Contains("Document vault unavailable", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
    }
}
