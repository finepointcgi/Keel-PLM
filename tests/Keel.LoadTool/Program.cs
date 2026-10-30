using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// keel-load run --api <url> --idp <realm url> --bom-root <part number> [--user alice] [--duration 20] [--concurrency 1,8,32] [--warmup 3] [--json out.json] [--markdown out.md]
//
// Closed-loop load against a running Keel host with real tokens from the dev realm (client keel-dev-cli, password grant). Each (operation, concurrency) phase runs for
// --duration seconds with that many workers, each issuing the next request when the last one finished. Operations: list (page of the part list at random offsets), search
// (free text from names found in the data), create-part (POST /api/v1/parts, writes data and an audit event in one transaction), bom-expand-1 (one level of the root) and
// bom-expand-all (every level of the root). Latency is wall-clock time to the last byte of the body. Exit code 1 when any request failed (non-2xx), 2 for usage errors.
// It is a smoke measurement on one machine, not a capacity test: docs/ops/performance.md states what it does and does not show.

var opts = ParseOptions(args);
if (args.Length == 0 || args[0] != "run" || !opts.TryGetValue("api", out var apiArg) || !opts.TryGetValue("idp", out var idpArg) || !opts.TryGetValue("bom-root", out var bomRoot))
{
    Console.Error.WriteLine("usage: keel-load run --api URL --idp REALM_URL --bom-root PART [--user alice] [--duration 20] [--concurrency 1,8,32] [--warmup 3] [--json FILE] [--markdown FILE]");
    return 2;
}
var api = apiArg.TrimEnd('/');
var user = opts.GetValueOrDefault("user") ?? "alice";
var duration = TimeSpan.FromSeconds(double.Parse(opts.GetValueOrDefault("duration") ?? "20", CultureInfo.InvariantCulture));
var warmup = TimeSpan.FromSeconds(double.Parse(opts.GetValueOrDefault("warmup") ?? "3", CultureInfo.InvariantCulture));
var levels = (opts.GetValueOrDefault("concurrency") ?? "1,8,32").Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();

using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 256, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
string token = await GetTokenAsync();
var tokenAt = Stopwatch.StartNew();
async Task<string> GetTokenAsync()
{
    using var req = new HttpRequestMessage(HttpMethod.Post, idpArg.TrimEnd('/') + "/protocol/openid-connect/token")
    {
        Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "password", ["client_id"] = "keel-dev-cli", ["username"] = user, ["password"] = "keel-dev-" + user, ["scope"] = "openid" }),
    };
    using var res = await http.SendAsync(req);
    res.EnsureSuccessStatusCode();
    return JsonNode.Parse(await res.Content.ReadAsStringAsync())!["access_token"]!.GetValue<string>();
}

async Task<(HttpStatusCode Code, long Bytes, double Ms)> SendAsync(HttpMethod method, string path, string? json = null)
{
    var sw = Stopwatch.StartNew();
    using var req = new HttpRequestMessage(method, api + path);
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
    using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    var body = await res.Content.ReadAsByteArrayAsync();
    return (res.StatusCode, body.Length, sw.Elapsed.TotalMilliseconds);
}

// What is in the system: the total part count (to size random offsets), names to search for, and the BOM root's size.
var first = JsonNode.Parse(await (await Authorized("/api/v1/parts?limit=200&offset=0")).Content.ReadAsStringAsync())!;
var total = first["total"]!.GetValue<int>();
// Search terms: every distinct word of at least four letters from the names on the first pages, plus a number fragment, so the queries differ.
var words = first["items"]!.AsArray().SelectMany(n => (n!["name"]?.GetValue<string>() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
    .Concat(first["items"]!.AsArray().Select(n => n!["number"]?.GetValue<string>() ?? "")).Where(w => w.Length >= 4).Distinct().Take(60).ToArray();
if (words.Length == 0) words = ["part"];
var fullBom = await Authorized($"/api/v1/parts/{Uri.EscapeDataString(bomRoot)}/bom");
Console.Error.WriteLine($"data: {total} parts visible to {user}; search words {words.Length}; BOM root {bomRoot} response {(await fullBom.Content.ReadAsByteArrayAsync()).Length} bytes");

async Task<HttpResponseMessage> Authorized(string path)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, api + path);
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    var res = await http.SendAsync(req);
    res.EnsureSuccessStatusCode();
    return res;
}

const string Marks = """{"exportJurisdiction":"None","distributionStatement":"A","owningOrg":"ENG"}""";
var created = 0;
var operations = new (string Name, int MaxConcurrency, Func<Random, Task<(HttpStatusCode, long, double)>> Run)[]
{
    ("list", int.MaxValue, r => SendAsync(HttpMethod.Get, $"/api/v1/parts?limit=50&offset={r.Next(0, Math.Max(1, total - 50))}")),
    ("search", int.MaxValue, r => SendAsync(HttpMethod.Get, $"/api/v1/parts?limit=50&q={Uri.EscapeDataString(words[r.Next(words.Length)])}")),
    ("create-part", int.MaxValue, r =>
    {
        var n = Interlocked.Increment(ref created);
        return SendAsync(HttpMethod.Post, "/api/v1/parts", $$"""{"name":"Load smoke part {{n}} {{Guid.NewGuid():N}}","partType":"Component","defaultUnit":"EA","source":"Make","attributes":{"material":"6061-T6"},"markings":{{Marks}}}""");
    }),
    ("bom-expand-1", int.MaxValue, _ => SendAsync(HttpMethod.Get, $"/api/v1/parts/{Uri.EscapeDataString(bomRoot)}/bom?depth=1")),
    // every level of a 50,000-line structure is tens of megabytes of JSON per response; many in flight at once measure the response size, not the service, so this one stops at 8 workers
    ("bom-expand-all", 8, _ => SendAsync(HttpMethod.Get, $"/api/v1/parts/{Uri.EscapeDataString(bomRoot)}/bom")),
};

var results = new List<PhaseResult>();
var anyError = false;
foreach (var (name, maxConcurrency, run) in operations)
{
    Console.Error.WriteLine($"-- {name}: warm-up {warmup.TotalSeconds:0}s at concurrency 2");
    await Phase(name, run, 2, warmup, record: false);
    foreach (var c in levels.Where(l => l <= maxConcurrency))
    {
        if (tokenAt.Elapsed > TimeSpan.FromMinutes(3)) { token = await GetTokenAsync(); tokenAt.Restart(); }
        var r = await Phase(name, run, c, duration, record: true);
        results.Add(r);
        anyError |= r.Errors > 0;
        Console.Error.WriteLine($"   c={c,-3} {r.Requests,6} req  {r.Rps,8:0.0}/s  p50 {r.P50,8:0.0}  p95 {r.P95,8:0.0}  p99 {r.P99,8:0.0}  max {r.Max,8:0.0} ms  errors {r.Errors}");
    }
}

var summary = new
{
    startedAt = DateTimeOffset.UtcNow,
    api,
    user,
    partsVisible = total,
    bomRoot,
    durationSeconds = duration.TotalSeconds,
    results,
};
if (opts.TryGetValue("json", out var jsonFile)) await File.WriteAllTextAsync(jsonFile, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
var md = new StringBuilder();
md.AppendLine("| Operation | Concurrency | Requests | Errors | Requests/s | p50 ms | p95 ms | p99 ms | max ms | Avg response bytes |");
md.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
foreach (var r in results)
    md.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {r.Operation} | {r.Concurrency} | {r.Requests} | {r.Errors} | {r.Rps:0.0} | {r.P50:0} | {r.P95:0} | {r.P99:0} | {r.Max:0} | {r.AvgBytes:0} |"));
if (opts.TryGetValue("markdown", out var mdFile)) await File.WriteAllTextAsync(mdFile, md.ToString());
Console.WriteLine(md.ToString());
return anyError ? 1 : 0;

async Task<PhaseResult> Phase(string name, Func<Random, Task<(HttpStatusCode Code, long Bytes, double Ms)>> run, int concurrency, TimeSpan length, bool record)
{
    var latencies = new ConcurrentBag<double>();
    long errors = 0, bytes = 0;
    var statuses = new ConcurrentDictionary<int, int>();
    var stop = Stopwatch.StartNew();
    var workers = Enumerable.Range(0, concurrency).Select(i => Task.Run(async () =>
    {
        var rng = new Random(unchecked(Environment.TickCount * 31 + i));
        while (stop.Elapsed < length)
        {
            try
            {
                var (code, size, ms) = await run(rng);
                latencies.Add(ms);
                Interlocked.Add(ref bytes, size);
                statuses.AddOrUpdate((int)code, 1, (_, v) => v + 1);
                if ((int)code is < 200 or >= 300) Interlocked.Increment(ref errors);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                Interlocked.Increment(ref errors);
                statuses.AddOrUpdate(-1, 1, (_, v) => v + 1);
            }
        }
    })).ToArray();
    await Task.WhenAll(workers);
    var secs = stop.Elapsed.TotalSeconds;
    var sorted = latencies.Order().ToArray();
    double P(double q) => sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
    return record
        ? new PhaseResult(name, concurrency, sorted.Length + statuses.GetValueOrDefault(-1), (int)errors, sorted.Length / secs, P(0.50), P(0.95), P(0.99), sorted.Length == 0 ? double.NaN : sorted[^1], sorted.Length == 0 ? 0 : (double)bytes / sorted.Length, statuses.OrderBy(k => k.Key).ToDictionary(k => k.Key.ToString(CultureInfo.InvariantCulture), k => k.Value))
        : new PhaseResult(name, concurrency, 0, (int)errors, 0, 0, 0, 0, 0, 0, []);
}

static Dictionary<string, string> ParseOptions(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 1; i < a.Length; i++)
        if (a[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < a.Length) d[a[i][2..]] = a[++i];
    return d;
}

internal sealed record PhaseResult(string Operation, int Concurrency, int Requests, int Errors, double Rps, double P50, double P95, double P99, double Max, double AvgBytes, Dictionary<string, int> Statuses);
