using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Keel.Core;
using Keel.Integration;
using Keel.Security;
using Keel.TestSupport;

namespace Keel.UnitTests;

public sealed class OutboundReleaseTests
{
    private static ImmutableSortedSet<string> S(params string[] v) => v.ToImmutableSortedSet(StringComparer.Ordinal);

    private static ClearanceProfile Profile(RecipientCategory category = RecipientCategory.DodContractor, bool us = true, string[]? jurisdictions = null, string[]? cui = null,
        string[]? programs = null, string[]? statements = null, string[]? auths = null, string[]? eccns = null, string[]? usml = null) => new("p", category, us,
        S(jurisdictions ?? ["None", "Ear", "Itar"]), S(eccns ?? []), S(usml ?? []), S(cui ?? ["EXPT", "CTI"]), S(programs ?? ["ORCA"]),
        (statements ?? ["A", "B", "C", "D", "E"]).Select(Enum.Parse<DistributionStatement>).ToImmutableSortedSet(), S(auths ?? []));

    private static Markings Itar(DistributionStatement d = DistributionStatement.D, string? program = "ORCA", string[]? auths = null, string usml = "XI(d)") =>
        Markings.Create(ExportJurisdiction.Itar, d, "ENG", usmlCategory: usml, cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology",
            program: program, exportAuthorizations: auths).Value;

    private static Markings Open => Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;

    [Fact]
    public void A_cleared_recipient_gets_itar_data_with_matching_statement() =>
        Assert.True(OutboundRelease.Evaluate(Profile(), Itar()).Permitted);

    [Theory]
    // statement, category, expected (no authorization involved)
    [InlineData("A", RecipientCategory.Public, true)]
    [InlineData("A", RecipientCategory.Foreign, true)]
    [InlineData("B", RecipientCategory.Usg, true)]
    [InlineData("B", RecipientCategory.Dod, true)]
    [InlineData("B", RecipientCategory.UsgContractor, false)]
    [InlineData("B", RecipientCategory.DodContractor, false)]
    [InlineData("B", RecipientCategory.Foreign, false)]
    [InlineData("C", RecipientCategory.UsgContractor, true)]
    [InlineData("C", RecipientCategory.DodContractor, true)]
    [InlineData("C", RecipientCategory.Public, false)]
    [InlineData("C", RecipientCategory.Foreign, false)]
    [InlineData("D", RecipientCategory.Dod, true)]
    [InlineData("D", RecipientCategory.DodContractor, true)]
    [InlineData("D", RecipientCategory.Usg, false)]
    [InlineData("D", RecipientCategory.UsgContractor, false)]
    [InlineData("E", RecipientCategory.Dod, true)]
    [InlineData("E", RecipientCategory.DodContractor, false)]
    [InlineData("E", RecipientCategory.Usg, false)]
    [InlineData("F", RecipientCategory.Dod, false)]
    [InlineData("F", RecipientCategory.Usg, false)]
    public void Distribution_statements_follow_DoDI_5230_24(string statement, RecipientCategory category, bool expected) =>
        Assert.Equal(expected, OutboundRelease.StatementAllowsCategory(Enum.Parse<DistributionStatement>(statement), category));

    [Fact]
    public void Statement_F_is_never_released_automatically_even_when_everything_else_matches()
    {
        var f = Markings.Create(ExportJurisdiction.None, DistributionStatement.F, "ENG", controllingOffice: "ACME", distributionReason: "Program need to know", program: "ORCA").Value;
        // The profile cannot even list F; evaluation refuses regardless.
        var d = OutboundRelease.Evaluate(Profile(RecipientCategory.Dod), f);
        Assert.False(d.Permitted);
        Assert.Contains(OutboundRelease.DistributionFManual, d.Reasons);
    }

    [Fact]
    public void A_profile_listing_statement_F_is_invalid() =>
        Assert.Contains((Profile() with { AllowedStatements = [DistributionStatement.F] }).Validate(), e => e.Contains("statement F", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Itar_to_a_recipient_that_is_not_us_persons_only_is_refused_without_a_listed_authorization()
    {
        var foreign = Profile(RecipientCategory.Foreign, us: false, auths: ["TAA-0042"]);
        var d = OutboundRelease.Evaluate(foreign, Itar());
        Assert.False(d.Permitted);
        Assert.Contains(OutboundRelease.UsPersonRequired, d.Reasons);
    }

    [Fact]
    public void A_foreign_recipient_with_the_authorization_listed_on_the_object_is_cleared()
    {
        var foreign = Profile(RecipientCategory.Foreign, us: false, auths: ["TAA-0042"]);
        Assert.True(OutboundRelease.Evaluate(foreign, Itar(auths: ["TAA-0042"])).Permitted);
        // An authorization the recipient does not hold does not help, nor does one it holds that the object does not list.
        Assert.False(OutboundRelease.Evaluate(foreign, Itar(auths: ["TAA-9999"])).Permitted);
    }

    [Fact]
    public void Every_cui_category_the_object_carries_must_be_allowed()
    {
        var m = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT", "PROPIN"], controllingOffice: "ACME",
            distributionReason: "r", program: "ORCA").Value;
        var d = OutboundRelease.Evaluate(Profile(), m);
        Assert.Contains($"{OutboundRelease.CuiCategoryNotAllowed}:PROPIN", d.Reasons);
        Assert.DoesNotContain(d.Reasons, r => r.EndsWith(":EXPT", StringComparison.Ordinal));
    }

    [Fact]
    public void Programs_jurisdictions_eccns_and_usml_categories_are_enforced()
    {
        Assert.Contains(OutboundRelease.ProgramNotAllowed, OutboundRelease.Evaluate(Profile(programs: ["KRAKEN"]), Itar()).Reasons);
        Assert.True(OutboundRelease.Evaluate(Profile(programs: []), Open).Permitted); // data without a program needs none
        Assert.Contains($"{OutboundRelease.JurisdictionNotAllowed}:Itar", OutboundRelease.Evaluate(Profile(jurisdictions: ["None", "Ear"]), Itar()).Reasons);
        Assert.Contains(OutboundRelease.UsmlNotAllowed, OutboundRelease.Evaluate(Profile(usml: ["VIII*"]), Itar(usml: "XI(d)")).Reasons);
        Assert.True(OutboundRelease.Evaluate(Profile(usml: ["XI*"]), Itar(usml: "XI(d)")).Permitted);
        var ear = Markings.Create(ExportJurisdiction.Ear, DistributionStatement.C, "ENG", eccn: "9A610", cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "r").Value;
        Assert.Contains(OutboundRelease.EccnNotAllowed, OutboundRelease.Evaluate(Profile(eccns: ["3A*"]), ear).Reasons);
        Assert.True(OutboundRelease.Evaluate(Profile(eccns: ["9A*"]), ear).Permitted);
        var ear99 = Markings.Create(ExportJurisdiction.Ear, DistributionStatement.A, "ENG", eccn: "EAR99").Value;
        Assert.True(OutboundRelease.Evaluate(Profile(eccns: ["3A*"], cui: []), ear99).Permitted);
    }

    [Fact]
    public void Public_data_goes_to_a_public_recipient_and_nothing_else_does()
    {
        var pub = Profile(RecipientCategory.Public, us: false, jurisdictions: ["None"], cui: [], programs: [], statements: ["A"]);
        Assert.True(OutboundRelease.Evaluate(pub, Open).Permitted);
        Assert.False(OutboundRelease.Evaluate(pub, Itar()).Permitted);
        Assert.Empty(pub.Validate());
        Assert.NotEmpty((pub with { AllowedStatements = [DistributionStatement.B] }).Validate());
    }

    /// <summary>
    /// Property-style: for thousands of random profile and marking combinations the decision equals an independent oracle written as a plain
    /// truth table, and a decision with no reasons is permitted (the result never carries a reason and a permit, nor neither).
    /// </summary>
    [Fact]
    public void The_decision_agrees_with_an_independent_oracle_for_random_combinations()
    {
        var rnd = new Random(5230);
        var cuiAll = new[] { "EXPT", "CTI", "PROPIN", "PRVCY" };
        var programs = new[] { "ORCA", "KRAKEN", "NARWHAL" };
        var categories = Enum.GetValues<RecipientCategory>();
        var statements = Enum.GetValues<DistributionStatement>();
        var checkedCases = 0;
        for (var i = 0; i < 6000; i++)
        {
            var jur = (ExportJurisdiction)rnd.Next(3);
            var stmt = statements[rnd.Next(statements.Length)];
            var program = rnd.Next(3) == 0 ? null : programs[rnd.Next(programs.Length)];
            var auths = rnd.Next(2) == 0 ? new[] { "TAA-1" } : Array.Empty<string>();
            var cui = cuiAll.Where(_ => rnd.Next(2) == 0).ToList();
            var eccn = jur == ExportJurisdiction.Ear ? (rnd.Next(2) == 0 ? "EAR99" : "9A610") : null;
            var controlled = jur == ExportJurisdiction.Itar || (jur == ExportJurisdiction.Ear && eccn != "EAR99");
            if (controlled && !cui.Contains("EXPT")) cui.Add("EXPT");
            var created = Markings.Create(jur, stmt, "ENG", eccn, jur == ExportJurisdiction.Itar ? "XI(d)" : null, cui, stmt == DistributionStatement.A ? null : "ACME", stmt == DistributionStatement.A ? null : "r",
                stmt == DistributionStatement.F ? program ?? "ORCA" : program, jur == ExportJurisdiction.None ? [] : auths);
            if (!created.IsSuccess) continue; // not a valid marking (for example controlled data under statement A)
            var m = created.Value;

            var category = categories[rnd.Next(categories.Length)];
            var us = rnd.Next(2) == 0;
            var profile = new ClearanceProfile("p", category, us, S(new[] { "None", "Ear", "Itar" }.Where(_ => rnd.Next(3) > 0).ToArray()), S(), S(),
                S(cuiAll.Where(_ => rnd.Next(3) > 0).ToArray()), S(programs.Where(_ => rnd.Next(2) == 0).ToArray()),
                statements.Where(s => s != DistributionStatement.F && rnd.Next(3) > 0).ToImmutableSortedSet(), S(rnd.Next(2) == 0 ? new[] { "TAA-1" } : Array.Empty<string>()));

            var holds = profile.ExportAuthorizations.Contains("TAA-1") && m.ExportAuthorizations.Contains("TAA-1");
            var statementOk = m.DistributionStatement switch
            {
                DistributionStatement.A => true,
                DistributionStatement.F => false,
                var s => category switch
                {
                    RecipientCategory.Foreign => holds,
                    RecipientCategory.Dod => true,
                    RecipientCategory.Usg => s is DistributionStatement.B or DistributionStatement.C,
                    RecipientCategory.UsgContractor => s == DistributionStatement.C,
                    RecipientCategory.DodContractor => s is DistributionStatement.C or DistributionStatement.D,
                    _ => false,
                },
            };
            var expected = statementOk && m.DistributionStatement != DistributionStatement.F && profile.AllowedStatements.Contains(m.DistributionStatement)
                && (!controlled || us || holds)
                && profile.AllowedJurisdictions.Contains(m.ExportJurisdiction.ToString())
                && m.CuiCategories.All(profile.AllowedCuiCategories.Contains)
                && (m.Program is null || profile.AllowedPrograms.Contains(m.Program));

            var decision = OutboundRelease.Evaluate(profile, m);
            Assert.Equal(expected, decision.Permitted);
            Assert.Equal(decision.Permitted, decision.Reasons.IsEmpty);
            checkedCases++;
        }
        Assert.True(checkedCases > 1000, $"only {checkedCases} valid combinations were generated");
    }

    [Fact]
    public void Reason_codes_have_readable_descriptions_that_do_not_name_the_object()
    {
        foreach (var code in new[] { OutboundRelease.DistributionFManual, OutboundRelease.UsPersonRequired, $"{OutboundRelease.CuiCategoryNotAllowed}:CTI", OutboundRelease.ProgramNotAllowed })
            Assert.NotEqual(code, OutboundRelease.Describe(code));
    }
}

public sealed class WebhookSignatureTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"id":"x","data":{"n":1}}""");

    [Fact]
    public void A_signature_verifies_with_the_secret_and_matches_the_documented_openssl_computation()
    {
        var header = WebhookSignature.Header(["s3cret"], 1_700_000_000, Body);
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("s3cret"), Encoding.UTF8.GetBytes("1700000000." + Encoding.UTF8.GetString(Body))));
        Assert.Equal($"v1={expected}", header);
        Assert.True(WebhookSignature.Verify(["s3cret"], header, 1_700_000_000, Body, DateTimeOffset.FromUnixTimeSeconds(1_700_000_010), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Tampering_a_wrong_secret_or_an_old_timestamp_fails()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_010);
        var header = WebhookSignature.Header(["s3cret"], 1_700_000_000, Body);
        Assert.False(WebhookSignature.Verify(["s3cret"], header, 1_700_000_000, Encoding.UTF8.GetBytes("""{"id":"x","data":{"n":2}}"""), now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify(["other"], header, 1_700_000_000, Body, now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify(["s3cret"], header, 1_700_000_001, Body, now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify(["s3cret"], header, 1_700_000_000, Body, now.AddHours(1), TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify(["s3cret"], null, 1_700_000_000, Body, now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void During_rotation_the_header_carries_both_signatures_and_either_secret_verifies()
    {
        var header = WebhookSignature.Header(["new", "old"], 1_700_000_000, Body);
        Assert.Equal(2, header.Split(',').Length);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        Assert.True(WebhookSignature.Verify(["old"], header, 1_700_000_000, Body, now, TimeSpan.FromMinutes(5)));
        Assert.True(WebhookSignature.Verify(["new"], header, 1_700_000_000, Body, now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify(["neither"], header, 1_700_000_000, Body, now, TimeSpan.FromMinutes(5)));
    }
}

public sealed class DeliveryRulesTests
{
    [Fact]
    public void Backoff_grows_exponentially_is_capped_and_jittered_within_bounds()
    {
        var b = TimeSpan.FromSeconds(5);
        var max = TimeSpan.FromMinutes(10);
        for (var attempts = 1; attempts <= 40; attempts++)
        {
            var cap = Math.Min(5 * Math.Pow(2, Math.Min(attempts - 1, 30)), max.TotalSeconds);
            foreach (var r in new[] { 0.0, 0.25, 0.5, 0.999 })
            {
                var d = Backoff.Delay(attempts, b, max, r).TotalSeconds;
                Assert.InRange(d, cap * 0.5 - 1e-6, cap + 1e-6);
            }
            Assert.True(Backoff.Delay(attempts, b, max, 0.1) <= Backoff.Delay(attempts, b, max, 0.9));
        }
        Assert.Equal(2.5, Backoff.Delay(1, b, max, 0).TotalSeconds, 6);
        Assert.True(Backoff.Delay(3, b, max, 0.5) > Backoff.Delay(2, b, max, 0.5));
    }

    [Theory]
    [InlineData(200, DeliveryResult.Delivered)]
    [InlineData(204, DeliveryResult.Delivered)]
    [InlineData(301, DeliveryResult.Permanent)]
    [InlineData(302, DeliveryResult.Permanent)]
    [InlineData(400, DeliveryResult.Permanent)]
    [InlineData(401, DeliveryResult.Permanent)]
    [InlineData(404, DeliveryResult.Permanent)]
    [InlineData(408, DeliveryResult.Retry)]
    [InlineData(410, DeliveryResult.Permanent)]
    [InlineData(422, DeliveryResult.Permanent)]
    [InlineData(429, DeliveryResult.Retry)]
    [InlineData(500, DeliveryResult.Retry)]
    [InlineData(503, DeliveryResult.Retry)]
    public void Response_codes_classify_as_documented(int status, DeliveryResult expected) =>
        Assert.Equal(expected, WebhookSink.Classify((HttpStatusCode)status).Result);
}

public sealed class SsrfGuardTests
{
    private static SsrfGuard Guard(string[]? hosts = null, string[]? networks = null, bool dangerous = false) =>
        new(new IntegrationOptions { AllowedHosts = hosts ?? [], AllowedNetworks = networks ?? [], AllowDangerousAddresses = dangerous });

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.1.1")]
    [InlineData("fe80::1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Loopback_link_local_and_metadata_addresses_are_dangerous(string address) => Assert.True(SsrfGuard.IsDangerous(IPAddress.Parse(address)));

    [Theory]
    [InlineData("10.20.30.40")]
    [InlineData("192.168.1.5")]
    [InlineData("172.16.0.9")]
    public void Private_site_addresses_are_not_dangerous_but_still_need_an_allow_list_entry(string address)
    {
        Assert.False(SsrfGuard.IsDangerous(IPAddress.Parse(address)));
        Assert.NotNull(Guard().CheckAddress("erp.site", IPAddress.Parse(address))); // default deny
        Assert.Null(Guard(networks: ["10.0.0.0/8", "192.168.0.0/16", "172.16.0.0/12"]).CheckAddress("erp.site", IPAddress.Parse(address)));
    }

    [Fact]
    public void Urls_are_checked_against_scheme_credentials_and_the_allow_list()
    {
        var g = Guard(hosts: ["erp.site.example", "*.erp.internal"], networks: ["10.20.0.0/16"]);
        Assert.Empty(g.CheckUrl("https://erp.site.example:8443/keel"));
        Assert.Empty(g.CheckUrl("https://hooks.erp.internal/x"));
        Assert.Empty(g.CheckUrl("https://10.20.5.5/x"));
        Assert.NotEmpty(g.CheckUrl("http://erp.site.example/keel"));
        Assert.NotEmpty(g.CheckUrl("https://user:pw@erp.site.example/keel"));
        Assert.NotEmpty(g.CheckUrl("https://10.99.5.5/x"));
        Assert.NotEmpty(g.CheckUrl("https://evil.example/x"));
        Assert.NotEmpty(g.CheckUrl("https://erp.internal/x")); // the wildcard needs a label in front
        Assert.NotEmpty(g.CheckUrl("not a url"));
        Assert.NotEmpty(Guard().CheckUrl("https://erp.site.example/keel")); // default deny: nothing configured
    }

    [Fact]
    public void Metadata_and_loopback_are_refused_even_when_listed_unless_the_development_switch_is_on()
    {
        Assert.NotEmpty(Guard(networks: ["169.254.0.0/16"]).CheckUrl("https://169.254.169.254/latest/meta-data"));
        Assert.NotNull(Guard(hosts: ["localhost"]).CheckAddress("localhost", IPAddress.Loopback));
        Assert.Null(Guard(hosts: ["localhost"], dangerous: true).CheckAddress("localhost", IPAddress.Loopback));
    }
}

public sealed class SinkConfigurationTests
{
    private static (WebhookSink Webhook, FileDropSink FileDrop, string Root) Sinks(Action<IntegrationOptions>? configure = null)
    {
        var root = Directory.CreateTempSubdirectory("keel-drop-").FullName;
        var o = new IntegrationOptions
        {
            AllowedHosts = ["erp.site.example"], FileDropRoot = root,
            Secrets = { ["erp-main"] = "s1", ["erp-next"] = "s2" },
        };
        configure?.Invoke(o);
        return (new WebhookSink(o, new SecretResolver(o), new SsrfGuard(o), TimeProvider.System), new FileDropSink(o, TimeProvider.System), root);
    }

    private static JsonObject Config(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void A_valid_webhook_configuration_has_no_errors()
    {
        var (w, _, _) = Sinks();
        Assert.Empty(w.ValidateConfig(Config("""{"url":"https://erp.site.example/keel","secretName":"erp-main","secondarySecretName":"erp-next","timeoutSeconds":5}"""), enabled: true));
    }

    [Fact]
    public void Webhook_problems_are_reported_as_messages()
    {
        var (w, _, _) = Sinks();
        Assert.Contains(w.ValidateConfig(Config("""{"url":"https://evil.example/x","secretName":"erp-main"}"""), true), e => e.Contains("allowed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(w.ValidateConfig(Config("""{"url":"https://erp.site.example/x","secretName":"missing"}"""), true), e => e.Contains("not provisioned", StringComparison.Ordinal));
        Assert.Empty(w.ValidateConfig(Config("""{"url":"https://erp.site.example/x","secretName":"missing"}"""), false)); // a disabled sink may be prepared before the secret is mounted
        Assert.Contains(w.ValidateConfig(Config("""{"url":"https://erp.site.example/x","secretName":"bad name!"}"""), false), e => e.Contains("secretName", StringComparison.Ordinal));
        Assert.Contains(w.ValidateConfig(Config("""{"url":"https://erp.site.example/x","secretName":"erp-main","secondarySecretName":"erp-main"}"""), true), e => e.Contains("differ", StringComparison.Ordinal));
        Assert.Contains(w.ValidateConfig(Config("""{"url":"https://erp.site.example/x","secretName":"erp-main","password":"x"}"""), true), e => e.Contains("Unknown setting", StringComparison.Ordinal));
        Assert.Contains(w.ValidateConfig(Config("""{"url":"https://erp.site.example/x","secretName":"erp-main","timeoutSeconds":0}"""), true), e => e.Contains("timeoutSeconds", StringComparison.Ordinal));
    }

    [Fact]
    public void File_drop_directories_cannot_escape_the_root()
    {
        var (_, f, root) = Sinks();
        Assert.Empty(f.ValidateConfig(Config("""{"directory":"outbound/erp-a","format":"ndjson"}"""), enabled: true));
        Assert.True(Directory.Exists(Path.Combine(root, "outbound", "erp-a")));
        foreach (var bad in new[] { "../outside", "/etc", "a/../../b", "a\\b" })
            Assert.NotEmpty(f.ValidateConfig(Config($$"""{"directory":"{{bad.Replace("\\", "\\\\", StringComparison.Ordinal)}}"}"""), true));
        Assert.NotEmpty(f.ValidateConfig(Config("""{"directory":"x","format":"xml"}"""), true));
        var (_, noRoot, _) = Sinks(o => o.FileDropRoot = "");
        Assert.Contains(noRoot.ValidateConfig(Config("""{"directory":"x"}"""), true), e => e.Contains("FileDropRoot", StringComparison.Ordinal));
    }

    [Fact]
    public void A_broker_sink_throws_NotSupportedException_naming_phase_4b()
    {
        var broker = new BrokerSink();
        var e1 = Assert.Throws<NotSupportedException>(() => broker.ValidateConfig(new JsonObject(), true));
        Assert.Contains("Phase 4b", e1.Message, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => { _ = broker.DeliverAsync(null!, null!, CancellationToken.None); });
    }
}

public sealed class FileDropSinkTests
{
    private static SinkRecord Sink(string directory, string format) => new(Guid.NewGuid(), "drop", "Drop", "filedrop", true, Guid.NewGuid(), "p",
        new JsonObject { ["directory"] = directory, ["format"] = format }, [], false, 1);

    private static EnvelopeMessage Message(long seq) => new(Guid.CreateVersion7(), "part.released", seq, DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes($$"""{"seq":{{seq}}}"""), 1);

    [Fact]
    public async Task Each_event_becomes_a_file_with_a_manifest_holding_its_sha256_and_no_temporary_files_remain()
    {
        var root = Directory.CreateTempSubdirectory("keel-drop-").FullName;
        var sink = new FileDropSink(new IntegrationOptions { FileDropRoot = root }, TimeProvider.System);
        var messages = new[] { Message(1), Message(2) };
        var results = await sink.DeliverBatchAsync(Sink("out", "json"), messages, CancellationToken.None);
        Assert.All(results, r => Assert.Equal(DeliveryResult.Delivered, r.Result));
        var files = Directory.GetFiles(Path.Combine(root, "out"));
        Assert.DoesNotContain(files, f => f.EndsWith(".tmp", StringComparison.Ordinal));
        var manifests = files.Where(f => f.EndsWith(".manifest.json", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, manifests.Count);
        foreach (var manifestPath in manifests)
        {
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
            var dataPath = Path.Combine(root, "out", manifest["file"]!.GetValue<string>());
            var bytes = await File.ReadAllBytesAsync(dataPath);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), manifest["sha256"]!.GetValue<string>());
            Assert.Equal(bytes.Length, manifest["sizeBytes"]!.GetValue<int>());
            Assert.Single(manifest["events"]!.AsArray());
        }
    }

    [Fact]
    public async Task Ndjson_writes_one_file_per_batch_with_one_line_per_event()
    {
        var root = Directory.CreateTempSubdirectory("keel-drop-").FullName;
        var sink = new FileDropSink(new IntegrationOptions { FileDropRoot = root }, TimeProvider.System);
        var results = await sink.DeliverBatchAsync(Sink("batch", "ndjson"), [Message(1), Message(2), Message(3)], CancellationToken.None);
        Assert.All(results, r => Assert.Equal(DeliveryResult.Delivered, r.Result));
        var data = Directory.GetFiles(Path.Combine(root, "batch"), "*.ndjson").Single();
        Assert.Equal(3, (await File.ReadAllLinesAsync(data)).Length);
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(data + ".manifest.json"))!;
        Assert.Equal(3, manifest["events"]!.AsArray().Count);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(data))), manifest["sha256"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_directory_outside_the_root_is_a_permanent_failure_and_writes_nothing()
    {
        var root = Directory.CreateTempSubdirectory("keel-drop-").FullName;
        var sink = new FileDropSink(new IntegrationOptions { FileDropRoot = root }, TimeProvider.System);
        var results = await sink.DeliverBatchAsync(Sink("../escape", "json"), [Message(1)], CancellationToken.None);
        Assert.Equal(DeliveryResult.Permanent, results[0].Result);
        Assert.False(Directory.Exists(Path.Combine(root, "..", "escape")));
    }
}

public sealed class EnvelopeTests
{
    [Fact]
    public void Event_types_are_versioned_and_the_default_subscription_excludes_the_structure_event()
    {
        Assert.All(EventTypes.All, t => Assert.Equal(1, EventTypes.CurrentVersion(t)));
        Assert.DoesNotContain(EventTypes.BomStructurePublished, EventTypes.Default);
        Assert.Contains(EventTypes.MbomReleased, EventTypes.Default); // added by adding a type here, a schema and an emission point (FUP-1)
        Assert.Throws<ArgumentException>(() => EventTypes.CurrentVersion("mbom.unreleased"));
    }

    [Fact]
    public void The_envelope_body_is_stable_and_carries_markings_and_data()
    {
        var markings = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "r", program: "ORCA").Value;
        var row = new OutboxRow(Guid.CreateVersion7(), 7, "part.released", 1, "part", Guid.NewGuid(), "P-000001", "A", DateTimeOffset.UtcNow, """{"number":"P-000001"}""", markings, "corr", "alice");
        var a = Envelope.Body(row);
        Assert.Equal(a, Envelope.Body(row));
        var json = JsonNode.Parse(a)!;
        Assert.Equal("keel-plm", json["source"]!.GetValue<string>());
        Assert.Equal("D", json["markings"]!["distributionStatement"]!.GetValue<string>());
        Assert.Equal("P-000001", json["data"]!["number"]!.GetValue<string>());
        Assert.Equal(row.Id.ToString("D"), json["id"]!.GetValue<string>());
    }
}

public sealed class JsonSchemaLiteTests
{
    private static IReadOnlyList<string> Check(string schema, string instance) => JsonSchemaLite.Validate(JsonNode.Parse(instance), JsonNode.Parse(schema)!);

    [Fact]
    public void The_validator_accepts_valid_and_rejects_invalid_instances()
    {
        const string schema = """{"type":"object","required":["a"],"additionalProperties":false,"properties":{"a":{"type":"integer","minimum":1},"b":{"type":["string","null"],"pattern":"^x"},"c":{"type":"array","items":{"enum":["p","q"]}},"d":{"const":true}}}""";
        Assert.Empty(Check(schema, """{"a":1,"b":null,"c":["p"],"d":true}"""));
        Assert.NotEmpty(Check(schema, """{}"""));
        Assert.NotEmpty(Check(schema, """{"a":0}"""));
        Assert.NotEmpty(Check(schema, """{"a":1.5}"""));
        Assert.NotEmpty(Check(schema, """{"a":1,"z":1}"""));
        Assert.NotEmpty(Check(schema, """{"a":1,"b":"y"}"""));
        Assert.NotEmpty(Check(schema, """{"a":1,"c":["r"]}"""));
        Assert.NotEmpty(Check(schema, """{"a":1,"d":false}"""));
        Assert.NotEmpty(Check(schema, """[1]"""));
    }

    [Fact]
    public void OneOf_requires_exactly_one_match_and_unknown_keywords_fail_loudly()
    {
        const string one = """{"oneOf":[{"type":"integer"},{"type":"number","minimum":10}]}""";
        Assert.Empty(Check(one, "5"));
        Assert.NotEmpty(Check(one, "12")); // matches both
        Assert.NotEmpty(Check(one, """ "s" """));
        Assert.NotEmpty(Check("""{"type":"object","patternProperties":{}}""", "{}"));
    }

    [Fact]
    public void The_committed_event_schemas_are_valid_documents_for_every_event_type()
    {
        var dir = Path.Combine(RepoRoot(), "docs", "integration", "schemas");
        Assert.True(File.Exists(Path.Combine(dir, "envelope.v1.json")));
        foreach (var type in EventTypes.All)
        {
            var file = Path.Combine(dir, $"{type}.v{EventTypes.CurrentVersion(type)}.json");
            Assert.True(File.Exists(file), $"missing schema {file}");
            var schema = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema["$schema"]!.GetValue<string>());
            // A schema must reject an empty payload (it requires something) and use only supported keywords.
            Assert.NotEmpty(JsonSchemaLite.Validate(new JsonObject(), schema));
        }
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
