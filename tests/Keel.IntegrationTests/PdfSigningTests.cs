using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.Parts;
using Keel.Reports;
using Keel.Reports.Signing;
using Keel.Security;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UglyToad.PdfPig;

namespace Keel.IntegrationTests;

/// <summary>Digitally signed release packages (ADR-0051) against the real host, PostgreSQL and vault.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PdfSigningTests(PostgresFixture pg)
{
    private static readonly Subject Alice = WorkflowEnv.Alice;
    private static readonly Subject Ada = WorkflowEnv.Ada;
    private static readonly Subject Lena = WorkflowEnv.Lena;
    private static readonly Subject Boss = WorkflowEnv.User("boss", [Roles.Admin, Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]);
    private static readonly Subject Kim = WorkflowEnv.User("kim", [Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]);

    private sealed record Env(ApiFactory Api, List<string> Files) : IDisposable
    {
        public void Dispose() { Api.Dispose(); foreach (var f in Files) if (File.Exists(f)) File.Delete(f); }
        public PartService Parts => Api.Services.GetRequiredService<PartService>();
        public BomService Bom => Api.Services.GetRequiredService<BomService>();
        public ReleasePackageService Packages => Api.Services.GetRequiredService<ReleasePackageService>();
        public PackageSigningService Signing => Api.Services.GetRequiredService<PackageSigningService>();

        public async Task<string> Part(string name, Markings? m = null)
        {
            var r = await Parts.CreateAsync(Boss, new CreatePartCommand(name, "Component", "EA", "Make", m ?? WorkflowEnv.Open), null);
            Assert.True(r.IsOk, string.Join("; ", r.Errors));
            return r.Value!.Number;
        }

        /// <summary>A released assembly of an ITAR bolt and an open nut; returns the assembly number.</summary>
        public async Task<string> ReleasedAssembly()
        {
            var bolt = await Part("Bolt", WorkflowEnv.Itar());
            var nut = await Part("Nut");
            var r = await Parts.CreateAsync(Boss, new CreatePartCommand("Pod", "Assembly", "EA", "Make", WorkflowEnv.Itar()), null);
            var asm = r.Value!.Number;
            Assert.True((await Parts.CheckOutAsync(Boss, asm, null)).IsOk);
            Assert.True((await Bom.AddUsageAsync(Boss, asm, new AddUsageCommand(bolt, 2m, null, 10, null), null)).IsOk);
            Assert.True((await Bom.AddUsageAsync(Boss, asm, new AddUsageCommand(nut, 2m, null, 20, null), null)).IsOk);
            Assert.True((await Parts.CheckInAsync(Boss, asm, "structure", null)).IsOk);
            foreach (var n in new[] { bolt, nut, asm }) Assert.True((await Parts.SetStateAsync(Boss, n, "Released", "synthetic", true, null)).IsOk);
            return asm;
        }

        /// <summary>The operator's path: a certificate bundle from "their PKI" and a key file, registered through the same service the administrator page uses.</summary>
        public async Task<SigningIdentityView> Identity(string name = "Production signing", bool activate = true)
        {
            var (bundle, keyPem) = DevSigningCertificate.Generate(DateTimeOffset.UtcNow);
            var key = Path.Combine(Path.GetTempPath(), "keel-it-sign-" + Guid.NewGuid().ToString("N") + ".key.pem");
            await File.WriteAllTextAsync(key, keyPem);
            Files.Add(key);
            var added = await Signing.AddIdentityAsync(Ada, new AddSigningIdentity(name, SigningKeySources.PemFile, bundle, key, null, "Approved for release", "Keel integration test"), "corr-add");
            Assert.True(added.IsOk, string.Join("; ", added.Errors));
            if (!activate) return added.Value!;
            var on = await Signing.ActivateAsync(Ada, added.Value!.Id, "corr-on");
            Assert.True(on.IsOk, string.Join("; ", on.Errors));
            return on.Value!;
        }

        public async Task<long> Scalar(string sql)
        {
            await using var conn = new NpgsqlConnection(Api.Database.SuperuserConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<List<AuditRecord>> Audit() => [.. await new AuditQueries(Api.Services.GetRequiredService<NpgsqlDataSource>()).ListAsync(new AuditQuery(Limit: 20000))];

        public async Task<byte[]> Download(Subject who, string number)
        {
            var d = await Packages.DownloadAsync(who, number, null);
            Assert.True(d.IsOk);
            await using var ms = new MemoryStream();
            await d.Value!.Content.CopyToAsync(ms);
            return ms.ToArray();
        }
    }

    private static bool D(AuditRecord r, string key, string value) => r.Payload.Contains($"\"{key}\":\"{value}\"", StringComparison.Ordinal);

    private async Task<Env> NewEnv() => new(await ApiFactory.CreateAsync(pg), []);

    // ---------------------------------------------------------------- administration

    [Fact]
    public async Task Only_an_administrator_manages_identities_and_a_bad_key_a_weak_certificate_or_a_missing_root_is_refused_with_nothing_stored()
    {
        using var e = await NewEnv();
        var (bundle, keyPem) = DevSigningCertificate.Generate(DateTimeOffset.UtcNow);
        var key = Path.Combine(Path.GetTempPath(), "keel-it-sign-" + Guid.NewGuid().ToString("N") + ".key.pem");
        await File.WriteAllTextAsync(key, keyPem);
        e.Files.Add(key);
        AddSigningIdentity Req(string pem, string? keyRef = null, string? pwd = null) => new("Test identity", SigningKeySources.PemFile, pem, keyRef ?? key, pwd, "Approved", "Site");

        Assert.Equal(OutcomeKind.Forbidden, (await e.Signing.AddIdentityAsync(Alice, Req(bundle), null)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await e.Signing.ListIdentitiesAsync(Alice)).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await e.Signing.SetRequiredAsync(Alice, true, null)).Kind);

        // A password is never typed into the form.
        var typed = await e.Signing.AddIdentityAsync(Ada, Req(bundle, pwd: "hunter2"), null);
        Assert.Equal(OutcomeKind.Invalid, typed.Kind);
        Assert.Contains("never typed", typed.Errors[0], StringComparison.Ordinal);
        // A key that belongs to another certificate is caught by the probe.
        var (_, otherKey) = DevSigningCertificate.Generate(DateTimeOffset.UtcNow);
        var wrong = Path.Combine(Path.GetTempPath(), "keel-it-sign-" + Guid.NewGuid().ToString("N") + ".key.pem");
        await File.WriteAllTextAsync(wrong, otherKey);
        e.Files.Add(wrong);
        var mismatch = await e.Signing.AddIdentityAsync(Ada, Req(bundle, wrong), null);
        Assert.Equal(OutcomeKind.Invalid, mismatch.Kind);
        Assert.Contains("cannot be used", mismatch.Errors[0], StringComparison.Ordinal);
        // The key file is missing; not a PEM bundle; no root at the end of the bundle.
        Assert.Equal(OutcomeKind.Invalid, (await e.Signing.AddIdentityAsync(Ada, Req(bundle, "/nonexistent/key.pem"), null)).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await e.Signing.AddIdentityAsync(Ada, Req("not a certificate"), null)).Kind);
        var leafOnly = bundle[..(bundle.IndexOf("-----END CERTIFICATE-----", StringComparison.Ordinal) + 25)];
        var noRoot = await e.Signing.AddIdentityAsync(Ada, Req(leafOnly), null);
        Assert.Equal(OutcomeKind.Invalid, noRoot.Kind);
        Assert.Contains("root", noRoot.Errors[0], StringComparison.Ordinal);
        // A 1024-bit RSA certificate is below the platform policy.
        using var weak = RSA.Create(1024);
        var req = new CertificateRequest("CN=weak", weak, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var weakCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var weakKey = Path.Combine(Path.GetTempPath(), "keel-it-sign-" + Guid.NewGuid().ToString("N") + ".key.pem");
        await File.WriteAllTextAsync(weakKey, weak.ExportPkcs8PrivateKeyPem());
        e.Files.Add(weakKey);
        var weakResult = await e.Signing.AddIdentityAsync(Ada, Req(weakCert.ExportCertificatePem(), weakKey), null);
        Assert.Equal(OutcomeKind.Invalid, weakResult.Kind);
        Assert.Contains("2048", weakResult.Errors[0], StringComparison.Ordinal);
        Assert.Equal(0, await e.Scalar("SELECT count(*) FROM rpt.signing_identity"));
        // This host is not a development host: the generator is off.
        Assert.Equal(OutcomeKind.Forbidden, (await e.Signing.GenerateDevelopmentIdentityAsync(Ada, null, null)).Kind);
        Assert.Equal(0, await e.Scalar("SELECT count(*) FROM rpt.signing_identity"));
    }

    [Fact]
    public async Task Identities_are_activated_one_at_a_time_retired_for_good_never_deleted_and_every_step_is_audited_without_secrets()
    {
        using var e = await NewEnv();
        var first = await e.Identity("First", activate: false);
        var second = await e.Identity("Second", activate: false);
        Assert.Equal("Standby", first.Status);
        Assert.False((await e.Signing.GetPolicyAsync()).ActiveIdentity);
        Assert.True((await e.Signing.ActivateAsync(Ada, first.Id, "c1")).IsOk);
        Assert.True((await e.Signing.ActivateAsync(Ada, second.Id, "c2")).IsOk);
        var list = (await e.Signing.ListIdentitiesAsync(Ada)).Value!;
        Assert.Equal("Standby", list.Single(i => i.Id == first.Id).Status);
        Assert.Equal("Active", list.Single(i => i.Id == second.Id).Status);
        Assert.Equal(1, await e.Scalar("SELECT count(*) FROM rpt.signing_identity WHERE is_active"));
        // Database guards: one active row, no edits of the certificate, no deletes.
        await Assert.ThrowsAnyAsync<PostgresException>(() => e.Api.Database.ExecuteAsSuperuserAsync($"UPDATE rpt.signing_identity SET is_active = true WHERE id = '{first.Id}'"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => e.Api.Database.ExecuteAsSuperuserAsync($"UPDATE rpt.signing_identity SET subject_dn = 'CN=x' WHERE id = '{first.Id}'"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => e.Api.Database.ExecuteAsSuperuserAsync($"DELETE FROM rpt.signing_identity WHERE id = '{first.Id}'"));

        var retired = await e.Signing.RetireAsync(Ada, second.Id, "c3");
        Assert.Equal("Retired", retired.Value!.Status);
        Assert.Equal(OutcomeKind.Conflict, (await e.Signing.ActivateAsync(Ada, second.Id, null)).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await e.Signing.RetireAsync(Ada, second.Id, null)).Kind);
        await Assert.ThrowsAnyAsync<PostgresException>(() => e.Api.Database.ExecuteAsSuperuserAsync($"UPDATE rpt.signing_identity SET retired_at = NULL, is_active = true WHERE id = '{second.Id}'"));
        Assert.False((await e.Signing.GetPolicyAsync()).ActiveIdentity);

        var audit = await e.Audit();
        foreach (var action in new[] { "output.signing.identity_add", "output.signing.identity_activate", "output.signing.identity_retire" })
            Assert.Contains(audit, r => r.Action == action && r.Outcome == AuditOutcome.Success);
        Assert.Contains(audit, r => r.Action == "output.signing.identity_retire" && r.CorrelationId == "c3");
        // Audit details carry thumbprints and names of algorithms, never key material, paths or the certificate text.
        var json = JsonSerializer.Serialize(audit.Where(r => r.Action.StartsWith("output.signing.", StringComparison.Ordinal)).Select(r => r.Payload));
        Assert.DoesNotContain("PRIVATE KEY", json, StringComparison.Ordinal);
        Assert.DoesNotContain("keel-it-sign", json, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN CERTIFICATE", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_development_generator_works_only_where_the_host_allows_it_and_labels_everything_dev_only()
    {
        using var e = await NewEnv();
        var dir = Path.Combine(Path.GetTempPath(), "keel-it-dev-" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new PackageSigningService(e.Api.Services.GetRequiredService<NpgsqlDataSource>(), e.Api.Services.GetRequiredService<IAuditLog>(), TimeProvider.System,
                e.Api.Services.GetRequiredService<Keel.Core.Reports.IReleasePackageSource>(), new SigningOptions { AllowDevelopmentCertificate = true, DevelopmentKeyDirectory = dir });
            Assert.True((await svc.GetPolicyAsync()).DevelopmentCertificatesAllowed);
            Assert.Equal(OutcomeKind.Forbidden, (await svc.GenerateDevelopmentIdentityAsync(Alice, null, null)).Kind);
            var made = await svc.GenerateDevelopmentIdentityAsync(Ada, null, "corr-dev");
            Assert.True(made.IsOk, string.Join("; ", made.Errors));
            Assert.True(made.Value!.IsDevelopment);
            Assert.Contains("DEV ONLY", made.Value.Subject, StringComparison.Ordinal);
            Assert.Contains("DEV ONLY", made.Value.Name, StringComparison.Ordinal);
            Assert.Equal("Standby", made.Value.Status);
            var keyFile = Directory.GetFiles(dir).Single();
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyFile));
            Assert.Contains(await e.Audit(), r => r.Action == "output.signing.dev_generate" && D(r, "development", "true"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ---------------------------------------------------------------- signing and verification

    [Fact]
    public async Task A_package_is_signed_at_generation_with_the_same_banners_stored_immutably_audited_and_verifies_as_valid_until_a_byte_changes()
    {
        using var e = await NewEnv();
        var asm = await e.ReleasedAssembly();
        var before = await e.Packages.GenerateAsync(Alice, "part", asm, "corr-unsigned");
        Assert.True(before.IsOk);
        Assert.Null(before.Value!.SignedBy);

        var identity = await e.Identity();
        var made = await e.Packages.GenerateAsync(Alice, "part", asm, "corr-signed");
        Assert.True(made.IsOk, string.Join("; ", made.Errors));
        var a = made.Value!;
        Assert.Contains("DEV ONLY", a.SignedBy!, StringComparison.Ordinal);
        Assert.NotNull(a.SignedAt);
        Assert.Contains("ITAR", a.Banner, StringComparison.Ordinal);

        // The stored bytes are the signed bytes; the row, the vault object and the recorded hash agree.
        var bytes = await e.Download(Alice, a.Number);
        Assert.Equal(a.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        // The banner and the distribution statement are on every page, twice, exactly as in an unsigned package.
        using (var doc = PdfDocument.Open(bytes))
        using (var unsignedDoc = PdfDocument.Open(await e.Download(Alice, before.Value.Number)))
        {
            Assert.Equal(unsignedDoc.NumberOfPages, doc.NumberOfPages);
            foreach (var page in doc.GetPages())
            {
                Assert.True(Regex.Count(page.Text, "ITAR") >= 2, $"page {page.Number} banner");
                Assert.Contains("DISTRIBUTION STATEMENT D", Regex.Replace(page.Text, @"\s+", " "), StringComparison.Ordinal);
            }
        }
        var row = await e.Scalar($"SELECT count(*) FROM rpt.artifact_signature s JOIN rpt.artifact a ON a.id = s.artifact_id WHERE a.number = '{a.Number}' AND s.thumbprint = '{identity.Thumbprint}'");
        Assert.Equal(1, row);
        Assert.Equal(0, await e.Scalar($"SELECT count(*) FROM rpt.artifact_signature s JOIN rpt.artifact a ON a.id = s.artifact_id WHERE a.number = '{before.Value.Number}'"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => e.Api.Database.ExecuteAsSuperuserAsync("UPDATE rpt.artifact_signature SET reason = 'x'"));
        await Assert.ThrowsAnyAsync<PostgresException>(() => e.Api.Database.ExecuteAsSuperuserAsync("DELETE FROM rpt.artifact_signature"));
        var listed = (await e.Packages.ListAsync(Alice, "part", asm)).Single(x => x.Number == a.Number);
        Assert.NotNull(listed.SignedAt);
        Assert.Contains("DEV ONLY", listed.SignedBy!, StringComparison.Ordinal);

        // Verification: valid; the recorded package is named to someone who may read all of it.
        var ok = await e.Signing.VerifyAsync(Alice, bytes, "corr-verify");
        Assert.Equal(VerificationVerdict.Valid, ok.Report.Verdict);
        Assert.Equal(a.Number, ok.Recorded!.Number);
        Assert.Equal("Approved for release", ok.Report.Signatures[0].Reason);
        Assert.Equal("Keel integration test", ok.Report.Signatures[0].Location);
        // One changed byte, appended bytes, and the unsigned twin are told apart.
        var changed = (byte[])bytes.Clone();
        changed[100] ^= 1;
        var bad = await e.Signing.VerifyAsync(Alice, changed, null);
        Assert.Equal(VerificationVerdict.Tampered, bad.Report.Verdict);
        Assert.Null(bad.Recorded); // a changed file is no longer the package Keel made
        Assert.Equal(VerificationVerdict.Tampered, (await e.Signing.VerifyAsync(Alice, [.. bytes, .. "x"u8.ToArray()], null)).Report.Verdict);
        Assert.Equal(VerificationVerdict.NotSigned, (await e.Signing.VerifyAsync(Alice, await e.Download(Alice, before.Value.Number), null)).Report.Verdict);

        // Retiring the identity does not invalidate what it signed, and a new package then needs no signature unless required.
        Assert.True((await e.Signing.RetireAsync(Ada, identity.Id, null)).IsOk);
        Assert.Equal(VerificationVerdict.Valid, (await e.Signing.VerifyAsync(Alice, bytes, null)).Report.Verdict);
        Assert.Null((await e.Packages.GenerateAsync(Alice, "part", asm, null)).Value!.SignedBy);

        var audit = await e.Audit();
        Assert.Contains(audit, r => r.Action == "output.package.sign" && r.Outcome == AuditOutcome.Success && r.ObjectId == a.Number && r.CorrelationId == "corr-signed" && D(r, "thumbprint", identity.Thumbprint));
        Assert.Contains(audit, r => r.Action == "output.package.generate" && r.ObjectId == a.Number && D(r, "signed", "true"));
        Assert.Contains(audit, r => r.Action == "output.package.verify" && r.CorrelationId == "corr-verify" && D(r, "verdict", "valid"));
        Assert.Contains(audit, r => r.Action == "output.package.verify" && D(r, "verdict", "tampered"));
    }

    [Fact]
    public async Task When_signing_is_required_or_the_active_key_fails_the_package_is_refused_and_never_made_unsigned()
    {
        using var e = await NewEnv();
        var asm = await e.ReleasedAssembly();
        Assert.True((await e.Signing.SetRequiredAsync(Ada, true, "corr-req")).IsOk);
        var none = await e.Packages.GenerateAsync(Alice, "part", asm, "corr-none");
        Assert.Equal(OutcomeKind.Unavailable, none.Kind);
        Assert.Contains("no signing identity is active", none.Errors[0], StringComparison.Ordinal);
        Assert.Equal(0, await e.Scalar("SELECT count(*) FROM rpt.artifact"));
        Assert.Contains(await e.Audit(), r => r.Action == "output.package.sign" && r.Outcome == AuditOutcome.Failure && r.CorrelationId == "corr-none");

        var identity = await e.Identity();
        Assert.True((await e.Packages.GenerateAsync(Alice, "part", asm, null)).IsOk);
        // The key file disappears: even with signing not required, an active identity means "sign", so the package is refused.
        Assert.True((await e.Signing.SetRequiredAsync(Ada, false, null)).IsOk);
        var keyFile = Directory.GetFiles(Path.GetTempPath(), "keel-it-sign-*.key.pem").Where(f => e.Files.Contains(f)).First();
        File.Delete(keyFile);
        var failed = await e.Packages.GenerateAsync(Alice, "part", asm, "corr-nokey");
        Assert.Equal(OutcomeKind.Unavailable, failed.Kind);
        Assert.Contains("cannot be used", failed.Errors[0], StringComparison.Ordinal);
        Assert.DoesNotContain("keel-it-sign", failed.Errors[0], StringComparison.Ordinal); // no path in the message
        Assert.Equal(1, await e.Scalar("SELECT count(*) FROM rpt.artifact"));
        Assert.Contains(await e.Audit(), r => r.Action == "output.signing.policy_set" && D(r, "required", "true"));
        Assert.True(identity.IsActive);
    }

    private static readonly Subject[] Everyone = [Alice, Ada, Lena, WorkflowEnv.Mallory, WorkflowEnv.Audrey, Kim, WorkflowEnv.Farid, Boss];

    [Fact]
    public async Task Verification_names_a_package_only_to_those_who_may_read_everything_in_it_and_matches_the_policy_oracle_for_every_subject()
    {
        using var e = await NewEnv();
        await e.Identity();
        var bolt = await e.Part("Bolt", WorkflowEnv.Itar());
        var nut = await e.Part("Nut");
        var r = await e.Parts.CreateAsync(Boss, new CreatePartCommand("Pod", "Assembly", "EA", "Make", WorkflowEnv.Open), null);
        var asm = r.Value!.Number;
        Assert.True((await e.Parts.CheckOutAsync(Boss, asm, null)).IsOk);
        Assert.True((await e.Bom.AddUsageAsync(Boss, asm, new AddUsageCommand(bolt, 1m, null, 10, null), null)).IsOk);
        Assert.True((await e.Bom.AddUsageAsync(Boss, asm, new AddUsageCommand(nut, 1m, null, 20, null), null)).IsOk);
        Assert.True((await e.Parts.CheckInAsync(Boss, asm, "s", null)).IsOk);
        foreach (var n in new[] { bolt, nut, asm }) Assert.True((await e.Parts.SetStateAsync(Boss, n, "Released", "synthetic", true, null)).IsOk);
        var made = await e.Packages.GenerateAsync(Boss, "part", asm, null);
        Assert.True(made.IsOk, string.Join("; ", made.Errors));
        var bytes = await e.Download(Boss, made.Value!.Number);
        var marked = new[] { WorkflowEnv.Itar(), WorkflowEnv.Open, WorkflowEnv.Open };
        var granted = 0;
        foreach (var subject in Everyone)
        {
            var expected = AccessPolicy.HasRoleFor(subject, PlmAction.Read) && marked.All(m => AccessPolicy.Evaluate(subject, m, PlmAction.Read).Permitted);
            var result = await e.Signing.VerifyAsync(subject, bytes, null);
            // The cryptographic result is the same for everyone (it is about the file in the caller's own hands) ...
            Assert.Equal(VerificationVerdict.Valid, result.Report.Verdict);
            // ... the Keel record (number, subject, author, banner) appears exactly when the policy lets the caller read every object in the package.
            Assert.True(expected == (result.Recorded is not null), $"{subject.Username}: expected recorded={expected}");
            if (expected) granted++;
        }
        Assert.InRange(granted, 1, Everyone.Length - 1);
        // The audit entry of a verification by someone who may not read the package names no package.
        var denied = (await e.Audit()).Where(a => a.Action == "output.package.verify" && a.Actor == Lena.Id).ToList();
        Assert.NotEmpty(denied);
        Assert.All(denied, a => Assert.DoesNotContain(made.Value.Number, a.ObjectId ?? "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_http_surface_verifies_uploads_for_every_role_and_keeps_identity_management_to_administrators()
    {
        using var e = await NewEnv();
        await e.Identity();
        var asm = await e.ReleasedAssembly();
        var made = await e.Packages.GenerateAsync(Boss, "part", asm, null);
        var bytes = await e.Download(Boss, made.Value!.Number);

        var viewer = e.Api.ClientFor(ApiFactory.Token(sub: "u-vic", roles: ["plm-viewer"]));
        var res = await viewer.PostAsync("/api/v1/output/packages/verify", new ByteArrayContent(bytes));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("valid", json.GetProperty("report").GetProperty("verdict").GetString());
        var tampered = (byte[])bytes.Clone();
        tampered[64] ^= 0x20;
        var bad = JsonDocument.Parse(await (await viewer.PostAsync("/api/v1/output/packages/verify", new ByteArrayContent(tampered))).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("tampered", bad.GetProperty("report").GetProperty("verdict").GetString());
        var junk = JsonDocument.Parse(await (await viewer.PostAsync("/api/v1/output/packages/verify", new ByteArrayContent("not a pdf"u8.ToArray()))).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("unreadable", junk.GetProperty("report").GetProperty("verdict").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/output/signing/identities")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync("/api/v1/output/signing/policy", new { required = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/output/signing/identities/development", new { name = "x" })).StatusCode);
        var anon = e.Api.CreateClient();
        Assert.True((await anon.PostAsync("/api/v1/output/packages/verify", new ByteArrayContent(bytes))).StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Found);

        var admin = e.Api.ClientFor(ApiFactory.Token(sub: "u-ada", roles: ["plm-admin", "plm-viewer", "plm-author"]));
        var ids = await admin.GetAsync("/api/v1/output/signing/identities");
        Assert.Equal(HttpStatusCode.OK, ids.StatusCode);
        var body = await ids.Content.ReadAsStringAsync();
        Assert.DoesNotContain("PRIVATE KEY", body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/output/signing/policy", new { required = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/v1/output/signing/identities/development", new { name = "x" })).StatusCode); // not a development host
    }
}
