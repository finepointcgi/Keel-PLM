using System.Globalization;
using System.Security.Cryptography;
using Keel.Audit;
using Keel.Seed;
using Keel.TestSupport;

namespace Keel.Seed
{
    /// <summary>An explicit entry point (not top-level statements) so this executable declares no type named Program: the integration tests also see the API host's Program.</summary>
    internal static class SeedEntryPoint
    {
        private static async Task<int> Main(string[] args) => await SeedCli.RunAsync(args, Console.Out, Console.Error);
    }

    /// <summary>Command line of <c>keel-seed</c>. See docs/setup.md and docs/increments/1.7-demo.md.</summary>
    public static class SeedCli
    {
        private const string Usage =
            """
            keel-seed: synthetic data for demonstrations and performance runs (never real data).

              dotnet run --project src/Keel.Seed -- --scenario demo [options]
              dotnet run --project src/Keel.Seed -- --scenario scale [--lines N] [--levels L] [--root-children N] [--released F] [--prefix P] [options]

            Options
              --scenario demo|scale   demo: the ORCA sensor pod with changes in flight (through the real services, fully audited).
                                      scale: bulk synthetic structure for performance demos (wraps the test generator; bulk SQL, not audited per object).
              --allow-non-empty       run even though plm.part_master already has rows (otherwise the tool refuses).
              --connection <cs>       app-role connection string (default: Keel__Database__AppConnectionString; the dev stack is used only with --use-dev-stack).
              --settings <file>       extra appsettings JSON (same keys as Keel.Api: Keel:Database, Keel:Vault).
              --public-key <pem>      trusted checkpoint public key, to verify checkpoint signatures as well as the hash chain.
              --manifest <file>       write the demo summary as JSON (the E2E tests read this).
              --help                  this text.

            Run against a migrated database (Keel.Api --migrate). The vault (S3) must be reachable for the demo scenario.
            """;

        public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
        {
            var opts = Parse(args);
            if (opts.ContainsKey("help") || args.Length == 0)
            {
                output.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }
            var scenario = opts.GetValueOrDefault("scenario");
            if (scenario is not ("demo" or "scale"))
            {
                error.WriteLine("Choose --scenario demo or --scenario scale (--help for details).");
                return 1;
            }
            try
            {
                var settings = SeedSettings.Resolve(opts.GetValueOrDefault("settings"), opts.GetValueOrDefault("connection"), opts.GetValueOrDefault("public-key"), args.Contains("--use-dev-stack"));
                await using var host = SeedHost.Create(settings);
                if (!opts.ContainsKey("allow-non-empty") && await DatabaseState.HasPartsAsync(host.DataSource))
                {
                    error.WriteLine("plm.part_master already has rows. This tool seeds a fresh database; refusing to run. Pass --allow-non-empty to add anyway.");
                    return 2;
                }
                return scenario == "demo" ? await RunDemoAsync(host, settings, opts, output) : await RunScaleAsync(host, opts, output);
            }
            catch (SeedException e)
            {
                error.WriteLine("Seed failed: " + e.Message);
                return 1;
            }
            catch (Exception e) when (e is Npgsql.NpgsqlException or InvalidOperationException or IOException or TimeoutException)
            {
                error.WriteLine("Seed failed: " + e.Message);
                return 1;
            }
        }

        private static async Task<int> RunDemoAsync(SeedHost host, SeedSettings settings, Dictionary<string, string> opts, TextWriter output)
        {
            // The vault hangs rather than failing fast when it cannot write (see docs/CLAUDE.md), so check it first.
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                try
                {
                    await host.Vault.PingAsync(timeout.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or Amazon.S3.AmazonS3Exception or HttpRequestException or Amazon.Runtime.AmazonClientException)
                {
                    throw new SeedException($"The vault ({settings.Vault.ServiceUrl}, bucket {settings.Vault.Bucket}) is not reachable with the configured identity: {e.Message}");
                }
            }

            var started = DateTimeOffset.UtcNow;
            var manifest = await new DemoSeeder(host, RealmUsers.Load(), output).RunAsync();
            var verdict = await host.VerifyAuditAsync(LoadKeys(settings.CheckpointPublicKeyPath));
            manifest.AuditChainValid = verdict.Valid;
            manifest.AuditEventsChecked = verdict.EventsChecked;
            manifest.AuditFailures = verdict.Failures;
            if (opts.TryGetValue("manifest", out var path)) await File.WriteAllTextAsync(path, manifest.Json());
            Print(manifest, output, DateTimeOffset.UtcNow - started);
            return verdict.Valid ? 0 : 3;
        }

        private static void Print(DemoManifest m, TextWriter w, TimeSpan took)
        {
            w.WriteLine();
            w.WriteLine("ORCA demo data created (synthetic) in " + took.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            w.WriteLine($"  {m.Parts} parts ({m.Released} released through the review workflow), {m.Documents} documents, {m.BomLines} BOM lines, {m.Changes} change objects, {m.Signatures} e-signatures, {m.AuditEvents} audit events");
            w.WriteLine();
            w.WriteLine("Objects for the demo script");
            void Row(string label, string value) => w.WriteLine($"  {label,-54} {value}");
            Row("Top assembly (In Work, 4 levels deep)", m.TopAssembly);
            Row("Pressure housing assembly (Released)", m.HousingAssembly);
            Row("Gimbal assembly (Released)", m.GimbalAssembly);
            Row("Sensor payload assembly (In Work; has a KRAKEN line)", m.PayloadAssembly);
            Row("Avionics bay assembly (Under Review, ada pending)", m.AvionicsAssemblyUnderReview);
            Row("Partner mounting interface assembly (TAA-0042)", m.PartnerAssembly);
            Row("Field spares kit (open; shows redacted lines)", m.SparesKit);
            Row("Gimbal bracket (Released A; ECN revises to B)", m.GimbalBracket);
            Row("Sensor end cap (Released A; B under review)", m.EndCap);
            Row("Lens barrel (Released A; B rejected)", m.LensBarrel);
            Row("Sensor mounting plate (Released A; ECR-1)", m.SensorMountingPlate);
            Row("Mounting interface plate (TAA-0042)", m.PartnerPlate);
            Row("KRAKEN part (invisible to ORCA-only users)", m.KrakenPart);
            Row("Open fastener (EAR99)", m.OpenFastener);
            Row("System specification (PDF)", m.SystemSpecification);
            Row("Gimbal bracket drawing (PDF)", m.BracketDrawing);
            Row("Partner interface drawing (PDF, TAA-0042)", m.PartnerDrawing);
            Row("CAD placeholder (gimbal bracket)", m.CadPlaceholder);
            Row("Torque standard (open)", m.TorqueStandard);
            Row("PR evaluating / PR open", $"{m.ProblemReportEvaluating} / {m.ProblemReportOpen}");
            Row("ECR awaiting CCB (Class I) / ECR draft (Class II)", $"{m.EcrAwaitingCcb} / {m.EcrClassIIDraft}");
            Row("ECN draft, revision B in work (Class II)", m.EcnDraftInWork);
            Row("ECN under review, approver pending", m.EcnUnderReview);
            Row("ECN rejected by the approver (back in Draft)", m.EcnRejected);
            Row("Change tasks", string.Join(", ", m.ChangeTasks));
            if (m.Extras is { } x)
            {
                Row($"Classification tree ({x.ClassCodes.Count} classes), parts classified", x.ClassifiedParts.ToString(CultureInfo.InvariantCulture));
                Row("Design baseline (established) / Release baseline (signed)", $"{x.DesignBaseline} / {x.ReleaseBaseline}");
                Row($"Effectivity context; assembly with two alternates by serial", $"{x.EffectivityContext}; {x.AlternatesAssembly}");
                Row("Part revision B effective from serial 101 / the ECN that did it", $"{x.RevisedPart} / {x.EffectivityEcn}");
                Row("MBOM released (PLANT-A) / MBOM in work, stale (pod)", $"{x.ReleasedMbom} / {x.StaleMbom}");
                Row("TDP export record (part, recipient profile)", $"{x.TdpPart}, {x.TdpRecipient} ({x.TdpExportId:D})");
                Row("Clearance profiles / disabled file-drop sink", $"{string.Join(", ", x.ClearanceProfiles)} / {x.DisabledSink}");
                Row("ERP outbox events written by the seed", x.OutboxEvents.ToString(CultureInfo.InvariantCulture));
            }
            w.WriteLine();
            w.WriteLine(m.AuditChainValid
                ? $"Audit chain verified: valid, {m.AuditEventsChecked} events checked."
                : $"AUDIT CHAIN INVALID after {m.AuditEventsChecked} events: {string.Join("; ", m.AuditFailures)}");
        }

        private static async Task<int> RunScaleAsync(SeedHost host, Dictionary<string, string> opts, TextWriter output)
        {
            int Int(string key, int fallback) => opts.TryGetValue(key, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
            var options = new SyntheticBomOptions
            {
                TargetLines = Int("lines", 5000),
                Levels = Int("levels", 6),
                RootChildren = Int("root-children", 12),
                ReleasedFraction = opts.TryGetValue("released", out var r) ? double.Parse(r, CultureInfo.InvariantCulture) : 0.5,
                NumberPrefix = opts.GetValueOrDefault("prefix") ?? "S",
            };
            var started = DateTimeOffset.UtcNow;
            var bom = await SyntheticBomGenerator.InsertAsync(host.DataSource, options);
            output.WriteLine($"Scale data created in {(DateTimeOffset.UtcNow - started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s: "
                + $"root {bom.RootNumber}, {bom.Parts.Count} parts, {bom.Lines.Count} usage lines over {options.Levels} levels, {bom.SharedPartNumbers.Count} shared standard parts.");
            output.WriteLine("Open the root assembly in the Parts list. Bulk SQL goes through the application role, so database guards apply, but per-object audit events are not written for generated rows.");
            var verdict = await host.VerifyAuditAsync(LoadKeys(null));
            output.WriteLine(verdict.Valid ? $"Audit chain verified: valid, {verdict.EventsChecked} events checked." : $"AUDIT CHAIN INVALID: {string.Join("; ", verdict.Failures)}");
            return verdict.Valid ? 0 : 3;
        }

        private static List<ECDsa> LoadKeys(string? publicKeyPath) => publicKeyPath is null ? [] : [CheckpointKeys.LoadPem(publicKeyPath)];

        private static Dictionary<string, string> Parse(string[] args)
        {
            var opts = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument '{args[i]}'. Use --help.");
                var key = args[i][2..];
                var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                opts[key] = hasValue ? args[++i] : "true";
            }
            return opts;
        }
    }
}
