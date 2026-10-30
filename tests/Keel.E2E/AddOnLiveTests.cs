using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Keel.E2E;

/// <summary>
/// The open-source CAD add-on (tools/freecad-keel) and the <c>keel-cad</c> command-line connector against the REAL host: real Keycloak, real bearer tokens of the dev
/// user alice (password grant of the dev client keel-dev-cli), real HTTP, PostgreSQL and vault. The Python steps are run as child processes
/// (tools/freecad-keel/scripts/live_server_check.py): under plain Python, and under FreeCAD's own Python through <c>freecadcmd</c> where a FreeCAD document is
/// needed. Every result is checked against what the server holds, read back through the API with a token of its own. Nothing here touches a FreeCAD
/// installation's user settings: the child processes get a temporary HOME and FREECAD_USER_HOME.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Category", "E2E")]
public sealed class AddOnLiveTests(E2EFixture fx)
{
    private E2EFixture Fx => fx;

    // The structure of tests/Keel.UnitTests/Fixtures/cad/frame-asm-*.step (and of the FreeCAD document the live script builds).
    private static readonly (string Parent, string Child, decimal Quantity)[] Frame =
    [
        ("KA-3000", "KA-3001", 2), ("KA-3000", "KP-1002", 1), ("KA-3000", "KF-2001", 2),
        ("KA-3001", "KP-1001", 1), ("KA-3001", "KF-2001", 4), ("KA-3001", "KF-2002", 4),
    ];

    // ---------------------------------------------------------------- the real host, as alice

    private async Task<string> TokenAsync(string user = "alice")
    {
        using var http = new HttpClient();
        var res = await http.PostAsync($"{fx.IdpUrl}/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password", ["client_id"] = "keel-dev-cli", ["username"] = user, ["password"] = $"keel-dev-{user}", ["scope"] = "openid",
        }));
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, "token request failed: " + body);
        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task<HttpClient> ApiAsync()
    {
        var c = new HttpClient { BaseAddress = new Uri(fx.BaseUrl), Timeout = TimeSpan.FromMinutes(2) };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
        return c;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {res.RequestMessage?.RequestUri?.AbsolutePath}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private const string Open = """{"exportJurisdiction":"None","distributionStatement":"A","owningOrg":"ENG"}""";

    private static async Task<string> CreateDocument(HttpClient c, string name, string type, byte[] bytes, string fileName)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent($$"""{"name":"{{name}}","documentType":"{{type}}","markings":{{Open}}}""", Encoding.UTF8, "application/json"), "metadata" },
        };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return (await Json(await c.PostAsync("/api/v1/documents", form))).GetProperty("number").GetString()!;
    }

    /// <summary>Records that the drawing was built against the model (what the first check-in of a drawing does), straight through the API, as the starting point.</summary>
    private static async Task LinkDrawingAsync(HttpClient c, string drawing, string model)
    {
        var set = await Json(await c.PostAsJsonAsync("/api/v1/cad/packages/checkout", new { top = drawing }));
        var id = set.GetProperty("id").GetString();
        await Json(await c.PostAsJsonAsync($"/api/v1/cad/checkouts/{id}/checkin", new { comment = "Linked", members = new[] { new { number = drawing, references = new[] { model } } } }));
    }

    private static async Task<JsonElement> DocumentAsync(HttpClient c, string number) => await Json(await c.GetAsync($"/api/v1/documents/{number}"));

    private static bool HasCheckout(JsonElement document) => document.TryGetProperty("checkout", out var c) && c.ValueKind == JsonValueKind.Object;

    private static async Task<int> StaleCountAsync(HttpClient c, string number) =>
        (await Json(await c.GetAsync($"/api/v1/cad/documents/{number}/package"))).GetProperty("staleCount").GetInt32();

    // ---------------------------------------------------------------- the add-on's Python as child processes

    private static string RepoFile(E2EFixture fx, string relative) => Path.Combine(fx.RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Where FreeCAD's command-line binary is, or null. KEEL_FREECAD_CMD overrides; no download is ever attempted.</summary>
    private static string? FreeCadCmd()
    {
        var fromEnv = Environment.GetEnvironmentVariable("KEEL_FREECAD_CMD");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return File.Exists(fromEnv) ? fromEnv : null;
        string[] candidates = ["/Applications/FreeCAD.app/Contents/Resources/bin/freecadcmd", "/Applications/FreeCAD.app/Contents/Resources/bin/FreeCADCmd"];
        foreach (var c in candidates) if (File.Exists(c)) return c;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in new[] { "freecadcmd", "FreeCADCmd" })
                if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    private sealed class Step(AddOnLiveTests owner, string work, string? freecad)
    {
        public string Work => work;

        /// <summary>Runs one step of live_server_check.py (under FreeCAD when <paramref name="freecad"/> is set for it) and returns its JSON result. A failing step fails the test with its trace.</summary>
        public async Task<JsonElement> RunAsync(string cmd, bool underFreeCad, string? folder = null, string? top = null, string? comment = null, bool declare = false, string? outDir = null, bool expectFailure = false, string? bracketNumber = null)
        {
            var script = RepoFile(owner.Fx, "tools/freecad-keel/scripts/live_server_check.py");
            var result = Path.Combine(work, $"result-{Guid.NewGuid():N}.json");
            var home = Path.Combine(work, "home");
            Directory.CreateDirectory(home);
            ProcessStartInfo psi;
            if (underFreeCad) { psi = new ProcessStartInfo(freecad ?? throw new InvalidOperationException("FreeCAD is required.")); psi.ArgumentList.Add(script); }
            else { psi = new ProcessStartInfo("python3"); psi.ArgumentList.Add("-I"); psi.ArgumentList.Add(script); }
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = work;
            psi.Environment["HOME"] = home;
            psi.Environment["FREECAD_USER_HOME"] = home;
            psi.Environment["KEEL_LIVE_CMD"] = cmd;
            psi.Environment["KEEL_LIVE_SERVER"] = owner.Fx.BaseUrl;
            psi.Environment["KEEL_CAD_TOKEN"] = await owner.TokenAsync();
            psi.Environment["KEEL_LIVE_RESULT"] = result;
            if (folder is not null) psi.Environment["KEEL_LIVE_FOLDER"] = folder;
            if (top is not null) psi.Environment["KEEL_LIVE_TOP"] = top;
            if (comment is not null) psi.Environment["KEEL_LIVE_COMMENT"] = comment;
            if (outDir is not null) psi.Environment["KEEL_LIVE_OUT"] = outDir;
            if (bracketNumber is not null) psi.Environment["KEEL_LIVE_BRACKET_NUMBER"] = bracketNumber;
            psi.Environment["KEEL_LIVE_DECLARE"] = declare ? "1" : "0";
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { p.Kill(entireProcessTree: true); throw new TimeoutException($"{cmd} did not finish in 3 minutes."); }
            var log = (await stdout) + (await stderr);
            Assert.True(File.Exists(result), $"{cmd} wrote no result (exit {p.ExitCode}): {log[^Math.Min(log.Length, 3000)..]}");
            var json = JsonDocument.Parse(await File.ReadAllTextAsync(result)).RootElement.Clone();
            if (!expectFailure && !json.GetProperty("ok").GetBoolean()) Assert.Fail($"{cmd} failed: {json.GetProperty("error").GetString()}\n{(json.TryGetProperty("trace", out var t) ? t.GetString() : "")}");
            return json;
        }

        public async Task<(int Exit, string Output)> CliAsync(params string[] args)
        {
            var dll = Path.Combine(owner.Fx.RepoRoot, "src", "Keel.CadAgent", "bin", AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug", "net10.0", "keel-cad.dll");
            Assert.True(File.Exists(dll), $"keel-cad is not built at {dll}");
            var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = work };
            psi.ArgumentList.Add(dll);
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--server");
            psi.ArgumentList.Add(owner.Fx.BaseUrl);
            psi.Environment["KEEL_CAD_TOKEN"] = await owner.TokenAsync();
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, (await stdout) + (await stderr));
        }
    }

    private Step NewStep(bool needFreeCad)
    {
        var freecad = FreeCadCmd();
        if (needFreeCad && freecad is null) Assert.Skip("FreeCAD is not installed (set KEEL_FREECAD_CMD or install it in /Applications); nothing was verified against a real FreeCAD.");
        var work = Directory.CreateTempSubdirectory("keel-addon-live-").FullName;
        return new Step(this, work, freecad);
    }

    // ---------------------------------------------------------------- structure proposals: review and apply through the API, read the EBOM back

    /// <summary>Decides every part as "create", applies, and returns the EBOM lines of every part below the top as (parent CAD number, child CAD number) to quantity.</summary>
    private static async Task<Dictionary<(string Parent, string Child), decimal>> ApplyAndReadBomAsync(HttpClient c, string proposalNumber, int expectedParts, int expectedLines)
    {
        var proposal = await Json(await c.GetAsync($"/api/v1/cad/bom-proposals/{proposalNumber}"));
        var parts = proposal.GetProperty("parts").EnumerateArray().ToList();
        Assert.Equal(expectedParts, parts.Count);
        Assert.Equal(expectedLines, proposal.GetProperty("lines").GetArrayLength());
        Assert.Equal("draft", proposal.GetProperty("status").GetString());
        var decided = await Json(await c.PutAsJsonAsync($"/api/v1/cad/bom-proposals/{proposalNumber}/decisions",
            new { decisions = parts.Select(p => new { @ref = p.GetProperty("ref").GetString(), decision = "create" }) }));
        Assert.True(decided.GetProperty("canApply").GetBoolean());
        var applied = await c.PostAsync($"/api/v1/cad/bom-proposals/{proposalNumber}/apply", null);
        await Json(applied);
        var after = await Json(await c.GetAsync($"/api/v1/cad/bom-proposals/{proposalNumber}"));
        Assert.Equal("applied", after.GetProperty("status").GetString());
        var created = after.GetProperty("parts").EnumerateArray().ToDictionary(p => p.GetProperty("cadNumber").GetString()!, p => p.GetProperty("createdNumber").GetString()!);
        var cad = created.ToDictionary(kv => kv.Value, kv => kv.Key);
        var result = new Dictionary<(string, string), decimal>();
        foreach (var (cadNumber, number) in created)
        {
            var bom = await Json(await c.GetAsync($"/api/v1/parts/{number}/bom?depth=1"));
            foreach (var line in bom.GetProperty("lines").EnumerateArray().Where(l => l.GetProperty("level").GetInt32() == 1))
                result[(cadNumber, cad[line.GetProperty("number").GetString()!])] = line.GetProperty("quantity").GetDecimal();
        }
        return result;
    }

    private static void AssertBom(Dictionary<(string Parent, string Child), decimal> actual, params (string Parent, string Child, decimal Quantity)[] expected)
    {
        var want = expected.ToDictionary(e => (e.Parent, e.Child), e => e.Quantity);
        Assert.Equal(want.OrderBy(k => k.Key).Select(k => $"{k.Key.Item1}>{k.Key.Item2}x{k.Value:0.####}"), actual.OrderBy(k => k.Key).Select(k => $"{k.Key.Item1}>{k.Key.Item2}x{k.Value:0.####}"));
    }

    private static (string Parent, string Child, decimal Quantity)[] FrameWith(string child, decimal quantity) =>
        [.. Frame.Select(l => l.Parent == "KA-3000" && l.Child == child ? (l.Parent, l.Child, quantity) : l)];

    // ================================================================ tests

    [E2EFact]
    public async Task The_python_client_and_keel_cad_run_a_package_against_the_real_host_and_a_real_step_assembly_becomes_the_ebom()
    {
        var step = NewStep(needFreeCad: false);
        using var api = await ApiAsync();
        var modelBytes = Encoding.UTF8.GetBytes("model v1 " + Guid.NewGuid());
        var model = await CreateDocument(api, "Live model", "CAD", modelBytes, "live-model.prt");
        var drawing = await CreateDocument(api, "Live drawing", "Drawing", Encoding.UTF8.GetBytes("drawing v1"), "live-drawing.drw");
        await LinkDrawingAsync(api, drawing, model);

        // ---- add-on client: check out the package of the model (it brings the drawing), files verified by hash
        var folder = Path.Combine(step.Work, "pkg");
        var co = await step.RunAsync("checkout", false, folder, top: model);
        var members = co.GetProperty("members").EnumerateArray().ToDictionary(m => m.GetProperty("number").GetString()!);
        Assert.Equal(2, members.Count);
        Assert.Equal("model", members[model].GetProperty("role").GetString());
        Assert.Equal("drawing", members[drawing].GetProperty("role").GetString());
        Assert.Equal(Sha(modelBytes), Sha(await File.ReadAllBytesAsync(Path.Combine(folder, "live-model.prt"))));
        Assert.True(File.Exists(Path.Combine(folder, "live-model.prt.markings.txt")));
        var doc = await DocumentAsync(api, model);
        Assert.True(doc.GetProperty("checkout").GetProperty("isMine").GetBoolean());

        // ---- change a file; status sees it; check in as a set
        await File.WriteAllBytesAsync(Path.Combine(folder, "live-model.prt"), Encoding.UTF8.GetBytes("model v2 " + Guid.NewGuid()));
        var status = await step.RunAsync("status", false, folder);
        var statusMembers = status.GetProperty("members").EnumerateArray().ToDictionary(m => m.GetProperty("number").GetString()!);
        Assert.Equal(["live-model.prt"], statusMembers[model].GetProperty("changed").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(statusMembers[drawing].GetProperty("changed").EnumerateArray());
        Assert.Equal("alice", statusMembers[model].GetProperty("lockedBy").GetString());
        Assert.True(statusMembers[model].GetProperty("mine").GetBoolean());
        Assert.Equal(0, status.GetProperty("staleCount").GetInt32());

        var checkin = await step.RunAsync("checkin", false, folder, comment: "Wall thickness");
        Assert.True(checkin.GetProperty("setClosed").GetBoolean());
        Assert.Equal(2, checkin.GetProperty("created").GetArrayLength());
        Assert.False(HasCheckout(await DocumentAsync(api, model)));

        // ---- the drawing was not regenerated: it is stale; the add-on's status says so
        Assert.Equal(1, await StaleCountAsync(api, drawing));
        var folder2 = Path.Combine(step.Work, "pkg2");
        await step.RunAsync("checkout", false, folder2, top: model);
        var stale = await step.RunAsync("status", false, folder2);
        Assert.Equal(1, stale.GetProperty("staleCount").GetInt32());
        Assert.Equal(drawing, stale.GetProperty("stale")[0].GetProperty("number").GetString());

        // ---- undo releases the locks and keeps the files
        var undone = await step.RunAsync("undo", false, folder2);
        Assert.True(undone.GetProperty("stateRemoved").GetBoolean());
        Assert.True(File.Exists(Path.Combine(folder2, "live-model.prt")));
        Assert.False(HasCheckout(await DocumentAsync(api, model)));
        Assert.DoesNotContain((await Json(await api.GetAsync("/api/v1/cad/checkouts"))).EnumerateArray(), s => s.GetProperty("top").GetString() == model);

        // ---- a refusal keeps its stable code: another author (rex) cannot take what alice holds
        await step.RunAsync("checkout", false, Path.Combine(step.Work, "pkg3"), top: model);
        var rex = new HttpClient { BaseAddress = new Uri(fx.BaseUrl) };
        rex.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync("rex"));
        var denied = await rex.PostAsJsonAsync("/api/v1/cad/packages/checkout", new { top = model });
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal("cad.locked", JsonDocument.Parse(await denied.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        var locked = await step.RunAsync("checkout", false, Path.Combine(step.Work, "pkg4"), top: model, expectFailure: true);
        Assert.False(locked.GetProperty("ok").GetBoolean());
        Assert.Equal("cad.locked", locked.GetProperty("code").GetString()); // alice herself: the package is already out in pkg3
        await step.RunAsync("undo", false, Path.Combine(step.Work, "pkg3"));

        // ---- keel-cad CLI against the same host: check out, change, check in declaring the drawing's reference (which asserts it was regenerated)
        var cliFolder = Path.Combine(step.Work, "cli");
        var (exit, output) = await step.CliAsync("checkout", model, "--dir", cliFolder);
        Assert.True(exit == 0, output);
        await File.WriteAllBytesAsync(Path.Combine(cliFolder, "live-model.prt"), Encoding.UTF8.GetBytes("model v3 " + Guid.NewGuid()));
        var refs = Path.Combine(step.Work, "refs.json");
        await File.WriteAllTextAsync(refs, JsonSerializer.Serialize(new Dictionary<string, string[]> { [drawing] = [model] }));
        (exit, output) = await step.CliAsync("checkin", "--dir", cliFolder, "--comment", "CLI change", "--refs-file", refs);
        Assert.True(exit == 0, output);
        Assert.Equal(0, await StaleCountAsync(api, drawing));
        (exit, output) = await step.CliAsync("checkout", model, "--dir", Path.Combine(step.Work, "cli2"));
        Assert.True(exit == 0, output);
        (exit, output) = await step.CliAsync("undo", "--dir", Path.Combine(step.Work, "cli2"));
        Assert.True(exit == 0, output);
        (exit, output) = await step.CliAsync("checkout", "D-999999", "--dir", Path.Combine(step.Work, "cli3"));
        Assert.Equal(2, exit);
        Assert.Contains("404", output, StringComparison.Ordinal);

        // ---- the real STEP fixtures through keel-cad to a reviewed, applied EBOM (AP214 and AP242)
        foreach (var file in new[] { "frame-asm-ap242.step", "frame-asm-ap214.step" })
        {
            (exit, output) = await step.CliAsync("bom", "push", RepoFile(fx, "tests/Keel.UnitTests/Fixtures/cad/" + file), "--title", "Frame " + file, "--markings-from", model);
            Assert.True(exit == 0, output);
            var number = System.Text.RegularExpressions.Regex.Match(output, @"CBP-\d+").Value;
            AssertBom(await ApplyAndReadBomAsync(api, number, expectedParts: 6, expectedLines: 6), Frame);
        }
    }

    [E2EFact]
    public async Task A_real_freecad_document_is_checked_out_changed_checked_in_and_pushed_as_a_BOM_through_the_add_on_against_the_real_host()
    {
        var step = NewStep(needFreeCad: true);
        using var api = await ApiAsync();

        // Real FreeCAD builds the files (headless): a frame assembly and a TechDraw drawing of its bracket in a file of its own.
        var built = Path.Combine(step.Work, "built");
        var build = await step.RunAsync("build", underFreeCad: true, outDir: built);
        var drawingInfo = build.GetProperty("drawings")[0];
        Assert.Equal("KP-1001", drawingInfo[1][0].GetString()); // TechDraw view -> external App::Link -> body: the part number is found
        Assert.Equal(["frame.FCStd"], build.GetProperty("dependencies").EnumerateArray().Select(e => e.GetString()));
        var modelV1 = await File.ReadAllBytesAsync(Path.Combine(built, "frame.FCStd"));
        var drawingV1 = await File.ReadAllBytesAsync(Path.Combine(built, "frame-drw.FCStd"));
        var model = await CreateDocument(api, "Frame model", "CAD", modelV1, "frame.FCStd");
        var drawing = await CreateDocument(api, "Frame drawing", "Drawing", drawingV1, "frame-drw.FCStd");
        await LinkDrawingAsync(api, drawing, model);

        var folder = Path.Combine(step.Work, "pkg");
        await step.RunAsync("checkout", false, folder, top: model);
        Assert.Equal(Sha(modelV1), Sha(await File.ReadAllBytesAsync(Path.Combine(folder, "frame.FCStd"))));
        Assert.Equal(Sha(drawingV1), Sha(await File.ReadAllBytesAsync(Path.Combine(folder, "frame-drw.FCStd"))));

        // The structure of the untouched document, extracted by the add-on inside FreeCAD, pushed over HTTP: the same EBOM as the STEP export of that structure.
        var push0 = await step.RunAsync("push", underFreeCad: true, folder: folder);
        Assert.Equal(6, push0.GetProperty("lines").GetInt32());
        AssertBom(await ApplyAndReadBomAsync(api, push0.GetProperty("proposal").GetString()!, expectedParts: 6, expectedLines: 6), Frame);

        // The user changes the model in FreeCAD (screws of the frame 2 -> 3) and saves; the add-on sees the file changed (and only that one).
        var edit = await step.RunAsync("edit", underFreeCad: true, folder: folder);
        Assert.Contains("FrameAsm", edit.GetProperty("roots").EnumerateArray().Select(e => e.GetString()));
        var status = await step.RunAsync("status", false, folder);
        var changed = status.GetProperty("members").EnumerateArray().ToDictionary(m => m.GetProperty("number").GetString()!, m => m.GetProperty("changed").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(["frame.FCStd"], changed[model]);
        Assert.Empty(changed[drawing]);
        var push1 = await step.RunAsync("push", underFreeCad: true, folder: folder);
        AssertBom(await ApplyAndReadBomAsync(api, push1.GetProperty("proposal").GetString()!, expectedParts: 6, expectedLines: 6), FrameWith("KF-2001", 3));

        // Check in as a set WITHOUT declaring references (the model changed, the drawing was not regenerated): stale.
        var checkin = await step.RunAsync("checkin", false, folder, comment: "Third screw");
        Assert.Equal(2, checkin.GetProperty("created").GetArrayLength());
        Assert.Equal(1, await StaleCountAsync(api, drawing));
        var folder2 = Path.Combine(step.Work, "pkg2");
        await step.RunAsync("checkout", false, folder2, top: model);
        Assert.Equal(drawing, (await step.RunAsync("status", false, folder2)).GetProperty("stale")[0].GetProperty("number").GetString());
        await step.RunAsync("undo", false, folder2);

        // Check in again from inside FreeCAD: the add-on derives the drawing's reference from the real external link and declares it: no longer stale.
        var folder3 = Path.Combine(step.Work, "pkg3");
        await step.RunAsync("checkout", false, folder3, top: model);
        var declared = await step.RunAsync("checkin", underFreeCad: true, folder: folder3, comment: "Drawing regenerated", declare: true);
        Assert.Equal(model, declared.GetProperty("references").GetProperty(drawing)[0].GetString());
        Assert.Equal(0, await StaleCountAsync(api, drawing));
        Assert.False(HasCheckout(await DocumentAsync(api, drawing)));
    }

    [E2EFact]
    public async Task The_freecad_gui_commands_run_the_package_workflow_in_a_real_freecad_with_its_gui_against_the_real_host()
    {
        var step = NewStep(needFreeCad: true);
        var gui = new[] { Environment.GetEnvironmentVariable("KEEL_FREECAD_GUI"), "/Applications/FreeCAD.app/Contents/Resources/bin/freecad" }.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
        if (gui is null) Assert.Skip("The FreeCAD GUI binary was not found (set KEEL_FREECAD_GUI); the GUI layer was not driven.");
        using var api = await ApiAsync();

        // A real Keel part for the bracket, so that the add-on's push matches it by number and Mark drawing-of has a part to record against.
        var bracketPart = (await Json(await api.PostAsJsonAsync("/api/v1/parts", new
        {
            name = "Frame bracket", partType = "Component", defaultUnit = "EA", source = "Make",
            markings = new { exportJurisdiction = "None", distributionStatement = "A", owningOrg = "ENG" },
        }))).GetProperty("number").GetString()!;

        var built = Path.Combine(step.Work, "built");
        await step.RunAsync("build", underFreeCad: true, outDir: built, bracketNumber: bracketPart);
        var modelV1 = await File.ReadAllBytesAsync(Path.Combine(built, "frame.FCStd"));
        var model = await CreateDocument(api, "Frame model (GUI)", "CAD", modelV1, "frame.FCStd");
        var drawing = await CreateDocument(api, "Frame drawing (GUI)", "Drawing", await File.ReadAllBytesAsync(Path.Combine(built, "frame-drw.FCStd")), "frame-drw.FCStd");
        await LinkDrawingAsync(api, drawing, model);
        // The files were authored elsewhere: that folder is gone by the time the user checks the package out (the drawing stores a path to the model it was made with).
        Directory.Delete(built, recursive: true);

        // The token comes from a token file, the add-on's own mechanism (mode 600, as for an SSH key).
        var tokenFile = Path.Combine(step.Work, "token");
        await File.WriteAllTextAsync(tokenFile, await TokenAsync());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = step.Work };
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add(RepoFile(fx, "tools/freecad-keel/scripts/run_gui_check.py"));
        psi.ArgumentList.Add("--freecad");
        psi.ArgumentList.Add(gui);
        psi.Environment["KEEL_GUI_LIVE"] = "1";
        psi.Environment["KEEL_GUI_SERVER"] = fx.BaseUrl;
        psi.Environment["KEEL_GUI_TOKEN_FILE"] = tokenFile;
        psi.Environment["KEEL_GUI_TOP"] = model;
        psi.Environment["KEEL_GUI_WORK"] = Path.Combine(step.Work, "guiwork");
        psi.Environment["KEEL_GUI_RESULT_COPY"] = Path.Combine(step.Work, "gui-result.json");
        using (var p = Process.Start(psi)!)
        {
            var output = (await p.StandardOutput.ReadToEndAsync()) + (await p.StandardError.ReadToEndAsync());
            await p.WaitForExitAsync();
            Assert.True(p.ExitCode == 0, $"FreeCAD GUI check exit {p.ExitCode}:\n{output}\n{(File.Exists(Path.Combine(step.Work, "gui-result.json")) ? File.ReadAllText(Path.Combine(step.Work, "gui-result.json")) : "")}");
        }

        // What the server holds after the GUI commands ran: the drawing's reference was derived from FreeCAD's real external link and declared (not stale),
        // everything was checked in, the proposal matched the bracket by its real Keel number, and the drawing is associated with the part.
        Assert.Equal(0, await StaleCountAsync(api, drawing));
        Assert.False(HasCheckout(await DocumentAsync(api, model)));
        Assert.False(HasCheckout(await DocumentAsync(api, drawing)));
        var proposals = await Json(await api.GetAsync("/api/v1/cad/bom-proposals"));
        var items = proposals.TryGetProperty("items", out var it) ? it : proposals;
        var proposal = items.EnumerateArray().First(p => p.GetProperty("title").GetString()!.Contains("from FreeCAD", StringComparison.Ordinal));
        var detail = await Json(await api.GetAsync($"/api/v1/cad/bom-proposals/{proposal.GetProperty("number").GetString()}"));
        var bracket = detail.GetProperty("parts").EnumerateArray().Single(p => p.GetProperty("cadNumber").GetString() == bracketPart);
        Assert.Equal("number", bracket.GetProperty("matchKind").GetString());
        var associated = await Json(await api.GetAsync($"/api/v1/cad/parts/{bracketPart}/documents"));
        Assert.Contains(associated.EnumerateArray(), a => a.GetProperty("documentNumber").GetString() == drawing && a.GetProperty("kind").GetString() == "drawing-of");
        var folderAfter = Path.Combine(step.Work, "after");
        var again = await step.RunAsync("checkout", false, folderAfter, top: model);
        var sha = again.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("number").GetString() == model).GetProperty("files")[0][1].GetString();
        Assert.NotEqual(Sha(modelV1), sha); // the GUI check-in saved the edited document and uploaded it
        await step.RunAsync("undo", false, folderAfter);
    }
}
