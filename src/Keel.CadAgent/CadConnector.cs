using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Keel.CadAgent;

/// <summary>The server refused a request. <see cref="Code"/> is the protocol's stable error code when there is one (docs/integration/cad-connector.md).</summary>
public sealed class AgentException(string message, int status, string? code) : Exception(message)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
}

/// <summary>The state kept in <c>.keel-cad/state.json</c> of a checked-out package folder.</summary>
public sealed class PackageState
{
    public Guid CheckoutId { get; set; }
    public string Top { get; set; } = "";
    public List<MemberState> Members { get; set; } = [];
}

public sealed class MemberState
{
    public string Number { get; set; } = "";
    public string Role { get; set; } = "";
    public bool IsNew { get; set; }
    public List<string> References { get; set; } = [];
    public List<FileState> Files { get; set; } = [];
}

public sealed class FileState
{
    public string FileName { get; set; } = "";
    public string FileRole { get; set; } = "primary";
    public string Sha256 { get; set; } = "";
}

/// <summary>
/// The reference implementation of the connector protocol: check a package out into a folder, push changed files, check it in as a set, send a CAD
/// structure as a proposal, and watch the folder. It uses only the REST API and the bearer token it is given; a vendor plug-in does the same calls.
/// </summary>
public sealed class CadConnector(HttpClient http, Action<string>? log = null)
{
    public const string StateDirectory = ".keel-cad";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Action<string> _log = log ?? (_ => { });

    // ---------------------------------------------------------------- check-out

    public async Task<PackageState> CheckOutAsync(string top, string dir, IReadOnlyList<string>? roles, bool allowPartial, CancellationToken ct = default)
    {
        if (Directory.Exists(Path.Combine(dir, StateDirectory))) throw new AgentException($"{dir} already holds a checked-out package; check it in or undo it first.", 0, null);
        var manifest = await SendAsync(HttpMethod.Post, "/api/v1/cad/packages/checkout", new { top, roles, includeDependents = true, allowPartial }, ct);
        var state = new PackageState { CheckoutId = manifest["id"]!.GetValue<Guid>(), Top = top };
        Directory.CreateDirectory(Path.Combine(dir, StateDirectory));
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var m in manifest["members"]!.AsArray())
            {
                var member = new MemberState { Number = m!["number"]!.GetValue<string>(), Role = m["cadRole"]!.GetValue<string>(), References = [.. m["references"]!.AsArray().Select(r => r!.GetValue<string>())] };
                foreach (var f in m["files"]!.AsArray())
                {
                    var file = new FileState { FileName = f!["fileName"]!.GetValue<string>(), FileRole = f["role"]!.GetValue<string>(), Sha256 = f["sha256"]!.GetValue<string>() };
                    if (!names.TryAdd(file.FileName, member.Number)) throw new AgentException($"{member.Number} and {names[file.FileName]} both have a file named {file.FileName}; a package folder needs unique file names.", 0, null);
                    await DownloadAsync(state.CheckoutId, member.Number, file, dir, ct);
                    member.Files.Add(file);
                }
                state.Members.Add(member);
            }
        }
        catch
        {
            // Never leave the user holding locks they cannot see: release the set when the folder could not be filled.
            try { await SendAsync(HttpMethod.Post, $"/api/v1/cad/checkouts/{state.CheckoutId}/undo", null, CancellationToken.None); } catch (AgentException) { }
            Directory.Delete(Path.Combine(dir, StateDirectory), true);
            throw;
        }
        Save(dir, state);
        _log($"Checked out {state.Members.Count} document(s) into {dir}.");
        return state;
    }

    private async Task DownloadAsync(Guid id, string number, FileState file, string dir, CancellationToken ct)
    {
        using var res = await http.GetAsync($"/api/v1/cad/checkouts/{id}/members/{Uri.EscapeDataString(number)}/files/{file.Sha256}", HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw await ErrorAsync(res, ct);
        await using var zipStream = await res.Content.ReadAsStreamAsync(ct);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = zip.GetEntry(file.FileName) ?? throw new AgentException($"The download of {file.FileName} did not contain the file.", 0, null);
        var target = SafePath(dir, file.FileName);
        await using (var output = File.Create(target)) await using (var input = await entry.OpenAsync(ct)) await input.CopyToAsync(output, ct);
        if (await HashAsync(target, ct) != file.Sha256) { File.Delete(target); throw new AgentException($"{file.FileName} failed the SHA-256 check after download.", 0, "cad.integrity_mismatch"); }
        // The cover sheet text travels beside the file so the markings are not lost when the folder is copied.
        if (zip.GetEntry("MARKINGS.txt") is { } marks)
        {
            await using var o = File.Create(target + ".markings.txt");
            await using var i = await marks.OpenAsync(ct);
            await i.CopyToAsync(o, ct);
        }
    }

    // ---------------------------------------------------------------- upload, check-in

    /// <summary>Uploads every tracked file whose bytes differ from the checked-out hash. Returns what was uploaded.</summary>
    public async Task<IReadOnlyList<string>> PushChangesAsync(string dir, CancellationToken ct = default)
    {
        var state = Load(dir);
        var pushed = new List<string>();
        foreach (var m in state.Members)
            foreach (var f in m.Files)
            {
                var path = SafePath(dir, f.FileName);
                if (!File.Exists(path)) throw new AgentException($"{f.FileName} (of {m.Number}) is missing from {dir}.", 0, null);
                var sha = await HashAsync(path, ct);
                if (sha == f.Sha256) continue;
                await UploadAsync(state.CheckoutId, m.Number, f.FileRole, f.FileName, path, sha, ct);
                f.Sha256 = sha;
                pushed.Add(f.FileName);
                Save(dir, state);
            }
        return pushed;
    }

    private async Task UploadAsync(Guid id, string number, string role, string fileName, string path, string sha, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/cad/checkouts/{id}/members/{Uri.EscapeDataString(number)}/files?fileName={Uri.EscapeDataString(fileName)}&role={role}") { Content = content };
        req.Headers.Add("X-Content-SHA256", sha);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw await ErrorAsync(res, ct);
        _log($"Uploaded {fileName}.");
    }

    /// <summary>Adds a new CAD file (for example a new drawing) to the package: creates the document, uploads the file and tracks it.</summary>
    public async Task<MemberState> AddAsync(string dir, string file, string name, string documentType, string role, IReadOnlyList<string> references, CancellationToken ct = default)
    {
        var state = Load(dir);
        var fileName = Path.GetFileName(file);
        var path = SafePath(dir, fileName);
        if (!File.Exists(path)) throw new AgentException($"{fileName} must be in {dir} to be added.", 0, null);
        var added = await SendAsync(HttpMethod.Post, $"/api/v1/cad/checkouts/{state.CheckoutId}/members", new { name, documentType, cadRole = role, references }, ct);
        var member = new MemberState { Number = added["number"]!.GetValue<string>(), Role = role, IsNew = true, References = [.. references] };
        state.Members.Add(member);
        var sha = await HashAsync(path, ct);
        await UploadAsync(state.CheckoutId, member.Number, "primary", fileName, path, sha, ct);
        member.Files.Add(new FileState { FileName = fileName, FileRole = "primary", Sha256 = sha });
        Save(dir, state);
        return member;
    }

    /// <summary>Uploads what changed and checks the whole set in atomically. <paramref name="references"/> maps a document number to the numbers it was built against.</summary>
    public async Task<JsonNode> CheckInAsync(string dir, string? comment, bool skipUnchanged, IReadOnlyDictionary<string, string[]>? references, CancellationToken ct = default)
    {
        await PushChangesAsync(dir, ct);
        var state = Load(dir);
        var members = state.Members.Select(m => new
        {
            number = m.Number,
            references = references is not null && references.TryGetValue(m.Number, out var r) ? r : m.IsNew ? m.References.ToArray() : null,
            files = m.Files.Select(f => new { fileName = f.FileName, sha256 = f.Sha256 }).ToArray(),
        }).ToArray();
        var result = await SendAsync(HttpMethod.Post, $"/api/v1/cad/checkouts/{state.CheckoutId}/checkin", new { comment, skipUnchanged, members }, ct);
        Directory.Delete(Path.Combine(dir, StateDirectory), true);
        _log($"Checked in {result["created"]!.AsArray().Count} document(s).");
        return result;
    }

    public async Task UndoAsync(string dir, CancellationToken ct = default)
    {
        var state = Load(dir);
        await SendAsync(HttpMethod.Post, $"/api/v1/cad/checkouts/{state.CheckoutId}/undo", null, ct);
        Directory.Delete(Path.Combine(dir, StateDirectory), true);
        _log("Released the check-out; the folder keeps your local files.");
    }

    // ---------------------------------------------------------------- watch

    /// <summary>One pass of the watcher: uploads changed tracked files to the working copy (never checks in). Returns the files uploaded.</summary>
    public Task<IReadOnlyList<string>> WatchOnceAsync(string dir, CancellationToken ct = default) => PushChangesAsync(dir, ct);

    public async Task WatchAsync(string dir, TimeSpan interval, CancellationToken ct)
    {
        _log($"Watching {dir} every {interval.TotalSeconds:0} s. Stop with Ctrl+C; check in with 'keel-cad checkin'.");
        while (!ct.IsCancellationRequested)
        {
            try { await WatchOnceAsync(dir, ct); }
            catch (AgentException e) { _log("Not uploaded: " + e.Message); }
            try { await Task.Delay(interval, ct); } catch (OperationCanceledException) { break; }
        }
    }

    // ---------------------------------------------------------------- structure proposal

    public async Task<JsonNode> PushBomAsync(string file, string format, string title, JsonObject markings, string? root, bool applyRemovals, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(file);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(new JsonObject { ["title"] = title, ["format"] = format, ["markings"] = markings.DeepClone(), ["rootNumber"] = root, ["applyRemovals"] = applyRemovals }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), "metadata");
        form.Add(new StreamContent(stream), "file", Path.GetFileName(file));
        using var res = await http.PostAsync("/api/v1/cad/bom-proposals", form, ct);
        if (!res.IsSuccessStatusCode) throw await ErrorAsync(res, ct);
        return (await res.Content.ReadFromJsonAsync<JsonNode>(Json, ct))!;
    }

    /// <summary>The markings of a document in the shape a proposal needs (a proposal takes the markings of the data it was drawn from).</summary>
    public async Task<JsonObject> MarkingsOfDocumentAsync(string number, CancellationToken ct = default)
    {
        var doc = await SendAsync(HttpMethod.Get, $"/api/v1/documents/{Uri.EscapeDataString(number)}", null, ct);
        var m = doc["markings"]!.AsObject();
        var result = new JsonObject();
        foreach (var key in new[] { "exportJurisdiction", "distributionStatement", "owningOrg", "eccn", "usmlCategory", "cuiCategories", "controllingOffice", "distributionReason", "program", "exportAuthorizations" })
            if (m[key] is { } v) result[key] = v.DeepClone();
        return result;
    }

    public static string? FormatFromExtension(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".json" => "json", ".stp" or ".step" or ".p21" => "step", ".csv" => "csv", ".xml" => "xml", _ => null,
    };

    // ---------------------------------------------------------------- plumbing

    private async Task<JsonNode> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw await ErrorAsync(res, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!;
    }

    private static async Task<AgentException> ErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        string? title = null, code = null;
        try
        {
            var p = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
            title = p?["title"]?.GetValue<string>();
            code = p?["code"]?.GetValue<string>();
            if (p?["errors"] is JsonObject errors) title = string.Join(" ", errors.SelectMany(e => e.Value!.AsArray().Select(x => x!.GetValue<string>())));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        return new AgentException($"{(int)res.StatusCode} {title ?? res.ReasonPhrase}", (int)res.StatusCode, code);
    }

    internal static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
    }

    /// <summary>Names come from the server; a name that would leave the folder is refused (the server already forbids separators, this is the second lock).</summary>
    private static string SafePath(string dir, string fileName)
    {
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName is "." or ".." or "" || fileName.StartsWith(StateDirectory, StringComparison.Ordinal))
            throw new AgentException($"'{fileName}' is not a usable file name.", 0, null);
        return Path.Combine(dir, fileName);
    }

    private static string StatePath(string dir) => Path.Combine(dir, StateDirectory, "state.json");
    public static PackageState Load(string dir) =>
        File.Exists(StatePath(dir)) ? JsonSerializer.Deserialize<PackageState>(File.ReadAllText(StatePath(dir)), Json)! : throw new AgentException($"{dir} is not a checked-out package folder (no {StateDirectory}).", 0, null);
    private static void Save(string dir, PackageState s) => File.WriteAllText(StatePath(dir), JsonSerializer.Serialize(s, Json));
}
