using System.Text;
using Keel.Core;
using Keel.Requirements;

namespace Keel.IntegrationTests;

/// <summary>Import with a reviewable dry run, export in three formats, and the rules that keep both inside the access policy.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RequirementsIoTests(PostgresFixture pg)
{
    private const string Csv =
        "key,parent,title,text,type,priority,method,rationale,status\n" +
        "X1,,Depth rating,\"The pod shall survive 6000 m, with margin.\",performance,high,test,Mission,approved\n" +
        "X2,X1,Housing rating,Housing shall be rated to 6000 m.,functional,medium,analysis,,approved\n" +
        ",X1,Sealing,Seals shall be double.,interface,low,inspection,,draft\n";

    [Fact]
    public async Task A_dry_run_changes_nothing_and_only_the_reviewed_plan_can_be_applied()
    {
        var env = await ReqEnv.CreateAsync(pg);
        var spec = await env.SpecAsync();
        var before = await env.IterationAsync(spec);

        var preview = await env.Req.ImportAsync(ReqEnv.Alice, spec, "csv", Csv, true, null, "corr-imp");
        Assert.True(preview.IsOk, string.Join("; ", preview.Errors));
        Assert.Equal((3, 0, 0, 0), (preview.Value!.Created, preview.Value.Updated, preview.Value.Unchanged, preview.Value.Errors));
        Assert.False(preview.Value.Applied);
        Assert.Equal(before, await env.IterationAsync(spec));
        Assert.Empty((await env.SetAsync(spec)).Rows);

        // The digest of the preview is required, and it must still match.
        Assert.Equal(OutcomeKind.Conflict, (await env.Req.ImportAsync(ReqEnv.Alice, spec, "csv", Csv, false, null, null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await env.Req.ImportAsync(ReqEnv.Alice, spec, "csv", Csv.Replace("6000 m, with", "7000 m, with", StringComparison.Ordinal), false, preview.Value.Digest, null)).Kind);
        var applied = await env.Req.ImportAsync(ReqEnv.Alice, spec, "csv", Csv, false, preview.Value.Digest, null);
        Assert.True(applied.IsOk, string.Join("; ", applied.Errors));
        Assert.True(applied.Value!.Applied);
        var set = await env.SetAsync(spec);
        Assert.Equal(["Depth rating", "Housing rating", "Sealing"], set.Rows.Select(r => r.Title));
        Assert.Equal(["1", "1.1", "1.2"], set.Rows.Select(r => r.Outline));
        Assert.Equal("The pod shall survive 6000 m, with margin.", set.Rows[0].Text);
        Assert.Equal(before + 1, await env.IterationAsync(spec)); // the whole file is one iteration

        // Running it again changes nothing: keys of the file are foreign ids (new rows) unless they are existing keys.
        var key = set.Rows[0].Key!;
        var update = $"key,title,text,type\n{key},Depth rating,Reworded text.,\nR-9999,Ghost,Not an existing key: a new requirement.,bogus\n";
        var bad = (await env.Req.ImportAsync(ReqEnv.Alice, spec, "csv", update, true, null, null)).Value!;
        Assert.Equal(1, bad.Errors);
        Assert.Equal(1, bad.Updated);
        Assert.Equal(OutcomeKind.Invalid, (await env.Req.ImportAsync(ReqEnv.Alice, spec, "csv", update, false, bad.Digest, null)).Kind); // a row with a problem blocks the apply
        var good = (await env.Req.ImportAsync(ReqEnv.Alice, spec, "csv", $"key,title,text\n{key},Depth rating,Reworded text.\n", true, null, null)).Value!;
        Assert.Equal((1, 0), (good.Updated, good.Errors));
        Assert.True((await env.Req.ImportAsync(ReqEnv.Alice, spec, "csv", $"key,title,text\n{key},Depth rating,Reworded text.\n", false, good.Digest, null)).IsOk);
        Assert.Equal("Reworded text.", (await env.SetAsync(spec)).Rows[0].Text);
        var same = (await env.Req.ImportAsync(ReqEnv.Alice, spec, "csv", $"key,title,text\n{key},Depth rating,Reworded text.\n", true, null, null)).Value!;
        Assert.Equal(1, same.Unchanged);
        Assert.Contains(await env.C.AuditAsync(), e => e.Action == "requirement.import" && e.Payload.Contains("digest", StringComparison.Ordinal));
        Assert.DoesNotContain("Reworded text", await env.AuditJsonAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restricted_requirement_cannot_be_discovered_or_changed_through_an_import()
    {
        var env = await ReqEnv.CreateAsync(pg);
        var spec = await env.SpecAsync();
        var secret = await env.AddAsync(spec, ReqEnv.Input("Secret", markings: ReqEnv.Itar));
        // A row naming a requirement the caller cannot read is a new requirement, exactly like one naming an id that does not exist: the plan is the same, the hidden one is never touched.
        var ghost = await env.Req.ImportAsync(ConfigEnv.Eve, spec, "csv", "key,title,text\nR-9999,Ghost,x\n", true, null, null);
        var hidden = await env.Req.ImportAsync(ConfigEnv.Eve, spec, "csv", $"key,title,text\n{secret},Ghost,x\n", true, null, null);
        Assert.Equal((ghost.Value!.Created, ghost.Value.Updated, ghost.Value.Errors), (hidden.Value!.Created, hidden.Value.Updated, hidden.Value.Errors));
        Assert.Equal((1, 0, 0), (hidden.Value.Created, hidden.Value.Updated, hidden.Value.Errors));
        // An export leaves the restricted requirement out entirely.
        await env.AddAsync(spec, ReqEnv.Input("Plain"));
        var file = (await env.Req.ExportAsync(ConfigEnv.Eve, spec, null, "csv", null)).Value!;
        var text = Encoding.UTF8.GetString(file.Content);
        Assert.Contains("Plain", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.Equal(1, file.Count);
        var full = Encoding.UTF8.GetString((await env.Req.ExportAsync(ReqEnv.Alice, spec, null, "csv", null)).Value!.Content);
        Assert.Contains("ITAR", full, StringComparison.Ordinal); // the banner of what the file holds
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("excel")]
    [InlineData("reqif")]
    public async Task What_is_exported_can_be_imported_into_another_specification_unchanged(string format)
    {
        var env = await ReqEnv.CreateAsync(pg);
        var spec = await env.SpecAsync("Source set");
        var a = await env.AddAsync(spec, ReqEnv.Input("=SUM(A1) guard, \"quoted\"", "Line one\nLine two; with, delimiters and \"quotes\"."));
        await env.AddAsync(spec, ReqEnv.Input("Child", "Child text <b>&amp;</b>", parent: a, type: "safety", priority: "critical", method: "demonstration"));
        var file = (await env.Req.ExportAsync(ReqEnv.Alice, spec, null, format, null)).Value!;
        Assert.EndsWith(format == "reqif" ? ".reqif" : ".csv", file.FileName, StringComparison.Ordinal);
        var content = Encoding.UTF8.GetString(file.Content);
        if (format != "reqif") Assert.Contains("'=SUM", content, StringComparison.Ordinal); // spreadsheet formula guard

        var target = await env.SpecAsync("Target set");
        var preview = (await env.Req.ImportAsync(ReqEnv.Alice, target, format, content, true, null, null)).Value!;
        Assert.Equal((2, 0), (preview.Created, preview.Errors));
        Assert.True((await env.Req.ImportAsync(ReqEnv.Alice, target, format, content, false, preview.Digest, null)).IsOk);
        var original = (await env.SetAsync(spec)).Rows;
        var copy = (await env.SetAsync(target)).Rows;
        Assert.Equal(original.Select(r => (r.Outline, r.Title, r.Text, r.Type, r.Priority, r.Method, r.Status)), copy.Select(r => (r.Outline, r.Title, r.Text, r.Type, r.Priority, r.Method, r.Status)));
    }

    [Fact]
    public async Task Import_needs_an_editable_revision_and_a_known_format_and_refuses_oversized_files()
    {
        var env = await ReqEnv.CreateAsync(pg);
        var spec = await env.SpecAsync();
        await env.AddAsync(spec, ReqEnv.Input("One"));
        Assert.Equal(OutcomeKind.Invalid, (await env.Req.ImportAsync(ReqEnv.Alice, spec, "docx", "x", true, null, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Req.ImportAsync(ReqEnv.Alice, spec, "csv", new string('a', RequirementIo.MaxBytes + 1), true, null, null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Req.ImportAsync(ReqEnv.Alice, spec, "reqif", "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><x>&e;</x>", true, null, null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Req.ImportAsync(ReqEnv.Mallory, spec, "csv", Csv, true, null, null)).Kind);
        await env.ReleaseAsync(spec);
        Assert.Equal(OutcomeKind.Invalid, (await env.Req.ImportAsync(ReqEnv.Alice, spec, "csv", Csv, true, null, null)).Kind);
    }
}
