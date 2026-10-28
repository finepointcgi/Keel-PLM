using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Keel.Documents;
using Keel.Integration;
using Keel.Security;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Technical Data Packages (MIL-STD-31000 aligned) against real Postgres and the real vault: structure, hashes, signature, completeness, recipient
/// restrictions, distribution statement aggregation, record immutability, streaming memory.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TdpTests(PostgresFixture pg)
{
    private static readonly Subject Kim = WorkflowEnv.User("kim", [Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"], cui: ["EXPT", "CTI", "PROPIN"]);

    private static Markings Itar(DistributionStatement d = DistributionStatement.D, string? program = "ORCA", string[]? auths = null) =>
        Markings.Create(ExportJurisdiction.Itar, d, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology",
            program: program, exportAuthorizations: auths).Value;

    private sealed record Fixture(IntegrationEnv Env, string Assembly, string Bolt, string Drawing, string Model, string Spec);

    private async Task<IntegrationEnv> EnvAsync()
    {
        var env = await IntegrationEnv.CreateAsync(pg, documents: true);
        await env.SaveProfileAsync(IntegrationEnv.Profile("tdp-dod", "DodContractor"));
        await env.SaveProfileAsync(IntegrationEnv.Profile("tdp-dod-component", "Dod"));
        await env.SaveProfileAsync(IntegrationEnv.Profile("tdp-foreign", "Foreign", usOnly: false, auths: ["TAA-0042"], cui: ["EXPT"]));
        await new Keel.Workflow.UserDirectory(env.Ds, TimeProvider.System, new Keel.Workflow.WorkflowOptions(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Keel.Workflow.UserDirectory>.Instance).UpsertAsync(Kim);
        return env;
    }

    private static async Task<string> DocumentAsync(IntegrationEnv env, string name, string type, Markings m, string fileName, byte[] bytes, Subject? by = null, string mediaType = "application/octet-stream")
    {
        var d = await env.Config.Docs!.CreateAsync(by ?? ConfigEnv.Alice, new CreateDocumentCommand(name, type, m, "Synthetic"), new FileUpload(fileName, mediaType, new MemoryStream(bytes)), null);
        Assert.True(d.IsOk, $"{name}: {string.Join(";", d.Errors)}");
        return d.Value!.Number;
    }

    private static async Task LinkAsync(IntegrationEnv env, string part, params string[] documents)
    {
        var c = env.Config;
        Assert.True((await c.Parts.CheckOutAsync(ConfigEnv.Alice, part, null)).IsOk);
        foreach (var d in documents)
        {
            var link = await c.Docs!.AddLinkToWorkingCopyAsync(ConfigEnv.Alice, part, d, "describes", null, null);
            Assert.True(link.IsOk, $"{part} -> {d}: {link.Kind} {string.Join(";", link.Errors)}");
        }
        await c.CheckInAsync(part, comment: "documents");
    }

    private async Task<Fixture> BuildAsync(Markings? markings = null, Markings? specMarkings = null)
    {
        var env = await EnvAsync();
        var c = env.Config;
        var m = markings ?? Itar();
        var bolt = await c.NewPartAsync("Bolt", "Component", m);
        var asm = await c.NewPartAsync("Pod", "Assembly", m);
        var drawing = await DocumentAsync(env, "Pod drawing", "Drawing", m, "pod.pdf", DocumentServiceTests.SamplePdf(1), mediaType: "application/pdf");
        var model = await DocumentAsync(env, "Bolt model", "CAD", m, "bolt.step", Encoding.UTF8.GetBytes("ISO-10303-21; synthetic model " + Guid.NewGuid()));
        var spec = await DocumentAsync(env, "Bolt specification", "Specification", specMarkings ?? m, "bolt-spec.txt", Encoding.UTF8.GetBytes("Synthetic specification " + Guid.NewGuid()), mediaType: "text/plain");
        await LinkAsync(env, bolt, model, spec);
        await c.CheckOutAsync(asm);
        await c.AddLineAsync(asm, bolt, 4m);
        await c.CheckInAsync(asm, comment: "structure");
        await LinkAsync(env, asm, drawing);
        foreach (var p in new[] { bolt, asm }) Assert.True((await c.Parts.SetStateAsync(ConfigEnv.Ada, p, "Released", "load", true, null)).IsOk);
        return new Fixture(env, asm, bolt, drawing, model, spec);
    }

    private static async Task<(byte[] Bytes, TdpExportRecord Record)> ExportAsync(IntegrationEnv env, Subject who, string number, string recipient, string type = "part")
    {
        var prepared = await env.Tdp!.PrepareAsync(who, new TdpRequest(type, number, null, recipient), "corr-tdp");
        Assert.True(prepared.IsOk, $"{prepared.Kind} {string.Join("; ", prepared.Errors)}");
        using var ms = new MemoryStream();
        var record = await prepared.Value!.WriteToAsync(ms, CancellationToken.None);
        return (ms.ToArray(), record);
    }

    private static Dictionary<string, byte[]> Unzip(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        return zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToDictionary(e => e.FullName, e =>
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        });
    }

    private static byte[] Zip(Dictionary<string, byte[]> entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(content);
            }
        return ms.ToArray();
    }

    private static async Task<TdpVerification> VerifyAsync(IntegrationEnv env, Subject who, byte[] package)
    {
        var o = await env.Tdp!.VerifyPackageAsync(who, new MemoryStream(package), "corr-verify");
        Assert.True(o.IsOk, string.Join(";", o.Errors));
        return o.Value!;
    }

    // ---------------------------------------------------------------- structure, hashes, signature

    [Fact]
    public async Task A_package_has_the_documented_structure_marked_files_a_signed_manifest_and_verifies_against_its_record()
    {
        var f = await BuildAsync();
        var (bytes, record) = await ExportAsync(f.Env, ConfigEnv.Alice, f.Assembly, "tdp-dod");
        var files = Unzip(bytes);
        var root = $"TDP-{f.Assembly}-A/";
        Assert.All(files.Keys, k => Assert.StartsWith(root, k, StringComparison.Ordinal));
        Assert.Contains(root + "cover-sheet.txt", files.Keys);
        Assert.Contains(root + "part.json", files.Keys);
        Assert.Contains(root + $"bom/{f.Assembly}_A_bom.csv", files.Keys);
        Assert.Contains(root + $"bom/{f.Assembly}_A_bom.json", files.Keys);
        Assert.Contains(root + "manifest.json", files.Keys);
        Assert.Contains(root + "manifest.sig", files.Keys);
        Assert.Contains(root + "public-key.pem", files.Keys);

        // Documents by MIL-STD-31000 element category; a PDF stays a PDF (stamped), anything else arrives in the marked ZIP wrapper.
        var pdf = files.Single(k => k.Key.StartsWith(root + "documents/DRAWINGS/", StringComparison.Ordinal));
        Assert.EndsWith("pod.pdf", pdf.Key, StringComparison.Ordinal);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf.Value, 0, 4), StringComparison.Ordinal);
        var model = files.Single(k => k.Key.StartsWith(root + "documents/MODELS/", StringComparison.Ordinal));
        Assert.EndsWith("bolt.step.zip", model.Key, StringComparison.Ordinal);
        Assert.Contains("MARKINGS.pdf", Unzip(model.Value).Keys);
        Assert.Contains(files.Keys, k => k.StartsWith(root + "documents/SPECIFICATIONS/", StringComparison.Ordinal));

        // The cover sheet carries the banner, the distribution statement, the export warning and the destruction notice.
        var cover = Encoding.UTF8.GetString(files[root + "cover-sheet.txt"]);
        Assert.StartsWith("CUI//EXPT", cover, StringComparison.Ordinal);
        Assert.Contains("DISTRIBUTION STATEMENT D", cover, StringComparison.Ordinal);
        Assert.Contains("Arms Export Control Act", cover, StringComparison.Ordinal);
        Assert.Contains("DESTRUCTION NOTICE", cover, StringComparison.Ordinal);
        Assert.Contains(f.Assembly, cover, StringComparison.Ordinal);

        // The BOM list is resolved at the part revision.
        var csv = Encoding.UTF8.GetString(files[root + $"bom/{f.Assembly}_A_bom.csv"]);
        Assert.Contains(f.Bolt, csv, StringComparison.Ordinal);
        Assert.Equal(4m, JsonNode.Parse(files[root + $"bom/{f.Assembly}_A_bom.json"])!["lines"]![0]!["quantity"]!.GetValue<decimal>());

        // Manifest: every element with its SHA-256 as written; provenance; counts reconcile.
        var manifestBytes = files[root + "manifest.json"];
        var manifest = JsonNode.Parse(manifestBytes)!;
        Assert.Equal(TdpService.Format, manifest["format"]!.GetValue<string>());
        foreach (var el in manifest["elements"]!.AsArray())
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(files[root + el!["path"]!.GetValue<string>()])), el["sha256"]!.GetValue<string>());
        Assert.Equal(3, manifest["provenance"]!["counts"]!["documentFiles"]!.GetValue<int>());
        Assert.Equal(2, manifest["provenance"]!["counts"]!["parts"]!.GetValue<int>());
        Assert.Equal("alice", manifest["provenance"]!["exporter"]!.GetValue<string>());
        Assert.Contains($"{f.Assembly} A", manifest["provenance"]!["sourceVersions"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal("tdp-dod", manifest["recipient"]!["profile"]!.GetValue<string>());
        Assert.Contains("DISTRIBUTION D", manifest["distribution"]!["banner"]!.GetValue<string>(), StringComparison.Ordinal);

        // The package verifies; the record holds the manifest and its hash, not the bytes.
        var verification = await VerifyAsync(f.Env, ConfigEnv.Alice, bytes);
        Assert.True(verification.Valid, string.Join("; ", verification.Checks.Where(c => !c.Ok).Select(c => c.Name + ": " + c.Detail)));
        Assert.Equal(record.Id, verification.ExportId);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), record.ManifestSha256);
        Assert.Equal(1, await f.Env.ScalarAsync("SELECT count(*) FROM integration.tdp_export"));
        Assert.True((await f.Env.Tdp!.VerifyRecordAsync(ConfigEnv.Alice, record.Id, null)).Value!.Valid);
        Assert.Equal(f.Env.Signer!.KeyId, record.SignatureKeyId);

        // Audit: the export (recipient, counts, manifest hash, key id) and one document.download per file.
        var audit = await f.Env.Config.AuditAsync();
        var exported = audit.Single(a => a.Action == "integration.tdp.export" && a.Outcome == AuditOutcome.Success);
        Assert.Contains(record.ManifestSha256, exported.Payload, StringComparison.Ordinal);
        Assert.Contains(record.SignatureKeyId, exported.Payload, StringComparison.Ordinal);
        Assert.Contains("tdp-dod", exported.Payload, StringComparison.Ordinal);
        Assert.Equal(3, audit.Count(a => a.Action == "document.download" && a.Outcome == AuditOutcome.Success));
    }

    [Fact]
    public async Task Any_change_to_a_file_the_manifest_or_the_signing_key_is_detected()
    {
        var f = await BuildAsync();
        var (bytes, _) = await ExportAsync(f.Env, ConfigEnv.Alice, f.Assembly, "tdp-dod");
        var root = $"TDP-{f.Assembly}-A/";

        // A changed document file.
        var files = Unzip(bytes);
        var doc = files.Keys.First(k => k.Contains("/documents/", StringComparison.Ordinal));
        files[doc] = [.. files[doc], 0x00];
        var changed = await VerifyAsync(f.Env, ConfigEnv.Alice, Zip(files));
        Assert.False(changed.Valid);
        Assert.False(changed.Checks.Single(c => c.Name == "files.hashes").Ok);
        Assert.True(changed.Checks.Single(c => c.Name == "signature.valid").Ok); // the manifest itself is untouched

        // A removed file and an added file.
        files = Unzip(bytes);
        files.Remove(doc);
        Assert.False((await VerifyAsync(f.Env, ConfigEnv.Alice, Zip(files))).Checks.Single(c => c.Name == "files.hashes").Ok);
        files = Unzip(bytes);
        files[root + "extra.txt"] = "smuggled"u8.ToArray();
        Assert.False((await VerifyAsync(f.Env, ConfigEnv.Alice, Zip(files))).Checks.Single(c => c.Name == "files.no_extra").Ok);

        // A changed manifest (a hash edited to match a tampered file) breaks the signature.
        files = Unzip(bytes);
        var manifest = JsonNode.Parse(files[root + "manifest.json"])!;
        manifest["recipient"]!["profile"] = "somebody-else";
        files[root + "manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var forged = await VerifyAsync(f.Env, ConfigEnv.Alice, Zip(files));
        Assert.False(forged.Checks.Single(c => c.Name == "signature.valid").Ok);
        Assert.False(forged.Valid);

        // A package re-signed with someone else's key: the embedded public key is not trusted by this installation.
        files = Unzip(bytes);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var attackerId = Keel.Audit.CheckpointKeys.KeyId(attacker);
        var signed = files[root + "manifest.json"];
        files[root + "manifest.sig"] = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["algorithm"] = "ECDSA-P256-SHA256-P1363", ["keyId"] = attackerId, ["manifestSha256"] = Convert.ToHexStringLower(SHA256.HashData(signed)),
            ["signature"] = Convert.ToBase64String(attacker.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
        }.ToJsonString());
        files[root + "public-key.pem"] = Encoding.ASCII.GetBytes(attacker.ExportSubjectPublicKeyInfoPem());
        var untrusted = await VerifyAsync(f.Env, ConfigEnv.Alice, Zip(files));
        Assert.False(untrusted.Checks.Single(c => c.Name == "signature.key_trusted").Ok);
        Assert.False(untrusted.Valid);

        // Not a ZIP at all.
        Assert.Equal(OutcomeKind.Invalid, (await f.Env.Tdp!.VerifyPackageAsync(ConfigEnv.Alice, new MemoryStream("not a zip"u8.ToArray()), null)).Kind);
    }

    // ---------------------------------------------------------------- categories

    [Fact]
    public async Task Unmapped_documents_go_into_other_technical_data_and_are_reported_and_the_mapping_is_editable()
    {
        var f = await BuildAsync();
        var plan = (await f.Env.Tdp!.PlanAsync(ConfigEnv.Alice, new TdpRequest("part", f.Assembly, null, "tdp-dod"), null)).Value!;
        Assert.Equal(0, plan.UnmappedDocuments);

        // An administrator removes the rule for specifications: the document is reported and still included under Other technical data.
        Assert.True((await f.Env.Admin.DeleteCategoryRuleAsync(IntegrationEnv.Ada, "Specification", "*", null)).IsOk);
        plan = (await f.Env.Tdp.PlanAsync(ConfigEnv.Alice, new TdpRequest("part", f.Assembly, null, "tdp-dod"), null)).Value!;
        Assert.Equal(1, plan.UnmappedDocuments);
        Assert.Contains(plan.Elements, e => e.DocumentNumber == f.Spec && e.Category == "OTHER" && !e.Mapped);
        var (bytes, _) = await ExportAsync(f.Env, ConfigEnv.Alice, f.Assembly, "tdp-dod");
        Assert.Contains("have no category mapping", Encoding.UTF8.GetString(Unzip(bytes)[$"TDP-{f.Assembly}-A/cover-sheet.txt"]), StringComparison.Ordinal);

        // Mapping it to quality assurance provisions moves it.
        Assert.True((await f.Env.Admin.SaveCategoryRuleAsync(IntegrationEnv.Ada, "Specification", null, "QUALITY_ASSURANCE", null)).IsOk);
        plan = (await f.Env.Tdp.PlanAsync(ConfigEnv.Alice, new TdpRequest("part", f.Assembly, null, "tdp-dod"), null)).Value!;
        Assert.Contains(plan.Elements, e => e.DocumentNumber == f.Spec && e.Category == "QUALITY_ASSURANCE" && e.Mapped);
        Assert.Equal(OutcomeKind.Forbidden, (await f.Env.Admin.SaveCategoryRuleAsync(ConfigEnv.Alice, "Report", null, "OTHER", null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await f.Env.Admin.SaveCategoryRuleAsync(IntegrationEnv.Ada, "Report", null, "NOT_A_CATEGORY", null)).Kind);
    }

    // ---------------------------------------------------------------- completeness

    [Fact]
    public async Task A_package_is_refused_with_counts_only_when_a_required_document_is_not_readable_and_nothing_is_recorded()
    {
        // The specification carries CUI category PROPIN; the exporter (alice: EXPT and CTI only) cannot read it.
        var env = await EnvAsync();
        var c = env.Config;
        var part = await c.NewPartAsync("Pod", "Component", Itar());
        var hidden = await DocumentAsync(env, "Hidden specification", "Specification", Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT", "PROPIN"], controllingOffice: "ACME", distributionReason: "r", program: "ORCA").Value, "hidden.txt", Encoding.UTF8.GetBytes("Synthetic hidden " + Guid.NewGuid()), Kim);
        var open = await DocumentAsync(env, "Pod drawing", "Drawing", Itar(), "pod.pdf", DocumentServiceTests.SamplePdf(1), mediaType: "application/pdf");
        Assert.True((await c.Parts.CheckOutAsync(Kim, part, null)).IsOk);
        Assert.True((await c.Docs!.AddLinkToWorkingCopyAsync(Kim, part, hidden, "references", null, null)).IsOk);
        Assert.True((await c.Docs.AddLinkToWorkingCopyAsync(Kim, part, open, "describes", null, null)).IsOk);
        Assert.True((await c.Parts.CheckInAsync(Kim, part, "docs", null)).IsOk);
        Assert.True((await c.Parts.SetStateAsync(ConfigEnv.Ada, part, "Released", "load", true, null)).IsOk);

        var plan = (await env.Tdp!.PlanAsync(ConfigEnv.Alice, new TdpRequest("part", part, null, "tdp-dod"), null)).Value!;
        Assert.False(plan.Permitted);
        var refusal = Assert.Single(plan.Refusals);
        Assert.Equal(("unreadable.documents", 1), (refusal.Reason, refusal.Count));
        var prepared = await env.Tdp.PrepareAsync(ConfigEnv.Alice, new TdpRequest("part", part, null, "tdp-dod"), "corr");
        Assert.Equal(OutcomeKind.Invalid, prepared.Kind);
        Assert.DoesNotContain(hidden, string.Join(" ", prepared.Errors), StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden specification", string.Join(" ", prepared.Errors), StringComparison.Ordinal);
        Assert.Equal(0, await env.ScalarAsync("SELECT count(*) FROM integration.tdp_export"));
        var denial = (await c.AuditAsync()).Single(a => a.Action == "integration.tdp.export" && a.Outcome == AuditOutcome.Denied);
        Assert.Contains("unreadable.documents=1", denial.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(hidden, denial.Payload, StringComparison.Ordinal);

        // The exporter who can read everything gets the complete package.
        await env.SaveProfileAsync(IntegrationEnv.Profile("tdp-dod-both", "DodContractor", cui: ["EXPT", "CTI", "PROPIN"]));
        Assert.True((await env.Tdp.PlanAsync(Kim, new TdpRequest("part", part, null, "tdp-dod-both"), null)).Value!.Permitted);
    }

    [Fact]
    public async Task Unreadable_structure_lines_and_missing_roles_refuse_the_export()
    {
        var env = await EnvAsync();
        var c = env.Config;
        var child = await c.NewPartAsync("Secret clip", "Component", Itar(program: "KRAKEN"), Kim);
        var asm = await c.NewPartAsync("Kit", "Assembly", Itar(program: "KRAKEN"), Kim);
        Assert.True((await c.Parts.CheckOutAsync(Kim, asm, null)).IsOk);
        Assert.True((await c.Bom.AddUsageAsync(Kim, asm, new Keel.Bom.AddUsageCommand(child, 2m), null)).IsOk);
        Assert.True((await c.Parts.CheckInAsync(Kim, asm, "s", null)).IsOk);
        var kai = WorkflowEnv.User("kai", [Roles.Admin, Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]);
        foreach (var p in new[] { child, asm }) Assert.True((await c.Parts.SetStateAsync(kai, p, "Released", "load", true, null)).IsOk);

        // alice cannot read the assembly at all: not found, not a refusal that names it.
        Assert.Equal(OutcomeKind.NotFound, (await env.Tdp!.PlanAsync(ConfigEnv.Alice, new TdpRequest("part", asm, null, "tdp-dod"), null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Tdp.PlanAsync(WorkflowEnv.User("svc", [Roles.Integration]), new TdpRequest("part", asm, null, "tdp-dod"), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Tdp.PlanAsync(Kim, new TdpRequest("part", asm, null, "no-such-profile"), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Tdp.PlanAsync(Kim, new TdpRequest("document", asm, null, "tdp-dod"), null)).Kind);
    }

    // ---------------------------------------------------------------- recipients and distribution statements

    [Fact]
    public async Task Itar_to_a_foreign_recipient_is_refused_unless_the_data_lists_an_authorization_the_recipient_holds()
    {
        var plain = await BuildAsync();
        var refused = (await plain.Env.Tdp!.PlanAsync(ConfigEnv.Alice, new TdpRequest("part", plain.Assembly, null, "tdp-foreign"), null)).Value!;
        Assert.False(refused.Permitted);
        Assert.Contains(refused.Refusals, r => r.Reason == OutboundRelease.UsPersonRequired && r.Count >= 4); // part, child and documents are each counted
        Assert.Equal(OutcomeKind.Invalid, (await plain.Env.Tdp.PrepareAsync(ConfigEnv.Alice, new TdpRequest("part", plain.Assembly, null, "tdp-foreign"), null)).Kind);

        // Every object lists TAA-0042 (and the recipient profile allows program ORCA): the foreign recipient may receive it.
        await plain.Env.SaveProfileAsync(IntegrationEnv.Profile("tdp-foreign", "Foreign", usOnly: false, auths: ["TAA-0042"], cui: ["EXPT"], programs: ["ORCA"]));
        var authorized = await BuildAsync(Itar(auths: ["TAA-0042"]));
        await authorized.Env.SaveProfileAsync(IntegrationEnv.Profile("tdp-foreign", "Foreign", usOnly: false, auths: ["TAA-0042"], cui: ["EXPT"], programs: ["ORCA"]));
        var plan = (await authorized.Env.Tdp!.PlanAsync(ConfigEnv.Alice, new TdpRequest("part", authorized.Assembly, null, "tdp-foreign"), null)).Value!;
        Assert.True(plan.Permitted, string.Join("; ", plan.Refusals.Select(r => $"{r.Reason}={r.Count}")));
        var (bytes, _) = await ExportAsync(authorized.Env, ConfigEnv.Alice, authorized.Assembly, "tdp-foreign");
        Assert.True((await VerifyAsync(authorized.Env, ConfigEnv.Alice, bytes)).Valid);
        // A different agreement does not help.
        await authorized.Env.SaveProfileAsync(IntegrationEnv.Profile("tdp-foreign-other", "Foreign", usOnly: false, auths: ["TAA-9999"], cui: ["EXPT"], programs: ["ORCA"]));
        Assert.False((await authorized.Env.Tdp.PlanAsync(ConfigEnv.Alice, new TdpRequest("part", authorized.Assembly, null, "tdp-foreign-other"), null)).Value!.Permitted);
    }

    [Fact]
    public async Task Distribution_statements_restrict_the_recipient_category_and_the_package_carries_the_most_restrictive_statement()
    {
        // The part is statement D, the specification is statement E: the package is E, which only a DoD component may receive.
        var f = await BuildAsync(Itar(DistributionStatement.D), Itar(DistributionStatement.E));
        var toContractor = (await f.Env.Tdp!.PlanAsync(ConfigEnv.Alice, new TdpRequest("part", f.Assembly, null, "tdp-dod"), null)).Value!;
        Assert.False(toContractor.Permitted);
        Assert.Contains(toContractor.Refusals, r => r.Reason.StartsWith(OutboundRelease.RecipientCategoryNotAllowed, StringComparison.Ordinal));
        var toDod = (await f.Env.Tdp.PlanAsync(ConfigEnv.Alice, new TdpRequest("part", f.Assembly, null, "tdp-dod-component"), null)).Value!;
        Assert.True(toDod.Permitted);
        Assert.Contains("DISTRIBUTION E", toDod.Banner, StringComparison.Ordinal);

        var (bytes, record) = await ExportAsync(f.Env, ConfigEnv.Alice, f.Assembly, "tdp-dod-component");
        Assert.Contains("DISTRIBUTION E", record.Banner, StringComparison.Ordinal);
        Assert.Equal(DistributionStatement.E, record.Markings.DistributionStatement);
        var cover = Encoding.UTF8.GetString(Unzip(bytes)[$"TDP-{f.Assembly}-A/cover-sheet.txt"]);
        Assert.Contains("DISTRIBUTION STATEMENT E", cover, StringComparison.Ordinal);
        Assert.DoesNotContain("STATEMENT D.", cover, StringComparison.Ordinal);

        // Statement F is never released automatically.
        var fPart = await BuildAsync(Itar(DistributionStatement.F));
        var plan = (await fPart.Env.Tdp!.PlanAsync(ConfigEnv.Alice, new TdpRequest("part", fPart.Assembly, null, "tdp-dod-component"), null)).Value!;
        Assert.Contains(plan.Refusals, r => r.Reason == OutboundRelease.DistributionFManual);
    }

    [Fact]
    public async Task The_export_history_is_filtered_by_the_readers_markings()
    {
        var f = await BuildAsync();
        await ExportAsync(f.Env, ConfigEnv.Alice, f.Assembly, "tdp-dod");
        Assert.Single((await f.Env.Tdp!.ListAsync(ConfigEnv.Alice, null, 10)).Value!);
        Assert.Empty((await f.Env.Tdp.ListAsync(WorkflowEnv.User("lena", [Roles.Viewer], us: false), null, 10)).Value!);
        Assert.Empty((await f.Env.Tdp.ListAsync(WorkflowEnv.User("eve", [Roles.Viewer], programs: []), null, 10)).Value!);
        var record = (await f.Env.Tdp.ListAsync(ConfigEnv.Alice, f.Assembly, 10)).Value!.Single();
        Assert.Equal(OutcomeKind.NotFound, (await f.Env.Tdp.GetAsync(WorkflowEnv.User("lena", [Roles.Viewer], us: false), record.Id, null)).Kind);
        Assert.NotNull((await f.Env.Tdp.GetAsync(ConfigEnv.Alice, record.Id, null)).Value!.ManifestText);
    }

    [Fact]
    public async Task The_export_record_is_immutable_at_the_database_level()
    {
        var f = await BuildAsync();
        await ExportAsync(f.Env, ConfigEnv.Alice, f.Assembly, "tdp-dod");
        foreach (var sql in new[] { "UPDATE integration.tdp_export SET exporter_name = 'mallory'", "DELETE FROM integration.tdp_export", "TRUNCATE integration.tdp_export" })
        {
            await using var cmd = f.Env.Ds.CreateCommand(sql);
            await Assert.ThrowsAsync<PostgresException>(async () => await cmd.ExecuteNonQueryAsync());
        }
        await using var owner = new NpgsqlConnection(f.Env.Config.Db.MigratorConnectionString);
        await owner.OpenAsync();
        await using var del = new NpgsqlCommand("DELETE FROM integration.tdp_export", owner);
        await Assert.ThrowsAsync<PostgresException>(async () => await del.ExecuteNonQueryAsync());
        Assert.Equal(1, await f.Env.ScalarAsync("SELECT count(*) FROM integration.tdp_export"));
    }

    // ---------------------------------------------------------------- baseline

    [Fact]
    public async Task A_baseline_exports_its_frozen_items_and_pinned_documents()
    {
        var f = await BuildAsync();
        var baseline = await f.Env.Config.BaselineAsync(f.Assembly, "Design", docs: true);
        // A draft baseline is not exported.
        var draft = (await f.Env.Tdp!.PlanAsync(ConfigEnv.Alice, new TdpRequest("baseline", baseline.Summary.Number, null, "tdp-dod"), null)).Value!;
        Assert.Contains(draft.Refusals, r => r.Reason == "baseline.not_established");
        Assert.True((await f.Env.Config.Baselines.EstablishAsync(ConfigEnv.Alice, baseline.Summary.Number, "Design freeze", null)).IsOk);

        var (bytes, record) = await ExportAsync(f.Env, ConfigEnv.Alice, baseline.Summary.Number, "tdp-dod", "baseline");
        Assert.Equal("baseline", record.ObjectType);
        var files = Unzip(bytes);
        var root = $"TDP-{f.Assembly}-A-{baseline.Summary.Number}/";
        Assert.Contains(root + $"bom/{f.Assembly}_A_bom.json", files.Keys);
        Assert.Equal(f.Bolt, JsonNode.Parse(files[root + $"bom/{f.Assembly}_A_bom.json"])!["lines"]![0]!["number"]!.GetValue<string>());
        Assert.Contains(files.Keys, k => k.StartsWith(root + "documents/", StringComparison.Ordinal));
        Assert.Contains(baseline.Summary.Number, JsonNode.Parse(files[root + "manifest.json"])!["provenance"]!["sourceVersions"]!.AsArray().Select(v => v!.GetValue<string>()).First(), StringComparison.Ordinal);
        Assert.True((await VerifyAsync(f.Env, ConfigEnv.Alice, bytes)).Valid);
    }

    // ---------------------------------------------------------------- streaming

    private sealed class RandomStream : Stream
    {
        private readonly long _total;
        private long _remaining;

        public RandomStream(long total) => (_total, _remaining) = (total, total);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _total;
        public override long Position { get => _total - _remaining; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, _remaining);
            RandomNumberGenerator.Fill(buffer.AsSpan(offset, n));
            _remaining -= n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingSink : Stream
    {
        public long Total { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Total; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Total += count;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Total += buffer.Length;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_package_of_large_files_streams_without_holding_it_in_memory()
    {
        var env = await EnvAsync();
        var c = env.Config;
        var m = Itar();
        var part = await c.NewPartAsync("Heavy", "Component", m);
        const long size = 48L * 1024 * 1024;
        var docs = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var d = await c.Docs!.CreateAsync(ConfigEnv.Alice, new CreateDocumentCommand($"Heavy model {i}", "CAD", m, "Synthetic"), new FileUpload($"heavy{i}.bin", "application/octet-stream", new RandomStream(size)), null);
            Assert.True(d.IsOk, string.Join(";", d.Errors));
            docs.Add(d.Value!.Number);
        }
        await LinkAsync(env, part, [.. docs]);
        Assert.True((await c.Parts.SetStateAsync(ConfigEnv.Ada, part, "Released", "load", true, null)).IsOk);

        var prepared = await env.Tdp!.PrepareAsync(ConfigEnv.Alice, new TdpRequest("part", part, null, "tdp-dod"), null);
        Assert.True(prepared.IsOk, string.Join(";", prepared.Errors));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetTotalMemory(true);
        long peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(false));
                await Task.Delay(5);
            }
        });
        var sink = new CountingSink();
        await prepared.Value!.WriteToAsync(sink, CancellationToken.None);
        await stop.CancelAsync();
        await sampler;
        var growthMb = (peak - baseline) / (1024.0 * 1024.0);
        Assert.True(sink.Total > 2 * size, $"the package was only {sink.Total} bytes");
        // The package is more than 96 MB; the managed heap grows by a small fraction of it (buffers and the ZIP bookkeeping), not by the package.
        Assert.True(growthMb < 48, $"managed heap grew by {growthMb:0.0} MB while streaming a {sink.Total / (1024 * 1024)} MB package");
        Console.WriteLine($"TDP streaming: package {sink.Total / (1024 * 1024)} MB, peak managed heap growth {growthMb:0.0} MB");
    }
}
