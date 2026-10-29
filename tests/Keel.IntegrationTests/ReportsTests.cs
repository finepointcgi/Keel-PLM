using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.Core.Reports;
using Keel.Parts;
using Keel.Reports;
using Keel.Reports.Tabular;
using Keel.Security;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UglyToad.PdfPig;

namespace Keel.IntegrationTests;

/// <summary>Output and bulk work (ADR-0044) against the real host, PostgreSQL and vault: release packages, BOM exchange, mass change, report builder.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ReportsTests(PostgresFixture pg)
{
    private static readonly Subject Alice = WorkflowEnv.Alice;
    private static readonly Subject Ada = WorkflowEnv.Ada;
    private static readonly Subject Lena = WorkflowEnv.Lena;
    private static readonly Subject Bob = WorkflowEnv.User("bob2", [Roles.Viewer, Roles.Author]);
    private static readonly Subject Boss = WorkflowEnv.User("boss", [Roles.Admin, Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]);
    private static readonly Subject Kim = WorkflowEnv.User("kim", [Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]);

    private sealed record Env(ApiFactory Api) : IDisposable
    {
        public void Dispose() => Api.Dispose();

        public PartService Parts => Api.Services.GetRequiredService<PartService>();
        public BomService Bom => Api.Services.GetRequiredService<BomService>();
        public ReleasePackageService Packages => Api.Services.GetRequiredService<ReleasePackageService>();
        public BomExchangeService Exchange => Api.Services.GetRequiredService<BomExchangeService>();
        public MassChangeService Mass => Api.Services.GetRequiredService<MassChangeService>();
        public ReportBuilderService Reports => Api.Services.GetRequiredService<ReportBuilderService>();

        public async Task<string> Part(string name, string type = "Component", Markings? m = null, Subject? by = null)
        {
            var r = await Parts.CreateAsync(by ?? Alice, new CreatePartCommand(name, type, "EA", "Make", m ?? WorkflowEnv.Open), null);
            Assert.True(r.IsOk, string.Join("; ", r.Errors));
            return r.Value!.Number;
        }

        public async Task Release(params string[] numbers)
        {
            foreach (var n in numbers) Assert.True((await Parts.SetStateAsync(Boss, n, "Released", "synthetic", true, null)).IsOk);
        }

        public async Task<string> Assembly(string name, Markings? m, params string[] children)
        {
            var asm = await Part(name, "Assembly", m, Boss);
            Assert.True((await Parts.CheckOutAsync(Boss, asm, null)).IsOk);
            var find = 10;
            foreach (var c in children) { Assert.True((await Bom.AddUsageAsync(Boss, asm, new AddUsageCommand(c, 2m, null, find, null), null)).IsOk); find += 10; }
            Assert.True((await Parts.CheckInAsync(Boss, asm, "structure", null)).IsOk);
            return asm;
        }

        public async Task<long> Scalar(string sql)
        {
            await using var conn = new NpgsqlConnection(Api.Database.SuperuserConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<List<AuditRecord>> Audit() => [.. await new AuditQueries(Api.Services.GetRequiredService<NpgsqlDataSource>()).ListAsync(new AuditQuery(Limit: 20000))];
    }

    private async Task<Env> NewEnv() => new(await ApiFactory.CreateAsync(pg));

    private static string Flat(string s) => Regex.Replace(s, @"\s+", "");

    // ---------------------------------------------------------------- release packages

    [Fact]
    public async Task A_release_package_is_stored_immutably_marked_on_every_page_audited_and_downloadable_only_by_those_who_read_everything()
    {
        using var f = await NewEnv();
        var e = f;
        var bolt = await e.Part("Bolt", m: WorkflowEnv.Itar());
        var nut = await e.Part("Nut");
        var asm = await e.Assembly("Pod", WorkflowEnv.Itar(), bolt, nut);
        await e.Release(bolt, nut, asm);

        var made = await e.Packages.GenerateAsync(Alice, "part", asm, "corr-pkg");
        Assert.True(made.IsOk, string.Join("; ", made.Errors));
        var a = made.Value!;
        Assert.Contains("ITAR", a.Banner, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", a.Sha256);

        // Immutable row; the vault object matches the recorded SHA-256.
        var download = await e.Packages.DownloadAsync(Alice, a.Number, "corr-dl");
        Assert.True(download.IsOk);
        await using var ms = new MemoryStream();
        await download.Value!.Content.CopyToAsync(ms);
        Assert.Equal(a.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ms.ToArray())));
        await Assert.ThrowsAnyAsync<PostgresException>(() => e.Api.Database.ExecuteAsSuperuserAsync($"UPDATE rpt.artifact SET title = 'x' WHERE number = '{a.Number}'"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => e.Api.Database.ExecuteAsSuperuserAsync($"DELETE FROM rpt.artifact WHERE number = '{a.Number}'"));

        // Every page carries the aggregate banner twice and the distribution statement.
        using (var doc = PdfDocument.Open(ms.ToArray()))
        {
            Assert.True(doc.NumberOfPages >= 5);
            foreach (var page in doc.GetPages())
            {
                Assert.True(Regex.Count(page.Text, "ITAR") >= 2, $"page {page.Number} banner");
                Assert.Contains("DISTRIBUTION STATEMENT D", Regex.Replace(page.Text, @"\s+", " "), StringComparison.Ordinal);
            }
            var all = Flat(string.Join(" ", doc.GetPages().Select(p => p.Text)));
            Assert.Contains(bolt, all, StringComparison.Ordinal);
            Assert.Contains(nut, all, StringComparison.Ordinal);
        }

        // Someone who cannot read the ITAR parts neither lists nor downloads it, and the denial is audited.
        Assert.Empty(await e.Packages.ListAsync(Lena, "part", asm));
        var denied = await e.Packages.DownloadAsync(Lena, a.Number, "corr-denied");
        Assert.Equal(OutcomeKind.NotFound, denied.Kind);
        Assert.Single(await e.Packages.ListAsync(Alice, "part", asm));

        var audit = await e.Audit();
        Assert.Contains(audit, r => r.Action == "output.package.generate" && r.Outcome == AuditOutcome.Success && r.ObjectId == a.Number);
        Assert.Contains(audit, r => r.Action == "output.package.download" && r.Outcome == AuditOutcome.Denied && r.CorrelationId == "corr-denied");
        Assert.Contains(audit, r => r.Action == "output.package.download" && r.Outcome == AuditOutcome.Success);

        // Over HTTP: the marking header and the PDF for an eligible user, 404 for a non-U.S. person.
        var alice = e.Api.ClientFor(ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"]));
        var ok = await alice.GetAsync($"/api/v1/output/packages/{a.Number}/download");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("application/pdf", ok.Content.Headers.ContentType!.MediaType);
        Assert.Contains("ITAR", Uri.UnescapeDataString(ok.Headers.GetValues("X-Keel-Markings").Single()), StringComparison.Ordinal);
        var lena = e.Api.ClientFor(ApiFactory.Token(sub: "u-lena", usPerson: false, roles: ["plm-viewer"]));
        Assert.Equal(HttpStatusCode.NotFound, (await lena.GetAsync($"/api/v1/output/packages/{a.Number}/download")).StatusCode);
    }

    [Fact]
    public async Task A_package_is_refused_not_redacted_when_a_part_it_needs_is_unreadable_and_nothing_is_stored()
    {
        using var e = await NewEnv();
        var secret = await e.Part("Secret", m: WorkflowEnv.Itar());
        var plain = await e.Part("Plain");
        var asm = await e.Assembly("Open assembly", WorkflowEnv.Open, plain, secret);
        await e.Release(secret, plain, asm);

        var refused = await e.Packages.GenerateAsync(Lena, "part", asm, "corr-refuse");
        Assert.Equal(OutcomeKind.Forbidden, refused.Kind);
        Assert.StartsWith("package.unreadable_objects:1", refused.Errors.Single(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, string.Join(" ", refused.Errors), StringComparison.Ordinal); // a count, never a name
        Assert.Equal(0, await e.Scalar("SELECT count(*) FROM rpt.artifact"));
        Assert.Contains(await e.Audit(), r => r.Action == "output.package.generate" && r.Outcome == AuditOutcome.Denied);
        Assert.True((await e.Packages.GenerateAsync(Alice, "part", asm, null)).IsOk);

        // Not released: refused with the reason; unknown number: not found.
        var draft = await e.Part("Draft");
        Assert.Equal(OutcomeKind.Conflict, (await e.Packages.GenerateAsync(Alice, "part", draft, null)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await e.Packages.GenerateAsync(Alice, "part", "P-999999", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await e.Packages.GenerateAsync(Alice, "widget", "x", null)).Kind);
    }

    // ---------------------------------------------------------------- BOM exchange

    [Fact]
    public async Task The_bom_export_is_read_filtered_marked_and_safe_against_formula_injection()
    {
        using var e = await NewEnv();
        var secret = await e.Part("Secret bracket", m: WorkflowEnv.Itar());
        var evil = await e.Part("=HYPERLINK(\"http://evil\")");
        var asm = await e.Assembly("Pod", WorkflowEnv.Open, evil, secret);
        await e.Release(secret, evil, asm);

        var full = await e.Exchange.ExportAsync(Alice, new BomExchangeRequest(BomExchangeKinds.Ebom, asm), "xlsx", "corr-x");
        Assert.True(full.IsOk, string.Join("; ", full.Errors));
        var rows = XlsxReader.ReadFirstSheet(full.Value!.Content);
        Assert.Contains("ITAR", rows[0][0], StringComparison.Ordinal);           // the banner is the first row
        var header = rows.First(r => r.Count > 0 && r[0] == "Level");
        Assert.Equal("Markings", header[^1]);
        var data = rows.SkipWhile(r => r.Count == 0 || r[0] != "Level").Skip(1).ToList();
        Assert.Equal(2, data.Count);
        Assert.Contains(data, r => r[2] == secret && r[^1].Contains("ITAR", StringComparison.Ordinal));
        Assert.Contains(data, r => r[3] == "=HYPERLINK(\"http://evil\")"); // restored as text on read ...
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(full.Value.Content)))
        using (var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()))
        {
            var xml = sheet.ReadToEnd();
            Assert.Contains("'=HYPERLINK", xml, StringComparison.Ordinal);   // ... and stored neutralized
            Assert.DoesNotContain("<f>", xml, StringComparison.Ordinal);
        }
        var csv = await e.Exchange.ExportAsync(Alice, new BomExchangeRequest(BomExchangeKinds.Ebom, asm), "csv", null);
        Assert.Contains("'=HYPERLINK", System.Text.Encoding.UTF8.GetString(csv.Value!.Content), StringComparison.Ordinal);

        // A reader without the clearance gets a placeholder line (find number, quantity, unit) and the file never names the part.
        var lena = await e.Exchange.ExportAsync(Lena, new BomExchangeRequest(BomExchangeKinds.Ebom, asm), "xlsx", null);
        Assert.True(lena.IsOk);
        Assert.Equal(1, lena.Value!.RedactedCount);
        var lenaText = string.Join("|", XlsxReader.ReadFirstSheet(lena.Value.Content).SelectMany(r => r));
        Assert.DoesNotContain(secret, lenaText, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret bracket", lenaText, StringComparison.Ordinal);
        Assert.Contains("(restricted)", lenaText, StringComparison.Ordinal);
        Assert.DoesNotContain("ITAR", lena.Value.Banner, StringComparison.Ordinal);

        Assert.Equal(OutcomeKind.NotFound, (await e.Exchange.ExportAsync(Alice, new BomExchangeRequest(BomExchangeKinds.Ebom, "P-999999"), "xlsx", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await e.Exchange.ExportAsync(Alice, new BomExchangeRequest(BomExchangeKinds.Ebom, asm), "pdf", null)).Kind);
        Assert.Contains(await e.Audit(), r => r.Action == "output.bom.export" && r.Outcome == AuditOutcome.Success);
    }

    private static byte[] Workbook(params string[][] rows) => XlsxWriter.Write([new SheetData("BOM changes", BomExchangeService.TemplateHeader, [.. rows.Select(r => (IReadOnlyList<Cell>)[.. r.Select((c, i) => i is 3 or 5 && decimal.TryParse(c, System.Globalization.CultureInfo.InvariantCulture, out var n) ? Cell.N(n) : Cell.T(c))])])]);

    [Fact]
    public async Task A_bom_import_is_a_dry_run_with_row_errors_then_applies_through_the_part_and_bom_services_once()
    {
        using var e = await NewEnv();
        var child = await e.Part("Washer");
        var other = await e.Part("Spacer");
        var asm = await e.Assembly("Frame", WorkflowEnv.Open, child);
        var locked = await e.Assembly("Locked frame", WorkflowEnv.Open, child);
        await e.Release(locked);
        var before = (await e.Parts.GetAsync(Alice, asm, null, null)).Value!.Revision.LatestIteration;

        // Dry run: errors are per row and nothing changes.
        var bad = await e.Exchange.CreateProposalAsync(Alice, "changes.xlsx", Workbook(
            ["add", asm, other, "2", "EA", "20", "R1, R2", "new line", ""],
            ["add", asm, "P-999999", "1", "", "", "", "", ""],
            ["update", asm, child, "5", "", "", "", "", ""],
            ["remove", locked, child, "", "", "", "", "", ""],
            ["add", asm, other, "0", "", "", "", "", ""]), "corr-dry");
        Assert.True(bad.IsOk);
        var p = bad.Value!;
        Assert.False(p.CanApply);
        Assert.Equal(3, p.Summary.ErrorRows);
        var mismatch = (await e.Exchange.CreateProposalAsync(Alice, "m.xlsx", Workbook(["add", asm, other, "3", "EA", "30", "R1, R2", "", ""]), null)).Value!;
        Assert.Contains("the counts must match", mismatch.Rows[0].Errors.Single(), StringComparison.Ordinal);
        Assert.Contains("was not found", p.Rows[1].Errors.Single(), StringComparison.Ordinal);
        Assert.Contains("locked", p.Rows[3].Errors.Single(), StringComparison.Ordinal);
        Assert.Contains(p.Rows[4].Errors, x => x.Contains("Quantity", StringComparison.Ordinal));
        Assert.Empty(p.Rows[0].Errors);
        Assert.Equal(OutcomeKind.Invalid, (await e.Exchange.ApplyAsync(Alice, p.Number, null)).Kind);
        Assert.Equal(before, (await e.Parts.GetAsync(Alice, asm, null, null)).Value!.Revision.LatestIteration);
        // A proposal belongs to its creator.
        Assert.Equal(OutcomeKind.NotFound, (await e.Exchange.GetProposalAsync(Bob, p.Number)).Kind);

        // A clean workbook applies once, with a new iteration; the second apply is refused.
        var good = (await e.Exchange.CreateProposalAsync(Alice, "good.xlsx", Workbook(
            ["add", asm, other, "2", "EA", "20", "R1, R2", "new line", ""], ["update", asm, child, "5", "", "", "", "", ""]), "corr-good")).Value!;
        Assert.True(good.CanApply, string.Join("; ", good.Rows.SelectMany(r => r.Errors)));
        var applied = await e.Exchange.ApplyAsync(Alice, good.Number, "corr-apply");
        Assert.True(applied.IsOk, string.Join("; ", applied.Errors) + " " + applied.Value?.Result);
        Assert.Equal("applied", applied.Value!.Status);
        var after = (await e.Parts.GetAsync(Alice, asm, null, null)).Value!.Revision.LatestIteration;
        Assert.Equal(before + 1, after);
        var lines = (await e.Bom.ExplodeAsync(Alice, new BomQuery(asm, Depth: 1), null)).Value!.Lines;
        Assert.Equal(2, lines.Count);
        Assert.Equal(5m, lines.Single(l => l.Number == child).Quantity);
        Assert.Equal(["R1", "R2"], lines.Single(l => l.Number == other).ReferenceDesignators);
        Assert.Equal(OutcomeKind.Conflict, (await e.Exchange.ApplyAsync(Alice, good.Number, null)).Kind);
        Assert.Contains(await e.Audit(), r => r.Action == "output.bom.import_apply" && r.CorrelationId == "corr-apply");
        Assert.Contains(await e.Audit(), r => r.Action == "part.checkin" && r.CorrelationId == "corr-apply"); // the normal services wrote their own events

        // Not a workbook, a bomb and a macro file are refused with a message, not an error.
        Assert.Equal(OutcomeKind.Invalid, (await e.Exchange.CreateProposalAsync(Alice, "x.xlsx", System.Text.Encoding.UTF8.GetBytes("not a zip"), null)).Kind);
    }

    // ---------------------------------------------------------------- mass change

    [Fact]
    public async Task Mass_change_previews_what_will_change_and_why_not_applies_per_object_with_one_correlation_reports_partial_failure_retries_and_undoes()
    {
        using var e = await NewEnv();
        var p1 = await e.Part("One");
        var p2 = await e.Part("Two");
        var p3 = await e.Part("Three, released");
        var itar = await e.Part("Itar part", m: WorkflowEnv.Itar());
        await e.Release(p3);

        var preview = await e.Mass.PreviewAsync(Lena, new MassRequest("part", [p1, p3, itar, "P-999999"], MassOperations.SetAttribute, new JsonObject { ["name"] = "material", ["value"] = "7075-T6" }), "c");
        Assert.True(preview.IsOk, string.Join("; ", preview.Errors));
        var byKey = preview.Value!.Items.ToDictionary(i => i.Key);
        Assert.True(byKey[p1].CanApply == false); // Lena has no Author role: she may read but not change
        Assert.Equal(byKey["P-999999"].Reason, byKey[itar].Reason); // unreadable and missing say the same words
        Assert.DoesNotContain("Itar", byKey[itar].Reason ?? "", StringComparison.Ordinal);

        var pv = await e.Mass.PreviewAsync(Alice, new MassRequest("part", [p1, p2, p3, "P-999999"], MassOperations.SetAttribute, new JsonObject { ["name"] = "material", ["value"] = "7075-T6" }), "c");
        var items = pv.Value!.Items.ToDictionary(i => i.Key);
        Assert.True(items[p1].CanApply && items[p2].CanApply);
        Assert.False(items[p3].CanApply);
        Assert.Contains("locked", items[p3].Reason!, StringComparison.Ordinal);
        Assert.False(items["P-999999"].CanApply);
        Assert.Equal(2, pv.Value.WillChange);
        Assert.Equal(2, pv.Value.Refused);
        var iterationBefore = (await e.Parts.GetAsync(Alice, p1, null, null)).Value!.Revision.LatestIteration;
        Assert.Equal(iterationBefore, (await e.Parts.GetAsync(Alice, p1, null, null)).Value!.Revision.LatestIteration); // the preview changed nothing

        // Bob holds p2's check-out: p2 fails, p1 succeeds; the failure is reported and nothing else is rolled back.
        Assert.True((await e.Parts.CheckOutAsync(Bob, p2, null)).IsOk);
        var partial = await e.Mass.ApplyAsync(Alice, pv.Value.Number, "request-corr");
        Assert.Equal(OutcomeKind.Conflict, partial.Kind);
        Assert.Equal(1, partial.Value!.Applied);
        Assert.Equal(1, partial.Value.Failed);
        Assert.Equal("failed", partial.Value.Items.Single(i => i.Key == p2).Result);
        Assert.Equal(iterationBefore + 1, (await e.Parts.GetAsync(Alice, p1, null, null)).Value!.Revision.LatestIteration);
        Assert.Equal("7075-T6", ((await e.Parts.GetAsync(Alice, p1, null, null)).Value!.Iteration.Attributes["material"])!.GetValue<string>());

        // Undo is refused while only partly applied; the retry touches only the failed object (p1 is not changed twice).
        Assert.Equal(OutcomeKind.Conflict, (await e.Mass.UndoAsync(Alice, pv.Value.Number, null)).Kind);
        Assert.True((await e.Parts.UndoCheckOutAsync(Bob, p2, null)).IsOk);
        var retry = await e.Mass.ApplyAsync(Alice, pv.Value.Number, null);
        Assert.True(retry.IsOk, string.Join("; ", retry.Errors));
        Assert.Equal("applied", retry.Value!.Status);
        Assert.Equal(iterationBefore + 1, (await e.Parts.GetAsync(Alice, p1, null, null)).Value!.Revision.LatestIteration);
        Assert.Equal(OutcomeKind.Conflict, (await e.Mass.ApplyAsync(Alice, pv.Value.Number, null)).Kind); // finished: nothing to repeat

        // Each part was audited by its own service, all under the one shared correlation id.
        var audit = await e.Audit();
        var corr = pv.Value.CorrelationId;
        Assert.Contains(audit, r => r.Action == "part.checkin" && r.ObjectId == p1 && r.CorrelationId == corr);
        Assert.Contains(audit, r => r.Action == "part.checkin" && r.ObjectId == p2 && r.CorrelationId == corr);
        Assert.Contains(audit, r => r.Action == "output.mass.apply" && r.CorrelationId == corr);

        // Undo restores the old value (in a new iteration: history is never rewritten).
        var undone = await e.Mass.UndoAsync(Alice, pv.Value.Number, null);
        Assert.True(undone.IsOk, string.Join("; ", undone.Errors));
        Assert.Equal("undone", undone.Value!.Status);
        var restored = (await e.Parts.GetAsync(Alice, p1, null, null)).Value!;
        Assert.False(restored.Iteration.Attributes.ContainsKey("material") && restored.Iteration.Attributes["material"] is not null && restored.Iteration.Attributes["material"]!.GetValue<string>() == "7075-T6");
        // Another user cannot see or act on the operation.
        Assert.Equal(OutcomeKind.NotFound, (await e.Mass.GetAsync(Bob, pv.Value.Number)).Kind);
        Assert.Equal(OutcomeKind.NotFound, (await e.Mass.ApplyAsync(Bob, pv.Value.Number, null)).Kind);
    }

    [Fact]
    public async Task Mass_change_markings_watch_favorite_and_baseline_proposal_follow_the_authority_of_the_user_and_undo()
    {
        using var e = await NewEnv();
        var a = await e.Part("A");
        var b = await e.Part("B");
        var orcaOnlyNode = JsonNode.Parse("""{"jurisdiction":"None","distributionStatement":"B","owningOrg":"ENG","controllingOffice":"ACME","distributionReason":"Test","program":"KRAKEN"}""")!.AsObject();
        JsonObject OrcaOnly() => (JsonObject)orcaOnlyNode.DeepClone();

        // Alice is not on program KRAKEN, so she may not set it: refused for every object, with the reason.
        var refused = await e.Mass.PreviewAsync(Alice, new MassRequest("part", [a, b], MassOperations.ChangeMarkings, new JsonObject { ["reason"] = "reclassify", ["markings"] = OrcaOnly() }), null);
        Assert.Equal(0, refused.Value!.WillChange);
        Assert.All(refused.Value.Items, i => Assert.Contains("may not set these markings", i.Reason!, StringComparison.Ordinal));
        // Kim is on KRAKEN: allowed; the change is audited per part and can be undone.
        var ok = (await e.Mass.PreviewAsync(Kim, new MassRequest("part", [a, b], MassOperations.ChangeMarkings, new JsonObject { ["reason"] = "reclassify", ["markings"] = OrcaOnly() }), null)).Value!;
        Assert.Equal(2, ok.WillChange);
        Assert.True((await e.Mass.ApplyAsync(Kim, ok.Number, null)).IsOk);
        Assert.Contains("KRAKEN", (await e.Parts.GetAsync(Kim, a, null, null, default)).Value!.Banner, StringComparison.Ordinal);
        Assert.True((await e.Mass.UndoAsync(Kim, ok.Number, null)).IsOk);
        Assert.DoesNotContain("KRAKEN", (await e.Parts.GetAsync(Kim, a, null, null)).Value!.Banner, StringComparison.Ordinal);

        // Watch and favorite are reversible; the baseline proposal is a saved list of readable parts.
        var w = (await e.Mass.PreviewAsync(Alice, new MassRequest("part", [a, b], MassOperations.Watch, new JsonObject { ["level"] = "decisions" }), null)).Value!;
        Assert.True((await e.Mass.ApplyAsync(Alice, w.Number, null)).IsOk);
        Assert.Equal(2, await e.Scalar($"SELECT count(*) FROM ntf.watch WHERE user_sub = '{Alice.Id}' AND level = 'decisions'"));
        Assert.True((await e.Mass.UndoAsync(Alice, w.Number, null)).IsOk);
        // Creating a part already watches it at the default level; undo restores that previous level.
        Assert.Equal(2, await e.Scalar($"SELECT count(*) FROM ntf.watch WHERE user_sub = '{Alice.Id}' AND level = 'all'"));
        var fav = (await e.Mass.PreviewAsync(Alice, new MassRequest("part", [a], MassOperations.Favorite, []), null)).Value!;
        Assert.True((await e.Mass.ApplyAsync(Alice, fav.Number, null)).IsOk);
        Assert.Equal(1, await e.Scalar($"SELECT count(*) FROM ws.favorite WHERE user_sub = '{Alice.Id}'"));
        var prop = (await e.Mass.PreviewAsync(Alice, new MassRequest("part", [a, b, "P-999999"], MassOperations.BaselineProposal, new JsonObject { ["name"] = "Gate 1" }), null)).Value!;
        Assert.Equal(2, prop.WillChange);
        Assert.True((await e.Mass.ApplyAsync(Alice, prop.Number, null)).IsOk);
        Assert.Equal([a, b], await e.Mass.ProposalPartsAsync(Alice, "Gate 1"));
        Assert.True((await e.Mass.UndoAsync(Alice, prop.Number, null)).IsOk);
        Assert.Empty(await e.Mass.ProposalPartsAsync(Alice, "Gate 1"));
    }

    [Fact]
    public async Task Mass_change_enforces_its_caps_and_validates_parameters()
    {
        using var e = await NewEnv();
        var a = await e.Part("A");
        var many = Enumerable.Range(1, ReportsLimits.MaxMassObjects + 1).Select(i => $"P-{i:D6}").ToArray();
        var big = await e.Mass.PreviewAsync(Alice, new MassRequest("part", many, MassOperations.Favorite, []), null);
        Assert.Equal(OutcomeKind.Invalid, big.Kind);
        Assert.Contains("500", big.Errors[0], StringComparison.Ordinal);
        Assert.Equal(OutcomeKind.Invalid, (await e.Mass.PreviewAsync(Alice, new MassRequest("part", [a], "delete_everything", []), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await e.Mass.PreviewAsync(Alice, new MassRequest("part", [a], MassOperations.SetAttribute, []), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await e.Mass.PreviewAsync(Alice, new MassRequest("widget", [a], MassOperations.Favorite, []), null)).Kind);
        var unknown = (await e.Mass.PreviewAsync(Alice, new MassRequest("part", [a], MassOperations.SetAttribute, new JsonObject { ["name"] = "nope", ["value"] = "x" }), null)).Value!;
        Assert.Equal(0, unknown.WillChange);
        // The rate limit: the 21st operation within the hour is refused.
        for (var i = 1; i < ReportsLimits.MaxMassPerHour; i++) Assert.True((await e.Mass.PreviewAsync(Alice, new MassRequest("part", [a], MassOperations.Favorite, []), null)).IsOk, $"preview {i}");
        Assert.Equal(OutcomeKind.Conflict, (await e.Mass.PreviewAsync(Alice, new MassRequest("part", [a], MassOperations.Favorite, []), null)).Kind);
    }

    // ---------------------------------------------------------------- report builder

    private static readonly Markings[] Variants =
    [
        WorkflowEnv.Open, WorkflowEnv.Itar(), WorkflowEnv.Itar("KRAKEN"),
        Markings.Create(ExportJurisdiction.Ear, DistributionStatement.C, "ENG", eccn: "9A610", cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "r").Value,
        Markings.Create(ExportJurisdiction.None, DistributionStatement.B, "ENG", controllingOffice: "ACME", distributionReason: "r", program: "ORCA").Value,
        Markings.Create(ExportJurisdiction.None, DistributionStatement.B, "ENG", cuiCategories: ["CTI"], controllingOffice: "ACME", distributionReason: "r").Value,
    ];

    [Fact]
    public async Task Every_dataset_returns_only_rows_the_caller_may_read_and_matches_the_policy_oracle_for_parts_and_structure()
    {
        using var e = await NewEnv();
        var parts = new Dictionary<string, Markings>();
        foreach (var m in Variants) parts[await e.Part($"Part {parts.Count}", m: m, by: Boss)] = m;
        var asm = await e.Part("Root", "Assembly", WorkflowEnv.Open, Boss);
        Assert.True((await e.Parts.CheckOutAsync(Boss, asm, null)).IsOk);
        var find = 10;
        foreach (var n in parts.Keys.Where(k => parts[k] == WorkflowEnv.Open || true)) { Assert.True((await e.Bom.AddUsageAsync(Boss, asm, new AddUsageCommand(n, 1m, null, find, null), null)).IsOk); find += 10; }
        Assert.True((await e.Parts.CheckInAsync(Boss, asm, "s", null)).IsOk);
        await e.Release(parts.Keys.Append(asm).ToArray());
        // One change object per marking variant for the other datasets.
        var changes = new Dictionary<string, Markings>();
        var changeSvc = e.Api.Services.GetRequiredService<Keel.Change.ChangeService>();
        foreach (var m in Variants)
        {
            var c = await changeSvc.CreateAsync(Boss, new Keel.Change.CreateChangeCommand("ECN", $"Change {changes.Count}", "d", "Medium", "r", m, "II"), null);
            if (c.IsOk) changes[c.Value!.Number] = m;
        }
        Assert.NotEmpty(changes);

        Subject[] subjects = [Alice, Ada, Lena, WorkflowEnv.Mallory, WorkflowEnv.Audrey, Kim, WorkflowEnv.Farid];
        var datasets = e.Reports.Catalog();
        Assert.Equal(["baselines", "bom", "changes", "compliance", "parts", "quality", "requirements", "schedule"], datasets.Select(d => d.Id).Order(StringComparer.Ordinal));
        foreach (var subject in subjects)
        {
            bool Readable(Markings m) => AccessPolicy.Evaluate(subject, m, PlmAction.Read).Permitted;
            foreach (var ds in datasets)
            {
                var spec = new ReportSpec([.. ds.Columns.Select(c => c.Key)], Parameter: ds.ParameterLabel is null ? null : asm);
                var run = await e.Reports.RunAsync(subject, ds.Id, spec, null);
                if (!run.IsOk) { Assert.True(run.Kind is OutcomeKind.Forbidden or OutcomeKind.NotFound, $"{ds.Id} for {subject.Username}: {run.Kind}"); continue; }
                // No row is marked above what the caller may read; the banner covers exactly the rows shown.
                foreach (var row in run.Value!.Rows) if (row.Banner is not null) Assert.Contains(row.Banner.Split(" · ")[0].StartsWith("CUI", StringComparison.Ordinal) ? "CUI" : "UNCLASSIFIED", row.Banner, StringComparison.Ordinal);
                if (ds.Id == "parts")
                {
                    var shown = run.Value.Rows.Select(r => r.Cells[0]!).Where(parts.ContainsKey).Order(StringComparer.Ordinal).ToList();
                    var expected = parts.Where(kv => Readable(kv.Value)).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
                    Assert.True(expected.SequenceEqual(shown), $"parts of {subject.Username}: expected {string.Join(",", expected)} got {string.Join(",", shown)}");
                    Assert.Equal(AccessPolicy.HasRoleFor(subject, PlmAction.Read) ? Markings.AggregateBanner(parts.Where(kv => Readable(kv.Value)).Select(kv => kv.Value).Append(WorkflowEnv.Open)).Split(" · ")[^1] : null, AccessPolicy.HasRoleFor(subject, PlmAction.Read) ? run.Value.Banner.Split(" · ")[^1] : null);
                }
                if (ds.Id == "bom")
                {
                    var lines = run.Value.Rows.Where(r => r.Cells[2] != "(restricted)").Select(r => r.Cells[2]!).Order(StringComparer.Ordinal).ToList();
                    var expected = parts.Where(kv => Readable(kv.Value)).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
                    Assert.True(expected.SequenceEqual(lines), $"bom lines of {subject.Username}");
                    Assert.Equal(parts.Count, run.Value.Rows.Count); // hidden lines stay as placeholders, one for one
                    Assert.Equal(parts.Count(kv => !Readable(kv.Value)), run.Value.Rows.Count(r => r.Cells[2] == "(restricted)"));
                    Assert.All(run.Value.Rows.Where(r => r.Cells[2] == "(restricted)"), r => Assert.Null(r.Cells[3]));
                }
                if (ds.Id == "changes")
                {
                    var shown = run.Value.Rows.Select(r => r.Cells[0]!).Where(changes.ContainsKey).Order(StringComparer.Ordinal).ToList();
                    var expected = changes.Where(kv => Readable(kv.Value)).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
                    Assert.True(expected.SequenceEqual(shown), $"changes of {subject.Username}");
                }
            }
        }
    }

    [Fact]
    public async Task Saved_reports_are_shareable_run_as_the_runner_and_export_with_the_banner_in_every_format()
    {
        using var e = await NewEnv();
        var open = await e.Part("Open bracket");
        var itar = await e.Part("Itar bracket", m: WorkflowEnv.Itar());
        var spec = new ReportSpec(["number", "name", "state"], [new DatasetFilter("name", FilterOps.Contains, "bracket")], SortColumn: "number", Descending: true, GroupBy: "state");
        var saved = await e.Reports.SaveAsync(Alice, null, "Brackets", "parts", spec, shared: true, null);
        Assert.True(saved.IsOk, string.Join("; ", saved.Errors));
        Assert.Equal(OutcomeKind.Conflict, (await e.Reports.SaveAsync(Alice, null, "brackets", "parts", spec, false, null)).Kind); // names are unique per owner
        Assert.Equal(OutcomeKind.Invalid, (await e.Reports.SaveAsync(Alice, null, "Bad", "parts", new ReportSpec(["number"], SortColumn: "x; drop"), false, null)).Kind);
        var shared = (await e.Reports.ListAsync(Kim)).Single(d => d.Name == "Brackets");
        Assert.False(shared.Mine);
        // Kim is not the owner: she cannot delete it; the owner can.
        Assert.Equal(OutcomeKind.NotFound, (await e.Reports.DeleteAsync(Kim, shared.Id, null)).Kind);

        // A viewer who cannot read the ITAR part runs the same definition and sees only what she may.
        var asLena = (await e.Reports.RunAsync(Lena, "parts", shared.Spec, null)).Value!;
        Assert.Equal([open], asLena.Rows.Select(r => r.Cells[0]));
        Assert.DoesNotContain("ITAR", asLena.Banner, StringComparison.Ordinal);
        var asAlice = (await e.Reports.RunAsync(Alice, "parts", shared.Spec, null)).Value!;
        Assert.Equal(new[] { itar, open }.Order(StringComparer.Ordinal).Reverse().ToList(), asAlice.Rows.Select(r => r.Cells[0]!).ToList());
        Assert.Contains("ITAR", asAlice.Banner, StringComparison.Ordinal);
        Assert.Equal(2, asAlice.Groups.Sum(g => g.Count));

        foreach (var format in new[] { "csv", "xlsx", "pdf" })
        {
            var file = await e.Reports.ExportAsync(Alice, "parts", spec, format, null);
            Assert.True(file.IsOk, format);
            Assert.Contains("ITAR", file.Value!.Banner, StringComparison.Ordinal);
            var text = format switch
            {
                "csv" => System.Text.Encoding.UTF8.GetString(file.Value.Content),
                "xlsx" => string.Join("|", XlsxReader.ReadFirstSheet(file.Value.Content).SelectMany(r => r)),
                _ => string.Join(" ", PdfDocument.Open(file.Value.Content).GetPages().Select(p => p.Text)),
            };
            Assert.Contains("ITAR", text, StringComparison.Ordinal);
            Assert.Contains(itar, Flat(text), StringComparison.Ordinal);
        }
        var lenaCsv = System.Text.Encoding.UTF8.GetString((await e.Reports.ExportAsync(Lena, "parts", spec, "csv", null)).Value!.Content);
        Assert.DoesNotContain(itar, lenaCsv, StringComparison.Ordinal);
        Assert.Contains(await e.Audit(), r => r.Action == "output.report.export" && r.Outcome == AuditOutcome.Success);

        // The REST surface: the catalog, a run and an export.
        var client = e.Api.ClientFor(ApiFactory.Token(sub: "u-alice", roles: ["plm-viewer", "plm-author"]));
        var datasets = JsonNode.Parse(await client.GetStringAsync("/api/v1/output/reports/datasets"))!.AsArray();
        Assert.Equal(8, datasets.Count);
        var res = await client.PostAsync("/api/v1/output/reports/run", JsonContent.Create(new { dataset = "parts", spec = new { columns = new[] { "number" } } }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var bad = await client.PostAsync("/api/v1/output/reports/run", JsonContent.Create(new { dataset = "parts", spec = new { columns = new[] { "nope" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await e.Api.CreateClient().GetAsync("/api/v1/output/reports/datasets")).StatusCode);
    }

    [Fact]
    public async Task The_published_reports_openapi_document_equals_the_committed_one_and_lists_nothing_else()
    {
        using var api = await ApiFactory.CreateAsync(pg);
        var json = await api.ClientFor(ApiFactory.Token(sub: "u-vic", roles: ["plm-viewer"])).GetStringAsync("/openapi/reports.json");
        var doc = JsonNode.Parse(json)!;
        Assert.StartsWith("3.1", doc["openapi"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.All(doc["paths"]!.AsObject().Select(p => p.Key), k => Assert.StartsWith("/api/v1/output", k, StringComparison.Ordinal));
        Assert.DoesNotContain("/ui/", json, StringComparison.Ordinal);
        var path = Path.Combine(FindRoot(), "docs", "reports", "openapi.json");
        var normalized = JsonNode.Parse(json)!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("KEEL_UPDATE_OPENAPI") == "1") { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, normalized); }
        Assert.True(File.Exists(path), "docs/reports/openapi.json is missing: run scripts/update-openapi.sh");
        Assert.Equal((await File.ReadAllTextAsync(path)).Replace("\r\n", "\n"), normalized);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
